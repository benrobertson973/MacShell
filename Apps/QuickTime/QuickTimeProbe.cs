using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Apps.QuickTime;

/// <summary>
/// The QuickTime probe (MacShell.exe --qt-probe &lt;folder&gt;): silent and off-screen. Test movies are written (one plain,
/// one that says it was filmed sideways), played in QuickTime Player windows and checked by their pixels; the screenshot
/// toolbar and a selection are drawn off-screen as PNGs; a short recording of a small corner of the screen is made both
/// ways (Desktop Duplication and GDI) and only measured - its file is deleted. Results in qt-probe.txt.
/// </summary>
public static class QuickTimeProbe
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        Settings.Load();
        Settings.ReadOnly = true;
        Settings.Current.Appearance = "light";
        Settings.Current.QuickTimeTurns = new();
        Theme.Apply();
        ShellHost.Offscreen = true;
        Finder.Sound.Muted = true;
        QuickTimeWindow.Silent = true;

        // ---- test movies
        string plain = Path.Combine(dir, "probe-plain.mp4"), turned = Path.Combine(dir, "probe-turned.mp4");
        foreach (var (file, rot) in new[] { (plain, 0), (turned, 90) })
        {
            try
            {
                var sw = Stopwatch.StartNew();
                MakeMovie(file, rot);
                log.AppendLine($"movie {Path.GetFileName(file)}: {new FileInfo(file).Length:N0} bytes in {sw.ElapsedMilliseconds} ms, orientation read back {MovieInspector.Orientation(file)}, {Props(file)}");
            }
            catch (Exception ex) { log.AppendLine($"movie {Path.GetFileName(file)}: FAILED {ex.Message}"); }
        }
        // the same, written as slowly as a recording is (a frame every 33 ms, 37 frames)
        string paced = Path.Combine(dir, "probe-paced.mp4");
        try
        {
            MakeMovie(paced, 0, frames: 37, paceMs: 33);
            log.AppendLine($"movie {Path.GetFileName(paced)}: {Props(paced)}");
        }
        catch (Exception ex) { log.AppendLine($"movie paced: FAILED {ex.Message}"); }

        // ---- QuickTime Player on them
        foreach (var file in new[] { plain, turned, paced })
        {
            if (!File.Exists(file)) continue;
            var w = QuickTimeWindow.Open(file, silent: true);
            Hide(w);
            WaitFor(() => !QuickTimeWindow.TestState().Contains("dur=-1"), 6000);
            if (file == paced)
            {
                Pump(800);
                log.AppendLine($"{Path.GetFileName(file)}: {QuickTimeWindow.TestState()}");
                w.Close();
                Pump(200);
                continue;
            }
            QuickTimeWindow.Test("pause");
            QuickTimeWindow.Test("seek");
            Pump(600);
            log.AppendLine($"{Path.GetFileName(file)}: {QuickTimeWindow.TestState()}");
            var shot = Render((FrameworkElement)w.Content, Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".png"));
            log.AppendLine($"  picture: {Describe(shot)}");
            if (file == plain)
            {
                // Edit › Rotate Right, then back; Flip Horizontal, then back
                QuickTimeWindow.Test("rotate");
                Pump(500);
                log.AppendLine($"  rotated: {QuickTimeWindow.TestState()}");
                log.AppendLine($"  rotated picture: {Describe(Render((FrameworkElement)w.Content, Path.Combine(dir, "probe-plain-rotated.png")))}");
                for (int i = 0; i < 3; i++) QuickTimeWindow.Test("rotate");
                QuickTimeWindow.Test("flip");
                Pump(400);
                log.AppendLine($"  flipped picture: {Describe(Render((FrameworkElement)w.Content, Path.Combine(dir, "probe-plain-flipped.png")))}");
                QuickTimeWindow.Test("flip");
                QuickTimeWindow.Test("bigger");
                Pump(200);
                log.AppendLine($"  bigger: {QuickTimeWindow.TestState()}");
                QuickTimeWindow.Test("controls");
                Pump(400);
                Render((FrameworkElement)w.Content, Path.Combine(dir, "probe-hud.png"));
            }
            w.Close();
            Pump(200);
        }

        // ---- the toolbar and a selection, off-screen
        try
        {
            var tb = new CaptureToolbar();
            tb.Show();
            tb.ShowMode(ScreenRecorder.Mode.RecordPortion);
            Pump(400);
            Render(tb, Path.Combine(dir, "toolbar.png"), Color.FromRgb(0x6A, 0x7F, 0x9C));
            tb.Close();
            var fake = new RECT(-24000, 100, -24000 + 900, 100 + 560);
            ScreenRecorder.SetSelectionLive(new RECT(fake.Left + 220, fake.Top + 140, fake.Left + 680, fake.Top + 420));
            var ov = new SelectionOverlay(fake);
            ov.Show();
            Pump(300);
            ov.Redraw();
            Pump(200);
            Render((FrameworkElement)ov.Content, Path.Combine(dir, "selection.png"), Color.FromRgb(0x8F, 0xB6, 0xD8));
            ov.Close();
            log.AppendLine($"camera pointer: {(ScreenRecorder.CameraCursor() != System.Windows.Input.Cursors.Cross ? "made" : "FAILED (cross)")}");
        }
        catch (Exception ex) { log.AppendLine("toolbar: FAILED " + ex); }

        // ---- Desktop Duplication against GDI, on a small corner of the main display (numbers only)
        var m = NativeMethods.GetMonitors().OrderByDescending(x => x.primary).First().bounds;
        var corner = new RECT(m.Left + 40, m.Top + 60, m.Left + 40 + 320, m.Top + 60 + 200);
        try
        {
            using var d = new DesktopDuplication(corner);
            for (int i = 0; i < 8; i++) d.Update(40);
            var a = new uint[corner.Width * corner.Height];
            unsafe { fixed (uint* p = a) d.CopyTo((IntPtr)p, corner.Width * 4); }
            var b = GdiScreenGrabber.Shot(corner);
            long diff = 0, same = 0;
            for (int i = 0; i < a.Length; i++) { if ((a[i] & 0xFFFFFF) == (b[i] & 0xFFFFFF)) same++; diff += Math.Abs((int)(a[i] & 0xFF) - (int)(b[i] & 0xFF)); }
            log.AppendLine($"duplication vs GDI: {d.Method}, {100.0 * same / a.Length:0.0}% pixels identical, mean blue difference {diff / (double)a.Length:0.00}");
        }
        catch (Exception ex) { log.AppendLine("duplication vs GDI: FAILED " + ex.Message); }

        // ---- short recordings, both ways
        foreach (bool gdi in new[] { false, true })
        {
            string rec = Path.Combine(dir, gdi ? "probe-rec-gdi.mp4" : "probe-rec.mp4");
            ScreenRecorder.ForceGdi = gdi;
            string result = ScreenRecorder.TestRecord(corner, rec, 1500);
            log.AppendLine($"recording ({(gdi ? "GDI" : "duplication")}): {result}; file: {(File.Exists(rec) ? Props(rec) : "none")}");
            if (File.Exists(rec))
            {
                var w = QuickTimeWindow.Open(rec, silent: true);
                Hide(w);
                WaitFor(() => QuickTimeWindow.TestState().Contains("dur=") && !QuickTimeWindow.TestState().Contains("dur=-1"), 6000);
                Pump(300);
                log.AppendLine($"  played back: {QuickTimeWindow.TestState()}");
                w.Close();
                Pump(300);
                if (Environment.GetEnvironmentVariable("QT_PROBE_KEEP") != "1")
                    try { File.Delete(rec); } catch (Exception ex) { log.AppendLine("  (not deleted: " + ex.Message + ")"); }
            }
        }
        ScreenRecorder.ForceGdi = false;

        // ---- (asked for) the whole main display for 3 s: the speed at full size - measured, then deleted
        if (Environment.GetEnvironmentVariable("QT_PROBE_FULL") == "1")
        {
            string rec = Path.Combine(dir, "probe-rec-full.mp4");
            var full = new RECT(m.Left, m.Top, m.Left + (m.Width & ~1), m.Top + (m.Height & ~1));
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            string result = ScreenRecorder.TestRecord(full, rec, 3000);
            double cpuPct = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            log.AppendLine($"full display {full.Width}x{full.Height}: {result}; file: {(File.Exists(rec) ? $"{new FileInfo(rec).Length / 1024:N0} KB, " + Props(rec) : "none")}; CPU {cpuPct:0}% of the computer");
            try { File.Delete(rec); } catch { }
        }

        File.WriteAllText(Path.Combine(dir, "qt-probe.txt"), log.ToString());
    }

    static void Hide(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        NativeMethods.AddExStyle(h, NativeMethods.WS_EX_TOOLWINDOW);
        w.Left = -20000;
    }

    /// <summary>320×180 at 30 fps (2 s): a white band along the top, red on the left, blue on the right, a green bar moving.</summary>
    static unsafe void MakeMovie(string file, int rotation, int frames = 60, int paceMs = 0)
    {
        const int W = 320, H = 180;
        if (File.Exists(file)) File.Delete(file);
        using var mp4 = new Mp4Writer(file, W, H, 30, 1_000_000, rotation);
        var px = new uint[W * H];
        for (int f = 0; f < frames; f++)
        {
            int bar = f * 5 % W;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    px[y * W + x] = y < 30 ? 0xFFFFFFFF : Math.Abs(x - bar) < 4 ? 0xFF00C000 : x < W / 2 ? 0xFFE00000 : 0xFF0000E0;
            fixed (uint* p = px) mp4.WriteFrame((IntPtr)p, W * 4, f * 10_000_000L / 30);
            if (paceMs > 0) Thread.Sleep(paceMs);
        }
        mp4.Finish();
    }

    /// <summary>What Windows reads from a movie file: its length, frame rate and frame size.</summary>
    static string Props(string file)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic item = shell.NameSpace(Path.GetDirectoryName(file))?.ParseName(Path.GetFileName(file));
            object P(string n) { try { return item?.ExtendedProperty(n); } catch { return null; } }
            object dur = P("System.Media.Duration"), fps = P("System.Video.FrameRate"), fw = P("System.Video.FrameWidth"), fh = P("System.Video.FrameHeight");
            double secs = dur is ulong u ? u / 1e7 : -1;
            return $"length {secs:0.000}s, {(fps is uint r ? r / 1000.0 : -1):0.##} fps, {fw}x{fh}";
        }
        catch (Exception ex) { return "properties: " + ex.Message; }
    }

    /// <summary>The colour at a few spots of a picture: where the white band, the red and the blue came out.</summary>
    static string Describe(BitmapSource bmp)
    {
        if (bmp == null) return "none";
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        string At(double fx, double fy)
        {
            int x = Math.Clamp((int)(fx * w), 0, w - 1), y = Math.Clamp((int)(fy * h), 0, h - 1), i = (y * w + x) * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            string name = r > 200 && g > 200 && b > 200 ? "white" : r > 150 && g < 90 && b < 90 ? "red" : b > 150 && r < 90 && g < 90 ? "blue" : g > 120 && r < 90 && b < 90 ? "green" : r < 30 && g < 30 && b < 30 ? "black" : $"#{r:X2}{g:X2}{b:X2}";
            return name;
        }
        return $"{w}x{h} top={At(0.5, 0.06)} left={At(0.15, 0.6)} right={At(0.85, 0.6)} bottom={At(0.5, 0.94)} leftEdge={At(0.04, 0.5)} rightEdge={At(0.96, 0.5)}";
    }

    static void WaitFor(Func<bool> ok, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!ok() && sw.ElapsedMilliseconds < ms) Pump(100);
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

    static BitmapSource Render(FrameworkElement fe, string path, Color? back = null)
    {
        fe.UpdateLayout();
        double w = Math.Max(1, fe.ActualWidth), h = Math.Max(1, fe.ActualHeight);
        const double s = 1.25;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (back is Color c) dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)(w * s), (int)(h * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        return rtb;
    }
}
