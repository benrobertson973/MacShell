using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Finder;

/// <summary>Finder's "Get Info" inspector window.</summary>
public class GetInfoWindow : MacWindow
{
    readonly FileItem _item;
    readonly StackPanel _stack = new() { Margin = new Thickness(16, 6, 16, 16) };
    TextBlock _sizeValue, _headerSize;
    CancellationTokenSource _cts;

    public static void ShowFor(FileItem item)
    {
        if (item == null) return;
        var w = new GetInfoWindow(item);
        w.Show();
        if (!ShellHost.Offscreen) w.Activate();
    }

    GetInfoWindow(FileItem item) : base(WindowTracker.FinderKey, 28)
    {
        _item = item;
        UseVibrancy = false;
        Width = 290;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Title = $"{item.DisplayName} Info";
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        var bar = new Grid { Height = 28 };
        var lights = new TrafficLights { Margin = new Thickness(9, 0, 0, 0), CanZoom = false };
        bar.Children.Add(lights);
        var t = new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(70, 0, 20, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        bar.Children.Add(t);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60, Focusable = false });
        Content = root;
        Build();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 30 - (FinderWindow.All.Count % 5) * 20;
        Top = wa.Top + 30;
        ApplyOffscreen();
        Closed += (_, _) => _cts?.Cancel();
    }

    TextBlock L(string s, double size = 11, string brush = "LabelBrush", FontWeight? w = null)
    {
        var tb = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = w ?? FontWeights.Normal };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    void Build()
    {
        var it = _item;
        // header
        var head = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 36, Height = 36, Source = it.Icon, Margin = new Thickness(0, 0, 8, 0) };
        head.Children.Add(icon);
        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(L(it.DisplayName, 13, w: FontWeights.SemiBold));
        nameStack.Children.Add(L("Modified: " + it.ModifiedText, 10.5, "SecondaryLabelBrush"));
        Grid.SetColumn(nameStack, 1);
        head.Children.Add(nameStack);
        _headerSize = L(it.IsFolder ? "--" : it.SizeText, 12, "SecondaryLabelBrush");
        _headerSize.VerticalAlignment = VerticalAlignment.Top;
        _headerSize.Margin = new Thickness(6, 2, 0, 0);
        Grid.SetColumn(_headerSize, 2);
        head.Children.Add(_headerSize);
        _stack.Children.Add(head);

        // tags field
        var tagBox = new Border { Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0.5), Margin = new Thickness(0, 6, 0, 8), Padding = new Thickness(6, 0, 6, 0), Cursor = Cursors.IBeam };
        tagBox.SetResourceReference(Border.BackgroundProperty, "TextFieldBrush");
        tagBox.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var tagPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void RefreshTags()
        {
            tagPanel.Children.Clear();
            it.RefreshTags();
            if (it.Tags.Count == 0) tagPanel.Children.Add(L("Add Tags…", 12, "TertiaryLabelBrush"));
            foreach (var tag in it.Tags)
            {
                var tc = Theme.TagColors.FirstOrDefault(x => x.id == tag);
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
                chip.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(tc.color), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                chip.Children.Add(L(tc.name, 12));
                tagPanel.Children.Add(chip);
            }
        }
        RefreshTags();
        tagBox.Child = tagPanel;
        tagBox.MouseLeftButtonUp += (_, _) =>
        {
            var cm = Mb.Context(Mb.TagRow(it.Tags, tag => { FileOps.SetTag(new[] { it.FullPath }, tag); RefreshTags(); }));
            cm.PlacementTarget = tagBox;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        };
        if (!it.IsApp && !it.IsTrashItem) _stack.Children.Add(tagBox);

        // General
        var general = new StackPanel();
        Row(general, "Kind:", it.Kind);
        _sizeValue = Row(general, "Size:", it.IsFolder || it.IsDrive ? "Calculating…" : SizeDetail(it.Size));
        string where = it.IsTrashItem ? it.OriginalLocation : it.IsApp ? AppCatalog.FindByParsingName(it.AppTarget)?.TargetPath ?? "Start menu" : Path.GetDirectoryName(it.FullPath?.TrimEnd('\\') ?? "");
        Row(general, "Where:", where ?? "--");
        Row(general, "Created:", it.CreatedText);
        Row(general, "Modified:", it.ModifiedText);
        if (it.IsDrive)
        {
            try
            {
                var d = new DriveInfo(it.FullPath);
                Row(general, "Capacity:", FileItem.FormatSize(d.TotalSize));
                Row(general, "Available:", FileItem.FormatSize(d.AvailableFreeSpace));
                Row(general, "Used:", FileItem.FormatSize(d.TotalSize - d.TotalFreeSpace));
                Row(general, "Format:", d.DriveFormat);
            }
            catch { }
        }
        if (!it.IsApp && !it.IsTrashItem && File.Exists(it.FullPath))
        {
            var locked = new CheckBox { Content = "Locked", Style = (Style)Application.Current.Resources["MacCheckBox"], FontSize = 11, Margin = new Thickness(78, 6, 0, 0) };
            try { locked.IsChecked = new FileInfo(it.FullPath).IsReadOnly; } catch { }
            locked.Click += (_, _) => { try { new FileInfo(it.FullPath).IsReadOnly = locked.IsChecked == true; } catch { } };
            general.Children.Add(locked);
        }
        Section("General", general, true);

        // More info
        var more = new StackPanel();
        if (!it.IsFolder && !it.IsApp && QuickLookImage(it.Extension))
        {
            try
            {
                var dec = BitmapDecoder.Create(new Uri(it.FullPath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                Row(more, "Dimensions:", $"{dec.Frames[0].PixelWidth} × {dec.Frames[0].PixelHeight}");
            }
            catch { }
        }
        if (File.Exists(it.FullPath))
        {
            try { Row(more, "Last opened:", FileItem.FormatDate(File.GetLastAccessTime(it.FullPath))); } catch { }
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(it.FullPath);
                if (!string.IsNullOrEmpty(vi.FileVersion)) Row(more, "Version:", vi.ProductVersion ?? vi.FileVersion);
                if (!string.IsNullOrEmpty(vi.CompanyName)) Row(more, "Company:", vi.CompanyName);
            }
            catch { }
        }
        if (more.Children.Count > 0) Section("More Info", more, false);

        // Name & Extension
        if (!it.IsApp && !it.IsTrashItem && !it.IsDrive)
        {
            var nameSec = new StackPanel();
            var box = new TextBox { Style = (Style)Application.Current.Resources["MacTextField"], Text = it.Name, FontSize = 12 };
            void Commit()
            {
                if (box.Text == it.Name) return;
                string r = FileOps.Rename(it.FullPath, box.Text);
                if (r != null) { it.FullPath = r; it.Name = Path.GetFileName(r.TrimEnd('\\')); Title = $"{it.DisplayName} Info"; }
                else box.Text = it.Name;
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            box.LostKeyboardFocus += (_, _) => Commit();
            nameSec.Children.Add(box);
            var hide = new CheckBox { Content = "Hide extension", Style = (Style)Application.Current.Resources["MacCheckBox"], FontSize = 11, Margin = new Thickness(0, 8, 0, 0), IsChecked = !Settings.Current.FinderShowExtensions };
            hide.Click += (_, _) => { Settings.Current.FinderShowExtensions = hide.IsChecked != true; Settings.Save(); foreach (var w in FinderWindow.All) w.Reload(true); };
            nameSec.Children.Add(hide);
            Section("Name & Extension", nameSec, true);
        }

        // Open with
        if (!it.IsFolder && !it.IsApp && !string.IsNullOrEmpty(it.Extension))
        {
            // like macOS: the popup picks the app for this document; Change All… makes it the default for the kind
            var ow = new StackPanel();
            var pick = new Button { Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 0, 6, 0) };
            OwApp current = OpenWith.DefaultFor(it.FullPath);
            void ShowCurrent()
            {
                var img = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), Source = MacIcons.GenericApp };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                if (current?.Key.StartsWith("internal:") == true) img.Source = MacIcons.ForInternal(current.Key);
                else if (!string.IsNullOrEmpty(current?.IconSource)) ShellIcons.Load(current.IconSource, 32, false, b => { if (b != null) img.Source = b; });
                var dp = new DockPanel();
                var chev = new SymbolIcon { Symbol = "chevron.updown", Width = 9, Height = 11, StrokeWidth = 1.6, Margin = new Thickness(6, 0, 0, 0) };
                chev.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
                DockPanel.SetDock(img, Dock.Left);
                DockPanel.SetDock(chev, Dock.Right);
                dp.Children.Add(img);
                dp.Children.Add(chev);
                dp.Children.Add(new TextBlock { Text = current == null ? "No application" : current.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                pick.Content = dp;
                dp.Width = Math.Max(0, pick.ActualWidth - 14);   // (icon and name on the left, arrows at the right edge)
                pick.SizeChanged += (_, _) => dp.Width = Math.Max(0, pick.ActualWidth - 14);
            }
            ShowCurrent();
            pick.Click += (_, _) =>
            {
                var items = new List<object>();
                foreach (var a in OpenWith.AppsFor(it.FullPath, recommendedOnly: true).Append(current).Where(a => a != null)
                             .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var app = a;
                    items.Add(Mb.Item(app.Name, () => { OpenWith.SetDefaultForFile(it.FullPath, app); current = app; ShowCurrent(); }, isChecked: current?.Name == app.Name));
                }
                items.Add(Mb.Sep());
                items.Add(Mb.Item("Other…", () =>
                {
                    var app = AppChooserWindow.Pick(it.FullPath);
                    if (app == null) return;
                    OpenWith.SetDefaultForFile(it.FullPath, app);
                    current = app;
                    ShowCurrent();
                }));
                var cm = Mb.Context(items.ToArray());
                cm.PlacementTarget = pick;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                cm.IsOpen = true;
            };
            ow.Children.Add(pick);
            ow.Children.Add(L($"Use this application to open all documents like this one.", 11, "SecondaryLabelBrush"));
            ((FrameworkElement)ow.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
            var change = new Button { Content = "Change All…", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            change.Click += (_, _) =>
            {
                if (current == null) return;
                if (ShellHost.Alert($"Are you sure you want to change all similar documents to open with the application “{current.Name}”?",
                        $"This change will apply to all documents with extension “{it.Extension.TrimStart('.')}”.", "Cancel", "Continue") == "Continue")
                    OpenWith.SetDefaultForKind(it.FullPath, current);
            };
            ow.Children.Add(change);
            var winLink = new TextBlock { Text = "Windows Default…", FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand, ToolTip = "Choose the app Windows itself uses for files like this (outside MacShell)" };
            winLink.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            winLink.MouseLeftButtonUp += (_, _) => AppCatalog.OpenWith(it.FullPath);
            ow.Children.Add(winLink);
            Section("Open with:", ow, true);
        }

        // Preview
        var prev = new Image { Height = 150, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 4) };
        prev.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(FileItem.Thumbnail)) { Source = it });
        Section("Preview", prev, true);

        // Sharing & Permissions
        var perm = new StackPanel();
        bool ro = false;
        try { ro = File.Exists(it.FullPath) && new FileInfo(it.FullPath).IsReadOnly; } catch { }
        perm.Children.Add(L(ro ? "You can only read" : "You can read and write", 11, "SecondaryLabelBrush"));
        var owner = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        owner.ColumnDefinitions.Add(new ColumnDefinition());
        owner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        owner.Children.Add(L(Environment.UserName + " (Me)", 11));
        var priv = L(ro ? "Read only" : "Read & Write", 11);
        Grid.SetColumn(priv, 1);
        owner.Children.Add(priv);
        perm.Children.Add(owner);
        var winProps = new Button { Content = "Windows Properties…", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        winProps.Click += (_, _) => FileOps.ShowWindowsProperties(it.FullPath);
        if (!it.IsApp && !it.IsTrashItem) perm.Children.Add(winProps);
        Section("Sharing & Permissions:", perm, false);

        if (it.IsFolder || it.IsDrive) ComputeFolderSize();
    }

    static bool QuickLookImage(string ext) => ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico" or ".webp";

    static string SizeDetail(long bytes) => bytes < 0 ? "--" : $"{bytes:N0} bytes ({FileItem.FormatSize(bytes)} on disk)";

    TextBlock Row(Panel parent, string key, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var k = L(key, 11, "SecondaryLabelBrush", FontWeights.SemiBold);
        k.TextAlignment = TextAlignment.Right;
        k.Margin = new Thickness(0, 0, 6, 0);
        g.Children.Add(k);
        var v = L(value, 11);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        parent.Children.Add(g);
        return v;
    }

    void Section(string title, UIElement body, bool expanded)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        _stack.Children.Add(line);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 4) };
        var chev = new SymbolIcon { Symbol = "chevron.right", Width = 9, Height = 9, StrokeWidth = 3, RenderTransformOrigin = new Point(0.5, 0.5), Margin = new Thickness(0, 0, 6, 0) };
        chev.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        chev.RenderTransform = new RotateTransform(expanded ? 90 : 0);
        header.Children.Add(chev);
        header.Children.Add(L(title, 12, w: FontWeights.SemiBold));
        var host = new Border { Child = body, Margin = new Thickness(4, 0, 0, 4), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
        header.MouseLeftButtonUp += (_, _) =>
        {
            bool open = host.Visibility != Visibility.Visible;
            host.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chev.RenderTransform = new RotateTransform(open ? 90 : 0);
        };
        _stack.Children.Add(header);
        _stack.Children.Add(host);
    }

    void ComputeFolderSize()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string path = _item.FullPath;
        if (_item.IsDrive)
        {
            try
            {
                var d = new DriveInfo(path);
                _sizeValue.Text = $"{FileItem.FormatSize(d.TotalSize - d.TotalFreeSpace)} used of {FileItem.FormatSize(d.TotalSize)}";
                _headerSize.Text = FileItem.FormatSize(d.TotalSize);
            }
            catch { }
            return;
        }
        Task.Run(() =>
        {
            long total = 0; int count = 0;
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
                {
                    if (ct.IsCancellationRequested) return;
                    try { total += f.Length; count++; } catch { }
                    if (count % 2000 == 0)
                    {
                        long t = total; int c = count;
                        Dispatcher.BeginInvoke(() => _sizeValue.Text = $"{t:N0} bytes for {c:N0} items…");
                    }
                }
            }
            catch { }
            Dispatcher.BeginInvoke(() =>
            {
                _sizeValue.Text = $"{total:N0} bytes ({FileItem.FormatSize(total)} on disk) for {count:N0} items";
                _headerSize.Text = FileItem.FormatSize(total);
            });
        }, ct);
    }
}
