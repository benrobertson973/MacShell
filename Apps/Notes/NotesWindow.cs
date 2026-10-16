using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps.Notes;

/// <summary>
/// Notes, like macOS Notes: the folders "On My Mac" in the sidebar (and Recently Deleted), the notes of the one chosen
/// (pinned ones first, then by date - Today, Yesterday, Previous 7 Days …), and the note itself to write in. The toolbar:
/// a new note, the Aa formatting menu, checklist, add a photo, search. Import from Apple Notes and export for it.
/// </summary>
public sealed class NotesWindow : MacWindow
{
    public const string AppId = "internal:notes";
    public static NotesWindow Instance { get; private set; }
    static readonly Color SelectionYellow = Color.FromRgb(0xFB, 0xD7, 0x56);

    public static void OpenApp()
    {
        NotesLibrary.Load();
        if (Instance != null)
        {
            if (Instance.WindowState == WindowState.Minimized) Instance.WindowState = WindowState.Normal;
            if (!ShellHost.Offscreen) Instance.Activate();
            return;
        }
        Instance = new NotesWindow();
        Instance.Show();
        if (!ShellHost.Offscreen) Instance.Activate();
    }

    readonly Grid _layout = new();
    readonly ColumnDefinition _sideCol = new(), _listCol = new();
    readonly StackPanel _sidebar = new() { Margin = new Thickness(0, 4, 0, 12) };
    readonly StackPanel _list = new() { Margin = new Thickness(0, 2, 0, 12) };
    readonly ScrollViewer _listScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly TextBlock _empty = new() { FontSize = 15, Text = "No Notes", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly NoteEditor _editor = new();
    readonly Grid _editorHost = new();
    readonly TextBlock _date = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
    readonly TextBox _search = new() { Width = 200 };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly List<(NoteInfo note, Border row)> _rows = new();
    readonly HashSet<NoteInfo> _selected = new();
    string _folder;
    NoteInfo _note;
    bool _dirty, _listFocused = true;
    Button _formatButton;

    NotesWindow() : base(AppId)
    {
        Title = "Notes";
        Width = 1040; Height = 680; MinWidth = 700; MinHeight = 400;
        PlaceAsBefore();
        _folder = NotesLibrary.Folders.Any(f => f.Id == Settings.Current.NotesFolder) || Settings.Current.NotesFolder is NotesLibrary.AllFolder or NotesLibrary.DeletedFolder
            ? Settings.Current.NotesFolder : NotesLibrary.DefaultFolder;
        BuildChrome();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveCurrent(); };
        _editor.Edited += () => { _dirty = true; _saveTimer.Stop(); _saveTimer.Start(); UpdateCurrentRow(); };
        _editor.GotKeyboardFocus += (_, _) => { _listFocused = false; PaintSelection(); };
        NotesLibrary.Changed += OnLibraryChanged;
        Closed += (_, _) =>
        {
            SaveCurrent();
            DropIfEmpty(_note);
            NotesLibrary.SaveNow();
            NotesLibrary.Changed -= OnLibraryChanged;
            Remember();
            Instance = null;
        };
        SizeChanged += (_, _) => Remember();
        LocationChanged += (_, _) => Remember();
        Activated += (_, _) => PaintSelection();
        Deactivated += (_, _) => PaintSelection();
        PreviewKeyDown += OnKey;
        BuildSidebar();
        ShowFolder(_folder, Settings.Current.NotesLastNote);
    }

    void OnLibraryChanged() { if (!_building) BuildSidebar(); }
    bool _building;

    /// <summary>Shown again after notes came in from outside (an import).</summary>
    public void Reload() => ShowFolder(_folder, _note?.Id, keepScroll: true);

    /// <summary>The folder imported files go into: the one shown, if it's a real folder.</summary>
    public string ImportFolder() => NotesLibrary.Folders.Any(f => f.Id == _folder) ? _folder : NotesLibrary.DefaultFolder;

    // ================================================================== chrome

