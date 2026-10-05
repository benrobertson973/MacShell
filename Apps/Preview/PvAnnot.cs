using System.Windows;
using System.Windows.Media;

namespace MacShell.Apps.Preview;

public enum AnKind { Rect, RRect, Oval, Line, Arrow, Star, Bubble, Text, Path }

/// <summary>
/// A Markup annotation (NoDitherOS annot.c): vector shapes, text boxes and freehand strokes kept separate from the
/// picture until it is saved, like macOS Preview. Coordinates are image pixels.
/// </summary>
public sealed class Annot
{
    public AnKind Kind;
    public double X0, Y0, X1, Y1;        // bounds (shapes, text) or end points (line, arrow)
    public uint Stroke, Fill;            // fill alpha 0 = no fill
    public double Width;                 // stroke width in image pixels
    public string Text = "";
    public double Font;                  // text size in image pixels
    public uint TextColor;
    public List<Point> Pts = new();      // Path (image pixels)

    public Annot Clone()
    {
        var a = (Annot)MemberwiseClone();
        a.Pts = new List<Point>(Pts);
        return a;
    }

    public static string KindName(AnKind k) => k switch
    {
        AnKind.Rect => "Rectangle", AnKind.RRect => "Rounded Rectangle", AnKind.Oval => "Oval", AnKind.Line => "Line", AnKind.Arrow => "Arrow",
        AnKind.Star => "Star", AnKind.Bubble => "Speech Bubble", AnKind.Text => "Text", AnKind.Path => "Sketch", _ => "Shape",
    };

    public bool IsBox => Kind != AnKind.Line && Kind != AnKind.Arrow && Kind != AnKind.Path;

    public void Box(out double x0, out double y0, out double x1, out double y1)
    {
        x0 = Math.Min(X0, X1); x1 = Math.Max(X0, X1); y0 = Math.Min(Y0, Y1); y1 = Math.Max(Y0, Y1);
    }

