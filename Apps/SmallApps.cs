using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Apps;

/// <summary>About This Mac (Sonoma layout).</summary>
public class AboutWindow : MacWindow
{
    static AboutWindow _instance;

    public static void ShowWindow()
    {
        if (_instance != null) { _instance.Activate(); return; }
        _instance = new AboutWindow();
        _instance.Show();
        if (!ShellHost.Offscreen) _instance.Activate();
    }

    AboutWindow() : base(WindowTracker.FinderKey, 30)
    {
        UseVibrancy = false;
        Title = "About This Mac";
        Width = 290;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Closed += (_, _) => _instance = null;
        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        var lights = new TrafficLights { Margin = new Thickness(10, 9, 0, 0), VerticalAlignment = VerticalAlignment.Top, CanMinimize = false, CanZoom = false };
        root.Children.Add(lights);
        var sp = new StackPanel { Margin = new Thickness(20, 40, 20, 22) };
        var img = new Image { Source = MacIcons.Laptop(Wallpaper.Image), Width = 170, Margin = new Thickness(0, 0, 0, 18) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        sp.Children.Add(img);
        sp.Children.Add(Center(MachineInfo.Model, 24, FontWeights.Bold));
        var sub = Center(MachineInfo.ModelDetail, 11, FontWeights.Normal, "SecondaryLabelBrush");
        sub.Margin = new Thickness(0, 2, 0, 18);
        sp.Children.Add(sub);
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        TextBlock serial = null;
        void Row(string k, string v, bool isSerial = false)
        {
            int r = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = Txt(k, 11, FontWeights.Normal, "LabelBrush");
            a.TextAlignment = TextAlignment.Right; a.Margin = new Thickness(0, 2, 8, 2);
            var b = Txt(v, 11, FontWeights.Normal, "SecondaryLabelBrush");
            b.Margin = new Thickness(0, 2, 0, 2); b.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(a, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
            rows.Children.Add(a); rows.Children.Add(b);
            if (isSerial) serial = b;
        }
        Row("Chip", MachineInfo.Cpu);
        Row("Memory", MachineInfo.MemoryGB);
        Row("Startup disk", MachineInfo.StartupDisk);
        Row("Serial number", "…", true);
        Row("macOS", $"{MachineInfo.OsName} {MachineInfo.OsVersion}");
        sp.Children.Add(rows);
        Task.Run(() => MachineInfo.Serial).ContinueWith(t => Dispatcher.BeginInvoke(() => { if (serial != null) serial.Text = t.Result; }));
        var more = new Button { Content = "More Info…", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0), MinWidth = 100 };
        more.Click += (_, _) => SettingsWindow.ShowPane("general");
        sp.Children.Add(more);
        var foot = Center("MacShell for Windows · Not affiliated with Apple Inc.", 9.5, FontWeights.Normal, "TertiaryLabelBrush");
        foot.Margin = new Thickness(0, 18, 0, 0);
        sp.Children.Add(foot);
        root.Children.Add(sp);
        Content = root;
        Loaded += (_, _) => CenterOnWorkArea(0.3);
    }

    static TextBlock Txt(string s, double size, FontWeight w, string brush)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = w };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static TextBlock Center(string s, double size, FontWeight w, string brush = "LabelBrush")
    {
        var t = Txt(s, size, w, brush);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }
}

/// <summary>Force Quit Applications (⌥⌘⎋).</summary>
public class ForceQuitWindow : MacWindow
{
    static ForceQuitWindow _instance;
    readonly ListBox _list = new() { Height = 250 };
    readonly Button _quit = new() { Content = "Force Quit", Style = (Style)Application.Current.Resources["MacDefaultButton"], MinWidth = 96 };

    public static void ShowWindow()
    {
        if (_instance != null) { _instance.Activate(); return; }
        _instance = new ForceQuitWindow();
        _instance.Show();
        _instance.Activate();
    }

    ForceQuitWindow() : base(WindowTracker.FinderKey, 28)
    {
        UseVibrancy = false;
        Title = "Force Quit Applications";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Closed += (_, _) => _instance = null;
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        var bar = new Grid { Height = 28 };
        bar.Children.Add(new TrafficLights { Margin = new Thickness(9, 0, 0, 0), CanMinimize = false, CanZoom = false });
        var title = new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        bar.Children.Add(title);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var sp = new StackPanel { Margin = new Thickness(20, 8, 20, 18) };
        var hint = new TextBlock { Text = "If an app doesn’t respond for a while, select its name and click Force Quit.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(hint);
        var box = new Border { BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(4), Child = _list, Padding = new Thickness(0, 4, 0, 4) };
        box.SetResourceReference(Border.BackgroundProperty, "ContentBackgroundBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        sp.Children.Add(box);
        var foot = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        var tip = new TextBlock { Text = "You can open this window by pressing\nCtrl+Alt+Escape.", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        foot.Children.Add(tip);
        _quit.HorizontalAlignment = HorizontalAlignment.Right;
        _quit.Click += (_, _) => QuitSelected();
        foot.Children.Add(_quit);
        sp.Children.Add(foot);
        root.Children.Add(sp);
        Content = root;
        Populate();
        _list.SelectionChanged += (_, _) =>
        {
            var app = (_list.SelectedItem as ListBoxItem)?.Tag as RunningApp;
            _quit.Content = app?.Key == WindowTracker.FinderKey ? "Relaunch" : "Force Quit";
        };
        WindowTracker.AppsChanged += Populate;
        Closed += (_, _) => WindowTracker.AppsChanged -= Populate;
        Loaded += (_, _) => CenterOnWorkArea(0.3);
    }

    void Populate()
    {
        var selKey = ((_list.SelectedItem as ListBoxItem)?.Tag as RunningApp)?.Key;
        _list.Items.Clear();
        foreach (var app in WindowTracker.Apps.Values.Where(a => a.Windows.Count > 0 || a.Key == WindowTracker.FinderKey).OrderBy(a => a.Name))
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Height = 24 };
            var img = new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
            if (app.IsInternal) img.Source = app.Key == "internal:settings" ? MacIcons.SystemSettings : MacIcons.Finder;
            else if (app.IconSource != null) ShellIcons.Load(app.IconSource, 48, false, b => img.Source = b);
            sp.Children.Add(img);
            sp.Children.Add(new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = sp, Tag = app };
            _list.Items.Add(item);
            if (app.Key == selKey) _list.SelectedItem = item;
        }
        if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    void QuitSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not RunningApp app) return;
        if (app.Key == WindowTracker.FinderKey)
        {
            foreach (var w in Finder.FinderWindow.All.ToList()) w.Close();
            ShellHost.OpenFinder(null);
            return;
        }
        if (ShellHost.Alert($"Do you want to force “{app.Name}” to quit?", "You will lose any unsaved changes.", "Cancel", "Force Quit") == "Force Quit")
            WindowTracker.ForceQuitApp(app);
    }
}
