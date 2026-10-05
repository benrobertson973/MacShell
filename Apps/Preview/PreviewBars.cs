using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Apps.Preview;

/// <summary>
/// Preview's window toolbar (title, Info, Zoom, Share, Markup, Rotate), the Markup toolbar, its popovers (Shapes,
/// Shape Style, Border Color, Fill Color, Text Style) and the Adjust Size / Export sheets (NoDitherOS pv_bars.c).
/// </summary>
public partial class PreviewWindow
{
    // the current Markup style (shared by every picture, like Preview)
    static uint StyleStroke = 0xFFFF3B30u, StyleFill = 0, StyleText = 0xFFFF3B30u;
    static double StyleWidth = 3, StyleFont = 24;   // points

    static readonly uint[] Palette =
    {
        0x000000, 0x5E5E5E, 0x929292, 0xC0C0C0, 0xEBEBEB, 0xFFFFFF,
        0xFF3B30, 0xFF9500, 0xFFCC00, 0x34C759, 0x00C7BE, 0x007AFF,
        0x5856D6, 0xAF52DE, 0xFF2D55, 0xA2845E, 0x8E1B12, 0x0A3D91,
    };
    static readonly double[] Widths = { 0.5, 1, 2, 3, 5, 8, 12 };
    static readonly double[] Sizes = { 12, 14, 18, 24, 36, 48, 64, 72, 96, 144 };
    static readonly AnKind[] ShapeKinds = { AnKind.Line, AnKind.Arrow, AnKind.Rect, AnKind.RRect, AnKind.Oval, AnKind.Bubble, AnKind.Star };
    static readonly string[] WhereNames = { "Pictures", "Desktop", "Documents", "Downloads" };

    enum Tb { Info, ZOut, ZIn, Share, Markup, Rotate, Count }
    enum Mb { Select, Sketch, Draw, Shapes, Text, Adjust, Size, Style, Border, Fill, TextStyle, Crop, Count }
    enum Popover { None, Shapes, Style, Border, Fill, Text }
    enum Sheet { None, Size, Export }
    enum Fmt { Png, Jpeg, Bmp, Tiff }

    readonly Rect[] _tb = new Rect[(int)Tb.Count], _mb = new Rect[(int)Mb.Count];
    Popover _popover;
    Rect _poR;
    Sheet _sheet;
    bool _szPercent, _szProp = true;
    Fmt _exFmt;
    int _exWhere;
    double _exQuality = 0.85;
    int _tfFocus;
    bool _sliderDrag;

    static uint HoverBg => Nd.Dark ? 0x26FFFFFFu : 0x17000000u;

    // ------------------------------------------------------------------ toolbars

    static void TbButton(DrawingContext dc, Rect r, string sym, bool on, bool enabled, uint tint, bool chevron)
    {
        if (on) Nd.FillRRect(dc, r.X, r.Y, r.Width, r.Height, SF(6), HoverBg);
        uint c = !enabled ? Nd.QuaternaryLabel : tint != 0 ? tint : Nd.SecondaryLabel;
        double sz = SF(18), sx = chevron ? r.X + SF(5) : r.X + (r.Width - sz) / 2;
        Nd.Symbol(dc, sym, sx, r.Y + (r.Height - sz) / 2, sz, 1.6, c);
        if (chevron) Nd.Symbol(dc, "chevron.down", r.Right - SF(13), r.Y + (r.Height - SF(8)) / 2, SF(8), 2.4, enabled ? Nd.SecondaryLabel : Nd.QuaternaryLabel);
    }

    Annot SelAnnot => _anSel >= 0 && _anSel < _st.An.Count ? _st.An[_anSel] : null;

    static string SelSymbol(Tool t) => t switch { Tool.Oval => "selection.oval", Tool.Lasso => "lasso", Tool.Alpha => "instant.alpha", _ => "selection.rect" };

    void LayoutMarkup(int y)
    {
        int[] wide = { 1, 0, 0, 1, 0, 0, 0, 1, 1, 1, 1, 0 };
        int[] gapBefore = { 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 1 };
        int x = S(10), h = S(26), by = y + (S(MarkupH) - h) / 2;
        for (int i = 0; i < (int)Mb.Count; i++)
        {
            if (gapBefore[i] != 0) x += S(14);
            int bw = wide[i] != 0 ? S(40) : S(30);
            _mb[i] = new Rect(x, by, bw, h);
            x += bw + S(3);
        }
    }

