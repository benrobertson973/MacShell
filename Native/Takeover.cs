using System.Runtime.InteropServices;
using static MacShell.Native.NativeMethods;

namespace MacShell.Native;

/// <summary>
/// Takes over the Windows desktop without fighting Explorer:
///  • the Windows taskbar is switched to auto-hide (so it stops reserving screen space) and hidden;
///    the user's original setting is saved to disk and restored on exit (or by --restore after a crash);
///  • the menu bar and Dock are registered as real app bars, so Explorer itself computes the work area
///    (maximised windows fit between them) — no periodic SPI_SETWORKAREA broadcasts that make apps resize;
///  • the Explorer desktop (Progman/WorkerW) is hidden behind MacShell's own desktop.
/// </summary>
public static class Takeover
{
    static readonly List<IntPtr> _hidden = new();
    static readonly HashSet<IntPtr> _trays = new();
    static int _originalTaskbarState = -1;
    static bool _hideTaskbar;
    static RECT _fallbackSavedWorkArea;
    static bool _fallbackUsed;

    public static bool Engaged { get; private set; }
    public static bool TaskbarTemporarilyShown { get; private set; }
    public static uint AppBarCallbackMessage { get; } = RegisterWindowMessage("MacShell.AppBarNotify");
    public static uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    /// <summary>Explorer restarted: its app-bar table and taskbar windows are new, so register/hide again.</summary>
    public static void OnExplorerRestarted()
    {
        TrayHost.OnExplorerRestarted();
        if (!Engaged) return;
        foreach (var b in _bars.Values) { b.Registered = false; b.Thickness = -1; }
        _trays.Clear();
        _hidden.RemoveAll(h => !IsWindow(h));
        if (_hideTaskbar && _originalTaskbarState >= 0) SetTaskbarState(ABS_AUTOHIDE | (_originalTaskbarState & ABS_ALWAYSONTOP));
        HideShellWindows();
    }

    static readonly Dictionary<IntPtr, AppBar> _bars = new();
    class AppBar { public uint Edge; public int Thickness = -1; public RECT Last; public bool Registered; }

    static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell", "taskbar-state.txt");

    static IntPtr Tray => TrayHost.ExplorerTray;
    static bool ExplorerRunning => Tray != IntPtr.Zero;

