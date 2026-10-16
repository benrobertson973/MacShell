using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps.Notes;

/// <summary>
/// A note on screen: <see cref="NoteDoc"/> made into a WPF FlowDocument (paragraph styles, list markers drawn like the
/// Mac's - •, –, 1., the yellow checklist circle - pictures), and read back from one after editing or pasting.
/// </summary>
public static class NoteFlow
{
    public const double IndentStep = 24, MarkerWidth = 22;
    public static readonly Color Yellow = Color.FromRgb(0xF5, 0xC0, 0x1E);

    /// <summary>A paragraph's Notes style (title, heading, subheading, body, mono).</summary>
    public static readonly DependencyProperty StyleProperty = DependencyProperty.RegisterAttached("NoteStyle", typeof(string), typeof(NoteFlow), new PropertyMetadata(null));
    public static string GetStyle(DependencyObject d) => (string)d.GetValue(StyleProperty);

    public static (double size, FontWeight weight, bool mono) StyleLook(string style) => style switch
    {
        "title" => (24, FontWeights.Bold, false),
        "heading" => (18, FontWeights.Bold, false),
        "subheading" => (15, FontWeights.SemiBold, false),
        "mono" => (12.5, FontWeights.Normal, true),
        _ => (13.5, FontWeights.Normal, false),
    };

    public static readonly string[] StyleNames = { "title", "heading", "subheading", "body", "mono" };

    public static void ApplyStyle(Paragraph p, string style)
    {
        var (size, weight, mono) = StyleLook(style);
        p.SetValue(StyleProperty, style);
        p.FontSize = size;
        p.FontWeight = weight;
        p.FontFamily = mono ? Theme.MonoFont : Theme.Font;
        double after = style is "title" ? 6 : style is "heading" or "subheading" ? 4 : 2;
        p.Margin = new Thickness(p.Margin.Left, 0, 0, after);
    }

    // ------------------------------------------------------------------ NoteDoc → FlowDocument

    public static FlowDocument ToFlow(NoteDoc doc, NoteInfo note)
    {
        var fd = new FlowDocument { PagePadding = new Thickness(0), FontFamily = Theme.Font, FontSize = 13.5, TextAlignment = TextAlignment.Left };
        fd.SetResourceReference(FlowDocument.ForegroundProperty, "LabelBrush");
        foreach (var b in doc.Blocks)
        {
            if (b.Image != null)
            {
                string path = note == null ? null : NotesLibrary.ImagePath(note, b.Image);
                fd.Blocks.Add(new BlockUIContainer(new NoteImage(path, b.Image, b.ImageWidth, b.ImageHeight)) { Margin = new Thickness(b.Indent * IndentStep, 4, 0, 6) });
            }
            else fd.Blocks.Add(MakeParagraph(b));
        }
        if (fd.Blocks.Count == 0) fd.Blocks.Add(MakeParagraph(new NoteBlock { Style = "title" }));
        Renumber(fd);
        return fd;
    }

    public static Paragraph MakeParagraph(NoteBlock b)
    {
        var p = new Paragraph();
        ApplyStyle(p, b.Style ?? "body");
        foreach (var r in b.Runs) foreach (var i in MakeInlines(r)) p.Inlines.Add(i);
        SetList(p, b.List, b.Indent, b.Checked);
        if (b.Quote) SetQuote(p, true);
        return p;
    }