    void DrawBars(DrawingContext dc, int W)
    {
        int tbh = S(ToolbarH);
        Nd.FillRect(dc, new Rect(0, 0, W, tbh), Nd.ToolbarBg);
        // buttons, right to left: Rotate, Markup, Share, Zoom In, Zoom Out, Info
        int x = W - S(12), y = S(12);
        Tb[] order = { Tb.Rotate, Tb.Markup, Tb.Share, Tb.ZIn, Tb.ZOut, Tb.Info };
        foreach (var id in order)
        {
            x -= S(32);
            _tb[(int)id] = new Rect(x, y, S(32), S(28));
            x -= id == Tb.ZIn ? 0 : S(6);
        }
        bool ok = _loaded;
        TbButton(dc, _tb[(int)Tb.Info], "info", false, ok, 0, false);
        TbButton(dc, _tb[(int)Tb.ZOut], "zoom.out", false, ok, 0, false);
        TbButton(dc, _tb[(int)Tb.ZIn], "zoom.in", false, ok, 0, false);
        TbButton(dc, _tb[(int)Tb.Share], "square.and.arrow.up", false, ok, 0, false);
        TbButton(dc, _tb[(int)Tb.Markup], "markup", _markup, true, _markup ? Nd.Accent : 0, false);
        TbButton(dc, _tb[(int)Tb.Rotate], "rotate.left", false, ok, 0, false);
        // title and subtitle
        double tx = SF(84), maxw = _tb[(int)Tb.Info].X - tx - SF(12);
        uint tc = IsActive ? Nd.Label : Nd.TertiaryLabel;
        if (Dirty && _loaded)
        {
            Nd.TextEllipsized(dc, _name, Nd.Weight.Bold, SF(13), tx, SF(24), maxw, tc);
            Nd.TextEllipsized(dc, _untitled ? "Not saved" : "Edited", Nd.Weight.Regular, SF(11), tx, SF(39), maxw, Nd.SecondaryLabel);
        }
        else Nd.TextEllipsized(dc, _name, Nd.Weight.Bold, SF(13), tx, SF(31), maxw, tc);
        if (!_markup)
        {
            Nd.FillRect(dc, new Rect(0, tbh - 1, W, 1), Nd.Separator);
            return;
        }
        // the Markup toolbar
        int my = tbh, mh = S(MarkupH);
        Nd.FillRect(dc, new Rect(0, my, W, mh), Nd.ToolbarBg);
        Nd.FillRect(dc, new Rect(0, my, W, 1), Nd.WithAlpha(Nd.Separator, 0.5f));
        Nd.FillRect(dc, new Rect(0, my + mh - 1, W, 1), Nd.Separator);
        LayoutMarkup(my);
        var a = SelAnnot;
        bool seltool = _tool <= Tool.Alpha;
        TbButton(dc, _mb[(int)Mb.Select], SelSymbol(_selTool), seltool, ok, 0, true);
        TbButton(dc, _mb[(int)Mb.Sketch], "scribble", _tool == Tool.Sketch, ok, 0, false);
        TbButton(dc, _mb[(int)Mb.Draw], "pencil", _tool == Tool.Draw, ok, 0, false);
        TbButton(dc, _mb[(int)Mb.Shapes], "shapes", _popover == Popover.Shapes, ok, 0, true);
        TbButton(dc, _mb[(int)Mb.Text], "textbox", false, ok, 0, false);
        TbButton(dc, _mb[(int)Mb.Adjust], "adjust.color", false, ok, 0, false);
        TbButton(dc, _mb[(int)Mb.Size], "adjust.size", false, ok, 0, false);
        TbButton(dc, _mb[(int)Mb.Style], "shape.style", _popover == Popover.Style, ok, 0, true);
        uint stroke = a?.Stroke ?? StyleStroke, fill = a?.Fill ?? StyleFill;
        TbButton(dc, _mb[(int)Mb.Border], "border.color", _popover == Popover.Border, ok, (stroke >> 24) != 0 ? stroke : Nd.SecondaryLabel, true);
        var fr = _mb[(int)Mb.Fill];
        TbButton(dc, fr, (fill >> 24) != 0 ? "fill.color" : "border.color", _popover == Popover.Fill, ok, (fill >> 24) != 0 ? fill : Nd.TertiaryLabel, true);
        if ((fill >> 24) == 0) Nd.Line(dc, fr.X + SF(8), fr.Y + SF(20), fr.X + SF(22), fr.Y + SF(6), SF(1.5), 0xFFFF3B30u);
        TbButton(dc, _mb[(int)Mb.TextStyle], "textformat", _popover == Popover.Text, ok, 0, true);
        TbButton(dc, _mb[(int)Mb.Crop], "crop", false, ok && _sel.Kind != SelKind.None, 0, false);
    }

    void TogglePopover(Popover po)
    {
        _popover = _popover == po ? Popover.None : po;
        Redraw();
    }

