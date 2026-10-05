using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

public class DockItem
{
    public string Kind;          // app | finder | launchpad | settings | sep | downloads | trash
    public string Target;
    public string ExePath;
    public string Key;
    public string Name;
    public bool Pinned;
    public RunningApp Running;
    public ImageSource Icon;
    public bool IconIsVector;
    public string IconSourceUsed;
    public string Badge;
    public double Size, TargetSize, X = double.NaN, TargetX;
    public double BounceStart = -1;
    public bool IsSeparator => Kind == "sep";
    public bool IsRunning => Kind == "finder" || (Running != null && Running.Windows.Count > 0);
}

/// <summary>The Dock: magnifying app launcher / switcher at the bottom of the screen.</summary>
public class DockWindow : Window
{
    readonly Surface _surface;
    readonly List<DockItem> _items = new();
    readonly Dictionary<string, DockItem> _byKey = new();
    IntPtr _hwnd;
    bool _hiddenForFullscreen;
    public IntPtr Handle => _hwnd;
    DispatcherTimer _trashTimer, _autoHideTimer;
    bool _trashFull;

    double Base => Math.Clamp(Settings.Current.DockIconSize, 24, 128);
    double Mag => Settings.Current.DockMagnification ? Math.Max(Base, Math.Clamp(Settings.Current.DockMagnifiedSize, 24, 160)) : Base;
    double Gap => Math.Round(Base * 0.07);
    double PadX => Math.Round(Base * 0.11);
    double PadTop => Math.Round(Base * 0.1);
    double PadBottom => Math.Round(Base * 0.15);
    double PanelHeight => Base + PadTop + PadBottom;
    const double BottomMargin = 4;
    const double LabelSpace = 48;
    double SepWidth => Math.Round(Base * 0.3);

    public double ReservedHeight => Settings.Current.DockAutoHide ? 2 : PanelHeight + BottomMargin + 6;

