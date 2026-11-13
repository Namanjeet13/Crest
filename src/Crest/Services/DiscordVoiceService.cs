using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace Crest.Services;

/// <summary>
/// Reads and drives Discord's voice controls (mute, deafen, disconnect) through the same
/// accessibility tree a screen reader uses. No Discord account link, token or approval is
/// needed, and it works while Discord is minimized or closed to the tray. It relies on
/// Discord's English labels ("Voice Connected", "Mute", "Deafen", "Disconnect").
/// All accessibility calls run on one background thread; <see cref="Changed"/> fires there.
/// </summary>
public sealed class DiscordVoiceService : IDisposable
{
    private static readonly string[] ProcessNames = { "Discord", "DiscordPTB", "DiscordCanary" };

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Queue<string> _commands = new();
    private Thread? _thread;
    private volatile bool _stop;

    private IntPtr _hwnd;
    private bool _woken;
    private AutomationElement? _voice, _mute, _deafen, _disconnect, _channel;

    public bool InCall { get; private set; }
    public bool Muted { get; private set; }
    public bool Deafened { get; private set; }
    /// <summary>Voice channel name, e.g. "general".</summary>
    public string Channel { get; private set; } = "";
    /// <summary>Server name, or the friend's name for a direct call.</summary>
    public string Server { get; private set; } = "";
    public DateTime Since { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Crest Discord voice", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _thread = null;
        if (InCall)
        {
            InCall = false;
            Changed?.Invoke();
        }
    }

    public void Dispose() => Stop();

    public void ToggleMute() => Send("mute");
    public void ToggleDeafen() => Send("deafen");
    public void Disconnect() => Send("disconnect");

