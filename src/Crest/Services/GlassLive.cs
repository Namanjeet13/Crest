using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Crest.Services;

/// <summary>
/// Live glass: photographs the screen behind the island on every screen refresh, on a background
/// thread while the island is open, so the glass follows a playing video or a scrolling page.
/// While it runs the island is left out of every screen capture (WDA_EXCLUDEFROMCAPTURE),
/// otherwise it would photograph itself. The copy comes from DXGI Desktop Duplication (GPU,
/// about a millisecond) with BitBlt as the fallback. A frame goes to the UI only when the picture
/// changed, and only after the UI took the previous one, so a busy UI thread never queues up frames.
/// </summary>
internal sealed class GlassLive
{
    public sealed class Frame
    {
        public int W, H, FW, FH;
        public byte[] Sharp = Array.Empty<byte>(), Frost = Array.Empty<byte>();
    }

    private readonly IntPtr _hwnd;
    private readonly Dispatcher _ui;
    private readonly Action<Frame> _apply;
    private readonly ManualResetEventSlim _done = new(true);
    private volatile bool _run;
    private volatile int _gen;
    /// <summary>Set while the glass can't be seen: no captures then.</summary>
    public volatile bool Paused;
    public bool Running => _run;

    public GlassLive(IntPtr hwnd, Dispatcher ui, Action<Frame> apply)
    { _hwnd = hwnd; _ui = ui; _apply = apply; }

    /// <summary>Starts capturing. Returns false if the island can't be left out of captures. UI thread.</summary>
    public bool Start()
    {
        if (_run) return true;
        if (!SetWindowDisplayAffinity(_hwnd, 0x11)) return false;
        _run = true;
        int gen = ++_gen;
        new Thread(() => Loop(gen)) { IsBackground = true, Name = "Crest live glass", Priority = ThreadPriority.BelowNormal }.Start();
        return true;
    }

    /// <summary>Stops capturing and lets screenshots see the island again. UI thread.</summary>
    public void Stop()
    {
        if (!_run) return;
        _run = false;
        SetWindowDisplayAffinity(_hwnd, 0);
    }

    private void Loop(int gen)
    {
        // Two frames: the UI shows one while the next is being made, so no refresh is skipped.
        var frames = new[] { new Frame(), new Frame() };
        int cur = 0;
        float[] a = Array.Empty<float>(), tmp = Array.Empty<float>();
        DesktopDup? dd = null;
        long retryAt = 0;
        IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero, bits = IntPtr.Zero;
        int dw = 0, dh = 0;
        try
        {
            while (_run && gen == _gen)
            {
                if (Paused || !GetWindowRect(_hwnd, out var rc) || rc.Right <= rc.Left || rc.Bottom <= rc.Top)
                {
                    Thread.Sleep(15);
                    continue;
                }
                int x = rc.Left, y = rc.Top, w = rc.Right - rc.Left, h = rc.Bottom - rc.Top, n = w * h * 4;
                var f = frames[cur];
                if (f.Sharp.Length != n) f.Sharp = new byte[n];

                // GPU screen copy. It waits for the desktop's next frame by itself, so it runs at the
                // monitor's refresh rate (60, 144, 300 Hz), and returns 0 when nothing on screen changed.
                if (dd != null && !dd.Contains(x, y, x + w, y + h)) { dd.Dispose(); dd = null; retryAt = 0; }
                if (dd == null && Environment.TickCount64 >= retryAt)
                {
                    dd = DesktopDup.Create(x + w / 2, y + h / 2);
                    if (dd == null) retryAt = Environment.TickCount64 + 3000;
                }
                if (dd != null)
                {
                    int got = dd.Grab(x, y, w, h, f.Sharp, 100);
                    if (got < 0) { dd.Dispose(); dd = null; retryAt = Environment.TickCount64 + 250; continue; }
                    if (got == 0) continue;
                }
                else
                {
                    // Fallback: BitBlt, paced by the screen refresh.
                    if (DwmFlush() != 0) Thread.Sleep(1);
                    if (screen == IntPtr.Zero) { screen = GetDC(IntPtr.Zero); mem = CreateCompatibleDC(screen); }
                    if (w != dw || h != dh)
                    {
                        if (dib != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(dib); dib = IntPtr.Zero; }
                        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                        dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
                        if (dib == IntPtr.Zero) { dw = dh = 0; Thread.Sleep(15); continue; }
                        old = SelectObject(mem, dib); dw = w; dh = h;
                    }
                    if (!BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020)) continue;
                    Marshal.Copy(bits, f.Sharp, 0, n);
                }
                if (!_run || gen != _gen) break;

                var sharp = f.Sharp;
                for (int i = 3; i < n; i += 4) sharp[i] = 255;
                var last = frames[cur ^ 1];
                if (last.W == w && last.H == h && sharp.AsSpan().SequenceEqual(last.Sharp)) continue;
                f.W = w; f.H = h;
                GlassCapture.FrostInto(sharp, w, h, ref f.Frost, ref a, ref tmp, out f.FW, out f.FH);

                // Hand it over once the UI has taken the previous frame (normally it already has).
                if (!_done.Wait(200) || !_run || gen != _gen) continue;
                _done.Reset();
                var send = f;
                _ui.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    try { if (_run && gen == _gen) _apply(send); } catch { } finally { _done.Set(); }
                });
                cur ^= 1;
            }
        }
        finally
        {
            dd?.Dispose();
            if (dib != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(dib); }
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }



    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
