using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Shell;

/// <summary>
/// The Wi-Fi menu (the menu bar's Wi-Fi icon, Control Center's Wi-Fi tile), as macOS Sonoma has it: Wi-Fi and its
/// switch; the known networks around (the one joined in a blue circle); Other Networks, folded away under its chevron
/// (joined with a click - the password asked for when there is one); Other… for a hidden network; Wi-Fi Settings….
/// </summary>
public class WifiPopover : Popover
{
    static WifiPopover _instance;
    static DateTime _closedAt;
    bool _othersOpen, _othersOpenSet;
    readonly StackPanel _known = new(), _others = new();
    readonly CheckBox _switch;
    readonly FrameworkElement _othersHeader;
    readonly SymbolIcon _othersChevron = new() { Symbol = "chevron.right", Width = 10, Height = 10, StrokeWidth = 2.6, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = T("", 11, brush: "SecondaryLabelBrush");
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(4) };
    string _joining;

    public static void Toggle(MenuBarWindow.StatusButton anchor)
    {
        if (_instance != null) { _instance.Close(); return; }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;
        _instance = new WifiPopover(anchor);
        if (ShellHost.Offscreen) { _instance.ShowActivated = false; _instance.Left = -20000; }
        _instance.Show();
        if (!ShellHost.Offscreen) _instance.Activate();
    }

    /// <summary>Tests: the menu open now.</summary>
    public static WifiPopover Current => _instance;

    /// <summary>Tests: Other Networks unfolded.</summary>
    public void TestExpand() { _othersOpen = true; _othersOpenSet = true; ShowOthers(); }

    WifiPopover(MenuBarWindow.StatusButton anchor) : base(300)
    {
        Title = "Wi-Fi";
        Closed += (_, _) => { _instance = null; _closedAt = DateTime.Now; _refresh.Stop(); };
        var root = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };

