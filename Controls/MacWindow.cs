using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Controls;

/// <summary>
/// Base class for MacShell's own app windows (Finder, System Settings …): borderless chrome with
/// DWM rounded corners, an acrylic "vibrancy" backdrop behind transparent regions, and macOS
/// window shortcuts (⌘W, ⌘M). Content supplies its own traffic lights via <see cref="TrafficLights"/>.
/// </summary>
public class MacWindow : Window
{
    public string AppKey { get; }
    public bool UseVibrancy { get; set; } = true;
    IntPtr _hwnd;

    public MacWindow(string appKey = WindowTracker.FinderKey, double captionHeight = 52)
    {
        AppKey = appKey;
        FontFamily = Theme.Font;
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetResourceReference(ForegroundProperty, "LabelBrush");
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = captionHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        SourceInitialized += OnSourceInitialized;
        Theme.Changed += ApplyDwmTheme;
        Closed += (_, _) => Theme.Changed -= ApplyDwmTheme;
    }

    void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(WndProc);
        // hide the DWM-drawn caption buttons: we draw traffic lights ourselves
        long style = GetStyle(_hwnd);
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref m);
        int round = 2;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        ApplyDwmTheme();
        WindowTracker.RegisterInternal(this, AppKey);
    }

    void ApplyDwmTheme()
    {
        if (_hwnd == IntPtr.Zero) return;
        int dark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        int backdrop = UseVibrancy ? 3 : 1; // 3 = acrylic (transient), 1 = none
        DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
        int border = Theme.IsDark ? 0x00505050 : 0x00B4B4B4;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, 4);
    }

    const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && ShellHost.TakeoverEnabled && WindowState == WindowState.Normal)
        {
            // like macOS: the window's top edge never goes under the menu bar
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOMOVE) == 0)
            {
                var scr = ShellHost.ScreenPx;
                int minY = scr.Top + (int)Math.Round(ShellHost.MenuBarHeight * ShellHost.Scale);
                if (pos.y < minY && pos.x < scr.Right && pos.x + Math.Max(pos.cx, 1) > scr.Left && pos.y > scr.Top - 4000)
                {
                    pos.y = minY;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
        }
        if (msg == WM_GETMINMAXINFO)
        {
            // Maximised ("zoomed") windows fill exactly the space between the menu bar and the Dock.
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var mon = MonitorFromWindow(hwnd, 2);
            var mi = NativeMethods.GetMonitorInfo(mon);
            mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
            mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
            mmi.ptMaxSize.X = mi.rcWork.Width;
            mmi.ptMaxSize.Y = mi.rcWork.Height;
            double scale = GetDpiForWindow(hwnd) / 96.0;
            mmi.ptMinTrackSize.X = (int)(MinWidth * scale);
            mmi.ptMinTrackSize.Y = (int)(MinHeight * scale);
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        return IntPtr.Zero;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.W && HandleCloseShortcut()) { e.Handled = true; }
            else if (e.Key == Key.M) { WindowState = WindowState.Minimized; e.Handled = true; }
        }
    }

    /// <summary>Override to intercept ⌘W (e.g. closing a tab). Return true when handled.</summary>
    protected virtual bool HandleCloseShortcut() { Close(); return true; }

    public void ToggleZoom() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Centres the window on the primary work area, with the classic slight upward bias.</summary>
    public void CenterOnWorkArea(double topBias = 0.42)
    {
        var wa = SystemParameters.WorkArea;
        double w = double.IsNaN(Width) ? ActualWidth : Width, h = double.IsNaN(Height) ? ActualHeight : Height;
        Left = wa.Left + (wa.Width - w) / 2;
        Top = wa.Top + Math.Max(0, (wa.Height - h) * topBias);
        ApplyOffscreen();
    }

    /// <summary>Test mode ("--open offscreen:on"): windows open off-screen and unfocused so they can be captured.</summary>
    public void ApplyOffscreen()
    {
        if (!ShellHost.Offscreen) return;
        ShowActivated = false;
        Left = -6000;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (ShellHost.Offscreen) ShowActivated = false;
    }
}

/// <summary>macOS traffic-light window buttons: close, minimise, zoom.</summary>
public class TrafficLights : StackPanel
{
    readonly Light _close, _min, _zoom;
    Window _window;
    bool _hover;
    public bool CanMinimize { get; set; } = true;
    public bool CanZoom { get; set; } = true;
    public event Action CloseRequested;

