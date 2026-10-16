using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps;

/// <summary>System Settings' building blocks: grouped rows, switches, pop-up buttons, sliders, page links and sheets.</summary>
public partial class SettingsWindow
{
    static TextBlock Txt(string s, double size = 13, FontWeight? w = null, string brush = "LabelBrush")
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static TextBlock Secondary(string s, double size = 13)
    {
        var t = Txt(s, size, brush: "SecondaryLabelBrush");
        t.TextWrapping = TextWrapping.NoWrap;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    void SectionTitle(string s, UIElement accessory = null)
    {
        var t = Txt(s, 13, FontWeights.Bold);
        t.VerticalAlignment = VerticalAlignment.Center;
        if (accessory == null) { t.Margin = new Thickness(4, 16, 0, 8); _content.Children.Add(t); return; }
        var g = new Grid { Margin = new Thickness(4, 12, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(t);
        Grid.SetColumn(accessory, 1);
        g.Children.Add(accessory);
        _content.Children.Add(g);
    }

    /// <summary>Small print under a group.</summary>
    void Footnote(string s)
    {
        var t = Txt(s, 11, brush: "SecondaryLabelBrush");
        t.Margin = new Thickness(4, -4, 4, 12);
        _content.Children.Add(t);
    }

    static Border GroupBox(IEnumerable<UIElement> rows)
    {
        var sp = new StackPanel();
        foreach (var r in rows)
        {
            if (r == null) continue;
            if (sp.Children.Count > 0)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(10, 0, 10, 0) };
                sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
                sp.Children.Add(sep);
            }
            sp.Children.Add(r);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0.5), Child = sp, Margin = new Thickness(0, 0, 0, 12) };
        box.SetResourceReference(Border.BackgroundProperty, "GroupBoxBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "GroupBoxBorderBrush");
        return box;
    }

    Border Group(params UIElement[] rows) => Group((IEnumerable<UIElement>)rows);

    Border Group(IEnumerable<UIElement> rows)
    {
        var box = GroupBox(rows);
        _content.Children.Add(box);
        return box;
    }

    /// <summary>A setting: its name (and a line explaining it) on the left, its control on the right.</summary>
    static FrameworkElement Row(string label, UIElement control, string sub = null)
    {
        var g = new Grid { MinHeight = 28, Margin = new Thickness(12, 6, 12, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Txt(label, 13));
        if (sub != null) { var s = Txt(sub, 11, brush: "SecondaryLabelBrush"); s.Margin = new Thickness(0, 2, 12, 0); left.Children.Add(s); }
        g.Children.Add(left);
        if (control != null)
        {
            if (control is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.Margin = new Thickness(12, 0, 0, 0);
                if (string.IsNullOrEmpty(AutomationProperties.GetName(fe))) AutomationProperties.SetName(fe, label);
            }
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
        }
        return g;
    }

    /// <summary>A row with an icon on the left (an app, a device, a network).</summary>
    static FrameworkElement IconRow(ImageSource icon, string label, UIElement control, string sub = null, double iconSize = 22, UIElement iconElement = null)
    {
        var row = (Grid)Row(label, control, sub);
        var left = (StackPanel)row.Children[0];
        row.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 1);
        if (control != null) Grid.SetColumn(control, 2);
        UIElement ic = iconElement ?? new Image { Source = icon, Width = iconSize, Height = iconSize };
        if (ic is FrameworkElement ife) { ife.Margin = new Thickness(0, 0, 10, 0); ife.VerticalAlignment = VerticalAlignment.Center; }
        if (ic is Image img) RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        row.Children.Add(ic);
        return row;
    }

    /// <summary>A row that opens a page of its own (the chevron on the right), showing its current value.</summary>
    FrameworkElement NavRow(ImageSource icon, string label, string target, string value = null, string sub = null, double iconSize = 22)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrEmpty(value)) { var v = Secondary(value); v.Margin = new Thickness(0, 0, 8, 0); v.VerticalAlignment = VerticalAlignment.Center; v.MaxWidth = 200; right.Children.Add(v); }
        var chev = new SymbolIcon { Symbol = "chevron.right", Width = 11, Height = 11, StrokeWidth = 2.4, VerticalAlignment = VerticalAlignment.Center };
        chev.SetResourceReference(SymbolIcon.ForegroundProperty, "TertiaryLabelBrush");
        right.Children.Add(chev);
        var row = icon != null ? IconRow(icon, label, right, sub, iconSize) : Row(label, right, sub);
        return Clickable(row, () => Open(target), "page " + target);
    }

    /// <summary>A whole row that does something when clicked (it darkens while pressed, like a Mac list row).</summary>
    static FrameworkElement Clickable(FrameworkElement row, Action click, string what = null)
    {
        var b = new Border { Child = row, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Cursor = Cursors.Arrow };
        AutomationProperties.SetHelpText(b, "click: " + (what ?? "action"));
        bool down = false;
        b.MouseLeftButtonDown += (_, e) => { down = true; b.SetResourceReference(Border.BackgroundProperty, "HoverBrush"); };
        b.MouseLeave += (_, _) => { down = false; b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            b.Background = Brushes.Transparent;
            if (!down) return;
            down = false;
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) return;   // (a button inside the row)
            click();
        };
        return b;
    }

    static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>A read-only value: the name on the left, the value (selectable) on the right.</summary>
    static FrameworkElement InfoRow(string label, string value, string sub = null)
    {
        var v = new TextBox
        {
            Text = value ?? "", IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 13,
            TextAlignment = TextAlignment.Right, MaxWidth = 300, TextWrapping = TextWrapping.Wrap, Cursor = Cursors.IBeam, Padding = new Thickness(0),
            FocusVisualStyle = null,
        };
        v.SetResourceReference(TextBox.ForegroundProperty, "SecondaryLabelBrush");
        v.SetResourceReference(TextBox.SelectionBrushProperty, "AccentBrush");
        v.Template = BareTextBox;
        return Row(label, v, sub);
    }

    static readonly ControlTemplate BareTextBox = MakeBareTextBox();

    static ControlTemplate MakeBareTextBox()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(FocusableProperty, false);
        t.VisualTree = sv;
        return t;
    }

    /// <summary>Buttons at the bottom right of a group ("Details…", "Add…").</summary>
    static FrameworkElement ButtonRow(params FrameworkElement[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 8) };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null) continue;
            if (sp.Children.Count > 0) buttons[i].Margin = new Thickness(8, 0, 0, 0);
            sp.Children.Add(buttons[i]);
        }
        return sp;
    }

    /// <summary>Buttons on both sides of a row: the left ones (like "Forget This Network…") and the right ones.</summary>
    static FrameworkElement SplitButtonRow(FrameworkElement[] left, FrameworkElement[] right)
    {
        var g = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        var l = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var b in left) { if (l.Children.Count > 0) b.Margin = new Thickness(8, 0, 0, 0); l.Children.Add(b); }
        var r = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in right) { if (r.Children.Count > 0) b.Margin = new Thickness(8, 0, 0, 0); r.Children.Add(b); }
        g.Children.Add(l); g.Children.Add(r);
        return g;
    }

    static Button Btn(string text, Action click, bool isDefault = false, double minWidth = 0)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources[isDefault ? "MacDefaultButton" : "MacButton"], MinWidth = minWidth };
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A Windows Settings page, for what Windows keeps to itself (the arrow says it leaves System Settings).</summary>
    static Button WinLink(string text, string uri)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new SymbolIcon { Symbol = "arrow.up.right", Width = 10, Height = 10, StrokeWidth = 2.2, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Style = (Style)Application.Current.Resources["MacButton"], MinWidth = 0 };
        AutomationProperties.SetName(b, text);
        AutomationProperties.SetHelpText(b, "windows: " + uri);
        b.Click += (_, _) => OpenUri(uri);
        return b;
    }

    static CheckBox Switch(bool value, Action<bool> set)
    {
        var cb = new CheckBox { Style = (Style)Application.Current.Resources["MacSwitch"], IsChecked = value };
        cb.Click += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    static CheckBox CheckBox(string text, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Style = (Style)Application.Current.Resources["MacCheckBox"], IsChecked = value, Content = text };
        AutomationProperties.SetName(cb, text);
        cb.Click += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    static Button PopUp(string[] options, int selected, Action<int> choose)
    {
        var text = new TextBlock { Text = options[Math.Clamp(selected, 0, options.Length - 1)], VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.updown", Width = 10, Height = 10, StrokeWidth = 2.4, Margin = new Thickness(8, 0, 0, 0) });
        var b = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = sp, Padding = new Thickness(9, 0, 7, 0), MinWidth = 0 };
        AutomationProperties.SetItemStatus(b, "popup: " + string.Join(" | ", options));
        b.Click += (_, _) =>
        {
            var cm = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, HorizontalOffset = -14, VerticalOffset = -8 };
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                if (options[i] == "-") { Mb.Add(cm.Items, Mb.Sep()); continue; }
                Mb.Add(cm.Items, Mb.Item(options[i], () => { text.Text = options[idx]; choose(idx); }, isChecked: options[i] == text.Text));
            }
            cm.IsOpen = true;
        };
        return b;
    }

    /// <summary>A pop-up of values (seconds, levels …): the one nearest the current value is shown.</summary>
    static Button PopUp<T>((string name, T value)[] options, T current, Action<T> choose)
    {
        int sel = Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.value, current));
        if (sel < 0 && current is IConvertible && options.Length > 0 && options[0].value is IConvertible)
        {
            double c = Convert.ToDouble(current);
            sel = options.Select((o, i) => (d: Math.Abs(Convert.ToDouble(o.value) - c), i)).OrderBy(x => x.d).First().i;
        }
        return PopUp(options.Select(o => o.name).ToArray(), Math.Max(0, sel), i => choose(options[i].value));
    }

    static FrameworkElement LabeledSlider(double min, double max, double value, string left, string right, Action<double> set, double width = 240)
    {
        var g = new Grid { Width = width };
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        var s = new Slider { Style = (Style)Application.Current.Resources["MacSlider"], Minimum = min, Maximum = max, Value = value };
        s.ValueChanged += (_, e) => set(e.NewValue);
        g.Children.Add(s);
        var l = Txt(left, 10, brush: "SecondaryLabelBrush");
        var r = Txt(right, 10, brush: "SecondaryLabelBrush");
        r.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(l, 1); Grid.SetRow(r, 1);
        g.Children.Add(l); g.Children.Add(r);
        return g;
    }

    /// <summary>A slider that stops at steps, with tick marks under it (and words under its ends) - set when let go.</summary>
    static FrameworkElement TickSlider(int steps, int value, string left, string right, Action<int> set, double width = 240, string[] labels = null)
    {
        var g = new Grid { Width = width };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var s = new Slider { Style = (Style)Application.Current.Resources["MacSlider"], Minimum = 0, Maximum = steps - 1, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 1 };
        int last = value;
        void Commit() { int v = (int)Math.Round(s.Value); if (v != last) { last = v; set(v); } }
        s.ValueChanged += (_, _) => { if (!s.IsMouseCaptureWithin) Commit(); };
        s.PreviewMouseLeftButtonUp += (_, _) => s.Dispatcher.BeginInvoke(Commit);
        s.LostMouseCapture += (_, _) => Commit();
        g.Children.Add(s);
        var ticks = new Grid { Height = 5, Margin = new Thickness(10, 0, 10, 0) };
        for (int i = 0; i < steps; i++)
        {
            var tk = new Border { Width = 1, Height = 5, HorizontalAlignment = HorizontalAlignment.Left };
            tk.SetResourceReference(Border.BackgroundProperty, "TertiaryLabelBrush");
            ticks.Children.Add(tk);
        }
        ticks.SizeChanged += (_, e) =>
        {
            for (int i = 0; i < ticks.Children.Count; i++)
                ((Border)ticks.Children[i]).Margin = new Thickness(steps == 1 ? 0 : i * (e.NewSize.Width - 1) / (steps - 1), 0, 0, 0);
        };
        Grid.SetRow(ticks, 1);
        g.Children.Add(ticks);
        var lbl = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        if (labels != null && labels.Length == steps)
        {
            for (int i = 0; i < steps; i++)
            {
                var t = Txt(labels[i], 10, brush: "SecondaryLabelBrush");
                t.TextWrapping = TextWrapping.NoWrap;
                lbl.Children.Add(t);
                int idx = i;
                lbl.SizeChanged += (_, e) => { t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); t.Margin = new Thickness(Math.Clamp(10 + idx * (e.NewSize.Width - 20) / (steps - 1) - t.DesiredSize.Width / 2, 0, Math.Max(0, e.NewSize.Width - t.DesiredSize.Width)), 0, 0, 0); };
            }
        }
        else
        {
            var l = Txt(left, 10, brush: "SecondaryLabelBrush");
            var r = Txt(right, 10, brush: "SecondaryLabelBrush");
            r.HorizontalAlignment = HorizontalAlignment.Right;
            lbl.Children.Add(l); lbl.Children.Add(r);
        }
        Grid.SetRow(lbl, 2);
        g.Children.Add(lbl);
        AutomationProperties.SetItemStatus(g, $"slider {value + 1}/{steps}");
        return g;
    }

    /// <summary>A Mac segmented control (one choice of a few, side by side).</summary>
    static FrameworkElement Segmented(string[] options, int selected, Action<int> choose)
    {
        var bar = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(2), BorderThickness = new Thickness(0.5) };
        bar.SetResourceReference(Border.BackgroundProperty, "SearchFieldBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "GroupBoxBorderBrush");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = sp;
        var cells = new List<Border>();
        void Paint(int sel)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (i == sel)
                {
                    cells[i].SetResourceReference(Border.BackgroundProperty, "ControlBrush");
                    cells[i].Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0.5, Direction = 270, Opacity = 0.18 };
                }
                else { cells[i].Background = Brushes.Transparent; cells[i].Effect = null; }
            }
        }
        for (int i = 0; i < options.Length; i++)
        {
            var t = Txt(options[i], 12);
            t.TextWrapping = TextWrapping.NoWrap;
            var cell = new Border { CornerRadius = new CornerRadius(4.5), Padding = new Thickness(12, 2, 12, 3), Child = t, Background = Brushes.Transparent, MinWidth = 60 };
            t.HorizontalAlignment = HorizontalAlignment.Center;
            int idx = i;
            cell.MouseLeftButtonUp += (_, _) => { Paint(idx); choose(idx); };
            cells.Add(cell);
            sp.Children.Add(cell);
        }
        Paint(selected);
        AutomationProperties.SetItemStatus(bar, "segments: " + string.Join(" | ", options));
        return bar;
    }

    /// <summary>The big icon, name and description at the top of a hub pane (General, Accessibility, Privacy & Security).</summary>
    void Hero(ImageSource icon, string title, string description)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 18, 24, 18) };
        sp.Children.Add(new Image { Source = icon, Width = 60, Height = 60, HorizontalAlignment = HorizontalAlignment.Center });
        var t = Txt(title, 16, FontWeights.Bold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 10, 0, 4);
        sp.Children.Add(t);
        var d = Txt(description, 12, brush: "SecondaryLabelBrush");
        d.TextAlignment = TextAlignment.Center;
        d.MaxWidth = 380;
        sp.Children.Add(d);
        Group(sp);
    }

    /// <summary>The tile icon of a pane (or of a page of one).</summary>
    static ImageSource Tile(string sym, string top, string bottom) => MacIcons.Tile(sym, top, bottom);

    static FrameworkElement StatusDot(Color color, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var t = Txt(text, 11, brush: "SecondaryLabelBrush");
        t.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(t);
        return sp;
    }

    static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59), Yellow = Color.FromRgb(0xFF, 0xCC, 0x00), Red = Color.FromRgb(0xFF, 0x3B, 0x30), Gray = Color.FromRgb(0x8E, 0x8E, 0x93);

    /// <summary>The small spinning "busy" wheel.</summary>
    static FrameworkElement Spinner(double size = 14)
    {
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7"),
            StrokeThickness = 1.8, Width = size, Height = size, Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "SecondaryLabelBrush");
        arc.Loaded += (_, _) => arc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }

    static TextBox Field(string text, double width, string placeholder = null)
    {
        var b = new TextBox { Style = (Style)Application.Current.Resources["MacTextField"], Text = text ?? "", Width = width, Height = 24, Tag = placeholder };
        return b;
    }

    // ------------------------------------------------------------------ sheets

    /// <summary>A Mac sheet over the window: its content, then the buttons (the last is the default, "Cancel" closes).</summary>
    void Sheet(UIElement content, double width, params (string text, Func<bool> click)[] buttons) => Sheet(content, width, null, buttons);

    void Sheet(UIElement content, double width, FrameworkElement leftButton, params (string text, Func<bool> click)[] buttons)
    {
        _sheetLayer.Children.Clear();
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(Theme.IsDark ? (byte)0x55 : (byte)0x26, 0, 0, 0)) };
        dim.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _sheetLayer.Children.Add(dim);
        var card = new Border
        {
            Width = width, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0.5), Padding = new Thickness(20, 18, 20, 16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 0, 30),
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.35 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        var dp = new DockPanel();
        var bar = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        if (leftButton != null) { leftButton.HorizontalAlignment = HorizontalAlignment.Left; bar.Children.Add(leftButton); }
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            var (text, click) = buttons[i];
            bool isDefault = i == buttons.Length - 1 && text != "Cancel";
            var b = new Button { Content = text, Style = (Style)Application.Current.Resources[isDefault ? "MacDefaultButton" : "MacButton"], MinWidth = 76, Height = 24, IsDefault = isDefault, IsCancel = text == "Cancel" };
            AutomationProperties.SetName(b, text);
            if (rightButtons.Children.Count > 0) b.Margin = new Thickness(10, 0, 0, 0);
            b.Click += (_, _) => { if (click == null || click()) CloseSheet(); };
            rightButtons.Children.Add(b);
        }
        bar.Children.Add(rightButtons);
        dp.Children.Add(bar);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, MaxHeight = Math.Max(200, ActualHeight - 170) };
        dp.Children.Add(scroll);
        card.Child = dp;
        _sheetLayer.Children.Add(card);
        _sheetLayer.Visibility = Visibility.Visible;
        var tr = new TranslateTransform(0, -18);
        card.RenderTransform = tr;
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void CloseSheet()
    {
        _sheetLayer.Children.Clear();
        _sheetLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>The top of a sheet: an icon and a bold title, with a line under it.</summary>
    static FrameworkElement SheetHeader(ImageSource icon, string title, string sub = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        if (icon != null) g.Children.Add(new Image { Source = icon, Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Txt(title, 15, FontWeights.Bold));
        if (sub != null) sp.Children.Add(Txt(sub, 12, brush: "SecondaryLabelBrush"));
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }

    /// <summary>A group for a sheet's content (not added to the page).</summary>
    static Border SheetGroup(params UIElement[] rows) => GroupBox(rows);
}
