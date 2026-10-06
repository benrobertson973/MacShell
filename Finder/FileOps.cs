using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using MacShell.Native;
using MacShell.Services;
using static MacShell.Native.NativeMethods;

namespace MacShell.Finder;

/// <summary>File operations (backed by the Windows shell so progress, conflicts and undo behave).</summary>
public static class FileOps
{
    static string Multi(IEnumerable<string> paths) => string.Join("\0", paths) + "\0\0";

    static bool Shell(uint func, IEnumerable<string> from, string to, ushort flags)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = func,
            pFrom = Multi(from),
            pTo = to == null ? null : to + "\0\0",
            fFlags = flags,
        };
        int r = SHFileOperation(ref op);
        return r == 0 && !op.fAnyOperationsAborted;
    }

    public static void MoveToTrash(IEnumerable<string> paths)
    {
        // (items already in the Trash stay as they are)
        var list = paths.Where(p => (File.Exists(p) || Directory.Exists(p)) && !RecycleBin.Contains(p)).ToList();
        if (list.Count == 0) return;
        Shell(FO_DELETE, list, null, (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING));
        Sound.Play("trash");
    }

    public static void CopyOrMove(IEnumerable<string> paths, string destFolder, bool move)
    {
        var list = paths.Where(p => (File.Exists(p) || Directory.Exists(p))).ToList();
        if (list.Count == 0 || destFolder == null) return;
        // dragged out of the Trash: put back, then moved here (under their own names, not the Recycle Bin's)
        var recycled = list.Where(RecycleBin.Contains).ToList();
        if (recycled.Count > 0)
        {
            RecycleBin.MoveOut(recycled, destFolder);
            list = list.Except(recycled).ToList();
            if (list.Count == 0) return;
        }
        // never move a folder into itself
        list = list.Where(p => !destFolder.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), destFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !move).ToList();
        if (list.Count == 0) return;
        bool sameFolder = list.All(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), destFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
        if (sameFolder) flags |= FOF_RENAMEONCOLLISION;
        Shell(move ? FO_MOVE : FO_COPY, list, destFolder, flags);
    }

    /// <summary>macOS drag semantics: same volume = move, different volume = copy (Alt/Ctrl forces copy).</summary>
    public static bool ShouldMove(IEnumerable<string> sources, string dest, DragDropKeyStates keys)
    {
        if ((keys & (DragDropKeyStates.AltKey | DragDropKeyStates.ControlKey)) != 0) return false;
        string destRoot = Path.GetPathRoot(dest) ?? "";
        return sources.All(s => string.Equals(Path.GetPathRoot(s), destRoot, StringComparison.OrdinalIgnoreCase));
    }

    public static string NewFolder(string parent)
    {
        string name = UniqueName(parent, "untitled folder");
        try { Directory.CreateDirectory(Path.Combine(parent, name)); return Path.Combine(parent, name); }
        catch (Exception ex) { ShellHost.ShowAlert("The folder couldn’t be created.", ex.Message); return null; }
    }

    public static string UniqueName(string folder, string baseName, string ext = "")
    {
        string candidate = baseName + ext;
        int i = 2;
        while (File.Exists(Path.Combine(folder, candidate)) || Directory.Exists(Path.Combine(folder, candidate)))
            candidate = $"{baseName} {i++}{ext}";
        return candidate;
    }

    public static string Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return null;
        newName = newName.Trim();
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShellHost.ShowAlert($"The name “{newName}” can’t be used.", "Try using a name with fewer characters, or with no punctuation marks ( \\ / : * ? \" < > | ).");
            return null;
        }
        string dir = Path.GetDirectoryName(path.TrimEnd('\\'));
        string target = Path.Combine(dir!, newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return path;
        try
        {
            bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                ShellHost.ShowAlert($"The name “{newName}” is already taken. Please choose a different name.", "");
                return null;
            }
            if (Directory.Exists(path))
            {
                if (caseOnly) { string tmp = path.TrimEnd('\\') + ".~ren"; Directory.Move(path, tmp); Directory.Move(tmp, target); }
                else Directory.Move(path, target);
            }
            else
            {
                if (caseOnly) { string tmp = path + ".~ren"; File.Move(path, tmp); File.Move(tmp, target); }
                else File.Move(path, target);
            }
            MoveTags(path, target);
            return target;
        }
        catch (Exception ex)
        {
            ShellHost.ShowAlert($"The item “{Path.GetFileName(path)}” couldn’t be renamed.", ex.Message);
            return null;
        }
    }

    static void MoveTags(string from, string to)
    {
        if (Settings.Current.Tags.Remove(from, out var tags)) { Settings.Current.Tags[to] = tags; Settings.Save(false); }
    }

    public static void Duplicate(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                string dir = Path.GetDirectoryName(p.TrimEnd('\\'))!;
                if (Directory.Exists(p))
                {
                    string name = UniqueName(dir, Path.GetFileName(p.TrimEnd('\\')) + " copy");
                    Shell(FO_COPY, new[] { p }, Path.Combine(dir, name), (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR));
                }
                else
                {
                    string ext = Path.GetExtension(p);
                    string name = UniqueName(dir, Path.GetFileNameWithoutExtension(p) + " copy", ext);
                    File.Copy(p, Path.Combine(dir, name));
                }
            }
            catch (Exception ex) { ShellHost.ShowAlert("The item couldn’t be duplicated.", ex.Message); }
        }
    }

    public static string Compress(IList<string> paths)
    {
        if (paths.Count == 0) return null;
        try
        {
            string dir = Path.GetDirectoryName(paths[0].TrimEnd('\\'))!;
            string baseName = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : "Archive";
            string zip = Path.Combine(dir, UniqueName(dir, baseName, ".zip"));
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    string root = Path.GetFileName(p.TrimEnd('\\'));
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        archive.CreateEntryFromFile(f, Path.Combine(root, Path.GetRelativePath(p, f)).Replace('\\', '/'));
                }
                else archive.CreateEntryFromFile(p, Path.GetFileName(p));
            }
            return zip;
        }
        catch (Exception ex) { ShellHost.ShowAlert("The archive couldn’t be created.", ex.Message); return null; }
    }

    public static void Extract(string zip)
    {
        try
        {
            string dir = Path.GetDirectoryName(zip)!;
            string target = Path.Combine(dir, UniqueName(dir, Path.GetFileNameWithoutExtension(zip)));
            ZipFile.ExtractToDirectory(zip, target);
        }
        catch (Exception ex) { ShellHost.ShowAlert("The archive couldn’t be expanded.", ex.Message); }
    }

    public static void MakeAlias(IEnumerable<string> paths)
    {
        try
        {
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            foreach (var p in paths)
            {
                string dir = Path.GetDirectoryName(p.TrimEnd('\\'))!;
                string name = UniqueName(dir, Path.GetFileNameWithoutExtension(p.TrimEnd('\\')) + " alias", ".lnk");
                dynamic sc = wsh.CreateShortcut(Path.Combine(dir, name));
                sc.TargetPath = p;
                sc.Save();
            }
        }
        catch (Exception ex) { ShellHost.ShowAlert("The alias couldn’t be created.", ex.Message); }
    }

    public static string ResolveAlias(string lnk)
    {
        try
        {
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = wsh.CreateShortcut(lnk);
            string t = sc.TargetPath;
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch { return null; }
    }

    public static void EmptyTrash(bool confirm = true)
    {
        if (confirm && ShellHost.Alert("Are you sure you want to permanently erase the items in the Trash?", "You can’t undo this action.", "Cancel", "Empty Trash") != "Empty Trash")
            return;
        SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        Sound.Play("emptytrash");
    }

    // ---------------------------------------------------------------- clipboard (Explorer-compatible)
    public static void CopyToClipboard(IList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        var data = new DataObject();
        var sc = new System.Collections.Specialized.StringCollection();
        sc.AddRange(paths.ToArray());
        data.SetFileDropList(sc);
        var effect = new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5));
        data.SetData("Preferred DropEffect", effect);
        data.SetText(string.Join(Environment.NewLine, paths));
        try { Clipboard.SetDataObject(data, true); } catch { }
    }

    public static (List<string> files, bool cut) ReadClipboard()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return (new List<string>(), false);
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            bool cut = false;
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var b = new byte[4];
                ms.Read(b, 0, 4);
                cut = (BitConverter.ToInt32(b, 0) & 2) != 0;
            }
            return (files, cut);
        }
        catch { return (new List<string>(), false); }
    }

    public static void Paste(string destFolder)
    {
        var (files, cut) = ReadClipboard();
        if (files.Count == 0 || destFolder == null) return;
        CopyOrMove(files, destFolder, cut);
        if (cut) try { Clipboard.Clear(); } catch { }
    }

    public static void ShowWindowsProperties(string path)
    {
        var info = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), lpVerb = "properties", lpFile = path, nShow = 1, fMask = SEE_MASK_INVOKEIDLIST };
        ShellExecuteEx(ref info);
    }

    public static void OpenInTerminal(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe")))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
        catch { }
    }

    public static void SetTag(IEnumerable<string> paths, string tag)
    {
        var list = paths.ToList();
        bool allHave = list.All(p => Settings.Current.Tags.TryGetValue(p, out var t) && t.Contains(tag));
        foreach (var p in list)
        {
            if (!Settings.Current.Tags.TryGetValue(p, out var t)) Settings.Current.Tags[p] = t = new List<string>();
            if (allHave) t.Remove(tag); else if (!t.Contains(tag)) t.Add(tag);
            if (t.Count == 0) Settings.Current.Tags.Remove(p);
        }
        Settings.Save(false);
    }
}

/// <summary>macOS-like UI sounds using Windows' built-in system sounds where available.</summary>
public static class Sound
{
    public static void Play(string what)
    {
        if (!Settings.Current.SoundEffects) return;
        try
        {
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            string file = what switch
            {
                "trash" => "Windows Recycle.wav",
                "emptytrash" => "Windows Recycle.wav",
                "screenshot" => "Windows Notify System Generic.wav",
                "alert" => "Windows Background.wav",
                _ => null,
            };
            if (file == null) return;
            string path = Path.Combine(media, file);
            if (File.Exists(path)) PlaySound(path, IntPtr.Zero, 0x0001 | 0x0002 | 0x00020000); // ASYNC | NODEFAULT | FILENAME
        }
        catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(string file, IntPtr hmod, uint flags);
}