    public TrafficLights()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Background = Brushes.Transparent;
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        _close = new Light(Theme.C("#FF5F57"), Theme.C("#E14640"), Theme.C("#4D0000"), "close");
        _min = new Light(Theme.C("#FEBC2E"), Theme.C("#E1A116"), Theme.C("#985712"), "min");
        _zoom = new Light(Theme.C("#28C840"), Theme.C("#17A82E"), Theme.C("#0B650D"), "zoom");
        _min.Margin = new Thickness(8, 0, 8, 0);
        Children.Add(_close); Children.Add(_min); Children.Add(_zoom);
        MouseEnter += (_, _) => { _hover = true; Refresh(); };
        MouseLeave += (_, _) => { _hover = false; Refresh(); };
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window == null) return;
            _window.Activated += (_, _) => Refresh();
            _window.Deactivated += (_, _) => Refresh();
            Refresh();
        };
        _close.Click += () => { if (CloseRequested != null) CloseRequested(); else _window?.Close(); };
        _min.Click += () => { if (_window != null && CanMinimize) _window.WindowState = WindowState.Minimized; };
        _zoom.Click += () =>
        {
            if (_window == null || !CanZoom) return;
            if (_window is MacWindow mw) mw.ToggleZoom();
            else _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        };
        Theme.Changed += Refresh;
    }

    void Refresh()
    {
        bool active = _window?.IsActive ?? true;
        _close.SetState(active || _hover, _hover, true);
        _min.SetState(active || _hover, _hover, CanMinimize);
        _zoom.SetState(active || _hover, _hover, CanZoom);
    }

    class Light : FrameworkElement
    {
        readonly Color _fill, _border, _glyph;
        readonly string _kind;
        bool _colored = true, _showGlyph, _enabled = true, _pressed;
        public event Action Click;

        public Light(Color fill, Color border, Color glyph, string kind)
        {
            _fill = fill; _border = border; _glyph = glyph; _kind = kind;
            Width = 12; Height = 12;
            Cursor = Cursors.Arrow;
            MouseLeftButtonDown += (_, e) => { _pressed = true; CaptureMouse(); InvalidateVisual(); e.Handled = true; };
            MouseLeftButtonUp += (_, e) =>
            {
                bool inside = IsMouseOver;
                _pressed = false; ReleaseMouseCapture(); InvalidateVisual();
                if (inside && _enabled) Click?.Invoke();
                e.Handled = true;
            };
        }

        public void SetState(bool colored, bool glyph, bool enabled)
        {
            _colored = colored; _showGlyph = glyph; _enabled = enabled;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = new Point(6, 6);
            Color fill, border;
            if (!_enabled) { fill = Theme.IsDark ? Theme.C("#4A4A4C") : Theme.C("#D9D9DB"); border = Theme.IsDark ? Theme.C("#404042") : Theme.C("#C8C8CA"); }
            else if (_colored) { fill = _fill; border = _border; }
            else { fill = Theme.IsDark ? Theme.C("#535355") : Theme.C("#DCDCDC"); border = Theme.IsDark ? Theme.C("#474749") : Theme.C("#CBCBCB"); }
            if (_pressed && _enabled) fill = Theme.Darken(fill, 0.15);
            dc.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(border), 0.5), c, 5.75, 5.75);
            if (!_showGlyph || !_enabled) return;
            var br = new SolidColorBrush(Color.FromArgb(200, _glyph.R, _glyph.G, _glyph.B));
            var pen = new Pen(br, 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            switch (_kind)
            {
                case "close":
                    dc.DrawLine(pen, new Point(3.8, 3.8), new Point(8.2, 8.2));
                    dc.DrawLine(pen, new Point(8.2, 3.8), new Point(3.8, 8.2));
                    break;
                case "min":
                    dc.DrawLine(new Pen(br, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(3.2, 6), new Point(8.8, 6));
                    break;
                case "zoom":
                    var g1 = Geometry.Parse("M3.4,3.4 L3.4,7.6 L7.6,3.4 Z");
                    var g2 = Geometry.Parse("M8.6,8.6 L8.6,4.4 L4.4,8.6 Z");
                    dc.DrawGeometry(br, null, g1);
                    dc.DrawGeometry(br, null, g2);
                    break;
            }
        }
    }
}
