using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Apps.Preview;

/// <summary>
/// Preview: shows pictures and edits them like macOS Preview — the Markup toolbar (selections, Instant Alpha,
/// sketch and draw, shapes, text, styles), Adjust Color, Adjust Size, rotate / flip / crop, undo, Save and Export.
/// A port of NoDitherOS's Preview (src/apps/preview): the same layout, drawing and picture maths, drawn in device
/// pixels through <see cref="Nd"/>. Toolbars, popovers and sheets are in PreviewBars.cs, the panels in PreviewPanels.cs.
/// </summary>
public partial class PreviewWindow : MacWindow
{
    public const string AppId = "internal:preview";
    public static readonly List<PreviewWindow> All = new();
    static PreviewWindow _last;

    const int MaxUndo = 40;
    const double ToolbarH = 52, MarkupH = 38;   // points

    enum Tool { Rect, Oval, Lasso, Alpha, Sketch, Draw }
    enum SelKind { None, Rect, Oval, Mask }
    enum Drag { None, Handle, Move, Draw, SelNew, SelMove, Lasso, Alpha }

    sealed class Sel
    {
        public SelKind Kind;
        public Int32Rect R;
        public byte[] Mask;   // Mask: R.Width × R.Height coverage (0 / 255)
    }

    // ------------------------------------------------------------------ the document
    string _path, _name = "Untitled";
    bool _untitled, _readonly, _loaded;
    string _err = "";
    long _fileLen;

    EditState _st = new();
    readonly List<EditState> _undo = new(), _redo = new();
    int _change, _savedChange, _imgGen;

    double _zoom;           // 0 = fit
    int _sx, _sy;
    bool _markup;
    Tool _tool, _selTool;
    Sel _sel = new();
    int _selGen;
    int _anSel = -1;        // selected annotation, -1 = none
    bool _textEdit, _textFresh;

    // pointer drag
    Drag _drag;
    int _handle;
    double _px, _py;        // image position of the press
    Annot _orig;
    Int32Rect _selOrig;
    bool _pushed;
    Annot _cur;             // freehand stroke / lasso being drawn
    int _alphaX, _alphaY;
    double _alphaSx, _alphaSy;
    int _alphaTol;

    bool _saving, _saveIsExport, _closeAfterSave;
    bool Dirty => _change != _savedChange;
    public bool HasPicture => _loaded;

    readonly PvCanvas _canvas;
    readonly Grid _root = new();

    // ------------------------------------------------------------------ opening

    public static IEnumerable<PreviewWindow> Docs => All.Where(w => w.IsLoaded);

    /// <summary>The focused picture, or the last one (for the panels and the menu bar).</summary>
    public static PreviewWindow Front
    {
        get
        {
            var f = All.FirstOrDefault(w => w.IsActive);
            if (f != null) return f;
            if (_last != null && All.Contains(_last)) return _last;
            return All.LastOrDefault();
        }
    }

