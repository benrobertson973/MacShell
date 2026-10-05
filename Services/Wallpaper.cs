using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MacShell.Native;

namespace MacShell.Services;

/// <summary>
/// Wallpaper management: procedurally rendered macOS-style wallpapers, custom images,
/// and a pre-blurred copy used to fake "vibrancy" for the menu bar, Dock and Launchpad.
/// </summary>
public static class Wallpaper
{
    public static BitmapSource Image { get; private set; }
    public static BitmapSource Blurred { get; private set; }
    public static bool TopIsDark { get; private set; }
    public static bool BottomIsDark { get; private set; }
    public static event Action Changed;

    public static readonly (string id, string name)[] BuiltIn =
    {
        ("gen:sonoma", "Sonoma Horizon"),
        ("gen:sequoia", "Sequoia"),
        ("gen:ventura", "Ventura"),
        ("gen:monterey", "Monterey"),
        ("gen:bigsur", "Big Sur Dusk"),
        ("gen:tahoe", "Tahoe Light"),
        ("gen:graphite", "Graphite"),
        ("gen:blue", "Blue Gradient"),
    };

    static string CacheDir => Path.Combine(Settings.DataDirectory, "wallpapers");

    public static (int w, int h) ScreenPixels()
    {
        var mons = NativeMethods.GetMonitors();
        var p = mons.FirstOrDefault(m => m.primary);
        if (p.handle == IntPtr.Zero && mons.Count > 0) p = mons[0];
        return (Math.Max(640, p.bounds.Width), Math.Max(480, p.bounds.Height));
    }

    public static string CurrentWindowsWallpaper()
    {
        var sb = new StringBuilder(520);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
        string s = sb.ToString();
        return File.Exists(s) ? s : null;
    }

    public static void Load()
    {
        string id = Settings.Current.Wallpaper ?? "gen:sonoma";
        var (w, h) = ScreenPixels();
        BitmapSource img = null;
        try
        {
            if (id.StartsWith("gen:")) img = GetGenerated(id, w, h);
            else
            {
                string path = id == "windows" ? CurrentWindowsWallpaper() : id;
                if (path != null && File.Exists(path)) img = LoadImageFile(path, w, h);
            }
        }
        catch { }
        if (img == null) { id = "gen:sonoma"; img = GetGenerated(id, w, h); }
        Image = img;
        Blurred = MakeBlurred(img);
        AnalyzeLuminance();
        Changed?.Invoke();
        if (ShellHost.TakeoverEnabled && Settings.Current.SyncWindowsWallpaper) SyncToWindows(id, w, h);
    }

    /// <summary>
    /// Gives Windows the same wallpaper, so the moment between signing in and MacShell appearing shows the
    /// Mac wallpaper rather than a different Windows one. Only touches Windows when the picture differs.
    /// </summary>
    static void SyncToWindows(string id, int w, int h)
    {
        string file = id.StartsWith("gen:") ? Path.Combine(CacheDir, $"{id[4..]}_{w}x{h}_v3.png") : id == "windows" ? null : id;
        if (file == null || !File.Exists(file)) return;
        Task.Run(() =>
        {
            try
            {
                if (string.Equals(CurrentWindowsWallpaper(), file, StringComparison.OrdinalIgnoreCase)) return;
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETDESKWALLPAPER, 0, file, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
            }
            catch { }
        });
    }

    static BitmapSource LoadImageFile(string path, int w, int h)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = Math.Min(3840, Math.Max(w, 1920));
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    static BitmapSource GetGenerated(string id, int w, int h)
    {
        Directory.CreateDirectory(CacheDir);
        string file = Path.Combine(CacheDir, $"{id[4..]}_{w}x{h}_v3.png");
        if (File.Exists(file))
        {
            try { return LoadImageFile(file, w, h); } catch { }
        }
        var bmp = Render(id, w, h);
        try
        {
            using var fs = File.Create(file);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(fs);
        }
        catch { }
        return bmp;
    }

