using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Finder;

public class IconRow { public List<FileItem> Items { get; set; } }

public class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new Thickness((value is int d ? d : 0) * 16, 0, 0, 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class FinderViewState
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(FinderViewState),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject d, bool v) => d.SetValue(IsActiveProperty, v);
}

public interface IFinderHost
{
    void ItemMouseDown(FileItem item, ModifierKeys mods, FinderView view);
    void ItemMouseUp(FileItem item, ModifierKeys mods);
    void ItemDoubleClick(FileItem item);
    void ItemRightDown(FileItem item);
    void BackgroundMouseDown(ModifierKeys mods);
    void ItemContextMenu(FileItem item, FrameworkElement target);
    void BackgroundContextMenu(FrameworkElement target);
    void StartDrag(FileItem item, FrameworkElement source);
    void DragOverTarget(FileItem folder, DragEventArgs e);
    void DropOn(FileItem folder, DragEventArgs e);
    void DragLeft();
    void MarqueeSelect(HashSet<FileItem> hits, bool additive);
    void CommitRename(FileItem item, string newName);
    void CancelRename(FileItem item);
    void ToggleExpand(FileItem item);
    void SortBy(string key);
    void ViewFocused(FinderView view);
    void LoadChildren(string folder, Action<List<FileItem>> done);
    void ActiveColumnChanged(string folder);
    string SortKey { get; }
    bool SortAscending { get; }
}

/// <summary>Common plumbing for Finder's views: hit-testing, selection gestures, drag &amp; drop, marquee.</summary>
public abstract class FinderView
{
    protected readonly IFinderHost Host;
    public Grid Root { get; } = new() { Background = Brushes.Transparent, ClipToBounds = true };
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly Rectangle _marquee = new() { Visibility = Visibility.Collapsed, StrokeThickness = 1 };
    FileItem _press;
    Point _pressPt, _marqueeStart;
    bool _marqueeOn, _marqueeAdditive;
    FrameworkElement _marqueeSurface;

    protected FinderView(IFinderHost host)
    {
        Host = host;
        _marquee.SetResourceReference(Shape.FillProperty, "QuaternaryLabelBrush");
        _marquee.SetResourceReference(Shape.StrokeProperty, "TertiaryLabelBrush");
        _overlay.Children.Add(_marquee);
        Panel.SetZIndex(_overlay, 100);
        Root.Children.Add(_overlay);
        Root.GotKeyboardFocus += (_, _) => Host.ViewFocused(this);
    }

    public virtual int ItemsPerRow => 1;
    public virtual IReadOnlyList<FileItem> CurrentItems => Items;
    public IReadOnlyList<FileItem> Items { get; protected set; } = Array.Empty<FileItem>();
    public abstract void Show(IReadOnlyList<FileItem> items);
    public abstract void Reveal(FileItem item);
    /// <summary>New contents (another folder, a new search) start at the top; refreshes keep their scroll position.</summary>
    public void ScrollToTop() => FindScrollViewer(Root)?.ScrollToTop();
    public virtual void OnSelectionChanged() { }
    public virtual bool HandleKey(Key key, ModifierKeys mods) => false;
    public bool Active { set => FinderViewState.SetIsActive(Root, value); }
    public virtual void FocusView() { if (!Root.IsKeyboardFocusWithin) { Root.Focusable = true; Keyboard.Focus(Root); } }

    protected static DataTemplate Tpl(string key) => (DataTemplate)Application.Current.Resources[key];

