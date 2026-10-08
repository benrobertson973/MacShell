using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>Base for menu-bar popovers (Control Center, Notification Center, battery …).</summary>
public class Popover : Window
{
    protected readonly Border Card = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5), Padding = new Thickness(10) };
    protected MenuBarWindow.StatusButton Anchor;
    bool _closing;

    public Popover(double width)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = width + 40;
        FontFamily = Theme.Font;
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetResourceReference(ForegroundProperty, "LabelBrush");
        Card.Margin = new Thickness(20, 4, 20, 30);
        Card.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0xF2, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xF2, 0xEC, 0xEC, 0xEE));
        Card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        Card.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.3 };
        Content = Card;
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_closing) Close(); };
        Closed += (_, _) => { if (Anchor != null) Anchor.Active = false; };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW);
            WindowTracker.RegisterChrome(this);
        };
    }

    protected void PlaceUnder(MenuBarWindow.StatusButton anchor, bool alignRightEdge = true)
    {
        Anchor = anchor;
        if (anchor != null) anchor.Active = true;
        Loaded += (_, _) =>
        {
            double screenRight = (ShellHost.ScreenPx.Left / ShellHost.Scale) + ShellHost.ScreenDip.Width;
            double right = anchor != null ? anchor.ScreenAnchor().X + 20 + 8 : screenRight - 4;
            if (alignRightEdge) right = Math.Min(right + 60, screenRight + 12);
            Left = Math.Min(right, screenRight + 12) - ActualWidth;
            Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.MenuBarHeight + 2;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
            Card.BeginAnimation(OpacityProperty, fade);
        };
    }

    protected static Border Module(UIElement child, double padding = 10)
    {
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child, Margin = new Thickness(5) };
        b.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        b.BorderBrush = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0));
        b.BorderThickness = new Thickness(0.5);
        return b;
    }

    protected static TextBlock T(string s, double size = 13, FontWeight? w = null, string brush = "LabelBrush")
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = w ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    protected static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    protected static Border Separator()
    {
        var b = new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4) };
        b.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        return b;
    }

    /// <summary>A menu-like row ("Sound Settings…") that highlights under the mouse.</summary>
    protected static Border Link(string text, Action click)
    {
        var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Child = T(text, 13) };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }
}

public class ControlCenterWindow : Popover
{
    static ControlCenterWindow _instance;
    static DateTime _closedAt;

    public static void Toggle(MenuBarWindow.StatusButton anchor)
    {
        if (_instance != null) { _instance.Close(); return; }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return; // click on the icon that just closed us
        _instance = new ControlCenterWindow(anchor);
        _instance.Show();
        _instance.Activate();
    }

    public static void ShowBattery(MenuBarWindow.StatusButton anchor) => new BatteryPopover(anchor).Show();

    ControlCenterWindow(MenuBarWindow.StatusButton anchor) : base(310)
    {
        Title = "Control Center";
        Closed += (_, _) => { _instance = null; _closedAt = DateTime.Now; };
        var root = new StackPanel();

        // row 1: connectivity (left) + focus / small tiles (right)
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition());
        row1.ColumnDefinitions.Add(new ColumnDefinition());
        var conn = new StackPanel();
        conn.Children.Add(Toggle("wifi", "Wi-Fi", WifiName(), true, () => OpenUri("ms-availablenetworks:")));
        conn.Children.Add(Toggle("bluetooth", "Bluetooth", "On", true, () => OpenUri("ms-settings:bluetooth")));
        conn.Children.Add(Toggle("airdrop", "Nearby Share", "Settings", false, () => OpenUri("ms-settings:crossdevice")));
        row1.Children.Add(Module(conn, 8));