    static IEnumerable<IntPtr> TopLevel(string cls)
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            if (!TrayHost.IsOurs(h)) yield return h;   // MacShell's own hidden Shell_TrayWnd (menu-bar tray icons)
    }

    static IEnumerable<IntPtr> TrayWindows() => TopLevel("Shell_TrayWnd").Concat(TopLevel("Shell_SecondaryTrayWnd"));
    static IEnumerable<IntPtr> DesktopWindows() => TopLevel("Progman").Concat(TopLevel("WorkerW"));

    // ------------------------------------------------------------------ engage / release

    public static void Engage(bool hideTaskbar)
    {
        Engaged = true;
        _hideTaskbar = hideTaskbar;
        if (hideTaskbar && ExplorerRunning) SetTaskbarAutoHide(true);
        HideShellWindows();
    }

    static void HideShellWindows()
    {
        foreach (var h in TrayWindows())
        {
            _trays.Add(h);
            if (!_hideTaskbar || TaskbarTemporarilyShown) continue;
            if (IsWindowVisible(h)) { ShowWindow(h, SW_HIDE); if (!_hidden.Contains(h)) _hidden.Add(h); }
        }
        foreach (var h in DesktopWindows())
        {
            if (IsWindowVisible(h)) { ShowWindow(h, SW_HIDE); if (!_hidden.Contains(h)) _hidden.Add(h); }
        }
    }

    /// <summary>Periodic safety net: Explorer occasionally re-shows its windows.</summary>
    public static void Maintain()
    {
        if (!Engaged) return;
        HideShellWindows();
    }

    /// <summary>Called for every EVENT_OBJECT_SHOW: re-hide the taskbar the instant Explorer shows it.</summary>
    public static void OnWindowShown(IntPtr hwnd)
    {
        if (!Engaged || !_hideTaskbar || TaskbarTemporarilyShown) return;
        if (_trays.Contains(hwnd)) ShowWindow(hwnd, SW_HIDE);
    }

    public static void ToggleTaskbar()
    {
        TaskbarTemporarilyShown = !TaskbarTemporarilyShown;
        if (TaskbarTemporarilyShown)
        {
            foreach (var h in TrayWindows()) ShowWindow(h, SW_SHOWNA);
        }
        else HideShellWindows();
    }

    public static void Release()
    {
        if (!Engaged) return;
        Engaged = false;
        foreach (var h in _bars.Keys.ToList()) RemoveAppBar(h);
        foreach (var h in _hidden) if (IsWindow(h)) ShowWindow(h, SW_SHOWNA);
        _hidden.Clear();
        foreach (var h in TrayWindows()) if (!IsWindowVisible(h)) ShowWindow(h, SW_SHOWNA);
        foreach (var h in TopLevel("Progman")) if (!IsWindowVisible(h)) ShowWindow(h, SW_SHOWNA);
        SetTaskbarAutoHide(false);
        if (_fallbackUsed)
        {
            var r = _fallbackSavedWorkArea;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);
        }
    }

    /// <summary>"MacShell --restore": undo everything after a crash.</summary>
    public static void EmergencyRestore()
    {
        foreach (var h in TrayWindows()) ShowWindow(h, SW_SHOWNA);
        foreach (var h in TopLevel("Progman")) ShowWindow(h, SW_SHOWNA);
        foreach (var h in TopLevel("WorkerW"))
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) ShowWindow(h, SW_SHOWNA);
        try
        {
            if (File.Exists(StateFile) && int.TryParse(File.ReadAllText(StateFile).Trim(), out int saved))
            {
                SetTaskbarState(saved);
                File.Delete(StateFile);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ taskbar auto-hide

    /// <summary>SHAppBarMessage, delivered straight to Explorer even while the menu-bar tray host is in front of it.</summary>
    static UIntPtr AppBarMsg(uint msg, ref APPBARDATA data)
    {
        var copy = data;
        UIntPtr r = UIntPtr.Zero;
        TrayHost.StepAside(() => r = SHAppBarMessage(msg, ref copy));
        data = copy;
        return r;
    }

    static APPBARDATA NewData(IntPtr hwnd) => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    static int GetTaskbarState()
    {
        var abd = NewData(Tray);
        return (int)AppBarMsg(ABM_GETSTATE, ref abd).ToUInt32();
    }

    static void SetTaskbarState(int state)
    {
        var abd = NewData(Tray);
        abd.lParam = new IntPtr(state);
        AppBarMsg(ABM_SETSTATE, ref abd);
    }

    static void SetTaskbarAutoHide(bool on)
    {
        try
        {
            if (on)
            {
                if (_originalTaskbarState < 0)
                {
                    // A leftover file means the previous session crashed while the taskbar was auto-hidden:
                    // its value (not the current state) is the user's real preference.
                    if (File.Exists(StateFile) && int.TryParse(File.ReadAllText(StateFile).Trim(), out int saved)) _originalTaskbarState = saved;
                    else
                    {
                        _originalTaskbarState = GetTaskbarState();
                        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                        File.WriteAllText(StateFile, _originalTaskbarState.ToString());
                    }
                }
                SetTaskbarState(ABS_AUTOHIDE | (_originalTaskbarState & ABS_ALWAYSONTOP));
            }
            else if (_originalTaskbarState >= 0)
            {
                SetTaskbarState(_originalTaskbarState);
                _originalTaskbarState = -1;
                if (File.Exists(StateFile)) File.Delete(StateFile);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ app bars

    /// <summary>
    /// Reserves <paramref name="topPx"/> at the top (menu bar) and <paramref name="bottomPx"/> at the bottom (Dock)
    /// of the primary display. Only talks to Explorer when a thickness actually changes.
    /// </summary>
    public static void Reserve(IntPtr menuBar, int topPx, IntPtr dock, int bottomPx)
    {
        if (!Engaged) return;
        if (!ExplorerRunning)
        {
            FallbackWorkArea(topPx, bottomPx);
            return;
        }
        SetBar(menuBar, ABE_TOP, topPx);
        SetBar(dock, ABE_BOTTOM, bottomPx);
    }

    static void SetBar(IntPtr hwnd, uint edge, int px)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!_bars.TryGetValue(hwnd, out var bar)) _bars[hwnd] = bar = new AppBar { Edge = edge };
        if (px <= 0)
        {
            RemoveAppBar(hwnd);
            _bars[hwnd] = new AppBar { Edge = edge, Thickness = 0 };
            return;
        }
        if (bar.Registered && bar.Thickness == px) return;
        if (!bar.Registered)
        {
            var abd = NewData(hwnd);
            abd.uCallbackMessage = AppBarCallbackMessage;
            AppBarMsg(ABM_NEW, ref abd);   // returns FALSE if already registered, which is fine
            bar.Registered = true;
        }
        bar.Thickness = px;
        Position(hwnd, bar, force: true);
    }

    static void Position(IntPtr hwnd, AppBar bar, bool force)
    {
        var mon = GetMonitorInfo(PrimaryMonitor()).rcMonitor;
        var abd = NewData(hwnd);
        abd.uEdge = bar.Edge;
        abd.rc = mon;
        if (bar.Edge == ABE_TOP) abd.rc.Bottom = mon.Top + bar.Thickness; else abd.rc.Top = mon.Bottom - bar.Thickness;
        AppBarMsg(ABM_QUERYPOS, ref abd);
        if (bar.Edge == ABE_TOP) abd.rc.Bottom = abd.rc.Top + bar.Thickness; else abd.rc.Top = abd.rc.Bottom - bar.Thickness;
        // ABN_POSCHANGED is broadcast to every app bar after any SETPOS; skip no-op updates to avoid ping-pong.
        if (!force && abd.rc.Left == bar.Last.Left && abd.rc.Top == bar.Last.Top && abd.rc.Right == bar.Last.Right && abd.rc.Bottom == bar.Last.Bottom) return;
        AppBarMsg(ABM_SETPOS, ref abd);
        bar.Last = abd.rc;
    }

    static void RemoveAppBar(IntPtr hwnd)
    {
        if (!_bars.TryGetValue(hwnd, out var bar) || !bar.Registered) { _bars.Remove(hwnd); return; }
        var abd = NewData(hwnd);
        AppBarMsg(ABM_REMOVE, ref abd);
        _bars.Remove(hwnd);
    }

    /// <summary>Forward the app-bar callback message from the menu bar / Dock window procedures.</summary>
    public static bool HandleAppBarMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if ((uint)msg != AppBarCallbackMessage) return false;
        if (wParam.ToInt32() == ABN_POSCHANGED && _bars.TryGetValue(hwnd, out var bar) && bar.Registered)
            Position(hwnd, bar, force: false);
        return true;
    }

    /// <summary>No Explorer (MacShell running as the real shell): set the work area directly, once.</summary>
    static void FallbackWorkArea(int topPx, int bottomPx)
    {
        var mon = GetMonitorInfo(PrimaryMonitor()).rcMonitor;
        var want = new RECT(mon.Left, mon.Top + topPx, mon.Right, mon.Bottom - bottomPx);
        RECT cur = default;
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref cur, 0);
        if (!_fallbackUsed) { _fallbackSavedWorkArea = cur; _fallbackUsed = true; }
        if (cur.Left == want.Left && cur.Top == want.Top && cur.Right == want.Right && cur.Bottom == want.Bottom) return;
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref want, SPIF_SENDCHANGE);
    }
}
