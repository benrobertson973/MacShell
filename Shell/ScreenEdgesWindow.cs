using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Services;

namespace MacShell.Shell;

/// <summary>
/// Screen Edges: for a screen whose edges can't all be seen (a TV, a covered or broken strip). A thick yellow line
/// runs along every edge of the screen, and one plain question asks whether all four can be seen. If not, each
/// line is brought in - big "Move in" / "Move out" buttons (hold them), or drag the line - until it shows; what's
/// outside the lines turns black. Done: the menu bar, the Dock, the desktop and maximized windows stay inside them.
/// Shown once on a new installation; any time from System Settings › Displays.
/// </summary>
public class ScreenEdgesWindow : Window
{
    static ScreenEdgesWindow _open;

    public static void Open(bool firstRun)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new ScreenEdgesWindow(firstRun);
        _open.Show();
        _open.Activate();
    }

    static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    const double LineDip = 8;   // the yellow line's thickness

    readonly bool _firstRun;
    readonly int[] _trim = new int[4];   // left, top, right, bottom (screen pixels)
    readonly int[] _before = new int[4];
    readonly double _w, _h, _s;          // the whole display (DIPs) and its scale
    readonly Canvas _canvas = new();
    readonly Rectangle[] _black = new Rectangle[4];
    readonly Grid[] _lines = new Grid[4];
    readonly TextBlock[] _labels = new TextBlock[4];
    readonly TextBlock[] _amounts = new TextBlock[4];
    readonly Border _card = new();
    bool _done;

    static readonly string[] SideNames = { "LEFT", "TOP", "RIGHT", "BOTTOM" };

    ScreenEdgesWindow(bool firstRun)
    {
        _firstRun = firstRun;
        var cur = Settings.Current.ScreenTrim;
        for (int i = 0; i < 4; i++) _trim[i] = _before[i] = cur is { Length: 4 } ? cur[i] : 0;
        _s = ShellHost.Scale;
        var m = ShellHost.MonitorPx;
        _w = m.Width / _s;
        _h = m.Height / _s;

        Title = "Screen Edges";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Left = m.Left / _s;
        Top = m.Top / _s;
        Width = _w;
        Height = _h;
        Background = new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x34), Color.FromRgb(0x12, 0x12, 0x16), 90);

        var root = new Grid();
        root.Children.Add(_canvas);
        for (int i = 0; i < 4; i++)
        {
            _black[i] = new Rectangle { Fill = Brushes.Black };
            _canvas.Children.Add(_black[i]);
        }
        for (int i = 0; i < 4; i++) _canvas.Children.Add(_lines[i] = MakeLine(i));
        for (int i = 0; i < 4; i++)
        {
            _labels[i] = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Yellow) };
            _canvas.Children.Add(_labels[i]);
        }
        _card.Background = Brushes.White;
        _card.CornerRadius = new CornerRadius(22);
        _card.Padding = new Thickness(40, 34, 40, 30);
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Center;
        _card.MaxWidth = 720;
        _card.Effect = new DropShadowEffect { BlurRadius = 50, ShadowDepth = 10, Direction = 270, Opacity = 0.5 };
        root.Children.Add(_card);
        Content = root;

        if (firstRun) ShowQuestion(); else ShowAdjust();
        Layout();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) Finish();
        };
        Closed += (_, _) =>
        {
            _open = null;
            if (_firstRun && !_done) { Settings.Current.ScreenTrimAsked = true; Settings.Save(notify: false); }   // (asked once)
        };
    }

    // ------------------------------------------------------------------ the two steps

    /// <summary>Step 1: one question - can all four yellow lines be seen?</summary>
    void ShowQuestion()
    {
        var sp = new StackPanel();
        sp.Children.Add(Picture());
        sp.Children.Add(Text("Can you see the yellow line on all 4 sides of your screen?", 30, FontWeights.Bold, top: 18));
        sp.Children.Add(Text("Look at the very edges of your screen: the top, the bottom, the left and the right. " +
                             "There is a thick yellow line along each one.", 18, top: 12, gray: true));
        var buttons = new Grid { Margin = new Thickness(0, 30, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var yes = BigButton("✓   Yes, I see all 4", Color.FromRgb(0x30, 0xB0, 0x50), Finish);
        var no = BigButton("✗   No, some are missing", Color.FromRgb(0xFF, 0x8C, 0x00), () =>
        {
            ShowAdjust();
            Layout();
        });
        Grid.SetColumn(no, 2);
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        sp.Children.Add(buttons);
        sp.Children.Add(Text("This makes sure nothing ends up hidden past the edge of your screen.", 15, top: 22, gray: true));
        _card.Child = sp;
    }

    /// <summary>Step 2: each side's line brought in until it can be seen.</summary>
    void ShowAdjust()
    {
        var sp = new StackPanel();
        sp.Children.Add(Text("Bring each yellow line in until you can see it", 28, FontWeights.Bold));
        sp.Children.Add(Text("For every side where you can't see the yellow line, press “Move in” (or hold it down). " +
                             "Stop as soon as the line appears. You can also drag the lines with the mouse.", 17, top: 10, gray: true));
        var rows = new Grid { Margin = new Thickness(0, 22, 0, 6) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // (in / out arrows point the way the line moves on that side)
        string[] inArrow = { "▶", "▼", "◀", "▲" }, outArrow = { "◀", "▲", "▶", "▼" };
        string[] names = { "Left side", "Top", "Right side", "Bottom" };
        for (int i = 0; i < 4; i++)
        {
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int side = i;
            var name = Text(names[i], 20, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(0, 6, 18, 6);
            Grid.SetRow(name, i);
            rows.Children.Add(name);
            var outB = HoldButton($"{outArrow[i]}  Move out", Color.FromRgb(0xE5, 0xE5, 0xEA), Brushes.Black, () => Nudge(side, -2));
            var amount = Text("", 18, FontWeights.SemiBold);
            amount.Width = 120;
            amount.TextAlignment = TextAlignment.Center;
            amount.VerticalAlignment = VerticalAlignment.Center;
            _amounts[i] = amount;
            var inB = HoldButton($"Move in  {inArrow[i]}", Color.FromRgb(0x0A, 0x84, 0xFF), Brushes.White, () => Nudge(side, +2));
            Grid.SetRow(outB, i); Grid.SetColumn(outB, 2);
            Grid.SetRow(amount, i); Grid.SetColumn(amount, 3);
            Grid.SetRow(inB, i); Grid.SetColumn(inB, 4);
            rows.Children.Add(outB);
            rows.Children.Add(amount);
            rows.Children.Add(inB);
        }
        sp.Children.Add(rows);
        var bottom = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var over = BigButton("Start over", Color.FromRgb(0xE5, 0xE5, 0xEA), () => { Array.Clear(_trim); Layout(); }, Brushes.Black, wide: false);
        var done = BigButton("✓   Done — I can see all 4 lines", Color.FromRgb(0x30, 0xB0, 0x50), Finish, wide: false);
        Grid.SetColumn(done, 2);
        bottom.Children.Add(over);
        bottom.Children.Add(done);
        sp.Children.Add(bottom);
        sp.Children.Add(Text("The menu bar, the Dock and your windows will stay inside the yellow lines.", 15, top: 18, gray: true));
        _card.Child = sp;
    }

    void Nudge(int side, int px)
    {
        int max = side is 0 or 2 ? (int)(_w * _s / 4) : (int)(_h * _s / 4);
        _trim[side] = Math.Clamp(_trim[side] + px, 0, max);
        Layout();
    }

    void Finish()
    {
        _done = true;
        bool changed = !_trim.SequenceEqual(_before);
        Settings.Current.ScreenTrim = _trim.Any(v => v > 0) ? (int[])_trim.Clone() : null;
        Settings.Current.ScreenTrimAsked = true;
        Settings.Save(notify: false);
        Close();
        if (changed) ShellHost.ApplyScreenTrim();
        Apps.SettingsWindow.Refresh("displays");
    }

    // ------------------------------------------------------------------ the lines

    /// <summary>A side's yellow line, with room around it to grab it by.</summary>
    Grid MakeLine(int side)
    {
        bool vertical = side is 0 or 2;
        var g = new Grid { Background = Brushes.Transparent, Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS };
        var line = new Rectangle { Fill = new SolidColorBrush(Yellow) };
        if (vertical)
        {
            line.Width = LineDip;
            line.HorizontalAlignment = side == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        else
        {
            line.Height = LineDip;
            line.VerticalAlignment = side == 1 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        }
        g.Children.Add(line);
        g.MouseLeftButtonDown += (_, e) => { g.CaptureMouse(); e.Handled = true; };
        g.MouseLeftButtonUp += (_, _) => g.ReleaseMouseCapture();
        g.MouseMove += (_, e) =>
        {
            if (!g.IsMouseCaptured) return;
            var p = e.GetPosition(_canvas);
            double dip = side switch { 0 => p.X, 1 => p.Y, 2 => _w - p.X, _ => _h - p.Y };
            int max = vertical ? (int)(_w * _s / 4) : (int)(_h * _s / 4);
            _trim[side] = Math.Clamp((int)Math.Round(dip * _s), 0, max);
            Layout();
        };
        return g;
    }

    /// <summary>Lines, labels and the black outside put where the trims say.</summary>
    void Layout()
    {
        double l = _trim[0] / _s, t = _trim[1] / _s, r = _trim[2] / _s, b = _trim[3] / _s;
        const double grab = 44;   // the line's grabbable width
        Place(_black[0], 0, 0, l, _h);
        Place(_black[1], 0, 0, _w, t);
        Place(_black[2], _w - r, 0, r, _h);
        Place(_black[3], 0, _h - b, _w, b);
        Place(_lines[0], l, t, grab, _h - t - b);
        Place(_lines[1], l, t, _w - l - r, grab);
        Place(_lines[2], _w - r - grab, t, grab, _h - t - b);
        Place(_lines[3], l, _h - b - grab, _w - l - r, grab);
        for (int i = 0; i < 4; i++)
        {
            _labels[i].Text = i switch { 0 => "◀ LEFT EDGE", 1 => "▲ TOP EDGE", 2 => "RIGHT EDGE ▶", _ => "▼ BOTTOM EDGE" };
            _labels[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = _labels[i].DesiredSize;
            var (x, y) = i switch
            {
                0 => (l + LineDip + 14, (_h - sz.Height) / 2),
                1 => ((_w - sz.Width) / 2, t + LineDip + 12),
                2 => (_w - r - LineDip - 14 - sz.Width, (_h - sz.Height) / 2),
                _ => ((_w - sz.Width) / 2, _h - b - LineDip - 12 - sz.Height),
            };
            Canvas.SetLeft(_labels[i], x);
            Canvas.SetTop(_labels[i], y);
            if (_amounts[i] != null) _amounts[i].Text = _trim[i] == 0 ? "not moved" : $"{_trim[i]} px in";
        }
    }

    static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>A little screen with a yellow line around it: what to look for.</summary>
    static UIElement Picture()
    {
        var screen = new Border
        {
            Width = 150, Height = 96, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2A)),
            BorderBrush = new SolidColorBrush(Yellow), BorderThickness = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stand = new Border { Width = 46, Height = 8, CornerRadius = new CornerRadius(0, 0, 4, 4), Background = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), HorizontalAlignment = HorizontalAlignment.Center };
        var sp = new StackPanel();
        sp.Children.Add(screen);
        sp.Children.Add(stand);
        return sp;
    }

    static TextBlock Text(string s, double size, FontWeight? weight = null, double top = 0, bool gray = false) => new()
    {
        Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        Foreground = gray ? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5C)) : Brushes.Black, Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>A big, plain button.</summary>
    static Border BigButton(string text, Color color, Action click, Brush fg = null, bool wide = true)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(14), Padding = new Thickness(wide ? 16 : 26, 18, wide ? 16 : 26, 18),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = fg ?? Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.88;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    /// <summary>A button that keeps going while it's held down (faster after a moment).</summary>
    static Border HoldButton(string text, Color color, Brush fg, Action step)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 5, 0, 5),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center },
        };
        var repeat = new DispatcherTimer();
        int ticks = 0;
        repeat.Tick += (_, _) =>
        {
            ticks++;
            repeat.Interval = TimeSpan.FromMilliseconds(ticks > 15 ? 25 : 60);
            step();
            if (ticks > 15) step();   // (held a while: faster)
        };
        void Stop() { repeat.Stop(); b.Opacity = 1; }
        b.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            b.Opacity = 0.75;
            step();
            ticks = 0;
            repeat.Interval = TimeSpan.FromMilliseconds(400);
            repeat.Start();
            b.CaptureMouse();
        };
        b.MouseLeftButtonUp += (_, _) => { Stop(); b.ReleaseMouseCapture(); };
        b.LostMouseCapture += (_, _) => Stop();
        return b;
    }
}
