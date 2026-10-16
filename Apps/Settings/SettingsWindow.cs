using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps;

/// <summary>
/// System Settings (macOS Sonoma/Sequoia layout): a sidebar of panes; a pane can open pages of its own (General ›
/// About, Privacy & Security › Camera …) that the back and forward buttons walk through, and sheets. Nearly
/// everything is set here, through Windows' own APIs - Windows Settings is only offered for what Windows keeps to itself.
/// </summary>
public partial class SettingsWindow : MacWindow
{
    public static SettingsWindow Instance { get; private set; }

    /// <summary>id, name, symbol, gradient top, gradient bottom ("-" = a gap between groups).</summary>
    public static readonly (string id, string name, string sym, string top, string bottom)[] Panes =
    {
        ("wifi", "Wi-Fi", "wifi", "#3D9BFF", "#0A6CFF"),
        ("bluetooth", "Bluetooth", "bluetooth", "#3D9BFF", "#0A6CFF"),
        ("network", "Network", "globe", "#3D9BFF", "#0A6CFF"),
        ("-", "", "", "", ""),
        ("notifications", "Notifications", "bell", "#FF6259", "#E8342B"),
        ("sound", "Sound", "speaker", "#FF5E7E", "#E9304F"),
        ("focus", "Focus", "moon", "#7A78F0", "#4C49D6"),
        ("-", "", "", "", ""),
        ("general", "General", "gear", "#A3A3A8", "#7C7C81"),
        ("appearance", "Appearance", "circle.lefthalf", "#3A3A3C", "#141414"),
        ("accessibility", "Accessibility", "accessibility", "#3D9BFF", "#0A6CFF"),
        ("controlcenter", "Control Center", "controlcenter", "#A3A3A8", "#7C7C81"),
        ("spotlight", "Spotlight", "magnifyingglass", "#A571F2", "#5E5CE6"),
        ("privacy", "Privacy & Security", "hand", "#3D9BFF", "#0A6CFF"),
        ("-", "", "", "", ""),
        ("dock", "Desktop & Dock", "dock", "#3A3A3C", "#141414"),
        ("displays", "Displays", "display", "#3D9BFF", "#0A6CFF"),
        ("wallpaper", "Wallpaper", "wallpaper", "#62D0FA", "#1AA7E8"),
        ("screensaver", "Screen Saver", "screensaver", "#5FD3E8", "#16A5C9"),
        ("battery", "Battery", "battery", "#5DD879", "#2FB24C"),
        ("-", "", "", "", ""),
        ("lockscreen", "Lock Screen", "lock", "#3A3A3C", "#141414"),
        ("users", "Users & Groups", "person.2", "#3D9BFF", "#0A6CFF"),
        ("-", "", "", "", ""),
        ("accounts", "Internet Accounts", "at", "#3D9BFF", "#0A6CFF"),
        ("-", "", "", "", ""),
        ("keyboard", "Keyboard", "keyboard", "#A3A3A8", "#7C7C81"),
        ("mouse", "Mouse", "computermouse", "#A3A3A8", "#7C7C81"),
        ("trackpad", "Trackpad", "trackpad", "#A3A3A8", "#7C7C81"),
        ("printers", "Printers & Scanners", "printer", "#A3A3A8", "#7C7C81"),
        ("-", "", "", "", ""),
        ("finder", "Finder", "@finder", "#6CD0FB", "#1B8CF2"),
        ("macshell", "MacShell", "command", "#3A3A3C", "#141414"),
    };

