using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Path = System.IO.Path;

namespace MacShell.Apps.QuickTime;

/// <summary>
/// QuickTime Player, like the Mac's: the movie fills a black window; moving the pointer over it brings up the title and
/// the floating controls (volume, back, play/pause, forward, Picture in Picture, full screen, the timeline with the time
/// gone and left), which fade away while it plays. The timeline is pressed anywhere and dragged (the movie holds still
/// under the playhead until it's let go); scrolling sideways, or the wheel over the timeline, moves through the movie.
/// The window keeps the movie's shape; Space plays and pauses, the arrows step, J/K/L shuttle; full screen hides the
/// menu bar and the Dock. Movies open from Finder, the Dock and File › Open.
/// </summary>
public sealed class QuickTimeWindow : MacWindow
{
    public const string AppId = "internal:quicktime";
    public static readonly List<QuickTimeWindow> All = new();
    public static QuickTimeWindow Front => All.FirstOrDefault(w => w.IsActive) ?? All.LastOrDefault();
    /// <summary>Tests: movies open silent (never a sound while testing).</summary>
    public static bool Silent { get; set; }
    /// <summary>Tests: where the pointer is along a pressed HUD slider (0 to 1), in place of the real pointer.</summary>
    public static double? TestPointerAt { get; set; }

