using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>Launchpad: full-screen grid of every installed app.</summary>
public class LaunchpadWindow : Window
{
    static LaunchpadWindow _instance;
    readonly Grid _root = new();
    readonly Grid _content = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly Canvas _pagesHost = new() { ClipToBounds = false };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBox _search = new();
    readonly List<Grid> _pages = new();
    List<AppEntry> _apps = new();
    int _page;
    const int Cols = 7, Rows = 5;
    double _pageW, _pageH, _cellW, _cellH, _icon;
    bool _closing;
    Point? _dragStart;
    double _dragOffset;
    readonly TranslateTransform _pageShift = new();

    public static void Toggle()
    {
        if (_instance != null) { _instance.CloseAnimated(); return; }
        _instance = new LaunchpadWindow();
        _instance.Show();
        _instance.Activate();
    }

    LaunchpadWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Launchpad";
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Background = Brushes.Black;
        Left = ShellHost.ScreenPx.Left / ShellHost.Scale;
        Top = ShellHost.ScreenPx.Top / ShellHost.Scale;
        Width = ShellHost.ScreenDip.Width;
        Height = ShellHost.ScreenDip.Height;

        var bg = new Image { Source = Wallpaper.Blurred, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(bg, BitmapScalingMode.HighQuality);
        _root.Children.Add(bg);
        _root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) });

        // search field
        _search.Style = (Style)Application.Current.Resources["MacSearchField"];
        _search.Width = 240;
        _search.Height = 28;
        _search.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        _search.Foreground = Brushes.White;
        _search.CaretBrush = Brushes.White;
        _search.Resources["SearchFieldBrush"] = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        _search.Resources["SecondaryLabelBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        _search.Resources["TertiaryLabelBrush"] = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        _search.Resources["LabelBrush"] = Brushes.White;
        _search.Resources["GroupBoxBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _search.HorizontalAlignment = HorizontalAlignment.Center;
        _search.VerticalAlignment = VerticalAlignment.Top;
        _search.Margin = new Thickness(0, 40, 0, 0);
        _search.TextChanged += (_, _) => BuildPages();
        _content.Children.Add(_search);

        _pagesHost.RenderTransform = _pageShift;
        _content.Children.Add(_pagesHost);
        _dots.VerticalAlignment = VerticalAlignment.Bottom;
        _dots.Margin = new Thickness(0, 0, 0, (ShellHost.Dock?.ReservedHeight ?? 70) + 16);
        _content.Children.Add(_dots);
        _root.Children.Add(_content);
        Content = _root;

        _apps = AppCatalog.Apps.ToList();
        Loaded += (_, _) => { Layout(); BuildPages(); AnimateIn(); _search.Focus(); };
        Deactivated += (_, _) => CloseAnimated();
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += (_, e) => { GoToPage(_page + (e.Delta < 0 ? 1 : -1)); e.Handled = true; };
        MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragOffset = 0; CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
            _dragOffset = e.GetPosition(this).X - _dragStart.Value.X;
            _pageShift.BeginAnimation(TranslateTransform.XProperty, null);
            _pageShift.X = -_page * _pageW + _dragOffset;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            if (_dragStart == null) return;
            _dragStart = null;
            if (Math.Abs(_dragOffset) > 60) GoToPage(_page + (_dragOffset < 0 ? 1 : -1));
            else if (Math.Abs(_dragOffset) < 4) CloseAnimated();
            else GoToPage(_page);
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            AddExStyle(h, WS_EX_TOOLWINDOW);
            WindowTracker.RegisterChrome(this);
        };
        ContentRendered += (_, _) => { if (ShellHost.Dock != null) { ShellHost.Dock.Topmost = false; ShellHost.Dock.Topmost = true; } };
    }

    void Layout()
    {
        double dockH = ShellHost.Dock?.ReservedHeight ?? 70;
        double top = 110, bottom = dockH + 50;
        double sidePad = Math.Max(60, Width * 0.08);
        _pageW = Width;
        _pageH = Height - top - bottom;
        _cellW = (Width - sidePad * 2) / Cols;
        _cellH = _pageH / Rows;
        _icon = Math.Clamp(Math.Min(_cellH - 34, _cellW * 0.62), 40, 104);
        Canvas.SetTop(_pagesHost, 0);
        _pagesHost.Margin = new Thickness(0, top, 0, 0);
        _sidePad = sidePad;
    }
    double _sidePad;

    void BuildPages()
    {
        string q = _search.Text.Trim();
        List<object> list;
        if (q.Length == 0)
        {
            var main = _apps.Where(a => !IsUtility(a)).Cast<object>().ToList();
            var other = _apps.Where(IsUtility).ToList();
            if (other.Count > 0) main.Insert(Math.Min(main.Count, Cols * Rows - 1), new LaunchFolder("Other", other));
            list = main;
        }
        else
        {
            list = _apps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(a => a.Name).Cast<object>().ToList();
        }
        _pagesHost.Children.Clear();
        _pages.Clear();
        int perPage = Cols * Rows;
        int pageCount = Math.Max(1, (int)Math.Ceiling(list.Count / (double)perPage));
        for (int p = 0; p < pageCount; p++)
        {
            var g = new Grid { Width = _pageW, Height = _pageH };
            var inner = new UniformGrid2(Cols, Rows) { Margin = new Thickness(_sidePad, 0, _sidePad, 0) };
            foreach (var entry in list.Skip(p * perPage).Take(perPage))
                inner.Children.Add(entry is LaunchFolder f ? MakeFolderTile(f) : MakeTile((AppEntry)entry, q.Length > 0 && entry == list[0], p == _page));
            g.Children.Add(inner);
            Canvas.SetLeft(g, p * _pageW);
            _pagesHost.Children.Add(g);
            _pages.Add(g);
        }
        _page = Math.Min(_page, pageCount - 1);
        if (q.Length > 0) _page = 0;
        _pageShift.BeginAnimation(TranslateTransform.XProperty, null);
        _pageShift.X = -_page * _pageW;
        BuildDots();
    }

    class LaunchFolder
    {
        public string Name; public List<AppEntry> Apps;
        public LaunchFolder(string n, List<AppEntry> a) { Name = n; Apps = a; }
    }

    static readonly HashSet<string> UtilityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Character Map", "Command Prompt", "Component Services", "Computer Management", "Control Panel", "Defragment and Optimize Drives",
        "Disk Cleanup", "Event Viewer", "iSCSI Initiator", "Local Security Policy", "ODBC Data Sources (32-bit)", "ODBC Data Sources (64-bit)",
        "Performance Monitor", "Print Management", "Recovery Drive", "Registry Editor", "Resource Monitor", "Services", "System Configuration",
        "System Information", "Task Scheduler", "Windows Memory Diagnostic", "Windows PowerShell ISE", "Windows PowerShell ISE (x86)",
        "Windows PowerShell (x86)", "Windows Tools", "Windows Fax and Scan", "Steps Recorder", "Math Input Panel", "Windows Speech Recognition",
        "Windows Defender Firewall with Advanced Security", "Remote Desktop Connection", "Quick Assist", "Magnifier", "Narrator", "On-Screen Keyboard",
        "Windows Media Player Legacy", "Task Manager", "Run", "File Explorer", "Windows Backup", "Disk Management", "Device Manager", "XPS Viewer",
        "Hyper-V Manager", "Hyper-V Quick Create", "Print 3D", "Mixed Reality Portal", "Dev Home", "Windows Security", "Get Started", "Tips",
    };

    static bool IsUtility(AppEntry a)
    {
        if (UtilityNames.Contains(a.Name)) return true;
        string t = a.TargetPath ?? "";
        if (t.EndsWith(".msc", StringComparison.OrdinalIgnoreCase) || t.EndsWith("mmc.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Name.StartsWith("Check for", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    FrameworkElement MakeFolderTile(LaunchFolder f)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        var box = new Border
        {
            Width = _icon, Height = _icon, CornerRadius = new CornerRadius(_icon * 0.24), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(_icon * 0.13),
        };
        var mini = new UniformGrid2(3, 3);
        foreach (var a in f.Apps.Take(9))
        {
            var img = new Image { Margin = new Thickness(_icon * 0.025) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            ShellIcons.Load(a.IconSource, 48, false, b => img.Source = b ?? MacIcons.GenericApp);
            mini.Children.Add(img);
        }
        box.Child = mini;
        sp.Children.Add(box);
        sp.Children.Add(new TextBlock
        {
            Text = f.Name, Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.6 },
        });
        sp.MouseLeftButtonDown += (_, e) => e.Handled = true;
        sp.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenFolder(f); };
        return sp;
    }

    Grid _folderOverlay;

    void OpenFolder(LaunchFolder f)
    {
        CloseFolder();
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)) };
        int cols = Math.Min(Cols, 6), rows = Math.Max(1, (int)Math.Ceiling(f.Apps.Count / (double)cols));
        double panelW = Math.Min(Width * 0.7, cols * _cellW), panelH = Math.Min(Height * 0.6, rows * _cellH * 0.95 + 20);
        var title = new TextBlock { Text = f.Name, Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16), Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 } };
        var grid = new UniformGrid2(cols, rows);
        foreach (var a in f.Apps) grid.Children.Add(MakeTile(a, false, true));
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = panelH - 20 };
        grid.Height = rows * _cellH * 0.95;
        var panel = new Border
        {
            Width = panelW, CornerRadius = new CornerRadius(34), Padding = new Thickness(20, 10, 20, 10), Child = scroll,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
        };
        panel.MouseLeftButtonDown += (_, e) => e.Handled = true;
        panel.MouseLeftButtonUp += (_, e) => e.Handled = true;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(panel);
        overlay.Children.Add(stack);
        overlay.MouseLeftButtonDown += (_, e) => e.Handled = true;
        overlay.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseFolder(); };
        _folderOverlay = overlay;
        _content.Children.Add(overlay);
        overlay.Opacity = 0;
        overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    bool CloseFolder()
    {
        if (_folderOverlay == null) return false;
        _content.Children.Remove(_folderOverlay);
        _folderOverlay = null;
        return true;
    }

    FrameworkElement MakeTile(AppEntry app, bool highlight, bool priority = false)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Arrow };
        var img = new Image { Width = _icon, Height = _icon, Source = MacIcons.GenericApp };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        ShellIcons.Load(app.IconSource, 256, false, b => { if (b != null) img.Source = b; }, priority, "dock:" + app.IconSource);
        var iconHost = new Border { Child = img, Padding = new Thickness(_icon * 0.04), CornerRadius = new CornerRadius(_icon * 0.24), HorizontalAlignment = HorizontalAlignment.Center };
        if (highlight) iconHost.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        sp.Children.Add(iconHost);
        var label = new TextBlock
        {
            Text = app.Name, Foreground = Brushes.White, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = _cellW - 10, Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.6 },
        };
        sp.Children.Add(label);
        Point? down = null;
        sp.MouseLeftButtonDown += (_, e) => { e.Handled = true; img.Opacity = 0.6; down = e.GetPosition(this); sp.CaptureMouse(); };
        sp.MouseLeave += (_, _) => { if (down == null) img.Opacity = 1; };
        sp.MouseMove += (_, e) =>
        {
            if (down == null || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - down.Value).Length < 7) return;
            // drag an app out of Launchpad (e.g. onto the Dock to keep it there)
            down = null;
            img.Opacity = 1;
            sp.ReleaseMouseCapture();
            var data = new DataObject();
            data.SetData(DockWindow.AppDragFormat, app.ParsingName);
            DragGhost.Run(sp, data, DragDropEffects.Copy | DragDropEffects.Link, new[] { img.Source }, _icon);
        };
        sp.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            img.Opacity = 1;
            sp.ReleaseMouseCapture();
            if (down == null) return;   // it was a drag
            down = null;
            AppCatalog.Launch(app.ParsingName);
            CloseAnimated();
        };
        sp.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Mb.Context(
                Mb.Item("Open", () => { AppCatalog.Launch(app.ParsingName); CloseAnimated(); }),
                Mb.Item("Keep in Dock", () =>
                {
                    Settings.Current.DockApps ??= new List<PinnedApp>();
                    if (Settings.Current.DockApps.All(p => p.Target != app.ParsingName))
                        Settings.Current.DockApps.Add(new PinnedApp { Name = app.Name, Target = app.ParsingName, ExePath = app.TargetPath });
                    Settings.Save();
                }),
                Mb.Item("Show in Finder", () => { CloseAnimated(); ShellHost.RevealInFinder(app.TargetPath); }, enabled: app.TargetPath != null && File.Exists(app.TargetPath))).IsOpen = true;
        };
        return sp;
    }

    void BuildDots()
    {
        _dots.Children.Clear();
        if (_pages.Count <= 1) return;
        for (int i = 0; i < _pages.Count; i++)
        {
            int idx = i;
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(5), Fill = new SolidColorBrush(i == _page ? Colors.White : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), Cursor = Cursors.Hand };
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoToPage(idx); };
            dot.MouseLeftButtonDown += (_, e) => e.Handled = true;
            _dots.Children.Add(dot);
        }
    }

    void GoToPage(int p)
    {
        p = Math.Clamp(p, 0, Math.Max(0, _pages.Count - 1));
        _page = p;
        var anim = new DoubleAnimation(-p * _pageW, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _pageShift.BeginAnimation(TranslateTransform.XProperty, anim);
        BuildDots();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (CloseFolder()) { } else if (_search.Text.Length > 0) _search.Text = ""; else CloseAnimated();
                e.Handled = true; break;
            case Key.Enter:
                {
                    string q = _search.Text.Trim();
                    var first = _apps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                        .OrderBy(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(a => a.Name).FirstOrDefault();
                    if (q.Length > 0 && first != null) { AppCatalog.Launch(first.ParsingName); CloseAnimated(); }
                    e.Handled = true; break;
                }
            case Key.Right when _search.Text.Length == 0: GoToPage(_page + 1); e.Handled = true; break;
            case Key.Left when _search.Text.Length == 0: GoToPage(_page - 1); e.Handled = true; break;
        }
    }

    void AnimateIn()
    {
        _content.RenderTransform = new ScaleTransform(1.1, 1.1);
        _content.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(260);
        ((ScaleTransform)_content.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        ((ScaleTransform)_content.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        _root.Opacity = 0;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _instance = null;
        var d = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        if (_content.RenderTransform is not ScaleTransform st) _content.RenderTransform = st = new ScaleTransform(1, 1);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.08, d) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) => Close();
        _root.BeginAnimation(OpacityProperty, fade);
    }
}

/// <summary>Fixed-size uniform grid that fills row-major.</summary>
public class UniformGrid2 : Panel
{
    readonly int _cols, _rows;
    public UniformGrid2(int cols, int rows) { _cols = cols; _rows = rows; }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 800 : available.Width;
        double h = double.IsInfinity(available.Height) ? 600 : available.Height;
        var cell = new Size(w / _cols, h / _rows);
        foreach (UIElement c in InternalChildren) c.Measure(cell);
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double cw = final.Width / _cols, ch = final.Height / _rows;
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(i % _cols * cw, i / _cols * ch, cw, ch));
        return final;
    }
}