    /// <summary>Builds a virtualising ItemsControl with an overlay-scrollbar ScrollViewer template.</summary>
    protected static ItemsControl MakeList(string itemTemplate, Orientation orientation = Orientation.Vertical, bool horizontalScroll = false)
    {
        string xaml = $@"
<ItemsControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              VirtualizingPanel.IsVirtualizing='True' VirtualizingPanel.VirtualizationMode='Recycling' VirtualizingPanel.ScrollUnit='Pixel'
              VirtualizingPanel.CacheLength='2,2' Focusable='False'>
  <ItemsControl.Template>
    <ControlTemplate TargetType='ItemsControl'>
      <ScrollViewer x:Name='sv' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CanContentScroll='True' Focusable='False'
                    HorizontalScrollBarVisibility='{(horizontalScroll ? "Auto" : "Disabled")}' VerticalScrollBarVisibility='{(horizontalScroll ? "Disabled" : "Auto")}'
                    Background='Transparent'>
        <ItemsPresenter/>
      </ScrollViewer>
    </ControlTemplate>
  </ItemsControl.Template>
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate><VirtualizingStackPanel Orientation='{orientation}'/></ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
</ItemsControl>";
        var ic = (ItemsControl)XamlReader.Parse(xaml);
        ic.ItemTemplate = Tpl(itemTemplate);
        return ic;
    }

