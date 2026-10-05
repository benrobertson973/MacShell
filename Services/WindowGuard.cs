using System.Windows.Threading;
using MacShell.Native;
using static MacShell.Native.NativeMethods;

namespace MacShell.Services;

/// <summary>
/// macOS rule: a window can never slide under the menu bar.
///  • While a window is dragged by its title bar (or resized from its top edge) the pointer is fenced so the
///    window's top stops exactly at the menu bar — sideways and downward movement stay free, so windows can
///    still be pushed half off the bottom of the screen.
///  • Windows that end up under it anyway (app restores its old position, custom drags) are nudged down.
/// Maximised and full-screen windows are never touched.
/// </summary>
public static class WindowGuard
{
    static bool _clipping;
    static IntPtr _active;
    static DispatcherTimer _watch;
    static readonly Dictionary<IntPtr, (int count, DateTime first)> _nudges = new();
    const uint SMTO_ABORTIFHUNG = 0x2;

    static int MenuBottom => ShellHost.ScreenPx.Top + (int)Math.Round(ShellHost.MenuBarHeight * ShellHost.Scale);
    /// <summary>Top of the Dock's reserved strip (the screen bottom when the Dock auto-hides).</summary>
    static int DockTop => ShellHost.ScreenPx.Bottom - (int)Math.Round((ShellHost.Dock?.ReservedHeight ?? 0) * ShellHost.Scale);

    static bool _bottomResize;

    public static void OnMoveSizeStart(IntPtr hwnd)
    {
        _bottomResize = false;
        _userMoved.Add(hwnd);   // the user is placing this window: open-time fitting stops for it
        if (!ShellHost.TakeoverEnabled || !IsGuardable(hwnd)) return;
        GetCursorPos(out var cur);
        int hit = HitTest(hwnd, cur);
        var vb = GetVisibleBounds(hwnd);
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var r = new RECT(vx, vy, vx + GetSystemMetrics(SM_CXVIRTUALSCREEN), vy + GetSystemMetrics(SM_CYVIRTUALSCREEN));
        if (hit is HTCAPTION or HTTOP or HTTOPLEFT or HTTOPRIGHT)
        {
            int grab = Math.Max(0, cur.Y - vb.Top);
            int minY = MenuBottom + grab;
            if (cur.Y < minY) minY = cur.Y;    // already overlapping when the drag began: don't yank the pointer
            r.Top = minY;
        }
        else if (hit is HTBOTTOM or HTBOTTOMLEFT or HTBOTTOMRIGHT && OnPrimary(vb))
        {
            // resizing never takes a window under the Dock (moving it there by the title bar is still allowed)
            _bottomResize = true;
            int maxY = DockTop - Math.Max(0, vb.Bottom - cur.Y);
            if (cur.Y > maxY) maxY = cur.Y;
            r.Bottom = maxY + 1;
        }
        else return;   // other resizes stay unconstrained
        if (!ClipCursor(ref r)) return;
        _clipping = true;
        _active = hwnd;
        _watch ??= CreateWatch();
        _watch.Start();
    }

    public static void OnMoveSizeEnd(IntPtr hwnd)
    {
        bool bottomResize = _bottomResize;
        _bottomResize = false;
        Release();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += (_, _) => { t.Stop(); KeepBelowMenuBar(hwnd); if (bottomResize) KeepAboveDock(hwnd, move: false); };
        t.Start();
    }

    // ------------------------------------------------------------------ new windows: never open under the Dock

    static readonly HashSet<IntPtr> _seen = new();
    static readonly HashSet<IntPtr> _userMoved = new();
    static bool _primed;

    /// <summary>
    /// Called after every window-list refresh. Windows MacShell hasn't seen before are fitted between the menu bar
    /// and the Dock. Windows already open when MacShell starts are left where they are.
    /// </summary>
    public static void NoticeWindows(IEnumerable<IntPtr> windows)
    {
        var fresh = new List<IntPtr>();
        foreach (var h in windows) if (_seen.Add(h) && _primed) fresh.Add(h);
        _seen.RemoveWhere(h => !IsWindow(h));
        _userMoved.RemoveWhere(h => !IsWindow(h));
        _primed = true;
        foreach (var h in fresh) ScheduleFit(h);
    }

