using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Shell;

/// <summary>
/// The bars probe (MacShell.exe --bars-probe &lt;folder&gt;): a menu bar, a Dock and a second display's desktop on made-up
/// displays far off-screen, put through what a new resolution or another display does - the main display's size and
/// scale change, and only later does Windows tell the windows their new scale (WM_DPICHANGED, sent here by hand). After
/// each step their rectangles are checked against where they belong, in pixels. Nothing is saved; results in
/// bars-probe.txt.
/// </summary>
public static class BarsProbe
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        Settings.Load();
        Settings.ReadOnly = true;
        Finder.Sound.Muted = true;
        Theme.Apply();

        const int X = -20000;   // (the made-up displays: far to the left of any real one)
        ShellHost.TestScreen = (new RECT(X, 0, X + 1920, 1080), 1.25);
        ShellHost.Remeasure();
        var bar = new MenuBarWindow();
        bar.Show();
        var dock = new DockWindow();
        dock.Show();
        // (what ShellHost does when a scale change reaches them: everything measured and put in place again)
        void Relayout() { ShellHost.Remeasure(); bar.Reposition(); dock.Reposition(); }
        bar.DpiChanged += (_, _) => bar.Dispatcher.BeginInvoke(Relayout);
        dock.DpiChanged += (_, _) => dock.Dispatcher.BeginInvoke(Relayout);
        Pump(600);

        int failures = 0;
        void Check(string what)
        {
            var s = ShellHost.ScreenPx;
            double k = ShellHost.Scale;
            var wantBar = new RECT(s.Left, s.Top, s.Right, s.Top + (int)Math.Round(ShellHost.MenuBarHeight * k));
            var wantDock = new RECT(s.Left, s.Bottom - (int)Math.Round(dock.WindowHeight * k), s.Right, s.Bottom);
            GetWindowRect(new WindowInteropHelper(bar).Handle, out var gotBar);
            GetWindowRect(new WindowInteropHelper(dock).Handle, out var gotDock);
            log.AppendLine($"{what}: main display {s.Right - s.Left}x{s.Bottom - s.Top} at {k * 100:0}%, the windows drawn at {ShellHost.WindowScale(dock) * 100:0}%");
            log.AppendLine($"  menu bar {Compare(gotBar, wantBar, ref failures)}");
            log.AppendLine($"  Dock     {Compare(gotDock, wantDock, ref failures)}");
        }

        Relayout(); Pump(300);
        Check("as started");

        // another display becomes the main one: 1600x900 at 150%; the windows hear of its scale only later
        // (the made-up displays stay within the real screen's size: Windows won't make a window bigger than all of them)
        ShellHost.TestScreen = (new RECT(X, 0, X + 1600, 900), 1.5);
        Relayout(); Pump(300);
        Check("switched to a 1600x900 display at 150%, before its scale reaches the windows");
        DpiTo(bar, 144); DpiTo(dock, 144); Pump(700);
        Check("  then its scale reached them");

        // a lower resolution at 100%
        ShellHost.TestScreen = (new RECT(X, 0, X + 1280, 720), 1.0);
        Relayout(); Pump(300);
        Check("resolution lowered to 1280x720 at 100%, before its scale reaches the windows");
        DpiTo(bar, 96); DpiTo(dock, 96); Pump(700);
        Check("  then its scale reached them");

        // back to a 1920x1080 display at 125%, its scale arriving first this time
        ShellHost.TestScreen = (new RECT(X, 0, X + 1920, 1080), 1.25);
        DpiTo(bar, 120); DpiTo(dock, 120); Pump(700);
        Check("back to 1920x1080 at 125%, its scale arriving first");

        // a second display's desktop told it has a new scale: it keeps covering its display exactly
        var other = new RECT(X + 4000, 0, X + 4000 + 1600, 900);
        var desk = new DesktopWindow(other, primary: false);
        desk.Show();
        Pump(400);
        DpiTo(desk, 144); Pump(700);
        GetWindowRect(new WindowInteropHelper(desk).Handle, out var gotDesk);
        log.AppendLine($"second display's desktop after a scale change: {Compare(gotDesk, other, ref failures)}");

        log.AppendLine(failures == 0 ? "ALL IN PLACE" : $"{failures} OUT OF PLACE");
        desk.Close();
        bar.Close();
        dock.Close();
        Pump(200);
        File.WriteAllText(Path.Combine(dir, "bars-probe.txt"), log.ToString());
    }

    static string Compare(RECT got, RECT want, ref int failures)
    {
        bool ok = Math.Abs(got.Left - want.Left) <= 1 && Math.Abs(got.Top - want.Top) <= 1 && Math.Abs(got.Right - want.Right) <= 1 && Math.Abs(got.Bottom - want.Bottom) <= 1;
        if (!ok) failures++;
        return $"{(ok ? "in place" : "OUT OF PLACE")}: at {R(got)}, belongs at {R(want)}";
    }

    static string R(RECT r) => $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}";

    /// <summary>What Windows sends a window whose display's scale changed (or that moved to a display with another
    /// scale): the new DPI and a suggested rectangle - its own, grown or shrunk by the change from its top-left corner.</summary>
    static void DpiTo(Window w, int dpi)
    {
        var h = new WindowInteropHelper(w).Handle;
        GetWindowRect(h, out var r);
        double f = dpi / 96.0 / ShellHost.WindowScale(w);
        var suggested = new RECT(r.Left, r.Top, r.Left + (int)Math.Round((r.Right - r.Left) * f), r.Top + (int)Math.Round((r.Bottom - r.Top) * f));
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(suggested, p, false);
            SendMessage(h, WM_DPICHANGED, (IntPtr)(dpi << 16 | dpi), p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var f = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => f.Continue = false));
            Dispatcher.PushFrame(f);
            Thread.Sleep(15);
        }
    }
}
