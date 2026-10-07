using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>
/// Eating mode's little window, in view the whole time it runs: the minute counting down, which minute it is, Pause
/// and End. Top right under the menu bar, or wherever it's dragged (remembered). It never takes the keyboard from the
/// app you're in; each minute's chime makes it blink once.
/// </summary>
public class EatingHud : Window
{
    static EatingHud _shown;

    /// <summary>Shows it while eating mode runs (or is paused), and takes it away when it's ended.</summary>
    public static void Install()
    {
        CountdownTimer.Changed += Sync;
        CountdownTimer.Chimed += () => _shown?.Blink();
        Sync();
    }

    static void Sync()
    {
        bool want = CountdownTimer.IsEating && CountdownTimer.Status is CountdownTimer.State.Running or CountdownTimer.State.Paused;
        if (want && _shown == null) { _shown = new EatingHud(); _shown.Show(); }
        else if (!want && _shown != null) _shown.Close();
        _shown?.Refresh();
    }

    const double W = 196;
    readonly TextBlock _left, _minute;
    readonly Border _bar;
    readonly Button _pause;

    EatingHud()
    {
        Title = "Eating Mode";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.Font;
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetResourceReference(ForegroundProperty, "LabelBrush");

        var card = new Border { Width = W, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5), Padding = new Thickness(12, 9, 12, 11), Margin = new Thickness(16, 8, 16, 22) };
        card.SetResourceReference(Border.BackgroundProperty, "PopoverBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.28 };
        var stack = new StackPanel();

        var head = new DockPanel();
        _minute = Label("", 11, "SecondaryLabelBrush");
        DockPanel.SetDock(_minute, Dock.Right);
        head.Children.Add(_minute);
        head.Children.Add(Label("Eating Mode", 11, "SecondaryLabelBrush", FontWeights.SemiBold));
        stack.Children.Add(head);

        _left = new TextBlock { FontFamily = Theme.DisplayFont, FontSize = 38, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
        _left.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(_left, FontNumeralAlignment.Tabular);
        stack.Children.Add(_left);

        var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5) };
        track.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0, 0, 0));
        _bar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
        _bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Child = _bar;
        stack.Children.Add(track);

        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 10, -3, 0) };
        _pause = Btn("Pause", () => { if (CountdownTimer.Status == CountdownTimer.State.Paused) CountdownTimer.Resume(); else CountdownTimer.Pause(); });
        buttons.Children.Add(_pause);
        buttons.Children.Add(Btn("End", CountdownTimer.Cancel));
        stack.Children.Add(buttons);
        card.Child = stack;
        Content = card;

        // dragged by anything but its buttons; where it's left is remembered
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) != null) return;
            try { DragMove(); } catch { }
            Settings.Current.EatingHudLeft = Left;
            Settings.Current.EatingHudTop = Top;
            Settings.Save(notify: false);
        };
        Loaded += (_, _) => Place();
        Closed += (_, _) => { if (_shown == this) _shown = null; };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            WindowTracker.RegisterChrome(this);
        };
    }

    /// <summary>Where it was left if that's still on the screen, else the top right under the menu bar.</summary>
    void Place()
    {
        double sl = ShellHost.ScreenPx.Left / ShellHost.Scale, st = ShellHost.ScreenPx.Top / ShellHost.Scale;
        double sr = sl + ShellHost.ScreenDip.Width, sb = st + ShellHost.ScreenDip.Height;
        if (Settings.Current.EatingHudLeft is double l && Settings.Current.EatingHudTop is double t
            && l >= sl - 16 && t >= st && l + ActualWidth <= sr + 16 && t + ActualHeight <= sb + 16)
        {
            Left = l;
            Top = t;
            return;
        }
        Left = sr - ActualWidth + 16 - 10;
        Top = st + ShellHost.MenuBarHeight + 2;
    }

    void Refresh()
    {
        _left.Text = CountdownTimer.Text;
        bool paused = CountdownTimer.Status == CountdownTimer.State.Paused;
        _minute.Text = paused ? "Paused" : $"Minute {CountdownTimer.Cycles + 1}";
        _pause.Content = paused ? "Resume" : "Pause";
        _left.Opacity = paused ? 0.5 : 1;
        double total = CountdownTimer.Duration.TotalSeconds;
        _bar.Width = total > 0 ? Math.Clamp(CountdownTimer.Remaining.TotalSeconds / total, 0, 1) * (W - 24) : 0;
    }

    /// <summary>The minute's up: the time turns the accent colour for a moment.</summary>
    void Blink()
    {
        var normal = (TryFindResource("LabelBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x00, 0x7A, 0xFF);
        var brush = new SolidColorBrush(accent);
        _left.Foreground = brush;
        var back = new ColorAnimation(accent, normal, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(400) };
        back.Completed += (_, _) => _left.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        brush.BeginAnimation(SolidColorBrush.ColorProperty, back);
    }

    static TextBlock Label(string text, double size, string brush, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["MacButton"], Height = 22, MinWidth = 0, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(3, 0, 3, 0), FontSize = 12 };
        b.Click += (_, _) => click();
        return b;
    }

    static Button FindButton(DependencyObject d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button b) return b;
        return null;
    }
}
