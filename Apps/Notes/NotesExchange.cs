using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace MacShell.Apps.Notes;

/// <summary>
/// Notes in and out: .enex - the export format Apple Notes on a Mac imports (File › Import to Notes…), keeping titles,
/// styles, lists, checklists and pictures - and imports from .enex (Evernote, Apple Notes exporters), web pages,
/// Markdown, RTF and plain text.
/// </summary>
public static class NotesExchange
{
    // ================================================================== export: .enex

    public static void WriteEnex(IEnumerable<NoteInfo> notes, string path)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE en-export SYSTEM \"http://xml.evernote.com/pub/evernote-export4.dtd\">\n");
        sb.Append($"<en-export export-date=\"{Stamp(DateTime.UtcNow)}\" application=\"MacShell Notes\" version=\"1.0\">\n");
        foreach (var n in notes)
        {
            var doc = NotesLibrary.LoadDoc(n);
            var resources = new List<(string mime, byte[] data, string name, int w, int h)>();
            string title = doc.Title();
            string enml = ToEnml(doc, n, resources, skipTitle: true);
            sb.Append("<note><title>").Append(Esc(string.IsNullOrWhiteSpace(title) ? "New Note" : title)).Append("</title>");
            sb.Append("<content><![CDATA[").Append(enml.Replace("]]>", "]]]]><![CDATA[>")).Append("]]></content>");
            sb.Append($"<created>{Stamp(n.Created.ToUniversalTime())}</created><updated>{Stamp(n.Modified.ToUniversalTime())}</updated>");
            foreach (var r in resources)
            {
                sb.Append("<resource><data encoding=\"base64\">").Append(Convert.ToBase64String(r.data, Base64FormattingOptions.InsertLineBreaks)).Append("</data>");
                sb.Append("<mime>").Append(r.mime).Append("</mime>");
                if (r.w > 0) sb.Append($"<width>{r.w}</width><height>{r.h}</height>");
                sb.Append("<resource-attributes><file-name>").Append(Esc(r.name)).Append("</file-name></resource-attributes></resource>");
            }
            sb.Append("</note>\n");
        }
        sb.Append("</en-export>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static string Stamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    static string Esc(string s) => SecurityEsc(s ?? "");
    static string SecurityEsc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp", ".heic" => "image/heic", _ => "application/octet-stream",
    };

    static string ToEnml(NoteDoc doc, NoteInfo n, List<(string, byte[], string, int, int)> resources, bool skipTitle)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n<!DOCTYPE en-note SYSTEM \"http://xml.evernote.com/pub/enml2.dtd\">\n<en-note>");
        var blocks = doc.Blocks.ToList();
        if (skipTitle)
        {
            int first = blocks.FindIndex(b => b.Image == null && b.Text.Trim().Length > 0);
            if (first >= 0) blocks.RemoveAt(first);
        }
        // open lists, innermost on top: kind (bullet, dash, number), level, and whether its last item is still open
        // (a list one level in goes inside that item, as HTML wants)
        var open = new List<(string kind, int level, bool itemOpen)>();
        string Tag(string kind) => kind == "number" ? "ol" : "ul";
        void CloseTop()
        {
            var top = open[^1];
            if (top.itemOpen) sb.Append("</li>");
            sb.Append("</").Append(Tag(top.kind)).Append('>');
            open.RemoveAt(open.Count - 1);
        }
        void CloseLists(int toLevel, string kind)
        {
            while (open.Count > 0 && (open[^1].level > toLevel || (open[^1].level == toLevel && open[^1].kind != kind))) CloseTop();
        }
        foreach (var b in blocks)
        {
            string listTag = b.List switch { "bullet" or "dash" => "ul", "number" => "ol", _ => null };
            if (listTag == null) CloseLists(-1, null);
            if (b.Image != null)
            {
                string path = NotesLibrary.ImagePath(n, b.Image);
                if (path != null && File.Exists(path))
                {
                    var data = File.ReadAllBytes(path);
                    string hash = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
                    resources.Add((Mime(path), data, b.Image, b.ImageWidth, b.ImageHeight));
                    sb.Append($"<div><en-media type=\"{Mime(path)}\" hash=\"{hash}\"/></div>");
                }
                continue;
            }
            string inner = Inlines(b);
            if (listTag != null)
            {
                CloseLists(b.Indent, b.List);
                if (open.Count > 0 && open[^1].level == b.Indent && open[^1].itemOpen)
                {
                    sb.Append("</li>");
                    open[^1] = (open[^1].kind, open[^1].level, false);
                }
                while (open.Count == 0 || open[^1].level < b.Indent)
                {
                    int level = open.Count == 0 ? 0 : open[^1].level + 1;
                    sb.Append('<').Append(Tag(b.List)).Append(b.List == "dash" ? " style=\"list-style-type: '–  ';\"" : "").Append('>');
                    open.Add((b.List, level, false));
                }
                sb.Append("<li><div>").Append(inner.Length == 0 ? "<br/>" : inner).Append("</div>");
                open[^1] = (open[^1].kind, open[^1].level, true);
                continue;
            }
            string pad = b.Indent > 0 ? $" style=\"padding-left:{b.Indent * 40}px;\"" : "";
            if (b.List == "check") { sb.Append("<div").Append(pad).Append("><en-todo checked=\"").Append(b.Checked ? "true" : "false").Append("\"/>").Append(inner).Append("</div>"); continue; }
            string open1 = b.Quote ? "<blockquote>" : "", close1 = b.Quote ? "</blockquote>" : "";
            switch (b.Style)
            {
                case "title": sb.Append(open1).Append("<h1>").Append(inner).Append("</h1>").Append(close1); break;
                case "heading": sb.Append(open1).Append("<h2>").Append(inner).Append("</h2>").Append(close1); break;
                case "subheading": sb.Append(open1).Append("<h3>").Append(inner).Append("</h3>").Append(close1); break;
                case "mono": sb.Append(open1).Append("<div").Append(pad).Append("><font face=\"Menlo\">").Append(inner.Length == 0 ? "<br/>" : inner).Append("</font></div>").Append(close1); break;
                default: sb.Append(open1).Append("<div").Append(pad).Append('>').Append(inner.Length == 0 ? "<br/>" : inner).Append("</div>").Append(close1); break;
            }
        }
        CloseLists(-1, null);
        sb.Append("</en-note>");
        return sb.ToString();
    }

    static string Inlines(NoteBlock b)
    {
        var sb = new StringBuilder();
        foreach (var r in b.Runs)
        {
            string t = Esc(r.T).Replace("\n", "<br/>");
            if (r.Mono) t = "<font face=\"Menlo\">" + t + "</font>";
            if (r.S) t = "<s>" + t + "</s>";
            if (r.U) t = "<u>" + t + "</u>";
            if (r.I) t = "<i>" + t + "</i>";
            if (r.B) t = "<b>" + t + "</b>";
            if (!string.IsNullOrEmpty(r.Link) && !r.Link.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) t = $"<a href=\"{Esc(r.Link)}\">{t}</a>";
            sb.Append(t);
        }
        return sb.ToString();
    }

    // ================================================================== import

    public sealed class ImportResult { public int Notes; public List<string> Problems = new(); }

    /// <summary>Imports files into a folder (on the UI thread: notes are made as it goes).</summary>
    public static ImportResult ImportFiles(IEnumerable<string> files, string folder)
    {
        var res = new ImportResult();
        foreach (var f in files)
        {
            try
            {
                switch (Path.GetExtension(f).ToLowerInvariant())
                {
                    case ".enex": res.Notes += ImportEnex(f, folder); break;
                    case ".html" or ".htm":
                        {
                            var n = NotesLibrary.Create(folder, new NoteDoc(), File.GetCreationTime(f), File.GetLastWriteTime(f));
                            var doc = new NoteDoc { Blocks = HtmlToBlocks(File.ReadAllText(f), src => ImageFromSrc(n, src, Path.GetDirectoryName(f))) };
                            NotesLibrary.SaveDoc(n, doc, touch: false);
                            res.Notes++;
                            break;
                        }
                    case ".md" or ".markdown":
                        {
                            var n = NotesLibrary.Create(folder, new NoteDoc(), File.GetCreationTime(f), File.GetLastWriteTime(f));
                            NotesLibrary.SaveDoc(n, MarkdownToDoc(File.ReadAllText(f), n, Path.GetDirectoryName(f)), touch: false);
                            res.Notes++;
                            break;
                        }
                    case ".rtf":
                        {
                            var n = NotesLibrary.Create(folder, new NoteDoc(), File.GetCreationTime(f), File.GetLastWriteTime(f));
                            var fd = new FlowDocument();
                            using (var fs = File.OpenRead(f)) new TextRange(fd.ContentStart, fd.ContentEnd).Load(fs, System.Windows.DataFormats.Rtf);
                            var doc = NoteFlow.FromFlow(fd, bmp => NotesLibrary.AddImage(n, bmp));
                            FirstAsTitle(doc);
                            NotesLibrary.SaveDoc(n, doc, touch: false);
                            res.Notes++;
                            break;
                        }
                    case ".txt" or ".text" or "":
                        {
                            var n = NotesLibrary.Create(folder, NoteDoc.FromText(File.ReadAllText(f)), File.GetCreationTime(f), File.GetLastWriteTime(f));
                            res.Notes++;
                            break;
                        }
                    default: res.Problems.Add($"“{Path.GetFileName(f)}” isn’t a kind of file Notes can import."); break;
                }
            }
            catch (Exception ex) { res.Problems.Add($"“{Path.GetFileName(f)}”: {ex.Message}"); App.Log("notes import: " + ex); }
        }
        return res;
    }

    static void FirstAsTitle(NoteDoc doc)
    {
        var first = doc.Blocks.FirstOrDefault(b => b.Image == null && b.Text.Trim().Length > 0);
        if (first != null && first.List == null && first.Style == "body") first.Style = "title";
    }

    static int ImportEnex(string path, string folder)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        XDocument x;
        using (var reader = XmlReader.Create(path, settings)) x = XDocument.Load(reader);
        int count = 0;
        foreach (var note in x.Descendants("note"))
        {
            string title = note.Element("title")?.Value?.Trim() ?? "";
            string content = note.Element("content")?.Value ?? "";
            DateTime created = ParseStamp(note.Element("created")?.Value) ?? DateTime.Now;
            DateTime updated = ParseStamp(note.Element("updated")?.Value) ?? created;
            var resources = new Dictionary<string, (byte[] data, string mime, string name)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in note.Elements("resource"))
            {
                try
                {
                    var data = Convert.FromBase64String(r.Element("data")?.Value ?? "");
                    resources[Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant()] = (data, r.Element("mime")?.Value, r.Element("resource-attributes")?.Element("file-name")?.Value);
                }
                catch { }
            }
            var n = NotesLibrary.Create(folder, new NoteDoc(), created, updated);
            var blocks = HtmlToBlocks(content, src =>
            {
                if (src.StartsWith("hash:") && resources.TryGetValue(src[5..], out var res))
                {
                    if (res.mime?.StartsWith("image/") == true) return ImageFromBytes(n, res.data, res.name);
                    return null;
                }
                return ImageFromSrc(n, src, Path.GetDirectoryName(path));
            });
            var doc = new NoteDoc { Blocks = blocks };
            var first = blocks.FirstOrDefault(b => b.Image == null && b.Text.Trim().Length > 0);
            if (title.Length > 0 && (first == null || !string.Equals(first.Text.Trim(), title, StringComparison.CurrentCulture)))
                doc.Blocks.Insert(0, new NoteBlock { Style = "title", Runs = { new NoteRun { T = title } } });
            else if (first != null && first.Style == "body" && first.List == null) first.Style = "title";
            NotesLibrary.SaveDoc(n, doc, touch: false);
            count++;
        }
        return count;
    }

    static DateTime? ParseStamp(string s) =>
        s != null && DateTime.TryParseExact(s.Trim(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : null;

    static NoteBlock ImageFromBytes(NoteInfo n, byte[] data, string name)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (NotesLibrary.AddImage(n, frame, Path.GetExtension(name ?? ".png") is { Length: > 0 } e ? e : ".png", data) is var (file, w, h))
                return new NoteBlock { Image = file, ImageWidth = w, ImageHeight = h };
        }
        catch { }
        return null;
    }

    static NoteBlock ImageFromSrc(NoteInfo n, string src, string baseDir)
    {
        try
        {
            if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(src, @"^data:image/([a-z0-9+.-]+);base64,(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return m.Success ? ImageFromBytes(n, Convert.FromBase64String(m.Groups[2].Value), "image." + m.Groups[1].Value) : null;
            }
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;   // (pictures on the web aren't fetched)
            string path = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(src).LocalPath : Path.Combine(baseDir ?? "", Uri.UnescapeDataString(src).Replace('/', '\\'));
            if (File.Exists(path) && NotesLibrary.AddImage(n, path) is var (file, w, h)) return new NoteBlock { Image = file, ImageWidth = w, ImageHeight = h };
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ HTML / ENML → blocks

    static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "pre", "tr", "section", "article", "header", "footer", "en-note", "body", "ul", "ol", "table", "hr", "dd", "dt" };
    static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "head", "title", "noscript" };

    /// <summary>HTML (or ENML) as note blocks: paragraphs and headings, lists and checklists, bold/italic/underline/
    /// strikethrough/monospaced text and links, pictures (image resolves an "hash:…" or src to a picture block).</summary>
    public static List<NoteBlock> HtmlToBlocks(string html, Func<string, NoteBlock> image)
    {
        var blocks = new List<NoteBlock>();
        NoteBlock cur = null;
        int bold = 0, italic = 0, under = 0, strike = 0, mono = 0, pre = 0, quote = 0;
        var links = new Stack<string>();
        var lists = new Stack<string>();
        var spanStyles = new Stack<(bool b, bool i, bool u, bool s, bool m)>();
        string pendingStyle = null;
        bool liPending = false;

        NoteBlock Cur()
        {
            if (cur != null) return cur;
            cur = new NoteBlock { Style = pendingStyle ?? (pre > 0 ? "mono" : "body"), Quote = quote > 0 };
            if (liPending && lists.Count > 0) { cur.List = lists.Peek() switch { "ol" => "number", "dash" => "dash", _ => "bullet" }; cur.Indent = lists.Count - 1; liPending = false; }
            return cur;
        }
        void End()
        {
            if (cur != null)
            {
                // a line all in a monospaced font is a Monostyled paragraph
                if (cur.Style == "body" && cur.List == null && cur.Runs.Count > 0 && cur.Runs.All(r => r.Mono || r.T.Trim().Length == 0))
                {
                    cur.Style = "mono";
                    foreach (var r in cur.Runs) r.Mono = false;
                }
                if (cur.Runs.Count > 0) cur.Runs[^1].T = cur.Runs[^1].T.TrimEnd();
                if (cur.Runs.Count > 0 && cur.Runs[0].T.Length > 0) cur.Runs[0].T = cur.Runs[0].T.TrimStart();
                cur.Runs.RemoveAll(r => r.T.Length == 0);
                blocks.Add(cur);
            }
            cur = null;
        }
        void Text(string t)
        {
            if (t.Length == 0) return;
            if (pre == 0)
            {
                t = Regex.Replace(t, @"\s+", " ");
                if (cur == null && t.Trim().Length == 0) return;
                if ((cur == null || cur.Runs.Count == 0 || cur.Runs[^1].T.EndsWith(' ')) && t.StartsWith(' ')) t = t.TrimStart();
                if (t.Length == 0) return;
            }
            else if (t.Contains('\n'))
            {
                var lines = t.Replace("\r", "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) { Cur(); End(); }
                    if (lines[i].Length > 0) Text(lines[i]);
                }
                return;
            }
            bool sb = spanStyles.Any(s => s.b), si = spanStyles.Any(s => s.i), su = spanStyles.Any(s => s.u), ss = spanStyles.Any(s => s.s), sm = spanStyles.Any(s => s.m);
            Cur().Runs.Add(new NoteRun { T = t, B = bold > 0 || sb, I = italic > 0 || si, U = under > 0 || su, S = strike > 0 || ss, Mono = (mono > 0 || sm) && pre == 0, Link = links.Count > 0 ? links.Peek() : null });
        }

        int pos = 0;
        while (pos < html.Length)
        {
            int lt = html.IndexOf('<', pos);
            if (lt < 0) { Text(WebUtility.HtmlDecode(html[pos..])); break; }
            if (lt > pos) Text(WebUtility.HtmlDecode(html[pos..lt]));
            if (html.AsSpan(lt).StartsWith("<!--")) { int e = html.IndexOf("-->", lt + 4); pos = e < 0 ? html.Length : e + 3; continue; }
            int gt = html.IndexOf('>', lt + 1);
            if (gt < 0) { Text(WebUtility.HtmlDecode(html[lt..])); break; }
            string tag = html[(lt + 1)..gt];
            pos = gt + 1;
            if (tag.StartsWith('!') || tag.StartsWith('?')) continue;
            bool closing = tag.StartsWith('/');
            bool selfClose = tag.EndsWith('/');
            string body = tag.Trim('/', ' ');
            int sp = body.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
            string name = (sp < 0 ? body : body[..sp]).ToLowerInvariant();
            var attrs = sp < 0 ? new Dictionary<string, string>() : Attributes(body[sp..]);
            if (!closing && SkipTags.Contains(name) && !selfClose)
            {
                int e = html.IndexOf("</" + name, pos, StringComparison.OrdinalIgnoreCase);
                pos = e < 0 ? html.Length : Math.Max(pos, html.IndexOf('>', e) + 1);
                continue;
            }
            if (closing)
            {
                switch (name)
                {
                    case "b" or "strong": bold = Math.Max(0, bold - 1); break;
                    case "i" or "em": italic = Math.Max(0, italic - 1); break;
                    case "u" or "ins": under = Math.Max(0, under - 1); break;
                    case "s" or "strike" or "del": strike = Math.Max(0, strike - 1); break;
                    case "code" or "tt" or "kbd" or "samp": mono = Math.Max(0, mono - 1); break;
                    case "a": if (links.Count > 0) links.Pop(); break;
                    case "span" or "font": if (spanStyles.Count > 0) spanStyles.Pop(); break;
                    case "pre": End(); pre = Math.Max(0, pre - 1); break;
                    case "blockquote": End(); quote = Math.Max(0, quote - 1); break;
                    case "ul" or "ol": End(); if (lists.Count > 0) lists.Pop(); break;
                    case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": End(); pendingStyle = null; break;
                    default: if (BlockTags.Contains(name)) End(); break;
                }
                continue;
            }
            switch (name)
            {
                case "br":
                    if (cur == null) { Cur(); End(); }   // an empty line
                    else End();
                    break;
                case "b" or "strong": bold++; break;
                case "i" or "em": italic++; break;
                case "u" or "ins": under++; break;
                case "s" or "strike" or "del": strike++; break;
                case "code" or "tt" or "kbd" or "samp": mono++; break;
                case "a": links.Push(attrs.TryGetValue("href", out var href) ? WebUtility.HtmlDecode(href) : null); if (selfClose) links.Pop(); break;
                case "span" or "font":
                    {
                        string style = (attrs.TryGetValue("style", out var st) ? st : "").ToLowerInvariant();
                        string face = (attrs.TryGetValue("face", out var fc) ? fc : "").ToLowerInvariant() + " " + style;
                        spanStyles.Push((Regex.IsMatch(style, @"font-weight\s*:\s*(bold|[6-9]00)"), style.Contains("italic"), style.Contains("underline"),
                                         style.Contains("line-through"), face.Contains("mono") || face.Contains("menlo") || face.Contains("courier") || face.Contains("consolas")));
                        if (selfClose) spanStyles.Pop();
                        break;
                    }
                case "en-todo" or "input":
                    if (name == "input" && !(attrs.TryGetValue("type", out var ty) && ty.Equals("checkbox", StringComparison.OrdinalIgnoreCase))) break;
                    {
                        var b = Cur();
                        b.List = "check";
                        b.Checked = attrs.TryGetValue("checked", out var ch) && !ch.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                case "en-media" or "img":
                    {
                        string src = name == "en-media" ? (attrs.TryGetValue("hash", out var h) ? "hash:" + h.ToLowerInvariant() : null) : attrs.TryGetValue("src", out var s2) ? WebUtility.HtmlDecode(s2) : null;
                        var img = src == null ? null : image?.Invoke(src);
                        if (img != null) { End(); blocks.Add(img); }
                        break;
                    }
                case "pre": End(); pre++; break;
                case "blockquote": End(); quote++; break;
                case "ul" or "ol":
                    End();
                    // (a dashed list, as Notes exports it: list-style-type '–')
                    lists.Push(name == "ul" && attrs.TryGetValue("style", out var ls) && (ls.Contains('–') || ls.Contains("dash", StringComparison.OrdinalIgnoreCase)) ? "dash" : name);
                    break;
                case "li": End(); liPending = true; break;
                case "h1": End(); pendingStyle = "title"; break;
                case "h2": End(); pendingStyle = "heading"; break;
                case "h3" or "h4" or "h5" or "h6": End(); pendingStyle = "subheading"; break;
                case "td" or "th": if (cur != null && cur.Runs.Count > 0) Text("\t"); break;
                default:
                    if (BlockTags.Contains(name))
                    {
                        End();
                        if (attrs.TryGetValue("style", out var ps) && Regex.Match(ps, @"padding-left\s*:\s*(\d+)") is { Success: true } pm)
                            Cur().Indent = Math.Clamp(int.Parse(pm.Groups[1].Value) / 40, 0, 8);
                    }
                    break;
            }
        }
        End();
        // blank lines at the ends dropped
        while (blocks.Count > 0 && blocks[0].IsEmpty && blocks[0].List == null) blocks.RemoveAt(0);
        while (blocks.Count > 0 && blocks[^1].IsEmpty && blocks[^1].List == null) blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }

    static Dictionary<string, string> Attributes(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(s, "([a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*(?:=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+)))?"))
            d[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : "";
        return d;
    }

    // ------------------------------------------------------------------ Markdown → a note

    public static NoteDoc MarkdownToDoc(string md, NoteInfo n, string baseDir)
    {
        var doc = new NoteDoc();
        bool fence = false;
        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { fence = !fence; continue; }
            if (fence) { doc.Blocks.Add(new NoteBlock { Style = "mono", Runs = { new NoteRun { T = raw } } }); continue; }
            int lead = line.Length - line.TrimStart().Length;
            string t = line.TrimStart();
            var b = new NoteBlock { Indent = Math.Min(8, lead / 2) };
            Match m;
            if ((m = Regex.Match(t, @"^(#{1,6})\s+(.*)$")).Success) { b.Style = m.Groups[1].Length == 1 ? "title" : m.Groups[1].Length == 2 ? "heading" : "subheading"; t = m.Groups[2].Value; }
            else if ((m = Regex.Match(t, @"^[-*+]\s+\[([ xX])\]\s+(.*)$")).Success) { b.List = "check"; b.Checked = m.Groups[1].Value != " "; t = m.Groups[2].Value; }
            else if ((m = Regex.Match(t, @"^[-*+]\s+(.*)$")).Success) { b.List = t.StartsWith('-') ? "dash" : "bullet"; t = m.Groups[1].Value; }
            else if ((m = Regex.Match(t, @"^\d+[.)]\s+(.*)$")).Success) { b.List = "number"; t = m.Groups[1].Value; }
            else if ((m = Regex.Match(t, @"^>\s?(.*)$")).Success) { b.Quote = true; t = m.Groups[1].Value; }
            if ((m = Regex.Match(t, @"^!\[[^\]]*\]\(([^)\s]+)[^)]*\)$")).Success)
            {
                var img = ImageFromSrc(n, m.Groups[1].Value, baseDir);
                if (img != null) { doc.Blocks.Add(img); continue; }
            }
            b.Runs = MarkdownInlines(t);
            doc.Blocks.Add(b);
        }
        while (doc.Blocks.Count > 0 && doc.Blocks[^1].IsEmpty) doc.Blocks.RemoveAt(doc.Blocks.Count - 1);
        FirstAsTitle(doc);
        return doc;
    }

    static List<NoteRun> MarkdownInlines(string t)
    {
        var runs = new List<NoteRun>();
        var rx = new Regex(@"(\*\*|__)(.+?)\1|(\*|_)(.+?)\3|~~(.+?)~~|`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)");
        int pos = 0;
        foreach (Match m in rx.Matches(t))
        {
            if (m.Index > pos) runs.Add(new NoteRun { T = t[pos..m.Index] });
            if (m.Groups[2].Success) runs.Add(new NoteRun { T = m.Groups[2].Value, B = true });
            else if (m.Groups[4].Success) runs.Add(new NoteRun { T = m.Groups[4].Value, I = true });
            else if (m.Groups[5].Success) runs.Add(new NoteRun { T = m.Groups[5].Value, S = true });
            else if (m.Groups[6].Success) runs.Add(new NoteRun { T = m.Groups[6].Value, Mono = true });
            else if (m.Groups[7].Success) runs.Add(new NoteRun { T = m.Groups[7].Value, Link = m.Groups[8].Value });
            pos = m.Index + m.Length;
        }
        if (pos < t.Length) runs.Add(new NoteRun { T = t[pos..] });
        return runs;
    }
}
