using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>Mission Control / App Exposé with live DWM window thumbnails.</summary>
public class MissionControlWindow : Window
{
    static MissionControlWindow _instance;

    class Thumb
    {
        public TrackedWindow Window;
        public IntPtr Handle;
        public Rect From, To;       // physical pixels, client coords
        public Rect Current;
    }

    readonly Canvas _overlay = new();
    readonly Border _highlight = new() { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly Border _titlePill = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly TextBlock _titleText = new() { Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
    readonly Image _titleIcon = new() { Width = 18, Height = 18, Margin = new Thickness(0, 0, 6, 0) };
    readonly List<Thumb> _thumbs = new();
    readonly RunningApp _filter;
    IntPtr _hwnd;
    double _t;         // animation 0 → 1
    bool _closing;
    TimeSpan _start;
    Thumb _hover;
    double S => ShellHost.Scale;

    public static void Toggle() { if (_instance != null) _instance.CloseAnimated(null); else Show(null); }

    public static void Show(RunningApp app)
    {
        if (_instance != null) return;
        _instance = new MissionControlWindow(app);
        _instance.Show();
        _instance.Activate();
    }

    MissionControlWindow(RunningApp filter)
    {
        _filter = filter;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Mission Control";
        FontFamily = Theme.Font;
        Background = Brushes.Black;
        Left = ShellHost.ScreenPx.Left / S; Top = ShellHost.ScreenPx.Top / S;
        Width = ShellHost.ScreenDip.Width; Height = ShellHost.ScreenDip.Height;

        var root = new Grid();
        var wall = new Image { Source = Wallpaper.Image, Stretch = Stretch.UniformToFill };
        root.Children.Add(wall);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) });
        if (filter == null) root.Children.Add(SpacesBar());
        _highlight.BorderBrush = new SolidColorBrush(Theme.Accent);
        _overlay.Children.Add(_highlight);
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_titleIcon);
        pillRow.Children.Add(_titleText);
        _titlePill.Child = pillRow;
        _titlePill.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x28, 0x28, 0x2A));
        _overlay.Children.Add(_titlePill);
        root.Children.Add(_overlay);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            AddExStyle(_hwnd, WS_EX_TOOLWINDOW);
            WindowTracker.RegisterChrome(this);
        };
        Loaded += (_, _) => { Build(); _start = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; };
        MouseMove += (_, e) => UpdateHover(e.GetPosition(this));
        MouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(this);
            var hit = HitTest(p);
            CloseAnimated(hit?.Window);
        };
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Up) CloseAnimated(null); };
        Deactivated += (_, _) => { if (!_closing) CloseAnimated(null); };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            foreach (var t in _thumbs) DwmUnregisterThumbnail(t.Handle);
            if (_instance == this) _instance = null;
        };
    }

    UIElement SpacesBar()
    {
        var bar = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        var thumb = new Border
        {
            Width = 150, Height = 150 * ShellHost.ScreenDip.Height / ShellHost.ScreenDip.Width, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            Background = new ImageBrush(Wallpaper.Image) { Stretch = Stretch.UniformToFill },
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Direction = 270, Opacity = 0.4 },
        };
        bar.Children.Add(thumb);
        bar.Children.Add(new TextBlock { Text = "Desktop", Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        return bar;
    }

    void Build()
    {
        var wins = WindowTracker.Windows.Where(w => !w.Minimized && IsWindow(w.Hwnd) && IsWindowVisible(w.Hwnd)).ToList();
        if (_filter != null) wins = wins.Where(w => w.AppKey == _filter.Key).ToList();
        double W = ShellHost.ScreenPx.Width, H = ShellHost.ScreenPx.Height;
        double top = (_filter == null ? 150 : 50) * S, pad = 50 * S, bottom = 50 * S;
        var area = new Rect(pad, top, W - pad * 2, H - top - bottom);
        int n = wins.Count;
        if (n == 0) return;

        // choose the grid that maximises thumbnail size
        int bestCols = 1; double bestScale = 0;
        var sizes = wins.Select(w => { var r = GetVisibleBounds(w.Hwnd); return new Size(Math.Max(100, r.Width), Math.Max(80, r.Height)); }).ToList();
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (int)Math.Ceiling(n / (double)cols);
            double cw = area.Width / cols, ch = area.Height / rows;
            double minScale = sizes.Min(sz => Math.Min((cw - 30 * S) / sz.Width, (ch - 50 * S) / sz.Height));
            if (minScale > bestScale) { bestScale = minScale; bestCols = cols; }
        }
        int cols2 = bestCols, rows2 = (int)Math.Ceiling(n / (double)cols2);
        double cellW = area.Width / cols2, cellH = area.Height / rows2;
        for (int i = 0; i < n; i++)
        {
            var w = wins[i];
            if (DwmRegisterThumbnail(_hwnd, w.Hwnd, out IntPtr th) != 0) continue;
            var vb = GetVisibleBounds(w.Hwnd);
            var from = new Rect(vb.Left - ShellHost.ScreenPx.Left, vb.Top - ShellHost.ScreenPx.Top, vb.Width, vb.Height);
            int row = i / cols2, col = i % cols2;
            int inRow = row == rows2 - 1 ? n - row * cols2 : cols2;
            double rowOffset = (cols2 - inRow) * cellW / 2;
            double scale = Math.Min(1, Math.Min((cellW - 30 * S) / sizes[i].Width, (cellH - 50 * S) / sizes[i].Height));
            double tw = sizes[i].Width * scale, thh = sizes[i].Height * scale;
            double cx = area.X + rowOffset + col * cellW + cellW / 2, cy = area.Y + row * cellH + (cellH - 24 * S) / 2;
            var to = new Rect(cx - tw / 2, cy - thh / 2, tw, thh);
            var t = new Thumb { Window = w, Handle = th, From = from, To = to, Current = from };
            _thumbs.Add(t);
            Apply(t);
        }
    }

    void Apply(Thumb t)
    {
        var r = t.Current;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom),
            opacity = 255, fVisible = true, fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(t.Handle, ref props);
    }

    void OnFrame(object sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_start == TimeSpan.Zero) _start = now;
        double elapsed = (now - _start).TotalMilliseconds;
        double p = Math.Clamp(elapsed / 260.0, 0, 1);
        double eased = 1 - Math.Pow(1 - p, 3);
        _t = _closing ? 1 - eased : eased;
        foreach (var t in _thumbs)
        {
            t.Current = new Rect(
                Lerp(t.From.X, t.To.X, _t), Lerp(t.From.Y, t.To.Y, _t),
                Lerp(t.From.Width, t.To.Width, _t), Lerp(t.From.Height, t.To.Height, _t));
            Apply(t);
        }
        if (p >= 1)
        {
            CompositionTarget.Rendering -= OnFrame;
            if (_closing) FinishClose();
        }
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    Thumb HitTest(Point dip)
    {
        var p = new Point(dip.X * S, dip.Y * S);
        return _thumbs.LastOrDefault(t => t.To.Contains(p));
    }

    void UpdateHover(Point dip)
    {
        if (_closing || _t < 0.99) return;
        var hit = HitTest(dip);
        if (hit == _hover) return;
        _hover = hit;
        if (hit == null) { _highlight.Visibility = _titlePill.Visibility = Visibility.Collapsed; return; }
        var r = hit.To;
        Canvas.SetLeft(_highlight, r.X / S - 5); Canvas.SetTop(_highlight, r.Y / S - 5);
        _highlight.Width = r.Width / S + 10; _highlight.Height = r.Height / S + 10;
        _highlight.Visibility = Visibility.Visible;
        var app = WindowTracker.FindByKey(hit.Window.AppKey);
        _titleText.Text = string.IsNullOrWhiteSpace(hit.Window.Title) ? app?.Name : hit.Window.Title;
        _titleIcon.Source = null;
        if (app?.IconSource != null) ShellIcons.Load(app.IconSource, 48, false, b => _titleIcon.Source = b);
        else if (app?.IsInternal == true) _titleIcon.Source = MacIcons.Finder;
        _titlePill.Visibility = Visibility.Visible;
        _titlePill.UpdateLayout();
        _titlePill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_titlePill, r.X / S + r.Width / S / 2 - _titlePill.DesiredSize.Width / 2);
        Canvas.SetTop(_titlePill, r.Bottom / S + 10);
    }

    TrackedWindow _activate;

    void CloseAnimated(TrackedWindow activate)
    {
        if (_closing) return;
        _closing = true;
        _activate = activate;
        _highlight.Visibility = _titlePill.Visibility = Visibility.Collapsed;
        if (activate != null)
        {
            // bring the chosen window to the front of the stack so it lands on top
            foreach (var t in _thumbs.Where(t => t.Window == activate).ToList()) { _thumbs.Remove(t); _thumbs.Add(t); }
        }
        _start = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    void FinishClose()
    {
        var a = _activate;
        Close();
        if (a != null) ActivateWindow(a.Hwnd);
    }
}