    protected static ScrollViewer FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }

    protected static T FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindChild<T>(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }

    public static IEnumerable<FrameworkElement> TaggedElements(DependencyObject d)
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is FrameworkElement fe && fe.Tag is string s && (s == "hit" || s == "row") && fe.DataContext is FileItem && fe.IsVisible)
                yield return fe;
            foreach (var x in TaggedElements(c)) yield return x;
        }
    }

    /// <summary>Finds the rename TextBox for an item (after layout).</summary>
    public TextBox FindRenameBox(FileItem item)
    {
        return FindRename(Root, item);
        static TextBox FindRename(DependencyObject d, FileItem item)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is TextBox tb && tb.Tag as string == "rename" && tb.DataContext == item) return tb;
                var r = FindRename(c, item);
                if (r != null) return r;
            }
            return null;
        }
    }

    protected (FileItem item, string zone) ItemAt(object source, FrameworkElement stop)
    {
        var d = source as DependencyObject;
        string zone = null;
        while (d != null && d != stop)
        {
            if (d is ScrollBar) return (null, "scrollbar");
            if (d is FrameworkElement fe && fe.Tag is string tag)
            {
                if (tag == "disclosure" && fe.DataContext is FileItem di) return (di, "disclosure");
                if (tag == "rename") return (fe.DataContext as FileItem, "rename");
                if ((tag == "hit" || tag == "row") && fe.DataContext is FileItem fi) return (fi, zone ?? tag);
            }
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return (null, null);
    }

    /// <summary>Hooks up mouse selection, drag &amp; drop, marquee and rename handling on a surface.</summary>
    protected void Wire(FrameworkElement surface, bool marquee, Action<FrameworkElement> beforeDown = null)
    {
        surface.PreviewMouseLeftButtonDown += (s, e) =>
        {
            var (item, zone) = ItemAt(e.OriginalSource, surface);
            if (zone is "scrollbar" or "rename") return;
            beforeDown?.Invoke(surface);
            FocusView();
            if (zone == "disclosure") { Host.ToggleExpand(item); e.Handled = true; return; }
            if (item != null)
            {
                if (e.ClickCount >= 2) { _press = null; Host.ItemDoubleClick(item); e.Handled = true; return; }
                Host.ItemMouseDown(item, Keyboard.Modifiers, this);
                _press = item;
                _pressPt = e.GetPosition(Root);
                e.Handled = true;
            }
            else
            {
                Host.BackgroundMouseDown(Keyboard.Modifiers);
                if (marquee) StartMarquee(surface, e.GetPosition(Root));
                e.Handled = true;
            }
        };
        surface.PreviewMouseMove += (s, e) =>
        {
            if (_press != null && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(Root) - _pressPt).Length > 5)
            {
                var p = _press;
                _press = null;
                Host.StartDrag(p, surface);
            }
            else if (_marqueeOn) UpdateMarquee(e.GetPosition(Root));
        };
        surface.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (_press != null) { Host.ItemMouseUp(_press, Keyboard.Modifiers); _press = null; }
            if (_marqueeOn) EndMarquee();
        };
        surface.PreviewMouseRightButtonDown += (s, e) =>
        {
            var (item, zone) = ItemAt(e.OriginalSource, surface);
            if (zone is "scrollbar" or "rename") return;
            beforeDown?.Invoke(surface);
            FocusView();
            if (item != null) Host.ItemRightDown(item); else Host.BackgroundMouseDown(ModifierKeys.None);
            e.Handled = true;
        };
        surface.PreviewMouseRightButtonUp += (s, e) =>
        {
            var (item, zone) = ItemAt(e.OriginalSource, surface);
            if (zone is "scrollbar" or "rename") return;
            if (item != null) Host.ItemContextMenu(item, surface); else Host.BackgroundContextMenu(surface);
            e.Handled = true;
        };
        surface.AllowDrop = true;
        surface.DragOver += (s, e) =>
        {
            var (item, _) = ItemAt(e.OriginalSource, surface);
            Host.DragOverTarget(item != null && (item.IsFolder || item.IsDrive) ? item : null, e);
            e.Handled = true;
        };
        surface.DragLeave += (s, e) => Host.DragLeft();
        surface.Drop += (s, e) =>
        {
            var (item, _) = ItemAt(e.OriginalSource, surface);
            Host.DropOn(item != null && (item.IsFolder || item.IsDrive) ? item : null, e);
            e.Handled = true;
        };
        surface.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
        {
            if (e.OriginalSource is TextBox tb && tb.Tag as string == "rename" && tb.DataContext is FileItem fi)
            {
                if (e.Key == Key.Enter) { Host.CommitRename(fi, tb.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { Host.CancelRename(fi); e.Handled = true; }
            }
        }), true);
        surface.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
        {
            if (e.OriginalSource is TextBox tb && tb.Tag as string == "rename" && tb.DataContext is FileItem fi && fi.IsRenaming)
                Host.CommitRename(fi, tb.Text);
        }), true);
    }

    void StartMarquee(FrameworkElement surface, Point p)
    {
        _marqueeOn = true;
        _marqueeSurface = surface;
        _marqueeStart = p;
        _marqueeAdditive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        surface.CaptureMouse();
    }

    void UpdateMarquee(Point p)
    {
        var r = new Rect(_marqueeStart, p);
        if (r.Width < 3 && r.Height < 3) return;
        _marquee.Visibility = Visibility.Visible;
        Canvas.SetLeft(_marquee, r.X); Canvas.SetTop(_marquee, r.Y);
        _marquee.Width = r.Width; _marquee.Height = r.Height;
        var hits = new HashSet<FileItem>();
        foreach (var fe in TaggedElements(_marqueeSurface))
        {
            try
            {
                var b = fe.TransformToAncestor(Root).TransformBounds(new Rect(fe.RenderSize));
                if (b.IntersectsWith(r)) hits.Add((FileItem)fe.DataContext);
            }
            catch { }
        }
        Host.MarqueeSelect(hits, _marqueeAdditive);
    }

    void EndMarquee()
    {
        _marqueeOn = false;
        _marquee.Visibility = Visibility.Collapsed;
        _marqueeSurface?.ReleaseMouseCapture();
    }
}

// ====================================================================== Icon view

public class IconView : FinderView
{
    readonly ItemsControl _ic;
    List<FileItem> _items = new();
    List<IconRow> _rows = new();
    int _perRow = 1;
    double _lastWidth;

