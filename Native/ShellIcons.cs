using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacShell.Native;

/// <summary>
/// Loads high-resolution icons and thumbnails through IShellItemImageFactory on a
/// small pool of STA worker threads, with an in-memory cache.
/// </summary>
public static class ShellIcons
{
    const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;
    static readonly Guid IID_Factory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<Action> Queue = new(new ConcurrentStack<Action>());
    static readonly BlockingCollection<Action> PriorityQueue = new(new ConcurrentQueue<Action>());
    static bool _started;

    static readonly HashSet<string> ThumbExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".svg", ".avif", ".jfif",
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm", ".pdf", ".psd", ".docx", ".xlsx", ".pptx", ".3mf", ".stl"
    };
    static readonly HashSet<string> UniqueIconExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".ico", ".cur", ".ani", ".msc", ".appref-ms", ".scr", ".cpl"
    };

    static void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < 3; i++)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Action a = null;
                    if (!PriorityQueue.TryTake(out a)) BlockingCollection<Action>.TakeFromAny(new[] { PriorityQueue, Queue }, out a);
                    try { a?.Invoke(); } catch { }
                }
            }) { IsBackground = true, Name = "IconWorker" + i };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public static bool IsThumbnailType(string path) => ThumbExts.Contains(Path.GetExtension(path) ?? "");

    public static string CacheKeyFor(string path, int px, bool thumbnail)
    {
        string ext = Path.GetExtension(path) ?? "";
        if (!thumbnail && !UniqueIconExts.Contains(ext) && File.Exists(path) && ext.Length > 0)
            return "ext:" + ext + ":" + px;
        return (thumbnail ? "thumb:" : "icon:") + path + ":" + px;
    }

    public static BitmapSource TryGetCached(string key) => Cache.TryGetValue(key, out var b) ? b : null;

    /// <summary>Loads an icon/thumbnail asynchronously; callback runs on the UI thread.</summary>
    public static void Load(string parsingName, int px, bool thumbnail, Action<BitmapSource> callback, bool priority = false, string cacheKey = null)
    {
        EnsureStarted();
        cacheKey ??= CacheKeyFor(parsingName, px, thumbnail);
        if (Cache.TryGetValue(cacheKey, out var cached)) { callback(cached); return; }
        var dispatcher = Application.Current.Dispatcher;
        Action work = () =>
        {
            var bmp = Cache.TryGetValue(cacheKey, out var c) ? c : Get(parsingName, px, thumbnail);
            if (bmp != null) Cache[cacheKey] = bmp;
            dispatcher.BeginInvoke(() => callback(bmp), System.Windows.Threading.DispatcherPriority.Background);
        };
        (priority ? PriorityQueue : Queue).Add(work);
    }

    public static Task<BitmapSource> LoadAsync(string parsingName, int px, bool thumbnail = false)
    {
        var tcs = new TaskCompletionSource<BitmapSource>();
        Load(parsingName, px, thumbnail, b => tcs.TrySetResult(b), true);
        return tcs.Task;
    }

    /// <summary>Synchronous fetch — call from an STA thread.</summary>
    public static BitmapSource Get(string parsingName, int px, bool thumbnail)
    {
        object obj = null;
        try
        {
            var iid = IID_Factory;
            NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out obj);
            if (obj is not IShellItemImageFactory factory) return null;
            int flags = thumbnail ? SIIGBF_RESIZETOFIT : SIIGBF_ICONONLY;
            int hr = factory.GetImage(new SIZE(px, px), flags, out IntPtr hbmp);
            if (hr != 0 && thumbnail) hr = factory.GetImage(new SIZE(px, px), SIIGBF_ICONONLY, out hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            BitmapSource result;
            try { result = FromHBitmap(hbmp); }
            finally { NativeMethods.DeleteObject(hbmp); }
            // Apps that ship only small icons come back as a tiny icon inside a grey frame (or in a corner)
            // at jumbo sizes; fetch the native small icon instead, like macOS shows legacy icons.
            if (!thumbnail && px >= 96 && result != null && IsUpscaledSmallIcon(result))
            {
                Marshal.ReleaseComObject(obj);
                obj = null;
                return Get(parsingName, 48, false) ?? result;
            }
            return result;
        }
        catch { return null; }
        finally { if (obj != null) Marshal.ReleaseComObject(obj); }
    }

    /// <summary>
    /// Detects Windows' jumbo rendering of a small icon: either framed (opaque edge ring with an empty band
    /// inside) or drawn small in a corner (content bounding box much smaller than the canvas).
    /// </summary>
    static bool IsUpscaledSmallIcon(BitmapSource bmp)
    {
        try
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w < 64 || h < 64) return false;
            int stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);
            int A(int x, int y) => px[y * stride + x * 4 + 3];

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2)
                    if (A(x, y) > 24) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            if (maxX < 0) return false;
            if ((maxX - minX) < w * 0.42 && (maxY - minY) < h * 0.42) return true;

            int edge = 0, edgeOpaque = 0, band = 0, bandClear = 0;
            int depth = Math.Max(3, w / 24);   // the frame line sits somewhere in the outermost few pixels
            for (int i = w / 8; i < w - w / 8; i += 3)
            {
                int top = 0, bottom = 0, left = 0, right = 0;
                for (int d = 0; d < depth; d++)
                {
                    top = Math.Max(top, A(i, d)); bottom = Math.Max(bottom, A(i, h - 1 - d));
                    left = Math.Max(left, A(d, i)); right = Math.Max(right, A(w - 1 - d, i));
                }
                foreach (var a in new[] { top, bottom, left, right }) { edge++; if (a > 40) edgeOpaque++; }
            }
            foreach (double f in new[] { 0.12, 0.2, 0.27 })
            {
                int o = (int)(w * f);
                for (int i = o; i < w - o; i += 3)
                    foreach (var (x, y) in new[] { (i, o), (i, h - 1 - o), (o, i), (w - 1 - o, i) }) { band++; if (A(x, y) < 12) bandClear++; }
            }
            return edge > 0 && band > 0 && edgeOpaque > edge * 0.7 && bandClear > band * 0.85;
        }
        catch { return false; }
    }

    /// <summary>
    /// Converts a shell HBITMAP to a BitmapSource. GDI is asked for top-down 32-bit rows (so orientation is
    /// always right — video thumbnails used to come out upside down), and the alpha channel is inspected to
    /// decide whether it is straight or premultiplied (treating straight alpha as premultiplied is what
    /// produced the bright/white fringes around icon edges).
    /// </summary>
    public static BitmapSource FromHBitmap(IntPtr hbmp)
    {
        if (NativeMethods.GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) == 0) return null;
        int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight);
        if (w <= 0 || h <= 0) return null;
        var bmi = new BITMAPINFO32
        {
            bmiHeader = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 },
        };
        int stride = w * 4;
        var px = new byte[stride * h];
        IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
        int lines;
        try { lines = NativeMethods.GetDIBits(hdc, hbmp, 0, (uint)h, px, ref bmi, 0); }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, hdc); }
        if (lines == 0)
        {
            var b = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            b.Freeze();
            return b;
        }

        bool anyAlpha = false, straight = false;
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            if (a != 0) anyAlpha = true;
            // In premultiplied data no colour channel can exceed alpha; if one does, the data is straight alpha.
            if (a < 255 && (px[i] > a || px[i + 1] > a || px[i + 2] > a)) { straight = true; if (anyAlpha) break; }
        }
        PixelFormat format;
        if (!anyAlpha)
        {
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;   // e.g. JPEG thumbnails carry no alpha
            format = PixelFormats.Bgra32;
        }
        else format = straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        var bmp = BitmapSource.Create(w, h, 96, 96, format, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    public static string GetTypeName(string path, bool isFolder)
    {
        if (isFolder) return "Folder";
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return "Document";
        return TypeNames.GetOrAdd(ext, e =>
        {
            var info = new SHFILEINFO();
            NativeMethods.SHGetFileInfo(e, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), NativeMethods.SHGFI_TYPENAME | NativeMethods.SHGFI_USEFILEATTRIBUTES);
            string n = info.szTypeName;
            return string.IsNullOrWhiteSpace(n) ? e.TrimStart('.').ToUpperInvariant() + " File" : n;
        });
    }
}
