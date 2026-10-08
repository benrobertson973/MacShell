using MacShell.Controls;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>
/// The menu bar's menus for apps with no Windows menu of their own to mirror (browsers, Electron apps, the new Notepad,
/// Office, Discord, Zoom, WhatsApp …): only what each one can really do, each item pressing that app's own keyboard
/// shortcut. An app MacShell doesn't know gets only what works almost everywhere (editing, closing, full screen) - a
/// menu item that does nothing is worse than none.
/// </summary>
static class AppMenus
{
    const ushort Ctrl = 0x11, Shift = 0x10, Alt = 0x12, Win = 0x5B;
    const ushort Tab = 0x09, Esc = 0x1B, Enter = 0x0D, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Home = 0x24;
    const ushort F3 = 0x72, F5 = 0x74, F11 = 0x7A, F12 = 0x7B;
    const ushort Plus = 0xBB, Minus = 0xBD, Comma = 0xBC, Period = 0xBE;

    enum Kind { Generic, Browser, Firefox, WebApp, Electron, Discord, Notepad, Office, Zoom, WhatsApp }

    static readonly string[] Chromium = { "brave", "chrome", "msedge", "vivaldi", "opera", "opera_gx", "arc", "thorium", "chromium" };
    static readonly string[] OfficeApps = { "winword", "excel", "powerpnt", "outlook", "onenote", "msaccess", "mspub", "visio" };
    // Electron apps that took their menu away (setMenu(null)): with it went Reload, Zoom … - so they get only what's left
    static readonly string[] NoMenuApps = { "bds fellowship room" };

    static Kind KindOf(RunningApp app, IntPtr hwnd)
    {
        string exe = Path.GetFileNameWithoutExtension(app.ExePath ?? "").ToLowerInvariant();
        string id = ((app.Aumid ?? "") + " " + (app.Key ?? "")).ToLowerInvariant();
        if (id.Contains("_crx_")) return Kind.WebApp;   // (a website installed as an app: Google Calendar …)
        if (Chromium.Contains(exe)) return Kind.Browser;
        if (exe == "firefox") return Kind.Firefox;
        if (exe == "discord") return Kind.Discord;
        if (exe == "notepad") return Kind.Notepad;
        if (OfficeApps.Contains(exe)) return Kind.Office;
        if (exe == "zoom") return Kind.Zoom;
        if (id.Contains("whatsapp") || exe.StartsWith("whatsapp")) return Kind.WhatsApp;
        if (NoMenuApps.Contains(exe)) return Kind.Generic;
        if (hwnd != IntPtr.Zero && GetClassNameOf(hwnd) == "Chrome_WidgetWin_1") return Kind.Electron;   // (Claude, Slack, VS Code …)
        return Kind.Generic;
    }