    /// <summary>
    /// A window was shown — also an app reopening a window it had hidden (tray apps reuse theirs), which the
    /// window list doesn't see as new.
    /// </summary>
    public static void OnWindowShown(IntPtr hwnd)
    {
        if (!_primed || !ShellHost.TakeoverEnabled || GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        _userMoved.Remove(hwnd);   // a fresh open: whatever the user did with it last time no longer counts
        ScheduleFit(hwnd);
    }

    /// <summary>
    /// Checks the window several times over its first seconds: many apps move their window into its saved
    /// position after showing it. Stops as soon as the user moves or resizes it — dragging a window past the
    /// Dock on purpose is allowed.
    /// </summary>
    static void ScheduleFit(IntPtr hwnd)
    {
        foreach (int ms in new[] { 150, 500, 1200, 2500 })
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_userMoved.Contains(hwnd) || hwnd == _active || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return;
                KeepAboveDock(hwnd, move: true);
            };
            t.Start();
        }
    }

    /// <summary>Whether a window belongs to the primary display (the one with the Dock). Not by its centre: a tall
    /// window opened low has its centre below the screen, and those are exactly the ones to fix.</summary>
    static bool OnPrimary(RECT vb)
    {
        var scr = ShellHost.ScreenPx;
        int cx = (vb.Left + vb.Right) / 2;
        return cx >= scr.Left && cx < scr.Right && vb.Top < scr.Bottom && vb.Bottom > scr.Top;
    }

    /// <summary>
    /// Brings a window's bottom edge up to the Dock: by moving it up (<paramref name="move"/>, for new windows)
    /// as far as the menu bar allows, then by shrinking it if it is resizable.
    /// </summary>
    public static void KeepAboveDock(IntPtr hwnd, bool move)
    {
        if (!ShellHost.TakeoverEnabled || !IsGuardable(hwnd)) return;
        var vb = GetVisibleBounds(hwnd);
        if (!OnPrimary(vb)) return;   // the Dock is on the primary display only
        int dockTop = DockTop, menuBottom = MenuBottom;
        if (vb.Bottom <= dockTop) return;
        int top = vb.Top, height = vb.Bottom - vb.Top;
        if (move) top = Math.Max(menuBottom, dockTop - height);
        bool resizable = (GetStyle(hwnd) & WS_THICKFRAME) != 0;
        if (resizable && top + height > dockTop) height = Math.Max(120, dockTop - top);
        if (top == vb.Top && height == vb.Bottom - vb.Top) return;
        GetWindowRect(hwnd, out var outer);
        int dy = top - vb.Top, dh = height - (vb.Bottom - vb.Top);
        SetWindowPos(hwnd, IntPtr.Zero, outer.Left, outer.Top + dy, outer.Right - outer.Left, outer.Bottom - outer.Top + dh, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static DispatcherTimer CreateWatch()
    {
        // safety net: never leave the pointer fenced if the end-of-move event is missed
        var w = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        w.Tick += (_, _) => { if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) Release(); };
        return w;
    }

    public static void Release()
    {
        if (_clipping) { ClipCursorNone(IntPtr.Zero); _clipping = false; }
        _active = IntPtr.Zero;
        _watch?.Stop();
    }

    /// <summary>Moves a window down so its top sits just below the menu bar (if it overlaps it).</summary>
    public static void KeepBelowMenuBar(IntPtr hwnd)
    {
        if (!ShellHost.TakeoverEnabled || hwnd == _active || !IsGuardable(hwnd)) return;
        var vb = GetVisibleBounds(hwnd);
        var scr = ShellHost.ScreenPx;
        if (vb.Right <= scr.Left || vb.Left >= scr.Right || vb.Bottom <= scr.Top || vb.Top >= scr.Bottom) return;  // other display
        int top = MenuBottom;
        if (vb.Top >= top) return;
        // give up on windows that keep putting themselves back (avoid a tug of war)
        if (_nudges.TryGetValue(hwnd, out var n))
        {
            if ((DateTime.Now - n.first).TotalSeconds > 15) n = (0, DateTime.Now);
            if (n.count >= 3) return;
        }
        else n = (0, DateTime.Now);
        _nudges[hwnd] = (n.count + 1, n.first);
        GetWindowRect(hwnd, out var outer);
        SetWindowPos(hwnd, IntPtr.Zero, outer.Left, outer.Top + (top - vb.Top), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static bool IsGuardable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;   // MacShell's own windows clamp themselves
        if (!WindowTracker.Windows.Any(w => w.Hwnd == hwnd)) return false;   // only real app windows
        var mi = GetMonitorInfo(MonitorFromWindow(hwnd, 2));
        var r = GetVisibleBounds(hwnd);
        bool fullscreen = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        return !fullscreen;
    }

    static int HitTest(IntPtr hwnd, POINT p)
    {
        var lp = new IntPtr(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
        return SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 150, out var res) != IntPtr.Zero ? res.ToInt32() : 0;
    }
}