    public static void Open(string path)
    {
        var existing = All.FirstOrDefault(w => !w._untitled && string.Equals(w._path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { existing.Activate(); return; }
        var win = new PreviewWindow();
        win.LoadFile(path, first: true);
        win.Show();
        if (!ShellHost.Offscreen) win.Activate();
        Settings.AddRecentDoc(path);
    }

    static void OpenImage(PvImage img)
    {
        var win = new PreviewWindow();
        win._name = "Untitled";
        win._untitled = true;
        win._st.Img = img;
        win._loaded = true;
        win._fileLen = 1;
        win._change = 1;   // a new picture is unsaved
        win._imgGen++;
        win.FitWindow();
        win.Show();
        if (!ShellHost.Offscreen) win.Activate();
    }

    /// <summary>Launching Preview: the front picture, or the Open panel (as Preview does on a Mac).</summary>
    public static void OpenApp()
    {
        var f = Front;
        if (f != null) { f.Activate(); return; }
        OpenFiles();
    }

    public static void OpenFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open",
            Multiselect = true,
            Filter = "Pictures|" + string.Join(";", PvFile.ImageExtensions.Select(e => "*" + e)) + "|All Files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) Open(f);
    }

    PreviewWindow() : base(AppId, 0)
    {
        UseVibrancy = false;
        Title = "Preview";
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 260;
        _canvas = new PvCanvas(this);
        _root.Children.Add(_canvas);
        _root.Children.Add(_sheetLayer);
        var lights = new TrafficLights { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var lightRow = new Grid { Height = ToolbarH, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        lightRow.Children.Add(lights);
        _root.Children.Add(lightRow);
        Content = _root;
        CenterOnWorkArea();
        All.Add(this);
        _last = this;
        Activated += (_, _) => { _last = this; _canvas.InvalidateVisual(); PreviewPanels.Refresh(); };
        Deactivated += (_, _) => _canvas.InvalidateVisual();
        Closed += (_, _) =>
        {
            All.Remove(this);
            if (_last == this) _last = null;
            PreviewPanels.Refresh();
        };
        Theme.Changed += Redraw;
        Closed += (_, _) => Theme.Changed -= Redraw;
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = DroppedFiles(e.Data).Length > 0 || HasPictureData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) => { DropPictures(e.Data, e.GetPosition(_canvas)); e.Handled = true; };
    }

    static string[] DroppedFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(f => File.Exists(f) && PvFile.IsImage(f)).ToArray() : Array.Empty<string>();

    static bool HasPictureData(IDataObject data) => data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("FileContents");

    /// <summary>
    /// Pictures dropped onto the picture are placed inside it, where they were let go, as a Markup picture that can
    /// be moved and resized (and is merged in on Save). Dropped on the toolbar, or with no picture open, they open.
    /// </summary>
    void DropPictures(IDataObject data, Point at)
    {
        var files = DroppedFiles(data);
        double x = at.X * Nd.Scale, y = at.Y * Nd.Scale;
        if (!_loaded || y < BarsH || _sheet != Sheet.None)
        {
            foreach (var f in files) Open(f);
            return;
        }
        var pics = new List<PvImage>();
        foreach (var f in files)
        {
            try { pics.Add(PvFile.Load(f)); }
            catch { ShellHost.ShowAlert($"The file “{Path.GetFileName(f)}” could not be opened.", "It may be damaged or use a file format that Preview doesn’t recognize."); }
        }
        if (files.Length == 0)
        {
            try
            {
                if (data.GetData("PNG") is MemoryStream ms)
                {
                    ms.Position = 0;
                    pics.Add(PvFile.Convert(BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]));
                }
                else if (data.GetData(DataFormats.Bitmap) is BitmapSource bs) pics.Add(PvFile.Convert(bs));
                else if (data.GetData("FileContents") is MemoryStream fc)   // a picture dragged out of a web browser
                {
                    fc.Position = 0;
                    pics.Add(PvFile.Convert(BitmapDecoder.Create(fc, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]));
                }
            }
            catch { }
        }
        if (pics.Count == 0) return;
        var g = GetView();
        ImgPt(g, x, y, out double cx, out double cy);
        EndText();
        double off = 0;
        foreach (var pic in pics) { PlacePicture(pic, cx + off, cy + off); off += 12 * Unit; }
        Activate();
    }

    /// <summary>A picture inside the picture: its own size, made smaller if it wouldn't fit, centred on (cx, cy).</summary>
    void PlacePicture(PvImage pic, double cx, double cy)
    {
        double w = pic.W, h = pic.H;
        double k = Math.Min(1.0, Math.Min(_st.Img.W * 0.6 / w, _st.Img.H * 0.6 / h));
        w *= k; h *= k;
        cx = Math.Clamp(cx, w / 2, Math.Max(w / 2, _st.Img.W - w / 2));
        cy = Math.Clamp(cy, h / 2, Math.Max(h / 2, _st.Img.H - h / 2));
        var a = new Annot { Kind = AnKind.Image, Pic = pic, X0 = cx - w / 2, Y0 = cy - h / 2, X1 = cx + w / 2, Y1 = cy + h / 2 };
        if (!_markup) _markup = true;   // (its handles and styles are Markup's)
        AddAnnot(a);
    }

    void Redraw() => _canvas.InvalidateVisual();

    void Changed()
    {
        _change++;
        Redraw();
        PreviewPanels.Refresh();
    }

    // ------------------------------------------------------------------ undo

    public void PushUndo()
    {
        if (_undo.Count == MaxUndo) _undo.RemoveAt(0);
        _undo.Add(_st.Copy());
        _redo.Clear();
    }

    void History(bool redo)
    {
        EndText();
        var from = redo ? _redo : _undo;
        var to = redo ? _undo : _redo;
        if (from.Count == 0) return;
        if (to.Count == MaxUndo) to.RemoveAt(0);
        to.Add(_st);
        _st = from[^1];
        from.RemoveAt(from.Count - 1);
        _anSel = -1;
        if (_sel.Kind != SelKind.None && (_sel.R.X + _sel.R.Width > _st.Img.W || _sel.R.Y + _sel.R.Height > _st.Img.H)) SelClear();
        _imgGen++;
        Changed();
    }

    void SelClear()
    {
        _sel = new Sel();
        _selGen++;
    }

    // ------------------------------------------------------------------ geometry (device pixels, as NoDitherOS)

    static int S(double v) => Nd.S(v);
    static double SF(double v) => Nd.SF(v);

    int PxW => (int)Math.Round(_canvas.ActualWidth * Nd.Scale);
    int PxH => (int)Math.Round(_canvas.ActualHeight * Nd.Scale);
    int BarsH => S(ToolbarH) + (_markup ? S(MarkupH) : 0);

    /// <summary>Image pixels per style point.</summary>
    public double Unit => _st.Img == null ? 1 : Math.Max(1.0, Math.Max(_st.Img.W, _st.Img.H) / 1000.0);

    public double ViewScale()
    {
        if (_zoom > 0) return _zoom;
        if (_st.Img == null) return 1;
        double aw = PxW, ah = PxH - BarsH;
        return Math.Min(1.0, Math.Min(aw / _st.Img.W, ah / _st.Img.H));
    }

    struct View
    {
        public Int32Rect Area;   // the picture area
        public double K;         // display pixels per image pixel
        public double Ox, Oy;    // position of image pixel (0, 0)
        public int Dw, Dh;
    }

    View GetView()
    {
        var g = new View();
        int top = BarsH;
        g.Area = new Int32Rect(0, top, PxW, Math.Max(1, PxH - top));
        g.K = ViewScale();
        int iw = _st.Img?.W ?? 1, ih = _st.Img?.H ?? 1;
        g.Dw = Math.Max(1, (int)Math.Round(iw * g.K));
        g.Dh = Math.Max(1, (int)Math.Round(ih * g.K));
        _sx = Math.Clamp(_sx, 0, Math.Max(0, g.Dw - g.Area.Width));
        _sy = Math.Clamp(_sy, 0, Math.Max(0, g.Dh - g.Area.Height));
        g.Ox = g.Dw <= g.Area.Width ? g.Area.X + (g.Area.Width - g.Dw) / 2 : g.Area.X - _sx;
        g.Oy = g.Dh <= g.Area.Height ? g.Area.Y + (g.Area.Height - g.Dh) / 2 : g.Area.Y - _sy;
        return g;
    }

    public void SetZoom(double z)
    {
        var g = GetView();
        // keep the centre of the view where it is
        double cx = (g.Area.X + g.Area.Width / 2 - g.Ox) / g.K, cy = (g.Area.Y + g.Area.Height / 2 - g.Oy) / g.K;
        _zoom = z <= 0 ? 0 : Math.Clamp(z, 0.02, 32.0);
        double k = ViewScale();
        _sx = (int)Math.Round(cx * k) - g.Area.Width / 2;
        _sy = (int)Math.Round(cy * k) - g.Area.Height / 2;
        Redraw();
    }

    static void ImgPt(View g, double x, double y, out double ix, out double iy)
    {
        ix = (x - g.Ox) / g.K;
        iy = (y - g.Oy) / g.K;
    }

    // ------------------------------------------------------------------ drawing the picture

    BitmapSource _cScaled, _cAdj;
    int _cGen = -1, _cScaledGen;
    Int32Rect _cVr;
    double _cK;
    Adjust _cAdjV;
    int _cAdjKey = -1;

    void DrawPicture(DrawingContext dc, View g)
    {
        var img = _st.Img;
        double k = g.K;
        int iw = img.W, ih = img.H;
        int x0 = Math.Max(0, (int)Math.Floor((g.Area.X - g.Ox) / k) - 2), y0 = Math.Max(0, (int)Math.Floor((g.Area.Y - g.Oy) / k) - 2);
        int x1 = Math.Min(iw, (int)Math.Ceiling((g.Area.X + g.Area.Width - g.Ox) / k) + 2);
        int y1 = Math.Min(ih, (int)Math.Ceiling((g.Area.Y + g.Area.Height - g.Oy) / k) + 2);
        if (x1 <= x0 || y1 <= y0) return;
        var vr = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        bool whole = vr.Width == iw && vr.Height == ih;
        PvImage basePic = img;
        int key = _imgGen & 0x7FFFFFFF;
        if (k < 1 || !whole)
        {
            // the visible part at display size (exact area average when shrinking)
            if (_cScaledPic == null || _cGen != _imgGen || !_cVr.Equals(vr) || _cK != k)
            {
                var part = whole ? img : PvOps.Crop(img, vr, false);
                _cScaledPic = k < 1 ? PvOps.Resize(part, Math.Max(1, (int)Math.Round(vr.Width * k)), Math.Max(1, (int)Math.Round(vr.Height * k))) : part;
                _cScaled = null;
                _cGen = _imgGen;
                _cVr = vr;
                _cK = k;
                _cScaledGen++;
            }
            basePic = _cScaledPic;
            key = _cScaledGen | int.MinValue;
        }
        PvImage show = basePic;
        if (!_st.Adj.IsIdentity)
        {
            if (_cAdjPic == null || _cAdjKey != key || !_cAdjV.Equals(_st.Adj))
            {
                _cAdjPic = PvOps.AdjustApply(basePic, _st.Adj);
                _cAdj = null;
                _cAdjKey = key;
                _cAdjV = _st.Adj;
            }
            show = _cAdjPic;
        }
        if (_fullGen != _imgGen) { _full = null; _fullGen = _imgGen; }
        BitmapSource bmp = show == _cAdjPic ? (_cAdj ??= show.ToBitmap()) : show == _cScaledPic ? (_cScaled ??= show.ToBitmap()) : (_full ??= show.ToBitmap());
        int bx = (int)Math.Round(g.Ox + vr.X * k), by = (int)Math.Round(g.Oy + vr.Y * k);
        var dg = new DrawingGroup();
        Rect dest = k <= 1 ? new Rect(bx, by, show.W, show.H) : new Rect(bx, by, Math.Round(vr.Width * k), Math.Round(vr.Height * k));
        RenderOptions.SetBitmapScalingMode(dg, k <= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        dg.Children.Add(new ImageDrawing(bmp, dest));
        dc.DrawDrawing(dg);
    }

    PvImage _cScaledPic, _cAdjPic;
    BitmapSource _full;
    int _fullGen = -1;


    // marching ants: alternating black and white dashes along a polyline
    static void Dashed(DrawingContext dc, IReadOnlyList<Point> p, bool closed)
    {
        double dash = SF(4), acc = 0;
        int n = p.Count;
        var black = Nd.RoundPen(0xFF000000u, 1.0);
        var white = Nd.RoundPen(0xFFFFFFFFu, 1.0);
        for (int i = 0; i + 1 < n + (closed ? 1 : 0); i++)
        {
            Point a = p[i], b = p[(i + 1) % n];
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01) continue;
            double ux = dx / len, uy = dy / len, t = 0;
            while (t < len)
            {
                double inn = acc - dash * Math.Floor(acc / dash);
                double seg = Math.Min(dash - inn, len - t);
                bool odd = ((int)Math.Floor(acc / dash) & 1) == 1;
                dc.DrawLine(odd ? white : black, new Point(a.X + ux * t, a.Y + uy * t), new Point(a.X + ux * (t + seg), a.Y + uy * (t + seg)));
                t += seg;
                acc += seg;
            }
        }
    }

    byte SelCov(int x, int y)
    {
        var r = _sel.R;
        if (x < r.X || y < r.Y || x >= r.X + r.Width || y >= r.Y + r.Height) return 0;
        switch (_sel.Kind)
        {
            case SelKind.Rect: return 255;
            case SelKind.Oval:
                {
                    double rx = r.Width / 2.0, ry = r.Height / 2.0;
                    double fx = (x + 0.5 - r.X - rx) / rx, fy = (y + 0.5 - r.Y - ry) / ry;
                    return fx * fx + fy * fy <= 1 ? (byte)255 : (byte)0;
                }
            case SelKind.Mask: return _sel.Mask[(y - r.Y) * r.Width + (x - r.X)];
        }
        return 0;
    }

    BitmapSource _ov;
    int _ovGen = -1;
    Int32Rect _ovArea;
    double _ovK, _ovOx, _ovOy;

    void DrawMaskOutline(DrawingContext dc, View g)
    {
        var r = _sel.R;
        int sx0 = (int)Math.Floor(g.Ox + r.X * g.K) - 1, sy0 = (int)Math.Floor(g.Oy + r.Y * g.K) - 1;
        var sr = PvOps.Intersect(new Int32Rect(sx0, sy0, (int)Math.Ceiling(r.Width * g.K) + 3, (int)Math.Ceiling(r.Height * g.K) + 3), int.MaxValue, int.MaxValue);
        sr = Inter(sr, g.Area);
        if (sr.IsEmpty) return;
        if (_ov == null || _ovGen != _selGen || !_ovArea.Equals(sr) || _ovK != g.K || _ovOx != g.Ox || _ovOy != g.Oy)
        {
            var px = new uint[sr.Width * sr.Height];
            // inside flags for the region plus a 1 pixel border
            int W = sr.Width + 2, H = sr.Height + 2;
            var inn = new bool[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double fx = (sr.X + x - 1 + 0.5 - g.Ox) / g.K, fy = (sr.Y + y - 1 + 0.5 - g.Oy) / g.K;
                    inn[y * W + x] = fx >= 0 && fy >= 0 && SelCov((int)fx, (int)fy) >= 128;
                }
            for (int y = 0; y < sr.Height; y++)
                for (int x = 0; x < sr.Width; x++)
                {
                    int c = (y + 1) * W + x + 1;
                    if (inn[c] && (!inn[c - 1] || !inn[c + 1] || !inn[c - W] || !inn[c + W]))
                        px[y * sr.Width + x] = (((sr.X + x + sr.Y + y) / 4) & 1) == 1 ? 0xFFFFFFFFu : 0xFF000000u;
                }
            _ov = new PvImage(sr.Width, sr.Height, px).ToBitmap();
            _ovGen = _selGen;
            _ovArea = sr;
            _ovK = g.K;
            _ovOx = g.Ox;
            _ovOy = g.Oy;
        }
        var dg = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(dg, BitmapScalingMode.NearestNeighbor);
        dg.Children.Add(new ImageDrawing(_ov, new Rect(sr.X, sr.Y, sr.Width, sr.Height)));
        dc.DrawDrawing(dg);
    }

    static Int32Rect Inter(Int32Rect a, Int32Rect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.X + a.Width, b.X + b.Width), y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return x1 > x0 && y1 > y0 ? new Int32Rect(x0, y0, x1 - x0, y1 - y0) : Int32Rect.Empty;
    }

