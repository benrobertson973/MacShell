using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MacShell.Services;

namespace MacShell.Controls;

/// <summary>Tiny builder for macOS-styled menus.</summary>
public static class Mb
{
    public static MenuItem Item(string header, Action click = null, string gesture = null, bool enabled = true, bool isChecked = false, object icon = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, IsChecked = isChecked, Icon = icon };
        if (click != null) mi.Click += (_, e) => { e.Handled = true; click(); };
        return mi;
    }

    public static MenuItem Sub(string header, params object[] children)
    {
        var mi = new MenuItem { Header = header };
        foreach (var c in children) Add(mi.Items, c);
        return mi;
    }

    /// <summary>Submenu whose items are built on demand when it opens.</summary>
    public static MenuItem LazySub(string header, Func<IEnumerable<object>> build)
    {
        var mi = new MenuItem { Header = header };
        mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
        mi.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != mi) return;
            mi.Items.Clear();
            foreach (var c in build()) Add(mi.Items, c);
            if (mi.Items.Count == 0) mi.Items.Add(new MenuItem { Header = "No Items", IsEnabled = false });
        };
        return mi;
    }

    public static Separator Sep() => new();

    public static MenuItem SectionHeader(string text) => new() { Header = text, IsEnabled = false };

    public static ContextMenu Context(params object[] items)
    {
        var cm = new ContextMenu();
        foreach (var c in items) Add(cm.Items, c);
        return cm;
    }

    public static void Add(ItemCollection items, object c)
    {
        switch (c)
        {
            case null: break;
            case IEnumerable<object> many: foreach (var m in many) Add(items, m); break;
            case Separator s:
                if (items.Count > 0 && items[^1] is not Separator) items.Add(s);
                break;
            default: items.Add(c); break;
        }
    }

    /// <summary>The row of seven Finder tag colour dots used in Finder's context menu.</summary>
    public static MenuItem TagRow(IList<string> current, Action<string> toggle)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-12, 2, 0, 2) };
        var mi = new MenuItem { Header = panel, StaysOpenOnClick = true };
        foreach (var (id, name, color) in Theme.TagColors)
        {
            bool on = current?.Contains(id) == true;
            var dot = new Grid { Width = 17, Height = 17, Margin = new Thickness(0, 0, 5, 0), Background = Brushes.Transparent, ToolTip = name, Cursor = System.Windows.Input.Cursors.Arrow };
            dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Theme.Darken(color, 0.15)), StrokeThickness = 0.5 });
            if (on) dot.Children.Add(new SymbolIcon { Symbol = "checkmark", Width = 10, Height = 10, StrokeWidth = 3, Foreground = Brushes.White });
            string tagId = id;
            dot.MouseEnter += (_, _) => dot.RenderTransform = new ScaleTransform(1.15, 1.15, 8.5, 8.5);
            dot.MouseLeave += (_, _) => dot.RenderTransform = null;
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                toggle(tagId);
                if (mi.Parent is ContextMenu cm) cm.IsOpen = false;
                else if (mi.Parent is MenuItem pm) pm.IsSubmenuOpen = false;
            };
            panel.Children.Add(dot);
        }
        mi.Template = (ControlTemplate)Application.Current.Resources["MacSubmenuItem"];
        return mi;
    }
}
