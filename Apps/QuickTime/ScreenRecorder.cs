using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Microsoft.Win32.SafeHandles;
using static MacShell.Native.NativeMethods;
using Path = System.IO.Path;

namespace MacShell.Apps.QuickTime;

/// <summary>
/// Screenshots and screen recordings, like macOS's toolbar (⇧⌘5 - here Win+Shift+5 - or QuickTime › File › New Screen
/// Recording): capture the entire screen, a window or a portion of the screen, or record the entire screen or a portion;
/// Options for where it goes, a timer, the floating thumbnail, keeping the selection and showing clicks. While it
/// records, a stop button sits in the menu bar (⌃⌘Esc - Ctrl+Win+Esc - stops too). Recordings are H.264 movies
/// ("Screen Recording 2026-10-16 at 11.45.12 AM.mp4"), made from the screen through Desktop Duplication (GDI when that
/// can't be had) at 30 frames a second, with the pointer drawn in.
/// </summary>
public static class ScreenRecorder
{
    public enum Mode { CaptureScreen, CaptureWindow, CapturePortion, RecordScreen, RecordPortion }
    static bool IsRecord(Mode m) => m is Mode.RecordScreen or Mode.RecordPortion;
    static bool IsPortion(Mode m) => m is Mode.CapturePortion or Mode.RecordPortion;

    static CaptureToolbar _toolbar;
    static readonly List<Window> _overlays = new();
    static Mode _mode;
    static RECT _monitor;          // the display the toolbar is on (screen pixels)
    static RECT _selection;        // the portion (screen pixels)
    static Session _session;
    static Window _countdown;
    static IntPtr _frontWindow;    // the window in front when the toolbar opened (Capture in window mode takes it)

    /// <summary>A recording is going on (the menu bar shows its stop button).</summary>
    public static bool Recording => _session != null;
    public static bool ToolbarOpen => _toolbar != null;
    public static RECT Selection => _selection;

    // ================================================================== the toolbar

    /// <summary>QuickTime › File › New Screen Recording: the toolbar, set to record.</summary>
    public static void Start() => ShowToolbar(record: true);

    /// <summary>⇧⌘5 (Win+Shift+5): the toolbar, as it was last used.</summary>
    public static void ShowToolbar() => ShowToolbar(null);

    static void ShowToolbar(bool? record)
    {
        if (Recording) return;
        if (_toolbar != null) { _toolbar.Activate(); return; }
        _frontWindow = GetForegroundWindow();
        var mode = Enum.TryParse(Settings.Current.CaptureMode, out Mode m) ? m : Mode.CapturePortion;
        if (record == true && !IsRecord(mode)) mode = Enum.TryParse(Settings.Current.CaptureRecordMode, out Mode r) && IsRecord(r) ? r : Mode.RecordScreen;
        _monitor = MonitorAtCursor();
        _selection = DefaultSelection();
        _toolbar = new CaptureToolbar();
        _toolbar.Closed += (_, _) => _toolbar = null;
        _toolbar.Show();
        SetMode(mode);
        // (from a shortcut another app is in front: the toolbar takes the keyboard - Return, Esc - all the same)
        if (!ShellHost.Offscreen) ActivateWindow(new WindowInteropHelper(_toolbar).Handle);
    }

    static RECT MonitorAtCursor()
    {
        GetCursorPos(out var p);
        foreach (var mon in GetMonitors())
            if (p.X >= mon.bounds.Left && p.X < mon.bounds.Right && p.Y >= mon.bounds.Top && p.Y < mon.bounds.Bottom) return mon.bounds;
        return ShellHost.MonitorPx;
    }

    /// <summary>The selection remembered (Options › Remember Last Selection) if it's still on a display; else half the screen, centered.</summary>
    static RECT DefaultSelection()
    {
        if (Settings.Current.CaptureRememberSelection && Settings.Current.CaptureSelection is { Length: 4 } s && s[2] >= 8 && s[3] >= 8)
        {
            var r = new RECT(s[0], s[1], s[0] + s[2], s[1] + s[3]);
            if (GetMonitors().Any(mon => r.Left >= mon.bounds.Left && r.Top >= mon.bounds.Top && r.Right <= mon.bounds.Right && r.Bottom <= mon.bounds.Bottom)) return r;
        }
        int w = _monitor.Width / 2, h = _monitor.Height / 2;
        return new RECT(_monitor.Left + (_monitor.Width - w) / 2, _monitor.Top + (_monitor.Height - h) / 2, _monitor.Left + (_monitor.Width + w) / 2, _monitor.Top + (_monitor.Height + h) / 2);
    }

    public static void SetMode(Mode mode)
    {
        _mode = mode;
        Settings.Current.CaptureMode = mode.ToString();
        if (IsRecord(mode)) Settings.Current.CaptureRecordMode = mode.ToString();
        Settings.Save(notify: false);
        CloseOverlays();
        if (IsPortion(mode)) foreach (var mon in GetMonitors()) Overlay(new SelectionOverlay(mon.bounds));
        else if (mode == Mode.CaptureWindow) foreach (var mon in GetMonitors()) Overlay(new WindowPicker(mon.bounds));
        _toolbar?.ShowMode(mode);
        _toolbar?.Activate();
    }

    static void Overlay(Window w)
    {
        _overlays.Add(w);
        w.Show();
    }

