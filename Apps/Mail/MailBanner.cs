using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Apps.Mail;

/// <summary>
/// New mail, announced like a macOS notification: a banner at the top right (Mail's icon, the sender, the subject, the
/// beginning of the message) for six seconds. Clicking it opens the message in Mail. It never takes the keyboard.
/// </summary>
public sealed class MailBanner : Window
{
    static MailBanner _shown;

    public static void Show(List<MailMessageInfo> fresh)
    {
        if (fresh == null || fresh.Count == 0) return;
        _shown?.Close();
        _shown = new MailBanner(fresh);
        _shown.Show();
    }

    MailBanner(List<MailMessageInfo> fresh)
    {
        var first = fresh.OrderByDescending(m => m.Date).First();
        Title = "Mail Banner";
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
        SetResourceReference(ForegroundProperty, "LabelBrush");

        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5), Padding = new Thickness(12, 10, 14, 11), Margin = new Thickness(20, 6, 20, 28), Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "PopoverBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        card.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.3 };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Image { Source = MacIcons.Mail, Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) });
        var text = new StackPanel();
        var top = new DockPanel();
        var when = Label(MailItem.FormatDate(first.Date), 11, "SecondaryLabelBrush");
        DockPanel.SetDock(when, Dock.Right);
        top.Children.Add(when);
        top.Children.Add(Label(fresh.Count == 1 ? first.Sender : $"{fresh.Count} new messages", 13, "LabelBrush", FontWeights.SemiBold));
        text.Children.Add(top);
        text.Children.Add(Label(fresh.Count == 1 ? (string.IsNullOrWhiteSpace(first.Subject) ? "(No Subject)" : first.Subject)
                                                 : string.Join(", ", fresh.Select(m => m.Sender).Distinct().Take(3)), 12.5, "LabelBrush"));
        if (fresh.Count == 1 && !string.IsNullOrWhiteSpace(first.Preview))
        {
            var p = Label(first.Preview, 12, "SecondaryLabelBrush");
            p.TextWrapping = TextWrapping.Wrap;
            p.MaxHeight = 32;
            text.Children.Add(p);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        card.Child = grid;
        Content = card;
        card.MouseLeftButtonUp += (_, _) => { Close(); MailWindow.Reveal(first); };

        var slide = new TranslateTransform(380, 0);
        card.RenderTransform = slide;
        Loaded += (_, _) =>
        {
            double screenRight = ShellHost.ScreenPx.Left / ShellHost.Scale + ShellHost.ScreenDip.Width;
            Left = screenRight + 10 - ActualWidth;
            Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.MenuBarHeight + 2;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); if (!IsMouseOver) Close(); else timer.Start(); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); if (_shown == this) _shown = null; };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            WindowTracker.RegisterChrome(this);
        };
    }

    static TextBlock Label(string s, double size, string brush, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}