    static IEnumerable<Inline> MakeInlines(NoteRun r)
    {
        var parts = (r.T ?? "").Split('\n');
        for (int k = 0; k < parts.Length; k++)
        {
            if (k > 0) yield return new LineBreak();
            if (parts[k].Length == 0) continue;
            var run = new Run(parts[k]);
            if (r.B) run.FontWeight = FontWeights.Bold;
            if (r.I) run.FontStyle = FontStyles.Italic;
            if (r.U || r.S)
            {
                var dec = new TextDecorationCollection();
                if (r.U) dec.Add(TextDecorations.Underline);
                if (r.S) dec.Add(TextDecorations.Strikethrough);
                run.TextDecorations = dec;
            }
            if (r.Mono) run.FontFamily = Theme.MonoFont;
            if (!string.IsNullOrEmpty(r.Link))
            {
                var h = new Hyperlink(run) { Cursor = Cursors.Hand };
                if (Uri.TryCreate(r.Link, UriKind.Absolute, out var u)) h.NavigateUri = u;
                h.Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x84, 0x00));
                h.Tag = r.Link;
                yield return h;
            }
            else yield return run;
        }
    }

    // ------------------------------------------------------------------ lists and their markers

    /// <summary>The list marker at the start of a paragraph (a check circle, •, – or a number), or null.</summary>
    public static FrameworkElement Marker(Paragraph p) =>
        p.Inlines.FirstInline is InlineUIContainer c && c.Child is FrameworkElement fe && fe.Tag is string t && t.StartsWith("marker:") ? fe : null;

    public static string ListOf(Paragraph p) => Marker(p)?.Tag is string t ? t[7..] : null;

    public static int IndentOf(Paragraph p)
    {
        double left = p.Margin.Left - (Marker(p) != null ? MarkerWidth : 0) - (p.BorderThickness.Left > 0 ? 0 : 0);
        return Math.Max(0, (int)Math.Round(left / IndentStep));
    }

    public static bool IsChecked(Paragraph p) => Marker(p) is CheckMarker cm && cm.IsChecked;

    /// <summary>Makes a paragraph a list item of a kind (null: no list) at an indent.</summary>
    public static void SetList(Paragraph p, string kind, int indent, bool isChecked = false)
    {
        if (Marker(p) != null) p.Inlines.Remove(p.Inlines.FirstInline);
        indent = Math.Clamp(indent, 0, 8);
        if (string.IsNullOrEmpty(kind))
        {
            p.Margin = new Thickness(indent * IndentStep, 0, 0, p.Margin.Bottom);
            p.TextIndent = 0;
            return;
        }
        FrameworkElement m = kind == "check" ? new CheckMarker { IsChecked = isChecked } : new TextMarker(kind);
        m.Tag = "marker:" + kind;
        var c = new InlineUIContainer(m) { BaselineAlignment = BaselineAlignment.Center };
        if (p.Inlines.FirstInline != null) p.Inlines.InsertBefore(p.Inlines.FirstInline, c); else p.Inlines.Add(c);
        p.Margin = new Thickness(indent * IndentStep + MarkerWidth, 0, 0, p.Margin.Bottom);
        p.TextIndent = -MarkerWidth;
    }

    public static bool IsQuote(Paragraph p) => p.BorderThickness.Left > 0;

    public static void SetQuote(Paragraph p, bool on)
    {
        if (on)
        {
            p.BorderThickness = new Thickness(3, 0, 0, 0);
            p.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));
            p.Padding = new Thickness(10, 1, 0, 1);
        }
        else { p.BorderThickness = new Thickness(0); p.Padding = new Thickness(0); }
    }

    /// <summary>Numbered lists counted again (1., 2., 3. … - each indent level on its own, starting over after any other paragraph).</summary>
    public static void Renumber(FlowDocument fd)
    {
        var counters = new int[10];
        foreach (var b in fd.Blocks)
        {
            if (b is not Paragraph p) { Array.Clear(counters); continue; }
            var m = Marker(p) as TextMarker;
            string kind = ListOf(p);
            int level = IndentOf(p);
            if (kind == null) { Array.Clear(counters); continue; }
            for (int i = level + 1; i < counters.Length; i++) counters[i] = 0;
            if (kind != "number") { counters[level] = 0; continue; }
            counters[level]++;
            m?.SetNumber(counters[level]);
        }
    }

    /// <summary>The paragraph a position is in (and the list item paragraphs of pasted lists).</summary>
    public static Paragraph ParagraphAt(TextPointer tp) => tp?.Paragraph;

    // ------------------------------------------------------------------ FlowDocument → NoteDoc

    /// <summary>A FlowDocument read back as a note: what was typed, styled and pasted, tidied into the note's own terms.
    /// storeImage keeps a picture pasted in (one that isn't a note picture yet).</summary>
    public static NoteDoc FromFlow(FlowDocument fd, Func<BitmapSource, (string file, int w, int h)?> storeImage = null)
    {
        var d = new NoteDoc();
        foreach (var b in fd.Blocks) AddBlock(d, b, 0, null, storeImage);
        if (d.Blocks.Count == 0) d.Blocks.Add(new NoteBlock { Style = "title" });
        return d;
    }

    static void AddBlock(NoteDoc d, Block b, int depth, string listKind, Func<BitmapSource, (string, int, int)?> store)
    {
        switch (b)
        {
            case Paragraph p:
                {
                    // pictures pasted inline become blocks of their own
                    var nb = FromParagraph(p, depth, listKind, out var images);
                    d.Blocks.Add(nb);
                    foreach (var img in images) AddImage(d, img, store);
                    break;
                }
            case BlockUIContainer bu:
                AddImage(d, bu.Child, store);
                break;
            case List l:
                {
                    string kind = l.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman ? "number" : "bullet";
                    foreach (var li in l.ListItems)
                        foreach (var inner in li.Blocks)
                            AddBlock(d, inner, inner is List ? depth + 1 : depth, kind, store);
                    break;
                }
            case Section s:
                foreach (var inner in s.Blocks) AddBlock(d, inner, depth, listKind, store);
                break;
            case Table t:
                foreach (var g in t.RowGroups)
                    foreach (var row in g.Rows)
                    {
                        var text = string.Join("\t", row.Cells.Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim()));
                        d.Blocks.Add(new NoteBlock { Runs = { new NoteRun { T = text } } });
                    }
                break;
        }
    }

    static void AddImage(NoteDoc d, UIElement child, Func<BitmapSource, (string, int, int)?> store)
    {
        if (child is NoteImage ni && ni.File != null)
            d.Blocks.Add(new NoteBlock { Image = ni.File, ImageWidth = ni.PixelWidth, ImageHeight = ni.PixelHeight });
        else if (child is Image { Source: BitmapSource bs } && store != null && store(bs) is var (file, w, h))
            d.Blocks.Add(new NoteBlock { Image = file, ImageWidth = w, ImageHeight = h });
    }

    public static string StyleOf(Paragraph p)
    {
        if (GetStyle(p) is string s && StyleNames.Contains(s)) return s;
        if (p.FontFamily?.Source is string f && IsMono(f)) return "mono";
        double size = p.FontSize;
        if (size >= 21) return "title";
        if (size >= 16.5) return "heading";
        if (size >= 14.6 && p.FontWeight >= FontWeights.SemiBold) return "subheading";
        return "body";
    }

    static bool IsMono(string family) => family.Contains("Mono", StringComparison.OrdinalIgnoreCase) || family.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                                          || family.Contains("Menlo", StringComparison.OrdinalIgnoreCase) || family.Contains("Courier", StringComparison.OrdinalIgnoreCase);

    static NoteBlock FromParagraph(Paragraph p, int depth, string listKind, out List<UIElement> images)
    {
        images = new List<UIElement>();
        var nb = new NoteBlock { Style = StyleOf(p) };
        string own = ListOf(p);
        if (own != null) { nb.List = own; nb.Checked = IsChecked(p); nb.Indent = IndentOf(p); }
        else if (listKind != null) { nb.List = listKind; nb.Indent = depth; }
        else nb.Indent = Math.Max(0, (int)Math.Round(p.Margin.Left / IndentStep));
        nb.Quote = IsQuote(p);
        var (_, styleWeight, styleMono) = StyleLook(nb.Style);
        foreach (var inline in p.Inlines) Walk(inline, nb, styleWeight, styleMono, images);
        // runs of the same look joined
        var merged = new List<NoteRun>();
        foreach (var r in nb.Runs)
        {
            if (r.T.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].SameLook(r)) merged[^1].T += r.T; else merged.Add(r);
        }
        nb.Runs = merged;
        return nb;
    }

    static void Walk(Inline inline, NoteBlock nb, FontWeight styleWeight, bool styleMono, List<UIElement> images)
    {
        switch (inline)
        {
            case InlineUIContainer c:
                if (c.Child is FrameworkElement fe && fe.Tag is string t && t.StartsWith("marker:")) return;
                if (c.Child is NoteImage or Image) images.Add(c.Child);
                return;
            case LineBreak:
                nb.Runs.Add(new NoteRun { T = "\n" });
                return;
            case Run r:
                {
                    if (string.IsNullOrEmpty(r.Text)) return;
                    var nr = new NoteRun { T = r.Text.Replace("\r\n", "\n").Replace('\r', '\n') };
                    nr.B = r.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight() && r.FontWeight.ToOpenTypeWeight() > styleWeight.ToOpenTypeWeight();
                    nr.I = r.FontStyle == FontStyles.Italic || r.FontStyle == FontStyles.Oblique;
                    for (DependencyObject e = r; e is Inline il; e = il.Parent)
                    {
                        // (a link's own underline is how links look, not underlined text)
                        if (il.TextDecorations != null && il is not Hyperlink)
                            foreach (var dec in il.TextDecorations)
                            {
                                if (dec.Location == TextDecorationLocation.Underline) nr.U = true;
                                if (dec.Location == TextDecorationLocation.Strikethrough) nr.S = true;
                            }
                        if (il is Hyperlink h) nr.Link = h.Tag as string ?? h.NavigateUri?.ToString();
                    }
                    nr.Mono = !styleMono && r.FontFamily?.Source is string f && IsMono(f);
                    nb.Runs.Add(nr);
                    return;
                }
            case Span s:
                foreach (var child in s.Inlines) Walk(child, nb, styleWeight, styleMono, images);
                return;
        }
    }
}