    public static ImageSource Thumbnail(string id)
    {
        if (id.StartsWith("gen:")) return Render(id, 240, 150);
        try
        {
            string path = id == "windows" ? CurrentWindowsWallpaper() : id;
            if (path == null) return null;
            var bi = new BitmapImage();
            bi.BeginInit(); bi.UriSource = new Uri(path); bi.DecodePixelWidth = 240; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    static BitmapSource MakeBlurred(BitmapSource src)
    {
        int bw = 320, bh = Math.Max(1, (int)Math.Round(320.0 * src.PixelHeight / src.PixelWidth));
        var dv = new DrawingVisual { Effect = new BlurEffect { Radius = 9, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
        using (var dc = dv.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            // draw slightly oversized so the blur has no transparent edge
            dc.DrawImage(src, new Rect(-12, -12, bw + 24, bh + 24));
        }
        var rtb = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static void AnalyzeLuminance()
    {
        try
        {
            var b = Blurred;
            int w = b.PixelWidth, h = b.PixelHeight;
            var px = new byte[w * h * 4];
            b.CopyPixels(px, w * 4, 0);
            double Lum(int y0, int y1)
            {
                double sum = 0; int n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < w; x += 2)
                    {
                        int i = (y * w + x) * 4;
                        sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                        n++;
                    }
                return n == 0 ? 128 : sum / n;
            }
            TopIsDark = Lum(0, Math.Max(1, h / 30)) < 118;
            BottomIsDark = Lum(h - Math.Max(1, h / 10), h) < 118;
        }
        catch { TopIsDark = false; BottomIsDark = false; }
    }

    /// <summary>A brush showing the blurred wallpaper region behind the given screen rectangle (DIPs).</summary>
    public static ImageBrush BlurBrush(Rect screenRect, Size screenSize)
    {
        var brush = new ImageBrush(Blurred)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(screenRect.X / screenSize.Width, screenRect.Y / screenSize.Height,
                               screenRect.Width / screenSize.Width, screenRect.Height / screenSize.Height),
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        return brush;
    }

    // ================================================================ rendering

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static LinearGradientBrush V(params (string c, double o)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (c, o) in stops) b.GradientStops.Add(new GradientStop(C(c), o));
        b.Freeze();
        return b;
    }

    static LinearGradientBrush L(Point a, Point b, params (string c, double o)[] stops)
    {
        var br = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        foreach (var (c, o) in stops) br.GradientStops.Add(new GradientStop(C(c), o));
        br.Freeze();
        return br;
    }

    /// <summary>Filled wave band: from a sum-of-sines ridge line down to the bottom of the canvas.</summary>
    static Geometry Ridge(double w, double h, double baseY, (double amp, double freq, double phase)[] waves, double bottom = -1)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            int n = 220;
            for (int i = 0; i <= n; i++)
            {
                double x = i / (double)n;
                double y = baseY;
                foreach (var (amp, freq, phase) in waves) y += amp * Math.Sin(x * freq * Math.PI * 2 + phase);
                var p = new Point(x * w, y * h);
                if (i == 0) ctx.BeginFigure(new Point(0, (bottom < 0 ? 1.2 : bottom) * h), true, true);
                ctx.LineTo(p, true, true);
            }
            ctx.LineTo(new Point(w, (bottom < 0 ? 1.2 : bottom) * h), true, true);
        }
        sg.Freeze();
        return sg;
    }

    static void Layer(ContainerVisual root, Action<DrawingContext> draw, double blur = 0)
    {
        var dv = new DrawingVisual();
        if (blur > 0) dv.Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian };
        using (var dc = dv.RenderOpen()) draw(dc);
        root.Children.Add(dv);
    }

