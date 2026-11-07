using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Crest.Services;

/// <summary>
/// Takes you to the song: brings the playing app forward and, for browsers,
/// switches to the very tab that is playing (found by its title).
/// </summary>
public static class SourceLauncher
{
    public static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen" };

    public static async Task OpenAsync(string aumid, string title)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        string key = ProcessKey(aumid);
        bool browser = Array.Exists(Browsers, b => key.Contains(b));

        IntPtr hwnd = await Task.Run(() =>
        {
            var windows = FindWindows(key);
            if (browser && title.Length > 0)
                foreach (var w in windows)
                    if (SelectTab(w, title)) return w;
            return windows.Count > 0 ? windows[0] : IntPtr.Zero;
        });

        if (hwnd != IntPtr.Zero)
        {
            Activate(hwnd);
            return;
        }

        // Packaged apps (Media Player, Apple Music...) or no window yet: let the shell activate it.
        try
        {
            string target = aumid.Contains('!') ? @"shell:AppsFolder\" + aumid : aumid;
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>"Spotify.exe" → spotify, "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → zunemusic.</summary>
    public static string ProcessKey(string aumid)
    {
        string s = aumid.ToLowerInvariant();
        if (s.Contains("spotify")) return "spotify";
        if (s.Contains("zunemusic")) return "media.player";
        if (s.Contains("applemusic")) return "applemusic";
        if (s == "308046b0af4a39cb") return "firefox";
        if (s.Contains('!')) s = s[..s.IndexOf('!')];
        if (s.EndsWith(".exe")) s = s[..^4];
        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s[(slash + 1)..];
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1 && !s.StartsWith("microsoft")) s = s[(dot + 1)..];
        return s;
    }

    internal static List<IntPtr> FindWindows(string key)
    {
        var found = new List<IntPtr>();
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0 || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!names.TryGetValue(pid, out var name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
                catch { name = ""; }
                names[pid] = name;
            }
            if (name.Length > 0 && (name.Contains(key) || key.Contains(name))) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool SelectTab(IntPtr hwnd, string title)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var tabs = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            foreach (AutomationElement tab in tabs)
            {
                string name = tab.Current.Name ?? "";
                if (name.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p))
                    ((SelectionItemPattern)p).Select();
                else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                    ((InvokePattern)inv).Invoke();
                return true;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return false;
    }

    public static bool IsBrowser(string aumid)
    {
        string key = ProcessKey(aumid);
        return Array.Exists(Browsers, b => key.Contains(b));
    }

    /// <summary>
    /// Is the browser tab playing <paramref name="title"/> on one of <paramref name="domains"/>?
    /// Reads the address bar when that tab is the selected one, otherwise judges by the tab's name.
    /// Slow (UI Automation): call off the UI thread.
    /// </summary>
    public static bool TabMatchesDomain(string aumid, string title, IList<string> domains)
    {
        if (string.IsNullOrEmpty(title) || domains.Count == 0) return false;
        try
        {
            foreach (var hwnd in FindWindows(ProcessKey(aumid)))
            {
                var root = AutomationElement.FromHandle(hwnd);
                var tabs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                foreach (AutomationElement tab in tabs)
                {
                    string name = tab.Current.Name ?? "";
                    if (name.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    bool selected = tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) &&
                                    ((SelectionItemPattern)sp).Current.IsSelected;
                    if (selected)
                    {
                        string url = AddressOf(root);
                        if (url.Length > 0)
                            return domains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase));
                    }
                    foreach (var d in domains)
                    {
                        // "youtube.com" → "youtube"; Spotify's player titles its tab "Song • Artist".
                        string site = SiteName(d);
                        if (site.Length > 0 && name.Contains(site, StringComparison.OrdinalIgnoreCase)) return true;
                        if (site == "spotify" && name.Contains(" • ")) return true;
                    }
                    return false;
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return false;
    }

    private static string AddressOf(AutomationElement root)
    {
        try
        {
            var edits = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            foreach (AutomationElement e in edits)
            {
                if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) continue;
                string v = ((ValuePattern)vp).Current.Value ?? "";
                if (v.Contains('.') && !v.Contains(' ')) return v;
            }
        }
        catch { }
        return "";
    }

    private static string SiteName(string domain)
    {
        var parts = domain.ToLowerInvariant().Replace("www.", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        return parts.Length >= 2 ? parts[^2] : parts[0];
    }

    private static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        // A tap of Alt lets a background process hand focus to another window.
        keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 2;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}
