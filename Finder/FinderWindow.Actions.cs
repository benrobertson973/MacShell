using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Finder;

public partial class FinderWindow
{
    // ================================================================== open

    public void Open(FileItem item, bool newTab = false)
    {
        if (item == null) return;
        if (item.IsTrashItem)
        {
            if (ShellHost.Alert($"“{item.Name}” is in the Trash.", "To use this item, first drag it out of the Trash.", "Put Back", "OK") == "Put Back")
                PutBack(new List<FileItem> { item });
            return;
        }
        if (item.IsApp) { AppCatalog.Launch(item.AppTarget); return; }
        if (item.IsFolder || item.IsDrive)
        {
            if (newTab) NewTab(item.FullPath); else Navigate(item.FullPath);
            return;
        }
        if (item.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string target = FileOps.ResolveAlias(item.FullPath);
            if (target != null && Directory.Exists(target)) { Navigate(target); return; }
        }
        AppCatalog.OpenFile(item.FullPath);
    }

    void OpenSelection()
    {
        var sel = SelectedItems;
        if (sel.Count == 1) Open(sel[0]);
        else foreach (var s in sel.Where(s => !s.IsFolder)) Open(s);
    }

    List<string> SelectedPaths => SelectedItems.Where(i => !i.IsApp && !FinderLocation.IsVirtual(i.FullPath)).Select(i => i.FullPath).ToList();