    public DockWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Title = "Dock";
        AllowDrop = true;
        UseLayoutRounding = false;
        _surface = new Surface(this);
        Content = _surface;
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        Reposition();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            AddExStyle(_hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            WindowTracker.RegisterChrome(this);
            HwndSource.FromHwnd(_hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
                if (Takeover.HandleAppBarMessage(h, msg, w, l)) handled = true;
                return IntPtr.Zero;
            });
        };

        WindowTracker.AppsChanged += Rebuild;
        Badges.Changed += RefreshBadges;
        WindowTracker.ActiveAppChanged += () => _surface.InvalidateVisual();
        Wallpaper.Changed += () => _surface.InvalidateVisual();
        Theme.Changed += () => _surface.InvalidateVisual();
        Settings.Changed += () => { Reposition(); Rebuild(); };

        _trashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trashTimer.Tick += (_, _) => UpdateTrash();
        _trashTimer.Start();
        UpdateTrash();

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _autoHideTimer.Tick += (_, _) => AutoHideTick();
        _autoHideTimer.Start();

        EnsureDefaultPins();
        Rebuild();
    }

    public void Reposition()
    {
        double h = Mag + PadTop + PadBottom + BottomMargin + LabelSpace;
        Left = ShellHost.ScreenPx.Left / ShellHost.Scale;
        Width = ShellHost.ScreenDip.Width;
        Height = h;
        Top = ShellHost.ScreenPx.Top / ShellHost.Scale + ShellHost.ScreenDip.Height - h;
        _surface?.Kick();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        _hiddenForFullscreen = hidden;
        if (hidden) Hide(); else { Show(); Topmost = false; Topmost = true; }
    }

    void UpdateTrash()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
            SHQueryRecycleBin(null, ref info);
            bool full = info.i64NumItems > 0;
            if (full != _trashFull)
            {
                _trashFull = full;
                if (_byKey.TryGetValue("internal:trash", out var t)) { t.Icon = MacIcons.Trash(full); _surface.InvalidateVisual(); }
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ items

    public void EnsureDefaultPins()
    {
        if (Settings.Current.DockApps != null || !AppCatalog.IsLoaded) return;
        var pins = new List<PinnedApp>();
        void Add(AppEntry e) { if (e != null && pins.All(p => p.Target != e.ParsingName)) pins.Add(new PinnedApp { Name = e.Name, Target = e.ParsingName, ExePath = e.TargetPath }); }
        string browser = AppCatalog.DefaultBrowserExe();
        Add(AppCatalog.FindByExe(browser) ?? AppCatalog.FindByName("Safari", "Google Chrome", "Microsoft Edge", "Firefox", "Brave"));
        Add(AppCatalog.FindByName("Outlook (new)", "Mail", "Outlook"));
        Add(AppCatalog.FindByName("Photos"));
        Add(AppCatalog.FindByName("Spotify", "Media Player", "Apple Music", "iTunes"));
        Add(AppCatalog.FindByName("Calendar"));
        Add(AppCatalog.FindByName("Notepad", "Sticky Notes"));
        Add(AppCatalog.FindByName("Calculator"));
        Add(AppCatalog.FindByName("Terminal", "Windows Terminal", "Windows PowerShell"));
        Add(AppCatalog.FindByName("Visual Studio Code"));
        Add(AppCatalog.FindByName("Microsoft Store"));
        pins.Add(new PinnedApp { Name = "System Settings", Target = "internal:settings" });
        Settings.Current.DockApps = pins;
        Settings.Save(false);
    }

    DockItem Get(string key, Func<DockItem> make)
    {
        if (!_byKey.TryGetValue(key, out var it)) { it = make(); it.Key = key; _byKey[key] = it; }
        return it;
    }

    public void Rebuild()
    {
        var list = new List<DockItem>();
        var apps = WindowTracker.Apps;

        var finder = Get("internal:finder", () => new DockItem { Kind = "finder", Name = "Finder", Target = "internal:finder", Icon = MacIcons.Finder, IconIsVector = true, Pinned = true });
        finder.Running = WindowTracker.FindByKey(WindowTracker.FinderKey);
        list.Add(finder);
        list.Add(Get("internal:launchpad", () => new DockItem { Kind = "launchpad", Name = "Launchpad", Target = "internal:launchpad", Icon = MacIcons.Launchpad, IconIsVector = true, Pinned = true }));

        var usedKeys = new HashSet<string> { WindowTracker.FinderKey, "internal:launchpad" };
        foreach (var p in Settings.Current.DockApps ?? new List<PinnedApp>())
        {
            string key = WindowTracker.KeyForTarget(p.Target, p.ExePath);
            if (key == null || usedKeys.Contains(key)) continue;
            usedKeys.Add(key);
            var item = Get(key, () => MakeAppItem(p.Target, p.ExePath, p.Name));
            item.Pinned = true;
            item.Target = p.Target;
            item.Running = apps.TryGetValue(key, out var ra) ? ra : null;
            if (item.Running != null && item.Name == null) item.Name = item.Running.Name;
            list.Add(item);
        }
        foreach (var ra in apps.Values)
        {
            if (usedKeys.Contains(ra.Key) || ra.Windows.Count == 0) continue;
            usedKeys.Add(ra.Key);
            var item = Get(ra.Key, () => MakeAppItem(ra.LaunchTarget, ra.ExePath, ra.Name, ra.IconSource));
            item.Pinned = false;
            item.Running = ra;
            item.Name = ra.Name;
            list.Add(item);
        }
        list.Add(Get("sep:1", () => new DockItem { Kind = "sep" }));
        list.Add(Get("internal:downloads", () => new DockItem { Kind = "downloads", Name = "Downloads", Icon = MacIcons.Folder("arrow.down.circle"), IconIsVector = true, Pinned = true }));
        var trash = Get("internal:trash", () => new DockItem { Kind = "trash", Name = "Trash", Icon = MacIcons.Trash(_trashFull), IconIsVector = true, Pinned = true });
        list.Add(trash);

        foreach (var it in list.Where(i => i.BounceStart >= 0 && i.IsRunning)) it.BounceStart = -1;
        if (AppCatalog.IsLoaded) foreach (var it in list.Where(i => i.Kind == "app")) EnsureIcon(it, it.Pinned ? null : it.Running?.IconSource);
        _items.Clear();
        _items.AddRange(list);
        RefreshBadges();
        _surface.Kick();
        ShellHost.UpdateWorkArea();
    }

    public IEnumerable<string> DescribeItems() =>
        _items.Select(i => $"item {i.Kind,-9} pinned={i.Pinned,-5} running={i.IsRunning,-5} badge={i.Badge ?? "-",-3} {i.Name} | key={i.Key}");

    void RefreshBadges()
    {
        bool changed = false;
        foreach (var it in _items)
        {
            string b = it.Kind is "app" or "finder" or "settings" ? Badges.For(it.Key, it.Target, it.Running) : null;
            if (b != it.Badge) { it.Badge = b; changed = true; }
        }
        if (changed) _surface.InvalidateVisual();
    }

    DockItem MakeAppItem(string target, string exe, string name, string iconSource = null)
    {
        var item = new DockItem { Kind = target == "internal:settings" ? "settings" : "app", Target = target, ExePath = exe, Name = name };
        if (target == "internal:settings") { item.Icon = MacIcons.SystemSettings; item.IconIsVector = true; item.Name = "System Settings"; return item; }
        item.Icon = MacIcons.GenericApp;
        item.IconIsVector = true;
        EnsureIcon(item, iconSource);
        return item;
    }

    /// <summary>(Re)loads an app item's icon once the best icon source is known (the catalog loads asynchronously).</summary>
    void EnsureIcon(DockItem item, string iconSource = null)
    {
        if (item.Kind != "app") return;
        string src = iconSource;
        var e = AppCatalog.FindByParsingName(item.Target);
        if (src == null) src = e != null ? e.IconSource : (File.Exists(item.Target ?? "") ? item.Target : item.ExePath ?? item.Running?.IconSource);
        if (e != null) item.Name ??= e.Name;
        item.Name ??= item.Running?.Name;
        if (src == null || src == item.IconSourceUsed) return;
        item.IconSourceUsed = src;
        ShellIcons.Load(src, 256, false, bmp =>
        {
            if (bmp == null || item.IconSourceUsed != src) return;
            item.Icon = bmp; item.IconIsVector = false;
            _surface.InvalidateVisual();
        }, true, "dock:" + src);
    }

    // ------------------------------------------------------------------ actions

    void Click(DockItem it)
    {
        switch (it.Kind)
        {
            case "sep": return;
            case "finder": ShellHost.OpenFinderOrActivate(); return;
            case "launchpad": LaunchpadWindow.Toggle(); return;
            case "settings": SettingsWindow.ShowPane(null); return;
            case "trash": ShellHost.OpenFinder(FinderLocation.Trash); return;
            case "downloads": StackPopup.Show(this, it, GetKnownFolder(FOLDERID_Downloads)); return;
        }
        var running = it.Running != null ? WindowTracker.FindByKey(it.Running.Key) ?? it.Running : null;
        if (running != null && running.Windows.Count > 0)
        {
            WindowTracker.ActivateApp(running);
            return;
        }
        if (AppCatalog.Launch(it.Target ?? it.ExePath))
        {
            it.BounceStart = Environment.TickCount64 / 1000.0;
            _surface.Kick();
        }
    }

    void ShowMenu(DockItem it, Rect iconRect)
    {
        var cm = new ContextMenu();
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var running = it.Running != null ? WindowTracker.FindByKey(it.Running.Key) ?? it.Running : null;
        switch (it.Kind)
        {
            case "sep":
                Mb.Add(cm.Items, Mb.Item("Turn Hiding On", () => { Settings.Current.DockAutoHide = !Settings.Current.DockAutoHide; Settings.Save(); }, isChecked: Settings.Current.DockAutoHide));
                Mb.Add(cm.Items, Mb.Item("Turn Magnification On", () => { Settings.Current.DockMagnification = !Settings.Current.DockMagnification; Settings.Save(); }, isChecked: Settings.Current.DockMagnification));
                Mb.Add(cm.Items, Mb.Sep());
                Mb.Add(cm.Items, Mb.Item("Dock Settings…", () => SettingsWindow.ShowPane("dock")));
                break;
            case "trash":
                Mb.Add(cm.Items, Mb.Item("Open", () => ShellHost.OpenFinder(FinderLocation.Trash)));
                Mb.Add(cm.Items, Mb.Sep());
                Mb.Add(cm.Items, Mb.Item("Empty Trash", () => FileOps.EmptyTrash(), enabled: _trashFull));
                break;
            case "downloads":
                string dl = GetKnownFolder(FOLDERID_Downloads);
                Mb.Add(cm.Items, Mb.Item("Open “Downloads”", () => ShellHost.OpenFinder(dl)));
                break;
            case "launchpad":
                Mb.Add(cm.Items, Mb.Item("Open", LaunchpadWindow.Toggle));
                break;
            case "finder":
                Mb.Add(cm.Items, Mb.Item("Run…", () => StartShell("explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}")));
                Mb.Add(cm.Items, Mb.Item("Windows Settings", () => StartShell("ms-settings:", null)));
                Mb.Add(cm.Items, Mb.Sep());
                AddWindowList(cm, running);
                Mb.Add(cm.Items, Mb.Item("New Finder Window", () => ShellHost.OpenFinder(null)));
                Mb.Add(cm.Items, Mb.Item("Find…", () => ShellHost.OpenFinder(null).FocusSearch()));
                Mb.Add(cm.Items, Mb.Sep());
                Mb.Add(cm.Items, Mb.Item("Hide", () => WindowTracker.HideApp(running), enabled: running?.Windows.Count > 0));
                break;
            default:
                AddWindowList(cm, running);
                var options = Mb.Sub("Options",
                    it.Pinned ? Mb.Item("Keep in Dock", () => Unpin(it), isChecked: true) : Mb.Item("Keep in Dock", () => Pin(it)),
                    Mb.Sep(),
                    Mb.Item("Show in Finder", () => ShellHost.RevealInFinder(it.ExePath ?? AppCatalog.FindByParsingName(it.Target)?.TargetPath ?? running?.ExePath),
                        enabled: (it.ExePath ?? AppCatalog.FindByParsingName(it.Target)?.TargetPath ?? running?.ExePath) != null));
                Mb.Add(cm.Items, options);
                Mb.Add(cm.Items, Mb.Sep());
                if (running != null && running.Windows.Count > 0)
                {
                    Mb.Add(cm.Items, Mb.Item("Show All Windows", () => MissionControlWindow.Show(running)));
                    Mb.Add(cm.Items, Mb.Item("Hide", () => WindowTracker.HideApp(running)));
                    Mb.Add(cm.Items, alt ? Mb.Item("Force Quit", () => WindowTracker.ForceQuitApp(running)) : Mb.Item("Quit", () => WindowTracker.QuitApp(running)));
                }
                else Mb.Add(cm.Items, Mb.Item("Open", () => Click(it)));
                break;
        }
        OpenMenuAbove(cm, iconRect);
    }

    static void StartShell(string file, string args)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); } catch { }
    }

    void AddWindowList(ContextMenu cm, RunningApp running)
    {
        if (running == null || running.Windows.Count == 0) return;
        foreach (var w in running.Windows.Take(15))
        {
            var h = w.Hwnd;
            string t = string.IsNullOrWhiteSpace(w.Title) ? running.Name : w.Title;
            if (t.Length > 50) t = t[..47] + "…";
            Mb.Add(cm.Items, Mb.Item(t, () => ActivateWindow(h), isChecked: h == WindowTracker.LastExternalForeground));
        }
        Mb.Add(cm.Items, Mb.Sep());
    }

    void OpenMenuAbove(ContextMenu cm, Rect iconRect)
    {
        cm.PlacementTarget = _surface;
        cm.Placement = PlacementMode.Custom;
        cm.PlacementRectangle = iconRect;
        cm.CustomPopupPlacementCallback = (popup, target, offset) =>
        {
            double k = target.Width > iconRect.Width * 1.2 ? ShellHost.Scale : 1; // device vs DIP units
            return new[] { new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height + (20 - 10) * k), PopupPrimaryAxis.Horizontal) };
        };
        _surface.MenuOpen = true;
        cm.Opened += (_, _) => MenuDismisser.Opened("dock", () => cm.IsOpen = false);
        cm.Closed += (_, _) => { _surface.MenuOpen = false; _surface.Kick(); MenuDismisser.Closed("dock"); };
        cm.IsOpen = true;
    }

    PinnedApp PinFor(DockItem item) => Settings.Current.DockApps?.FirstOrDefault(p => WindowTracker.KeyForTarget(p.Target, p.ExePath) == item.Key);

    static bool IsPinnedApp(DockItem i) => i.Pinned && i.Kind is "app" or "settings";

    /// <summary>A launch target that survives app updates: the catalog ID rather than a versioned/packaged exe path.</summary>
    static (string target, string exe, string name) Stable(string target, string exe, string name)
    {
        if (target != null && !target.StartsWith("internal:") && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var e = AppCatalog.FindByExe(target);
            if (e != null) return (e.ParsingName, e.TargetPath, e.Name);
        }
        return (target, exe, name);
    }

    void Pin(DockItem it)
    {
        var list = Settings.Current.DockApps ??= new List<PinnedApp>();
        var (target, exe, name) = Stable(it.Target ?? it.Running?.LaunchTarget ?? it.ExePath, it.ExePath ?? it.Running?.ExePath, it.Name);
        if (target == null || list.Any(p => WindowTracker.KeyForTarget(p.Target, p.ExePath) == it.Key)) return;
        list.Add(new PinnedApp { Name = name, Target = target, ExePath = exe });
        Settings.Save();
    }

    void Unpin(DockItem it)
    {
        var list = Settings.Current.DockApps;
        if (list == null) return;
        list.RemoveAll(p => WindowTracker.KeyForTarget(p.Target, p.ExePath) == it.Key);
        Settings.Save();
    }

    /// <summary>
    /// Where in the saved pin list something dropped at <paramref name="insertAt"/> belongs: right after the pinned
    /// icon to its left (so hidden or duplicate pins in the list can't throw the position off).
    /// </summary>
    int PinIndexFor(List<DockItem> layout, int insertAt)
    {
        var list = Settings.Current.DockApps;
        for (int i = Math.Min(insertAt, layout.Count) - 1; i >= 0; i--)
        {
            if (!IsPinnedApp(layout[i])) continue;
            var p = PinFor(layout[i]);
            if (p != null) return list.IndexOf(p) + 1;
        }
        for (int i = Math.Max(0, insertAt); i < layout.Count; i++)
        {
            if (!IsPinnedApp(layout[i])) continue;
            var p = PinFor(layout[i]);
            if (p != null) return list.IndexOf(p);
        }
        return list.Count;
    }

    /// <summary>Moves (or pins) an app to the slot it was dropped in; <paramref name="layout"/> excludes the dragged icon.</summary>
    void MoveTo(DockItem it, List<DockItem> layout, int insertAt)
    {
        var list = Settings.Current.DockApps ??= new List<PinnedApp>();
        var pin = PinFor(it);
        if (pin == null)
        {
            var (target, exe, name) = Stable(it.Target ?? it.Running?.LaunchTarget ?? it.ExePath, it.ExePath ?? it.Running?.ExePath, it.Name);
            if (target == null) return;
            pin = new PinnedApp { Name = name, Target = target, ExePath = exe };
        }
        else list.Remove(pin);
        list.Insert(Math.Clamp(PinIndexFor(layout, insertAt), 0, list.Count), pin);
        Settings.Save();
    }

    /// <summary>Pins apps / PWAs / shortcuts dropped between Dock icons at that position.</summary>
    void PinTargets(List<string> targets, int insertAt)
    {
        var list = Settings.Current.DockApps ??= new List<PinnedApp>();
        var pins = new List<PinnedApp>();
        foreach (var raw in targets)
        {
            var e = AppCatalog.FindByParsingName(raw);
            var (t, exe, name) = Stable(raw, e?.TargetPath ?? (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw : null),
                                       e?.Name ?? (File.Exists(raw) ? Path.GetFileNameWithoutExtension(raw) : raw));
            string key = WindowTracker.KeyForTarget(t, exe);
            var existing = list.FirstOrDefault(p => WindowTracker.KeyForTarget(p.Target, p.ExePath) == key);
            if (existing != null) list.Remove(existing);
            pins.Add(existing ?? new PinnedApp { Name = name, Target = t, ExePath = exe });
        }
        int at = Math.Clamp(PinIndexFor(_items, insertAt), 0, list.Count);
        list.InsertRange(at, pins);
        Settings.Save();
    }

    /// <summary>
    /// One-time tidy of saved pins: drop the ApplicationFrameHost placeholder (Store apps are identified properly
    /// now), convert versioned / packaged exe paths to stable app IDs, and remove duplicates.
    /// </summary>
    public void NormalizePins()
    {
        var list = Settings.Current.DockApps;
        if (list == null || !AppCatalog.IsLoaded) return;
        bool changed = false;
        var seen = new HashSet<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (string.IsNullOrEmpty(p.Target) || p.Target.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i--); changed = true; continue;
            }
            var (t, exe, name) = Stable(p.Target, p.ExePath, p.Name);
            if (t != p.Target) { p.Target = t; p.ExePath = exe; p.Name = name; changed = true; }
            string key = WindowTracker.KeyForTarget(p.Target, p.ExePath);
            if (key == null || !seen.Add(key)) { list.RemoveAt(i--); changed = true; }
        }
        if (changed) Settings.Save(false);
    }
    // ------------------------------------------------------------------ auto-hide

    double _hideOffset;          // 0 = shown, >0 = pushed down
    DateTime _lastInside = DateTime.Now;

    void AutoHideTick()
    {
        if (!Settings.Current.DockAutoHide)
        {
            if (_hideOffset != 0) { _hideOffset = 0; _surface.Kick(); }
            return;
        }
        GetCursorPos(out POINT p);
        double y = p.Y / ShellHost.Scale, screenBottom = (ShellHost.ScreenPx.Bottom) / ShellHost.Scale;
        bool atEdge = y >= screenBottom - 3;
        bool inside = _surface.IsHovering || _surface.MenuOpen || _surface.Dragging || StackPopup.IsOpen;
        if (atEdge || inside) _lastInside = DateTime.Now;
        double target = (DateTime.Now - _lastInside).TotalMilliseconds > 450 ? PanelHeight + BottomMargin + 10 : 0;
        if (Math.Abs(target - _hideOffset) > 0.5 || (target == 0 && _hideOffset != 0))
        {
            _surface.HideTarget = target;
            _surface.Kick();
        }
    }

    // ================================================================== rendering surface

    public const string AppDragFormat = "MacShell.AppTarget";

    /// <summary>Things that can be pinned when dropped between Dock icons (apps, PWAs, shortcuts).</summary>
    public static List<string> AppTargetsFrom(IDataObject data)
    {
        var list = new List<string>();
        if (data.GetDataPresent(AppDragFormat) && data.GetData(AppDragFormat) is string s)
            list.AddRange(s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 &&
                 files.All(f => File.Exists(f) && Path.GetExtension(f).ToLowerInvariant() is ".lnk" or ".exe" or ".appref-ms" or ".url"))
            list.AddRange(files);
        return list;
    }

    class Surface : FrameworkElement
    {
        readonly DockWindow _d;
        bool _animating;
        TimeSpan _lastFrame;
        Point _mouse;
        public bool IsHovering, MenuOpen, Dragging;
        DockItem _hover, _pressed, _dragItem, _dropTarget;
        Point _pressPoint;
        double _dragX, _dragY;
        DateTime _outsideSince = DateTime.MaxValue;
        bool _extInsert;           // an app is being dragged in from Launchpad / Finder / the desktop
        double _extX;
        int _insertAt = -1;        // index into the layout list (the list without the dragged icon)
        public double HideTarget;
        double _hide;
        Rect _panel;
        Point _poofAt;
        double _poofStart = -1;

        public Surface(DockWindow d)
        {
            _d = d;
            ClipToBounds = false;
            MouseMove += OnMove;
            MouseLeave += (_, _) => { if (!Dragging) { IsHovering = false; _hover = null; Kick(); } };
            MouseLeftButtonDown += OnDown;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += OnRight;
            LostMouseCapture += (_, _) => { if (Dragging) CancelDrag(); };
            AllowDrop = true;
            DragOver += OnDragOver;
            DragLeave += (_, _) => { _dropTarget = null; _extInsert = false; IsHovering = false; Kick(); };
            Drop += OnDrop;
        }

        public void Kick()
        {
            if (!_animating)
            {
                _animating = true;
                _lastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += OnFrame;
            }
            InvalidateVisual();
        }

        double Now => Environment.TickCount64 / 1000.0;

        void OnFrame(object sender, EventArgs e)
        {
            var rt = ((RenderingEventArgs)e).RenderingTime;
            double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((rt - _lastFrame).TotalSeconds, 0.001, 0.05);
            if (rt == _lastFrame) return;
            _lastFrame = rt;
            bool moving = Step(dt);
            InvalidateVisual();
            if (!moving)
            {
                _animating = false;
                CompositionTarget.Rendering -= OnFrame;
            }
        }

        double SlotW(DockItem i) => i.IsSeparator ? _d.SepWidth : _d.Base;

        List<DockItem> LayoutItems => Dragging && _dragItem != null ? _d._items.Where(i => i != _dragItem).ToList() : _d._items;

        /// <summary>
        /// Where an app dropped at <paramref name="x"/> would go. Computed on the compact layout (no gap), so the
        /// answer depends only on the pointer and doesn't flip back and forth as the gap opens.
        /// </summary>
        int InsertIndex(List<DockItem> layout, double x)
        {
            double gap = _d.Gap;
            double W = ActualWidth > 0 ? ActualWidth : _d.Width;
            double total = layout.Sum(SlotW) + gap * Math.Max(0, layout.Count - 1);
            double cx = W / 2 - total / 2;
            int firstApp = Math.Min(2, layout.Count);                       // after Finder and Launchpad
            int sep = layout.FindIndex(i => i.IsSeparator);
            if (sep < 0) sep = layout.Count;
            int idx = sep;
            for (int i = 0; i < layout.Count; i++)
            {
                double w = SlotW(layout[i]);
                if (x < cx + w / 2) { idx = i; break; }
                cx += w + gap;
            }
            return Math.Clamp(idx, firstApp, sep);
        }

        bool FarOutside => Dragging && _dragY < PanelTop - _d.Base * 1.3;
        /// <summary>mac: an icon is only removed after it has been held away from the Dock for a moment.</summary>
        bool RemoveArmed => FarOutside && _dragItem != null && _dragItem.Pinned && (DateTime.Now - _outsideSince).TotalMilliseconds > 450;

        /// <summary>Advances the layout toward its targets; returns true while anything is still moving.</summary>
        bool Step(double dt)
        {
            var items = _d._items;
            if (items.Count == 0) return false;
            double b = _d.Base, M = _d.Mag, gap = _d.Gap;
            double W = ActualWidth > 0 ? ActualWidth : _d.Width;
            var layout = LayoutItems;

            bool inserting = (Dragging && _dragItem != null && !RemoveArmed) || _extInsert;
            _insertAt = inserting ? InsertIndex(layout, Dragging ? _dragX : _extX) : -1;
            double total = layout.Sum(SlotW) + gap * (layout.Count - 1) + (inserting ? b + gap : 0);
            double start = W / 2 - total / 2;

            var baseLeft = new double[layout.Count];
            double x = start;
            for (int i = 0; i < layout.Count; i++)
            {
                if (i == _insertAt) x += b + gap;
                baseLeft[i] = x;
                x += SlotW(layout[i]) + gap;
            }

            bool magnify = IsHovering && !MenuOpen && M > b + 0.5 && !Dragging && !_extInsert;
            double R = b * 3.1;
            for (int i = 0; i < layout.Count; i++)
            {
                var it = layout[i];
                double s = SlotW(it);
                if (magnify && !it.IsSeparator)
                {
                    double c = baseLeft[i] + s / 2;
                    double d = Math.Abs(_mouse.X - c);
                    double f = d < R ? (Math.Cos(Math.PI * d / R) + 1) / 2 : 0;
                    s = b + (M - b) * f;
                }
                it.TargetSize = s;
            }
            if (magnify)
            {
                // anchor the item under the cursor so it stays under the cursor while neighbours spread
                int k = 0;
                for (int i = 0; i < layout.Count; i++)
                    if (_mouse.X >= baseLeft[i] - gap / 2) k = i;
                double slot = SlotW(layout[k]) + gap;
                double t = Math.Clamp((_mouse.X - (baseLeft[k] - gap / 2)) / slot, 0, 1);
                double left = _mouse.X - t * (layout[k].TargetSize + gap) + gap / 2;
                layout[k].TargetX = left;
                for (int i = k - 1; i >= 0; i--) layout[i].TargetX = layout[i + 1].TargetX - gap - layout[i].TargetSize;
                for (int i = k + 1; i < layout.Count; i++) layout[i].TargetX = layout[i - 1].TargetX + layout[i - 1].TargetSize + gap;
            }
            else
            {
                for (int i = 0; i < layout.Count; i++) layout[i].TargetX = baseLeft[i];
            }

            double kLerp = 1 - Math.Pow(0.0008, dt / 0.22);
            bool moving = false;
            foreach (var it in layout)
            {
                if (double.IsNaN(it.X)) { it.X = it.TargetX; it.Size = it.TargetSize; }
                double nx = it.X + (it.TargetX - it.X) * kLerp;
                double ns = it.Size + (it.TargetSize - it.Size) * kLerp;
                if (Math.Abs(nx - it.TargetX) < 0.05) nx = it.TargetX;
                if (Math.Abs(ns - it.TargetSize) < 0.05) ns = it.TargetSize;
                if (nx != it.TargetX || ns != it.TargetSize) moving = true;
                it.X = nx; it.Size = ns;
                if (it.BounceStart >= 0)
                {
                    moving = true;
                    if (Now - it.BounceStart > 0.6 * 4) it.BounceStart = -1;
                }
            }
            double nh = _hide + (HideTarget - _hide) * kLerp;
            if (Math.Abs(nh - HideTarget) < 0.3) nh = HideTarget;
            if (nh != HideTarget) moving = true;
            _hide = nh;
            _d._hideOffset = _hide;
            if (_poofStart >= 0) { if (Now - _poofStart > 0.45) _poofStart = -1; else moving = true; }
            // keep ticking while dragging so "Remove" can appear while the pointer is held still
            return moving || Dragging;
        }

        double PanelBottom => (ActualHeight > 0 ? ActualHeight : _d.Height) - BottomMargin + _hide;
        double PanelTop => PanelBottom - _d.PanelHeight;

        Rect IconRect(DockItem it)
        {
            double bottom = PanelBottom - _d.PadBottom;
            double bounce = 0;
            if (it.BounceStart >= 0)
            {
                double t = Now - it.BounceStart;
                bounce = Math.Abs(Math.Sin(Math.PI * t / 0.6)) * _d.Base * 0.45;
            }
            return new Rect(it.X, bottom - it.Size - bounce, it.Size, it.Size);
        }

        DockItem HitTest(Point p)
        {
            if (p.Y < PanelTop - (_d.Mag - _d.Base) - 8 || p.Y > PanelBottom + 2) return null;
            foreach (var it in _d._items)
            {
                if (double.IsNaN(it.X) || (Dragging && it == _dragItem)) continue;
                double half = _d.Gap / 2;
                if (p.X >= it.X - half && p.X < it.X + it.Size + half)
                {
                    if (p.Y < PanelBottom - _d.PadBottom - it.Size - 6 && p.Y < PanelTop) return null;
                    return it;
                }
            }
            return null;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            _mouse = e.GetPosition(this);
            if (_pressed != null && e.LeftButton == MouseButtonState.Pressed && !Dragging)
            {
                if ((_mouse - _pressPoint).Length > 5 && (_pressed.Kind is "app" or "settings"))
                {
                    Dragging = true;
                    _dragItem = _pressed;
                    _outsideSince = DateTime.MaxValue;
                }
            }
            if (Dragging)
            {
                _dragX = _mouse.X; _dragY = _mouse.Y;
                if (FarOutside) { if (_outsideSince == DateTime.MaxValue) _outsideSince = DateTime.Now; }
                else _outsideSince = DateTime.MaxValue;
                Kick();
                return;
            }
            bool inZone = _mouse.Y >= PanelTop - (IsHovering ? _d.Mag - _d.Base + 10 : 0) && _mouse.X >= _panel.Left - 2 && _mouse.X <= _panel.Right + 2;
            if (inZone != IsHovering) IsHovering = inZone;
            _hover = IsHovering ? HitTest(_mouse) : null;
            Kick();
        }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pressPoint = e.GetPosition(this);
            _pressed = HitTest(_pressPoint);
            if (_pressed != null) CaptureMouse();   // keep receiving moves when the drag leaves the Dock
            InvalidateVisual();
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (Dragging)
            {
                var it = _dragItem;
                bool remove = RemoveArmed;
                var layout = LayoutItems;
                int insertAt = _insertAt >= 0 ? _insertAt : InsertIndex(layout, _dragX);
                Dragging = false; _dragItem = null; _pressed = null;
                ReleaseMouseCapture();
                if (remove)
                {
                    _poofAt = new Point(_dragX, _dragY);
                    _poofStart = Now;
                    _d.Unpin(it);
                }
                else _d.MoveTo(it, layout, insertAt);
                _d.Rebuild();
                return;
            }
            ReleaseMouseCapture();
            var hit = HitTest(e.GetPosition(this));
            var pressed = _pressed;
            _pressed = null;
            InvalidateVisual();
            if (hit != null && hit == pressed) _d.Click(hit);
        }

        void CancelDrag()
        {
            Dragging = false; _dragItem = null; _pressed = null; _outsideSince = DateTime.MaxValue;
            Kick();
        }

        void OnRight(object sender, MouseButtonEventArgs e)
        {
            var hit = HitTest(e.GetPosition(this));
            e.Handled = true;
            if (hit == null) hit = _d._items.FirstOrDefault(i => i.IsSeparator);
            if (hit == null) return;
            _d.ShowMenu(hit, IconRect(hit));
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            _mouse = e.GetPosition(this);
            IsHovering = true;
            e.Effects = DragDropEffects.None;
            if (AppTargetsFrom(e.Data).Count > 0)
            {
                // apps / PWAs / shortcuts: open a gap to pin them
                _extInsert = true;
                _extX = _mouse.X;
                _dropTarget = null;
                e.Effects = DragDropEffects.Copy | DragDropEffects.Link;
            }
            else
            {
                _extInsert = false;
                var hit = HitTest(_mouse);
                _dropTarget = hit;
                if (e.Data.GetDataPresent(DataFormats.FileDrop) && hit != null)
                {
                    if (hit.Kind == "trash") e.Effects = DragDropEffects.Move;
                    else if (hit.Kind is "app" or "downloads" or "finder") e.Effects = DragDropEffects.Copy;
                }
            }
            e.Handled = true;
            Kick();
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            var hit = _dropTarget;
            _dropTarget = null;
            IsHovering = false;
            var apps = AppTargetsFrom(e.Data);
            if (_extInsert && apps.Count > 0)
            {
                _extInsert = false;
                int at = InsertIndex(_d._items, _extX);
                _d.PinTargets(apps, at);
                Kick();
                return;
            }
            _extInsert = false;
            Kick();
            if (hit == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            switch (hit.Kind)
            {
                case "trash": FileOps.MoveToTrash(files); break;
                case "downloads": FileOps.CopyOrMove(files, GetKnownFolder(FOLDERID_Downloads), move: true); break;
                case "finder": foreach (var f in files) ShellHost.RevealInFinder(f); break;
                case "app":
                    AppCatalog.OpenFilesWith(new AppEntryOrTarget(hit.Target, hit.ExePath ?? AppCatalog.FindByParsingName(hit.Target)?.TargetPath ?? hit.Running?.ExePath), files);
                    break;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var items = _d._items;
            if (items.Count == 0) return;
            if (double.IsNaN(items[0].X)) Step(1);
            bool dark = Theme.IsDark;
            double b = _d.Base;

            var visible = items.Where(i => !(Dragging && i == _dragItem)).ToList();
            double left = visible.Min(i => i.X) - _d.PadX;
            double right = visible.Max(i => i.X + i.Size) + _d.PadX;
            _panel = new Rect(left, PanelTop, right - left, _d.PanelHeight);
            double radius = Math.Round(_d.PanelHeight * 0.32);

            // hover catcher keeps mouse events flowing over the transparent magnification zone
            if (IsHovering || Dragging)
            {
                double top = PanelTop - (_d.Mag - b) - 12;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(left - 8, Math.Max(0, top), right - left + 16, PanelBottom - top + BottomMargin));
            }

            // shelf: blurred wallpaper + tint + hairlines (fake vibrancy)
            var shelf = new RectangleGeometry(_panel, radius, radius);
            var screenRect = new Rect(_d.Left + _panel.X, _d.Top + _panel.Y, _panel.Width, _panel.Height);
            if (Wallpaper.Blurred != null) dc.DrawGeometry(Wallpaper.BlurBrush(screenRect, ShellHost.ScreenDip), null, shelf);
            dc.DrawGeometry(new SolidColorBrush(dark ? Color.FromArgb(0x70, 0x26, 0x26, 0x28) : Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF)), null, shelf);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x66, 0, 0, 0) : Color.FromArgb(0x26, 0, 0, 0)), 0.8), shelf);
            var inner = new RectangleGeometry(new Rect(_panel.X + 0.8, _panel.Y + 0.8, _panel.Width - 1.6, _panel.Height - 1.6), radius - 0.8, radius - 0.8);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 0.8), inner);

            foreach (var it in visible)
            {
                if (it.IsSeparator)
                {
                    double cx = Math.Round(it.X + it.Size / 2) + 0.5;
                    var sepPen = new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0, 0, 0)), 1);
                    dc.DrawLine(sepPen, new Point(cx, PanelTop + _d.PanelHeight * 0.17), new Point(cx, PanelBottom - _d.PanelHeight * 0.17));
                    continue;
                }
                var r = IconRect(it);
                DrawIcon(dc, it, r, (it == _pressed && !Dragging) || it == _dropTarget);
                if (it.IsRunning && Settings.Current.DockShowIndicators)
                {
                    double cx = it.X + it.Size / 2;
                    var dot = new SolidColorBrush(dark ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0, 0, 0));
                    dc.DrawEllipse(dot, null, new Point(cx, PanelBottom - _d.PadBottom * 0.42), 2, 2);
                }
                if (!string.IsNullOrEmpty(it.Badge)) DrawBadge(dc, it.Badge, r);
            }

            if (Dragging && _dragItem != null)
            {
                var r = new Rect(_dragX - b / 2, _dragY - b / 2, b, b);
                bool armed = RemoveArmed;
                dc.PushOpacity(armed ? 0.55 : 0.95);
                DrawIcon(dc, _dragItem, r, false);
                dc.Pop();
                if (armed) DrawLabel(dc, "Remove", new Point(_dragX, r.Top - 8));
            }
            else if (_hover != null && !MenuOpen && !_hover.IsSeparator && IsHovering && !_extInsert)
            {
                var r = IconRect(_hover);
                DrawLabel(dc, _hover.Name ?? "", new Point(r.X + r.Width / 2, r.Top - 10));
            }

            if (_poofStart >= 0) DrawPoof(dc, (Now - _poofStart) / 0.45);
        }

        void DrawIcon(DrawingContext dc, DockItem it, Rect r, bool dim)
        {
            if (it.Icon == null) return;
            // Windows icons fill their canvas edge-to-edge; inset them to match the optical size of macOS icons
            var draw = it.IconIsVector ? r : new Rect(r.X + r.Width * 0.06, r.Y + r.Height * 0.06, r.Width * 0.88, r.Height * 0.88);
            dc.DrawImage(it.Icon, draw);
            if (dim)
            {
                dc.PushOpacityMask(new ImageBrush(it.Icon) { Viewport = draw, ViewportUnits = BrushMappingMode.Absolute });
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), null, draw);
                dc.Pop();
            }
        }

        /// <summary>macOS notification badge: red pill with white bold count at the icon's top-right.</summary>
        static void DrawBadge(DrawingContext dc, string text, Rect icon)
        {
            double h = Math.Max(15, icon.Height * 0.36);
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), h * 0.62, Brushes.White, 1.5);
            double w = Math.Max(h, ft.Width + h * 0.62);
            var rect = new Rect(icon.Right - w * 0.75, icon.Top - h * 0.18, w, h);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null, new Rect(rect.X, rect.Y + 0.8, rect.Width, rect.Height), h / 2, h / 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), null, rect, h / 2, h / 2);
            dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
        }

        /// <summary>The little cloud puff when an icon is dragged out of the Dock.</summary>
        void DrawPoof(DrawingContext dc, double t)
        {
            t = Math.Clamp(t, 0, 1);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(220 * (1 - t)), 0xF2, 0xF2, 0xF4));
            double spread = _d.Base * (0.25 + 0.5 * t);
            for (int i = 0; i < 7; i++)
            {
                double a = i / 7.0 * Math.PI * 2 + 0.4;
                double rr = _d.Base * (0.2 + 0.12 * (1 - t));
                dc.DrawEllipse(brush, null, new Point(_poofAt.X + Math.Cos(a) * spread, _poofAt.Y + Math.Sin(a) * spread * 0.7), rr, rr);
            }
            dc.DrawEllipse(brush, null, _poofAt, _d.Base * 0.28 * (1 - t * 0.5), _d.Base * 0.28 * (1 - t * 0.5));
        }

        void DrawLabel(DrawingContext dc, string text, Point bottomCenter)
        {
            bool dark = Theme.IsDark;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13,
                new SolidColorBrush(dark ? Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0, 0, 0)), 1.5);
            double w = ft.Width + 22, h = 24;
            var rect = new Rect(Math.Round(bottomCenter.X - w / 2), Math.Round(bottomCenter.Y - h), w, h);
            rect.X = Math.Clamp(rect.X, 4, Math.Max(4, ActualWidth - w - 4));
            var geo = new RectangleGeometry(rect, 6, 6);
            var screenRect = new Rect(_d.Left + rect.X, _d.Top + rect.Y, rect.Width, rect.Height);
            if (Wallpaper.Blurred != null) dc.DrawGeometry(Wallpaper.BlurBrush(screenRect, ShellHost.ScreenDip), null, geo);
            dc.DrawGeometry(new SolidColorBrush(dark ? Color.FromArgb(0xD0, 0x30, 0x30, 0x32) : Color.FromArgb(0xC8, 0xF2, 0xF2, 0xF2)), new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x55, 0, 0, 0) : Color.FromArgb(0x22, 0, 0, 0)), 0.6), geo);
            dc.DrawText(ft, new Point(rect.X + 11, rect.Y + (h - ft.Height) / 2));
        }
    }

    // ================================================================== Downloads stack

    /// <summary>Grid-style stack popover showing the newest items of a folder.</summary>
    public static class StackPopup
    {
        static Window _win;
        public static bool IsOpen => _win != null;

        public static void Show(DockWindow dock, DockItem item, string folder)
        {
            if (_win != null) { _win.Close(); return; }
            if (folder == null || !Directory.Exists(folder)) return;
            var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(f => f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime).Take(16).ToList();

            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                FontFamily = Theme.Font, FontSize = 12,
            };
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
            var outer = new Border
            {
                Margin = new Thickness(20, 20, 20, 24), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 10),
                BorderThickness = new Thickness(0.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.3 },
            };
            outer.SetResourceReference(Border.BackgroundProperty, "PopoverBackgroundBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            var stack = new StackPanel();
            var header = new TextBlock { Text = Path.GetFileName(folder.TrimEnd('\\')), FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            stack.Children.Add(header);
            var grid = new WrapPanel { Width = 4 * 92 };
            foreach (var f in entries)
            {
                var cell = new StackPanel { Width = 92, Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var img = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 0, 4) };
                if (f is DirectoryInfo) img.Source = MacIcons.Folder(MacIcons.FolderGlyphFor(f.FullName));
                else ShellIcons.Load(f.FullName, 128, ShellIcons.IsThumbnailType(f.FullName), b => img.Source = b ?? MacIcons.GenericDocument);
                var name = new TextBlock { Text = f.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 86, TextWrapping = TextWrapping.Wrap, MaxHeight = 32 };
                name.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
                cell.Children.Add(img); cell.Children.Add(name);
                string path = f.FullName;
                cell.MouseLeftButtonUp += (_, _) => { w.Close(); AppCatalog.OpenFile(path); };
                cell.MouseMove += (_, e) =>
                {
                    if (e.LeftButton == MouseButtonState.Pressed)
                        DragDrop.DoDragDrop(cell, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
                };
                grid.Children.Add(cell);
            }
            if (entries.Count == 0) grid.Children.Add(new TextBlock { Text = "No Items", Margin = new Thickness(0, 20, 0, 20), Opacity = 0.5, Width = 4 * 92, TextAlignment = TextAlignment.Center });
            stack.Children.Add(grid);
            var open = new Button { Content = "Open in Finder", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            open.Click += (_, _) => { w.Close(); ShellHost.OpenFinder(folder); };
            stack.Children.Add(open);
            outer.Child = stack;
            w.Content = outer;
            bool closing = false;
            w.Closing += (_, _) => closing = true;
            w.Deactivated += (_, _) => { if (!closing) w.Close(); };
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape && !closing) w.Close(); };
            w.Closed += (_, _) => _win = null;
            _win = w;
            w.Loaded += (_, _) =>
            {
                double cx = dock.Left + item.X + item.Size / 2;
                w.Left = Math.Max(0, cx - w.ActualWidth / 2);
                w.Top = dock.Top + dock.Height - BottomMargin - dock.PanelHeight - w.ActualHeight + 12;
            };
            w.Show();
            w.Activate();
        }
    }
}
