using System.Diagnostics;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Shell;
using static MacShell.Native.NativeMethods;

namespace MacShell.Services;

/// <summary>
/// Hot Corners (Settings › Desktop & Dock › Hot Corners…): the pointer pushed into a corner of the screen does what that
/// corner is set to - once, until it leaves the corner. Not while a mouse button is held or a full-screen app is in front.
/// </summary>
public static class HotCorners
{
    static DispatcherTimer _timer;
    static int _corner = -1;
    static DateTime _since;
    static bool _done;

    public static void Start()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    static void Check()
    {
        var actions = Settings.Current.HotCorners;
        if (actions == null || actions.All(string.IsNullOrEmpty)) { _corner = -1; return; }
        if (!GetCursorPos(out var p)) return;
        // the corners of the whole desktop (all displays together): the pointer stops there by itself
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        bool left = p.X <= vx, right = p.X >= vx + vw - 1, top = p.Y <= vy, bottom = p.Y >= vy + vh - 1;
        int corner = top && left ? 0 : top && right ? 1 : bottom && left ? 2 : bottom && right ? 3 : -1;
        if (corner < 0) { _corner = -1; _done = false; return; }
        if (corner != _corner) { _corner = corner; _since = DateTime.Now; _done = false; return; }
        if (_done || (DateTime.Now - _since).TotalMilliseconds < 150) return;
        if (GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0 || ShellHost.FullscreenAppInFront) return;
        _done = true;
        Do(actions[corner]);
    }

    public static void Do(string action)
    {
        try
        {
            switch (action)
            {
                case "missioncontrol": MissionControlWindow.Toggle(); break;
                case "desktop": ShellHost.ToggleShowDesktop(); break;
                case "notifications": NotificationCenterWindow.Toggle(null); break;
                case "launchpad": LaunchpadWindow.Toggle(); break;
                case "lock": ShellHost.Lock(); break;
                case "displaysleep": PostMessage((IntPtr)0xFFFF, 0x0112 /*WM_SYSCOMMAND*/, (IntPtr)0xF170 /*SC_MONITORPOWER*/, (IntPtr)2); break;
                case "screensaver":
                    {
                        string scr = SysParams.ScreenSaverPath;
                        if (string.IsNullOrEmpty(scr) || !File.Exists(scr)) scr = Path.Combine(Environment.SystemDirectory, "scrnsave.scr");
                        if (File.Exists(scr)) Process.Start(new ProcessStartInfo(scr, "/s") { UseShellExecute = false });
                        break;
                    }
            }
        }
        catch (Exception ex) { App.Log($"hot corner {action}: {ex.Message}"); }
    }
}
