using System.Diagnostics;
using System.Runtime.InteropServices;
using static MacShell.Native.NativeMethods;

namespace MacShell.Native;

/// <summary>A part of the screen, frame after frame, copied into a 32-bit top-down BGRA buffer.</summary>
public interface IScreenGrabber : IDisposable
{
    /// <summary>Takes in what's new on the screen, waiting for it up to <paramref name="waitMs"/>.</summary>
    void Update(int waitMs);
    /// <summary>The area as it is now (the last picture taken in), into <paramref name="bits"/> (top row first).</summary>
    void CopyTo(IntPtr bits, int stride);
    string Method { get; }
}

/// <summary>
/// The screen's picture through DXGI Desktop Duplication: what Windows composes for a display, straight from the
/// graphics card (a GDI screenshot of a 1080p screen takes 20-40 ms; this, a few). It only reads: the display's mode,
/// colors and gamma are never touched (of the output's methods only GetDesc and DuplicateOutput are called). The pointer
/// isn't in the picture - it's drawn on separately. One display at a time; use it on one thread.
/// </summary>
public sealed unsafe class DesktopDuplication : IScreenGrabber
{
    // vtable slots, in dxgi.h / dxgi1_2.h / d3d11.h order - these are the only ones called
    const int QueryInterfaceSlot = 0, ReleaseSlot = 2;
    const int Factory1_EnumAdapters1 = 12;   // IDXGIObject 3-6, IDXGIFactory 7-11, IDXGIFactory1 12-13
    const int Adapter_EnumOutputs = 7;       // IDXGIAdapter 7-9
    const int Output_GetDesc = 7;            // IDXGIOutput 7-18
    const int Output1_DuplicateOutput = 22;  // IDXGIOutput1 19-22
    const int Dupl_GetDesc = 7, Dupl_AcquireNextFrame = 8, Dupl_ReleaseFrame = 14;   // IDXGIOutputDuplication 7-14
    const int Device_CreateTexture2D = 5;
    const int Texture2D_GetDesc = 10;        // ID3D11DeviceChild 3-6, ID3D11Resource 7-9
    const int Context_Map = 14, Context_Unmap = 15, Context_CopyResource = 47;

    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels, uint sdk, IntPtr* device, int* level, IntPtr* context);

    IntPtr _adapter, _output1, _device, _context, _dupl, _staging;
    readonly RECT _area;       // the part recorded (screen pixels)
    RECT _outRect;             // the display's place on the desktop
    int _rotation;             // DXGI_MODE_ROTATION: 1 none, 2 90°, 3 180°, 4 270°
    int _texW, _texH;          // the duplicated picture (as the panel has it)
    bool _haveFrame;
    long _lastRetry;
    /// <summary>For 90° and 270°: which way the picture turns (checked against a GDI screenshot when it starts).</summary>
    bool _quarterSwap;

    public string Method => "desktop duplication" + (_rotation is 2 or 3 or 4 ? $" (display turned {(_rotation - 1) * 90}°)" : "");

    public DesktopDuplication(RECT area)
    {
        _area = area;
        try { Open(); }
        catch { Dispose(); throw; }
    }

    static void* Slot(IntPtr o, int i) => (*(void***)o)[i];

    static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, ReleaseSlot))(p);
        p = IntPtr.Zero;
    }

    static int QueryInterface(IntPtr o, Guid iid, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(o, QueryInterfaceSlot))(o, &iid, &r);
        result = hr >= 0 ? r : IntPtr.Zero;
        return hr;
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new IOException($"The screen couldn’t be recorded ({what}: 0x{hr:X8}).");
    }

    void Open()
    {
        // the display the area is on (its graphics card makes the device: duplication needs it to be that one)
        IntPtr factory = IntPtr.Zero, output = IntPtr.Zero;
        try
        {
            Guid iid = IID_IDXGIFactory1;
            Check(CreateDXGIFactory1(&iid, &factory), "DXGI");
            int cx = _area.Left + _area.Width / 2, cy = _area.Top + _area.Height / 2;
            var desc = stackalloc byte[96];   // DXGI_OUTPUT_DESC
            for (uint a = 0; output == IntPtr.Zero; a++)
            {
                IntPtr adapter;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, Factory1_EnumAdapters1))(factory, a, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Check(hr, "the graphics cards");
                for (uint o = 0; ; o++)
                {
                    IntPtr outp;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, Adapter_EnumOutputs))(adapter, o, &outp);
                    if (hr < 0) break;
                    hr =((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(outp, Output_GetDesc))(outp, desc);
                    var r = *(RECT*)(desc + 64);
                    bool attached = *(int*)(desc + 80) != 0;
                    if (hr >= 0 && attached && cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom)
                    {
                        output = outp;
                        _outRect = r;
                        _rotation = *(int*)(desc + 84);
                        _adapter = adapter;
                        break;
                    }
                    Release(ref outp);
                }
                if (output == IntPtr.Zero) Release(ref adapter);
            }
            if (output == IntPtr.Zero) throw new IOException("The screen couldn’t be recorded (no display shows that part of the screen).");

            IntPtr dev, ctx;
            int level;
            Check(D3D11CreateDevice(_adapter, 0 /*UNKNOWN: the adapter given*/, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/, IntPtr.Zero, 0, 7, &dev, &level, &ctx), "Direct3D");
            _device = dev; _context = ctx;
            Check(QueryInterface(output, IID_IDXGIOutput1, out _output1), "the display");
        }
        finally
        {
            Release(ref output);
            Release(ref factory);
        }
        Duplicate();
    }

    void Duplicate()
    {
        IntPtr dupl;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(_output1, Output1_DuplicateOutput))(_output1, _device, &dupl), "Desktop Duplication");
        _dupl = dupl;
        var d = stackalloc byte[64];   // DXGI_OUTDUPL_DESC: ModeDesc (Width, Height, RefreshRate, Format, ScanlineOrdering, Scaling), Rotation, DesktopImageInSystemMemory
        ((delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)Slot(_dupl, Dupl_GetDesc))(_dupl, d);
        _rotation = *(int*)(d + 28);
    }

    /// <summary>The staging copy (CPU-readable), made to match the duplicated picture.</summary>
    void EnsureStaging(IntPtr tex)
    {
        // D3D11_TEXTURE2D_DESC: Width, Height, MipLevels, ArraySize, Format, SampleDesc (Count, Quality), Usage, BindFlags,
        // CPUAccessFlags, MiscFlags
        var desc = stackalloc uint[11];
        ((delegate* unmanaged[Stdcall]<IntPtr, uint*, void>)Slot(tex, Texture2D_GetDesc))(tex, desc);
        if (_staging != IntPtr.Zero && desc[0] == _texW && desc[1] == _texH) return;
        if (desc[4] != DXGI_FORMAT_B8G8R8A8_UNORM) throw new IOException($"The screen couldn’t be recorded (its picture format {desc[4]} isn’t supported).");
        Release(ref _staging);
        _texW = (int)desc[0]; _texH = (int)desc[1];
        var s = stackalloc uint[11];
        s[0] = desc[0]; s[1] = desc[1]; s[2] = 1; s[3] = 1; s[4] = DXGI_FORMAT_B8G8R8A8_UNORM;
        s[5] = 1; s[6] = 0;            // one sample
        s[7] = 3;                      // D3D11_USAGE_STAGING
        s[8] = 0;                      // no binding
        s[9] = 0x20000;                // D3D11_CPU_ACCESS_READ
        s[10] = 0;
        IntPtr staging;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr, IntPtr*, int>)Slot(_device, Device_CreateTexture2D))(_device, s, IntPtr.Zero, &staging), "a copy of the screen");
        _staging = staging;
    }

    public void Update(int waitMs)
    {
        if (_dupl == IntPtr.Zero)
        {
            // (lost - a new screen mode, the sign-in screen …: tried again now and then; the last picture stays)
            if (Stopwatch.GetTimestamp() - _lastRetry < Stopwatch.Frequency / 4) { if (waitMs > 0) Thread.Sleep(waitMs); return; }
            _lastRetry = Stopwatch.GetTimestamp();
            try { Duplicate(); } catch { if (waitMs > 0) Thread.Sleep(waitMs); return; }
        }
        var info = stackalloc byte[64];   // DXGI_OUTDUPL_FRAME_INFO
        IntPtr res;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)Slot(_dupl, Dupl_AcquireNextFrame))(_dupl, (uint)Math.Max(0, waitMs), info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return;
        if (hr == DXGI_ERROR_ACCESS_LOST || hr < 0) { Release(ref _dupl); return; }
        try
        {
            long lastPresent = *(long*)info;
            if (lastPresent != 0 || !_haveFrame)
            {
                if (QueryInterface(res, IID_ID3D11Texture2D, out IntPtr tex) >= 0)
                {
                    try
                    {
                        EnsureStaging(tex);
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_context, Context_CopyResource))(_context, _staging, tex);
                        _haveFrame = true;
                    }
                    finally { Release(ref tex); }
                }
            }
        }
        finally
        {
            Release(ref res);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_dupl, Dupl_ReleaseFrame))(_dupl);
        }
    }

    public void CopyTo(IntPtr bits, int stride)
    {
        int w = _area.Width, h = _area.Height;
        if (!_haveFrame || _staging == IntPtr.Zero)
        {
            for (int y = 0; y < h; y++) new Span<byte>((byte*)bits + (long)y * stride, w * 4).Clear();
            return;
        }
        var mapped = stackalloc byte[16];   // D3D11_MAPPED_SUBRESOURCE: pData, RowPitch, DepthPitch
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, byte*, int>)Slot(_context, Context_Map))(_context, _staging, 0, 1 /*READ*/, 0, mapped);
        if (hr < 0) return;
        try
        {
            byte* src = *(byte**)mapped;
            int pitch = *(int*)(mapped + 8);
            Copy(src, pitch, (byte*)bits, stride, w, h);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, Context_Unmap))(_context, _staging, 0);
        }
    }

    /// <summary>The area out of the display's picture, turned upright if the display is turned.</summary>
    void Copy(byte* src, int pitch, byte* dst, int stride, int w, int h)
    {
        int ox = _area.Left - _outRect.Left, oy = _area.Top - _outRect.Top;
        int W = _outRect.Width, H = _outRect.Height;
        switch (_rotation)
        {
            case 3:   // 180°
                for (int y = 0; y < h; y++)
                {
                    uint* d = (uint*)(dst + (long)y * stride);
                    int ty = H - 1 - (oy + y);
                    if (ty < 0 || ty >= _texH) { new Span<uint>(d, w).Clear(); continue; }
                    uint* s = (uint*)(src + (long)ty * pitch);
                    for (int x = 0; x < w; x++)
                    {
                        int tx = W - 1 - (ox + x);
                        d[x] = tx >= 0 && tx < _texW ? s[tx] : 0;
                    }
                }
                break;
            case 2 or 4:   // a quarter turn: one way or the other (see _quarterSwap)
                bool cw = (_rotation == 2) ^ _quarterSwap;
                for (int y = 0; y < h; y++)
                {
                    uint* d = (uint*)(dst + (long)y * stride);
                    int Y = oy + y;
                    for (int x = 0; x < w; x++)
                    {
                        int X = ox + x;
                        int tx = cw ? Y : H - 1 - Y, ty = cw ? W - 1 - X : X;
                        d[x] = tx >= 0 && tx < _texW && ty >= 0 && ty < _texH ? *(uint*)(src + (long)ty * pitch + tx * 4) : 0;
                    }
                }
                break;
            default:
                for (int y = 0; y < h; y++)
                {
                    int ty = oy + y;
                    if (ty < 0 || ty >= _texH) { new Span<byte>(dst + (long)y * stride, w * 4).Clear(); continue; }
                    int x0 = Math.Max(0, ox), x1 = Math.Min(_texW, ox + w);
                    if (x1 > x0) Buffer.MemoryCopy(src + (long)ty * pitch + x0 * 4, dst + (long)y * stride + (x0 - ox) * 4, (x1 - x0) * 4, (x1 - x0) * 4);
                }
                break;
        }
    }

    /// <summary>
    /// For a display turned a quarter: which way the duplicated picture turns isn't the same on every graphics card, so
    /// a frame is compared with a GDI screenshot of the same spot, both ways; the closer one is kept.
    /// </summary>
    public void CheckQuarterTurn(Func<RECT, uint[]> gdiShot)
    {
        if (_rotation is not (2 or 4)) return;
        for (int i = 0; i < 20 && !_haveFrame; i++) Update(50);
        if (!_haveFrame) return;
        int w = Math.Min(160, _area.Width), h = Math.Min(160, _area.Height);
        var spot = new RECT(_area.Left + (_area.Width - w) / 2, _area.Top + (_area.Height - h) / 2, 0, 0);
        spot = new RECT(spot.Left, spot.Top, spot.Left + w, spot.Top + h);
        var reference = gdiShot(spot);
        if (reference == null) return;
        long Diff(bool swap)
        {
            _quarterSwap = swap;
            var buf = new uint[_area.Width * _area.Height];
            fixed (uint* p = buf) CopyTo((IntPtr)p, _area.Width * 4);
            long sum = 0;
            int sx = spot.Left - _area.Left, sy = spot.Top - _area.Top;
            for (int y = 0; y < h; y += 4)
                for (int x = 0; x < w; x += 4)
                {
                    uint a = buf[(sy + y) * _area.Width + sx + x], b = reference[y * w + x];
                    sum += Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
                }
            return sum;
        }
        long plain = Diff(false), swapped = Diff(true);
        _quarterSwap = swapped < plain;
    }

    public void Dispose()
    {
        Release(ref _staging);
        Release(ref _dupl);
        Release(ref _output1);
        Release(ref _context);
        Release(ref _device);
        Release(ref _adapter);
    }
}

