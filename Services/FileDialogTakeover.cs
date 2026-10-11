using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using MacShell.Finder;
using MacShell.Native;
using static MacShell.Native.NativeMethods;

namespace MacShell.Services;

/// <summary>
/// The Windows file dialog, in every app, becomes the Mac's Open / Save panel (Finder/FilePanel). When an app shows its
/// Open or Save dialog, MacShell reads it - what it's for, its kinds of file, its folder, the name it suggests - hides
/// it (see-through and click-through: still there, not closed) and shows the panel instead. What's chosen there is typed
/// into the dialog and its Open / Save button pressed, so the app gets its file exactly as if the dialog had been used;
/// Cancel cancels it. "Use the Windows window" brings the dialog back. Apps running as administrator are left alone
/// (Windows won't let MacShell reach them), and so is anything that isn't the modern file dialog.
/// </summary>
public static class FileDialogTakeover
{
    sealed class Dialog
    {
        public IntPtr Hwnd, NameBox, TypeCombo;
        public long ExStyle;
        public bool Save, Folders;
        public FilePanel Panel;
        public bool Replace;
        public int Filter;
    }

    static readonly Dictionary<IntPtr, Dialog> _open = new();
    static readonly HashSet<IntPtr> _leftAlone = new();   // (shown again for the user: not taken twice)
    static DispatcherTimer _watch;
    static readonly uint Me = (uint)Environment.ProcessId;

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    const int WM_GETTEXT = 0x000D, WM_SETTEXT = 0x000C, CB_GETCOUNT = 0x0146, CB_GETCURSEL = 0x0147, CB_GETLBTEXT = 0x0148, CB_SETCURSEL = 0x014E,
              BM_CLICK = 0x00F5, CBN_SELCHANGE = 1, IDOK = 1, IDCANCEL = 2, IDYES = 6;
    const uint LWA_ALPHA = 0x2;

    static string LeftoversFile => Path.Combine(Settings.DataDirectory, "hidden-dialogs.txt");

    // ================================================================== a dialog appears

    /// <summary>Every window shown (WindowTracker's event hook): is it another app's file dialog?</summary>
    public static void OnWindowShown(IntPtr hwnd)
    {
        if (!Settings.Current.MacFilePanels || hwnd == IntPtr.Zero || _open.ContainsKey(hwnd) || _leftAlone.Contains(hwnd)) return;
        if (GetClassNameOf(hwnd) != "#32770" || GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Me) return;
        try { TakeOver(hwnd); } catch (Exception ex) { App.Log("file dialog: " + ex.Message); }
    }

    static List<(IntPtr h, string cls, int id)> Parts(IntPtr hwnd)
    {
        var parts = new List<(IntPtr h, string cls, int id)>();
        EnumChildWindows(hwnd, (h, _) => { parts.Add((h, GetClassNameOf(h), GetDlgCtrlID(h))); return true; }, IntPtr.Zero);
        return parts;
    }

