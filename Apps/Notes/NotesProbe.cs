using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacShell.Services;

namespace MacShell.Apps.Notes;

/// <summary>
/// The notes probe (MacShell.exe --notes-probe &lt;folder&gt; [--dark]): Notes run offscreen on a library of its own in that
/// folder - never the real one - with sample notes; the window and the Aa menu saved as PNGs; a note read back after
/// editing, through .enex (out and in), HTML and Markdown, checked against itself. Results in notes-probe.txt.
/// </summary>
public static class NotesProbe
{
    public static void Run(string dir, bool dark)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        Settings.Load();
        Settings.ReadOnly = true;
        Settings.Current.Appearance = dark ? "dark" : "light";
        Settings.Current.NotesWindowBounds = null;
        Theme.Apply();
        ShellHost.Offscreen = true;
        string lib = Path.Combine(dir, "library");
        if (Directory.Exists(lib)) Directory.Delete(lib, true);
        NotesLibrary.Dir = lib;
        NotesLibrary.Load();
        // a picture for the notes
        string pic = Path.Combine(dir, "probe-picture.png");
        MakePicture(pic);

        NotesWindow.OpenApp();
        var w = NotesWindow.Instance;
        new WindowInteropHelper(w).EnsureHandle();
        Native.NativeMethods.AddExStyle(new WindowInteropHelper(w).Handle, Native.NativeMethods.WS_EX_TOOLWINDOW);
        w.Left = -20000;
        Pump(400);
        NotesWindow.Test("sample");
        Pump(300);
        var editor = (NoteEditor)typeof(NotesWindow).GetField("_editor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w);
        editor.InsertImageFiles(new[] { pic });
        Pump(400);
        // a second folder and a few more notes, for the list and the sidebar
        var f = NotesLibrary.AddFolder("Recipes");
        NotesLibrary.Create(f.Id, NoteDoc.FromText("Pancakes\nFlour, eggs, milk"), DateTime.Now.AddDays(-3), DateTime.Now.AddDays(-3));
        NotesLibrary.Create(NotesLibrary.DefaultFolder, NoteDoc.FromText("Old idea\nSomething from last month"), DateTime.Now.AddDays(-20), DateTime.Now.AddDays(-20));
        w.Reload();
        Pump(500);
        Render((FrameworkElement)w.Content, Path.Combine(dir, $"notes{(dark ? "-dark" : "")}.png"));

        // the editor's note, read back
        var doc = editor.Read();
        log.AppendLine("blocks read back: " + string.Join(" | ", doc.Blocks.Select(b => b.Image != null ? "[image]" : $"{b.Style}{(b.List != null ? "/" + b.List + (b.Checked ? "✓" : "") : "")}{(b.Indent > 0 ? ">" + b.Indent : "")}{(b.Quote ? "/quote" : "")}:{string.Concat(b.Runs.Select(r => (r.B ? "*" : "") + (r.I ? "_" : "") + (r.S ? "~" : "") + (r.U ? "+" : "") + (r.Link != null ? "@" : "") + r.T))}")));

        // the Aa menu
        NotesWindow.Test("format");
        Pump(400);
        var popup = w.FormatPopup;
        if (popup?.Child is FrameworkElement card) Render(card, Path.Combine(dir, $"format{(dark ? "-dark" : "")}.png"));
        if (popup != null) popup.IsOpen = false;

        // through .enex and back
        var note = NotesLibrary.Notes.First(n => n.Title.StartsWith("Groceries"));
        NotesLibrary.SaveDoc(note, doc);
        string enex = Path.Combine(dir, "probe.enex");
        NotesExchange.WriteEnex(new[] { note }, enex);
        var imported = NotesExchange.ImportFiles(new[] { enex }, NotesLibrary.DefaultFolder);
        var back = NotesLibrary.Notes.OrderByDescending(n => n.Created).First(n => n.Id != note.Id && n.Title.StartsWith("Groceries"));
        var backDoc = NotesLibrary.LoadDoc(back);
        log.AppendLine($"enex: {imported.Notes} imported, problems: {string.Join("; ", imported.Problems)}");
        log.AppendLine("enex back: " + Describe(backDoc));
        log.AppendLine("original:  " + Describe(doc));

        // HTML and Markdown
        string html = Path.Combine(dir, "probe.html");
        File.WriteAllText(html, "<html><body><h1>Trip</h1><p>Pack <b>light</b> and <a href='https://example.com'>check</a>.</p><ul><li>Socks</li><li>Hat<ul><li>Wool</li></ul></li></ul><ol><li>One</li><li>Two</li></ol><p><input type=checkbox checked> Passport</p><pre>code\nline 2</pre></body></html>");
        string md = Path.Combine(dir, "probe.md");
        File.WriteAllText(md, "# Plan\nSome **bold** and *italic* and ~~gone~~ and `code`.\n\n## Steps\n- [x] Done\n- [ ] Todo\n* Bullet\n- Dash\n1. First\n2. Second\n> A quote\n");
        var r2 = NotesExchange.ImportFiles(new[] { html, md }, NotesLibrary.DefaultFolder);
        log.AppendLine($"html+md: {r2.Notes} imported, problems: {string.Join("; ", r2.Problems)}");
        foreach (var n in NotesLibrary.Notes.Where(n => n.Title is "Trip" or "Plan")) log.AppendLine($"{n.Title}: " + Describe(NotesLibrary.LoadDoc(n)));

        // web addresses written as plain text become links when the note opens
        var linky = NotesLibrary.Create(NotesLibrary.DefaultFolder, NoteDoc.FromText("Links\nJaden 20 minutes\nhttps://www.slw-ele.com/m270htn01-1.html\nSee apple.com, or write to me@example.com."));
        w.Reload();
        typeof(NotesWindow).GetMethod("Select", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, new object[] { linky });
        Pump(300);
        log.AppendLine("autolink: " + Describe(editor.Read()) + " | links: " + string.Join(", ", editor.Read().Blocks.SelectMany(b => b.Runs).Where(r => r.Link != null).Select(r => r.Link)));

        // the Apple Notes body decoder, on a body made here the way a Mac stores one
        log.AppendLine("apple: " + AppleProbe());

        File.WriteAllText(Path.Combine(dir, $"notes-probe{(dark ? "-dark" : "")}.txt"), log.ToString());
        w.Close();
    }

