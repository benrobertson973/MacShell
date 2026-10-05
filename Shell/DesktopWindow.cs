using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;
using Path = System.IO.Path;

namespace MacShell.Shell;

/// <summary>
/// The desktop: wallpaper plus desktop icons (primary display), pinned to the bottom of the z-order.
/// Icons are placed freely (no snapping) and remember where you put them; dragging an icon off the
/// exposed desktop turns into a real file drag that other apps, the Dock and Finder can accept.
/// </summary>
public class DesktopWindow : Window
{
    public bool IsPrimary { get; }
    public IntPtr Handle { get; private set; }
    readonly RECT _bounds;
    readonly Grid _root = new();
    readonly Image _wall = new() { Stretch = Stretch.UniformToFill };
    readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    readonly Rectangle _marquee = new() { Visibility = Visibility.Collapsed, Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };
    readonly List<FileItem> _items = new();                                   // what is displayed (plain items or stacks)
    readonly List<FileItem> _files = new();                                   // everything on the desktop
    readonly Dictionary<FileItem, ContentPresenter> _cells = new();
    readonly Dictionary<string, ContentPresenter> _presenters = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<FileItem, Point> _pos = new();
    readonly Dictionary<string, DateTime> _pendingPositions = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> _watchers = new();
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    string _layoutSig;

    // interaction state
    FileItem _anchor, _press, _deferSingle, _renaming, _dropTarget;
    bool _dragged, _marqueeOn;
    Point _pressPt, _marqueeStart;
    HashSet<FileItem> _marqueeBase;
    Point? _contextPoint;

    // live move (icons follow the pointer across the exposed desktop)
    bool _moving;
    List<FileItem> _moveItems;
    Dictionary<FileItem, Point> _moveStart;
    Point _moveOrigin;

    // an OLE drag that started on the desktop (so dropping back on the desktop repositions)
    bool _oleFromDesktop;
    List<FileItem> _oleItems;
    FileItem _olePress;
    Vector _oleGrab;

    double _cellW, _cellH, _top;
    int _rows = 1;

