using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacShell.Services;

namespace MacShell.Apps.Notes;

/// <summary>
/// The note being written: a rich text editor that types like Notes on a Mac - Return continues a list (and ends it on
/// an empty item), Delete at an item's start takes its marker away, Tab and Shift-Tab indent, a title or heading is
/// followed by body text; "- ", "* " and "1. " at a line's start begin a list; pictures can be dropped, pasted or added.
/// </summary>
public sealed class NoteEditor : RichTextBox
{
    public NoteInfo Note { get; private set; }
    /// <summary>The user changed the note (saved by the window a moment later).</summary>
    public event Action Edited;
    bool _loading, _guarding;
    Point _downAt;

    public NoteEditor()
    {
        AcceptsTab = true;
        AcceptsReturn = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(22, 4, 22, 40);
        FocusVisualStyle = null;
        AllowDrop = true;
        SpellCheck.IsEnabled = true;
        SetResourceReference(ForegroundProperty, "LabelBrush");
        SetResourceReference(CaretBrushProperty, "LabelBrush");
        SelectionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x4D));
        SelectionOpacity = 0.45;
        TextChanged += (_, _) => { if (!_loading) { NoteFlow.Renumber(Document); Edited?.Invoke(); } };
        SelectionChanged += (_, _) => GuardCaret();
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTextInput;
        DataObject.AddPastingHandler(this, OnPasting);
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
        PreviewMouseLeftButtonDown += (_, e) => _downAt = e.GetPosition(this);
        PreviewMouseLeftButtonUp += OnMouseUp;
        Loaded += (_, _) => { CheckMarker.Toggled -= OnCheckToggled; CheckMarker.Toggled += OnCheckToggled; };
        Unloaded += (_, _) => CheckMarker.Toggled -= OnCheckToggled;
    }

    void OnCheckToggled(CheckMarker m)
    {
        if (_loading || Document == null) return;
        for (DependencyObject d = m; d != null; d = LogicalTreeHelper.GetParent(d))
            if (d == Document) { Edited?.Invoke(); return; }
    }

    /// <summary>Shows a note.</summary>
    public void Show(NoteInfo n, NoteDoc d)
    {
        _loading = true;
        try
        {
            Note = n;
            Document = NoteFlow.ToFlow(d, n);
            Document.PagePadding = new Thickness(0);
            CaretPosition = Document.ContentEnd;
        }
        finally { _loading = false; }
    }

    /// <summary>The note as it is now (pasted pictures kept as the note's own).</summary>
    public NoteDoc Read() => NoteFlow.FromFlow(Document, bmp => Note == null ? null : NotesLibrary.AddImage(Note, bmp));

    // ------------------------------------------------------------------ the paragraphs the selection touches

    public List<Paragraph> SelectedParagraphs()
    {
        var list = new List<Paragraph>();
        var start = Selection.Start.Paragraph;
        var end = Selection.End.Paragraph;
        if (start == null) return list;
        bool inside = false;
        foreach (var b in Document.Blocks)
        {
            if (b is not Paragraph p) continue;
            if (p == start) inside = true;
            if (inside) list.Add(p);
            if (p == end || end == null && p == start) break;
        }
        if (list.Count == 0) list.Add(start);
        return list;
    }

    TextPointer TextStart(Paragraph p) => NoteFlow.Marker(p) != null ? p.Inlines.FirstInline.ElementEnd : p.ContentStart;

    bool AtTextStart(Paragraph p) =>
        Selection.IsEmpty && CaretPosition.GetInsertionPosition(LogicalDirection.Forward).CompareTo(TextStart(p).GetInsertionPosition(LogicalDirection.Forward)) <= 0;

    static bool HasText(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length > 0;

    /// <summary>The caret never sits in front of a list item's marker.</summary>
    void GuardCaret()
    {
        if (_guarding || _loading || !Selection.IsEmpty) return;
        var p = CaretPosition.Paragraph;
        if (p == null || NoteFlow.Marker(p) == null) return;
        var after = p.Inlines.FirstInline.ElementEnd;
        if (CaretPosition.CompareTo(after) < 0)
        {
            _guarding = true;
            try { CaretPosition = after; } finally { _guarding = false; }
        }
    }

    // ------------------------------------------------------------------ formatting (the Format menu and the Aa button)

    public string CurrentStyle() => Selection.Start.Paragraph is Paragraph p ? StyleName(p) : "body";

    static string StyleName(Paragraph p) => NoteFlow.StyleOf(p);

    public string CurrentList() => Selection.Start.Paragraph is Paragraph p ? NoteFlow.ListOf(p) : null;

    public void SetStyle(string style)
    {
        BeginChange();
        try
        {
            foreach (var p in SelectedParagraphs())
            {
                NoteFlow.ApplyStyle(p, style);
                foreach (var r in Descendants(p).OfType<TextElement>()) { r.ClearValue(TextElement.FontSizeProperty); r.ClearValue(TextElement.FontFamilyProperty); }
            }
        }
        finally { EndChange(); }
        Edited?.Invoke();
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    /// <summary>The selected paragraphs made a list of a kind - or, if they all already are one, plain paragraphs again.</summary>
    public void ToggleList(string kind)
    {
        var ps = SelectedParagraphs();
        bool allAlready = ps.All(p => NoteFlow.ListOf(p) == kind);
        BeginChange();
        try { foreach (var p in ps) NoteFlow.SetList(p, allAlready ? null : kind, NoteFlow.IndentOf(p)); }
        finally { EndChange(); }
        NoteFlow.Renumber(Document);
        GuardCaret();
        Edited?.Invoke();
    }

    public void ToggleChecked()
    {
        foreach (var p in SelectedParagraphs())
            if (NoteFlow.Marker(p) is CheckMarker m) m.IsChecked = !m.IsChecked;
        Edited?.Invoke();
    }

    public void Indent(int delta)
    {
        BeginChange();
        try
        {
            foreach (var p in SelectedParagraphs())
            {
                int now = NoteFlow.IndentOf(p);
                NoteFlow.SetList(p, NoteFlow.ListOf(p), Math.Max(0, now + delta), NoteFlow.IsChecked(p));
            }
        }
        finally { EndChange(); }
        NoteFlow.Renumber(Document);
        GuardCaret();
        Edited?.Invoke();
    }

    public void ToggleQuote()
    {
        var ps = SelectedParagraphs();
        bool all = ps.All(NoteFlow.IsQuote);
        foreach (var p in ps) NoteFlow.SetQuote(p, !all);
        Edited?.Invoke();
    }

    public void ToggleDecoration(TextDecorationLocation where)
    {
        var cur = Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool has = cur?.Any(d => d.Location == where) == true;
        var next = new TextDecorationCollection();
        if (cur != null) foreach (var d in cur) if (d.Location != where) next.Add(d);
        if (!has) next.Add(where == TextDecorationLocation.Strikethrough ? TextDecorations.Strikethrough[0] : TextDecorations.Underline[0]);
        Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        Edited?.Invoke();
    }

    public bool SelectionIs(DependencyProperty prop, object value) => Equals(Selection.GetPropertyValue(prop), value);

    public bool SelectionHas(TextDecorationLocation where) =>
        Selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection c && c.Any(d => d.Location == where);

    /// <summary>A link on the selected words (Edit › Add Link, ⌘K).</summary>
    public void AddLink(Window owner)
    {
        if (Selection.IsEmpty) return;
        string url = TextPrompt.Ask(owner, "Add Link", "The address the selected text links to:", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        if (!url.Contains("://") && !url.StartsWith("mailto:")) url = "https://" + url.Trim();
        var h = new Hyperlink(Selection.Start, Selection.End) { Tag = url, Cursor = Cursors.Hand, Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x84, 0x00)) };
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) h.NavigateUri = u;
        Edited?.Invoke();
    }

    /// <summary>A click on a link opens it (as in Notes), unless the click was a drag selecting text.</summary>
    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Selection.IsEmpty || (e.GetPosition(this) - _downAt).Length > 3) return;
        var pos = GetPositionFromPoint(e.GetPosition(this), false);
        for (DependencyObject d = pos?.Parent; d != null; d = d is TextElement te ? te.Parent : null)
            if (d is Hyperlink h && (h.Tag as string ?? h.NavigateUri?.ToString()) is string url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                e.Handled = true;
                return;
            }
    }

    // ------------------------------------------------------------------ typing

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var p = CaretPosition.Paragraph;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.T: SetStyle("title"); e.Handled = true; return;
                case Key.H: SetStyle("heading"); e.Handled = true; return;
                case Key.J: SetStyle("subheading"); e.Handled = true; return;
                case Key.B: SetStyle("body"); e.Handled = true; return;
                case Key.M: SetStyle("mono"); e.Handled = true; return;
                case Key.D7 or Key.NumPad7: ToggleList("bullet"); e.Handled = true; return;
                case Key.D8 or Key.NumPad8: ToggleList("dash"); e.Handled = true; return;
                case Key.D9 or Key.NumPad9: ToggleList("number"); e.Handled = true; return;
                case Key.L: ToggleList("check"); e.Handled = true; return;
                case Key.U: ToggleChecked(); e.Handled = true; return;
                case Key.X: ToggleDecoration(TextDecorationLocation.Strikethrough); e.Handled = true; return;
                case Key.OemQuotes: ToggleQuote(); e.Handled = true; return;
            }
        }
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.K: AddLink(Window.GetWindow(this)); e.Handled = true; return;
                case Key.OemCloseBrackets: Indent(1); e.Handled = true; return;
                case Key.OemOpenBrackets: Indent(-1); e.Handled = true; return;
                case Key.U: ToggleDecoration(TextDecorationLocation.Underline); e.Handled = true; return;
            }
        }
        if (p == null) return;
        string kind = NoteFlow.ListOf(p);
        if (key == Key.Enter && mods == ModifierKeys.None)
        {
            e.Handled = true;
            Enter(p, kind);
        }
        else if (key == Key.Back && mods == ModifierKeys.None && AtTextStart(p))
        {
            if (kind != null) { BeginChange(); NoteFlow.SetList(p, null, NoteFlow.IndentOf(p)); EndChange(); NoteFlow.Renumber(Document); Edited?.Invoke(); e.Handled = true; }
            else if (NoteFlow.IsQuote(p)) { NoteFlow.SetQuote(p, false); Edited?.Invoke(); e.Handled = true; }
            else if (NoteFlow.IndentOf(p) > 0) { Indent(-1); e.Handled = true; }
        }
        else if (key == Key.Tab && (kind != null || mods == ModifierKeys.Shift))
        {
            Indent(mods == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    void Enter(Paragraph p, string kind)
    {
        // an empty list item: Return ends the list (as on a Mac)
        if (kind != null && !HasText(p))
        {
            BeginChange(); NoteFlow.SetList(p, null, NoteFlow.IndentOf(p)); EndChange();
            NoteFlow.Renumber(Document);
            Edited?.Invoke();
            return;
        }
        string style = StyleName(p);
        bool atEnd = CaretPosition.GetInsertionPosition(LogicalDirection.Forward).CompareTo(p.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)) >= 0;
        int indent = NoteFlow.IndentOf(p);
        bool quote = NoteFlow.IsQuote(p);
        BeginChange();
        try
        {
            if (!Selection.IsEmpty) Selection.Text = "";
            EditingCommands.EnterParagraphBreak.Execute(null, this);
            var np = CaretPosition.Paragraph;
            if (np != null && np != p)
            {
                if (np.Inlines.FirstInline is InlineUIContainer c && c.Child is FrameworkElement fe && fe.Tag is string t && t.StartsWith("marker:")) np.Inlines.Remove(c);
                if (style is "title" or "heading" or "subheading" && atEnd) NoteFlow.ApplyStyle(np, "body");
                else NoteFlow.ApplyStyle(np, style);
                NoteFlow.SetList(np, kind, indent);
                NoteFlow.SetQuote(np, quote);
                CaretPosition = TextStart(np);
            }
        }
        finally { EndChange(); }
        NoteFlow.Renumber(Document);
        Edited?.Invoke();
    }

    /// <summary>"- ", "* " or "1. " typed at the start of a line begins a list (as in Notes).</summary>
    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != " ") return;
        var p = CaretPosition.Paragraph;
        if (p == null || NoteFlow.ListOf(p) != null) return;
        string before = new TextRange(p.ContentStart, CaretPosition).Text;
        string kind = before switch { "-" => "dash", "*" => "bullet", "1." => "number", "[]" or "[ ]" => "check", _ => null };
        if (kind == null) return;
        e.Handled = true;
        BeginChange();
        try
        {
            new TextRange(p.ContentStart, CaretPosition).Text = "";
            NoteFlow.SetList(p, kind, NoteFlow.IndentOf(p));
            CaretPosition = TextStart(p);
        }
        finally { EndChange(); }
        NoteFlow.Renumber(Document);
        Edited?.Invoke();
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>Pictures put in at the caret, each a block of its own, with a line to type on after them.</summary>
    public void InsertImages(IEnumerable<(string file, int w, int h)> images)
    {
        var list = images.ToList();
        if (list.Count == 0 || Note == null) return;
        BeginChange();
        try
        {
            var p = CaretPosition.Paragraph;
            Block after = p;
            if (p != null && HasText(p) && !AtTextStart(p))
            {
                // the paragraph is split where the caret is
                bool atEnd = CaretPosition.GetInsertionPosition(LogicalDirection.Forward).CompareTo(p.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)) >= 0;
                if (!atEnd) { EditingCommands.EnterParagraphBreak.Execute(null, this); after = p; }
            }
            else if (p != null && !HasText(p) && NoteFlow.ListOf(p) == null)
            {
                after = p.PreviousBlock;   // an empty line: the pictures take its place
                Document.Blocks.Remove(p);
            }
            foreach (var (file, w, h) in list)
            {
                var bu = new BlockUIContainer(new NoteImage(NotesLibrary.ImagePath(Note, file), file, w, h)) { Margin = new Thickness(0, 4, 0, 6) };
                if (after != null) Document.Blocks.InsertAfter(after, bu);
                else if (Document.Blocks.FirstBlock != null) Document.Blocks.InsertBefore(Document.Blocks.FirstBlock, bu);
                else Document.Blocks.Add(bu);
                after = bu;
            }
            if (after.NextBlock is not Paragraph next)
            {
                next = new Paragraph();
                NoteFlow.ApplyStyle(next, "body");
                Document.Blocks.InsertAfter(after, next);
            }
            CaretPosition = next.ContentStart;
        }
        finally { EndChange(); }
        Edited?.Invoke();
        Focus();
    }

    public void InsertImageFiles(IEnumerable<string> paths)
    {
        if (Note == null) return;
        var added = paths.Where(NotesLibrary.IsImageFile).Select(f => NotesLibrary.AddImage(Note, f)).Where(r => r != null).Select(r => r.Value).ToList();
        InsertImages(added);
    }

    void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        try
        {
            var data = e.DataObject;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Any(NotesLibrary.IsImageFile))
            {
                e.CancelCommand();
                InsertImageFiles(files);
                return;
            }
            bool hasText = data.GetDataPresent(DataFormats.Rtf) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);
            if (!hasText && (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG")))
            {
                e.CancelCommand();
                var bmp = ReadClipboardImage(data);
                if (bmp != null && Note != null && NotesLibrary.AddImage(Note, bmp) is { } img) InsertImages(new[] { img });
                return;
            }
            if (data.GetDataPresent(DataFormats.Rtf) || data.GetDataPresent(DataFormats.Xaml))
                Dispatcher.BeginInvoke(Tidy, System.Windows.Threading.DispatcherPriority.Background);   // (pasted styles become the note's own)
        }
        catch (Exception ex) { App.Log("notes paste: " + ex.Message); }
    }

    static BitmapSource ReadClipboardImage(IDataObject data)
    {
        try
        {
            // "PNG" keeps transparency (browsers, screenshot tools); a bitmap otherwise
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms)
            {
                var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return dec.Frames[0];
            }
            return Clipboard.GetImage();
        }
        catch { return null; }
    }

    /// <summary>Pasted rich text made into the note's own paragraphs and styles, keeping where the caret is.</summary>
    void Tidy()
    {
        if (Note == null) return;
        int caret = Document.ContentStart.GetOffsetToPosition(CaretPosition);
        var doc = Read();
        _loading = true;
        try
        {
            Document = NoteFlow.ToFlow(doc, Note);
            var pos = Document.ContentStart.GetPositionAtOffset(Math.Min(caret, Document.ContentStart.GetOffsetToPosition(Document.ContentEnd)));
            if (pos != null) CaretPosition = pos.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally { _loading = false; }
        Edited?.Invoke();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(NotesLibrary.IsImageFile))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else if (WebDrop.Has(e.Data) && !e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var at = GetPositionFromPoint(e.GetPosition(this), true);
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(NotesLibrary.IsImageFile))
        {
            e.Handled = true;
            if (at != null) CaretPosition = at;
            InsertImageFiles(files);
            return;
        }
        if (!WebDrop.Has(e.Data)) return;
        // a picture dragged out of a web page: saved, then put in
        var items = WebDrop.Extract(e.Data);
        bool pictures = items.Any(i => NotesLibrary.IsImageFile(i.Name) || (i.Url != null && NotesLibrary.IsImageFile(new Uri(i.Url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(i.Url).AbsolutePath : i.Url)));
        if (!pictures) return;
        e.Handled = true;
        if (at != null) CaretPosition = at;
        string temp = Path.Combine(Path.GetTempPath(), "macshell-notes-drop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        _ = WebDrop.SaveAsync(items, temp, null).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.IsCompletedSuccessfully) InsertImageFiles(t.Result);
            try { Directory.Delete(temp, true); } catch { }
        }));
    }
}