        var rightCol = new Grid();
        rightCol.RowDefinitions.Add(new RowDefinition());
        rightCol.RowDefinitions.Add(new RowDefinition());
        var focus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        focus.Children.Add(Circle("moon", false, 28));
        focus.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Children = { T("Focus", 13, FontWeights.SemiBold) } });
        var focusMod = Module(focus, 10);
        focusMod.Cursor = Cursors.Hand;
        focusMod.MouseLeftButtonUp += (_, _) => { OpenUri("ms-settings:notifications"); Close(); };
        rightCol.Children.Add(focusMod);
        var small = new Grid();
        small.ColumnDefinitions.Add(new ColumnDefinition());
        small.ColumnDefinitions.Add(new ColumnDefinition());
        var dm = SmallTile("circle.lefthalf", "Dark Mode", Theme.IsDark, () =>
        {
            Settings.Current.Appearance = Theme.IsDark ? "light" : "dark";
            Settings.Save();
            Theme.Apply();
            Close();
        });
        small.Children.Add(dm);
        var tb = SmallTile("dock", "Taskbar", Takeover.TaskbarTemporarilyShown, () => { Takeover.ToggleTaskbar(); Close(); });
        Grid.SetColumn(tb, 1);
        small.Children.Add(tb);
        Grid.SetRow(small, 1);
        rightCol.Children.Add(small);
        Grid.SetColumn(rightCol, 1);
        row1.Children.Add(rightCol);
        root.Children.Add(row1);

        // display
        int? bright = Brightness.Get();
        if (bright != null)
        {
            var disp = new StackPanel();
            disp.Children.Add(T("Display", 13, FontWeights.SemiBold));
            var s = SliderRow("sun", bright.Value / 100.0, v => Brightness.Set((int)(v * 100)));
            disp.Children.Add(s);
            root.Children.Add(Module(disp));
        }

        // sound
        var snd = new StackPanel();
        var sndHeader = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Windows sound controls (devices and per-app volume)" };
        var more = new SymbolIcon { Symbol = "chevron.right", Width = 10, Height = 10, StrokeWidth = 2.6, VerticalAlignment = VerticalAlignment.Center };
        more.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        DockPanel.SetDock(more, Dock.Right);
        sndHeader.Children.Add(more);
        sndHeader.Children.Add(T("Sound", 13, FontWeights.SemiBold));
        sndHeader.MouseLeftButtonUp += (_, _) => { Close(); ShellHost.OpenWindowsSoundFlyout(); };
        snd.Children.Add(sndHeader);
        snd.Children.Add(SliderRow(AudioVolume.IsMuted() ? "speaker.slash" : "speaker", AudioVolume.Get() ?? 0.5, v => AudioVolume.Set(v), toggleMute: true));
        root.Children.Add(Module(snd));

        // now playing (media keys)
        var media = new Grid();
        media.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        media.ColumnDefinitions.Add(new ColumnDefinition());
        media.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var art = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(7), Background = new LinearGradientBrush(Theme.C("#FC5C7D"), Theme.C("#6A82FB"), 45) };
        art.Child = new SymbolIcon { Symbol = "music.note", Width = 20, Height = 20, Foreground = Brushes.White, StrokeWidth = 2 };
        media.Children.Add(art);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        info.Children.Add(T("Now Playing", 13, FontWeights.SemiBold));
        info.Children.Add(T("Media controls", 11, brush: "SecondaryLabelBrush"));
        Grid.SetColumn(info, 1);
        media.Children.Add(info);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(MediaButton("⏮", 0xB1));
        btns.Children.Add(MediaButton("⏯", 0xB3));
        btns.Children.Add(MediaButton("⏭", 0xB0));
        Grid.SetColumn(btns, 2);
        media.Children.Add(btns);
        root.Children.Add(Module(media));

        var edit = new TextBlock { Text = "Control Center Settings…", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2), Cursor = Cursors.Hand };
        edit.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        edit.MouseLeftButtonUp += (_, _) => { Close(); SettingsWindow.ShowPane("controlcenter"); };
        root.Children.Add(edit);

        Card.Child = root;
        PlaceUnder(anchor);
    }

    static string WifiName()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(800);
            var m = System.Text.RegularExpressions.Regex.Match(o, @"^\s*SSID\s*:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        catch { }
        return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? "Connected" : "Not Connected";
    }

    static Border Circle(string sym, bool on, double size = 28)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2) };
        if (on) b.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        else b.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0, 0, 0));
        var icon = new SymbolIcon { Symbol = sym, Width = size * 0.55, Height = size * 0.55, StrokeWidth = 2.1 };
        if (on) icon.Foreground = Brushes.White; else icon.SetResourceReference(SymbolIcon.ForegroundProperty, "LabelBrush");
        b.Child = icon;
        return b;
    }

    FrameworkElement Toggle(string sym, string title, string sub, bool on, Action click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        sp.Children.Add(Circle(sym, on));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(T(title, 13, FontWeights.SemiBold));
        var s = T(sub, 11, brush: "SecondaryLabelBrush");
        s.MaxWidth = 96;
        text.Children.Add(s);
        sp.Children.Add(text);
        sp.MouseLeftButtonUp += (_, _) => { click(); Close(); };
        return sp;
    }

    Border SmallTile(string sym, string title, bool on, Action click)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var c = Circle(sym, on, 26);
        c.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(c);
        var t = T(title, 10, brush: "LabelBrush");
        t.TextTrimming = TextTrimming.None;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        t.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(t);
        var m = Module(sp, 6);
        m.Cursor = Cursors.Hand;
        m.MouseLeftButtonUp += (_, _) => click();
        return m;
    }

    FrameworkElement SliderRow(string sym, double value, Action<double> set, bool toggleMute = false)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        var slider = new Slider { Style = (Style)Application.Current.Resources["CCSlider"], Minimum = 0, Maximum = 1, Value = value };
        slider.Resources["SliderTrackBrush"] = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0, 0, 0));
        var throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        throttle.Tick += (_, _) => { throttle.Stop(); set(slider.Value); };
        slider.ValueChanged += (_, _) => { if (!throttle.IsEnabled) throttle.Start(); };
        g.Children.Add(slider);
        var icon = new SymbolIcon { Symbol = sym, Width = 14, Height = 14, StrokeWidth = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73)) };
        if (toggleMute)
        {
            icon.IsHitTestVisible = true;
            icon.Cursor = Cursors.Hand;
            icon.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                bool m = !AudioVolume.IsMuted();
                AudioVolume.SetMute(m);
                icon.Symbol = m ? "speaker.slash" : "speaker";
            };
        }
        else icon.IsHitTestVisible = false;
        g.Children.Add(icon);
        return g;
    }

    static FrameworkElement MediaButton(string glyph, ushort vk)
    {
        var t = new TextBlock { Text = glyph, FontSize = 17, Margin = new Thickness(5, 0, 5, 0), Cursor = Cursors.Hand, FontFamily = new FontFamily("Segoe UI Symbol") };
        t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        t.MouseLeftButtonUp += (_, _) => SendKeys(vk);
        return t;
    }

    class BatteryPopover : Popover
    {
        public BatteryPopover(MenuBarWindow.StatusButton anchor) : base(260)
        {
            GetSystemPowerStatus(out var ps);
            var sp = new StackPanel { Margin = new Thickness(6) };
            var head = new Grid();
            head.Children.Add(T("Battery", 13, FontWeights.SemiBold));
            var pct = T(ps.BatteryLifePercent + "%", 13, brush: "SecondaryLabelBrush");
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(pct);
            sp.Children.Add(head);
            sp.Children.Add(T(ps.ACLineStatus == 1 ? "Power Source: Power Adapter" : "Power Source: Battery", 11, brush: "SecondaryLabelBrush"));
            if (ps.BatteryLifeTime > 0) sp.Children.Add(T($"{TimeSpan.FromSeconds(ps.BatteryLifeTime):h\\:mm} remaining", 11, brush: "SecondaryLabelBrush"));
            var sep = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 6) };
            sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
            sp.Children.Add(sep);
            var link = T("Battery Settings…", 13);
            link.Cursor = Cursors.Hand;
            link.MouseLeftButtonUp += (_, _) => { OpenUri("ms-settings:batterysaver"); Close(); };
            sp.Children.Add(link);
            Card.Child = sp;
            PlaceUnder(anchor);
            Loaded += (_, _) => Activate();
        }
    }
}

