using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Crest.Services;

/// <summary>
/// Takes a picture of the screen behind the island, with the island itself left out
/// (WDA_EXCLUDEFROMCAPTURE for the few milliseconds of the grab), and makes a softened
/// copy for the frosted inside of the glass. Both pictures are frozen, ready for the shader.
/// </summary>
internal static class GlassCapture
{
    public sealed record Shot(BitmapSource Sharp, BitmapSource Frost);

    /// <summary>Grabs the pixels under the window. Must run on the UI thread (it toggles the window's capture flag).</summary>
    public static (byte[] px, int w, int h)? Grab(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;

        bool excluded = SetWindowDisplayAffinity(hwnd, 0x11); // WDA_EXCLUDEFROMCAPTURE
        if (excluded) DwmFlush();
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        byte[]? px = null;
        if (dib != IntPtr.Zero)
        {
            IntPtr old = SelectObject(mem, dib);
            if (BitBlt(mem, 0, 0, w, h, screen, rc.Left, rc.Top, 0x00CC0020)) // SRCCOPY
            {
                px = new byte[w * h * 4];
                Marshal.Copy(bits, px, 0, px.Length);
            }
            SelectObject(mem, old);
            DeleteObject(dib);
        }
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        if (excluded) SetWindowDisplayAffinity(hwnd, 0);
        if (!excluded || px == null) return null; // without the exclusion the island would see itself
        return (px, w, h);
    }

    /// <summary>Builds the sharp and frosted pictures. Safe on any thread.</summary>
    public static Shot Process(byte[] px, int w, int h)
    {
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var sharp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
        sharp.Freeze();

        // Frost: a quarter-size copy, box-blurred three times (close to a Gaussian).
        const int f = 4;
        int fw = Math.Max(1, w / f), fh = Math.Max(1, h / f);
        var a = new float[fw * fh * 3];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int yy = 0; yy < f; yy++)
                {
                    int row = ((y * f + yy) * w + x * f) * 4;
                    for (int xx = 0; xx < f; xx++, row += 4) { b += px[row]; g += px[row + 1]; r += px[row + 2]; }
                }
                int o = (y * fw + x) * 3;
                a[o] = b; a[o + 1] = g; a[o + 2] = r;
            }
        var tmp = new float[a.Length];
        int rad = Math.Max(3, w / 100); // three passes: a soft, wide frost, like iOS
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(a, tmp, fw, fh, rad);
            BoxV(tmp, a, fw, fh, rad);
        }
        var outPx = new byte[fw * fh * 4];
        float scale = 1f / (f * f);
        for (int i = 0, o = 0; i < a.Length; i += 3, o += 4)
        {
            outPx[o] = (byte)Math.Min(255, a[i] * scale);
            outPx[o + 1] = (byte)Math.Min(255, a[i + 1] * scale);
            outPx[o + 2] = (byte)Math.Min(255, a[i + 2] * scale);
            outPx[o + 3] = 255;
        }
        var frost = BitmapSource.Create(fw, fh, 96, 96, PixelFormats.Bgr32, null, outPx, fw * 4);
        frost.Freeze();
        return new Shot(sharp, frost);
    }

    public static Task<Shot> ProcessAsync(byte[] px, int w, int h) => Task.Run(() => Process(px, w, h));

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int c = 0; c < 3; c++)
            {
                float sum = 0;
                for (int x = -r; x <= r; x++) sum += src[(row + Math.Clamp(x, 0, w - 1)) * 3 + c];
                for (int x = 0; x < w; x++)
                {
                    dst[(row + x) * 3 + c] = sum * inv;
                    sum += src[(row + Math.Min(x + r + 1, w - 1)) * 3 + c] - src[(row + Math.Max(x - r, 0)) * 3 + c];
                }
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 3; c++)
            {
                float sum = 0;
                for (int y = -r; y <= r; y++) sum += src[(Math.Clamp(y, 0, h - 1) * w + x) * 3 + c];
                for (int y = 0; y < h; y++)
                {
                    dst[(y * w + x) * 3 + c] = sum * inv;
                    sum += src[(Math.Min(y + r + 1, h - 1) * w + x) * 3 + c] - src[(Math.Max(y - r, 0) * w + x) * 3 + c];
                }
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
