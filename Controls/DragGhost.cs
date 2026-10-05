using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Controls;

/// <summary>
/// macOS-style drag images: while a file drag is in progress, translucent copies of the dragged icons
/// (with a red count badge for several items) follow the pointer over every window on screen.
/// </summary>
public static class DragGhost
{
    public static DragDropEffects Run(DependencyObject source, IDataObject data, DragDropEffects allowed, IList<ImageSource> icons, double size = 64)
    {
        Ghost ghost = null;
        try
        {
            if (icons != null && icons.Count > 0)
            {
                ghost = new Ghost(icons, size);
                ghost.Show();
                ghost.Follow();
            }
        }
        catch { ghost = null; }
        GiveFeedbackEventHandler feedback = (_, _) => ghost?.Follow();
        DragDrop.AddGiveFeedbackHandler(source, feedback);
        try { return DragDrop.DoDragDrop(source, data, allowed); }
        catch { return DragDropEffects.None; }
        finally
        {
            DragDrop.RemoveGiveFeedbackHandler(source, feedback);
            try { ghost?.Close(); } catch { }
        }
    }

    class Ghost : Window
    {
        IntPtr _hwnd;
        readonly double _size;

        public Ghost(IList<ImageSource> icons, double size)
        {
            _size = size;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            Width = size + 40;
            Height = size + 40;
            Left = -10000;
            Top = -10000;
            var grid = new Grid();
            int n = Math.Min(3, icons.Count);
            for (int i = n - 1; i >= 0; i--)
            {
                var img = new Image
                {
                    Source = icons[i], Width = size, Height = size, Opacity = i == 0 ? 0.82 : 0.55,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(20 + i * 5, 20 + i * 4, 0, 0),
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            if (icons.Count > 1)
            {
                var badge = new Border
                {
                    MinWidth = 22, Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(6, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(size + 2, 8, 0, 0),
                    Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.35 },
                    Child = new TextBlock
                    {
                        Text = icons.Count.ToString(), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold,
                        FontFamily = Theme.Font, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                grid.Children.Add(badge);
            }
            Content = grid;
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                // click-through so the drop target underneath is found normally
                AddExStyle(_hwnd, WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
        }

        public void Follow()
        {
            if (_hwnd == IntPtr.Zero) return;
            GetCursorPos(out var p);
            int off = (int)((20 + _size / 2) * ShellHost.Scale);
            SetWindowPos(_hwnd, HWND_TOPMOST, p.X - off, p.Y - off, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
