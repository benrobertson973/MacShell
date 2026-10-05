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

    public static void OnMoveSizeStart(IntPtr hwnd)
    {
        if (!ShellHost.TakeoverEnabled || !IsGuardable(hwnd)) return;
        GetCursorPos(out var cur);
        int hit = HitTest(hwnd, cur);
        if (hit is not (HTCAPTION or HTTOP or HTTOPLEFT or HTTOPRIGHT)) return;   // other resizes stay unconstrained
        var vb = GetVisibleBounds(hwnd);
        int grab = Math.Max(0, cur.Y - vb.Top);
        int minY = MenuBottom + grab;
        if (cur.Y < minY) minY = cur.Y;    // already overlapping when the drag began: don't yank the pointer
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var r = new RECT(vx, minY, vx + GetSystemMetrics(SM_CXVIRTUALSCREEN), vy + GetSystemMetrics(SM_CYVIRTUALSCREEN));
        if (!ClipCursor(ref r)) return;
        _clipping = true;
        _active = hwnd;
        _watch ??= CreateWatch();
        _watch.Start();
    }

    public static void OnMoveSizeEnd(IntPtr hwnd)
    {
        Release();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += (_, _) => { t.Stop(); KeepBelowMenuBar(hwnd); };
        t.Start();
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
