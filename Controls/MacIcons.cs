using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MacShell.Services;

namespace MacShell.Controls;

/// <summary>
/// Vector recreations of macOS-style icons (Finder, Launchpad, System Settings, Trash,
/// blue folders, drives, the Apple logo …). All are resolution-independent DrawingImages
/// on a 100×100 canvas.
/// </summary>
public static class MacIcons
{
    static readonly Dictionary<string, ImageSource> Cache = new();

    static readonly Color Neutral = Color.FromRgb(0x8E, 0x8E, 0x93);

    /// <summary>Parses a color string; anything missing or malformed becomes a neutral gray
    /// so a bad entry in a data table never takes down the caller.</summary>
    static Color C(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Neutral;
        try { return ColorConverter.ConvertFromString(hex) is Color c ? c : Neutral; }
        catch (FormatException) { return Neutral; }
        catch (NotSupportedException) { return Neutral; }
    }
    static Brush Solid(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    static Brush VGrad(string top, string bottom) { var b = new LinearGradientBrush(C(top), C(bottom), 90); b.Freeze(); return b; }
    static Geometry G(string s) { var g = Geometry.Parse(s); g.Freeze(); return g; }

    /// <summary>Apple's continuous-corner "squircle" (superellipse, n≈5).</summary>
    public static Geometry Squircle(double x, double y, double size, double n = 5)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            double r = size / 2, cx = x + r, cy = y + r;
            const int steps = 160;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps * Math.PI * 2;
                double ct = Math.Cos(t), st = Math.Sin(t);
                double px = cx + r * Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2.0 / n);
                double py = cy + r * Math.Sign(st) * Math.Pow(Math.Abs(st), 2.0 / n);
                if (i == 0) ctx.BeginFigure(new Point(px, py), true, true); else ctx.LineTo(new Point(px, py), true, true);
            }
        }
        sg.Freeze();
        return sg;
    }

    static readonly Geometry AppShape = Squircle(6, 6, 88);

    static ImageSource Freeze(DrawingGroup dg)
    {
        // pad to a stable 100×100 box
        dg.Children.Insert(0, new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        var img = new DrawingImage(dg);
        img.Freeze();
        return img;
    }

    static ImageSource Cached(string key, Func<ImageSource> make)
    {
        if (!Cache.TryGetValue(key, out var v)) Cache[key] = v = make();
        return v;
    }

    static void AppBase(DrawingGroup dg, Brush fill)
    {
        // soft contact shadow
        var shadow = Squircle(6, 8.5, 88);
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, shadow));
        dg.Children.Add(new GeometryDrawing(fill, null, AppShape));
    }

    // ------------------------------------------------------------------ Finder
    public static ImageSource Finder => Cached("finder", () =>
    {
        var dg = new DrawingGroup();
        AppBase(dg, Solid("#FFFFFF"));
        var clip = new DrawingGroup { ClipGeometry = AppShape };
        var left = G("M0,0 L56,0 C54.5,13 51.5,25 47.5,37 C45.5,43 43.5,49 42.2,55 L51,55 C50.6,62 50.8,70 51.8,77.5 C52.8,86 54.8,93 57.3,100 L0,100 Z");
        var right = G("M56,0 L100,0 L100,100 L57.3,100 C54.8,93 52.8,86 51.8,77.5 C50.8,70 50.6,62 51,55 L42.2,55 C43.5,49 45.5,43 47.5,37 C51.5,25 54.5,13 56,0 Z");
        clip.Children.Add(new GeometryDrawing(VGrad("#6CD0FB", "#1B8CF2"), null, left));
        clip.Children.Add(new GeometryDrawing(VGrad("#F4F8FB", "#D3E1EC"), null, right));
        var ink = Solid("#1F2B3A");
        clip.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(28, 27, 6, 16), 3, 3)));
        clip.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(67, 27, 6, 16), 3, 3)));
        var smilePen = new Pen(ink, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        clip.Children.Add(new GeometryDrawing(null, smilePen, G("M22,66.5 C34,77.5 66,77.5 78,66.5")));
        dg.Children.Add(clip);
        dg.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), 0.6), AppShape));
        return Freeze(dg);
    });

    // --------------------------------------------------------------- Launchpad
    public static ImageSource Launchpad => Cached("launchpad", () =>
    {
        var dg = new DrawingGroup();
        AppBase(dg, VGrad("#55555A", "#2C2C30"));
        string[] colors = { "#FF453A", "#FF9F0A", "#FFD60A", "#32D74B", "#64D2FF", "#0A84FF", "#BF5AF2", "#FF375F", "#AEAEB2" };
        double size = 17, gap = 7.5, start = 50 - (size * 3 + gap * 2) / 2;
        for (int i = 0; i < 9; i++)
        {
            double x = start + (i % 3) * (size + gap), y = start + (i / 3) * (size + gap);
            var b = new LinearGradientBrush(Lighten(C(colors[i]), 0.18), C(colors[i]), 90); b.Freeze();
            dg.Children.Add(new GeometryDrawing(b, null, new RectangleGeometry(new Rect(x, y, size, size), 4.5, 4.5)));
        }
        return Freeze(dg);
    });

    // ------------------------------------------------------------------ Preview (NoDitherOS ui/icons.c draw_preview)
    public static ImageSource Preview => Cached("preview", () =>
    {
        var dg = new DrawingGroup();
        AppBase(dg, VGrad("#F5F5F7", "#DADADF"));
        dg.Children.Add(new GeometryDrawing(Solid("#FFFFFF"), null, new RectangleGeometry(new Rect(30, 20, 50, 42), 3, 3)));
        dg.Children.Add(new GeometryDrawing(VGrad("#7FC4F7", "#CDE9FB"), null, new RectangleGeometry(new Rect(20, 32, 52, 42), 3, 3)));
        dg.Children.Add(new GeometryDrawing(VGrad("#5DBB63", "#3A9A48"), null, G("M20,74 L20,63 C29,54 37,53 45,61 C52,54 62,53 72,62 L72,74 Z")));
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), null, new EllipseGeometry(new Point(64, 64), 13, 13)));
        dg.Children.Add(new GeometryDrawing(null, new Pen(Solid("#3A3A3C"), 4), new EllipseGeometry(new Point(64, 64), 13, 13)));
        var handle = new Pen(Solid("#3A3A3C"), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dg.Children.Add(new GeometryDrawing(null, handle, G("M73.5,73.5 L84,84")));
        return Freeze(dg);
    });

    /// <summary>The icon of one of MacShell's own apps (internal:*).</summary>
    public static ImageSource ForInternal(string key) => key switch
    {
        "internal:settings" => SystemSettings,
        "internal:preview" => Preview,
        _ => Finder,
    };

    // ----------------------------------------------------------- System Settings
    public static ImageSource SystemSettings => Cached("settings", () =>
    {
        var dg = new DrawingGroup();
        AppBase(dg, VGrad("#A3A3A8", "#6A6A6F"));
        var gear = GearFill(50, 50, 36, 30.5, 36, 0);
        dg.Children.Add(new GeometryDrawing(VGrad("#F2F2F4", "#BDBDC2"), new Pen(Solid("#5A5A5F"), 0.8), gear));
        dg.Children.Add(new GeometryDrawing(VGrad("#6E6E73", "#9A9A9F"), null, new EllipseGeometry(new Point(50, 50), 24, 24)));
        var inner = GearFill(50, 50, 21, 16.5, 9, 0);
        dg.Children.Add(new GeometryDrawing(VGrad("#E5E5EA", "#AEAEB2"), null, inner));
        dg.Children.Add(new GeometryDrawing(VGrad("#7C7C80", "#A8A8AD"), null, new EllipseGeometry(new Point(50, 50), 7.5, 7.5)));
        return Freeze(dg);
    });

    static Geometry GearFill(double cx, double cy, double rOuter, double rInner, int teeth, double hole)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            int n = teeth * 4;
            for (int i = 0; i <= n; i++)
            {
                double a = i / (double)n * Math.PI * 2;
                double r = (i % 4) is 1 or 2 ? rOuter : rInner;
                var p = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
                if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, true);
            }
        }
        sg.Freeze();
        return sg;
    }

    // ------------------------------------------------------------------- Trash
    public static ImageSource Trash(bool full) => Cached("trash" + full, () =>
    {
        var dg = new DrawingGroup();
        var body = G("M20,24 L80,24 L74.5,91 C74.2,94 72,96 69,96 L31,96 C28,96 25.8,94 25.5,91 Z");
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)), null, G("M22,28 L78,28 L73,96 L27,96 Z")));
        if (full)
        {
            var paper = VGrad("#FFFFFF", "#DADADF");
            var pen = new Pen(Solid("#B8B8BD"), 0.6);
            dg.Children.Add(new GeometryDrawing(paper, pen, G("M28,24 C26,14 34,6 44,9 C50,3 60,5 62,12 C70,9 78,15 74,24 Z")));
            dg.Children.Add(new GeometryDrawing(Solid("#F7F7F9"), pen, G("M40,20 C42,12 52,10 56,16 C58,19 56,22 54,24 L40,24 Z")));
        }
        var glass = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(235, 250, 250, 252), 0),
            new GradientStop(Color.FromArgb(225, 214, 216, 222), 0.55),
            new GradientStop(Color.FromArgb(235, 190, 193, 200), 1),
        }, 90);
        glass.Freeze();
        dg.Children.Add(new GeometryDrawing(glass, new Pen(Solid("#9C9EA5"), 0.8), body));
        var groove = new Pen(new SolidColorBrush(Color.FromArgb(70, 110, 112, 120)), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var hi = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double[] xs = { 33, 41.5, 50, 58.5, 67 };
        foreach (var x in xs)
        {
            double bottomX = 50 + (x - 50) * 0.84;
            dg.Children.Add(new GeometryDrawing(null, groove, new LineGeometry(new Point(x, 33), new Point(bottomX, 88))));
            dg.Children.Add(new GeometryDrawing(null, hi, new LineGeometry(new Point(x + 1.6, 33), new Point(bottomX + 1.5, 88))));
        }
        dg.Children.Add(new GeometryDrawing(VGrad("#E9EAEE", "#B5B7BE"), new Pen(Solid("#8E9097"), 0.8), new RectangleGeometry(new Rect(16, 19, 68, 8), 4, 4)));
        return Freeze(dg);
    });

    // ------------------------------------------------------------------ Folders
    /// <summary>macOS Big Sur–style blue folder, optionally with an embossed SF glyph.</summary>
    public static ImageSource Folder(string glyph = null) => Cached("folder:" + glyph, () =>
    {
        var dg = new DrawingGroup();
        var back = G("M7,21 Q7,15 13,15 L35,15 Q38.5,15 40.5,17.5 L43,20.5 Q44.2,22 46.5,22 L88,22 Q93,22 93,27 L93,80 Q93,85 88,85 L12,85 Q7,85 7,80 Z");
        var front = G("M7,34 Q7,29 12,29 L88,29 Q93,29 93,34 L93,80 Q93,85 88,85 L12,85 Q7,85 7,80 Z");
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(38, 0, 40, 90)), null, G("M8,36 L92,36 L92,82 Q92,87.5 87,87.5 L13,87.5 Q8,87.5 8,82 Z")));
        dg.Children.Add(new GeometryDrawing(VGrad("#4FA6EA", "#3A92DD"), null, back));
        dg.Children.Add(new GeometryDrawing(VGrad("#86CBF8", "#62B3F1"), null, front));
        dg.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 0.9), G("M8.5,33.8 Q8.5,30.4 12,30.4 L88,30.4 Q91.5,30.4 91.5,33.8")));
        if (glyph != null && Sym.Exists(glyph))
        {
            var glyphBrush = new SolidColorBrush(Color.FromArgb(215, 45, 128, 205)); glyphBrush.Freeze();
            var g = new DrawingGroup { Transform = new MatrixTransform(1.25, 0, 0, 1.25, 50 - 15, 58 - 15) };
            var pen = new Pen(glyphBrush, 2.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            foreach (var (geo, fill) in Sym.Get(glyph)) g.Children.Add(new GeometryDrawing(fill ? glyphBrush : null, fill ? null : pen, geo));
            dg.Children.Add(g);
        }
        return Freeze(dg);
    });

    public static string FolderGlyphFor(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string p = path.TrimEnd('\\');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Eq(p, Environment.GetFolderPath(Environment.SpecialFolder.Desktop))) return "desktop";
        if (Eq(p, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))) return "doc";
        if (Eq(p, Native.NativeMethods.GetKnownFolder(Native.NativeMethods.FOLDERID_Downloads))) return "arrow.down.circle";
        if (Eq(p, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))) return "photo";
        if (Eq(p, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic))) return "music.note";
        if (Eq(p, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))) return "film";
        if (Eq(p, home)) return "house";
        if (Eq(p, Environment.GetEnvironmentVariable("OneDrive"))) return "icloud";
        return null;
    }

    static bool Eq(string a, string b) => b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------- Drives
    public static ImageSource Drive(bool external = false) => Cached("drive" + external, () =>
    {
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, new RectangleGeometry(new Rect(9, 36, 82, 42), 7, 7)));
        dg.Children.Add(new GeometryDrawing(VGrad(external ? "#F4F4F6" : "#E9E9EC", external ? "#C8C8CD" : "#A9AAAF"), new Pen(Solid("#8A8B90"), 0.8), new RectangleGeometry(new Rect(8, 30, 84, 44), 7, 7)));
        dg.Children.Add(new GeometryDrawing(VGrad("#FDFDFE", "#DADADE"), null, new RectangleGeometry(new Rect(10, 31.5, 80, 20), 6, 6)));
        dg.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 0.8), new LineGeometry(new Point(12, 60), new Point(88, 60))));
        dg.Children.Add(new GeometryDrawing(Solid(external ? "#FF9F0A" : "#34C759"), null, new EllipseGeometry(new Point(80, 66.5), 2.2, 2.2)));
        return Freeze(dg);
    });

    // ------------------------------------------------------------- Apple logo
    public static readonly Geometry AppleLogo = G(
        "M50.5,36 C56,36 62,30.5 72.5,30.5 C81,30.5 89.5,34.5 95.5,42.5 C85,48.5 79.5,58.5 79.5,69.5 " +
        "C79.5,82.5 87,92 98,96.5 C95,105.5 90,113.5 84.5,119.5 C80.5,124 76,126 70.5,126 " +
        "C64,126 60,121.5 50,121.5 C40,121.5 36,126 29.5,126 C24,126 19.5,123.5 15.5,119.5 " +
        "C7,110.5 1.5,94 1.5,77 C1.5,51.5 17,35 34.5,35 C41.5,35 45.5,36 50.5,36 Z " +
        "M70,2 C70.8,10 67.5,17.5 63,22.5 C58.5,27.5 52,31 46,30.5 C45.2,22.5 48.8,15 53.2,10.5 C57.8,5.5 64.5,2.3 70,2 Z");

    public static ImageSource AppleLogoImage(Brush b)
    {
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(b, null, AppleLogo));
        var img = new DrawingImage(dg); img.Freeze();
        return img;
    }

    // ------------------------------------------------------- Settings tiles
    /// <summary>Coloured rounded-square tile with a white glyph, as used in System Settings' sidebar.</summary>
    public static ImageSource Tile(string symbol, string top, string bottom, string glyphColor = "#FFFFFF") => Cached($"tile:{symbol}:{top}:{bottom}:{glyphColor}", () =>
    {
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(VGrad(top, bottom), null, new RectangleGeometry(new Rect(0, 0, 100, 100), 23, 23)));
        var g = new DrawingGroup { Transform = new MatrixTransform(3.0, 0, 0, 3.0, 14, 14) };
        var brush = Solid(glyphColor);
        var pen = new Pen(brush, 1.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var (geo, fill) in Sym.Get(symbol)) g.Children.Add(new GeometryDrawing(fill ? brush : null, fill ? null : pen, geo));
        dg.Children.Add(g);
        return Freeze(dg);
    });

    /// <summary>Generic app icon for things we can't resolve.</summary>
    public static ImageSource GenericApp => Cached("genericapp", () =>
    {
        var dg = new DrawingGroup();
        AppBase(dg, VGrad("#E8E8ED", "#C4C4CA"));
        var pen = new Pen(Solid("#8E8E93"), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dg.Children.Add(new GeometryDrawing(null, pen, G("M32,70 L50,30 L68,70 M38,58 H62")));
        return Freeze(dg);
    });

    public static ImageSource GenericDocument => Cached("genericdoc", () =>
    {
        var dg = new DrawingGroup();
        var page = G("M22,6 L62,6 L80,24 L80,94 L22,94 Z");
        dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, G("M23,8 L63,8 L81,26 L81,96 L23,96 Z")));
        dg.Children.Add(new GeometryDrawing(Solid("#FFFFFF"), new Pen(Solid("#C7C7CC"), 0.8), page));
        dg.Children.Add(new GeometryDrawing(Solid("#E5E5EA"), new Pen(Solid("#C7C7CC"), 0.8), G("M62,6 L62,24 L80,24 Z")));
        return Freeze(dg);
    });

    // -------------------------------------------------------- Mac illustration
    public static DrawingImage Laptop(ImageSource screen)
    {
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 200, 130))));
        dg.Children.Add(new GeometryDrawing(Solid("#1C1C1E"), new Pen(Solid("#A1A1A6"), 1.2), new RectangleGeometry(new Rect(26, 6, 148, 100), 7, 7)));
        if (screen != null)
        {
            var clip = new RectangleGeometry(new Rect(31, 11, 138, 90), 2, 2);
            var sg = new DrawingGroup { ClipGeometry = clip };
            sg.Children.Add(new ImageDrawing(screen, new Rect(31, 11, 138, 90)));
            dg.Children.Add(sg);
        }
        dg.Children.Add(new GeometryDrawing(Solid("#0B0B0C"), null, new RectangleGeometry(new Rect(92, 11, 16, 3.2), 1.6, 1.6)));
        dg.Children.Add(new GeometryDrawing(VGrad("#E3E4E8", "#A7A9AF"), null, G("M4,108 L196,108 L196,111 C196,115 190,118 184,118 L16,118 C10,118 4,115 4,111 Z")));
        dg.Children.Add(new GeometryDrawing(Solid("#8E9096"), null, new RectangleGeometry(new Rect(84, 108, 32, 3), 1.5, 1.5)));
        var img = new DrawingImage(dg);
        img.Freeze();
        return img;
    }

    public static Color Lighten(Color c, double amt) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * amt), (byte)(c.G + (255 - c.G) * amt), (byte)(c.B + (255 - c.B) * amt));

    /// <summary>Renders a DrawingImage to a bitmap (used where a BitmapSource is required).</summary>
    public static BitmapSource Rasterize(ImageSource src, int px)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, px, px));
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
