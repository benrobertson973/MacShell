using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Finder;

public class FinderTab
{
    public string Location;
    public readonly List<string> Back = new(), Forward = new();
    public string ViewMode;
    public string Search = "";
    public string SearchScope = "mac";
    public string DisplayFolder;   // for column view: deepest active column
}

/// <summary>Finder: the macOS file manager.</summary>
public partial class FinderWindow : MacWindow, IFinderHost
{
    public static readonly List<FinderWindow> All = new();
    public static FinderWindow Active { get; private set; }

    readonly List<FinderTab> _tabs = new();
    FinderTab _tab;
    FinderView _view;
    IconView _iconView;
    ListView2 _listView;
    ColumnView _columnView;
    GalleryView _galleryView;

    List<FileItem> _all = new();          // top-level items of the location
    List<FileItem> _display = new();      // flattened & sorted (list view expansions)
    readonly Dictionary<string, List<FileItem>> _children = new(StringComparer.OrdinalIgnoreCase);
    FileItem _anchor, _deferSingle;
    CancellationTokenSource _loadCts, _searchCts;
    FileSystemWatcher _watcher;
    DispatcherTimer _refreshTimer, _searchTimer;
    string _pendingSelect;

    // chrome
    readonly Grid _layout = new();
    readonly ColumnDefinition _sidebarCol = new() { Width = new GridLength(180) };
    readonly Border _sidebarHost = new();
    readonly StackPanel _sidebar = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly Border _contentHost = new();
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _backBtn = new(), _fwdBtn = new();
    readonly RadioButton[] _viewButtons = new RadioButton[4];
    readonly TextBox _search = new();
    readonly Border _tabBar = new() { Height = 28, Visibility = Visibility.Collapsed };
    readonly Grid _tabStrip = new();
    readonly Border _scopeBar = new() { Height = 34, Visibility = Visibility.Collapsed };
    readonly Border _trashBar = new() { Height = 34, Visibility = Visibility.Collapsed };
    readonly Border _pathBar = new() { Height = 24 };
    readonly StackPanel _pathCrumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _statusBar = new() { Height = 26 };
    readonly TextBlock _statusText = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider _sizeSlider = new() { Width = 90, Minimum = 16, Maximum = 256, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    readonly Border _emptyHint = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly TextBlock _emptyText = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<string, Border> _sidebarRows = new(StringComparer.OrdinalIgnoreCase);
    ToggleButton _scopeMac, _scopeFolder;

    public string ViewMode => _tab?.ViewMode ?? "icons";
    public string CurrentLocation => _tab?.Location;
    public string CurrentFolder
    {
        get
        {
            if (_tab == null) return null;
            if (_tab.ViewMode == "columns" && _columnView != null && !string.IsNullOrEmpty(_columnView.ActiveFolder) && !FinderLocation.IsVirtual(_columnView.ActiveFolder))
                return _columnView.ActiveFolder;
            return FinderLocation.IsVirtual(_tab.Location) ? null : _tab.Location;
        }
    }

    public FinderWindow(string location) : base(WindowTracker.FinderKey, 52)
    {
        All.Add(this);
        Active = this;
        Width = Math.Min(980, ShellHost.ScreenDip.Width * 0.72);
        Height = Math.Min(580, (ShellHost.ScreenDip.Height - 24) * 0.72);
        MinWidth = 520; MinHeight = 300;
        CascadePosition();
        BuildChrome();
        _iconView = new IconView(this);
        _listView = new ListView2(this);
        _columnView = new ColumnView(this);
        _galleryView = new GalleryView(this);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Reload(true); };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };

        var tab = new FinderTab();
        _tabs.Add(tab);
        _tab = tab;
        Navigate(location ?? DefaultLocation(), false);