    public IconView(IFinderHost host) : base(host)
    {
        _ic = MakeList("IconRow");
        _ic.Padding = new Thickness(0, 6, 0, 6);
        Root.Children.Insert(0, _ic);
        _ic.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - _lastWidth) > 1) Relayout(); };
        Wire(_ic, true);
        Relayout();
        Settings.Changed += () => { if (_iconSize != Settings.Current.FinderIconSize) Relayout(); };
    }

    double _iconSize;
    public override int ItemsPerRow => _perRow;

    public override void Show(IReadOnlyList<FileItem> items)
    {
        Items = items;
        _items = items.ToList();
        Relayout(true);
    }

    void Relayout(bool force = false)
    {
        _iconSize = Math.Clamp(Settings.Current.FinderIconSize, 16, 256);
        foreach (var it in _items.Take(400)) it.RequestIcon((int)_iconSize);
        double baseCell = Math.Max(_iconSize + 40, 92);
        double w = Math.Max(100, _ic.ActualWidth - 16);
        _lastWidth = _ic.ActualWidth;
        int perRow = Math.Max(1, (int)(w / baseCell));
        double cell = Math.Floor(w / perRow);
        Root.Resources["FinderIconSize"] = _iconSize;
        Root.Resources["FinderCellWidth"] = cell;
        Root.Resources["FinderLabelWidth"] = Math.Min(cell - 6, Math.Max(_iconSize + 36, 90));
        Root.Resources["FinderRowMargin"] = new Thickness(8, 0, 8, 0);
        if (perRow == _perRow && !force && _rows.Count > 0) return;
        _perRow = perRow;
        _rows = _items.Chunk(perRow).Select(c => new IconRow { Items = c.ToList() }).ToList();
        _ic.ItemsSource = _rows;
    }

    public override void Reveal(FileItem item)
    {
        int idx = _items.IndexOf(item);
        if (idx < 0) return;
        int row = idx / _perRow;
        var panel = FindChild<VirtualizingStackPanel>(_ic);
        panel?.BringIndexIntoViewPublic(row);
    }
}

// ====================================================================== List view

public class ListView2 : FinderView
{
    readonly ItemsControl _ic;
    readonly Grid _header = new() { Height = 28 };
    readonly Dictionary<string, TextBlock> _headerText = new();
    readonly Dictionary<string, SymbolIcon> _headerChevron = new();
    DrawingBrush _stripes;
    readonly TranslateTransform _stripeShift = new();

    public ListView2(IFinderHost host) : base(host)
    {
        Root.Resources["ColDateWidth"] = new GridLength(190);
        Root.Resources["ColSizeWidth"] = new GridLength(80);
        Root.Resources["ColKindWidth"] = new GridLength(130);
        var layout = new DockPanel();
        BuildHeader();
        DockPanel.SetDock(_header, Dock.Top);
        layout.Children.Add(_header);
        _ic = MakeList("ListRow");
        layout.Children.Add(_ic);
        Root.Children.Insert(0, layout);
        Wire(_ic, true);
        _ic.Loaded += (_, _) =>
        {
            var sv = FindScrollViewer(_ic);
            if (sv != null) sv.ScrollChanged += (_, e) => _stripeShift.Y = -(e.VerticalOffset % 48);
            ApplyStripes();
        };
        Theme.Changed += ApplyStripes;
    }

