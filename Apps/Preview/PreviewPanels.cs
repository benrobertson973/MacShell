using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Services;

namespace MacShell.Apps.Preview;

/// <summary>
/// Preview's floating panels (NoDitherOS pv_panels.c): Adjust Color (histogram with levels, exposure, contrast,
/// highlights, shadows, saturation, temperature, tint, sepia, sharpness, Auto Levels, Reset All) and the Inspector.
/// Both follow the front picture.
/// </summary>
public static class PreviewPanels
{
    static AdjustPanel _adjust;
    static InspectorPanel _insp;

    const double TitleH = 28;
    static readonly int Rows = AdjustPanel.RowDefs.Length;
    static double AdjustH => TitleH + 14 + 96 + 28 + 30 * Rows + 64;
    static double InspH => TitleH + 64 + 20 * 10 + 16;

    public static void Refresh()
    {
        _adjust?.Redraw();
        _insp?.Redraw();
    }

    public static void ShowAdjust()
    {
        if (_adjust != null) { _adjust.Activate(); return; }
        _adjust = new AdjustPanel();
        _adjust.Closed += (_, _) => _adjust = null;
        Place(_adjust, 0);
        _adjust.ApplyOffscreen();
        _adjust.Show();
    }

    public static void ShowInspector()
    {
        if (_insp != null) { _insp.Activate(); return; }
        _insp = new InspectorPanel();
        _insp.Closed += (_, _) => _insp = null;
        Place(_insp, AdjustH + 16);
        _insp.ApplyOffscreen();
        _insp.Show();
    }

    public static void CloseAll()
    {
        _adjust?.Close();
        _insp?.Close();
    }

    static void Place(Window w, double dy)
    {
        var wa = SystemParameters.WorkArea;
        w.Left = wa.Right - w.Width - 24;
        w.Top = wa.Top + 40 + dy;
    }

    static PreviewWindow FrontDoc => PreviewWindow.Front is { HasPicture: true } f ? f : null;

    /// <summary>A NoDitherOS utility window: title bar, no minimise / zoom, drawn in device pixels.</summary>
    abstract class Panel : MacWindow
    {
        readonly PanelCanvas _canvas;

