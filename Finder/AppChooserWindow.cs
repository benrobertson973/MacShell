using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Finder;

/// <summary>
/// Open With › Other…, like macOS: "Choose an application to open the document". The apps that can open it come first,
/// then every app; a search field narrows the list, and "Always Open With" makes the choice the default for the kind.
/// </summary>
public class AppChooserWindow : PanelWindow
{
    readonly string _path;
    readonly TextBox _search = new();
    readonly StackPanel _list = new();
    readonly CheckBox _always;
    readonly List<(OwApp app, Border row)> _rows = new();
    readonly List<OwApp> _recommended, _all;
    int _sel = -1;
    OwApp _result;

    public static void Choose(string path, bool always)
    {
        var w = new AppChooserWindow(path, always, pickOnly: false);
        if (ShellHost.Offscreen) { w.Left = -6000; w.ShowActivated = false; } else w.CenterOnScreen(0.22);   // (off-screen: test mode)
        w.ShowDialog();
        if (w._result == null) return;
        if (w._always.IsChecked == true) OpenWith.SetDefaultForKind(path, w._result);
        if (!OpenWith.Open(path, w._result)) AppCatalog.OpenWith(path);
    }

    /// <summary>Get Info's Other…: just choose an app (nothing is opened).</summary>
    public static OwApp Pick(string path)
    {
        var w = new AppChooserWindow(path, false, pickOnly: true);
        if (ShellHost.Offscreen) { w.Left = -6000; w.ShowActivated = false; } else w.CenterOnScreen(0.22);   // (off-screen: test mode)
        w.ShowDialog();
        return w._result;
    }

    AppChooserWindow(string path, bool always, bool pickOnly)
    {
        _path = path;
        Title = "Choose Application";
        _recommended = OpenWith.AppsFor(path, recommendedOnly: false).Where(a => a.Recommended).ToList();
        var names = new HashSet<string>(_recommended.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        _all = AppCatalog.Apps.Where(a => !names.Contains(a.Name))
            .Select(a => new OwApp { Key = "app:" + a.ParsingName, Name = a.Name, IconSource = a.IconSource })
            .Concat(OpenWith.AppsFor(path, recommendedOnly: false).Where(a => !a.Recommended && !names.Contains(a.Name)))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var sp = new StackPanel { Width = 420, Margin = new Thickness(20, 18, 20, 16) };
        sp.Children.Add(Text($"Choose an application to open the document “{Path.GetFileName(path)}”.", 13, FontWeights.SemiBold));
        _search.Style = (Style)Application.Current.Resources["MacSearchField"];
        _search.Margin = new Thickness(0, 12, 0, 8);
        _search.TextChanged += (_, _) => Fill();
        sp.Children.Add(_search);
        var box = new Border { Height = 300, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0.5) };
        box.SetResourceReference(Border.BackgroundProperty, "ContentBackgroundBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        box.Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        sp.Children.Add(box);
        _always = new CheckBox { Content = "Always Open With", IsChecked = always, Margin = new Thickness(0, 12, 0, 0), Style = (Style)Application.Current.Resources["MacCheckBox"], Visibility = pickOnly ? Visibility.Collapsed : Visibility.Visible };
        sp.Children.Add(_always);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.Resources["MacButton"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var open = new Button { Content = pickOnly ? "Add" : "Open", Style = (Style)Application.Current.Resources["MacDefaultButton"], IsDefault = true };
        cancel.Click += (_, _) => Close();
        open.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        sp.Children.Add(buttons);
        Card.Child = sp;
        Fill();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            if (ShellHost.Offscreen) return;
            Activate();
            _search.Focus();
        };
    }

    void Accept()
    {
        if (_sel < 0 || _sel >= _rows.Count) return;
        _result = _rows[_sel].app;
        Close();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { Select(Math.Min(_rows.Count - 1, _sel + 1)); e.Handled = true; }
        else if (e.Key == Key.Up) { Select(Math.Max(0, _sel - 1)); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    void Fill()
    {
        string q = _search.Text.Trim();
        bool Match(OwApp a) => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        _list.Children.Clear();
        _rows.Clear();
        void Section(string title, IEnumerable<OwApp> apps)
        {
            var shown = apps.Where(Match).ToList();
            if (shown.Count == 0) return;
            var h = Text(title, 11, FontWeights.SemiBold, "SecondaryLabelBrush");
            h.Margin = new Thickness(10, _list.Children.Count == 0 ? 6 : 10, 0, 3);
            _list.Children.Add(h);
            foreach (var a in shown) _list.Children.Add(Row(a));
        }
        Section("Recommended Applications", _recommended);
        Section("All Applications", _all);
        Select(_rows.Count > 0 ? 0 : -1);
    }

    Border Row(OwApp app)
    {
        var img = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), Source = MacIcons.GenericApp };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (app.Key.StartsWith("internal:")) img.Source = MacIcons.ForInternal(app.Key);
        else if (!string.IsNullOrEmpty(app.IconSource)) ShellIcons.Load(app.IconSource, 32, false, b => { if (b != null) img.Source = b; });
        var name = Text(app.Name, 13);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        var dp = new DockPanel();
        DockPanel.SetDock(img, Dock.Left);
        dp.Children.Add(img);
        dp.Children.Add(name);
        var row = new Border { Height = 26, CornerRadius = new CornerRadius(5), Margin = new Thickness(5, 0, 5, 0), Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Child = dp, Tag = name };
        int index = _rows.Count;
        row.MouseLeftButtonDown += (_, e) =>
        {
            Select(index);
            if (e.ClickCount >= 2) Accept();
            e.Handled = true;
        };
        _rows.Add((app, row));
        return row;
    }

    void Select(int i)
    {
        _sel = i;
        for (int k = 0; k < _rows.Count; k++)
        {
            var (_, row) = _rows[k];
            var t = (TextBlock)row.Tag;
            if (k == i)
            {
                row.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                t.Foreground = Brushes.White;
                row.BringIntoView();
            }
            else
            {
                row.Background = Brushes.Transparent;
                t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            }
        }
    }
}
