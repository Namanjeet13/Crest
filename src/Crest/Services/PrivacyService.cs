using System;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Crest.Services;

/// <summary>
/// Knows when any app is using the microphone or camera, by reading the same
/// consent store Windows uses for its own privacy indicator. Also gives the
/// time the microphone was opened, so the call timer is right even if Crest
/// started in the middle of a call.
/// </summary>
public sealed class PrivacyService
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private readonly DispatcherTimer _timer;

    public bool MicInUse { get; private set; }
    public bool CameraInUse { get; private set; }
    public DateTime MicSince { get; private set; }
    public string MicApp { get; private set; } = "";

    public event Action? Changed;

    public PrivacyService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    private void Poll()
    {
        var mic = Check("microphone");
        var cam = Check("webcam");
        bool changed = mic.inUse != MicInUse || cam.inUse != CameraInUse;
        MicInUse = mic.inUse;
        CameraInUse = cam.inUse;
        if (mic.inUse)
        {
            if (changed || MicSince == default) MicSince = mic.since;
            MicApp = mic.app;
        }
        else
        {
            MicSince = default;
            MicApp = "";
        }
        if (changed) Changed?.Invoke();
    }

    // Windows leaves a record "open" forever when an app crashes or updates while it is using the
    // mic (Discord moves to a new app-x.y.z folder on every update). Such stale records made the
    // call timer read hundreds of hours. A record only counts if it began after this boot and,
    // for desktop apps, that exact exe is running now. Of the live ones, the newest is the call.
    private static (bool inUse, DateTime since, string app) Check(string capability)
    {
        DateTime latest = DateTime.MinValue;
        string app = "";
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (root == null) return (false, default, "");
            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var np = root.OpenSubKey(name);
                    if (np == null) continue;
                    foreach (var sub in np.GetSubKeyNames())
                    {
                        using var k = np.OpenSubKey(sub);
                        Inspect(k, sub, true, ref latest, ref app);
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(name);
                    Inspect(k, name, false, ref latest, ref app);
                }
            }
        }
        catch
        {
            return (false, default, "");
        }
        return latest == DateTime.MinValue ? (false, default, "") : (true, latest, app);
    }

    private static void Inspect(RegistryKey? key, string name, bool desktopApp, ref DateTime latest, ref string app)
    {
        if (key == null) return;
        if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
            key.GetValue("LastUsedTimeStart") is long start && start > 0)
        {
            DateTime since;
            try { since = DateTime.FromFileTime(start); } catch { return; }
            var now = DateTime.Now;
            var boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (since < boot) return;                       // left open before a restart
            if (since > now) since = now;
            if (desktopApp && !IsRunning(name.Replace('#', '\\'))) return; // crashed or updated away
            if (since > latest)
            {
                latest = since;
                app = PrettyApp(name);
            }
        }
    }

    private static bool IsRunning(string exePath)
    {
        string exe = System.IO.Path.GetFileNameWithoutExtension(exePath);
        var procs = System.Diagnostics.Process.GetProcessesByName(exe);
        try
        {
            if (procs.Length == 0) return false;
            bool anyUnreadable = false;
            foreach (var p in procs)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path == null) { anyUnreadable = true; continue; }
                    if (string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { anyUnreadable = true; } // elevated or protected: trust the name
            }
            return anyUnreadable;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string PrettyApp(string keyName)
    {
        // NonPackaged keys look like C:#Program Files#Zoom#bin#Zoom.exe
        string s = keyName.Replace('#', '\\');
        int slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us]; // packaged family names
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s;
    }
}
