using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacShell.Services;

namespace MacShell.Apps.Notes;

public sealed class NoteFolder
{
    public string Id { get; set; }
    public string Name { get; set; }
    public DateTime Created { get; set; }
}

/// <summary>A note as the list knows it (its words are in its own note.json).</summary>
public sealed class NoteInfo
{
    public string Id { get; set; }
    public string Folder { get; set; }
    public string Title { get; set; } = "";
    public string Preview { get; set; } = "";
    /// <summary>Its words, for searching.</summary>
    public string Text { get; set; } = "";
    public string Image { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public bool Pinned { get; set; }
    /// <summary>When it was deleted (it's in Recently Deleted for 30 days), and the folder it was in.</summary>
    public DateTime? Deleted { get; set; }
    public string DeletedFrom { get; set; }

    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "New Note" : Title;
}

/// <summary>
/// Notes' library "On My Mac": the folders and notes, kept in %APPDATA%\MacShell\Notes (library.json, and a folder
/// per note with its note.json and pictures). Deleted notes stay in Recently Deleted for 30 days.
/// </summary>
public static class NotesLibrary
{
    public const string DefaultFolder = "notes", DeletedFolder = "deleted", AllFolder = "all";
    /// <summary>Where the library is (another folder only for the notes probe).</summary>
    public static string Dir { get; set; } = Path.Combine(Settings.DataDirectory, "Notes");
    static string IndexFile => Path.Combine(Dir, "library.json");

    public static List<NoteFolder> Folders { get; private set; } = new();
    public static List<NoteInfo> Notes { get; private set; } = new();
    public static event Action Changed;
    static bool _loaded;
    static System.Windows.Threading.DispatcherTimer _saveTimer;

