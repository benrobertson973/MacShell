using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MacShell.Native;

/// <summary>
/// Windows' per-user system parameters (SystemParametersInfo and friends): keyboard repeat, mouse and touchpad,
/// the screen saver, window snapping, accessibility options. Changes are saved and announced to every app, as
/// Windows Settings does. None of these need administrator rights.
/// </summary>
public static class SysParams
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int uiParam, IntPtr pvParam, int winIni);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int uiParam, out int pvParam, int winIni);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int uiParam, int[] pvParam, int winIni);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int uiParam, byte[] pvParam, int winIni);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetCaretBlinkTime();
    [DllImport("user32.dll")] static extern bool SetCaretBlinkTime(uint ms);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);

    const int SPIF_SAVE = 0x1 | 0x2;   // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

    const int SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004;
    const int SPI_GETKEYBOARDSPEED = 0x000A, SPI_SETKEYBOARDSPEED = 0x000B;
    const int SPI_GETSCREENSAVETIMEOUT = 0x000E, SPI_SETSCREENSAVETIMEOUT = 0x000F;
    const int SPI_GETSCREENSAVEACTIVE = 0x0010, SPI_SETSCREENSAVEACTIVE = 0x0011;
    const int SPI_GETKEYBOARDDELAY = 0x0016, SPI_SETKEYBOARDDELAY = 0x0017;
    const int SPI_SETDOUBLECLICKTIME = 0x0020;
    const int SPI_SETMOUSEBUTTONSWAP = 0x0021;
    const int SPI_GETFILTERKEYS = 0x0032, SPI_SETFILTERKEYS = 0x0033;
    const int SPI_GETTOGGLEKEYS = 0x0034, SPI_SETTOGGLEKEYS = 0x0035;
    const int SPI_GETMOUSEKEYS = 0x0036, SPI_SETMOUSEKEYS = 0x0037;
    const int SPI_GETSTICKYKEYS = 0x003A, SPI_SETSTICKYKEYS = 0x003B;
    const int SPI_GETANIMATION = 0x0048, SPI_SETANIMATION = 0x0049;
    const int SPI_SETCURSORS = 0x0057;
    const int SPI_GETWHEELSCROLLLINES = 0x0068, SPI_SETWHEELSCROLLLINES = 0x0069;
    const int SPI_GETMOUSESPEED = 0x0070, SPI_SETMOUSESPEED = 0x0071;
    const int SPI_GETSCREENSAVESECURE = 0x0076, SPI_SETSCREENSAVESECURE = 0x0077;
    const int SPI_GETWINARRANGING = 0x0082, SPI_SETWINARRANGING = 0x0083;
    const int SPI_GETTOUCHPADPARAMETERS = 0x00AE, SPI_SETTOUCHPADPARAMETERS = 0x00AF;
    const int SPI_GETMOUSESONAR = 0x101C, SPI_SETMOUSESONAR = 0x101D;
    const int SPI_GETMOUSEVANISH = 0x1020, SPI_SETMOUSEVANISH = 0x1021;
    const int SPI_GETCLIENTAREAANIMATION = 0x1042, SPI_SETCLIENTAREAANIMATION = 0x1043;
    const int SPI_GETMESSAGEDURATION = 0x2016, SPI_SETMESSAGEDURATION = 0x2017;

    static int GetInt(int action) => SystemParametersInfo(action, 0, out int v, 0) ? v : 0;
    static bool GetBool(int action) => GetInt(action) != 0;
    /// <summary>A setting whose new value goes in uiParam (most numbers).</summary>
    static bool SetUi(int action, int value) => SystemParametersInfo(action, value, IntPtr.Zero, SPIF_SAVE);
    /// <summary>A setting whose new value goes in pvParam (most on/off switches).</summary>
    static bool SetPv(int action, int value) => SystemParametersInfo(action, 0, (IntPtr)value, SPIF_SAVE);

    // ------------------------------------------------------------------ keyboard

    /// <summary>Key repeat rate: 0 (slowest, about 2.5 a second) … 31 (fastest, about 30 a second).</summary>
    public static int KeyRepeatSpeed { get => GetInt(SPI_GETKEYBOARDSPEED); set => SetUi(SPI_SETKEYBOARDSPEED, Math.Clamp(value, 0, 31)); }
    /// <summary>Delay until repeat: 0 (shortest, 250 ms) … 3 (longest, 1 s).</summary>
    public static int KeyRepeatDelay { get => GetInt(SPI_GETKEYBOARDDELAY); set => SetUi(SPI_SETKEYBOARDDELAY, Math.Clamp(value, 0, 3)); }

    /// <summary>The text cursor's blink time in ms (-1 / uint.MaxValue: it doesn't blink).</summary>
    public static int CaretBlinkMs
    {
        get { uint v = GetCaretBlinkTime(); return v == uint.MaxValue ? -1 : (int)v; }
        set
        {
            SetCaretBlinkTime(value < 0 ? uint.MaxValue : (uint)value);
            try { Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "CursorBlinkRate", value < 0 ? "-1" : value.ToString()); } catch { }
        }
    }

    // ------------------------------------------------------------------ mouse

    /// <summary>Pointer speed 1 … 20 (10 is Windows' default).</summary>
    public static int MouseSpeed { get => GetInt(SPI_GETMOUSESPEED); set => SystemParametersInfo(SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(value, 1, 20), SPIF_SAVE); }

    /// <summary>"Enhance pointer precision": the pointer goes further when the mouse moves faster (pointer acceleration).</summary>
    public static bool PointerAcceleration
    {
        get { var v = new int[3]; return SystemParametersInfo(SPI_GETMOUSE, 0, v, 0) && v[2] != 0; }
        set
        {
            var v = new int[3];
            SystemParametersInfo(SPI_GETMOUSE, 0, v, 0);
            if (value) { v[0] = 6; v[1] = 10; v[2] = 1; } else { v[0] = 0; v[1] = 0; v[2] = 0; }
            SystemParametersInfo(SPI_SETMOUSE, 0, v, SPIF_SAVE);
        }
    }

    public static int DoubleClickMs { get => (int)GetDoubleClickTime(); set => SetUi(SPI_SETDOUBLECLICKTIME, Math.Clamp(value, 200, 900)); }

    /// <summary>Lines one notch of the wheel scrolls (-1: a screenful).</summary>
    public static int WheelLines { get => GetInt(SPI_GETWHEELSCROLLLINES); set => SetUi(SPI_SETWHEELSCROLLLINES, value); }

    /// <summary>The right button is the primary one.</summary>
    public static bool SwapButtons { get => GetSystemMetrics(23) != 0; set => SetUi(SPI_SETMOUSEBUTTONSWAP, value ? 1 : 0); }

    /// <summary>Show where the pointer is when Ctrl is pressed (rings around it).</summary>
    public static bool PointerSonar { get => GetBool(SPI_GETMOUSESONAR); set => SetPv(SPI_SETMOUSESONAR, value ? 1 : 0); }

    /// <summary>Hide the pointer while typing.</summary>
    public static bool HidePointerWhileTyping { get => GetBool(SPI_GETMOUSEVANISH); set => SetPv(SPI_SETMOUSEVANISH, value ? 1 : 0); }

    /// <summary>Pointer size 1 … 15 (Windows' Accessibility › Mouse pointer size).</summary>
    public static int PointerSize
    {
        get
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "CursorSize", 1) is int v ? Math.Clamp(v, 1, 15) : 1; }
            catch { return 1; }
        }
        set
        {
            int size = Math.Clamp(value, 1, 15);
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "CursorSize", size, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Cursors", "CursorBaseSize", 32 + (size - 1) * 16, RegistryValueKind.DWord);
            }
            catch { }
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SAVE);
        }
    }

    // ------------------------------------------------------------------ touchpad (Windows 11 24H2+)

    /// <summary>Windows' precision touchpad settings (TOUCHPAD_PARAMETERS, version 1, 48 bytes).</summary>
    public class Touchpad
    {
        public bool Present, Enabled, ExternalMouse, LeaveOnWithMouse, Tap, TapAndDrag, TwoFingerTap, RightClickZone, Pan, Zoom, ScrollReversed, AccelHonored, Feedback;
        /// <summary>0 most sensitive … 4 least sensitive.</summary>
        public int Sensitivity;
        /// <summary>1 … 20 (Windows' touchpad speed slider × 2).</summary>
        public int CursorSpeed;
        internal byte[] Raw;
    }

    static Touchpad _touchpadCache;
    static DateTime _touchpadAt;

    public static Touchpad GetTouchpad()
    {
        var buf = new byte[48];
        BitConverter.GetBytes(1).CopyTo(buf, 0);
        if (!SystemParametersInfo(SPI_GETTOUCHPADPARAMETERS, buf.Length, buf, 0)) return null;
        uint a = BitConverter.ToUInt32(buf, 12), b = BitConverter.ToUInt32(buf, 16);
        return new Touchpad
        {
            Raw = buf,
            Present = (a & 1) != 0, ExternalMouse = (a & 4) != 0, Enabled = (a & 8) != 0,
            LeaveOnWithMouse = (b & 1) != 0, Feedback = (b & 2) != 0, Tap = (b & 4) != 0, TapAndDrag = (b & 8) != 0, TwoFingerTap = (b & 16) != 0,
            RightClickZone = (b & 32) != 0, AccelHonored = (b & 64) != 0, Pan = (b & 128) != 0, Zoom = (b & 256) != 0, ScrollReversed = (b & 512) != 0,
            Sensitivity = BitConverter.ToInt32(buf, 20), CursorSpeed = BitConverter.ToInt32(buf, 24),
        };
    }

    /// <summary>Saves a changed touchpad setting (read it, change it, save it).</summary>
    public static bool SetTouchpad(Touchpad t)
    {
        if (t?.Raw == null) return false;
        var buf = (byte[])t.Raw.Clone();
        uint b = BitConverter.ToUInt32(buf, 16);
        uint Bit(uint v, uint mask, bool on) => on ? v | mask : v & ~mask;
        b = Bit(b, 1, t.LeaveOnWithMouse); b = Bit(b, 4, t.Tap); b = Bit(b, 8, t.TapAndDrag); b = Bit(b, 16, t.TwoFingerTap);
        b = Bit(b, 32, t.RightClickZone); b = Bit(b, 128, t.Pan); b = Bit(b, 256, t.Zoom); b = Bit(b, 512, t.ScrollReversed);
        BitConverter.GetBytes(b).CopyTo(buf, 16);
        BitConverter.GetBytes(Math.Clamp(t.Sensitivity, 0, 4)).CopyTo(buf, 20);
        BitConverter.GetBytes(Math.Clamp(t.CursorSpeed, 1, 20)).CopyTo(buf, 24);
        bool ok = SystemParametersInfo(SPI_SETTOUCHPADPARAMETERS, buf.Length, buf, SPIF_SAVE);
        _touchpadCache = null;
        return ok;
    }

    /// <summary>A precision touchpad is built in (Trackpad settings are shown).</summary>
    public static bool HasTouchpad
    {
        get
        {
            if (_touchpadCache == null || (DateTime.Now - _touchpadAt).TotalSeconds > 30) { _touchpadCache = GetTouchpad(); _touchpadAt = DateTime.Now; }
            return _touchpadCache?.Present == true;
        }
    }

    // ------------------------------------------------------------------ screen saver

    public static bool ScreenSaverActive { get => GetBool(SPI_GETSCREENSAVEACTIVE); set => SetUi(SPI_SETSCREENSAVEACTIVE, value ? 1 : 0); }
    /// <summary>Seconds of inactivity before the screen saver starts.</summary>
    public static int ScreenSaverTimeout { get => GetInt(SPI_GETSCREENSAVETIMEOUT); set => SetUi(SPI_SETSCREENSAVETIMEOUT, Math.Max(60, value)); }
    /// <summary>Show the sign-in screen when the screen saver ends.</summary>
    public static bool ScreenSaverSecure { get => GetBool(SPI_GETSCREENSAVESECURE); set => SetUi(SPI_SETSCREENSAVESECURE, value ? 1 : 0); }

    /// <summary>The screen saver chosen (a .scr path), or null for none.</summary>
    public static string ScreenSaverPath
    {
        get
        {
            try
            {
                var s = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "SCRNSAVE.EXE", null) as string;
                return string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s);
            }
            catch { return null; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                if (string.IsNullOrEmpty(value)) k?.DeleteValue("SCRNSAVE.EXE", false);
                else k?.SetValue("SCRNSAVE.EXE", value);
            }
            catch { }
            ScreenSaverActive = !string.IsNullOrEmpty(value);
        }
    }

    // ------------------------------------------------------------------ windows

    /// <summary>Windows Snap: drag windows to the screen's edges to tile them.</summary>
    public static bool WindowSnapping { get => GetBool(SPI_GETWINARRANGING); set => SetPv(SPI_SETWINARRANGING, value ? 1 : 0); }

    /// <summary>Windows' "Snap layouts" when hovering a maximize button (Windows 11).</summary>
    public static bool SnapLayoutsFlyout
    {
        get => RegDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "EnableSnapAssistFlyout", 1) != 0;
        set { SetRegDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "EnableSnapAssistFlyout", value ? 1 : 0); Broadcast("TraySettings"); }
    }

    /// <summary>Windows' animation effects (in apps and when windows open, minimize and maximize).</summary>
    public static bool Animations
    {
        get => GetBool(SPI_GETCLIENTAREAANIMATION);
        set
        {
            SetPv(SPI_SETCLIENTAREAANIMATION, value ? 1 : 0);
            var ai = new byte[8];
            BitConverter.GetBytes(8).CopyTo(ai, 0);
            BitConverter.GetBytes(value ? 1 : 0).CopyTo(ai, 4);
            SystemParametersInfo(SPI_SETANIMATION, 8, ai, SPIF_SAVE);
        }
    }

    /// <summary>Windows' transparency effects (and MacShell's acrylic windows, which follow them).</summary>
    public static bool Transparency
    {
        get => RegDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) != 0;
        set { SetRegDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", value ? 1 : 0); Broadcast("ImmersiveColorSet"); }
    }

    /// <summary>How long notifications stay before they're dismissed (seconds, 5 … 300).</summary>
    public static int NotificationSeconds { get => GetInt(SPI_GETMESSAGEDURATION); set => SystemParametersInfo(SPI_SETMESSAGEDURATION, 0, (IntPtr)Math.Clamp(value, 5, 300), SPIF_SAVE); }

    // ------------------------------------------------------------------ accessibility keys

    static uint GetFlags(int action, int size)
    {
        var b = new byte[size];
        BitConverter.GetBytes(size).CopyTo(b, 0);
        return SystemParametersInfo(action, size, b, 0) ? BitConverter.ToUInt32(b, 4) : 0;
    }

    static void SetFlag(int getAction, int setAction, int size, uint flag, bool on)
    {
        var b = new byte[size];
        BitConverter.GetBytes(size).CopyTo(b, 0);
        if (!SystemParametersInfo(getAction, size, b, 0)) return;
        uint f = BitConverter.ToUInt32(b, 4);
        f = on ? f | flag : f & ~flag;
        BitConverter.GetBytes(f).CopyTo(b, 4);
        SystemParametersInfo(setAction, size, b, SPIF_SAVE);
    }

    /// <summary>Sticky Keys: modifier keys stay pressed until the next key (STICKYKEYS, 8 bytes).</summary>
    public static bool StickyKeys { get => (GetFlags(SPI_GETSTICKYKEYS, 8) & 1) != 0; set => SetFlag(SPI_GETSTICKYKEYS, SPI_SETSTICKYKEYS, 8, 1, value); }
    /// <summary>Pressing Shift five times turns Sticky Keys on or off.</summary>
    public static bool StickyKeysShortcut { get => (GetFlags(SPI_GETSTICKYKEYS, 8) & 4) != 0; set => SetFlag(SPI_GETSTICKYKEYS, SPI_SETSTICKYKEYS, 8, 4, value); }
    /// <summary>Slow Keys (Filter Keys): short or repeated keystrokes are ignored (FILTERKEYS, 24 bytes).</summary>
    public static bool SlowKeys { get => (GetFlags(SPI_GETFILTERKEYS, 24) & 1) != 0; set => SetFlag(SPI_GETFILTERKEYS, SPI_SETFILTERKEYS, 24, 1, value); }
    /// <summary>Mouse Keys: the numeric keypad moves the pointer (MOUSEKEYS, 28 bytes).</summary>
    public static bool MouseKeys { get => (GetFlags(SPI_GETMOUSEKEYS, 28) & 1) != 0; set => SetFlag(SPI_GETMOUSEKEYS, SPI_SETMOUSEKEYS, 28, 1, value); }
    /// <summary>Toggle Keys: a tone when Caps Lock, Num Lock or Scroll Lock is pressed (TOGGLEKEYS, 8 bytes).</summary>
    public static bool ToggleKeys { get => (GetFlags(SPI_GETTOGGLEKEYS, 8) & 1) != 0; set => SetFlag(SPI_GETTOGGLEKEYS, SPI_SETTOGGLEKEYS, 8, 1, value); }

    // ------------------------------------------------------------------ helpers

    public static int RegDword(string key, string name, int fallback)
    {
        try { return Registry.GetValue(key, name, fallback) is int v ? v : fallback; } catch { return fallback; }
    }

    public static void SetRegDword(string key, string name, int value)
    {
        try { Registry.SetValue(key, name, value, RegistryValueKind.DWord); } catch { }
    }

    /// <summary>Tells every app a setting changed (WM_SETTINGCHANGE with the area's name).</summary>
    public static void Broadcast(string area)
    {
        try { SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, area, 0x0002, 1000, out _); } catch { }
    }
}
