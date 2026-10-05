using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MacShell.Services;

namespace MacShell.Apps.Preview;

/// <summary>
/// NoDitherOS's drawing toolkit, rebuilt on WPF so Preview looks exactly like NoDitherOS's: its theme colours
/// (ui/theme.c), the Inter font, text placed by baseline the same way (gfx/text.c), its push buttons, sliders,
/// checkboxes and text fields (ui/widgets.c, ui/textfield.c) and its 24-point symbol set (ui/icons.c).
/// Everything is drawn in device pixels, exactly as NoDitherOS does: S() / SF() turn points into pixels, bare numbers
/// are pixels. Surfaces push a 1/Scale transform so this lands 1:1 on the screen.
/// </summary>
public static class Nd
{
    // ------------------------------------------------------------------ colours (0xAARRGGBB, like NoDitherOS's Color)

    public static bool Dark => Theme.IsDark;
    public static uint Accent
    {
        get
        {
            var c = Theme.Accent;
            return 0xFF000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
        }
    }
    public static uint Label => Dark ? 0xD9FFFFFFu : 0xD9000000u;
    public static uint SecondaryLabel => Dark ? 0x8CFFFFFFu : 0x80000000u;
    public static uint TertiaryLabel => Dark ? 0x40FFFFFFu : 0x42000000u;
    public static uint QuaternaryLabel => Dark ? 0x1AFFFFFFu : 0x1A000000u;
    public static uint Separator => Dark ? 0x1AFFFFFFu : 0x1A000000u;
    public static uint WindowBg => Dark ? 0xFF323232u : 0xFFECECECu;
    public static uint ToolbarBg => Dark ? 0xFF2B2B2Bu : 0xFFF6F6F6u;
    public static uint Pressed => Dark ? 0x26FFFFFFu : 0x1F000000u;
    public static uint Control => Dark ? 0xFF5A5A5Au : 0xFFFFFFFFu;
    public static uint ControlBorder => Dark ? 0x1AFFFFFFu : 0x33000000u;
    public static uint ControlBottomBorder => Dark ? 0x1AFFFFFFu : 0x40000000u;
    public static uint TextField => Dark ? 0x0DFFFFFFu : 0xFFFFFFFFu;

    public static uint WithAlpha(uint c, float a) => (uint)Math.Round((c >> 24) * Math.Clamp(a, 0f, 1f)) << 24 | (c & 0xFFFFFF);
    public static uint Lighten(uint c, float amt)
    {
        uint r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
        r += (uint)((255 - r) * amt); g += (uint)((255 - g) * amt); b += (uint)((255 - b) * amt);
        return (c & 0xFF000000u) | r << 16 | g << 8 | b;
    }
    public static uint Darken(uint c, float amt)
    {
        uint r = (uint)(((c >> 16) & 0xFF) * (1 - amt)), g = (uint)(((c >> 8) & 0xFF) * (1 - amt)), b = (uint)((c & 0xFF) * (1 - amt));
        return (c & 0xFF000000u) | r << 16 | g << 8 | b;
    }

    static readonly Dictionary<uint, SolidColorBrush> Brushes = new();
    public static SolidColorBrush Br(uint c)
    {
        if (!Brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
            b.Freeze();
            Brushes[c] = b;
        }
        return b;
    }