    public static BitmapSource Render(string id, int w, int h)
    {
        var root = new ContainerVisual();
        double k = w / 2560.0; // blur radii scale with output size
        var full = new Rect(0, 0, w, h);
        switch (id)
        {
            case "gen:sonoma":
                Layer(root, dc => dc.DrawRectangle(V(("#6FA9DE", 0), ("#A8CBE6", 0.38), ("#E4D9C4", 0.62), ("#F1CFA6", 1)), null, full));
                Layer(root, dc => dc.DrawGeometry(V(("#9DB7A6", 0), ("#7FA792", 1)), null, Ridge(w, h, 0.56, new[] { (0.035, 0.8, 0.4), (0.018, 2.1, 1.3) })), 18 * k);
                Layer(root, dc => dc.DrawGeometry(V(("#6E9F7B", 0), ("#3F7A5C", 1)), null, Ridge(w, h, 0.64, new[] { (0.05, 0.62, 2.2), (0.02, 1.7, 0.2) })), 8 * k);
                Layer(root, dc => dc.DrawRectangle(V(("#00FFFFFF", 0), ("#00FFFFFF", 0.55), ("#40FFF3E0", 0.68), ("#00FFFFFF", 0.8)), null, full));
                Layer(root, dc => dc.DrawGeometry(V(("#4F8F5E", 0), ("#2B6446", 1)), null, Ridge(w, h, 0.74, new[] { (0.06, 0.5, 4.1), (0.025, 1.4, 1.1) })), 4 * k);
                Layer(root, dc => dc.DrawGeometry(V(("#3B7A4C", 0), ("#1E4A35", 1)), null, Ridge(w, h, 0.86, new[] { (0.05, 0.7, 0.9), (0.02, 1.9, 2.4) })), 2 * k);
                Layer(root, dc => dc.DrawRectangle(new RadialGradientBrush(C("#35FFF6DC"), C("#00FFF6DC")) { Center = new Point(0.72, 0.5), GradientOrigin = new Point(0.72, 0.5), RadiusX = 0.5, RadiusY = 0.4 }, null, full));
                break;

            case "gen:sequoia":
                Layer(root, dc => dc.DrawRectangle(V(("#0A1440", 0), ("#10245E", 0.5), ("#0B1336", 1)), null, full));
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 1), ("#2A6BD8", 0), ("#1FB3C9", 0.5), ("#1A3E9E", 1)), null, Ridge(w, h, 0.45, new[] { (0.12, 0.55, 0.3), (0.04, 1.4, 1.9) })), 60 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#5A2DB8", 0), ("#2F6CE0", 0.5), ("#23C1D6", 1)), null, Ridge(w, h, 0.62, new[] { (0.1, 0.45, 2.6), (0.035, 1.2, 0.4) })), 30 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#1B2B86", 0), ("#3B2AA8", 0.6), ("#0F4FA6", 1)), null, Ridge(w, h, 0.8, new[] { (0.08, 0.5, 4.6), (0.03, 1.5, 2.2) })), 14 * k);
                Layer(root, dc => dc.DrawRectangle(new RadialGradientBrush(C("#3055E0FF"), C("#0055E0FF")) { Center = new Point(0.3, 0.35), GradientOrigin = new Point(0.3, 0.35), RadiusX = 0.6, RadiusY = 0.5 }, null, full));
                break;

            case "gen:ventura":
                Layer(root, dc => dc.DrawRectangle(L(new Point(0, 0), new Point(1, 1), ("#FFB36B", 0), ("#F2703F", 0.45), ("#C2274F", 1)), null, full));
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#FFD27A", 0), ("#FF8A3D", 0.5), ("#F04E4E", 1)), null, Ridge(w, h, 0.42, new[] { (0.16, 0.5, 0.2), (0.05, 1.3, 2.0) })), 40 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#FF6A3D", 0), ("#E63B5A", 0.6), ("#A81F5E", 1)), null, Ridge(w, h, 0.6, new[] { (0.12, 0.6, 2.8), (0.04, 1.6, 0.7) })), 20 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#D8344C", 0), ("#8E1B55", 1)), null, Ridge(w, h, 0.8, new[] { (0.08, 0.7, 1.2), (0.03, 1.8, 3.1) })), 8 * k);
                break;

            case "gen:monterey":
                Layer(root, dc => dc.DrawRectangle(V(("#3B1F8C", 0), ("#7A3FB5", 0.5), ("#D15A9E", 1)), null, full));
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#5B6CF0", 0), ("#9B5DE5", 0.5), ("#F15BB5", 1)), null, Ridge(w, h, 0.38, new[] { (0.14, 0.45, 1.0), (0.05, 1.2, 0.3) })), 50 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#3A86FF", 0), ("#8338EC", 0.55), ("#FF5DA2", 1)), null, Ridge(w, h, 0.58, new[] { (0.11, 0.55, 3.2), (0.04, 1.5, 1.4) })), 22 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#2A1B6E", 0), ("#5B2A9E", 0.5), ("#A0327E", 1)), null, Ridge(w, h, 0.78, new[] { (0.09, 0.6, 5.0), (0.03, 1.7, 2.0) })), 10 * k);
                break;

            case "gen:bigsur":
                Layer(root, dc => dc.DrawRectangle(V(("#16224F", 0), ("#3D3A86", 0.35), ("#9C5A9E", 0.6), ("#F59A7A", 0.8), ("#FFC28A", 1)), null, full));
                Layer(root, dc => dc.DrawGeometry(V(("#5A3F86", 0), ("#3A2A63", 1)), null, Ridge(w, h, 0.6, new[] { (0.06, 1.1, 0.6), (0.03, 3.2, 1.7), (0.012, 7.5, 0.3) })), 6 * k);
                Layer(root, dc => dc.DrawGeometry(V(("#3A2862", 0), ("#221840", 1)), null, Ridge(w, h, 0.7, new[] { (0.07, 0.9, 2.3), (0.03, 2.6, 0.2), (0.01, 8.0, 1.2) })), 3 * k);
                Layer(root, dc => dc.DrawGeometry(V(("#231A3F", 0), ("#120D24", 1)), null, Ridge(w, h, 0.82, new[] { (0.07, 0.7, 4.0), (0.025, 2.2, 1.0) })), 1.5 * k);
                break;

            case "gen:tahoe":
                Layer(root, dc => dc.DrawRectangle(V(("#BFE3F7", 0), ("#DDEFF7", 0.45), ("#F4F1EA", 1)), null, full));
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#7CC4F2", 0), ("#A7D8F5", 0.5), ("#5FB3EA", 1)), null, Ridge(w, h, 0.5, new[] { (0.12, 0.5, 0.8), (0.04, 1.3, 2.2) })), 45 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#3E8FDB", 0), ("#6FB7EE", 0.5), ("#2F79C9", 1)), null, Ridge(w, h, 0.68, new[] { (0.1, 0.55, 3.5), (0.035, 1.6, 0.9) })), 20 * k);
                Layer(root, dc => dc.DrawGeometry(L(new Point(0, 0), new Point(1, 0), ("#2A6CC0", 0), ("#1F58A8", 1)), null, Ridge(w, h, 0.86, new[] { (0.06, 0.7, 1.7), (0.02, 1.9, 0.1) })), 8 * k);
                break;

            case "gen:graphite":
                Layer(root, dc => dc.DrawRectangle(L(new Point(0, 0), new Point(1, 1), ("#5B5F66", 0), ("#34373C", 0.55), ("#1D1F22", 1)), null, full));
                Layer(root, dc => dc.DrawRectangle(new RadialGradientBrush(C("#30FFFFFF"), C("#00FFFFFF")) { Center = new Point(0.25, 0.2), GradientOrigin = new Point(0.25, 0.2), RadiusX = 0.7, RadiusY = 0.7 }, null, full));
                break;

            default: // gen:blue
                Layer(root, dc => dc.DrawRectangle(L(new Point(0, 0), new Point(1, 1), ("#5AC8FA", 0), ("#1E6FD9", 0.5), ("#1A2A7A", 1)), null, full));
                break;
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}