    /// <summary>The menus after the app's own (bold) one, and the app's Settings… shortcut (null: none known).</summary>
    public static (List<(string title, object[] items)> menus, ushort[] settings) For(RunningApp app, IntPtr hwnd)
    {
        var kind = KindOf(app, hwnd);
        var menus = new List<(string, object[])>();
        // Settings… (⌘,) only where the app has that shortcut
        ushort[] settings = kind is Kind.Electron or Kind.Discord or Kind.WhatsApp ? new[] { Ctrl, Comma } : null;
        switch (kind)
        {
            case Kind.Browser:
            case Kind.Firefox:
                bool ff = kind == Kind.Firefox;
                menus.Add(("File", new object[]
                {
                    K("New Tab", Ctrl, 0x54), K("New Window", Ctrl, 0x4E),
                    ff ? K("New Private Window", Ctrl, Shift, 0x50) : K("New Private Window", Ctrl, Shift, 0x4E),
                    K("Reopen Closed Tab", Ctrl, Shift, 0x54),
                    Mb.Sep(),
                    K("Open File…", Ctrl, 0x4F), K("Open Location…", Ctrl, 0x4C),
                    Mb.Sep(),
                    K("Close Tab", Ctrl, 0x57), CloseWindow(),
                    K("Save Page As…", Ctrl, 0x53),
                    Mb.Sep(),
                    K("Print…", Ctrl, 0x50),
                }));
                menus.Add(("Edit", Editing(redoShift: true, find: true, findNext: ff ? null : new[] { Ctrl, (ushort)0x47 })));
                menus.Add(("View", new object[]
                {
                    K("Reload Page", Ctrl, 0x52), K("Force Reload", Ctrl, Shift, 0x52),
                    Mb.Sep(),
                    K("Actual Size", Ctrl, 0x30), K("Zoom In", Ctrl, Plus), K("Zoom Out", Ctrl, Minus),
                    Mb.Sep(),
                    K("Enter Full Screen", F11),
                    Mb.Sep(),
                    K("Developer Tools", Ctrl, Shift, 0x49), K("View Source", Ctrl, 0x55),
                }));
                menus.Add(("History", new object[]
                {
                    K("Back", Alt, Left), K("Forward", Alt, Right), K("Home", Alt, Home),
                    Mb.Sep(),
                    K("Show All History", Ctrl, 0x48), K("Downloads", Ctrl, 0x4A),
                }));
                menus.Add(("Bookmarks", new object[]
                {
                    K("Bookmark This Page…", Ctrl, 0x44), K("Bookmark All Tabs…", Ctrl, Shift, 0x44),
                    Mb.Sep(),
                    ff ? K("Show All Bookmarks", Ctrl, Shift, 0x4F) : K("Bookmark Manager", Ctrl, Shift, 0x4F),
                    K("Show Bookmarks Bar", Ctrl, Shift, 0x42),
                }));
                menus.Add(("Tab", new object[]
                {
                    K("Show Next Tab", Ctrl, Tab), K("Show Previous Tab", Ctrl, Shift, Tab),
                }));
                break;

            case Kind.WebApp:
                menus.Add(("File", new object[] { K("Print…", Ctrl, 0x50), Mb.Sep(), CloseWindow() }));
                menus.Add(("Edit", Editing(redoShift: true, find: true)));
                menus.Add(("View", new object[]
                {
                    K("Reload Page", Ctrl, 0x52),
                    Mb.Sep(),
                    K("Actual Size", Ctrl, 0x30), K("Zoom In", Ctrl, Plus), K("Zoom Out", Ctrl, Minus),
                    Mb.Sep(),
                    K("Enter Full Screen", F11),
                }));
                menus.Add(("History", new object[] { K("Back", Alt, Left), K("Forward", Alt, Right) }));
                break;

            case Kind.Notepad:
                menus.Add(("File", new object[]
                {
                    K("New Tab", Ctrl, 0x4E), K("New Window", Ctrl, Shift, 0x4E),
                    K("Open…", Ctrl, 0x4F),
                    Mb.Sep(),
                    K("Save", Ctrl, 0x53), K("Save As…", Ctrl, Shift, 0x53), K("Save All", Ctrl, Alt, 0x53),
                    Mb.Sep(),
                    K("Print…", Ctrl, 0x50),
                    Mb.Sep(),
                    K("Close Tab", Ctrl, 0x57), K("Close Window", Ctrl, Shift, 0x57),
                }));
                menus.Add(("Edit", new object[]
                {
                    K("Undo", Ctrl, 0x5A), K("Redo", Ctrl, 0x59),
                    Mb.Sep(),
                    K("Cut", Ctrl, 0x58), K("Copy", Ctrl, 0x43), K("Paste", Ctrl, 0x56), K("Select All", Ctrl, 0x41),
                    Mb.Sep(),
                    K("Find…", Ctrl, 0x46), K("Find Next", F3), K("Find Previous", Shift, F3), K("Replace…", Ctrl, 0x48), K("Go To Line…", Ctrl, 0x47),
                    Mb.Sep(),
                    K("Insert Time/Date", F5),
                    Emoji(),
                }));
                menus.Add(("View", new object[] { K("Actual Size", Ctrl, 0x30), K("Zoom In", Ctrl, Plus), K("Zoom Out", Ctrl, Minus) }));
                break;

            case Kind.Office:
                menus.Add(("File", new object[]
                {
                    K("New", Ctrl, 0x4E), K("Open…", Ctrl, 0x4F),
                    Mb.Sep(),
                    K("Close", Ctrl, 0x57), K("Save", Ctrl, 0x53), K("Save As…", F12),
                    Mb.Sep(),
                    K("Print…", Ctrl, 0x50),
                }));
                menus.Add(("Edit", new object[]
                {
                    K("Undo", Ctrl, 0x5A), K("Redo", Ctrl, 0x59),
                    Mb.Sep(),
                    K("Cut", Ctrl, 0x58), K("Copy", Ctrl, 0x43), K("Paste", Ctrl, 0x56), K("Select All", Ctrl, 0x41),
                    Mb.Sep(),
                    K("Find…", Ctrl, 0x46), K("Replace…", Ctrl, 0x48),
                    Emoji(),
                }));
                break;

            case Kind.Discord:
                menus.Add(("File", new object[] { K("Upload a File…", Ctrl, Shift, 0x55), Mb.Sep(), CloseWindow() }));
                menus.Add(("Edit", Editing(redoShift: true, find: true)));
                menus.Add(("View", new object[]
                {
                    K("Reload", Ctrl, 0x52),
                    Mb.Sep(),
                    K("Actual Size", Ctrl, 0x30), K("Zoom In", Ctrl, Plus), K("Zoom Out", Ctrl, Minus),
                }));
                menus.Add(("Go", new object[]
                {
                    K("Quick Switcher…", Ctrl, 0x4B),
                    Mb.Sep(),
                    K("Previous Channel", Alt, Up), K("Next Channel", Alt, Down),
                    K("Previous Unread Channel", Alt, Shift, Up), K("Next Unread Channel", Alt, Shift, Down),
                    Mb.Sep(),
                    K("Mark Channel Read", Esc), K("Mark Server Read", Shift, Esc),
                }));
                menus.Add(("Call", new object[]
                {
                    K("Answer Call", Ctrl, Enter), K("Decline Call", Esc),
                    Mb.Sep(),
                    K("Mute / Unmute", Ctrl, Shift, 0x4D), K("Deafen / Undeafen", Ctrl, Shift, 0x44),
                }));
                break;

            case Kind.Zoom:
                menus.Add(("Edit", Editing(redoShift: false, find: false)));
                menus.Add(("Meeting", new object[]
                {
                    K("Mute / Unmute Audio", Alt, 0x41), K("Start / Stop Video", Alt, 0x56),
                    Mb.Sep(),
                    K("Share Screen", Alt, 0x53), K("Raise / Lower Hand", Alt, 0x59),
                    Mb.Sep(),
                    K("Chat", Alt, 0x48), K("Participants", Alt, 0x55), K("Invite…", Alt, 0x49),
                    Mb.Sep(),
                    K("Enter / Exit Full Screen", Alt, 0x46),
                    Mb.Sep(),
                    K("Leave Meeting", Alt, 0x51),
                }));
                break;

            case Kind.WhatsApp:
                menus.Add(("File", new object[] { K("New Chat", Ctrl, 0x4E), Mb.Sep(), CloseWindow() }));
                menus.Add(("Edit", Editing(redoShift: false, find: true)));
                menus.Add(("Chat", new object[]
                {
                    K("Mute", Ctrl, Shift, 0x4D), K("Mark as Unread", Ctrl, Shift, 0x55), K("Archive Chat", Ctrl, 0x45),
                }));
                break;

            case Kind.Electron:
                menus.Add(("File", new object[] { CloseWindow() }));
                menus.Add(("Edit", Editing(redoShift: true, find: false)));
                menus.Add(("View", new object[]
                {
                    K("Reload", Ctrl, 0x52),
                    Mb.Sep(),
                    K("Actual Size", Ctrl, 0x30), K("Zoom In", Ctrl, Plus), K("Zoom Out", Ctrl, Minus),
                    Mb.Sep(),
                    K("Toggle Full Screen", F11),
                    Mb.Sep(),
                    K("Developer Tools", Ctrl, Shift, 0x49),
                }));
                break;

            default:
                // (an app MacShell doesn't know: what nearly every app does)
                menus.Add(("File", new object[] { CloseWindow() }));
                menus.Add(("Edit", Editing(redoShift: false, find: false)));
                menus.Add(("View", new object[] { K("Enter Full Screen", F11) }));
                break;
        }
        return (menus, settings);
    }