/// <summary>A picture in a note: as wide as it is (in screen pixels) up to the note's width, with rounded corners.</summary>
public sealed class NoteImage : FrameworkElement
{
    public string File { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    BitmapSource _bmp;
    readonly string _path;

    public NoteImage(string path, string file, int w, int h)
    {
        _path = path;
        File = file;
        PixelWidth = Math.Max(1, w);
        PixelHeight = Math.Max(1, h);
        Cursor = Cursors.Arrow;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => { if (_bmp == null) Load(); };
    }

    void Load()
    {
        if (_path == null || !System.IO.File.Exists(_path)) return;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(_path);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (PixelWidth > 1600) bi.DecodePixelWidth = 1600;
            bi.EndInit();
            bi.Freeze();
            _bmp = bi;
            InvalidateVisual();
        }
        catch { }
    }

    protected override Size MeasureOverride(Size available)
    {
        double scale = 1;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        double w = PixelWidth / Math.Max(1, scale);
        if (!double.IsInfinity(available.Width) && available.Width > 0) w = Math.Min(w, available.Width);
        w = Math.Max(16, w);
        return new Size(w, w * PixelHeight / PixelWidth);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.PushClip(new RectangleGeometry(r, 6, 6));
        if (_bmp != null) dc.DrawImage(_bmp, r);
        else dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)), null, r);
        dc.Pop();
    }
}

