using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using MailKit;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps.Mail;

/// <summary>
/// Mail, like macOS Mail: the mailboxes in the sidebar (Favorites - All Inboxes, Flagged, Drafts, Sent - then each
/// account's), the messages of the one chosen (newest first: a blue dot for unread, sender, date, subject, a preview),
/// and the message itself. The toolbar: compose, archive, trash, junk, reply, reply all, forward, flag, search.
/// </summary>
public sealed class MailWindow : MacWindow
{
    public const string AppId = "internal:mail";
    public static readonly List<MailWindow> All = new();
    public static MailWindow Front => All.FirstOrDefault(w => w.IsActive) ?? All.LastOrDefault();

    /// <summary>Opening Mail: the window - or, with no account yet, the account sheet first (as on a Mac).</summary>
    public static void OpenApp()
    {
        MailService.Start();
        if (MailService.Accounts.Count == 0) { MailSetupWindow.Open(); return; }
        var f = Front;
        if (f != null)
        {
            if (f.WindowState == WindowState.Minimized) f.WindowState = WindowState.Normal;
            f.Activate();
            return;
        }
        var w = new MailWindow();
        w.Show();
        if (!ShellHost.Offscreen) w.Activate();
    }

    /// <summary>A message opened from a banner: in a Mail window, in All Inboxes (another mailbox's: in that mailbox).
    /// A search that would hide it is cleared.</summary>
    public static void Reveal(MailMessageInfo m)
    {
        OpenApp();
        var w = Front;
        if (w == null || m == null) return;
        var sync = MailService.Sync(m.AccountId);
        var f = sync?.Folder(m.Folder);
        if (f == null) return;
        var box = f.Role == "inbox" ? AllInboxes : new Mailbox(m.AccountId, f.FullName, f.Role, f.Name);
        w._search.Text = "";
        w.ShowMailbox(box, m.Key);
        if ((w._list.SelectedItem as MailItem)?.Key != m.Key && w._unreadOnly.IsChecked == true)
        {
            w._unreadOnly.IsChecked = false;   // (read since - on another device: not in Show Only Unread)
            w.ShowMailbox(box, m.Key);
        }
        w.FocusSelected();
    }

    static Mailbox AllInboxes => new(null, null, "inbox", MailService.Accounts.Count > 1 ? "All Inboxes" : "Inbox");