    void ApplyStripes()
    {
        var alt = (Brush)Application.Current.Resources["AlternateRowBrush"];
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 10, 24))));
        dg.Children.Add(new GeometryDrawing(alt, null, new RectangleGeometry(new Rect(0, 24, 10, 24))));
        _stripes = new DrawingBrush(dg)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.Fill,
            Viewport = new Rect(0, 0, 10, 48), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 10, 48), ViewboxUnits = BrushMappingMode.Absolute,
            Transform = _stripeShift,
        };
        var panel = FindChild<VirtualizingStackPanel>(_ic);
        if (panel != null) panel.Background = _stripes;
        else _ic.Dispatcher.BeginInvoke(() => { var p = FindChild<VirtualizingStackPanel>(_ic); if (p != null) p.Background = _stripes; }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void BuildHeader()
    {
        _header.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        foreach (var k in new[] { "ColDateWidth", "ColSizeWidth", "ColKindWidth" })
        {
            var cd = new ColumnDefinition();
            cd.SetResourceReference(ColumnDefinition.WidthProperty, k);
            _header.ColumnDefinitions.Add(cd);
        }
        _header.Margin = new Thickness(16, 0, 10, 0);
        AddHeader("Name", "name", 0, new Thickness(17, 0, 0, 0));
        AddHeader("Date Modified", "date", 1, new Thickness(8, 0, 0, 0));
        AddHeader("Size", "size", 2, new Thickness(0, 0, 10, 0), HorizontalAlignment.Right);
        AddHeader("Kind", "kind", 3, new Thickness(8, 0, 0, 0));
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-16, 0, -10, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        Grid.SetColumnSpan(line, 4);
        _header.Children.Add(line);
        RefreshHeader();
    }

    void AddHeader(string text, string key, int col, Thickness margin, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var cell = new Grid { Background = Brushes.Transparent };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
        var tb = new TextBlock { Text = text, FontSize = 12 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        var chev = new SymbolIcon { Symbol = "chevron.down", Width = 9, Height = 9, StrokeWidth = 2.6, Margin = new Thickness(5, 1, 0, 0), Visibility = Visibility.Collapsed };
        chev.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
        if (align == HorizontalAlignment.Right) { sp.Children.Add(chev); chev.Margin = new Thickness(0, 1, 5, 0); sp.Children.Add(tb); }
        else { sp.Children.Add(tb); sp.Children.Add(chev); }
        cell.Children.Add(sp);
        if (col > 0)
        {
            var sep = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };
            sep.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
            cell.Children.Add(sep);
        }
        cell.MouseLeftButtonUp += (_, _) => { Host.SortBy(key); RefreshHeader(); };
        Grid.SetColumn(cell, col);
        _header.Children.Add(cell);
        _headerText[key] = tb;
        _headerChevron[key] = chev;
    }

    public void RefreshHeader()
    {
        foreach (var (k, tb) in _headerText)
        {
            bool on = Host.SortKey == k;
            tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            tb.SetResourceReference(TextBlock.ForegroundProperty, on ? "LabelBrush" : "SecondaryLabelBrush");
            _headerChevron[k].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Date and size sort newest/largest first by default: that is a descending order (chevron down, as on a Mac).
            bool descending = (k is "date" or "size") == Host.SortAscending;
            _headerChevron[k].Symbol = descending ? "chevron.down" : "chevron.up";
        }
    }

    public override void Show(IReadOnlyList<FileItem> items)
    {
        Items = items;
        _ic.ItemsSource = null;
        _ic.ItemsSource = items;
        RefreshHeader();
    }

    public override void Reveal(FileItem item)
    {
        int idx = Items is IList<FileItem> l ? l.IndexOf(item) : Items.ToList().IndexOf(item);
        if (idx < 0) return;
        FindChild<VirtualizingStackPanel>(_ic)?.BringIndexIntoViewPublic(idx);
    }
}

// ====================================================================== Column view

public class ColumnView : FinderView
{
    class Column
    {
        public string Folder;
        public List<FileItem> Items;
        public ItemsControl List;
        public Border Root;
    }

    readonly ScrollViewer _hsv = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    readonly StackPanel _cols = new() { Orientation = Orientation.Horizontal };
    readonly List<Column> _columns = new();
    Border _preview;
    int _active;
    string _rootFolder;
    const double ColWidth = 232;

    public ColumnView(IFinderHost host) : base(host)
    {
        _hsv.Content = _cols;
        Root.Children.Insert(0, _hsv);
    }

    public string RootFolder { get => _rootFolder; set => _rootFolder = value; }
    public override IReadOnlyList<FileItem> CurrentItems => _active < _columns.Count ? _columns[_active].Items : Array.Empty<FileItem>();
    public string ActiveFolder => _active < _columns.Count ? _columns[_active].Folder : _rootFolder;