    /// <summary>More words each pane answers to in the sidebar's search (beyond its name).</summary>
    static readonly Dictionary<string, string> Keywords = new()
    {
        ["wifi"] = "wireless network internet ssid hotspot join known networks",
        ["bluetooth"] = "pair devices headphones mouse keyboard speaker",
        ["network"] = "ethernet ip address dns router vpn proxy firewall",
        ["notifications"] = "alerts banners badges toast",
        ["sound"] = "volume output input microphone speakers balance alert",
        ["focus"] = "do not disturb dnd quiet hours silence",
        ["general"] = "about storage software update login items language region date time sharing computer name",
        ["appearance"] = "dark mode light accent color highlight scroll bars",
        ["accessibility"] = "zoom magnifier contrast motion transparency narrator voiceover sticky keys pointer cursor",
        ["controlcenter"] = "menu bar clock battery percentage weather timer",
        ["spotlight"] = "search results",
        ["privacy"] = "location camera microphone permissions security firewall contacts calendars photos",
        ["dock"] = "magnification hide hot corners mission control windows tiling snap desktop",
        ["displays"] = "brightness resolution screen edges",
        ["wallpaper"] = "background desktop picture",
        ["screensaver"] = "screen saver",
        ["battery"] = "energy power mode low power sleep",
        ["lockscreen"] = "password sleep display off screen saver require",
        ["users"] = "accounts administrator",
        ["accounts"] = "mail email internet accounts",
        ["keyboard"] = "key repeat delay shortcuts input sources",
        ["mouse"] = "tracking speed double-click scrolling pointer",
        ["trackpad"] = "tap to click scroll gestures touchpad",
        ["printers"] = "printer scanner default",
        ["finder"] = "extensions hidden files open save",
        ["macshell"] = "taskbar windows key alt tab quit",
    };