/// <summary>
/// A frame being made: a 32-bit top-down DIB section (so GDI can draw the pointer on it) with its memory DC.
/// </summary>
public sealed unsafe class FrameCanvas : IDisposable
{
    public readonly int Width, Height, Stride;
    public readonly IntPtr Bits, Dc;
    readonly IntPtr _dib, _old;

    public FrameCanvas(int width, int height)
    {
        Width = width; Height = height; Stride = width * 4;
        IntPtr screen = GetDC(IntPtr.Zero);
        Dc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        _dib = ScreenGdi.CreateDIBSection(Dc, ref bi, 0, out Bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) { DeleteDC(Dc); throw new IOException("The screen couldn’t be recorded (not enough memory for a frame)."); }
        _old = SelectObject(Dc, _dib);
    }

    /// <summary>
    /// The pointer as it is on the screen now, drawn where it is in <paramref name="area"/> (screen pixels); with
    /// <paramref name="clicks"/>, a dark ring around it while a mouse button is down (Show Mouse Clicks).
    /// </summary>
    public void DrawPointer(RECT area, bool clicks)
    {
        var ci = new ScreenGdi.CURSORINFO { cbSize = Marshal.SizeOf<ScreenGdi.CURSORINFO>() };
        if (!ScreenGdi.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (ScreenGdi.GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        int px = ci.ptScreenPos.X - area.Left, py = ci.ptScreenPos.Y - area.Top;
        if (clicks && ((ScreenGdi.GetAsyncKeyState(0x01) | ScreenGdi.GetAsyncKeyState(0x02)) & 0x8000) != 0)
            Ring(px, py, (int)Math.Round(17 * ScreenGdi.ScaleAt(ci.ptScreenPos)));
        ScreenGdi.DrawIconEx(Dc, px - hx, py - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 0x0003 /*DI_NORMAL*/);
    }

    /// <summary>macOS's click ring: a dark translucent disc around the pointer.</summary>
    void Ring(int cx, int cy, int r)
    {
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(Width - 1, cx + r), y0 = Math.Max(0, cy - r), y1 = Math.Min(Height - 1, cy + r);
        for (int y = y0; y <= y1; y++)
        {
            uint* row = (uint*)((byte*)Bits + (long)y * Stride);
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d > r) continue;
                double a = 0.42 * Math.Clamp(r - d, 0, 1);   // (soft edge, one pixel)
                uint p = row[x];
                uint b = (uint)((p & 0xFF) * (1 - a)), g = (uint)(((p >> 8) & 0xFF) * (1 - a)), rr = (uint)(((p >> 16) & 0xFF) * (1 - a));
                row[x] = 0xFF000000 | (rr << 16) | (g << 8) | b;
            }
        }
    }