    sealed class Library { public List<NoteFolder> Folders { get; set; } = new(); public List<NoteInfo> Notes { get; set; } = new(); }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(IndexFile))
            {
                var lib = JsonSerializer.Deserialize<Library>(File.ReadAllText(IndexFile), Opts);
                Folders = lib?.Folders ?? new();
                Notes = lib?.Notes ?? new();
            }
        }
        catch (Exception ex) { App.Log("notes: " + ex.Message); }
        if (Folders.All(f => f.Id != DefaultFolder)) Folders.Insert(0, new NoteFolder { Id = DefaultFolder, Name = "Notes", Created = DateTime.Now });
        foreach (var n in Notes) if (n.Folder != DeletedFolder && Folders.All(f => f.Id != n.Folder)) n.Folder = DefaultFolder;
        Purge();
    }

    public static void Save(bool now = false)
    {
        Changed?.Invoke();
        if (now) { SaveNow(); return; }
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Library { Folders = Folders, Notes = Notes }, Opts));
            File.Move(tmp, IndexFile, true);
        }
        catch (Exception ex) { App.Log("notes save: " + ex.Message); }
    }

    public static string NoteDir(NoteInfo n) => Path.Combine(Dir, "notes", n.Id);
    public static string ImagePath(NoteInfo n, string file) => file == null ? null : Path.Combine(NoteDir(n), file);

    public static NoteDoc LoadDoc(NoteInfo n)
    {
        try
        {
            string f = Path.Combine(NoteDir(n), "note.json");
            if (File.Exists(f)) return NoteDoc.FromJson(File.ReadAllText(f));
        }
        catch (Exception ex) { App.Log("notes load: " + ex.Message); }
        return NoteDoc.Empty();
    }

    /// <summary>Saves a note's words, and what the list shows of them.</summary>
    public static void SaveDoc(NoteInfo n, NoteDoc d, bool touch = true)
    {
        n.Title = d.Title();
        n.Preview = d.Preview();
        string text = d.PlainText();
        n.Text = text.Length > 20000 ? text[..20000] : text;
        n.Image = d.FirstImage();
        if (touch) n.Modified = DateTime.Now;
        {
            try
            {
                Directory.CreateDirectory(NoteDir(n));
                string f = Path.Combine(NoteDir(n), "note.json");
                File.WriteAllText(f + ".tmp", d.ToJson());
                File.Move(f + ".tmp", f, true);
                // pictures no longer in the note are let go
                var keep = new HashSet<string>(d.Blocks.Where(b => b.Image != null).Select(b => b.Image), StringComparer.OrdinalIgnoreCase) { "note.json" };
                // (and files the note links to - attachments brought over from Apple Notes)
                foreach (var r in d.Blocks.SelectMany(b => b.Runs).Where(r => r.Link?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true))
                    try { keep.Add(Path.GetFileName(new Uri(r.Link).LocalPath)); } catch { }
                foreach (var file in Directory.EnumerateFiles(NoteDir(n)))
                    if (!keep.Contains(Path.GetFileName(file)) && !file.EndsWith(".tmp") && (DateTime.Now - File.GetCreationTime(file)).TotalMinutes > 2)
                        try { File.Delete(file); } catch { }
            }
            catch (Exception ex) { App.Log("notes save note: " + ex.Message); }
        }
        Save();
    }

    public static NoteInfo Create(string folder, NoteDoc doc = null, DateTime? created = null, DateTime? modified = null)
    {
        Load();
        if (folder is null or AllFolder or DeletedFolder || Folders.All(f => f.Id != folder)) folder = DefaultFolder;
        var n = new NoteInfo { Id = Guid.NewGuid().ToString("N"), Folder = folder, Created = created ?? DateTime.Now, Modified = modified ?? created ?? DateTime.Now };
        Notes.Add(n);
        SaveDoc(n, doc ?? NoteDoc.Empty(), touch: modified == null);
        return n;
    }

    public static NoteInfo Duplicate(NoteInfo n)
    {
        var doc = LoadDoc(n);
        var copy = new NoteInfo { Id = Guid.NewGuid().ToString("N"), Folder = n.Folder, Created = DateTime.Now, Modified = DateTime.Now };
        try
        {
            Directory.CreateDirectory(NoteDir(copy));
            foreach (var f in Directory.EnumerateFiles(NoteDir(n)))
                if (!f.EndsWith("note.json")) File.Copy(f, Path.Combine(NoteDir(copy), Path.GetFileName(f)), true);
        }
        catch { }
        Notes.Add(copy);
        SaveDoc(copy, doc);
        return copy;
    }

    /// <summary>A note deleted: to Recently Deleted - or, from there, for good.</summary>
    public static void Delete(NoteInfo n)
    {
        if (n.Folder == DeletedFolder)
        {
            Notes.Remove(n);
            try { if (Directory.Exists(NoteDir(n))) Directory.Delete(NoteDir(n), true); } catch { }
        }
        else
        {
            n.DeletedFrom = n.Folder;
            n.Folder = DeletedFolder;
            n.Deleted = DateTime.Now;
            n.Pinned = false;
        }
        Save();
    }

    public static void Recover(NoteInfo n)
    {
        n.Folder = Folders.Any(f => f.Id == n.DeletedFrom) ? n.DeletedFrom : DefaultFolder;
        n.Deleted = null;
        n.DeletedFrom = null;
        Save();
    }

    public static void Move(NoteInfo n, string folder)
    {
        if (folder == DeletedFolder) { Delete(n); return; }
        if (Folders.All(f => f.Id != folder)) return;
        n.Folder = folder;
        n.Deleted = null;
        Save();
    }

    public static NoteFolder AddFolder(string name)
    {
        Load();
        string unique = name;
        for (int i = 2; Folders.Any(f => string.Equals(f.Name, unique, StringComparison.CurrentCultureIgnoreCase)); i++) unique = $"{name} {i}";
        var f = new NoteFolder { Id = Guid.NewGuid().ToString("N")[..12], Name = unique, Created = DateTime.Now };
        Folders.Add(f);
        Save();
        return f;
    }

    public static void RenameFolder(NoteFolder f, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        f.Name = name.Trim();
        Save();
    }

    /// <summary>A folder deleted: its notes go to Recently Deleted.</summary>
    public static void DeleteFolder(NoteFolder f)
    {
        if (f.Id == DefaultFolder) return;
        foreach (var n in Notes.Where(n => n.Folder == f.Id).ToList()) { n.DeletedFrom = DefaultFolder; n.Folder = DeletedFolder; n.Deleted = DateTime.Now; n.Pinned = false; }
        Folders.Remove(f);
        Save();
    }

    /// <summary>Notes deleted more than 30 days ago are gone for good.</summary>
    static void Purge()
    {
        foreach (var n in Notes.Where(n => n.Folder == DeletedFolder && n.Deleted is DateTime d && (DateTime.Now - d).TotalDays > 30).ToList())
        {
            Notes.Remove(n);
            try { if (Directory.Exists(NoteDir(n))) Directory.Delete(NoteDir(n), true); } catch { }
        }
    }

    public static IEnumerable<NoteInfo> InFolder(string folder) => folder switch
    {
        AllFolder => Notes.Where(n => n.Folder != DeletedFolder),
        _ => Notes.Where(n => n.Folder == folder),
    };

    // ------------------------------------------------------------------ pictures

    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".jfif", ".ico" };
    public static bool IsImageFile(string path) => ImageExts.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    /// <summary>A picture put in a note: copied into the note's folder (a very large one made smaller). Returns its file
    /// name and size, or null if it isn't a picture this computer can read.</summary>
    public static (string file, int w, int h)? AddImage(NoteInfo n, string sourcePath)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(sourcePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return AddImage(n, frame, Path.GetExtension(sourcePath), File.ReadAllBytes(sourcePath));
        }
        catch (Exception ex) { App.Log($"notes picture {Path.GetFileName(sourcePath)}: {ex.Message}"); return null; }
    }

    public static (string file, int w, int h)? AddImage(NoteInfo n, BitmapSource bmp, string ext = ".png", byte[] original = null)
    {
        try
        {
            Directory.CreateDirectory(NoteDir(n));
            ext = (ext ?? ".png").ToLowerInvariant();
            const int max = 2400;
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            string name = "img-" + Guid.NewGuid().ToString("N")[..10];
            if (original != null && w <= max && h <= max && ext is ".png" or ".jpg" or ".jpeg" or ".gif")
            {
                // kept exactly as it was (no re-encoding)
                string file = name + (ext == ".jpeg" ? ".jpg" : ext);
                File.WriteAllBytes(Path.Combine(NoteDir(n), file), original);
                return (file, w, h);
            }
            BitmapSource src = bmp;
            if (w > max || h > max)
            {
                double k = Math.Min((double)max / w, (double)max / h);
                var scaled = new TransformedBitmap(bmp, new ScaleTransform(k, k));
                src = scaled;
                w = scaled.PixelWidth; h = scaled.PixelHeight;
            }
            BitmapEncoder enc = ext is ".jpg" or ".jpeg" or ".jfif" or ".heic" ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            string outFile = name + (enc is JpegBitmapEncoder ? ".jpg" : ".png");
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(Path.Combine(NoteDir(n), outFile))) enc.Save(fs);
            return (outFile, w, h);
        }
        catch (Exception ex) { App.Log("notes picture: " + ex.Message); return null; }
    }

    static readonly Dictionary<string, BitmapSource> _thumbs = new();

    /// <summary>A small copy of a note's picture (the list's thumbnails), loaded once.</summary>
    public static BitmapSource Thumbnail(NoteInfo n, int px = 96)
    {
        string path = ImagePath(n, n.Image);
        if (path == null || !File.Exists(path)) return null;
        string key = path + "|" + px;
        if (_thumbs.TryGetValue(key, out var b)) return b;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = px;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return _thumbs[key] = bi;
        }
        catch { return null; }
    }
}