    public static Pen RoundPen(uint c, double width)
    {
        var p = new Pen(Br(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    // ------------------------------------------------------------------ fonts (Inter, as in NoDitherOS)

    public enum Weight { Regular, Medium, SemiBold, Bold }
    static readonly FontFamily InterFamily = new(new Uri("pack://application:,,,/"), "./Fonts/#Inter");
    static readonly Typeface[] Faces =
    {
        new(InterFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        new(InterFamily, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
        new(InterFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
        new(InterFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
    };
    public static Typeface Face(Weight w) => Faces[(int)w];
    public static FontFamily Family => InterFamily;

    public static double CapHeight(Weight w, double size)
    {
        if (Faces[(int)w].TryGetGlyphTypeface(out var g)) return g.CapsHeight * size;
        return size * 0.727;
    }

    /// <summary>Device pixels per point (g_scale): 1.5 at 150 %.</summary>
    public static double Scale = 1;
    public static int S(double v) => (int)Math.Round(v * Scale, MidpointRounding.AwayFromZero);
    public static double SF(double v) => v * Scale;

    public static FormattedText Ft(string s, Weight w, double size, uint color)
    {
        var ft = new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Faces[(int)w], Math.Max(0.5, size), Br(color), 1.0);   // layout units are device pixels
        return ft;
    }

    public static double TextWidth(string s, Weight w, double size) => string.IsNullOrEmpty(s) ? 0 : Ft(s, w, size, 0xFF000000).WidthIncludingTrailingWhitespace;

    /// <summary>text_draw: <paramref name="baseline"/> is the y of the text baseline.</summary>
    public static double Text(DrawingContext dc, string s, Weight w, double size, double x, double baseline, uint color)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var ft = Ft(s, w, size, color);
        dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>text_draw_centered: horizontally centred, vertically by cap height.</summary>
    public static void TextCentered(DrawingContext dc, string s, Weight w, double size, Rect r, uint color)
    {
        double tw = TextWidth(s, w, size);
        double baseline = r.Y + (r.Height + CapHeight(w, size)) / 2;
        Text(dc, s, w, size, r.X + (r.Width - tw) / 2, baseline, color);
    }

    static int Fit(string s, Weight w, double size, double maxW)
    {
        int best = 0;
        for (int i = 1; i <= s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i - 1]) && i < s.Length) continue;
            if (TextWidth(s[..i], w, size) > maxW) break;
            best = i;
        }
        return best;
    }

    public static void TextEllipsized(DrawingContext dc, string s, Weight w, double size, double x, double baseline, double maxW, uint color)
    {
        if (TextWidth(s, w, size) <= maxW) { Text(dc, s, w, size, x, baseline, color); return; }
        const string ell = "…";
        double ew = TextWidth(ell, w, size);
        int n = Fit(s, w, size, maxW - ew);
        while (n > 0 && s[n - 1] == ' ') n--;
        double tw = Text(dc, s[..n], w, size, x, baseline, color);
        Text(dc, ell, w, size, x + tw, baseline, color);
    }

    /// <summary>NoDitherOS's greedy word wrap (ui/widgets.c): one string per line.</summary>
    public static List<string> Wrap(string text, Weight w, double size, double maxW)
    {
        var lines = new List<string>();
        text ??= "";
        foreach (var para in text.Split('\n'))
        {
            string p = para;
            int lineStart = 0, len = 0, i = 0;
            while (i < p.Length)
            {
                int j = i;
                while (j < p.Length && p[j] == ' ') j++;
                while (j < p.Length && p[j] != ' ') j++;
                double width = TextWidth(p[lineStart..j], w, size);
                if (width <= maxW || len == 0)
                {
                    if (width > maxW && len == 0)
                    {
                        // a single word wider than the line: break it by characters
                        int fit = Fit(p[lineStart..], w, size, maxW);
                        if (fit <= 0) fit = 1;
                        lines.Add(p.Substring(lineStart, fit));
                        lineStart += fit;
                        i = lineStart;
                        len = 0;
                        continue;
                    }
                    len = j - lineStart;
                    i = j;
                }
                else
                {
                    lines.Add(p.Substring(lineStart, len));
                    lineStart += len;
                    while (lineStart < p.Length && p[lineStart] == ' ') lineStart++;
                    i = lineStart;
                    len = 0;
                }
            }
            lines.Add(p[lineStart..]);
        }
        return lines;
    }

    public static double MeasureWrapped(string text, Weight w, double size, double maxW, double lineH) =>
        Math.Round(lineH * Wrap(text, w, size, maxW).Count);

    /// <summary>ui_draw_wrapped: line <c>i</c> sits on baseline r.y + lineH·i + (lineH + capHeight) / 2.</summary>
    public static void DrawWrapped(DrawingContext dc, string text, Weight w, double size, Rect r, double lineH, uint color)
    {
        var lines = Wrap(text, w, size, r.Width);
        double cap = CapHeight(w, size);
        for (int i = 0; i < lines.Count; i++)
            Text(dc, lines[i], w, size, r.X, r.Y + lineH * i + (lineH + cap) / 2, color);
    }

    // ------------------------------------------------------------------ shapes

    public static void FillRect(DrawingContext dc, Rect r, uint c) => dc.DrawRectangle(Br(c), null, r);
    public static void FillRRect(DrawingContext dc, double x, double y, double w, double h, double rad, uint c) =>
        dc.DrawRoundedRectangle(Br(c), null, new Rect(x, y, Math.Max(0, w), Math.Max(0, h)), rad, rad);
    public static void FillRRect(DrawingContext dc, double x, double y, double w, double h, double rad, Brush b) =>
        dc.DrawRoundedRectangle(b, null, new Rect(x, y, Math.Max(0, w), Math.Max(0, h)), rad, rad);
    public static void StrokeRRect(DrawingContext dc, double x, double y, double w, double h, double rad, double width, uint c) =>
        dc.DrawRoundedRectangle(null, new Pen(Br(c), width), new Rect(x, y, Math.Max(0, w), Math.Max(0, h)), rad, rad);
    public static void FillCircle(DrawingContext dc, double cx, double cy, double r, uint c) =>
        dc.DrawEllipse(Br(c), null, new Point(cx, cy), r, r);
    public static void Line(DrawingContext dc, double x0, double y0, double x1, double y1, double width, uint c) =>
        dc.DrawLine(RoundPen(c, width), new Point(x0, y0), new Point(x1, y1));

    // ------------------------------------------------------------------ widgets

    public static void Button(DrawingContext dc, Rect r, string label, bool primary, bool pressed, bool enabled)
    {
        double x = r.X, y = r.Y, w = r.Width, h = r.Height, rad = SF(5);
        uint text;
        if (primary && enabled)
        {
            // bottom edge shade, then the accent body with a subtle top highlight
            FillRRect(dc, x, y + SF(0.6), w, h, rad + SF(0.5), 0x40000000u);
            var body = new LinearGradientBrush(Br(Lighten(Accent, 0.10f)).Color, Br(Accent).Color, 90);
            body.Freeze();
            FillRRect(dc, x, y, w, h, rad, body);
            if (pressed) FillRRect(dc, x, y, w, h, rad, 0x33000000u);
            text = 0xFFFFFFFFu;
        }
        else
        {
            FillRRect(dc, x, y + SF(0.6), w, h, rad + SF(0.5), ControlBottomBorder);
            FillRRect(dc, x, y, w, h, rad, Control);
            StrokeRRect(dc, x + 0.25, y + 0.25, w - 0.5, h - 0.5, rad, 0.5, ControlBorder);
            if (pressed) FillRRect(dc, x, y, w, h, rad, Pressed);
            text = enabled ? Label : TertiaryLabel;
        }
        TextCentered(dc, label, primary ? Weight.Medium : Weight.Regular, SF(13), r, text);
    }

    /// <summary>ui_slider: <paramref name="y"/> is the track centre.</summary>
    public static void Slider(DrawingContext dc, double x, double y, double w, double frac, bool centered, bool enabled)
    {
        frac = Math.Clamp(frac, 0, 1);
        double th = SF(4), ty = y - th / 2, k = SF(18);
        double kx = x + k / 2 + (w - k) * frac;
        FillRRect(dc, x, ty, w, th, th / 2, Dark ? 0x33FFFFFFu : 0x1F000000u);
        uint fill = enabled ? Accent : TertiaryLabel;
        if (centered)
        {
            double cx = x + w / 2;
            FillRect(dc, new Rect(Math.Round(Math.Min(cx, kx)), Math.Round(ty), Math.Max(1, Math.Round(Math.Abs(kx - cx))), Math.Round(th)), fill);
        }
        else FillRRect(dc, x, ty, kx - x, th, th / 2, fill);
        FillCircle(dc, kx, y + SF(0.7), k / 2 + SF(0.5), 0x33000000u);
        FillCircle(dc, kx, y, k / 2, enabled ? 0xFFFFFFFFu : 0xFFF2F2F2u);
        FillCircle(dc, kx, y, k / 2 - SF(0.5), Dark ? 0xFFDADADAu : 0xFFFFFFFFu);
    }

    public static void Checkbox(DrawingContext dc, double x, double y, bool on)
    {
        double k = SF(14);
        if (on)
        {
            FillRRect(dc, x, y, k, k, SF(3.5), Accent);
            Symbol(dc, "checkmark", x + SF(2), y + SF(2), SF(10), 3.0, 0xFFFFFFFFu);
        }
        else
        {
            FillRRect(dc, x, y, k, k, SF(3.5), Control);
            StrokeRRect(dc, x + 0.25, y + 0.25, k - 0.5, k - 0.5, SF(3.5), 0.5, ControlBottomBorder);
        }
    }

    /// <summary>The frame of tf_draw (the text itself is a real TextBox laid over it).</summary>
    public static void TextFieldFrame(DrawingContext dc, Rect r, bool focused)
    {
        double rad = SF(5);
        if (focused) FillRRect(dc, r.X - SF(3), r.Y - SF(3), r.Width + SF(6), r.Height + SF(6), rad + SF(3), WithAlpha(Accent, 0.5f));
        FillRRect(dc, r.X, r.Y, r.Width, r.Height, rad, TextField);
        StrokeRRect(dc, r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1, rad, 1, ControlBorder);
    }

    /// <summary>The popup button used in sheets (pv_bars.c popup_button).</summary>
    public static void PopupButton(DrawingContext dc, Rect r, string label)
    {
        Button(dc, r, "", false, false, true);
        TextEllipsized(dc, label, Weight.Regular, SF(13), r.X + SF(9), r.Y + SF(15.5), r.Width - SF(30), Label);
        FillRRect(dc, r.Right - SF(19), r.Y + SF(3), SF(16), r.Height - SF(6), SF(4), Accent);
        Symbol(dc, "chevron.updown", r.Right - SF(18), r.Y + (r.Height - SF(14)) / 2, SF(14), 2.2, 0xFFFFFFFFu);
    }

    // ------------------------------------------------------------------ symbols (ui/icons.c, 24 x 24 grid)

    static readonly Dictionary<string, string[]> Syms = new()
    {
        ["markup"] = new[] { "circle:12,12,9.2", "M9,15.5 L9,13.2 L14.6,7.6 A1.2,1.2 0 0 1 16.4,9.4 L10.8,15 Z", "M8.2,16.3 H15.8" },
        ["rotate.left"] = new[] { "rect:7,9.5,10,10,1.8", "M5.5,7.5 A7,7 0 0 1 16.5,5.2", "M5.2,3.8 V7.8 H9.2" },
        ["zoom.in"] = new[] { "circle:10.4,10.4,6.6", "M15.2,15.2 L20.6,20.6", "M10.4,7.6 V13.2 M7.6,10.4 H13.2" },
        ["zoom.out"] = new[] { "circle:10.4,10.4,6.6", "M15.2,15.2 L20.6,20.6", "M7.6,10.4 H13.2" },
        ["selection.rect"] = new[] { "M4,4 H7 M10,4 H14 M17,4 H20 V7 M20,10 V14 M20,17 V20 H17 M14,20 H10 M7,20 H4 V17 M4,14 V10 M4,7 V4" },
        ["selection.oval"] = new[] { "M12,4 A8,8 0 0 1 14.6,4.4 M17.6,5.9 A8,8 0 0 1 19.5,8.4 M20,11 A8,8 0 0 1 19.5,15.2 M17.6,18.1 A8,8 0 0 1 14.6,19.6 M12,20 A8,8 0 0 1 9.4,19.6 M6.4,18.1 A8,8 0 0 1 4.5,15.6 M4,12.9 A8,8 0 0 1 4.5,8.8 M6.4,5.9 A8,8 0 0 1 9.4,4.4" },
        ["lasso"] = new[] { "M12,3.8 C17.3,3.8 20.6,6.5 20.6,9.8 C20.6,13.1 17,15.4 12.3,15.4 C7.6,15.4 3.4,13.3 3.4,9.8 C3.4,6.4 6.8,3.8 12,3.8 Z", "M8,14.8 C6.4,16.8 7.2,19.4 10.2,20.6" },
        ["instant.alpha"] = new[] { "M4,20.5 L14.5,10", "M16.5,3 V7.4 M14.3,5.2 H18.7", "M19.8,9.2 V12 M18.4,10.6 H21.2", "M9.8,3.6 V6 M8.6,4.8 H11" },
        ["scribble"] = new[] { "M3.5,15 C6,8 8.5,7 9.5,9.5 C10.5,12 7.5,17 10,17.5 C12.5,18 13.5,7.5 16.5,8 C19,8.5 16.5,14 20.5,13.5" },
        ["pencil"] = new[] { "M4,20 L4.8,16.2 L15.6,5.4 A2.1,2.1 0 0 1 18.6,8.4 L7.8,19.2 Z", "M13.8,7.2 L16.8,10.2" },
        ["shapes"] = new[] { "rect:3.5,3.5,10,10,1.6", "circle:15,15,5.6" },
        ["textbox"] = new[] { "rect:3,4,18,16,2.2", "M8,16 L12,7.5 L16,16 M9.5,13 H14.5" },
        ["adjust.color"] = new[] { "M12,3.5 L20.5,19.5 H3.5 Z", "M8.5,19.5 L12,11.5 L15.5,19.5" },
        ["adjust.size"] = new[] { "rect:3.5,3.5,17,17,2.2", "M9,15 L15,9", "M11,9 H15 V13" },
        ["shape.style"] = new[] { "M4,6 H20", "M4,11.5 H20", "M4,17.5 H20" },
        ["border.color"] = new[] { "rect:4,4,16,16,2.2" },
        ["fill.color"] = new[] { "!rect:4,4,16,16,2.2" },
        ["textformat"] = new[] { "M3.5,18 L8,6 L12.5,18 M5.2,14 H10.8", "M16.5,11 A2.6,2.6 0 1 1 16.5,16.2 A2.6,2.6 0 1 1 16.5,11 Z M19.1,10.8 V18" },
        ["crop"] = new[] { "M7,3 V17 H21", "M3,7 H17 V21" },
        ["square.and.arrow.up"] = new[] { "M12,3.5 V15", "M8,7.2 L12,3.2 L16,7.2", "M8.5,10 H6.5 A1.5,1.5 0 0 0 5,11.5 V19 A1.5,1.5 0 0 0 6.5,20.5 H17.5 A1.5,1.5 0 0 0 19,19 V11.5 A1.5,1.5 0 0 0 17.5,10 H15.5" },
        ["chevron.down"] = new[] { "M5,8.8 L12,15.8 L19,8.8" },
        ["chevron.updown"] = new[] { "M7.5,9.5 L12,5 L16.5,9.5", "M7.5,14.5 L12,19 L16.5,14.5" },
        ["checkmark"] = new[] { "M5,12.6 L9.8,17.4 L19,6.6" },
        ["info"] = new[] { "circle:12,12,9", "M12,10.8 V16.6", "!circle:12,7.6,1.25" },
    };

    static readonly Dictionary<string, (Geometry geo, bool fill)[]> SymGeo = new();

    static (Geometry, bool)[] Parts(string name)
    {
        if (SymGeo.TryGetValue(name, out var g)) return g;
        if (!Syms.TryGetValue(name, out var def)) return null;
        var list = new List<(Geometry, bool)>();
        foreach (var raw in def)
        {
            bool fill = raw.StartsWith('!');
            string p = fill ? raw[1..] : raw;
            Geometry geo;
            if (p.StartsWith("circle:"))
            {
                var v = Nums(p[7..]);
                geo = new EllipseGeometry(new Point(v[0], v[1]), v[2], v[2]);
            }
            else if (p.StartsWith("rect:"))
            {
                var v = Nums(p[5..]);
                geo = new RectangleGeometry(new Rect(v[0], v[1], v[2], v[3]), v[4], v[4]);
            }
            else geo = Geometry.Parse(p);
            geo.Freeze();
            list.Add((geo, fill));
        }
        return SymGeo[name] = list.ToArray();
    }

    static double[] Nums(string s) => s.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>symbol_draw: the symbol's 24-unit grid scaled to <paramref name="size"/> at (x, y); stroke in grid units.</summary>
    public static void Symbol(DrawingContext dc, string name, double x, double y, double size, double stroke, uint color)
    {
        var parts = Parts(name);
        if (parts == null) return;
        double k = size / 24.0;
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, x, y));
        var pen = RoundPen(color, stroke);   // in the 24-unit grid: NoDitherOS strokes scale with the transform
        foreach (var (geo, fill) in parts)
        {
            if (fill) dc.DrawGeometry(Br(color), null, geo);
            else dc.DrawGeometry(null, pen, geo);
        }
        dc.Pop();
    }
}