    void PutBack(List<FileItem> items)
    {
        foreach (var it in items)
        {
            try
            {
                dynamic fi = it.ShellObject;
                if (fi == null) continue;
                foreach (dynamic verb in fi.Verbs())
                {
                    string name = ((string)verb.Name).Replace("&", "");
                    if (name.StartsWith("Restore", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Undelete", StringComparison.OrdinalIgnoreCase))
                    {
                        verb.DoIt();
                        break;
                    }
                }
            }
            catch { }
        }
        Dispatcher.BeginInvoke(() => Reload(false), DispatcherPriority.Background);
    }

    void DeleteImmediately(List<FileItem> items)
    {
        if (ShellHost.Alert($"Are you sure you want to delete {(items.Count == 1 ? $"“{items[0].Name}”" : $"these {items.Count} items")} immediately?", "You can’t undo this action.", "Cancel", "Delete") != "Delete") return;
        foreach (var it in items)
        {
            try
            {
                if (Directory.Exists(it.FullPath)) Directory.Delete(it.FullPath, true);
                else if (File.Exists(it.FullPath)) File.Delete(it.FullPath);
            }
            catch { }
        }
        Reload(false);
    }

    // ================================================================== commands

    public void Execute(string cmd)
    {
        var sel = SelectedItems;
        switch (cmd)
        {
            case "newWindow": ShellHost.OpenFinder(null); break;
            case "newTab": NewTab(null); break;
            case "newFolder": NewFolder(); break;
            case "open": OpenSelection(); break;
            case "close": HandleCloseShortcut(); break;
            case "getInfo": foreach (var s in (sel.Count > 0 ? sel : new List<FileItem> { ItemForPath(CurrentFolder ?? FinderLocation.Home) }).Take(10)) GetInfoWindow.ShowFor(s); break;
            case "rename": BeginRename(); break;
            case "compress": { var z = FileOps.Compress(SelectedPaths); if (z != null) _pendingSelect = z; break; }
            case "duplicate": FileOps.Duplicate(SelectedPaths); break;
            case "alias": FileOps.MakeAlias(SelectedPaths); break;
            case "quicklook": QuickLookWindow.Toggle(this, sel.FirstOrDefault()); break;
            case "showOriginal":
                {
                    var lnk = sel.FirstOrDefault(s => s.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase));
                    string t = lnk != null ? FileOps.ResolveAlias(lnk.FullPath) : null;
                    if (t != null) ShellHost.RevealInFinder(t);
                    break;
                }
            case "trash":
                if (_tab.Location == FinderLocation.Trash) DeleteImmediately(sel);
                else FileOps.MoveToTrash(SelectedPaths);
                break;
            case "find": FocusSearch(); break;
            case "cut": { var p = SelectedPaths; FileOps.CopyToClipboard(p, true); foreach (var s in sel) s.IsCut = true; break; }
            case "copy": FileOps.CopyToClipboard(SelectedPaths, false); break;
            case "paste": if (CurrentFolder != null) FileOps.Paste(CurrentFolder); break;
            case "selectAll": SelectAll(); break;
            case "copyPath": { var p = SelectedPaths; try { Clipboard.SetText(p.Count > 0 ? string.Join(Environment.NewLine, p) : CurrentFolder ?? ""); } catch { } break; }
            case "showHidden": Settings.Current.ShowHiddenFiles = !Settings.Current.ShowHiddenFiles; Settings.Save(); foreach (var w in All) w.Reload(true); break;
            case "view:icons": SetViewMode("icons"); break;
            case "view:list": SetViewMode("list"); break;
            case "view:columns": SetViewMode("columns"); break;
            case "view:gallery": SetViewMode("gallery"); break;
            case "sort:name": SortBy("name"); break;
            case "sort:kind": SortBy("kind"); break;
            case "sort:date": SortBy("date"); break;
            case "sort:size": SortBy("size"); break;
            case "toggleSidebar": Settings.Current.FinderShowSidebar = !Settings.Current.FinderShowSidebar; Settings.Save(); break;
            case "togglePathBar": Settings.Current.FinderShowPathBar = !Settings.Current.FinderShowPathBar; Settings.Save(); break;
            case "toggleStatusBar": Settings.Current.FinderShowStatusBar = !Settings.Current.FinderShowStatusBar; Settings.Save(); break;
            case "zoom": ToggleZoom(); break;
            case "back": GoBack(); break;
            case "forward": GoForward(); break;
            case "up": GoUp(); break;
            case "go:recents": Navigate(FinderLocation.Recents); break;
            case "go:documents": Navigate(FinderLocation.Documents); break;
            case "go:desktop": Navigate(FinderLocation.Desktop); break;
            case "go:downloads": Navigate(FinderLocation.Downloads); break;
            case "go:home": Navigate(FinderLocation.Home); break;
            case "go:computer": Navigate(FinderLocation.Computer); break;
            case "go:applications": Navigate(FinderLocation.Applications); break;
            case "go:icloud": if (FinderLocation.ICloud != null) Navigate(FinderLocation.ICloud); break;
            case "go:utilities": Navigate(Environment.GetFolderPath(Environment.SpecialFolder.CommonAdminTools) is { Length: > 0 } adm && Directory.Exists(adm) ? adm : FinderLocation.Applications); break;
            case "go:trash": Navigate(FinderLocation.Trash); break;
            case "goto": GoToFolderDialog(); break;
            case "emptyTrash": FileOps.EmptyTrash(); if (_tab.Location == FinderLocation.Trash) Reload(false); break;
            case "hide": WindowState = WindowState.Minimized; break;
            case "terminal": FileOps.OpenInTerminal(CurrentFolder ?? FinderLocation.Home); break;
        }
    }

    void NewFolder()
    {
        string folder = CurrentFolder;
        if (folder == null) return;
        string path = FileOps.NewFolder(folder);
        if (path == null) return;
        _pendingSelect = path;
        _renameAfterLoad = path;
        Reload(false);
    }
    string _renameAfterLoad;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _view?.FocusView();
    }

