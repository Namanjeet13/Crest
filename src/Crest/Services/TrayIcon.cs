using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Crest.Services;

/// <summary>
/// Notification-area icon on plain Win32 (Shell_NotifyIcon + a popup menu), so Crest
/// never has to load WinForms and System.Drawing just for one tray icon.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAY = 0x8001;
    private const int ID_OPEN = 1, ID_SETTINGS = 2, ID_QUIT = 3;

    private readonly Action _open, _openSettings, _quit;
    private readonly HwndSource _window;
    private readonly IntPtr _hicon;
    private readonly uint _taskbarCreated;

    public TrayIcon(Action open, Action openSettings, Action quit)
    {
        _open = open; _openSettings = openSettings; _quit = quit;
        // A hidden tool window that receives the icon's clicks. Not message-only: those miss the
        // "TaskbarCreated" broadcast, and the icon would vanish for good if Explorer restarts.
        _window = new HwndSource(new HwndSourceParameters("CrestTray")
        { WindowStyle = 0, ExtendedWindowStyle = 0x80, Width = 0, Height = 0 }); // WS_EX_TOOLWINDOW
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hicon = MakeIcon();
        Add();
    }

    private void Add()
    {
        var data = Data();
        data.uFlags = 0x1 | 0x2 | 0x4; // message, icon, tip
        Shell_NotifyIcon(0, ref data);  // NIM_ADD
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uCallbackMessage = WM_TRAY,
        hIcon = _hicon,
        szTip = "Crest",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            int ev = (int)lParam & 0xFFFF;
            if (ev == 0x0202) _open();          // WM_LBUTTONUP
            else if (ev == 0x0205) ShowMenu();  // WM_RBUTTONUP
            handled = true;
        }
        else if (msg == (int)_taskbarCreated)
        {
            Add(); // Explorer restarted: put the icon back.
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, ID_OPEN, "Open Crest");
        AppendMenu(menu, 0, ID_SETTINGS, "Settings");
        AppendMenu(menu, 0x800, 0, null); // separator
        AppendMenu(menu, 0, ID_QUIT, "Quit Crest");
        GetCursorPos(out var pt);
        SetForegroundWindow(_window.Handle); // so the menu closes when you click away
        int id = TrackPopupMenuEx(menu, 0x100 | 0x80, pt.X, pt.Y, _window.Handle, IntPtr.Zero); // RETURNCMD | NONOTIFY
        DestroyMenu(menu);
        switch (id)
        {
            case ID_OPEN: _open(); break;
            case ID_SETTINGS: _openSettings(); break;
            case ID_QUIT: _quit(); break;
        }
    }

    /// <summary>A dark pill with a white outline and a green dot, drawn straight into 32x32 pixels.</summary>
    private static IntPtr MakeIcon()
    {
        const int S = 32;
        var px = new int[S * S];
        const float cx0 = 9f, cx1 = 23f, cy = 16f, r = 6f; // pill: two half circles joined
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float nx = Math.Clamp(fx, cx0, cx1);
                float d = MathF.Sqrt((fx - nx) * (fx - nx) + (fy - cy) * (fy - cy)) - r; // signed distance to the pill edge
                float fill = Math.Clamp(0.5f - d, 0, 1);
                float ring = Math.Clamp(1.5f - MathF.Abs(d), 0, 1);
                float dot = Math.Clamp(2.5f - MathF.Sqrt((fx - 22f) * (fx - 22f) + (fy - 16f) * (fy - 16f)), 0, 1);
                // Composite: fill (#141416), then the white ring, then the green dot (#30D158).
                float a = fill, rr = 20, gg = 20, bb = 22;
                Over(ref a, ref rr, ref gg, ref bb, ring, 255, 255, 255);
                Over(ref a, ref rr, ref gg, ref bb, dot, 48, 209, 88);
                byte A = (byte)(a * 255);
                // Premultiplied BGRA for a 32-bit DIB icon.
                px[y * S + x] = (A << 24) | ((byte)(rr * a) << 16) | ((byte)(gg * a) << 8) | (byte)(bb * a);
            }

        IntPtr color = CreateBitmap(S, S, 1, 32, px);
        IntPtr mask = CreateBitmap(S, S, 1, 1, new byte[S * S / 8]);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private static void Over(ref float a, ref float r, ref float g, ref float b, float sa, float sr, float sg, float sb)
    {
        if (sa <= 0) return;
        float oa = sa + a * (1 - sa);
        if (oa <= 0) return;
        r = (sr * sa + r * a * (1 - sa)) / oa;
        g = (sg * sa + g * a * (1 - sa)) / oa;
        b = (sb * sa + b * a * (1 - sa)) / oa;
        a = oa;
    }

    public void Dispose()
    {
        var data = Data();
        Shell_NotifyIcon(2, ref data); // NIM_DELETE
        DestroyIcon(_hicon);
        _window.Dispose();
    }

    // ── Win32 ──
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, int[] bits);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
