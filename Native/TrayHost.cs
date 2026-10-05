using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using static MacShell.Native.NativeMethods;

namespace MacShell.Native;

/// <summary>One app's notification-area icon (Shell_NotifyIcon), shown as a menu extra.</summary>
public class TrayIcon
{
    public IntPtr HWnd;
    public uint UID;
    public Guid Guid;
    public uint CallbackMessage;
    public uint Version;
    public BitmapSource Image;
    public string Tip = "";
    public bool Hidden;
    public long Order;
    public int ProcessId;
    public string Key => Guid != Guid.Empty ? Guid.ToString() : $"{HWnd.ToInt64():X}:{UID}";
}

/// <summary>
/// Collects the notification-area ("system tray") icons of running apps so the menu bar can show them on the
/// right, like macOS menu extras. A hidden window of class Shell_TrayWnd is kept in front of Explorer's
/// (Shell_NotifyIcon and SHAppBarMessage find the tray with FindWindow, which walks the z-order), and every
/// message it gets is also forwarded to Explorer's real taskbar, so app bars, toasts and the Windows taskbar
/// keep working — and still have every icon when MacShell quits.
/// </summary>
public static class TrayHost
{
    public static readonly List<TrayIcon> Icons = new();
    public static event Action Changed;
    public static IntPtr Hwnd { get; private set; }
    /// <summary>True while MacShell itself broadcasts TaskbarCreated (not an Explorer restart).</summary>
    public static bool SelfBroadcast { get; private set; }

    static WndProcDelegate _proc;   // kept alive: the native class points at it
    static IntPtr _notifyWnd;
    static long _order;
    static Func<TrayIcon, RECT?> _iconRect;
    static System.Windows.Threading.DispatcherTimer _changeTimer;

    const string TrayClass = "Shell_TrayWnd", NotifyClass = "TrayNotifyWnd";
    const int WM_COPYDATA = 0x004A, WM_USER = 0x0400, WM_COMMAND = 0x0111;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_STATE = 8, NIF_GUID = 0x20;
    const uint NIS_HIDDEN = 1;
    const int TRAY_SIGNATURE = 0x34753423;
    const uint SMTO_ABORTIFHUNG = 2;

    public static bool Running => Hwnd != IntPtr.Zero;