        protected Panel(string title, double height) : base(PreviewWindow.AppId, 0)
        {
            UseVibrancy = false;
            Title = title;
            Width = 300;
            Height = height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            _canvas = new PanelCanvas(this);
            var root = new Grid();
            root.Children.Add(_canvas);
            var lights = new TrafficLights { CanMinimize = false, CanZoom = false, Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { Height = TitleH, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            row.Children.Add(lights);
            root.Children.Add(row);
            Content = root;
            Activated += (_, _) => Redraw();
            Deactivated += (_, _) => Redraw();
            Theme.Changed += Redraw;
            Closed += (_, _) => Theme.Changed -= Redraw;
        }

        public void Redraw() => _canvas.InvalidateVisual();

        protected int Wpx => (int)Math.Round(_canvas.ActualWidth * Nd.Scale);
        protected int Hpx => (int)Math.Round(_canvas.ActualHeight * Nd.Scale);
        protected static int S(double v) => Nd.S(v);
        protected static double SF(double v) => Nd.SF(v);
        protected int Tb => S(TitleH);

        void Render(DrawingContext dc)
        {
            int W = Wpx, H = Hpx;
            Nd.FillRect(dc, new Rect(0, 0, W, H), Nd.WindowBg);
            Nd.FillRect(dc, new Rect(0, 0, W, Tb), Nd.Dark ? 0xFF2E2E2Eu : 0xFFE9E9E9u);
            Nd.FillRect(dc, new Rect(0, Tb - 1, W, 1), Nd.Dark ? 0xFF000000u : 0x1F000000u);
            double maxW = W - 2 * (S(13) + S(70));
            double tw = Math.Min(Nd.TextWidth(Title, Nd.Weight.SemiBold, SF(13)), maxW);
            double cap = Nd.CapHeight(Nd.Weight.SemiBold, SF(13));
            Nd.TextEllipsized(dc, Title, Nd.Weight.SemiBold, SF(13), (W - tw) / 2, Tb / 2.0 + cap / 2, maxW, IsActive ? Nd.Label : Nd.TertiaryLabel);
            var d = FrontDoc;
            if (d == null)
            {
                Nd.TextCentered(dc, "No picture is open.", Nd.Weight.Regular, SF(13), new Rect(0, Tb, W, Math.Max(1, H - Tb)), Nd.TertiaryLabel);
                return;
            }
            Draw(dc, d, W, H);
        }

        protected abstract void Draw(DrawingContext dc, PreviewWindow d, int W, int H);
        protected virtual void Down(PreviewWindow d, double x, double y) { }
        protected virtual void Move(PreviewWindow d, double x, double y) { }
        protected virtual void Up() { }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // keys typed in a panel (⌘Z, …) go to the picture
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key != Key.W && PreviewWindow.Front is { } f)
            {
                f.RaiseEvent(new KeyEventArgs(e.KeyboardDevice, e.InputSource, e.Timestamp, e.Key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        sealed class PanelCanvas : FrameworkElement
        {
            readonly Panel _p;
            public PanelCanvas(Panel p)
            {
                _p = p;
                TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            }

            protected override void OnRender(DrawingContext dc)
            {
                Nd.Scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                dc.PushTransform(new ScaleTransform(1 / Nd.Scale, 1 / Nd.Scale));
                _p.Render(dc);
                dc.Pop();
            }

            protected override void OnMouseDown(MouseButtonEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.ChangedButton != MouseButton.Left) return;
                var pt = e.GetPosition(this);
                double x = pt.X * Nd.Scale, y = pt.Y * Nd.Scale;
                if (y < _p.Tb) { try { _p.DragMove(); } catch { } return; }
                CaptureMouse();
                if (FrontDoc is { } d) _p.Down(d, x, y);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (e.LeftButton != MouseButtonState.Pressed || !IsMouseCaptured) return;
                var pt = e.GetPosition(this);
                if (FrontDoc is { } d) _p.Move(d, pt.X * Nd.Scale, pt.Y * Nd.Scale);
            }

            protected override void OnMouseUp(MouseButtonEventArgs e)
            {
                base.OnMouseUp(e);
                ReleaseMouseCapture();
                _p.Up();
            }
        }
    }

    // ------------------------------------------------------------------ Adjust Color

    sealed class AdjustPanel : Panel
    {
        public static readonly (string name, double lo, double hi, bool centered)[] RowDefs =
        {
            ("Exposure", -2, 2, true), ("Contrast", -1, 1, true), ("Highlights", -1, 1, true), ("Shadows", -1, 1, true),
            ("Saturation", 0, 2, true), ("Temperature", -1, 1, true), ("Tint", -1, 1, true), ("Sepia", 0, 1, false),
            ("Sharpness", -1, 1, true),
        };

        const int HNone = 0, HBlack = 1, HMid = 2, HWhite = 3, HRow = 10;
        int _drag;

        // the histogram is of a small adjusted copy, cached per picture and adjustment
        PvImage _thumb;
        int _thumbGen = -1;
        uint[][] _hist;
        Adjust _histAdj;
        PreviewWindow _histDoc;

        public AdjustPanel() : base("Adjust Color", AdjustH) { }

        struct Geo { public Rect Hist, AutoB, ResetB; public Rect[] Rows; public int Sx, Sw; }

        Geo GetGeo(int W, int H)
        {
            var g = new Geo { Rows = new Rect[RowDefs.Length] };
            g.Hist = new Rect(S(16), Tb + S(14), W - S(32), S(96));
            double y = g.Hist.Y + g.Hist.Height + S(28);
            g.Sx = S(112);
            g.Sw = W - g.Sx - S(18);
            for (int i = 0; i < RowDefs.Length; i++) g.Rows[i] = new Rect(0, y + i * S(30), W, S(30));
            g.AutoB = new Rect(S(16), H - S(40), S(112), S(24));
            g.ResetB = new Rect(W - S(16) - S(112), H - S(40), S(112), S(24));
            return g;
        }

        static double MidT(Adjust a) => Math.Pow(0.5, a.Gamma);   // where output 0.5 sits between black and white
        static float GammaFor(double t) => (float)Math.Clamp(Math.Log(t) / Math.Log(0.5), 0.2, 5.0);

        PvImage Thumb(PreviewWindow d)
        {
            if (_thumb == null || _histDoc != d || _thumbGen != d.ImageGeneration)
            {
                var img = d.State.Img;
                int tw = img.W, th = img.H;
                if (Math.Max(tw, th) > 320)
                {
                    double k = 320.0 / Math.Max(tw, th);
                    tw = Math.Max(1, (int)Math.Round(tw * k));
                    th = Math.Max(1, (int)Math.Round(th * k));
                }
                _thumb = PvOps.Resize(img, tw, th);
                _thumbGen = d.ImageGeneration;
                _histDoc = d;
                _hist = null;
            }
            return _thumb;
        }

        uint[][] Histogram(PreviewWindow d)
        {
            var t = Thumb(d);
            if (_hist == null || !_histAdj.Equals(d.State.Adj))
            {
                _hist = PvOps.Histogram(PvOps.AdjustApply(t, d.State.Adj));
                _histAdj = d.State.Adj;
            }
            return _hist;
        }

        static void Triangle(DrawingContext dc, double cx, double y, uint fill, uint edge)
        {
            var sg = new StreamGeometry();
            using (var g = sg.Open())
            {
                g.BeginFigure(new Point(cx, y), true, true);
                g.LineTo(new Point(cx + SF(6), y + SF(10)), true, true);
                g.LineTo(new Point(cx - SF(6), y + SF(10)), true, true);
            }
            dc.DrawGeometry(Nd.Br(fill), new Pen(Nd.Br(edge), 1), sg);
        }

        protected override void Draw(DrawingContext dc, PreviewWindow d, int W, int H)
        {
            var g = GetGeo(W, H);
            var h = g.Hist;
            Nd.FillRRect(dc, h.X, h.Y, h.Width, h.Height, SF(4), 0xFF1C1C1Eu);
            var hist = Histogram(d);
            uint maxv = 1;
            for (int c = 0; c < 3; c++)
                for (int i = 2; i < 254; i++) maxv = Math.Max(maxv, hist[c][i]);
            uint[] cols = { 0x8CFF453Au, 0x8C32D74Bu, 0x8C0A84FFu };
            for (int c = 0; c < 3; c++)
            {
                var br = Nd.Br(cols[c]);
                int bars = (int)h.Width - S(2);
                for (int x = 0; x < bars; x++)
                {
                    int bin = x * 256 / bars;
                    double v = Math.Min(1.0, hist[c][bin] / (double)maxv);
                    int bh = (int)Math.Round(v * (h.Height - S(6)));
                    if (bh > 0) dc.DrawRectangle(br, null, new Rect(h.X + S(1) + x, h.Y + h.Height - S(3) - bh, 1, bh));
                }
            }
            var a = d.State.Adj;
            double by = h.Y + h.Height + S(2);
            double bx = h.X + a.Black * h.Width, wx = h.X + a.White * h.Width;
            Triangle(dc, bx, by, 0xFF000000u, 0xFF8E8E93u);
            Triangle(dc, bx + (wx - bx) * MidT(a), by, 0xFF8E8E93u, 0xFF5E5E5Eu);
            Triangle(dc, wx, by, 0xFFFFFFFFu, 0xFF8E8E93u);
            for (int i = 0; i < RowDefs.Length; i++)
            {
                var r = g.Rows[i];
                Nd.Text(dc, RowDefs[i].name, Nd.Weight.Regular, SF(13), SF(16), r.Y + SF(19), Nd.Label);
                double v = a[i], frac = (v - RowDefs[i].lo) / (RowDefs[i].hi - RowDefs[i].lo);
                Nd.Slider(dc, g.Sx, r.Y + r.Height / 2, g.Sw, frac, RowDefs[i].centered, true);
            }
            Nd.Button(dc, g.AutoB, "Auto Levels", false, false, true);
            Nd.Button(dc, g.ResetB, "Reset All", false, false, !a.IsIdentity);
        }

        void Apply(PreviewWindow d, double x)
        {
            var g = GetGeo(Wpx, Hpx);
            var a = d.State.Adj;
            var h = g.Hist;
            double fx = (x - h.X) / h.Width;
            if (_drag == HBlack) a.Black = (float)Math.Clamp(fx, 0.0, a.White - 0.02);
            else if (_drag == HWhite) a.White = (float)Math.Clamp(fx, a.Black + 0.02, 1.0);
            else if (_drag == HMid) a.Gamma = GammaFor(Math.Clamp((fx - a.Black) / Math.Max(0.02, a.White - a.Black), 0.03, 0.97));
            else if (_drag >= HRow)
            {
                int i = _drag - HRow;
                double k = SF(18), frac = Math.Clamp((x - g.Sx - k / 2) / (g.Sw - k), 0.0, 1.0);
                double v = RowDefs[i].lo + frac * (RowDefs[i].hi - RowDefs[i].lo);
                double def = i == 4 ? 1.0 : 0.0;   // snap to the middle
                if (RowDefs[i].centered && Math.Abs(v - def) < (RowDefs[i].hi - RowDefs[i].lo) * 0.015) v = def;
                a[i] = (float)v;
            }
            d.State.Adj = a;
        }

        protected override void Down(PreviewWindow d, double x, double y)
        {
            var g = GetGeo(Wpx, Hpx);
            _drag = HNone;
            var a = d.State.Adj;
            var h = g.Hist;
            double hy = h.Y + h.Height;
            if (y >= hy - S(4) && y <= hy + S(16))
            {
                double bx = h.X + a.Black * h.Width, wx = h.X + a.White * h.Width, mx = bx + (wx - bx) * MidT(a);
                double db = Math.Abs(x - bx), dm = Math.Abs(x - mx), dw = Math.Abs(x - wx);
                double best = Math.Min(db, Math.Min(dm, dw));
                if (best <= SF(9)) _drag = best == dm ? HMid : best == db ? HBlack : HWhite;
            }
            for (int i = 0; i < RowDefs.Length && _drag == HNone; i++)
                if (g.Rows[i].Contains(new Point(x, y)) && x >= g.Sx - S(8)) _drag = HRow + i;
            if (_drag != HNone)
            {
                d.PushUndo();
                Apply(d, x);
                d.AdjustChanged();
            }
            else if (g.AutoB.Contains(new Point(x, y)))
            {
                d.PushUndo();
                var adj = d.State.Adj;
                PvOps.AutoLevels(Thumb(d), ref adj);
                d.State.Adj = adj;
                d.AdjustChanged();
            }
            else if (g.ResetB.Contains(new Point(x, y)) && !a.IsIdentity)
            {
                d.PushUndo();
                d.State.Adj = Adjust.Identity;
                d.AdjustChanged();
            }
        }

        protected override void Move(PreviewWindow d, double x, double y)
        {
            if (_drag == HNone) return;
            Apply(d, x);
            d.AdjustChanged();
        }

        protected override void Up() => _drag = HNone;
    }

    // ------------------------------------------------------------------ Inspector

    sealed class InspectorPanel : Panel
    {
        public InspectorPanel() : base("General Info", InspH) { }

        protected override void Draw(DrawingContext dc, PreviewWindow d, int W, int H)
        {
            // header: the general info tab
            double y = Tb + S(10);
            Nd.Symbol(dc, "info", (W - SF(20)) / 2, y, SF(20), 1.6, Nd.Accent);
            y += S(30);
            Nd.FillRect(dc, new Rect(S(12), y, W - S(24), 1), Nd.Separator);
            y += S(14);
            var st = d.State;
            string path = d.DocPath;
            string where = path == null ? "Not saved" : Path.GetDirectoryName(path)?.Replace("\\", " › ").TrimEnd(' ', '›') ?? "";
            string kind = path == null ? "Image" : FileItem.FromInfo(new FileInfo(path)).Kind;
            bool alpha = PvFile.HasAlpha(st.Img);
            string[,] rows =
            {
                { "Name:", d.DocName },
                { "Kind:", kind },
                { "Image Size:", $"{st.Img.W} × {st.Img.H} pixels" },
                { "File Size:", path == null ? "—" : FileItem.FormatSize(d.FileLength) },
                { "Image DPI:", "72 pixels/inch" },
                { "Color Model:", "RGB" },
                { "Alpha Channel:", alpha ? "Yes" : "No" },
                { "Color Adjusted:", st.Adj.IsIdentity ? "No" : "Yes" },
                { "Annotations:", st.An.Count.ToString() },
                { "Where:", where },
            };
            double lx = SF(118);
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                double lw = Nd.TextWidth(rows[i, 0], Nd.Weight.SemiBold, SF(12));
                Nd.Text(dc, rows[i, 0], Nd.Weight.SemiBold, SF(12), lx - lw, y + SF(12), Nd.Label);
                Nd.TextEllipsized(dc, rows[i, 1], Nd.Weight.Regular, SF(12), lx + SF(8), y + SF(12), W - lx - SF(20), Nd.Label);
                y += S(20);
            }
        }
    }
}
