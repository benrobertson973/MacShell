using System.IO.Compression;
using System.Text;
using MacShell.Native;

namespace MacShell.Apps.Notes;

/// <summary>
/// Notes brought over from Apple Notes on a Mac: its database (NoteStore.sqlite, with the Accounts folder of pictures)
/// from "group.com.apple.notes" - zipped, as a folder, or the file itself. Each note's body is a gzipped protobuf (the
/// text, and runs of formatting: paragraph styles, lists and checklists, bold/italic/underline/strikethrough, links,
/// attachments); folders come along, pictures are copied in, locked notes can't be read.
/// </summary>
public static class AppleNotesImport
{
    public sealed class Result
    {
        public int Notes, Folders, Locked, Pictures, Failed;
        public List<string> Problems = new();
    }

    /// <summary>Imports from a zip of group.com.apple.notes, that folder, or NoteStore.sqlite. Off the UI thread;
    /// progress(done, total) along the way; the new notes are added on the UI thread through add.</summary>
    public static Result Import(string source, Action<int, int> progress, Action<Action> onUi)
    {
        var result = new Result();
        string temp = Path.Combine(Path.GetTempPath(), "macshell-applenotes-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(temp);
            string root;
            if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(source, temp);
                root = temp;
            }
            else root = Directory.Exists(source) ? source : Path.GetDirectoryName(source);
            string db = Directory.EnumerateFiles(root, "NoteStore.sqlite", SearchOption.AllDirectories)
                                 .FirstOrDefault(f => !f.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase));
            if (db == null) { result.Problems.Add("There’s no NoteStore.sqlite in it. Zip the whole “group.com.apple.notes” folder (see the steps)."); return result; }
            string notesRoot = Path.GetDirectoryName(db);
            // a copy (with its journal) that SQLite may bring up to date
            string work = Path.Combine(temp, "db");
            Directory.CreateDirectory(work);
            foreach (var f in new[] { "", "-wal", "-shm" })
                if (File.Exists(db + f)) File.Copy(db + f, Path.Combine(work, "NoteStore.sqlite" + f), true);
            using var sql = Sqlite.Open(Path.Combine(work, "NoteStore.sqlite"), writable: true);
            Read(sql, notesRoot, result, progress, onUi);
        }
        catch (Exception ex)
        {
            result.Problems.Add(ex.Message);
            App.Log("apple notes import: " + ex);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
        return result;
    }

    sealed class Attachment { public string Uti, MediaId, MediaFile, AltText, Url; public long Pk; }

    static void Read(Sqlite sql, string root, Result result, Action<int, int> progress, Action<Action> onUi)
    {
        const string T = "ZICCLOUDSYNCINGOBJECT";
        if (!sql.HasTable(T) || !sql.HasTable("ZICNOTEDATA")) throw new InvalidDataException("This isn’t a Notes database from a recent Mac (macOS 10.13 or later).");
        var cols = sql.Columns(T);
        var dataCols = sql.Columns("ZICNOTEDATA");
        string C(params string[] names) => names.FirstOrDefault(cols.Contains);

        // folders (not Recently Deleted, not smart folders)
        string folderType = C("ZFOLDERTYPE");
        var folders = new Dictionary<long, (string name, bool trash)>();
        if (cols.Contains("ZTITLE2"))
            foreach (var r in sql.Query($"SELECT Z_PK, ZTITLE2{(folderType != null ? ", " + folderType : "")} FROM {T} WHERE ZTITLE2 IS NOT NULL"))
                folders[(long)r["Z_PK"]] = (r["ZTITLE2"] as string, folderType != null && r[folderType] is long ft && ft == 1);

        // attachments and their media files
        var attachments = new Dictionary<string, Attachment>(StringComparer.OrdinalIgnoreCase);
        var media = new Dictionary<long, (string id, string file)>();
        if (cols.Contains("ZIDENTIFIER"))
        {
            if (cols.Contains("ZFILENAME"))
                foreach (var r in sql.Query($"SELECT Z_PK, ZIDENTIFIER, ZFILENAME FROM {T} WHERE ZFILENAME IS NOT NULL"))
                    media[(long)r["Z_PK"]] = (r["ZIDENTIFIER"] as string, r["ZFILENAME"] as string);
            if (cols.Contains("ZTYPEUTI"))
            {
                string extra = (cols.Contains("ZMEDIA") ? ", ZMEDIA" : "") + (cols.Contains("ZALTTEXT") ? ", ZALTTEXT" : "") + (cols.Contains("ZURLSTRING") ? ", ZURLSTRING" : "");
                foreach (var r in sql.Query($"SELECT Z_PK, ZIDENTIFIER, ZTYPEUTI{extra} FROM {T} WHERE ZTYPEUTI IS NOT NULL"))
                {
                    var a = new Attachment { Pk = (long)r["Z_PK"], Uti = r["ZTYPEUTI"] as string };
                    if (r.TryGetValue("ZMEDIA", out var m) && m is long mpk && media.TryGetValue(mpk, out var mf)) { a.MediaId = mf.id; a.MediaFile = mf.file; }
                    if (r.TryGetValue("ZALTTEXT", out var alt)) a.AltText = alt as string;
                    if (r.TryGetValue("ZURLSTRING", out var url)) a.Url = url as string;
                    if (r["ZIDENTIFIER"] is string id) attachments[id] = a;
                }
            }
        }
        // inline attachments (hashtags, mentions) are words: those are in ZALTTEXT
        _altText = attachments.Where(a => !string.IsNullOrEmpty(a.Value.AltText)).ToDictionary(a => a.Key, a => a.Value.AltText, StringComparer.OrdinalIgnoreCase);

        // where the pictures are: a folder named after each media item (…/Media/<id>/…), and drawings' rendered pictures
        var mediaDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                mediaDirs.TryAdd(Path.GetFileName(dir), dir);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (f.Contains("FallbackImages", StringComparison.OrdinalIgnoreCase) || f.Contains("Previews", StringComparison.OrdinalIgnoreCase))
                    fallback.TryAdd(Path.GetFileNameWithoutExtension(f).Split('-')[0], f);
        }
        catch { }

        // the notes
        string created = C("ZCREATIONDATE3", "ZCREATIONDATE1", "ZCREATIONDATE"), modified = C("ZMODIFICATIONDATE1", "ZMODIFICATIONDATE");
        string locked = C("ZISPASSWORDPROTECTED"), deleted = C("ZMARKEDFORDELETION"), folderCol = C("ZFOLDER");
        string join = dataCols.Contains("ZNOTE") ? "d.ZNOTE = o.Z_PK" : "d.Z_PK = o.ZNOTEDATA";
        string select = $"SELECT o.Z_PK, d.ZDATA" + (created != null ? $", o.{created} AS C" : "") + (modified != null ? $", o.{modified} AS M" : "") +
                        (locked != null ? $", o.{locked} AS L" : "") + (deleted != null ? $", o.{deleted} AS D" : "") + (folderCol != null ? $", o.{folderCol} AS F" : "") +
                        $" FROM {T} o JOIN ZICNOTEDATA d ON {join} WHERE d.ZDATA IS NOT NULL";
        var rows = sql.Query(select);
        int done = 0;
        var folderIds = new Dictionary<long, string>();
        foreach (var r in rows)
        {
            done++;
            if (done % 10 == 0) progress?.Invoke(done, rows.Count);
            if (r.TryGetValue("D", out var del) && del is long dl && dl == 1) continue;
            long fpk = r.TryGetValue("F", out var fo) && fo is long fl ? fl : -1;
            if (folders.TryGetValue(fpk, out var fi) && fi.trash) continue;
            if (r.TryGetValue("L", out var lk) && lk is long ll && ll == 1) { result.Locked++; continue; }
            try
            {
                var blocks = Decode(r["ZDATA"] as byte[], out var pending);
                DateTime? c = r.TryGetValue("C", out var cv) ? AppleDate(cv) : null, m = r.TryGetValue("M", out var mv) ? AppleDate(mv) : null;
                string folderName = folders.TryGetValue(fpk, out var fn) ? fn.name : "Notes";
                var res = result;
                onUi(() =>
                {
                    // the folder (made once), the note, then its pictures copied into it
                    if (!folderIds.TryGetValue(fpk, out string fid))
                    {
                        var existing = NotesLibrary.Folders.FirstOrDefault(f => string.Equals(f.Name, folderName, StringComparison.CurrentCultureIgnoreCase));
                        if (existing == null) { existing = NotesLibrary.AddFolder(folderName); res.Folders++; }
                        folderIds[fpk] = fid = existing.Id;
                    }
                    var doc = new NoteDoc { Blocks = blocks };
                    var note = NotesLibrary.Create(fid, new NoteDoc(), c ?? m ?? DateTime.Now, m ?? c ?? DateTime.Now);
                    foreach (var (index, attachmentId) in pending)
                    {
                        var b = blocks[index];
                        if (!attachments.TryGetValue(attachmentId, out var a)) { b.Image = null; b.Runs = new() { new NoteRun { T = "[attachment]" } }; continue; }
                        string file = FindMedia(a, mediaDirs, fallback, attachmentId);
                        if (file != null && NotesLibrary.IsImageFile(file) && NotesLibrary.AddImage(note, file) is var (name, w, h))
                        {
                            b.Image = name; b.ImageWidth = w; b.ImageHeight = h; b.Runs = new();
                            res.Pictures++;
                        }
                        else if (file != null)
                        {
                            // a file of another kind (or a picture this PC can't read, e.g. HEIC without its extension): kept, as a link
                            string copy = Path.Combine(NotesLibrary.NoteDir(note), Path.GetFileName(file));
                            try { Directory.CreateDirectory(NotesLibrary.NoteDir(note)); File.Copy(file, copy, true); } catch { }
                            b.Image = null;
                            b.Runs = new() { new NoteRun { T = "📎 " + Path.GetFileName(file), Link = new Uri(copy).AbsoluteUri } };
                        }
                        else
                        {
                            b.Image = null;
                            b.Runs = new() { new NoteRun { T = a.Uti switch
                            {
                                "com.apple.notes.table" => "[table]",
                                "com.apple.notes.gallery" => "[scanned document]",
                                "public.url" => a.Url ?? "[link]",
                                _ => "[attachment]",
                            }, Link = a.Uti == "public.url" ? a.Url : null } };
                        }
                    }
                    NotesLibrary.SaveDoc(note, doc, touch: false);
                    res.Notes++;
                });
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Problems.Count < 5) result.Problems.Add(ex.Message);
            }
        }
        progress?.Invoke(rows.Count, rows.Count);

        string FindMedia(Attachment a, Dictionary<string, string> dirs, Dictionary<string, string> fb, string attachmentId)
        {
            if (a.MediaId != null && dirs.TryGetValue(a.MediaId, out var d))
            {
                var files = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).ToList();
                var exact = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), a.MediaFile, StringComparison.OrdinalIgnoreCase));
                if (exact != null || files.Count > 0) return exact ?? files[0];
            }
            return fb.TryGetValue(attachmentId, out var f2) ? f2 : null;
        }
    }

    [ThreadStatic] static Dictionary<string, string> _altText;

    static DateTime? AppleDate(object v) => v switch
    {
        double d => new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(d).ToLocalTime(),
        long l => new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(l).ToLocalTime(),
        _ => null,
    };

    // ------------------------------------------------------------------ the note body (protobuf)

    sealed class Run
    {
        public int Length;
        public int? Style;
        public int Indent;
        public bool Checked, Quote;
        public int Weight;
        public bool Underline, Strike, Mono;
        public string Link, AttachmentId, AttachmentUti;
    }

    /// <summary>A note's body as blocks; the attachments to fill in afterwards (block index, attachment id).</summary>
    public static List<NoteBlock> Decode(byte[] data, out List<(int index, string id)> attachments)
    {
        attachments = new List<(int, string)>();
        if (data == null || data.Length == 0) return new List<NoteBlock>();
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        // NoteStoreProto { 2: Document { 3: Note { 2: text, 5: AttributeRun… } } }
        var document = Pb.First(data, 2) ?? throw new InvalidDataException("unreadable note");
        var note = Pb.First(document, 3) ?? throw new InvalidDataException("unreadable note");
        string text = Encoding.UTF8.GetString(Pb.First(note, 2) ?? Array.Empty<byte>());
        var runs = Pb.All(note, 5).Select(ParseRun).ToList();
        if (runs.Count == 0) runs.Add(new Run { Length = text.Length });

        var blocks = new List<NoteBlock>();
        var current = new NoteBlock();
        int pos = 0;
        void Finish(Run style)
        {
            ApplyParagraph(current, style);
            blocks.Add(current);
            current = new NoteBlock();
        }
        foreach (var r in runs)
        {
            if (pos >= text.Length) break;
            int len = Math.Min(r.Length, text.Length - pos);
            string seg = text.Substring(pos, len);
            pos += len;
            if (r.AttachmentId != null)
            {
                string alt = _altText != null && _altText.TryGetValue(r.AttachmentId, out var a) ? a : null;
                bool inline = r.AttachmentUti?.StartsWith("com.apple.notes.inlinetextattachment") == true;
                if (inline || alt != null)
                {
                    current.Runs.Add(new NoteRun { T = alt ?? "" });
                    continue;
                }
                // the attachment: a block of its own (a picture, or a line naming it)
                if (!current.IsEmpty) { var keep = current; current = new NoteBlock(); ApplyParagraph(keep, r); blocks.Add(keep); }
                blocks.Add(new NoteBlock { Image = "?" });
                attachments.Add((blocks.Count - 1, r.AttachmentId));
                seg = seg.Replace("￼", "");
                if (seg.Length == 0) continue;
            }
            var pieces = seg.Split('\n');
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pieces[i].Length > 0)
                    current.Runs.Add(new NoteRun
                    {
                        T = pieces[i].Replace("￼", ""),
                        B = r.Weight is 1 or 3, I = r.Weight is 2 or 3, U = r.Underline, S = r.Strike, Mono = r.Mono && r.Style != 4, Link = r.Link,
                    });
                if (i < pieces.Length - 1) Finish(r);
            }
        }
        if (!current.IsEmpty) Finish(runs.LastOrDefault());
        // runs of the same look joined
        foreach (var b in blocks)
        {
            var merged = new List<NoteRun>();
            foreach (var nr in b.Runs.Where(x => x.T.Length > 0))
                if (merged.Count > 0 && merged[^1].SameLook(nr)) merged[^1].T += nr.T; else merged.Add(nr);
            b.Runs = merged;
        }
        return blocks;
    }

    static void ApplyParagraph(NoteBlock b, Run r)
    {
        if (r == null) return;
        b.Indent = Math.Clamp(r.Indent, 0, 8);
        b.Quote = r.Quote;
        switch (r.Style)
        {
            case 0: b.Style = "title"; break;
            case 1: b.Style = "heading"; break;
            case 2: b.Style = "subheading"; break;
            case 4: b.Style = "mono"; break;
            case 100: b.List = "bullet"; break;
            case 101: b.List = "dash"; break;
            case 102: b.List = "number"; break;
            case 103: b.List = "check"; b.Checked = r.Checked; break;
        }
    }

    static Run ParseRun(byte[] m)
    {
        var r = new Run();
        foreach (var (field, wire, value, bytes) in Pb.Fields(m))
        {
            switch (field)
            {
                case 1 when wire == 0: r.Length = (int)value; break;
                case 2 when bytes != null:
                    foreach (var (pf, pw, pv, pb) in Pb.Fields(bytes))
                    {
                        if (pf == 1 && pw == 0) r.Style = (int)(long)pv;
                        else if (pf == 4 && pw == 0) r.Indent = (int)pv;
                        else if (pf == 5 && pb != null) r.Checked = Pb.Fields(pb).Any(c => c.field == 2 && c.value == 1);
                        else if (pf == 8 && pw == 0) r.Quote = pv == 1;
                    }
                    break;
                case 3 when bytes != null:
                    {
                        string font = Pb.First(bytes, 1) is byte[] fb ? Encoding.UTF8.GetString(fb) : null;
                        r.Mono = font != null && (font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Menlo", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase));
                        break;
                    }
                case 5 when wire == 0: r.Weight = (int)value; break;
                case 6 when wire == 0: r.Underline = value != 0; break;
                case 7 when wire == 0: r.Strike = value != 0; break;
                case 9 when bytes != null: r.Link = Encoding.UTF8.GetString(bytes); break;
                case 12 when bytes != null:
                    r.AttachmentId = Pb.First(bytes, 1) is byte[] ab ? Encoding.UTF8.GetString(ab) : null;
                    r.AttachmentUti = Pb.First(bytes, 2) is byte[] ub ? Encoding.UTF8.GetString(ub) : null;
                    break;
            }
        }
        return r;
    }
}

