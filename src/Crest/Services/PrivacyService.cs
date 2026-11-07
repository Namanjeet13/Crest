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

    private static (bool inUse, DateTime since, string app) Check(string capability)
    {
        DateTime earliest = DateTime.MaxValue;
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
                        Inspect(k, sub, ref earliest, ref app);
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(name);
                    Inspect(k, name, ref earliest, ref app);
                }
            }
        }
        catch
        {
            return (false, default, "");
        }
        return earliest == DateTime.MaxValue ? (false, default, "") : (true, earliest, app);
    }

    private static void Inspect(RegistryKey? key, string name, ref DateTime earliest, ref string app)
    {
        if (key == null) return;
        if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
            key.GetValue("LastUsedTimeStart") is long start && start > 0)
        {
            DateTime since;
            try { since = DateTime.FromFileTime(start); } catch { since = DateTime.Now; }
            if (since < earliest)
            {
                earliest = since;
                app = PrettyApp(name);
            }
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