    readonly Grid _layout = new();
    readonly ColumnDefinition _sideCol = new(), _listCol = new();
    readonly StackPanel _sidebar = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly Dictionary<Mailbox, Border> _rows = new();
    readonly ListBox _list = new();
    readonly ObservableCollection<MailItem> _items = new();
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _subtitle = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBox _search = new() { Width = 180 };
    readonly Button _loadMore = new() { Content = "Load More Messages", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8), Visibility = Visibility.Collapsed };
    readonly ToggleButton _unreadOnly = new();
    readonly TextBlock _empty = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "No Messages" };
    readonly MailViewer _viewer = new();
    Mailbox _box;

    MailWindow() : base(AppId)
    {
        Title = "Mail";
        Width = 1180;
        Height = 760;
        MinWidth = 760;
        MinHeight = 420;
        PlaceAsBefore();
        All.Add(this);
        Closed += (_, _) =>
        {
            All.Remove(this);
            MailService.Changed -= OnMailChanged;
            Remember();
        };
        SizeChanged += (_, _) => Remember();
        LocationChanged += (_, _) => Remember();
        StateChanged += (_, _) => Remember();
        BuildChrome();
        MailService.Changed += OnMailChanged;
        var first = MailService.Accounts.Count > 0 ? AllInboxes : null;
        BuildSidebar();
        if (first != null) ShowMailbox(first);
        PreviewKeyDown += OnKey;
    }

    // ================================================================== chrome

    static Button ToolButton(string symbol, string tip, Action click, double size = 17)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Content = new SymbolIcon { Symbol = symbol, Width = size, Height = size, StrokeWidth = 1.8 },
            ToolTip = tip,
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (_, _) => click();
        return b;
    }

    void BuildChrome()
    {
        _sideCol.Width = new GridLength(Math.Clamp(Settings.Current.MailSidebarWidth, 150, 340));
        _listCol.Width = new GridLength(Math.Clamp(Settings.Current.MailListWidth, 240, 520));
        _layout.ColumnDefinitions.Add(_sideCol);
        _layout.ColumnDefinitions.Add(_listCol);
        _layout.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 280 });

        // ---------------- sidebar (the window's vibrancy shows through)
        var side = new Grid();
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        side.RowDefinitions.Add(new RowDefinition());
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "SidebarTintBrush");
        Grid.SetRowSpan(tint, 2);
        side.Children.Add(tint);
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(new TrafficLights { Margin = new Thickness(20, 0, 0, 0) });
        var toggle = ToolButton("sidebar.left", "Hide Sidebar", ToggleSidebar);
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(toggle);
        side.Children.Add(head);
        var sideScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(sideScroll, 1);
        side.Children.Add(sideScroll);
        _layout.Children.Add(side);
        var split1 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
        WindowChrome.SetIsHitTestVisibleInChrome(split1, true);
        split1.DragCompleted += (_, _) => Remember();
        _layout.Children.Add(split1);

        // ---------------- message list: its toolbar part (the mailbox's name and count), then the list
        var listCol = new DockPanel();
        listCol.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var listEdge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = listCol };
        listEdge.SetResourceReference(Border.BorderBrushProperty, "StrongSeparatorBrush");
        Grid.SetColumn(listEdge, 1);
        _layout.Children.Add(listEdge);
        var listBar = new DockPanel { Height = 52, LastChildFill = true };
        listBar.SetResourceReference(Panel.BackgroundProperty, "ToolbarBackgroundBrush");
        _unreadOnly.Style = (Style)Application.Current.Resources["ToolbarButton"];
        _unreadOnly.Content = new SymbolIcon { Symbol = "line.3.horizontal.decrease.circle", Width = 17, Height = 17, StrokeWidth = 1.8 };
        _unreadOnly.ToolTip = "Show Only Unread Messages";
        _unreadOnly.Margin = new Thickness(0, 0, 8, 0);
        _unreadOnly.Click += (_, _) => UnreadOnlyChanged();
        WindowChrome.SetIsHitTestVisibleInChrome(_unreadOnly, true);
        DockPanel.SetDock(_unreadOnly, Dock.Right);
        listBar.Children.Add(_unreadOnly);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        listBar.Children.Add(titles);
        DockPanel.SetDock(listBar, Dock.Top);
        listCol.Children.Add(listBar);
        var listLine = new Border { Height = 1 };
        listLine.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        DockPanel.SetDock(listLine, Dock.Top);
        listCol.Children.Add(listLine);
        _loadMore.Style = (Style)Application.Current.Resources["MacButton"];
        _loadMore.Click += (_, _) => LoadMore();
        DockPanel.SetDock(_loadMore, Dock.Bottom);
        listCol.Children.Add(_loadMore);
        var listHost = new Grid();
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        listHost.Children.Add(_empty);
        BuildList();
        listHost.Children.Add(_list);
        listCol.Children.Add(listHost);
        var split2 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
        Grid.SetColumn(split2, 1);
        WindowChrome.SetIsHitTestVisibleInChrome(split2, true);
        split2.DragCompleted += (_, _) => Remember();
        _layout.Children.Add(split2);

        // ---------------- the message: its toolbar, then the viewer
        var viewCol = new DockPanel();
        viewCol.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");
        var viewEdge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = viewCol };
        viewEdge.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        Grid.SetColumn(viewEdge, 2);
        _layout.Children.Add(viewEdge);
        var bar = new DockPanel { Height = 52, LastChildFill = false };
        bar.SetResourceReference(Panel.BackgroundProperty, "ToolbarBackgroundBrush");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        tools.Children.Add(ToolButton("square.and.pencil", "New Message", () => Compose()));
        tools.Children.Add(Gap());
        tools.Children.Add(ToolButton("archivebox", "Archive", Archive));
        tools.Children.Add(ToolButton("trash", "Delete", Delete));
        tools.Children.Add(ToolButton("xmark.bin", "Move to Junk", Junk));
        tools.Children.Add(Gap());
        tools.Children.Add(ToolButton("arrowshape.turn.up.left", "Reply", () => Reply(false)));
        tools.Children.Add(ToolButton("arrowshape.turn.up.left.2", "Reply All", () => Reply(true)));
        tools.Children.Add(ToolButton("arrowshape.turn.up.right", "Forward", Forward));
        tools.Children.Add(Gap());
        tools.Children.Add(ToolButton("flag", "Flag", ToggleFlag));
        bar.Children.Add(tools);
        _search.Style = (Style)Application.Current.Resources["MacSearchField"];
        _search.Margin = new Thickness(0, 0, 12, 0);
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RefreshList(keepSelection: true);
        WindowChrome.SetIsHitTestVisibleInChrome(_search, true);
        DockPanel.SetDock(_search, Dock.Right);
        bar.Children.Add(_search);
        DockPanel.SetDock(bar, Dock.Top);
        viewCol.Children.Add(bar);
        var viewLine = new Border { Height = 1 };
        viewLine.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        DockPanel.SetDock(viewLine, Dock.Top);
        viewCol.Children.Add(viewLine);
        viewCol.Children.Add(_viewer);

        Content = _layout;
    }

    static FrameworkElement Gap() => new Border { Width = 10 };

    /// <summary>Diagnostics (--open mailstate): what the front window shows (counts only).</summary>
    public static string TestState() => Front is not { } w ? "no window" :
        $"box={w._box?.Title} role={w._box?.Role} items={w._items.Count} selected={w._list.SelectedItems.Count} " +
        $"unreadOnly={w._unreadOnly.IsChecked == true} search={w._search.Text.Length} subtitle=\"{w._subtitle.Text}\" empty={w._empty.Visibility} " +
        $"viewerLoaded={w._viewer.Message != null}";

    /// <summary>Where and how big Mail's window was last time (if that's still on a screen), and zoomed if it was.</summary>
    void PlaceAsBefore()
    {
        var b = Settings.Current.MailWindowBounds;
        bool onScreen = b is { Length: 4 } && b[2] >= MinWidth - 1 && b[3] >= MinHeight - 1 &&
                        b[0] + b[2] - 120 > SystemParameters.VirtualScreenLeft && b[0] + 120 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                        b[1] >= SystemParameters.VirtualScreenTop - 10 && b[1] + 40 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!onScreen) { CenterOnWorkArea(); return; }
        Left = b[0];
        Top = b[1];
        Width = b[2];
        Height = b[3];
        if (Settings.Current.MailWindowZoomed) WindowState = WindowState.Maximized;
        ApplyOffscreen();
    }

    /// <summary>The window's place, size and columns kept as they change - so they're saved even when MacShell quits
    /// (it saves its settings before closing windows).</summary>
    void Remember()
    {
        if (!IsLoaded || ShellHost.Offscreen || WindowState == WindowState.Minimized) return;
        var r = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        if (r.IsEmpty || r.Width < 100 || r.Height < 100) return;
        var s = Settings.Current;
        s.MailWindowBounds = new[] { Math.Round(r.Left), Math.Round(r.Top), Math.Round(r.Width), Math.Round(r.Height) };
        s.MailWindowZoomed = WindowState == WindowState.Maximized;
        if (_sideCol.ActualWidth > 0) s.MailSidebarWidth = Math.Round(_sideCol.ActualWidth);
        if (_listCol.ActualWidth > 0) s.MailListWidth = Math.Round(_listCol.ActualWidth);
        Settings.Save(notify: false);
    }

    void ToggleSidebar()
    {
        if (_sideCol.Width.Value > 0) { Settings.Current.MailSidebarWidth = _sideCol.ActualWidth; _sideCol.Width = new GridLength(0); }
        else _sideCol.Width = new GridLength(Math.Clamp(Settings.Current.MailSidebarWidth, 150, 340));
    }

    // ================================================================== sidebar

    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _rows.Clear();
        var accounts = MailService.Accounts;
        if (accounts.Count == 0)
        {
            var add = new Button { Content = "Add Account…", Style = (Style)Application.Current.Resources["MacButton"], Margin = new Thickness(18, 12, 18, 0) };
            add.Click += (_, _) => MailSetupWindow.Open();
            _sidebar.Children.Add(add);
            return;
        }
        Section("Favorites", null);
        Row(AllInboxes, "tray");
        Row(new Mailbox(null, null, "flagged", "Flagged"), "flag");
        Row(new Mailbox(null, null, "drafts", "Drafts"), "doc");
        Row(new Mailbox(null, null, "sent", "Sent"), "paperplane");
        bool twoOfAKind = accounts.GroupBy(a => a.Provider).Any(g => g.Count() > 1);
        foreach (var a in accounts)
        {
            var sync = MailService.Sync(a.Id);
            if (sync == null) continue;
            Section(twoOfAKind ? a.Email : a.Title, sync);
            foreach (var f in sync.Folders)
                Row(new Mailbox(a.Id, f.FullName, f.Role, f.Name), Symbol(f.Role, a.Provider), Depth(f, a.Provider));
        }
        Highlight();
    }

    static string Symbol(string role, MailProvider p) => role switch
    {
        "inbox" => "tray", "drafts" => "doc", "sent" => "paperplane", "junk" => "xmark.bin", "trash" => "trash",
        "archive" or "all" => "archivebox", "flagged" => p == MailProvider.Gmail ? "star" : "flag", _ => "folder",
    };

    /// <summary>How deep a mailbox of the user's own is nested (Gmail's labels "Work/Clients" …).</summary>
    static int Depth(MailFolderInfo f, MailProvider p)
    {
        if (f.Role != null) return 0;
        string name = p == MailProvider.Gmail && f.FullName.StartsWith("[Gmail]/") ? f.FullName[8..] : f.FullName;
        return Math.Min(3, name.Count(c => c == '/'));
    }

    void Section(string title, MailSync sync)
    {
        var dp = new DockPanel { Margin = new Thickness(18, _sidebar.Children.Count == 0 ? 0 : 14, 12, 4) };
        if (sync?.Error is string err)
        {
            var warn = new TextBlock { Text = "⚠", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), ToolTip = err, Cursor = Cursors.Hand };
            warn.MouseLeftButtonUp += (_, _) =>
            {
                if (ShellHost.Alert($"There was a problem with “{sync.Account.Email}”.", err, "OK", "Account Settings…") == "Account Settings…")
                    MailSetupWindow.Open();
            };
            DockPanel.SetDock(warn, Dock.Right);
            dp.Children.Add(warn);
        }
        var tb = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        dp.Children.Add(tb);
        _sidebar.Children.Add(dp);
    }

    void Row(Mailbox box, string symbol, int depth = 0)
    {
        var dp = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var (total, unread) = MailService.Counts(box);
        int shown = box.Role == "drafts" ? total : unread;
        if (shown > 0)
        {
            var count = new TextBlock { Text = shown.ToString("N0"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            DockPanel.SetDock(count, Dock.Right);
            dp.Children.Add(count);
        }
        var icon = new SymbolIcon { Symbol = symbol, Width = 17, Height = 17, StrokeWidth = 1.8, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "AccentBrush");
        dp.Children.Add(icon);
        var tb = new TextBlock { Text = box.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        dp.Children.Add(tb);
        var row = new Border { Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(10 + depth * 14, 0, 10, 0), Padding = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent, Child = dp, AllowDrop = box.AccountId != null };
        row.MouseLeftButtonUp += (_, _) => ShowMailbox(box);
        // messages dragged onto a mailbox: moved there
        row.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(MailItem.DragFormat) && box.AccountId != null ? DragDropEffects.Move : DragDropEffects.None;
            if (e.Effects != DragDropEffects.None) row.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            e.Handled = true;
        };
        row.DragLeave += (_, _) => Highlight();
        row.Drop += (_, e) =>
        {
            Highlight();
            if (box.AccountId == null) return;
            var moving = Selection().Where(m => m.AccountId == box.AccountId && m.Folder != box.Folder).ToList();
            if (moving.Count > 0) _ = MailService.Sync(box.AccountId).MoveAsync(moving, box.Folder);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var items = new List<object> { Mb.Item("Get New Mail", MailService.GetMail) };
            if (box.AccountId != null && box.Role is "trash" or "junk")
                items.Add(Mb.Item(box.Role == "trash" ? "Erase Deleted Items…" : "Erase Junk Mail…", () => EraseAll(box)));
            Mb.Context(items.ToArray()).IsOpen = true;
        };
        _rows[box] = row;
        _sidebar.Children.Add(row);
    }

    void Highlight()
    {
        foreach (var (box, row) in _rows)
        {
            if (box == _box) row.SetResourceReference(Border.BackgroundProperty, "SidebarSelectionActiveBrush");
            else row.Background = Brushes.Transparent;
        }
    }

    void EraseAll(Mailbox box)
    {
        var sync = MailService.Sync(box.AccountId);
        var all = sync?.Messages(box.Folder).ToList();
        if (all == null || all.Count == 0) return;
        if (ShellHost.Alert($"Are you sure you want to permanently erase the {all.Count} message{(all.Count == 1 ? "" : "s")} in {box.Title}?", "You can’t undo this action.", "Cancel", "Erase") == "Erase")
            _ = sync.EraseAsync(all);
    }

    // ================================================================== the list

    void BuildList()
    {
        _list.ItemsSource = _items;
        _list.SelectionMode = SelectionMode.Extended;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.Focusable = true;
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _list.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        _list.ItemContainerStyle = (Style)XamlReader.Parse(MailItem.ContainerXaml);
        _list.ItemTemplate = (DataTemplate)XamlReader.Parse(MailItem.TemplateXaml);
        _list.SelectionChanged += (_, _) => OnSelection();
        _list.MouseDoubleClick += (_, e) =>
        {
            // a draft opens to be written on
            if (_list.SelectedItem is MailItem it && it.Info.Draft) EditDraft(it.Info);
        };
        _list.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _list.SelectedItems.Count == 0 || !_dragArmed) return;
            if ((e.GetPosition(_list) - _dragFrom).Length < 6) return;
            _dragArmed = false;
            var data = new DataObject(MailItem.DragFormat, "messages");
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Move);
        };
        _list.PreviewMouseLeftButtonDown += (_, e) => { _dragArmed = true; _dragFrom = e.GetPosition(_list); };
        _list.PreviewMouseLeftButtonUp += (_, _) => _dragArmed = false;
        _list.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true;
            var sel = Selection();
            if (sel.Count == 0) return;
            bool unread = sel.Any(m => !m.Seen), flagged = sel.All(m => m.Flagged);
            Mb.Context(
                Mb.Item("Reply", () => Reply(false)), Mb.Item("Reply All", () => Reply(true)), Mb.Item("Forward", Forward),
                Mb.Sep(),
                Mb.Item(unread ? "Mark as Read" : "Mark as Unread", ToggleRead),
                Mb.Item(flagged ? "Unflag" : "Flag", ToggleFlag),
                Mb.Sep(),
                Mb.Item("Archive", Archive), Mb.Item("Move to Junk", Junk), Mb.Item("Delete", Delete)).IsOpen = true;
        };
    }

    bool _dragArmed;
    Point _dragFrom;

    void ShowMailbox(Mailbox box, string selectKey = null)
    {
        _box = box;
        _stillShown.Clear();
        foreach (var s in MailService.All) s.Watching = box.AccountId == s.Account.Id ? box.Folder : null;
        if (box.AccountId != null) _ = MailService.Sync(box.AccountId)?.SyncFolderAsync(box.Folder);
        Highlight();
        _items.Clear();
        RefreshList(keepSelection: false);
        if (selectKey != null && _items.FirstOrDefault(i => i.Key == selectKey) is { } it) { _list.SelectedItem = it; _list.ScrollIntoView(it); }
        else if (_items.Count > 0) _list.SelectedIndex = 0;
    }

    void OnMailChanged()
    {
        BuildSidebar();
        RefreshList(keepSelection: true);
    }

    /// <summary>What Show Only Unread has shown: they stay while it's on, read or not - as on a Mac, reading one doesn't
    /// whisk it out of the list - until it's turned off or on again, or another mailbox is chosen.</summary>
    readonly HashSet<string> _stillShown = new();

    void UnreadOnlyChanged()
    {
        _stillShown.Clear();
        RefreshList(keepSelection: true);
    }

    /// <summary>The list brought up to date in place (what's new goes in, what's gone comes out): it doesn't jump.</summary>
    void RefreshList(bool keepSelection)
    {
        var box = _box;
        var source = MailService.Messages(box);
        string q = _search.Text.Trim();
        IEnumerable<MailMessageInfo> shown = source;
        bool unreadOnly = _unreadOnly.IsChecked == true;
        if (unreadOnly) shown = shown.Where(m => !m.Seen || _stillShown.Contains(m.Key));
        if (q.Length > 0)
            shown = shown.Where(m => Has(m.Subject, q) || Has(m.FromName, q) || Has(m.FromAddress, q) || Has(m.Preview, q) || Has(m.To, q));
        var want = shown.ToList();
        if (unreadOnly) _stillShown.UnionWith(want.Select(m => m.Key));
        bool outgoing = box?.Role is "sent" or "drafts";
        // what's gone comes out first - the rest then needn't move (moving re-creates rows, and the row with the
        // keyboard focus would lose it: the next ↓ would start again from the top)
        var wanted = want.Select(m => m.Key).ToHashSet();
        bool hadFocus = _list.IsKeyboardFocusWithin;
        for (int i = _items.Count - 1; i >= 0; i--) if (!wanted.Contains(_items[i].Key)) _items.RemoveAt(i);
        var byKey = _items.ToDictionary(i => i.Key);
        for (int i = 0; i < want.Count; i++)
        {
            var m = want[i];
            if (i < _items.Count && _items[i].Key == m.Key) { _items[i].Update(m, outgoing); continue; }
            if (byKey.TryGetValue(m.Key, out var existing))
            {
                int at = _items.IndexOf(existing);
                if (at >= 0) { _items.Move(at, i); existing.Update(m, outgoing); continue; }
            }
            _items.Insert(i, new MailItem(m, outgoing));
        }
        while (_items.Count > want.Count) _items.RemoveAt(_items.Count - 1);
        if (!keepSelection && _items.Count > 0 && _list.SelectedItem == null) _list.SelectedIndex = 0;
        if (hadFocus && !_list.IsKeyboardFocusWithin) FocusSelected();

        var (total, unread) = MailService.Counts(box);
        string accountTitle = MailService.Accounts.FirstOrDefault(a => a.Id == box?.AccountId)?.Title;
        _title.Text = box == null ? "Mail" : accountTitle != null && MailService.Accounts.Count > 1 ? $"{box.Title} — {accountTitle}" : box.Title;
        bool busy = MailService.All.Any(s => s.Busy);
        _subtitle.Text = busy ? "Checking for new mail…" : $"{total:N0} message{(total == 1 ? "" : "s")}" + (unread > 0 ? $", {unread:N0} unread" : "");
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = q.Length > 0 ? "No Results" : "No Messages";
        _loadMore.Visibility = box?.AccountId != null && MailService.Sync(box.AccountId)?.Folder(box.Folder)?.HasOlder == true && q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = box != null ? $"{_title.Text} ({total:N0} messages)" : "Mail";
    }

    static bool Has(string s, string q) => s != null && s.Contains(q, StringComparison.CurrentCultureIgnoreCase);

    void LoadMore()
    {
        if (_box?.AccountId == null) return;
        _ = MailService.Sync(_box.AccountId)?.SyncFolderAsync(_box.Folder, older: true);
    }

    List<MailMessageInfo> Selection() => _list.SelectedItems.Cast<MailItem>().Select(i => i.Info).ToList();

    void OnSelection()
    {
        var sel = Selection();
        _viewer.Show(sel.Count == 1 ? sel[0] : null, sel.Count);
    }

    /// <summary>After messages leave the list: the one that came after them is chosen (as Mail does).</summary>
    void SelectAfter(List<MailMessageInfo> leaving)
    {
        if (leaving.Count == 0) return;
        var keys = leaving.Select(m => m.Key).ToHashSet();
        int idx = _items.Select((it, i) => (it, i)).Where(x => keys.Contains(x.it.Key)).Select(x => x.i).DefaultIfEmpty(-1).Max();
        var next = _items.Skip(idx + 1).FirstOrDefault(it => !keys.Contains(it.Key)) ?? _items.Take(Math.Max(0, idx)).LastOrDefault(it => !keys.Contains(it.Key));
        Dispatcher.BeginInvoke(() =>
        {
            if (next == null || !_items.Contains(next)) return;
            _list.SelectedItem = next;
            FocusSelected();
        });
    }

    /// <summary>The keyboard on the chosen message's row (not on the list as a whole: ↓ would then go to the top).</summary>
    void FocusSelected()
    {
        var it = _list.SelectedItem;
        if (it == null) { _list.Focus(); return; }
        _list.ScrollIntoView(it);
        _list.UpdateLayout();
        if (_list.ItemContainerGenerator.ContainerFromItem(it) is ListBoxItem row) row.Focus(); else _list.Focus();
    }

    // ================================================================== actions

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var mods = Keyboard.Modifiers;
        if ((e.Key is Key.Delete or Key.Back) && mods == ModifierKeys.None) { Delete(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && e.Key == Key.N) { Compose(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && e.Key == Key.R) { Reply(false); e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.R: Reply(true); break;
                case Key.F: Forward(); break;
                case Key.N: MailService.GetMail(); break;
                case Key.U: ToggleRead(); break;
                case Key.L: ToggleFlag(); break;
                case Key.J: Junk(); break;
                case Key.A: Archive(); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
    }

    public void Compose()
    {
        var account = MailService.AccountOf(Selection().FirstOrDefault()) ?? (_box?.AccountId != null ? MailService.Accounts.FirstOrDefault(a => a.Id == _box.AccountId) : null);
        MailComposeWindow.New(account);
    }

    public async void Reply(bool all)
    {
        var m = Selection().FirstOrDefault();
        if (m == null) return;
        var msg = await MailService.Sync(m.AccountId).GetMessageAsync(m);
        if (msg != null) MailComposeWindow.Reply(m, msg, all);
    }

    public async void Forward()
    {
        var m = Selection().FirstOrDefault();
        if (m == null) return;
        var msg = await MailService.Sync(m.AccountId).GetMessageAsync(m);
        if (msg != null) MailComposeWindow.Forward(m, msg);
    }

    async void EditDraft(MailMessageInfo m)
    {
        var msg = await MailService.Sync(m.AccountId).GetMessageAsync(m);
        if (msg != null) MailComposeWindow.EditDraft(m, msg);
    }

    public void ToggleRead()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        bool read = sel.Any(m => !m.Seen);
        foreach (var g in sel.GroupBy(m => m.AccountId)) _ = MailService.Sync(g.Key)?.SetFlagAsync(g.ToList(), MessageFlags.Seen, read);
        RefreshList(true);
    }

    public void ToggleFlag()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        bool flag = !sel.All(m => m.Flagged);
        foreach (var g in sel.GroupBy(m => m.AccountId)) _ = MailService.Sync(g.Key)?.SetFlagAsync(g.ToList(), MessageFlags.Flagged, flag);
        RefreshList(true);
    }

    /// <summary>Into each account's mailbox of a role (archive: Archive, or Gmail's All Mail).</summary>
    void MoveTo(Func<MailSync, MailFolderInfo> dest, Func<MailMessageInfo, MailSync, bool> skip = null)
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        var moved = new List<MailMessageInfo>();
        foreach (var g in sel.GroupBy(m => m.AccountId))
        {
            var sync = MailService.Sync(g.Key);
            var to = sync == null ? null : dest(sync);
            if (to == null) continue;
            var list = g.Where(m => m.Folder != to.FullName && (skip == null || !skip(m, sync))).ToList();
            if (list.Count == 0) continue;
            moved.AddRange(list);
            _ = sync.MoveAsync(list, to.FullName);
        }
        SelectAfter(moved);
    }

    public void Archive() => MoveTo(s => s.FolderByRole("archive") ?? s.FolderByRole("all"));

    public void Junk()
    {
        // (already in Junk: back to the Inbox - "Not Junk")
        var sel = Selection();
        if (sel.Count > 0 && sel.All(m => MailService.Sync(m.AccountId)?.Folder(m.Folder)?.Role == "junk"))
            MoveTo(s => s.FolderByRole("inbox"));
        else MoveTo(s => s.FolderByRole("junk"));
    }

    public void Delete()
    {
        var sel = Selection();
        if (sel.Count == 0) return;
        var erase = new List<MailMessageInfo>();
        foreach (var g in sel.GroupBy(m => m.AccountId))
        {
            var sync = MailService.Sync(g.Key);
            if (sync == null) continue;
            var trash = sync.FolderByRole("trash");
            var inTrash = g.Where(m => trash == null || m.Folder == trash.FullName).ToList();
            erase.AddRange(inTrash);
            var rest = g.Except(inTrash).ToList();
            if (rest.Count > 0) _ = sync.MoveAsync(rest, trash.FullName);
        }
        if (erase.Count > 0)
        {
            if (ShellHost.Alert($"Are you sure you want to permanently delete {(erase.Count == 1 ? "this message" : $"these {erase.Count} messages")}?", "You can’t undo this action.", "Cancel", "Delete") != "Delete") return;
            foreach (var g in erase.GroupBy(m => m.AccountId)) _ = MailService.Sync(g.Key)?.EraseAsync(g.ToList());
        }
        SelectAfter(sel);
    }

    // ================================================================== menu bar

    public static object[] MenuMail() => new object[]
    {
        Mb.Item("About Mail", AboutWindow.ShowWindow),
        Mb.Sep(),
        Mb.Item("Settings…", MailSetupWindow.Open, "⌘,"),
        Mb.Item("Add Account…", MailSetupWindow.AddAccount),
        Mb.Sep(),
        Mb.Item("Quit Mail", () => ShellHost.QuitInternal(AppId), "⌘Q"),
    };

    public static object[] MenuFile() => new object[]
    {
        Mb.Item("New Message", () => MailComposeWindow.New(null), "⌘N"),
        Mb.Item("New Viewer Window", () => { MailService.Start(); var w = new MailWindow(); w.Show(); w.Activate(); }, "⌥⌘N"),
        Mb.Sep(),
        Mb.Item("Close Window", () => Front?.Close(), "⌘W"),
    };

    public static object[] MenuMailbox() => new object[]
    {
        Mb.Item("Get All New Mail", MailService.GetMail, "⇧⌘N"),
        Mb.Sep(),
        Mb.Item("Go to All Inboxes", () => Front?.ShowMailbox(AllInboxes)),
        Mb.Item("Go to Flagged", () => Front?.ShowMailbox(new Mailbox(null, null, "flagged", "Flagged"))),
    };

    public static object[] MenuMessage()
    {
        var w = Front;
        bool one = w?._list.SelectedItems.Count > 0;
        return new object[]
        {
            Mb.Item("Reply", () => w?.Reply(false), "⌘R", enabled: one),
            Mb.Item("Reply All", () => w?.Reply(true), "⇧⌘R", enabled: one),
            Mb.Item("Forward", () => w?.Forward(), "⇧⌘F", enabled: one),
            Mb.Sep(),
            Mb.Item("Mark as Read / Unread", () => w?.ToggleRead(), "⇧⌘U", enabled: one),
            Mb.Item("Flag / Unflag", () => w?.ToggleFlag(), "⇧⌘L", enabled: one),
            Mb.Sep(),
            Mb.Item("Archive", () => w?.Archive(), "⇧⌘A", enabled: one),
            Mb.Item("Move to Junk", () => w?.Junk(), "⇧⌘J", enabled: one),
            Mb.Item("Delete", () => w?.Delete(), "⌫", enabled: one),
        };
    }

    public static object[] MenuView() => new object[]
    {
        Mb.Item("Show / Hide Sidebar", () => Front?.ToggleSidebar(), "⌃⌘S"),
        Mb.Item("Show Only Unread", () => { var w = Front; if (w != null) { w._unreadOnly.IsChecked = w._unreadOnly.IsChecked != true; w.UnreadOnlyChanged(); } }),
    };
}

