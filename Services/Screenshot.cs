using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Finder;
using MacShell.Native;
using static MacShell.Native.NativeMethods;
using Path = System.IO.Path;

namespace MacShell.Services;

/// <summary>
/// macOS-style screenshots: full screen (⇧⌘3 → Win+Shift+3) and selection (⇧⌘4 → Win+Shift+4); the ⇧⌘5 toolbar
/// (Apps/QuickTime/ScreenRecorder) takes windows and portions too. Its Options apply to all of them: where they go, the
/// floating thumbnail, the pointer.
/// </summary>
public static class Screenshot
{
    public static void CaptureFullScreen()
    {
        var r = ShellHost.ScreenPx;
        Deliver(Capture(r.Left, r.Top, r.Width, r.Height, Settings.Current.CaptureShowPointer));
    }

    public static void CaptureArea()
    {
        var overlay = new AreaOverlay();
        overlay.Show();
        overlay.Activate();
    }

    /// <summary>A part of the screen (screen pixels), with the pointer drawn in if asked (Options › Show Mouse Pointer).</summary>
    public static BitmapSource Capture(int x, int y, int w, int h, bool pointer = false)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        if (pointer) DrawPointer(mem, x, y);
        SelectObject(mem, old);
        var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return src;
    }

    static void DrawPointer(IntPtr dc, int x, int y)
    {
        var ci = new ScreenGdi.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ScreenGdi.CURSORINFO>() };
        if (!ScreenGdi.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (ScreenGdi.GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        ScreenGdi.DrawIconEx(dc, ci.ptScreenPos.X - x - hx, ci.ptScreenPos.Y - y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 0x0003);
    }

    /// <summary>
    /// A screenshot taken: where Options › Save to says - a file on the Desktop, in Documents or a folder chosen (with the
    /// floating thumbnail), the clipboard, or Preview.
    /// </summary>
    public static void Deliver(BitmapSource img)
    {
        string to = Settings.Current.ScreenshotSaveTo ?? "desktop";
        if (to == "clipboard")
        {
            try { Clipboard.SetImage(img); } catch (Exception ex) { ShellHost.ShowAlert("The screenshot couldn’t be copied.", ex.Message); return; }
            Sound.Play("screenshot");
            return;
        }
        string dir = to switch
        {
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "preview" => Path.Combine(Path.GetTempPath(), "MacShell Screenshots"),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            _ => Directory.Exists(to) ? to : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        var now = DateTime.Now;
        string path = Path.Combine(dir, $"Screenshot {now:yyyy-MM-dd} at {now:h.mm.ss tt}.png");
        try
        {
            Directory.CreateDirectory(dir);
            using var fs = File.Create(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            enc.Save(fs);
        }
        catch (Exception ex) { ShellHost.ShowAlert("The screenshot couldn’t be saved.", ex.Message); return; }
        Sound.Play("screenshot");
        if (to == "preview") { Apps.Preview.PreviewWindow.Open(path); return; }
        Flash();
        if (Settings.Current.CaptureThumbnail) ShowThumbnail(img, path);
    }

    static void Flash()
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.White, Topmost = true, ShowInTaskbar = false,
            Left = ShellHost.ScreenPx.Left / ShellHost.Scale, Top = ShellHost.ScreenPx.Top / ShellHost.Scale,
            Width = ShellHost.ScreenDip.Width, Height = ShellHost.ScreenDip.Height, Opacity = 0.6, IsHitTestVisible = false, ShowActivated = false,
        };
        w.Show();
        var a = new DoubleAnimation(0.6, 0, TimeSpan.FromMilliseconds(300));
        a.Completed += (_, _) => w.Close();
        w.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>The floating thumbnail in the corner for a few seconds: a click opens the file, dragging takes it along.</summary>
    public static void ShowThumbnail(BitmapSource img, string path)
    {
        double tw = 170, th = tw * img.PixelHeight / Math.Max(1, img.PixelWidth);
        var border = new Border
        {
            Width = tw, Height = th, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3), BorderBrush = Brushes.White,
            Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, Margin = new Thickness(20),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = 0.4 }, Cursor = Cursors.Hand,
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight, Content = border, ShowActivated = false,
        };
        w.Loaded += (_, _) =>
        {
            w.Left = ShellHost.ScreenPx.Left / ShellHost.Scale + ShellHost.ScreenDip.Width - w.ActualWidth - 4;
            w.Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.ScreenDip.Height - w.ActualHeight - (ShellHost.Dock?.ReservedHeight ?? 0) - 4;
            var tr = new TranslateTransform(tw + 40, 0);
            border.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        border.MouseLeftButtonUp += (_, _) => { w.Close(); AppCatalog.OpenFile(path); };
        border.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(border, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
                w.Close();
            }
        };
        w.Show();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) => { t.Stop(); if (w.IsVisible) w.Close(); };
        t.Start();
    }

    /// <summary>Crosshair selection overlay with a live "w × h" readout.</summary>
    class AreaOverlay : Window
    {
        readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)) };
        readonly Rectangle _rect = new() { Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Visibility = Visibility.Collapsed };
        readonly TextBlock _dims = new() { Foreground = Brushes.White, FontSize = 11, Visibility = Visibility.Collapsed, Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.8 } };
        Point? _start;

        public AreaOverlay()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
            Left = ShellHost.ScreenPx.Left / ShellHost.Scale; Top = ShellHost.ScreenPx.Top / ShellHost.Scale;
            Width = ShellHost.ScreenDip.Width; Height = ShellHost.ScreenDip.Height;
            Cursor = Cursors.Cross;
            FontFamily = Theme.Font;
            _canvas.Children.Add(_rect);
            _canvas.Children.Add(_dims);
            Content = _canvas;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(this); CaptureMouse(); };
            MouseMove += (_, e) =>
            {
                var p = e.GetPosition(this);
                if (_start == null)
                {
                    _dims.Visibility = Visibility.Visible;
                    _dims.Text = $"{(int)(p.X * ShellHost.Scale)}\n{(int)(p.Y * ShellHost.Scale)}";
                    Canvas.SetLeft(_dims, p.X + 12); Canvas.SetTop(_dims, p.Y + 8);
                    return;
                }
                var r = new Rect(_start.Value, p);
                _rect.Visibility = Visibility.Visible;
                Canvas.SetLeft(_rect, r.X); Canvas.SetTop(_rect, r.Y);
                _rect.Width = r.Width; _rect.Height = r.Height;
                _dims.Text = $"{(int)(r.Width * ShellHost.Scale)}  {(int)(r.Height * ShellHost.Scale)}";
                Canvas.SetLeft(_dims, p.X + 12); Canvas.SetTop(_dims, p.Y + 8);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                ReleaseMouseCapture();
                if (_start == null) return;
                var r = new Rect(_start.Value, e.GetPosition(this));
                _start = null;
                Close();
                if (r.Width < 4 || r.Height < 4) return;
                double s = ShellHost.Scale;
                int x = (int)(ShellHost.ScreenPx.Left + r.X * s), y = (int)(ShellHost.ScreenPx.Top + r.Y * s), w = (int)(r.Width * s), h = (int)(r.Height * s);
                Dispatcher.BeginInvoke(() => Deliver(Capture(x, y, w, h, Settings.Current.CaptureShowPointer)), DispatcherPriority.ApplicationIdle);
            };
        }
    }
}