/// <summary>
/// The Sound menu-bar drop-down: volume, output device picker, and the way into Windows' own
/// (more advanced) sound controls — the Quick Settings volume mixer and Sound settings.
/// </summary>
public class SoundPopover : Popover
{
    static SoundPopover _instance;
    static DateTime _closedAt;
    readonly StackPanel _devices = new();

    public static void Toggle(MenuBarWindow.StatusButton anchor)
    {
        if (_instance != null) { _instance.Close(); return; }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;
        _instance = new SoundPopover(anchor);
        _instance.Show();
        _instance.Activate();
    }

    SoundPopover(MenuBarWindow.StatusButton anchor) : base(290)
    {
        Title = "Sound";
        Closed += (_, _) => { _instance = null; _closedAt = DateTime.Now; ShellHost.MenuBar?.UpdateSoundIcon(); };
        var root = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
        var title = T("Sound", 13, FontWeights.SemiBold);
        title.Margin = new Thickness(6, 2, 0, 8);
        root.Children.Add(title);

        var sliderRow = new Grid { Margin = new Thickness(4, 0, 4, 10) };
        var slider = new Slider { Style = (Style)Application.Current.Resources["CCSlider"], Minimum = 0, Maximum = 1, Value = AudioVolume.Get() ?? 0.5 };
        slider.Resources["SliderTrackBrush"] = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0, 0, 0));
        var throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        throttle.Tick += (_, _) => { throttle.Stop(); AudioVolume.Set(slider.Value); };
        slider.ValueChanged += (_, _) => { if (!throttle.IsEnabled) throttle.Start(); };
        sliderRow.Children.Add(slider);
        var spk = new SymbolIcon { Symbol = AudioVolume.IsMuted() ? "speaker.slash" : "speaker", Width = 14, Height = 14, StrokeWidth = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73)), Cursor = Cursors.Hand };
        spk.MouseLeftButtonDown += (_, e) => { e.Handled = true; bool m = !AudioVolume.IsMuted(); AudioVolume.SetMute(m); spk.Symbol = m ? "speaker.slash" : "speaker"; };
        sliderRow.Children.Add(spk);
        root.Children.Add(sliderRow);

        root.Children.Add(Separator());
        var outHdr = T("Output", 11, FontWeights.SemiBold, "SecondaryLabelBrush");
        outHdr.Margin = new Thickness(6, 6, 0, 4);
        root.Children.Add(outHdr);
        root.Children.Add(_devices);
        FillDevices();
        root.Children.Add(Separator());
        root.Children.Add(Link("Volume Mixer…", () => { Close(); ShellHost.OpenWindowsSoundFlyout(); }));
        root.Children.Add(Link("Sound Settings…", () => { Close(); OpenUri("ms-settings:sound"); }));
        Card.Child = root;
        PlaceUnder(anchor);
    }

    void FillDevices()
    {
        _devices.Children.Clear();
        var list = AudioVolume.OutputDevices();
        if (list.Count == 0) { var none = T("No output devices", 12, brush: "SecondaryLabelBrush"); none.Margin = new Thickness(8, 2, 0, 4); _devices.Children.Add(none); return; }
        foreach (var d in list)
        {
            var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 6, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13) };
            if (d.IsDefault) circle.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else circle.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0, 0, 0));
            bool headphones = d.Name.Contains("head", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("buds", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("airpods", StringComparison.OrdinalIgnoreCase);
            var glyph = new SymbolIcon { Symbol = headphones ? "music.note" : d.Name.Contains("display", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("hdmi", StringComparison.OrdinalIgnoreCase) ? "display" : "speaker", Width = 14, Height = 14, StrokeWidth = 2 };
            if (d.IsDefault) glyph.Foreground = Brushes.White; else glyph.SetResourceReference(SymbolIcon.ForegroundProperty, "LabelBrush");
            circle.Child = glyph;
            sp.Children.Add(circle);
            var name = T(d.Name, 13);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(9, 0, 0, 0);
            name.MaxWidth = 210;
            sp.Children.Add(name);
            row.Child = sp;
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            string id = d.Id;
            row.MouseLeftButtonUp += (_, _) => { AudioVolume.SetDefaultDevice(id); FillDevices(); };
            _devices.Children.Add(row);
        }
    }
}