    static void CloseOverlays()
    {
        foreach (var w in _overlays.ToList()) w.Close();
        _overlays.Clear();
    }

    /// <summary>The portion being dragged: shown at once (kept when the drag ends).</summary>
    public static void SetSelectionLive(RECT r)
    {
        _selection = r;
        foreach (var o in _overlays.OfType<SelectionOverlay>()) o.Redraw();
    }

    /// <summary>The portion chosen: kept for next time (Options › Remember Last Selection).</summary>
    public static void SelectionChanged()
    {
        foreach (var o in _overlays.OfType<SelectionOverlay>()) o.Redraw();
        if (Settings.Current.CaptureRememberSelection && _selection.Width >= 8 && _selection.Height >= 8)
        {
            Settings.Current.CaptureSelection = new[] { _selection.Left, _selection.Top, _selection.Width, _selection.Height };
            Settings.Save(notify: false);
        }
    }

    public static void SetSelection(RECT r)
    {
        _selection = r;
        SelectionChanged();
    }

    /// <summary>The overlays put aside while something else is shown over them (Options › Other Location…).</summary>
    public static void WithoutOverlays(Action a)
    {
        foreach (var o in _overlays) o.Hide();
        try { a(); }
        finally
        {
            foreach (var o in _overlays) o.Show();
            _toolbar?.Activate();
        }
    }

    /// <summary>Esc or the toolbar's ⓧ: everything put away, nothing taken.</summary>
    public static void Cancel()
    {
        CloseOverlays();
        _toolbar?.Close();
    }

    /// <summary>The toolbar's Capture / Record (or Return): after the timer, if one is set.</summary>
    public static void Go()
    {
        var mode = _mode;
        var area = IsPortion(mode) ? _selection : _monitor;
        IntPtr window = mode == Mode.CaptureWindow ? FrontAppWindow() : IntPtr.Zero;
        if (mode == Mode.CaptureWindow && window == IntPtr.Zero) return;
        Cancel();
        AfterTimer(area, () =>
        {
            switch (mode)
            {
                case Mode.CaptureScreen or Mode.CapturePortion: Screenshot.Deliver(Screenshot.Capture(area.Left, area.Top, area.Width, area.Height, Settings.Current.CaptureShowPointer)); break;
                case Mode.CaptureWindow: CaptureWindow(window, shadow: true); break;
                default: BeginRecording(area); break;
            }
        });
    }

    /// <summary>The window clicked (window mode): captured once the picker is gone. Option (Alt) leaves its shadow out.</summary>
    public static void PickWindow(IntPtr hwnd)
    {
        bool shadow = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        Cancel();
        AfterTimer(_monitor, () => CaptureWindow(hwnd, shadow));
    }