    /// <summary>Diagnostics (--open appmenus): the menus each running app gets.</summary>
    public static string Describe(RunningApp app, IntPtr hwnd)
    {
        var (menus, settings) = For(app, hwnd);
        var sb = new System.Text.StringBuilder($"{app.Name} [{KindOf(app, hwnd)}]{(settings != null ? " Settings " + Gesture(settings) : "")}\n");
        foreach (var (title, items) in menus)
            sb.Append($"   {title}: ").AppendLine(string.Join(", ", items.OfType<System.Windows.Controls.MenuItem>().Select(i => $"{i.Header} {i.InputGestureText}".Trim())));
        return sb.ToString();
    }

    /// <summary>Undo … Select All, as nearly every text field takes them; Find where the app has it.</summary>
    static object[] Editing(bool redoShift, bool find, ushort[] findNext = null)
    {
        var items = new List<object>
        {
            K("Undo", Ctrl, 0x5A),
            redoShift ? K("Redo", Ctrl, Shift, 0x5A) : K("Redo", Ctrl, 0x59),
            Mb.Sep(),
            K("Cut", Ctrl, 0x58), K("Copy", Ctrl, 0x43), K("Paste", Ctrl, 0x56), K("Select All", Ctrl, 0x41),
        };
        if (find)
        {
            items.Add(Mb.Sep());
            items.Add(K("Find…", Ctrl, 0x46));
            if (findNext != null) items.Add(K("Find Next", findNext));
        }
        items.Add(Mb.Sep());
        items.Add(Emoji());
        return items.ToArray();
    }

    static object Emoji() => Mb.Item("Emoji & Symbols", () => ShellHost.SendToApp(Win, Period), "fn E");

    static object CloseWindow() => Mb.Item("Close Window", () =>
    {
        var h = WindowTracker.LastExternalForeground;
        if (h != IntPtr.Zero) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }, "⇧⌘W");

    /// <summary>A menu item that presses the app's shortcut; it shows that shortcut the Mac way (⌘ for Ctrl).</summary>
    static object K(string title, params ushort[] keys) => Mb.Item(title, () => ShellHost.SendToApp(keys), Gesture(keys));

    public static string Gesture(ushort[] keys)
    {
        string mods = (keys.Contains(Alt) ? "⌥" : "") + (keys.Contains(Shift) ? "⇧" : "") + (keys.Contains(Ctrl) ? "⌘" : "");
        var key = keys.LastOrDefault(k => k is not (Ctrl or Shift or Alt or Win));
        string name = key switch
        {
            >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x70 and <= 0x7B => "F" + (key - 0x6F),
            Tab => "⇥", Esc => "⎋", Enter => "↩", Left => "←", Up => "↑", Right => "→", Down => "↓", Home => "↖",
            Plus => "+", Minus => "−", Comma => ",", Period => ".",
            _ => "",
        };
        return mods + name;
    }
}