    void GoToFolderDialog()
    {
        var path = TextPrompt.Ask(this, "Go to Folder", "Enter a path:", CurrentFolder ?? FinderLocation.Home);
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith("~")) path = FinderLocation.Home + path[1..].Replace('/', '\\');
        if (Directory.Exists(path)) Navigate(path);
        else if (File.Exists(path)) ShellHost.RevealInFinder(path);
        else ShellHost.Alert("The folder can’t be found.", path, "OK");
    }

    void ShareSelection()
    {
        var p = SelectedPaths;
        var items = new List<object>
        {
            Mb.Item("Copy", () => FileOps.CopyToClipboard(p, false), enabled: p.Count > 0),
            Mb.Item("Copy as Pathname", () => Execute("copyPath")),
            Mb.Sep(),
            Mb.Item("Show in Windows Explorer", () => RevealInExplorer(p.FirstOrDefault() ?? CurrentFolder)),
        };
        Mb.Context(items.ToArray()).IsOpen = true;
    }

    static void RevealInExplorer(string path)
    {
        if (path == null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\""); } catch { }
    }

    List<string> SelectedCommonTags()
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return new List<string>();
        return Theme.TagColors.Select(t => t.id).Where(id => sel.All(s => s.Tags.Contains(id))).ToList();
    }

    void ToggleTag(string tag)
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        FileOps.SetTag(sel.Select(s => s.FullPath), tag);
        foreach (var s in sel) s.RefreshTags();
        if (_tab.Location.StartsWith(FinderLocation.TagPrefix)) Reload(true);
    }

    // ================================================================== menus

    void ShowMenuUnder(FrameworkElement anchor, IEnumerable<object> items)
    {
        var cm = Mb.Context(items.ToArray());
        cm.PlacementTarget = anchor;
        cm.Placement = PlacementMode.Bottom;
        cm.HorizontalOffset = -14;
        cm.VerticalOffset = -2;
        cm.IsOpen = true;
    }

    IEnumerable<object> SortMenuItems() => new object[]
    {
        Mb.SectionHeader("Sort By"),
        Mb.Item("Name", () => SortBy("name"), isChecked: _sortKey == "name"),
        Mb.Item("Kind", () => SortBy("kind"), isChecked: _sortKey == "kind"),
        Mb.Item("Date Modified", () => SortBy("date"), isChecked: _sortKey == "date"),
        Mb.Item("Size", () => SortBy("size"), isChecked: _sortKey == "size"),
        Mb.Sep(),
        Mb.Item("Keep Folders on Top", () => { Settings.Current.FinderFoldersOnTop = !Settings.Current.FinderFoldersOnTop; Settings.Save(); Rebuild(); }, isChecked: Settings.Current.FinderFoldersOnTop),
    };

    object[] ItemMenuItems(List<FileItem> sel)
    {
        if (sel.Count == 0) return BackgroundMenuItems();
        var first = sel[0];
        string label = sel.Count == 1 ? $"“{first.DisplayName}”" : $"{sel.Count} Items";
        if (first.IsTrashItem)
        {
            return new object[]
            {
                Mb.Item("Put Back", () => PutBack(sel)),
                Mb.Item("Delete Immediately…", () => DeleteImmediately(sel)),
                Mb.Item("Empty Trash", () => { FileOps.EmptyTrash(); Reload(false); }),
                Mb.Sep(),
                Mb.Item("Get Info", () => GetInfoWindow.ShowFor(first)),
                Mb.Item($"Quick Look {label}", () => QuickLookWindow.Toggle(this, first)),
            };
        }
        if (first.IsApp)
        {
            return new object[]
            {
                Mb.Item("Open", () => { foreach (var s in sel) Open(s); }),
                Mb.Sep(),
                Mb.Item("Show in Enclosing Folder", () => ShellHost.RevealInFinder(AppCatalog.FindByParsingName(first.AppTarget)?.TargetPath), enabled: AppCatalog.FindByParsingName(first.AppTarget)?.TargetPath != null),
                Mb.Item("Get Info", () => GetInfoWindow.ShowFor(first)),
                Mb.Sep(),
                Mb.Item("Keep in Dock", () =>
                {
                    Settings.Current.DockApps ??= new List<PinnedApp>();
                    foreach (var s in sel)
                        if (Settings.Current.DockApps.All(p => p.Target != s.AppTarget))
                            Settings.Current.DockApps.Add(new PinnedApp { Name = s.Name, Target = s.AppTarget, ExePath = AppCatalog.FindByParsingName(s.AppTarget)?.TargetPath });
                    Settings.Save();
                }),
            };
        }
        bool isZip = sel.Count == 1 && first.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        var list = new List<object>
        {
            Mb.Item("Open", OpenSelection),
            sel.Count == 1 && !first.IsFolder ? Mb.Sub("Open With",
                Mb.Item("Default Application", () => AppCatalog.OpenFile(first.FullPath)),
                Mb.Sep(),
                Mb.Item("Other…", () => AppCatalog.OpenWith(first.FullPath))) : null,
            sel.Count == 1 && first.IsFolder ? Mb.Item("Open in New Tab", () => NewTab(first.FullPath)) : null,
            Mb.Sep(),
            Mb.Item("Move to Trash", () => Execute("trash")),
            Mb.Sep(),
            Mb.Item("Get Info", () => Execute("getInfo")),
            Mb.Item("Rename", BeginRename, enabled: sel.Count == 1),
            isZip ? Mb.Item($"Expand {label}", () => FileOps.Extract(first.FullPath)) : Mb.Item($"Compress {label}", () => Execute("compress")),
            Mb.Item("Duplicate", () => Execute("duplicate")),
            Mb.Item("Make Alias", () => Execute("alias")),
            Mb.Item($"Quick Look {(sel.Count == 1 ? label : "")}".Trim(), () => QuickLookWindow.Toggle(this, first)),
            Mb.Sep(),
            Mb.Item($"Copy {label}", () => Execute("copy")),
            Mb.Sep(),
            Mb.TagRow(SelectedCommonTags(), ToggleTag),
            Mb.Sep(),
            first.IsFolder && sel.Count == 1 ? Mb.Item("Add to Sidebar", () =>
            {
                Settings.Current.SidebarFavorites ??= new List<string>();
                if (!Settings.Current.SidebarFavorites.Contains(first.FullPath)) Settings.Current.SidebarFavorites.Add(first.FullPath);
                Settings.Save();
                foreach (var w in All) { w.BuildSidebar(); w.HighlightSidebar(); }
            }) : null,
            !string.IsNullOrEmpty(_tab.Search) || FinderLocation.IsVirtual(_tab.Location) ? Mb.Item("Show in Enclosing Folder", () => ShellHost.RevealInFinder(first.FullPath)) : null,
            Mb.Sub("Services",
                first.IsFolder ? Mb.Item("New Terminal at Folder", () => FileOps.OpenInTerminal(first.FullPath)) : null,
                Mb.Item("Copy as Pathname", () => Execute("copyPath")),
                Mb.Item("Show in Windows Explorer", () => RevealInExplorer(first.FullPath)),
                Mb.Item("Windows Properties…", () => FileOps.ShowWindowsProperties(first.FullPath))),
        };
        return list.ToArray();
    }

    object[] BackgroundMenuItems()
    {
        bool real = CurrentFolder != null;
        bool canPaste = false;
        try { canPaste = real && Clipboard.ContainsFileDropList(); } catch { }
        string mode = _tab.ViewMode;
        return new object[]
        {
            Mb.Item("New Folder", NewFolder, enabled: real),
            Mb.Sep(),
            Mb.Item("Get Info", () => Execute("getInfo")),
            Mb.Sep(),
            Mb.Item("Paste Item", () => Execute("paste"), enabled: canPaste),
            Mb.Sep(),
            Mb.Sub("View",
                Mb.Item("as Icons", () => SetViewMode("icons"), isChecked: mode == "icons"),
                Mb.Item("as List", () => SetViewMode("list"), isChecked: mode == "list"),
                Mb.Item("as Columns", () => SetViewMode("columns"), isChecked: mode == "columns"),
                Mb.Item("as Gallery", () => SetViewMode("gallery"), isChecked: mode == "gallery")),
            Mb.Sub("Sort By", SortMenuItems().Skip(1).ToArray()),
            Mb.Sep(),
            _tab.Location == FinderLocation.Trash ? Mb.Item("Empty Trash", () => { FileOps.EmptyTrash(); Reload(false); }) : null,
            real ? Mb.Item("New Terminal at Folder", () => FileOps.OpenInTerminal(CurrentFolder)) : null,
            real ? Mb.Item("Show in Windows Explorer", () => RevealInExplorer(CurrentFolder)) : null,
        };
    }

    // ================================================================== keyboard

    string _typeBuffer = "";
    DateTime _typeTime;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool inText = Keyboard.FocusedElement is TextBox;
        if (inText)
        {
            if (mods == ModifierKeys.Control && key == Key.W) { base.OnPreviewKeyDown(e); }
            return;
        }

        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        string cmd = null;
        if (ctrl && !alt)
        {
            cmd = (key, shift) switch
            {
                (Key.N, false) => "newWindow",
                (Key.N, true) => "newFolder",
                (Key.T, false) => "newTab",
                (Key.O, false) => "open",
                (Key.Down, false) => "open",
                (Key.Up, false) => "up",
                (Key.I, false) => "getInfo",
                (Key.D, false) => "duplicate",
                (Key.Y, false) => "quicklook",
                (Key.F, false) => "find",
                (Key.A, false) => "selectAll",
                (Key.C, false) => "copy",
                (Key.X, false) => "cut",
                (Key.V, false) => "paste",
                (Key.Back, false) => "trash",
                (Key.Back, true) => "emptyTrash",
                (Key.D1, false) => "view:icons",
                (Key.D2, false) => "view:list",
                (Key.D3, false) => "view:columns",
                (Key.D4, false) => "view:gallery",
                (Key.OemOpenBrackets, false) => "back",
                (Key.OemCloseBrackets, false) => "forward",
                (Key.G, true) => "goto",
                (Key.D, true) => "go:desktop",
                (Key.O, true) => "go:documents",
                (Key.H, true) => "go:home",
                (Key.A, true) => "go:applications",
                (Key.F, true) => "go:recents",
                (Key.C, true) => "go:computer",
                (Key.I, true) => "go:icloud",
                (Key.OemPeriod, true) => "showHidden",
                (Key.OemQuestion, false) => "toggleStatusBar",
                (Key.L, false) => "alias",
                _ => null,
            };
        }
        else if (ctrl && alt)
        {
            cmd = key switch { Key.L => "go:downloads", Key.C => "copyPath", Key.P => "togglePathBar", Key.S => "toggleSidebar", _ => null };
        }
        else if (alt && !ctrl)
        {
            cmd = key switch { Key.Left => "back", Key.Right => "forward", Key.Up => "up", _ => null };
        }
        else if (mods == ModifierKeys.None)
        {
            cmd = key switch
            {
                Key.Delete => "trash",
                Key.F2 => "rename",
                Key.Enter => "rename",
                Key.Space => "quicklook",
                Key.Back => "back",
                Key.F5 => "refresh",
                _ => null,
            };
        }
        if (cmd == "refresh") { Reload(true); e.Handled = true; return; }
        if (cmd != null)
        {
            Execute(cmd);
            e.Handled = true;
            return;
        }

        if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End && !ctrl && !alt)
        {
            if (_view.HandleKey(key, mods)) { e.Handled = true; return; }
            MoveSelection(key, shift);
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.None || mods == ModifierKeys.Shift)
        {
            string ch = KeyToChar(key);
            if (ch != null)
            {
                if ((DateTime.Now - _typeTime).TotalMilliseconds > 900) _typeBuffer = "";
                _typeTime = DateTime.Now;
                _typeBuffer += ch;
                var match = Scope.FirstOrDefault(i => i.Name.StartsWith(_typeBuffer, StringComparison.CurrentCultureIgnoreCase))
                            ?? Scope.Where(i => string.Compare(i.Name, _typeBuffer, StringComparison.CurrentCultureIgnoreCase) > 0).FirstOrDefault();
                if (match != null) { SelectOnly(match); _view.Reveal(match); }
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    static string KeyToChar(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return ((char)('a' + (k - Key.A))).ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        return null;
    }

    void MoveSelection(Key key, bool extend)
    {
        var list = Scope.ToList();
        if (list.Count == 0) return;
        var current = list.LastOrDefault(i => i.IsSelected && i == _anchor) ?? list.LastOrDefault(i => i.IsSelected);
        int idx = current == null ? -1 : list.IndexOf(current);
        int perRow = _tab.ViewMode == "icons" ? _view.ItemsPerRow : 1;
        int next = idx;
        switch (key)
        {
            case Key.Home: next = 0; break;
            case Key.End: next = list.Count - 1; break;
            case Key.Left:
                if (_tab.ViewMode == "list")
                {
                    if (current != null && current.IsExpanded) { ToggleExpand(current); return; }
                    if (current?.ParentItem != null) { SelectOnly(current.ParentItem); _view.Reveal(current.ParentItem); return; }
                    return;
                }
                next = idx < 0 ? 0 : idx - 1; break;
            case Key.Right:
                if (_tab.ViewMode == "list") { if (current != null && current.IsFolder && !current.IsExpanded) ToggleExpand(current); return; }
                next = idx < 0 ? 0 : idx + 1; break;
            case Key.Up: next = idx < 0 ? list.Count - 1 : idx - perRow; break;
            case Key.Down: next = idx < 0 ? 0 : idx + perRow; break;
        }
        if (_tab.ViewMode == "gallery" && key is Key.Up or Key.Down) next = key == Key.Up ? idx - 1 : idx + 1;
        if (next < 0 || next >= list.Count) return;
        var target = list[next];
        if (extend && _anchor != null)
        {
            int a = list.IndexOf(_anchor);
            if (a < 0) a = next;
            int lo = Math.Min(a, next), hi = Math.Max(a, next);
            for (int i = 0; i < list.Count; i++) list[i].IsSelected = i >= lo && i <= hi;
            SelectionChanged();
        }
        else SelectOnly(target);
        _view.Reveal(target);
    }

    /// <summary>Called by QuickLook to step through items with the arrow keys.</summary>
    public void QuickLookStep(Key key) => MoveSelection(key, false);

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_renameAfterLoad != null)
        {
            var target = _display.FirstOrDefault(i => string.Equals(i.FullPath, _renameAfterLoad, StringComparison.OrdinalIgnoreCase));
            if (target != null) { _renameAfterLoad = null; SelectOnly(target); BeginRename(); }
        }
    }

    public void TryPendingRename()
    {
        if (_renameAfterLoad == null) return;
        var target = _display.FirstOrDefault(i => string.Equals(i.FullPath, _renameAfterLoad, StringComparison.OrdinalIgnoreCase));
        if (target != null) { _renameAfterLoad = null; SelectOnly(target); BeginRename(); }
    }
}