        Activated += (_, _) => { Active = this; _view.Active = true; ShellHost.MenuBar?.RebuildMenus(); };
        Deactivated += (_, _) => { _view.Active = false; };
        Closed += (_, _) =>
        {
            All.Remove(this);
            if (Active == this) Active = All.LastOrDefault();
            _watcher?.Dispose();
            _loadCts?.Cancel();
            _searchCts?.Cancel();
        };
        Settings.Changed += OnSettingsChanged;
        Closed += (_, _) => Settings.Changed -= OnSettingsChanged;
        AppCatalog.Loaded += () => { if (_tab?.Location == FinderLocation.Applications) Reload(false); };
    }

    static string DefaultLocation() => Settings.Current.FinderNewWindowTarget switch
    {
        "home" => FinderLocation.Home,
        "desktop" => FinderLocation.Desktop,
        "documents" => FinderLocation.Documents,
        "downloads" => FinderLocation.Downloads,
        _ => FinderLocation.Recents,
    };

    void CascadePosition()
    {
        var wa = SystemParameters.WorkArea;
        int n = All.Count - 1;
        Left = wa.Left + (wa.Width - Width) / 2 - 60 + n % 6 * 24;
        Top = wa.Top + Math.Max(8, (wa.Height - Height) * 0.35) + n % 6 * 24;
        ApplyOffscreen();
    }

    void OnSettingsChanged()
    {
        _sidebarCol.Width = Settings.Current.FinderShowSidebar ? new GridLength(_sidebarCol.Width.Value > 0 ? Math.Max(140, _sidebarCol.Width.Value) : 180) : new GridLength(0);
        _pathBar.Visibility = Settings.Current.FinderShowPathBar ? Visibility.Visible : Visibility.Collapsed;
        _statusBar.Visibility = Settings.Current.FinderShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== chrome

    static Button ToolButton(string symbol, string tip, Action click, double iconSize = 17, double stroke = 1.9)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Content = new SymbolIcon { Symbol = symbol, Width = iconSize, Height = iconSize, StrokeWidth = stroke },
            ToolTip = tip,
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => click();
        return b;
    }

    static FrameworkElement WithChevron(string symbol, double size = 17)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new SymbolIcon { Symbol = symbol, Width = size, Height = size, StrokeWidth = 1.9 });
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.down", Width = 8, Height = 8, StrokeWidth = 2.8, Margin = new Thickness(3, 1, 0, 0) });
        return sp;
    }

    void BuildChrome()
    {
        _layout.ColumnDefinitions.Add(_sidebarCol);
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        if (!Settings.Current.FinderShowSidebar) _sidebarCol.Width = new GridLength(0);

        // ---------------- sidebar (transparent → acrylic vibrancy shows through)
        var sideGrid = new Grid();
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        sideGrid.RowDefinitions.Add(new RowDefinition());
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "SidebarTintBrush");
        Grid.SetRowSpan(tint, 2);
        sideGrid.Children.Add(tint);
        var lights = new TrafficLights { Margin = new Thickness(20, 0, 0, 0) };
        sideGrid.Children.Add(lights);
        var sideScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(sideScroll, 1);
        sideGrid.Children.Add(sideScroll);
        _sidebarHost.Child = sideGrid;
        _layout.Children.Add(_sidebarHost);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
        WindowChrome.SetIsHitTestVisibleInChrome(splitter, true);
        _layout.Children.Add(splitter);

        // ---------------- content column
        var content = new DockPanel { LastChildFill = true };
        content.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var leftEdge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = content };
        leftEdge.SetResourceReference(Border.BorderBrushProperty, "StrongSeparatorBrush");
        Grid.SetColumn(leftEdge, 1);
        _layout.Children.Add(leftEdge);

        // toolbar
        var toolbar = new Grid { Height = 52 };
        toolbar.SetResourceReference(Panel.BackgroundProperty, "ToolbarBackgroundBrush");
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _backBtn.Style = _fwdBtn.Style = (Style)Application.Current.Resources["ToolbarButton"];
        _backBtn.Content = new SymbolIcon { Symbol = "chevron.left", Width = 17, Height = 17, StrokeWidth = 2.1 };
        _fwdBtn.Content = new SymbolIcon { Symbol = "chevron.right", Width = 17, Height = 17, StrokeWidth = 2.1 };
        _backBtn.ToolTip = "Back"; _fwdBtn.ToolTip = "Forward";
        _backBtn.Width = _fwdBtn.Width = 30;
        WindowChrome.SetIsHitTestVisibleInChrome(_backBtn, true);
        WindowChrome.SetIsHitTestVisibleInChrome(_fwdBtn, true);
        _backBtn.Click += (_, _) => GoBack();
        _fwdBtn.Click += (_, _) => GoForward();
        nav.Children.Add(_backBtn); nav.Children.Add(_fwdBtn);
        toolbar.Children.Add(nav);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        _title.Margin = new Thickness(4, 0, 12, 1);
        Grid.SetColumn(_title, 1);
        toolbar.Children.Add(_title);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        string[] modes = { "icons", "list", "columns", "gallery" };
        string[] syms = { "grid", "list", "columns", "gallery" };
        string[] tips = { "Icons", "List", "Columns", "Gallery" };
        for (int i = 0; i < 4; i++)
        {
            string mode = modes[i];
            var rb = new RadioButton
            {
                Style = (Style)Application.Current.Resources["SegmentButton"], GroupName = "view" + GetHashCode(),
                Content = new SymbolIcon { Symbol = syms[i], Width = 16, Height = 16, StrokeWidth = 1.8 }, ToolTip = tips[i],
            };
            WindowChrome.SetIsHitTestVisibleInChrome(rb, true);
            rb.Checked += (_, _) => { if (_tab != null && _tab.ViewMode != mode) SetViewMode(mode); };
            _viewButtons[i] = rb;
            seg.Children.Add(rb);
        }
        right.Children.Add(seg);
        right.Children.Add(Spacer(8));
        var groupBtn = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = WithChevron("group"), ToolTip = "Sort By" };
        WindowChrome.SetIsHitTestVisibleInChrome(groupBtn, true);
        groupBtn.Click += (_, _) => ShowMenuUnder(groupBtn, SortMenuItems());
        right.Children.Add(groupBtn);
        right.Children.Add(Spacer(2));
        right.Children.Add(ToolButton("share", "Share", () => ShareSelection()));
        var tagBtn = ToolButton("tag", "Edit Tags", () => { });
        tagBtn.Click += (_, _) => ShowMenuUnder(tagBtn, new object[] { Mb.TagRow(SelectedCommonTags(), t => ToggleTag(t)) });
        right.Children.Add(tagBtn);
        var actionBtn = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = WithChevron("ellipsis.circle"), ToolTip = "Action" };
        WindowChrome.SetIsHitTestVisibleInChrome(actionBtn, true);
        actionBtn.Click += (_, _) =>
        {
            var sel = SelectedItems;
            ShowMenuUnder(actionBtn, sel.Count > 0 ? ItemMenuItems(sel) : BackgroundMenuItems());
        };
        right.Children.Add(actionBtn);
        right.Children.Add(Spacer(8));
        _search.Style = (Style)Application.Current.Resources["MacSearchField"];
        _search.Width = 170;
        WindowChrome.SetIsHitTestVisibleInChrome(_search, true);
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _search.Text = ""; FocusView(); e.Handled = true; }
            else if (e.Key == Key.Enter) { _searchTimer.Stop(); RunSearch(); e.Handled = true; }
        };
        right.Children.Add(_search);
        Grid.SetColumn(right, 2);
        toolbar.Children.Add(right);
        var tbLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        tbLine.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        Grid.SetColumnSpan(tbLine, 3);
        toolbar.Children.Add(tbLine);
        DockPanel.SetDock(toolbar, Dock.Top);
        content.Children.Add(toolbar);

        // tab bar
        _tabBar.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        _tabBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _tabBar.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        _tabBar.Child = _tabStrip;
        DockPanel.SetDock(_tabBar, Dock.Top);
        content.Children.Add(_tabBar);

        // search scope bar
        BuildScopeBar();
        DockPanel.SetDock(_scopeBar, Dock.Top);
        content.Children.Add(_scopeBar);

        // trash bar
        BuildTrashBar();
        DockPanel.SetDock(_trashBar, Dock.Top);
        content.Children.Add(_trashBar);

        // status bar + path bar (bottom)
        _statusBar.BorderThickness = new Thickness(0, 1, 0, 0);
        _statusBar.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        _statusBar.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        var statusGrid = new Grid();
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        statusGrid.Children.Add(_statusText);
        _sizeSlider.Style = (Style)Application.Current.Resources["MacSlider"];
        _sizeSlider.Value = Settings.Current.FinderIconSize;
        _sizeSlider.ValueChanged += (_, e) => { Settings.Current.FinderIconSize = Math.Round(e.NewValue / 4) * 4; Settings.Save(); };
        _sizeSlider.LayoutTransform = new ScaleTransform(0.75, 0.75);
        statusGrid.Children.Add(_sizeSlider);
        _statusBar.Child = statusGrid;
        _statusBar.Visibility = Settings.Current.FinderShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        content.Children.Add(_statusBar);

        _pathBar.BorderThickness = new Thickness(0, 1, 0, 0);
        _pathBar.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        _pathBar.SetResourceReference(Border.BackgroundProperty, "ContentBackgroundBrush");
        _pathBar.Padding = new Thickness(10, 0, 10, 0);
        _pathBar.Child = new ScrollViewer { Content = _pathCrumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        _pathBar.Visibility = Settings.Current.FinderShowPathBar ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(_pathBar, Dock.Bottom);
        content.Children.Add(_pathBar);

        var main = new Grid();
        main.Children.Add(_contentHost);
        _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _emptyHint.Child = _emptyText;
        main.Children.Add(_emptyHint);
        content.Children.Add(main);

        Content = _layout;
        BuildSidebar();
    }

    static FrameworkElement Spacer(double w) => new Border { Width = w };

    void BuildScopeBar()
    {
        _scopeBar.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        _scopeBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _scopeBar.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var lbl = new TextBlock { Text = "Search:", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(lbl);
        _scopeMac = ScopeToggle("This Mac", "mac");
        _scopeFolder = ScopeToggle("Folder", "folder");
        sp.Children.Add(_scopeMac);
        sp.Children.Add(_scopeFolder);
        _scopeBar.Child = sp;
    }

    ToggleButton ScopeToggle(string text, string scope)
    {
        var tb = new ToggleButton { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 22, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 4, 0) };
        var t = new TextBlock { Text = text, FontSize = 12 };
        tb.Content = t;
        tb.Checked += (_, _) => { tb.Background = Theme.Res("PressedBrush"); t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush"); };
        tb.Unchecked += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        tb.Click += (_, _) =>
        {
            _tab.SearchScope = scope;
            _scopeMac.IsChecked = scope == "mac";
            _scopeFolder.IsChecked = scope == "folder";
            RunSearch();
        };
        return tb;
    }

    void BuildTrashBar()
    {
        _trashBar.SetResourceReference(Border.BackgroundProperty, "ToolbarBackgroundBrush");
        _trashBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _trashBar.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var g = new Grid { Margin = new Thickness(14, 0, 12, 0) };
        var t = new TextBlock { Text = "Trash", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        g.Children.Add(t);
        var empty = new Button { Content = "Empty", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 60 };
        empty.Click += (_, _) => { FileOps.EmptyTrash(); Reload(false); };
        g.Children.Add(empty);
        _trashBar.Child = g;
    }

    // ------------------------------------------------------------------ sidebar

    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _sidebarRows.Clear();
        Section("Favorites");
        Row("Recents", "clock", FinderLocation.Recents);
        Row("Applications", "apps", FinderLocation.Applications);
        Row("Desktop", "desktop", FinderLocation.Desktop);
        Row("Documents", "doc", FinderLocation.Documents);
        Row("Downloads", "arrow.down.circle", FinderLocation.Downloads);
        Row(MenuBarName(), "house", FinderLocation.Home);
        foreach (var fav in Settings.Current.SidebarFavorites ?? new List<string>())
            if (Directory.Exists(fav)) Row(Path.GetFileName(fav.TrimEnd('\\')), "folder", fav, removable: true);

        string icloud = Path.Combine(FinderLocation.Home, "iCloudDrive");
        if (Directory.Exists(icloud) || (FinderLocation.ICloud != null && Directory.Exists(FinderLocation.ICloud)))
        {
            Section("iCloud");
            if (Directory.Exists(icloud)) Row("iCloud Drive", "icloud", icloud);
            if (FinderLocation.ICloud != null && Directory.Exists(FinderLocation.ICloud)) Row("OneDrive", "icloud", FinderLocation.ICloud);
        }

        Section("Locations");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                string name = FinderLocation.DisplayName(d.RootDirectory.FullName);
                Row(name, d.DriveType == DriveType.Removable || d.DriveType == DriveType.Network ? "externaldrive" : "internaldrive", d.RootDirectory.FullName);
            }
            catch { }
        }
        Row("Trash", "trash", FinderLocation.Trash);

        Section("Tags");
        foreach (var (id, name, color) in Theme.TagColors)
            TagRow(name, color, FinderLocation.TagPrefix + id);
    }

    static string MenuBarName() => Environment.UserName;

    void Section(string title)
    {
        var tb = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(18, _sidebar.Children.Count == 0 ? 0 : 14, 0, 4) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _sidebar.Children.Add(tb);
    }

    Border MakeRow(FrameworkElement icon, string text, string location)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(icon);
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(tb);
        var row = new Border { Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(8, 0, 6, 0), Background = Brushes.Transparent, Child = sp, Tag = location, AllowDrop = true };
        row.MouseLeftButtonUp += (_, _) => Navigate(location);
        row.DragOver += (_, e) =>
        {
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && (location == FinderLocation.Trash || Directory.Exists(location)))
            {
                e.Effects = location == FinderLocation.Trash ? DragDropEffects.Move : FileOps.ShouldMove((string[])e.Data.GetData(DataFormats.FileDrop), location, e.KeyStates) ? DragDropEffects.Move : DragDropEffects.Copy;
                row.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            }
            e.Handled = true;
        };
        row.DragLeave += (_, _) => HighlightSidebar();
        row.Drop += (_, e) =>
        {
            HighlightSidebar();
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            if (location == FinderLocation.Trash) FileOps.MoveToTrash(files);
            else FileOps.CopyOrMove(files, location, FileOps.ShouldMove(files, location, e.KeyStates));
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var items = new List<object>
            {
                Mb.Item("Open in New Tab", () => NewTab(location)),
                Mb.Item("Open in New Window", () => ShellHost.OpenFinder(location)),
            };
            if (!FinderLocation.IsVirtual(location))
            {
                items.Add(Mb.Sep());
                items.Add(Mb.Item("Get Info", () => GetInfoWindow.ShowFor(ItemForPath(location))));
                items.Add(Mb.Item("Open in Terminal", () => FileOps.OpenInTerminal(location)));
            }
            if ((Settings.Current.SidebarFavorites ?? new()).Contains(location))
                items.Add(Mb.Item("Remove from Sidebar", () => { Settings.Current.SidebarFavorites.Remove(location); Settings.Save(); BuildSidebar(); HighlightSidebar(); }));
            if (location == FinderLocation.Trash) { items.Add(Mb.Sep()); items.Add(Mb.Item("Empty Trash", () => FileOps.EmptyTrash())); }
            Mb.Context(items.ToArray()).IsOpen = true;
        };
        return row;
    }

    void Row(string text, string symbol, string location, bool removable = false)
    {
        var icon = new SymbolIcon { Symbol = symbol, Width = 17, Height = 17, StrokeWidth = 1.8 };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "IconTintBrush");
        var row = MakeRow(icon, text, location);
        _sidebarRows[location] = row;
        _sidebar.Children.Add(row);
    }

    void TagRow(string text, Color color, string location)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(color), Margin = new Thickness(3, 0, 3, 0) };
        var row = MakeRow(dot, text, location);
        row.AllowDrop = false;
        _sidebarRows[location] = row;
        _sidebar.Children.Add(row);
    }

    void HighlightSidebar()
    {
        string loc = _tab?.Location ?? "";
        foreach (var (key, row) in _sidebarRows)
        {
            bool on = string.Equals(key.TrimEnd('\\'), loc.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(_tab?.Search);
            if (on) row.SetResourceReference(Border.BackgroundProperty, "SidebarSelectionActiveBrush");
            else row.Background = Brushes.Transparent;
        }
    }

    // ------------------------------------------------------------------ tabs

    void RefreshTabBar()
    {
        _tabBar.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _tabStrip.Children.Clear();
        _tabStrip.ColumnDefinitions.Clear();
        if (_tabs.Count <= 1) return;
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabStrip.ColumnDefinitions.Add(new ColumnDefinition());
            var t = _tabs[i];
            bool active = t == _tab;
            var cell = new Grid { Background = active ? Theme.Res("ContentBackgroundBrush") : Brushes.Transparent };
            if (!active) cell.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
            var title = new TextBlock { Text = TitleFor(t), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(24, 0, 24, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, active ? "LabelBrush" : "SecondaryLabelBrush");
            cell.Children.Add(title);
            var close = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 18, MinWidth = 18, Width = 18, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Hidden, Content = new SymbolIcon { Symbol = "xmark", Width = 10, Height = 10, StrokeWidth = 2.6 } };
            var tabRef = t;
            close.Click += (_, _) => CloseTab(tabRef);
            cell.Children.Add(close);
            cell.MouseEnter += (_, _) => close.Visibility = Visibility.Visible;
            cell.MouseLeave += (_, _) => close.Visibility = Visibility.Hidden;
            cell.MouseLeftButtonUp += (_, _) => SwitchTab(tabRef);
            if (i > 0)
            {
                var sep = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left };
                sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
                cell.Children.Add(sep);
            }
            Grid.SetColumn(cell, i);
            _tabStrip.Children.Add(cell);
        }
        _tabStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        var plus = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 22, MinWidth = 22, Content = new SymbolIcon { Symbol = "plus", Width = 12, Height = 12, StrokeWidth = 2.2 } };
        plus.Click += (_, _) => NewTab(null);
        Grid.SetColumn(plus, _tabs.Count);
        _tabStrip.Children.Add(plus);
    }

    static string TitleFor(FinderTab t) => !string.IsNullOrEmpty(t.Search) ? $"Searching “{t.Search}”" : FinderLocation.DisplayName(t.DisplayFolder ?? t.Location);

    public void NewTab(string location)
    {
        SaveTabState();
        var tab = new FinderTab();
        _tabs.Insert(_tabs.IndexOf(_tab) + 1, tab);
        _tab = tab;
        _search.Text = "";
        Navigate(location ?? DefaultLocation(), false);
        RefreshTabBar();
    }

    void SwitchTab(FinderTab t)
    {
        if (t == _tab) return;
        SaveTabState();
        _tab = t;
        _search.Text = t.Search;
        ApplyViewMode();
        Reload(false);
        UpdateChrome();
        RefreshTabBar();
    }

    void CloseTab(FinderTab t)
    {
        if (_tabs.Count <= 1) { Close(); return; }
        int idx = _tabs.IndexOf(t);
        _tabs.Remove(t);
        if (t == _tab) { _tab = _tabs[Math.Min(idx, _tabs.Count - 1)]; _search.Text = _tab.Search; ApplyViewMode(); Reload(false); UpdateChrome(); }
        RefreshTabBar();
    }

    void SaveTabState() { if (_tab != null) _tab.Search = _search.Text; }

    protected override bool HandleCloseShortcut()
    {
        if (_tabs.Count > 1) CloseTab(_tab); else Close();
        return true;
    }

    // ================================================================== navigation

    public void Navigate(string location, bool addHistory = true)
    {
        if (string.IsNullOrEmpty(location)) return;
        if (!FinderLocation.IsVirtual(location) && !Directory.Exists(location))
        {
            if (File.Exists(location)) { AppCatalog.OpenFile(location); return; }
            ShellHost.ShowAlert($"The folder “{Path.GetFileName(location)}” can’t be found.", "");
            return;
        }
        if (addHistory && _tab.Location != null && !string.Equals(_tab.Location, location, StringComparison.OrdinalIgnoreCase))
        {
            _tab.Back.Add(_tab.Location);
            _tab.Forward.Clear();
        }
        _tab.Location = location;
        _tab.DisplayFolder = null;
        _tab.Search = "";
        if (_search.Text.Length > 0) { _search.Text = ""; _searchTimer.Stop(); }
        _children.Clear();
        _tab.ViewMode = Settings.Current.FolderViews.TryGetValue(location, out var vm) ? vm
            : location == FinderLocation.Applications ? "icons"
            : location == FinderLocation.Recents ? "icons"
            : Settings.Current.FinderDefaultView;
        ApplyViewMode();
        _view?.ScrollToTop();
        Reload(false);
        UpdateChrome();
        RefreshTabBar();
    }

    public void GoBack()
    {
        if (_tab.Back.Count == 0) return;
        string prev = _tab.Back[^1];
        _tab.Back.RemoveAt(_tab.Back.Count - 1);
        _tab.Forward.Add(_tab.Location);
        string cur = _tab.Location;
        Navigate(prev, false);
        _pendingSelect = cur;
    }

    public void GoForward()
    {
        if (_tab.Forward.Count == 0) return;
        string next = _tab.Forward[^1];
        _tab.Forward.RemoveAt(_tab.Forward.Count - 1);
        _tab.Back.Add(_tab.Location);
        Navigate(next, false);
    }

    public void GoUp()
    {
        string folder = CurrentFolder ?? _tab.Location;
        if (FinderLocation.IsVirtual(folder)) return;
        var parent = Directory.GetParent(folder.TrimEnd('\\'));
        if (parent == null) { Navigate(FinderLocation.Computer); _pendingSelect = folder; return; }
        string from = folder;
        Navigate(parent.FullName);
        _pendingSelect = from;
    }

    void UpdateChrome()
    {
        string title = TitleFor(_tab);
        _title.Text = title;
        Title = title;
        _backBtn.IsEnabled = _tab.Back.Count > 0;
        _fwdBtn.IsEnabled = _tab.Forward.Count > 0;
        _trashBar.Visibility = _tab.Location == FinderLocation.Trash && string.IsNullOrEmpty(_tab.Search) ? Visibility.Visible : Visibility.Collapsed;
        _scopeBar.Visibility = string.IsNullOrEmpty(_tab.Search) ? Visibility.Collapsed : Visibility.Visible;
        if (_scopeFolder != null)
        {
            string folderName = FinderLocation.DisplayName(_tab.Location);
            ((TextBlock)_scopeFolder.Content).Text = $"“{folderName}”";
            _scopeFolder.Visibility = FinderLocation.IsVirtual(_tab.Location) ? Visibility.Collapsed : Visibility.Visible;
            _scopeMac.IsChecked = _tab.SearchScope == "mac";
            _scopeFolder.IsChecked = _tab.SearchScope == "folder";
        }
        HighlightSidebar();
        BuildPathBar();
        UpdateStatus();
    }

    void BuildPathBar()
    {
        _pathCrumbs.Children.Clear();
        string loc = _tab.DisplayFolder ?? _tab.Location;
        var parts = new List<(string name, string path, FrameworkElement icon)>();
        if (FinderLocation.IsVirtual(loc) || loc == null)
        {
            parts.Add((FinderLocation.DisplayName(loc), loc, new SymbolIcon { Symbol = loc == FinderLocation.Trash ? "trash" : loc == FinderLocation.Applications ? "apps" : "clock", Width = 13, Height = 13 }));
        }
        else
        {
            var chain = new List<string>();
            var d = new DirectoryInfo(loc);
            while (d != null) { chain.Insert(0, d.FullName); d = d.Parent; }
            foreach (var p in chain)
            {
                bool drive = p.Length <= 3;
                FrameworkElement icon = new Image { Source = drive ? MacIcons.Drive() : MacIcons.Folder(MacIcons.FolderGlyphFor(p)), Width = 15, Height = 15 };
                parts.Add((FinderLocation.DisplayName(p), p, icon));
            }
        }
        for (int i = 0; i < parts.Count; i++)
        {
            var (name, path, icon) = parts[i];
            if (i > 0)
            {
                var chev = new SymbolIcon { Symbol = "chevron.right", Width = 8, Height = 8, StrokeWidth = 2.8, Margin = new Thickness(6, 0, 6, 0) };
                chev.SetResourceReference(SymbolIcon.ForegroundProperty, "TertiaryLabelBrush");
                _pathCrumbs.Children.Add(chev);
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            if (icon is SymbolIcon si) si.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
            icon.Margin = new Thickness(0, 0, 4, 0);
            sp.Children.Add(icon);
            var tb = new TextBlock { Text = name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            sp.Children.Add(tb);
            string target = path;
            sp.MouseLeftButtonUp += (_, e) => { if (e.ClickCount == 1) Navigate(target); };
            _pathCrumbs.Children.Add(sp);
        }
    }

    // ================================================================== views

    public void SetViewMode(string mode)
    {
        if (!string.IsNullOrEmpty(_tab.Search) && mode == "columns") mode = "list";
        _tab.ViewMode = mode;
        if (!string.IsNullOrEmpty(_tab.Location) && string.IsNullOrEmpty(_tab.Search))
        {
            Settings.Current.FolderViews[_tab.Location] = mode;
            Settings.Save(false);
        }
        var selPaths = SelectedItems.Select(i => i.FullPath).ToList();
        ApplyViewMode();
        foreach (var it in _all) it.IsExpanded = false;
        Rebuild();
        foreach (var it in _display) it.IsSelected = selPaths.Contains(it.FullPath);
        SelectionChanged();
        ShellHost.MenuBar?.RebuildMenus();
    }

    void ApplyViewMode()
    {
        string mode = _tab.ViewMode ?? "icons";
        if (!string.IsNullOrEmpty(_tab.Search) && mode == "columns") mode = _tab.ViewMode = "list";
        var v = mode switch { "list" => (FinderView)_listView, "columns" => _columnView, "gallery" => _galleryView, _ => _iconView };
        if (mode == "columns") _columnView.RootFolder = _tab.Location;
        if (_view != v)
        {
            _view = v;
            _contentHost.Child = v.Root;
            v.Active = IsActive;
        }
        int idx = mode switch { "list" => 1, "columns" => 2, "gallery" => 3, _ => 0 };
        _viewButtons[idx].IsChecked = true;
        _sizeSlider.Visibility = mode == "icons" ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusView() => _view?.FocusView();
    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

    // ================================================================== loading

    public void Reload(bool preserve)
    {
        if (!string.IsNullOrEmpty(_tab.Search)) { RunSearch(); return; }
        var keepSel = preserve ? SelectedItems.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var expanded = preserve ? _display.Where(i => i.IsExpanded).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        string loc = _tab.Location;
        bool hidden = Settings.Current.ShowHiddenFiles;
        SetupWatcher(loc);
        if (loc == FinderLocation.Applications)
        {
            var apps = AppCatalog.Apps.Select(a => new FileItem { Name = a.Name, IsApp = true, AppTarget = a.ParsingName, FullPath = a.TargetPath ?? ("shell:AppsFolder\\" + a.ParsingName) }).ToList();
            OnItemsLoaded(apps, keepSel, expanded);
            return;
        }
        Task.Run(() => LoadLocation(loc, hidden, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (t.Result.error != null)
                {
                    OnItemsLoaded(new List<FileItem>(), null, null);
                    _emptyText.Text = t.Result.error;
                    _emptyHint.Visibility = Visibility.Visible;
                    return;
                }
                OnItemsLoaded(t.Result.items, keepSel, expanded);
            });
        });
    }

    public static (List<FileItem> items, string error) LoadLocation(string loc, bool showHidden, CancellationToken ct)
    {
        try
        {
            if (loc == FinderLocation.Recents) return (LoadRecents(ct), null);
            if (loc == FinderLocation.Computer)
            {
                var list = new List<FileItem>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady) continue;
                        list.Add(new FileItem { Name = FinderLocation.DisplayName(d.RootDirectory.FullName), FullPath = d.RootDirectory.FullName, IsFolder = true, IsDrive = true, Size = d.TotalSize });
                    }
                    catch { }
                }
                return (list, null);
            }
            if (loc == FinderLocation.Trash) return (LoadTrash(), null);
            if (loc.StartsWith(FinderLocation.TagPrefix))
            {
                string tag = loc[FinderLocation.TagPrefix.Length..];
                var list = new List<FileItem>();
                foreach (var (path, tags) in Settings.Current.Tags.ToList())
                {
                    if (!tags.Contains(tag)) continue;
                    if (Directory.Exists(path)) list.Add(FileItem.FromInfo(new DirectoryInfo(path)));
                    else if (File.Exists(path)) list.Add(FileItem.FromInfo(new FileInfo(path)));
                }
                return (list, null);
            }
            var di = new DirectoryInfo(loc);
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            var items = new List<FileItem>();
            foreach (var fi in di.EnumerateFileSystemInfos("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                var item = FileItem.FromInfo(fi);
                if (!showHidden && (item.IsHidden || fi.Name.StartsWith("."))) continue;
                if (!showHidden && fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(item);
            }
            return (items, null);
        }
        catch (UnauthorizedAccessException) { return (null, "You don’t have permission to see this folder’s contents."); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    internal static List<FileItem> LoadRecents(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(FileItem item, DateTime when)>();
        foreach (var p in Settings.Current.RecentDocs.ToList())
            if (File.Exists(p) && seen.Add(p)) result.Add((FileItem.FromInfo(new FileInfo(p)), DateTime.Now));
        try
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var links = new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(160).ToList();
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            foreach (var l in links)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string target = wsh.CreateShortcut(l.FullName).TargetPath;
                    if (string.IsNullOrEmpty(target) || !File.Exists(target) || !seen.Add(target)) continue;
                    result.Add((FileItem.FromInfo(new FileInfo(target)), l.LastWriteTime));
                }
                catch { }
                if (result.Count >= 120) break;
            }
        }
        catch { }
        if (result.Count < 30)
        {
            // Windows' recent-items list is often empty or disabled; like Spotlight-backed Recents on a Mac,
            // fall back to recently changed files in the user's main folders.
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            var cutoff = DateTime.Now.AddDays(-45);
            foreach (var root in new[] { FinderLocation.Desktop, FinderLocation.Documents, FinderLocation.Downloads, FinderLocation.Pictures, FinderLocation.Movies, FinderLocation.Music })
            {
                if (ct.IsCancellationRequested || string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
                    {
                        if (f.LastWriteTime < cutoff || f.Name.StartsWith(".") || f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (f.FullName.Contains("\\node_modules\\") || f.FullName.Contains("\\.git\\") || f.FullName.Contains("\\bin\\") || f.FullName.Contains("\\obj\\")) continue;
                        if (seen.Add(f.FullName)) result.Add((FileItem.FromInfo(f), f.LastWriteTime));
                    }
                }
                catch { }
            }
        }
        return result.OrderByDescending(r => r.when).Take(150).Select(r => r.item).ToList();
    }

    static List<FileItem> LoadTrash()
    {
        var list = new List<FileItem>();
        var t = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic bin = shell.NameSpace(10);
                foreach (dynamic it in bin.Items())
                {
                    try
                    {
                        string name = it.Name;
                        string path = it.Path;
                        bool folder = it.IsFolder;
                        var item = new FileItem { Name = name, FullPath = path, IsFolder = folder && Directory.Exists(path), IsTrashItem = true };
                        try { item.OriginalLocation = it.ExtendedProperty("System.Recycle.DeletedFrom") as string; } catch { }
                        try { if (it.ExtendedProperty("System.Recycle.DateDeleted") is DateTime dd) item.Modified = dd; } catch { }
                        try { item.Size = Convert.ToInt64(it.Size); } catch { }
                        if (item.Modified == default) try { item.Modified = File.GetLastWriteTime(path); } catch { }
                        list.Add(item);
                    }
                    catch { }
                }
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return list;
    }

    void OnItemsLoaded(List<FileItem> items, HashSet<string> keepSel, HashSet<string> expanded)
    {
        _emptyHint.Visibility = Visibility.Collapsed;
        _all = items ?? new List<FileItem>();
        if (expanded != null)
            foreach (var it in _all.Where(i => expanded.Contains(i.FullPath))) { it.IsExpanded = true; }
        Rebuild();
        if (keepSel != null) foreach (var it in _display) it.IsSelected = keepSel.Contains(it.FullPath);
        if (_pendingSelect != null)
        {
            var target = _display.FirstOrDefault(i => string.Equals(i.FullPath?.TrimEnd('\\'), _pendingSelect.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            _pendingSelect = null;
            if (target != null)
            {
                foreach (var it in _display) it.IsSelected = it == target;
                _anchor = target;
                Dispatcher.BeginInvoke(() => _view.Reveal(target), DispatcherPriority.Loaded);
            }
        }
        if (_all.Count == 0 && _emptyHint.Visibility != Visibility.Visible && _tab.Location == FinderLocation.Trash)
        {
            _emptyText.Text = "Trash is empty";
            _emptyHint.Visibility = Visibility.Visible;
        }
        SelectionChanged();
        if (_renameAfterLoad != null) Dispatcher.BeginInvoke(TryPendingRename, DispatcherPriority.Loaded);
    }

    /// <summary>Sorts, expands and pushes items into the current view.</summary>
    void Rebuild()
    {
        var sorted = Sort(_all);
        var flat = new List<FileItem>();
        void AddRec(IEnumerable<FileItem> list)
        {
            foreach (var it in list)
            {
                flat.Add(it);
                if (it.IsExpanded && _tab.ViewMode == "list" && _children.TryGetValue(it.FullPath, out var kids)) AddRec(Sort(kids));
            }
        }
        AddRec(sorted);
        _display = flat;
        if (_tab.ViewMode == "columns") _columnView.RootFolder = _tab.Location;
        _view.Show(_display);
        UpdateStatus();
    }

    List<FileItem> Sort(IEnumerable<FileItem> items)
    {
        Comparison<FileItem> cmp = SortKey switch
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
            return SortAscending ? c : -c;
        });
        return list;
    }

    // Folders share one sort; Recents keeps its own, newest first (Date Modified) like macOS.
    string _folderSortKey = "name", _recentsSortKey = "date";
    bool _folderSortAsc = true, _recentsSortAsc = true;
    bool InRecents => _tab?.Location == FinderLocation.Recents;

    public string SortKey
    {
        get => InRecents ? _recentsSortKey : _folderSortKey;
        private set { if (InRecents) _recentsSortKey = value; else _folderSortKey = value; }
    }

    public bool SortAscending
    {
        get => InRecents ? _recentsSortAsc : _folderSortAsc;
        private set { if (InRecents) _recentsSortAsc = value; else _folderSortAsc = value; }
    }

    public void SortBy(string key)
    {
        if (SortKey == key) SortAscending = !SortAscending;
        else { SortKey = key; SortAscending = true; }
        var sel = SelectedItems;
        Rebuild();
        foreach (var s in sel) s.IsSelected = true;
        SelectionChanged();
    }

    void SetupWatcher(string loc)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (FinderLocation.IsVirtual(loc) || !Directory.Exists(loc)) return;
        try
        {
            _watcher = new FileSystemWatcher(loc) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    public void LoadChildren(string folder, Action<List<FileItem>> done)
    {
        bool hidden = Settings.Current.ShowHiddenFiles;
        Task.Run(() => LoadLocation(folder, hidden, CancellationToken.None)).ContinueWith(t =>
            Dispatcher.BeginInvoke(() => done(Sort(t.Result.items ?? new List<FileItem>()))));
    }

    public void ToggleExpand(FileItem item)
    {
        if (!item.IsFolder || _tab.ViewMode != "list") return;
        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            Rebuild();
            return;
        }
        LoadChildren(item.FullPath, kids =>
        {
            foreach (var k in kids) { k.Depth = item.Depth + 1; k.ParentItem = item; }
            _children[item.FullPath] = kids;
            item.IsExpanded = true;
            Rebuild();
        });
    }

    public void ActiveColumnChanged(string folder)
    {
        _tab.DisplayFolder = folder;
        _title.Text = Title = FinderLocation.DisplayName(folder);
        BuildPathBar();
        UpdateStatus();
    }

    // ================================================================== search

    void RunSearch()
    {
        string q = _search.Text.Trim();
        _tab.Search = q;
        _searchCts?.Cancel();
        if (q.Length == 0)
        {
            ApplyViewMode();
            UpdateChrome();
            RefreshTabBar();
            _view?.ScrollToTop();
            Reload(false);
            return;
        }
        if (_tab.ViewMode == "columns") { _tab.ViewMode = "list"; }
        if (FinderLocation.IsVirtual(_tab.Location)) _tab.SearchScope = "mac";
        ApplyViewMode();
        UpdateChrome();
        RefreshTabBar();
        var cts = _searchCts = new CancellationTokenSource();
        string scope = _tab.SearchScope;
        string folder = _tab.Location;
        bool hidden = Settings.Current.ShowHiddenFiles;
        _all = new List<FileItem>();
        _view?.ScrollToTop();
        Rebuild();
        Task.Run(() =>
        {
            var results = scope == "mac" ? SearchIndex(q, 400) : null;
            if (results == null || results.Count == 0)
            {
                string root = scope == "mac" ? FinderLocation.Home : folder;
                results = SearchWalk(root, q, hidden, 1500, cts.Token, batch =>
                {
                    if (!cts.IsCancellationRequested) Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) { _all = batch; Rebuild(); } });
                });
            }
            return results;
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                _all = t.Result;
                Rebuild();
                SelectionChanged();
                if (_all.Count == 0) { _emptyText.Text = "No Results"; _emptyHint.Visibility = Visibility.Visible; }
                else _emptyHint.Visibility = Visibility.Collapsed;
            });
        });
    }

    /// <summary>Queries the Windows Search index (fast "This Mac" search).</summary>
    public static List<FileItem> SearchIndex(string q, int max)
    {
        var list = new List<FileItem>();
        try
        {
            string term = q.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
            dynamic conn = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection"));
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            string sql = $"SELECT TOP {max} System.ItemPathDisplay FROM SystemIndex WHERE System.FileName LIKE '%{term}%' AND (SCOPE='file:') ORDER BY System.DateModified DESC";
            dynamic rs = conn.Execute(sql);
            while (!rs.EOF)
            {
                string path = rs.Fields.Item(0).Value as string;
                if (!string.IsNullOrEmpty(path))
                {
                    if (Directory.Exists(path)) list.Add(FileItem.FromInfo(new DirectoryInfo(path)));
                    else if (File.Exists(path)) list.Add(FileItem.FromInfo(new FileInfo(path)));
                }
                rs.MoveNext();
            }
            rs.Close();
            conn.Close();
        }
        catch { }
        return list;
    }

    static List<FileItem> SearchWalk(string root, string q, bool hidden, int max, CancellationToken ct, Action<List<FileItem>> progress)
    {
        var list = new List<FileItem>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = hidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
        int lastReport = 0;
        try
        {
            foreach (var fi in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + q + "*", opts))
            {
                if (ct.IsCancellationRequested) break;
                list.Add(FileItem.FromInfo(fi));
                if (list.Count >= max) break;
                if (list.Count - lastReport >= 60) { lastReport = list.Count; progress(list.ToList()); }
            }
        }
        catch { }
        return list;
    }

    // ================================================================== selection

    public List<FileItem> SelectedItems => (_view?.CurrentItems ?? _display).Where(i => i.IsSelected).ToList();
    IReadOnlyList<FileItem> Scope => _view?.CurrentItems ?? _display;

    void SelectOnly(FileItem item)
    {
        foreach (var i in Scope) if (i != item) i.IsSelected = false;
        if (item != null) item.IsSelected = true;
        _anchor = item;
        SelectionChanged();
    }

    public void SelectAll()
    {
        foreach (var i in Scope) i.IsSelected = true;
        SelectionChanged();
    }

    void ClearSelection()
    {
        foreach (var i in Scope) i.IsSelected = false;
        SelectionChanged();
    }

    void SelectionChanged()
    {
        _view?.OnSelectionChanged();
        UpdateSelCorners();
        UpdateStatus();
        QuickLookWindow.Current?.ShowItem(SelectedItems.FirstOrDefault());
    }

    void UpdateSelCorners()
    {
        if (_tab.ViewMode != "list") return;
        for (int i = 0; i < _display.Count; i++)
        {
            var it = _display[i];
            if (!it.IsSelected) continue;
            bool prev = i > 0 && _display[i - 1].IsSelected;
            bool next = i < _display.Count - 1 && _display[i + 1].IsSelected;
            it.SelCorner = new CornerRadius(prev ? 0 : 5, prev ? 0 : 5, next ? 0 : 5, next ? 0 : 5);
        }
    }

    public void SelectPathWhenLoaded(string path) => _pendingSelect = path;

    void UpdateStatus()
    {
        int n = Scope.Count;
        int sel = Scope.Count(i => i.IsSelected);
        string free = "";
        try
        {
            string folder = CurrentFolder;
            if (folder != null)
            {
                var d = new DriveInfo(Path.GetPathRoot(folder)!);
                free = ", " + FileItem.FormatSize(d.AvailableFreeSpace) + " available";
            }
        }
        catch { }
        _statusText.Text = sel > 0 ? $"{sel} of {n} selected{free}" : $"{n} item{(n == 1 ? "" : "s")}{free}";
    }

    // ================================================================== IFinderHost

    public void ItemMouseDown(FileItem item, ModifierKeys mods, FinderView view)
    {
        CommitAnyRename();
        _deferSingle = null;
        if (mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Shift))
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
            SelectionChanged();
        }
        else if (mods.HasFlag(ModifierKeys.Shift) && _anchor != null && Scope.Contains(_anchor))
        {
            var list = Scope.ToList();
            int a = list.IndexOf(_anchor), b = list.IndexOf(item);
            if (a > b) (a, b) = (b, a);
            for (int i = 0; i < list.Count; i++) list[i].IsSelected = (i >= a && i <= b) || (mods.HasFlag(ModifierKeys.Control) && list[i].IsSelected);
            SelectionChanged();
        }
        else if (!item.IsSelected) SelectOnly(item);
        else _deferSingle = item;
    }

    public void ItemMouseUp(FileItem item, ModifierKeys mods)
    {
        if (_deferSingle == item && mods == ModifierKeys.None)
        {
            bool wasOnlySelection = Scope.Count(i => i.IsSelected) == 1;
            SelectOnly(item);
            // macOS: click on an already-selected item's name after a pause starts renaming
            if (wasOnlySelection && _lastClickItem == item && (DateTime.Now - _lastClickTime).TotalMilliseconds is > 600 and < 3000)
                BeginRename();
        }
        _deferSingle = null;
        _lastClickItem = item;
        _lastClickTime = DateTime.Now;
    }
    FileItem _lastClickItem;
    DateTime _lastClickTime;

    public void ItemDoubleClick(FileItem item) => Open(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));

    public void ItemRightDown(FileItem item)
    {
        CommitAnyRename();
        if (!item.IsSelected) SelectOnly(item);
    }

    public void BackgroundMouseDown(ModifierKeys mods)
    {
        CommitAnyRename();
        if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) { _marqueeBase = null; ClearSelection(); }
        else _marqueeBase = SelectedItems.ToHashSet();
    }
    HashSet<FileItem> _marqueeBase;

    public void MarqueeSelect(HashSet<FileItem> hits, bool additive)
    {
        foreach (var i in Scope)
            i.IsSelected = hits.Contains(i) || (additive && _marqueeBase != null && _marqueeBase.Contains(i));
        SelectionChanged();
    }

    public void ItemContextMenu(FileItem item, FrameworkElement target) => Mb.Context(ItemMenuItems(SelectedItems)).IsOpen = true;
    public void BackgroundContextMenu(FrameworkElement target) => Mb.Context(BackgroundMenuItems()).IsOpen = true;

    public void ViewFocused(FinderView view) { view.Active = IsActive; }

    // ------------------------------------------------------------------ drag & drop

    FileItem _dropHighlight;

    public void StartDrag(FileItem item, FrameworkElement source)
    {
        var sel = SelectedItems;
        if (sel.Count > 0 && sel.All(i => i.IsApp))
        {
            // apps (incl. PWAs) travel as app identities so the Dock can pin them
            var appData = new DataObject();
            appData.SetData(Shell.DockWindow.AppDragFormat, string.Join("\n", sel.Select(i => i.AppTarget)));
            DragGhost.Run(source, appData, DragDropEffects.Copy | DragDropEffects.Link, sel.Select(i => i.Icon).Where(i => i != null).ToList(), 56);
            return;
        }
        var paths = SelectedItems.Select(i => i.IsApp ? AppCatalog.FindByParsingName(i.AppTarget)?.TargetPath : i.FullPath)
            .Where(p => p != null && (File.Exists(p) || Directory.Exists(p))).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        var icons = SelectedItems.Select(i => i.Icon).Where(i => i != null).ToList();
        DragGhost.Run(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, icons, 56);
    }

    string DropFolderFor(FileItem folder)
    {
        if (folder != null && !folder.IsTrashItem) return folder.FullPath;
        if (_tab.Location == FinderLocation.Trash) return FinderLocation.Trash;
        return CurrentFolder;
    }

    public void DragOverTarget(FileItem folder, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (_dropHighlight != folder) { if (_dropHighlight != null) _dropHighlight.IsDropTarget = false; _dropHighlight = folder; }
        if (WebDrop.Has(e.Data))
        {
            // a picture (or a link to a file) from a web browser: saved into this folder
            string wd = DropFolderFor(folder);
            if (wd == null || wd == FinderLocation.Trash || FinderLocation.IsVirtual(wd)) return;
            if (folder != null) folder.IsDropTarget = true;
            e.Effects = DragDropEffects.Copy;
            return;
        }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        string dest = DropFolderFor(folder);
        if (dest == null) return;
        if (folder != null && files.Any(f => string.Equals(f, folder.FullPath, StringComparison.OrdinalIgnoreCase))) { folder = null; dest = DropFolderFor(null); }
        if (dest == FinderLocation.Trash) { e.Effects = DragDropEffects.Move; return; }
        if (folder == null && files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        if (folder != null) folder.IsDropTarget = true;
        e.Effects = FileOps.ShouldMove(files, dest, e.KeyStates) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    public void DragLeft()
    {
        if (_dropHighlight != null) { _dropHighlight.IsDropTarget = false; _dropHighlight = null; }
    }

    public void DropOn(FileItem folder, DragEventArgs e)
    {
        DragLeft();
        if (WebDrop.Has(e.Data))
        {
            string wd = DropFolderFor(folder);
            if (wd == null || wd == FinderLocation.Trash || FinderLocation.IsVirtual(wd)) return;
            var webItems = WebDrop.Extract(e.Data);   // (the browser's data is only there during the drop)
            if (webItems.Count > 0) _ = WebDrop.SaveAsync(webItems, wd, null);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (folder != null && files.Any(f => string.Equals(f, folder.FullPath, StringComparison.OrdinalIgnoreCase))) folder = null;
        string dest = DropFolderFor(folder);
        if (dest == null) return;
        var keys = e.KeyStates;
        Dispatcher.BeginInvoke(() =>
        {
            if (dest == FinderLocation.Trash) FileOps.MoveToTrash(files);
            else
            {
                if (folder == null && files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
                FileOps.CopyOrMove(files, dest, FileOps.ShouldMove(files, dest, keys));
            }
        });
    }

    // ------------------------------------------------------------------ rename

    FileItem _renaming;

    public void BeginRename()
    {
        var sel = SelectedItems;
        if (sel.Count != 1) return;
        var item = sel[0];
        if (item.IsApp || item.IsTrashItem || item.IsDrive || FinderLocation.IsVirtual(item.FullPath)) return;
        CommitAnyRename();
        _renaming = item;
        item.IsRenaming = true;
        _view.Reveal(item);
        Dispatcher.BeginInvoke(() =>
        {
            var tb = _view.FindRenameBox(item);
            if (tb == null) { item.IsRenaming = false; _renaming = null; return; }
            tb.Text = item.Name;
            tb.Focus();
            int dot = item.IsFolder ? -1 : item.Name.LastIndexOf('.');
            if (dot > 0) tb.Select(0, dot); else tb.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    void CommitAnyRename()
    {
        if (_renaming != null && _renaming.IsRenaming)
        {
            var tb = _view.FindRenameBox(_renaming);
            if (tb != null) CommitRename(_renaming, tb.Text); else CancelRename(_renaming);
        }
    }

    public void CommitRename(FileItem item, string newName)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        _renaming = null;
        if (!string.IsNullOrWhiteSpace(newName) && newName != item.Name)
        {
            string result = FileOps.Rename(item.FullPath, newName);
            if (result != null)
            {
                item.FullPath = result;
                item.Name = Path.GetFileName(result.TrimEnd('\\'));
                _pendingSelect = result;
            }
        }
        FocusView();
    }

    public void CancelRename(FileItem item)
    {
        item.IsRenaming = false;
        _renaming = null;
        FocusView();
    }

    FileItem ItemForPath(string path)
    {
        if (Directory.Exists(path)) return FileItem.FromInfo(new DirectoryInfo(path));
        if (File.Exists(path)) return FileItem.FromInfo(new FileInfo(path));
        return new FileItem { Name = FinderLocation.DisplayName(path), FullPath = path, IsFolder = true };
    }
}