    static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public DesktopWindow(RECT bounds, bool primary)
    {
        _bounds = bounds;
        IsPrimary = primary;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Title = "Desktop";
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Background = Brushes.Black;
        RenderOptions.SetBitmapScalingMode(_wall, BitmapScalingMode.HighQuality);
        _root.Children.Add(_wall);
        if (primary)
        {
            _root.Children.Add(_canvas);
            _canvas.Children.Add(_marquee);
            Panel.SetZIndex(_marquee, 1000);
            AllowDrop = true;
            WireInput();
        }
        Content = _root;
        Left = bounds.Left / ShellHost.Scale; Top = bounds.Top / ShellHost.Scale;
        Width = bounds.Width / ShellHost.Scale; Height = bounds.Height / ShellHost.Scale;
        ApplyWallpaper();
        Wallpaper.Changed += ApplyWallpaper;
        Settings.Changed += OnSettingsChanged;
        _refresh.Tick += (_, _) => { _refresh.Stop(); if (!_moving && !_oleFromDesktop) LoadItems(true); else _refresh.Start(); };

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            AddExStyle(Handle, WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(Handle).AddHook(WndProc);
            SetWindowPos(Handle, HWND_BOTTOM, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE);
        };
        Loaded += (_, _) =>
        {
            if (!IsPrimary) return;
            _layoutSig = LayoutSignature();
            SetupWatchers();
            LoadItems(false);
        };
        Closed += (_, _) =>
        {
            foreach (var w in _watchers) w.Dispose();
            Wallpaper.Changed -= ApplyWallpaper;
            Settings.Changed -= OnSettingsChanged;
        };
        Activated += (_, _) => FinderViewState.SetIsActive(_canvas, true);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            // stay glued to the bottom of the z-order
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.hwndInsertAfter = HWND_BOTTOM;
            pos.flags &= ~SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    void ApplyWallpaper() => _wall.Source = Wallpaper.Image;

    static string LayoutSignature()
    {
        var s = Settings.Current;
        return $"{s.DesktopIconSize}|{s.DesktopUseStacks}|{s.DockAutoHide}|{s.DockIconSize}";
    }

    bool _lastHidden = Settings.Current.ShowHiddenFiles, _lastExt = Settings.Current.FinderShowExtensions;

    /// <summary>Only re-lay out for settings that affect the desktop (settings change often).</summary>
    void OnSettingsChanged()
    {
        if (!IsPrimary || !IsLoaded) return;
        var s = Settings.Current;
        if (s.ShowHiddenFiles != _lastHidden || s.FinderShowExtensions != _lastExt)
        {
            _lastHidden = s.ShowHiddenFiles; _lastExt = s.FinderShowExtensions;
            LoadItems(true);
            return;
        }
        string sig = LayoutSignature();
        if (sig == _layoutSig) return;
        _layoutSig = sig;
        BuildDisplay();
        Layout();
    }

    // ================================================================== items

    void SetupWatchers()
    {
        foreach (var dir in new[] { UserDesktop, PublicDesktop })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refresh.Stop(); _refresh.Start(); });
                w.Created += h; w.Deleted += h;
                w.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refresh.Stop(); _refresh.Start(); });
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { }
        }
    }

    void LoadItems(bool preserve)
    {
        var keep = preserve ? _items.Where(i => i.IsSelected).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        var list = new List<FileItem>();
        bool hidden = Settings.Current.ShowHiddenFiles;
        foreach (var dir in new[] { UserDesktop, PublicDesktop }.Distinct())
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var fi in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    var it = FileItem.FromInfo(fi);
                    if (!hidden && (it.IsHidden || fi.Name.StartsWith("."))) continue;
                    if (fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(it);
                }
            }
            catch { }
        }
        list.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
        _files.Clear();
        _files.AddRange(list);
        foreach (var it in _files) { it.IsSelected = keep.Contains(it.FullPath); it.RequestIcon((int)Settings.Current.DesktopIconSize); }
        PrunePositions();
        BuildDisplay();
        if (_pendingSelect != null)
        {
            var t = _items.FirstOrDefault(i => string.Equals(i.FullPath, _pendingSelect, StringComparison.OrdinalIgnoreCase));
            if (t != null) { foreach (var i in _items) i.IsSelected = i == t; _anchor = t; }
            _pendingSelect = null;
        }
        Layout();
        if (_renameAfter != null)
        {
            var t = _items.FirstOrDefault(i => string.Equals(i.FullPath, _renameAfter, StringComparison.OrdinalIgnoreCase));
            _renameAfter = null;
            if (t != null) Dispatcher.BeginInvoke(() => BeginRename(t), DispatcherPriority.Loaded);
        }
    }
    string _pendingSelect, _renameAfter;

    /// <summary>Forget positions of desktop items that no longer exist (moved, trashed, renamed elsewhere).</summary>
    void PrunePositions()
    {
        var saved = Settings.Current.DesktopPositions;
        var existing = new HashSet<string>(_files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var k in _pendingPositions.Where(p => (DateTime.Now - p.Value).TotalSeconds > 90).Select(p => p.Key).ToList()) _pendingPositions.Remove(k);
        bool changed = false;
        foreach (var key in saved.Keys.ToList())
        {
            if (existing.Contains(key) || _pendingPositions.ContainsKey(key)) continue;
            string dir = Path.GetDirectoryName(key);
            if (string.Equals(dir, UserDesktop, StringComparison.OrdinalIgnoreCase) || string.Equals(dir, PublicDesktop, StringComparison.OrdinalIgnoreCase))
            {
                saved.Remove(key);
                changed = true;
            }
        }
        if (changed) Settings.Save(false);
    }

    // ------------------------------------------------------------------ stacks

    readonly Dictionary<string, FileItem> _stacks = new();

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".avif", ".svg", ".ico", ".psd" };
    static readonly HashSet<string> MovieExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".wmv", ".webm", ".flv", ".mts" };
    static readonly HashSet<string> MusicExt = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".aiff" };
    static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".txt", ".rtf", ".md", ".odt", ".pages", ".log" };
    static readonly HashSet<string> SheetExt = new(StringComparer.OrdinalIgnoreCase) { ".xls", ".xlsx", ".csv", ".ods", ".numbers" };
    static readonly HashSet<string> SlideExt = new(StringComparer.OrdinalIgnoreCase) { ".ppt", ".pptx", ".odp", ".key" };
    static readonly HashSet<string> WebExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".url", ".webloc" };
    static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".dmg", ".apk", ".msi" };

    static string StackKind(FileItem f)
    {
        if (f.IsFolder) return null;
        string e = f.Extension;
        if (ImageExt.Contains(e)) return f.Name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase) ? "Screenshots" : "Images";
        if (MovieExt.Contains(e)) return f.Name.StartsWith("Screen Recording", StringComparison.OrdinalIgnoreCase) ? "Screen Recordings" : "Movies";
        if (MusicExt.Contains(e)) return "Music";
        if (e.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "PDF Documents";
        if (DocExt.Contains(e)) return "Documents";
        if (SheetExt.Contains(e)) return "Spreadsheets";
        if (SlideExt.Contains(e)) return "Presentations";
        if (WebExt.Contains(e)) return "Web Pages";
        if (ArchiveExt.Contains(e)) return "Archives";
        if (e is ".lnk" or ".exe" or ".appref-ms") return "Applications";
        return "Other";
    }

    /// <summary>Builds the visible sequence: plain items, or macOS-style Stacks grouped by kind.</summary>
    void BuildDisplay()
    {
        _items.Clear();
        if (!Settings.Current.DesktopUseStacks)
        {
            _items.AddRange(_files);
            return;
        }
        var groups = _files.Where(f => StackKind(f) != null).GroupBy(StackKind).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var singles = new List<FileItem>();
        foreach (var g in groups)
        {
            var members = g.OrderByDescending(f => f.Modified).ToList();
            if (members.Count < 2) { singles.AddRange(members); continue; }
            if (!_stacks.TryGetValue(g.Key, out var stack))
                _stacks[g.Key] = stack = new FileItem { Name = g.Key, IsStack = true, FullPath = "::stack:" + g.Key };
            stack.StackItems = members;
            ComposeStackIcon(stack);
            _items.Add(stack);
            if (stack.StackExpanded) _items.AddRange(members);
        }
        _items.AddRange(singles.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));
        _items.AddRange(_files.Where(f => f.IsFolder));
    }

    /// <summary>Fanned pile of the stack's three newest items (redrawn as their thumbnails arrive).</summary>
    void ComposeStackIcon(FileItem stack)
    {
        var top = stack.StackItems.Take(3).Reverse().ToList();
        foreach (var t in top)
        {
            t.PropertyChanged -= StackChildChanged;
            t.PropertyChanged += StackChildChanged;
        }
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        double[] angles = { -7, 6, 0 };
        for (int i = 0; i < top.Count; i++)
        {
            var src = top[i].Icon;
            if (src == null) continue;
            double a = angles[3 - top.Count + i];
            var g = new DrawingGroup { Transform = new RotateTransform(a, 50, 55) };
            double w = src.Width, h = src.Height;
            double k = Math.Min(78 / Math.Max(1, w), 78 / Math.Max(1, h));
            double dw = w * k, dh = h * k;
            var rect = new Rect(50 - dw / 2, 55 - dh / 2, dw, dh);
            if (ShellIcons.IsThumbnailType(top[i].FullPath ?? ""))
            {
                g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new RectangleGeometry(new Rect(rect.X - 2, rect.Y - 1, rect.Width + 4, rect.Height + 5), 3, 3)));
                g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(rect.X - 2.5, rect.Y - 2.5, rect.Width + 5, rect.Height + 5), 2.5, 2.5)));
            }
            g.Children.Add(new ImageDrawing(src, rect));
            dg.Children.Add(g);
        }
        var img = new DrawingImage(dg);
        img.Freeze();
        stack.SetIcon(img);
    }

    void StackChildChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileItem.Icon)) return;
        foreach (var s in _stacks.Values.Where(s => s.StackItems?.Contains(sender as FileItem) == true)) ComposeStackIcon(s);
    }

    void ToggleStack(FileItem stack)
    {
        stack.StackExpanded = !stack.StackExpanded;
        BuildDisplay();
        Layout();
    }

    // ================================================================== layout

    bool FreeLayout => !Settings.Current.DesktopUseStacks;

    void MeasureGrid()
    {
        double icon = Math.Clamp(Settings.Current.DesktopIconSize, 32, 128);
        _cellW = Math.Max(icon + 40, 96);
        _cellH = icon + 50;
        Resources["DesktopIconSize"] = icon;
        Resources["DesktopCellWidth"] = _cellW;
        Resources["DesktopLabelWidth"] = _cellW - 6;
        _top = ShellHost.MenuBarHeight + 10;
        double bottomReserve = (ShellHost.Dock?.ReservedHeight ?? 70) + 10;
        _rows = Math.Max(1, (int)((Height - _top - bottomReserve) / _cellH));
    }

    Point GridSlot(int i)
    {
        int col = i / _rows, row = i % _rows;
        double x = Math.Max(4, Width - 14 - (col + 1) * _cellW);
        return new Point(x, _top + row * _cellH);
    }

    int GridColumns => Math.Max(1, (int)((Width - 18) / _cellW));
    Rect CellRect(Point p) => new(p, new Size(_cellW, _cellH));
    static Rect Shrink(Rect r) { r.Inflate(-12, -12); return r; }

    Point ClampPos(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, Width - _cellW)),
        Math.Clamp(p.Y, ShellHost.MenuBarHeight + 2, Math.Max(ShellHost.MenuBarHeight + 2, Height - _cellH)));

    void Place(FileItem it, Point p)
    {
        if (!_cells.TryGetValue(it, out var cp)) return;
        Canvas.SetLeft(cp, p.X);
        Canvas.SetTop(cp, p.Y);
    }

    /// <summary>
    /// Positions every icon: saved free-form positions win; anything new goes into the next free grid slot
    /// (top-right, column by column, like a Mac) and is then remembered too.
    /// </summary>
    void Layout()
    {
        MeasureGrid();
        var tpl = (DataTemplate)Application.Current.Resources["DesktopCell"];
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _cells.Clear();
        foreach (var it in _items)
        {
            string key = it.FullPath ?? it.Name;
            live.Add(key);
            if (!_presenters.TryGetValue(key, out var cp))
            {
                cp = new ContentPresenter { ContentTemplate = tpl };
                _presenters[key] = cp;
                _canvas.Children.Add(cp);
            }
            if (!ReferenceEquals(cp.Content, it)) cp.Content = it;
            Panel.SetZIndex(cp, 0);
            _cells[it] = cp;
        }
        foreach (var key in _presenters.Keys.ToList())
            if (!live.Contains(key)) { _canvas.Children.Remove(_presenters[key]); _presenters.Remove(key); }

        _pos.Clear();
        bool free = FreeLayout;
        var saved = Settings.Current.DesktopPositions;
        var occupied = new List<Rect>();
        var unplaced = new List<FileItem>();
        foreach (var it in _items)
        {
            if (free && !it.IsStack && saved.TryGetValue(it.FullPath, out var p) && p is { Length: 2 })
            {
                var pt = ClampPos(new Point(p[0], p[1]));
                _pos[it] = pt;
                occupied.Add(CellRect(pt));
            }
            else unplaced.Add(it);
        }
        int slot = 0;
        bool changed = false;
        foreach (var it in unplaced)
        {
            var pt = GridSlot(slot++);
            if (free)
                while (slot < 4000 && occupied.Any(o => o.IntersectsWith(Shrink(CellRect(pt))))) pt = GridSlot(slot++);
            _pos[it] = pt;
            occupied.Add(CellRect(pt));
            if (free && !it.IsStack) { saved[it.FullPath] = new[] { pt.X, pt.Y }; changed = true; }
        }
        if (changed) Settings.Save(false);
        foreach (var it in _items) Place(it, _pos[it]);
    }

    void SavePosition(FileItem it, Point p)
    {
        _pos[it] = p;
        Place(it, p);
        if (FreeLayout && !it.IsStack && it.FullPath != null) Settings.Current.DesktopPositions[it.FullPath] = new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) };
    }

    /// <summary>"Clean Up": snap every icon to the nearest free grid spot, keeping the rough arrangement.</summary>
    void CleanUp()
    {
        if (!FreeLayout) return;
        MeasureGrid();
        int total = _rows * GridColumns;
        var taken = new HashSet<int>();
        foreach (var it in _items.OrderByDescending(i => _pos.TryGetValue(i, out var p) ? p.X : 0).ThenBy(i => _pos.TryGetValue(i, out var p) ? p.Y : 0))
        {
            var cur = _pos.TryGetValue(it, out var cp) ? cp : GridSlot(0);
            int best = -1; double bestD = double.MaxValue;
            for (int s = 0; s < total; s++)
            {
                if (taken.Contains(s)) continue;
                var g = GridSlot(s);
                double d = (g - cur).LengthSquared;
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best < 0) best = taken.Count;
            taken.Add(best);
            SavePosition(it, GridSlot(best));
        }
        Settings.Save(false);
    }

    /// <summary>"Clean Up By": re-flow every icon into the grid in the given order.</summary>
    void CleanUpBy(string key)
    {
        if (!FreeLayout) return;
        MeasureGrid();
        IEnumerable<FileItem> order = key switch
        {
            "kind" => _items.OrderBy(i => i.Kind, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => _items.OrderByDescending(i => i.Modified),
            "size" => _items.OrderByDescending(i => i.Size),
            _ => _items.OrderBy(i => i.Name, Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b))),
        };
        int slot = 0;
        foreach (var it in order.ToList()) SavePosition(it, GridSlot(slot++));
        Settings.Save(false);
    }

    void PreassignPosition(string path, Point p)
    {
        MeasureGrid();
        var pt = ClampPos(p);
        Settings.Current.DesktopPositions[path] = new[] { pt.X, pt.Y };
        _pendingPositions[path] = DateTime.Now;
    }

    // ================================================================== input

    FileItem ItemAt(object src)
    {
        var d = src as DependencyObject;
        while (d != null && d != _canvas)
        {
            if (d is FrameworkElement fe && fe.Tag as string == "hit" && fe.DataContext is FileItem fi) return fi;
            if (d is TextBox tb && tb.Tag as string == "rename") return null;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Folder icon under a canvas point (hit-tested on the icon/label, ignoring the icons being dragged).</summary>
    FileItem FolderAt(Point p, ICollection<FileItem> exclude)
    {
        foreach (var (it, cp) in _cells)
        {
            if (!it.IsFolder || it.IsStack || (exclude != null && exclude.Contains(it))) continue;
            foreach (var fe in FinderView.TaggedElements(cp))
            {
                try
                {
                    var b = fe.TransformToAncestor(_canvas).TransformBounds(new Rect(fe.RenderSize));
                    if (b.Contains(p)) return it;
                }
                catch { }
            }
        }
        return null;
    }

    void SetDropHighlight(FileItem folder)
    {
        if (_dropTarget == folder) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = folder;
        if (folder != null) folder.IsDropTarget = true;
    }

    static bool InRenameBox(object src)
    {
        var d = src as DependencyObject;
        while (d != null) { if (d is TextBox tb && tb.Tag as string == "rename") return true; d = d is Visual ? VisualTreeHelper.GetParent(d) : null; }
        return false;
    }

    static List<string> PathsOf(IEnumerable<FileItem> items) =>
        items.SelectMany(s => s.IsStack ? s.StackItems.Select(x => x.FullPath) : new[] { s.FullPath }).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static DragDropKeyStates CurrentKeys()
    {
        var k = DragDropKeyStates.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) k |= DragDropKeyStates.ControlKey;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) k |= DragDropKeyStates.AltKey;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) k |= DragDropKeyStates.ShiftKey;
        return k;
    }

    void WireInput()
    {
        _canvas.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (InRenameBox(e.OriginalSource)) return;
            _contextPoint = null;
            Activate();
            CommitRename();
            var it = ItemAt(e.OriginalSource);
            if (it != null)
            {
                if (e.ClickCount >= 2) { if (!it.IsStack) Open(it); e.Handled = true; return; }
                ItemDown(it, Keyboard.Modifiers);
                _press = it;
                _pressPt = e.GetPosition(_canvas);
                _dragged = false;
            }
            else
            {
                var mods = Keyboard.Modifiers;
                if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) { foreach (var i in _items) i.IsSelected = false; _marqueeBase = null; }
                else _marqueeBase = _items.Where(i => i.IsSelected).ToHashSet();
                _marqueeOn = true;
                _marqueeStart = e.GetPosition(_canvas);
                _canvas.CaptureMouse();
            }
            e.Handled = true;
        };
        _canvas.PreviewMouseMove += (_, e) =>
        {
            var pos = e.GetPosition(_canvas);
            if (_moving)
            {
                if (e.LeftButton != MouseButtonState.Pressed) { EndMove(pos); return; }
                UpdateMove(pos);
                return;
            }
            if (_press != null && e.LeftButton == MouseButtonState.Pressed && (pos - _pressPt).Length > 4)
            {
                BeginMove();
                return;
            }
            if (_marqueeOn)
            {
                var r = new Rect(_marqueeStart, pos);
                _marquee.Visibility = Visibility.Visible;
                Canvas.SetLeft(_marquee, r.X); Canvas.SetTop(_marquee, r.Y);
                _marquee.Width = r.Width; _marquee.Height = r.Height;
                foreach (var (it, cp) in _cells)
                {
                    bool hit = false;
                    foreach (var fe in FinderView.TaggedElements(cp))
                    {
                        var b = fe.TransformToAncestor(_canvas).TransformBounds(new Rect(fe.RenderSize));
                        if (b.IntersectsWith(r)) { hit = true; break; }
                    }
                    it.IsSelected = hit || (_marqueeBase?.Contains(it) ?? false);
                }
            }
        };
        _canvas.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_moving) { EndMove(e.GetPosition(_canvas)); e.Handled = true; return; }
            if (_press != null && _deferSingle == _press && Keyboard.Modifiers == ModifierKeys.None) SelectOnly(_press);
            if (_press != null && _press.IsStack && !_dragged && Keyboard.Modifiers == ModifierKeys.None) ToggleStack(_press);
            _press = null; _deferSingle = null; _dragged = false;
            if (_marqueeOn) { _marqueeOn = false; _marquee.Visibility = Visibility.Collapsed; _canvas.ReleaseMouseCapture(); }
        };
        _canvas.LostMouseCapture += (_, _) => { if (_moving) CancelMove(); };
        _canvas.PreviewMouseRightButtonDown += (_, e) =>
        {
            Activate();
            CommitRename();
            var it = ItemAt(e.OriginalSource);
            _contextPoint = it == null ? e.GetPosition(_canvas) : null;
            if (it != null && !it.IsSelected) SelectOnly(it);
            if (it == null) foreach (var i in _items) i.IsSelected = false;
            e.Handled = true;
        };
        _canvas.PreviewMouseRightButtonUp += (_, e) =>
        {
            var it = ItemAt(e.OriginalSource);
            (it != null ? ItemMenu() : BackgroundMenu()).IsOpen = true;
            e.Handled = true;
        };

        _canvas.AllowDrop = true;
        _canvas.DragOver += (_, e) =>
        {
            e.Effects = DragDropEffects.None;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var it = ItemAt(e.OriginalSource);
                bool own = _oleItems != null && it != null && _oleItems.Contains(it);
                var folder = it != null && it.IsFolder && !it.IsStack && !own && !files.Contains(it.FullPath, StringComparer.OrdinalIgnoreCase) ? it : null;
                SetDropHighlight(folder);
                if (folder != null) e.Effects = FileOps.ShouldMove(files, folder.FullPath, e.KeyStates) ? DragDropEffects.Move : DragDropEffects.Copy;
                else if (_oleFromDesktop || AllOnDesktop(files)) e.Effects = FreeLayout ? DragDropEffects.Move : DragDropEffects.None;
                else e.Effects = FileOps.ShouldMove(files, UserDesktop, e.KeyStates) ? DragDropEffects.Move : DragDropEffects.Copy;
            }
            e.Handled = true;
        };
        _canvas.DragLeave += (_, _) => SetDropHighlight(null);
        _canvas.Drop += (_, e) =>
        {
            var folder = _dropTarget;
            SetDropHighlight(null);
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            var dropPt = e.GetPosition(_canvas);
            var keys = e.KeyStates;
            if (folder != null)
            {
                string dest = folder.FullPath;
                Dispatcher.BeginInvoke(() => FileOps.CopyOrMove(files, dest, FileOps.ShouldMove(files, dest, keys)));
                return;
            }
            if (_oleFromDesktop && _oleItems != null)
            {
                RepositionAt(_oleItems, _olePress, dropPt, _oleGrab);
                e.Effects = DragDropEffects.Move;
                return;
            }
            if (AllOnDesktop(files))
            {
                var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                var items = _items.Where(i => i.FullPath != null && set.Contains(i.FullPath)).ToList();
                if (items.Count > 0) RepositionAt(items, items[0], dropPt, new Vector(_cellW / 2, 30));
                return;
            }
            // files from elsewhere land where they were dropped (a small grid if there are several)
            MeasureGrid();
            for (int i = 0; i < files.Length; i++)
            {
                string target = Path.Combine(UserDesktop, Path.GetFileName(files[i].TrimEnd('\\')));
                PreassignPosition(target, new Point(dropPt.X - _cellW / 2 + (i % 5) * _cellW * 0.9, dropPt.Y - 30 + (i / 5) * _cellH * 0.9));
            }
            Settings.Save(false);
            Dispatcher.BeginInvoke(() => FileOps.CopyOrMove(files, UserDesktop, FileOps.ShouldMove(files, UserDesktop, keys)));
        };

        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), true);
        AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox tb && tb.Tag as string == "rename") CommitRename();
        }), true);
    }

    static bool AllOnDesktop(string[] files) => files.All(f =>
        string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), UserDesktop, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), PublicDesktop, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ moving icons

    void BeginMove()
    {
        var press = _press;
        if (press == null) return;
        _dragged = true;
        if (!press.IsSelected) SelectOnly(press);
        var items = Selected;
        if (items.Count == 0) items = new List<FileItem> { press };
        if (!FreeLayout)
        {
            _press = null;
            StartOleDrag(items, press);
            return;
        }
        _moving = true;
        _moveItems = items;
        _moveStart = items.ToDictionary(i => i, i => _pos.TryGetValue(i, out var p) ? p : new Point());
        _moveOrigin = _pressPt;
        foreach (var it in items) if (_cells.TryGetValue(it, out var cp)) Panel.SetZIndex(cp, 500);
        _canvas.CaptureMouse();
    }

    void UpdateMove(Point pos)
    {
        var d = pos - _moveOrigin;
        foreach (var it in _moveItems) Place(it, _moveStart[it] + d);

        // Over an app window, the Dock, a Finder window …? Then it becomes a real file drag.
        GetCursorPos(out var sp);
        var under = GetAncestor(WindowFromPoint(sp), GA_ROOT);
        if (under != IntPtr.Zero && under != Handle)
        {
            var items = _moveItems;
            var press = _press ?? items[0];
            var grab = _pressPt - (_moveStart.TryGetValue(press, out var ps) ? ps : _pressPt);
            CancelMove();
            _press = null;
            StartOleDrag(items, press, grab);
            return;
        }
        SetDropHighlight(FolderAt(pos, _moveItems));
    }

    void EndMove(Point pos)
    {
        if (!_moving) return;
        var d = pos - _moveOrigin;
        var items = _moveItems;
        var folder = _dropTarget;
        SetDropHighlight(null);
        _moving = false;
        _canvas.ReleaseMouseCapture();
        foreach (var it in items) if (_cells.TryGetValue(it, out var cp)) Panel.SetZIndex(cp, 0);
        if (folder != null)
        {
            foreach (var it in items) Place(it, _moveStart[it]);
            var paths = PathsOf(items);
            string dest = folder.FullPath;
            var keys = CurrentKeys();
            Dispatcher.BeginInvoke(() => FileOps.CopyOrMove(paths, dest, FileOps.ShouldMove(paths, dest, keys)));
        }
        else
        {
            foreach (var it in items) SavePosition(it, ClampPos(_moveStart[it] + d));
            Settings.Save(false);
        }
        _press = null;
        _deferSingle = null;
    }

    void CancelMove()
    {
        if (!_moving) return;
        _moving = false;
        foreach (var it in _moveItems)
        {
            Place(it, _moveStart[it]);
            if (_cells.TryGetValue(it, out var cp)) Panel.SetZIndex(cp, 0);
        }
        SetDropHighlight(null);
        _canvas.ReleaseMouseCapture();
    }

    void StartOleDrag(List<FileItem> items, FileItem press, Vector? grab = null)
    {
        var paths = PathsOf(items).ToArray();
        if (paths.Length == 0) return;
        _oleFromDesktop = true;
        _oleItems = items;
        _olePress = press;
        _oleGrab = grab ?? (_pressPt - (_pos.TryGetValue(press, out var pp) ? pp : _pressPt));
        var icons = items.Select(i => i.Icon).Where(i => i != null).ToList();
        try
        {
            DragGhost.Run(_canvas, new DataObject(DataFormats.FileDrop, paths),
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, icons, Settings.Current.DesktopIconSize);
        }
        finally
        {
            _oleFromDesktop = false;
            _oleItems = null;
            _olePress = null;
            SetDropHighlight(null);
        }
    }

    /// <summary>Drops a group of desktop icons so the grabbed one lands under the pointer, keeping their spacing.</summary>
    void RepositionAt(List<FileItem> items, FileItem anchor, Point dropPt, Vector grab)
    {
        if (!FreeLayout || items.Count == 0) return;
        anchor ??= items[0];
        var anchorPos = _pos.TryGetValue(anchor, out var ap) ? ap : dropPt;
        var d = (dropPt - grab) - anchorPos;
        foreach (var it in items)
            if (_pos.TryGetValue(it, out var p)) SavePosition(it, ClampPos(p + d));
        Settings.Save(false);
    }

    void ItemDown(FileItem it, ModifierKeys mods)
    {
        _deferSingle = null;
        if (mods.HasFlag(ModifierKeys.Control)) { it.IsSelected = !it.IsSelected; _anchor = it; }
        else if (mods.HasFlag(ModifierKeys.Shift) && _anchor != null)
        {
            // Shift-click extends: add everything inside the rectangle spanned by the anchor and this icon
            if (_pos.TryGetValue(_anchor, out var a) && _pos.TryGetValue(it, out var b))
            {
                var span = new Rect(a, b);
                span.Inflate(_cellW / 2, _cellH / 2);
                foreach (var i in _items) i.IsSelected = _pos.TryGetValue(i, out var p) && span.Contains(new Point(p.X + _cellW / 2, p.Y + _cellH / 2));
            }
            it.IsSelected = true;
        }
        else if (!it.IsSelected) SelectOnly(it);
        else _deferSingle = it;
    }

    void SelectOnly(FileItem it)
    {
        foreach (var i in _items) i.IsSelected = i == it;
        _anchor = it;
        QuickLookWindow.Current?.ShowItem(it);
    }

    List<FileItem> Selected => _items.Where(i => i.IsSelected).ToList();
    List<string> SelectedPaths => PathsOf(Selected);

    void Open(FileItem it)
    {
        if (it.IsStack) { ToggleStack(it); return; }
        if (it.IsFolder) { ShellHost.OpenFinder(it.FullPath); return; }
        if (it.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string t = FileOps.ResolveAlias(it.FullPath);
            if (t != null && Directory.Exists(t)) { ShellHost.OpenFinder(t); return; }
        }
        AppCatalog.OpenFile(it.FullPath);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox tb && tb.Tag as string == "rename")
        {
            if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
            else if (e.Key == Key.Escape) { if (_renaming != null) { _renaming.IsRenaming = false; _renaming = null; } Activate(); e.Handled = true; }
            return;
        }
        if (_moving && e.Key == Key.Escape) { CancelMove(); _press = null; e.Handled = true; return; }
        var mods = Keyboard.Modifiers;
        string cmd = null;
        if (mods == ModifierKeys.Control)
            cmd = e.Key switch { Key.A => "selectAll", Key.C => "copy", Key.X => "cut", Key.V => "paste", Key.D => "duplicate", Key.I => "getInfo", Key.O => "open", Key.Down => "open", Key.Back => "trash", Key.Y => "quicklook", Key.N => "newWindow", _ => null };
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
            cmd = e.Key switch { Key.N => "newFolder", _ => null };
        else if (mods == ModifierKeys.None)
            cmd = e.Key switch { Key.Enter => "rename", Key.F2 => "rename", Key.Delete => "trash", Key.Space => "quicklook", _ => null };
        if (cmd == "newWindow") { ShellHost.OpenFinder(null); e.Handled = true; return; }
        if (cmd != null) { Execute(cmd); e.Handled = true; return; }
        if (mods == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            MoveSelection(e.Key);
            e.Handled = true;
        }
    }

    /// <summary>Arrow keys pick the nearest icon in that direction (icons are free-form, not a grid).</summary>
    void MoveSelection(Key key)
    {
        if (_items.Count == 0) return;
        var cur = _anchor != null && _anchor.IsSelected ? _anchor : Selected.FirstOrDefault();
        if (cur == null || !_pos.TryGetValue(cur, out var cp))
        {
            var first = _items.OrderBy(i => _pos.TryGetValue(i, out var p) ? p.Y : 0).ThenByDescending(i => _pos.TryGetValue(i, out var p) ? p.X : 0).First();
            SelectOnly(first);
            return;
        }
        var c = new Point(cp.X + _cellW / 2, cp.Y + _cellH / 2);
        FileItem best = null; double bestScore = double.MaxValue;
        foreach (var it in _items)
        {
            if (it == cur || !_pos.TryGetValue(it, out var p)) continue;
            var o = new Point(p.X + _cellW / 2, p.Y + _cellH / 2);
            double dx = o.X - c.X, dy = o.Y - c.Y;
            double along = key switch { Key.Left => -dx, Key.Right => dx, Key.Up => -dy, _ => dy };
            double across = key is Key.Left or Key.Right ? Math.Abs(dy) : Math.Abs(dx);
            if (along < 8) continue;
            double score = along + across * 2.5;
            if (score < bestScore) { bestScore = score; best = it; }
        }
        if (best != null) SelectOnly(best);
    }

    // ================================================================== commands

    static readonly HashSet<string> Commands = new() { "newFolder", "getInfo", "rename", "duplicate", "trash", "copy", "cut", "paste", "selectAll", "open", "alias", "compress", "quicklook", "copyPath" };
    public bool HandlesCommand(string cmd) => IsPrimary && Commands.Contains(cmd);

    public void Execute(string cmd)
    {
        var sel = Selected.Where(s => !s.IsStack).ToList();
        var paths = SelectedPaths;
        switch (cmd)
        {
            case "newFolder":
                {
                    var at = _contextPoint;
                    _contextPoint = null;
                    string p = FileOps.NewFolder(UserDesktop);
                    if (p != null)
                    {
                        if (at is Point pt) { MeasureGrid(); PreassignPosition(p, new Point(pt.X - _cellW / 2, pt.Y - 24)); Settings.Save(false); }
                        _pendingSelect = p;
                        _renameAfter = p;
                        LoadItems(false);
                    }
                    break;
                }
            case "getInfo": foreach (var s in sel.Take(10)) GetInfoWindow.ShowFor(s); break;
            case "rename": if (sel.Count == 1 && Selected.Count == 1) BeginRename(sel[0]); break;
            case "duplicate": FileOps.Duplicate(paths); break;
            case "trash": FileOps.MoveToTrash(paths); break;
            case "copy": FileOps.CopyToClipboard(paths, false); break;
            case "cut": FileOps.CopyToClipboard(paths, true); foreach (var s in sel) s.IsCut = true; break;
            case "paste": FileOps.Paste(UserDesktop); break;
            case "selectAll": foreach (var i in _items) i.IsSelected = true; break;
            case "open": foreach (var s in Selected) Open(s); break;
            case "alias": FileOps.MakeAlias(paths); break;
            case "compress": FileOps.Compress(paths); break;
            case "quicklook": QuickLookWindow.Toggle(null, sel.FirstOrDefault()); break;
            case "copyPath": try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { } break;
        }
    }

    void BeginRename(FileItem it)
    {
        CommitRename();
        _renaming = it;
        it.IsRenaming = true;
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_cells.TryGetValue(it, out var cp)) return;
            Panel.SetZIndex(cp, 400);
            var tb = FindRename(cp);
            if (tb == null) return;
            tb.Text = it.Name;
            tb.Focus();
            int dot = it.IsFolder ? -1 : it.Name.LastIndexOf('.');
            if (dot > 0) tb.Select(0, dot); else tb.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    static TextBox FindRename(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is TextBox tb && tb.Tag as string == "rename") return tb;
            var r = FindRename(c);
            if (r != null) return r;
        }
        return null;
    }

    void CommitRename()
    {
        var it = _renaming;
        if (it == null || !it.IsRenaming) return;
        _renaming = null;
        string text = _cells.TryGetValue(it, out var cp) ? FindRename(cp)?.Text : null;
        if (cp != null) Panel.SetZIndex(cp, 0);
        it.IsRenaming = false;
        if (!string.IsNullOrWhiteSpace(text) && text != it.Name)
        {
            string old = it.FullPath;
            string r = FileOps.Rename(old, text);
            if (r != null)
            {
                // the icon keeps its spot under its new name
                if (Settings.Current.DesktopPositions.Remove(old, out var pos)) Settings.Current.DesktopPositions[r] = pos;
                _pendingPositions[r] = DateTime.Now;
                Settings.Save(false);
                _pendingSelect = r;
            }
        }
    }

    ContextMenu ItemMenu()
    {
        if (Selected.Count == 1 && Selected[0].IsStack)
        {
            var st = Selected[0];
            return Mb.Context(
                Mb.Item(st.StackExpanded ? "Collapse Stack" : "Expand Stack", () => ToggleStack(st)),
                Mb.Sep(),
                Mb.Item($"Move {st.StackItems.Count} Items to Trash", () => Execute("trash")),
                Mb.Item($"Copy {st.StackItems.Count} Items", () => Execute("copy")),
                Mb.Sep(),
                Mb.Item("Use Stacks", () => { Settings.Current.DesktopUseStacks = false; Settings.Save(); }, isChecked: true));
        }
        var sel = Selected;
        var first = sel.FirstOrDefault();
        string label = sel.Count == 1 ? $"“{first.DisplayName}”" : $"{sel.Count} Items";
        return Mb.Context(
            Mb.Item("Open", () => Execute("open")),
            Mb.Sep(),
            Mb.Item("Move to Trash", () => Execute("trash")),
            Mb.Sep(),
            Mb.Item("Get Info", () => Execute("getInfo")),
            Mb.Item("Rename", () => Execute("rename"), enabled: sel.Count == 1),
            Mb.Item($"Compress {label}", () => Execute("compress")),
            Mb.Item("Duplicate", () => Execute("duplicate")),
            Mb.Item("Make Alias", () => Execute("alias")),
            Mb.Item($"Quick Look {label}", () => Execute("quicklook")),
            Mb.Sep(),
            Mb.Item($"Copy {label}", () => Execute("copy")),
            Mb.Sep(),
            Mb.TagRow(Theme.TagColors.Select(t => t.id).Where(id => sel.All(s => s.Tags.Contains(id))).ToList(), tag =>
            {
                FileOps.SetTag(sel.Select(s => s.FullPath), tag);
                foreach (var s in sel) s.RefreshTags();
            }),
            Mb.Sep(),
            Mb.Item("Show in Enclosing Folder", () => ShellHost.RevealInFinder(first.FullPath)));
    }

    ContextMenu BackgroundMenu()
    {
        bool canPaste = false;
        try { canPaste = Clipboard.ContainsFileDropList(); } catch { }
        bool free = FreeLayout;
        return Mb.Context(
            Mb.Item("New Folder", () => Execute("newFolder")),
            Mb.Sep(),
            Mb.Item("Get Info", () => GetInfoWindow.ShowFor(FileItem.FromInfo(new DirectoryInfo(UserDesktop)))),
            Mb.Sep(),
            Mb.Item("Paste Item", () => Execute("paste"), enabled: canPaste),
            Mb.Sep(),
            Mb.Item("Change Wallpaper…", () => SettingsWindow.ShowPane("wallpaper")),
            Mb.Item("Edit Widgets…", () => NotificationCenterWindow.Toggle(null)),
            Mb.Sep(),
            Mb.Item("Use Stacks", () => { Settings.Current.DesktopUseStacks = !Settings.Current.DesktopUseStacks; Settings.Save(); }, isChecked: Settings.Current.DesktopUseStacks),
            Mb.Item("Clean Up", CleanUp, enabled: free),
            Mb.Sub("Clean Up By",
                Mb.Item("Name", () => CleanUpBy("name"), enabled: free),
                Mb.Item("Kind", () => CleanUpBy("kind"), enabled: free),
                Mb.Item("Date Modified", () => CleanUpBy("date"), enabled: free),
                Mb.Item("Size", () => CleanUpBy("size"), enabled: free)),
            Mb.Sub("Icon Size",
                Mb.Item("Small", () => { Settings.Current.DesktopIconSize = 48; Settings.Save(); }, isChecked: Settings.Current.DesktopIconSize == 48),
                Mb.Item("Medium", () => { Settings.Current.DesktopIconSize = 64; Settings.Save(); }, isChecked: Settings.Current.DesktopIconSize == 64),
                Mb.Item("Large", () => { Settings.Current.DesktopIconSize = 88; Settings.Save(); }, isChecked: Settings.Current.DesktopIconSize == 88)),
            Mb.Sep(),
            Mb.Item("Show in Finder", () => ShellHost.OpenFinder(UserDesktop)));
    }
}