    readonly StackPanel _sidebar = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly StackPanel _content = new() { Margin = new Thickness(20, 4, 20, 24) };
    readonly ScrollViewer _contentScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly TextBlock _paneTitle = new() { FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBox _search = new();
    readonly Dictionary<string, Border> _rows = new();
    readonly List<string> _history = new();
    readonly Grid _sheetLayer = new() { Visibility = Visibility.Collapsed };
    readonly Button _back, _fwd;
    int _historyPos = -1;
    string _page;
    /// <summary>Bumped on every page change: work started for an older page is dropped when it finishes.</summary>
    int _generation;
    /// <summary>Background work still running for this page (the probe waits for it).</summary>
    int _pending;
    readonly List<DispatcherTimer> _pageTimers = new();
    readonly List<Action> _pageCleanup = new();

    /// <summary>The pane shown (the part before "/" of the page).</summary>
    string Pane => _page?.Split('/')[0];

    public static void ShowPane(string id)
    {
        if (Instance == null)
        {
            Instance = new SettingsWindow();
            Instance.Show();
        }
        if (Instance.WindowState == WindowState.Minimized) Instance.WindowState = WindowState.Normal;
        if (!Instance.IsVisible) Instance.Show();
        if (!ShellHost.Offscreen) Instance.Activate();
        Instance.Open(id ?? Instance._page ?? "appearance");
    }

    SettingsWindow() : base("internal:settings", 52)
    {
        Title = "System Settings";
        Width = 720; Height = Math.Min(640, SystemParameters.WorkArea.Height - 40);
        MinWidth = 720; MaxWidth = 720; MinHeight = 400;
        Closed += (_, _) => { if (Instance == this) Instance = null; LeavePage(); };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());

        // sidebar
        var side = new Grid();
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition());
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "SidebarTintBrush");
        Grid.SetRowSpan(tint, 3);
        side.Children.Add(tint);
        side.Children.Add(new TrafficLights { Margin = new Thickness(20, 0, 0, 0), CanZoom = false });
        _search.Style = (Style)Application.Current.Resources["MacSearchField"];
        _search.Margin = new Thickness(10, 0, 10, 8);
        _search.Height = 26;
        _search.TextChanged += (_, _) => BuildSidebar();
        Grid.SetRow(_search, 1);
        side.Children.Add(_search);
        var sideScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(sideScroll, 2);
        side.Children.Add(sideScroll);
        layout.Children.Add(side);

        // content
        var right = new DockPanel();
        right.SetResourceReference(Panel.BackgroundProperty, "SettingsPaneBrush");
        var header = new Grid { Height = 52 };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _back = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new SymbolIcon { Symbol = "chevron.left", Width = 16, Height = 16, StrokeWidth = 2.1 }, Width = 28, ToolTip = "Back" };
        WindowChrome.SetIsHitTestVisibleInChrome(_back, true);
        _back.Click += (_, _) => GoBack();
        _fwd = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new SymbolIcon { Symbol = "chevron.right", Width = 16, Height = 16, StrokeWidth = 2.1 }, Width = 28, ToolTip = "Forward" };
        WindowChrome.SetIsHitTestVisibleInChrome(_fwd, true);
        _fwd.Click += (_, _) => GoForward();
        nav.Children.Add(_back); nav.Children.Add(_fwd);
        _paneTitle.Margin = new Thickness(6, 0, 0, 1);
        _paneTitle.MaxWidth = 380;
        _paneTitle.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        nav.Children.Add(_paneTitle);
        header.Children.Add(nav);
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        _contentScroll.Content = _content;
        right.Children.Add(_contentScroll);
        var edge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = right };
        edge.SetResourceReference(Border.BorderBrushProperty, "StrongSeparatorBrush");
        Grid.SetColumn(edge, 1);
        layout.Children.Add(edge);

        // sheets slide over the whole window
        Grid.SetColumnSpan(_sheetLayer, 2);
        layout.Children.Add(_sheetLayer);
        Content = layout;
        BuildSidebar();
        Loaded += (_, _) => CenterOnWorkArea(0.35);
        Theme.Changed += OnTheme;
        Closed += (_, _) => Theme.Changed -= OnTheme;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _sheetLayer.Visibility == Visibility.Visible) { CloseSheet(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemOpenBrackets) { GoBack(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemCloseBrackets) { GoForward(); e.Handled = true; }
        };
    }

    void OnTheme() { BuildSidebar(); if (_page != null) Rebuild(); }

    // ------------------------------------------------------------------ sidebar

    public static ImageSource PaneIcon((string id, string name, string sym, string top, string bottom) p) =>
        p.sym == "@finder" ? MacIcons.Finder : MacIcons.Tile(p.sym, p.top, p.bottom);

    /// <summary>The panes this computer has (Trackpad only with a touchpad; Battery is Energy without one).</summary>
    public static IEnumerable<(string id, string name, string sym, string top, string bottom)> VisiblePanes()
    {
        foreach (var p in Panes)
        {
            if (p.id == "trackpad" && !Native.SysParams.HasTouchpad) continue;
            if (p.id == "battery" && !Native.PowerSettings.HasBattery) { yield return (p.id, "Energy", "power", p.top, p.bottom); continue; }
            yield return p;
        }
    }

    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _rows.Clear();
        string q = _search.Text.Trim();
        if (q.Length == 0) _sidebar.Children.Add(AccountRow());
        bool lastWasSep = true;
        foreach (var p in VisiblePanes())
        {
            if (p.id == "-")
            {
                if (!lastWasSep && q.Length == 0) { _sidebar.Children.Add(new Border { Height = 10 }); lastWasSep = true; }
                continue;
            }
            if (q.Length > 0 && !p.name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                && !(Keywords.TryGetValue(p.id, out var kw) && kw.Split(' ').Any(w => w.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)))) continue;
            lastWasSep = false;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Image { Source = PaneIcon(p), Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) });
            var t = Txt(p.name, 13);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            var row = new Border { Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(8, 0, 8, 0), Child = sp, Background = Brushes.Transparent, Tag = t };
            string id = p.id;
            row.MouseLeftButtonUp += (_, _) => Open(id);
            _rows[id] = row;
            _sidebar.Children.Add(row);
        }
        if (q.Length > 0 && _rows.Count == 0)
        {
            var none = Txt("No Results", 13, brush: "SecondaryLabelBrush");
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.Margin = new Thickness(0, 20, 0, 0);
            _sidebar.Children.Add(none);
        }
        HighlightRow();
    }

    /// <summary>The account at the top of the sidebar: the Windows account's picture and name; it opens Users & Groups.</summary>
    FrameworkElement AccountRow()
    {
        var acct = new Grid { Margin = new Thickness(10, 2, 10, 10), Height = 48, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        acct.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        acct.ColumnDefinitions.Add(new ColumnDefinition());
        string name = Shell.MenuBarWindow.UserDisplayName();
        var avatar = Avatar(name, 36);
        avatar.Margin = new Thickness(6, 0, 10, 0);
        acct.Children.Add(avatar);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Txt(Native.UserAccounts.CurrentFullName ?? name, 13, FontWeights.SemiBold));
        names.Children.Add(Txt(Native.UserAccounts.CurrentIsMicrosoftAccount ? "Microsoft Account" : "Windows Account", 11, brush: "SecondaryLabelBrush"));
        Grid.SetColumn(names, 1);
        acct.Children.Add(names);
        acct.MouseLeftButtonUp += (_, _) => Open("users");
        return acct;
    }

    /// <summary>The user's picture (Windows' account picture), or their initials on gray as macOS shows without one.</summary>
    public static FrameworkElement Avatar(string name, double size, string picturePath = null)
    {
        var g = new Grid { Width = size, Height = size };
        string pic = picturePath ?? Native.UserAccounts.CurrentPicture(size >= 48 ? 192 : 96);
        if (pic != null && File.Exists(pic))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(pic);
                bmp.DecodePixelWidth = (int)(size * 2);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                g.Children.Add(new Ellipse { Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill } });
                return g;
            }
            catch { }
        }
        g.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Theme.C("#A7A7AC"), Theme.C("#6E6E73"), 90) });
        g.Children.Add(new TextBlock
        {
            Text = string.Concat((name ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0]))),
            Foreground = Brushes.White, FontSize = size * 0.39, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    void HighlightRow()
    {
        foreach (var (id, row) in _rows)
        {
            var t = (TextBlock)row.Tag;
            if (id == Pane) { row.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); t.Foreground = Brushes.White; }
            else { row.Background = Brushes.Transparent; t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush"); }
        }
    }

    // ------------------------------------------------------------------ navigation

    public void GoBack()
    {
        if (_sheetLayer.Visibility == Visibility.Visible) CloseSheet();
        if (_historyPos <= 0) return;
        _historyPos--;
        Open(_history[_historyPos], false);
    }

    public void GoForward()
    {
        if (_sheetLayer.Visibility == Visibility.Visible) CloseSheet();
        if (_historyPos >= _history.Count - 1) return;
        _historyPos++;
        Open(_history[_historyPos], false);
    }

    /// <summary>Shows a page: a pane ("wifi") or one of its pages ("general/about").</summary>
    public void Open(string id, bool addHistory = true)
    {
        if (string.IsNullOrEmpty(id)) id = "appearance";
        string paneId = id.Split('/')[0];
        if (!VisiblePanes().Any(p => p.id == paneId)) { id = paneId = "appearance"; }
        if (addHistory && (_historyPos < 0 || _history[_historyPos] != id))
        {
            if (_historyPos < _history.Count - 1) _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
            _history.Add(id);
            _historyPos = _history.Count - 1;
        }
        Show(id);
        _contentScroll.ScrollToTop();
    }

    /// <summary>Builds the page again in place (its data changed), keeping where it was scrolled to.</summary>
    public void Rebuild()
    {
        if (_page == null) return;
        double offset = _contentScroll.VerticalOffset;
        Show(_page);
        _contentScroll.UpdateLayout();
        _contentScroll.ScrollToVerticalOffset(offset);
    }

    void Show(string id)
    {
        LeavePage();
        CloseSheet();
        _page = id;
        _generation++;
        var pane = VisiblePanes().First(p => p.id == Pane);
        _paneTitle.Text = pane.name;
        _content.Children.Clear();
        try { BuildPage(id); }
        catch (Exception ex)
        {
            App.Log($"settings page {id}: {ex}");
            _content.Children.Clear();
            Group(Row("This page couldn’t be shown", null, ex.Message));
        }
        HighlightRow();
        _back.IsEnabled = _historyPos > 0;
        _fwd.IsEnabled = _historyPos < _history.Count - 1;
    }

    /// <summary>Stops what the page left running (timers, watchers).</summary>
    void LeavePage()
    {
        foreach (var t in _pageTimers) t.Stop();
        _pageTimers.Clear();
        foreach (var a in _pageCleanup) { try { a(); } catch { } }
        _pageCleanup.Clear();
    }

    /// <summary>The page title in the toolbar (a pane's own pages set theirs).</summary>
    void Title_(string title) => _paneTitle.Text = title;

    /// <summary>The pane shown again if it's this one (what it shows was changed elsewhere).</summary>
    public static void Refresh(string id)
    {
        if (Instance is { } w && (w._page == id || w.Pane == id)) w.Rebuild();
    }

    void BuildPage(string id)
    {
        string[] parts = id.Split('/', 2);
        string sub = parts.Length > 1 ? parts[1] : null;
        switch (parts[0])
        {
            case "wifi": if (sub == null) BuildWifi(); else BuildWifiPage(sub); break;
            case "bluetooth": BuildBluetooth(); break;
            case "network": if (sub == null) BuildNetwork(); else BuildNetworkPage(sub); break;
            case "notifications": if (sub == null) BuildNotifications(); else BuildNotificationApp(sub); break;
            case "sound": BuildSound(); break;
            case "focus": if (sub == null) BuildFocus(); else BuildFocusPage(sub); break;
            case "general": if (sub == null) BuildGeneral(); else BuildGeneralPage(sub); break;
            case "appearance": BuildAppearance(); break;
            case "accessibility": if (sub == null) BuildAccessibility(); else BuildAccessibilityPage(sub); break;
            case "controlcenter": BuildControlCenter(); break;
            case "spotlight": BuildSpotlight(); break;
            case "privacy": if (sub == null) BuildPrivacy(); else BuildPrivacyPage(sub); break;
            case "dock": BuildDock(); break;
            case "displays": BuildDisplays(); break;
            case "wallpaper": BuildWallpaper(); break;
            case "screensaver": BuildScreenSaver(); break;
            case "battery": if (sub == null) BuildBattery(); else BuildBatteryPage(sub); break;
            case "lockscreen": BuildLockScreen(); break;
            case "users": BuildUsers(); break;
            case "accounts": BuildInternetAccounts(); break;
            case "keyboard": if (sub == null) BuildKeyboard(); else BuildKeyboardPage(sub); break;
            case "mouse": BuildMouse(); break;
            case "trackpad": BuildTrackpad(); break;
            case "printers": BuildPrinters(); break;
            case "finder": BuildFinder(); break;
            case "macshell": BuildMacShell(); break;
            default: BuildAppearance(); break;
        }
    }

    // ------------------------------------------------------------------ background work and live pages

    /// <summary>Runs work off the UI thread, then shows its result - if the same page is still showing.</summary>
    void Run<T>(Func<T> work, Action<T> then)
    {
        int gen = _generation;
        _pending++;
        Task.Run(() =>
        {
            T result = default;
            Exception error = null;
            try { result = work(); } catch (Exception ex) { error = ex; }
            Dispatcher.BeginInvoke(() =>
            {
                _pending--;
                if (error != null) App.Log($"settings {_page}: {error.Message}");
                if (gen != _generation) return;
                try { then(result); } catch (Exception ex) { App.Log($"settings {_page}: {ex}"); }
            });
        });
    }

    /// <summary>Runs work off the UI thread, then rebuilds the page (an action that changes what it shows).</summary>
    void RunThenRebuild(Action work, Action after = null) => Run(() => { work(); return 0; }, _ => { after?.Invoke(); Rebuild(); });

    /// <summary>A timer that lives as long as the page.</summary>
    void Every(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => { try { tick(); } catch (Exception ex) { App.Log($"settings {_page} timer: {ex.Message}"); t.Stop(); } };
        t.Start();
        _pageTimers.Add(t);
    }

    void OnLeave(Action a) => _pageCleanup.Add(a);

    static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    static void Save() => Settings.Save();
}