    void DrawSelection(DrawingContext dc, View g)
    {
        if (_sel.Kind == SelKind.None) return;
        double x0 = g.Ox + _sel.R.X * g.K, y0 = g.Oy + _sel.R.Y * g.K;
        double x1 = x0 + _sel.R.Width * g.K, y1 = y0 + _sel.R.Height * g.K;
        if (_sel.Kind == SelKind.Rect)
            Dashed(dc, new[] { new Point(x0 + 0.5, y0 + 0.5), new Point(x1 - 0.5, y0 + 0.5), new Point(x1 - 0.5, y1 - 0.5), new Point(x0 + 0.5, y1 - 0.5) }, true);
        else if (_sel.Kind == SelKind.Oval)
        {
            var p = new Point[96];
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
            for (int i = 0; i < 96; i++)
            {
                double a = i * 6.2831853 / 96;
                p[i] = new Point(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry);
            }
            Dashed(dc, p, true);
        }
        else DrawMaskOutline(dc, g);
        // size badge while dragging a new marquee (like Preview)
        if (_drag == Drag.SelNew && _sel.Kind != SelKind.Mask)
        {
            string b = $"{_sel.R.Width} × {_sel.R.Height}";
            double tw = Nd.TextWidth(b, Nd.Weight.Medium, SF(11));
            double bx = x1 + SF(8), by = y1 + SF(8);
            Nd.FillRRect(dc, bx, by, tw + SF(12), SF(18), SF(5), 0xCC000000u);
            Nd.Text(dc, b, Nd.Weight.Medium, SF(11), bx + SF(6), by + SF(13), 0xFFFFFFFFu);
        }
    }

    static Point[] HandlePts(Annot a)
    {
        if (a.Kind == AnKind.Line || a.Kind == AnKind.Arrow) return new[] { new Point(a.X0, a.Y0), new Point(a.X1, a.Y1) };
        a.TightBox(out double x0, out double y0, out double x1, out double y1);
        double mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        return new[] { new Point(x0, y0), new Point(mx, y0), new Point(x1, y0), new Point(x1, my), new Point(x1, y1), new Point(mx, y1), new Point(x0, y1), new Point(x0, my) };
    }

    void DrawHandles(DrawingContext dc, View g)
    {
        if (_anSel < 0 || _anSel >= _st.An.Count) return;
        var a = _st.An[_anSel];
        if (a.Kind == AnKind.Text || a.Kind == AnKind.Path)
        {
            a.TightBox(out double x0, out double y0, out double x1, out double y1);
            uint c = _textEdit ? Nd.Accent : 0x99808080u;
            Nd.StrokeRRect(dc, g.Ox + x0 * g.K - 0.5, g.Oy + y0 * g.K - 0.5, (x1 - x0) * g.K + 1, (y1 - y0) * g.K + 1, 0, 1, c);
        }
        if (_textEdit) return;
        foreach (var p in HandlePts(a))
        {
            double x = g.Ox + p.X * g.K, y = g.Oy + p.Y * g.K;
            Nd.FillCircle(dc, x, y + SF(0.5), SF(5), 0x40000000u);
            Nd.FillCircle(dc, x, y, SF(4.5), 0xFFFFFFFFu);
            Nd.FillCircle(dc, x, y, SF(3.5), Nd.Accent);
        }
    }

