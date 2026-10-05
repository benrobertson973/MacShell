using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MacShell.Native;
using static MacShell.Native.NativeMethods;

namespace MacShell.Controls;

/// <summary>
/// The menu bar and Dock never take keyboard focus (so the app you're using keeps it, like on a Mac).
/// The downside is that Windows never tells them when you click elsewhere, so an open menu would stay open.
/// While any of their menus is open, a low-level mouse hook watches for clicks outside our menus and
/// closes them; Esc closes them too (handled by the keyboard hook).
/// </summary>
public static class MenuDismisser
{
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr hmod, uint tid);
    const int WH_MOUSE_LL = 14;
    const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B;

    static readonly Dictionary<string, Action> _open = new();
    static readonly LowLevelMouseProc _proc = HookProc;
    static IntPtr _hook;

    public static bool AnyOpen => _open.Count > 0;

    public static void Opened(string key, Action close)
    {
        _open[key] = close;
        if (_hook == IntPtr.Zero) _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public static void Closed(string key)
    {
        _open.Remove(key);
        if (_open.Count == 0) Unhook();
    }

    public static void CloseAll()
    {
        var actions = _open.Values.ToList();
        _open.Clear();
        Unhook();
        foreach (var a in actions) { try { a(); } catch { } }
    }

    static void Unhook()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _open.Count > 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsOutsideMenus(info.pt))
                    Application.Current?.Dispatcher.BeginInvoke(CloseAll);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>True unless the click lands on the menu bar, the Dock, or one of our menu popups.</summary>
    static bool IsOutsideMenus(POINT pt)
    {
        var root = GetAncestor(WindowFromPoint(pt), GA_ROOT);
        if (root == IntPtr.Zero) return true;
        GetWindowThreadProcessId(root, out uint pid);
        if (pid != (uint)Environment.ProcessId) return true;
        if (root == ShellHost.MenuBar?.Handle || root == ShellHost.Dock?.Handle) return false;
        // Our own full windows (Finder, desktop …) count as "outside"; anything else of ours is a menu popup.
        foreach (Window w in Application.Current.Windows)
            if (new WindowInteropHelper(w).Handle == root) return true;
        return false;
    }
}