/// <summary>Just enough protobuf reading: a message's fields (number, wire type, varint value, bytes of a length-delimited one).</summary>
static class Pb
{
    public static IEnumerable<(int field, int wire, ulong value, byte[] bytes)> Fields(byte[] d)
    {
        int i = 0;
        while (i < d.Length)
        {
            ulong tag = Varint(d, ref i);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: yield return (field, wire, Varint(d, ref i), null); break;
                case 1: i += 8; yield return (field, wire, 0, null); break;
                case 2:
                    {
                        int len = (int)Varint(d, ref i);
                        if (len < 0 || i + len > d.Length) yield break;
                        var b = new byte[len];
                        Buffer.BlockCopy(d, i, b, 0, len);
                        i += len;
                        yield return (field, wire, 0, b);
                        break;
                    }
                case 5: i += 4; yield return (field, wire, 0, null); break;
                default: yield break;   // (groups and the unknown: not in Notes' data)
            }
        }
    }

    static ulong Varint(byte[] d, ref int i)
    {
        ulong v = 0;
        int shift = 0;
        while (i < d.Length)
        {
            byte b = d[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) break;
        }
        return v;
    }

    public static byte[] First(byte[] d, int field) => Fields(d).FirstOrDefault(f => f.field == field && f.bytes != null).bytes;
    public static IEnumerable<byte[]> All(byte[] d, int field) => Fields(d).Where(f => f.field == field && f.bytes != null).Select(f => f.bytes);
}
