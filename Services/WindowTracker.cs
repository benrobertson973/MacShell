using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacShell.Native;
using static MacShell.Native.NativeMethods;

namespace MacShell.Services;

public class TrackedWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; }
    public uint Pid { get; init; }
    public string ExePath { get; init; }
    public string Aumid { get; init; }
    public string AppKey { get; set; }
    public bool Minimized { get; set; }
    public bool IsInternal { get; init; }
}

public class RunningApp
{
    public string Key { get; init; }
    public string Name { get; set; }
    public string ExePath { get; set; }
    public string Aumid { get; set; }
    public AppEntry Catalog { get; set; }
    public string LaunchTarget { get; set; }
    public string IconSource { get; set; }
    public bool IsInternal { get; set; }
    public List<TrackedWindow> Windows { get; } = new();
    public HashSet<uint> Pids { get; } = new();
}

/// <summary>
/// Tracks top-level application windows (the same set Alt+Tab would show), groups them into
/// apps, and follows the foreground window so the menu bar and Dock stay in sync.
/// </summary>
public static class WindowTracker
{
    public static List<TrackedWindow> Windows { get; private set; } = new();
    public static Dictionary<string, RunningApp> Apps { get; private set; } = new();
    public static RunningApp ActiveApp { get; private set; }
    public static IntPtr LastExternalForeground { get; private set; }
    public static IntPtr LastForeground { get; private set; }
    public static event Action AppsChanged;
    public static event Action ActiveAppChanged;
    public static event Action ForegroundChanged;

    static readonly Dictionary<IntPtr, string> Internal = new();
    static readonly HashSet<IntPtr> Chrome = new();   // shell chrome: menubar, dock, popups...
    static readonly Dictionary<uint, string> PidPaths = new();
    static readonly Dictionary<IntPtr, string> AumidCache = new();
    static readonly Dictionary<uint, string> PidAumids = new();

    static string ProcessAumid(uint pid)
    {
        if (!PidAumids.TryGetValue(pid, out var id)) PidAumids[pid] = id = GetProcessAumid(pid);
        return id;
    }
    static readonly List<IntPtr> Recency = new();
    static readonly WinEventDelegate _evProc = OnWinEvent;
    static readonly List<IntPtr> _hooks = new();
    static DispatcherTimer _timer, _debounce;
    static string _lastSignature = "";
    static readonly uint MyPid = (uint)Environment.ProcessId;

    public const string FinderKey = "internal:finder";

    static readonly HashSet<string> IgnoredClasses = new()
    {
        "Windows.UI.Core.CoreWindow", "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging",
        "MultitaskingViewFrame", "EdgeUiInputTopWndClass", "NotifyIconOverflowWindow", "Windows.Internal.Shell.TabProxyWindow",
        "ApplicationManager_ImmersiveShellWindow", "Shell_InputSwitchTopLevelWindow", "PseudoConsoleWindow"
    };