    /// <param name="iconRect">Screen rectangle (pixels) of an icon in the menu bar, for apps that ask where it is.</param>
    public static void Start(Func<TrayIcon, RECT?> iconRect)
    {
        if (Running) return;
        _iconRect = iconRect;
        _proc = WndProc;
        var hinst = GetModuleHandle(null);
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = hinst, lpszClassName = TrayClass };
        RegisterClassEx(ref wc);
        var wc2 = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = hinst, lpszClassName = NotifyClass };
        RegisterClassEx(ref wc2);
        Hwnd = CreateWindowEx((uint)(WS_EX_TOOLWINDOW | WS_EX_TOPMOST), TrayClass, "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) return;
        _notifyWnd = CreateWindowEx(0, NotifyClass, "", WS_CHILD, 0, 0, 0, 0, Hwnd, IntPtr.Zero, hinst, IntPtr.Zero);
        RaiseAboveExplorer();
        Rebroadcast();
    }

    public static void Stop()
    {
        if (!Running) return;
        var h = Hwnd;
        Hwnd = IntPtr.Zero;
        DestroyWindow(h);
        Icons.Clear();
        Changed?.Invoke();
        // Apps re-add their icons to Explorer's tray (it also has them already, from forwarding).
        Rebroadcast();
    }

    /// <summary>Ask every app to (re-)add its icons: the same broadcast Explorer sends when it starts.</summary>
    static void Rebroadcast()
    {
        SelfBroadcast = true;
        try { SendNotifyMessage(HWND_BROADCAST, (int)RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero); }
        finally { SelfBroadcast = false; }
    }

    /// <summary>Keep our window first in z-order among Shell_TrayWnd windows; called periodically and after Explorer restarts.</summary>
    public static void Maintain()
    {
        if (!Running) return;
        if (FindWindow(TrayClass, null) != Hwnd) RaiseAboveExplorer();
        Prune();
    }

    public static void OnExplorerRestarted()
    {
        if (!Running) return;
        // Explorer's new taskbar may have been first in z-order when apps re-added their icons: ask again.
        RaiseAboveExplorer();
        Rebroadcast();
    }

    /// <summary>
    /// Run MacShell's own SHAppBarMessage calls with Explorer's taskbar first in z-order: Explorer only applies
    /// app-bar positions to the work area when the message reaches it directly, not when forwarded by us.
    /// </summary>
    public static void StepAside(Action a)
    {
        if (!Running) { a(); return; }
        SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        SetWindowPos(Hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        try { a(); }
        finally { RaiseAboveExplorer(); }
    }

    static void RaiseAboveExplorer() =>
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>Explorer's own taskbar (any Shell_TrayWnd not belonging to MacShell).</summary>
    public static IntPtr ExplorerTray
    {
        get
        {
            IntPtr h = IntPtr.Zero;
            int me = Environment.ProcessId;
            while ((h = FindWindowEx(IntPtr.Zero, h, TrayClass, null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return h;
            }
            return IntPtr.Zero;
        }
    }

    public static bool IsOurs(IntPtr h) => h != IntPtr.Zero && (h == Hwnd || h == _notifyWnd);

    // ------------------------------------------------------------------ window procedure

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (hwnd == Hwnd && msg == WM_COPYDATA && lParam != IntPtr.Zero)
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (cds.dwData == (IntPtr)1 && cds.lpData != IntPtr.Zero && cds.cbData >= 8)
                {
                    bool ok = HandleNotifyIcon(cds);
                    Forward(msg, wParam, lParam);   // keep Explorer's tray in sync (toasts, quitting MacShell)
                    return ok ? (IntPtr)1 : IntPtr.Zero;
                }
                if (cds.dwData == (IntPtr)3 && cds.lpData != IntPtr.Zero)
                {
                    var r = HandleGetRect(cds);
                    if (r.HasValue) return r.Value;
                }
                return Forward(msg, wParam, lParam);   // app bars (dwData 0) and anything else: Explorer handles it
            }
            if (hwnd == Hwnd && (msg >= WM_USER || msg == WM_COMMAND))
                return Forward(msg, wParam, lParam);
        }
        catch { }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static IntPtr Forward(int msg, IntPtr wParam, IntPtr lParam)
    {
        var tray = ExplorerTray;
        if (tray == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(tray, msg, wParam, lParam, SMTO_ABORTIFHUNG, 4000, out var result);
        return result;
    }

    // Layout of NOTIFYICONDATAW as Shell_NotifyIcon packs it for the tray (handles are 32-bit on every platform).
    const int OFS_HWND = 4, OFS_UID = 8, OFS_FLAGS = 12, OFS_CALLBACK = 16, OFS_HICON = 20, OFS_TIP = 24,
              OFS_STATE = 24 + 256, OFS_STATEMASK = OFS_STATE + 4, OFS_VERSION = OFS_STATEMASK + 4 + 512,
              OFS_GUID = OFS_VERSION + 4 + 128 + 4;

    static bool HandleNotifyIcon(COPYDATASTRUCT cds)
    {
        int sig = Marshal.ReadInt32(cds.lpData);
        if (sig != TRAY_SIGNATURE) return false;
        uint cmd = (uint)Marshal.ReadInt32(cds.lpData, 4);
        IntPtr nid = cds.lpData + 8;
        int size = cds.cbData - 8;
        uint U(int ofs) => ofs + 4 <= size ? (uint)Marshal.ReadInt32(nid, ofs) : 0;

        uint flags = U(OFS_FLAGS);
        var h = new IntPtr((int)U(OFS_HWND));
        uint uid = U(OFS_UID);
        Guid guid = Guid.Empty;
        if ((flags & NIF_GUID) != 0 && OFS_GUID + 16 <= size)
        {
            var b = new byte[16];
            Marshal.Copy(nid + OFS_GUID, b, 0, 16);
            guid = new Guid(b);
        }
        TrayIcon Find() => Icons.FirstOrDefault(i => guid != Guid.Empty ? i.Guid == guid : i.HWnd == h && i.UID == uid);

        var icon = Find();
        switch (cmd)
        {
            case NIM_ADD:
            case NIM_MODIFY:
                if (cmd == NIM_MODIFY && icon == null) return false;
                bool added = icon == null;
                icon ??= new TrayIcon { HWnd = h, UID = uid, Guid = guid, Order = ++_order };
                icon.HWnd = h; icon.UID = uid;
                GetWindowThreadProcessId(h, out uint pid);
                icon.ProcessId = (int)pid;
                if ((flags & NIF_MESSAGE) != 0) icon.CallbackMessage = U(OFS_CALLBACK);
                if ((flags & NIF_TIP) != 0 && OFS_TIP + 256 <= size) icon.Tip = Marshal.PtrToStringUni(nid + OFS_TIP, 128).Split('\0')[0];
                if ((flags & NIF_STATE) != 0)
                {
                    uint mask = U(OFS_STATEMASK), state = U(OFS_STATE);
                    if ((mask & NIS_HIDDEN) != 0) icon.Hidden = (state & NIS_HIDDEN) != 0;
                }
                if ((flags & NIF_ICON) != 0)
                {
                    var img = IconToBitmap(new IntPtr((int)U(OFS_HICON)));
                    if (img != null) icon.Image = img;
                }
                if (added) Icons.Add(icon);
                NotifyChanged();
                return true;
            case NIM_DELETE:
                if (icon == null) return false;
                Icons.Remove(icon);
                NotifyChanged();
                return true;
            case NIM_SETVERSION:
                if (icon == null) return false;
                icon.Version = U(OFS_VERSION);
                return true;
            case NIM_SETFOCUS:
                return true;
        }
        return false;
    }

    /// <summary>Shell_NotifyIconGetRect: tell the app where its icon is (so its popup opens under the menu bar).</summary>
    static IntPtr? HandleGetRect(COPYDATASTRUCT cds)
    {
        // struct { DWORD magic; DWORD msg; DWORD cbSize; DWORD pad; HWND (64-bit) hWnd; UINT uID; GUID guidItem; }
        if (cds.cbData < 44 || Marshal.ReadInt32(cds.lpData) != TRAY_SIGNATURE) return null;
        int which = Marshal.ReadInt32(cds.lpData, 4);
        var h = new IntPtr(Marshal.ReadInt32(cds.lpData, 16));
        uint uid = (uint)Marshal.ReadInt32(cds.lpData, 24);
        var b = new byte[16];
        Marshal.Copy(cds.lpData + 28, b, 0, 16);
        var guid = new Guid(b);
        var icon = Icons.FirstOrDefault(i => guid != Guid.Empty ? i.Guid == guid : i.HWnd == h && i.UID == uid);
        if (icon == null || _iconRect == null) return null;
        var r = _iconRect(icon);
        if (r == null) return null;
        var rc = r.Value;
        int lo = which == 1 ? rc.Left : rc.Right, hi = which == 1 ? rc.Top : rc.Bottom;
        return new IntPtr((hi << 16) | (lo & 0xFFFF));
    }

    static void NotifyChanged()
    {
        // Apps often send several updates in a row (icon + tip + state): coalesce them.
        if (_changeTimer == null)
        {
            _changeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _changeTimer.Tick += (_, _) => { _changeTimer.Stop(); Changed?.Invoke(); };
        }
        _changeTimer.Stop();
        _changeTimer.Start();
    }

    static BitmapSource IconToBitmap(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ clicks

    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
              WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400;

    /// <summary>Send the click to the app exactly as Explorer's tray would; <paramref name="anchor"/> is in screen pixels.</summary>
    public static void Click(TrayIcon icon, bool right, bool dbl, Point anchor)
    {
        if (!IsWindow(icon.HWnd)) { Icons.Remove(icon); Changed?.Invoke(); return; }
        if (icon.ProcessId > 0) AllowSetForegroundWindow(icon.ProcessId);
        void Send(int m)
        {
            IntPtr w, l;
            if (icon.Version >= 4)
            {
                w = new IntPtr(((int)anchor.Y << 16) | ((int)anchor.X & 0xFFFF));
                l = new IntPtr(((int)icon.UID << 16) | (m & 0xFFFF));
            }
            else { w = new IntPtr((int)icon.UID); l = new IntPtr(m); }
            SendNotifyMessage(icon.HWnd, (int)icon.CallbackMessage, w, l);
        }
        Send(WM_MOUSEMOVE);
        if (dbl) { Send(WM_LBUTTONDBLCLK); Send(WM_LBUTTONUP); return; }
        if (right)
        {
            Send(WM_RBUTTONDOWN); Send(WM_RBUTTONUP);
            if (icon.Version >= 3) Send(WM_CONTEXTMENU);
        }
        else
        {
            Send(WM_LBUTTONDOWN); Send(WM_LBUTTONUP);
            if (icon.Version >= 3) Send(NIN_SELECT);
        }
    }

    /// <summary>Drop icons whose window is gone (apps that crashed without NIM_DELETE).</summary>
    public static void Prune()
    {
        if (Icons.RemoveAll(i => !IsWindow(i.HWnd)) > 0) Changed?.Invoke();
    }

    // ------------------------------------------------------------------ interop

    delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }

    const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000;
    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