        var head = new Grid { Margin = new Thickness(6, 2, 2, 6) };
        head.Children.Add(T("Wi-Fi", 13, FontWeights.SemiBold));
        _switch = new CheckBox { Style = (Style)Application.Current.Resources["MacSwitch"], HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _switch.Click += (_, _) =>
        {
            bool on = _switch.IsChecked == true;
            SetStatus(on ? "Turning Wi-Fi on…" : "");
            Task.Run(() => Radios.Set(Radios.Kind.WiFi, on)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (on) Task.Run(Wlan.Scan);
                // (the adapter takes a few seconds to find the networks around)
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(on ? 3 : 0.3) };
                t.Tick += (_, _) => { t.Stop(); SetStatus(""); Fill(); ShellHost.MenuBar?.RefreshNetwork(); };
                t.Start();
            }));
        };
        head.Children.Add(_switch);
        root.Children.Add(head);
        _status.Margin = new Thickness(8, 0, 0, 4);
        _status.Visibility = Visibility.Collapsed;
        root.Children.Add(_status);
        root.Children.Add(_known);

        // Other Networks, folded under its chevron
        var oh = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 0) };
        _othersChevron.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        DockPanel.SetDock(_othersChevron, Dock.Right);
        oh.Children.Add(_othersChevron);
        oh.Children.Add(T("Other Networks", 11, FontWeights.SemiBold, "SecondaryLabelBrush"));
        var ohRow = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = oh };
        ohRow.MouseEnter += (_, _) => ohRow.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        ohRow.MouseLeave += (_, _) => ohRow.Background = Brushes.Transparent;
        ohRow.MouseLeftButtonUp += (_, _) => { _othersOpen = !_othersOpen; ShowOthers(); };
        _othersHeader = ohRow;
        root.Children.Add(Separator());
        root.Children.Add(ohRow);
        root.Children.Add(_others);
        root.Children.Add(Separator());
        root.Children.Add(Link("Wi-Fi Settings…", () => { Close(); SettingsWindow.ShowPane("wifi"); }));
        Card.Child = root;
        PlaceUnder(anchor);
        Fill();
        Loaded += (_, _) => Task.Run(Wlan.Scan);
        _refresh.Tick += (_, _) => Fill();
        _refresh.Start();
    }

    void ShowOthers()
    {
        _others.Visibility = _othersOpen ? Visibility.Visible : Visibility.Collapsed;
        _othersChevron.Symbol = _othersOpen ? "chevron.down" : "chevron.right";
    }

    /// <summary>The lists, read on another thread (the adapter can take a moment) and shown when ready.</summary>
    void Fill()
    {
        Task.Run(() => (radio: Wlan.RadioOn(), nets: Wlan.Networks(), adapter: Wlan.Primary != null)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.BeginInvoke(() => Show(t.Result.radio, t.Result.nets, t.Result.adapter));
        });
    }

    void SetStatus(string s)
    {
        _status.Text = s;
        _status.Visibility = string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    }

    void Show(bool? radio, List<Wlan.Network> nets, bool adapter)
    {
        _known.Children.Clear();
        _others.Children.Clear();
        bool turning = _status.Text.StartsWith("Turning");
        if (!turning) _switch.IsChecked = radio == true;
        _switch.IsEnabled = adapter;
        if (!adapter) { SetStatus("No Wi-Fi hardware installed"); _othersHeader.Visibility = Visibility.Collapsed; _others.Visibility = Visibility.Collapsed; return; }
        if (radio != true) { if (!turning) SetStatus(""); _othersHeader.Visibility = Visibility.Collapsed; _others.Visibility = Visibility.Collapsed; return; }
        if (!turning) SetStatus("");
        _othersHeader.Visibility = Visibility.Visible;
        var known = nets.Where(n => n.Known || n.Connected).ToList();
        if (known.Count > 0)
        {
            var h = T(known.Count == 1 ? "Known Network" : "Known Networks", 11, FontWeights.SemiBold, "SecondaryLabelBrush");
            h.Margin = new Thickness(8, 4, 0, 2);
            _known.Children.Add(h);
            foreach (var n in known) _known.Children.Add(NetworkRow(n));
        }
        // (a network not known yet, folded away when one is joined - open while none is)
        if (!known.Any(n => n.Connected) && !_othersOpenSet) { _othersOpen = true; _othersOpenSet = true; }
        foreach (var n in nets.Where(n => !n.Known && !n.Connected)) _others.Children.Add(NetworkRow(n));
        _others.Children.Add(Link("Other…", () => { Close(); WifiJoinDialog.ShowHidden(); }));
        ShowOthers();
    }

    FrameworkElement NetworkRow(Wlan.Network n)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13) };
        if (n.Connected) circle.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        else circle.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0, 0, 0));
        var glyph = new WifiGlyph { Width = 15, Height = 12, Level = WifiGlyph.LevelFor(n.Signal) };
        if (n.Connected) glyph.Foreground = Brushes.White; else glyph.SetResourceReference(WifiGlyph.ForegroundProperty, "LabelBrush");
        circle.Child = glyph;
        g.Children.Add(circle);
        var name = T(n.Ssid, 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(9, 0, 6, 0);
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        if (_joining == n.Ssid)
        {
            var sp = T("Joining…", 11, brush: "SecondaryLabelBrush");
            sp.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sp, 2);
            g.Children.Add(sp);
        }
        else if (n.Secure)
        {
            var lockIcon = new SymbolIcon { Symbol = "lock", Width = 11, Height = 11, StrokeWidth = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            lockIcon.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
            Grid.SetColumn(lockIcon, 2);
            g.Children.Add(lockIcon);
        }
        var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 8, 4), Background = Brushes.Transparent, Child = g, ToolTip = $"{n.Security}{(n.Band != null ? " · " + n.Band : "")}" };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => Choose(n);
        return row;
    }

    void Choose(Wlan.Network n)
    {
        if (n.Connected) { Close(); SettingsWindow.ShowPane("wifi"); return; }
        if (n.Known && !string.IsNullOrEmpty(n.Profile))
        {
            _joining = n.Ssid;
            Fill();
            Task.Run(() => Wlan.Join(n.Ssid, n.Auth, n.Cipher, null, true, false, n.Profile)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                _joining = null;
                if (t.Result != null) ShellHost.ShowAlert($"The Wi-Fi network “{n.Ssid}” couldn’t be joined.", t.Result);
                if (IsLoaded) Fill();
                ShellHost.MenuBar?.RefreshNetwork();
            }));
            return;
        }
        Close();
        WifiJoinDialog.Join(n);
    }
}

