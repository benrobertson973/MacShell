using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Finder;

/// <summary>One file, folder, drive, app or trash entry shown by Finder.</summary>
public class FileItem : INotifyPropertyChanged
{
    public string Name { get => _name; set { _name = value; OnChanged(); OnChanged(nameof(DisplayName)); } }
    string _name;
    public string FullPath { get; set; }
    public bool IsFolder { get; set; }
    public bool IsDrive { get; set; }
    public bool IsApp { get; set; }             // Launchpad/Applications entry
    public string AppTarget { get; set; }
    public bool IsTrashItem { get; set; }
    public string OriginalLocation { get; set; }
    public long Size { get; set; } = -1;
    public DateTime Modified { get; set; }
    public DateTime Created { get; set; }
    public bool IsHidden { get; set; }
    public int Depth { get; set; }
    public bool IsExpanded { get => _expanded; set { _expanded = value; OnChanged(); } }
    bool _expanded;
    public FileItem ParentItem { get; set; }

    public string Extension => IsFolder || IsApp ? "" : Path.GetExtension(FullPath ?? "") ?? "";

    public string DisplayName
    {
        get
        {
            if (IsFolder || IsApp || IsDrive) return Name;
            string ext = Path.GetExtension(Name);
            // aliases never show their extension on a Mac (and Windows hides .lnk too)
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return Name[..^ext.Length];
            if (Settings.Current.FinderShowExtensions) return Name;
            return string.IsNullOrEmpty(ext) ? Name : Name[..^ext.Length];
        }
    }

    public bool IsSelected { get => _sel; set { if (_sel != value) { _sel = value; OnChanged(); } } }
    bool _sel;
    public bool IsRenaming { get => _ren; set { _ren = value; OnChanged(); } }
    bool _ren;
    public bool IsCut { get => _cut; set { _cut = value; OnChanged(); } }
    bool _cut;
    public bool IsDropTarget { get => _drop; set { _drop = value; OnChanged(); } }
    bool _drop;
    /// <summary>Rounded corners for contiguous selection blocks in list views.</summary>
    public System.Windows.CornerRadius SelCorner { get => _corner; set { if (_corner != value) { _corner = value; OnChanged(); } } }
    System.Windows.CornerRadius _corner = new(5);

    public string Kind
    {
        get
        {
            if (IsDrive) return "Volume";
            if (IsApp) return "Application";
            if (IsFolder) return "Folder";
            string ext = Extension.ToLowerInvariant();
            if (ext == ".exe") return "Application";
            if (ext == ".lnk" || ext == ".url") return "Alias";
            return ShellIcons.GetTypeName(FullPath, false);
        }
    }

    public string SizeText => IsFolder || IsApp ? "--" : Size < 0 ? "--" : FormatSize(Size);
    public string ModifiedText => Modified == default ? "--" : FormatDate(Modified);
    public string CreatedText => Created == default ? "--" : FormatDate(Created);

    List<string> _tags;
    public List<string> Tags => _tags ??= Settings.Current.Tags.TryGetValue(FullPath ?? "", out var t) ? t : new List<string>();
    public bool HasTags => Tags.Count > 0;
    public IEnumerable<Color> TagColors => Tags.Select(t => Theme.TagColors.FirstOrDefault(c => c.id == t).color);
    public void RefreshTags() { _tags = null; OnChanged(nameof(Tags)); OnChanged(nameof(HasTags)); OnChanged(nameof(TagColors)); }

    // ---------------------------------------------------------------- icons
    ImageSource _icon, _thumb;
    int _iconPx;
    public ImageSource Icon
    {
        get
        {
            if (_icon == null) RequestIcon(64);
            return _icon;
        }
        private set { _icon = value; OnChanged(); }
    }

    /// <summary>Large preview (thumbnail) for gallery / column preview / Quick Look.</summary>
    public ImageSource Thumbnail
    {
        get
        {
            if (_thumb == null) RequestThumb();
            return _thumb ?? Icon;
        }
    }

    // ---------------------------------------------------------------- desktop stacks
    public bool IsStack { get; set; }
    public List<FileItem> StackItems { get; set; }
    public bool StackExpanded { get => _stackExp; set { _stackExp = value; OnChanged(); } }
    bool _stackExp;
    public void SetIcon(ImageSource img) { _icon = img; _iconPx = 9999; OnChanged(nameof(Icon)); }

