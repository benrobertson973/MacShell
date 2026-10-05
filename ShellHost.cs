using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;
using MacShell.Shell;
using Microsoft.Win32;
using static MacShell.Native.NativeMethods;

namespace MacShell;

/// <summary>Central coordinator for the shell: owns the menu bar, Dock, desktops and global services.</summary>
public static class ShellHost
{
    public const double MenuBarHeight = 24;
    public static double Scale { get; private set; } = 1;
    public static Size ScreenDip { get; private set; }
    public static RECT ScreenPx { get; private set; }
    public static bool TakeoverEnabled { get; private set; }
    public static bool Offscreen { get; set; }

    public static MenuBarWindow MenuBar { get; private set; }
    public static DockWindow Dock { get; private set; }
    public static readonly List<DesktopWindow> Desktops = new();
    static KeyboardHook _hook;
    static DispatcherTimer _maintain;
    static List<IntPtr> _shownDesktopMinimized;
    static bool _fullscreen;

    public static Dispatcher UI => Application.Current.Dispatcher;

    public static void Start(bool noTakeover)
    {
        TakeoverEnabled = !noTakeover;
        Settings.Load();
        Theme.Apply();
        ComputeMetrics();
        Wallpaper.Load();
        AppCatalog.Loaded += () =>
        {
            Dock?.EnsureDefaultPins();
            Dock?.NormalizePins();
            WindowTracker.Refresh();
            Dock?.Rebuild();
            MenuBar?.RebuildMenus();
        };
        _ = AppCatalog.LoadAsync();

        if (TakeoverEnabled) Takeover.Engage(Settings.Current.HideWindowsTaskbar);

        foreach (var mon in GetMonitors())
        {
            var d = new DesktopWindow(mon.bounds, mon.primary);
            Desktops.Add(d);
            d.Show();
        }

        MenuBar = new MenuBarWindow();
        MenuBar.Show();
        if (Settings.Current.MenuBarTrayIcons) TrayHost.Start(MenuBar.TrayIconRect);
        Dock = new DockWindow();
        Dock.Show();
        UpdateWorkArea();

        WindowTracker.Start();
        WindowTracker.ForegroundChanged += CheckFullscreen;
        Badges.Start();

        _hook = new KeyboardHook(UI)
        {
            Combo = OnCombo,
            WinTapAction = () => Settings.Current.WinKeyAction ?? "nothing",
            ReplaceAltTab = () => Settings.Current.ReplaceAltTab,
            AltSpaceSpotlight = () => Settings.Current.AltSpaceSpotlight,
        };
        _hook.WinTap += () => LaunchpadWindow.Toggle();
        _hook.SwitcherStart += rev => AppSwitcherWindow.Begin(rev);
        _hook.SwitcherStep += rev => AppSwitcherWindow.Step(rev);
        _hook.SwitcherCommit += () => AppSwitcherWindow.Commit();
        _hook.SwitcherCancel += () => AppSwitcherWindow.Cancel();
        _hook.SwitcherKey += vk => AppSwitcherWindow.Key(vk);
        _hook.Install();

        _maintain = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _maintain.Tick += (_, _) =>
        {
            if (TakeoverEnabled) Takeover.Maintain();
            TrayHost.Maintain();
            CheckFullscreen();
        };
        _maintain.Start();

        SystemEvents.DisplaySettingsChanged += (_, _) => UI.BeginInvoke(OnDisplayChanged);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Settings.Current.Appearance == "auto")
                UI.BeginInvoke(() => Theme.Apply());
        };
        Settings.Changed += () => UpdateWorkArea();

        // First launch: open a Finder window, just like logging in to a Mac.
        UI.BeginInvoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    static void ComputeMetrics()
    {
        var mons = GetMonitors();
        var primary = mons.FirstOrDefault(m => m.primary);
        if (primary.handle == IntPtr.Zero && mons.Count > 0) primary = mons[0];
        ScreenPx = primary.bounds;
        Scale = GetDpiForSystem() / 96.0;
        if (Scale <= 0) Scale = 1;
        ScreenDip = new Size(ScreenPx.Width / Scale, ScreenPx.Height / Scale);
    }

    static void OnDisplayChanged()
    {
        ComputeMetrics();
        Wallpaper.Load();
        var mons = GetMonitors();
        foreach (var d in Desktops.ToList()) d.Close();
        Desktops.Clear();
        foreach (var mon in mons)
        {
            var d = new DesktopWindow(mon.bounds, mon.primary);
            Desktops.Add(d);
            d.Show();
        }
        MenuBar?.Reposition();
        Dock?.Reposition();
        UpdateWorkArea();
    }

    static string ExeName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; }
    }

    public static void UpdateWorkArea()
    {
        if (!TakeoverEnabled || Dock == null || MenuBar == null) return;
        Takeover.Reserve(MenuBar.Handle, (int)Math.Round(MenuBarHeight * Scale), Dock.Handle, (int)Math.Round(Dock.ReservedHeight * Scale));
    }

    public static bool IsDesktopWindow(IntPtr h) => Desktops.Any(d => d.Handle == h);

    static void Post(Action a) => UI.BeginInvoke(a);

    /// <summary>Global shortcut dispatch (runs inside the keyboard hook: decide fast, post the work).</summary>
    static bool OnCombo(int vk, Mods mods)
    {
        if (mods == Mods.Win)
        {
            switch (vk)
            {
                case VK_SPACE: Post(SpotlightWindow.Toggle); return true;
                case 0x45: /*E*/ Post(() => OpenFinder(null)); return true;
                case 0x44: /*D*/ Post(ToggleShowDesktop); return true;
                case VK_UP: Post(MissionControlWindow.Toggle); return true;
                case VK_OEM_COMMA: Post(() => SettingsWindow.ShowPane(null)); return true;
            }
        }
        if (mods == (Mods.Win | Mods.Shift))
        {
            if (vk == 0x33) { Post(() => Screenshot.CaptureFullScreen()); return true; }
            if (vk == 0x34) { Post(() => Screenshot.CaptureArea()); return true; }
        }
        if (mods == (Mods.Ctrl | Mods.Alt) && vk == VK_ESCAPE) { Post(ForceQuitWindow.ShowWindow); return true; }
        if (mods == (Mods.Win | Mods.Alt) && vk == VK_ESCAPE) { Post(ForceQuitWindow.ShowWindow); return true; }
        if (mods == (Mods.Ctrl | Mods.Alt | Mods.Shift) && vk == 0x51) { Post(Quit); return true; }
        return false;
    }

    /// <summary>
    /// Scriptable entry point: "MacShell.exe --open launchpad" etc.
    /// finder[:path] | launchpad | spotlight[:query] | missioncontrol | settings[:pane] | controlcenter |
    /// notifications | about | forcequit | screenshot | area | menu:N | quit
    /// </summary>
    public static void RunCommand(string cmd)
    {
        string arg = null;
        int colon = cmd.IndexOf(':');
        if (colon > 0 && !(cmd.Length > colon + 1 && cmd[colon + 1] == '\\')) { arg = cmd[(colon + 1)..]; cmd = cmd[..colon]; }
        else if (colon == 1) { arg = cmd; cmd = "finder"; } // a bare path like C:\Users
        switch (cmd.ToLowerInvariant())
        {
            case "finder": OpenFinder(string.IsNullOrEmpty(arg) ? null : arg); break;
            case "launchpad": LaunchpadWindow.Toggle(); break;
            case "spotlight": SpotlightWindow.Toggle(); if (arg != null) SpotlightWindow.SetQuery(arg); break;
            case "missioncontrol": MissionControlWindow.Toggle(); break;
            case "settings": SettingsWindow.ShowPane(arg); break;
            case "controlcenter": ControlCenterWindow.Toggle(null); break;
            case "notifications": NotificationCenterWindow.Toggle(null); break;
            case "about": AboutWindow.ShowWindow(); break;
            case "forcequit": ForceQuitWindow.ShowWindow(); break;
            case "screenshot": Screenshot.CaptureFullScreen(); break;
            case "area": Screenshot.CaptureArea(); break;
            case "switcher": AppSwitcherWindow.Begin(false); break;
            case "switcherend": AppSwitcherWindow.Cancel(); break;
            case "menu": if (int.TryParse(arg, out int m)) MenuBar?.OpenMenu(m); break;
            case "menuclose": MenuBar?.OpenMenu(-1); break;
            case "offscreen": Offscreen = arg == "on"; break;
            case "delay": { var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; t.Tick += (_, _) => { t.Stop(); RunCommand(arg); }; t.Start(); break; }
            case "tray": if (arg == "on") TrayHost.Start(MenuBar.TrayIconRect); else TrayHost.Stop(); break;
            case "trayclick":   // diagnostics: trayclick:<pid>[:right]
                {
                    var parts = (arg ?? "").Split(':');
                    var icon = TrayHost.Icons.FirstOrDefault(i => i.ProcessId.ToString() == parts[0]);
                    if (icon != null) TrayHost.Click(icon, parts.Length > 1 && parts[1] == "right", false, new Point(0, 0));
                    break;
                }
            case "dumptray":
                File.WriteAllText(Path.Combine(Settings.DataDirectory, "tray.txt"), string.Join(Environment.NewLine,
                    TrayHost.Icons.Select(i => $"pid={i.ProcessId} exe={ExeName(i.ProcessId)} v={i.Version} hidden={i.Hidden} img={i.Image?.PixelWidth} cb=0x{i.CallbackMessage:X}"))
                    + Environment.NewLine + $"running={TrayHost.Running} first={FindWindow("Shell_TrayWnd", null) == TrayHost.Hwnd}");
                break;
            case "theme": Settings.Current.Appearance = arg; Settings.Save(); Theme.Apply(); break;
            case "snap": Snapshot(arg); break;
            case "dumpdock":
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in Settings.Current.DockApps ?? new List<PinnedApp>())
                        sb.AppendLine($"pin  {p.Name} | {p.Target} | key={WindowTracker.KeyForTarget(p.Target, p.ExePath)}");
                    foreach (var line in Dock?.DescribeItems() ?? Enumerable.Empty<string>()) sb.AppendLine(line);
                    File.WriteAllText(Path.Combine(Settings.DataDirectory, "dock.txt"), sb.ToString());
                    break;
                }
            case "dumpapps":
                {
                    // diagnostics: app identities only (no window titles)
                    var sb = new System.Text.StringBuilder();
                    foreach (var w in WindowTracker.Windows)
                        sb.AppendLine($"window key={w.AppKey} aumid={w.Aumid} exe={Path.GetFileName(w.ExePath ?? "")}");
                    foreach (var a in AppCatalog.Apps.Where(a => a.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("BDS", StringComparison.OrdinalIgnoreCase)))
                        sb.AppendLine($"catalog name={a.Name} parsing={a.ParsingName} target={a.TargetPath}");
                    File.WriteAllText(Path.Combine(Settings.DataDirectory, "apps.txt"), sb.ToString());
                    break;
                }
            case "getinfo":
                if (File.Exists(arg)) GetInfoWindow.ShowFor(FileItem.FromInfo(new FileInfo(arg)));
                else if (Directory.Exists(arg)) GetInfoWindow.ShowFor(FileItem.FromInfo(new DirectoryInfo(arg)));
                break;
            case "quicklook":
                if (File.Exists(arg)) QuickLookWindow.Toggle(null, FileItem.FromInfo(new FileInfo(arg)));
                break;
            case "closeall":
                foreach (Window w in Application.Current.Windows.Cast<Window>().ToList())
                    if (w is Controls.MacWindow) w.Close();
                break;
            case "desktop": ToggleShowDesktop(); break;
            case "quit": Quit(); break;
        }
    }

    /// <summary>Renders a MacShell window's WPF content to %APPDATA%\MacShell\snap.png (for automated visual checks).</summary>
    static void Snapshot(string title)
    {
        var w = Application.Current.Windows.Cast<Window>().FirstOrDefault(x => x.Title == title);
        if (w?.Content is not FrameworkElement fe || fe.ActualWidth < 1) return;
        double s = 1.5;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = Theme.Res(w is Controls.MacWindow ? "SidebarBackgroundBrush" : "WindowBackgroundBrush");
            dc.DrawRectangle(bg, null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(fe), null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(fe.ActualWidth * s), (int)(fe.ActualHeight * s), 96 * s, 96 * s, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(Settings.DataDirectory, "snap.png"));
        enc.Save(fs);
    }

    // ------------------------------------------------------------------ internal apps

    public static void OpenInternal(string key)
    {
        switch (key)
        {
            case "internal:finder": OpenFinderOrActivate(); break;
            case "internal:launchpad": LaunchpadWindow.Toggle(); break;
            case "internal:settings": SettingsWindow.ShowPane(null); break;
            case "internal:missioncontrol": MissionControlWindow.Toggle(); break;
            case "internal:trash": OpenFinder(FinderLocation.Trash); break;
            case "internal:downloads": OpenFinder(GetKnownFolder(FOLDERID_Downloads)); break;
        }
    }

    public static void QuitInternal(string key)
    {
        if (key == "internal:settings") SettingsWindow.Instance?.Close();
        else if (key == WindowTracker.FinderKey) foreach (var w in FinderWindow.All.ToList()) w.Close();
    }

    public static void OpenFinderOrActivate()
    {
        var wins = FinderWindow.All.ToList();
        if (wins.Count == 0) { OpenFinder(null); return; }
        var app = WindowTracker.FindByKey(WindowTracker.FinderKey);
        if (app != null && app.Windows.Count > 0) WindowTracker.ActivateApp(app);
        else { var w = wins.Last(); if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); }
    }

    public static FinderWindow OpenFinder(string path)
    {
        var w = new FinderWindow(path);
        w.Show();
        if (!Offscreen) w.Activate();
        return w;
    }

    public static void RevealInFinder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        string dir = Directory.Exists(path) && !File.Exists(path) ? Path.GetDirectoryName(path.TrimEnd('\\')) : Path.GetDirectoryName(path);
        if (dir == null) { OpenFinder(path); return; }
        var w = OpenFinder(dir);
        w.SelectPathWhenLoaded(path);
    }

    public static string Alert(string title, string message, params string[] buttons)
    {
        var a = new MacAlert(title, message, buttons.Length == 0 ? new[] { "OK" } : buttons);
        a.ShowDialog();
        return a.Result;
    }

    public static void ShowAlert(string title, string message) => UI.BeginInvoke(() => Alert(title, message, "OK"));

    // ------------------------------------------------------------------ desktop / windows

    public static void ToggleShowDesktop()
    {
        if (_shownDesktopMinimized != null && _shownDesktopMinimized.Count > 0)
        {
            foreach (var h in _shownDesktopMinimized.AsEnumerable().Reverse())
                if (IsWindow(h) && IsIconic(h)) ShowWindow(h, SW_SHOWNOACTIVATE);
            _shownDesktopMinimized = null;
            return;
        }
        _shownDesktopMinimized = new List<IntPtr>();
        foreach (var w in WindowTracker.Windows.Where(w => !w.Minimized))
        {
            ShowWindow(w.Hwnd, SW_SHOWMINNOACTIVE);
            _shownDesktopMinimized.Add(w.Hwnd);
        }
        Desktops.FirstOrDefault(d => d.IsPrimary)?.Activate();
    }

    static void CheckFullscreen()
    {
        if (MenuBar == null || Dock == null) return;
        var fg = GetForegroundWindow();
        bool fs = false;
        if (fg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != (uint)Environment.ProcessId)
            {
                string cls = GetClassNameOf(fg);
                if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd") && IsWindowVisible(fg) && !IsIconic(fg))
                {
                    GetWindowRect(fg, out RECT r);
                    var mi = NativeMethods.GetMonitorInfo(MonitorFromWindow(fg, 2));
                    var m = mi.rcMonitor;
                    fs = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom
                         && (mi.dwFlags & 1) != 0;
                }
            }
        }
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        MenuBar.SetHiddenForFullscreen(fs);
        Dock.SetHiddenForFullscreen(fs);
    }

    /// <summary>
    /// Windows' own sound controls (output devices + per-app volume mixer): Win+Ctrl+V opens Quick Settings
    /// straight on its sound page on Windows 11.
    /// </summary>
    public static void OpenWindowsSoundFlyout()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) => { t.Stop(); SendKeys(0x5B, 0x11, 0x56); };
        t.Start();
    }

    /// <summary>Sends a keyboard shortcut to the frontmost external app (used by generic app menus).</summary>
    public static void SendToApp(params ushort[] keys)
    {
        var target = WindowTracker.LastExternalForeground;
        if (target != IntPtr.Zero && IsWindow(target))
        {
            ActivateWindow(target);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            t.Tick += (_, _) => { t.Stop(); SendKeys(keys); };
            t.Start();
        }
    }

    // ------------------------------------------------------------------ power

    public static void Sleep() { try { SetSuspendState(false, false, false); } catch { } }
    public static void Lock() => LockWorkStation();

    public static void ConfirmRestart()
    {
        if (Alert("Are you sure you want to restart your computer now?", "If you do nothing, the computer will restart when you click Restart.", "Cancel", "Restart") == "Restart")
            RunQuiet("shutdown", "/r /t 0");
    }

    public static void ConfirmShutDown()
    {
        if (Alert("Are you sure you want to shut down your computer now?", "Any open applications will be asked to quit.", "Cancel", "Shut Down") == "Shut Down")
            RunQuiet("shutdown", "/s /t 0");
    }

    public static void ConfirmLogOut()
    {
        if (Alert($"Are you sure you want to quit all applications and log out now?", "Any unsaved changes will be lost.", "Cancel", "Log Out") == "Log Out")
        {
            Takeover.Release();
            ExitWindowsEx(0, 0);
        }
    }

    public static void ConfirmExitToWindows()
    {
        if (Alert("Return to Windows?", "MacShell will quit and the Windows taskbar and desktop will be restored. Your apps keep running.", "Cancel", "Return to Windows") == "Return to Windows")
            Quit();
    }

    static void RunQuiet(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }

    public static void Quit()
    {
        try { Settings.SaveNow(); } catch { }
        try { WindowGuard.Release(); } catch { }
        try { _hook?.Dispose(); } catch { }
        try { WindowTracker.Stop(); } catch { }
        try { TrayHost.Stop(); } catch { }
        try { Takeover.Release(); } catch { }
        Application.Current.Shutdown();
    }
}