/// <summary>A checklist item's circle: empty, or yellow with a white check. Clicking it checks it.</summary>
public sealed class CheckMarker : FrameworkElement
{
    bool _checked;
    public bool IsChecked { get => _checked; set { _checked = value; InvalidateVisual(); } }
    public static event Action<CheckMarker> Toggled;

    public CheckMarker()
    {
        Width = NoteFlow.MarkerWidth;
        Height = 18;
        Cursor = Cursors.Hand;
        MouseLeftButtonDown += (_, e) => { e.Handled = true; IsChecked = !IsChecked; Toggled?.Invoke(this); };
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var c = new Point(8.5, ActualHeight / 2);
        if (_checked)
        {
            dc.DrawEllipse(new SolidColorBrush(NoteFlow.Yellow), null, c, 7.5, 7.5);
            var pen = new Pen(Brushes.White, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X - 3.4, c.Y + 0.2), false, false);
                ctx.LineTo(new Point(c.X - 0.9, c.Y + 2.7), true, true);
                ctx.LineTo(new Point(c.X + 3.6, c.Y - 2.6), true, true);
            }
            dc.DrawGeometry(null, pen, g);
        }
        else dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80)), 1.2), c, 7.2, 7.2);
    }
}

/// <summary>A bullet, a dash or a number at the start of a list item.</summary>
public sealed class TextMarker : TextBlock
{
    public TextMarker(string kind)
    {
        Width = NoteFlow.MarkerWidth;
        TextAlignment = kind == "number" ? TextAlignment.Right : TextAlignment.Center;
        Padding = kind == "number" ? new Thickness(0, 0, 5, 0) : new Thickness(0, 0, 4, 0);
        Text = kind switch { "bullet" => "•", "dash" => "–", _ => "1." };
        IsHitTestVisible = false;
    }

    public void SetNumber(int n) { string t = n + "."; if (Text != t) Text = t; }
}