/// <summary>⌘Tab-style application switcher.</summary>
public class AppSwitcherWindow : Window
{
    static AppSwitcherWindow _instance;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 14, 14, 10) };
    readonly List<(RunningApp app, Border cell, TextBlock label)> _cells = new();
    int _sel;
    const double IconSize = 96;

    public static void Begin(bool reverse)
    {
        _instance?.Close();
        var apps = WindowTracker.AppsByRecency();
        if (apps.Count == 0) return;
        _instance = new AppSwitcherWindow(apps);
        _instance._sel = apps.Count > 1 ? (reverse ? apps.Count - 1 : 1) : 0;
        _instance.Show();
        _instance.Refresh();
    }

    public static void Step(bool reverse)
    {
        if (_instance == null) { Begin(reverse); return; }
        int n = _instance._cells.Count;
        if (n == 0) return;
        _instance._sel = (_instance._sel + (reverse ? -1 : 1) + n) % n;
        _instance.Refresh();
    }

    public static void Commit()
    {
        var w = _instance;
        if (w == null) return;
        _instance = null;
        var app = w._cells.Count > w._sel ? w._cells[w._sel].app : null;
        w.Close();
        if (app != null) WindowTracker.ActivateApp(app);
    }

    public static void Cancel() { _instance?.Close(); _instance = null; }

    public static void Key(int vk)
    {
        var w = _instance;
        if (w == null || w._cells.Count == 0) return;
        var app = w._cells[w._sel].app;
        switch (vk)
        {
            case 0x25: Step(true); break;
            case 0x27: Step(false); break;
            case 0x51: // Q
                WindowTracker.QuitApp(app);
                w._row.Children.Remove(w._cells[w._sel].cell);
                w._cells.RemoveAt(w._sel);
                if (w._sel >= w._cells.Count) w._sel = Math.Max(0, w._cells.Count - 1);
                w.Refresh();
                break;
            case 0x48: WindowTracker.HideApp(app); break; // H
        }
    }

    AppSwitcherWindow(List<RunningApp> apps)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.Font;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        bool dark = Theme.IsDark;
        var card = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(dark ? Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xE6, 0xEC, 0xEC, 0xEE)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Margin = new Thickness(40),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Direction = 270, Opacity = 0.35 },
            Child = _row,
        };
        foreach (var app in apps.Take(14))
        {
            var sp = new StackPanel();
            var img = new Image { Width = IconSize, Height = IconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            if (app.IsInternal) img.Source = MacIcons.ForInternal(app.Key);
            else if (app.IconSource != null) ShellIcons.Load(app.IconSource, 256, false, b => img.Source = b ?? MacIcons.GenericApp, true, "dock:" + app.IconSource);
            else img.Source = MacIcons.GenericApp;
            var cell = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(8), Margin = new Thickness(2, 0, 2, 0), Child = img };
            sp.Children.Add(cell);
            var label = new TextBlock { Text = app.Name, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Hidden, MaxWidth = IconSize + 60, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Foreground = new SolidColorBrush(dark ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD9, 0, 0, 0));
            sp.Children.Add(label);
            _row.Children.Add(sp);
            _cells.Add((app, cell, label));
            int idx = _cells.Count - 1;
            sp.MouseLeftButtonUp += (_, _) => { _sel = idx; Commit(); };
            sp.MouseEnter += (_, _) => { _sel = idx; Refresh(); };
        }
        Content = card;
        Loaded += (_, _) =>
        {
            Left = ShellHost.ScreenPx.Left / ShellHost.Scale + (ShellHost.ScreenDip.Width - ActualWidth) / 2;
            Top = ShellHost.ScreenPx.Top / ShellHost.Scale + (ShellHost.ScreenDip.Height - ActualHeight) / 2;
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            AddExStyle(h, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            WindowTracker.RegisterChrome(this);
        };
    }

    void Refresh()
    {
        bool dark = Theme.IsDark;
        for (int i = 0; i < _cells.Count; i++)
        {
            var (_, cell, label) = _cells[i];
            cell.Background = i == _sel ? new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)) : Brushes.Transparent;
            label.Visibility = i == _sel ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
