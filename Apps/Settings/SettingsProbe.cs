using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacShell.Services;

namespace MacShell.Apps;

/// <summary>
/// The settings probe (MacShell.exe --settings-probe &lt;folder&gt; [--dark] [--page &lt;id&gt;]): every page of System Settings,
/// and the pages they open, built with the computer's real data in a window nobody sees, saved as PNGs, with a list
/// of each page's controls (what they're set to, and the ones that do nothing). Nothing is saved or changed.
/// </summary>
public partial class SettingsWindow
{
    public static void Probe(string dir, bool dark, string only)
    {
        Directory.CreateDirectory(dir);
        Settings.Load();
        Settings.ReadOnly = true;
        Settings.Current.Appearance = dark ? "dark" : "light";
        Theme.Apply();
        Theme.ApplyScrollBars();
        ShellHost.Offscreen = true;
        try { Wallpaper.Load(); } catch { }
        LoginItem.ReadState();
        bool catalog = false;
        AppCatalog.Loaded += () => catalog = true;
        _ = AppCatalog.LoadAsync();
        Pump(() => catalog, 15000);

        if (only == "wifimenu")
        {
            // the menu bar's Wi-Fi menu (folded and unfolded) and the password windows, off-screen; nothing is joined
            string sfx = dark ? "-dark" : "";
            Shell.WifiPopover.Toggle(null);
            Pump(() => false, 3000);
            if (Shell.WifiPopover.Current is { } pop)
            {
                Render(pop, Path.Combine(dir, "wifimenu" + sfx + ".png"));
                pop.TestExpand();
                Pump(() => false, 300);
                Render(pop, Path.Combine(dir, "wifimenu-others" + sfx + ".png"));
                pop.Close();
            }
            Shell.WifiJoinDialog.ShowForTest("Coffee Shop");
            Pump(() => false, 400);
            if (Shell.WifiJoinDialog.Current is { } jd) { Render(jd, Path.Combine(dir, "wifijoin" + sfx + ".png")); jd.Close(); }
            Shell.WifiJoinDialog.ShowHidden();
            Pump(() => false, 400);
            if (Shell.WifiJoinDialog.Current is { } hd) { Render(hd, Path.Combine(dir, "wifiother" + sfx + ".png")); hd.Close(); }
            return;
        }

        var w = new SettingsWindow { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        w.SourceInitialized += (_, _) => Native.NativeMethods.AddExStyle(new WindowInteropHelper(w).Handle, Native.NativeMethods.WS_EX_TOOLWINDOW);
        w.Show();
        w.Left = -20000;
        Pump(() => false, 300);

        var summary = new StringBuilder();
        var queue = new List<string>();
        if (only != null) queue.Add(only);
        else queue.AddRange(VisiblePanes().Where(p => p.id != "-").Select(p => p.id));
        var seen = new HashSet<string>(queue);
        var subCount = new Dictionary<string, int>();
        for (int i = 0; i < queue.Count; i++)
        {
            string page = queue[i];
            var sw = Stopwatch.StartNew();
            w.Open(page);
            Pump(() => w._pending == 0, 15000);
            Pump(() => false, 250);
            string file = Safe(page) + (dark ? "-dark" : "");
            Render(w._content, Path.Combine(dir, file + ".png"));
            var lines = new List<string>();
            var links = new List<string>();
            Describe(w._content, lines, links, 0);
            bool broken = lines.Any(l => l.Contains("couldn’t be shown"));
            File.WriteAllLines(Path.Combine(dir, file + ".txt"), new[] { $"# {page}  “{w._paneTitle.Text}”  ({sw.ElapsedMilliseconds} ms)" }.Concat(lines));
            summary.AppendLine($"{(broken ? "ERROR " : "")}{page} “{w._paneTitle.Text}”: {lines.Count} lines, {sw.ElapsedMilliseconds} ms, links {links.Count}");
            if (only != null) continue;
            foreach (var l in links)
            {
                string pane = l.Split('/')[0];
                subCount.TryGetValue(pane, out int n);
                if (n >= 8 || !seen.Add(l)) continue;   // (a pane's first pages are enough: Notifications has one per app)
                subCount[pane] = n + 1;
                queue.Add(l);
            }
        }
        // the sidebar, once
        if (w.Content is Grid layout && layout.Children[0] is FrameworkElement side) Render(side, Path.Combine(dir, "_sidebar" + (dark ? "-dark" : "") + ".png"));
        File.WriteAllText(Path.Combine(dir, "_summary" + (dark ? "-dark" : "") + ".txt"), summary.ToString());
        w.Close();
    }

    static string Safe(string page) => string.Concat(page.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    static void Pump(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var f = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => f.Continue = false));
            Dispatcher.PushFrame(f);
            if (done() || sw.ElapsedMilliseconds > timeoutMs) break;
            Thread.Sleep(15);
        }
    }

    static void Render(FrameworkElement fe, string path)
    {
        fe.UpdateLayout();
        double w = Math.Max(1, fe.ActualWidth), h = Math.Max(1, fe.ActualHeight);
        const double s = 1.25;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Theme.Res("SettingsPaneBrush"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)(w * s), (int)(h * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>The page's controls, one a line: what each is, its row's name and its value; pages it opens go in links.</summary>
    static void Describe(DependencyObject d, List<string> lines, List<string> links, int depth)
    {
        switch (d)
        {
            case TextBlock tb when tb.Text?.Contains("couldn’t be shown") == true:
                lines.Add("PAGE ERROR: " + tb.Text);
                break;
            case CheckBox cb:
                lines.Add($"{(cb.Style == Application.Current.Resources["MacSwitch"] ? "switch" : "checkbox")} [{AutomationProperties.GetName(cb)}] = {(cb.IsChecked == true ? "on" : "off")}{(cb.IsEnabled ? "" : " DISABLED")}{(HasClick(cb) ? "" : " NO-HANDLER")}");
                return;
            case Button b:
                {
                    string status = AutomationProperties.GetItemStatus(b);
                    string help = AutomationProperties.GetHelpText(b);
                    string text = AutomationProperties.GetName(b);
                    if (string.IsNullOrEmpty(text)) text = Flatten(b.Content);
                    lines.Add($"button [{text}]{(string.IsNullOrEmpty(status) ? "" : " " + status)}{(string.IsNullOrEmpty(help) ? "" : " " + help)}{(b.IsEnabled ? "" : " DISABLED")}{(HasClick(b) ? "" : " NO-HANDLER")}");
                    return;
                }
            case Slider s:
                lines.Add($"slider [{AutomationProperties.GetName(s)}] = {s.Value:0.##} ({s.Minimum:0.##}…{s.Maximum:0.##})");
                return;
            case TextBox t when !t.IsReadOnly:
                lines.Add($"field [{AutomationProperties.GetName(t)}] = “{t.Text}”");
                return;
            case Border bd when AutomationProperties.GetHelpText(bd) is { Length: > 0 } help && help.StartsWith("click: "):
                lines.Add($"row-link [{FirstText(bd)}] {help}");
                if (help.StartsWith("click: page ")) links.Add(help["click: page ".Length..]);
                break;
            case FrameworkElement fe when AutomationProperties.GetItemStatus(fe) is { Length: > 0 } st:
                lines.Add($"{st} [{AutomationProperties.GetName(fe)}]");
                break;
        }
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Describe(VisualTreeHelper.GetChild(d, i), lines, links, depth + 1);
    }

    static string FirstText(DependencyObject d)
    {
        if (d is TextBlock t && !string.IsNullOrEmpty(t.Text)) return t.Text;
        if (d is not Visual) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var r = FirstText(VisualTreeHelper.GetChild(d, i)); if (r != null) return r; }
        return null;
    }

    static string Flatten(object content) => content switch
    {
        string s => s,
        TextBlock t => t.Text,
        Panel p => string.Join(" ", p.Children.OfType<object>().Select(Flatten).Where(x => !string.IsNullOrEmpty(x))),
        _ => content?.GetType().Name,
    };

    /// <summary>The control has a Click handler (a button that does something).</summary>
    static bool HasClick(ButtonBase b)
    {
        try
        {
            var store = typeof(UIElement).GetProperty("EventHandlersStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(b);
            if (store == null) return b.Command != null;
            var get = store.GetType().GetMethod("GetRoutedEventHandlers");
            var handlers = get?.Invoke(store, new object[] { ButtonBase.ClickEvent }) as Array;
            return handlers?.Length > 0 || b.Command != null;
        }
        catch { return true; }
    }
}