    internal void Render(DrawingContext dc)
    {
        var g = GetView();
        int W = PxW, H = PxH;
        Nd.FillRect(dc, new Rect(g.Area.X, g.Area.Y, g.Area.Width, g.Area.Height), Nd.Dark ? 0xFF1E1E1Eu : 0xFFECECECu);
        var areaRect = new Rect(g.Area.X, g.Area.Y, g.Area.Width, g.Area.Height);
        if (_err.Length > 0)
        {
            var r = new Rect(g.Area.X + S(40), g.Area.Y + g.Area.Height / 2 - S(20), Math.Max(1, g.Area.Width - S(80)), S(60));
            var lines = Nd.Wrap(_err, Nd.Weight.Regular, SF(13), r.Width);
            double cap = Nd.CapHeight(Nd.Weight.Regular, SF(13));
            for (int i = 0; i < lines.Count; i++)
            {
                double tw = Nd.TextWidth(lines[i], Nd.Weight.Regular, SF(13));
                Nd.Text(dc, lines[i], Nd.Weight.Regular, SF(13), r.X + (r.Width - tw) / 2, r.Y + SF(18) * i + (SF(18) + cap) / 2, Nd.SecondaryLabel);
            }
        }
        else if (!_loaded)
            Nd.TextCentered(dc, "Loading…", Nd.Weight.Regular, SF(13), areaRect, Nd.TertiaryLabel);
        else
        {
            dc.PushClip(new RectangleGeometry(areaRect));
            DrawPicture(dc, g);
            var pic = Rect.Intersect(new Rect((int)g.Ox, (int)g.Oy, g.Dw + 1, g.Dh + 1), areaRect);
            if (!pic.IsEmpty)
            {
                dc.PushClip(new RectangleGeometry(pic));
                foreach (var a in _st.An) a.Draw(dc, g.K, g.Ox, g.Oy);
                if (_drag == Drag.Draw && _cur != null && _cur.Pts.Count > 0) _cur.Draw(dc, g.K, g.Ox, g.Oy);
                dc.Pop();
            }
            DrawSelection(dc, g);
            if (_drag == Drag.Lasso && _cur != null && _cur.Pts.Count > 1)
                Dashed(dc, _cur.Pts.Select(p => new Point(g.Ox + p.X * g.K, g.Oy + p.Y * g.K)).ToList(), false);
            if (_drag == Drag.Alpha)
            {
                string b = $"Instant Alpha: {_alphaTol * 100 / 255}%";
                double tw = Nd.TextWidth(b, Nd.Weight.Medium, SF(11)), bx = _alphaSx + SF(12), by = _alphaSy + SF(12);
                Nd.FillRRect(dc, bx, by, tw + SF(12), SF(18), SF(5), 0xCC000000u);
                Nd.Text(dc, b, Nd.Weight.Medium, SF(11), bx + SF(6), by + SF(13), 0xFFFFFFFFu);
                Nd.FillCircle(dc, _alphaSx, _alphaSy, SF(3), Nd.Accent);
            }
            DrawHandles(dc, g);
            dc.Pop();
        }
        DrawBars(dc, W);
        if (_saving)
        {
            string msg = !_saveIsExport ? "Saving…" : "Exporting…";
            double tw = Nd.TextWidth(msg, Nd.Weight.Medium, SF(12));
            double bx = (W - tw) / 2 - SF(14), by = H - SF(44);
            Nd.FillRRect(dc, bx, by, tw + SF(28), SF(26), SF(13), 0xD9323232u);
            Nd.Text(dc, msg, Nd.Weight.Medium, SF(12), bx + SF(14), by + SF(17), 0xFFFFFFFFu);
        }
        DrawPopover(dc, W);
        DrawSheet(dc, W, H);
    }

    // ------------------------------------------------------------------ picture operations

    public void ReplaceImage(PvImage img, double[] m)
    {
        if (img == null) { ShellHost.ShowAlert("The picture couldn't be changed.", "There isn't enough memory."); return; }
        PushUndo();
        _st.Img = img;
        if (m != null)
            foreach (var a in _st.An)
            {
                double tw = a.X1 - a.X0;
                a.Transform(m);
                if (a.Kind == AnKind.Text && m[0] == 0)
                {
                    // rotated: text stays horizontal, same width, about the same centre
                    double cx = (a.X0 + a.X1) / 2, cy = (a.Y0 + a.Y1) / 2;
                    a.X0 = cx - tw / 2; a.X1 = cx + tw / 2; a.Y0 = cy;
                    FitText(a);
                    double h = a.Y1 - a.Y0;
                    a.Y0 = cy - h / 2; a.Y1 = cy + h / 2;
                }
            }
        SelClear();
        _imgGen++;
        Changed();
    }

    public void Rotate(bool cw)
    {
        if (!_loaded) return;
        EndText();
        int iw = _st.Img.W, ih = _st.Img.H;
        double[] mc = { 0, -1, ih, 1, 0, 0 }, mw = { 0, 1, 0, -1, 0, iw };
        ReplaceImage(PvOps.Rotate(_st.Img, cw), cw ? mc : mw);
    }

    public void Flip(bool horizontal)
    {
        if (!_loaded) return;
        EndText();
        int iw = _st.Img.W, ih = _st.Img.H;
        double[] mh = { -1, 0, iw, 0, 1, 0 }, mv = { 1, 0, 0, 0, -1, ih };
        ReplaceImage(PvOps.Flip(_st.Img, horizontal), horizontal ? mh : mv);
    }

    public bool HasSelection => _sel.Kind != SelKind.None;

    public void Crop()
    {
        if (!_loaded || _sel.Kind == SelKind.None) return;
        EndText();
        var r = _sel.R;
        var o = PvOps.Crop(_st.Img, r, _sel.Kind == SelKind.Oval);
        if (o != null && _sel.Kind == SelKind.Mask)
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                    if (_sel.Mask[y * r.Width + x] == 0) o.Px[y * o.W + x] = 0;
        ReplaceImage(o, new double[] { 1, 0, -r.X, 0, 1, -r.Y });
        _zoom = 0;
    }

    public void Resize(int nw, int nh)
    {
        if (!_loaded || nw < 1 || nh < 1) return;
        if (nw == _st.Img.W && nh == _st.Img.H) return;
        if ((long)nw * nh > 100_000_000) { ShellHost.ShowAlert("The picture can't be that large.", "Choose a size under 100 megapixels."); return; }
        EndText();
        ReplaceImage(PvOps.Resize(_st.Img, nw, nh), new double[] { (double)nw / _st.Img.W, 0, 0, 0, (double)nh / _st.Img.H, 0 });
        _zoom = 0;
    }

    // Delete: the selected pixels become transparent
    void DeleteSelectionPixels()
    {
        var o = _st.Img.Copy();
        var r = _sel.R;
        for (int y = r.Y; y < r.Y + r.Height; y++)
            for (int x = r.X; x < r.X + r.Width; x++)
            {
                uint c = SelCov(x, y);
                if (c == 0) continue;
                uint p = o.Px[y * o.W + x], k = 255 - c, q = 0;
                for (int sh = 0; sh < 32; sh += 8) q |= PvOps.Div255(((p >> sh) & 0xFF) * k) << sh;
                o.Px[y * o.W + x] = q;
            }
        var keep = _sel;
        ReplaceImage(o, null);
        _sel = keep;   // (ReplaceImage clears the selection; keep it like Preview does)
        _selGen++;
    }

