using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Crest.Interop;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Crest.Services;

public readonly record struct SystemSnapshot(
    double Cpu, double Ram, double RamUsedGb, double RamTotalGb,
    double? Battery, bool Charging, TimeSpan Uptime);

/// <summary>CPU, memory, battery. Sampled only while someone is looking.</summary>
public sealed class SystemStatsService
{
    private long _idle, _kernel, _user;

    public SystemSnapshot Sample()
    {
        double cpu = 0;
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
            long total = dk + du;
            if (_kernel != 0 && total > 0) cpu = Math.Clamp((total - di) * 100.0 / total, 0, 100);
            _idle = idle; _kernel = kernel; _user = user;
        }

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        double ram = 0, used = 0, totalGb = 0;
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            ram = mem.dwMemoryLoad;
            totalGb = mem.ullTotalPhys / 1073741824.0;
            used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        }

        double? battery = null;
        bool charging = false;
        if (GetSystemPowerStatus(out var power))
        {
            // 128 = no battery, 255 = unknown; percent 255 = unknown.
            if ((power.BatteryFlag & 128) == 0 && power.BatteryFlag != 255 && power.BatteryLifePercent <= 100)
                battery = power.BatteryLifePercent;
            charging = power.ACLineStatus == 1;
        }

        return new SystemSnapshot(cpu, ram, used, totalGb, battery, charging, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>
/// Screen brightness. Tries, in order: the laptop panel (WMI), external monitors over DDC/CI,
/// and finally a soft dimming of the picture (gamma) so the slider always does something.
/// All hardware calls run off the UI thread and rapid slider moves are coalesced.
/// </summary>
public static class BrightnessService
{
    private enum Mode { Unknown, Wmi, Ddc, Gamma }
    private static Mode _mode = Mode.Unknown;
    private static int _pending = -1;
    private static int _running;
    private static int _last = 100;
    private static readonly object Gate = new();
    private static List<(IntPtr handle, uint min, uint max)>? _monitors;

    /// <summary>Current brightness 0..100. Slow on DDC/CI: call off the UI thread.</summary>
    public static int Get()
    {
        lock (Gate)
        {
            Detect();
            try
            {
                switch (_mode)
                {
                    case Mode.Wmi:
                        using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                            foreach (ManagementObject o in searcher.Get())
                                return _last = Convert.ToInt32(o["CurrentBrightness"]);
                        break;
                    case Mode.Ddc:
                        foreach (var m in _monitors!)
                            if (GetMonitorBrightness(m.handle, out uint mn, out uint cur, out uint mx) && mx > mn)
                                return _last = (int)Math.Round((cur - mn) * 100.0 / (mx - mn));
                        break;
                }
            }
            catch { }
            return _last;
        }
    }

    public static Task<int> GetAsync() => Task.Run(Get);

    /// <summary>Coalesces rapid slider moves into one hardware call at a time.</summary>
    public static void Set(int value)
    {
        value = Math.Clamp(value, 0, 100);
        _last = value;
        Interlocked.Exchange(ref _pending, value);
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                int v;
                while ((v = Interlocked.Exchange(ref _pending, -1)) >= 0)
                    lock (Gate) Apply(v);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                int left = Volatile.Read(ref _pending);
                if (left >= 0) Set(left);
            }
        });
    }

    private static void Apply(int v)
    {
        Detect();
        try
        {
            switch (_mode)
            {
                case Mode.Wmi:
                    using (var mc = new ManagementClass("root\\WMI", "WmiMonitorBrightnessMethods", null))
                        foreach (ManagementObject o in mc.GetInstances())
                            o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
                    break;
                case Mode.Ddc:
                    foreach (var m in _monitors!)
                        SetMonitorBrightness(m.handle, (uint)(m.min + (m.max - m.min) * v / 100.0));
                    break;
                case Mode.Gamma:
                    SetGamma(v);
                    break;
            }
        }
        catch { }
    }

    private static void Detect()
    {
        if (_mode != Mode.Unknown) return;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get()) { _last = Convert.ToInt32(o["CurrentBrightness"]); _mode = Mode.Wmi; return; }
        }
        catch { }

        var found = new List<(IntPtr, uint, uint)>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint n) || n == 0) return true;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
                foreach (var pm in arr)
                {
                    if (GetMonitorBrightness(pm.hPhysicalMonitor, out uint mn, out uint cur, out uint mx) && mx > mn)
                    {
                        found.Add((pm.hPhysicalMonitor, mn, mx));
                        _last = (int)Math.Round((cur - mn) * 100.0 / (mx - mn));
                    }
                    else DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        if (found.Count > 0) { _monitors = found; _mode = Mode.Ddc; return; }
        _mode = Mode.Gamma;
        _last = 100;
    }

    public static bool IsSoftware { get { lock (Gate) { Detect(); return _mode == Mode.Gamma; } } }

    private static void SetGamma(int v)
    {
        // 100 is the normal picture, 0 is about 40% light. Windows refuses ramps that are too dark.
        double k = 0.4 + 0.6 * v / 100.0;
        var ramp = new ushort[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            ushort val = (ushort)Math.Min(65535, i * 256 * k);
            ramp[i] = ramp[256 + i] = ramp[512 + i] = val;
        }
        IntPtr dc = GetDC(IntPtr.Zero);
        try { SetDeviceGammaRamp(dc, ramp); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>Put the picture back to normal when Crest exits (only matters for the soft dimming).</summary>
    public static void Restore()
    {
        try
        {
            if (_mode == Mode.Gamma) SetGamma(100);
            if (_monitors != null)
                foreach (var m in _monitors) DestroyPhysicalMonitor(m.handle);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}

/// <summary>Wi-Fi and Bluetooth radios.</summary>
public static class RadioService
{
    public static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool?> ToggleAsync(RadioKind kind)
    {
        var radio = await FindAsync(kind);
        if (radio == null) return null;
        var target = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        try
        {
            var result = await radio.SetStateAsync(target);
            return result == RadioAccessStatus.Allowed ? target == RadioState.On : radio.State == RadioState.On;
        }
        catch
        {
            return null;
        }
    }
}

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Whether Windows will start Crest at sign-in (the installer can turn this on too).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Crest") is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath is { } path)
                key.SetValue("Crest", $"\"{path}\"");
            else
                key.DeleteValue("Crest", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
