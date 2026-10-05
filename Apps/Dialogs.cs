using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps;

/// <summary>Base for small floating panels: rounded, shadowed, theme-aware.</summary>
public class PanelWindow : Window
{
    protected readonly Border Card = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0.5) };

    public PanelWindow(double margin = 30)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = Theme.Font;
        FontSize = 13;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetResourceReference(ForegroundProperty, "LabelBrush");
        Card.Margin = new Thickness(margin, margin * 0.7, margin, margin * 1.3);
        Card.SetResourceReference(Border.BackgroundProperty, "PopoverBackgroundBrush");
        Card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        Card.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.32 };
        Content = Card;
    }

    protected static TextBlock Text(string s, double size = 13, FontWeight? weight = null, string brush = "LabelBrush", TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock { Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextAlignment = align };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    public void CenterOnScreen(double topBias = 0.3)
    {
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + (wa.Height - ActualHeight) * topBias;
        };
    }
}

/// <summary>macOS (Big Sur+) style alert: icon, bold title, message, compact buttons.</summary>
public class MacAlert : PanelWindow
{
    public string Result { get; private set; }

    public MacAlert(string title, string message, string[] buttons)
    {
        Topmost = true;
        Title = title;
        var sp = new StackPanel { Width = 260, Margin = new Thickness(18, 20, 18, 16) };
        sp.Children.Add(new Image { Source = MacIcons.Finder, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) });
        sp.Children.Add(Text(title, 13, FontWeights.Bold, align: TextAlignment.Center));
        if (!string.IsNullOrWhiteSpace(message))
        {
            var m = Text(message, 11, align: TextAlignment.Center);
            m.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(m);
        }
        Result = buttons.FirstOrDefault(b => b == "Cancel") ?? buttons[0];
        var panel = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        bool stacked = buttons.Length > 2 || buttons.Any(b => b.Length > 14);
        for (int i = 0; i < buttons.Length; i++)
        {
            string label = buttons[i];
            bool isDefault = i == buttons.Length - 1;
            var b = new Button
            {
                Content = label,
                Style = (Style)Application.Current.Resources[isDefault ? "MacDefaultButton" : "MacButton"],
                Height = 28, MinWidth = 0,
                IsDefault = isDefault,
                IsCancel = label == "Cancel",
            };
            b.Click += (_, _) => { Result = label; Close(); };
            if (stacked)
            {
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                b.Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
                Grid.SetRow(b, i);
            }
            else
            {
                panel.ColumnDefinitions.Add(new ColumnDefinition());
                b.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 0);
                Grid.SetColumn(b, i);
            }
            panel.Children.Add(b);
        }
        sp.Children.Add(panel);
        Card.Child = sp;
        Card.CornerRadius = new CornerRadius(14);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        CenterOnScreen(0.28);
        Loaded += (_, _) => Activate();
    }
}

/// <summary>Single-line text prompt ("Go to Folder", "New Folder" …).</summary>
public class TextPrompt : PanelWindow
{
    readonly TextBox _box;
    bool _ok;

    TextPrompt(string title, string message, string initial)
    {
        Topmost = true;
        var sp = new StackPanel { Width = 380, Margin = new Thickness(20, 18, 20, 16) };
        sp.Children.Add(Text(title, 13, FontWeights.Bold));
        var m = Text(message, 12, brush: "SecondaryLabelBrush");
        m.Margin = new Thickness(0, 4, 0, 10);
        sp.Children.Add(m);
        _box = new TextBox { Style = (Style)Application.Current.Resources["MacTextField"], Text = initial ?? "", Height = 24 };
        sp.Children.Add(_box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.Resources["MacButton"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var go = new Button { Content = "Go", Style = (Style)Application.Current.Resources["MacDefaultButton"], IsDefault = true };
        cancel.Click += (_, _) => Close();
        go.Click += (_, _) => { _ok = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(go);
        sp.Children.Add(buttons);
        Card.Child = sp;
        Loaded += (_, _) => { Activate(); _box.Focus(); _box.SelectAll(); };
    }

    public static string Ask(Window owner, string title, string message, string initial)
    {
        var p = new TextPrompt(title, message, initial);
        if (owner != null && owner.IsVisible)
        {
            p.Owner = owner;
            p.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else p.CenterOnScreen(0.25);
        p.ShowDialog();
        return p._ok ? p._box.Text : null;
    }
}
