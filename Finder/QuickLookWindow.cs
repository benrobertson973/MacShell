using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Finder;

/// <summary>Quick Look: spacebar preview of images, text, media and anything with a thumbnail.</summary>
public class QuickLookWindow : MacWindow
{
    public static QuickLookWindow Current { get; private set; }
    readonly FinderWindow _owner;
    readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(120, 0, 160, 0) };
    readonly Border _body = new();
    readonly Button _openWith = new();
    FileItem _item;
    MediaElement _media;

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".avif" };
    static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac" };
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".json", ".xml", ".yml", ".yaml", ".toml", ".csv", ".tsv", ".cs", ".xaml", ".csproj",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".html", ".htm", ".css", ".scss", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".rs", ".go", ".rb",
        ".php", ".swift", ".sh", ".bat", ".cmd", ".ps1", ".sql", ".lua", ".gitignore", ".editorconfig", ".env", ".svg", ".reg", ".vue", ".dart"
    };

    QuickLookWindow(FinderWindow owner) : base(WindowTracker.FinderKey, 44)
    {
        _owner = owner;
        UseVibrancy = false;
        Title = "Quick Look";
        MinWidth = 320; MinHeight = 240;
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var bar = new Grid { Height = 44 };
        bar.SetResourceReference(Panel.BackgroundProperty, "ToolbarBackgroundBrush");
        var lights = new TrafficLights { Margin = new Thickness(16, 0, 0, 0) };
        bar.Children.Add(lights);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        bar.Children.Add(_title);
        _openWith.Style = (Style)Application.Current.Resources["MacButton"];
        _openWith.HorizontalAlignment = HorizontalAlignment.Right;
        _openWith.Margin = new Thickness(0, 0, 14, 0);
        WindowChrome.SetIsHitTestVisibleInChrome(_openWith, true);
        _openWith.Click += (_, _) => { if (_item != null) _owner?.Open(_item); };
        bar.Children.Add(_openWith);
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        bar.Children.Add(line);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_body);
        Content = root;
        Closed += (_, _) => { _media?.Stop(); _media?.Close(); if (Current == this) Current = null; _owner?.Activate(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { _owner?.QuickLookStep(e.Key); e.Handled = true; }
        };
    }

    public static void Toggle(FinderWindow owner, FileItem item)
    {
        if (Current != null) { Current.Close(); return; }
        if (item == null) return;
        var w = new QuickLookWindow(owner);
        Current = w;
        w.ShowItem(item, true);
        w.Show();
        w.Activate();
    }

    public void ShowItem(FileItem item, bool first = false)
    {
        if (item == null) { Close(); return; }
        _item = item;
        _title.Text = item.DisplayName;
        _media?.Stop(); _media?.Close(); _media = null;
        string app = item.IsFolder ? "Finder" : DefaultAppName(item.Extension);
        _openWith.Content = item.IsApp || app == null ? "Open" : $"Open with {app}";
        var wa = SystemParameters.WorkArea;
        double w = 760, h = 560;
        string ext = item.Extension;
        if (!item.IsFolder && ImageExt.Contains(ext)) BuildImage(item, ref w, ref h, wa);
        else if (!item.IsFolder && MediaExt.Contains(ext)) BuildMedia(item);
        else if (!item.IsFolder && (TextExt.Contains(ext) || LooksLikeText(item.FullPath))) BuildText(item);
        else BuildGeneric(item);
        if (first)
        {
            Width = w; Height = h;
            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Top + (wa.Height - h) / 2;
            ApplyOffscreen();
        }
    }

    void BuildImage(FileItem item, ref double w, ref double h, Rect wa)
    {
        var img = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var grid = new Grid();
        grid.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        grid.Children.Add(img);
        _body.Child = grid;
        try
        {
            var dec = BitmapDecoder.Create(new Uri(item.FullPath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var f = dec.Frames[0];
            double iw = f.PixelWidth, ih = f.PixelHeight;
            double scale = Math.Min(1, Math.Min(wa.Width * 0.8 / iw, (wa.Height * 0.85 - 44) / ih));
            w = Math.Max(360, iw * scale); h = Math.Max(260, ih * scale + 44);
        }
        catch { }
        string path = item.FullPath;
        img.Source = item.Thumbnail;
        Task.Run(() =>
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 2400;
                bi.EndInit();
                bi.Freeze();
                return (BitmapSource)bi;
            }
            catch { return null; }
        }).ContinueWith(t => Dispatcher.BeginInvoke(() => { if (t.Result != null && _item?.FullPath == path) img.Source = t.Result; }));
    }

    void BuildMedia(FileItem item)
    {
        var grid = new Grid { Background = Brushes.Black };
        _media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Source = new Uri(item.FullPath), Stretch = Stretch.Uniform };
        grid.Children.Add(_media);
        var controls = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 44, Background = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20)), CornerRadius = new CornerRadius(10), Margin = new Thickness(40, 0, 40, 16) };
        var cg = new Grid { Margin = new Thickness(12, 0, 16, 0) };
        cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cg.ColumnDefinitions.Add(new ColumnDefinition());
        var play = new TextBlock { Text = "❚❚", Foreground = Brushes.White, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0), Cursor = Cursors.Hand };
        bool playing = true;
        play.MouseLeftButtonUp += (_, _) => { if (playing) _media.Pause(); else _media.Play(); playing = !playing; play.Text = playing ? "❚❚" : "▶"; };
        cg.Children.Add(play);
        var slider = new Slider { Style = (Style)Application.Current.Resources["MacSlider"], VerticalAlignment = VerticalAlignment.Center, Maximum = 1 };
        Grid.SetColumn(slider, 1);
        cg.Children.Add(slider);
        controls.Child = cg;
        grid.Children.Add(controls);
        bool dragging = false;
        slider.PreviewMouseDown += (_, _) => dragging = true;
        slider.PreviewMouseUp += (_, _) =>
        {
            dragging = false;
            if (_media.NaturalDuration.HasTimeSpan) _media.Position = TimeSpan.FromSeconds(slider.Value * _media.NaturalDuration.TimeSpan.TotalSeconds);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (_media == null) { timer.Stop(); return; }
            if (!dragging && _media.NaturalDuration.HasTimeSpan && _media.NaturalDuration.TimeSpan.TotalSeconds > 0)
                slider.Value = _media.Position.TotalSeconds / _media.NaturalDuration.TimeSpan.TotalSeconds;
        };
        timer.Start();
        _body.Child = grid;
        _media.Play();
    }

    void BuildText(FileItem item)
    {
        string text;
        try
        {
            using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(fs.Length, 400_000)];
            int n = fs.Read(buf, 0, buf.Length);
            text = Encoding.UTF8.GetString(buf, 0, n);
            if (fs.Length > buf.Length) text += "\n\n…";
        }
        catch (Exception ex) { text = ex.Message; }
        bool prose = item.Extension is ".txt" or ".md" or ".markdown" or ".log";
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(24, 18, 24, 18),
            FontFamily = prose ? Theme.Font : Theme.MonoFont, FontSize = prose ? 14 : 12.5,
            TextWrapping = prose ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
        };
        tb.SetResourceReference(TextBox.ForegroundProperty, "LabelBrush");
        var host = new Border { Child = tb };
        host.SetResourceReference(Border.BackgroundProperty, "ContentBackgroundBrush");
        _body.Child = host;
    }

    void BuildGeneric(FileItem item)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var img = new Image { Width = 256, Height = 256, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(FileItem.Thumbnail)) { Source = item });
        sp.Children.Add(img);
        var name = new TextBlock { Text = item.DisplayName, FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 4) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(name);
        string detail = item.Kind;
        if (item.IsFolder)
        {
            try { detail += " – " + Directory.EnumerateFileSystemEntries(item.FullPath).Count() + " items"; } catch { }
        }
        else if (item.Size >= 0) detail += " – " + item.SizeText;
        var d = new TextBlock { Text = detail, HorizontalAlignment = HorizontalAlignment.Center };
        d.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        sp.Children.Add(d);
        var m = new TextBlock { Text = "Modified: " + item.ModifiedText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        m.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        sp.Children.Add(m);
        var host = new Border { Child = sp };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
        _body.Child = host;
    }

    static bool LooksLikeText(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 2_000_000 || fi.Length == 0) return false;
            using var fs = fi.OpenRead();
            var buf = new byte[Math.Min(4096, fi.Length)];
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] == 0) return false;
            return true;
        }
        catch { return false; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outStr, ref uint outLen);

    public static string DefaultAppName(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return null;
        try
        {
            uint len = 256;
            var sb = new StringBuilder((int)len);
            if (AssocQueryString(0, 4 /*FRIENDLYAPPNAME*/, ext, null, sb, ref len) == 0)
            {
                string s = sb.ToString();
                if (s.Contains("Pick an app", StringComparison.OrdinalIgnoreCase) || s.Contains("Pick an application", StringComparison.OrdinalIgnoreCase) || s.Contains("OpenWith", StringComparison.OrdinalIgnoreCase)) return null;
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { }
        return null;
    }
}