    static void TakeOver(IntPtr hwnd)
    {
        // the modern file dialog (its folder view is DirectUI): hidden at once - see-through and click-through - so it
        // never flashes up, while its other parts may still be on their way
        if (!Parts(hwnd).Any(p => p.cls == "DUIViewWndClassName")) return;
        long ex = GetExStyle(hwnd);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));
        GetWindowThreadProcessId(hwnd, out uint pid);
        if ((GetExStyle(hwnd) & WS_EX_LAYERED) == 0) { Log($"0x{hwnd.ToInt64():X} ({ProcessName(pid)}): out of reach (an administrator's app?)"); return; }
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        var d = new Dialog { Hwnd = hwnd, ExStyle = ex };
        _open[hwnd] = d;
        SaveLeftovers();
        Log($"0x{hwnd.ToInt64():X} ({ProcessName(pid)}) \"{GetWindowTitle(hwnd)}\": a file dialog - hidden");
        Ready(d, 0);
    }

    /// <summary>Its name box, OK and Cancel there yet? (an app like Brave makes them a moment after the window) - then
    /// read and answered with the panel; not there within a second: not a dialog the panel can stand in for.</summary>
    static void Ready(Dialog d, int attempt)
    {
        if (!_open.ContainsKey(d.Hwnd)) return;
        if (!IsWindow(d.Hwnd)) { Forget(d); return; }
        var parts = Parts(d.Hwnd);
        // the name box: Open's (1148), Save's (1001) or a folder picker's (1152)
        var nameBox = parts.FirstOrDefault(p => p.cls == "Edit" && p.id is 1148 or 1001 or 1152 && IsWindowVisible(p.h)).h;
        var ok = parts.FirstOrDefault(p => p.cls == "Button" && p.id == IDOK).h;
        if (nameBox == IntPtr.Zero || ok == IntPtr.Zero || !parts.Any(p => p.cls == "Button" && p.id == IDCANCEL))
        {
            if (attempt < 16) { Delay(60, () => Ready(d, attempt + 1)); return; }
            Log($"0x{d.Hwnd.ToInt64():X}: no name box or buttons after a second - the Windows dialog is shown again");
            ShowAgain(d);
            return;
        }
        Describe(d, parts, nameBox, ok);
    }

    static void Delay(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    static void Describe(Dialog d, List<(IntPtr h, string cls, int id)> parts, IntPtr nameBox, IntPtr ok)
    {
        var hwnd = d.Hwnd;
        int nameId = GetDlgCtrlID(nameBox);
        bool save = nameId == 1001;
        // the kinds of file: Open's list (1136), or Save's (the other drop-down beside the name)
        var nameCombo = GetParent(nameBox);
        var type = parts.FirstOrDefault(p => p.cls == "ComboBox" && p.id == 1136 && IsWindowVisible(p.h)).h;
        if (type == IntPtr.Zero && save)
            type = parts.FirstOrDefault(p => p.cls == "ComboBox" && p.h != nameCombo && IsWindowVisible(p.h) && GetClassNameOf(GetParent(p.h)) == "FloatNotifySink").h;
        d.NameBox = nameBox;
        d.TypeCombo = type;
        d.Save = save;

        string okText = Text(ok).Replace("&", "").Trim();
        string label = Text(parts.FirstOrDefault(p => p.cls == "Static" && p.id == 1090).h);
        d.Folders = !save && (nameId == 1152 || okText.Contains("Folder", StringComparison.OrdinalIgnoreCase) || label.TrimStart('&').StartsWith("Folder", StringComparison.OrdinalIgnoreCase));
        var filters = new List<string>();
        int current = 0;
        if (type != IntPtr.Zero)
        {
            int n = (int)SendMessage(type, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < n && i < 100; i++)
            {
                var sb = new StringBuilder(1024);
                SendMessage(type, CB_GETLBTEXT, new IntPtr(i), sb);
                filters.Add(sb.ToString());
            }
            current = Math.Max(0, (int)SendMessage(type, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero));
        }
        d.Filter = current;
        var owner = GetWindow(hwnd, GW_OWNER);
        GetWindowRect(owner != IntPtr.Zero && IsWindowVisible(owner) ? owner : hwnd, out RECT over);
        GetWindowThreadProcessId(hwnd, out uint pid);
        var (appName, appKey) = AppOf(pid);
        var req = new FilePanelRequest
        {
            Save = save, Folders = d.Folders, Title = GetWindowTitle(hwnd), Prompt = okText,
            Folder = FolderOf(parts), FileName = save ? Text(nameBox) : null,
            Filters = filters, FilterIndex = current, AppName = appName, AppKey = appKey, Over = over, OfferWindowsDialog = true,
        };
        // more than one file allowed? (only the dialog's list knows: asked off the UI thread - it's another process)
        Task.Run(() => !save && CanChooseMany(hwnd)).ContinueWith(t => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_open.ContainsKey(hwnd)) return;
            req.Multiple = !t.IsFaulted && t.Result;
            Log($"0x{hwnd.ToInt64():X}: {(save ? "save" : d.Folders ? "folder" : "open")} panel for {appName} - name box {GetDlgCtrlID(d.NameBox)}, " +
                $"{filters.Count} kinds, folder \"{req.Folder}\", many={req.Multiple}");
            d.Panel = FilePanel.Show(req, r => Answer(d, r));
            StartWatching();
        }));
    }

    static string ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    /// <summary>What happened to each dialog (%APPDATA%\MacShell\file-dialogs.log, kept short): for finding out why an
    /// app's dialog wasn't taken over, or didn't take what was chosen.</summary>
    static void Log(string s)
    {
        try
        {
            string f = Path.Combine(Settings.DataDirectory, "file-dialogs.log");
            if (File.Exists(f) && new FileInfo(f).Length > 128 * 1024) File.WriteAllLines(f, File.ReadAllLines(f).TakeLast(300));
            File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {s}{Environment.NewLine}");
        }
        catch { }
    }

    static bool CanChooseMany(IntPtr dlg)
    {
        try
        {
            var view = AutomationElement.FromHandle(dlg).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "UIItemsView"));
            return view?.GetCurrentPattern(SelectionPattern.Pattern) is SelectionPattern sp && sp.Current.CanSelectMultiple;
        }
        catch { return false; }
    }

    /// <summary>Which app is asking: its name, and a key to remember its folder by (its program's file name).</summary>
    static (string name, string key) AppOf(uint pid)
    {
        var app = WindowTracker.Apps.Values.FirstOrDefault(a => a.Pids.Contains(pid));
        string path = GetProcessPath(pid);
        string key = path != null ? Path.GetFileName(path).ToLowerInvariant() : null;
        string name = app?.Name;
        if (name == null && path != null)
        {
            try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
        }
        return (name, key);
    }

    /// <summary>The dialog's folder, from its address bar: a path - or a known folder's name ("Documents").</summary>
    static string FolderOf(List<(IntPtr h, string cls, int id)> parts)
    {
        var bar = parts.FirstOrDefault(p => p.cls == "ToolbarWindow32" && p.id == 1001 && GetClassNameOf(GetParent(p.h)) == "Breadcrumb Parent").h;
        if (bar == IntPtr.Zero) return null;
        string t = Text(bar);
        int colon = t.IndexOf(": ", StringComparison.Ordinal);
        string v = (colon >= 0 ? t[(colon + 2)..] : t).Trim();
        if (v.Length == 0) return null;
        if (Path.IsPathRooted(v) && Directory.Exists(v)) return v;
        foreach (var (name, path) in KnownFolders())
            if (string.Equals(name, v, StringComparison.CurrentCultureIgnoreCase)) return path;
        return null;
    }

    static List<(string name, string path)> _known;

    /// <summary>The well-known folders, by the names Windows shows for them (in its own language).</summary>
    static List<(string name, string path)> KnownFolders()
    {
        if (_known != null) return _known;
        var list = new List<(string, string)>();
        foreach (var p in new[] { FinderLocation.Desktop, FinderLocation.Documents, FinderLocation.Downloads, FinderLocation.Pictures, FinderLocation.Music, FinderLocation.Movies, FinderLocation.Home, FinderLocation.ICloud })
        {
            if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) continue;
            var info = new SHFILEINFO();
            SHGetFileInfo(p, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
            if (!string.IsNullOrEmpty(info.szDisplayName)) list.Add((info.szDisplayName, p));
            list.Add((Path.GetFileName(p.TrimEnd('\\')), p));
        }
        return _known = list;
    }

    static string Text(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(2048);
        SendMessage(h, WM_GETTEXT, new IntPtr(sb.Capacity), sb);
        return sb.ToString();
    }

    // ================================================================== the answer

    static void Answer(Dialog d, FilePanelResult r)
    {
        d.Panel = null;
        if (!IsWindow(d.Hwnd)) { Forget(d); return; }
        if (r == null)
        {
            Log($"0x{d.Hwnd.ToInt64():X}: cancelled");
            PostMessage(d.Hwnd, WM_COMMAND, new IntPtr(IDCANCEL), IntPtr.Zero);
            Forget(d);
            return;
        }
        if (r.UseWindowsDialog)
        {
            Log($"0x{d.Hwnd.ToInt64():X}: the Windows dialog wanted - shown again");
            ShowAgain(d);
            ActivateWindow(d.Hwnd);
            return;
        }
        Log($"0x{d.Hwnd.ToInt64():X}: chosen - {r.Paths.Count} item(s), kind {r.FilterIndex}");
        // the kind chosen in the panel (an app may go by it - which format to save in)
        if (d.TypeCombo != IntPtr.Zero && r.FilterIndex != d.Filter)
        {
            SendMessage(d.TypeCombo, CB_SETCURSEL, new IntPtr(r.FilterIndex), IntPtr.Zero);
            var parent = GetParent(d.TypeCombo);
            SendMessage(parent, WM_COMMAND, new IntPtr((CBN_SELCHANGE << 16) | (GetDlgCtrlID(d.TypeCombo) & 0xFFFF)), d.TypeCombo);
        }
        string text = r.Paths.Count == 1 ? r.Paths[0] : string.Join(" ", r.Paths.Select(p => "\"" + p + "\""));
        SendMessage(d.NameBox, WM_SETTEXT, IntPtr.Zero, text);
        d.Replace = r.ReplaceConfirmed;
        PostMessage(d.Hwnd, WM_COMMAND, new IntPtr(IDOK), IntPtr.Zero);
        if (d.Folders && r.Paths.Count == 1) SelectCurrentFolder(d, r.Paths[0]);
        else WatchClosing(d);
    }

    /// <summary>A folder picker given a folder's path goes into it (rather than choosing it): once it's there, an empty
    /// name and Select Folder again choose the folder it's in.</summary>
    static void SelectCurrentFolder(Dialog d, string folder)
    {
        string leaf = Path.GetFileName(folder.TrimEnd('\\'));
        if (leaf.Length == 0) leaf = folder;
        var started = DateTime.Now;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        t.Tick += (_, _) =>
        {
            if (!IsWindow(d.Hwnd) || !IsWindowVisible(d.Hwnd)) { t.Stop(); Log($"0x{d.Hwnd.ToInt64():X}: the app took it"); Forget(d); return; }
            // there yet? (its address bar names the folder - or, after a moment, it's taken as there)
            var bar = Parts(d.Hwnd).FirstOrDefault(p => p.cls == "ToolbarWindow32" && p.id == 1001 && GetClassNameOf(GetParent(p.h)) == "Breadcrumb Parent").h;
            string at = Text(bar);
            bool there = at.EndsWith(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || at.EndsWith(": " + leaf, StringComparison.OrdinalIgnoreCase) || at.EndsWith("\\" + leaf, StringComparison.OrdinalIgnoreCase);
            if (!there && (DateTime.Now - started).TotalSeconds < 2) return;
            t.Stop();
            SendMessage(d.NameBox, WM_SETTEXT, IntPtr.Zero, "");
            PostMessage(d.Hwnd, WM_COMMAND, new IntPtr(IDOK), IntPtr.Zero);
            WatchClosing(d);
        };
        t.Start();
    }

    /// <summary>After OK: gone - done. Windows asking "replace it?" - yes (the panel asked already). Still there after a
    /// few seconds (it didn't take the name) - shown again, for the user to see why.</summary>
    static void WatchClosing(Dialog d)
    {
        var started = DateTime.Now;
        bool said = false;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        t.Tick += (_, _) =>
        {
            if (!IsWindow(d.Hwnd) || !IsWindowVisible(d.Hwnd)) { t.Stop(); Log($"0x{d.Hwnd.ToInt64():X}: the app took it"); Forget(d); return; }
            var question = OwnedDialog(d.Hwnd);
            if (question != IntPtr.Zero)
            {
                if (d.Replace && !said && FindChild(question, IDYES) is var yes && yes != IntPtr.Zero)
                {
                    said = true;
                    SendMessage(yes, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                started = DateTime.Now;   // (the user is answering a question of the app's)
                return;
            }
            if ((DateTime.Now - started).TotalSeconds > 3)
            {
                t.Stop();
                Log($"0x{d.Hwnd.ToInt64():X}: still open 3 s after OK (name box now \"{Text(d.NameBox)}\") - the Windows dialog is shown again");
                ShowAgain(d);
            }
        };
        t.Start();
    }

    static IntPtr OwnedDialog(IntPtr owner)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, GW_OWNER) == owner && GetClassNameOf(h) == "#32770") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr FindChild(IntPtr parent, int id)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) => { if (GetDlgCtrlID(h) == id && GetClassNameOf(h) == "Button") { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    /// <summary>The Windows dialog visible and clickable again (and not taken over a second time).</summary>
    static void ShowAgain(Dialog d)
    {
        if (IsWindow(d.Hwnd)) SetWindowLongPtr(d.Hwnd, GWL_EXSTYLE, new IntPtr(d.ExStyle));
        _leftAlone.Add(d.Hwnd);
        Forget(d);
    }

    static void Forget(Dialog d)
    {
        _open.Remove(d.Hwnd);
        _leftAlone.RemoveWhere(h => !IsWindow(h));
        SaveLeftovers();
        if (d.Panel != null) { var p = d.Panel; d.Panel = null; p.Dismiss(); }
    }

    // ================================================================== while a panel stands in

    static void StartWatching()
    {
        if (_watch != null) { _watch.Start(); return; }
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watch.Tick += (_, _) =>
        {
            foreach (var d in _open.Values.ToList())
            {
                // the app closed its dialog itself: the panel goes too
                if (!IsWindow(d.Hwnd)) { Forget(d); continue; }
                // the app's window clicked (it brings its dialog forward, invisible): the panel is brought back on top
                if (d.Panel != null && GetForegroundWindow() == d.Hwnd)
                    ActivateWindow(new System.Windows.Interop.WindowInteropHelper(d.Panel).Handle);
            }
            if (_open.Count == 0) _watch.Stop();
        };
        _watch.Start();
    }

    // ================================================================== never left hidden

    /// <summary>MacShell quitting: any dialog it hid is shown again (the app keeps working).</summary>
    public static void RestoreAll()
    {
        foreach (var d in _open.Values.ToList())
        {
            if (IsWindow(d.Hwnd)) SetWindowLongPtr(d.Hwnd, GWL_EXSTYLE, new IntPtr(d.ExStyle));
            _open.Remove(d.Hwnd);
        }
        SaveLeftovers();
    }

    /// <summary>MacShell starting after it stopped unexpectedly: dialogs it had hidden are shown again.</summary>
    public static void RestoreLeftovers()
    {
        try
        {
            if (!File.Exists(LeftoversFile)) return;
            foreach (var line in File.ReadAllLines(LeftoversFile))
            {
                var parts = line.Split(' ');
                if (parts.Length != 2 || !long.TryParse(parts[0], out long h) || !long.TryParse(parts[1], out long ex)) continue;
                var hwnd = new IntPtr(h);
                if (IsWindow(hwnd) && GetClassNameOf(hwnd) == "#32770" && (GetExStyle(hwnd) & WS_EX_LAYERED) != 0)
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            }
            File.Delete(LeftoversFile);
        }
        catch { }
    }

    static void SaveLeftovers()
    {
        try
        {
            if (_open.Count == 0) { if (File.Exists(LeftoversFile)) File.Delete(LeftoversFile); return; }
            File.WriteAllLines(LeftoversFile, _open.Values.Select(d => $"{d.Hwnd.ToInt64()} {d.ExStyle}"));
        }
        catch { }
    }

    /// <summary>Diagnostics: what's taken over now.</summary>
    public static string Describe() => _open.Count == 0 ? "none" :
        string.Join("; ", _open.Values.Select(d => $"0x{d.Hwnd.ToInt64():X} save={d.Save} folders={d.Folders} panel={(d.Panel != null)}"));
}