    /// <summary>The window that was in front (an app's, not MacShell's bars) when the toolbar opened.</summary>
    static IntPtr FrontAppWindow()
    {
        var h = _frontWindow;
        if (h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h) && !IsOwnChrome(h)) return h;
        return WindowsInOrder().FirstOrDefault(w => !IsOwnChrome(w));
    }

    /// <summary>
    /// The screen is left a moment to redraw without the toolbar (so it's never in the picture); with Options › Timer,
    /// a countdown first (left out of the picture itself).
    /// </summary>
    static void AfterTimer(RECT area, Action act)
    {
        int seconds = Settings.Current.CaptureTimer;
        if (seconds <= 0) { Later(200, act); return; }
        var label = new TextBlock { FontSize = 64, FontWeight = FontWeights.Light, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var box = new Border { Width = 140, Height = 140, CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x1E, 0x20)), Child = label };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, Content = box, FontFamily = Theme.Font, Title = "Screenshot Timer",
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowDisplayAffinity(h, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/);
            int size = (int)Math.Round(140 * ScreenGdi.ScaleAt(new POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 }));
            SetWindowPos(h, HWND_TOPMOST, area.Left + (area.Width - size) / 2, area.Top + (area.Height - size) / 2, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        };
        int left = seconds;
        label.Text = left.ToString();
        _countdown = w;
        w.Show();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            left--;
            if (left > 0) { label.Text = left.ToString(); return; }
            t.Stop();
            w.Close();
            _countdown = null;
            Later(150, act);
        };
        t.Start();
    }

    static void Later(int ms, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); act(); };
        t.Start();
    }

    // ================================================================== recording

    static void BeginRecording(RECT area)
    {
        // (the encoder wants an even width and height)
        area = new RECT(area.Left, area.Top, area.Left + (area.Width & ~1), area.Top + (area.Height & ~1));
        if (area.Width < 16 || area.Height < 16) return;
        string file = RecordingPath();
        _session = new Session(area, file, Settings.Current.CaptureShowClicks);
        _session.Begin();
        ShellHost.MenuBar?.SetRecording(true);
        App.Log($"screen recording: started, {area.Width}x{area.Height} px at {area.Left},{area.Top}");
    }

    static string RecordingPath()
    {
        var now = DateTime.Now;
        string name = $"Screen Recording {now:yyyy-MM-dd} at {now:h.mm.ss tt}.mp4";
        string to = Settings.Current.RecordingSaveTo ?? "desktop";
        string dir = to switch
        {
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "quicktime" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            _ => Directory.Exists(to) ? to : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        return Path.Combine(dir, name);
    }

    /// <summary>The menu bar's stop button (or ⌃⌘Esc): the movie is finished and shown.</summary>
    public static async void Stop()
    {
        var s = _session;
        if (s == null) return;
        _session = null;
        ShellHost.MenuBar?.SetRecording(false);
        await s.EndAsync();
        Finished(s);
    }

    /// <summary>MacShell quitting: the movie is finished first (an unfinished one can't be played).</summary>
    public static void StopForQuit()
    {
        var s = _session;
        if (s == null) return;
        _session = null;
        s.EndAsync().Wait(TimeSpan.FromSeconds(5));
    }

    static void Finished(Session s)
    {
        if (s.Error != null)
        {
            App.Log($"screen recording failed: {s.Error}");
            try { if (File.Exists(s.File) && new FileInfo(s.File).Length == 0) File.Delete(s.File); } catch { }
            ShellHost.ShowAlert("The screen recording couldn’t be saved.", s.Error.Message);
            return;
        }
        App.Log($"screen recording: {s.Frames} frames in {s.Length.TotalSeconds:0.0} s, {s.FrameMs:0.0} ms a frame ({s.Method}) → {s.File}");
        Settings.AddRecentDoc(s.File);
        if (Settings.Current.RecordingSaveTo == "quicktime") { QuickTimeWindow.Open(s.File); return; }
        if (Settings.Current.CaptureThumbnail && s.Thumbnail != null) Screenshot.ShowThumbnail(s.Thumbnail, s.File);
    }

    /// <summary>A recording: the screen taken in and written out on a thread of its own.</summary>
    sealed class Session
    {
        public readonly RECT Area;
        public readonly string File;
        readonly bool _clicks;
        volatile bool _stop;
        readonly ManualResetEventSlim _done = new();
        public Exception Error;
        public BitmapSource Thumbnail;
        public string Method = "";
        public int Frames;
        public TimeSpan Length;
        /// <summary>The time a frame took to make (taking it in, the pointer, encoding), on average.</summary>
        public double FrameMs;
        public const int Fps = 30;

        public Session(RECT area, string file, bool clicks) { Area = area; File = file; _clicks = clicks; }

        public void Begin() => new Thread(Run) { IsBackground = true, Name = "Screen recording", Priority = ThreadPriority.AboveNormal }.Start();

        public Task EndAsync()
        {
            _stop = true;
            return Task.Run(() => _done.Wait());
        }

        void Run()
        {
            IScreenGrabber grab = null;
            FrameCanvas canvas = null;
            Mp4Writer movie = null;
            try
            {
                try
                {
                    if (ForceGdi) throw new IOException("GDI asked for");
                    var d = new DesktopDuplication(Area);
                    grab = d;
                    d.CheckQuarterTurn(GdiScreenGrabber.Shot);
                }
                catch (Exception ex)
                {
                    App.Log($"screen recording: Desktop Duplication unavailable ({ex.Message}) - using GDI");
                    grab?.Dispose();
                    grab = new GdiScreenGrabber(Area);
                }
                Method = grab.Method;
                canvas = new FrameCanvas(Area.Width, Area.Height);
                // (about 10 Mbit/s for a 1080p screen: sharp text, a sensible size)
                int bitrate = (int)Math.Clamp((long)Area.Width * Area.Height * Fps / 6, 1_500_000, 40_000_000);
                movie = new Mp4Writer(File, Area.Width, Area.Height, Fps, bitrate);
                long frame = TimeSpan.TicksPerSecond / Fps;
                var clock = Stopwatch.StartNew();
                long n = 0, busy = 0;
                while (!_stop)
                {
                    long due = n * TimeSpan.TicksPerSecond / Fps;
                    long now = clock.Elapsed.Ticks;
                    if (now < due) { grab.Update((int)Math.Max(1, (due - now) / TimeSpan.TicksPerMillisecond)); continue; }
                    grab.CopyTo(canvas.Bits, canvas.Stride);
                    canvas.DrawPointer(Area, _clicks);
                    ScreenGdi.GdiFlush();
                    movie.WriteFrame(canvas.Bits, canvas.Stride, due);
                    busy += clock.Elapsed.Ticks - now;
                    Frames++;
                    n++;
                    // fallen behind (a busy computer): frames are left out rather than the movie running slow
                    long after = clock.Elapsed.Ticks;
                    if (after - n * frame > 4 * frame) n = after / frame + 1;
                }
                Length = TimeSpan.FromTicks(n * frame);
                FrameMs = Frames > 0 ? busy / (double)TimeSpan.TicksPerMillisecond / Frames : 0;
                Thumbnail = Snapshot(canvas);
                movie.Finish();
            }
            catch (Exception ex) { Error = ex; }
            finally
            {
                try { movie?.Dispose(); } catch { }
                canvas?.Dispose();
                grab?.Dispose();
                _done.Set();
            }
        }

        /// <summary>The last frame, small, for the floating thumbnail.</summary>
        static BitmapSource Snapshot(FrameCanvas c)
        {
            try
            {
                var full = BitmapSource.Create(c.Width, c.Height, 96, 96, PixelFormats.Bgr32, null, c.Bits, c.Stride * c.Height, c.Stride);
                double k = Math.Min(1, 480.0 / c.Width);
                BitmapSource small = k < 1 ? new TransformedBitmap(full, new ScaleTransform(k, k)) : full;
                var copy = new WriteableBitmap(small);
                copy.Freeze();
                return copy;
            }
            catch { return null; }
        }
    }

    // ================================================================== window screenshots

    static readonly HashSet<string> OwnChromeTitles = new() { "Menu Bar", "Dock", "Desktop", "Screenshot", "Screenshot Timer" };

    static bool IsOwnChrome(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (pid != (uint)Environment.ProcessId) return false;
        var sb = new System.Text.StringBuilder(64);
        GetWindowText(h, sb, 64);
        return OwnChromeTitles.Contains(sb.ToString()) || sb.Length == 0;
    }

    /// <summary>Top-level windows, front to back, that can be seen (not minimized, not hidden away by Windows).</summary>
    static IEnumerable<IntPtr> WindowsInOrder()
    {
        var list = new List<IntPtr>();
        var picker = new HashSet<IntPtr>(_overlays.Select(o => new WindowInteropHelper(o).Handle));
        if (_toolbar != null) picker.Add(new WindowInteropHelper(_toolbar).Handle);
        EnumWindows((h, _) =>
        {
            if (!picker.Contains(h) && IsWindowVisible(h) && !IsIconic(h) && !Cloaked(h) && FrameBounds(h) is { Width: > 1, Height: > 1 })
            {
                var sb = new System.Text.StringBuilder(64);
                GetClassName(h, sb, 64);
                string cls = sb.ToString();
                if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window under a point (screen pixels), skipping the picker itself.</summary>
    public static IntPtr WindowAt(int x, int y) =>
        WindowsInOrder().FirstOrDefault(h => FrameBounds(h) is var r && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom);

    static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int c, 4) == 0 && c != 0;

    /// <summary>A window's visible frame (without the invisible resize borders), screen pixels.</summary>
    public static RECT FrameBounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0) return r;
        GetWindowRect(h, out r);
        return r;
    }

    /// <summary>
    /// A window on its own, as macOS takes it: just the window (even if something covers part of it), its corners
    /// rounded, on a transparent background with its shadow.
    /// </summary>
    static void CaptureWindow(IntPtr hwnd, bool shadow)
    {
        if (!IsWindow(hwnd)) return;
        var frame = FrameBounds(hwnd);
        var img = PrintedWindow(hwnd, frame) ?? Screenshot.Capture(frame.Left, frame.Top, frame.Width, frame.Height);
        double scale = ScreenGdi.ScaleAt(new POINT { X = frame.Left + frame.Width / 2, Y = frame.Top + frame.Height / 2 });
        double radius = IsZoomed(hwnd) ? 0 : 8 * scale;
        int pad = shadow ? (int)Math.Round(34 * scale) : 0;
        int w = img.PixelWidth + 2 * pad, h = img.PixelHeight + 2 * pad;
        var dv = new DrawingVisual();
        if (shadow) dv.Effect = new DropShadowEffect { BlurRadius = 28 * scale, ShadowDepth = 9 * scale, Direction = 270, Opacity = 0.42, Color = Colors.Black };
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(pad, pad, img.PixelWidth, img.PixelHeight);
            dc.PushClip(new RectangleGeometry(rect, radius, radius));
            dc.DrawImage(img, rect);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        Screenshot.Deliver(rtb);
    }

    /// <summary>The window drawn by itself (PrintWindow): what's behind other windows too. Null if it comes out blank.</summary>
    static unsafe BitmapSource PrintedWindow(IntPtr hwnd, RECT frame)
    {
        try
        {
            GetWindowRect(hwnd, out RECT wr);
            if (wr.Width <= 0 || wr.Height <= 0) return null;
            using var c = new FrameCanvas(wr.Width, wr.Height);
            if (!PrintWindow(hwnd, c.Dc, 2 /*PW_RENDERFULLCONTENT*/)) return null;
            ScreenGdi.GdiFlush();
            int ox = frame.Left - wr.Left, oy = frame.Top - wr.Top, fw = Math.Min(frame.Width, wr.Width - ox), fh = Math.Min(frame.Height, wr.Height - oy);
            if (ox < 0 || oy < 0 || fw <= 0 || fh <= 0) return null;
            // blank (black or empty) - some windows (layered, or drawn by the graphics card) don't print themselves
            long lit = 0, seen = 0;
            for (int y = oy; y < oy + fh; y += 7)
            {
                uint* row = (uint*)((byte*)c.Bits + (long)y * c.Stride);
                for (int x = ox; x < ox + fw; x += 7) { seen++; if ((row[x] & 0xFFFFFF) != 0) lit++; }
            }
            if (seen == 0 || lit < seen / 50) return null;
            var bmp = new WriteableBitmap(fw, fh, 96, 96, PixelFormats.Bgr32, null);
            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), c.Bits + oy * c.Stride + ox * 4, (fh - 1) * c.Stride + fw * 4, c.Stride);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ================================================================== the camera pointer (window mode)

    static Cursor _camera;

    public static Cursor CameraCursor()
    {
        if (_camera != null) return _camera;
        try
        {
            double s = Math.Max(1, ShellHost.Scale);
            int size = (int)Math.Round(32 * s);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(s, s));
                var outline = new Pen(Brushes.White, 1.4) { LineJoin = PenLineJoin.Round };
                var shape = Geometry.Combine(new RectangleGeometry(new Rect(4, 10, 24, 15), 3, 3), new RectangleGeometry(new Rect(11.5, 7, 9, 4.5), 1.4, 1.4), GeometryCombineMode.Union, null);
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), outline, shape);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), new Pen(Brushes.White, 1.3), new Point(16, 17.5), 4.6, 4.6);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), null, new Point(16, 17.5), 2.2, 2.2);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[size * size * 4];
            rtb.CopyPixels(px, size * 4, 0);
            // a 32-bit icon: the colour bitmap with its alpha, and an (unused) mask
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr color = ScreenGdi.CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            ReleaseDC(IntPtr.Zero, screen);
            Marshal.Copy(px, 0, bits, px.Length);
            IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
            var ii = new ICONINFO_CURSOR { fIcon = false, xHotspot = size / 2, yHotspot = (int)Math.Round(17.5 * s), hbmMask = mask, hbmColor = color };
            IntPtr icon = CreateIconIndirect(ref ii);
            DeleteObject(color);
            DeleteObject(mask);
            if (icon == IntPtr.Zero) return _camera = Cursors.Cross;
            return _camera = CursorInteropHelper.Create(new CursorHandle(icon));
        }
        catch { return _camera = Cursors.Cross; }
    }

    sealed class CursorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CursorHandle(IntPtr h) : base(true) { SetHandle(h); }
        protected override bool ReleaseHandle() => DestroyCursor(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO_CURSOR { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO_CURSOR info);
    [DllImport("user32.dll")] static extern bool DestroyCursor(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ================================================================== diagnostics

    /// <summary>Tests: the GDI way, as if Desktop Duplication couldn't be had.</summary>
    public static bool ForceGdi { get; set; }

    /// <summary>Tests: a recording of <paramref name="area"/> for <paramref name="ms"/>, waited for; what came of it.</summary>
    public static string TestRecord(RECT area, string file, int ms)
    {
        var s = new Session(area, file, clicks: true);
        s.Begin();
        Thread.Sleep(ms);
        s.EndAsync().Wait();
        return s.Error != null ? "error: " + s.Error.Message : $"frames={s.Frames} length={s.Length.TotalSeconds:0.00}s {s.FrameMs:0.0} ms a frame, method={s.Method} thumbnail={(s.Thumbnail != null ? $"{s.Thumbnail.PixelWidth}x{s.Thumbnail.PixelHeight}" : "none")}";
    }

    public static string TestState() =>
        $"toolbar={(_toolbar != null)} mode={_mode} overlays={_overlays.Count} recording={Recording} selection={_selection} monitor={_monitor} " +
        $"saveTo={Settings.Current.RecordingSaveTo} timer={Settings.Current.CaptureTimer}";

    /// <summary>--open screenrecord:&lt;what&gt; - toolbar | record | mode:&lt;Mode&gt; | select:x,y,w,h | go | stop | cancel | state</summary>
    public static void Test(string what)
    {
        what ??= "state";
        if (what.StartsWith("mode:") && Enum.TryParse(what[5..], out Mode m)) { if (_toolbar == null) ShowToolbar(IsRecord(m)); SetMode(m); return; }
        if (what.StartsWith("select:"))
        {
            var p = what[7..].Split(',').Select(int.Parse).ToArray();
            SetSelection(new RECT(p[0], p[1], p[0] + p[2], p[1] + p[3]));
            return;
        }
        switch (what)
        {
            case "toolbar": ShowToolbar(); break;
            case "record": Start(); break;
            case "go": Go(); break;
            case "stop": Stop(); break;
            case "cancel": Cancel(); break;
        }
        File.WriteAllText(Path.Combine(Settings.DataDirectory, "screenrecord.txt"), TestState());
    }
}

/// <summary>The floating toolbar: ⓧ | three ways to capture | two ways to record | Options ⌄ | Capture / Record.</summary>
sealed class CaptureToolbar : Window
{
    readonly Dictionary<ScreenRecorder.Mode, Border> _modes = new();
    readonly TextBlock _go = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _goButton;
    readonly Border _options;
    ScreenRecorder.Mode _mode;

    static readonly Brush Selected = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

    public CaptureToolbar()
    {
        Title = "Screenshot";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Theme.Font;
        UseLayoutRounding = true;
        Left = -32000; Top = -32000;   // (first drawn out of sight; Place puts it where it goes)

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // ⓧ
        var close = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(0x55, 0x8E, 0x8E, 0x93)), Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Close" };
        close.Child = new SymbolIcon { Symbol = "xmark", Width = 10, Height = 10, StrokeWidth = 2.2, Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)) };
        close.MouseLeftButtonUp += (_, _) => ScreenRecorder.Cancel();
        row.Children.Add(close);
        row.Children.Add(Divider());
        row.Children.Add(ModeButton(ScreenRecorder.Mode.CaptureScreen, "rect.full", "Capture Entire Screen"));
        row.Children.Add(ModeButton(ScreenRecorder.Mode.CaptureWindow, "rect.window", "Capture Selected Window"));
        row.Children.Add(ModeButton(ScreenRecorder.Mode.CapturePortion, "selection.rect", "Capture Selected Portion"));
        row.Children.Add(Divider());
        row.Children.Add(ModeButton(ScreenRecorder.Mode.RecordScreen, "rect.record", "Record Entire Screen"));
        row.Children.Add(ModeButton(ScreenRecorder.Mode.RecordPortion, "selection.record", "Record Selected Portion"));
        row.Children.Add(Divider());
        // Options ⌄
        var opt = new StackPanel { Orientation = Orientation.Horizontal };
        opt.Children.Add(new TextBlock { Text = "Options", Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        opt.Children.Add(new SymbolIcon { Symbol = "chevron.down", Width = 9, Height = 9, StrokeWidth = 2.4, Foreground = Brushes.White, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _options = TextButton(opt, () => ShowOptions());
        row.Children.Add(_options);
        // Capture / Record
        _goButton = TextButton(_go, ScreenRecorder.Go);
        _goButton.Margin = new Thickness(2, 0, 4, 0);
        row.Children.Add(_goButton);

        var bar = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(4, 6, 4, 6), Margin = new Thickness(24),
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x26, 0x26, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.45 },
            Child = row,
        };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        Content = bar;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { ScreenRecorder.Cancel(); e.Handled = true; }
            else if (e.Key == Key.Enter) { ScreenRecorder.Go(); e.Handled = true; }
        };
        Loaded += (_, _) => Place();
    }

    /// <summary>Near the bottom of the display, in the middle, above the Dock.</summary>
    void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (ShellHost.Offscreen) { SetWindowPos(h, HWND_TOPMOST, -21000, 200, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE); return; }
        GetCursorPos(out var p);
        var mon = GetMonitors().FirstOrDefault(m => p.X >= m.bounds.Left && p.X < m.bounds.Right && p.Y >= m.bounds.Top && p.Y < m.bounds.Bottom).bounds;
        if (mon.Width <= 0) mon = ShellHost.MonitorPx;
        double s = ScreenGdi.ScaleAt(p);
        int w = (int)Math.Round(ActualWidth * s), hh = (int)Math.Round(ActualHeight * s);
        int dock = (int)Math.Round(((ShellHost.Dock?.ReservedHeight ?? 70) + 26) * s);
        SetWindowPos(h, HWND_TOPMOST, mon.Left + (mon.Width - w) / 2, mon.Bottom - hh - dock + (int)Math.Round(24 * s), 0, 0, SWP_NOSIZE);
    }

    static FrameworkElement Divider() => new Border { Width = 1, Height = 26, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    Border ModeButton(ScreenRecorder.Mode mode, string symbol, string tip)
    {
        var b = new Border
        {
            Width = 38, Height = 32, CornerRadius = new CornerRadius(7), Margin = new Thickness(1, 0, 1, 0), Background = Brushes.Transparent, ToolTip = tip,
            Child = new SymbolIcon { Symbol = symbol, Width = 22, Height = 22, StrokeWidth = 1.6, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseEnter += (_, _) => { if (_mode != mode) b.Background = Hover; };
        b.MouseLeave += (_, _) => { if (_mode != mode) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, _) => ScreenRecorder.SetMode(mode);
        _modes[mode] = b;
        return b;
    }

    static Border TextButton(UIElement content, Action click)
    {
        var b = new Border { Padding = new Thickness(9, 5, 9, 5), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Child = content, VerticalAlignment = VerticalAlignment.Center };
        b.MouseEnter += (_, _) => b.Background = Hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    public void ShowMode(ScreenRecorder.Mode mode)
    {
        _mode = mode;
        foreach (var (m, b) in _modes) b.Background = m == mode ? Selected : Brushes.Transparent;
        _go.Text = mode is ScreenRecorder.Mode.RecordScreen or ScreenRecorder.Mode.RecordPortion ? "Record" : "Capture";
    }

    /// <summary>Options: Save to, Timer, Options - as the Mac's menu has them for the mode chosen.</summary>
    void ShowOptions()
    {
        var st = Settings.Current;
        bool record = _mode is ScreenRecorder.Mode.RecordScreen or ScreenRecorder.Mode.RecordPortion;
        string to = record ? st.RecordingSaveTo ?? "desktop" : st.ScreenshotSaveTo ?? "desktop";
        void SaveTo(string v) { if (record) st.RecordingSaveTo = v; else st.ScreenshotSaveTo = v; Settings.Save(notify: false); }
        var items = new List<object>
        {
            Mb.SectionHeader("Save to"),
            Mb.Item("Desktop", () => SaveTo("desktop"), isChecked: to == "desktop"),
            Mb.Item("Documents", () => SaveTo("documents"), isChecked: to == "documents"),
        };
        if (!record) items.Add(Mb.Item("Clipboard", () => SaveTo("clipboard"), isChecked: to == "clipboard"));
        items.Add(record ? Mb.Item("QuickTime Player", () => SaveTo("quicktime"), isChecked: to == "quicktime")
                         : Mb.Item("Preview", () => SaveTo("preview"), isChecked: to == "preview"));
        if (to is not ("desktop" or "documents" or "clipboard" or "preview" or "quicktime") && Directory.Exists(to))
            items.Add(Mb.Item(Path.GetFileName(to.TrimEnd('\\')) is { Length: > 0 } n ? n : to, () => SaveTo(to), isChecked: true));
        items.Add(Mb.Item("Other Location…", () => ScreenRecorder.WithoutOverlays(() =>
        {
            var r = Finder.FilePanel.ShowModal(this, new Finder.FilePanelRequest { Title = "Choose a Folder", Prompt = "Choose", Folders = true, AppKey = "macshell:screenshot" });
            if (r?.Paths.Count > 0 && Directory.Exists(r.Paths[0])) SaveTo(r.Paths[0]);
        })));
        items.Add(Mb.Sep());
        items.Add(Mb.SectionHeader("Timer"));
        foreach (var (label, secs) in new[] { ("None", 0), ("5 Seconds", 5), ("10 Seconds", 10) })
        {
            int s = secs;
            items.Add(Mb.Item(label, () => { st.CaptureTimer = s; Settings.Save(notify: false); }, isChecked: st.CaptureTimer == s));
        }
        items.Add(Mb.Sep());
        items.Add(Mb.SectionHeader("Options"));
        items.Add(Mb.Item("Show Floating Thumbnail", () => { st.CaptureThumbnail = !st.CaptureThumbnail; Settings.Save(notify: false); }, isChecked: st.CaptureThumbnail));
        items.Add(Mb.Item("Remember Last Selection", () => { st.CaptureRememberSelection = !st.CaptureRememberSelection; Settings.Save(notify: false); }, isChecked: st.CaptureRememberSelection));
        if (record) items.Add(Mb.Item("Show Mouse Clicks", () => { st.CaptureShowClicks = !st.CaptureShowClicks; Settings.Save(notify: false); }, isChecked: st.CaptureShowClicks));
        else items.Add(Mb.Item("Show Mouse Pointer", () => { st.CaptureShowPointer = !st.CaptureShowPointer; Settings.Save(notify: false); }, isChecked: st.CaptureShowPointer));
        var menu = Mb.Context(items.ToArray());
        menu.PlacementTarget = _options;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }
}

/// <summary>
/// One display, dimmed outside the portion chosen; the portion has a dashed outline and eight handles. Dragging inside
/// moves it, a handle resizes it, dragging anywhere else draws a new one.
/// </summary>
sealed class SelectionOverlay : Window
{
    readonly RECT _mon;
    readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x5C, 0, 0, 0)) };
    readonly Rectangle _outline = new() { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 }, SnapsToDevicePixels = true };
    readonly Rectangle _outlineShade = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), StrokeThickness = 1, SnapsToDevicePixels = true };
    readonly Ellipse[] _handles = new Ellipse[8];
    enum Drag { None, New, Move, Resize }
    Drag _drag;
    POINT _anchor;          // New: the corner held; Move: the grab point
    RECT _start;
    int _edges;             // Resize: 1 left, 2 top, 4 right, 8 bottom

    public SelectionOverlay(RECT monitor)
    {
        _mon = monitor;
        Title = "Screenshot Selection";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_outlineShade);
        _canvas.Children.Add(_outline);
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Ellipse { Width = 9, Height = 9, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), StrokeThickness = 0.8 };
            _canvas.Children.Add(_handles[i]);
        }
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            // (never activated: the toolbar keeps the keyboard - Return, Esc - and stays in front)
            var h = new WindowInteropHelper(this).Handle;
            AddExStyle(h, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(h, HWND_TOPMOST, _mon.Left, _mon.Top, _mon.Width, _mon.Height, SWP_NOACTIVATE);
        };
        Loaded += (_, _) => Redraw();
        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) =>
        {
            if (_drag == Drag.None) return;
            _drag = Drag.None;
            ReleaseMouseCapture();
            // (a click without a drag leaves the old portion as it was)
            if (ScreenRecorder.Selection.Width < 8 || ScreenRecorder.Selection.Height < 8) ScreenRecorder.SetSelectionLive(_start);
            ScreenRecorder.SelectionChanged();
        };
    }

    POINT Px(MouseEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        return new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
    }

    Point Local(int x, int y) => PointFromScreen(new Point(x, y));

    bool OnThisDisplay(RECT r) => r.Left >= _mon.Left && r.Top >= _mon.Top && r.Right <= _mon.Right && r.Bottom <= _mon.Bottom;

    public void Redraw()
    {
        if (PresentationSource.FromVisual(this) == null) return;
        var sel = ScreenRecorder.Selection;
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        bool here = OnThisDisplay(sel) && sel.Width > 0;
        _outline.Visibility = _outlineShade.Visibility = here ? Visibility.Visible : Visibility.Collapsed;
        foreach (var hnd in _handles) hnd.Visibility = _outline.Visibility;
        if (!here) { _dim.Data = full; return; }
        var a = Local(sel.Left, sel.Top);
        var b = Local(sel.Right, sel.Bottom);
        var r = new Rect(a, b);
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r));
        foreach (var o in new[] { _outlineShade, _outline })
        {
            Canvas.SetLeft(o, r.Left - 0.5); Canvas.SetTop(o, r.Top - 0.5);
            o.Width = r.Width + 1; o.Height = r.Height + 1;
        }
        var pts = HandlePoints(r);
        for (int i = 0; i < 8; i++) { Canvas.SetLeft(_handles[i], pts[i].X - 4.5); Canvas.SetTop(_handles[i], pts[i].Y - 4.5); }
    }

    static Point[] HandlePoints(Rect r) => new[]
    {
        r.TopLeft, new Point(r.Left + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Top + r.Height / 2),
        r.BottomRight, new Point(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Top + r.Height / 2),
    };

    static readonly int[] HandleEdges = { 1 | 2, 2, 2 | 4, 4, 4 | 8, 8, 8 | 1, 1 };

    /// <summary>The handle (its edges) under a point, or 0.</summary>
    int HandleAt(Point local)
    {
        var sel = ScreenRecorder.Selection;
        if (!OnThisDisplay(sel)) return 0;
        var pts = HandlePoints(new Rect(Local(sel.Left, sel.Top), Local(sel.Right, sel.Bottom)));
        for (int i = 0; i < 8; i++) if ((pts[i] - local).Length <= 8) return HandleEdges[i];
        return 0;
    }

    bool Inside(POINT p)
    {
        var s = ScreenRecorder.Selection;
        return p.X >= s.Left && p.X < s.Right && p.Y >= s.Top && p.Y < s.Bottom;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = Px(e);
        _start = ScreenRecorder.Selection;
        _edges = HandleAt(e.GetPosition(this));
        if (_edges != 0) _drag = Drag.Resize;
        else if (Inside(p)) { _drag = Drag.Move; _anchor = p; }
        else { _drag = Drag.New; _anchor = p; ScreenRecorder.SetSelectionLive(new RECT(p.X, p.Y, p.X, p.Y)); }
        CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var p = Px(e);
        if (_drag == Drag.None)
        {
            int edges = HandleAt(e.GetPosition(this));
            Cursor = edges switch
            {
                3 or 12 => Cursors.SizeNWSE,
                6 or 9 => Cursors.SizeNESW,
                1 or 4 => Cursors.SizeWE,
                2 or 8 => Cursors.SizeNS,
                _ => Inside(p) ? Cursors.SizeAll : Cursors.Cross,
            };
            return;
        }
        int x = Math.Clamp(p.X, _mon.Left, _mon.Right), y = Math.Clamp(p.Y, _mon.Top, _mon.Bottom);
        RECT r;
        switch (_drag)
        {
            case Drag.New:
                r = new RECT(Math.Min(_anchor.X, x), Math.Min(_anchor.Y, y), Math.Max(_anchor.X, x), Math.Max(_anchor.Y, y));
                break;
            case Drag.Move:
                {
                    int dx = Math.Clamp(p.X - _anchor.X, _mon.Left - _start.Left, _mon.Right - _start.Right);
                    int dy = Math.Clamp(p.Y - _anchor.Y, _mon.Top - _start.Top, _mon.Bottom - _start.Bottom);
                    r = new RECT(_start.Left + dx, _start.Top + dy, _start.Right + dx, _start.Bottom + dy);
                    break;
                }
            default:
                {
                    int l = _start.Left, t = _start.Top, rr = _start.Right, b = _start.Bottom;
                    if ((_edges & 1) != 0) l = Math.Min(x, rr - 8);
                    if ((_edges & 2) != 0) t = Math.Min(y, b - 8);
                    if ((_edges & 4) != 0) rr = Math.Max(x, l + 8);
                    if ((_edges & 8) != 0) b = Math.Max(y, t + 8);
                    r = new RECT(l, t, rr, b);
                    break;
                }
        }
        ScreenRecorder.SetSelectionLive(r);
    }
}

