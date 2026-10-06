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
/// The menu bar timer's drop-down, with two tabs. Timer: hours / minutes / seconds (type, arrow keys or the scroll
/// wheel) or a preset. Alarm: a time of day to ring at ("4:30 PM", "4pm"). Return starts either. While it runs: the
/// time left, Cancel, +1 Min and Pause / Resume (an alarm: Cancel).
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

    const double Inner = 222;   // the content's width
    readonly StackPanel _root = new() { Margin = new Thickness(4, 2, 4, 2) };
    CountdownTimer.State _built = (CountdownTimer.State)(-1);
    TextBlock _left, _sub, _atHint;
    Border _bar;
    TextBox _h, _m, _s, _at;
    bool _activated;

    TimerPopover(MenuBarWindow.StatusButton anchor) : base(250)
    {
        Title = "Timer";
        Card.Child = _root;
        Build();
        CountdownTimer.Changed += Refresh;
        Closed += (_, _) => { CountdownTimer.Changed -= Refresh; _instance = null; _closedAt = DateTime.Now; };
        Activated += (_, _) => { if (!_activated) { _activated = true; FocusField(); } };
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

    /// <summary>The field to type in, ready to type over: the minutes, or the alarm's time.</summary>
    void FocusField() => (_at ?? _m)?.Focus();

    bool AlarmMode => Settings.Current.TimerMode == "alarm";

    void Refresh()
    {
        if (CountdownTimer.Status != _built) { Build(); FocusField(); return; }
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
        _left = _sub = _atHint = null;
        _bar = null;
        _h = _m = _s = _at = null;
        if (_built == CountdownTimer.State.Idle)
        {
            _root.Children.Add(Tabs());
            if (AlarmMode) BuildAlarmSetter(); else BuildSetter();
            return;
        }
        var title = T(CountdownTimer.IsAlarm ? "Alarm" : "Timer", 13, FontWeights.SemiBold);
        title.Margin = new Thickness(2, 2, 0, 10);
        _root.Children.Add(title);
        if (_built == CountdownTimer.State.Ringing) BuildRinging(); else BuildRunning();
    }

    /// <summary>Timer | Alarm, a macOS segmented control.</summary>
    FrameworkElement Tabs()
    {
        var bg = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Margin = new Thickness(0, 2, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
        bg.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, mode) in new[] { ("Timer", "timer"), ("Alarm", "alarm") })
        {
            bool on = (Settings.Current.TimerMode ?? "timer") == mode;
            var seg = new Border { Width = 82, Height = 22, CornerRadius = new CornerRadius(5.5), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            if (on)
            {
                seg.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x5C, 0x8E, 0x8E, 0x93) : Colors.White);
                seg.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0.6, Direction = 270, Opacity = 0.18 };
            }
            var t = T(label, 12, on ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            seg.Child = t;
            string m = mode;
            seg.MouseLeftButtonUp += (_, _) =>
            {
                if (Settings.Current.TimerMode == m) return;
                Settings.Current.TimerMode = m;
                Settings.Save(notify: false);
                Build();
                FocusField();
            };
            row.Children.Add(seg);
        }
        bg.Child = row;
        return bg;
    }

    // ------------------------------------------------------------------ a length of time

    void BuildSetter()
    {
        long total = (long)Math.Round(Settings.Current.TimerSeconds);
        if (total < 1) total = 300;
        var fields = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        fields.Children.Add(Field(total / 3600, "hr", out _h));
        fields.Children.Add(Colon());
        fields.Children.Add(Field(total / 60 % 60, "min", out _m));
        fields.Children.Add(Colon());
        fields.Children.Add(Field(total % 60, "sec", out _s));
        _root.Children.Add(fields);

        var presets = new UniformGrid { Columns = 4, Margin = new Thickness(-2.5, 12, -2.5, 0) };
        foreach (var (label, minutes) in new[] { ("1 min", 1), ("3 min", 3), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("45 min", 45), ("1 hr", 60) })
        {
            var b = new Button { Content = label, Style = (Style)Application.Current.Resources["MacButton"], Height = 24, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(2.5), FontSize = 12 };
            b.Click += (_, _) => { CountdownTimer.Start(TimeSpan.FromMinutes(minutes)); Close(); };
            presets.Children.Add(b);
        }
        _root.Children.Add(presets);
        _root.Children.Add(SoundRow());

        var start = StartButton("Start");
        start.Click += (_, _) =>
        {
            var length = new TimeSpan(Value(_h), Value(_m), Value(_s));   // (90 minutes is fine: 1:30:00)
            if (length.TotalSeconds < 1) { _m.Focus(); return; }
            CountdownTimer.Start(length);
            Close();
        };
        _root.Children.Add(start);
    }

    FrameworkElement Field(long value, string caption, out TextBox box)
    {
        var tb = BigField(value.ToString("00"), 54, 26);
        tb.MaxLength = 2;
        tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(tb, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string s || !s.Trim().All(char.IsDigit)) e.CancelCommand();
        });
        tb.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down) { Step(tb, e.Key == Key.Up ? 1 : -1); e.Handled = true; } };
        tb.MouseWheel += (_, e) => { Step(tb, e.Delta > 0 ? 1 : -1); e.Handled = true; };
        tb.LostKeyboardFocus += (_, _) => tb.Text = Value(tb).ToString("00");
        var cap = T(caption, 11, brush: "SecondaryLabelBrush");
        cap.HorizontalAlignment = HorizontalAlignment.Center;
        cap.Margin = new Thickness(0, 4, 0, 0);
        var sp = new StackPanel();
        sp.Children.Add(tb);
        sp.Children.Add(cap);
        box = tb;
        return sp;
    }

    /// <summary>A large text field whose text is all selected when it gets focus, so typing replaces it.</summary>
    static TextBox BigField(string text, double width, double size)
    {
        var tb = new TextBox
        {
            Style = (Style)Application.Current.Resources["MacTextField"],
            Text = text, Width = width, Height = 40, Padding = new Thickness(0),
            FontFamily = Theme.DisplayFont, FontSize = size, FontWeight = FontWeights.Light, TextAlignment = TextAlignment.Center,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        tb.GotKeyboardFocus += (_, _) => tb.SelectAll();
        tb.PreviewMouseLeftButtonDown += (_, e) => { if (!tb.IsKeyboardFocusWithin) { e.Handled = true; tb.Focus(); } };
        return tb;
    }

    static FrameworkElement Colon()
    {
        var t = T(":", 24, FontWeights.Light);
        t.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Height = 40, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 0, 4, 0), Children = { t } };
    }

    static int Value(TextBox tb) => int.TryParse(tb.Text, out int v) ? Math.Clamp(v, 0, 99) : 0;

    static void Step(TextBox tb, int by)
    {
        tb.Text = Math.Clamp(Value(tb) + by, 0, 99).ToString("00");
        tb.SelectAll();
    }

    // ------------------------------------------------------------------ a time of day (an alarm)

    void BuildAlarmSetter()
    {
        _at = BigField(Settings.Current.TimerAlarmText ?? "", Inner, 24);
        _at.MaxLength = 12;
        _at.TextChanged += (_, _) => UpdateAtHint();
        _root.Children.Add(_at);
        _atHint = T("", 12, brush: "SecondaryLabelBrush");
        _atHint.HorizontalAlignment = HorizontalAlignment.Center;
        _atHint.Margin = new Thickness(0, 7, 0, 0);
        _root.Children.Add(_atHint);
        UpdateAtHint();
        _root.Children.Add(SoundRow());

        var start = StartButton("Set Alarm");
        start.Click += (_, _) =>
        {
            if (!CountdownTimer.TryParseTimeOfDay(_at.Text, DateTime.Now, Settings.Current.Clock24Hour, out var when)) { _at.Focus(); _at.SelectAll(); return; }
            Settings.Current.TimerAlarmText = _at.Text.Trim();
            CountdownTimer.StartAt(when);
            Close();
        };
        _root.Children.Add(start);
    }

    void UpdateAtHint()
    {
        if (_at == null || _atHint == null) return;
        if (string.IsNullOrWhiteSpace(_at.Text)) { _atHint.Text = "Type a time, like 4:30 PM or 4pm"; return; }
        if (!CountdownTimer.TryParseTimeOfDay(_at.Text, DateTime.Now, Settings.Current.Clock24Hour, out var when)) { _atHint.Text = "That isn’t a time. Try 4:30 PM or 4pm"; return; }
        string day = when.Date == DateTime.Today ? "today" : "tomorrow";
        _atHint.Text = $"Rings {day} at {Clock(when)} · in {DescribeUntil(when - DateTime.Now)}";
    }

    static string Clock(DateTime t) => t.ToString(Settings.Current.Clock24Hour ? "H:mm" : "h:mm tt");

    /// <summary>"1 hr 12 min", "25 min", "less than a minute" (whole minutes, rounded up).</summary>
    static string DescribeUntil(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return "less than a minute";
        long min = (long)Math.Ceiling(t.TotalMinutes - 0.001);
        return min >= 60 ? (min % 60 > 0 ? $"{min / 60} hr {min % 60} min" : $"{min / 60} hr") : $"{min} min";
    }

    // ------------------------------------------------------------------ shared

    FrameworkElement SoundRow()
    {
        var row = new DockPanel { Margin = new Thickness(2, 12, 0, 0), LastChildFill = true };
        var pick = SoundPicker();
        DockPanel.SetDock(pick, Dock.Right);
        row.Children.Add(pick);
        var label = T(AlarmMode ? "Sound" : "When Timer Ends", 13);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        return row;
    }

    static Button StartButton(string text) =>
        new() { Content = text, Style = (Style)Application.Current.Resources["MacDefaultButton"], Height = 28, MinWidth = 0, IsDefault = true, Margin = new Thickness(0, 14, 0, 2) };

    Button SoundPicker()
    {
        var text = new TextBlock { Text = CountdownTimer.SoundName(Settings.Current.TimerSound), VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.updown", Width = 10, Height = 10, StrokeWidth = 2.4, Margin = new Thickness(8, 0, 0, 0) });
        var b = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = sp, Padding = new Thickness(9, 0, 7, 0), MinWidth = 0 };
        b.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, HorizontalOffset = -14, VerticalOffset = -8 };
            foreach (var sound in CountdownTimer.Sounds)
            {
                string chosen = sound;
                Mb.Add(cm.Items, Mb.Item(CountdownTimer.SoundName(sound), () =>
                {
                    Settings.Current.TimerSound = chosen;
                    Settings.Save();
                    text.Text = CountdownTimer.SoundName(chosen);
                    CountdownTimer.Preview(chosen);
                }, isChecked: sound == Settings.Current.TimerSound));
            }
            cm.IsOpen = true;
        };
        return b;
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
            var cancel = Btn("Cancel Alarm", false, CountdownTimer.Cancel);
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
/// (a timer) or Snooze (an alarm). It doesn't take the keyboard from the app you're in.
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
            Child = new SymbolIcon { Symbol = CountdownTimer.IsAlarm ? "bell" : "clock", Width = 26, Height = 26, StrokeWidth = 2.2, Foreground = Brushes.White },
        };
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 10, 0) };
        var head = new TextBlock { Text = CountdownTimer.IsAlarm ? "Alarm" : "Timer", FontWeight = FontWeights.SemiBold };
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
