using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Crest.Services;

/// <summary>
/// Starts Spotify out of sight: minimized, without taking focus from what you are doing.
/// Works for both the Microsoft Store and the spotify.com installs.
/// </summary>
public static class SpotifyStarter
{
    public static bool IsRunning
    {
        get
        {
            var procs = Process.GetProcessesByName("Spotify");
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
    }

    public static bool IsInstalled => Path() != null;

    private static string? Path()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var p in new[]
        {
            System.IO.Path.Combine(roaming, "Spotify", "Spotify.exe"),                     // spotify.com install
            System.IO.Path.Combine(local, "Microsoft", "WindowsApps", "Spotify.exe"),      // Store install (app alias)
        })
            if (File.Exists(p)) return p;
        return null;
    }

    public static bool Start()
    {
        string? exe = Path();
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--minimized")
            {
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Minimized,
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    /// <summary>
    /// Spotify sometimes opens its window anyway. Minimize it without stealing focus and hand
    /// focus back to the window you were in. Returns true if a window had to be put away.
    /// </summary>
    public static bool PutAway(IntPtr giveFocusTo)
    {
        bool moved = false;
        foreach (var h in Windows())
        {
            if (!IsWindowVisible(h) || IsIconic(h)) continue;
            ShowWindow(h, SW_SHOWMINNOACTIVE);
            moved = true;
        }
        if (moved && giveFocusTo != IntPtr.Zero && GetForegroundWindow() != giveFocusTo)
            SetForegroundWindow(giveFocusTo);
        return moved;
    }

    public static IntPtr Foreground => GetForegroundWindow();


    private static List<IntPtr> Windows()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Spotify"))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        var list = new List<IntPtr>();
        if (pids.Count == 0) return list;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pids.Contains(pid) && GetWindow(h, GW_OWNER) == IntPtr.Zero && GetWindowTextLength(h) > 0)
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private const int SW_SHOWMINNOACTIVE = 7;
    private const uint GW_OWNER = 4;
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
}
