using System.Text;
using System.Windows;
using MacShell.Services;

namespace MacShell.Finder;

/// <summary>
/// The Windows Recycle Bin behind MacShell's Trash. A deleted item lives in "&lt;drive&gt;\$Recycle.Bin\&lt;user&gt;\" as a
/// "$R…" file or folder, beside a "$I…" record of where it came from. Items are put back with Windows' own Restore (the
/// "undelete" verb), which also clears that record; an item dragged out of the Trash is put back and then moved to
/// where it was dropped, like dragging out of the Trash on a Mac.
/// </summary>
public static class RecycleBin
{
    /// <summary>A path inside a Recycle Bin.</summary>
    public static bool Contains(string path) =>
        path != null && path.IndexOf("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Where a recycled item was deleted from (its whole original path), read from its "$I…" record.</summary>
    public static string OriginalPath(string recycled)
    {
        try
        {
            string p = recycled.TrimEnd('\\');
            string name = Path.GetFileName(p);
            if (!name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] b = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(p)!, "$I" + name[2..]));
            if (b.Length < 28) return null;
            // version 2 (Windows 10+): the path's length (characters) at 24, the path at 28; version 1: the path at 24, 260 characters
            string path = BitConverter.ToInt64(b, 0) == 2
                ? Encoding.Unicode.GetString(b, 28, Math.Min(BitConverter.ToInt32(b, 24) * 2, b.Length - 28))
                : Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            int nul = path.IndexOf('\0');
            path = nul >= 0 ? path[..nul] : path;
            return Path.IsPathFullyQualified(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Windows' Restore on these recycled items: each goes back where it was deleted from (Windows asks first if
    /// another item has taken its place). Returns when it's done.</summary>
    public static void Restore(ICollection<string> recycled)
    {
        if (recycled.Count == 0) return;
        var want = new HashSet<string>(recycled.Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        var t = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic bin = shell.NameSpace(10);
                var found = new List<object>();
                foreach (dynamic it in bin.Items())
                {
                    string p = it.Path;
                    if (p != null && want.Contains(p.TrimEnd('\\'))) found.Add(it);
                }
                foreach (dynamic it in found)
                {
                    try { it.InvokeVerb("undelete"); } catch { }
                }
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    /// <summary>
    /// Items dragged out of the Trash into <paramref name="folder"/>: put back, then moved there. One whose original
    /// place has been taken by another item stays in the Trash (putting it back there would mean replacing that item).
    /// </summary>
    public static void MoveOut(IEnumerable<string> recycled, string folder)
    {
        var plan = recycled.Select(r => (recycled: r, original: OriginalPath(r))).Where(x => x.original != null).ToList();
        var taken = plan.Where(x => File.Exists(x.original) || Directory.Exists(x.original)).ToList();
        var go = plan.Except(taken).ToList();
        // the folders Windows will make to put them back (removed again if they end up empty)
        var made = new List<string>();
        foreach (var x in go)
            for (string d = Path.GetDirectoryName(x.original); d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
                if (!made.Contains(d, StringComparer.OrdinalIgnoreCase)) made.Add(d);
        var ui = Application.Current.Dispatcher;
        Task.Run(() =>
        {
            Restore(go.Select(x => x.recycled).ToList());
            WaitFor(go.Select(x => x.original).ToList(), TimeSpan.FromSeconds(5));
        }).ContinueWith(_ => ui.BeginInvoke(() =>
        {
            var back = go.Select(x => x.original).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            var move = back.Where(p => !string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (move.Count > 0) FileOps.CopyOrMove(move, folder, move: true);
            foreach (var d in made.OrderByDescending(d => d.Length))
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            if (taken.Count > 0)
            {
                string names = string.Join(", ", taken.Select(x => $"“{Path.GetFileName(x.original.TrimEnd('\\'))}”"));
                ShellHost.ShowAlert(taken.Count == 1 ? $"{names} couldn’t be moved out of the Trash." : $"{taken.Count} items couldn’t be moved out of the Trash.",
                    "An item with the same name is already where it was deleted from. Rename or move that item first, then try again.");
            }
        }));
    }

    static void WaitFor(List<string> paths, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until && !paths.All(p => File.Exists(p) || Directory.Exists(p))) Thread.Sleep(50);
    }
}