/// <summary>Notification Center with widgets (calendar, clock, battery).</summary>
public class NotificationCenterWindow : Window
{
    static NotificationCenterWindow _instance;
    static DateTime _closedAt;
    readonly StackPanel _stack = new() { Margin = new Thickness(0, 8, 0, 8) };
    MenuBarWindow.StatusButton _anchor;
    bool _ncClosing;

    public static void Toggle(MenuBarWindow.StatusButton anchor)
    {
        if (_instance != null) { _instance.Close(); return; }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;
        _instance = new NotificationCenterWindow(anchor);
        _instance.Show();
        _instance.Activate();
    }

    NotificationCenterWindow(MenuBarWindow.StatusButton anchor)
    {
        _anchor = anchor;
        if (anchor != null) anchor.Active = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SetResourceReference(ForegroundProperty, "LabelBrush");
        Width = 360;
        Left = ShellHost.ScreenPx.Left / ShellHost.Scale + ShellHost.ScreenDip.Width - Width;
        Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.MenuBarHeight;
        Height = ShellHost.ScreenDip.Height - ShellHost.MenuBarHeight - (ShellHost.Dock?.ReservedHeight ?? 0);
        var scroll = new ScrollViewer { Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var tr = new TranslateTransform(Width, 0);
        scroll.RenderTransform = tr;
        Content = scroll;
        Build();
        Loaded += (_, _) => tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Closing += (_, _) => _ncClosing = true;
        Deactivated += (_, _) => { if (!_ncClosing) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_ncClosing) Close(); };
        Closed += (_, _) => { _instance = null; _closedAt = DateTime.Now; if (_anchor != null) _anchor.Active = false; };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW);
            WindowTracker.RegisterChrome(this);
        };
    }

    Border Widget(UIElement child, double height = double.NaN)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(20), Padding = new Thickness(16), Margin = new Thickness(16, 8, 16, 8), Child = child, Height = height,
            Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2E) : Color.FromArgb(0xE6, 0xF4, 0xF4, 0xF6)),
            BorderBrush = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.18 },
        };
        return b;
    }

    static TextBlock T(string s, double size, FontWeight? w = null, Brush brush = null, string res = "LabelBrush")
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = w ?? FontWeights.Normal };
        if (brush != null) t.Foreground = brush; else t.SetResourceReference(TextBlock.ForegroundProperty, res);
        return t;
    }

    void Build()
    {
        var today = DateTime.Today;
        // Notifications header
        var nh = T("No Notifications", 13, FontWeights.SemiBold, res: "SecondaryLabelBrush");
        nh.HorizontalAlignment = HorizontalAlignment.Center;
        nh.Margin = new Thickness(0, 6, 0, 6);
        nh.Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
        nh.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6 };
        _stack.Children.Add(nh);

        // Calendar + clock side by side (small widgets)
        var smalls = new Grid();
        smalls.ColumnDefinitions.Add(new ColumnDefinition());
        smalls.ColumnDefinitions.Add(new ColumnDefinition());
        var cal = new StackPanel();
        cal.Children.Add(T(today.ToString("dddd").ToUpperInvariant(), 11, FontWeights.SemiBold, new SolidColorBrush(Theme.C("#FF3B30"))));
        cal.Children.Add(T(today.Day.ToString(), 40, FontWeights.Light));
        cal.Children.Add(T("No events today", 11, res: "SecondaryLabelBrush"));
        var calW = Widget(cal, 150);
        calW.Margin = new Thickness(16, 8, 6, 8);
        smalls.Children.Add(calW);
        var clock = new AnalogClock { Width = 110, Height = 110, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var clockW = Widget(clock, 150);
        clockW.Margin = new Thickness(6, 8, 16, 8);
        clockW.Padding = new Thickness(8);
        Grid.SetColumn(clockW, 1);
        smalls.Children.Add(clockW);
        _stack.Children.Add(smalls);

        // month calendar (medium widget)
        var month = new StackPanel();
        month.Children.Add(T(today.ToString("MMMM").ToUpperInvariant(), 11, FontWeights.SemiBold, new SolidColorBrush(Theme.C("#FF3B30"))));
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        string[] dn = { "S", "M", "T", "W", "T", "F", "S" };
        for (int c = 0; c < 7; c++)
        {
            var d = T(dn[c], 10, FontWeights.SemiBold, res: "SecondaryLabelBrush");
            d.HorizontalAlignment = HorizontalAlignment.Center; d.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(d, c); grid.Children.Add(d);
        }
        var first = new DateTime(today.Year, today.Month, 1);
        int offset = (int)first.DayOfWeek;
        int days = DateTime.DaysInMonth(today.Year, today.Month);
        for (int day = 1; day <= days; day++)
        {
            int idx = offset + day - 1;
            int row = idx / 7 + 1, col = idx % 7;
            if (row > 6) break;
            bool isToday = day == today.Day;
            var cell = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Center };
            if (isToday) cell.Background = new SolidColorBrush(Theme.C("#FF3B30"));
            var t = T(day.ToString(), 11, isToday ? FontWeights.SemiBold : FontWeights.Normal, isToday ? Brushes.White : null);
            t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            cell.Child = t;
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }
        month.Children.Add(grid);
        _stack.Children.Add(Widget(month));

        // battery widget
        GetSystemPowerStatus(out var ps);
        if (ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100)
        {
            var bat = new DockPanel();
            var ring = new Grid { Width = 48, Height = 48 };
            ring.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x33, 0x34, 0xC7, 0x59)), StrokeThickness = 5 });
            ring.Children.Add(new Arc { Fraction = ps.BatteryLifePercent / 100.0, Stroke = new SolidColorBrush(Theme.C("#34C759")), Thickness = 5 });
            ring.Children.Add(new SymbolIcon { Symbol = "laptop", Width = 20, Height = 20, StrokeWidth = 1.8 });
            bat.Children.Add(ring);
            var bt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            bt.Children.Add(T(Environment.MachineName, 13, FontWeights.SemiBold));
            bt.Children.Add(T(ps.BatteryLifePercent + "%", 22, FontWeights.Light));
            bat.Children.Add(bt);
            _stack.Children.Add(Widget(bat));
        }

        var edit = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x60, 0x60)) };
        edit.Child = T("Edit Widgets", 12, brush: Brushes.White);
        edit.Cursor = Cursors.Hand;
        edit.MouseLeftButtonUp += (_, _) => { Close(); SettingsWindow.ShowPane("controlcenter"); };
        _stack.Children.Add(edit);
    }

    class AnalogClock : FrameworkElement
    {
        readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
        public AnalogClock() { _t.Tick += (_, _) => InvalidateVisual(); _t.Start(); Unloaded += (_, _) => _t.Stop(); }
        protected override void OnRender(DrawingContext dc)
        {
            double r = Math.Min(ActualWidth, ActualHeight) / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            bool dark = Theme.IsDark;
            dc.DrawEllipse(new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White), null, c, r, r);
            var ink = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                var ft = new FormattedText((i == 0 ? 12 : i).ToString(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), r * 0.2, ink, 1.5);
                dc.DrawText(ft, new Point(c.X + Math.Sin(a) * r * 0.78 - ft.Width / 2, c.Y - Math.Cos(a) * r * 0.78 - ft.Height / 2));
            }
            var now = DateTime.Now;
            void Hand(double angle, double len, double w, Brush b)
            {
                var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(p, c, new Point(c.X + Math.Sin(angle) * len, c.Y - Math.Cos(angle) * len));
            }
            Hand((now.Hour % 12 + now.Minute / 60.0) * Math.PI / 6, r * 0.45, 3.5, ink);
            Hand((now.Minute + now.Second / 60.0) * Math.PI / 30, r * 0.68, 2.5, ink);
            Hand(now.Second * Math.PI / 30, r * 0.75, 1, new SolidColorBrush(Theme.C("#FF9500")));
            dc.DrawEllipse(new SolidColorBrush(Theme.C("#FF9500")), null, c, 2.5, 2.5);
        }
    }

    class Arc : FrameworkElement
    {
        public double Fraction; public Brush Stroke; public double Thickness = 4;
        protected override void OnRender(DrawingContext dc)
        {
            double r = Math.Min(ActualWidth, ActualHeight) / 2 - Thickness / 2;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double a = Math.Clamp(Fraction, 0, 0.9999) * Math.PI * 2;
            var start = new Point(c.X, c.Y - r);
            var end = new Point(c.X + Math.Sin(a) * r, c.Y - Math.Cos(a) * r);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
    }
}
