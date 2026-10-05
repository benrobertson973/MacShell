using System.Runtime.InteropServices;
using System.Windows.Threading;
using static MacShell.Native.NativeMethods;

namespace MacShell.Native;

[Flags]
public enum Mods { None = 0, Win = 1, Alt = 2, Ctrl = 4, Shift = 8 }

/// <summary>
/// Low-level keyboard hook that implements macOS-style global shortcuts:
///   Win (tap)        → Launchpad
///   Win/Alt + Space  → Spotlight
///   Win/Alt + Tab    → App Switcher (⌘Tab)
///   Win + ↑          → Mission Control
///   Win + E          → Finder,  Win + D → Show Desktop
///   Ctrl+Alt+Esc     → Force Quit,  Win+Shift+3/4 → screenshots
///   Ctrl+Alt+Shift+Q → quit MacShell and restore Windows
/// Handlers run asynchronously on the UI dispatcher so the hook never blocks input.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    readonly LowLevelKeyboardProc _proc;
    IntPtr _hook;
    readonly Dispatcher _ui;
    bool _winDown, _winUsed, _winSwallowed, _altDown, _altSwallowed, _ctrlDown, _shiftDown;
    readonly HashSet<int> _swallowedKeys = new();

    public bool SwitcherActive { get; private set; }
    Mods _switcherModifier;

    /// <summary>"nothing" | "launchpad" | "start" for a lone tap of the Windows key.</summary>
    public Func<string> WinTapAction = () => "nothing";
    public Func<bool> ReplaceAltTab = () => true;
    public Func<bool> AltSpaceSpotlight = () => true;

    public event Action WinTap;
    /// <summary>Global combo (vk, modifiers). Return true if handled (key is swallowed).</summary>
    public Func<int, Mods, bool> Combo;
    public event Action<bool> SwitcherStart;   // arg: reverse
    public event Action<bool> SwitcherStep;    // arg: reverse
    public event Action SwitcherCommit;
    public event Action SwitcherCancel;
    public event Action<int> SwitcherKey;      // Q / H / arrows while switcher is up

    public KeyboardHook(Dispatcher ui)
    {
        _ui = ui;
        _proc = HookProc;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    Mods Current => (_winDown ? Mods.Win : 0) | (_altDown ? Mods.Alt : 0) | (_ctrlDown ? Mods.Ctrl : 0) | (_shiftDown ? Mods.Shift : 0);

    void Post(Action a) => _ui.BeginInvoke(a, DispatcherPriority.Send);

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        int vk = (int)info.vkCode;

        try
        {
            switch (vk)
            {
                case VK_LWIN or VK_RWIN:
                    if (!up)
                    {
                        if (!_winDown) { _winUsed = false; _winSwallowed = false; }
                        _winDown = true;
                    }
                    else
                    {
                        _winDown = false;
                        if (SwitcherActive && _switcherModifier == Mods.Win) { SwitcherActive = false; SendMaskKey(); Post(() => SwitcherCommit?.Invoke()); }
                        else if (!_winUsed)
                        {
                            string action = WinTapAction();
                            if (action != "start")
                            {
                                SendMaskKey();   // swallow the Start menu
                                if (action == "launchpad") Post(() => WinTap?.Invoke());
                            }
                        }
                        else if (_winSwallowed) SendMaskKey();
                    }
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                case VK_LMENU or VK_RMENU or VK_MENU:
                    if (!up)
                    {
                        if (!_altDown) _altSwallowed = false;
                        _altDown = true;
                    }
                    else
                    {
                        _altDown = false;
                        if (SwitcherActive && _switcherModifier == Mods.Alt) { SwitcherActive = false; SendMaskKey(); Post(() => SwitcherCommit?.Invoke()); }
                        else if (_altSwallowed) SendMaskKey();
                    }
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                case VK_LCONTROL or VK_RCONTROL or VK_CONTROL:
                    _ctrlDown = !up;
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                case VK_LSHIFT or VK_RSHIFT or VK_SHIFT:
                    _shiftDown = !up;
                    return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (up)
            {
                if (_swallowedKeys.Remove(vk)) return (IntPtr)1;
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (vk == VK_ESCAPE && MacShell.Controls.MenuDismisser.AnyOpen)
            {
                Swallow(vk);
                Post(MacShell.Controls.MenuDismisser.CloseAll);
                return (IntPtr)1;
            }

            if (_winDown) _winUsed = true;
            var mods = Current;

            // ---- App switcher (⌘Tab) ----
            if (SwitcherActive)
            {
                if (vk == VK_TAB) { Swallow(vk); bool rev = _shiftDown; Post(() => SwitcherStep?.Invoke(rev)); return (IntPtr)1; }
                if (vk == VK_ESCAPE) { SwitcherActive = false; Swallow(vk); Post(() => SwitcherCancel?.Invoke()); return (IntPtr)1; }
                if (vk is VK_LEFT or VK_RIGHT or 0x51 /*Q*/ or 0x48 /*H*/ or VK_OEM_3)
                {
                    Swallow(vk); Post(() => SwitcherKey?.Invoke(vk)); return (IntPtr)1;
                }
            }
            if (vk == VK_TAB && !_ctrlDown)
            {
                bool viaAlt = _altDown && !_winDown && ReplaceAltTab();
                bool viaWin = _winDown && !_altDown;
                if (viaAlt || viaWin)
                {
                    Swallow(vk);
                    if (viaAlt) _altSwallowed = true; else _winSwallowed = true;
                    SwitcherActive = true;
                    _switcherModifier = viaAlt ? Mods.Alt : Mods.Win;
                    bool rev = _shiftDown;
                    Post(() => SwitcherStart?.Invoke(rev));
                    return (IntPtr)1;
                }
            }

            // ---- Spotlight via Alt+Space (physical ⌘Space) ----
            if (vk == VK_SPACE && _altDown && !_winDown && !_ctrlDown && AltSpaceSpotlight())
            {
                Swallow(vk); _altSwallowed = true;
                Post(() => Combo?.Invoke(VK_SPACE, Mods.Win));
                return (IntPtr)1;
            }

            // ---- Other combos ----
            if (mods != Mods.None && Combo != null)
            {
                bool handled = false;
                // evaluate synchronously but cheaply: Combo only decides; heavy work is posted by the handler itself
                handled = Combo(vk, mods);
                if (handled)
                {
                    Swallow(vk);
                    if (_winDown) _winSwallowed = true;
                    if (_altDown) _altSwallowed = true;
                    return (IntPtr)1;
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Swallow(int vk) => _swallowedKeys.Add(vk);

    public void EndSwitcher() => SwitcherActive = false;
}
