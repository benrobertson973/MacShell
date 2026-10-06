using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MacShell.Native;

namespace MacShell.Controls;

/// <summary>
/// Keeps every menu between the menu bar and the Dock, like macOS: WPF only keeps popups on the screen, so a menu
/// opened near the top slid under the menu bar and one near the bottom ran behind the Dock. When a context menu or
/// a submenu opens, its visible card is measured against the monitor's work area and placed back inside it (at an
/// absolute position, restored when it closes); a menu taller than the space scrolls. Menus hanging from the menu
/// bar, and menus their owner placed itself (a Dock icon's, just above the icon), are only shortened, never moved.
/// </summary>
public static class MenuFit
{
    sealed class Saved { public PlacementMode Placement; public double H, V; public Rect R; public bool Moved; }
    static readonly ConditionalWeakTable<DependencyObject, Saved> State = new();

    /// <summary>Where a menu is held: nowhere (it can be moved), from its top (the menu bar's menus) or from its bottom
    /// (a Dock icon's menu, which rises from just above the icon).</summary>
    enum Hang { Free, FromTop, FromBottom }

    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) =>
        {
            if (e.OriginalSource != s) return;
            var cm = (ContextMenu)s;
            var st = State.GetValue(cm, _ => new Saved());
            if (!st.Moved) { st.Placement = cm.Placement; st.H = cm.HorizontalOffset; st.V = cm.VerticalOffset; st.R = cm.PlacementRectangle; }
            var hang = st.Placement == PlacementMode.Custom ? Hang.FromBottom : Hang.Free;
            cm.Dispatcher.BeginInvoke(() => Fit(cm, cm, (x, y) =>
            {
                st.Moved = true;
                cm.Placement = PlacementMode.Absolute;
                cm.PlacementRectangle = Rect.Empty;   // (Absolute would add a placement rectangle's position to the screen's)
                cm.HorizontalOffset = x;
                cm.VerticalOffset = y;
            }, hang), DispatcherPriority.Loaded);
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, e) =>
        {
            if (e.OriginalSource != s || !State.TryGetValue((ContextMenu)s, out var st) || !st.Moved) return;
            var cm = (ContextMenu)s;
            st.Moved = false;
            cm.Placement = st.Placement;
            cm.PlacementRectangle = st.R;
            cm.HorizontalOffset = st.H;
            cm.VerticalOffset = st.V;
        }));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) =>
        {
            if (e.OriginalSource != s) return;
            var mi = (MenuItem)s;
            if (mi.Template?.FindName("PART_Popup", mi) is not Popup p || p.Child is not FrameworkElement root) return;
            var st = State.GetValue(p, _ =>
            {
                var n = new Saved();
                p.Closed += (_, _) =>
                {
                    if (!n.Moved) return;
                    n.Moved = false;
                    p.Placement = n.Placement;
                    p.PlacementRectangle = n.R;
                    p.HorizontalOffset = n.H;
                    p.VerticalOffset = n.V;
                };
                return n;
            });
            if (!st.Moved) { st.Placement = p.Placement; st.H = p.HorizontalOffset; st.V = p.VerticalOffset; st.R = p.PlacementRectangle; }
            bool bar = mi.Role == MenuItemRole.TopLevelHeader && mi.Parent is Menu;
            mi.Dispatcher.BeginInvoke(() => Fit(root, root, (x, y) =>
            {
                st.Moved = true;
                p.Placement = PlacementMode.Absolute;
                p.PlacementRectangle = Rect.Empty;
                p.HorizontalOffset = x;
                p.VerticalOffset = y;
            }, bar ? Hang.FromTop : Hang.Free), DispatcherPriority.Loaded);
        }));
    }

    /// <summary>The menu's visible card (the border with the shadow); the rest of the popup is transparent margin.</summary>
    static Border Card(DependencyObject d)
    {
        if (d is Border b && b.Effect != null) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (Card(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    /// <summary>Diagnostics: the visible card's screen rectangle in pixels.</summary>
    public static Rect VisibleRect(Visual root)
    {
        var card = Card(root);
        if (card == null || PresentationSource.FromVisual(card) == null) return Rect.Empty;
        double s = VisualTreeHelper.GetDpi(card).DpiScaleY;
        var tl = card.PointToScreen(new Point(0, 0));
        return new Rect(tl.X, tl.Y, card.ActualWidth * s, card.ActualHeight * s);
    }

    static ScrollViewer Scroller(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (Scroller(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    /// <param name="moveTo">the popup's new top-left, in device-independent screen units (Absolute placement)</param>
    static void Fit(FrameworkElement popupRoot, Visual searchFrom, Action<double, double> moveTo, Hang hang)
    {
        try
        {
            var card = Card(searchFrom);
            if (card == null || PresentationSource.FromVisual(card) == null || card.ActualHeight <= 0) return;
            double s = VisualTreeHelper.GetDpi(card).DpiScaleY;
            var tl = card.PointToScreen(new Point(0, 0));
            var mon = NativeMethods.MonitorFromPoint(new POINT { X = (int)tl.X + 4, Y = (int)tl.Y + 4 }, 2);
            var work = NativeMethods.GetMonitorInfo(mon).rcWork;
            const double gap = 4;   // pixels kept clear of the menu bar and the Dock
            // never taller than the space it has
            var sv = Scroller(card);
            if (sv != null)
            {
                double chrome = card.ActualHeight - sv.ActualHeight;
                double room = hang switch
                {
                    Hang.FromTop => work.Bottom - gap - tl.Y,
                    Hang.FromBottom => tl.Y + card.ActualHeight * s - (work.Top + gap),
                    _ => work.Bottom - work.Top - 2 * gap,
                } / s - chrome;
                if (sv.ActualHeight > room) { sv.MaxHeight = Math.Max(60, room); card.UpdateLayout(); }
            }
            if (hang != Hang.Free) return;
            tl = card.PointToScreen(new Point(0, 0));
            double h = card.ActualHeight * s;
            double top = Math.Max(work.Top + gap, Math.Min(tl.Y, work.Bottom - gap - h));
            if (Math.Abs(top - tl.Y) < 1) return;
            // move the whole popup by the same amount (the card sits inside a transparent margin)
            var root = popupRoot.PointToScreen(new Point(0, 0));
            moveTo(root.X / s, (root.Y + top - tl.Y) / s);
        }
        catch { }
    }
}
