using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Path = System.IO.Path;

namespace MacShell.Apps;

/// <summary>System Settings (macOS Ventura+ layout).</summary>
public class SettingsWindow : MacWindow
{
    public static SettingsWindow Instance { get; private set; }

    /// <summary>id, name, symbol, gradient top, gradient bottom. Ids starting with "ms-settings:" open Windows Settings.</summary>
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
        ("spotlight", "Siri & Spotlight", "magnifyingglass", "#A571F2", "#5E5CE6"),
        ("privacy", "Privacy & Security", "hand", "#3D9BFF", "#0A6CFF"),
        ("-", "", "", "", ""),
        ("dock", "Desktop & Dock", "dock", "#3A3A3C", "#141414"),
        ("displays", "Displays", "display", "#3D9BFF", "#0A6CFF"),
        ("wallpaper", "Wallpaper", "wallpaper", "#62D0FA", "#1AA7E8"),
        ("battery", "Battery", "battery", "#5DD879", "#2FB24C"),
        ("-", "", "", "", ""),
        ("lockscreen", "Lock Screen", "lock", "#3A3A3C", "#141414"),
        ("users", "Users & Groups", "person.circle", "#3D9BFF", "#0A6CFF"),
        ("-", "", "", "", ""),
        ("keyboard", "Keyboard", "keyboard", "#A3A3A8", "#7C7C81"),
        ("finder", "Finder", "@finder", "", ""),
        ("macshell", "MacShell", "command", "#3A3A3C", "#141414"),
    };

    static readonly Dictionary<string, (string uri, string blurb)> WindowsPanes = new()
    {
        ["wifi"] = ("ms-settings:network-wifi", "Join networks, manage known networks and hardware properties."),
        ["bluetooth"] = ("ms-settings:bluetooth", "Pair and manage Bluetooth devices."),
        ["network"] = ("ms-settings:network-status", "Ethernet, VPN, proxy and advanced network settings."),
        ["notifications"] = ("ms-settings:notifications", "Choose which apps can show notifications."),
        ["focus"] = ("ms-settings:quiethours", "Silence notifications with Focus / Do Not Disturb."),
        ["accessibility"] = ("ms-settings:easeofaccess", "Vision, hearing and interaction settings."),
        ["privacy"] = ("ms-settings:privacy", "App permissions, location, camera and microphone access."),
        ["displays"] = ("ms-settings:display", "Resolution, scaling, Night Light and multiple displays."),
        ["battery"] = ("ms-settings:batterysaver", "Battery usage and power modes."),
        ["lockscreen"] = ("ms-settings:lockscreen", "Lock screen image and sign-in options."),
        ["users"] = ("ms-settings:otherusers", "Manage user accounts on this computer."),
    };

    readonly StackPanel _sidebar = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly StackPanel _content = new() { Margin = new Thickness(20, 4, 20, 24) };
    readonly ScrollViewer _contentScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly TextBlock _paneTitle = new() { FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _search = new();
    readonly Dictionary<string, Border> _rows = new();
    readonly List<string> _history = new();
    string _pane;

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
        Instance.Open(id ?? Instance._pane ?? "appearance");
    }

    SettingsWindow() : base("internal:settings", 52)
    {
        Title = "System Settings";
        Width = 720; Height = Math.Min(640, SystemParameters.WorkArea.Height - 40);
        MinWidth = 720; MaxWidth = 720; MinHeight = 400;
        Closed += (_, _) => Instance = null;
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
        var back = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new SymbolIcon { Symbol = "chevron.left", Width = 16, Height = 16, StrokeWidth = 2.1 }, Width = 28 };
        WindowChrome.SetIsHitTestVisibleInChrome(back, true);
        back.Click += (_, _) => GoBack();
        var fwd = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new SymbolIcon { Symbol = "chevron.right", Width = 16, Height = 16, StrokeWidth = 2.1 }, Width = 28, IsEnabled = false };
        nav.Children.Add(back); nav.Children.Add(fwd);
        _paneTitle.Margin = new Thickness(6, 0, 0, 1);
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
        Content = layout;
        BuildSidebar();
        Loaded += (_, _) => CenterOnWorkArea(0.35);
        Theme.Changed += OnTheme;
        Closed += (_, _) => Theme.Changed -= OnTheme;
    }

    void OnTheme() { BuildSidebar(); if (_pane != null) Open(_pane, false); }

    // ------------------------------------------------------------------ sidebar

    static ImageSource PaneIcon((string id, string name, string sym, string top, string bottom) p) =>
        p.sym == "@finder" ? MacIcons.Finder : MacIcons.Tile(p.sym, p.top, p.bottom);

    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _rows.Clear();
        string q = _search.Text.Trim();
        if (q.Length == 0)
        {
            // account row
            var acct = new Grid { Margin = new Thickness(10, 2, 10, 10), Height = 48 };
            acct.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            acct.ColumnDefinitions.Add(new ColumnDefinition());
            string name = Shell.MenuBarWindow.UserDisplayName();
            var avatar = new Grid { Width = 36, Height = 36, Margin = new Thickness(6, 0, 10, 0) };
            avatar.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Theme.C("#A7A7AC"), Theme.C("#6E6E73"), 90) });
            avatar.Children.Add(new TextBlock { Text = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0]))), Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            acct.Children.Add(avatar);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Txt(name, 13, FontWeights.SemiBold));
            names.Children.Add(Txt("Windows Account", 11, brush: "SecondaryLabelBrush"));
            Grid.SetColumn(names, 1);
            acct.Children.Add(names);
            _sidebar.Children.Add(acct);
        }
        bool lastWasSep = true;
        foreach (var p in Panes)
        {
            if (p.id == "-")
            {
                if (!lastWasSep && q.Length == 0) { _sidebar.Children.Add(new Border { Height = 10 }); lastWasSep = true; }
                continue;
            }
            if (q.Length > 0 && !p.name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
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
        HighlightRow();
    }

    void HighlightRow()
    {
        foreach (var (id, row) in _rows)
        {
            var t = (TextBlock)row.Tag;
            if (id == _pane) { row.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); t.Foreground = Brushes.White; }
            else { row.Background = Brushes.Transparent; t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush"); }
        }
    }

    public void GoBack()
    {
        if (_history.Count < 2) return;
        _history.RemoveAt(_history.Count - 1);
        Open(_history[^1], false);
    }

    void Open(string id, bool addHistory = true)
    {
        var pane = Panes.FirstOrDefault(p => p.id == id);
        if (pane.id == null) { id = "appearance"; pane = Panes.First(p => p.id == id); }
        _pane = id;
        if (addHistory && (_history.Count == 0 || _history[^1] != id)) _history.Add(id);
        _paneTitle.Text = pane.name;
        _content.Children.Clear();
        switch (id)
        {
            case "appearance": BuildAppearance(); break;
            case "wallpaper": BuildWallpaper(); break;
            case "dock": BuildDock(); break;
            case "controlcenter": BuildControlCenter(); break;
            case "spotlight": BuildSpotlight(); break;
            case "sound": BuildSound(); break;
            case "general": BuildGeneral(); break;
            case "keyboard": BuildKeyboard(); break;
            case "finder": BuildFinder(); break;
            case "macshell": BuildMacShell(); break;
            default: BuildWindowsLink(pane); break;
        }
        _contentScroll.ScrollToTop();
        HighlightRow();
    }

    // ------------------------------------------------------------------ building blocks

    static TextBlock Txt(string s, double size = 13, FontWeight? w = null, string brush = "LabelBrush")
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    void SectionTitle(string s)
    {
        var t = Txt(s, 13, FontWeights.Bold);
        t.Margin = new Thickness(4, 16, 0, 8);
        _content.Children.Add(t);
    }

    void Group(params UIElement[] rows)
    {
        var sp = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] == null) continue;
            if (sp.Children.Count > 0)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(10, 0, 10, 0) };
                sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
                sp.Children.Add(sep);
            }
            sp.Children.Add(rows[i]);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0.5), Child = sp, Margin = new Thickness(0, 0, 0, 12) };
        box.SetResourceReference(Border.BackgroundProperty, "GroupBoxBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "GroupBoxBorderBrush");
        _content.Children.Add(box);
    }

    static FrameworkElement Row(string label, UIElement control, string sub = null)
    {
        var g = new Grid { MinHeight = 38, Margin = new Thickness(12, 6, 12, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Txt(label, 13));
        if (sub != null) { var s = Txt(sub, 11, brush: "SecondaryLabelBrush"); s.Margin = new Thickness(0, 2, 12, 0); left.Children.Add(s); }
        g.Children.Add(left);
        if (control != null)
        {
            if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(12, 0, 0, 0); }
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
        }
        return g;
    }

    static CheckBox Switch(bool value, Action<bool> set)
    {
        var cb = new CheckBox { Style = (Style)Application.Current.Resources["MacSwitch"], IsChecked = value };
        cb.Click += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    static Button PopUp(string[] options, int selected, Action<int> choose)
    {
        var text = new TextBlock { Text = options[Math.Clamp(selected, 0, options.Length - 1)], VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.updown", Width = 10, Height = 10, StrokeWidth = 2.4, Margin = new Thickness(8, 0, 0, 0) });
        var b = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = sp, Padding = new Thickness(9, 0, 7, 0), MinWidth = 0 };
        b.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, HorizontalOffset = -14, VerticalOffset = -8 };
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                Mb.Add(cm.Items, Mb.Item(options[i], () => { text.Text = options[idx]; choose(idx); }, isChecked: options[i] == text.Text));
            }
            cm.IsOpen = true;
        };
        return b;
    }

    static FrameworkElement LabeledSlider(double min, double max, double value, string left, string right, Action<double> set, double width = 240)
    {
        var g = new Grid { Width = width };
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        var s = new Slider { Style = (Style)Application.Current.Resources["MacSlider"], Minimum = min, Maximum = max, Value = value };
        s.ValueChanged += (_, e) => set(e.NewValue);
        g.Children.Add(s);
        var l = Txt(left, 10, brush: "SecondaryLabelBrush");
        var r = Txt(right, 10, brush: "SecondaryLabelBrush");
        r.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(l, 1); Grid.SetRow(r, 1);
        g.Children.Add(l); g.Children.Add(r);
        return g;
    }

    static void Save() => Settings.Save();

    // ------------------------------------------------------------------ panes

    void BuildAppearance()
    {
        var s = Settings.Current;
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label) in new[] { ("auto", "Auto"), ("light", "Light"), ("dark", "Dark") })
        {
            var cell = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
            var thumb = new Border { Width = 68, Height = 44, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(s.Appearance == id ? 3 : 0.5), ClipToBounds = true };
            if (s.Appearance == id) thumb.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else thumb.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            thumb.Child = AppearanceThumb(id);
            cell.Children.Add(thumb);
            var t = Txt(label, 11, s.Appearance == id ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 4, 0, 0);
            cell.Children.Add(t);
            string pick = id;
            cell.MouseLeftButtonUp += (_, _) => { Settings.Current.Appearance = pick; Save(); Theme.Apply(); };
            choices.Children.Add(cell);
        }
        var accents = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, name, light, dark) in Theme.Accents)
        {
            var c = Theme.IsDark ? dark : light;
            var dot = new Grid { Width = 18, Height = 18, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = name };
            if (id == "multicolor")
            {
                dot.Children.Add(new Ellipse { Fill = new LinearGradientBrush(new GradientStopCollection { new(Theme.C("#FF5F57"), 0), new(Theme.C("#FEBC2E"), 0.33), new(Theme.C("#28C840"), 0.66), new(Theme.C("#0A84FF"), 1) }, 45) });
            }
            else dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Theme.Darken(c, 0.2)), StrokeThickness = 0.5 });
            if (Settings.Current.Accent == id) dot.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White });
            string pick = id;
            dot.MouseLeftButtonUp += (_, _) => { Settings.Current.Accent = pick; Save(); Theme.Apply(); };
            accents.Children.Add(dot);
        }
        Group(Row("Appearance", choices), Row("Accent color", accents));
        Group(
            Row("Show scroll bars", PopUp(new[] { "Automatically based on mouse or trackpad", "When scrolling", "Always" }, 0, _ => { })),
            Row("Sound effects", Switch(s.SoundEffects, v => { Settings.Current.SoundEffects = v; Save(); }), "Play user-interface sound effects"));
    }

    static UIElement AppearanceThumb(string id)
    {
        Brush Bg(bool dark) => new SolidColorBrush(dark ? Theme.C("#1E1E1E") : Theme.C("#F5F5F5"));
        Brush Win(bool dark) => new SolidColorBrush(dark ? Theme.C("#3A3A3C") : Colors.White);
        var g = new Grid();
        if (id == "auto")
        {
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new Border { Background = Bg(false) }; var r = new Border { Background = Bg(true) };
            Grid.SetColumn(r, 1);
            g.Children.Add(l); g.Children.Add(r);
            var w = new Border { Margin = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(3), Background = new LinearGradientBrush(Colors.White, Theme.C("#3A3A3C"), 0) };
            Grid.SetColumnSpan(w, 2);
            g.Children.Add(w);
        }
        else
        {
            bool dark = id == "dark";
            g.Children.Add(new Border { Background = Bg(dark) });
            g.Children.Add(new Border { Margin = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(3), Background = Win(dark) });
            g.Children.Add(new Border { Margin = new Thickness(10, 8, 44, 8), CornerRadius = new CornerRadius(3, 0, 0, 3), Background = new SolidColorBrush(dark ? Theme.C("#2A2A2C") : Theme.C("#E5E5EA")) });
        }
        return g;
    }

    void BuildWallpaper()
    {
        var cur = Settings.Current.Wallpaper;
        var head = new Grid { Margin = new Thickness(0, 4, 0, 16) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var preview = new Border { Width = 180, Height = 112, CornerRadius = new CornerRadius(8), Background = new ImageBrush(Wallpaper.Image) { Stretch = Stretch.UniformToFill }, BorderThickness = new Thickness(0.5) };
        preview.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        head.Children.Add(preview);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        string curName = Wallpaper.BuiltIn.FirstOrDefault(w => w.id == cur).name ?? (cur == "windows" ? "Windows Wallpaper" : Path.GetFileNameWithoutExtension(cur));
        info.Children.Add(Txt(curName, 15, FontWeights.SemiBold));
        info.Children.Add(Txt(cur.StartsWith("gen:") ? "Dynamic Wallpaper" : "Picture", 11, brush: "SecondaryLabelBrush"));
        var add = new Button { Content = "Add Photo…", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.jfif;*.heic|All files|*.*" };
            if (dlg.ShowDialog(this) == true) SetWallpaper(dlg.FileName);
        };
        info.Children.Add(add);
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        _content.Children.Add(head);

        SectionTitle("Dynamic Wallpapers");
        var grid = new WrapPanel();
        foreach (var (id, name) in Wallpaper.BuiltIn) grid.Children.Add(WallTile(id, name, cur == id));
        _content.Children.Add(grid);
        SectionTitle("Pictures");
        var pics = new WrapPanel();
        if (Wallpaper.CurrentWindowsWallpaper() != null) pics.Children.Add(WallTile("windows", "Windows Wallpaper", cur == "windows"));
        if (!cur.StartsWith("gen:") && cur != "windows" && File.Exists(cur)) pics.Children.Add(WallTile(cur, Path.GetFileNameWithoutExtension(cur), true));
        string winWalls = @"C:\Windows\Web\Wallpaper";
        try
        {
            foreach (var f in Directory.EnumerateFiles(winWalls, "*.jpg", SearchOption.AllDirectories).Take(8))
                pics.Children.Add(WallTile(f, Path.GetFileNameWithoutExtension(f), cur == f));
        }
        catch { }
        _content.Children.Add(pics);
    }

    FrameworkElement WallTile(string id, string name, bool selected)
    {
        var sp = new StackPanel { Width = 116, Margin = new Thickness(0, 0, 12, 12), Cursor = Cursors.Hand };
        var b = new Border { Height = 72, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(selected ? 3 : 0.5), ClipToBounds = true };
        if (selected) b.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else b.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var img = new Image { Stretch = Stretch.UniformToFill };
        b.Child = img;
        Dispatcher.BeginInvoke(() => img.Source = Wallpaper.Thumbnail(id), System.Windows.Threading.DispatcherPriority.Background);
        sp.Children.Add(b);
        var t = Txt(name, 11, brush: "SecondaryLabelBrush");
        t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap;
        t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(t);
        sp.MouseLeftButtonUp += (_, _) => SetWallpaper(id);
        return sp;
    }

    void SetWallpaper(string id)
    {
        Settings.Current.Wallpaper = id;
        Settings.SaveNow();
        Mouse.OverrideCursor = Cursors.Wait;
        try { Wallpaper.Load(); } finally { Mouse.OverrideCursor = null; }
        Open("wallpaper", false);
    }

    void BuildDock()
    {
        var s = Settings.Current;
        SectionTitle("Dock");
        Group(
            Row("Size", LabeledSlider(24, 96, s.DockIconSize, "Small", "Large", v => { Settings.Current.DockIconSize = Math.Round(v); Save(); })),
            Row("Magnification", LabeledSlider(24, 128, s.DockMagnifiedSize, "Off", "Large", v => { Settings.Current.DockMagnifiedSize = Math.Round(v); Settings.Current.DockMagnification = v > Settings.Current.DockIconSize + 2; Save(); })),
            Row("Position on screen", PopUp(new[] { "Bottom" }, 0, _ => { })),
            Row("Minimize windows using", PopUp(new[] { "Genie Effect", "Scale Effect" }, 0, _ => { })),
            Row("Automatically hide and show the Dock", Switch(s.DockAutoHide, v => { Settings.Current.DockAutoHide = v; Save(); })),
            Row("Show indicators for open applications", Switch(s.DockShowIndicators, v => { Settings.Current.DockShowIndicators = v; Save(); })),
            Row("Show notification badges", Switch(s.DockShowBadges, v => { Settings.Current.DockShowBadges = v; Save(); }), "Red counts on app icons"),
            Row("Also count notifications in Notification Center", Switch(s.BadgeCountNotifications, v => { Settings.Current.BadgeCountNotifications = v; Save(); }), "Off: badges show only the unread counts apps set themselves"),
            Row("Minimize windows into application icon", Switch(s.MinimizeIntoAppIcon, v => { Settings.Current.MinimizeIntoAppIcon = v; Save(); })));
        SectionTitle("Desktop");
        Group(
            Row("Icon size", PopUp(new[] { "Small", "Medium", "Large" }, s.DesktopIconSize <= 48 ? 0 : s.DesktopIconSize >= 88 ? 2 : 1, i => { Settings.Current.DesktopIconSize = i switch { 0 => 48, 2 => 88, _ => 64 }; Save(); })),
            Row("Show hidden files", Switch(s.ShowHiddenFiles, v => { Settings.Current.ShowHiddenFiles = v; Save(); })));
        SectionTitle("Mission Control");
        Group(
            Row("Mission Control", Txt("Win + ↑", 13, brush: "SecondaryLabelBrush"), "Shows all open windows"),
            Row("Show Desktop", Txt("Win + D", 13, brush: "SecondaryLabelBrush")),
            Row("Application windows", Txt("Right-click an app in the Dock", 12, brush: "SecondaryLabelBrush")));
    }

    void BuildControlCenter()
    {
        var s = Settings.Current;
        SectionTitle("Menu Bar");
        Group(
            MenuBarRow("Timer", "timer"),
            MenuBarRow("Battery", "battery"),
            MenuBarRow("Wi‑Fi", "wifi"),
            MenuBarRow("Sound", "sound"),
            MenuBarRow("Spotlight", "spotlight"));
        SectionTitle("App Icons in the Menu Bar");
        var apps = Shell.MenuBarWindow.TrayApps();
        if (apps.Count == 0) Group(Row("No app icons right now", null, "Apps running in the background show their icons in the menu bar"));
        else Group(apps.Select(a => MenuBarRow(a.name, a.key, a.image)).ToArray());
        var tip = Txt("Tip: hold Alt and drag an icon out of the menu bar to take it out.", 11, brush: "SecondaryLabelBrush");
        tip.Margin = new Thickness(4, -4, 0, 4);
        _content.Children.Add(tip);
        SectionTitle("Clock");
        Group(
            Row("Show date", Switch(s.ClockShowDate, v => { Settings.Current.ClockShowDate = v; Save(); })),
            Row("Show the day of the week", Switch(s.ClockShowDay, v => { Settings.Current.ClockShowDay = v; Save(); })),
            Row("Use a 24-hour clock", Switch(s.Clock24Hour, v => { Settings.Current.Clock24Hour = v; Save(); })),
            Row("Display the time with seconds", Switch(s.ClockShowSeconds, v => { Settings.Current.ClockShowSeconds = v; Save(); })));
        SectionTitle("Battery");
        Group(Row("Show Percentage", Switch(s.ShowBatteryPercent, v => { Settings.Current.ShowBatteryPercent = v; Save(); })));
        SectionTitle("Timer");
        Group(
            Row("When the timer ends", PopUp(CountdownTimer.Sounds.Select(CountdownTimer.SoundName).ToArray(), Math.Max(0, Array.IndexOf(CountdownTimer.Sounds, s.TimerSound)), i =>
            {
                Settings.Current.TimerSound = CountdownTimer.Sounds[i]; Save();
                CountdownTimer.Preview(CountdownTimer.Sounds[i]);
            }), "Click 00:00 in the menu bar, then type 5:00 for 5 minutes, or a time like 5:30pm"));
    }

    /// <summary>A "show in the menu bar" switch for one menu bar item (an app's: with its icon).</summary>
    static FrameworkElement MenuBarRow(string name, string key, ImageSource icon = null)
    {
        var row = (Grid)Row(name, Switch(!Shell.MenuBarWindow.IsHidden(key), v => Shell.MenuBarWindow.SetHidden(key, name, !v)));
        if (icon != null && row.Children[0] is StackPanel left)
        {
            var img = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var label = (UIElement)left.Children[0];
            left.Children.RemoveAt(0);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(img);
            line.Children.Add(label);
            left.Children.Insert(0, line);
        }
        return row;
    }

    void BuildSpotlight()
    {
        var s = Settings.Current;
        Group(
            Row("Keyboard shortcut", Txt("Win + Space", 13, brush: "SecondaryLabelBrush"), "Opens Spotlight from anywhere"),
            Row("Also use Alt + Space", Switch(s.AltSpaceSpotlight, v => { Settings.Current.AltSpaceSpotlight = v; Save(); }), "Alt sits where ⌘ is on a Mac keyboard"));
        SectionTitle("Search results");
        Group(
            Row("Applications", Switch(true, _ => { })),
            Row("Documents & Folders", Switch(true, _ => { }), "Uses the Windows Search index"),
            Row("Calculator", Switch(true, _ => { })),
            Row("System Settings", Switch(true, _ => { })),
            Row("Web Search", Switch(true, _ => { })));
    }

    void BuildSound()
    {
        var s = Settings.Current;
        SectionTitle("Output & Input");
        var vol = AudioVolume.Get() ?? 0.5;
        var sl = new Slider { Style = (Style)Application.Current.Resources["MacSlider"], Width = 240, Minimum = 0, Maximum = 1, Value = vol };
        sl.ValueChanged += (_, e) => AudioVolume.Set(e.NewValue);
        Group(
            Row("Output volume", sl),
            Row("Mute", Switch(AudioVolume.IsMuted(), v => AudioVolume.SetMute(v))),
            Row("Output & Input devices", MakeLink("Open Sound Settings…", "ms-settings:sound")));
        SectionTitle("Sound Effects");
        Group(Row("Play user interface sound effects", Switch(s.SoundEffects, v => { Settings.Current.SoundEffects = v; Save(); })));
    }

    void BuildGeneral()
    {
        var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 16) };
        var img = new Image { Source = MacIcons.Laptop(Wallpaper.Image), Width = 150 };
        head.Children.Add(img);
        var n = Txt(MachineInfo.Model, 20, FontWeights.Bold);
        n.HorizontalAlignment = HorizontalAlignment.Center; n.Margin = new Thickness(0, 10, 0, 0);
        head.Children.Add(n);
        _content.Children.Add(head);
        Group(
            Row("Name", Txt(Environment.MachineName, 13, brush: "SecondaryLabelBrush")),
            Row("Chip", Txt(MachineInfo.Cpu, 12, brush: "SecondaryLabelBrush")),
            Row("Memory", Txt(MachineInfo.MemoryGB, 13, brush: "SecondaryLabelBrush")),
            Row("Startup disk", Txt(MachineInfo.StartupDisk, 13, brush: "SecondaryLabelBrush")),
            Row("macOS", Txt($"{MachineInfo.OsName} {MachineInfo.OsVersion}", 12, brush: "SecondaryLabelBrush")));
        SectionTitle("Login Items");
        Group(Row("Open MacShell at login", Switch(LoginItem.Enabled, v => Task.Run(() => LoginItem.Set(v))), "MacShell takes over the desktop every time you sign in"));
        var check = new Button { Content = "Check Now", Style = (Style)Application.Current.Resources["MacButton"], MinWidth = 0 };
        check.Click += (_, _) => _ = Updater.CheckAsync(userInitiated: true);
        Group(Row($"MacShell {Updater.VersionText}", check, string.IsNullOrEmpty(Updater.Status) ? "Updates come from GitHub" : Updater.Status));
        Group(
            Row("Software Update", MakeLink("Windows Update…", "ms-settings:windowsupdate")),
            Row("Storage", MakeLink("Manage…", "ms-settings:storagesense")),
            Row("Date & Time", MakeLink("Open…", "ms-settings:dateandtime")),
            Row("Language & Region", MakeLink("Open…", "ms-settings:regionlanguage")));
    }

    static FrameworkElement MakeLink(string text, string uri)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["MacButton"], MinWidth = 0 };
        b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { } };
        return b;
    }

    void BuildKeyboard()
    {
        SectionTitle("MacShell Keyboard Shortcuts");
        var note = Txt("On a PC keyboard, ⌘ (Command) maps to Ctrl inside MacShell’s own apps (Finder, Settings). System-wide shortcuts use the Windows key.", 12, brush: "SecondaryLabelBrush");
        note.Margin = new Thickness(4, 0, 0, 10);
        _content.Children.Add(note);
        (string, string)[] items =
        {
            ("Launchpad", "Dock icon (Win key tap: see MacShell settings)"),
            ("Spotlight", "Win + Space  or  Alt + Space"),
            ("App Switcher (⌘Tab)", "Alt + Tab  or  Win + Tab"),
            ("Mission Control", "Win + ↑"),
            ("Show Desktop", "Win + D"),
            ("New Finder window", "Win + E"),
            ("System Settings", "Win + ,"),
            ("Screenshot (full screen)", "Win + Shift + 3"),
            ("Screenshot (selection)", "Win + Shift + 4"),
            ("Force Quit", "Ctrl + Alt + Esc"),
            ("Return to Windows", "Ctrl + Alt + Shift + Q"),
        };
        Group(items.Select(i => Row(i.Item1, Txt(i.Item2, 12, brush: "SecondaryLabelBrush"))).ToArray());
        SectionTitle("Finder");
        (string, string)[] finder =
        {
            ("Open", "Ctrl + O  /  Ctrl + ↓  /  double-click"), ("Rename", "Return"), ("Quick Look", "Space"), ("Move to Trash", "Delete  /  Ctrl + Backspace"),
            ("Get Info", "Ctrl + I"), ("New Folder", "Ctrl + Shift + N"), ("New Tab", "Ctrl + T"), ("Views", "Ctrl + 1 – 4"),
            ("Enclosing Folder", "Ctrl + ↑"), ("Go to Folder", "Ctrl + Shift + G"), ("Show hidden files", "Ctrl + Shift + ."),
        };
        Group(finder.Select(i => Row(i.Item1, Txt(i.Item2, 12, brush: "SecondaryLabelBrush"))).ToArray());
        Group(Row("Keyboard layouts & input", MakeLink("Open Windows Settings…", "ms-settings:keyboard")));
    }

    void BuildFinder()
    {
        var s = Settings.Current;
        string[] targets = { "recents", "home", "desktop", "documents", "downloads" };
        string[] targetNames = { "Recents", Environment.UserName, "Desktop", "Documents", "Downloads" };
        string[] views = { "icons", "list", "columns", "gallery" };
        string[] viewNames = { "Icons", "List", "Columns", "Gallery" };
        SectionTitle("General");
        Group(
            Row("New Finder windows show", PopUp(targetNames, Math.Max(0, Array.IndexOf(targets, s.FinderNewWindowTarget)), i => { Settings.Current.FinderNewWindowTarget = targets[i]; Save(); })),
            Row("Default view", PopUp(viewNames, Math.Max(0, Array.IndexOf(views, s.FinderDefaultView)), i => { Settings.Current.FinderDefaultView = views[i]; Save(); })));
        SectionTitle("Advanced");
        Group(
            Row("Show all filename extensions", Switch(s.FinderShowExtensions, v => { Settings.Current.FinderShowExtensions = v; Save(); foreach (var w in Finder.FinderWindow.All) w.Reload(true); })),
            Row("Keep folders on top", Switch(s.FinderFoldersOnTop, v => { Settings.Current.FinderFoldersOnTop = v; Save(); foreach (var w in Finder.FinderWindow.All) w.Reload(true); })),
            Row("Show hidden files", Switch(s.ShowHiddenFiles, v => { Settings.Current.ShowHiddenFiles = v; Save(); foreach (var w in Finder.FinderWindow.All) w.Reload(true); })),
            Row("Show path bar", Switch(s.FinderShowPathBar, v => { Settings.Current.FinderShowPathBar = v; Save(); })),
            Row("Show status bar", Switch(s.FinderShowStatusBar, v => { Settings.Current.FinderShowStatusBar = v; Save(); })));
        SectionTitle("Tags");
        Group(Row("Tagged items", Txt($"{s.Tags.Count} items", 12, brush: "SecondaryLabelBrush"), "Tags are stored by MacShell and shown in the Finder sidebar"));
    }

    void BuildMacShell()
    {
        var s = Settings.Current;
        var intro = Txt("MacShell replaces the Windows desktop with a macOS-style environment. Your Windows apps keep running inside it: they appear in the Dock, the menu bar, Mission Control and the App Switcher.", 12, brush: "SecondaryLabelBrush");
        intro.Margin = new Thickness(4, 0, 0, 12);
        _content.Children.Add(intro);
        SectionTitle("Takeover");
        Group(
            Row("Hide the Windows taskbar", Switch(s.HideWindowsTaskbar, v => { Settings.Current.HideWindowsTaskbar = v; Save(); if (v == Takeover.TaskbarTemporarilyShown) Takeover.ToggleTaskbar(); })),
            Row("Show app icons in the menu bar", Switch(s.MenuBarTrayIcons, v =>
            {
                Settings.Current.MenuBarTrayIcons = v; Save();
                if (v) TrayHost.Start(ShellHost.MenuBar.TrayIconRect); else TrayHost.Stop();
            }), "Icons of apps running in the background (the Windows system tray)"),
            Row("Use this wallpaper for Windows too", Switch(s.SyncWindowsWallpaper, v =>
            {
                Settings.Current.SyncWindowsWallpaper = v; Save();
                if (v) Wallpaper.Load();
            }), "Windows shows its wallpaper for a moment while you sign in"),
            Row("Tapping the Windows key", PopUp(new[] { "Does nothing", "Opens Launchpad", "Opens the Start menu" },
                s.WinKeyAction switch { "launchpad" => 1, "start" => 2, _ => 0 },
                i => { Settings.Current.WinKeyAction = i switch { 1 => "launchpad", 2 => "start", _ => "nothing" }; Save(); })),
            Row("Replace Alt + Tab with the App Switcher", Switch(s.ReplaceAltTab, v => { Settings.Current.ReplaceAltTab = v; Save(); })),
            Row("Open MacShell at login", Switch(LoginItem.Enabled, v => Task.Run(() => LoginItem.Set(v)))));
        SectionTitle("Windows");
        var toggleTb = new Button { Content = Takeover.TaskbarTemporarilyShown ? "Hide Taskbar" : "Show Taskbar", Style = (Style)Application.Current.Resources["MacButton"] };
        toggleTb.Click += (_, _) => { Takeover.ToggleTaskbar(); toggleTb.Content = Takeover.TaskbarTemporarilyShown ? "Hide Taskbar" : "Show Taskbar"; };
        var exit = new Button { Content = "Return to Windows…", Style = (Style)Application.Current.Resources["MacButton"] };
        exit.Click += (_, _) => ShellHost.ConfirmExitToWindows();
        Group(
            Row("Windows taskbar & system tray", toggleTb, "Temporarily bring back the taskbar to reach tray icons"),
            Row("Windows Settings", MakeLink("Open…", "ms-settings:")),
            Row("Quit MacShell", exit, "Restores the taskbar and desktop. Emergency shortcut: Ctrl + Alt + Shift + Q"));
    }

    void BuildWindowsLink((string id, string name, string sym, string top, string bottom) pane)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
        sp.Children.Add(new Image { Source = PaneIcon(pane), Width = 64, Height = 64 });
        var t = Txt(pane.name, 20, FontWeights.Bold);
        t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 14, 0, 6);
        sp.Children.Add(t);
        WindowsPanes.TryGetValue(pane.id, out var info);
        var d = Txt(info.blurb ?? "", 12, brush: "SecondaryLabelBrush");
        d.TextAlignment = TextAlignment.Center; d.MaxWidth = 340;
        sp.Children.Add(d);
        var b = new Button { Content = "Open in Windows Settings…", Style = (Style)Application.Current.Resources["MacDefaultButton"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0), Height = 26 };
        b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(info.uri ?? "ms-settings:") { UseShellExecute = true }); } catch { } };
        sp.Children.Add(b);
        _content.Children.Add(sp);
    }
}
