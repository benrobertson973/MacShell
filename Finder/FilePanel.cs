using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Shapes = System.Windows.Shapes;

namespace MacShell.Finder;

/// <summary>What an Open or Save panel is asked for.</summary>
public sealed class FilePanelRequest
{
    public bool Save;
    /// <summary>Folders are chosen, not files.</summary>
    public bool Folders;
    public bool Multiple;
    /// <summary>The asking app's own words ("Select the extension directory."): shown along the top, as a Mac panel
    /// shows an app's message - unless they're only "Open" or "Save As".</summary>
    public string Title;
    /// <summary>The button's name: Open, Save, Upload, Select Folder …</summary>
    public string Prompt;
    public string Folder;
    /// <summary>Save: the name suggested.</summary>
    public string FileName;
    /// <summary>The kinds of file, as the app names them ("Images (*.png;*.jpg)").</summary>
    public List<string> Filters = new();
    public int FilterIndex;
    public string AppName;
    /// <summary>Remembers where this app last opened or saved (the folder a panel for it starts in), and whether its
    /// Save panel was opened out.</summary>
    public string AppKey;
    /// <summary>The asking window (pixels): the panel hangs from it, as a sheet.</summary>
    public RECT? Over;
    /// <summary>"Show Windows Dialog" among the options, for what the panel can't do (an app's own extra options).</summary>
    public bool OfferWindowsDialog;
}

public sealed class FilePanelResult
{
    public List<string> Paths = new();
    public int FilterIndex;
    public bool UseWindowsDialog;
    /// <summary>Save: the user already agreed to replace the file there (in the panel).</summary>
    public bool ReplaceConfirmed;
}

/// <summary>
/// The Mac's Open and Save panels, as in macOS Sonoma: Finder's sidebar down the left, and its views (icons, list,
/// columns, gallery) under a toolbar of back and forward, view, sort, the folder's path and search - to choose files,
/// or folders; Cancel and Open along the bottom. Saving: a small sheet (Save As, Tags, Where) that opens out into the
/// same browser. Files of another kind are greyed out. The panel's own questions (New Folder, Go to Folder, Replace?)
/// drop down over it as sheets. Used by MacShell itself, and in place of the Windows file dialog in other apps
/// (Services/FileDialogTakeover).
/// </summary>
public class FilePanel : MacWindow, IFinderHost
{
    public static FilePanel Current { get; private set; }

    /// <summary>A panel that answers later (another app's dialog stands in for it meanwhile).</summary>
    public static FilePanel Show(FilePanelRequest req, Action<FilePanelResult> done)
    {
        var p = new FilePanel(req, done);
        p.Show();
        if (!ShellHost.Offscreen) p.Activate();
        return p;
    }

    /// <summary>A panel for MacShell's own windows: waits for the answer (null: cancelled).</summary>
    public static FilePanelResult ShowModal(Window owner, FilePanelRequest req)
    {
        FilePanelResult result = null;
        var p = new FilePanel(req, r => result = r);
        if (owner != null && owner.IsVisible) p.Owner = owner;
        p.ShowDialog();
        return result;
    }

    const double FieldWidth = 240, CompactWidth = 460, SidebarWidth = 170;

    readonly FilePanelRequest _req;
    Action<FilePanelResult> _done;