    private void Send(string command)
    {
        lock (_gate) _commands.Enqueue(command);
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            int delay;
            try
            {
                RunCommands();
                delay = Poll();
            }
            catch
            {
                Forget();
                delay = 3000;
            }
            _wake.WaitOne(delay);
        }
        Forget();
    }

    private void RunCommands()
    {
        while (true)
        {
            string? cmd;
            lock (_gate) cmd = _commands.Count > 0 ? _commands.Dequeue() : null;
            if (cmd == null) return;
            if (!InCall) continue;
            try
            {
                switch (cmd)
                {
                    case "mute": Toggle(_mute); break;
                    case "deafen": Toggle(_deafen); break;
                    case "disconnect": Invoke(_disconnect); break;
                }
            }
            catch { Forget(); }
            // Discord takes a moment to redraw; read the result instead of assuming it.
            for (int i = 0; i < 6 && !_stop; i++)
            {
                Thread.Sleep(150);
                if (!Refresh()) break;
            }
        }
    }

    /// <summary>Returns how long to wait before the next look.</summary>
    private int Poll()
    {
        IntPtr hwnd = FindDiscordWindow();
        if (hwnd == IntPtr.Zero)
        {
            Forget();
            Set(false, false, false, "", "");
            return 5000;
        }
        if (hwnd != _hwnd)
        {
            Forget();
            _hwnd = hwnd;
        }
        if (!_woken)
        {
            WakeAccessibility(hwnd);
            _woken = true;
        }
        if (_voice != null && Refresh()) return 1000;
        Scan();
        return InCall ? 1000 : 3000;
    }

    /// <summary>Re-reads the cached buttons. False if they went stale and a full scan is needed.</summary>
    private bool Refresh()
    {
        if (_voice == null || _mute == null || _deafen == null) return false;
        try
        {
            if (!_voice.Current.Name.Contains("Voice Connected", StringComparison.Ordinal)) return false;
            bool muted = IsOn(_mute), deaf = IsOn(_deafen);
            string link = _channel?.Current.Name ?? "";
            SplitChannel(link, out var channel, out var server);
            Set(true, muted, deaf, channel, server);
            return true;
        }
        catch
        {
            _voice = _mute = _deafen = _disconnect = _channel = null;
            return false;
        }
    }

    private void Scan()
    {
        var root = AutomationElement.FromHandle(_hwnd);
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationElement? voice = null, mute = null, deafen = null, disconnect = null, channel = null;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            string name;
            ControlType type;
            try { name = e.Current.Name; type = e.Current.ControlType; }
            catch { continue; }
            if (name.Length == 0) continue;
            if (voice == null && type == ControlType.Button && name.Contains("Voice Connected", StringComparison.Ordinal))
            {
                voice = e;
                // The link right after it reads "channel / server".
                if (i + 1 < all.Count)
                {
                    try
                    {
                        var next = all[i + 1];
                        if (next.Current.ControlType == ControlType.Hyperlink) channel = next;
                    }
                    catch { }
                }
            }
            else if (type == ControlType.Button && name == "Disconnect" && voice != null && disconnect == null) disconnect = e;
            else if (type == ControlType.Button && name == "Mute" && mute == null && HasToggle(e)) mute = e;
            else if (type == ControlType.Button && name == "Deafen" && deafen == null && HasToggle(e)) deafen = e;
        }

        if (voice != null && mute != null && deafen != null)
        {
            _voice = voice; _mute = mute; _deafen = deafen; _disconnect = disconnect; _channel = channel;
            Refresh();
        }
        else
        {
            _voice = _mute = _deafen = _disconnect = _channel = null;
            Set(false, false, false, "", "");
        }
    }

    private void Set(bool inCall, bool muted, bool deaf, string channel, string server)
    {
        bool changed = inCall != InCall || muted != Muted || deaf != Deafened || channel != Channel || server != Server;
        if (inCall && !InCall) Since = DateTime.Now;
        if (!inCall) Since = default;
        InCall = inCall;
        Muted = inCall && muted;
        Deafened = inCall && deaf;
        Channel = channel;
        Server = server;
        if (changed) Changed?.Invoke();
    }

    private void Forget()
    {
        _hwnd = IntPtr.Zero;
        _woken = false;
        _voice = _mute = _deafen = _disconnect = _channel = null;
    }

    private static void SplitChannel(string link, out string channel, out string server)
    {
        int slash = link.LastIndexOf(" / ", StringComparison.Ordinal);
        if (slash > 0)
        {
            channel = link[..slash].Trim();
            server = link[(slash + 3)..].Trim();
        }
        else
        {
            channel = link.Trim();
            server = "";
        }
    }

    private static bool HasToggle(AutomationElement e) =>
        e.TryGetCurrentPattern(TogglePattern.Pattern, out _);

    private static bool IsOn(AutomationElement e) =>
        e.TryGetCurrentPattern(TogglePattern.Pattern, out var p) &&
        ((TogglePattern)p).Current.ToggleState == ToggleState.On;

    private static void Toggle(AutomationElement? e)
    {
        if (e == null) return;
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) ((TogglePattern)t).Toggle();
        else if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var i)) ((InvokePattern)i).Invoke();
    }

    private static void Invoke(AutomationElement? e)
    {
        if (e == null) return;
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var i)) ((InvokePattern)i).Invoke();
        else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) ((TogglePattern)t).Toggle();
    }

    // ---- Finding Discord's window, even when it is hidden in the tray ----

    private static IntPtr FindDiscordWindow()
    {
        var pids = new HashSet<uint>();
        foreach (var name in ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs)
            {
                pids.Add((uint)p.Id);
                p.Dispose();
            }
        }
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        var cls = new StringBuilder(64);
        var title = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            cls.Clear();
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            title.Clear();
            GetWindowText(h, title, title.Capacity);
            string t = title.ToString();
            if (t == "Discord" || t.EndsWith(" - Discord", StringComparison.Ordinal) ||
                t.StartsWith("Discord", StringComparison.Ordinal) && GetWindow(h, GW_OWNER) == IntPtr.Zero)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Chromium builds its accessibility tree only once something asks for it.</summary>
    private static void WakeAccessibility(IntPtr hwnd)
    {
        try
        {
            var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"); // IAccessible
            if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var acc) == 0 && acc != null)
                Marshal.ReleaseComObject(acc);
        }
        catch { }
    }

    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private const uint GW_OWNER = 4;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
}