/// <summary>One message in the list.</summary>
public sealed class MailItem : INotifyPropertyChanged
{
    public const string DragFormat = "MacShell.MailMessages";
    public MailMessageInfo Info { get; private set; }
    bool _outgoing;
    public event PropertyChangedEventHandler PropertyChanged;

    public MailItem(MailMessageInfo m, bool outgoing) { Info = m; _outgoing = outgoing; }

    public string Key => Info.Key;

    public void Update(MailMessageInfo m, bool outgoing)
    {
        Info = m;
        _outgoing = outgoing;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>Who it's from - or, in Sent and Drafts, who it's to.</summary>
    public string Sender
    {
        get
        {
            if (_outgoing && !string.IsNullOrWhiteSpace(Info.To))
            {
                var names = Info.To.Split(',').Select(t => { int lt = t.IndexOf('<'); return (lt > 0 ? t[..lt] : t).Trim().Trim('"'); });
                return string.Join(", ", names);
            }
            return Info.Sender;
        }
    }

    public string Subject => string.IsNullOrWhiteSpace(Info.Subject) ? "(No Subject)" : Info.Subject;
    public string Preview => Info.Preview;
    public string DateText => FormatDate(Info.Date);
    public Visibility UnreadVis => Info.Seen ? Visibility.Hidden : Visibility.Visible;
    public Visibility RepliedVis => Info.Seen && Info.Answered ? Visibility.Visible : Visibility.Hidden;
    public Visibility ClipVis => Info.HasAttachments ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FlagVis => Info.Flagged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Today: the time; yesterday; this week: the day; else the date (as Mail's list shows them).</summary>
    public static string FormatDate(DateTimeOffset d)
    {
        var local = d.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString(Settings.Current.Clock24Hour ? "H:mm" : "h:mm tt");
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd");
        return local.ToString("M/d/yy");
    }

    public const string ContainerXaml = """
        <Style TargetType="ListBoxItem" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Setter Property="OverridesDefaultStyle" Value="True"/>
          <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
          <Setter Property="Foreground" Value="{DynamicResource LabelBrush}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Grid Background="Transparent">
                  <Border x:Name="sel" Margin="8,1,8,1" CornerRadius="6" Background="Transparent"/>
                  <ContentPresenter Margin="8,1,8,1"/>
                  <Border x:Name="line" Height="1" VerticalAlignment="Bottom" Margin="38,0,16,0" Background="{DynamicResource SeparatorBrush}"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="sel" Property="Background" Value="{DynamicResource UnfocusedSelectionBrush}"/>
                    <Setter TargetName="line" Property="Visibility" Value="Hidden"/>
                  </Trigger>
                  <MultiTrigger>
                    <MultiTrigger.Conditions>
                      <Condition Property="IsSelected" Value="True"/>
                      <Condition Property="Selector.IsSelectionActive" Value="True"/>
                    </MultiTrigger.Conditions>
                    <Setter TargetName="sel" Property="Background" Value="{DynamicResource SelectionBrush}"/>
                    <Setter Property="Foreground" Value="{DynamicResource SelectionTextBrush}"/>
                  </MultiTrigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    public const string TemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:c="clr-namespace:MacShell.Controls;assembly=MacShell">
          <Grid Margin="0,8,12,9">
            <Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition/></Grid.ColumnDefinitions>
            <Ellipse x:Name="dot" Width="9" Height="9" Margin="0,4,0,0" VerticalAlignment="Top" HorizontalAlignment="Center" Fill="{DynamicResource AccentBrush}" Visibility="{Binding UnreadVis}"/>
            <c:SymbolIcon Symbol="arrowshape.turn.up.left" Width="11" Height="11" StrokeWidth="2.2" Margin="0,3,0,0" VerticalAlignment="Top" HorizontalAlignment="Center" Opacity="0.5" Visibility="{Binding RepliedVis}"/>
            <StackPanel Grid.Column="1">
              <DockPanel>
                <TextBlock DockPanel.Dock="Right" Text="{Binding DateText}" FontSize="11.5" Opacity="0.55" Margin="8,1,0,0"/>
                <TextBlock Text="{Binding Sender}" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
              </DockPanel>
              <DockPanel Margin="0,1,0,0">
                <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Margin="6,0,0,0">
                  <c:SymbolIcon Symbol="paperclip" Width="12" Height="12" StrokeWidth="2" Opacity="0.55" Visibility="{Binding ClipVis}"/>
                  <c:SymbolIcon Symbol="flag.fill" Width="12" Height="12" StrokeWidth="1.6" Margin="4,0,0,0" Foreground="#FF9500" Visibility="{Binding FlagVis}"/>
                </StackPanel>
                <TextBlock Text="{Binding Subject}" FontSize="12.5" TextTrimming="CharacterEllipsis"/>
              </DockPanel>
              <TextBlock Text="{Binding Preview}" FontSize="12" Opacity="0.55" TextWrapping="Wrap" TextTrimming="CharacterEllipsis" MaxHeight="32" Margin="0,2,0,0"/>
            </StackPanel>
          </Grid>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
              <Setter TargetName="dot" Property="Fill" Value="White"/>
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """;
}
