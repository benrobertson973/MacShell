using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>
/// The menu bar timer's drop-down: one field. Type a length of time ("5:00" is 5 minutes) or a time of day with AM/PM
/// ("5:30pm") and press Return - the line under the field says which it understood - or click a quick timer (5, 10, 14,
/// 15 or 30 minutes). While it runs: the time left,
/// Cancel, +1 Min and Pause / Resume (ringing at a time of day: Cancel).
/// </summary>
public class TimerPopover : Popover
{
    static TimerPopover _instance;
    static DateTime _closedAt;
    public static bool IsOpen => _instance != null;

    public static void Toggle(MenuBarWindow.StatusButton anchor)
    {
        if (_instance != null) { _instance.Close(); return; }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;
        _instance = new TimerPopover(anchor);
        _instance.Show();
        _instance.Activate();
    }

    const double Inner = 242;   // the content's width
    readonly StackPanel _root = new() { Margin = new Thickness(4, 2, 4, 2) };
    CountdownTimer.State _built = (CountdownTimer.State)(-1);
    TextBlock _left, _sub, _hint;
    Border _bar;
    TextBox _field;
    bool _activated;

    TimerPopover(MenuBarWindow.StatusButton anchor) : base(270)
    {
        Title = "Timer";
        Card.Child = _root;
        Build();
        CountdownTimer.Changed += Refresh;
        Closed += (_, _) => { CountdownTimer.Changed -= Refresh; _instance = null; _closedAt = DateTime.Now; };
        Activated += (_, _) => { if (!_activated) { _activated = true; _field?.Focus(); } };
        PlaceUnder(anchor, alignRightEdge: false);
        // (from the item's left edge, like the menu of a macOS menu extra)
        if (anchor != null)
            Loaded += (_, _) =>
            {
                double screenRight = ShellHost.ScreenPx.Left / ShellHost.Scale + ShellHost.ScreenDip.Width;
                double itemLeft = anchor.PointToScreen(new Point(0, 0)).X / ShellHost.Scale;
                Left = Math.Min(itemLeft - 20 - 4, screenRight + 12 - ActualWidth);
            };
    }

    void Refresh()
    {
        if (CountdownTimer.Status != _built) { Build(); _field?.Focus(); return; }
        if (_left == null) return;
        _left.Text = CountdownTimer.Text;
        _sub.Text = Subtitle();
        double total = CountdownTimer.Duration.TotalSeconds;
        _bar.Width = total > 0 ? Math.Clamp(CountdownTimer.Remaining.TotalSeconds / total, 0, 1) * Inner : 0;
    }

    void Build()
    {
        _built = CountdownTimer.Status;
        _root.Children.Clear();
        _left = _sub = _hint = null;
        _bar = null;
        _field = null;
        var title = T("Timer", 13, FontWeights.SemiBold);
        title.Margin = new Thickness(2, 2, 0, 10);
        _root.Children.Add(title);
        switch (_built)
        {
            case CountdownTimer.State.Idle: BuildSetter(); break;
            case CountdownTimer.State.Ringing: BuildRinging(); break;
            default: BuildRunning(); break;
        }
    }

    // ------------------------------------------------------------------ setting it