    public void Dispose()
    {
        SelectObject(Dc, _old);
        DeleteObject(_dib);
        DeleteDC(Dc);
    }
}

/// <summary>The GDI and pointer calls the screen recorder needs.</summary>
public static class ScreenGdi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    /// <summary>The display scale where a point is (1 = 100 %).</summary>
    public static double ScaleAt(POINT p)
    {
        var mon = MonitorFromPoint(p, 2 /*NEAREST*/);
        return mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
    }
}

/// <summary>The screen through GDI (when Desktop Duplication can't be had): slower, but it works everywhere.</summary>
public sealed class GdiScreenGrabber : IScreenGrabber
{
    readonly RECT _area;
    readonly IntPtr _screen;
    readonly FrameCanvas _canvas;

    public string Method => "GDI";

    public GdiScreenGrabber(RECT area)
    {
        _area = area;
        _screen = GetDC(IntPtr.Zero);
        _canvas = new FrameCanvas(area.Width, area.Height);
    }

    public void Update(int waitMs) { if (waitMs > 0) Thread.Sleep(waitMs); }

    public unsafe void CopyTo(IntPtr bits, int stride)
    {
        // (no CAPTUREBLT: with it, Windows hides the pointer for every frame - it would flicker)
        BitBlt(_canvas.Dc, 0, 0, _area.Width, _area.Height, _screen, _area.Left, _area.Top, 0x00CC0020);
        ScreenGdi.GdiFlush();
        int row = _area.Width * 4;
        for (int y = 0; y < _area.Height; y++) Buffer.MemoryCopy((byte*)_canvas.Bits + (long)y * _canvas.Stride, (byte*)bits + (long)y * stride, row, row);
    }

    /// <summary>A GDI screenshot of a spot (screen pixels), as BGRA values a row at a time.</summary>
    public static unsafe uint[] Shot(RECT r)
    {
        using var g = new GdiScreenGrabber(r);
        var buf = new uint[r.Width * r.Height];
        fixed (uint* p = buf) g.CopyTo((IntPtr)p, r.Width * 4);
        return buf;
    }

    public void Dispose()
    {
        _canvas.Dispose();
        ReleaseDC(IntPtr.Zero, _screen);
    }
}
