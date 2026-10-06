using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;
using Path = System.Windows.Shapes.Path;

namespace MacShell.Shell;

/// <summary>The macOS menu bar: Apple menu, the active app's menus, status items and clock.</summary>
public class MenuBarWindow : Window
{
    readonly Border _bg = new(), _tint = new();
    readonly Menu _menu = new() { VerticalAlignment = VerticalAlignment.Stretch };
    readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock _clock = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _timerText = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly SolidColorBrush _text = new(Colors.Black);
    StatusButton _ccButton, _clockButton, _wifiButton, _batteryButton, _timerButton, _soundButton, _searchButton;
    BatteryIcon _battery;
    TextBlock _batteryPct;
    SymbolIcon _wifiIcon, _soundIcon;
    int _soundTick;
    IntPtr _hwnd;
    bool _dark;
    public IntPtr Handle => _hwnd;

    public MenuBarWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        FontFamily = Theme.Font;
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Title = "Menu Bar";
        Resources["MenuBarHighlightBrush"] = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0));
        Foreground = _text;

        var root = new Grid();
        root.Children.Add(_bg);
        root.Children.Add(_tint);
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_right, Dock.Right);
        dock.Children.Add(_right);
        _menu.Margin = new Thickness(6, 0, 0, 0);
        dock.Children.Add(_menu);
        root.Children.Add(dock);
        Content = root;

        _menu.AddHandler(MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is MenuItem mi && mi.Parent == _menu)
                MenuDismisser.Opened("menubar", () => { foreach (MenuItem m in _menu.Items) m.IsSubmenuOpen = false; });
        }));
        _menu.AddHandler(MenuItem.SubmenuClosedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is MenuItem mi && mi.Parent == _menu && !_menu.Items.OfType<MenuItem>().Any(m => m.IsSubmenuOpen))
                MenuDismisser.Closed("menubar");
        }));

        BuildStatusItems();
        Reposition();
        ApplyAppearance();
        RebuildMenus();

        Wallpaper.Changed += ApplyAppearance;
        Theme.Changed += () => { ApplyAppearance(); RebuildMenus(); };
        WindowTracker.ActiveAppChanged += RebuildMenus;
        Settings.Changed += UpdateClock;
        Settings.Changed += ApplyHidden;
        CountdownTimer.Changed += UpdateTimer;
        TrayHost.Changed += RebuildTrayIcons;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { UpdateClock(); if (++_soundTick % 2 == 0) UpdateSoundIcon(); };
        timer.Start();
        var slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        slow.Tick += (_, _) => UpdateStatus();
        slow.Start();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(UpdateStatus);
        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(UpdateStatus);
        UpdateClock();
        ApplyHidden();   // (also the timer, battery and network items)

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            AddExStyle(_hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            WindowTracker.RegisterChrome(this);
            HwndSource.FromHwnd(_hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
                if (Takeover.HandleAppBarMessage(h, msg, w, l)) handled = true;
                else if ((uint)msg == Takeover.TaskbarCreatedMessage && !TrayHost.SelfBroadcast) Dispatcher.BeginInvoke(() => { Takeover.OnExplorerRestarted(); ShellHost.UpdateWorkArea(); });
                return IntPtr.Zero;
            });
        };
    }

    public void Reposition()
    {
        Left = ShellHost.ScreenPx.Left / ShellHost.Scale;
        Top = ShellHost.ScreenPx.Top / ShellHost.Scale;
        Width = ShellHost.ScreenDip.Width;
        Height = ShellHost.MenuBarHeight;
        ApplyAppearance();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden) Hide(); else { Show(); Topmost = false; Topmost = true; }
    }

    void ApplyAppearance()
    {
        _dark = Theme.IsDark || Wallpaper.TopIsDark;
        if (Wallpaper.Blurred != null)
            _bg.Background = Wallpaper.BlurBrush(new Rect(0, 0, ShellHost.ScreenDip.Width, ShellHost.MenuBarHeight), ShellHost.ScreenDip);
        _tint.Background = new SolidColorBrush(_dark ? Color.FromArgb(0x3A, 0x10, 0x10, 0x12) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        _text.Color = _dark ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0, 0, 0);
        // Replaced, not edited: menu templates use it through DynamicResource, which freezes it.
        Resources["MenuBarHighlightBrush"] = new SolidColorBrush(_dark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0));
    }

    // ------------------------------------------------------------------ status items

    void BuildStatusItems()
    {
        _right.Children.Clear();
        // the timer: "00:00", counting down once set; a click while its alarm rings stops it
        _timerText.Foreground = _text;
        System.Windows.Documents.Typography.SetNumeralAlignment(_timerText, FontNumeralAlignment.Tabular);
        _timerButton = new StatusButton(_timerText, b =>
        {
            if (CountdownTimer.Status == CountdownTimer.State.Ringing) CountdownTimer.Cancel();
            else TimerPopover.Toggle(b);
        }) { HideKey = "timer", HideName = "Timer" };
        _right.Children.Add(_timerButton);
        _right.Children.Add(_trayPanel);

        _battery = new BatteryIcon { Foreground = _text, VerticalAlignment = VerticalAlignment.Center };
        _batteryPct = new TextBlock { Foreground = _text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), FontSize = 12 };
        var bp = new StackPanel { Orientation = Orientation.Horizontal };
        bp.Children.Add(_batteryPct); bp.Children.Add(_battery);
        _batteryButton = new StatusButton(bp, b => ControlCenterWindow.ShowBattery(b)) { HideKey = "battery", HideName = "Battery" };
        _right.Children.Add(_batteryButton);

        _wifiIcon = new SymbolIcon { Symbol = "wifi", Width = 17, Height = 17, StrokeWidth = 2.0, Foreground = _text };
        _wifiButton = new StatusButton(_wifiIcon, _ => OpenUri("ms-availablenetworks:")) { HideKey = "wifi", HideName = "Wi‑Fi" };
        _right.Children.Add(_wifiButton);

        _soundIcon = new SymbolIcon { Symbol = "speaker", Width = 17, Height = 17, StrokeWidth = 1.9, Foreground = _text };
        _soundButton = new StatusButton(_soundIcon, b => SoundPopover.Toggle(b)) { HideKey = "sound", HideName = "Sound" };
        _right.Children.Add(_soundButton);
        UpdateSoundIcon();

        var search = new SymbolIcon { Symbol = "magnifyingglass", Width = 15, Height = 15, StrokeWidth = 2.1, Foreground = _text };
        _searchButton = new StatusButton(search, _ => SpotlightWindow.Toggle()) { HideKey = "spotlight", HideName = "Spotlight" };
        _right.Children.Add(_searchButton);

        var cc = new SymbolIcon { Symbol = "controlcenter", Width = 16, Height = 16, StrokeWidth = 1.8, Foreground = _text };
        _ccButton = new StatusButton(cc, b => ControlCenterWindow.Toggle(b));
        _right.Children.Add(_ccButton);

        _clock.Foreground = _text;
        _clockButton = new StatusButton(_clock, b => NotificationCenterWindow.Toggle(b)) { Padding = new Thickness(8, 0, 6, 0) };
        _right.Children.Add(_clockButton);
    }

    // ------------------------------------------------------------------ app menu extras (Windows tray icons)

    readonly StackPanel _trayPanel = new() { Orientation = Orientation.Horizontal };
    readonly Dictionary<string, TrayButton> _trayButtons = new();

    /// <summary>Newest icon leftmost, as macOS adds menu extras; hidden (NIS_HIDDEN), icon-less and taken-out ones skipped.</summary>
    void RebuildTrayIcons()
    {
        var shown = TrayHost.Icons.Where(i => !i.Hidden && i.Image != null && !IsHidden(TrayApp(i).key)).OrderByDescending(i => i.Order).ToList();
        var keep = new HashSet<string>(shown.Select(i => i.Key));
        foreach (var k in _trayButtons.Keys.Where(k => !keep.Contains(k)).ToList()) _trayButtons.Remove(k);
        _trayPanel.Children.Clear();
        foreach (var icon in shown)
        {
            if (!_trayButtons.TryGetValue(icon.Key, out var b)) _trayButtons[icon.Key] = b = new TrayButton();
            b.Bind(icon);
            _trayPanel.Children.Add(b);
        }
    }

    /// <summary>Where an icon is on screen (pixels), for Shell_NotifyIconGetRect.</summary>
    public RECT? TrayIconRect(TrayIcon icon)
    {
        if (!_trayButtons.TryGetValue(icon.Key, out var b) || !b.IsVisible) return null;
        var tl = b.PointToScreen(new Point(0, 0));
        var br = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
        return new RECT((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
    }

    /// <summary>A tray icon in the menu bar: left click / double click / right click go to the app like Explorer's tray.</summary>
    class TrayButton : Border
    {
        readonly Image _img = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
        TrayIcon _icon;

        public TrayButton()
        {
            Child = _img;
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
            Padding = new Thickness(6, 0, 6, 0);
            Margin = new Thickness(0, 1, 0, 1);
            CornerRadius = new CornerRadius(4);
            Background = Brushes.Transparent;
            VerticalAlignment = VerticalAlignment.Stretch;
            ToolTipService.SetInitialShowDelay(this, 600);
            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (_icon != null && MenuExtraDrag.ModifierHeld)
                {
                    var (key, name) = TrayApp(_icon);
                    MenuExtraDrag.Begin(this, key, name);
                    return;
                }
                Press(true);
                if (e.ClickCount == 2) Send(false, true);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (MenuExtraDrag.Active) return;   // (an Alt-drag, not a click for the app)
                Press(false);
                if (e.ClickCount < 2) Send(false, false);
            };
            MouseRightButtonDown += (_, e) => { e.Handled = true; Press(true); };
            MouseRightButtonUp += (_, e) => { e.Handled = true; Press(false); Send(true, false); };
            MouseLeave += (_, _) => Press(false);
        }

        public void Bind(TrayIcon icon)
        {
            _icon = icon;
            _img.Source = icon.Image;
            ToolTip = string.IsNullOrWhiteSpace(icon.Tip) ? null : icon.Tip;
        }

        void Press(bool down) => Background = down ? (Brush)FindResource("MenuBarHighlightBrush") : Brushes.Transparent;

        void Send(bool right, bool dbl)
        {
            if (_icon == null) return;
            var p = PointToScreen(new Point(ActualWidth / 2, ActualHeight));
            MenuDismisser.CloseAll();
            TrayHost.Click(_icon, right, dbl, p);
        }
    }

    static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    void UpdateClock()
    {
        var s = Settings.Current;
        var now = DateTime.Now;
        string time = s.Clock24Hour ? now.ToString(s.ClockShowSeconds ? "H:mm:ss" : "H:mm") : now.ToString(s.ClockShowSeconds ? "h:mm:ss tt" : "h:mm tt");
        var parts = new List<string>();
        if (s.ClockShowDay) parts.Add(now.ToString("ddd"));
        if (s.ClockShowDate) parts.Add(now.ToString("MMM d"));
        string date = string.Join(" ", parts);
        _clock.Text = date.Length > 0 ? date + "  " + time : time;
    }

    void UpdateTimer()
    {
        if (_timerButton == null) return;
        _timerButton.Visibility = IsHidden("timer") ? Visibility.Collapsed : Visibility.Visible;
        _timerText.Text = CountdownTimer.Text;
        _timerText.Opacity = CountdownTimer.Status == CountdownTimer.State.Paused ? 0.55 : 1;
        if (!TimerPopover.IsOpen) _timerButton.Active = CountdownTimer.Flash;   // (flashes while the alarm rings)
    }

    /// <summary>Diagnostics (--open timerpop): the timer's drop-down, as clicking "00:00" opens it.</summary>
    public void ToggleTimerPopover() => TimerPopover.Toggle(_timerButton);

    // ------------------------------------------------------------------ taking items out of the menu bar

    /// <summary>Items can be taken out of the menu bar (Settings › Control Center, or Alt-drag them out, like ⌘-drag
    /// on a Mac): the timer, battery, Wi-Fi, sound and Spotlight items, and apps' icons ("app:&lt;exe name&gt;").</summary>
    public static bool IsHidden(string key) => key != null && Settings.Current.MenuBarHidden.ContainsKey(key);

    public static void SetHidden(string key, string name, bool hidden)
    {
        if (key == null || IsHidden(key) == hidden) return;
        if (hidden) Settings.Current.MenuBarHidden[key] = name ?? key;
        else Settings.Current.MenuBarHidden.Remove(key);
        Settings.Save();
    }

    void ApplyHidden()
    {
        if (_wifiButton == null) return;
        _wifiButton.Visibility = IsHidden("wifi") ? Visibility.Collapsed : Visibility.Visible;
        _soundButton.Visibility = IsHidden("sound") ? Visibility.Collapsed : Visibility.Visible;
        _searchButton.Visibility = IsHidden("spotlight") ? Visibility.Collapsed : Visibility.Visible;
        UpdateTimer();
        UpdateStatus();
        RebuildTrayIcons();
    }

    static readonly Dictionary<int, string> _trayPaths = new();

    /// <summary>The app a tray icon belongs to: "app:&lt;exe file name&gt;" and its name (the exe's description). Windows'
    /// own icons (all explorer.exe's) are told apart: "app:explorer.exe#&lt;icon&gt;", named by their tooltip.</summary>
    public static (string key, string name) TrayApp(TrayIcon icon)
    {
        string path = null;
        if (icon.ProcessId > 0 && !_trayPaths.TryGetValue(icon.ProcessId, out path))
        {
            path = GetProcessPath((uint)icon.ProcessId);
            if (path != null) _trayPaths[icon.ProcessId] = path;
        }
        string tip = (icon.Tip ?? "").Split('\n')[0].Trim();
        if (path == null) return ("tip:" + tip.ToLowerInvariant(), tip.Length > 0 ? tip : "App");
        string exe = System.IO.Path.GetFileName(path).ToLowerInvariant();
        if (exe == "explorer.exe")
            return ($"app:explorer.exe#{(icon.Guid != Guid.Empty ? icon.Guid.ToString() : icon.UID.ToString())}", tip.Length > 0 ? tip : "Windows");
        string name = null;
        try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
        if (string.IsNullOrEmpty(name)) name = tip;
        if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
        return ("app:" + exe, name);
    }

    /// <summary>For Settings: the apps with icons in the menu bar now, and those taken out of it.</summary>
    public static List<(string key, string name, ImageSource image)> TrayApps()
    {
        var list = new List<(string key, string name, ImageSource image)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var icon in TrayHost.Icons.Where(i => !i.Hidden && i.Image != null).OrderByDescending(i => i.Order))
        {
            var (key, name) = TrayApp(icon);
            if (seen.Add(key)) list.Add((key, name, icon.Image));
        }
        foreach (var (key, name) in Settings.Current.MenuBarHidden)
        {
            bool app = key.StartsWith("app:") || key.StartsWith("tip:");
            if (app && seen.Add(key)) list.Add((key, name, null));
        }
        return list;
    }

    /// <summary>
    /// Alt-drag (or Ctrl-drag) an item out of the menu bar to take it out, like ⌘-drag on a Mac: a copy of it follows
    /// the pointer, with an ✕ once it's far enough below the menu bar; let go there and it's gone (Settings › Control
    /// Center brings it back). Let go anywhere else and nothing changes.
    /// </summary>
    static class MenuExtraDrag
    {
        const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_CONTROL = 0x11, VK_MENU = 0x12;
        public static bool Active { get; private set; }
        public static bool ModifierHeld => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        public static void Begin(FrameworkElement item, string key, string name)
        {
            if (Active || key == null || item.ActualWidth < 1) return;
            Active = true;
            GetCursorPos(out var start);
            var tl = item.PointToScreen(new Point(0, 0));
            int grabX = start.X - (int)tl.X, grabY = start.Y - (int)tl.Y;
            var ghost = new ItemGhost(item);
            ghost.Show();
            ghost.MoveTo(start.X - grabX, start.Y - grabY, false);
            item.Opacity = 0.3;
            double s = ShellHost.Scale;
            int barBottom = (int)Math.Round(ShellHost.ScreenPx.Top + ShellHost.MenuBarHeight * s);
            int button = SystemParameters.SwapButtons ? VK_RBUTTON : VK_LBUTTON;
            bool armed = false;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            t.Tick += (_, _) =>
            {
                GetCursorPos(out var p);
                armed = p.Y > barBottom + (int)(22 * s);
                ghost.MoveTo(p.X - grabX, p.Y - grabY, armed);
                if ((GetAsyncKeyState(button) & 0x8000) != 0) return;
                t.Stop();
                ghost.Close();
                item.Opacity = 1;
                Active = false;
                if (armed) SetHidden(key, name, true);
            };
            t.Start();
        }

        /// <summary>The dragged item: a picture of it on a small pill, click-through, with an ✕ when it will go.</summary>
        sealed class ItemGhost : Window
        {
            readonly Border _x;
            IntPtr _hwnd;

            public ItemGhost(FrameworkElement item)
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowInTaskbar = false;
                ShowActivated = false;
                Topmost = true;
                IsHitTestVisible = false;
                ResizeMode = ResizeMode.NoResize;
                SizeToContent = SizeToContent.WidthAndHeight;
                Left = -10000;
                Top = -10000;
                // (a picture taken now, before the item in the menu bar dims)
                double s = VisualTreeHelper.GetDpi(item).DpiScaleX;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(item), null, new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(item.ActualWidth * s), (int)Math.Ceiling(item.ActualHeight * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
                shot.Render(dv);
                shot.Freeze();
                bool dark = Theme.IsDark || Wallpaper.TopIsDark;
                var pill = new Border
                {
                    Width = item.ActualWidth, Height = item.ActualHeight, CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(dark ? Color.FromArgb(0xB0, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xD0, 0xF2, 0xF2, 0xF4)),
                    Child = new Image { Source = shot, Width = item.ActualWidth, Height = item.ActualHeight },
                };
                _x = new Border
                {
                    Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Visibility = Visibility.Hidden,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5E)),
                    Child = new SymbolIcon { Symbol = "xmark", Width = 8, Height = 8, StrokeWidth = 3, Foreground = Brushes.White },
                };
                var grid = new Grid { Margin = new Thickness(0) };
                pill.Margin = new Thickness(7, 7, 0, 0);
                grid.Children.Add(pill);
                grid.Children.Add(_x);
                Content = grid;
                SourceInitialized += (_, _) =>
                {
                    _hwnd = new WindowInteropHelper(this).Handle;
                    AddExStyle(_hwnd, WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                };
            }

            /// <summary>The item's top-left at (x, y) screen pixels.</summary>
            public void MoveTo(int x, int y, bool armed)
            {
                _x.Visibility = armed ? Visibility.Visible : Visibility.Hidden;
                if (_hwnd == IntPtr.Zero) return;
                int pad = (int)Math.Round(7 * ShellHost.Scale);
                SetWindowPos(_hwnd, HWND_TOPMOST, x - pad, y - pad, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
    }

    /// <summary>Speaker glyph reflects mute and volume level, like the macOS Sound menu extra.</summary>
    public void UpdateSoundIcon()
    {
        if (_soundIcon == null) return;
        double v = AudioVolume.Get() ?? 0.5;
        _soundIcon.Symbol = AudioVolume.IsMuted() ? "speaker.slash" : v < 0.01 ? "speaker.0" : v < 0.34 ? "speaker.1" : "speaker";
    }

    void UpdateStatus()
    {
        GetSystemPowerStatus(out var ps);
        bool hasBattery = ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100;
        _batteryButton.Visibility = hasBattery && !IsHidden("battery") ? Visibility.Visible : Visibility.Collapsed;
        if (hasBattery)
        {
            _battery.Level = ps.BatteryLifePercent / 100.0;
            _battery.Charging = ps.ACLineStatus == 1;
            _batteryPct.Text = Settings.Current.ShowBatteryPercent ? ps.BatteryLifePercent + "%" : "";
            _batteryPct.Visibility = Settings.Current.ShowBatteryPercent ? Visibility.Visible : Visibility.Collapsed;
        }
        bool wifi = false, anyUp = false;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                anyUp = true;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = true;
            }
        }
        catch { }
        _wifiIcon.Symbol = wifi || !anyUp ? "wifi" : "network";
        _wifiIcon.Opacity = anyUp ? 1 : 0.35;
    }

    // ------------------------------------------------------------------ menus

    public void OpenMenu(int index)
    {
        foreach (MenuItem other in _menu.Items) other.IsSubmenuOpen = false;
        if (index >= 0 && index < _menu.Items.Count && _menu.Items[index] is MenuItem mi) mi.IsSubmenuOpen = true;
    }

    public void RebuildMenus()
    {
        foreach (MenuItem mi in _menu.Items) mi.IsSubmenuOpen = false;
        _menu.Items.Clear();
        _menu.Items.Add(AppleMenu());
        var app = WindowTracker.ActiveApp;
        if (app == null || app.Key == WindowTracker.FinderKey) AddFinderMenus();
        else if (app.Key == "internal:settings") AddSettingsMenus();
        else if (app.Key == Apps.Preview.PreviewWindow.AppId) AddPreviewMenus();
        else AddAppMenus(app);
    }

    /// <summary>A menu whose items are rebuilt every time it opens (enabled states follow the front document).</summary>
    MenuItem LiveMenu(string header, Func<object[]> build)
    {
        var mi = TopMenu(header, false, build());
        mi.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != mi) return;
            mi.Items.Clear();
            foreach (var i in build()) Mb.Add(mi.Items, i);
        };
        return mi;
    }

    void AddPreviewMenus()
    {
        _menu.Items.Add(TopMenu("Preview", true,
            Mb.Item("About Preview", AboutWindow.ShowWindow),
            Mb.Sep(),
            Mb.Item("Hide Preview", Apps.Preview.PreviewWindow.HideAll, "⌘H"),
            Mb.Sep(),
            Mb.Item("Quit Preview", () => ShellHost.QuitInternal(Apps.Preview.PreviewWindow.AppId), "⌘Q")));
        _menu.Items.Add(LiveMenu("File", Apps.Preview.PreviewWindow.MenuFile));
        _menu.Items.Add(LiveMenu("Edit", Apps.Preview.PreviewWindow.MenuEdit));
        _menu.Items.Add(LiveMenu("View", Apps.Preview.PreviewWindow.MenuView));
        _menu.Items.Add(LiveMenu("Tools", Apps.Preview.PreviewWindow.MenuTools));
        _menu.Items.Add(WindowMenu(WindowTracker.FindByKey(Apps.Preview.PreviewWindow.AppId)));
        _menu.Items.Add(HelpMenu("Preview"));
    }

    MenuItem TopMenu(object header, bool bold = false, params object[] items)
    {
        object h = header is string s ? new TextBlock { Text = s, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = _text, VerticalAlignment = VerticalAlignment.Center } : header;
        var mi = new MenuItem { Header = h, Foreground = _text };
        foreach (var i in items) Mb.Add(mi.Items, i);
        return mi;
    }

    MenuItem AppleMenu()
    {
        var logo = new Path { Data = MacIcons.AppleLogo, Fill = _text, Stretch = Stretch.Uniform, Height = 15, Width = 13, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = false };
        var mi = TopMenu(logo, false,
            Mb.Item("About This Mac", AboutWindow.ShowWindow),
            Mb.Sep(),
            Mb.Item("System Settings…", () => SettingsWindow.ShowPane(null)),
            Mb.Item("App Store…", () => AppCatalog.Launch("Microsoft.WindowsStore_8wekyb3d8bbwe!App")),
            Mb.Sep(),
            Mb.LazySub("Recent Items", RecentItems),
            Mb.Sep(),
            Mb.Item("Force Quit…", ForceQuitWindow.ShowWindow, "⌥⌘⎋"),
            Mb.Sep(),
            Mb.Item("Sleep", ShellHost.Sleep),
            Mb.Item("Restart…", ShellHost.ConfirmRestart),
            Mb.Item("Shut Down…", ShellHost.ConfirmShutDown),
            Mb.Sep(),
            Mb.Item("Lock Screen", ShellHost.Lock, "⌃⌘Q"),
            Mb.Item($"Log Out {UserDisplayName()}…", ShellHost.ConfirmLogOut, "⇧⌘Q"),
            Mb.Sep(),
            Mb.Item("Return to Windows…", ShellHost.ConfirmExitToWindows, "⌃⌥⇧Q"));
        mi.Padding = new Thickness(11, 0, 11, 0);
        return mi;
    }

    public static string UserDisplayName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
            var n = k?.GetValue("Logon User Name") as string;
            if (!string.IsNullOrWhiteSpace(n)) return n.Contains('\\') ? n[(n.LastIndexOf('\\') + 1)..] : n;
        }
        catch { }
        return Environment.UserName;
    }

    IEnumerable<object> RecentItems()
    {
        yield return Mb.SectionHeader("Applications");
        foreach (var t in Settings.Current.RecentApps.Take(10))
        {
            var e = AppCatalog.FindByParsingName(t);
            string name = e?.Name ?? (File.Exists(t) ? WindowTracker.FriendlyExeName(t) : t);
            string target = t;
            yield return Mb.Item(name, () => AppCatalog.Launch(target));
        }
        yield return Mb.Sep();
        yield return Mb.SectionHeader("Documents");
        foreach (var d in Settings.Current.RecentDocs.Where(File.Exists).Take(10))
        {
            string path = d;
            yield return Mb.Item(System.IO.Path.GetFileName(d), () => AppCatalog.OpenFile(path));
        }
        yield return Mb.Sep();
        yield return Mb.Item("Clear Menu", () => { Settings.Current.RecentApps.Clear(); Settings.Current.RecentDocs.Clear(); Settings.Save(); });
    }

    static void F(string cmd) => FinderCommands.Run(cmd);

    void AddFinderMenus()
    {
        var s = Settings.Current;
        _menu.Items.Add(TopMenu("Finder", true,
            Mb.Item("About Finder", AboutWindow.ShowWindow),
            Mb.Sep(),
            Mb.Item("Settings…", () => SettingsWindow.ShowPane("finder"), "⌘,"),
            Mb.Sep(),
            Mb.Item("Empty Trash…", () => F("emptyTrash"), "⇧⌘⌫"),
            Mb.Sep(),
            Mb.Item("Hide Finder", () => F("hide"), "⌘H"),
            Mb.Item("Hide Others", HideOthers, "⌥⌘H"),
            Mb.Item("Show All", ShowAll)));
        _menu.Items.Add(TopMenu("File", false,
            Mb.Item("New Finder Window", () => F("newWindow"), "⌘N"),
            Mb.Item("New Folder", () => F("newFolder"), "⇧⌘N"),
            Mb.Item("New Tab", () => F("newTab"), "⌘T"),
            Mb.Item("Open", () => F("open"), "⌘O"),
            Mb.Item("Close Window", () => F("close"), "⌘W"),
            Mb.Sep(),
            Mb.Item("Get Info", () => F("getInfo"), "⌘I"),
            Mb.Item("Rename", () => F("rename")),
            Mb.Item("Compress", () => F("compress")),
            Mb.Item("Duplicate", () => F("duplicate"), "⌘D"),
            Mb.Item("Make Alias", () => F("alias"), "⌃⌘A"),
            Mb.Item("Quick Look", () => F("quicklook"), "⌘Y"),
            Mb.Item("Show Original", () => F("showOriginal"), "⌘R"),
            Mb.Sep(),
            Mb.Item("Move to Trash", () => F("trash"), "⌘⌫"),
            Mb.Sep(),
            Mb.Item("Find", () => F("find"), "⌘F")));
        _menu.Items.Add(TopMenu("Edit", false,
            Mb.Item("Undo", null, "⌘Z", false),
            Mb.Item("Redo", null, "⇧⌘Z", false),
            Mb.Sep(),
            Mb.Item("Cut", () => F("cut"), "⌘X"),
            Mb.Item("Copy", () => F("copy"), "⌘C"),
            Mb.Item("Paste Item", () => F("paste"), "⌘V"),
            Mb.Item("Select All", () => F("selectAll"), "⌘A"),
            Mb.Sep(),
            Mb.Item("Copy as Pathname", () => F("copyPath"), "⌥⌘C"),
            Mb.Sep(),
            Mb.Item("Show Hidden Files", () => F("showHidden"), "⇧⌘.", isChecked: s.ShowHiddenFiles)));
        var active = FinderWindow.Active;
        string view = active?.ViewMode ?? "icons";
        _menu.Items.Add(TopMenu("View", false,
            Mb.Item("as Icons", () => F("view:icons"), "⌘1", isChecked: view == "icons"),
            Mb.Item("as List", () => F("view:list"), "⌘2", isChecked: view == "list"),
            Mb.Item("as Columns", () => F("view:columns"), "⌘3", isChecked: view == "columns"),
            Mb.Item("as Gallery", () => F("view:gallery"), "⌘4", isChecked: view == "gallery"),
            Mb.Sep(),
            Mb.Sub("Sort By",
                Mb.Item("Name", () => F("sort:name")),
                Mb.Item("Kind", () => F("sort:kind")),
                Mb.Item("Date Modified", () => F("sort:date")),
                Mb.Item("Size", () => F("sort:size"))),
            Mb.Sep(),
            Mb.Item(s.FinderShowSidebar ? "Hide Sidebar" : "Show Sidebar", () => F("toggleSidebar"), "⌃⌘S"),
            Mb.Item(s.FinderShowPathBar ? "Hide Path Bar" : "Show Path Bar", () => F("togglePathBar"), "⌥⌘P"),
            Mb.Item(s.FinderShowStatusBar ? "Hide Status Bar" : "Show Status Bar", () => F("toggleStatusBar"), "⌘/"),
            Mb.Sep(),
            Mb.Item("Enter Full Screen", () => F("zoom"), "⌃⌘F")));
        _menu.Items.Add(TopMenu("Go", false,
            Mb.Item("Back", () => F("back"), "⌘["),
            Mb.Item("Forward", () => F("forward"), "⌘]"),
            Mb.Item("Enclosing Folder", () => F("up"), "⌘↑"),
            Mb.Sep(),
            Mb.Item("Recents", () => F("go:recents"), "⇧⌘F"),
            Mb.Item("Documents", () => F("go:documents"), "⇧⌘O"),
            Mb.Item("Desktop", () => F("go:desktop"), "⇧⌘D"),
            Mb.Item("Downloads", () => F("go:downloads"), "⌥⌘L"),
            Mb.Item("Home", () => F("go:home"), "⇧⌘H"),
            Mb.Item("Computer", () => F("go:computer"), "⇧⌘C"),
            Mb.Item("iCloud Drive", () => F("go:icloud"), "⇧⌘I", Environment.GetEnvironmentVariable("OneDrive") != null),
            Mb.Item("Applications", () => F("go:applications"), "⇧⌘A"),
            Mb.Item("Utilities", () => F("go:utilities"), "⇧⌘U"),
            Mb.Sep(),
            Mb.Item("Go to Folder…", () => F("goto"), "⇧⌘G")));
        _menu.Items.Add(WindowMenu(WindowTracker.FindByKey(WindowTracker.FinderKey)));
        _menu.Items.Add(HelpMenu("macOS"));
    }

    void AddSettingsMenus()
    {
        _menu.Items.Add(TopMenu("System Settings", true,
            Mb.Item("About System Settings", AboutWindow.ShowWindow),
            Mb.Sep(),
            Mb.Item("Hide System Settings", () => SettingsWindow.Instance?.Hide(), "⌘H"),
            Mb.Sep(),
            Mb.Item("Quit System Settings", () => SettingsWindow.Instance?.Close(), "⌘Q")));
        _menu.Items.Add(TopMenu("Edit", false, Mb.Item("Undo", null, "⌘Z", false), Mb.Sep(), Mb.Item("Cut", null, "⌘X"), Mb.Item("Copy", null, "⌘C"), Mb.Item("Paste", null, "⌘V")));
        _menu.Items.Add(TopMenu("View", false,
            Mb.Item("Back", () => SettingsWindow.Instance?.GoBack(), "⌘["),
            Mb.Sep(),
            Mb.Item("Appearance", () => SettingsWindow.ShowPane("appearance")),
            Mb.Item("Desktop & Dock", () => SettingsWindow.ShowPane("dock")),
            Mb.Item("Wallpaper", () => SettingsWindow.ShowPane("wallpaper"))));
        _menu.Items.Add(WindowMenu(WindowTracker.FindByKey("internal:settings")));
        _menu.Items.Add(HelpMenu("System Settings"));
    }

    void AddAppMenus(RunningApp app)
    {
        string name = app.Name ?? "App";
        _menu.Items.Add(TopMenu(name, true,
            Mb.Item($"About {name}", () => AboutApp(app)),
            Mb.Sep(),
            Mb.Item("Settings…", () => ShellHost.SendToApp(0x11, 0xBC), "⌘,"),
            Mb.Sep(),
            Mb.Item($"Hide {name}", () => WindowTracker.HideApp(app), "⌘H"),
            Mb.Item("Hide Others", HideOthers, "⌥⌘H"),
            Mb.Item("Show All", ShowAll),
            Mb.Sep(),
            Mb.Item($"Quit {name}", () => WindowTracker.QuitApp(app), "⌘Q")));

        // Classic Win32 apps: mirror their real menu bar (global menu, just like a Mac).
        var hwnd = WindowTracker.LastExternalForeground;
        IntPtr hmenu = hwnd != IntPtr.Zero ? GetMenu(hwnd) : IntPtr.Zero;
        var native = hmenu != IntPtr.Zero ? NativeMenu.Read(hmenu, hwnd) : new List<NativeMenuItem>();
        if (native.Count > 0)
        {
            int idx = 0;
            foreach (var top in native.Where(n => !n.Separator))
            {
                int index = idx++;
                var mi = TopMenu(top.Text);
                if (top.SubMenu != IntPtr.Zero)
                {
                    mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
                    var sub = top.SubMenu;
                    mi.SubmenuOpened += (_, e) => { if (e.OriginalSource == mi) FillNative(mi, sub, hwnd, index); };
                }
                else
                {
                    uint id = top.Id;
                    mi.Click += (_, _) => PostMessage(hwnd, WM_COMMAND, new IntPtr(id), IntPtr.Zero);
                }
                _menu.Items.Add(mi);
            }
        }
        else
        {
            _menu.Items.Add(TopMenu("File", false,
                Mb.Item("New Window", () => ShellHost.SendToApp(0x11, 0x4E), "⌘N"),
                Mb.Item("New Tab", () => ShellHost.SendToApp(0x11, 0x54), "⌘T"),
                Mb.Item("Open…", () => ShellHost.SendToApp(0x11, 0x4F), "⌘O"),
                Mb.Sep(),
                Mb.Item("Close Window", () => PostMessage(WindowTracker.LastExternalForeground, WM_CLOSE, IntPtr.Zero, IntPtr.Zero), "⌘W"),
                Mb.Item("Save", () => ShellHost.SendToApp(0x11, 0x53), "⌘S"),
                Mb.Item("Save As…", () => ShellHost.SendToApp(0x11, 0x10, 0x53), "⇧⌘S"),
                Mb.Sep(),
                Mb.Item("Print…", () => ShellHost.SendToApp(0x11, 0x50), "⌘P")));
            _menu.Items.Add(TopMenu("Edit", false,
                Mb.Item("Undo", () => ShellHost.SendToApp(0x11, 0x5A), "⌘Z"),
                Mb.Item("Redo", () => ShellHost.SendToApp(0x11, 0x59), "⇧⌘Z"),
                Mb.Sep(),
                Mb.Item("Cut", () => ShellHost.SendToApp(0x11, 0x58), "⌘X"),
                Mb.Item("Copy", () => ShellHost.SendToApp(0x11, 0x43), "⌘C"),
                Mb.Item("Paste", () => ShellHost.SendToApp(0x11, 0x56), "⌘V"),
                Mb.Item("Select All", () => ShellHost.SendToApp(0x11, 0x41), "⌘A"),
                Mb.Sep(),
                Mb.Item("Find…", () => ShellHost.SendToApp(0x11, 0x46), "⌘F"),
                Mb.Sep(),
                Mb.Item("Emoji & Symbols", () => ShellHost.SendToApp(0x5B, 0xBE), "fn E")));
            _menu.Items.Add(TopMenu("View", false,
                Mb.Item("Actual Size", () => ShellHost.SendToApp(0x11, 0x30), "⌘0"),
                Mb.Item("Zoom In", () => ShellHost.SendToApp(0x11, 0xBB), "⌘+"),
                Mb.Item("Zoom Out", () => ShellHost.SendToApp(0x11, 0xBD), "⌘−"),
                Mb.Sep(),
                Mb.Item("Enter Full Screen", () => ShellHost.SendToApp(0x7A), "⌃⌘F")));
        }
        _menu.Items.Add(WindowMenu(app));
        _menu.Items.Add(HelpMenu(name));
    }

    void FillNative(MenuItem parent, IntPtr hmenu, IntPtr owner, int index)
    {
        parent.Items.Clear();
        foreach (var n in NativeMenu.Read(hmenu, owner, index))
        {
            if (n.Separator) { Mb.Add(parent.Items, Mb.Sep()); continue; }
            var mi = new MenuItem { Header = n.Text, InputGestureText = n.Shortcut ?? "", IsEnabled = n.Enabled, IsChecked = n.Checked };
            if (n.SubMenu != IntPtr.Zero)
            {
                mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
                var sub = n.SubMenu;
                mi.SubmenuOpened += (_, e) => { if (e.OriginalSource == mi) FillNative(mi, sub, owner, 0); };
            }
            else
            {
                uint id = n.Id;
                mi.Click += (_, e) =>
                {
                    e.Handled = true;
                    ActivateWindow(owner);
                    PostMessage(owner, WM_COMMAND, new IntPtr(id), IntPtr.Zero);
                };
            }
            parent.Items.Add(mi);
        }
    }

    MenuItem WindowMenu(RunningApp app)
    {
        var mi = TopMenu("Window");
        mi.Items.Add(new MenuItem { Header = "…" });
        mi.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != mi) return;
            mi.Items.Clear();
            var internalApp = app == null || app.IsInternal;
            Mb.Add(mi.Items, Mb.Item("Minimize", () => WindowAction("min"), "⌘M"));
            Mb.Add(mi.Items, Mb.Item("Zoom", () => WindowAction("zoom")));
            Mb.Add(mi.Items, Mb.Item("Fill", () => WindowAction("fill"), "fn⌃F"));
            Mb.Add(mi.Items, Mb.Item("Center", () => WindowAction("center"), "fn⌃C"));
            Mb.Add(mi.Items, Mb.Sub("Move & Resize",
                Mb.SectionHeader("Halves"),
                Mb.Item("Left", () => WindowAction("left"), "fn⌃←"),
                Mb.Item("Right", () => WindowAction("right"), "fn⌃→"),
                Mb.Item("Top", () => WindowAction("top"), "fn⌃↑"),
                Mb.Item("Bottom", () => WindowAction("bottom"), "fn⌃↓"),
                Mb.Sep(),
                Mb.SectionHeader("Quarters"),
                Mb.Item("Top Left", () => WindowAction("tl")),
                Mb.Item("Top Right", () => WindowAction("tr")),
                Mb.Item("Bottom Left", () => WindowAction("bl")),
                Mb.Item("Bottom Right", () => WindowAction("br"))));
            Mb.Add(mi.Items, Mb.Sep());
            Mb.Add(mi.Items, Mb.Item("Bring All to Front", () => { if (app != null) WindowTracker.ActivateApp(app); }));
            var live = app != null ? WindowTracker.FindByKey(app.Key) ?? app : null;
            if (live != null && live.Windows.Count > 0)
            {
                Mb.Add(mi.Items, Mb.Sep());
                var fg = GetForegroundWindow();
                foreach (var w in live.Windows)
                {
                    var hw = w.Hwnd;
                    string title = string.IsNullOrWhiteSpace(w.Title) ? live.Name : w.Title;
                    if (title.Length > 60) title = title[..57] + "…";
                    Mb.Add(mi.Items, Mb.Item(title, () => ActivateWindow(hw), isChecked: hw == fg || hw == WindowTracker.LastForeground));
                }
            }
        };
        return mi;
    }

    MenuItem HelpMenu(string name) => TopMenu("Help", false,
        Mb.Item($"{name} Help", () => ShellHost.SendToApp(0x70)),
        Mb.Sep(),
        Mb.Item("MacShell Keyboard Shortcuts", () => SettingsWindow.ShowPane("keyboard")));

    /// <summary>Window-management for the frontmost window (external app or our own).</summary>
    public static void WindowAction(string action)
    {
        IntPtr h = WindowTracker.ActiveApp?.IsInternal == true ? WindowTracker.LastForeground : WindowTracker.LastExternalForeground;
        if (h == IntPtr.Zero || !IsWindow(h)) return;
        RECT wa = default;
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref wa, 0);
        void Place(int x, int y, int w, int hh)
        {
            if (IsZoomed(h) || IsIconic(h)) ShowWindow(h, SW_RESTORE);
            // compensate for invisible resize borders so the visible frame lands exactly
            GetWindowRect(h, out RECT outer);
            var vis = GetVisibleBounds(h);
            int l = vis.Left - outer.Left, t = vis.Top - outer.Top, r = outer.Right - vis.Right, b = outer.Bottom - vis.Bottom;
            SetWindowPos(h, IntPtr.Zero, x - l, y - t, w + l + r, hh + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        int W = wa.Width, H = wa.Height, X = wa.Left, Y = wa.Top;
        switch (action)
        {
            case "min": ShowWindow(h, SW_MINIMIZE); break;
            case "zoom": ShowWindow(h, IsZoomed(h) ? SW_RESTORE : SW_MAXIMIZE); break;
            case "fill": Place(X, Y, W, H); break;
            case "center":
                {
                    var v = GetVisibleBounds(h);
                    Place(X + (W - v.Width) / 2, Y + (H - v.Height) / 2, v.Width, v.Height);
                    break;
                }
            case "left": Place(X, Y, W / 2, H); break;
            case "right": Place(X + W / 2, Y, W - W / 2, H); break;
            case "top": Place(X, Y, W, H / 2); break;
            case "bottom": Place(X, Y + H / 2, W, H - H / 2); break;
            case "tl": Place(X, Y, W / 2, H / 2); break;
            case "tr": Place(X + W / 2, Y, W - W / 2, H / 2); break;
            case "bl": Place(X, Y + H / 2, W / 2, H - H / 2); break;
            case "br": Place(X + W / 2, Y + H / 2, W - W / 2, H - H / 2); break;
        }
    }

    static void HideOthers()
    {
        var active = WindowTracker.ActiveApp;
        foreach (var a in WindowTracker.Apps.Values)
            if (a.Key != active?.Key) WindowTracker.HideApp(a);
    }

    static void ShowAll()
    {
        foreach (var a in WindowTracker.Apps.Values)
            foreach (var w in a.Windows.Where(w => w.Minimized)) ShowWindow(w.Hwnd, SW_SHOWNOACTIVATE);
    }

    static void AboutApp(RunningApp app)
    {
        string info = "";
        try
        {
            if (!string.IsNullOrEmpty(app.ExePath))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(app.ExePath);
                info = $"Version {vi.ProductVersion ?? vi.FileVersion}\n{vi.CompanyName}\n{vi.LegalCopyright}".Trim();
            }
        }
        catch { }
        ShellHost.Alert(app.Name, string.IsNullOrWhiteSpace(info) ? "No version information available." : info, "OK");
    }

    // ------------------------------------------------------------------ helper controls

    /// <summary>A menu-bar status item: highlights with a rounded pill while its popup is open.</summary>
    public class StatusButton : Border
    {
        public bool Active { get => _active; set { _active = value; Background = value ? (Brush)FindResource("MenuBarHighlightBrush") : Brushes.Transparent; } }
        bool _active;
        /// <summary>Set for items that can be taken out of the menu bar (Alt-drag, or Settings).</summary>
        public string HideKey, HideName;

        public StatusButton(UIElement content, Action<StatusButton> click)
        {
            Child = content;
            Padding = new Thickness(7, 0, 7, 0);
            Margin = new Thickness(0, 1, 0, 1);
            CornerRadius = new CornerRadius(4);
            Background = Brushes.Transparent;
            VerticalAlignment = VerticalAlignment.Stretch;
            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (HideKey != null && MenuExtraDrag.ModifierHeld) { MenuExtraDrag.Begin(this, HideKey, HideName); return; }
                click(this);
            };
        }

        /// <summary>Screen position (DIPs) of this item's bottom-right corner.</summary>
        public Point ScreenAnchor()
        {
            var p = PointToScreen(new Point(ActualWidth, ActualHeight));
            return new Point(p.X / ShellHost.Scale, p.Y / ShellHost.Scale);
        }
    }

    public class BatteryIcon : FrameworkElement
    {
        public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(BatteryIcon),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        double _level = 1; bool _charging;
        public double Level { get => _level; set { _level = value; InvalidateVisual(); } }
        public bool Charging { get => _charging; set { _charging = value; InvalidateVisual(); } }
        public BatteryIcon() { Width = 25; Height = 12; }

        protected override void OnRender(DrawingContext dc)
        {
            var fg = Foreground;
            var dim = fg.Clone(); dim.Opacity = 0.4;
            dc.DrawRoundedRectangle(null, new Pen(dim, 1), new Rect(0.5, 0.5, 21.5, 11), 3.2, 3.2);
            dc.DrawRoundedRectangle(dim, null, new Rect(23, 4, 1.6, 4), 0.8, 0.8);
            var fill = _level <= 0.2 && !_charging ? new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)) : _charging ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)) : fg;
            dc.DrawRoundedRectangle(fill, null, new Rect(2, 2, Math.Max(1.5, 18.5 * _level), 8), 1.8, 1.8);
            if (_charging)
            {
                var bolt = Geometry.Parse("M12.5,1.6 L7.5,7 L10.6,7 L9.5,10.8 L14.5,5.2 L11.4,5.2 Z");
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 0.3), bolt);
            }
        }
    }
}
