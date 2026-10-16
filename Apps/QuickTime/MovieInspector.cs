using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Services;

namespace MacShell.Apps.QuickTime;

/// <summary>QuickTime's Movie Inspector (⌘I): a dark floating panel with the movie's source, format, size, frame rate,
/// data rate and length - from the file's Windows properties.</summary>
public static class MovieInspector
{
    static Window _open;

    public static void Show(Window owner, string path, MediaElement media)
    {
        _open?.Close();
        var rows = new List<(string, string)> { ("Source", path) };
        var length = Duration(path);
        if (length <= TimeSpan.Zero && media.NaturalDuration.HasTimeSpan) length = media.NaturalDuration.TimeSpan;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(path));
            dynamic item = folder?.ParseName(Path.GetFileName(path));
            object P(string name) { try { return item?.ExtendedProperty(name); } catch { return null; } }
            string codec = P("System.Video.Compression") as string;
            string audio = P("System.Audio.Format") as string;
            rows.Add(("Format", string.Join(", ", new[] { CodecName(codec), audio != null ? "Audio" : null }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } f ? f : Path.GetExtension(path).TrimStart('.').ToUpperInvariant()));
            if (P("System.Video.FrameRate") is uint fr && fr > 0) rows.Add(("FPS", (fr / 1000.0).ToString("0.##", CultureInfo.CurrentCulture)));
            long bytes = new FileInfo(path).Length;
            rows.Add(("Data Size", Size(bytes)));
            if (length.TotalSeconds > 0)
                rows.Add(("Data Rate", (bytes * 8 / length.TotalSeconds / 1_000_000).ToString("0.##", CultureInfo.CurrentCulture) + " Mbit/s"));
        }
        catch { }
        if (media.NaturalVideoWidth > 0) rows.Add(("Current Size", $"{media.NaturalVideoWidth} × {media.NaturalVideoHeight}"));
        if (length > TimeSpan.Zero) rows.Add(("Duration", length.ToString(length.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)));

        var w = new Window
        {
            Title = Path.GetFileName(path) + " Inspector", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Topmost = true, FontFamily = Theme.Font, FontSize = 12, Owner = owner,
        };
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 16, 14), Margin = new Thickness(16), Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x22, 0x24)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5) };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.4 };
        var sp = new StackPanel { Width = 300 };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = new TextBlock { Text = "✕", Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Cursor = Cursors.Hand, FontSize = 11 };
        close.MouseLeftButtonUp += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = Path.GetFileName(path), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(head);
        foreach (var (label, value) in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0) });
            var v = new TextBlock { Text = value, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            sp.Children.Add(g);
        }
        card.Child = sp;
        w.Content = card;
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch { } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Loaded += (_, _) =>
        {
            if (owner != null) { w.Left = owner.Left + owner.ActualWidth + 8; w.Top = owner.Top; }
            var wa = SystemParameters.WorkArea;
            if (w.Left + w.ActualWidth > wa.Right) w.Left = Math.Max(wa.Left, wa.Right - w.ActualWidth);
        };
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        _open = w;
        w.Show();
    }

    /// <summary>The turn a movie asks to be shown with (System.Video.Orientation - phones film sideways and say so):
    /// 0, 90, 180 or 270 degrees.</summary>
    public static int Orientation(string path)
    {
        int deg = Property(path, "System.Video.Orientation") switch { uint u => (int)(u % 360), int i => (i % 360 + 360) % 360, ushort s => s % 360, _ => 0 };
        return deg is 90 or 180 or 270 ? deg : 0;
    }

    /// <summary>A movie's exact length, as the file gives it (System.Media.Duration); zero if it doesn't.</summary>
    public static TimeSpan Duration(string path) => Property(path, "System.Media.Duration") is ulong u ? TimeSpan.FromTicks((long)u) : TimeSpan.Zero;

    static object Property(string path, string name)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(path));
            dynamic item = folder?.ParseName(Path.GetFileName(path));
            return item?.ExtendedProperty(name);
        }
        catch { return null; }
    }

    static string CodecName(string fourcc) => fourcc?.Trim('{', '}').ToUpperInvariant() switch
    {
        null or "" => null,
        var s when s.StartsWith("34363248") || s.Contains("H264") || s.Contains("AVC1") => "H.264",
        var s when s.StartsWith("43564548") || s.Contains("HEVC") || s.Contains("HVC1") => "HEVC",
        var s when s.StartsWith("31305641") || s.Contains("AV01") => "AV1",
        var s when s.StartsWith("30395056") || s.Contains("VP90") => "VP9",
        var s => s.Length > 12 ? null : s,
    };

    static string Size(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB" : Math.Max(1, b / 1024) + " KB";
}