    void SetMaskSel(byte[] full, int iw, int ih)
    {
        int x0 = iw, y0 = ih, x1 = -1, y1 = -1;
        for (int y = 0; y < ih; y++)
            for (int x = 0; x < iw; x++)
                if (full[y * iw + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        SelClear();
        if (x1 < 0) return;
        var r = new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        var m = new byte[r.Width * r.Height];
        for (int y = 0; y < r.Height; y++) Array.Copy(full, (y + r.Y) * iw + r.X, m, y * r.Width, r.Width);
        _sel = new Sel { Kind = SelKind.Mask, R = r, Mask = m };
    }

    public void InvertSelection()
    {
        if (!_loaded) return;
        int iw = _st.Img.W, ih = _st.Img.H;
        var full = new byte[iw * ih];
        for (int y = 0; y < ih; y++)
            for (int x = 0; x < iw; x++) full[y * iw + x] = SelCov(x, y) >= 128 ? (byte)0 : (byte)255;
        SetMaskSel(full, iw, ih);
        Redraw();
    }

    // Instant Alpha: the connected area of similar colour
    void InstantAlpha(int sx, int sy, int tol)
    {
        var img = _st.Img;
        int iw = img.W, ih = img.H;
        if (sx < 0 || sy < 0 || sx >= iw || sy >= ih) return;
        var m = new byte[iw * ih];
        uint seed = img.Px[sy * iw + sx];
        bool Similar(int x, int y)
        {
            uint p = img.Px[y * iw + x];
            int d = 0;
            for (int s = 0; s < 32; s += 8) d = Math.Max(d, Math.Abs((int)((p >> s) & 0xFF) - (int)((seed >> s) & 0xFF)));
            return d <= tol;
        }
        var stk = new Stack<(int x, int y)>();
        stk.Push((sx, sy));
        while (stk.Count > 0)
        {
            var (x, y) = stk.Pop();
            if (m[y * iw + x] != 0 || !Similar(x, y)) continue;
            int l = x, r = x;
            while (l > 0 && m[y * iw + l - 1] == 0 && Similar(l - 1, y)) l--;
            while (r < iw - 1 && m[y * iw + r + 1] == 0 && Similar(r + 1, y)) r++;
            for (int i = l; i <= r; i++) m[y * iw + i] = 255;
            for (int ny = y - 1; ny <= y + 1; ny += 2)
            {
                if (ny < 0 || ny >= ih) continue;
                for (int i = l; i <= r; i++)
                {
                    if (m[ny * iw + i] != 0 || !Similar(i, ny)) continue;
                    stk.Push((i, ny));
                    while (i + 1 <= r && Similar(i + 1, ny)) i++;
                }
            }
        }
        SetMaskSel(m, iw, ih);
    }

    void LassoSelection()
    {
        if (_cur == null || _cur.Pts.Count < 3) { SelClear(); return; }
        SelClear();
        var m = PvOps.FillPolygon(_cur.Pts, _st.Img.W, _st.Img.H, out var b);
        if (m != null && m.Any(v => v != 0)) _sel = new Sel { Kind = SelKind.Mask, R = b, Mask = m };
    }

    public void SelectAll()
    {
        if (!_loaded) return;
        SelClear();
        _sel = new Sel { Kind = SelKind.Rect, R = new Int32Rect(0, 0, _st.Img.W, _st.Img.H) };
        _anSel = -1;
        Redraw();
    }

    public void Deselect()
    {
        EndText();
        SelClear();
        _anSel = -1;
        Redraw();
    }

    // ------------------------------------------------------------------ annotations

    int AddAnnot(Annot a)
    {
        EndText();
        PushUndo();
        _st.An.Add(a);
        _anSel = _st.An.Count - 1;
        SelClear();
        Changed();
        return _anSel;
    }

    void RemoveAnnot(int i)
    {
        _st.An.RemoveAt(i);
        if (_anSel == i) _anSel = -1;
        else if (_anSel > i) _anSel--;
    }

    static void FitText(Annot a)
    {
        double pad = a.Font * 0.25, w = Math.Max(1.0, a.X1 - a.X0 - 2 * pad);
        double h = Nd.MeasureWrapped(a.Text.Length > 0 ? a.Text : " ", Nd.Weight.Regular, a.Font, w, a.Font * 1.25);
        a.Y1 = a.Y0 + h + 2 * pad;
    }

    void EndText()
    {
        if (!_textEdit) return;
        _textEdit = false;
        if (_anSel >= 0 && _anSel < _st.An.Count && _st.An[_anSel].Text.Length == 0) RemoveAnnot(_anSel);
    }

    Annot Styled(AnKind kind)
    {
        double u = Unit;
        bool closed = kind != AnKind.Line && kind != AnKind.Arrow && kind != AnKind.Path && kind != AnKind.Text;
        return new Annot
        {
            Kind = kind,
            Stroke = StyleStroke,
            Fill = closed ? StyleFill : 0,
            Width = StyleWidth * u,
            Font = StyleFont * u,
            TextColor = StyleText,
        };
    }

    // the middle of what is on screen, in image pixels, and a size that suits it
    void ViewCenter(out double cx, out double cy, out double size)
    {
        var g = GetView();
        ImgPt(g, g.Area.X, g.Area.Y, out double x0, out double y0);
        ImgPt(g, g.Area.X + g.Area.Width, g.Area.Y + g.Area.Height, out double x1, out double y1);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(_st.Img.W, x1); y1 = Math.Min(_st.Img.H, y1);
        cx = (x0 + x1) / 2; cy = (y0 + y1) / 2;
        size = Math.Max(8.0, Math.Min(x1 - x0, y1 - y0) * 0.3);
    }

    public void InsertShape(AnKind kind)
    {
        if (!_loaded) return;
        if (kind == AnKind.Text) { InsertText(); return; }
        ViewCenter(out double cx, out double cy, out double sz);
        var a = Styled(kind);
        double hw = kind == AnKind.Bubble ? sz * 0.65 : sz / 2, hh = sz / 2;
        if (kind == AnKind.Line || kind == AnKind.Arrow) hh = 0;
        a.X0 = cx - hw; a.X1 = cx + hw; a.Y0 = cy - hh; a.Y1 = cy + hh;
        AddAnnot(a);
    }

    public void InsertText()
    {
        if (!_loaded) return;
        ViewCenter(out double cx, out double cy, out _);
        var a = Styled(AnKind.Text);
        a.Stroke = 0;
        a.Width = 0;
        a.Text = "Text";
        double bw = a.Font * 8;
        a.X0 = cx - bw / 2; a.X1 = cx + bw / 2; a.Y0 = cy - a.Font;
        FitText(a);
        AddAnnot(a);
        _textEdit = true;
        _textFresh = true;
        Redraw();
    }

    static void ApplyHandle(Annot a, Annot o, int h, double ix, double iy)
    {
        if (o.Kind == AnKind.Line || o.Kind == AnKind.Arrow)
        {
            if (h == 0) { a.X0 = ix; a.Y0 = iy; } else { a.X1 = ix; a.Y1 = iy; }
            return;
        }
        o.TightBox(out double x0, out double y0, out double x1, out double y1);
        double nx0 = x0, ny0 = y0, nx1 = x1, ny1 = y1;
        if (h == 0 || h == 6 || h == 7) nx0 = ix;
        if (h == 2 || h == 3 || h == 4) nx1 = ix;
        if (h == 0 || h == 1 || h == 2) ny0 = iy;
        if (h == 4 || h == 5 || h == 6) ny1 = iy;
        if (o.Kind == AnKind.Path)
        {
            a.Pts = new List<Point>(o.Pts);
            a.X0 = o.X0; a.Y0 = o.Y0; a.X1 = o.X1; a.Y1 = o.Y1;
            double sx = x1 - x0 > 0.5 ? (nx1 - nx0) / (x1 - x0) : 1, sy = y1 - y0 > 0.5 ? (ny1 - ny0) / (y1 - y0) : 1;
            double wd = o.Width, fs = o.Font;
            a.Transform(new[] { sx, 0, nx0 - x0 * sx, 0, sy, ny0 - y0 * sy });
            a.Width = wd; a.Font = fs;
            return;
        }
        a.X0 = Math.Min(nx0, nx1); a.X1 = Math.Max(nx0, nx1); a.Y0 = Math.Min(ny0, ny1); a.Y1 = Math.Max(ny0, ny1);
        if (a.Kind == AnKind.Text) FitText(a);
    }

    static double PtSeg(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2 : 0;
        t = Math.Clamp(t, 0, 1);
        double qx = a.X + t * dx - p.X, qy = a.Y + t * dy - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // Sketch: a stroke that looks like a line, rectangle or oval becomes that shape
    static void Recognize(Annot a)
    {
        int n = a.Pts.Count;
        if (n < 8) return;
        a.TightBox(out double x0, out double y0, out double x1, out double y1);
        double w = x1 - x0, h = y1 - y0, sz = Math.Max(w, h);
        if (sz < 12) return;
        Point f = a.Pts[0], l = a.Pts[n - 1];
        double gap = Math.Sqrt((f.X - l.X) * (f.X - l.X) + (f.Y - l.Y) * (f.Y - l.Y));
        if (gap > 0.25 * sz)
        {
            double dev = 0;
            for (int i = 0; i < n; i++) dev = Math.Max(dev, PtSeg(a.Pts[i], f, l));
            if (gap > sz * 0.5 && dev < 0.06 * gap)
            {
                a.Kind = AnKind.Line;
                a.X0 = f.X; a.Y0 = f.Y; a.X1 = l.X; a.Y1 = l.Y;
                a.Pts.Clear();
            }
            return;
        }
        double rx = w / 2, ry = h / 2, cx = x0 + rx, cy = y0 + ry;
        if (Math.Min(rx, ry) < 6) return;
        double ee = 0, er = 0;
        foreach (var p in a.Pts)
        {
            double ex = (p.X - cx) / rx, ey = (p.Y - cy) / ry;
            ee += Math.Abs(Math.Sqrt(ex * ex + ey * ey) - 1);
            er += Math.Min(Math.Min(Math.Abs(p.X - x0), Math.Abs(p.X - x1)), Math.Min(Math.Abs(p.Y - y0), Math.Abs(p.Y - y1))) / Math.Min(rx, ry);
        }
        ee /= n; er /= n;
        AnKind? kind = null;
        if (er < ee && er < 0.09) kind = AnKind.Rect;
        else if (ee < 0.12) kind = AnKind.Oval;
        if (kind == null) return;
        a.Kind = kind.Value;
        a.X0 = x0; a.Y0 = y0; a.X1 = x1; a.Y1 = y1;
        a.Fill = 0;
        a.Pts.Clear();
    }

    // ------------------------------------------------------------------ clipboard (markup stays inside Preview; pictures go
    // on the Windows clipboard, so other apps get them and Preview gets theirs)

    static Annot _clipAn;
    static bool _clipIsAn;
    static PvImage _clipImg;
    static uint _clipSeq;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    /// <summary>A picture someone else copied since Preview last did.</summary>
    static bool ClipHasForeignPicture => GetClipboardSequenceNumber() != _clipSeq && PvFile.ClipboardHasPicture();

    public static bool CanPaste(PreviewWindow w) => ClipHasForeignPicture || (_clipIsAn ? w != null && w._loaded : _clipImg != null);
    public static bool CanNewFromClipboard => ClipHasForeignPicture || _clipImg != null;

    public void Copy()
    {
        if (!_loaded) return;
        if (_anSel >= 0)
        {
            _clipAn = _st.An[_anSel].Clone();
            _clipIsAn = true;
            try { Clipboard.Clear(); } catch { }   // (a copied shape means nothing to other apps)
            _clipSeq = GetClipboardSequenceNumber();
            return;
        }
        var flat = _st.Flatten();
        if (_sel.Kind != SelKind.None)
        {
            var c = PvOps.Crop(flat, _sel.R, _sel.Kind == SelKind.Oval);
            if (c != null && _sel.Kind == SelKind.Mask)
                for (int y = 0; y < c.H; y++)
                    for (int x = 0; x < c.W; x++)
                        if (_sel.Mask[y * _sel.R.Width + x] == 0) c.Px[y * c.W + x] = 0;
            flat = c;
        }
        _clipImg = flat;
        _clipIsAn = false;
        if (flat != null) PvFile.CopyToClipboard(flat, _untitled ? "" : _name);
        _clipSeq = GetClipboardSequenceNumber();
    }

    public void DeleteSel()
    {
        if (_anSel >= 0)
        {
            PushUndo();
            RemoveAnnot(_anSel);
            Changed();
        }
        else if (_sel.Kind != SelKind.None) DeleteSelectionPixels();
    }

    public void Cut() { Copy(); DeleteSel(); }

    public static void NewFromClipboard()
    {
        var pic = ClipHasForeignPicture ? PvFile.FromClipboard() : null;
        if (pic != null) OpenImage(pic);
        else if (_clipImg != null) OpenImage(_clipImg.Copy());
    }

    public static void Paste(PreviewWindow w)
    {
        var pic = ClipHasForeignPicture ? PvFile.FromClipboard() : null;
        if (pic != null) OpenImage(pic);
        else if (_clipIsAn)
        {
            if (w == null || !w._loaded) return;
            var a = _clipAn.Clone();
            double off = 12 * w.Unit;
            a.Move(off, off);
            w.AddAnnot(a);
        }
        else if (_clipImg != null) OpenImage(_clipImg.Copy());
    }

    // ------------------------------------------------------------------ input

    int HitHandle(View g, double x, double y)
    {
        if (_anSel < 0 || _textEdit) return -1;
        var p = HandlePts(_st.An[_anSel]);
        for (int i = 0; i < p.Length; i++)
        {
            double dx = g.Ox + p[i].X * g.K - x, dy = g.Oy + p[i].Y * g.K - y;
            if (dx * dx + dy * dy <= SF(7) * SF(7)) return i;
        }
        return -1;
    }

    int HitAnnot(double ix, double iy, double tol)
    {
        for (int i = _st.An.Count - 1; i >= 0; i--)
            if (_st.An[i].Hit(ix, iy, tol)) return i;
        return -1;
    }

    void CanvasDown(double x, double y, int clicks)
    {
        var g = GetView();
        ImgPt(g, x, y, out double ix, out double iy);
        _px = ix; _py = iy;
        _pushed = false;
        int h = HitHandle(g, x, y);
        if (h >= 0)
        {
            _orig = _st.An[_anSel].Clone();
            _drag = Drag.Handle;
            _handle = h;
            return;
        }
        int hit = HitAnnot(ix, iy, SF(5) / g.K);
        if (_textEdit && hit != _anSel) EndText();
        if (hit >= 0)
        {
            hit = HitAnnot(ix, iy, SF(5) / g.K);   // indices may have moved
            _anSel = hit;
            if (hit < 0) return;
            if (clicks >= 2 && _st.An[hit].Kind == AnKind.Text && !_textEdit)
            {
                PushUndo();
                _textEdit = true;
                _textFresh = false;
                Redraw();
                return;
            }
            if (_textEdit) { Redraw(); return; }
            _orig = _st.An[hit].Clone();
            _drag = Drag.Move;
            Redraw();
            return;
        }
        _anSel = -1;
        var img = _st.Img;
        switch (_tool)
        {
            case Tool.Sketch:
            case Tool.Draw:
                _cur = Styled(AnKind.Path);
                if (_tool == Tool.Draw) _cur.Width = Math.Max(1.0, _cur.Width * 0.7);
                _cur.Pts.Add(new Point(ix, iy));
                _drag = Drag.Draw;
                break;
            case Tool.Lasso:
                _cur = new Annot();
                _cur.Pts.Add(new Point(Math.Clamp(ix, 0, img.W), Math.Clamp(iy, 0, img.H)));
                _drag = Drag.Lasso;
                break;
            case Tool.Alpha:
                _alphaX = (int)Math.Floor(ix); _alphaY = (int)Math.Floor(iy);
                _alphaSx = x; _alphaSy = y;
                _alphaTol = 24;
                _drag = Drag.Alpha;
                break;
            default:
                if (_sel.Kind == (_tool == Tool.Oval ? SelKind.Oval : SelKind.Rect) && SelCov((int)Math.Floor(ix), (int)Math.Floor(iy)) != 0)
                {
                    _selOrig = _sel.R;
                    _drag = Drag.SelMove;
                }
                else
                {
                    SelClear();
                    _drag = Drag.SelNew;
                }
                break;
        }
        Redraw();
    }

    void CanvasMove(double x, double y, bool shift)
    {
        var g = GetView();
        ImgPt(g, x, y, out double ix, out double iy);
        var img = _st.Img;
        switch (_drag)
        {
            case Drag.Handle:
            case Drag.Move:
                {
                    if (!_pushed)
                    {
                        if (_drag == Drag.Move && Math.Abs(ix - _px) * g.K < 2 && Math.Abs(iy - _py) * g.K < 2) return;
                        PushUndo();
                        _pushed = true;
                    }
                    var a = _st.An[_anSel];
                    if (_drag == Drag.Handle) ApplyHandle(a, _orig, _handle, ix, iy);
                    else
                    {
                        var c = _orig.Clone();
                        c.Move(ix - _px, iy - _py);
                        _st.An[_anSel] = c;
                    }
                    _change++;
                    break;
                }
            case Drag.Draw:
                {
                    var l = _cur.Pts[^1];
                    if (Math.Abs(ix - l.X) * g.K + Math.Abs(iy - l.Y) * g.K >= SF(1.5)) _cur.Pts.Add(new Point(ix, iy));
                    break;
                }
            case Drag.Lasso:
                _cur.Pts.Add(new Point(Math.Clamp(ix, 0, img.W), Math.Clamp(iy, 0, img.H)));
                break;
            case Drag.SelNew:
                {
                    double ax = Math.Clamp(_px, 0, img.W), ay = Math.Clamp(_py, 0, img.H);
                    double bx = Math.Clamp(ix, 0, img.W), by = Math.Clamp(iy, 0, img.H);
                    if (shift)
                    {
                        // square / circle
                        double s = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
                        bx = ax + (bx < ax ? -s : s); by = ay + (by < ay ? -s : s);
                        bx = Math.Clamp(bx, 0, img.W); by = Math.Clamp(by, 0, img.H);
                    }
                    int x0 = (int)Math.Round(Math.Min(ax, bx)), y0 = (int)Math.Round(Math.Min(ay, by)), x1 = (int)Math.Round(Math.Max(ax, bx)), y1 = (int)Math.Round(Math.Max(ay, by));
                    _sel = new Sel { Kind = _tool == Tool.Oval ? SelKind.Oval : SelKind.Rect, R = new Int32Rect(x0, y0, x1 - x0, y1 - y0) };
                    _selGen++;
                    break;
                }
            case Drag.SelMove:
                {
                    var r = _selOrig;
                    r.X = Math.Clamp(r.X + (int)Math.Round(ix - _px), 0, img.W - r.Width);
                    r.Y = Math.Clamp(r.Y + (int)Math.Round(iy - _py), 0, img.H - r.Height);
                    _sel.R = r;
                    _selGen++;
                    break;
                }
            case Drag.Alpha:
                _alphaTol = Math.Clamp(24 + (int)((x - _alphaSx) + (y - _alphaSy)) / 2, 0, 255);
                break;
            default: return;
        }
        Redraw();
    }

    void CanvasUp()
    {
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Handle:
            case Drag.Move:
                if (_pushed) Changed();
                break;
            case Drag.Draw:
                {
                    var a = _cur;
                    _cur = null;
                    if (_tool == Tool.Sketch) Recognize(a);
                    AddAnnot(a);
                    break;
                }
            case Drag.Lasso:
                LassoSelection();
                _cur = null;
                break;
            case Drag.SelNew:
                if (_sel.R.Width < 2 || _sel.R.Height < 2) SelClear();
                break;
            case Drag.Alpha:
                InstantAlpha(_alphaX, _alphaY, _alphaTol);
                break;
        }
        Redraw();
    }

    bool TextKey(Key key, ModifierKeys mods)
    {
        var a = _st.An[_anSel];
        if ((mods & ModifierKeys.Control) != 0) return false;
        switch (key)
        {
            case Key.Escape: EndText(); break;
            case Key.Back:
                if (_textFresh) a.Text = "";
                if (a.Text.Length > 0)
                {
                    int n = a.Text.Length - 1;
                    if (n > 0 && char.IsLowSurrogate(a.Text[n])) n--;
                    a.Text = a.Text[..n];
                }
                break;
            case Key.Enter:
                if (_textFresh) a.Text = "";
                a.Text += "\n";
                break;
            default: return false;
        }
        _textFresh = false;
        if (_textEdit) FitText(a);
        Changed();
        return true;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (_sheet != Sheet.None || !_textEdit || _anSel < 0 || _anSel >= _st.An.Count) return;
        string t = e.Text;
        if (string.IsNullOrEmpty(t) || t.Any(c => c < 32 || c == 127)) return;
        var a = _st.An[_anSel];
        if (_textFresh) a.Text = "";
        if (a.Text.Length + t.Length < 511) a.Text += t;
        _textFresh = false;
        FitText(a);
        Changed();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_sheet != Sheet.None) { SheetKey(e); if (e.Handled) return; base.OnPreviewKeyDown(e); return; }
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool cmd = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if (_textEdit && _anSel >= 0 && _anSel < _st.An.Count && TextKey(key, mods)) { e.Handled = true; return; }
        if (cmd)
        {
            bool used = true;
            switch (key)
            {
                case Key.S: Save(); break;
                case Key.Z: History(shift); break;
                case Key.C: if (shift) PreviewPanels.ShowAdjust(); else Copy(); break;
                case Key.X: Cut(); break;
                case Key.V: Paste(this); break;
                case Key.N: NewFromClipboard(); break;
                case Key.O: OpenFiles(); break;
                case Key.A: if (shift) ShowMarkup(!_markup); else SelectAll(); break;
                case Key.I: if (shift) InvertSelection(); else PreviewPanels.ShowInspector(); break;
                case Key.K: Crop(); break;
                case Key.L: Rotate(false); break;
                case Key.R: Rotate(true); break;
                case Key.D0: case Key.NumPad0: SetZoom(1); break;
                case Key.D9: case Key.NumPad9: SetZoom(0); break;
                case Key.OemPlus: case Key.Add: SetZoom(ViewScale() * 1.25); break;
                case Key.OemMinus: case Key.Subtract: SetZoom(ViewScale() / 1.25); break;
                default: used = false; break;
            }
            if (used) { e.Handled = true; return; }
            base.OnPreviewKeyDown(e);   // ⌘W, ⌘M
            return;
        }
        switch (key)
        {
            case Key.Back:
            case Key.Delete: DeleteSel(); e.Handled = true; return;
            case Key.Escape:
                if (_popover != Popover.None) _popover = Popover.None;
                else Deselect();
                Redraw();
                e.Handled = true;
                return;
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                if (_anSel >= 0)
                {
                    // nudge
                    double s = (shift ? 10 : 1) / ViewScale();
                    double dx = key == Key.Left ? -s : key == Key.Right ? s : 0, dy = key == Key.Up ? -s : key == Key.Down ? s : 0;
                    if (!e.IsRepeat) PushUndo();
                    _st.An[_anSel].Move(dx, dy);
                    Changed();
                }
                else if (!Dirty && !_untitled) Step(key == Key.Right || key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
        }
        base.OnPreviewKeyDown(e);
    }

    void ShowMarkup(bool on)
    {
        _markup = on;
        Redraw();
    }

    public void ToggleMarkup() => ShowMarkup(!_markup);
    public bool MarkupShown => _markup;

    internal void OnCanvasDown(Point p, int clicks, bool left)
    {
        double x = p.X * Nd.Scale, y = p.Y * Nd.Scale;
        if (_sheet != Sheet.None) { SheetMouse(x, y, true, false); return; }
        if (!left) return;
        if (_popover != Popover.None) { PopoverMouse(x, y, clicks); return; }
        if (y < BarsH)
        {
            EndText();
            BarsMouse(x, y, clicks);
            return;
        }
        if (!_loaded) return;
        CanvasDown(x, y, clicks);
    }

    internal void OnCanvasMove(Point p, bool leftDown)
    {
        double x = p.X * Nd.Scale, y = p.Y * Nd.Scale;
        if (_sheet != Sheet.None) { if (leftDown) SheetMouse(x, y, false, false); return; }
        if (_drag != Drag.None && leftDown) CanvasMove(x, y, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    internal void OnCanvasUp(Point p)
    {
        double x = p.X * Nd.Scale, y = p.Y * Nd.Scale;
        if (_sheet != Sheet.None) { SheetMouse(x, y, false, true); return; }
        if (_drag != Drag.None) CanvasUp();
    }

    internal void OnCanvasWheel(int notches)
    {
        if (!_loaded || _sheet != Sheet.None) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0) SetZoom(ViewScale() * (notches > 0 ? 1.1 : 1 / 1.1));
        else
        {
            if ((mods & ModifierKeys.Shift) != 0) _sx -= notches * S(48);
            else _sy -= notches * S(48);
            Redraw();
        }
    }

    // ------------------------------------------------------------------ saving

    static bool CanEncode(string name)
    {
        string x = Path.GetExtension(name ?? "").ToLowerInvariant();
        return FormatFor(x) != null;
    }

    public void Save()
    {
        if (!_loaded || _saving) return;
        EndText();
        if (_untitled || _readonly || !CanEncode(_name)) { BeginExport(); return; }
        _saveIsExport = false;
        Write(_path, FormatFor(Path.GetExtension(_name).ToLowerInvariant()).Value, 90, unique: false);
    }

    void ExportFinish()
    {
        _sheet = Sheet.None;
        HideSheetFields();
        _saveIsExport = true;
        string dir = WhereDir(_exWhere);
        if (dir == null) { ShellHost.ShowAlert("The picture couldn't be exported.", "There is no disk to save to."); return; }
        string name = _tfA.Text;
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        name += _exFmt == Fmt.Jpeg ? ".jpeg" : ".png";
        Write(Path.Combine(dir, name), _exFmt, (int)Math.Round(_exQuality * 100), unique: true);
    }

    void Write(string path, Fmt fmt, int quality, bool unique)
    {
        var state = _st.Copy();
        int saveChange = _change;
        bool export = _saveIsExport;
        _saving = true;
        Redraw();
        Task.Run(() =>
        {
            var flat = state.Flatten();
            byte[] data = Encode(flat, fmt, quality);
            string target = unique ? UniquePath(path) : path;
            string tmp = target + ".saving";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, target, true);
            return (target, data.LongLength);
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _saving = false;
            if (t.IsFaulted)
            {
                ShellHost.ShowAlert(export ? "The picture couldn't be exported." : "The picture couldn't be saved.", "The disk may be full, read-only or removed.");
                _closeAfterSave = false;
            }
            else
            {
                var (target, len) = t.Result;
                if (!export || _untitled || _readonly)
                {
                    if (export)
                    {
                        // the document is now the exported file
                        _path = target;
                        _name = Path.GetFileName(target);
                        _untitled = _readonly = false;
                        Title = _name;
                    }
                    _savedChange = saveChange;
                }
                _fileLen = len;
            }
            Redraw();
            PreviewPanels.Refresh();
            if (_closeAfterSave && !Dirty) { Close(); return; }
            _closeAfterSave = false;
        }));
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} {i}{ext}");
            if (!File.Exists(p)) return p;
        }
    }

    // ------------------------------------------------------------------ loading

    void FitWindow()
    {
        var wa = SystemParameters.WorkArea;
        double scale = Nd.Scale;
        int bars = (int)Math.Round((ToolbarH + (_markup ? MarkupH : 0)) * scale);
        double maxw = wa.Width * scale * 0.8, maxh = wa.Height * scale * 0.9 - bars;
        double k = Math.Min(1.0, Math.Min(maxw / _st.Img.W, maxh / _st.Img.H));
        double ww = Math.Max(MinWidth * scale, Math.Round(_st.Img.W * k)), wh = Math.Max(320 * scale, Math.Round(_st.Img.H * k) + bars);
        Width = Math.Min(ww / scale, wa.Width);
        Height = Math.Min(wh / scale, wa.Height);
    }

    void ResetDoc()
    {
        _undo.Clear();
        _redo.Clear();
        _st = new EditState();
        SelClear();
        _anSel = -1;
        _textEdit = false;
        _loaded = false;
        _err = "";
        _zoom = 0;
        _sx = _sy = 0;
        _change = _savedChange = 0;
        _imgGen++;
    }

    void LoadFile(string path, bool first)
    {
        ResetDoc();
        _path = path;
        _name = Path.GetFileName(path);
        Title = _name;
        try { _readonly = new FileInfo(path).IsReadOnly; } catch { _readonly = false; }
        Redraw();
        Task.Run(() => (PvFile.Load(path), new FileInfo(path).Length)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (_path != path) return;
            if (t.IsFaulted || t.Result.Item1 == null)
            {
                _err = t.Exception?.InnerException is IOException or UnauthorizedAccessException
                    ? "The file couldn't be read."
                    : $"The file “{_name}” could not be opened. It may be damaged or use a file format that Preview doesn’t recognise.";
            }
            else
            {
                _st.Img = t.Result.Item1;
                _fileLen = t.Result.Item2;
                _loaded = true;
                _imgGen++;
                if (first)
                {
                    FitWindow();
                    CenterOnWorkArea();
                }
            }
            Redraw();
            PreviewPanels.Refresh();
        }));
    }

    void Step(int dir)
    {
        if (_untitled) return;
        string parent = Path.GetDirectoryName(_path);
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(parent).Where(f => PvFile.IsImage(f) && !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => Path.GetFileName(f), Comparer<string>.Create((a, b) => NativeMethods.StrCmpLogicalW(a, b))).ToList();
        }
        catch { return; }
        if (files.Count == 0) return;
        int cur = files.FindIndex(f => string.Equals(f, _path, StringComparison.OrdinalIgnoreCase));
        int i = ((cur + dir) % files.Count + files.Count) % files.Count;
        if (!string.Equals(files[i], _path, StringComparison.OrdinalIgnoreCase)) LoadFile(files[i], first: false);
    }

    public void Revert()
    {
        if (!_untitled && Dirty) LoadFile(_path, first: false);
    }

    /// <summary>
    /// Closing an edited picture saves it, without asking: in place when Preview can write its format exactly
    /// (PNG, JPEG, BMP, TIFF); otherwise (GIF, WebP, HEIC … or a new picture) as a PNG beside it / in Pictures —
    /// never dithered, and the original is left as it was. If it can't be saved the window stays open.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !Dirty || !_loaded) return;
        EndText();
        try { AutoSave(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            ShellHost.ShowAlert($"“{_name}” couldn’t be saved.", ex is UnauthorizedAccessException or IOException ? "The disk may be full, read-only or removed." : ex.Message);
        }
    }

    /// <summary>MacShell quitting / Windows signing out: every edited picture is saved (closing may not get the chance).</summary>
    public static void AutoSaveAll()
    {
        foreach (var w in All.ToList())
        {
            if (!w.Dirty || !w._loaded) continue;
            try { w.EndText(); w.AutoSave(); } catch (Exception ex) { App.Log("Preview autosave failed: " + ex.Message); }
        }
    }

    void AutoSave()
    {
        string ext = Path.GetExtension(_name).ToLowerInvariant();
        string target;
        Fmt fmt;
        if (!_untitled && !_readonly && FormatFor(ext) is Fmt f)
        {
            target = _path;
            fmt = f;
        }
        else
        {
            string dir = _untitled || _readonly ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : Path.GetDirectoryName(_path);
            target = UniquePath(Path.Combine(dir, (_untitled ? "Untitled" : Path.GetFileNameWithoutExtension(_name)) + ".png"));
            fmt = Fmt.Png;
        }
        var data = Encode(_st.Flatten(), fmt, 90);
        string tmp = target + ".saving";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, target, true);
        _savedChange = _change;
        if (!_untitled && string.Equals(target, _path, StringComparison.OrdinalIgnoreCase)) return;
        Settings.AddRecentDoc(target);
    }

    /// <summary>The format Preview writes a file of this kind in, exactly (null: it can't, without losing detail).</summary>
    static Fmt? FormatFor(string ext) => ext switch
    {
        ".png" => Fmt.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => Fmt.Jpeg,
        ".bmp" or ".dib" => Fmt.Bmp,
        ".tif" or ".tiff" => Fmt.Tiff,
        _ => null,
    };

    static byte[] Encode(PvImage img, Fmt fmt, int quality) => fmt switch
    {
        Fmt.Jpeg => PvFile.EncodeJpeg(img, quality),
        Fmt.Bmp => PvFile.EncodeBmp(img),
        Fmt.Tiff => PvFile.EncodeTiff(img),
        _ => PvFile.EncodePng(img),
    };

    // ------------------------------------------------------------------ for the menu bar and panels

    public bool CanUndo => _loaded && _undo.Count > 0;
    public bool CanRedo => _loaded && _redo.Count > 0;
    public bool HasSomethingSelected => _loaded && (_anSel >= 0 || _sel.Kind != SelKind.None);
    public bool IsUntitledOrClean => _untitled || !Dirty;
    public void Undo() => History(false);
    public void Redo() => History(true);
    public void ZoomIn() => SetZoom(ViewScale() * 1.25);
    public void ZoomOut() => SetZoom(ViewScale() / 1.25);
    internal EditState State => _st;
    internal string DocName => _name;
    internal string DocPath => _untitled ? null : _path;
    internal long FileLength => _fileLen;
    internal int ImageGeneration => _imgGen;

    internal void AdjustChanged() => Changed();

    // ------------------------------------------------------------------ the drawing surface

    sealed class PvCanvas : FrameworkElement
    {
        readonly PreviewWindow _w;

        public PvCanvas(PreviewWindow w)
        {
            _w = w;
            Focusable = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        }

        protected override void OnRender(DrawingContext dc)
        {
            Nd.Scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            dc.PushTransform(new ScaleTransform(1 / Nd.Scale, 1 / Nd.Scale));
            _w.Render(dc);
            dc.Pop();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.ChangedButton == MouseButton.Left) CaptureMouse();
            _w.OnCanvasDown(e.GetPosition(this), e.ClickCount, e.ChangedButton == MouseButton.Left);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _w.OnCanvasMove(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.ChangedButton != MouseButton.Left) return;
            ReleaseMouseCapture();
            _w.OnCanvasUp(e.GetPosition(this));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            _w.OnCanvasWheel(Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120));
        }
    }
}