    static readonly string[] VideoExts = { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp", ".3g2", ".flv", ".asf" };
    static readonly string[] AudioExts = { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".aiff", ".aif", ".ogg", ".opus" };
    public static bool IsMovie(string path) => VideoExts.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
    public static bool IsPlayable(string path) => IsMovie(path) || AudioExts.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    /// <summary>Opening QuickTime Player (the Dock, Launchpad): the Open panel, as on a Mac.</summary>
    public static void OpenApp()
    {
        if (Front is { } f) { if (f.WindowState == WindowState.Minimized) f.WindowState = WindowState.Normal; f.Activate(); return; }
        ChooseAndOpen(null);
    }

    public static void ChooseAndOpen(Window owner)
    {
        var r = Finder.FilePanel.ShowModal(owner, new Finder.FilePanelRequest
        {
            Title = "Open", Prompt = "Open", AppKey = "macshell:quicktime", Multiple = true,
            Folder = Settings.Current.FilePanelPlaces.ContainsKey("macshell:quicktime") ? null : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Filters = { "Movies (*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.mpg;*.mpeg;*.m2ts;*.mts;*.ts;*.3gp;*.flv)", "Audio (*.mp3;*.m4a;*.aac;*.wav;*.wma;*.flac;*.aiff;*.ogg;*.opus)", "All files (*.*)" },
        });
        if (r?.Paths.Count > 0) foreach (var p in r.Paths) Open(p);
    }

    /// <summary>Quit QuickTime Player: every movie window closed.</summary>
    public static void QuitApp()
    {
        foreach (var w in All.ToList()) w.Close();
    }

    /// <summary>Opens a movie in a window of its own (the window it's already in, if it's open). <paramref name="silent"/>:
    /// muted for good (tests).</summary>
    public static QuickTimeWindow Open(string path, bool silent = false)
    {
        var open = All.FirstOrDefault(w => string.Equals(w._path, path, StringComparison.OrdinalIgnoreCase));
        if (open != null) { if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal; open.Activate(); return open; }
        var w = new QuickTimeWindow(path, silent || Silent);
        w.Show();
        if (!ShellHost.Offscreen) w.Activate();
        Settings.Current.QuickTimeRecent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Settings.Current.QuickTimeRecent.Insert(0, path);
        if (Settings.Current.QuickTimeRecent.Count > 10) Settings.Current.QuickTimeRecent.RemoveRange(10, Settings.Current.QuickTimeRecent.Count - 10);
        Settings.Save(notify: false);
        Settings.AddRecentDoc(path);
        return w;
    }

    readonly string _path;
    readonly Grid _root = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    readonly Grid _titleBar = new() { Height = 30, VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Border _hud = new() { CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, Padding = new Thickness(14, 8, 14, 9) };
    readonly SymbolIcon _playIcon = new() { Symbol = "play.fill", Width = 22, Height = 22, Foreground = Brushes.White };
    readonly SymbolIcon _volIcon = new() { Symbol = "speaker.wave.2", Width = 16, Height = 16, StrokeWidth = 1.8, Foreground = Brushes.White };
    readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 64, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider _scrub = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _elapsed = new() { Foreground = Brushes.White, FontSize = 11, Width = 50, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _remaining = new() { Foreground = Brushes.White, FontSize = 11, Width = 54, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _message = new() { Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.2) };
    bool _playing, _scrubbing, _resumeAfterScrub, _loop, _fullScreen, _pip, _audioOnly, _float;
    readonly bool _silent;
    double _aspect = 16.0 / 9;
    Rect _normalBounds;
    Point _lastMouse;
    double _speed = 1;
    IntPtr _hwnd;
    TimeSpan _duration;
    /// <summary>The movie's length (exact: from the file).</summary>
    TimeSpan Duration => _duration > TimeSpan.Zero ? _duration : _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
    /// <summary>The turn the file asks for (phones film sideways and say so), Edit › Rotate's quarter turns (clockwise),
    /// and Edit › Flip.</summary>
    int _metaTurn, _turn;
    bool _flipH, _flipV;

    QuickTimeWindow(string path, bool silent) : base(AppId, 30)
    {
        _path = path;
        _silent = silent;
        Title = Path.GetFileName(path);
        _title.Text = Title;
        Width = 720; Height = 405;
        MinWidth = 280; MinHeight = 160;
        CenterOnWorkArea(0.4);   // (placed before it's shown: never a jump)
        UseVibrancy = false;
        All.Add(this);
        Build();
        Closed += (_, _) =>
        {
            All.Remove(this);
            _tick.Stop(); _idle.Stop();
            try { _media.Stop(); _media.Close(); } catch { }
            if (_fullScreen) LeaveFullScreen();
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(KeepShape);
            HwndSource.FromHwnd(_hwnd)?.AddHook(SideScroll);
        };
        Loaded += (_, _) => CenterOnWorkArea(0.4);
        PreviewKeyDown += OnKey;
        _media.Source = new Uri(path);
        _media.Volume = Settings.Current.QuickTimeVolume;
        _media.IsMuted = _silent;
        _metaTurn = MovieInspector.Orientation(path) / 90 & 3;
        if (Settings.Current.QuickTimeTurns?.TryGetValue(path, out int saved) == true) { _turn = saved & 3; _flipH = (saved & 4) != 0; _flipV = (saved & 8) != 0; }
        ApplyTurn();
        _media.MediaOpened += (_, _) => Opened();
        _media.MediaFailed += (_, e) =>
        {
            _message.Text = $"The movie “{Path.GetFileName(path)}” couldn’t be opened.\n{e.ErrorException?.Message}";
            _message.Visibility = Visibility.Visible;
            App.Log($"quicktime {Path.GetFileName(path)}: {e.ErrorException?.Message}");
        };
        _media.MediaEnded += (_, _) =>
        {
            if (_loop) { _media.Position = TimeSpan.Zero; _media.Play(); return; }
            Pause();
            ShowControls();
        };
        _media.Play();   // (opening starts it; paused at once in Opened, so it shows its first frame)
        _media.Pause();
        _tick.Tick += (_, _) => UpdateTime();
        _idle.Tick += (_, _) => { _idle.Stop(); if (_playing && !_hud.IsMouseOver) HideControls(); };
    }

    // ================================================================== building

    void Build()
    {
        _root.Children.Add(_media);
        _message.Visibility = Visibility.Collapsed;
        _root.Children.Add(_message);

        // the title, over a dark gradient, shown with the controls
        _titleBar.Background = new LinearGradientBrush(Color.FromArgb(0xB0, 0, 0, 0), Color.FromArgb(0x00, 0, 0, 0), 90);
        var lights = new TrafficLights { Margin = new Thickness(12, 0, 0, 0) };
        _titleBar.Children.Add(lights);
        _title.Margin = new Thickness(80, 0, 80, 0);
        _titleBar.Children.Add(_title);
        _root.Children.Add(_titleBar);

        // the floating controls
        _hud.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x1E, 0x1E, 0x20));
        _hud.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _hud.BorderThickness = new Thickness(0.5);
        _hud.Margin = new Thickness(0, 0, 0, 22);
        _hud.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.45 };
        var rows = new StackPanel { Width = 420 };
        var top = new Grid { Height = 32 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        // volume
        var vol = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var volButton = HudButton(_volIcon, "Mute", ToggleMute);
        vol.Children.Add(volButton);
        _volume.Style = HudSliderStyle();
        PressAndDrag(_volume, null, v => _volume.Value = v, null);
        _volume.Value = Settings.Current.QuickTimeVolume;
        _volume.ValueChanged += (_, e) =>
        {
            _media.Volume = e.NewValue;
            if (!_silent) _media.IsMuted = false;
            Settings.Current.QuickTimeVolume = Math.Round(e.NewValue, 3);
            Settings.Save(notify: false);
            UpdateVolumeIcon();
        };
        _volume.Margin = new Thickness(4, 0, 0, 0);
        vol.Children.Add(_volume);
        top.Children.Add(vol);
        // back, play/pause, forward
        var mid = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        mid.Children.Add(HudButton(new SymbolIcon { Symbol = "backward.fill", Width = 20, Height = 20, Foreground = Brushes.White }, "Rewind (J)", () => Skip(-10)));
        mid.Children.Add(HudButton(_playIcon, "Play (Space)", TogglePlay, 40));
        mid.Children.Add(HudButton(new SymbolIcon { Symbol = "forward.fill", Width = 20, Height = 20, Foreground = Brushes.White }, "Fast Forward (L)", () => Skip(10)));
        Grid.SetColumn(mid, 1);
        top.Children.Add(mid);
        // picture in picture, full screen
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(HudButton(new SymbolIcon { Symbol = "pip", Width = 17, Height = 17, StrokeWidth = 1.7, Foreground = Brushes.White }, "Picture in Picture", TogglePip));
        right.Children.Add(HudButton(new SymbolIcon { Symbol = "arrow.up.left.and.arrow.down.right", Width = 15, Height = 15, StrokeWidth = 2, Foreground = Brushes.White }, "Full Screen (⌃⌘F)", ToggleFullScreen));
        Grid.SetColumn(right, 2);
        top.Children.Add(right);
        rows.Children.Add(top);
        // the timeline: pressed anywhere and dragged; the wheel over it moves through the movie
        var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0), Background = Brushes.Transparent };
        DockPanel.SetDock(_elapsed, Dock.Left);
        DockPanel.SetDock(_remaining, Dock.Right);
        line.Children.Add(_elapsed);
        line.Children.Add(_remaining);
        _scrub.Style = HudSliderStyle();
        PressAndDrag(_scrub, BeginScrub, ScrubTo, EndScrub);
        line.MouseWheel += (_, e) => { ScrollBy(e.Delta); e.Handled = true; };
        line.Children.Add(_scrub);
        rows.Children.Add(line);
        _hud.Child = rows;
        _root.Children.Add(_hud);
        // the controls can be dragged around in the window, as on a Mac
        Point? grab = null;
        _hud.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not FrameworkElement fe || FindAncestor<ButtonBase>(fe) == null && FindAncestor<Slider>(fe) == null) { grab = e.GetPosition(_root); _hud.CaptureMouse(); e.Handled = true; } };
        _hud.MouseMove += (_, e) =>
        {
            if (grab is not Point g || !_hud.IsMouseCaptured) return;
            var p = e.GetPosition(_root);
            var m = _hud.Margin;
            _hud.Margin = new Thickness(m.Left + 2 * (p.X - g.X), 0, 0, Math.Max(4, m.Bottom - (p.Y - g.Y)));
            grab = p;
        };
        _hud.MouseLeftButtonUp += (_, _) => { grab = null; _hud.ReleaseMouseCapture(); };

        // the movie: dragged to move the window, double-clicked for full screen
        _root.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(_root);
            if ((p - _lastMouse).Length < 2) return;
            _lastMouse = p;
            ShowControls();
        };
        _root.MouseLeave += (_, _) => { if (!_fullScreen) HideControls(); };
        _media.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ToggleFullScreen(); e.Handled = true; return; }
            if (!_fullScreen && e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
        };
        _root.MouseRightButtonUp += (_, e) => { e.Handled = true; Mb.Context(ContextItems()).IsOpen = true; };
        _root.AllowDrop = true;
        _root.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files.Where(IsPlayable)) Open(f);
        };
        WindowChrome.SetIsHitTestVisibleInChrome(_hud, true);
        Content = _root;
    }

    static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    static Button HudButton(UIElement content, string tip, Action click, double size = 32)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["PlainButton"], Content = content, Width = size, Height = 32, ToolTip = tip, Focusable = false, Cursor = Cursors.Arrow, Opacity = 0.92 };
        b.MouseEnter += (_, _) => b.Opacity = 1;
        b.MouseLeave += (_, _) => b.Opacity = 0.92;
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A HUD slider the Mac's way: pressed anywhere, its knob jumps under the pointer and follows it until it's let
    /// go. (WPF's own Slider takes the press for itself, so the timeline never knew it was being dragged.)</summary>
    static void PressAndDrag(Slider s, Action begin, Action<double> to, Action end)
    {
        bool down = false;
        double At(MouseEventArgs e)
        {
            double v = TestPointerAt is double f ? s.Minimum + f * (s.Maximum - s.Minimum)
                : s.Template?.FindName("PART_Track", s) is Track t ? t.ValueFromPoint(e.GetPosition(t)) : s.Value;
            return double.IsFinite(v) ? Math.Clamp(v, s.Minimum, s.Maximum) : s.Value;
        }
        s.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            e.Handled = true;   // (the knob's own drag, the track's jumps and the HUD's dragging stay out of it)
            down = true;
            s.CaptureMouse();
            begin?.Invoke();
            to(At(e));
        }), true);
        s.PreviewMouseMove += (_, e) => { if (down) to(At(e)); };
        s.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!down) return;
            e.Handled = true;
            down = false;
            to(At(e));
            s.ReleaseMouseCapture();
            end?.Invoke();
        };
        s.LostMouseCapture += (_, _) => { if (down) { down = false; end?.Invoke(); } };
    }

    static Style _hudSlider;

    /// <summary>The HUD's thin white sliders (the timeline and the volume).</summary>
    static Style HudSliderStyle() => _hudSlider ??= (Style)System.Windows.Markup.XamlReader.Parse(
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>" +
        "<Setter Property='Focusable' Value='False'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>" +
        "<Grid Height='16' Background='Transparent'><Border Height='4' CornerRadius='2' Background='#55FFFFFF'/>" +
        "<Track x:Name='PART_Track'>" +
        "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
        "<Border Height='4' CornerRadius='2' Background='#E6FFFFFF' Margin='0,0,-5,0'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
        "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
        "<Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
        "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Ellipse Width='11' Height='11' Fill='White'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
        "</Track></Grid></ControlTemplate></Setter.Value></Setter></Style>");

    // ================================================================== playing

    void Opened()
    {
        _message.Visibility = Visibility.Collapsed;
        int w = _media.NaturalVideoWidth, h = _media.NaturalVideoHeight;
        _audioOnly = w <= 0 || h <= 0;
        if (_audioOnly)
        {
            _message.Text = "♪  " + Path.GetFileNameWithoutExtension(_path);
            _message.Visibility = Visibility.Visible;
            _metaTurn = _turn = 0; _flipH = _flipV = false;
            ApplyTurn();
            w = 640; h = 200;
        }
        else if (Quarter % 2 == 1) (w, h) = (h, w);
        _aspect = (double)w / h;
        // the movie's own size (in screen pixels), smaller if it doesn't fit
        double scale = ShellHost.Scale <= 0 ? 1 : ShellHost.Scale;
        var wa = SystemParameters.WorkArea;
        double dw = w / scale, dh = h / scale;
        double k = Math.Min(1, Math.Min(wa.Width * 0.85 / dw, wa.Height * 0.85 / dh));
        Width = Math.Max(MinWidth, dw * k);
        Height = Width / _aspect;
        CenterOnWorkArea(0.4);
        // (the media engine rounds a movie's length down to whole seconds: the file's own, exact one is taken)
        var whole = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var exact = MovieInspector.Duration(_path);
        _duration = exact > whole && exact - whole < TimeSpan.FromSeconds(1.5) ? exact : whole;
        if (Duration > TimeSpan.Zero) _scrub.Maximum = Math.Max(0.1, Duration.TotalSeconds);
        UpdateTime();
        Play();
        ShowControls();
    }

    public void Play()
    {
        if (Duration > TimeSpan.Zero && _media.Position >= Duration - TimeSpan.FromMilliseconds(200)) _media.Position = TimeSpan.Zero;
        _media.SpeedRatio = _speed;
        _media.Play();
        _playing = true;
        _playIcon.Symbol = "pause.fill";
        _tick.Start();
        _idle.Stop(); _idle.Start();
    }

    public void Pause()
    {
        _media.Pause();
        _playing = false;
        _playIcon.Symbol = "play.fill";
        _tick.Stop();
        UpdateTime();
    }

    public void TogglePlay() { if (_playing) Pause(); else Play(); ShowControls(); }

    void Seek(double seconds)
    {
        // (a hair before the end: at the very end there's no frame, and the picture would stay on the one it had)
        double end = Duration > TimeSpan.Zero ? Math.Max(0, Duration.TotalSeconds - 0.05) : double.MaxValue;
        _media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, end));
        UpdateTime();
    }

    /// <summary>The timeline pressed: while it's held the movie stands still on the frame under the playhead, as on a Mac.</summary>
    void BeginScrub()
    {
        _scrubbing = true;
        _resumeAfterScrub = _playing;
        if (_playing) _media.Pause();
        ShowControls();
    }

    void ScrubTo(double seconds)
    {
        _scrub.Value = seconds;
        Seek(seconds);
    }

    /// <summary>The timeline let go: a movie that was playing plays on from there.</summary>
    void EndScrub()
    {
        _scrubbing = false;
        Seek(_scrub.Value);
        if (_resumeAfterScrub && _playing) { _media.SpeedRatio = _speed; _media.Play(); }
        _resumeAfterScrub = false;
        ShowControls();
    }

    /// <summary>Scrolling through the movie (a trackpad's sideways swipe anywhere on it, or the wheel over the timeline;
    /// right or up is later): a notch is a hundredth of the movie, at least half a second and at most ten.</summary>
    void ScrollBy(int delta)
    {
        if (delta == 0 || Duration <= TimeSpan.Zero) return;
        double notch = Math.Clamp(Duration.TotalSeconds / 100, 0.5, 10);
        Seek(_media.Position.TotalSeconds + delta / 120.0 * notch);
        ShowControls();
    }

    /// <summary>Sideways scrolling over the window (WM_MOUSEHWHEEL: a trackpad's two-finger swipe, a tilting wheel).</summary>
    IntPtr SideScroll(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
        ScrollBy((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        handled = true;
        return IntPtr.Zero;
    }

    void Skip(double seconds)
    {
        Seek(_media.Position.TotalSeconds + seconds);
        ShowControls();
    }

    /// <summary>One frame on or back (about a thirtieth of a second), paused.</summary>
    void Step(int frames)
    {
        if (_playing) Pause();
        Seek(_media.Position.TotalSeconds + frames / 30.0);
    }

    void ToggleMute()
    {
        if (_silent) return;
        _media.IsMuted = !_media.IsMuted;
        UpdateVolumeIcon();
    }

    void UpdateVolumeIcon() => _volIcon.Symbol = _media.IsMuted || _media.Volume < 0.01 ? "speaker.slash" : _media.Volume < 0.5 ? "speaker.1" : "speaker.wave.2";

    void ChangeVolume(double delta) => _volume.Value = Math.Clamp(_volume.Value + delta, 0, 1);

    public void SetSpeed(double speed)
    {
        _speed = speed;
        _media.SpeedRatio = speed;
    }

    /// <summary>The time gone and left, and the playhead (which stays under the pointer while the timeline is held).</summary>
    void UpdateTime()
    {
        var pos = _scrubbing ? TimeSpan.FromSeconds(_scrub.Value) : _media.Position;
        var dur = Duration;
        _elapsed.Text = Format(pos);
        _remaining.Text = dur > TimeSpan.Zero ? "-" + Format(dur - pos) : "";
        if (!_scrubbing) _scrub.Value = Math.Min(_scrub.Maximum, pos.TotalSeconds);
    }

    static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    // ================================================================== the controls fading in and out

    void ShowControls()
    {
        Fade(_hud, 1);
        Fade(_titleBar, _fullScreen ? 0 : 1);
        _root.Cursor = null;
        _idle.Stop();
        _idle.Start();
    }

    void HideControls()
    {
        Fade(_hud, 0);
        Fade(_titleBar, 0);
        if (_fullScreen) _root.Cursor = Cursors.None;
    }

    static void Fade(UIElement e, double to) => e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 150 : 350)));

    // ================================================================== full screen, picture in picture, sizes

    public void ToggleFullScreen() { if (_fullScreen) LeaveFullScreen(); else EnterFullScreen(); }

    void EnterFullScreen()
    {
        if (_pip) TogglePip();
        _normalBounds = new Rect(Left, Top, Width, Height);
        _fullScreen = true;
        AllowUnderMenuBar = true;
        var m = ShellHost.MonitorPx;
        double s = ShellHost.Scale <= 0 ? 1 : ShellHost.Scale;
        Topmost = true;
        Left = m.Left / s; Top = m.Top / s; Width = m.Width / s; Height = m.Height / s;
        ApplyOffscreen();
        ShellHost.OwnFullscreen = _hwnd;
        ShellHost.RecheckFullscreen();
        _titleBar.Opacity = 0;
        ShowControls();
    }

    void LeaveFullScreen()
    {
        _fullScreen = false;
        ShellHost.OwnFullscreen = IntPtr.Zero;
        ShellHost.RecheckFullscreen();
        Topmost = _float;
        AllowUnderMenuBar = false;
        _root.Cursor = null;
        if (!_normalBounds.IsEmpty) { Left = _normalBounds.Left; Top = _normalBounds.Top; Width = _normalBounds.Width; Height = _normalBounds.Height; }
    }

    /// <summary>Picture in Picture: the movie small, floating over everything in a corner; again: back where it was.</summary>
    public void TogglePip()
    {
        if (_fullScreen) LeaveFullScreen();
        if (!_pip)
        {
            _normalBounds = new Rect(Left, Top, Width, Height);
            _pip = true;
            Topmost = true;
            double w = 400, h = w / _aspect;
            var wa = SystemParameters.WorkArea;
            Width = w; Height = h;
            Left = wa.Right - w - 16;
            ApplyOffscreen();
            Top = wa.Bottom - h - 16;
        }
        else
        {
            _pip = false;
            Topmost = _float;
            Left = _normalBounds.Left; Top = _normalBounds.Top; Width = _normalBounds.Width; Height = _normalBounds.Height;
        }
    }

    /// <summary>View › Actual Size (the movie's own pixels) or Fit to Screen.</summary>
    public void SizeTo(bool fit)
    {
        if (_fullScreen) LeaveFullScreen();
        double scale = ShellHost.Scale <= 0 ? 1 : ShellHost.Scale;
        var wa = SystemParameters.WorkArea;
        double w = _media.NaturalVideoWidth > 0 ? _media.NaturalVideoWidth / scale : Width;
        if (fit) w = Math.Min(wa.Width - 40, (wa.Height - 40) * _aspect);
        else w = Math.Min(w, wa.Width - 20);
        Width = w;
        Height = w / _aspect;
        CenterOnWorkArea(0.4);
    }

    /// <summary>View › Increase / Decrease Size (⌘+ / ⌘−), around the window's middle.</summary>
    public void Resize(double factor)
    {
        if (_fullScreen) return;
        var wa = SystemParameters.WorkArea;
        double w = Math.Clamp(Width * factor, MinWidth, Math.Max(MinWidth, Math.Min(wa.Width - 20, (wa.Height - 20) * _aspect)));
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = w; Height = w / _aspect;
        Left = cx - w / 2; Top = Math.Max(wa.Top, cy - Height / 2);
    }

    /// <summary>View › Float on Top.</summary>
    public void ToggleFloat()
    {
        _float = !_float;
        if (!_pip && !_fullScreen) Topmost = _float;
    }

    /// <summary>The green button: full screen, as a movie does on a Mac.</summary>
    public override void ToggleZoom() => ToggleFullScreen();

    /// <summary>The quarter turns the picture is shown with (the file's own, then Edit › Rotate's).</summary>
    int Quarter => (_metaTurn + _turn) & 3;

    void ApplyTurn()
    {
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(_flipH ? -1 : 1, _flipV ? -1 : 1));
        g.Children.Add(new RotateTransform(90 * Quarter));
        _media.LayoutTransform = g;
    }

    /// <summary>Edit › Rotate Left / Right (quarter turns) and Flip Horizontal / Vertical - remembered for this movie.</summary>
    public void Turn(int quarters, bool flipH = false, bool flipV = false)
    {
        if (_audioOnly) return;
        _turn = (_turn + quarters + 4) & 3;
        if (flipH || flipV)
        {
            // (flipped as the picture is seen now: on its side, left-right is the movie's up-down)
            bool sideways = Quarter % 2 == 1;
            if (flipH ^ sideways) _flipH = !_flipH; else _flipV = !_flipV;
        }
        ApplyTurn();
        int w = _media.NaturalVideoWidth, h = _media.NaturalVideoHeight;
        if (w > 0 && h > 0)
        {
            if (Quarter % 2 == 1) (w, h) = (h, w);
            double old = _aspect;
            _aspect = (double)w / h;
            if (!_fullScreen && Math.Abs(old - _aspect) > 0.001)
            {
                // the window keeps about its size, in the new shape
                var wa = SystemParameters.WorkArea;
                double nw = Math.Min(Math.Sqrt(Width * Height * _aspect), Math.Min(wa.Width - 20, (wa.Height - 20) * _aspect));
                double cx = Left + Width / 2, cy = Top + Height / 2;
                Width = Math.Max(MinWidth, nw); Height = Width / _aspect;
                Left = cx - Width / 2; Top = Math.Max(wa.Top, cy - Height / 2);
            }
        }
        int saved = _turn | (_flipH ? 4 : 0) | (_flipV ? 8 : 0);
        var turns = Settings.Current.QuickTimeTurns ??= new();
        if (saved == 0) turns.Remove(_path); else turns[_path] = saved;
        Settings.Save(notify: false);
        ShowControls();
    }

    /// <summary>Edit › Copy: the movie, to paste in Finder, Mail …</summary>
    void CopyMovie()
    {
        try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _path }); } catch { }
    }

    /// <summary>The window keeps the movie's shape while it's resized (WM_SIZING).</summary>
    IntPtr KeepShape(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        if (msg != WM_SIZING || _fullScreen || _aspect <= 0) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<RECT>(lParam);
        int edge = wParam.ToInt32();
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        switch (edge)
        {
            case 3 or 6:   // top, bottom: the width follows
                r.Right = r.Left + (int)Math.Round(h * _aspect);
                break;
            case 4:   // top left: the top follows the width
                r.Top = r.Bottom - (int)Math.Round(w / _aspect);
                break;
            case 5:   // top right
                r.Top = r.Bottom - (int)Math.Round(w / _aspect);
                break;
            default:   // left, right, bottom corners: the height follows
                r.Bottom = r.Top + (int)Math.Round(w / _aspect);
                break;
        }
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    // ================================================================== keys and menus

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Space or Key.K: TogglePlay(); break;
            case Key.Left when mods == ModifierKeys.Alt: Seek(0); break;
            case Key.Right when mods == ModifierKeys.Alt: if (Duration > TimeSpan.Zero) Seek(Duration.TotalSeconds - 0.05); break;
            case Key.Left: if (_playing) Skip(-5); else Step(-1); break;
            case Key.Right: if (_playing) Skip(5); else Step(1); break;
            case Key.Home: Seek(0); break;
            case Key.J: Skip(-10); break;
            case Key.L when mods == ModifierKeys.None: if (!_playing) Play(); else SetSpeed(Math.Min(4, _speed * 2)); break;
            case Key.Up when mods == ModifierKeys.Control: ChangeVolume(0.1); break;
            case Key.Down when mods == ModifierKeys.Control: ChangeVolume(-0.1); break;
            case Key.F when mods == ModifierKeys.Control || mods == (ModifierKeys.Control | ModifierKeys.Shift): ToggleFullScreen(); break;
            case Key.Escape when _fullScreen: LeaveFullScreen(); break;
            case Key.D1 when mods == ModifierKeys.Control: SizeTo(false); break;
            case Key.D3 when mods == ModifierKeys.Control: SizeTo(true); break;
            case Key.L when mods == (ModifierKeys.Control | ModifierKeys.Alt): _loop = !_loop; break;
            case Key.O when mods == ModifierKeys.Control: ChooseAndOpen(this); break;
            case Key.I when mods == ModifierKeys.Control: MovieInspector.Show(this, _path, _media); break;
            case Key.C when mods == ModifierKeys.Control: CopyMovie(); break;
            case Key.L when mods == (ModifierKeys.Control | ModifierKeys.Shift): Turn(-1); break;
            case Key.R when mods == (ModifierKeys.Control | ModifierKeys.Shift): Turn(1); break;
            case Key.H when mods == (ModifierKeys.Control | ModifierKeys.Shift): Turn(0, flipH: true); break;
            case Key.V when mods == (ModifierKeys.Control | ModifierKeys.Shift): Turn(0, flipV: true); break;
            case Key.OemPlus or Key.Add when mods == ModifierKeys.Control: Resize(1.25); break;
            case Key.OemMinus or Key.Subtract when mods == ModifierKeys.Control: Resize(0.8); break;
            default: return;
        }
        e.Handled = true;
    }

    object[] ContextItems() => new object[]
    {
        Mb.Item(_playing ? "Pause" : "Play", TogglePlay),
        Mb.Sep(),
        Mb.Item("Loop", () => _loop = !_loop, isChecked: _loop),
        Mb.Sub("Playback Speed", SpeedItems()),
        Mb.Sep(),
        Mb.Item("Show in Finder", () => ShellHost.RevealInFinder(_path)),
        Mb.Item("Show Movie Inspector", () => MovieInspector.Show(this, _path, _media), "⌘I"),
    };

    object[] SpeedItems() => new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 }
        .Select(s => (object)Mb.Item(s == 1 ? "Normal" : s.ToString("0.##", CultureInfo.InvariantCulture) + "×", () => SetSpeed(s), isChecked: Math.Abs(_speed - s) < 0.01)).ToArray();

    // ------------------------------------------------------------------ the menu bar's menus (QuickTime in front)

    public static object[] MenuApp() => new object[]
    {
        Mb.Item("About QuickTime Player", AboutWindow.ShowWindow),
        Mb.Sep(),
        Mb.Item("Hide QuickTime Player", () => { foreach (var w in All) w.WindowState = WindowState.Minimized; }, "⌘H"),
        Mb.Sep(),
        Mb.Item("Quit QuickTime Player", QuitApp, "⌘Q"),
    };

    public static object[] MenuEdit()
    {
        var w = Front;
        bool on = w != null && !w._audioOnly;
        return new object[]
        {
            Mb.Item("Copy", () => w.CopyMovie(), "⌘C", enabled: w != null),
            Mb.Sep(),
            Mb.Item("Rotate Left", () => w.Turn(-1), "⇧⌘L", enabled: on),
            Mb.Item("Rotate Right", () => w.Turn(1), "⇧⌘R", enabled: on),
            Mb.Item("Flip Horizontal", () => w.Turn(0, flipH: true), "⇧⌘H", enabled: on),
            Mb.Item("Flip Vertical", () => w.Turn(0, flipV: true), "⇧⌘V", enabled: on),
        };
    }

    public static object[] MenuFile()
    {
        var w = Front;
        return new object[]
        {
            Mb.Item("New Screen Recording", ScreenRecorder.Start, "⌃⌘N"),
            Mb.Sep(),
            Mb.Item("Open File…", () => ChooseAndOpen(w), "⌘O"),
            Mb.LazySub("Open Recent", () => Settings.Current.QuickTimeRecent.Where(File.Exists).Select(p => (object)Mb.Item(Path.GetFileName(p), () => Open(p)))),
            Mb.Sep(),
            Mb.Item("Close", () => w?.Close(), "⌘W", enabled: w != null),
            Mb.Sep(),
            Mb.Item("Show in Finder", () => ShellHost.RevealInFinder(w._path), enabled: w != null),
        };
    }

    public static object[] MenuView()
    {
        var w = Front;
        bool on = w != null;
        bool windowed = on && !w._fullScreen;
        return new object[]
        {
            Mb.Item("Actual Size", () => w.SizeTo(false), "⌘1", enabled: on),
            Mb.Item("Fit to Screen", () => w.SizeTo(true), "⌘3", enabled: on),
            Mb.Item("Increase Size", () => w.Resize(1.25), "⌘+", enabled: windowed),
            Mb.Item("Decrease Size", () => w.Resize(0.8), "⌘−", enabled: windowed),
            Mb.Sep(),
            Mb.Item(w?._fullScreen == true ? "Exit Full Screen" : "Enter Full Screen", () => w.ToggleFullScreen(), "⌃⌘F", enabled: on),
            Mb.Item(w?._pip == true ? "Exit Picture in Picture" : "Enter Picture in Picture", () => w.TogglePip(), enabled: on),
            Mb.Item("Float on Top", () => w.ToggleFloat(), enabled: on, isChecked: w?._float == true),
            Mb.Sep(),
            Mb.Item("Loop", () => w._loop = !w._loop, "⌥⌘L", enabled: on, isChecked: w?._loop == true),
            on ? Mb.Sub("Playback Speed", w.SpeedItems()) : Mb.Item("Playback Speed", enabled: false),
        };
    }

    public static object[] MenuWindowExtras()
    {
        var w = Front;
        return new object[] { Mb.Sep(), Mb.Item("Show Movie Inspector", () => MovieInspector.Show(w, w._path, w._media), "⌘I", enabled: w != null) };
    }

    // ------------------------------------------------------------------ diagnostics

    public static string TestState() => Front is not { } w ? "no window" :
        $"file={Path.GetFileName(w._path)} playing={w._playing} muted={w._media.IsMuted} pos={w._media.Position.TotalSeconds:0.0} dur={(w.Duration > TimeSpan.Zero ? w.Duration.TotalSeconds : -1):0.0##} scrubMax={w._scrub.Maximum:0.0##} " +
        $"video={w._media.NaturalVideoWidth}x{w._media.NaturalVideoHeight} window={w.ActualWidth:0}x{w.ActualHeight:0} aspect={w._aspect:0.000} hud={w._hud.Opacity:0.0} full={w._fullScreen} pip={w._pip} " +
        $"turn={w.Quarter} meta={w._metaTurn * 90} flip={(w._flipH ? "h" : "")}{(w._flipV ? "v" : "")} topmost={w.Topmost}";

    /// <summary>--open qttest:&lt;what&gt; (the front movie window).</summary>
    public static void Test(string what)
    {
        var w = Front;
        switch (what)
        {
            case "pause": w?.Pause(); break;
            case "play": w?.Play(); break;
            case "controls": w?.ShowControls(); break;
            case "seek": w?.Seek(2); break;
            case "forward": w?.Skip(10); break;
            case "back": w?.Skip(-10); break;
            case "full": w?.ToggleFullScreen(); break;
            case "pip": w?.TogglePip(); break;
            case "rotate": w?.Turn(1); break;
            case "flip": w?.Turn(0, flipH: true); break;
            case "bigger": w?.Resize(1.25); break;
            case "smaller": w?.Resize(0.8); break;
            case "float": w?.ToggleFloat(); break;
            case "close": QuitApp(); break;
        }
    }
}
