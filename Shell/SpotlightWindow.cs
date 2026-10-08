using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>Spotlight search (âŒ˜Space â†’ Win/Alt+Space).</summary>
public class SpotlightWindow : Window
{
    static SpotlightWindow _instance;

    class Result
    {
        public string Title, Subtitle, Group, Path, Kind;
        public ImageSource Icon;
        public string IconSource;
        public bool IconThumb;
        public Action Open, Reveal;
        public FileItem File;
    }

    readonly Border _card = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5) };
    readonly TextBox _box = new();
    readonly TextBlock _placeholder = new() { Text = "Spotlight Search", FontSize = 22, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly Image _queryIcon = new() { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
    readonly Grid _resultsArea = new() { Height = 380, Visibility = Visibility.Collapsed };
    readonly StackPanel _list = new() { Margin = new Thickness(0, 6, 0, 6) };
    readonly ScrollViewer _listScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly Border _preview = new();
    readonly List<(Result r, Border row)> _rows = new();
    List<Result> _results = new();
    int _sel;
    CancellationTokenSource _cts;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(140) };
    const double PanelWidth = 680;
    bool _closing;

    /// <summary>Diagnostics: selected row index/group and list scroll offset (no result titles).</summary>
    public static string Describe() => _instance == null ? "closed" : $"sel={_instance._sel} group={(_instance._sel < _instance._rows.Count ? _instance._rows[_instance._sel].r.Group : "-")} rows={_instance._rows.Count} offset={_instance._listScroll.VerticalOffset:0}";

    public static void Toggle()
    {
        if (_instance != null) { _instance.Close(); return; }
        _instance = new SpotlightWindow();
        _instance.Show();
        _instance.Activate();
        _instance._box.Focus();
    }

    public static void SetQuery(string q)
    {
        if (_instance == null) return;
        _instance._box.Text = q;
        _instance._box.CaretIndex = q.Length;
    }

    SpotlightWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth + 80;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Title = "Spotlight";

        _card.Margin = new Thickness(40, 20, 40, 60);
        _card.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        _card.Background = new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0xF4, 0x2C, 0x2C, 0x2E) : Color.FromArgb(0xF6, 0xF0, 0xF0, 0xF1));
        _card.Effect = new DropShadowEffect { BlurRadius = 50, ShadowDepth = 16, Direction = 270, Opacity = 0.35 };

        var stack = new StackPanel();
        var field = new Grid { Height = 54, Margin = new Thickness(16, 0, 16, 0) };
        var fieldRow = new DockPanel();
        var glass = new SymbolIcon { Symbol = "magnifyingglass", Width = 22, Height = 22, StrokeWidth = 2.0, Margin = new Thickness(0, 0, 10, 0) };
        glass.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        DockPanel.SetDock(glass, Dock.Left);
        fieldRow.Children.Add(glass);
        DockPanel.SetDock(_queryIcon, Dock.Right);
        fieldRow.Children.Add(_queryIcon);
        var boxHost = new Grid();
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _placeholder.FontWeight = FontWeights.Light;
        boxHost.Children.Add(_placeholder);
        _box.FontSize = 22;
        _box.FontWeight = FontWeights.Light;
        _box.BorderThickness = new Thickness(0);
        _box.Background = Brushes.Transparent;
        _box.VerticalAlignment = VerticalAlignment.Center;
        _box.SetResourceReference(TextBox.ForegroundProperty, "LabelBrush");
        _box.SetResourceReference(TextBox.CaretBrushProperty, "LabelBrush");
        _box.FocusVisualStyle = null;
        _box.Template = MakeBareTextBoxTemplate();
        _box.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop(); _debounce.Start();
            QuickResults();
        };
        boxHost.Children.Add(_box);
        fieldRow.Children.Add(boxHost);
        field.Children.Add(fieldRow);
        stack.Children.Add(field);

        var sep = new Border { Height = 1 };
        sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        _resultsArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _resultsArea.RowDefinitions.Add(new RowDefinition());
        _resultsArea.Children.Add(sep);
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        split.ColumnDefinitions.Add(new ColumnDefinition());
        _listScroll.Content = _list;
        split.Children.Add(_listScroll);
        var vsep = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left };
        vsep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        Grid.SetColumn(vsep, 1);
        split.Children.Add(vsep);
        Grid.SetColumn(_preview, 1);
        split.Children.Add(_preview);
        Grid.SetRow(split, 1);
        _resultsArea.Children.Add(split);
        stack.Children.Add(_resultsArea);
        _card.Child = stack;
        Content = _card;

        _debounce.Tick += (_, _) => { _debounce.Stop(); SlowResults(); };
        PreviewKeyDown += OnKey;
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) Close(); };
        Closed += (_, _) => { _cts?.Cancel(); if (_instance == this) _instance = null; };
        Loaded += (_, _) =>
        {
            Left = ShellHost.ScreenPx.Left / ShellHost.Scale + (ShellHost.ScreenDip.Width - ActualWidth) / 2;
            Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.ScreenDip.Height * 0.2 - 20;
        };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW);
            WindowTracker.RegisterChrome(this);
        };
    }

    static ControlTemplate MakeBareTextBoxTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        sv.SetValue(FocusableProperty, false);
        t.VisualTree = sv;
        return t;
    }

    // ------------------------------------------------------------------ searching

    void QuickResults()
    {
        string q = _box.Text.Trim();
        _cts?.Cancel();
        if (q.Length == 0) { _results.Clear(); Render(); return; }
        var list = new List<Result>();

        // calculator
        string calc = TryCalc(q);
        if (calc != null)
            list.Add(new Result
            {
                Title = calc, Subtitle = q + " =", Group = "Calculator", Kind = "Calculator",
                Icon = MacIcons.Tile("plus", "#FF9F0A", "#F07A00"),
                Open = () => { try { Clipboard.SetText(calc); } catch { } Close(); },
            });

        // applications
        var apps = AppCatalog.Apps
            .Select(a => (a, score: Score(a.Name, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.a.Name.Length)
            .Take(8).Select(x => x.a).ToList();
        foreach (var a in apps)
        {
            var app = a;
            list.Add(new Result
            {
                Title = a.Name, Group = "Applications", Kind = "Application", IconSource = a.IconSource, Path = a.TargetPath,
                Subtitle = a.TargetPath,
                Open = () => { AppCatalog.Launch(app.ParsingName); Close(); },
                Reveal = app.TargetPath != null ? () => { ShellHost.RevealInFinder(app.TargetPath); Close(); } : null,
            });
        }

        // built-in shell items and actions
        foreach (var (name, sym, top, bottom, act) in ShellActions())
            if (Score(name, q) > 0)
                list.Add(new Result { Title = name, Group = "System", Kind = "System", Icon = sym == "@finder" ? MacIcons.Finder : sym == "@settings" ? MacIcons.SystemSettings : sym == "@preview" ? MacIcons.Preview : sym == "@mail" ? MacIcons.Mail : sym == "@launchpad" ? MacIcons.Launchpad : MacIcons.Tile(sym, top, bottom), Open = () => { Close(); act(); } });

        // settings panes
        foreach (var pane in SettingsWindow.Panes)
            if (pane.id != "-" && Score(pane.name, q) > 0)
            {
                string id = pane.id;
                list.Add(new Result { Title = pane.name, Group = "System Settings", Kind = "Settings", Icon = SettingsWindow.PaneIcon(pane), Open = () => { Close(); SettingsWindow.ShowPane(id); } });
            }

        // web
        list.Add(new Result
        {
            Title = $"Search the Web for “{q}”", Group = "Web", Kind = "Web Search",
            Icon = MacIcons.Tile("globe", "#5AC8FA", "#1E88E5"),
            Open = () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true }); } catch { } Close(); },
        });

        // Top Hit = best non-web result
        var top1 = list.FirstOrDefault(r => r.Group is "Applications" or "Calculator") ?? list.FirstOrDefault(r => r.Group != "Web");
        if (top1 != null) { list.Remove(top1); top1.Group = "Top Hit"; list.Insert(0, top1); }
        _results = list;
        _sel = 0;
        Render();
    }

    void SlowResults()
    {
        string q = _box.Text.Trim();
        if (q.Length < 2) return;
        var cts = _cts = new CancellationTokenSource();
        Task.Run(() => FinderWindow.SearchIndex(q, 150), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested || _box.Text.Trim() != q) return;
                var files = t.Result.Where(f => !Regex.IsMatch(f.FullPath, @"\\(node_modules|\.git|obj|bin|\.vs|\.next|dist|build|__pycache__|site-packages|AppData)\\", RegexOptions.IgnoreCase)).ToList();
                var folders = files.Where(f => f.IsFolder).Take(5).ToList();
                var docs = files.Where(f => !f.IsFolder).OrderByDescending(f => Score(f.Name, q)).ThenByDescending(f => f.Modified).Take(10).ToList();
                int insertAt = _results.FindIndex(r => r.Group == "Web");
                if (insertAt < 0) insertAt = _results.Count;
                var extra = new List<Result>();
                foreach (var f in docs) extra.Add(FileResult(f, "Documents"));
                foreach (var f in folders) extra.Add(FileResult(f, "Folders"));
                _results.InsertRange(insertAt, extra);
                if (extra.Count > 0 && !_results.Any(r => r.Group == "Top Hit"))
                {
                    var t0 = extra[0];
                    _results.Remove(t0);
                    t0.Group = "Top Hit";
                    _results.Insert(0, t0);
                }
                // Keep a row the user moved to; otherwise the (new) Top Hit stays selected, like macOS.
                var keep = _sel > 0 && _sel < _rows.Count ? _rows[_sel].r : null;
                _sel = 0;
                Render();
                if (keep != null) { int i = _results.IndexOf(keep); if (i >= 0) Select(i); }
            });
        });
    }

    Result FileResult(FileItem f, string group) => new()
    {
        Title = f.Name, Group = group, Kind = f.Kind, Path = f.FullPath, File = f,
        Subtitle = Path.GetDirectoryName(f.FullPath),
        IconSource = f.IsFolder ? null : f.FullPath, IconThumb = ShellIcons.IsThumbnailType(f.FullPath),
        Icon = f.IsFolder ? MacIcons.Folder(MacIcons.FolderGlyphFor(f.FullPath)) : null,
        Open = () => { if (f.IsFolder) ShellHost.OpenFinder(f.FullPath); else AppCatalog.OpenFile(f.FullPath); Close(); },
        Reveal = () => { ShellHost.RevealInFinder(f.FullPath); Close(); },
    };

    static int Score(string name, string q)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (name.Equals(q, StringComparison.CurrentCultureIgnoreCase)) return 100;
        if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 80;
        var words = Regex.Split(name, @"[\s\-_.]+");
        if (words.Any(w => w.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))) return 60;
        string initials = string.Concat(words.Where(w => w.Length > 0).Select(w => w[0]));
        if (q.Length >= 2 && initials.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 50;
        if (name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return 30;
        return 0;
    }

    static IEnumerable<(string name, string sym, string top, string bottom, Action act)> ShellActions()
    {
        yield return ("Finder", "@finder", "", "", () => ShellHost.OpenFinder(null));
        yield return ("System Settings", "@settings", "", "", () => SettingsWindow.ShowPane(null));
        yield return ("Preview", "@preview", "", "", Apps.Preview.PreviewWindow.OpenApp);
        yield return ("Mail", "@mail", "", "", Apps.Mail.MailWindow.OpenApp);
        yield return ("Launchpad", "@launchpad", "", "", LaunchpadWindow.Toggle);
        yield return ("Mission Control", "rectangle.stack", "#5E5CE6", "#3634A3", MissionControlWindow.Toggle);
        yield return ("Trash", "trash", "#8E8E93", "#636366", () => ShellHost.OpenFinder(FinderLocation.Trash));
        yield return ("Empty Trash", "trash", "#8E8E93", "#636366", () => FileOps.EmptyTrash());
        yield return ("Force Quit Applications", "xmark", "#FF453A", "#C4231A", ForceQuitWindow.ShowWindow);
        yield return ("About This Mac", "info", "#8E8E93", "#636366", AboutWindow.ShowWindow);
        yield return ("Lock Screen", "lock", "#1C1C1E", "#000000", ShellHost.Lock);
        yield return ("Sleep", "moon", "#5E5CE6", "#3634A3", ShellHost.Sleep);
        yield return ("Restart", "arrow.clockwise", "#8E8E93", "#636366", ShellHost.ConfirmRestart);
        yield return ("Shut Down", "power", "#8E8E93", "#636366", ShellHost.ConfirmShutDown);
        yield return ("Log Out", "person.circle", "#8E8E93", "#636366", ShellHost.ConfirmLogOut);
        yield return ("Screenshot", "display", "#8E8E93", "#636366", () => Screenshot.CaptureArea());
        yield return ("Return to Windows", "xmark", "#0A84FF", "#0060DF", ShellHost.ConfirmExitToWindows);
        yield return ("Downloads", "arrow.down.circle", "#5AC8FA", "#1E88E5", () => ShellHost.OpenFinder(FinderLocation.Downloads));
        yield return ("Documents", "doc", "#5AC8FA", "#1E88E5", () => ShellHost.OpenFinder(FinderLocation.Documents));
        yield return ("Desktop", "desktop", "#5AC8FA", "#1E88E5", () => ShellHost.OpenFinder(FinderLocation.Desktop));
        yield return ("Applications", "apps", "#5AC8FA", "#1E88E5", () => ShellHost.OpenFinder(FinderLocation.Applications));
    }

    static string TryCalc(string q)
    {
        string expr = q.Replace("×", "*").Replace("÷", "/").Replace("x", "*").Replace(",", "");
        if (!Regex.IsMatch(expr, @"^[\d\s\.\+\-\*/\(\)%\^]+$") || !Regex.IsMatch(expr, @"\d\s*[\+\-\*/%\^]\s*[\d\(]")) return null;
        try
        {
            expr = Regex.Replace(expr, @"(\d+(\.\d+)?)\s*\^\s*(\d+(\.\d+)?)", m => Math.Pow(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
            var v = new DataTable().Compute(expr, null);
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            return Math.Abs(d - Math.Round(d)) < 1e-10 ? Math.Round(d).ToString("#,0", CultureInfo.CurrentCulture) : d.ToString("#,0.##########", CultureInfo.CurrentCulture);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ rendering

    void Render()
    {
        _list.Children.Clear();
        _listScroll.ScrollToTop();   // a new result list starts at the top, not at the previous scroll position
        _rows.Clear();
        _resultsArea.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string lastGroup = null;
        foreach (var r in _results)
        {
            if (r.Group != lastGroup)
            {
                lastGroup = r.Group;
                var h = new TextBlock { Text = r.Group, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, _list.Children.Count == 0 ? 2 : 10, 0, 3) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
                _list.Children.Add(h);
            }
            var row = MakeRow(r);
            _rows.Add((r, row));
            _list.Children.Add(row);
        }
        if (_rows.Count > 0) Select(Math.Clamp(_sel, 0, _rows.Count - 1));
        else { _preview.Child = null; _queryIcon.Visibility = Visibility.Collapsed; }
    }

    Border MakeRow(Result r)
    {
        var dp = new DockPanel();
        var img = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), Source = r.Icon };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (r.Icon == null && r.IconSource != null)
            ShellIcons.Load(r.IconSource, r.IconThumb ? 128 : 48, r.IconThumb, b => { if (b != null) { img.Source = b; r.Icon = b; if (_rows.Count > _sel && _rows[_sel].r == r) Select(_sel); } }, true);
        DockPanel.SetDock(img, Dock.Left);
        dp.Children.Add(img);
        var t = new TextBlock { Text = r.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        dp.Children.Add(t);
        var row = new Border { Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(8, 0, 8, 0), Child = dp, Background = Brushes.Transparent, Tag = t };
        row.MouseLeftButtonDown += (_, e) =>
        {
            int i = _rows.FindIndex(x => x.row == row);
            if (e.ClickCount >= 2) r.Open?.Invoke(); else Select(i);
        };
        return row;
    }

    void Select(int i)
    {
        if (i < 0 || i >= _rows.Count) return;
        _sel = i;
        for (int k = 0; k < _rows.Count; k++)
        {
            var (r, row) = _rows[k];
            var t = (TextBlock)row.Tag;
            if (k == i)
            {
                row.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                t.Foreground = Brushes.White;
            }
            else
            {
                row.Background = Brushes.Transparent;
                t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            }
        }
        var shown = _rows[i].row;
        Dispatcher.BeginInvoke(() => shown.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);   // after layout
        var sel = _rows[i].r;
        _queryIcon.Source = sel.Icon;
        _queryIcon.Visibility = sel.Icon != null ? Visibility.Visible : Visibility.Collapsed;
        _preview.Child = BuildPreview(sel);
    }

    UIElement BuildPreview(Result r)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        var img = new Image { Width = 128, Height = 128, Source = r.Icon, Margin = new Thickness(0, 0, 0, 14) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (r.File != null && !r.File.IsFolder)
            ShellIcons.Load(r.File.FullPath, 256, ShellIcons.IsThumbnailType(r.File.FullPath), b => { if (b != null) img.Source = b; }, true);
        else if (r.IconSource != null && r.Kind == "Application")
            ShellIcons.Load(r.IconSource, 256, false, b => { if (b != null) img.Source = b; }, true, "dock:" + r.IconSource);
        sp.Children.Add(img);
        var title = new TextBlock { Text = r.Title, FontSize = r.Group == "Calculator" || (r.Group == "Top Hit" && r.Kind == "Calculator") ? 30 : 17, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(title);
        var kind = new TextBlock { Text = r.Kind, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        sp.Children.Add(kind);
        if (r.File != null)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 10), Width = 300 };
            sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
            sp.Children.Add(sep);
            void Row(string k, string v)
            {
                var g = new Grid { Width = 300, Margin = new Thickness(0, 1, 0, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var a = new TextBlock { Text = k, FontSize = 11, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
                a.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
                var b = new TextBlock { Text = v, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                b.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
                Grid.SetColumn(b, 1);
                g.Children.Add(a); g.Children.Add(b);
                sp.Children.Add(g);
            }
            if (!r.File.IsFolder) Row("Size", r.File.SizeText);
            Row("Modified", r.File.ModifiedText);
            Row("Where", Path.GetDirectoryName(r.File.FullPath) ?? "");
        }
        else if (!string.IsNullOrEmpty(r.Subtitle))
        {
            var s = new TextBlock { Text = r.Subtitle, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 8, 0, 0) };
            s.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
            sp.Children.Add(s);
        }
        return sp;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_box.Text.Length > 0) _box.Text = ""; else Close();
                e.Handled = true; break;
            case Key.Down: Select(Math.Min(_sel + 1, _rows.Count - 1)); e.Handled = true; break;
            case Key.Up: Select(Math.Max(_sel - 1, 0)); e.Handled = true; break;
            case Key.Enter:
                if (_sel < _rows.Count)
                {
                    var r = _rows[_sel].r;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && r.Reveal != null) r.Reveal(); else r.Open?.Invoke();
                }
                e.Handled = true; break;
            case Key.Tab:
                Select(_sel + 1 < _rows.Count ? _sel + 1 : 0); e.Handled = true; break;
        }
    }
}