/// <summary>The Wi-Fi fan: a dot and two arcs, those beyond the signal's strength dimmed (as the Mac draws it).</summary>
public class WifiGlyph : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(WifiGlyph),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    int _level = 3;
    /// <summary>1-3 (the dot, then each arc).</summary>
    public int Level { get => _level; set { _level = Math.Clamp(value, 0, 3); InvalidateVisual(); } }

    public static int LevelFor(int signal) => signal >= 66 ? 3 : signal >= 33 ? 2 : 1;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        // the fan as wedges around the bottom point (cx, by)
        double cx = w / 2, by = h * 0.98, r = Math.Min(w / 2 / Math.Sin(Math.PI / 4), h) * 0.98;
        var fg = Foreground;
        var dim = fg.Clone(); dim.Opacity = 0.28; dim.Freeze();
        double[] radii = { r * 0.36, r * 0.68, r };
        double band = r * 0.17;
        for (int i = 0; i < 3; i++)
        {
            var brush = i < _level ? fg : dim;
            double ro = radii[i], ri = i == 0 ? 0 : radii[i] - band;
            var g = Wedge(cx, by, ri, ro, 45);
            dc.DrawGeometry(brush, null, g);
        }
    }

    static Geometry Wedge(double cx, double cy, double ri, double ro, double halfAngle)
    {
        double a = halfAngle * Math.PI / 180;
        Point P(double rad, double sign) => new(cx + sign * rad * Math.Sin(a), cy - rad * Math.Cos(a));
        var f = new PathFigure { StartPoint = P(ri, -1), IsClosed = true, IsFilled = true };
        f.Segments.Add(new LineSegment(P(ro, -1), true));
        f.Segments.Add(new ArcSegment(P(ro, 1), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true));
        if (ri > 0)
        {
            f.Segments.Add(new LineSegment(P(ri, 1), true));
            f.Segments.Add(new ArcSegment(P(ri, -1), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true));
        }
        var g = new PathGeometry(new[] { f });
        g.Freeze();
        return g;
    }
}

/// <summary>
/// macOS's Wi-Fi password window ("The Wi-Fi network “…” requires a WPA2 password.") and its "Find and join a
/// Wi-Fi network." for one that hides its name: the password (Show password), Remember this network; Join waits for
/// the network and says if it didn't work.
/// </summary>
public class WifiJoinDialog : Window
{
    static WifiJoinDialog _open;
    readonly TextBox _name = new();
    readonly PasswordBox _password = new();
    readonly TextBox _passwordShown = new();
    readonly CheckBox _show, _remember;
    readonly TextBlock _message;
    readonly Button _join;
    readonly FrameworkElement _busy;
    readonly Wlan.Network _network;   // null: a hidden network
    int _auth = 7;

    static readonly (string name, int auth)[] SecurityChoices =
    {
        ("None", 1), ("WPA/WPA2 Personal", 7), ("WPA2/WPA3 Personal", 9), ("WPA3 Personal", 9), ("Enhanced Open", 10),
    };