    public void RequestIcon(int px)
    {
        if (IsStack) return;
        if (_icon != null && _iconPx >= px) return;
        _iconPx = px;
        if (IsFolder && !IsApp)
        {
            _icon = MacIcons.Folder(MacIcons.FolderGlyphFor(FullPath));
            OnChanged(nameof(Icon));
            return;
        }
        if (IsDrive)
        {
            _icon = MacIcons.Drive(!string.Equals(Path.GetPathRoot(Environment.SystemDirectory), FullPath, StringComparison.OrdinalIgnoreCase));
            OnChanged(nameof(Icon));
            return;
        }
        _icon ??= MacIcons.GenericDocument;
        string src = IsApp ? "shell:AppsFolder\\" + AppTarget : FullPath;
        bool thumb = !IsApp && ShellIcons.IsThumbnailType(FullPath);
        int size = px <= 32 ? 48 : px <= 64 ? 128 : 256;
        ShellIcons.Load(src, size, thumb, b => { if (b != null) Icon = b; });
    }

    void RequestThumb()
    {
        if (IsFolder || IsDrive) { _thumb = Icon; return; }
        string src = IsApp ? "shell:AppsFolder\\" + AppTarget : FullPath;
        ShellIcons.Load(src, 512, !IsApp, b => { if (b != null) { _thumb = b; OnChanged(nameof(Thumbnail)); } }, true);
    }

    // ---------------------------------------------------------------- factory
    public static FileItem FromInfo(FileSystemInfo fi, int depth = 0)
    {
        bool dir = fi is DirectoryInfo;
        var item = new FileItem
        {
            FullPath = fi.FullName,
            IsFolder = dir,
            Depth = depth,
        };
        item._name = fi.Name;
        try
        {
            item.Modified = fi.LastWriteTime;
            item.Created = fi.CreationTime;
            item.IsHidden = (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            if (fi is FileInfo f) item.Size = f.Length;
        }
        catch { }
        return item;
    }

    // ---------------------------------------------------------------- formatting
    public static string FormatSize(long bytes)
    {
        // macOS uses decimal units (1 KB = 1000 bytes)
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int u = -1;
        do { v /= 1000; u++; } while (v >= 1000 && u < units.Length - 1);
        return (v >= 100 || u == 0 ? Math.Round(v).ToString("0") : v.ToString("0.#")) + " " + units[u];
    }

    public static string FormatDate(DateTime d)
    {
        var today = DateTime.Today;
        string time = d.ToString("h:mm tt");
        if (d.Date == today) return "Today at " + time;
        if (d.Date == today.AddDays(-1)) return "Yesterday at " + time;
        return d.ToString("d MMM yyyy") + " at " + time;
    }

    public event PropertyChangedEventHandler PropertyChanged;
    public void OnChanged([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public override string ToString() => Name;
}

public static class FinderLocation
{
    public const string Recents = "::recents";
    public const string Applications = "::applications";
    public const string Computer = "::computer";
    public const string Trash = "::trash";
    public const string TagPrefix = "::tag:";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Downloads => NativeMethods.GetKnownFolder(NativeMethods.FOLDERID_Downloads) ?? Path.Combine(Home, "Downloads");
    public static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string Movies => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public static string ICloud => Environment.GetEnvironmentVariable("OneDrive");

    public static bool IsVirtual(string loc) => loc != null && loc.StartsWith("::");

    public static string DisplayName(string loc)
    {
        if (loc == null) return "";
        switch (loc)
        {
            case Recents: return "Recents";
            case Applications: return "Applications";
            case Computer: return Environment.MachineName;
            case Trash: return "Trash";
        }
        if (loc.StartsWith(TagPrefix)) return Theme.TagColors.FirstOrDefault(t => t.id == loc[TagPrefix.Length..]).name ?? "Tag";
        if (loc.StartsWith("::search:")) return "Searching “" + loc[9..] + "”";
        string trimmed = loc.TrimEnd('\\');
        if (trimmed.Length <= 2 && trimmed.EndsWith(":"))
        {
            try { var di = new DriveInfo(trimmed); return string.IsNullOrEmpty(di.VolumeLabel) ? (di.DriveType == DriveType.Fixed && trimmed.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\')) ? "Macintosh HD" : "Local Disk") : di.VolumeLabel; }
            catch { return trimmed; }
        }
        return Path.GetFileName(trimmed);
    }
}