    void BarsMouse(double x, double y, int clicks)
    {
        var p = new Point(x, y);
        int hit = -1;
        if (y < S(ToolbarH))
        {
            for (int i = 0; i < (int)Tb.Count; i++) if (_tb[i].Contains(p)) hit = i;
            bool ok = _loaded;
            switch ((Tb)hit)
            {
                case Tb.Info: if (ok) PreviewPanels.ShowInspector(); break;
                case Tb.ZOut: if (ok) SetZoom(ViewScale() / 1.25); break;
                case Tb.ZIn: if (ok) SetZoom(ViewScale() * 1.25); break;
                case Tb.Share: if (ok) ShareMenu(_tb[(int)Tb.Share]); break;
                case Tb.Markup:
                    _markup = !_markup;
                    _popover = Popover.None;
                    Redraw();
                    break;
                case Tb.Rotate:
                    if (ok) Rotate((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0);
                    break;
                default:
                    if (clicks >= 2) ToggleZoom();
                    else { try { _canvas.ReleaseMouseCapture(); DragMove(); } catch { } }
                    break;
            }
            return;
        }
        if (!_markup || !_loaded) return;
        for (int i = 0; i < (int)Mb.Count; i++) if (_mb[i].Contains(p)) hit = i;
        switch ((Mb)hit)
        {
            case Mb.Select:
                {
                    string[] names = { "Rectangular Selection", "Elliptical Selection", "Lasso Selection", "Instant Alpha" };
                    _tool = _selTool;
                    var items = new object[4];
                    for (int i = 0; i < 4; i++)
                    {
                        var t = (Tool)i;
                        items[i] = MacShell.Controls.Mb.Item(names[i], () => { _tool = _selTool = t; Redraw(); }, isChecked: _selTool == t);
                    }
                    PopupMenu(_mb[(int)Mb.Select], items);
                    break;
                }
            case Mb.Sketch:
            case Mb.Draw:
                {
                    var t = (Mb)hit == Mb.Sketch ? Tool.Sketch : Tool.Draw;
                    _tool = _tool == t ? _selTool : t;
                    break;
                }
            case Mb.Shapes: TogglePopover(Popover.Shapes); break;
            case Mb.Text: InsertText(); break;
            case Mb.Adjust: PreviewPanels.ShowAdjust(); break;
            case Mb.Size: BeginSize(); break;
            case Mb.Style: TogglePopover(Popover.Style); break;
            case Mb.Border: TogglePopover(Popover.Border); break;
            case Mb.Fill: TogglePopover(Popover.Fill); break;
            case Mb.TextStyle: TogglePopover(Popover.Text); break;
            case Mb.Crop: Crop(); break;
        }
        Redraw();
    }

    /// <summary>A MacShell menu under a toolbar button (rect in device pixels).</summary>
    void PopupMenu(Rect anchor, object[] items)
    {
        var cm = new ContextMenu();
        foreach (var i in items) MacShell.Controls.Mb.Add(cm.Items, i);
        cm.PlacementTarget = _canvas;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
        cm.HorizontalOffset = anchor.X / Nd.Scale;
        cm.VerticalOffset = (anchor.Bottom + S(4)) / Nd.Scale;
        cm.IsOpen = true;
    }

    // ------------------------------------------------------------------ popovers

    static Mb AnchorOf(Popover po) => po switch { Popover.Shapes => Mb.Shapes, Popover.Style => Mb.Style, Popover.Border => Mb.Border, Popover.Fill => Mb.Fill, _ => Mb.TextStyle };
    static int Cell => S(26);

    Rect PopoverRect(int W)
    {
        int pw, ph;
        switch (_popover)
        {
            case Popover.Shapes: pw = S(4 * 44 + 16); ph = S(2 * 44 + 16); break;
            case Popover.Style: pw = S(150); ph = S(12) + S(24) * Widths.Length; break;
            case Popover.Fill: pw = S(16) + 6 * Cell; ph = S(16) + S(28) + 3 * Cell; break;
            case Popover.Border: pw = S(16) + 6 * Cell; ph = S(16) + 3 * Cell; break;
            default: pw = S(16) + 6 * Cell; ph = S(16) + S(2 * 26 + 10) + 3 * Cell; break;
        }
        var b = _mb[(int)AnchorOf(_popover)];
        double x = Math.Clamp(b.X + b.Width / 2 - pw / 2, S(8), Math.Max(S(8), W - pw - S(8)));
        return new Rect((int)x, BarsH + S(11), pw, ph);
    }

    static Rect PalCell(Rect pr, int top, int i) => new(pr.X + S(8) + i % 6 * Cell, pr.Y + top + i / 6 * Cell, Cell, Cell);

    static void DrawSwatches(DrawingContext dc, Rect pr, int top, uint cur)
    {
        for (int i = 0; i < Palette.Length; i++)
        {
            var c = PalCell(pr, top, i);
            uint col = 0xFF000000u | Palette[i];
            if ((cur >> 24) != 0 && (cur & 0xFFFFFF) == Palette[i]) Nd.StrokeRRect(dc, c.X + SF(1), c.Y + SF(1), c.Width - SF(2), c.Height - SF(2), SF(5), SF(2), Nd.Accent);
            Nd.FillRRect(dc, c.X + SF(4), c.Y + SF(4), c.Width - SF(8), c.Height - SF(8), SF(3), col);
            Nd.StrokeRRect(dc, c.X + SF(4), c.Y + SF(4), c.Width - SF(8), c.Height - SF(8), SF(3), 1, 0x33000000u);
        }
    }

    void DrawPopover(DrawingContext dc, int W)
    {
        if (_popover == Popover.None || !_markup) return;
        var pr = PopoverRect(W);
        _poR = pr;
        var b = _mb[(int)AnchorOf(_popover)];
        uint bg = Nd.Dark ? 0xFF323232u : 0xFFF6F6F6u, edge = Nd.Dark ? 0x80000000u : 0x33000000u;
        // arrow
        double ax = b.X + b.Width / 2, ay = pr.Y;
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(ax - SF(9), ay + SF(0.5)), true, true);
            g.LineTo(new Point(ax, ay - SF(8)), true, true);
            g.LineTo(new Point(ax + SF(9), ay + SF(0.5)), true, true);
        }
        Nd.FillRRect(dc, pr.X - 1, pr.Y - 1, pr.Width + 2, pr.Height + 2, SF(9), edge);
        dc.PushTransform(new TranslateTransform(0, -1));
        dc.DrawGeometry(Nd.Br(edge), null, arrow);
        dc.Pop();
        Nd.FillRRect(dc, pr.X, pr.Y, pr.Width, pr.Height, SF(8), bg);
        dc.DrawGeometry(Nd.Br(bg), null, arrow);
        var a = SelAnnot;
        switch (_popover)
        {
            case Popover.Shapes:
                for (int i = 0; i < ShapeKinds.Length; i++)
                {
                    double cx = pr.X + SF(8) + i % 4 * SF(44), cy = pr.Y + SF(8) + i / 4 * SF(44);
                    var sh = new Annot { Kind = ShapeKinds[i], Stroke = Nd.Label, Width = SF(1.6) };
                    sh.X0 = cx + SF(10); sh.Y0 = cy + SF(10); sh.X1 = cx + SF(34); sh.Y1 = cy + SF(34);
                    if (sh.Kind is AnKind.Line or AnKind.Arrow) { sh.Y0 = cy + SF(32); sh.Y1 = cy + SF(12); sh.X0 = cx + SF(10); sh.X1 = cx + SF(33); }
                    if (sh.Kind == AnKind.Bubble) sh.Y0 = cy + SF(12);
                    sh.Draw(dc, 1, 0, 0);
                }
                break;
            case Popover.Style:
                {
                    double cur = a != null ? a.Width / Unit : StyleWidth;
                    for (int i = 0; i < Widths.Length; i++)
                    {
                        double yy = pr.Y + SF(6) + i * SF(24);
                        if (Math.Abs(cur - Widths[i]) < 0.01) Nd.Symbol(dc, "checkmark", pr.X + SF(8), yy + SF(6), SF(12), 2.2, Nd.Label);
                        double t = Math.Max(1.0, SF(Widths[i]));
                        Nd.FillRect(dc, new Rect(pr.X + S(28), Math.Round(yy + SF(12) - t / 2), pr.Width - S(40), Math.Round(t)), Nd.Label);
                    }
                    break;
                }
            case Popover.Border:
                DrawSwatches(dc, pr, S(8), a?.Stroke ?? StyleStroke);
                break;
            case Popover.Fill:
                {
                    uint cur = a?.Fill ?? StyleFill;
                    var nf = new Rect(pr.X + S(8), pr.Y + S(8), pr.Width - S(16), S(22));
                    if ((cur >> 24) == 0) Nd.FillRRect(dc, nf.X, nf.Y, nf.Width, nf.Height, SF(5), HoverBg);
                    Nd.TextCentered(dc, "No Fill", Nd.Weight.Regular, SF(12), nf, Nd.Label);
                    DrawSwatches(dc, pr, S(36), cur);
                    break;
                }
            case Popover.Text:
                {
                    double cur = a != null && a.Kind == AnKind.Text ? a.Font / Unit : StyleFont;
                    for (int i = 0; i < Sizes.Length; i++)
                    {
                        var c = SizeCell(pr, i);
                        bool on = Math.Abs(cur - Sizes[i]) < 0.5;
                        if (on) Nd.FillRRect(dc, c.X + SF(2), c.Y, c.Width - SF(4), c.Height, SF(5), Nd.Accent);
                        Nd.TextCentered(dc, ((int)Sizes[i]).ToString(), Nd.Weight.Regular, SF(12), c, on ? 0xFFFFFFFFu : Nd.Label);
                    }
                    DrawSwatches(dc, pr, S(2 * 26 + 18), a != null && a.Kind == AnKind.Text ? a.TextColor : StyleText);
                    break;
                }
        }
    }

    static Rect SizeCell(Rect pr, int i) =>
        new(pr.X + S(8) + i % 5 * ((pr.Width - S(16)) / 5), pr.Y + S(8) + i / 5 * S(26), (int)((pr.Width - S(16)) / 5), S(24));

    enum St { Stroke, Fill, Width, Font, Text }

    void ApplyStyle(St what, uint c, double v)
    {
        double u = Unit;
        switch (what)
        {
            case St.Stroke: StyleStroke = c; break;
            case St.Fill: StyleFill = c; break;
            case St.Width: StyleWidth = v; break;
            case St.Font: StyleFont = v; break;
            case St.Text: StyleText = c; break;
        }
        if (SelAnnot == null) { Redraw(); return; }
        PushUndo();
        var a = SelAnnot;
        switch (what)
        {
            case St.Stroke:
                a.Stroke = c;
                if (a.Kind == AnKind.Text && a.Width <= 0) a.Width = u;
                break;
            case St.Fill:
                if (a.Kind != AnKind.Line && a.Kind != AnKind.Arrow && a.Kind != AnKind.Path) a.Fill = c;
                break;
            case St.Width: a.Width = v * u; break;
            case St.Font: a.Font = v * u; break;
            case St.Text: a.TextColor = c; break;
        }
        if (a.Kind == AnKind.Text && what == St.Font)
        {
            // keep the box fitted to the text
            double pad = a.Font * 0.25;
            a.X1 = Math.Max(a.X1, a.X0 + a.Font * 4);
            double h = Nd.MeasureWrapped(a.Text.Length > 0 ? a.Text : " ", Nd.Weight.Regular, a.Font, Math.Max(1.0, a.X1 - a.X0 - 2 * pad), a.Font * 1.25);
            a.Y1 = a.Y0 + h + 2 * pad;
        }
        Changed();
    }

    static int SwatchAt(Rect pr, int top, Point p)
    {
        for (int i = 0; i < Palette.Length; i++)
            if (PalCell(pr, top, i).Contains(p)) return i;
        return -1;
    }

    void PopoverMouse(double x, double y, int clicks)
    {
        var pr = _poR;
        var po = _popover;
        var p = new Point(x, y);
        if (!pr.Contains(p))
        {
            _popover = Popover.None;
            // a click on another popover button opens that one straight away
            if (y >= S(ToolbarH) && y < BarsH && !_mb[(int)AnchorOf(po)].Contains(p)) BarsMouse(x, y, clicks);
            Redraw();
            return;
        }
        switch (po)
        {
            case Popover.Shapes:
                for (int i = 0; i < ShapeKinds.Length; i++)
                {
                    var c = new Rect(pr.X + S(8) + i % 4 * S(44), pr.Y + S(8) + i / 4 * S(44), S(44), S(44));
                    if (c.Contains(p)) { _popover = Popover.None; InsertShape(ShapeKinds[i]); }
                }
                break;
            case Popover.Style:
                {
                    int i = (int)((y - pr.Y - S(6)) / S(24));
                    if (i >= 0 && i < Widths.Length) { _popover = Popover.None; ApplyStyle(St.Width, 0, Widths[i]); }
                    break;
                }
            case Popover.Border:
                {
                    int i = SwatchAt(pr, S(8), p);
                    if (i >= 0) ApplyStyle(St.Stroke, 0xFF000000u | Palette[i], 0);
                    break;
                }
            case Popover.Fill:
                {
                    if (new Rect(pr.X + S(8), pr.Y + S(8), pr.Width - S(16), S(22)).Contains(p)) ApplyStyle(St.Fill, 0, 0);
                    int i = SwatchAt(pr, S(36), p);
                    if (i >= 0) ApplyStyle(St.Fill, 0xFF000000u | Palette[i], 0);
                    break;
                }
            case Popover.Text:
                {
                    for (int i = 0; i < Sizes.Length; i++)
                        if (SizeCell(pr, i).Contains(p)) ApplyStyle(St.Font, 0, Sizes[i]);
                    int j = SwatchAt(pr, S(2 * 26 + 18), p);
                    if (j >= 0) ApplyStyle(St.Text, 0xFF000000u | Palette[j], 0);
                    break;
                }
        }
        Redraw();
    }

    // ------------------------------------------------------------------ Share

    void ShareMenu(Rect anchor)
    {
        PopupMenu(anchor, new object[]
        {
            MacShell.Controls.Mb.Item("Export…", () => { if (_loaded) BeginExport(); }),
            MacShell.Controls.Mb.Item("Save a Copy to Desktop", SaveCopyToDesktop),
            MacShell.Controls.Mb.Sep(),
            MacShell.Controls.Mb.Item("Show in Finder", () => ShellHost.RevealInFinder(_path), enabled: !_untitled),
        });
    }

    void SaveCopyToDesktop()
    {
        if (!_loaded) return;
        // Export straight to the Desktop in the picture's own format
        string x = Path.GetExtension(_name).ToLowerInvariant();
        _exFmt = x is ".jpg" or ".jpeg" ? Fmt.Jpeg : Fmt.Png;
        _exWhere = 1;
        _tfA.Text = _name;
        ExportFinish();
    }

    // ------------------------------------------------------------------ sheets

    readonly Canvas _sheetLayer = new() { IsHitTestVisible = true, Visibility = Visibility.Collapsed };
    TextBox _tfA, _tfB;

    TextBox MakeField()
    {
        var t = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = Nd.Family,
            FontSize = 13,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            Visibility = Visibility.Collapsed,
        };
        t.Template = BareTextBoxTemplate();
        t.SetResourceReference(TextBox.ForegroundProperty, "LabelBrush");
        t.CaretBrush = Nd.Br(Nd.Accent);
        t.SelectionBrush = Nd.Br(Nd.Accent);
        t.SelectionOpacity = Nd.Dark ? 0.55 : 0.3;
        t.GotKeyboardFocus += (_, _) => { _tfFocus = t == _tfA ? 0 : 1; Redraw(); };
        t.TextChanged += (_, _) => { if (_sheet == Sheet.Size && t.IsKeyboardFocused && !_linking) LinkSizes(); Redraw(); };
        _sheetLayer.Children.Add(t);
        return t;
    }

    static ControlTemplate BareTextBoxTemplate()
    {
        var tpl = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        sv.SetValue(FocusableProperty, false);
        sv.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        tpl.VisualTree = sv;
        return tpl;
    }

    void EnsureFields()
    {
        _tfA ??= MakeField();
        _tfB ??= MakeField();
    }

    void HideSheetFields()
    {
        _sheetLayer.Visibility = Visibility.Collapsed;
        if (_tfA != null) _tfA.Visibility = Visibility.Collapsed;
        if (_tfB != null) _tfB.Visibility = Visibility.Collapsed;
        _canvas.Focus();
    }

    static double ParseNum(string s)
    {
        double v = 0, frac = 0;
        bool dot = false, any = false;
        foreach (char ch in s ?? "")
        {
            if (ch >= '0' && ch <= '9')
            {
                any = true;
                if (dot) { frac /= 10; v += (ch - '0') * frac; }
                else v = v * 10 + (ch - '0');
            }
            else if ((ch == '.' || ch == ',') && !dot) { dot = true; frac = 1; }
            else if (ch != ' ') return -1;
        }
        return any ? v : -1;
    }

    static string Num(double v, bool decimals)
    {
        if (decimals && Math.Abs(v - Math.Round(v)) > 0.001)
        {
            int iv = (int)(v * 10 + 0.5);
            return $"{iv / 10}.{iv % 10}";
        }
        return ((int)(v + 0.5)).ToString();
    }

    void SizeResult(out int nw, out int nh)
    {
        double a = ParseNum(_tfA.Text), b = ParseNum(_tfB.Text);
        int iw = _st.Img.W, ih = _st.Img.H;
        if (_szPercent) { nw = (int)Math.Round(iw * a / 100); nh = (int)Math.Round(ih * b / 100); }
        else { nw = (int)Math.Round(a); nh = (int)Math.Round(b); }
        if (a <= 0) nw = 0;
        if (b <= 0) nh = 0;
    }

    public void BeginSize()
    {
        if (!_loaded) return;
        EnsureFields();
        _popover = Popover.None;
        _sheet = Sheet.Size;
        _tfFocus = 0;
        _linking = true;
        if (_szPercent) { _tfA.Text = "100"; _tfB.Text = "100"; }
        else { _tfA.Text = Num(_st.Img.W, false); _tfB.Text = Num(_st.Img.H, false); }
        _linking = false;
        ShowSheetFields();
        _tfA.Focus();
        _tfA.SelectAll();
    }

    bool CanSameFolder => !_untitled && !_readonly && _path != null;

    public void BeginExport()
    {
        if (!_loaded) return;
        EnsureFields();
        _popover = Popover.None;
        _sheet = Sheet.Export;
        _tfFocus = 0;
        string x = Path.GetExtension(_name).ToLowerInvariant();
        _exFmt = x is ".jpg" or ".jpeg" ? Fmt.Jpeg : Fmt.Png;
        _exWhere = CanSameFolder ? 4 : 0;
        string name = _untitled ? "Untitled" : _name;
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        int stem = name.Length;
        _tfA.Text = name + (_exFmt == Fmt.Jpeg ? ".jpeg" : ".png");
        ShowSheetFields();
        _tfA.Focus();
        _tfA.Select(0, stem);
    }

    void SetFormat(Fmt fmt)
    {
        _exFmt = fmt;
        string name = _tfA.Text;
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        _tfA.Text = name + (fmt == Fmt.Jpeg ? ".jpeg" : ".png");
        PositionSheetFields();
        Redraw();
    }

    string WhereDir(int where)
    {
        if (where == 4 && CanSameFolder) return Path.GetDirectoryName(_path);
        return Math.Clamp(where, 0, 3) switch
        {
            0 => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            1 => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            2 => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _ => GetKnownFolder(FOLDERID_Downloads),
        };
    }

    // sheet geometry (device pixels)
    Rect SheetRect(int W)
    {
        int sw = _sheet == Sheet.Size ? S(400) : S(440);
        int sh = _sheet == Sheet.Size ? S(214) : _exFmt == Fmt.Jpeg ? S(236) : S(186);
        sw = Math.Min(sw, W - S(20));
        return new Rect((W - sw) / 2, S(ToolbarH), sw, sh);
    }

    struct SheetGeo { public Rect A, B, Units, Prop, Cancel, Ok, Where, Fmt, Slider; }

    SheetGeo Geo(int W)
    {
        var r = SheetRect(W);
        var g = new SheetGeo
        {
            Cancel = new Rect(r.X + r.Width - S(196), r.Y + r.Height - S(40), S(84), S(24)),
            Ok = new Rect(r.X + r.Width - S(104), r.Y + r.Height - S(40), S(84), S(24)),
        };
        if (_sheet == Sheet.Size)
        {
            g.A = new Rect(r.X + S(118), r.Y + S(20), S(90), S(22));
            g.B = new Rect(r.X + S(118), r.Y + S(52), S(90), S(22));
            g.Units = new Rect(r.X + S(226), r.Y + S(36), S(110), S(22));
            g.Prop = new Rect(r.X + S(118), r.Y + S(88), S(200), S(18));
        }
        else
        {
            g.A = new Rect(r.X + S(110), r.Y + S(20), r.Width - S(130), S(22));
            g.Where = new Rect(r.X + S(110), r.Y + S(54), S(190), S(22));
            g.Fmt = new Rect(r.X + S(110), r.Y + S(88), S(120), S(22));
            g.Slider = new Rect(r.X + S(110), r.Y + S(122), S(220), S(22));
        }
        return g;
    }

    void ShowSheetFields()
    {
        _sheetLayer.Visibility = Visibility.Visible;
        _tfA.Visibility = Visibility.Visible;
        _tfB.Visibility = _sheet == Sheet.Size ? Visibility.Visible : Visibility.Collapsed;
        PositionSheetFields();
        Redraw();
    }

    void PositionSheetFields()
    {
        if (_sheet == Sheet.None || _tfA == null) return;
        var g = Geo(PxW);
        void Place(TextBox t, Rect r)
        {
            double pad = SF(7);
            Canvas.SetLeft(t, (r.X + pad) / Nd.Scale);
            Canvas.SetTop(t, (r.Y + 1) / Nd.Scale);
            t.Width = Math.Max(1, (r.Width - 2 * pad) / Nd.Scale);
            t.Height = Math.Max(1, (r.Height - 2) / Nd.Scale);
        }
        Place(_tfA, g.A);
        if (_sheet == Sheet.Size) Place(_tfB, g.B);
    }

    static void LabelR(DrawingContext dc, double right, double baseline, string t)
    {
        double w = Nd.TextWidth(t, Nd.Weight.Regular, SF(13));
        Nd.Text(dc, t, Nd.Weight.Regular, SF(13), right - w, baseline, Nd.Label);
    }

    void DrawSheet(DrawingContext dc, int W, int H)
    {
        if (_sheet == Sheet.None) return;
        PositionSheetFields();
        var r = SheetRect(W);
        var g = Geo(W);
        // the shadow and the sheet hanging from the toolbar
        var shadow = new System.Windows.Media.Effects.DropShadowEffect();
        for (int i = 6; i >= 1; i--)
            Nd.FillRRect(dc, r.X - SF(2 * i), r.Y - SF(10) + SF(4) - SF(i), r.Width + SF(4 * i), r.Height + SF(10) + SF(2 * i), SF(10 + 2 * i), (uint)(0x0E - i) << 24);
        Nd.FillRRect(dc, r.X, r.Y - SF(10), r.Width, r.Height + SF(10), SF(10), Nd.WindowBg);
        Nd.StrokeRRect(dc, r.X + 0.5, r.Y - SF(10), r.Width - 1, r.Height + SF(9.5), SF(10), 1, Nd.Separator);
        if (_sheet == Sheet.Size)
        {
            LabelR(dc, g.A.X - SF(8), g.A.Y + SF(16), "Width:");
            LabelR(dc, g.B.X - SF(8), g.B.Y + SF(16), "Height:");
            Nd.TextFieldFrame(dc, g.A, _tfA.IsKeyboardFocused);
            Nd.TextFieldFrame(dc, g.B, _tfB.IsKeyboardFocused);
            // the bracket linking width and height
            uint lc = _szProp ? Nd.SecondaryLabel : Nd.QuaternaryLabel;
            double bx = g.A.Right + SF(6);
            var pen = new Pen(Nd.Br(lc), 1);
            dc.DrawLine(pen, new Point(bx, g.A.Y + SF(11)), new Point(bx + SF(6), g.A.Y + SF(11)));
            dc.DrawLine(pen, new Point(bx + SF(6), g.A.Y + SF(11)), new Point(bx + SF(6), g.B.Y + SF(11)));
            dc.DrawLine(pen, new Point(bx, g.B.Y + SF(11)), new Point(bx + SF(6), g.B.Y + SF(11)));
            Nd.PopupButton(dc, g.Units, _szPercent ? "percent" : "pixels");
            Nd.Checkbox(dc, g.Prop.X, g.Prop.Y + S(2), _szProp);
            Nd.Text(dc, "Scale proportionally", Nd.Weight.Regular, SF(13), g.Prop.X + SF(22), g.Prop.Y + SF(14), Nd.Label);
            SizeResult(out int nw, out int nh);
            string b = nw > 0 && nh > 0 ? $"{nw} × {nh} pixels ({FileItem.FormatSize((long)Math.Max(nw, 0) * Math.Max(nh, 0) * 4)})" : "—";
            LabelR(dc, g.A.X - SF(8), r.Y + SF(136), "Resulting Size:");
            Nd.Text(dc, b, Nd.Weight.Regular, SF(13), g.A.X, r.Y + SF(136), Nd.SecondaryLabel);
            Nd.Button(dc, g.Cancel, "Cancel", false, false, true);
            Nd.Button(dc, g.Ok, "OK", true, false, nw > 0 && nh > 0);
        }
        else
        {
            LabelR(dc, g.A.X - SF(8), g.A.Y + SF(16), "Export As:");
            Nd.TextFieldFrame(dc, g.A, true);
            LabelR(dc, g.Where.X - SF(8), g.Where.Y + SF(16), "Where:");
            string wn = _exWhere == 4 ? (Path.GetFileName(Path.GetDirectoryName(_path) ?? "") is { Length: > 0 } f ? f : Path.GetDirectoryName(_path)) : WhereNames[Math.Clamp(_exWhere, 0, 3)];
            Nd.PopupButton(dc, g.Where, wn);
            LabelR(dc, g.Fmt.X - SF(8), g.Fmt.Y + SF(16), "Format:");
            Nd.PopupButton(dc, g.Fmt, _exFmt == Fmt.Jpeg ? "JPEG" : "PNG");
            if (_exFmt == Fmt.Jpeg)
            {
                LabelR(dc, g.Slider.X - SF(8), g.Slider.Y + SF(15), "Quality:");
                Nd.Slider(dc, g.Slider.X, g.Slider.Y + g.Slider.Height / 2, g.Slider.Width, _exQuality, false, true);
                Nd.Text(dc, "Least", Nd.Weight.Regular, SF(10), g.Slider.X, g.Slider.Y + SF(36), Nd.SecondaryLabel);
                double bw = Nd.TextWidth("Best", Nd.Weight.Regular, SF(10));
                Nd.Text(dc, "Best", Nd.Weight.Regular, SF(10), g.Slider.Right - bw, g.Slider.Y + SF(36), Nd.SecondaryLabel);
            }
            Nd.Button(dc, g.Cancel, "Cancel", false, false, true);
            Nd.Button(dc, g.Ok, "Save", true, false, _tfA.Text.Length > 0);
        }
    }

    bool _linking;

    void LinkSizes()
    {
        if (!_szProp) return;
        var src = _tfFocus == 0 ? _tfA : _tfB;
        var dst = _tfFocus == 0 ? _tfB : _tfA;
        double v = ParseNum(src.Text);
        if (v <= 0) return;
        _linking = true;
        if (_szPercent) dst.Text = Num(v, true);
        else
        {
            double iw = _st.Img.W, ih = _st.Img.H;
            dst.Text = Num(_tfFocus == 0 ? v * ih / iw : v * iw / ih, false);
        }
        _linking = false;
    }

    void SheetOk()
    {
        if (_sheet == Sheet.Size)
        {
            SizeResult(out int nw, out int nh);
            if (nw <= 0 || nh <= 0) return;
            _sheet = Sheet.None;
            HideSheetFields();
            Resize(nw, nh);
        }
        else
        {
            string t = _tfA.Text;
            if (t.Length == 0 || t.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            ExportFinish();
        }
        Redraw();
    }

    void SheetCancel()
    {
        _sheet = Sheet.None;
        _closeAfterSave = false;
        HideSheetFields();
        Redraw();
    }

    void SheetKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: SheetOk(); e.Handled = true; break;
            case Key.Escape: SheetCancel(); e.Handled = true; break;
            case Key.Tab:
                if (_sheet == Sheet.Size)
                {
                    var n = _tfFocus == 0 ? _tfB : _tfA;
                    n.Focus();
                    n.SelectAll();
                }
                e.Handled = true;
                break;
        }
    }

    void SheetMouse(double x, double y, bool down, bool up)
    {
        var g = Geo(PxW);
        var p = new Point(x, y);
        if (down)
        {
            _sliderDrag = false;
            if (g.Cancel.Contains(p)) SheetCancel();
            else if (g.Ok.Contains(p)) SheetOk();
            else if (_sheet == Sheet.Size && g.Prop.Contains(p))
            {
                _szProp = !_szProp;
                if (_szProp) LinkSizes();
            }
            else if (_sheet == Sheet.Size && g.Units.Contains(p))
            {
                PopupMenu(g.Units, new object[]
                {
                    MacShell.Controls.Mb.Item("pixels", () => SetUnits(false), isChecked: !_szPercent),
                    MacShell.Controls.Mb.Item("percent", () => SetUnits(true), isChecked: _szPercent),
                });
            }
            else if (_sheet == Sheet.Export && g.Where.Contains(p))
            {
                var items = new List<object>();
                if (CanSameFolder)
                {
                    string dir = Path.GetDirectoryName(_path);
                    items.Add(MacShell.Controls.Mb.Item(Path.GetFileName(dir) is { Length: > 0 } f ? f : dir, () => { _exWhere = 4; Redraw(); }, isChecked: _exWhere == 4));
                    items.Add(MacShell.Controls.Mb.Sep());
                }
                for (int i = 0; i < 4; i++)
                {
                    int w = i;
                    items.Add(MacShell.Controls.Mb.Item(WhereNames[i], () => { _exWhere = w; Redraw(); }, isChecked: _exWhere == i));
                }
                PopupMenu(g.Where, items.ToArray());
            }
            else if (_sheet == Sheet.Export && g.Fmt.Contains(p))
            {
                PopupMenu(g.Fmt, new object[]
                {
                    MacShell.Controls.Mb.Item("JPEG", () => SetFormat(Fmt.Jpeg), isChecked: _exFmt == Fmt.Jpeg),
                    MacShell.Controls.Mb.Item("PNG", () => SetFormat(Fmt.Png), isChecked: _exFmt == Fmt.Png),
                });
            }
            else if (_sheet == Sheet.Export && _exFmt == Fmt.Jpeg && new Rect(g.Slider.X - S(8), g.Slider.Y, g.Slider.Width + S(16), g.Slider.Height).Contains(p))
                _sliderDrag = true;
        }
        if (_sliderDrag && !up)
        {
            double k = SF(18);
            _exQuality = Math.Clamp((x - g.Slider.X - k / 2) / (g.Slider.Width - k), 0.05, 1.0);
        }
        if (up) _sliderDrag = false;
        Redraw();
    }

    void SetUnits(bool pct)
    {
        if (pct == _szPercent || !_loaded) return;
        SizeResult(out int nw, out int nh);
        _szPercent = pct;
        _linking = true;
        if (pct)
        {
            _tfA.Text = Num(nw * 100.0 / _st.Img.W, true);
            _tfB.Text = Num(nh * 100.0 / _st.Img.H, true);
        }
        else
        {
            _tfA.Text = Num(nw, false);
            _tfB.Text = Num(nh, false);
        }
        _linking = false;
        Redraw();
    }
}