    /// <summary>A network around that isn't known: open ones are joined at once; a password is asked for.</summary>
    public static void Join(Wlan.Network n)
    {
        if (!Wlan.CanJoinHere(n.Auth))
        {
            // (WEP, Enterprise: Windows' own list asks for what those need)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-availablenetworks:") { UseShellExecute = true }); } catch { }
            return;
        }
        if (!Wlan.NeedsPassword(n.Auth))
        {
            Task.Run(() => Wlan.Join(n.Ssid, n.Auth, n.Cipher, null, true, false, null)).ContinueWith(t => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (t.Result != null) ShellHost.ShowAlert($"The Wi-Fi network “{n.Ssid}” couldn’t be joined.", t.Result);
                ShellHost.MenuBar?.RefreshNetwork();
            }));
            return;
        }
        Open(new WifiJoinDialog(n));
    }

    /// <summary>Other…: a network that hides its name.</summary>
    public static void ShowHidden() => Open(new WifiJoinDialog(null));

    static void Open(WifiJoinDialog d)
    {
        _open?.Close();
        _open = d;
        d.Closed += (_, _) => { if (_open == d) _open = null; };
        if (ShellHost.Offscreen) { d.ShowActivated = false; d.Left = -20000; d.Show(); return; }
        d.Show();
        NativeMethods.ActivateWindow(new System.Windows.Interop.WindowInteropHelper(d).Handle);
        d.Dispatcher.BeginInvoke(() => { if (d._network == null) d._name.Focus(); else d._password.Focus(); }, DispatcherPriority.Input);
    }

    /// <summary>Tests: the window open now.</summary>
    public static WifiJoinDialog Current => _open;

    /// <summary>Tests: the password window for a made-up network (nothing is joined).</summary>
    public static void ShowForTest(string ssid) => Open(new WifiJoinDialog(new Wlan.Network(ssid, "", 80, true, "WPA2 Personal", 7, false, false, "5 GHz")));

    WifiJoinDialog(Wlan.Network n)
    {
        _network = n;
        if (n != null) _auth = n.Auth;
        Title = "Wi-Fi";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Theme.Font; FontSize = 13;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - 460) / 2; Top = wa.Top + wa.Height * 0.18;

        var card = new Border { Width = 420, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0.5), Padding = new Thickness(20, 18, 20, 16), Margin = new Thickness(20, 10, 20, 30) };
        card.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        card.Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.35 };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(new Image { Source = MacIcons.Tile("wifi", "#3D9BFF", "#0A6CFF"), Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) });
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);
        string kind = n != null ? KindName(n) : null;
        right.Children.Add(Label(n != null ? $"The Wi-Fi network “{n.Ssid}” requires a {kind} password." : "Find and join a Wi-Fi network.", 13, FontWeights.Bold));
        if (n == null) right.Children.Add(Label("Enter the name and security type of the network you want to join.", 11, null, "SecondaryLabelBrush", 4));

        var form = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void AddRow(string label, UIElement control)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Label(label, 13);
            l.HorizontalAlignment = HorizontalAlignment.Right;
            l.VerticalAlignment = VerticalAlignment.Center;
            l.Margin = new Thickness(0, 0, 8, 8);
            Grid.SetRow(l, row);
            form.Children.Add(l);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
            row++;
        }
        if (n == null)
        {
            _name.Style = (Style)Application.Current.Resources["MacTextField"];
            _name.Height = 24;
            _name.TextChanged += (_, _) => Validate();
            AddRow("Network Name:", _name);
            AddRow("Security:", SecurityPopUp());
        }
        var pwGrid = new Grid { Height = 24 };
        _password.Style = (Style)Application.Current.Resources["MacPasswordField"];
        _password.PasswordChanged += (_, _) => { if (_show.IsChecked != true) _passwordShown.Text = _password.Password; Validate(); };
        _passwordShown.Style = (Style)Application.Current.Resources["MacTextField"];
        _passwordShown.Visibility = Visibility.Collapsed;
        _passwordShown.TextChanged += (_, _) => { if (_show.IsChecked == true) _password.Password = _passwordShown.Text; Validate(); };
        pwGrid.Children.Add(_password);
        pwGrid.Children.Add(_passwordShown);
        AddRow("Password:", pwGrid);
        _show = MacCheck("Show password", false);
        _show.Click += (_, _) =>
        {
            bool s = _show.IsChecked == true;
            if (s) _passwordShown.Text = _password.Password; else _password.Password = _passwordShown.Text;
            _passwordShown.Visibility = s ? Visibility.Visible : Visibility.Collapsed;
            _password.Visibility = s ? Visibility.Collapsed : Visibility.Visible;
        };
        AddRow("", _show);
        _remember = MacCheck("Remember this network", true);
        AddRow("", _remember);
        right.Children.Add(form);
        _message = Label("", 11, null, "SystemRedBrush", 2);
        _message.Visibility = Visibility.Collapsed;
        right.Children.Add(_message);

        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var spin = new TextBlock { Text = "Joining…", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        spin.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        _busy = spin;
        buttons.Children.Add(spin);
        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.Resources["MacButton"], MinWidth = 76, Height = 24, IsCancel = true };
        cancel.Click += (_, _) => Close();
        _join = new Button { Content = "Join", Style = (Style)Application.Current.Resources["MacDefaultButton"], MinWidth = 76, Height = 24, IsDefault = true, Margin = new Thickness(10, 0, 0, 0) };
        _join.Click += (_, _) => DoJoin();
        bp.Children.Add(cancel);
        bp.Children.Add(_join);
        buttons.Children.Add(bp);
        right.Children.Add(buttons);
        card.Child = layout;
        Content = card;
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border or Grid or StackPanel) try { DragMove(); } catch { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Validate();
    }

    static string KindName(Wlan.Network n) => n.Auth switch { 9 => "WPA3", 7 => "WPA2", 4 => "WPA", _ => n.Security };

    static TextBlock Label(string s, double size, FontWeight? w = null, string brush = "LabelBrush", double top = 0)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static CheckBox MacCheck(string text, bool on) => new() { Style = (Style)Application.Current.Resources["MacCheckBox"], Content = text, IsChecked = on };

    FrameworkElement SecurityPopUp()
    {
        var text = new TextBlock { Text = "WPA/WPA2 Personal", VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.updown", Width = 10, Height = 10, StrokeWidth = 2.4, Margin = new Thickness(8, 0, 0, 0) });
        var b = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = sp, Padding = new Thickness(9, 0, 7, 0), HorizontalAlignment = HorizontalAlignment.Left, Height = 24 };
        b.Click += (_, _) =>
        {
            var items = SecurityChoices.Select(c => (object)Mb.Item(c.name, () => { text.Text = c.name; _auth = c.auth; Validate(); }, isChecked: text.Text == c.name)).ToArray();
            var cm = Mb.Context(items);
            cm.PlacementTarget = b;
            cm.IsOpen = true;
        };
        return b;
    }

    string Password => _show.IsChecked == true ? _passwordShown.Text : _password.Password;

    void Validate()
    {
        if (_join == null) return;
        bool nameOk = _network != null || _name.Text.Trim().Length > 0;
        bool pwOk = !Wlan.NeedsPassword(_auth) || Password.Length >= 8 && Password.Length <= 63 || Password.Length == 64;
        _join.IsEnabled = nameOk && pwOk;
        bool needsPw = Wlan.NeedsPassword(_auth);
        _password.IsEnabled = _passwordShown.IsEnabled = needsPw;
    }

    void DoJoin()
    {
        if (!_join.IsEnabled) return;
        string ssid = _network?.Ssid ?? _name.Text.Trim();
        int auth = _auth, cipher = _network?.Cipher ?? 4;
        string pw = Wlan.NeedsPassword(auth) ? Password : null;
        bool remember = _remember.IsChecked == true, hidden = _network == null;
        _join.IsEnabled = false;
        _busy.Visibility = Visibility.Visible;
        _message.Visibility = Visibility.Collapsed;
        Task.Run(() => Wlan.Join(ssid, auth, cipher, pw, remember, hidden, null)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _busy.Visibility = Visibility.Collapsed;
            ShellHost.MenuBar?.RefreshNetwork();
            if (t.Result == null) { Close(); return; }
            _message.Text = $"The Wi-Fi network “{ssid}” couldn’t be joined. {t.Result}";
            _message.Visibility = Visibility.Visible;
            _join.IsEnabled = true;
            Shake();
        }));
    }

    /// <summary>The window shakes, as a Mac's does for a wrong password.</summary>
    void Shake()
    {
        double x = Left;
        var steps = new[] { -10.0, 10, -8, 8, -4, 4, 0 };
        int i = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        t.Tick += (_, _) => { if (i >= steps.Length) { t.Stop(); Left = x; return; } Left = x + steps[i++]; };
        t.Start();
    }
}