    void BuildSetter()
    {
        _field = new TextBox
        {
            Style = (Style)Application.Current.Resources["MacTextField"],
            Text = Settings.Current.TimerText ?? "", MaxLength = 24, Height = 40, Padding = new Thickness(0),
            FontFamily = Theme.DisplayFont, FontSize = 26, FontWeight = FontWeights.Light, TextAlignment = TextAlignment.Center,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(_field, FontNumeralAlignment.Tabular);
        // (all selected when it gets focus: typing replaces it)
        _field.GotKeyboardFocus += (_, _) => _field.SelectAll();
        _field.PreviewMouseLeftButtonDown += (_, e) => { if (!_field.IsKeyboardFocusWithin) { e.Handled = true; _field.Focus(); } };
        _field.TextChanged += (_, _) => UpdateHint();
        _root.Children.Add(_field);

        _hint = T("", 12, brush: "SecondaryLabelBrush");
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.Margin = new Thickness(0, 7, 0, 0);
        _root.Children.Add(_hint);
        UpdateHint();

        // quick timers: start right away
        var presets = new UniformGrid { Columns = 5, Margin = new Thickness(-2, 12, -2, 0) };
        foreach (int minutes in new[] { 5, 10, 14, 15, 30 })
        {
            var b = new Button { Content = $"{minutes} min", Style = (Style)Application.Current.Resources["MacButton"], Height = 24, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(2), FontSize = 12 };
            b.Click += (_, _) => { CountdownTimer.Start(TimeSpan.FromMinutes(minutes)); Close(); };
            presets.Children.Add(b);
        }
        _root.Children.Add(presets);

        var start = new Button { Content = "Start", Style = (Style)Application.Current.Resources["MacDefaultButton"], Height = 28, MinWidth = 0, IsDefault = true, Margin = new Thickness(0, 14, 0, 2) };
        start.Click += (_, _) =>
        {
            if (!CountdownTimer.TryParse(_field.Text, DateTime.Now, out var length, out var at)) { _field.Focus(); _field.SelectAll(); return; }
            Settings.Current.TimerText = _field.Text.Trim();
            if (at is DateTime when) CountdownTimer.StartAt(when); else CountdownTimer.Start(length);
            Close();
        };
        _root.Children.Add(start);
    }

    void UpdateHint()
    {
        if (_field == null || _hint == null) return;
        if (string.IsNullOrWhiteSpace(_field.Text)) { _hint.Text = "Type 5:00 for 5 minutes, or a time like 5:30pm"; return; }
        if (!CountdownTimer.TryParse(_field.Text, DateTime.Now, out var length, out var at)) { _hint.Text = "Try 5:00 for 5 minutes, or 5:30pm"; return; }
        _hint.Text = at is DateTime when
            ? $"Rings {(when.Date == DateTime.Today ? "today" : "tomorrow")} at {Clock(when)} · in {DescribeUntil(when - DateTime.Now)}"
            : $"{Describe(length)} timer · ends at {Clock(DateTime.Now + length)}";
    }

    static string Clock(DateTime t) => t.ToString(Settings.Current.Clock24Hour ? "H:mm" : "h:mm tt");

    /// <summary>"1 hr 12 min", "25 min", "less than a minute" (whole minutes, rounded up).</summary>
    static string DescribeUntil(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return "less than a minute";
        long min = (long)Math.Ceiling(t.TotalMinutes - 0.001);
        return min >= 60 ? (min % 60 > 0 ? $"{min / 60} hr {min % 60} min" : $"{min / 60} hr") : $"{min} min";
    }

    // ------------------------------------------------------------------ running

    void BuildRunning()
    {
        _left = new TextBlock { Text = CountdownTimer.Text, FontFamily = Theme.DisplayFont, FontSize = 44, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
        _left.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(_left, FontNumeralAlignment.Tabular);
        _root.Children.Add(_left);

        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0) };
        track.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0, 0, 0));
        _bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        _bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Child = _bar;
        _root.Children.Add(track);

        _sub = T(Subtitle(), 12, brush: "SecondaryLabelBrush");
        _sub.HorizontalAlignment = HorizontalAlignment.Center;
        _sub.Margin = new Thickness(0, 8, 0, 0);
        _root.Children.Add(_sub);

        if (CountdownTimer.IsAlarm)
        {
            var cancel = Btn("Cancel", false, CountdownTimer.Cancel);
            cancel.Margin = new Thickness(0, 14, 0, 2);
            _root.Children.Add(cancel);
        }
        else
        {
            var buttons = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 14, -3, 2) };
            buttons.Children.Add(Btn("Cancel", false, CountdownTimer.Cancel));
            buttons.Children.Add(Btn("+1 Min", false, CountdownTimer.AddMinute));
            bool paused = _built == CountdownTimer.State.Paused;
            buttons.Children.Add(Btn(paused ? "Resume" : "Pause", true, paused ? (Action)CountdownTimer.Resume : CountdownTimer.Pause));
            _root.Children.Add(buttons);
        }
        Refresh();
    }

    static string Subtitle()
    {
        if (CountdownTimer.Status == CountdownTimer.State.Paused) return "Paused";
        if (CountdownTimer.RingsAt is DateTime at) return $"Rings {(at.Date == DateTime.Today ? "" : "tomorrow ")}at {Clock(at)}";
        return "Ends at " + Clock(DateTime.Now + CountdownTimer.Remaining);
    }

    void BuildRinging()
    {
        var t = new TextBlock
        {
            Text = CountdownTimer.RingsAt is DateTime at ? $"It’s {Clock(at)}" : "Time’s up",
            FontFamily = Theme.DisplayFont, FontSize = 30, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        _root.Children.Add(t);
        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 14, -3, 2) };
        buttons.Children.Add(CountdownTimer.IsAlarm ? Btn("Snooze", false, CountdownTimer.Snooze) : Btn("Repeat", false, CountdownTimer.Repeat));
        var stop = Btn("Stop", true, CountdownTimer.Cancel);
        stop.IsDefault = true;
        buttons.Children.Add(stop);
        _root.Children.Add(buttons);
    }

    static Button Btn(string text, bool primary, Action click)
    {
        var b = new Button
        {
            Content = text, Style = (Style)Application.Current.Resources[primary ? "MacDefaultButton" : "MacButton"],
            Height = 28, MinWidth = 0, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(3, 0, 3, 0),
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>"5 min", "1 hr 30 min", "45 sec" …</summary>
    public static string Describe(TimeSpan t)
    {
        long s = (long)Math.Round(t.TotalSeconds);
        var parts = new List<string>();
        if (s >= 3600) parts.Add($"{s / 3600} hr");
        if (s / 60 % 60 > 0) parts.Add($"{s / 60 % 60} min");
        if (s % 60 > 0) parts.Add($"{s % 60} sec");
        return parts.Count > 0 ? string.Join(" ", parts) : "0 sec";
    }

    public static string ClockText(DateTime t) => Clock(t);
}

/// <summary>
/// "Time's up": a banner at the top right like a macOS notification, while the timer's alarm rings - Stop, and Repeat
/// (a length of time) or Snooze (a time of day). It doesn't take the keyboard from the app you're in.
/// </summary>
public class TimerBanner : Window
{
    static TimerBanner _shown;
    static CountdownTimer.State _last;

    /// <summary>Shows the banner when the timer starts ringing and takes it away when it stops.</summary>
    public static void Install()
    {
        CountdownTimer.Changed += () =>
        {
            var s = CountdownTimer.Status;
            if (s == _last) return;
            _last = s;
            _shown?.Close();
            if (s == CountdownTimer.State.Ringing) { _shown = new TimerBanner(); _shown.Show(); }
        };
    }

    TimerBanner()
    {
        Title = "Timer Banner";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 344 + 40;
        FontFamily = Theme.Font;
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetResourceReference(ForegroundProperty, "LabelBrush");

        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5), Padding = new Thickness(12, 11, 12, 11), Margin = new Thickness(20, 6, 20, 28) };
        card.SetResourceReference(Border.BackgroundProperty, "PopoverBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        card.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.3 };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Theme.C("#FFB340"), Theme.C("#FF8A00"), 90),
            Child = new SymbolIcon { Symbol = "clock", Width = 26, Height = 26, StrokeWidth = 2.2, Foreground = Brushes.White },
        };
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 10, 0) };
        var head = new TextBlock { Text = "Timer", FontWeight = FontWeights.SemiBold };
        head.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        text.Children.Add(head);
        string message = CountdownTimer.RingsAt is DateTime at
            ? $"It’s {TimerPopover.ClockText(at)}."
            : $"Time’s up! Your {TimerPopover.Describe(CountdownTimer.Duration)} timer is done.";
        var body = new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        text.Children.Add(body);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var stop = new Button { Content = "Stop", Style = (Style)Application.Current.Resources["MacDefaultButton"], Height = 24, MinWidth = 76 };
        stop.Click += (_, _) => CountdownTimer.Cancel();
        var again = new Button { Content = CountdownTimer.IsAlarm ? "Snooze" : "Repeat", Style = (Style)Application.Current.Resources["MacButton"], Height = 24, MinWidth = 76, Margin = new Thickness(0, 6, 0, 0) };
        again.Click += (_, _) => { if (CountdownTimer.IsAlarm) CountdownTimer.Snooze(); else CountdownTimer.Repeat(); };
        buttons.Children.Add(stop);
        buttons.Children.Add(again);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        card.Child = grid;
        Content = card;

        // slides in from the right edge, under the menu bar
        var slide = new TranslateTransform(380, 0);
        card.RenderTransform = slide;
        Loaded += (_, _) =>
        {
            double screenRight = ShellHost.ScreenPx.Left / ShellHost.Scale + ShellHost.ScreenDip.Width;
            Left = screenRight + 10 - ActualWidth;
            Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.MenuBarHeight + 2;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        Closed += (_, _) => { if (_shown == this) _shown = null; };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            WindowTracker.RegisterChrome(this);
        };
    }
}
