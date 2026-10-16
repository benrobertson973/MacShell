using System.Diagnostics;
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
        PreviewMouseMove += (_, e) => Cursor = LinkAt(e.GetPosition(this)) != null ? Cursors.Hand : Cursors.IBeam;
        AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler((_, e) => { OpenUrl(e.Uri?.ToString()); e.Handled = true; }));
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, e) => BuildContextMenu(e);
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
            AutoLinkAll();   // (web addresses written before links were made)
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

    /// <summary>A link on the selected words (Edit › Add Link, ⌘K) - or the link the caret is in, changed.</summary>
    public void AddLink(Window owner)
    {
        var existing = LinkAtPosition(Selection.Start);
        if (Selection.IsEmpty && existing == null) return;
        string url = TextPrompt.Ask(owner, existing != null ? "Edit Link" : "Add Link", "The address the text links to:", existing != null ? LinkUrl(existing) : "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        url = NormalizeUrl(url.Trim());
        if (existing != null) { existing.Tag = url; if (Uri.TryCreate(url, UriKind.Absolute, out var eu)) existing.NavigateUri = eu; }
        else MakeLink(Selection.Start, Selection.End, url);
        Edited?.Invoke();
    }

    static string NormalizeUrl(string url) =>
        url.Contains("://") || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? url : url.Contains('@') && !url.Contains('/') ? "mailto:" + url : "https://" + url;

    static Hyperlink MakeLink(TextPointer start, TextPointer end, string url)
    {
        var h = new Hyperlink(start, end) { Tag = url, Cursor = Cursors.Hand, Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x84, 0x00)) };
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) h.NavigateUri = u;
        return h;
    }

    static string LinkUrl(Hyperlink h) => h.Tag as string ?? h.NavigateUri?.ToString();

    static Hyperlink LinkAtPosition(TextPointer pos)
    {
        for (DependencyObject d = pos?.Parent; d != null; d = d is TextElement te ? te.Parent : null)
            if (d is Hyperlink h) return h;
        return null;
    }

    Hyperlink LinkAt(Point pt) => LinkAtPosition(GetPositionFromPoint(pt, false));

    static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(NormalizeUrl(url)) { UseShellExecute = true }); } catch (Exception ex) { App.Log("notes link: " + ex.Message); }
    }

    /// <summary>A click on a link opens it (as in Notes), unless the click was a drag selecting text.</summary>
    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.GetPosition(this) - _downAt).Length > 3 || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var h = LinkAt(e.GetPosition(this));
        if (h == null) return;
        OpenUrl(LinkUrl(h));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ web addresses become links by themselves

    static readonly System.Text.RegularExpressions.Regex UrlInText = new(
        @"(?<![\w@/.-])((?:https?://|www\.)[^\s<>""]+|[\w.+-]+@[\w-]+(?:\.[\w-]+)+|(?:[a-z0-9-]+\.)+(?:com|org|net|edu|gov|io|co|us|uk|ca|de|fr|app|dev|ai|me|tv|info|biz|ly|gg|nz|au)(?:/[^\s<>""]*)?)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A position this many characters (insertion positions) further on.</summary>
    static TextPointer Forward(TextPointer p, int chars)
    {
        for (int i = 0; i < chars && p != null; i++) p = p.GetNextInsertionPosition(LogicalDirection.Forward);
        return p;
    }

    /// <summary>The web addresses and email addresses in a paragraph, not yet links, made links.</summary>
    void AutoLink(Paragraph p)
    {
        var start = TextStart(p);
        string text = new TextRange(start, p.ContentEnd).Text;
        var matches = UrlInText.Matches(text).Cast<System.Text.RegularExpressions.Match>().ToList();
        // from the end, so earlier positions stay right
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            string word = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"');
            if (word.Length < 4 || word.Contains('\n')) continue;
            var a = Forward(start.GetInsertionPosition(LogicalDirection.Forward), m.Index);
            var b = Forward(a, word.Length);
            if (a == null || b == null || LinkAtPosition(a.GetPositionAtOffset(1) ?? a) != null) continue;
            if (new TextRange(a, b).Text != word) continue;   // (the paragraph has pieces that aren't text: leave it)
            try { MakeLink(a, b, NormalizeUrl(word)); } catch { }
        }
    }

    void AutoLinkAll()
    {
        foreach (var p in Document.Blocks.OfType<Paragraph>().ToList()) AutoLink(p);
    }

    /// <summary>The word just typed (before a space or Return) made a link if it's an address.</summary>
    void LinkWordBeforeCaret()
    {
        var p = CaretPosition.Paragraph;
        if (p == null || LinkAtPosition(CaretPosition) != null) return;
        string before = new TextRange(TextStart(p), CaretPosition).Text;
        var m = System.Text.RegularExpressions.Regex.Match(before, @"(\S+)$");
        if (!m.Success || !UrlInText.IsMatch(m.Value)) return;
        int caret = Document.ContentStart.GetOffsetToPosition(CaretPosition);
        BeginChange();
        try { AutoLink(p); }
        finally { EndChange(); }
        var back = Document.ContentStart.GetPositionAtOffset(caret);
        // (the caret goes after the new link, not inside it)
        if (LinkAtPosition(CaretPosition) is Hyperlink h) CaretPosition = h.ElementEnd;
        else if (back != null) CaretPosition = back;
    }

    // ------------------------------------------------------------------ the right-click menu, as in Notes on a Mac

    static dynamic _voice;

    void BuildContextMenu(ContextMenuEventArgs e)
    {
        var cm = ContextMenu;
        cm.Items.Clear();
        var at = GetPositionFromPoint(Mouse.GetPosition(this), true) ?? CaretPosition;
        // a click outside the selection moves the caret there first (as on a Mac)
        if (!Selection.IsEmpty && !Selection.Contains(at)) Selection.Select(at, at);
        else if (Selection.IsEmpty) CaretPosition = at;
        string word = Selection.IsEmpty ? WordAt(at) : Selection.Text.Trim();
        var err = GetSpellingError(at);
        if (err != null)
        {
            var suggestions = err.Suggestions.Take(5).ToList();
            foreach (var s in suggestions) Mb.Add(cm.Items, Mb.Item(s, () => { err.Correct(s); Edited?.Invoke(); }));
            if (suggestions.Count == 0) Mb.Add(cm.Items, Mb.Item("No Guesses Found", enabled: false));
            Mb.Add(cm.Items, Mb.Sep());
            Mb.Add(cm.Items, Mb.Item("Ignore Spelling", () => err.IgnoreAll()));
            Mb.Add(cm.Items, Mb.Sep());
        }
        if (!string.IsNullOrWhiteSpace(word))
        {
            string shortWord = word.Length > 24 ? word[..24].TrimEnd() + "…" : word;
            string q = Uri.EscapeDataString(word);
            Mb.Add(cm.Items, Mb.Item($"Look Up “{shortWord}”", () => OpenUrl("https://www.google.com/search?q=define+" + q)));
            Mb.Add(cm.Items, Mb.Item($"Translate “{shortWord}”", () => OpenUrl("https://translate.google.com/?sl=auto&tl=en&op=translate&text=" + q)));
            Mb.Add(cm.Items, Mb.Item("Search With Google", () => OpenUrl("https://www.google.com/search?q=" + q)));
            Mb.Add(cm.Items, Mb.Sep());
        }
        bool sel = !Selection.IsEmpty;
        Mb.Add(cm.Items, Mb.Item("Cut", () => ApplicationCommands.Cut.Execute(null, this), enabled: sel && !IsReadOnly));
        Mb.Add(cm.Items, Mb.Item("Copy", () => ApplicationCommands.Copy.Execute(null, this), enabled: sel));
        bool canPaste = false;
        try { canPaste = !IsReadOnly && (Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()); } catch { }
        Mb.Add(cm.Items, Mb.Item("Paste", () => ApplicationCommands.Paste.Execute(null, this), enabled: canPaste));
        Mb.Add(cm.Items, Mb.Sep());
        var link = LinkAtPosition(at);
        if (link != null)
        {
            Mb.Add(cm.Items, Mb.Item("Open Link", () => OpenUrl(LinkUrl(link))));
            Mb.Add(cm.Items, Mb.Item("Copy Link", () => { try { Clipboard.SetText(LinkUrl(link) ?? ""); } catch { } }));
            Mb.Add(cm.Items, Mb.Item("Edit Link…", () => AddLink(Window.GetWindow(this)), enabled: !IsReadOnly));
            Mb.Add(cm.Items, Mb.Item("Remove Link", () => RemoveLink(link), enabled: !IsReadOnly));
        }
        else Mb.Add(cm.Items, Mb.Item("Add Link…", () => AddLink(Window.GetWindow(this)), "⌘K", enabled: sel && !IsReadOnly));
        Mb.Add(cm.Items, Mb.Sep());
        if (!IsReadOnly)
        {
            Mb.Add(cm.Items, Mb.Sub("Font",
                Mb.Item("Bold", () => EditingCommands.ToggleBold.Execute(null, this), "⌘B", isChecked: SelectionIs(TextElement.FontWeightProperty, FontWeights.Bold)),
                Mb.Item("Italic", () => EditingCommands.ToggleItalic.Execute(null, this), "⌘I", isChecked: SelectionIs(TextElement.FontStyleProperty, FontStyles.Italic)),
                Mb.Item("Underline", () => ToggleDecoration(TextDecorationLocation.Underline), "⌘U", isChecked: SelectionHas(TextDecorationLocation.Underline)),
                Mb.Item("Strikethrough", () => ToggleDecoration(TextDecorationLocation.Strikethrough), "⇧⌘X", isChecked: SelectionHas(TextDecorationLocation.Strikethrough))));
            Mb.Add(cm.Items, Mb.Sub("Spelling and Grammar",
                Mb.Item("Check Spelling While Typing", () => SpellCheck.SetIsEnabled(this, !SpellCheck.GetIsEnabled(this)), isChecked: SpellCheck.GetIsEnabled(this))));
            Mb.Add(cm.Items, Mb.Sub("Transformations",
                Mb.Item("Make Upper Case", () => Transform(s => s.ToUpper(System.Globalization.CultureInfo.CurrentCulture)), enabled: sel),
                Mb.Item("Make Lower Case", () => Transform(s => s.ToLower(System.Globalization.CultureInfo.CurrentCulture)), enabled: sel),
                Mb.Item("Capitalize", () => Transform(s => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(System.Globalization.CultureInfo.CurrentCulture))), enabled: sel)));
        }
        string speak = sel ? Selection.Text : new TextRange(Document.ContentStart, Document.ContentEnd).Text;
        Mb.Add(cm.Items, Mb.Sub("Speech",
            Mb.Item("Start Speaking", () => Speak(speak)),
            Mb.Item("Stop Speaking", () => Speak(null))));
        if (cm.Items.Count > 0 && cm.Items[^1] is Separator s2) cm.Items.Remove(s2);
    }

    static string WordAt(TextPointer p)
    {
        if (p == null) return null;
        string before = p.GetTextInRun(LogicalDirection.Backward), after = p.GetTextInRun(LogicalDirection.Forward);
        var b = System.Text.RegularExpressions.Regex.Match(before ?? "", @"[\w'-]*$").Value;
        var a = System.Text.RegularExpressions.Regex.Match(after ?? "", @"^[\w'-]*").Value;
        string w = (b + a).Trim('\'', '-');
        return w.Length == 0 ? null : w;
    }

    void Transform(Func<string, string> f)
    {
        if (Selection.IsEmpty) return;
        Selection.Text = f(Selection.Text);
        Edited?.Invoke();
    }

    void RemoveLink(Hyperlink h)
    {
        if (h.Parent is not Paragraph and not Span) return;
        var start = h.ContentStart;
        var text = new TextRange(h.ContentStart, h.ContentEnd).Text;
        var r = new Run(text);
        if (h.Parent is Paragraph pp) { pp.Inlines.InsertBefore(h, r); pp.Inlines.Remove(h); }
        else if (h.Parent is Span sp) { sp.Inlines.InsertBefore(h, r); sp.Inlines.Remove(h); }
        Edited?.Invoke();
    }

    /// <summary>Speech › Start Speaking: Windows' own voice reads it (Stop Speaking: quiet).</summary>
    static void Speak(string text)
    {
        try
        {
            _voice ??= Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
            _voice.Speak(string.IsNullOrWhiteSpace(text) ? "" : text, text == null ? 2 /*purge*/ : 1 | 2 /*async, purge what was being said*/);
        }
        catch (Exception ex) { App.Log("notes speech: " + ex.Message); }
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
        LinkWordBeforeCaret();
        p = CaretPosition.Paragraph ?? p;
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
        if (e.Text is " " or "\t") LinkWordBeforeCaret();
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
            else
                Dispatcher.BeginInvoke(() =>
                {
                    // web addresses in pasted text become links
                    int caret = Document.ContentStart.GetOffsetToPosition(CaretPosition);
                    BeginChange();
                    try { AutoLinkAll(); } finally { EndChange(); }
                    if (Document.ContentStart.GetPositionAtOffset(caret) is { } back) CaretPosition = back;
                }, System.Windows.Threading.DispatcherPriority.Background);
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
            AutoLinkAll();
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