/// <summary>Routes menu-bar Finder commands to the frontmost Finder window (or the desktop).</summary>
public static class FinderCommands
{
    public static void Run(string cmd)
    {
        var w = FinderWindow.Active != null && FinderWindow.All.Contains(FinderWindow.Active) ? FinderWindow.Active : null;
        bool desktopFront = w == null || ShellHost.IsDesktopWindow(Native.NativeMethods.GetForegroundWindow()) || !w.IsActive && WindowTracker.LastForeground != IntPtr.Zero && ShellHost.IsDesktopWindow(WindowTracker.LastForeground);
        if (desktopFront)
        {
            var d = ShellHost.Desktops.FirstOrDefault(x => x.IsPrimary);
            if (d != null && d.HandlesCommand(cmd)) { d.Execute(cmd); return; }
        }
        switch (cmd)
        {
            case "newWindow": ShellHost.OpenFinder(null); return;
            case "emptyTrash": FileOps.EmptyTrash(); return;
            case "hide": foreach (var f in FinderWindow.All) f.WindowState = WindowState.Minimized; return;
        }
        if (w == null)
        {
            if (cmd.StartsWith("go:") || cmd == "goto") { w = ShellHost.OpenFinder(null); }
            else return;
        }
        w.Execute(cmd);
    }
}