/// <summary>Window mode: the window under the pointer lit up in blue; a click takes it.</summary>
sealed class WindowPicker : Window
{
    readonly RECT _mon;
    readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    readonly Rectangle _light = new() { Fill = new SolidColorBrush(Color.FromArgb(0x50, 0x5B, 0x9C, 0xF0)), Visibility = Visibility.Collapsed };
    IntPtr _hover;

    public WindowPicker(RECT monitor)
    {
        _mon = monitor;
        Title = "Screenshot Selection";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        _canvas.Children.Add(_light);
        Content = _canvas;
        Cursor = ScreenRecorder.CameraCursor();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            AddExStyle(h, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(h, HWND_TOPMOST, _mon.Left, _mon.Top, _mon.Width, _mon.Height, SWP_NOACTIVATE);
        };
        MouseMove += (_, e) =>
        {
            var p = PointToScreen(e.GetPosition(this));
            var h = ScreenRecorder.WindowAt((int)p.X, (int)p.Y);
            if (h == _hover) return;
            _hover = h;
            if (h == IntPtr.Zero) { _light.Visibility = Visibility.Collapsed; return; }
            var r = ScreenRecorder.FrameBounds(h);
            var a = PointFromScreen(new Point(r.Left, r.Top));
            var b = PointFromScreen(new Point(r.Right, r.Bottom));
            Canvas.SetLeft(_light, a.X); Canvas.SetTop(_light, a.Y);
            _light.Width = Math.Max(0, b.X - a.X); _light.Height = Math.Max(0, b.Y - a.Y);
            _light.Visibility = Visibility.Visible;
        };
        MouseLeave += (_, _) => { _hover = IntPtr.Zero; _light.Visibility = Visibility.Collapsed; };
        MouseLeftButtonUp += (_, _) => { if (_hover != IntPtr.Zero) ScreenRecorder.PickWindow(_hover); };
    }
}
