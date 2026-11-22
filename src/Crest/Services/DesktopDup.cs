using System;
using System.Runtime.InteropServices;

namespace Crest.Services;

/// <summary>
/// Screen copy through DXGI Desktop Duplication: the GPU hands over each new desktop frame and only
/// the part behind the island is copied back, about a millisecond instead of BitBlt's ten. Windows
/// with WDA_EXCLUDEFROMCAPTURE (the island) are left out, as with BitBlt. COM calls go straight
/// through the vtables, so no extra package is needed. Any failure means "use BitBlt instead".
/// </summary>
internal sealed unsafe class DesktopDup : IDisposable
{
    private static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    private IntPtr _dev, _ctx, _dup, _stage;
    private int _sw, _sh;
    private readonly int _ol, _ot, _or, _ob; // the output's desktop rectangle

    private DesktopDup(IntPtr dev, IntPtr ctx, IntPtr dup, int l, int t, int r, int b)
    { _dev = dev; _ctx = ctx; _dup = dup; _ol = l; _ot = t; _or = r; _ob = b; }

    /// <summary>True if the rectangle lies wholly on this duplication's monitor.</summary>
    public bool Contains(int l, int t, int r, int b) => l >= _ol && t >= _ot && r <= _or && b <= _ob;

    /// <summary>Sets up duplication of the monitor that holds the given point. Null if it can't be done.</summary>
    public static DesktopDup? Create(int px, int py) => Create(px, py, out _);

    public static DesktopDup? Create(int px, int py, out string why)
    {
        why = "";
        IntPtr factory = IntPtr.Zero;
        try
        {
            int hr = CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory);
            if (hr < 0) { why = $"factory {hr:X8}"; return null; }
            for (uint ai = 0; ; ai++)
            {
                IntPtr adapter;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, ai, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0) { why = why.Length > 0 ? why : "no output holds the island"; return null; }
                try
                {
                    for (uint oi = 0; ; oi++)
                    {
                        IntPtr output;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, 7))(adapter, oi, &output);
                        if (hr < 0) break;
                        try
                        {
                            byte* desc = stackalloc byte[96];
                            if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(output, 7))(output, desc) < 0) continue;
                            int l = *(int*)(desc + 64), t = *(int*)(desc + 68), r = *(int*)(desc + 72), b = *(int*)(desc + 76);
                            if (px < l || px >= r || py < t || py >= b) continue;
                            return Open(adapter, output, l, t, r, b, out why);
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch (Exception e) { why = e.GetType().Name; return null; }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
    }

    private static DesktopDup? Open(IntPtr adapter, IntPtr output, int l, int t, int r, int b, out string why)
    {
        IntPtr dev, ctx, out1 = IntPtr.Zero, dup = IntPtr.Zero;
        int fl;
        int hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, &dev, &fl, &ctx);
        if (hr < 0) { why = $"device {hr:X8}"; return null; }
        try
        {
            hr = Marshal.QueryInterface(output, ref IID_IDXGIOutput1, out out1);
            if (hr < 0) { why = $"output1 {hr:X8}"; goto fail; }
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(out1, 22))(out1, dev, &dup);
            if (hr < 0) { why = $"duplicate {hr:X8}"; goto fail; }
            why = "ok";
            return new DesktopDup(dev, ctx, dup, l, t, r, b);
        }
        finally { if (out1 != IntPtr.Zero) Marshal.Release(out1); }
    fail:
        Marshal.Release(ctx); Marshal.Release(dev);
        return null;
    }

    /// <summary>
    /// Waits up to timeoutMs for the next desktop frame and copies the w×h area at (x, y) into dst (BGRA).
    /// 1 = copied, 0 = nothing new on screen, -1 = duplication lost (dispose and make a new one).
    /// </summary>
    public int Grab(int x, int y, int w, int h, byte[] dst, uint timeoutMs)
    {
        if (_dup == IntPtr.Zero || !Contains(x, y, x + w, y + h) || dst.Length < w * h * 4) return -1;
        if ((w != _sw || h != _sh) && !MakeStage(w, h)) return -1;

        byte* info = stackalloc byte[48];
        IntPtr res;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)Slot(_dup, 8))(_dup, timeoutMs, info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 0;
        if (hr < 0) return -1;
        try
        {
            if (*(long*)info == 0) return 0; // only the mouse moved: the picture is the same
            IntPtr tex;
            if (Marshal.QueryInterface(res, ref IID_ID3D11Texture2D, out tex) < 0) return -1;
            try
            {
                uint* box = stackalloc uint[6];
                box[0] = (uint)(x - _ol); box[1] = (uint)(y - _ot); box[2] = 0;
                box[3] = box[0] + (uint)w; box[4] = box[1] + (uint)h; box[5] = 1;
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, uint*, void>)Slot(_ctx, 46))
                    (_ctx, _stage, 0, 0, 0, 0, tex, 0, box);
            }
            finally { Marshal.Release(tex); }

            byte* map = stackalloc byte[16];
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, byte*, int>)Slot(_ctx, 14))(_ctx, _stage, 0, 1, 0, map) < 0) return -1;
            try
            {
                byte* src = *(byte**)map;
                int pitch = *(int*)(map + 8), row = w * 4;
                fixed (byte* d = dst)
                    for (int yy = 0; yy < h; yy++)
                        Buffer.MemoryCopy(src + (long)yy * pitch, d + (long)yy * row, row, row);
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_ctx, 15))(_ctx, _stage, 0); }
            return 1;
        }
        finally
        {
            Marshal.Release(res);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_dup, 14))(_dup);
        }
    }

    private bool MakeStage(int w, int h)
    {
        if (_stage != IntPtr.Zero) { Marshal.Release(_stage); _stage = IntPtr.Zero; _sw = _sh = 0; }
        uint* d = stackalloc uint[11];
        d[0] = (uint)w; d[1] = (uint)h; d[2] = 1; d[3] = 1; d[4] = 87; // B8G8R8A8_UNORM
        d[5] = 1; d[6] = 0; d[7] = 3; d[8] = 0; d[9] = 0x20000; d[10] = 0; // staging, CPU read
        IntPtr t;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr, IntPtr*, int>)Slot(_dev, 5))(_dev, d, IntPtr.Zero, &t) < 0) return false;
        _stage = t; _sw = w; _sh = h;
        return true;
    }

    public void Dispose()
    {
        if (_stage != IntPtr.Zero) { Marshal.Release(_stage); _stage = IntPtr.Zero; }
        if (_dup != IntPtr.Zero) { Marshal.Release(_dup); _dup = IntPtr.Zero; }
        if (_ctx != IntPtr.Zero) { Marshal.Release(_ctx); _ctx = IntPtr.Zero; }
        if (_dev != IntPtr.Zero) { Marshal.Release(_dev); _dev = IntPtr.Zero; }
    }


    private static IntPtr Slot(IntPtr obj, int i) => (*(IntPtr**)obj)[i];

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numLevels, uint sdk, IntPtr* device, int* level, IntPtr* context);
}