    static Button ToolButton(string symbol, string tip, Action click, double size = 17, object content = null)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Content = content ?? new SymbolIcon { Symbol = symbol, Width = size, Height = size, StrokeWidth = 1.8 },
            ToolTip = tip,
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => click();
        return b;
    }

    void BuildChrome()
    {
        _listShowFolders = ToolButton("sidebar.left", "Show Folders", ToggleSidebar);
        _sideCol.Width = new GridLength(Math.Clamp(Settings.Current.NotesSidebarWidth, 150, 340));
        _listCol.Width = new GridLength(Math.Clamp(Settings.Current.NotesListWidth, 220, 520));
        _layout.ColumnDefinitions.Add(_sideCol);
        _layout.ColumnDefinitions.Add(_listCol);
        _layout.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 300 });

        // ---------------- sidebar
        var side = new Grid();
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        side.RowDefinitions.Add(new RowDefinition());
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "SidebarTintBrush");
        Grid.SetRowSpan(tint, 3);
        side.Children.Add(tint);
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(new TrafficLights { Margin = new Thickness(20, 0, 0, 0) });
        var toggle = ToolButton("sidebar.left", "Hide Sidebar", ToggleSidebar);
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(toggle);
        side.Children.Add(head);
        var sideScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(sideScroll, 1);
        side.Children.Add(sideScroll);
        // "New Folder" at the bottom, as on a Mac
        var nf = new StackPanel { Orientation = Orientation.Horizontal };
        var plus = new SymbolIcon { Symbol = "plus.circle", Width = 16, Height = 16, StrokeWidth = 1.6, VerticalAlignment = VerticalAlignment.Center };
        plus.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        nf.Children.Add(plus);
        var nft = new TextBlock { Text = "New Folder", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        nft.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        nf.Children.Add(nft);
        var newFolder = new Button { Style = (Style)Application.Current.Resources["PlainButton"], Content = nf, Margin = new Thickness(18, 8, 0, 12), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
        newFolder.Click += (_, _) => NewFolder();
        Grid.SetRow(newFolder, 2);
        side.Children.Add(newFolder);
        _layout.Children.Add(side);
        var split1 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
        WindowChrome.SetIsHitTestVisibleInChrome(split1, true);
        split1.DragCompleted += (_, _) =>
        {
            // dragged nearly shut: the folders are hidden (and the window's buttons move to the list)
            if (_sideCol.ActualWidth < 120) _sideCol.Width = new GridLength(0);
            SidebarShown();
            Remember();
        };
        _layout.Children.Add(split1);

        // ---------------- the note list
        var listCol = new DockPanel();
        listCol.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var listEdge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = listCol };
        listEdge.SetResourceReference(Border.BorderBrushProperty, "StrongSeparatorBrush");
        Grid.SetColumn(listEdge, 1);
        _layout.Children.Add(listEdge);
        var listBar = new DockPanel { Height = 52, LastChildFill = false };
        // (with the folders hidden, the window's buttons move to the list's top-left, as on a Mac)
        _listLights.Margin = new Thickness(20, 0, 0, 0);
        _listLights.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_listLights, Dock.Left);
        listBar.Children.Add(_listLights);
        _listShowFolders.Visibility = Visibility.Collapsed;
        _listShowFolders.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(_listShowFolders, Dock.Left);
        listBar.Children.Add(_listShowFolders);
        var trash = ToolButton("trash", "Delete", DeleteSelected);
        trash.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(trash, Dock.Right);
        listBar.Children.Add(trash);
        DockPanel.SetDock(listBar, Dock.Top);
        listCol.Children.Add(listBar);
        var listHost = new Grid();
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        listHost.Children.Add(_empty);
        _listScroll.Content = _list;
        listHost.Children.Add(_listScroll);
        listHost.Background = Brushes.Transparent;
        listHost.MouseLeftButtonDown += (_, _) => { _listFocused = true; PaintSelection(); };
        listHost.MouseRightButtonUp += (_, e) => { if (e.OriginalSource == _listScroll || e.OriginalSource == listHost) ListBackgroundMenu(); };
        listCol.Children.Add(listHost);
        var split2 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
        Grid.SetColumn(split2, 1);
        WindowChrome.SetIsHitTestVisibleInChrome(split2, true);
        split2.DragCompleted += (_, _) => Remember();
        _layout.Children.Add(split2);

        // ---------------- the note
        var noteCol = new DockPanel();
        noteCol.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var noteEdge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = noteCol };
        noteEdge.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        Grid.SetColumn(noteEdge, 2);
        _layout.Children.Add(noteEdge);
        var bar = new DockPanel { Height = 52, LastChildFill = false };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        tools.Children.Add(ToolButton("square.and.pencil", "New Note", () => NewNote()));
        tools.Children.Add(new Border { Width = 14 });
        var aa = new TextBlock { Text = "Aa", FontSize = 15, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        _formatButton = ToolButton(null, "Format", () => ShowFormatPopover(), content: aa);
        tools.Children.Add(_formatButton);
        tools.Children.Add(ToolButton("checklist", "Checklist", () => { EnsureNote(); _editor.ToggleList("check"); }));
        tools.Children.Add(ToolButton("photo", "Add Photo", AddPhoto));
        bar.Children.Add(tools);
        _search.Style = (Style)Application.Current.Resources["MacSearchField"];
        _search.Margin = new Thickness(0, 0, 12, 0);
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => ShowFolder(_folder, _note?.Id, keepScroll: false);
        WindowChrome.SetIsHitTestVisibleInChrome(_search, true);
        DockPanel.SetDock(_search, Dock.Right);
        bar.Children.Add(_search);
        DockPanel.SetDock(bar, Dock.Top);
        noteCol.Children.Add(bar);
        _date.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        DockPanel.SetDock(_date, Dock.Top);
        noteCol.Children.Add(_date);
        _editorHost.Children.Add(_editor);
        noteCol.Children.Add(_editorHost);

        Content = _layout;
    }

    readonly TrafficLights _listLights = new();
    Button _listShowFolders;

    void ToggleSidebar()
    {
        if (_sideCol.Width.Value > 0) { Settings.Current.NotesSidebarWidth = Math.Max(150, _sideCol.ActualWidth); _sideCol.Width = new GridLength(0); }
        else _sideCol.Width = new GridLength(Math.Clamp(Settings.Current.NotesSidebarWidth, 150, 340));
        SidebarShown();
    }

    void SidebarShown()
    {
        bool hidden = _sideCol.Width.Value < 1;
        _listLights.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        _listShowFolders.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    void PlaceAsBefore()
    {
        var b = Settings.Current.NotesWindowBounds;
        bool onScreen = b is { Length: 4 } && b[2] >= MinWidth - 1 && b[3] >= MinHeight - 1 &&
                        b[0] + b[2] - 120 > SystemParameters.VirtualScreenLeft && b[0] + 120 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                        b[1] >= SystemParameters.VirtualScreenTop - 10 && b[1] + 40 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!onScreen) { Loaded += (_, _) => CenterOnWorkArea(); return; }
        Left = b[0]; Top = b[1]; Width = b[2]; Height = b[3];
        ApplyOffscreen();
    }

    void Remember()
    {
        if (!IsLoaded || ShellHost.Offscreen || WindowState != WindowState.Normal) return;
        var s = Settings.Current;
        s.NotesWindowBounds = new[] { Math.Round(Left), Math.Round(Top), Math.Round(ActualWidth), Math.Round(ActualHeight) };
        if (_sideCol.ActualWidth > 0) s.NotesSidebarWidth = Math.Round(_sideCol.ActualWidth);
        if (_listCol.ActualWidth > 0) s.NotesListWidth = Math.Round(_listCol.ActualWidth);
        Settings.Save(notify: false);
    }

    // ================================================================== sidebar

    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        var head = new TextBlock { Text = "On My Mac", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(18, 2, 12, 4) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _sidebar.Children.Add(head);
        var folders = NotesLibrary.Folders;
        if (folders.Count > 1) SidebarRow(NotesLibrary.AllFolder, "All On My Mac", "tray.full", NotesLibrary.InFolder(NotesLibrary.AllFolder).Count());
        foreach (var f in folders.OrderBy(f => f.Id == NotesLibrary.DefaultFolder ? 0 : 1).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            SidebarRow(f.Id, f.Name, "folder", NotesLibrary.Notes.Count(n => n.Folder == f.Id), f);
        int deleted = NotesLibrary.Notes.Count(n => n.Folder == NotesLibrary.DeletedFolder);
        if (deleted > 0 || _folder == NotesLibrary.DeletedFolder) SidebarRow(NotesLibrary.DeletedFolder, "Recently Deleted", "trash", deleted);
    }

    void SidebarRow(string id, string name, string symbol, int count, NoteFolder folder = null)
    {
        var dp = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        if (count > 0)
        {
            var c = new TextBlock { Text = count.ToString("N0"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            c.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            DockPanel.SetDock(c, Dock.Right);
            dp.Children.Add(c);
        }
        var icon = new SymbolIcon { Symbol = symbol, Width = 17, Height = 17, StrokeWidth = 1.8, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0xA9, 0x08)) };
        dp.Children.Add(icon);
        var tb = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        dp.Children.Add(tb);
        var row = new Border { Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(8, 0, 8, 0), Child = dp, Background = Brushes.Transparent, AllowDrop = id != NotesLibrary.AllFolder };
        if (id == _folder) row.SetResourceReference(Border.BackgroundProperty, "SidebarSelectionActiveBrush");
        row.MouseLeftButtonUp += (_, _) => { _search.Text = ""; ShowFolder(id); };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; FolderMenu(id, folder); };
        row.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("MacShellNotes") && id != _folder ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        row.Drop += (_, e) =>
        {
            if (e.Data.GetData("MacShellNotes") is not string ids) return;
            foreach (var n in NotesLibrary.Notes.Where(n => ids.Split('|').Contains(n.Id)).ToList()) NotesLibrary.Move(n, id);
            ShowFolder(_folder);
        };
        _sidebar.Children.Add(row);
    }

    void FolderMenu(string id, NoteFolder folder)
    {
        var items = new List<object> { Mb.Item("New Folder", NewFolder) };
        if (folder != null && folder.Id != NotesLibrary.DefaultFolder)
        {
            items.Add(Mb.Sep());
            items.Add(Mb.Item("Rename Folder", () =>
            {
                string name = TextPrompt.Ask(this, "Rename Folder", "A new name for the folder:", folder.Name);
                if (!string.IsNullOrWhiteSpace(name)) { NotesLibrary.RenameFolder(folder, name); BuildSidebar(); }
            }));
            items.Add(Mb.Item("Delete Folder", () =>
            {
                int n = NotesLibrary.Notes.Count(x => x.Folder == folder.Id);
                if (n > 0 && ShellHost.Alert($"Delete the folder “{folder.Name}”?", $"Its {n} {(n == 1 ? "note" : "notes")} will be moved to Recently Deleted.", "Cancel", "Delete Folder") != "Delete Folder") return;
                NotesLibrary.DeleteFolder(folder);
                if (_folder == folder.Id) ShowFolder(NotesLibrary.DefaultFolder); else BuildSidebar();
            }));
        }
        if (id != NotesLibrary.DeletedFolder)
        {
            items.Add(Mb.Sep());
            items.Add(Mb.Item("Export to Apple Notes…", () => NotesTransfer.Export(this, NotesLibrary.InFolder(id).ToList(), folder?.Name ?? "Notes")));
        }
        Mb.Context(items.ToArray()).IsOpen = true;
    }

    void NewFolder()
    {
        string name = TextPrompt.Ask(this, "New Folder", "Name:", "New Folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var f = NotesLibrary.AddFolder(name.Trim());
        ShowFolder(f.Id);
    }

    // ================================================================== the list

    /// <summary>Shows a folder's notes (or the search's), choosing a note (the last one, or the first).</summary>
    void ShowFolder(string folder, string selectId = null, bool keepScroll = false)
    {
        SaveCurrent();
        if (_note != null && _note.Id != selectId) DropIfEmpty(_note);
        _folder = folder;
        Settings.Current.NotesFolder = folder;
        Settings.Save(notify: false);
        BuildSidebar();
        double offset = _listScroll.VerticalOffset;
        string q = _search.Text.Trim();
        var notes = (q.Length > 0
            ? NotesLibrary.Notes.Where(n => n.Folder != NotesLibrary.DeletedFolder &&
                ((n.Title?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false) || (n.Text?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            : NotesLibrary.InFolder(folder)).ToList();
        BuildList(notes, q.Length > 0);
        _empty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = q.Length > 0 ? "No Results" : "No Notes";
        var pick = notes.FirstOrDefault(n => n.Id == selectId) ?? (keepScroll ? null : Ordered(notes).FirstOrDefault());
        Select(pick);
        if (keepScroll) { _listScroll.UpdateLayout(); _listScroll.ScrollToVerticalOffset(offset); } else _listScroll.ScrollToTop();
    }

    static IEnumerable<NoteInfo> Ordered(IEnumerable<NoteInfo> notes) =>
        notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Folder == NotesLibrary.DeletedFolder ? n.Deleted ?? n.Modified : n.Modified);

    void BuildList(List<NoteInfo> notes, bool searching)
    {
        _list.Children.Clear();
        _rows.Clear();
        string lastGroup = null;
        foreach (var n in Ordered(notes))
        {
            string group = searching ? "Top Results" : n.Pinned ? "Pinned" : DateGroup(n.Folder == NotesLibrary.DeletedFolder ? n.Deleted ?? n.Modified : n.Modified);
            if (group != lastGroup)
            {
                var h = new TextBlock { Text = group, FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(20, _list.Children.Count == 0 ? 2 : 14, 12, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
                _list.Children.Add(h);
                lastGroup = group;
            }
            var row = MakeRow(n);
            _rows.Add((n, row));
            _list.Children.Add(row);
        }
    }

    static string DateGroup(DateTime d)
    {
        var today = DateTime.Today;
        if (d >= today) return "Today";
        if (d >= today.AddDays(-1)) return "Yesterday";
        if (d >= today.AddDays(-7)) return "Previous 7 Days";
        if (d >= today.AddDays(-30)) return "Previous 30 Days";
        if (d.Year == today.Year) return d.ToString("MMMM", CultureInfo.CurrentCulture);
        return d.Year.ToString();
    }

    static string ShortDate(DateTime d)
    {
        var today = DateTime.Today;
        if (d >= today) return d.ToString(Settings.Current.Clock24Hour ? "H:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        if (d >= today.AddDays(-1)) return "Yesterday";
        if (d >= today.AddDays(-7)) return d.ToString("dddd", CultureInfo.CurrentCulture);
        return d.ToString("d", CultureInfo.CurrentCulture);
    }

    Border MakeRow(NoteInfo n)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = n.DisplayTitle, FontSize = 13, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "title" };
        texts.Children.Add(title);
        var line = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), Tag = "line" };
        line.Inlines.Add(new Run(ShortDate(n.Modified)) { Tag = "date" });
        line.Inlines.Add(new Run("  " + (string.IsNullOrWhiteSpace(n.Preview) ? "No additional text" : n.Preview)) { Tag = "preview" });
        texts.Children.Add(line);
        if (n.Folder != _folder && (_folder == NotesLibrary.AllFolder || _search.Text.Trim().Length > 0))
        {
            var where = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Tag = "folder" };
            where.Inlines.Add(new Run(n.Folder == NotesLibrary.DeletedFolder ? "Recently Deleted" : NotesLibrary.Folders.FirstOrDefault(f => f.Id == n.Folder)?.Name ?? ""));
            texts.Children.Add(where);
        }
        g.Children.Add(texts);
        if (NotesLibrary.Thumbnail(n) is { } thumb)
        {
            var img = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(5), Margin = new Thickness(8, 0, 0, 0), Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } };
            Grid.SetColumn(img, 1);
            g.Children.Add(img);
        }
        var row = new Border { CornerRadius = new CornerRadius(7), Margin = new Thickness(10, 0, 10, 1), Padding = new Thickness(10, 8, 10, 8), Child = g, Background = Brushes.Transparent, Tag = n };
        row.MouseLeftButtonDown += (_, e) => OnRowDown(n, row, e);
        row.MouseMove += (_, e) => OnRowMove(n, row, e);
        row.MouseLeftButtonUp += (_, e) => OnRowUp(n, e);
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_selected.Contains(n)) Select(n);
            NoteMenu();
        };
        PaintRow(n, row);
        return row;
    }

    Point _pressAt;
    NoteInfo _pressed;

    void OnRowDown(NoteInfo n, Border row, MouseButtonEventArgs e)
    {
        _listFocused = true;
        _pressAt = e.GetPosition(this);
        _pressed = n;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            if (!_selected.Remove(n)) _selected.Add(n);
            PaintSelection();
        }
        else if (mods.HasFlag(ModifierKeys.Shift) && _note != null)
        {
            int a = _rows.FindIndex(r => r.note == _note), b = _rows.FindIndex(r => r.note == n);
            if (a >= 0 && b >= 0) for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selected.Add(_rows[i].note);
            PaintSelection();
        }
        else if (!_selected.Contains(n) || _selected.Count == 1) Select(n);
        Keyboard.Focus(_listScroll);
    }

    void OnRowMove(NoteInfo n, Border row, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed != n) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressAt.X) < 5 && Math.Abs(p.Y - _pressAt.Y) < 5) return;
        _pressed = null;
        SaveCurrent();
        var ids = string.Join("|", (_selected.Contains(n) ? _selected : new HashSet<NoteInfo> { n }).Select(x => x.Id));
        var data = new DataObject("MacShellNotes", ids);
        data.SetText(string.Join("\n\n", (_selected.Contains(n) ? _selected : new HashSet<NoteInfo> { n }).Select(x => x.Title + "\n" + x.Text)));
        DragDrop.DoDragDrop(row, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    void OnRowUp(NoteInfo n, MouseButtonEventArgs e)
    {
        if (_pressed == n && Keyboard.Modifiers == ModifierKeys.None && _selected.Count > 1) Select(n);
        _pressed = null;
    }

    void PaintRow(NoteInfo n, Border row)
    {
        bool sel = _selected.Contains(n);
        bool active = IsActive && _listFocused;
        if (sel) row.Background = active ? new SolidColorBrush(SelectionYellow) : (Brush)FindResource("UnfocusedSelectionBrush");
        else row.Background = Brushes.Transparent;
        var texts = (StackPanel)((Grid)row.Child).Children[0];
        foreach (var t in texts.Children.OfType<TextBlock>())
        {
            if ((string)t.Tag == "title")
            {
                if (sel && active) t.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)); else t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            }
            else
                foreach (var r in t.Inlines.OfType<Run>().ToList())
                {
                    if (sel && active) r.Foreground = new SolidColorBrush(Color.FromArgb((string)r.Tag == "date" ? (byte)0xD0 : (byte)0x99, 0, 0, 0));
                    else r.SetResourceReference(TextElement.ForegroundProperty, (string)r.Tag == "date" ? "LabelBrush" : "SecondaryLabelBrush");
                }
        }
    }

    void PaintSelection() { foreach (var (n, row) in _rows) PaintRow(n, row); }

    /// <summary>Opens a note in the editor (saving the one before).</summary>
    void Select(NoteInfo n)
    {
        if (n == _note && n != null) { _selected.Clear(); _selected.Add(n); PaintSelection(); return; }
        SaveCurrent();
        var previous = _note;
        _selected.Clear();
        if (n != null) _selected.Add(n);
        _note = n;
        Settings.Current.NotesLastNote = n?.Id;
        if (previous != null && previous != n && DropIfEmpty(previous))
        {
            var r = _rows.FirstOrDefault(x => x.note == previous);
            if (r.row != null) { _list.Children.Remove(r.row); _rows.Remove(r); }
        }
        PaintSelection();
        if (n == null)
        {
            _editor.Visibility = Visibility.Collapsed;
            _date.Text = "";
            return;
        }
        _editor.Visibility = Visibility.Visible;
        _editor.IsReadOnly = n.Folder == NotesLibrary.DeletedFolder;
        _editor.Show(n, NotesLibrary.LoadDoc(n));
        _date.Text = n.Modified.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) + " at " + n.Modified.ToString(Settings.Current.Clock24Hour ? "H:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        _dirty = false;
        var row = _rows.FirstOrDefault(r => r.note == n).row;
        row?.BringIntoView();
    }

    void SaveCurrent()
    {
        _saveTimer.Stop();
        if (!_dirty || _note == null) return;
        _dirty = false;
        try { NotesLibrary.SaveDoc(_note, _editor.Read()); }
        catch (Exception ex) { App.Log("notes save: " + ex.Message); }
    }

    /// <summary>A new note left empty disappears when another is chosen (as on a Mac).</summary>
    bool DropIfEmpty(NoteInfo n)
    {
        if (n == null || n.Folder == NotesLibrary.DeletedFolder || !NotesLibrary.Notes.Contains(n)) return false;
        if (!string.IsNullOrWhiteSpace(n.Title) || n.Image != null || !string.IsNullOrWhiteSpace(n.Text)) return false;
        NotesLibrary.Notes.Remove(n);
        try { if (Directory.Exists(NotesLibrary.NoteDir(n))) Directory.Delete(NotesLibrary.NoteDir(n), true); } catch { }
        NotesLibrary.Save();
        return true;
    }

    /// <summary>The chosen note's row shows its title and first words as they're typed.</summary>
    void UpdateCurrentRow()
    {
        if (_note == null) return;
        var row = _rows.FirstOrDefault(r => r.note == _note).row;
        if (row == null) return;
        var doc = _editor.Read();
        var texts = (StackPanel)((Grid)row.Child).Children[0];
        foreach (var t in texts.Children.OfType<TextBlock>())
        {
            if ((string)t.Tag == "title") t.Text = string.IsNullOrWhiteSpace(doc.Title()) ? "New Note" : doc.Title();
            else if ((string)t.Tag == "line")
                foreach (var r in t.Inlines.OfType<Run>().ToList())
                {
                    if ((string)r.Tag == "date") r.Text = ShortDate(DateTime.Now);
                    else { string pv = doc.Preview(); r.Text = "  " + (string.IsNullOrWhiteSpace(pv) ? "No additional text" : pv); }
                }
        }
    }

    // ================================================================== commands

    public void NewNote()
    {
        if (_folder == NotesLibrary.DeletedFolder) _folder = NotesLibrary.DefaultFolder;
        _search.Text = "";
        SaveCurrent();
        var n = NotesLibrary.Create(_folder == NotesLibrary.AllFolder ? NotesLibrary.DefaultFolder : _folder);
        ShowFolder(_folder, n.Id);
        _editor.Focus();
        _editor.CaretPosition = _editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    void EnsureNote() { if (_note == null) NewNote(); }

    List<NoteInfo> Chosen() => _selected.Count > 0 ? _selected.ToList() : _note != null ? new List<NoteInfo> { _note } : new List<NoteInfo>();

    public void DeleteSelected()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) return;
        if (chosen[0].Folder == NotesLibrary.DeletedFolder &&
            ShellHost.Alert(chosen.Count == 1 ? "Delete this note immediately?" : $"Delete {chosen.Count} notes immediately?", "You can’t undo this action.", "Cancel", "Delete") != "Delete") return;
        SaveCurrent();
        int index = _rows.FindIndex(r => r.note == chosen[0]);
        foreach (var n in chosen) NotesLibrary.Delete(n);
        _note = null;
        var rest = _rows.Where(r => !chosen.Contains(r.note)).ToList();
        var next = rest.Count == 0 ? null : rest[Math.Clamp(index, 0, rest.Count - 1)].note;
        ShowFolder(_folder, next?.Id, keepScroll: true);
    }

    void TogglePin()
    {
        var chosen = Chosen();
        bool pin = chosen.Any(n => !n.Pinned);
        foreach (var n in chosen) n.Pinned = pin;
        NotesLibrary.Save();
        ShowFolder(_folder, _note?.Id, keepScroll: true);
    }

    void NoteMenu()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) return;
        var items = new List<object>();
        if (chosen[0].Folder == NotesLibrary.DeletedFolder)
        {
            items.Add(Mb.Item("Recover", () => { foreach (var n in chosen) NotesLibrary.Recover(n); ShowFolder(_folder, null, keepScroll: true); }));
            items.Add(Mb.Item("Delete Immediately…", DeleteSelected));
        }
        else
        {
            // (as in Notes on a Mac: pin; send a copy, move; duplicate, delete)
            items.Add(Mb.Item(chosen.All(n => n.Pinned) ? "Unpin Note" : "Pin Note", TogglePin));
            items.Add(Mb.Sep());
            items.Add(Mb.Sub("Send a Copy",
                Mb.Item(chosen.Count == 1 ? "Export to Apple Notes…" : $"Export {chosen.Count} Notes to Apple Notes…", () => { SaveCurrent(); NotesTransfer.Export(this, chosen, chosen.Count == 1 ? chosen[0].DisplayTitle : "Notes"); }),
                Mb.Item("Copy Text", () =>
                {
                    SaveCurrent();
                    try { Clipboard.SetText(string.Join("\n\n", chosen.Select(n => NotesLibrary.LoadDoc(n).PlainText().TrimEnd()))); } catch { }
                })));
            items.Add(Mb.Sub("Move to", NotesLibrary.Folders.Select(f => (object)Mb.Item(f.Name, () =>
            {
                SaveCurrent();
                foreach (var n in chosen) NotesLibrary.Move(n, f.Id);
                ShowFolder(_folder, null, keepScroll: true);
            }, enabled: chosen.Any(n => n.Folder != f.Id))).ToArray()));
            items.Add(Mb.Sep());
            items.Add(Mb.Item("Duplicate Note", () => { SaveCurrent(); var copy = NotesLibrary.Duplicate(chosen[0]); ShowFolder(_folder, copy.Id); }, enabled: chosen.Count == 1));
            items.Add(Mb.Item(chosen.Count == 1 ? "Delete" : $"Delete {chosen.Count} Notes", DeleteSelected));
        }
        Mb.Context(items.ToArray()).IsOpen = true;
    }

    void ListBackgroundMenu() => Mb.Context(Mb.Item("New Note", () => NewNote()), Mb.Item("New Folder", NewFolder)).IsOpen = true;

    void AddPhoto()
    {
        EnsureNote();
        var r = Finder.FilePanel.ShowModal(this, new Finder.FilePanelRequest
        {
            Title = "Add a photo to the note", Prompt = "Add", AppKey = "macshell:notes", Multiple = true,
            Folder = Settings.Current.FilePanelPlaces.ContainsKey("macshell:notes") ? null : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Filters = { "Images (*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.heic)", "All files (*.*)" },
        });
        if (r?.Paths.Count > 0) _editor.InsertImageFiles(r.Paths);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.N) { NewNote(); e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N) { NewFolder(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (_listFocused && !_editor.IsKeyboardFocusWithin && !_search.IsKeyboardFocusWithin)
        {
            if (e.Key is Key.Delete or Key.Back) { DeleteSelected(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Up)
            {
                int i = _rows.FindIndex(r => r.note == _note);
                int j = Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, _rows.Count - 1);
                if (j >= 0 && j < _rows.Count) Select(_rows[j].note);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _note != null) { _editor.Focus(); e.Handled = true; }
        }
        else if (_search.IsKeyboardFocusWithin && e.Key == Key.Escape) { _search.Text = ""; e.Handled = true; }
    }

    // ================================================================== the Aa menu

    void ShowFormatPopover()
    {
        EnsureNote();
        var pop = new Popup { PlacementTarget = _formatButton, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = -20, VerticalOffset = 2 };
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0.5), Padding = new Thickness(6), Margin = new Thickness(14, 4, 14, 20), Width = 236 };
        card.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        card.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.28 };
        var sp = new StackPanel();
        // B I U S
        var fmt = new UniformGrid { Columns = 4, Margin = new Thickness(4, 4, 4, 8), Height = 30 };
        void Fmt(string label, FontWeight w, FontStyle st, TextDecorationLocation? dec, Action act, bool on)
        {
            var t = new TextBlock { Text = label, FontSize = 15, FontWeight = w, FontStyle = st, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (dec == TextDecorationLocation.Underline) t.TextDecorations = TextDecorations.Underline;
            if (dec == TextDecorationLocation.Strikethrough) t.TextDecorations = TextDecorations.Strikethrough;
            var b = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = t, Height = 28, Margin = new Thickness(1, 0, 1, 0) };
            if (on) b.SetResourceReference(Control.BackgroundProperty, "PressedBrush");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            b.Click += (_, _) => { act(); pop.IsOpen = false; _editor.Focus(); };
            fmt.Children.Add(b);
        }
        Fmt("B", FontWeights.Bold, FontStyles.Normal, null, () => EditingCommands.ToggleBold.Execute(null, _editor), _editor.SelectionIs(TextElement.FontWeightProperty, FontWeights.Bold));
        Fmt("I", FontWeights.Normal, FontStyles.Italic, null, () => EditingCommands.ToggleItalic.Execute(null, _editor), _editor.SelectionIs(TextElement.FontStyleProperty, FontStyles.Italic));
        Fmt("U", FontWeights.Normal, FontStyles.Normal, TextDecorationLocation.Underline, () => _editor.ToggleDecoration(TextDecorationLocation.Underline), _editor.SelectionHas(TextDecorationLocation.Underline));
        Fmt("S", FontWeights.Normal, FontStyles.Normal, TextDecorationLocation.Strikethrough, () => _editor.ToggleDecoration(TextDecorationLocation.Strikethrough), _editor.SelectionHas(TextDecorationLocation.Strikethrough));
        sp.Children.Add(fmt);
        string style = _editor.CurrentStyle(), list = _editor.CurrentList();
        void Choice(string label, double size, FontWeight w, bool mono, bool on, Action act, string prefix = null)
        {
            var dp = new DockPanel();
            var check = new SymbolIcon { Symbol = "checkmark", Width = 10, Height = 10, StrokeWidth = 2.6, Margin = new Thickness(4, 0, 8, 0), Visibility = on ? Visibility.Visible : Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(SymbolIcon.ForegroundProperty, "MenuTextBrush");
            dp.Children.Add(check);
            var t = new TextBlock { Text = (prefix ?? "") + label, FontSize = size, FontWeight = w, FontFamily = mono ? Theme.MonoFont : Theme.Font, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MenuTextBrush");
            dp.Children.Add(t);
            var row = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(4, 3, 8, 3), Child = dp, Background = Brushes.Transparent };
            row.MouseEnter += (_, _) => { row.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); t.Foreground = Brushes.White; check.Foreground = Brushes.White; };
            row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; t.SetResourceReference(TextBlock.ForegroundProperty, "MenuTextBrush"); check.SetResourceReference(SymbolIcon.ForegroundProperty, "MenuTextBrush"); };
            row.MouseLeftButtonUp += (_, _) => { pop.IsOpen = false; act(); _editor.Focus(); };
            sp.Children.Add(row);
        }
        Choice("Title", 20, FontWeights.Bold, false, style == "title" && list == null, () => _editor.SetStyle("title"));
        Choice("Heading", 16, FontWeights.Bold, false, style == "heading" && list == null, () => _editor.SetStyle("heading"));
        Choice("Subheading", 14, FontWeights.SemiBold, false, style == "subheading" && list == null, () => _editor.SetStyle("subheading"));
        Choice("Body", 13, FontWeights.Normal, false, style == "body" && list == null, () => _editor.SetStyle("body"));
        Choice("Monostyled", 12.5, FontWeights.Normal, true, style == "mono" && list == null, () => _editor.SetStyle("mono"));
        var sep = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
        sep.SetResourceReference(Border.BackgroundProperty, "MenuSeparatorBrush");
        sp.Children.Add(sep);
        Choice("Bulleted List", 13, FontWeights.Normal, false, list == "bullet", () => _editor.ToggleList("bullet"), "•  ");
        Choice("Dashed List", 13, FontWeights.Normal, false, list == "dash", () => _editor.ToggleList("dash"), "–  ");
        Choice("Numbered List", 13, FontWeights.Normal, false, list == "number", () => _editor.ToggleList("number"), "1.  ");
        var sep2 = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
        sep2.SetResourceReference(Border.BackgroundProperty, "MenuSeparatorBrush");
        sp.Children.Add(sep2);
        Choice("Block Quote", 13, FontWeights.Normal, false, _editor.CaretPosition.Paragraph is Paragraph qp && NoteFlow.IsQuote(qp), () => _editor.ToggleQuote(), "▍ ");
        card.Child = sp;
        pop.Child = card;
        pop.IsOpen = true;
        FormatPopup = pop;
    }

    /// <summary>The Aa menu last opened (the notes probe draws it).</summary>
    internal Popup FormatPopup { get; private set; }

    // ================================================================== the menu bar's menus (Notes is in front)

    public static object[] MenuNotes() => new object[]
    {
        Mb.Item("About Notes", AboutWindow.ShowWindow),
        Mb.Sep(),
        Mb.Item("Hide Notes", () => Instance?.Hide(), "⌘H"),
        Mb.Sep(),
        Mb.Item("Quit Notes", () => Instance?.Close(), "⌘Q"),
    };

    public static object[] MenuFile()
    {
        var w = Instance;
        bool note = w?._note != null;
        return new object[]
        {
            Mb.Item("New Note", () => { OpenApp(); Instance?.NewNote(); }, "⌘N"),
            Mb.Item("New Folder", () => { OpenApp(); Instance?.NewFolder(); }, "⇧⌘N"),
            Mb.Sep(),
            Mb.Item("Close", () => w?.Close(), "⌘W", enabled: w != null),
            Mb.Sep(),
            Mb.Item("Import to Notes…", () => { OpenApp(); NotesTransfer.ShowImport(Instance); }),
            Mb.Item("Import from Apple Notes on a Mac…", () => { OpenApp(); NotesTransfer.ShowImport(Instance, appleFirst: true); }),
            Mb.Item("Export to Apple Notes…", () => { w.SaveCurrent(); NotesTransfer.Export(w, w.Chosen(), w.Chosen().Count == 1 ? w.Chosen()[0].DisplayTitle : "Notes"); }, enabled: note),
            Mb.Sep(),
            Mb.Item(w?._note?.Pinned == true ? "Unpin Note" : "Pin Note", () => w.TogglePin(), enabled: note),
        };
    }

    public static object[] MenuFormat()
    {
        var w = Instance;
        bool ok = w?._note != null && w._note.Folder != NotesLibrary.DeletedFolder;
        var e = w?._editor;
        return new object[]
        {
            Mb.Item("Title", () => e.SetStyle("title"), "⇧⌘T", enabled: ok),
            Mb.Item("Heading", () => e.SetStyle("heading"), "⇧⌘H", enabled: ok),
            Mb.Item("Subheading", () => e.SetStyle("subheading"), "⇧⌘J", enabled: ok),
            Mb.Item("Body", () => e.SetStyle("body"), "⇧⌘B", enabled: ok),
            Mb.Item("Monostyled", () => e.SetStyle("mono"), "⇧⌘M", enabled: ok),
            Mb.Sep(),
            Mb.Item("Bulleted List", () => e.ToggleList("bullet"), "⇧⌘7", enabled: ok),
            Mb.Item("Dashed List", () => e.ToggleList("dash"), "⇧⌘8", enabled: ok),
            Mb.Item("Numbered List", () => e.ToggleList("number"), "⇧⌘9", enabled: ok),
            Mb.Sep(),
            Mb.Item("Checklist", () => e.ToggleList("check"), "⇧⌘L", enabled: ok),
            Mb.Item("Mark as Checked", () => e.ToggleChecked(), "⇧⌘U", enabled: ok),
            Mb.Sep(),
            Mb.Item("Block Quote", () => e.ToggleQuote(), "⇧⌘'", enabled: ok),
            Mb.Item("Increase Indentation", () => e.Indent(1), "⌘]", enabled: ok),
            Mb.Item("Decrease Indentation", () => e.Indent(-1), "⌘[", enabled: ok),
            Mb.Sep(),
            Mb.Sub("Font",
                Mb.Item("Bold", () => EditingCommands.ToggleBold.Execute(null, e), "⌘B", enabled: ok),
                Mb.Item("Italic", () => EditingCommands.ToggleItalic.Execute(null, e), "⌘I", enabled: ok),
                Mb.Item("Underline", () => e.ToggleDecoration(TextDecorationLocation.Underline), "⌘U", enabled: ok),
                Mb.Item("Strikethrough", () => e.ToggleDecoration(TextDecorationLocation.Strikethrough), "⇧⌘X", enabled: ok)),
            Mb.Item("Add Link…", () => e.AddLink(w), "⌘K", enabled: ok && !e.Selection.IsEmpty),
        };
    }

    public static object[] MenuView()
    {
        var w = Instance;
        return new object[]
        {
            Mb.Item(w != null && w._sideCol.Width.Value == 0 ? "Show Folders" : "Hide Folders", () => w?.ToggleSidebar(), "⌥⌘S", enabled: w != null),
            Mb.Sep(),
            Mb.Item("Search", () => { w?._search.Focus(); }, "⌘F", enabled: w != null),
        };
    }

    // ================================================================== diagnostics

    /// <summary>--open notestest:&lt;what&gt;: the front window's state, or a sample note to look at.</summary>
    public static string TestState()
    {
        var w = Instance;
        if (w == null) return "no window";
        return $"folder={w._folder} rows={w._rows.Count} selected={w._selected.Count} note={(w._note == null ? "-" : w._note.DisplayTitle)} blocks={w._editor.Document?.Blocks.Count}";
    }

    static readonly List<string> _testNotes = new();

    public static void Test(string what)
    {
        var w = Instance;
        if (w == null) return;
        switch (what)
        {
            case "sample":
                {
                    var d = new NoteDoc();
                    d.Blocks.Add(new NoteBlock { Style = "title", Runs = { new NoteRun { T = "Groceries and plans" } } });
                    d.Blocks.Add(new NoteBlock { Runs = { new NoteRun { T = "Things to pick up " }, new NoteRun { T = "this week", B = true }, new NoteRun { T = ", and a " }, new NoteRun { T = "link", Link = "https://example.com" }, new NoteRun { T = "." } } });
                    d.Blocks.Add(new NoteBlock { Style = "heading", Runs = { new NoteRun { T = "Shopping" } } });
                    d.Blocks.Add(new NoteBlock { List = "check", Checked = true, Runs = { new NoteRun { T = "Milk" } } });
                    d.Blocks.Add(new NoteBlock { List = "check", Runs = { new NoteRun { T = "Bread" } } });
                    d.Blocks.Add(new NoteBlock { List = "bullet", Runs = { new NoteRun { T = "Apples" } } });
                    d.Blocks.Add(new NoteBlock { List = "bullet", Indent = 1, Runs = { new NoteRun { T = "Green ones" } } });
                    d.Blocks.Add(new NoteBlock { List = "dash", Runs = { new NoteRun { T = "Cheese" } } });
                    d.Blocks.Add(new NoteBlock { List = "number", Runs = { new NoteRun { T = "First" } } });
                    d.Blocks.Add(new NoteBlock { List = "number", Runs = { new NoteRun { T = "Second", I = true } } });
                    d.Blocks.Add(new NoteBlock { Style = "subheading", Runs = { new NoteRun { T = "Notes" } } });
                    d.Blocks.Add(new NoteBlock { Runs = { new NoteRun { T = "Struck", S = true }, new NoteRun { T = " and " }, new NoteRun { T = "underlined", U = true } } });
                    d.Blocks.Add(new NoteBlock { Style = "mono", Runs = { new NoteRun { T = "let x = 42;" } } });
                    d.Blocks.Add(new NoteBlock { Quote = true, Runs = { new NoteRun { T = "A quote to remember." } } });
                    var n = NotesLibrary.Create(NotesLibrary.DefaultFolder, d);
                    _testNotes.Add(n.Id);
                    w.ShowFolder(NotesLibrary.DefaultFolder, n.Id);
                    break;
                }
            case "cleanup":   // the sample notes made by the test go, for good
                foreach (var n in NotesLibrary.Notes.Where(x => _testNotes.Contains(x.Id)).ToList())
                {
                    if (w._note == n) { w._note = null; w._dirty = false; }
                    n.Folder = NotesLibrary.DeletedFolder;
                    NotesLibrary.Delete(n);
                }
                _testNotes.Clear();
                w.ShowFolder(w._folder);
                break;
            case "format": w.ShowFormatPopover(); break;
            case "roundtrip":
                {
                    if (w._note == null) break;
                    var before = NotesLibrary.LoadDoc(w._note).ToJson();
                    var after = w._editor.Read().ToJson();
                    File.WriteAllText(Path.Combine(Settings.DataDirectory, "notes-roundtrip.txt"), (before == after ? "SAME" : "DIFFERENT") + "\n" + before + "\n" + after);
                    break;
                }
        }
    }
}