    // the panel's parts
    readonly Grid _root = new();
    readonly Grid _body = new();                        // the sidebar, and the files
    readonly ColumnDefinition _sideCol = new() { Width = new GridLength(SidebarWidth), MaxWidth = 320 };
    readonly StackPanel _sidebar = new() { Margin = new Thickness(0, 12, 0, 12) };
    readonly Dictionary<string, Border> _sideRows = new(StringComparer.OrdinalIgnoreCase);
    readonly Grid _viewHost = new();
    readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, Visibility = Visibility.Collapsed };
    readonly StackPanel _footStack = new(), _compactStack = new();   // under the files / across a small Save panel
    Border _saveHead, _compactFoot;
    Grid _saveGrid;
    DockPanel _whereRow;
    TextBlock _whereLabel;
    readonly Grid _bar = new() { Height = 52 };         // Cancel and Open, and New Folder … on the left
    readonly StackPanel _barLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    readonly Border _options = new();                   // the app's options: Format …
    readonly Grid _sheetHost = new() { Visibility = Visibility.Collapsed };
    readonly List<(Border card, Action<KeyEventArgs> keys, IInputElement focusBefore)> _sheets = new();

    // its controls
    readonly Button _back = new(), _fwd = new(), _viewBtn = new(), _sortBtn = new(), _pathBtn = new(), _ok = new(), _cancel = new(),
                    _filterBtn = new(), _whereBtn = new(), _newFolder = new(), _optionsBtn = new(), _expand = new();
    readonly CheckBox _hideExt = new();
    readonly TextBlock _pathText = new() { MaxWidth = 210 }, _filterText = new(), _whereText = new();
    readonly Border _pathIcon = new() { Width = 16, Height = 16 }, _whereIcon = new() { Width = 16, Height = 16 };
    readonly SymbolIcon _viewIcon = new() { Width = 16, Height = 16, StrokeWidth = 1.8, Symbol = "list" };
    readonly TextBox _search = new(), _name = new(), _tagText = new();
    readonly StackPanel _tagTokens = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _tagList = new() { Margin = new Thickness(0, 5, 0, 5) };
    readonly List<string> _tags = new();
    List<(string id, string name, Color color)> _tagMatches = new();
    Border _tagBox;
    Popup _tagPop;
    int _tagPick;
    bool _tagArrowed;

    // what it shows
    ListView2 _list;
    IconView _icons;
    ColumnView _columns;
    GalleryView _gallery;
    FinderView _view;
    string _viewMode;
    string _loc;
    readonly List<string> _backStack = new(), _fwdStack = new();
    List<FileItem> _all = new(), _display = new();
    readonly Dictionary<string, List<FileItem>> _children = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource _loadCts;
    int _filter;
    string[] _patterns = { "*" };
    FileItem _anchor, _deferSingle;
    string _sortKey = "name";
    bool _sortAsc = true;
    bool _answered, _expanded, _showOptions;
    string _hiddenExt = "";
    string _pendingSelect;

    FilePanel(FilePanelRequest req, Action<FilePanelResult> done) : base(WindowTracker.FinderKey, 0)
    {
        _req = req;
        _done = done;
        Current = this;
        Title = req.Save ? "Save" : "Open";
        ShowInTaskbar = false;
        _filter = Math.Clamp(req.FilterIndex, 0, Math.Max(0, req.Filters.Count - 1));
        _patterns = Patterns(req.Filters.ElementAtOrDefault(_filter));
        _viewMode = Settings.Current.FilePanelView is "icons" or "list" or "columns" or "gallery" ? Settings.Current.FilePanelView : "list";
        _expanded = !req.Save || (req.AppKey != null && Settings.Current.FilePanelExpanded.Contains(req.AppKey, StringComparer.OrdinalIgnoreCase));
        _showOptions = req.Save || (Settings.Current.FilePanelShowOptions ?? HasFormats);
        Build();
        Closed += (_, _) =>
        {
            if (Current == this) Current = null;
            _loadCts?.Cancel();
            if (_tagPop != null) _tagPop.IsOpen = false;
            if (!Compact && !ShellHost.Offscreen && ActualWidth > 100)
            {
                Settings.Current.FilePanelSize = new[] { Math.Round(ActualWidth), Math.Round(ActualHeight) };
                Settings.Save(notify: false);
            }
            Answer(null);
        };
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            if (_req.Save) { _name.Focus(); SelectBaseName(); }
            else _view?.FocusView();
        };
        // the tags' list doesn't float on when the panel is left or moved
        Deactivated += (_, _) => { if (_tagPop != null) _tagPop.IsOpen = false; };
        LocationChanged += (_, _) => { if (_tagPop != null) _tagPop.IsOpen = false; };
        Navigate(StartFolder(), false);
        Arrange();
        Fit(first: true);
    }

    /// <summary>Where it starts: the folder asked for; else where this app was last; else Documents (saving, or
    /// choosing a folder) or Recents (opening a file).</summary>
    string StartFolder()
    {
        if (!string.IsNullOrEmpty(_req.Folder) && (Directory.Exists(_req.Folder) || FinderLocation.IsVirtual(_req.Folder))) return _req.Folder;
        if (_req.AppKey != null && Settings.Current.FilePanelPlaces.TryGetValue(_req.AppKey, out var last) && Directory.Exists(last)) return last;
        return _req.Save || _req.Folders ? FinderLocation.Documents : FinderLocation.Recents;
    }

    bool Compact => _req.Save && !_expanded;
    bool HasFormats => _req.Filters.Count > 1 && !_req.Folders;
    /// <summary>Options to show (as a Mac panel shows an app's accessory): the kinds of file to choose between. (The
    /// app's own Windows dialog is offered among them, and in the menu of the files' background.)</summary>
    bool HasOptions => HasFormats;

    /// <summary>Sized for what it shows: a small Save panel as tall as it needs; an Open panel, or Save opened out, as
    /// the user last left one. The first time it hangs from the asking window (centered, a little below its top, as a
    /// sheet from a Mac window); opening out and back it keeps its middle and its top.</summary>
    void Fit(bool first)
    {
        double w, h;
        if (Compact)
        {
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 0; MinHeight = 0;
            w = CompactWidth; h = 0;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 560; MinHeight = _req.Save ? 420 : 340;
            var s = Settings.Current.FilePanelSize;
            w = s is { Length: 2 } && s[0] >= MinWidth ? s[0] : 820;
            h = s is { Length: 2 } && s[1] >= MinHeight ? s[1] : _req.Save ? 560 : 480;
            w = Math.Min(w, ShellHost.ScreenDip.Width - 40);
            h = Math.Min(h, ShellHost.ScreenDip.Height - ShellHost.MenuBarHeight - 90);
        }
        if (first) Place(w, h);
        else Place(w, h, Left + ActualWidth / 2, Top);
    }

    void Place(double w, double h, double? centerX = null, double? top = null)
    {
        SizeToContent = h > 0 ? SizeToContent.Manual : SizeToContent.Height;
        Width = w;
        if (h > 0) Height = h;
        double s = ShellHost.Scale;
        var scr = ShellHost.ScreenPx;
        double sl = scr.Left / s, st = scr.Top / s + ShellHost.MenuBarHeight, sr = scr.Right / s, sb = scr.Bottom / s - (ShellHost.Dock?.ReservedHeight ?? 0);
        double tall = h > 0 ? h : 260, left, y;
        if (centerX is double cx && !ShellHost.Offscreen) { left = cx - w / 2; y = top ?? st; }
        else if (_req.Over is RECT o && o.Width > 100) { left = (o.Left + o.Right) / 2.0 / s - w / 2; y = o.Top / s + 46; }
        else { left = (sl + sr) / 2 - w / 2; y = st + (sb - st - tall) * 0.3; }
        Left = Math.Clamp(left, sl + 4, Math.Max(sl + 4, sr - w - 4));
        Top = Math.Clamp(y, st + 4, Math.Max(st + 4, sb - tall - 4));
        ApplyOffscreen();
    }

    // ================================================================== building

    void Build()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // the app's message
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // Save As, Tags, Where
        _root.RowDefinitions.Add(new RowDefinition());                              // the sidebar and the files
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // a small Save panel's options and buttons

        if (Message() is string message)
        {
            var band = new Border { Padding = new Thickness(24, 10, 24, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
            band.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
            band.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
            var t = new TextBlock { Text = message, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            band.Child = t;
            Draggable(band);
            _root.Children.Add(band);
        }
        if (_req.Save)
        {
            _saveHead = BuildSaveHead();
            Grid.SetRow(_saveHead, 1);
            _root.Children.Add(_saveHead);
        }
        BuildBody();
        Grid.SetRow(_body, 2);
        _root.Children.Add(_body);
        BuildBottom();
        Grid.SetRow(_compactFoot, 3);
        _root.Children.Add(_compactFoot);
        Grid.SetRowSpan(_sheetHost, 4);
        _root.Children.Add(_sheetHost);

        // a folder (or a file) dragged in from Finder or the desktop: the panel goes there
        _root.AllowDrop = true;
        _root.DragOver += (_, e) =>
        {
            if (e.Handled) return;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        _root.Drop += (_, e) => { if (!e.Handled) { DropOn(null, e); e.Handled = true; } };
        Content = _root;
    }

    /// <summary>The app's own words for what it wants ("Select the extension directory."), shown along the top as a Mac
    /// panel shows an app's message. A plain "Open", "Save As" or "Select Folder" says nothing the panel doesn't.</summary>
    string Message()
    {
        string t = (_req.Title ?? "").Trim();
        if (t.Length == 0) return null;
        if (_req.AppName != null && string.Equals(t, _req.AppName, StringComparison.OrdinalIgnoreCase)) return null;
        var plain = new HashSet<string> { "open", "save", "as", "a", "the", "file", "files", "folder", "folders", "select", "choose", "browse", "for", "to", "upload", "attach", "export", "import", "location", "directory", "pick" };
        var words = Regex.Split(t.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length > 0).ToList();
        return words.Count <= 3 && words.All(plain.Contains) ? null : t;
    }

    /// <summary>The window is moved by its empty parts (it has no title bar).</summary>
    void Draggable(FrameworkElement e)
    {
        e.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.OriginalSource is TextBox || ev.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        };
    }

    static Style Res(string key) => (Style)Application.Current.Resources[key];

    // ------------------------------------------------------------------ Save As, Tags, Where

    Border BuildSaveHead()
    {
        _saveGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 14, 20, 16) };
        _saveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _saveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FieldWidth) });
        _saveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });   // (the open-out button, opened out)
        for (int i = 0; i < 3; i++) _saveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _saveGrid.Children.Add(FormLabel("Save As:", 0, 0));
        _name.Style = Res("MacTextField");
        _name.Text = SuggestedName();
        _name.TextChanged += (_, _) => UpdateOk();
        Grid.SetColumn(_name, 1);
        _saveGrid.Children.Add(_name);

        _saveGrid.Children.Add(FormLabel("Tags:", 1, 10));
        var tags = BuildTagsField();
        tags.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(tags, 1);
        Grid.SetColumn(tags, 1);
        _saveGrid.Children.Add(tags);

        _whereLabel = FormLabel("Where:", 2, 18);
        _saveGrid.Children.Add(_whereLabel);
        _whereRow = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = true };
        _expand.Style = Res("MacButton");
        _expand.Width = 20;
        _expand.Height = 20;
        _expand.MinWidth = 0;
        _expand.Padding = new Thickness(0);
        _expand.Focusable = false;
        _expand.Content = new SymbolIcon { Symbol = "chevron.down", Width = 10, Height = 10, StrokeWidth = 2.4 };
        _expand.Click += (_, _) => SetExpanded(!_expanded);
        _whereBtn.Style = Res("MacPopupButton");
        _whereBtn.Height = 22;
        _whereBtn.Focusable = false;
        _whereBtn.Content = PopupContent(_whereIcon, _whereText);
        _whereBtn.Click += (_, _) => WhereMenu();
        _whereRow.Children.Add(_whereBtn);
        Grid.SetRow(_whereRow, 2);
        Grid.SetColumn(_whereRow, 1);
        _saveGrid.Children.Add(_whereRow);

        var head = new Border { Child = _saveGrid };
        head.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        head.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        Draggable(head);
        return head;
    }

    static TextBlock FormLabel(string text, int row, double top)
    {
        var t = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 8, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        Grid.SetRow(t, row);
        return t;
    }

    /// <summary>What a Mac pop-up button shows: an icon and a name, and at its right end the accent-coloured square with
    /// the arrows up and down.</summary>
    static FrameworkElement PopupContent(FrameworkElement icon, TextBlock text)
    {
        var dock = new DockPanel { LastChildFill = true };
        var arrows = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new SymbolIcon { Symbol = "chevron.updown", Width = 11, Height = 11, StrokeWidth = 2.2, Foreground = Brushes.White },
        };
        arrows.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        DockPanel.SetDock(arrows, Dock.Right);
        dock.Children.Add(arrows);
        if (icon != null)
        {
            icon.Margin = new Thickness(0, 0, 6, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
        }
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        dock.Children.Add(text);
        return dock;
    }

    string SuggestedName()
    {
        string n = _req.FileName ?? "";
        if (n.Length == 0) n = "Untitled";
        try { n = Path.GetFileName(n); } catch { }
        return n;
    }

    /// <summary>The name without its extension chosen, ready to be typed over (as on a Mac).</summary>
    void SelectBaseName()
    {
        string n = _name.Text;
        int dot = n.LastIndexOf('.');
        _name.Select(0, dot > 0 ? dot : n.Length);
    }

    /// <summary>The name to save as: what's typed, and the extension the field hides (Hide extension).</summary>
    string SaveName() => _name.Text.Trim() + (_hideExt.IsChecked == true ? _hiddenExt : "");

    void HideExtension(bool hide)
    {
        if (hide)
        {
            _hiddenExt = Path.GetExtension(_name.Text.Trim());
            if (_hiddenExt.Length > 0) _name.Text = _name.Text.Trim()[..^_hiddenExt.Length];
        }
        else
        {
            _name.Text = _name.Text.Trim() + _hiddenExt;
            _hiddenExt = "";
        }
    }

    /// <summary>Opened out (the sidebar and the files too) or small (just where) - as a Mac remembers it for each app.</summary>
    void SetExpanded(bool open)
    {
        if (!_req.Save || open == _expanded) return;
        _expanded = open;
        if (_req.AppKey != null)
        {
            Settings.Current.FilePanelExpanded.RemoveAll(k => string.Equals(k, _req.AppKey, StringComparison.OrdinalIgnoreCase));
            if (open) Settings.Current.FilePanelExpanded.Add(_req.AppKey);
            Settings.Save(notify: false);
        }
        Arrange();
        Fit(first: false);
        UpdateChrome();
        _name.Focus();
    }

    // ------------------------------------------------------------------ tags

    /// <summary>Tags, as on a Mac: the ones chosen show in the field as tokens; clicking in it (or typing) lists the
    /// others. They're put on the file as it's saved.</summary>
    FrameworkElement BuildTagsField()
    {
        _tagText.BorderThickness = new Thickness(0);
        _tagText.Background = Brushes.Transparent;
        _tagText.Padding = new Thickness(0);
        _tagText.VerticalContentAlignment = VerticalAlignment.Center;
        _tagText.FocusVisualStyle = null;
        _tagText.FontSize = 13;
        _tagText.MinWidth = 24;
        _tagText.SetResourceReference(ForegroundProperty, "LabelBrush");
        _tagText.SetResourceReference(TextBoxBase.CaretBrushProperty, "LabelBrush");
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(3, 0, 4, 0) };
        DockPanel.SetDock(_tagTokens, Dock.Left);
        dock.Children.Add(_tagTokens);
        dock.Children.Add(_tagText);
        _tagBox = new Border { Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0.5), Child = dock, Cursor = Cursors.IBeam };
        _tagBox.SetResourceReference(Border.BackgroundProperty, "TextFieldBrush");
        _tagBox.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        _tagBox.MouseLeftButtonDown += (_, e) => { _tagText.Focus(); e.Handled = true; };
        var ring = new Border { Margin = new Thickness(-3), CornerRadius = new CornerRadius(7.5), BorderThickness = new Thickness(3.5), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        ring.SetResourceReference(Border.BorderBrushProperty, "FocusRingBrush");
        var host = new Grid();
        host.Children.Add(ring);
        host.Children.Add(_tagBox);
        _tagText.GotKeyboardFocus += (_, _) => { ring.Visibility = Visibility.Visible; _tagPick = 0; _tagArrowed = false; ShowTagList(); };
        _tagText.LostKeyboardFocus += (_, _) => { ring.Visibility = Visibility.Collapsed; _tagPop.IsOpen = false; };
        _tagText.TextChanged += (_, _) => { _tagPick = 0; ShowTagList(); };

        var menu = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(0.5), Margin = new Thickness(12, 2, 12, 18), MinWidth = 170, Child = _tagList };
        menu.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        menu.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        menu.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 270, Opacity = 0.25 };
        System.Windows.Documents.TextElement.SetFontFamily(menu, Theme.Font);
        _tagPop = new Popup { PlacementTarget = _tagBox, Placement = PlacementMode.Bottom, HorizontalOffset = -12, AllowsTransparency = true, StaysOpen = true, Focusable = false, Child = menu };
        return host;
    }

    void ShowTagList()
    {
        if (_tagPop == null) return;
        if (!_tagText.IsKeyboardFocused) { _tagPop.IsOpen = false; return; }
        string q = _tagText.Text.Trim();
        _tagMatches = Theme.TagColors.Where(t => !_tags.Contains(t.id) && (q.Length == 0 || t.name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _tagList.Children.Clear();
        if (_tagMatches.Count == 0) { _tagPop.IsOpen = false; return; }
        _tagPick = Math.Clamp(_tagPick, 0, _tagMatches.Count - 1);
        for (int i = 0; i < _tagMatches.Count; i++)
        {
            var (_, name, color) = _tagMatches[i];
            bool on = i == _tagPick && (_tagArrowed || q.Length > 0);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 14, 0) };
            sp.Children.Add(new Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Theme.Darken(color, 0.15)), StrokeThickness = 0.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var t = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            if (on) t.Foreground = Brushes.White; else t.SetResourceReference(TextBlock.ForegroundProperty, "MenuTextBrush");
            sp.Children.Add(t);
            var row = new Border { Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 5, 0), Child = sp, Background = Brushes.Transparent };
            if (on) row.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            int idx = i;
            row.MouseEnter += (_, _) => { if (_tagPick != idx || !_tagArrowed) { _tagPick = idx; _tagArrowed = true; ShowTagList(); } };
            row.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; AddTag(_tagMatches[idx].id); };
            _tagList.Children.Add(row);
        }
        _tagPop.IsOpen = true;
    }

    void AddTag(string id)
    {
        if (!_tags.Contains(id)) _tags.Add(id);
        _tagArrowed = false;
        _tagText.Text = "";
        RebuildTokens();
        _tagText.Focus();
        ShowTagList();
    }

    void RebuildTokens()
    {
        _tagTokens.Children.Clear();
        foreach (var id in _tags)
        {
            var (_, name, color) = Theme.TagColors.First(t => t.id == id);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            var t = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            sp.Children.Add(t);
            var token = new Border { Height = 17, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 6, 0), Margin = new Thickness(0, 0, 3, 0), Child = sp, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Arrow };
            token.SetResourceReference(Border.BackgroundProperty, "QuaternaryLabelBrush");
            string tagId = id;
            token.MouseLeftButtonDown += (_, e) => { e.Handled = true; _tags.Remove(tagId); RebuildTokens(); _tagText.Focus(); ShowTagList(); };
            _tagTokens.Children.Add(token);
        }
    }

    /// <summary>Keys typed in the Tags field: arrows pick from the list, Return or Tab (or a comma) takes the one picked,
    /// Delete on an empty field takes off the last tag. True: dealt with.</summary>
    bool TagKey(Key key)
    {
        bool open = _tagPop.IsOpen && _tagMatches.Count > 0;
        bool typed = _tagText.Text.Trim().Length > 0;
        switch (key)
        {
            case Key.Down:
                if (!open) { _tagPick = 0; _tagArrowed = true; ShowTagList(); return true; }
                _tagPick = _tagArrowed ? (_tagPick + 1) % _tagMatches.Count : 0;
                _tagArrowed = true;
                ShowTagList();
                return true;
            case Key.Up when open:
                _tagPick = (_tagPick - 1 + _tagMatches.Count) % _tagMatches.Count;
                _tagArrowed = true;
                ShowTagList();
                return true;
            case Key.Enter or Key.Tab or Key.OemComma when open && (typed || _tagArrowed):
                AddTag(_tagMatches[_tagPick].id);
                return true;
            case Key.Escape when open:
                _tagPop.IsOpen = false;
                return true;
            case Key.Back when !typed && _tags.Count > 0:
                _tags.RemoveAt(_tags.Count - 1);
                RebuildTokens();
                ShowTagList();
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the sidebar and the files

    void BuildBody()
    {
        _body.ColumnDefinitions.Add(_sideCol);
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        // the sidebar: the window's frosted backdrop shows through, tinted
        var side = new Grid();
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "SidebarTintBrush");
        side.Children.Add(tint);
        side.Children.Add(new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        _body.Children.Add(side);
        _body.Children.Add(new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.CurrentAndNext, Focusable = false });
        BuildSidebar();

        var content = new DockPanel { LastChildFill = true };
        content.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var edge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = content };
        edge.SetResourceReference(Border.BorderBrushProperty, "StrongSeparatorBrush");
        Grid.SetColumn(edge, 1);
        _body.Children.Add(edge);

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        content.Children.Add(toolbar);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _footStack };
        foot.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        foot.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        DockPanel.SetDock(foot, Dock.Bottom);
        content.Children.Add(foot);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _viewHost.Children.Add(_empty);
        content.Children.Add(_viewHost);
        SetViewMode(_viewMode, save: false);
    }

    /// <summary>The toolbar over the files, as a Mac panel's: back and forward, the view and the sort (bordered, with
    /// their menus), the folder's path in the middle, search on the right.</summary>
    FrameworkElement BuildToolbar()
    {
        var bar = new Grid { Height = 48 };
        bar.SetResourceReference(Panel.BackgroundProperty, "ToolbarBackgroundBrush");
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Draggable(bar);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        left.Children.Add(BackForward());
        Bordered(_viewBtn, _viewIcon, "View");
        _viewBtn.Margin = new Thickness(8, 0, 0, 0);
        _viewBtn.Click += (_, _) => ShowMenu(_viewBtn, ViewMenuItems());
        left.Children.Add(_viewBtn);
        Bordered(_sortBtn, new SymbolIcon { Symbol = "group", Width = 16, Height = 16, StrokeWidth = 1.8 }, "Sort By");
        _sortBtn.Margin = new Thickness(6, 0, 0, 0);
        _sortBtn.Click += (_, _) => ShowMenu(_sortBtn, SortMenuItems());
        left.Children.Add(_sortBtn);
        bar.Children.Add(left);

        _pathBtn.Style = Res("MacPopupButton");
        _pathBtn.Height = 24;
        _pathBtn.MinWidth = 170;
        _pathBtn.MaxWidth = 280;
        _pathBtn.HorizontalAlignment = HorizontalAlignment.Center;
        _pathBtn.Margin = new Thickness(10, 0, 10, 0);
        _pathBtn.Focusable = false;
        _pathBtn.Content = PopupContent(_pathIcon, _pathText);
        _pathBtn.Click += (_, _) => PathMenu();
        Grid.SetColumn(_pathBtn, 1);
        bar.Children.Add(_pathBtn);

        _search.Style = Res("MacSearchField");
        _search.Height = 24;
        _search.Width = 170;
        _search.Margin = new Thickness(0, 0, 12, 0);
        _search.VerticalAlignment = VerticalAlignment.Center;
        var searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Reload(); };
        _search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        Grid.SetColumn(_search, 2);
        bar.Children.Add(_search);

        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        Grid.SetColumnSpan(line, 3);
        bar.Children.Add(line);
        return bar;
    }

    /// <summary>Back and forward: one bordered control in two halves.</summary>
    FrameworkElement BackForward()
    {
        void Half(Button b, string sym, string tip, Action act)
        {
            b.Style = Res("ToolbarButton");
            b.Height = 23;
            b.Width = 27;
            b.MinWidth = 0;
            b.Padding = new Thickness(0);
            b.SetResourceReference(ForegroundProperty, "LabelBrush");
            b.Content = new SymbolIcon { Symbol = sym, Width = 13, Height = 13, StrokeWidth = 2.3 };
            b.ToolTip = tip;
            b.Click += (_, _) => act();
        }
        Half(_back, "chevron.left", "Back", GoBack);
        Half(_fwd, "chevron.right", "Forward", GoForward);
        var under = new Border { CornerRadius = new CornerRadius(5.5), Margin = new Thickness(0, 0, 0, -0.6) };
        under.SetResourceReference(Border.BackgroundProperty, "ControlBottomBorderBrush");
        var bd = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0.5) };
        bd.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        bd.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(_back);
        var div = new Border { Width = 0.5, Margin = new Thickness(0, 5, 0, 5) };
        div.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        sp.Children.Add(div);
        sp.Children.Add(_fwd);
        bd.Child = sp;
        var g = new Grid { Height = 24 };
        g.Children.Add(under);
        g.Children.Add(bd);
        return g;
    }

    /// <summary>A bordered toolbar button with a menu: its symbol, and a small arrow down.</summary>
    static void Bordered(Button b, FrameworkElement icon, string tip)
    {
        b.Style = Res("MacButton");
        b.Height = 24;
        b.MinWidth = 0;
        b.Padding = new Thickness(7, 0, 6, 0);
        b.Focusable = false;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.down", Width = 8, Height = 8, StrokeWidth = 2.8, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        b.Content = sp;
        b.ToolTip = tip;
    }

    // ------------------------------------------------------------------ along the bottom

    void BuildBottom()
    {
        _bar.ColumnDefinitions.Add(new ColumnDefinition());
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Draggable(_bar);
        _optionsBtn.Style = Res("MacButton");
        _optionsBtn.Height = 24;
        _optionsBtn.Margin = new Thickness(0, 0, 12, 0);
        _optionsBtn.Focusable = false;
        _optionsBtn.Click += (_, _) =>
        {
            _showOptions = !_showOptions;
            Settings.Current.FilePanelShowOptions = _showOptions;
            Settings.Save(notify: false);
            Arrange();
        };
        _barLeft.Children.Add(_optionsBtn);
        _hideExt.Style = Res("MacCheckBox");
        _hideExt.Content = "Hide extension";
        _hideExt.VerticalAlignment = VerticalAlignment.Center;
        _hideExt.Margin = new Thickness(0, 0, 14, 0);
        _hideExt.Focusable = false;
        _hideExt.Checked += (_, _) => HideExtension(true);
        _hideExt.Unchecked += (_, _) => HideExtension(false);
        _barLeft.Children.Add(_hideExt);
        _newFolder.Style = Res("MacButton");
        _newFolder.Content = "New Folder";
        _newFolder.Height = 24;
        _newFolder.Focusable = false;
        _newFolder.Click += (_, _) => NewFolder();
        _barLeft.Children.Add(_newFolder);
        _bar.Children.Add(_barLeft);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        _cancel.Style = Res("MacButton");
        _cancel.Content = "Cancel";
        _cancel.MinWidth = 72;
        _cancel.Height = 24;
        _cancel.Margin = new Thickness(0, 0, 12, 0);
        _cancel.Focusable = false;
        _cancel.Click += (_, _) => Answer(null);
        _ok.Style = Res("MacDefaultButton");
        _ok.Content = Prompt();
        _ok.MinWidth = 72;
        _ok.Height = 24;
        _ok.Focusable = false;
        _ok.Click += (_, _) => Choose();
        right.Children.Add(_cancel);
        right.Children.Add(_ok);
        Grid.SetColumn(right, 1);
        _bar.Children.Add(right);

        BuildOptions();
        _compactFoot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _compactStack };
        _compactFoot.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        _compactFoot.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
    }

    /// <summary>The app's options, as a Mac panel's accessory: the kinds of file it takes (Format), and - for another
    /// app's dialog - its own Windows dialog, for options of its own.</summary>
    void BuildOptions()
    {
        var dock = new DockPanel { LastChildFill = true, MinHeight = 24 };
        if (_req.OfferWindowsDialog && HasFormats)
        {
            var t = new TextBlock { Text = "Show Windows Dialog", FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            var link = new Button { Style = Res("PlainButton"), Content = t, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Focusable = false, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Use the app’s own Windows file dialog instead – for options of its own" };
            link.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => t.TextDecorations = null;
            link.Click += (_, _) => Answer(new FilePanelResult { UseWindowsDialog = true });
            DockPanel.SetDock(link, Dock.Right);
            dock.Children.Add(link);
        }
        if (HasFormats)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = "Format:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            sp.Children.Add(label);
            _filterBtn.Style = Res("MacPopupButton");
            _filterBtn.Height = 22;
            _filterBtn.MinWidth = 200;
            _filterBtn.MaxWidth = 330;
            _filterBtn.Focusable = false;
            _filterText.Text = _req.Filters[_filter];
            _filterBtn.Content = PopupContent(null, _filterText);
            _filterBtn.Click += (_, _) => FilterMenu();
            sp.Children.Add(_filterBtn);
            dock.Children.Add(sp);
        }
        _options.Child = dock;
        _options.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
    }

    string Prompt()
    {
        string p = (_req.Prompt ?? "").Replace("&", "").Trim();
        if (p.Length > 0) return p;
        return _req.Save ? "Save" : _req.Folders ? "Choose" : "Open";
    }

    /// <summary>Lays the panel out for what it is now: an Open panel; or Save, small (name, tags, where) or opened out
    /// (the folders too). The same buttons and options, moved to suit.</summary>
    void Arrange()
    {
        Detach(_options);
        Detach(_bar);
        var stack = Compact ? _compactStack : _footStack;
        stack.Children.Add(_options);
        stack.Children.Add(_bar);
        _options.Padding = Compact ? new Thickness(16, 14, 16, 14) : new Thickness(16, 12, 16, 0);
        _options.BorderThickness = new Thickness(0, 0, 0, Compact ? 1 : 0);
        _body.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        _compactFoot.Visibility = Compact ? Visibility.Visible : Visibility.Collapsed;
        _options.Visibility = HasOptions && _showOptions ? Visibility.Visible : Visibility.Collapsed;
        _optionsBtn.Visibility = !_req.Save && HasOptions ? Visibility.Visible : Visibility.Collapsed;
        _optionsBtn.Content = _showOptions ? "Hide Options" : "Show Options";
        _newFolder.Visibility = _req.Folders || (_req.Save && !Compact) ? Visibility.Visible : Visibility.Collapsed;
        _hideExt.Visibility = _req.Save && !Compact ? Visibility.Visible : Visibility.Collapsed;
        if (_req.Save)
        {
            // small: the open-out button beside Where; opened out (no Where - the files show where): beside the name
            Detach(_expand);
            if (Compact) { DockPanel.SetDock(_expand, Dock.Right); _whereRow.Children.Insert(0, _expand); }
            else { Grid.SetRow(_expand, 0); Grid.SetColumn(_expand, 2); _saveGrid.Children.Add(_expand); }
            _expand.Margin = new Thickness(7, 0, 0, 0);
            ((SymbolIcon)_expand.Content).Symbol = Compact ? "chevron.down" : "chevron.up";
            _expand.ToolTip = Compact ? "Show the folders" : "Hide the folders";
            _whereLabel.Visibility = _whereRow.Visibility = Compact ? Visibility.Visible : Visibility.Collapsed;
            _saveHead.BorderThickness = new Thickness(0, 0, 0, Compact ? 0 : 1);
        }
    }

    static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }

    // ------------------------------------------------------------------ sidebar

    /// <summary>Finder's sidebar: Favorites, iCloud, Locations, Tags. (Saving: only places a file can go.)</summary>
    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _sideRows.Clear();
        Section("Favorites");
        if (!_req.Save)
        {
            SideRow("Recents", "clock", FinderLocation.Recents);
            SideRow("Applications", "apps", FinderLocation.Applications);
        }
        SideRow("Desktop", "desktop", FinderLocation.Desktop);
        SideRow("Documents", "doc", FinderLocation.Documents);
        SideRow("Downloads", "arrow.down.circle", FinderLocation.Downloads);
        SideRow(Environment.UserName, "house", FinderLocation.Home);
        foreach (var fav in Settings.Current.SidebarFavorites ?? new List<string>())
            if (Directory.Exists(fav)) SideRow(Path.GetFileName(fav.TrimEnd('\\')), "folder", fav);
        var (icloud, onedrive) = Clouds();
        if (icloud != null || onedrive != null)
        {
            Section("iCloud");
            if (icloud != null) SideRow("iCloud Drive", "icloud", icloud);
            if (onedrive != null) SideRow("OneDrive", "icloud", onedrive);
        }
        Section("Locations");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                SideRow(FinderLocation.DisplayName(d.RootDirectory.FullName), d.DriveType is DriveType.Removable or DriveType.Network ? "externaldrive" : "internaldrive", d.RootDirectory.FullName);
            }
            catch { }
        }
        if (!_req.Save)
        {
            Section("Tags");
            foreach (var (id, name, color) in Theme.TagColors)
            {
                var dot = new Shapes.Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(color), Margin = new Thickness(3, 0, 3, 0) };
                AddSideRow(dot, name, FinderLocation.TagPrefix + id);
            }
        }
    }

    static (string icloud, string onedrive) Clouds()
    {
        string icloud = Path.Combine(FinderLocation.Home, "iCloudDrive");
        return (Directory.Exists(icloud) ? icloud : null, FinderLocation.ICloud != null && Directory.Exists(FinderLocation.ICloud) ? FinderLocation.ICloud : null);
    }

    void Section(string title)
    {
        var t = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(18, _sidebar.Children.Count == 0 ? 0 : 14, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _sidebar.Children.Add(t);
    }

    void SideRow(string text, string sym, string loc)
    {
        var icon = new SymbolIcon { Symbol = sym, Width = 17, Height = 17, StrokeWidth = 1.8 };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "IconTintBrush");
        AddSideRow(icon, text, loc);
    }

    void AddSideRow(FrameworkElement icon, string text, string loc)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        icon.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(icon);
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(tb);
        var row = new Border { Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(8, 0, 6, 0), Background = Brushes.Transparent, Child = sp };
        row.MouseLeftButtonUp += (_, _) => { _search.Text = ""; Navigate(loc); };
        _sideRows[loc] = row;
        _sidebar.Children.Add(row);
    }

    void HighlightSidebar()
    {
        foreach (var (key, row) in _sideRows)
        {
            bool on = string.Equals(key.TrimEnd('\\'), (_loc ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && _search.Text.Length == 0;
            if (on) row.SetResourceReference(Border.BackgroundProperty, "SidebarSelectionActiveBrush");
            else row.Background = Brushes.Transparent;
        }
    }

    void ToggleSidebar()
    {
        bool shown = _sideCol.ActualWidth > 1;
        _sideCol.Width = new GridLength(shown ? 0 : SidebarWidth);
    }

    // ------------------------------------------------------------------ menus

    static void ShowMenu(FrameworkElement under, IEnumerable<object> items)
    {
        var cm = Mb.Context(items.ToArray());
        cm.PlacementTarget = under;
        cm.Placement = PlacementMode.Bottom;
        cm.HorizontalOffset = -14;
        cm.VerticalOffset = -2;
        cm.IsOpen = true;
    }

    /// <summary>A place's icon, as Finder shows it: a drive; a blue folder (with its glyph, for the usual ones); a tag's
    /// colour; or the symbol of a list such as Recents.</summary>
    static FrameworkElement PlaceIcon(string loc, double size = 16)
    {
        if (loc == null)
        {
            var mag = new SymbolIcon { Symbol = "magnifyingglass", Width = size - 2, Height = size - 2, StrokeWidth = 2 };
            mag.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
            return mag;
        }
        if (loc.StartsWith(FinderLocation.TagPrefix))
        {
            var color = Theme.TagColors.FirstOrDefault(t => t.id == loc[FinderLocation.TagPrefix.Length..]).color;
            return new Shapes.Ellipse { Width = size - 5, Height = size - 5, Fill = new SolidColorBrush(color) };
        }
        if (FinderLocation.IsVirtual(loc))
        {
            string sym = loc == FinderLocation.Recents ? "clock" : loc == FinderLocation.Applications ? "apps" : loc == FinderLocation.Computer ? "laptop" : "folder";
            var si = new SymbolIcon { Symbol = sym, Width = size, Height = size, StrokeWidth = 1.8 };
            si.SetResourceReference(SymbolIcon.ForegroundProperty, "IconTintBrush");
            return si;
        }
        bool drive = loc.TrimEnd('\\').Length <= 2;
        var img = new Image { Source = drive ? MacIcons.Drive() : MacIcons.Folder(MacIcons.FolderGlyphFor(loc)), Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    static bool Same(string a, string b) => a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The path pop-up's menu, as a Mac's: the folder and each folder it's in, down to the computer; then the
    /// places used lately.</summary>
    void PathMenu()
    {
        var items = new List<object>();
        string folder = CurrentFolder;
        if (folder != null && !FinderLocation.IsVirtual(folder))
        {
            for (var d = new DirectoryInfo(folder); d != null; d = d.Parent)
            {
                string p = d.FullName;
                items.Add(Mb.Item(FinderLocation.DisplayName(p), () => Navigate(p), isChecked: Same(p, folder), icon: PlaceIcon(p)));
            }
            items.Add(Mb.Item(FinderLocation.DisplayName(FinderLocation.Computer), () => Navigate(FinderLocation.Computer), icon: PlaceIcon(FinderLocation.Computer)));
        }
        else items.Add(Mb.Item(FinderLocation.DisplayName(folder), null, isChecked: true, icon: PlaceIcon(folder)));
        var recent = Settings.Current.FilePanelRecentFolders.Where(r => !Same(r, folder) && Directory.Exists(r)).Take(6).ToList();
        if (recent.Count > 0)
        {
            items.Add(Mb.Sep());
            items.Add(Mb.SectionHeader("Recent Places"));
            foreach (var r in recent) { string l = r; items.Add(Mb.Item(FinderLocation.DisplayName(r), () => Navigate(l), icon: PlaceIcon(r))); }
        }
        ShowMenu(_pathBtn, items);
    }

    /// <summary>Where (a small Save panel): the folder it's going in, the Finder sidebar's places (but Recents and
    /// Applications), iCloud, the drives, and the places used lately.</summary>
    void WhereMenu()
    {
        string cur = SaveFolder();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var places = new List<object>();
        void Place(string name, string path)
        {
            if (path == null || !Directory.Exists(path) || !listed.Add(path.TrimEnd('\\'))) return;
            places.Add(Mb.Item(name, () => Navigate(path), isChecked: Same(path, cur), icon: PlaceIcon(path)));
        }
        places.Add(Mb.SectionHeader("Favorites"));
        Place("Desktop", FinderLocation.Desktop);
        Place("Documents", FinderLocation.Documents);
        Place("Downloads", FinderLocation.Downloads);
        Place(Environment.UserName, FinderLocation.Home);
        foreach (var fav in Settings.Current.SidebarFavorites ?? new List<string>()) Place(Path.GetFileName(fav.TrimEnd('\\')), fav);
        var (icloud, onedrive) = Clouds();
        if (icloud != null || onedrive != null)
        {
            places.Add(Mb.SectionHeader("iCloud"));
            Place("iCloud Drive", icloud);
            Place("OneDrive", onedrive);
        }
        places.Add(Mb.SectionHeader("Locations"));
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady) Place(FinderLocation.DisplayName(d.RootDirectory.FullName), d.RootDirectory.FullName); } catch { }
        }
        var recent = Settings.Current.FilePanelRecentFolders.Where(r => Directory.Exists(r) && !listed.Contains(r.TrimEnd('\\'))).Take(6).ToList();
        if (recent.Count > 0)
        {
            places.Add(Mb.Sep());
            places.Add(Mb.SectionHeader("Recent Places"));
            foreach (var r in recent) Place(FinderLocation.DisplayName(r), r);
        }
        var items = new List<object>();
        if (cur != null && !listed.Contains(cur.TrimEnd('\\')))
        {
            items.Add(Mb.Item(FinderLocation.DisplayName(cur), null, isChecked: true, icon: PlaceIcon(cur)));
            items.Add(Mb.Sep());
        }
        items.AddRange(places);
        ShowMenu(_whereBtn, items);
    }

    IEnumerable<object> ViewMenuItems() => new object[]
    {
        Mb.Item("as Icons", () => SetViewMode("icons"), "⌘1", isChecked: _viewMode == "icons"),
        Mb.Item("as List", () => SetViewMode("list"), "⌘2", isChecked: _viewMode == "list"),
        Mb.Item("as Columns", () => SetViewMode("columns"), "⌘3", isChecked: _viewMode == "columns"),
        Mb.Item("as Gallery", () => SetViewMode("gallery"), "⌘4", isChecked: _viewMode == "gallery"),
        Mb.Sep(),
        Mb.Item(_sideCol.ActualWidth > 1 ? "Hide Sidebar" : "Show Sidebar", ToggleSidebar, "⌥⌘S"),
        Mb.Item(Settings.Current.ShowHiddenFiles ? "Hide Hidden Files" : "Show Hidden Files", ToggleHidden, "⇧⌘."),
    };

    IEnumerable<object> SortMenuItems() => new object[]
    {
        Mb.SectionHeader("Sort By"),
        Mb.Item("Name", () => SortBy("name", true), isChecked: _sortKey == "name"),
        Mb.Item("Kind", () => SortBy("kind", true), isChecked: _sortKey == "kind"),
        Mb.Item("Date Modified", () => SortBy("date", true), isChecked: _sortKey == "date"),
        Mb.Item("Size", () => SortBy("size", true), isChecked: _sortKey == "size"),
        Mb.Sep(),
        Mb.Item("Keep Folders on Top", () => { Settings.Current.FinderFoldersOnTop = !Settings.Current.FinderFoldersOnTop; Settings.Save(); Rebuild(); }, isChecked: Settings.Current.FinderFoldersOnTop),
    };

    void FilterMenu()
    {
        var items = new List<object>();
        for (int i = 0; i < _req.Filters.Count; i++)
        {
            int idx = i;
            items.Add(Mb.Item(_req.Filters[i], () => SetFilter(idx), isChecked: i == _filter));
        }
        ShowMenu(_filterBtn, items);
    }

    void SetFilter(int idx)
    {
        _filter = idx;
        _patterns = Patterns(_req.Filters[idx]);
        _filterText.Text = _req.Filters[idx];
        // Save: the name takes the new kind's extension
        if (_req.Save && ConcreteExtension(_patterns) is string ext)
        {
            if (_hideExt.IsChecked == true) _hiddenExt = ext;
            else
            {
                string n = _name.Text.Trim();
                string bare = Path.GetExtension(n).Length > 0 ? n[..^Path.GetExtension(n).Length] : n;
                _name.Text = bare + ext;
            }
        }
        ApplyEnabled(_all);
        foreach (var kids in _children.Values) ApplyEnabled(kids);
        UpdateOk();
    }

    void ToggleHidden()
    {
        Settings.Current.ShowHiddenFiles = !Settings.Current.ShowHiddenFiles;
        Settings.Save();
        Reload();
    }

    // ================================================================== sheets

    /// <summary>A sheet over the panel, as a Mac panel's own questions drop from its top: what's under it waits until
    /// it's answered. Sheets stack (a question about the New Folder sheet's name drops over it).</summary>
    void Sheet(FrameworkElement content, double width, Action<KeyEventArgs> keys, FrameworkElement focus)
    {
        if (_sheets.Count == 0)
        {
            var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)) };
            shade.MouseLeftButtonDown += (_, e) => e.Handled = true;
            shade.MouseRightButtonUp += (_, e) => e.Handled = true;
            _sheetHost.Children.Add(shade);
        }
        var card = new Border
        {
            Width = width, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 10),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0.5), Child = content,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.3 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        var slide = new TranslateTransform(0, -18);
        card.RenderTransform = slide;
        _sheets.Add((card, keys, Keyboard.FocusedElement));
        _sheetHost.Children.Add(card);
        _sheetHost.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        if (focus != null) Dispatcher.BeginInvoke(() => { focus.Focus(); if (focus is TextBox tb) tb.SelectAll(); }, DispatcherPriority.Input);
        else Dispatcher.BeginInvoke(() => { card.Focusable = true; card.Focus(); }, DispatcherPriority.Input);
    }

    void CloseSheet()
    {
        if (_sheets.Count == 0) return;
        var (card, _, before) = _sheets[^1];
        _sheets.RemoveAt(_sheets.Count - 1);
        _sheetHost.Children.Remove(card);
        if (_sheets.Count == 0)
        {
            _sheetHost.Children.Clear();
            _sheetHost.Visibility = Visibility.Collapsed;
        }
        if (before is UIElement u && u.IsVisible) u.Focus();
        else if (_req.Save) _name.Focus();
        else _view?.FocusView();
    }

    static TextBlock SheetText(string s, double size, bool bold = false, string brush = "LabelBrush", TextAlignment align = TextAlignment.Left)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextAlignment = align };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>The yellow caution sign of a Mac alert.</summary>
    static FrameworkElement CautionIcon()
    {
        var g = new Grid { Width = 54, Height = 54, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        g.Children.Add(new Shapes.Path
        {
            Data = Geometry.Parse("M27,6 L50,47 L4,47 Z"), Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x2C)), Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x2C)),
            StrokeThickness = 6, StrokeLineJoin = PenLineJoin.Round,
        });
        g.Children.Add(new Shapes.Path { Data = Geometry.Parse("M27,19 L27,33"), Stroke = Brushes.White, StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        g.Children.Add(new Shapes.Ellipse { Width = 5.5, Height = 5.5, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24.25, 37.5, 0, 0) });
        return g;
    }

    /// <summary>An alert, as a sheet over the panel (Big Sur's: the sign, a bold question, a few words, the buttons side
    /// by side - the default one blue, on the right).</summary>
    void SheetAlert(string title, string message, string[] buttons, int defaultButton, Action<string> done)
    {
        var sp = new StackPanel { Margin = new Thickness(18, 20, 18, 16) };
        sp.Children.Add(CautionIcon());
        sp.Children.Add(SheetText(title, 13, true, align: TextAlignment.Center));
        if (!string.IsNullOrWhiteSpace(message))
        {
            var m = SheetText(message, 11, align: TextAlignment.Center);
            m.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(m);
        }
        var row = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        var made = new List<Button>();
        for (int i = 0; i < buttons.Length; i++)
        {
            string label = buttons[i];
            var b = new Button { Content = label, Style = Res(i == defaultButton ? "MacDefaultButton" : "MacButton"), Height = 24, MinWidth = 0, Focusable = false };
            b.Click += (_, _) => { CloseSheet(); done?.Invoke(label); };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            b.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 0);
            Grid.SetColumn(b, i);
            row.Children.Add(b);
            made.Add(b);
        }
        sp.Children.Add(row);
        void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Sheet(sp, 272, e =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Press(made.FirstOrDefault(b => (string)b.Content == "Cancel") ?? made[defaultButton]); }
            else if (e.Key == Key.Enter) { e.Handled = true; Press(made[defaultButton]); }
        }, null);
    }

    /// <summary>New Folder, as a sheet: its name ("untitled folder", chosen to be typed over); made in the folder the
    /// panel is in, which then goes into it.</summary>
    void NewFolder()
    {
        string parent = _req.Save ? SaveFolder() : CurrentFolder;
        if (parent == null || FinderLocation.IsVirtual(parent) || !Directory.Exists(parent)) return;
        var box = new TextBox { Style = Res("MacTextField"), Text = "untitled folder" };
        var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        sp.Children.Add(SheetText("New Folder", 13, true));
        var label = SheetText("Name of new folder:", 12, brush: "SecondaryLabelBrush");
        label.Margin = new Thickness(0, 8, 0, 6);
        sp.Children.Add(label);
        sp.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Style = Res("MacButton"), Content = "Cancel", MinWidth = 72, Height = 24, Margin = new Thickness(0, 0, 12, 0), Focusable = false };
        var create = new Button { Style = Res("MacDefaultButton"), Content = "Create", MinWidth = 72, Height = 24, Focusable = false };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        sp.Children.Add(buttons);
        void Make()
        {
            string name = box.Text.Trim();
            if (name.Length == 0) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.').Length == 0)
            {
                SheetAlert($"The name “{name}” can’t be used.", "Try using a name with fewer characters, or with no punctuation marks.", new[] { "OK" }, 0, null);
                return;
            }
            string path = Path.Combine(parent, name);
            if (Directory.Exists(path) || File.Exists(path))
            {
                SheetAlert($"The name “{name}” is already taken.", "Please choose a different name.", new[] { "OK" }, 0, null);
                return;
            }
            try { Directory.CreateDirectory(path); }
            catch (Exception ex) { SheetAlert("The folder couldn’t be made.", ex.Message, new[] { "OK" }, 0, null); return; }
            CloseSheet();
            Navigate(path);
        }
        cancel.Click += (_, _) => CloseSheet();
        create.Click += (_, _) => Make();
        box.TextChanged += (_, _) => create.IsEnabled = box.Text.Trim().Length > 0;
        Sheet(sp, 340, e =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; CloseSheet(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Make(); }
        }, box);
    }

    /// <summary>Go to Folder (⇧⌘G, or typing / or ~), as a sheet: a path typed (~ is the home folder, / the top of the
    /// system drive) with the folders it could go on to listed under it - arrows pick, Tab completes, Return goes.</summary>
    void GoToFolder(string start)
    {
        var box = new TextBox { Style = Res("MacTextField"), Height = 24, Text = start ?? DefaultGoText() };
        var list = new StackPanel();
        var hits = new List<string>();
        int pick = -1;
        void Render()
        {
            list.Children.Clear();
            list.Margin = new Thickness(0, hits.Count > 0 ? 8 : 0, 0, 0);
            for (int i = 0; i < hits.Count; i++)
            {
                string p = hits[i];
                bool on = i == pick;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
                var icon = PlaceIcon(p);
                icon.Margin = new Thickness(0, 0, 8, 0);
                sp.Children.Add(icon);
                var t = new TextBlock { Text = p, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
                if (on) t.Foreground = Brushes.White; else t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
                sp.Children.Add(t);
                var row = new Border { Height = 24, CornerRadius = new CornerRadius(5), Child = sp, Background = Brushes.Transparent, Cursor = Cursors.Arrow };
                if (on) row.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                row.MouseLeftButtonUp += (_, _) => Go(p);
                list.Children.Add(row);
            }
        }
        void Refresh() { hits = Suggest(box.Text); pick = -1; Render(); }
        box.TextChanged += (_, _) => Refresh();
        var field = new DockPanel();
        var fi = new SymbolIcon { Symbol = "folder", Width = 17, Height = 17, StrokeWidth = 1.8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        fi.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        DockPanel.SetDock(fi, Dock.Left);
        field.Children.Add(fi);
        field.Children.Add(box);
        var sp2 = new StackPanel { Margin = new Thickness(14, 14, 14, 14) };
        sp2.Children.Add(SheetText("Go to Folder", 13, true));
        field.Margin = new Thickness(0, 10, 0, 0);
        sp2.Children.Add(field);
        sp2.Children.Add(list);
        Refresh();
        Sheet(sp2, 470, e =>
        {
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; CloseSheet(); break;
                case Key.Enter: e.Handled = true; Go(pick >= 0 && pick < hits.Count ? hits[pick] : box.Text); break;
                case Key.Down when hits.Count > 0: e.Handled = true; pick = (pick + 1) % hits.Count; Render(); break;
                case Key.Up when hits.Count > 0: e.Handled = true; pick = pick <= 0 ? hits.Count - 1 : pick - 1; Render(); break;
                case Key.Tab when hits.Count > 0:
                    e.Handled = true;
                    box.Text = hits[Math.Max(0, pick)].TrimEnd('\\') + "\\";
                    box.CaretIndex = box.Text.Length;
                    break;
            }
        }, box);
        if (start != null) Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }, DispatcherPriority.Input + 1);
    }

    string DefaultGoText() => CurrentFolder is string f && !FinderLocation.IsVirtual(f) ? f : "";

    void Go(string typed)
    {
        string p = ResolveTyped(typed);
        if (p.Length > 0 && Directory.Exists(p)) { CloseSheet(); Navigate(p.Length == 2 && p[1] == ':' ? p + "\\" : p); }
        else if (p.Length > 0 && File.Exists(p)) { CloseSheet(); _pendingSelect = p; Navigate(Path.GetDirectoryName(p)); }
        else SheetAlert("The folder can’t be found.", $"“{typed}”", new[] { "OK" }, 0, null);
    }

    /// <summary>A typed path made real: ~ the home folder, / the top of the system drive (as on a Mac), %VARIABLES%.</summary>
    static string ResolveTyped(string typed)
    {
        string t = (typed ?? "").Trim().Trim('"');
        if (t.Length == 0) return t;
        if (t.StartsWith("~")) t = FinderLocation.Home + "\\" + t[1..].TrimStart('/', '\\');
        else if (t.StartsWith("/") && !t.StartsWith("//")) t = Path.GetPathRoot(Environment.SystemDirectory) + t[1..];
        t = Environment.ExpandEnvironmentVariables(t.Replace('/', '\\'));
        return t;
    }

    /// <summary>The folders a typed path could go on to: those in its folder whose names start with what's after the last
    /// slash (nothing typed: the folders used lately).</summary>
    static List<string> Suggest(string typed)
    {
        string t = ResolveTyped(typed);
        if (t.Length == 0) return Settings.Current.FilePanelRecentFolders.Where(Directory.Exists).Take(8).ToList();
        var result = new List<string>();
        try
        {
            string dir, prefix;
            if (t.EndsWith("\\")) { dir = t; prefix = ""; }
            else { dir = Path.GetDirectoryName(t); prefix = Path.GetFileName(t); }
            if (dir == null || !Directory.Exists(dir)) return result;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories(prefix + "*", opts))
            {
                result.Add(d.FullName);
                if (result.Count >= 40) break;
            }
            result.Sort((a, b) => NativeMethods.StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
        }
        catch { }
        return result.Take(8).ToList();
    }

    // ================================================================== kinds of file

    /// <summary>The patterns in a filter's name ("Images (*.png;*.jpg)" → *.png, *.jpg); none: everything.</summary>
    public static string[] Patterns(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return new[] { "*" };
        var m = Regex.Match(filter, @"\(([^()]*\*[^()]*)\)\s*$");
        string inner = m.Success ? m.Groups[1].Value : filter.Contains('*') ? filter : "";
        var pats = inner.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(p => p.Contains('*')).ToArray();
        return pats.Length > 0 ? pats : new[] { "*" };
    }

    static string ConcreteExtension(string[] pats)
    {
        var p = pats.FirstOrDefault();
        if (p == null || !p.StartsWith("*.") || p.IndexOf('*', 2) >= 0 || p == "*.*") return null;
        return p[1..];
    }

    bool Matches(FileItem it)
    {
        if (it.IsFolder || it.IsDrive) return true;
        string name = it.IsApp ? Path.GetFileName(it.FullPath ?? "") : it.Name ?? "";
        foreach (var p in _patterns)
            if (p is "*" or "*.*" || FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)) return true;
        return false;
    }

    /// <summary>What can be chosen: files of the kind asked for (an app, by its program), or folders when folders are
    /// asked for.</summary>
    bool Choosable(FileItem it)
    {
        if (_req.Folders) return (it.IsFolder || it.IsDrive) && !it.IsApp;
        if (it.IsFolder || it.IsDrive) return false;
        if (it.IsApp && !File.Exists(it.FullPath)) return false;
        return Matches(it);
    }

    /// <summary>Greys out what can't be chosen (folders stay: they're gone into).</summary>
    void ApplyEnabled(IEnumerable<FileItem> items)
    {
        foreach (var it in items)
            it.IsDisabled = !(it.IsFolder || it.IsDrive) && (_req.Save || !Choosable(it));
    }

    // ================================================================== moving around

    string CurrentFolder => _view is ColumnView cv && _viewMode == "columns" && cv.ActiveFolder != null ? cv.ActiveFolder : _loc;

    void Navigate(string loc, bool addHistory = true)
    {
        if (loc == null) return;
        if (addHistory && _loc != null && !Same(_loc, loc)) { _backStack.Add(_loc); _fwdStack.Clear(); }
        _loc = loc;
        _children.Clear();
        if (_search.Text.Length > 0) _search.Text = "";   // (its timer reloads too: harmless)
        Reload();
        UpdateChrome();
    }

    void GoBack()
    {
        if (_backStack.Count == 0) return;
        _fwdStack.Add(_loc);
        string prev = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        Navigate(prev, false);
    }

    void GoForward()
    {
        if (_fwdStack.Count == 0) return;
        _backStack.Add(_loc);
        string next = _fwdStack[^1];
        _fwdStack.RemoveAt(_fwdStack.Count - 1);
        Navigate(next, false);
    }

    void GoUp()
    {
        string folder = CurrentFolder;
        if (folder == null || FinderLocation.IsVirtual(folder)) return;
        var parent = Directory.GetParent(folder.TrimEnd('\\'));
        _pendingSelect = folder;
        Navigate(parent != null ? parent.FullName : FinderLocation.Computer);
    }

    void UpdateChrome()
    {
        _back.IsEnabled = _backStack.Count > 0;
        _fwd.IsEnabled = _fwdStack.Count > 0;
        string folder = CurrentFolder;
        bool searching = _search.Text.Length > 0;
        _pathText.Text = searching ? $"Searching “{_search.Text}”" : FinderLocation.DisplayName(folder);
        _pathIcon.Child = PlaceIcon(searching ? null : folder);
        string where = SaveFolder();
        _whereText.Text = where != null ? FinderLocation.DisplayName(where) : "Choose a folder";
        _whereIcon.Child = PlaceIcon(where ?? FinderLocation.Documents);
        _viewIcon.Symbol = _viewMode switch { "icons" => "grid", "columns" => "columns", "gallery" => "gallery", _ => "list" };
        HighlightSidebar();
        UpdateOk();
    }

    void Reload()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        string loc = _loc, q = _search.Text.Trim();
        bool hidden = Settings.Current.ShowHiddenFiles;
        Task.Run(() =>
        {
            if (q.Length > 0) return (FinderWindow.SearchIndex(q, 400), (string)null);
            if (loc == FinderLocation.Applications)
                return (AppCatalog.Apps.Select(a => new FileItem { Name = a.Name, IsApp = true, AppTarget = a.ParsingName, FullPath = a.TargetPath ?? ("shell:AppsFolder\\" + a.ParsingName) }).ToList(), null);
            return FinderWindow.LoadLocation(loc, hidden, cts.Token);
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                _all = t.Result.Item1 ?? new List<FileItem>();
                ApplyEnabled(_all);
                _empty.Text = t.Result.Item2 ?? (q.Length > 0 ? "No Results" : "");
                _empty.Visibility = _all.Count == 0 && _empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                Rebuild(scrollTop: true);
                if (_pendingSelect != null)
                {
                    var target = _display.FirstOrDefault(i => Same(i.FullPath, _pendingSelect));
                    _pendingSelect = null;
                    if (target != null) { SelectOnly(target); Dispatcher.BeginInvoke(() => _view.Reveal(target), DispatcherPriority.Loaded); }
                }
                UpdateChrome();
            });
        });
    }

    void Rebuild(bool scrollTop = false)
    {
        var flat = new List<FileItem>();
        void Add(IEnumerable<FileItem> list)
        {
            foreach (var it in list)
            {
                flat.Add(it);
                if (it.IsExpanded && _viewMode == "list" && _children.TryGetValue(it.FullPath, out var kids)) Add(Sort(kids));
            }
        }
        Add(Sort(_all));
        _display = flat;
        if (_view is ColumnView cv) cv.RootFolder = _loc;
        _view.Show(_display);
        if (scrollTop) _view.ScrollToTop();
        SelectionChanged();
    }

    List<FileItem> Sort(IEnumerable<FileItem> items)
    {
        Comparison<FileItem> cmp = _sortKey switch
        {
            "kind" => (a, b) => { int c = string.Compare(a.Kind, b.Kind, StringComparison.CurrentCultureIgnoreCase); return c != 0 ? c : NativeMethods.StrCmpLogicalW(a.Name, b.Name); },
            "date" => (a, b) => { int c = b.Modified.CompareTo(a.Modified); return c != 0 ? c : NativeMethods.StrCmpLogicalW(a.Name, b.Name); },
            "size" => (a, b) => { int c = b.Size.CompareTo(a.Size); return c != 0 ? c : NativeMethods.StrCmpLogicalW(a.Name, b.Name); },
            _ => (a, b) => NativeMethods.StrCmpLogicalW(a.Name ?? "", b.Name ?? ""),
        };
        var list = items.ToList();
        list.Sort((a, b) =>
        {
            if (Settings.Current.FinderFoldersOnTop && a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int c = cmp(a, b);
            return _sortAsc ? c : -c;
        });
        return list;
    }

    void SetViewMode(string mode, bool save = true)
    {
        _viewMode = mode;
        if (_view != null) _viewHost.Children.Remove(_view.Root);
        _view = mode switch
        {
            "icons" => _icons ??= new IconView(this),
            "columns" => _columns ??= new ColumnView(this),
            "gallery" => _gallery ??= new GalleryView(this),
            _ => _list ??= new ListView2(this),
        };
        _viewHost.Children.Insert(0, _view.Root);
        if (save) { Settings.Current.FilePanelView = mode; Settings.Save(notify: false); }
        if (_loc != null) Rebuild(scrollTop: true);
        _view.Active = true;
        _viewIcon.Symbol = mode switch { "icons" => "grid", "columns" => "columns", "gallery" => "gallery", _ => "list" };
    }

    // ================================================================== choosing

    List<FileItem> Scope => (_view?.CurrentItems ?? _display).ToList();
    List<FileItem> SelectedItems => Scope.Where(i => i.IsSelected).ToList();

    void SelectOnly(FileItem item)
    {
        foreach (var i in Scope) if (i != item) i.IsSelected = false;
        if (item != null) item.IsSelected = true;
        _anchor = item;
        SelectionChanged();
    }

    void SelectionChanged()
    {
        _view?.OnSelectionChanged();
        if (_viewMode == "list")
            for (int i = 0; i < _display.Count; i++)
            {
                var it = _display[i];
                if (!it.IsSelected) continue;
                bool prev = i > 0 && _display[i - 1].IsSelected, next = i < _display.Count - 1 && _display[i + 1].IsSelected;
                it.SelCorner = new CornerRadius(prev ? 0 : 5, prev ? 0 : 5, next ? 0 : 5, next ? 0 : 5);
            }
        UpdateOk();
    }

    /// <summary>Save: a (greyed) file clicked gives its name - keeping the extension being saved as.</summary>
    void TakeName(FileItem item)
    {
        string ext = _hideExt.IsChecked == true ? "" : Path.GetExtension(_name.Text.Trim());
        string bare = Path.GetFileNameWithoutExtension(item.Name ?? "");
        _name.Text = ext.Length > 0 || _hideExt.IsChecked == true ? bare + ext : item.Name;
        _name.Focus();
        SelectBaseName();
    }

    void UpdateOk()
    {
        if (_req.Save) _ok.IsEnabled = _name.Text.Trim().Length > 0 && SaveFolder() != null;
        else if (_req.Folders) _ok.IsEnabled = SelectedItems.Any(Choosable) || (CurrentFolder != null && Directory.Exists(CurrentFolder));
        else _ok.IsEnabled = SelectedItems.Any(i => Choosable(i) || i.IsFolder || i.IsDrive);
    }

    /// <summary>Save: the folder it goes in - the one selected among the files, else the one shown (null: none - a
    /// list like Recents).</summary>
    string SaveFolder()
    {
        string f = CurrentFolder;
        if (_req.Save && !Compact && SelectedItems is { Count: 1 } s && (s[0].IsFolder || s[0].IsDrive) && Directory.Exists(s[0].FullPath)) f = s[0].FullPath;
        return f != null && !FinderLocation.IsVirtual(f) && Directory.Exists(f) ? f : null;
    }

    void Choose()
    {
        if (!_ok.IsEnabled || _sheets.Count > 0) return;
        if (_req.Save) { ChooseSave(); return; }
        var sel = SelectedItems;
        var picks = sel.Where(Choosable).ToList();
        // a folder chosen in an Open panel: go into it (Open works on folders as on files)
        if (!_req.Folders && picks.Count == 0 && sel.FirstOrDefault(i => i.IsFolder || i.IsDrive) is { } dir) { Navigate(dir.FullPath); return; }
        var paths = picks.Select(p => p.FullPath).ToList();
        if (_req.Folders && paths.Count == 0 && CurrentFolder is string cf && Directory.Exists(cf)) paths.Add(cf);
        if (paths.Count == 0) return;
        if (!_req.Multiple && paths.Count > 1) paths = paths.Take(1).ToList();
        Remember(_req.Folders ? paths[0] : Path.GetDirectoryName(paths[0]));
        Answer(new FilePanelResult { Paths = paths, FilterIndex = _filter });
    }

    void ChooseSave()
    {
        string folder = SaveFolder();
        string name = SaveName();
        if (folder == null || name.Length == 0) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.').Length == 0)
        {
            SheetAlert($"The name “{name}” can’t be used.", "Try using a name with fewer characters, or with no punctuation marks.", new[] { "OK" }, 0, null);
            return;
        }
        if (Path.GetExtension(name).Length == 0 && ConcreteExtension(_patterns) is string ext) name += ext;
        string path = Path.Combine(folder, name);
        if (Directory.Exists(path))
        {
            SheetAlert($"The name “{name}” is already taken.", "Please choose a different name.", new[] { "OK" }, 0, null);
            return;
        }
        if (File.Exists(path))
        {
            SheetAlert($"“{name}” already exists. Do you want to replace it?",
                $"A file or folder with the same name already exists in the folder {FinderLocation.DisplayName(folder)}. Replacing it will overwrite its current contents.",
                new[] { "Replace", "Cancel" }, 1, b => { if (b == "Replace") Saved(folder, path, true); });
            return;
        }
        Saved(folder, path, false);
    }

    void Saved(string folder, string path, bool replace)
    {
        Remember(folder);
        if (_tags.Count > 0)
        {
            Settings.Current.Tags[path] = _tags.ToList();
            Settings.Save(notify: false);
        }
        Answer(new FilePanelResult { Paths = new List<string> { path }, FilterIndex = _filter, ReplaceConfirmed = replace });
    }

    void Remember(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var s = Settings.Current;
        if (_req.AppKey != null) s.FilePanelPlaces[_req.AppKey] = folder;
        s.FilePanelRecentFolders.RemoveAll(f => Same(f, folder));
        s.FilePanelRecentFolders.Insert(0, folder);
        if (s.FilePanelRecentFolders.Count > 10) s.FilePanelRecentFolders.RemoveRange(10, s.FilePanelRecentFolders.Count - 10);
        Settings.Save(notify: false);
    }

    /// <summary>The answer, once (null: cancelled); then the panel goes.</summary>
    void Answer(FilePanelResult r)
    {
        if (_answered) return;
        _answered = true;
        var done = _done;
        _done = null;
        try { done?.Invoke(r); } catch { }
        if (IsLoaded || IsVisible) Dispatcher.BeginInvoke(() => { try { Close(); } catch { } });
    }

    // ================================================================== keys

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        if (_sheets.Count > 0) { _sheets[^1].keys?.Invoke(e); return; }
        if (e.OriginalSource == _tagText && mods is ModifierKeys.None or ModifierKeys.Shift && TagKey(key)) { e.Handled = true; return; }
        if (e.OriginalSource == _search)
        {
            if (key == Key.Escape && _search.Text.Length > 0) { _search.Text = ""; e.Handled = true; return; }
            if (key == Key.Enter) { Reload(); e.Handled = true; return; }
        }
        if (key == Key.Escape || (ctrl && !shift && key == Key.OemPeriod)) { Answer(null); e.Handled = true; return; }
        if (key == Key.Enter) { Choose(); e.Handled = true; return; }

        string go = null;
        if (ctrl && !alt)
        {
            switch (key, shift)
            {
                case (Key.S, false) when _req.Save: Choose(); e.Handled = true; return;
                case (Key.O, false) when !_req.Save: Choose(); e.Handled = true; return;
                case (Key.D1, false): SetViewMode("icons"); UpdateChrome(); e.Handled = true; return;
                case (Key.D2, false): SetViewMode("list"); UpdateChrome(); e.Handled = true; return;
                case (Key.D3, false): SetViewMode("columns"); UpdateChrome(); e.Handled = true; return;
                case (Key.D4, false): SetViewMode("gallery"); UpdateChrome(); e.Handled = true; return;
                case (Key.G, true): GoToFolder(null); e.Handled = true; return;
                case (Key.N, true): if (!Compact) NewFolder(); e.Handled = true; return;
                case (Key.OemPeriod, true): ToggleHidden(); e.Handled = true; return;
                case (Key.F, false): if (!Compact) { _search.Focus(); _search.SelectAll(); } e.Handled = true; return;
                case (Key.R, false): ShellHost.RevealInFinder(SelectedItems.FirstOrDefault()?.FullPath ?? SaveFolder() ?? CurrentFolder); e.Handled = true; return;
                case (Key.Up, false): GoUp(); e.Handled = true; return;
                case (Key.Down, false):
                    if (SelectedItems.FirstOrDefault() is { } it && (it.IsFolder || it.IsDrive)) Navigate(it.FullPath); else Choose();
                    e.Handled = true;
                    return;
                case (Key.OemOpenBrackets, false): GoBack(); e.Handled = true; return;
                case (Key.OemCloseBrackets, false): GoForward(); e.Handled = true; return;
                case (Key.D, _): go = FinderLocation.Desktop; break;
                case (Key.O, true): go = FinderLocation.Documents; break;
                case (Key.H, true): go = FinderLocation.Home; break;
                case (Key.A, true) when !_req.Save: go = FinderLocation.Applications; break;
                case (Key.F, true) when !_req.Save: go = FinderLocation.Recents; break;
                case (Key.C, true): go = FinderLocation.Computer; break;
                case (Key.I, true): go = Clouds() is var (ic, od) ? ic ?? od : null; break;
            }
        }
        else if (ctrl && alt)
        {
            if (key == Key.L) go = FinderLocation.Downloads;
            else if (key == Key.S && !Compact) { ToggleSidebar(); e.Handled = true; return; }
        }
        else if (alt && !ctrl)
        {
            if (key == Key.Left) { GoBack(); e.Handled = true; return; }
            if (key == Key.Right) { GoForward(); e.Handled = true; return; }
            if (key == Key.Up) { GoUp(); e.Handled = true; return; }
        }
        if (go != null)
        {
            e.Handled = true;
            if (Compact && go == FinderLocation.Computer) return;
            if (FinderLocation.IsVirtual(go) || Directory.Exists(go)) Navigate(go);
            return;
        }
        if (e.OriginalSource is TextBox) return;   // (typing a name, a tag or a search)
        // typing / or ~: Go to Folder, starting with it (as on a Mac)
        if (!ctrl && !alt && (key is Key.Oem2 or Key.Divide && !shift || key == Key.Oem3 && shift))
        {
            GoToFolder(key == Key.Oem3 ? "~" : "/");
            e.Handled = true;
            return;
        }
        if (key == Key.Space && mods == ModifierKeys.None) { QuickLookWindow.Toggle(null, SelectedItems.FirstOrDefault()); e.Handled = true; return; }
        if (ctrl && key == Key.A && _req.Multiple) { foreach (var i in Scope) i.IsSelected = Choosable(i); SelectionChanged(); e.Handled = true; return; }
        if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End && !ctrl)
        {
            if (_view.HandleKey(key, mods)) { SelectionChanged(); e.Handled = true; return; }
            Move(key, shift && _req.Multiple);
            e.Handled = true;
            return;
        }
        if (mods is ModifierKeys.None or ModifierKeys.Shift && KeyChar(key) is string ch)
        {
            if ((DateTime.Now - _typeAt).TotalMilliseconds > 900) _typed = "";
            _typeAt = DateTime.Now;
            _typed += ch;
            var match = Scope.FirstOrDefault(i => !i.IsDisabled && i.Name.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase))
                        ?? Scope.FirstOrDefault(i => i.Name.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase));
            if (match != null && (!match.IsDisabled || _req.Save)) { SelectOnly(match); _view.Reveal(match); }
            e.Handled = true;
        }
    }

    string _typed = "";
    DateTime _typeAt;

    static string KeyChar(Key k) =>
        k >= Key.A && k <= Key.Z ? ((char)('a' + (k - Key.A))).ToString() :
        k >= Key.D0 && k <= Key.D9 ? ((char)('0' + (k - Key.D0))).ToString() : null;

    void Move(Key key, bool extend)
    {
        var list = Scope;
        if (list.Count == 0) return;
        var current = list.LastOrDefault(i => i.IsSelected && i == _anchor) ?? list.LastOrDefault(i => i.IsSelected);
        int idx = current == null ? -1 : list.IndexOf(current);
        int perRow = _viewMode == "icons" ? _view.ItemsPerRow : 1;
        if (_viewMode == "list" && key is Key.Left or Key.Right)
        {
            if (current == null) return;
            if (key == Key.Right && (current.IsFolder || current.IsDrive) && !current.IsExpanded) ToggleExpand(current);
            else if (key == Key.Left && current.IsExpanded) ToggleExpand(current);
            else if (key == Key.Left && current.ParentItem != null) { SelectOnly(current.ParentItem); _view.Reveal(current.ParentItem); }
            return;
        }
        int step = key switch { Key.Up => -perRow, Key.Down => perRow, Key.Left => -1, Key.Right => 1, Key.Home => 1, _ => -1 };
        int next = key switch
        {
            Key.Home => 0,
            Key.End => list.Count - 1,
            _ when idx < 0 => step > 0 ? 0 : list.Count - 1,
            _ => idx + step,
        };
        // greyed-out files are passed over (an Open panel can't choose them)
        while (next >= 0 && next < list.Count && list[next].IsDisabled && !_req.Save) next += step;
        if (next < 0 || next >= list.Count) return;
        var target = list[next];
        if (extend && _anchor != null)
        {
            int a = Math.Max(0, list.IndexOf(_anchor));
            int lo = Math.Min(a, next), hi = Math.Max(a, next);
            for (int i = 0; i < list.Count; i++) list[i].IsSelected = i >= lo && i <= hi && (Choosable(list[i]) || i == next);
            SelectionChanged();
        }
        else SelectOnly(target);
        _view.Reveal(target);
    }

    // ================================================================== IFinderHost

    public string SortKey => _sortKey;
    public bool SortAscending => _sortAsc;

    public void ItemMouseDown(FileItem item, ModifierKeys mods, FinderView view)
    {
        _deferSingle = null;
        if (item.IsDisabled)
        {
            // greyed out: not chosen - but saving, a file's name is taken from it
            if (_req.Save) TakeName(item);
            return;
        }
        if (_req.Multiple && mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Shift))
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
            SelectionChanged();
        }
        else if (_req.Multiple && mods.HasFlag(ModifierKeys.Shift) && _anchor != null && Scope.Contains(_anchor))
        {
            var list = Scope;
            int a = list.IndexOf(_anchor), b = list.IndexOf(item);
            if (a > b) (a, b) = (b, a);
            for (int i = 0; i < list.Count; i++) list[i].IsSelected = (i >= a && i <= b && !list[i].IsDisabled) || (mods.HasFlag(ModifierKeys.Control) && list[i].IsSelected);
            SelectionChanged();
        }
        else if (!item.IsSelected) SelectOnly(item);
        else _deferSingle = item;
    }

    public void ItemMouseUp(FileItem item, ModifierKeys mods)
    {
        if (_deferSingle == item && mods == ModifierKeys.None) SelectOnly(item);
        _deferSingle = null;
    }

    public void ItemDoubleClick(FileItem item)
    {
        if (item.IsFolder || item.IsDrive) { Navigate(item.FullPath); return; }
        if (_req.Save) { TakeName(item); Choose(); return; }
        if (!Choosable(item)) return;
        SelectOnly(item);
        Choose();
    }

    public void ItemRightDown(FileItem item) { if (!item.IsSelected && !item.IsDisabled) SelectOnly(item); }

    public void BackgroundMouseDown(ModifierKeys mods)
    {
        if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            foreach (var i in Scope) i.IsSelected = false;
            SelectionChanged();
        }
    }

    public void ItemContextMenu(FileItem item, FrameworkElement target)
    {
        var items = new List<object> { Mb.Item("Quick Look", () => QuickLookWindow.Toggle(null, item), "Space") };
        if (!item.IsFolder && !FinderLocation.IsVirtual(item.FullPath) && (_search.Text.Length > 0 || FinderLocation.IsVirtual(_loc)))
            items.Add(Mb.Item("Show in Enclosing Folder", () => { _pendingSelect = item.FullPath; Navigate(Path.GetDirectoryName(item.FullPath)); }));
        items.Add(Mb.Item("Show in Finder", () => ShellHost.RevealInFinder(item.FullPath), "⌘R"));
        Mb.Context(items.ToArray()).IsOpen = true;
    }

    public void BackgroundContextMenu(FrameworkElement target)
    {
        var items = new List<object>();
        if (!FinderLocation.IsVirtual(CurrentFolder)) items.Add(Mb.Item("New Folder", NewFolder, "⇧⌘N"));
        items.Add(Mb.Sub("View", ViewMenuItems().Take(4).ToArray()));
        items.Add(Mb.Sub("Sort By", SortMenuItems().Skip(1).ToArray()));
        items.Add(Mb.Sep());
        items.Add(Mb.Item(Settings.Current.ShowHiddenFiles ? "Hide Hidden Files" : "Show Hidden Files", ToggleHidden, "⇧⌘."));
        items.Add(Mb.Item("Go to Folder…", () => GoToFolder(null), "⇧⌘G"));
        if (_req.OfferWindowsDialog) { items.Add(Mb.Sep()); items.Add(Mb.Item("Show Windows Dialog", () => Answer(new FilePanelResult { UseWindowsDialog = true }))); }
        Mb.Context(items.ToArray()).IsOpen = true;
    }

    public void StartDrag(FileItem item, FrameworkElement source) { }

    public void DragOverTarget(FileItem folder, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
    }

    /// <summary>A folder dropped on the panel (from Finder, the desktop …) is gone to; a file, the folder it's in -
    /// and it's chosen.</summary>
    public void DropOn(FileItem folder, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        string first = files[0];
        if (Directory.Exists(first) && !_req.Folders) { Navigate(first); return; }
        _pendingSelect = first;
        Navigate(Path.GetDirectoryName(first.TrimEnd('\\')) ?? first);
    }

    public void DragLeft() { }

    public void MarqueeSelect(HashSet<FileItem> hits, bool additive)
    {
        if (!_req.Multiple) return;
        foreach (var i in Scope) i.IsSelected = hits.Contains(i) && !i.IsDisabled;
        SelectionChanged();
    }

    public void CommitRename(FileItem item, string newName) { item.IsRenaming = false; }
    public void CancelRename(FileItem item) { item.IsRenaming = false; }

    public void ToggleExpand(FileItem item)
    {
        if (!(item.IsFolder || item.IsDrive)) return;
        if (item.IsExpanded) { item.IsExpanded = false; Rebuild(); return; }
        LoadChildren(item.FullPath, kids =>
        {
            foreach (var k in kids) { k.Depth = item.Depth + 1; k.ParentItem = item; }
            _children[item.FullPath] = kids;
            item.IsExpanded = true;
            Rebuild();
        });
    }

    public void SortBy(string key) => SortBy(key, false);

    /// <summary>Sorted by a column (clicked again: the other way), or chosen from the Sort By menu (from the start).</summary>
    void SortBy(string key, bool fromMenu)
    {
        if (_sortKey == key && !fromMenu) _sortAsc = !_sortAsc;
        else { _sortKey = key; _sortAsc = true; }
        Rebuild();
    }

    public void ViewFocused(FinderView view) => view.Active = true;

    public void LoadChildren(string folder, Action<List<FileItem>> done)
    {
        bool hidden = Settings.Current.ShowHiddenFiles;
        Task.Run(() => FinderWindow.LoadLocation(folder, hidden, CancellationToken.None)).ContinueWith(t =>
        {
            var items = t.IsFaulted ? new List<FileItem>() : t.Result.items ?? new List<FileItem>();
            Dispatcher.BeginInvoke(() =>
            {
                ApplyEnabled(items);
                done(Sort(items));
            });
        });
    }

    public void ActiveColumnChanged(string folder) => UpdateChrome();

    // ================================================================== diagnostics

    /// <summary>Diagnostics (--open filepanel:pick:&lt;path&gt;|…): answers as if those were chosen.</summary>
    public void TestPick(IEnumerable<string> paths) => Answer(new FilePanelResult { Paths = paths.ToList(), FilterIndex = _filter });
    public void TestCancel() => Answer(null);
    /// <summary>Closed from outside (the app closed its own dialog): no answer to give.</summary>
    public void Dismiss() { _done = null; Answer(null); }

    /// <summary>Diagnostics (--open filepanel:test:&lt;what&gt;): the panel put in a state to look at - opened out or
    /// small, a sheet, tags, a view.</summary>
    public void Test(string what)
    {
        string arg = what.Contains(':') ? what[(what.IndexOf(':') + 1)..] : null;
        switch (what.Split(':')[0])
        {
            case "expand": SetExpanded(true); break;
            case "collapse": SetExpanded(false); break;
            case "newfolder": NewFolder(); break;
            case "goto": GoToFolder(arg ?? "~/"); break;
            case "replace": SheetAlert("“Untitled.txt” already exists. Do you want to replace it?", "A file or folder with the same name already exists in the folder Documents. Replacing it will overwrite its current contents.", new[] { "Replace", "Cancel" }, 1, null); break;
            case "closesheet": CloseSheet(); break;
            case "tags": AddTag("red"); AddTag("blue"); break;
            case "options": _showOptions = !_showOptions; Arrange(); break;
            case "view": SetViewMode(arg ?? "list", save: false); UpdateChrome(); break;
            case "go": if (arg != null) Navigate(ResolveTyped(arg)); break;
            case "select": if (_display.FirstOrDefault(i => string.Equals(i.Name, arg, StringComparison.OrdinalIgnoreCase)) is { } it) SelectOnly(it); break;
        }
    }

    public string TestState() =>
        $"save={_req.Save} compact={Compact} folders={_req.Folders} multiple={_req.Multiple} size={ActualWidth:0}x{ActualHeight:0} at=\"{CurrentFolder}\" items={_display.Count} " +
        $"greyed={_display.Count(i => i.IsDisabled)} filter={_filter}:{string.Join(",", _patterns)} name=\"{(_req.Save ? SaveName() : "")}\" tags={string.Join(",", _tags)} " +
        $"ok=\"{_ok.Content}\" okEnabled={_ok.IsEnabled} message=\"{Message()}\" options={_options.Visibility == Visibility.Visible} sheets={_sheets.Count} view={_viewMode} " +
        $"left={Left:0} top={Top:0} offscreen={ShellHost.Offscreen}";
}