    public override void Show(IReadOnlyList<FileItem> items)
    {
        Items = items;
        // keep deeper columns if the root is unchanged (refresh), otherwise reset
        if (_columns.Count > 0 && string.Equals(_columns[0].Folder, _rootFolder, StringComparison.OrdinalIgnoreCase))
        {
            SetColumnItems(_columns[0], items.ToList());
            return;
        }
        _cols.Children.Clear();
        _columns.Clear();
        _preview = null;
        _active = 0;
        AddColumn(_rootFolder, items.ToList());
        Activate(0);
    }

    void SetColumnItems(Column c, List<FileItem> items)
    {
        c.Items = items;
        c.List.ItemsSource = items;
    }

    Column AddColumn(string folder, List<FileItem> items)
    {
        var list = MakeList("ColumnRow");
        list.Padding = new Thickness(0, 4, 0, 4);
        var border = new Border { Width = ColWidth, BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        border.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var col = new Column { Folder = folder, Items = items, List = list, Root = border };
        list.ItemsSource = items;
        int index = _columns.Count;
        Wire(list, false, _ => SetActive(_columns.IndexOf(col)));
        _columns.Add(col);
        if (_preview != null) _cols.Children.Remove(_preview);
        _preview = null;
        _cols.Children.Add(border);
        return col;
    }

    void SetActive(int idx)
    {
        if (idx < 0 || idx >= _columns.Count) return;
        Activate(idx);
    }

    void Activate(int idx)
    {
        _active = idx;
        for (int i = 0; i < _columns.Count; i++) FinderViewState.SetIsActive(_columns[i].Root, i == idx);
        Host.ActiveColumnChanged(_columns[idx].Folder);
    }

    void TruncateAfter(int idx)
    {
        while (_columns.Count > idx + 1)
        {
            var c = _columns[^1];
            foreach (var it in c.Items) it.IsSelected = false;
            _cols.Children.Remove(c.Root);
            _columns.RemoveAt(_columns.Count - 1);
        }
        if (_preview != null) { _cols.Children.Remove(_preview); _preview = null; }
    }

    public override void OnSelectionChanged()
    {
        if (_active >= _columns.Count) return;
        var col = _columns[_active];
        var sel = col.Items.Where(i => i.IsSelected).ToList();
        TruncateAfter(_active);
        if (sel.Count != 1) return;
        var item = sel[0];
        if (item.IsFolder || item.IsDrive)
        {
            string folder = item.FullPath;
            var placeholder = AddColumn(folder, new List<FileItem>());
            FinderViewState.SetIsActive(placeholder.Root, false);
            Host.LoadChildren(folder, children =>
            {
                if (!_columns.Contains(placeholder)) return;
                SetColumnItems(placeholder, children);
            });
        }
        else
        {
            _preview = PreviewPane.Build(item, 280);
            _cols.Children.Add(_preview);
        }
        _hsv.Dispatcher.BeginInvoke(() => _hsv.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public override bool HandleKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Right)
        {
            if (_active + 1 < _columns.Count && _columns[_active + 1].Items.Count > 0)
            {
                var next = _columns[_active + 1];
                Activate(_active + 1);
                foreach (var i in next.Items) i.IsSelected = false;
                next.Items[0].IsSelected = true;
                OnSelectionChanged();
                Reveal(next.Items[0]);
            }
            return true;
        }
        if (key == Key.Left)
        {
            if (_active > 0)
            {
                foreach (var i in _columns[_active].Items) i.IsSelected = false;
                Activate(_active - 1);
                OnSelectionChanged();
            }
            return true;
        }
        return false;
    }

    public override void Reveal(FileItem item)
    {
        foreach (var c in _columns)
        {
            int idx = c.Items.IndexOf(item);
            if (idx >= 0) { FindChild<VirtualizingStackPanel>(c.List)?.BringIndexIntoViewPublic(idx); return; }
        }
    }
}

// ====================================================================== Gallery view

public class GalleryView : FinderView
{
    readonly ItemsControl _strip;
    readonly Image _big = new() { Stretch = Stretch.Uniform, Margin = new Thickness(30, 24, 30, 10) };
    readonly TextBlock _caption = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 13, Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Border _infoHost = new() { Width = 260, BorderThickness = new Thickness(1, 0, 0, 0) };