    public static void RegisterInternal(Window w, string appKey)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        Internal[h] = appKey;
        w.Closed += (_, _) => { Internal.Remove(h); ScheduleRefresh(); };
        ScheduleRefresh();
    }

    public static void RegisterChrome(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        Chrome.Add(h);
        w.Closed += (_, _) => Chrome.Remove(h);
    }

    public static bool IsChrome(IntPtr h) => Chrome.Contains(h);

    public static void Start()
    {
        foreach (var (a, b) in new[] { (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND), (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                                        (EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND),
                                        (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE), (EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE),
                                        (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED) })
            _hooks.Add(SetWinEventHook(a, b, IntPtr.Zero, _evProc, 0, 0, WINEVENT_OUTOFCONTEXT));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        Refresh();
        OnForeground(GetForegroundWindow());
    }

    public static void Stop()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        _timer?.Stop();
    }

    public static void ScheduleRefresh()
    {
        if (_debounce == null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0) return; // only window objects
        if (ev == EVENT_OBJECT_SHOW) { Takeover.OnWindowShown(hwnd); WindowGuard.OnWindowShown(hwnd); }
        if (ev == EVENT_SYSTEM_MOVESIZESTART) { WindowGuard.OnMoveSizeStart(hwnd); return; }
        if (ev == EVENT_SYSTEM_MOVESIZEEND) { WindowGuard.OnMoveSizeEnd(hwnd); return; }
        if (ev == EVENT_SYSTEM_FOREGROUND) OnForeground(hwnd);
        else if (ev == EVENT_OBJECT_NAMECHANGE)
        {
            var w = Windows.FirstOrDefault(x => x.Hwnd == hwnd);
            if (w != null) ScheduleRefresh();
        }
        else ScheduleRefresh();
    }

    static void OnForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        if (root != IntPtr.Zero) hwnd = root;
        LastForeground = hwnd;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == MyPid)
        {
            if (Chrome.Contains(hwnd)) return;  // clicking the menu bar/dock does not change the active app
            if (!Internal.ContainsKey(hwnd) && !ShellHost.IsDesktopWindow(hwnd)) return;
        }
        else
        {
            string cls = GetClassNameOf(hwnd);
            if (IgnoredClasses.Contains(cls) || cls.StartsWith("Shell_")) return;
            LastExternalForeground = hwnd;
        }
        Recency.Remove(hwnd);
        Recency.Insert(0, hwnd);
        if (Recency.Count > 200) Recency.RemoveAt(Recency.Count - 1);

        ForegroundChanged?.Invoke();
        var app = ResolveApp(hwnd, pid);
        if (app?.Key != ActiveApp?.Key || app?.Name != ActiveApp?.Name)
        {
            ActiveApp = app;
            ActiveAppChanged?.Invoke();
        }
        else ActiveApp = app;
        ScheduleRefresh();
    }

    static RunningApp ResolveApp(IntPtr hwnd, uint pid)
    {
        if (pid == MyPid)
        {
            string key = Internal.TryGetValue(hwnd, out var k) ? k : FinderKey;
            return Apps.TryGetValue(key, out var existing) ? existing : MakeInternalApp(key);
        }
        foreach (var a in Apps.Values) if (a.Windows.Any(w => w.Hwnd == hwnd)) return a;
        var tw = Describe(hwnd, pid);
        if (tw == null) return null;
        var app = MakeApp(tw);
        app.Windows.Add(tw);
        return app;
    }

    public static RunningApp MakeInternalApp(string key) => new()
    {
        Key = key,
        Name = key switch { "internal:settings" => "System Settings", _ => "Finder" },
        IsInternal = true,
        LaunchTarget = key,
    };

    static bool IsAppWindow(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        long ex = GetExStyle(h);
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (IsCloaked(h)) return false;
        if (GetWindowTextLength(h) == 0) return false;
        string cls = GetClassNameOf(h);
        if (IgnoredClasses.Contains(cls)) return false;
        GetWindowRect(h, out RECT r);
        if (r.Width <= 1 || r.Height <= 1) return false;
        return true;
    }

    static TrackedWindow Describe(IntPtr h, uint pid)
    {
        if (!PidPaths.TryGetValue(pid, out string exe))
        {
            exe = GetProcessPath(pid);
            PidPaths[pid] = exe;
        }
        if (!AumidCache.TryGetValue(h, out string aumid))
        {
            // 1) an ID set on the window itself (Chromium profiles / PWAs), 2) the ID of a packaged process
            aumid = GetAppUserModelId(h) ?? ProcessAumid(pid);
            bool frameHost = exe != null && exe.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            if (aumid == null && frameHost)
            {
                // Store (UWP) apps: the frame belongs to ApplicationFrameHost; the app itself is its CoreWindow child
                var core = FindCoreWindow(h);
                if (core != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(core, out uint cpid);
                    aumid = ProcessAumid(cpid);
                }
                // suspended apps detach their CoreWindow: fall back to the window title = app name
                aumid ??= AppCatalog.Apps.FirstOrDefault(a => a.IsPackaged && string.Equals(a.Name, GetWindowTitle(h), StringComparison.OrdinalIgnoreCase))?.ParsingName;
            }
            // cache the answer, except an unresolved frame (its CoreWindow may attach later)
            if (aumid != null || !frameHost) AumidCache[h] = aumid;
        }
        var tw = new TrackedWindow { Hwnd = h, Pid = pid, ExePath = exe, Aumid = aumid, Title = GetWindowTitle(h), Minimized = IsIconic(h) };
        tw.AppKey = KeyFor(aumid, exe);
        return tw;
    }

    static readonly Dictionary<string, (string aumid, string target)> _shortcutInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// App identity, the way the Windows taskbar groups windows: an explicit AppUserModelID wins (so a browser
    /// PWA like "Brave._crx_…" is its own app), otherwise the executable.
    /// </summary>
    public static string KeyFor(string aumid, string exe)
    {
        if (!string.IsNullOrEmpty(aumid))
        {
            var e = AppCatalog.FindByParsingName(aumid);
            if (e != null) return "app:" + e.ParsingName.ToLowerInvariant();
            // PWAs and packaged apps run inside another exe but are apps of their own
            if (aumid.Contains("_crx_", StringComparison.OrdinalIgnoreCase) || aumid.Contains('!') ||
                exe == null || exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                return "aumid:" + aumid.ToLowerInvariant();
        }
        if (!string.IsNullOrEmpty(exe))
        {
            var e = AppCatalog.FindByExe(exe);
            if (e != null) return "app:" + e.ParsingName.ToLowerInvariant();
            return "exe:" + exe.ToLowerInvariant();
        }
        return "aumid:" + (aumid ?? "?").ToLowerInvariant();
    }

    /// <summary>Identity key for a Dock/Launchpad target so running windows can be matched to pins.</summary>
    public static string KeyForTarget(string target, string exePath = null)
    {
        if (string.IsNullOrEmpty(target)) return null;
        if (target.StartsWith("internal:")) return target;
        var e = AppCatalog.FindByParsingName(target);
        if (e != null) return "app:" + e.ParsingName.ToLowerInvariant();
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            // shortcuts (PWAs on the desktop, Start-menu links) carry the app's ID or point at its exe
            if (!_shortcutInfo.TryGetValue(target, out var info))
                _shortcutInfo[target] = info = (GetShortcutAppId(target), Finder.FileOps.ResolveAlias(target));
            if (info.aumid != null) return KeyFor(info.aumid, info.target);
            if (info.target != null && File.Exists(info.target)) return KeyFor(null, info.target);
            return "exe:" + target.ToLowerInvariant();
        }
        if (File.Exists(target)) return KeyFor(null, target);
        if (!string.IsNullOrEmpty(exePath)) return KeyFor(null, exePath);
        return "app:" + target.ToLowerInvariant();
    }
    static RunningApp MakeApp(TrackedWindow w)
    {
        var app = new RunningApp { Key = w.AppKey, ExePath = w.ExePath, Aumid = w.Aumid };
        AppEntry entry = null;
        if (w.AppKey.StartsWith("app:"))
        {
            entry = AppCatalog.Apps.FirstOrDefault(a => ("app:" + a.ParsingName.ToLowerInvariant()) == w.AppKey);
        }
        app.Catalog = entry;
        if (entry != null)
        {
            app.Name = entry.Name;
            app.LaunchTarget = entry.ParsingName;
            app.IconSource = entry.IconSource;
        }
        else
        {
            app.Name = FriendlyExeName(w.ExePath) ?? w.Title;
            app.LaunchTarget = w.ExePath;
            app.IconSource = w.ExePath;
        }
        return app;
    }

    static readonly Dictionary<string, string> FriendlyCache = new(StringComparer.OrdinalIgnoreCase);
    public static string FriendlyExeName(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        if (FriendlyCache.TryGetValue(exe, out var n)) return n;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            n = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
              : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim()
              : Path.GetFileNameWithoutExtension(exe);
            if (n.Length > 32) n = !string.IsNullOrWhiteSpace(vi.ProductName) && vi.ProductName.Length < n.Length ? vi.ProductName.Trim() : Path.GetFileNameWithoutExtension(exe);
        }
        catch { n = Path.GetFileNameWithoutExtension(exe); }
        FriendlyCache[exe] = n;
        return n;
    }

    public static void Refresh()
    {
        var list = new List<TrackedWindow>();
        var seen = new HashSet<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == MyPid)
            {
                if (Internal.TryGetValue(h, out var key) && IsWindowVisible(h))
                    list.Add(new TrackedWindow { Hwnd = h, Pid = pid, Title = GetWindowTitle(h), AppKey = key, Minimized = IsIconic(h), IsInternal = true });
                return true;
            }
            if (!IsAppWindow(h)) return true;
            var tw = Describe(h, pid);
            list.Add(tw);
            seen.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (var k in AumidCache.Keys.Where(k => !seen.Contains(k)).ToList()) AumidCache.Remove(k);
        var livePids = list.Select(w => w.Pid).ToHashSet();
        foreach (var k in PidAumids.Keys.Where(k => !livePids.Contains(k)).ToList()) PidAumids.Remove(k);
        foreach (var k in PidPaths.Keys.Where(k => !livePids.Contains(k)).ToList()) PidPaths.Remove(k);

        var apps = new Dictionary<string, RunningApp>();
        foreach (var w in list)
        {
            if (!apps.TryGetValue(w.AppKey, out var app))
            {
                app = w.IsInternal ? MakeInternalApp(w.AppKey) : MakeApp(w);
                apps[w.AppKey] = app;
            }
            app.Windows.Add(w);
            app.Pids.Add(w.Pid);
        }
        // Finder is always running, like on a Mac.
        if (!apps.ContainsKey(FinderKey)) apps[FinderKey] = MakeInternalApp(FinderKey);

        foreach (var w in list) if (!w.IsInternal && !w.Minimized) WindowGuard.KeepBelowMenuBar(w.Hwnd);

        string sig = string.Join("|", list.Select(w => $"{w.Hwnd}:{w.AppKey}:{w.Minimized}:{w.Title}"));
        Windows = list;
        WindowGuard.NoticeWindows(list.Where(w => !w.IsInternal).Select(w => w.Hwnd));
        Apps = apps;
        if (ActiveApp != null && apps.TryGetValue(ActiveApp.Key, out var refreshed)) ActiveApp = refreshed;
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            AppsChanged?.Invoke();
        }
    }

    /// <summary>Windows of an app ordered by most recently focused first.</summary>
    public static List<TrackedWindow> OrderedWindows(RunningApp app)
    {
        return app.Windows.OrderBy(w => { int i = Recency.IndexOf(w.Hwnd); return i < 0 ? int.MaxValue : i; }).ToList();
    }

    /// <summary>Running apps ordered by recency (for ⌘Tab).</summary>
    public static List<RunningApp> AppsByRecency()
    {
        return Apps.Values.Where(a => a.Windows.Count > 0 || a.Key == FinderKey)
            .OrderBy(a =>
            {
                int best = int.MaxValue;
                foreach (var w in a.Windows) { int i = Recency.IndexOf(w.Hwnd); if (i >= 0 && i < best) best = i; }
                if (a.Key == FinderKey && best == int.MaxValue) best = int.MaxValue - 1;
                return best;
            }).ToList();
    }

    public static void ActivateApp(RunningApp app)
    {
        if (app == null) return;
        if (app.IsInternal && app.Windows.Count == 0) { ShellHost.OpenInternal(app.Key); return; }
        var ordered = OrderedWindows(app);
        if (ordered.Count == 0) return;
        bool allMin = ordered.All(w => w.Minimized);
        if (allMin)
        {
            foreach (var w in ordered.AsEnumerable().Reverse()) ShowWindow(w.Hwnd, SW_RESTORE);
        }
        else
        {
            // bring all non-minimized windows of the app forward, most recent last so it ends on top
            foreach (var w in ordered.Where(x => !x.Minimized).Skip(1).Reverse())
                SetWindowPos(w.Hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        var target = ordered.FirstOrDefault(w => !w.Minimized) ?? ordered[0];
        ActivateWindow(target.Hwnd);
    }

    public static void HideApp(RunningApp app)
    {
        if (app == null) return;
        foreach (var w in app.Windows) ShowWindow(w.Hwnd, SW_MINIMIZE);
        ScheduleRefresh();
    }

    public static void QuitApp(RunningApp app)
    {
        if (app == null) return;
        if (app.IsInternal) { ShellHost.QuitInternal(app.Key); return; }
        foreach (var w in app.Windows) PostMessage(w.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        ScheduleRefresh();
    }

    public static void ForceQuitApp(RunningApp app)
    {
        if (app == null || app.IsInternal) return;
        foreach (var pid in app.Pids.Count > 0 ? app.Pids : app.Windows.Select(w => w.Pid).ToHashSet())
        {
            try { Process.GetProcessById((int)pid).Kill(); } catch { }
        }
        ScheduleRefresh();
    }

    public static RunningApp FindByKey(string key) => key != null && Apps.TryGetValue(key, out var a) ? a : null;
}