    static string Describe(NoteDoc d) => string.Join(" | ", d.Blocks.Select(b => b.Image != null ? "[image]" :
        $"{b.Style}{(b.List != null ? "/" + b.List + (b.Checked ? "✓" : "") : "")}{(b.Indent > 0 ? ">" + b.Indent : "")}{(b.Quote ? "/quote" : "")}:{string.Concat(b.Runs.Select(r => (r.B ? "*" : "") + (r.I ? "_" : "") + (r.S ? "~" : "") + (r.U ? "+" : "") + (r.Mono ? "`" : "") + (r.Link != null ? "@" : "") + r.T))}"));

    static Popup FindPopup(DependencyObject root)
    {
        // the Aa menu's popup hangs off the format button
        foreach (var o in LogicalTreeHelperDescendants(root))
            if (o is Popup p && p.IsOpen) return p;
        return null;
    }

    static IEnumerable<object> LogicalTreeHelperDescendants(DependencyObject d)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d))
        {
            yield return c;
            if (c is DependencyObject dc) foreach (var x in LogicalTreeHelperDescendants(dc)) yield return x;
        }
    }

    static void MakePicture(string path)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5B, 0x9B, 0xD5), Color.FromRgb(0xF4, 0xB1, 0x83), 45), null, new Rect(0, 0, 480, 270));
            dc.DrawEllipse(Brushes.White, null, new Point(360, 80), 34, 34);
        }
        var rtb = new RenderTargetBitmap(480, 270, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
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

    static void Render(FrameworkElement fe, string path)
    {
        fe.UpdateLayout();
        double w = Math.Max(1, fe.ActualWidth), h = Math.Max(1, fe.ActualHeight);
        const double s = 1.25;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Theme.Res("SidebarBackgroundBrush"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)(w * s), (int)(h * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ------------------------------------------------------------------ an Apple Notes body, made the way a Mac stores it

    static string AppleProbe()
    {
        try
        {
            string text = "Shopping\nMilk\nEggs\nBold words here\nfirst\n";
            var runs = new List<byte[]>
            {
                Run(9, style: 0),                       // "Shopping\n" title
                Run(5, style: 103, done: true),         // "Milk\n" checked
                Run(5, style: 103, done: false),        // "Eggs\n"
                Run(4, weight: 1), Run(12),             // "Bold" bold + " words here\n"
                Run(6, style: 102),                     // "first\n" numbered
            };
            var note = Concat(Field(2, Encoding.UTF8.GetBytes(text)), Concat(runs.Select(r => Field(5, r)).ToArray()));
            var docu = Concat(Varint(2 << 3 | 0, 0), Field(3, note));
            var root = Field(2, docu);
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) gz.Write(root);
            var blocks = AppleNotesImport.Decode(ms.ToArray(), out var att);
            return Describe(new NoteDoc { Blocks = blocks }) + $" (attachments {att.Count})";
        }
        catch (Exception ex) { return "FAILED " + ex.Message; }
    }

    static byte[] Run(int length, int? style = null, bool? done = null, int weight = 0)
    {
        var parts = new List<byte[]> { Varint(1 << 3, (ulong)length) };
        if (style != null)
        {
            var ps = new List<byte[]> { Varint(1 << 3, (ulong)style.Value) };
            if (done != null) ps.Add(Field(5, Concat(Field(1, new byte[16]), Varint(2 << 3, done.Value ? 1UL : 0UL))));
            parts.Add(Field(2, Concat(ps.ToArray())));
        }
        if (weight != 0) parts.Add(Varint(5 << 3, (ulong)weight));
        return Concat(parts.ToArray());
    }

    static byte[] Field(int field, byte[] data) => Concat(Varint((ulong)(field << 3 | 2)), Varint((ulong)data.Length), data);
    static byte[] Varint(int tag, ulong v) => Concat(Varint((ulong)tag), Varint(v));
    static byte[] Varint(ulong v)
    {
        var b = new List<byte>();
        do { byte x = (byte)(v & 0x7F); v >>= 7; if (v != 0) x |= 0x80; b.Add(x); } while (v != 0);
        return b.ToArray();
    }
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