    public GalleryView(IFinderHost host) : base(host)
    {
        RenderOptions.SetBitmapScalingMode(_big, BitmapScalingMode.HighQuality);
        _big.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.22 };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        _infoHost.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
        grid.Children.Add(_big);
        Grid.SetRow(_caption, 1);
        grid.Children.Add(_caption);
        _strip = MakeList("FilmThumb", Orientation.Horizontal, true);
        _strip.Padding = new Thickness(10, 8, 10, 8);
        var stripBorder = new Border { Child = _strip, BorderThickness = new Thickness(0, 1, 0, 0) };
        stripBorder.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        Grid.SetRow(stripBorder, 2);
        grid.Children.Add(stripBorder);
        Grid.SetColumn(_infoHost, 1);
        Grid.SetRowSpan(_infoHost, 3);
        grid.Children.Add(_infoHost);
        Root.Children.Insert(0, grid);
        Wire(_strip, false);
        Wire(_big, false);
    }

    public override void Show(IReadOnlyList<FileItem> items)
    {
        Items = items;
        _strip.ItemsSource = items;
        OnSelectionChanged();
    }

    public override void OnSelectionChanged()
    {
        var sel = Items.FirstOrDefault(i => i.IsSelected) ?? Items.FirstOrDefault();
        if (sel == null) { _big.Source = null; _caption.Text = ""; _infoHost.Child = null; return; }
        _big.DataContext = sel;
        _big.SetBinding(Image.SourceProperty, new Binding(nameof(FileItem.Thumbnail)) { Source = sel });
        _caption.Text = sel.DisplayName;
        _infoHost.Child = PreviewPane.Build(sel, 260, showImage: false);
    }

    public override void Reveal(FileItem item)
    {
        int idx = Items.ToList().IndexOf(item);
        if (idx >= 0) FindChild<VirtualizingStackPanel>(_strip)?.BringIndexIntoViewPublic(idx);
    }

    public override bool HandleKey(Key key, ModifierKeys mods) => false;
}

/// <summary>The "Preview" pane used by Column and Gallery views.</summary>
public static class PreviewPane
{
    public static Border Build(FileItem item, double width, bool showImage = true)
    {
        var sp = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        if (showImage)
        {
            var img = new Image { Height = 180, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10, 0, 14) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.SetBinding(Image.SourceProperty, new Binding(nameof(FileItem.Thumbnail)) { Source = item });
            sp.Children.Add(img);
        }
        var name = new TextBlock { Text = item.DisplayName, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(name);
        var kind = new TextBlock { Text = item.Kind + (item.Size >= 0 && !item.IsFolder ? " – " + item.SizeText : ""), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        sp.Children.Add(kind);
        var header = new TextBlock { Text = "Information", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 18, 0, 6) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        sp.Children.Add(header);
        void Row(string k, string v)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var a = new TextBlock { Text = k, FontSize = 12 };
            a.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            var b = new TextBlock { Text = v, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0) };
            b.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            Grid.SetColumn(b, 1);
            g.Children.Add(a); g.Children.Add(b);
            sp.Children.Add(g);
        }
        if (item.IsTrashItem && item.OriginalLocation != null) Row("Where", item.OriginalLocation);
        Row("Created", item.CreatedText);
        Row("Modified", item.ModifiedText);
        if (!item.IsFolder && !item.IsApp && item.FullPath != null)
        {
            try { var fi = new FileInfo(item.FullPath); if (fi.Exists) Row("Last opened", FileItem.FormatDate(fi.LastAccessTime)); } catch { }
        }
        var more = new TextBlock { Text = "More…", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand };
        more.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        more.MouseLeftButtonUp += (_, _) => GetInfoWindow.ShowFor(item);
        sp.Children.Add(more);
        return new Border { Width = width, Child = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } };
    }
}