    /// <summary>The box around the shape itself (path points for sketches).</summary>
    public void TightBox(out double x0, out double y0, out double x1, out double y1)
    {
        if (Kind == AnKind.Path && Pts.Count > 0)
        {
            x0 = x1 = Pts[0].X; y0 = y1 = Pts[0].Y;
            foreach (var p in Pts) { x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y); }
        }
        else Box(out x0, out y0, out x1, out y1);
    }

    /// <summary>Image pixels, including the stroke.</summary>
    public Int32Rect Bounds()
    {
        TightBox(out double x0, out double y0, out double x1, out double y1);
        double pad = Width * (Kind == AnKind.Arrow ? 4 : 1) + 2;
        return new Int32Rect((int)Math.Floor(x0 - pad), (int)Math.Floor(y0 - pad), (int)Math.Ceiling(x1 - x0 + 2 * pad), (int)Math.Ceiling(y1 - y0 + 2 * pad));
    }

    // ------------------------------------------------------------------ geometry

    static void StarPath(StreamGeometryContext g, double x0, double y0, double x1, double y1)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5, k = (i & 1) == 1 ? 0.4 : 1.0;
            var p = new Point(cx + Math.Cos(ang) * rx * k, cy + Math.Sin(ang) * ry * k);
            if (i == 0) g.BeginFigure(p, true, true); else g.LineTo(p, true, true);
        }
    }

    static void BubblePath(StreamGeometryContext g, double x0, double y0, double x1, double y1)
    {
        double h = y1 - y0, bh = h * 0.78, r = Math.Min(x1 - x0, bh) * 0.25, by = y0 + bh;
        double tx = x0 + (x1 - x0) * 0.22;
        g.BeginFigure(new Point(x0 + r, y0), true, true);
        g.LineTo(new Point(x1 - r, y0), true, true);
        g.QuadraticBezierTo(new Point(x1, y0), new Point(x1, y0 + r), true, true);
        g.LineTo(new Point(x1, by - r), true, true);
        g.QuadraticBezierTo(new Point(x1, by), new Point(x1 - r, by), true, true);
        g.LineTo(new Point(tx + (x1 - x0) * 0.14, by), true, true);
        g.LineTo(new Point(tx - (x1 - x0) * 0.06, y1), true, true);   // the tail
        g.LineTo(new Point(tx, by), true, true);
        g.LineTo(new Point(x0 + r, by), true, true);
        g.QuadraticBezierTo(new Point(x0, by), new Point(x0, by - r), true, true);
        g.LineTo(new Point(x0, y0 + r), true, true);
        g.QuadraticBezierTo(new Point(x0, y0), new Point(x0 + r, y0), true, true);
    }

    public Geometry ShapeGeometry()
    {
        Box(out double x0, out double y0, out double x1, out double y1);
        switch (Kind)
        {
            case AnKind.Rect:
            case AnKind.Text: return new RectangleGeometry(new Rect(x0, y0, x1 - x0, y1 - y0));
            case AnKind.RRect: { double r = Math.Min(x1 - x0, y1 - y0) * 0.18; return new RectangleGeometry(new Rect(x0, y0, x1 - x0, y1 - y0), r, r); }
            case AnKind.Oval: return new EllipseGeometry(new Point((x0 + x1) / 2, (y0 + y1) / 2), (x1 - x0) / 2, (y1 - y0) / 2);
        }
        var sg = new StreamGeometry();
        using (var g = sg.Open())
        {
            switch (Kind)
            {
                case AnKind.Star: StarPath(g, x0, y0, x1, y1); break;
                case AnKind.Bubble: BubblePath(g, x0, y0, x1, y1); break;
                case AnKind.Line:
                case AnKind.Arrow:
                    g.BeginFigure(new Point(X0, Y0), false, false);
                    g.LineTo(new Point(X1, Y1), true, true);
                    break;
                case AnKind.Path:
                    if (Pts.Count == 0) break;
                    g.BeginFigure(Pts[0], false, false);
                    if (Pts.Count == 1) g.LineTo(new Point(Pts[0].X + 0.01, Pts[0].Y), true, true);
                    else g.PolyLineTo(Pts.Skip(1).ToList(), true, true);
                    break;
            }
        }
        return sg;
    }

    // ------------------------------------------------------------------ drawing

    static Pen StrokePen(uint c, double w)
    {
        var p = new Pen(Nd.Br(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    /// <summary>annot_draw: image → surface is p · scale + (ox, oy).</summary>
    public void Draw(DrawingContext dc, double scale, double ox, double oy)
    {
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, ox, oy));
        var geo = ShapeGeometry();
        if (IsBox && (Fill >> 24) != 0 && Kind != AnKind.Text) dc.DrawGeometry(Nd.Br(Fill), null, geo);
        if (Kind == AnKind.Text)
        {
            if ((Fill >> 24) != 0) dc.DrawGeometry(Nd.Br(Fill), null, geo);
            if ((Stroke >> 24) != 0 && Width > 0) dc.DrawGeometry(null, StrokePen(Stroke, Width), geo);
            Box(out double x0, out double y0, out double x1, out double _);
            double pad = Font * 0.25;
            Nd.DrawWrapped(dc, Text, Nd.Weight.Regular, Font, new Rect(x0 + pad, y0 + pad, Math.Max(1, x1 - x0 - 2 * pad), 1e7), Font * 1.25, TextColor);
        }
        else if (Width > 0 && (Stroke >> 24) != 0)
        {
            dc.DrawGeometry(null, StrokePen(Stroke, Width), geo);
            if (Kind == AnKind.Arrow)
            {
                double dx = X1 - X0, dy = Y1 - Y0, len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.5)
                {
                    double ux = dx / len, uy = dy / len, hl = Math.Max(Width * 4.5, 12.0), hw = hl * 0.55;
                    var head = new StreamGeometry();
                    using (var g = head.Open())
                    {
                        g.BeginFigure(new Point(X1 + ux * Width, Y1 + uy * Width), true, true);
                        g.LineTo(new Point(X1 - ux * hl - uy * hw, Y1 - uy * hl + ux * hw), true, true);
                        g.LineTo(new Point(X1 - ux * hl + uy * hw, Y1 - uy * hl - ux * hw), true, true);
                    }
                    dc.DrawGeometry(Nd.Br(Stroke), null, head);
                }
            }
        }
        dc.Pop();
    }

    // ------------------------------------------------------------------ hit testing and editing

    static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
        t = Math.Clamp(t, 0, 1);
        double qx = ax + t * dx - px, qy = ay + t * dy - py;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    public bool Hit(double x, double y, double tol)
    {
        tol += Width / 2;
        if (Kind == AnKind.Line || Kind == AnKind.Arrow) return SegDist(x, y, X0, Y0, X1, Y1) <= tol;
        if (Kind == AnKind.Path)
        {
            for (int i = 0; i + 1 < Pts.Count; i++)
                if (SegDist(x, y, Pts[i].X, Pts[i].Y, Pts[i + 1].X, Pts[i + 1].Y) <= tol) return true;
            return Pts.Count == 1 && SegDist(x, y, Pts[0].X, Pts[0].Y, Pts[0].X, Pts[0].Y) <= tol;
        }
        Box(out double x0, out double y0, out double x1, out double y1);
        bool inside = x >= x0 - tol && x <= x1 + tol && y >= y0 - tol && y <= y1 + tol;
        if (!inside) return false;
        if (Kind == AnKind.Text || (Fill >> 24) != 0) return true;
        if (Kind == AnKind.Oval)
        {
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = Math.Max(1.0, (x1 - x0) / 2), ry = Math.Max(1.0, (y1 - y0) / 2);
            double d = Math.Sqrt((x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry));
            return Math.Abs(d - 1) * Math.Min(rx, ry) <= tol;
        }
        // unfilled shapes are picked up by their outline (like Preview)
        return x <= x0 + tol || x >= x1 - tol || y <= y0 + tol || y >= y1 - tol;
    }

    public void Move(double dx, double dy)
    {
        X0 += dx; X1 += dx; Y0 += dy; Y1 += dy;
        for (int i = 0; i < Pts.Count; i++) Pts[i] = new Point(Pts[i].X + dx, Pts[i].Y + dy);
    }

    /// <summary>x' = m0 x + m1 y + m2, y' = m3 x + m4 y + m5 (also scales width and font).</summary>
    public void Transform(double[] m)
    {
        double TX(double x, double y) => m[0] * x + m[1] * y + m[2];
        double TY(double x, double y) => m[3] * x + m[4] * y + m[5];
        double nx0 = TX(X0, Y0), ny0 = TY(X0, Y0), nx1 = TX(X1, Y1), ny1 = TY(X1, Y1);
        X0 = nx0; Y0 = ny0; X1 = nx1; Y1 = ny1;
        if (IsBox)
        {
            double a = Math.Min(X0, X1), b = Math.Max(X0, X1), c = Math.Min(Y0, Y1), d = Math.Max(Y0, Y1);
            X0 = a; X1 = b; Y0 = c; Y1 = d;
        }
        for (int i = 0; i < Pts.Count; i++) Pts[i] = new Point(TX(Pts[i].X, Pts[i].Y), TY(Pts[i].X, Pts[i].Y));
        double k = Math.Sqrt(Math.Abs(m[0] * m[4] - m[1] * m[3]));
        Width *= k;
        Font *= k;
    }
}

/// <summary>One undo step: the picture (shared between steps), its colour adjustments and the annotations.</summary>
public sealed class EditState
{
    public PvImage Img;
    public Adjust Adj = Adjust.Identity;
    public List<Annot> An = new();

    public EditState Copy() => new() { Img = Img, Adj = Adj, An = An.Select(a => a.Clone()).ToList() };

    /// <summary>Adjustments and annotations applied at full size (what gets saved).</summary>
    public PvImage Flatten()
    {
        var o = PvOps.AdjustApply(Img, Adj);
        if (An.Count == 0) return o;
        // annotations rendered on their own at 1:1, then composited exactly over the picture
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            foreach (var a in An) a.Draw(dc, 1, 0, 0);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(o.W, o.H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var layer = new uint[o.W * o.H];
        rtb.CopyPixels(layer, o.W * 4, 0);
        PvOps.Over(o, layer);
        return o;
    }
}
