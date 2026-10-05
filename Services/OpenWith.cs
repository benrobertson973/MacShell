using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Native;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MacShell.Services;

/// <summary>An app that can open a document (an Open With choice).</summary>
public sealed class OwApp
{
    /// <summary>"internal:preview", "assoc:&lt;handler name&gt;", "app:&lt;AppsFolder name&gt;", "exe:&lt;path&gt;" or "windows" (Windows' own default).</summary>
    public string Key;
    public string Name;
    public string IconSource;   // for ShellIcons (null: MacShell's own app, see MacIcons.ForInternal)
    public bool Recommended;
}

/// <summary>
/// Open With, like macOS: the apps that can open a document (Windows' registered handlers, plus MacShell's own
/// Preview for pictures), opening a document with any of them, and MacShell's own defaults — per document (Get Info)
/// or for every document of a kind ("Change All…", "Always Open With"). Windows doesn't let programs change its own
/// defaults, so these apply to everything opened through MacShell (Finder, the desktop, Spotlight, the Dock …).
/// </summary>
public static class OpenWith
{
    public const string PreviewKey = "internal:preview";

    static string Ext(string path) => Path.GetExtension(path ?? "").ToLowerInvariant();

    // ------------------------------------------------------------------ the apps that can open a kind of document

    sealed class Handler
    {
        public OwApp App;
        public IAssocHandler Com;
    }

    static readonly Dictionary<string, (DateTime at, List<Handler> list)> Cache = new();

    static List<Handler> Handlers(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return new List<Handler>();
        if (Cache.TryGetValue(ext, out var c) && (DateTime.Now - c.at).TotalSeconds < 30) return c.list;
        var list = new List<Handler>();
        try
        {
            if (SHAssocEnumHandlers(ext, 0 /*ASSOC_FILTER_NONE*/, out var en) == 0 && en != null)
            {
                var one = new IAssocHandler[1];
                while (en.Next(1, one, out uint got) == 0 && got == 1)
                {
                    var h = one[0];
                    if (h.GetName(out string name) != 0 || h.GetUIName(out string ui) != 0 || string.IsNullOrWhiteSpace(ui)) continue;
                    string icon = name.Contains('!') ? "shell:AppsFolder\\" + name
                        : File.Exists(name) ? name
                        : AppCatalog.Apps.FirstOrDefault(a => string.Equals(a.Name, ui, StringComparison.OrdinalIgnoreCase))?.IconSource;
                    list.Add(new Handler { Com = h, App = new OwApp { Key = "assoc:" + name, Name = ui, IconSource = icon, Recommended = h.IsRecommended() == 0 } });
                }
            }
        }
        catch { }
        // (the same app can be registered more than once: keep the recommended one)
        list = list.GroupBy(h => h.App.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(h => h.App.Recommended).First()).ToList();
        Cache[ext] = (DateTime.Now, list);
        return list;
    }

    /// <summary>
    /// Store apps registered for the kind (Photos, Media Player …): Windows' handler list leaves their names blank, so they
    /// come from the extension's OpenWithProgids and the app's AppUserModelID (opened with ActivateForFile).
    /// </summary>
    static List<OwApp> PackagedFor(string ext)
    {
        var list = new List<OwApp>();
        if (string.IsNullOrEmpty(ext)) return list;
        try
        {
            var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids"), Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{ext}\OpenWithProgids") })
                using (root)
                    if (root != null) foreach (var n in root.GetValueNames()) if (!string.IsNullOrEmpty(n)) progIds.Add(n);
            foreach (var pid in progIds)
            {
                using var app = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{pid}\Application");
                if (app?.GetValue("AppUserModelID") is not string aumid) continue;
                var entry = AppCatalog.FindByParsingName(aumid);
                if (entry == null) continue;
                list.Add(new OwApp { Key = "app:" + aumid, Name = entry.Name, IconSource = entry.IconSource, Recommended = true });
            }
        }
        catch { }
        return list;
    }

    /// <summary>Recommended apps first (then the rest), Preview included for pictures; no duplicates.</summary>
    public static List<OwApp> AppsFor(string path, bool recommendedOnly)
    {
        var apps = new List<OwApp>();
        if (Apps.Preview.PvFile.IsImage(path)) apps.Add(PreviewApp);
        apps.AddRange(PackagedFor(Ext(path)));
        foreach (var h in Handlers(Ext(path)))
            if (!recommendedOnly || h.App.Recommended) apps.Add(h.App);
        return apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(a => a.Recommended).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static readonly OwApp PreviewApp = new() { Key = PreviewKey, Name = "Preview", Recommended = true };

    /// <summary>Windows' own default for this kind of document (its name), or null.</summary>
    public static string WindowsDefaultName(string ext)
    {
        try
        {
            uint len = 512;
            var sb = new StringBuilder((int)len);
            if (AssocQueryString(0, 4 /*ASSOCSTR_FRIENDLYAPPNAME*/, ext, null, sb, ref len) != 0) return null;
            string s = sb.ToString();
            if (s.Length == 0 || s.Contains("Pick an app", StringComparison.OrdinalIgnoreCase) || s.Contains("OpenWith", StringComparison.OrdinalIgnoreCase)) return null;
            return s;
        }
        catch { return null; }
    }

    static OwApp WindowsDefault(string path)
    {
        string name = WindowsDefaultName(Ext(path));
        if (name == null) return null;
        var h = AppsFor(path, false).FirstOrDefault(x => x.Key != PreviewKey && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return h ?? new OwApp { Key = "windows", Name = name };
    }

    // ------------------------------------------------------------------ defaults

    static Dictionary<string, string> ByKind => Settings.Current.OpenWithDefaults ??= new Dictionary<string, string>();
    static Dictionary<string, string> ByFile => Settings.Current.OpenWithFiles ??= new Dictionary<string, string>();

    static string FileKey(string path) => ByFile.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The app this document opens with in MacShell: its own choice (Get Info), its kind's, Preview for pictures, else Windows'.</summary>
    public static OwApp DefaultFor(string path)
    {
        string fk = FileKey(path);
        if (fk != null && Resolve(ByFile[fk], path) is { } f) return f;
        if (ByKind.TryGetValue(Ext(path), out var k) && Resolve(k, path) is { } d) return d;
        if (Apps.Preview.PvFile.IsImage(path)) return PreviewApp;
        return WindowsDefault(path);
    }

    /// <summary>Whether MacShell (rather than Windows) decides how this document opens.</summary>
    public static bool HasMacShellDefault(string path) => FileKey(path) != null || ByKind.ContainsKey(Ext(path)) || Apps.Preview.PvFile.IsImage(path);

    public static void SetDefaultForKind(string path, OwApp app)
    {
        string ext = Ext(path);
        if (string.IsNullOrEmpty(ext)) return;
        ByKind[ext] = app.Key;
        // documents of this kind that had their own choice now follow it too (Change All)
        foreach (var k in ByFile.Keys.Where(k => Ext(k) == ext).ToList()) ByFile.Remove(k);
        Settings.Save();
    }

    public static void SetDefaultForFile(string path, OwApp app)
    {
        if (FileKey(path) is { } old) ByFile.Remove(old);
        if (DefaultForKindOnly(path)?.Key != app.Key) ByFile[path] = app.Key;
        Settings.Save();
    }

    static OwApp DefaultForKindOnly(string path)
    {
        if (ByKind.TryGetValue(Ext(path), out var k) && Resolve(k, path) is { } d) return d;
        if (Apps.Preview.PvFile.IsImage(path)) return PreviewApp;
        return WindowsDefault(path);
    }

    static OwApp Resolve(string key, string path)
    {
        if (key == PreviewKey) return PreviewApp;
        if (key == "windows") return WindowsDefault(path);
        if (key.StartsWith("assoc:")) return Handlers(Ext(path)).FirstOrDefault(h => h.App.Key == key)?.App;
        if (key.StartsWith("app:"))
        {
            var a = AppCatalog.FindByParsingName(key[4..]);
            return a == null ? null : new OwApp { Key = key, Name = a.Name, IconSource = a.IconSource };
        }
        if (key.StartsWith("exe:") && File.Exists(key[4..]))
            return new OwApp { Key = key, Name = FileVersionInfo.GetVersionInfo(key[4..]).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(key[4..]), IconSource = key[4..] };
        return null;
    }

    // ------------------------------------------------------------------ opening

    /// <summary>Opens with MacShell's default when it has one; false = let Windows open it.</summary>
    public static bool OpenDefault(string path)
    {
        if (!HasMacShellDefault(path)) return false;
        var app = DefaultFor(path);
        return app != null && Open(path, app);
    }

    public static bool Open(string path, OwApp app)
    {
        try
        {
            string key = app.Key;
            if (key == PreviewKey) { Apps.Preview.PreviewWindow.Open(path); return true; }
            if (key == "windows") { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); return Done(path); }
            if (key.StartsWith("assoc:"))
            {
                var h = Handlers(Ext(path)).FirstOrDefault(x => x.App.Key == key);
                if (h == null) return false;
                var dobj = DataObjectFor(path);
                if (dobj == null || h.Com.Invoke(dobj) != 0) return false;
                return Done(path);
            }
            if (key.StartsWith("app:"))
            {
                var a = AppCatalog.FindByParsingName(key[4..]);
                if (a == null) return false;
                if (a.IsPackaged) return ActivateForFile(a.ParsingName, path) && Done(path);
                string exe = a.TargetPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
                if (exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) { Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = true }); return Done(path); }
                Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
                return Done(path);
            }
            if (key.StartsWith("exe:"))
            {
                Process.Start(new ProcessStartInfo(key[4..], $"\"{path}\"") { UseShellExecute = false });
                return Done(path);
            }
        }
        catch (Exception ex)
        {
            ShellHost.ShowAlert($"The document “{Path.GetFileName(path)}” could not be opened with {app.Name}.", ex.Message);
            return true;   // (handled: don't fall through to another app)
        }
        return false;
    }

    static bool Done(string path)
    {
        if (File.Exists(path)) Settings.AddRecentDoc(path);
        return true;
    }

    static ComDataObject DataObjectFor(string path)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o);
            var item = (IShellItem)o;
            var bhid = new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");   // BHID_DataObject
            var riid = new Guid("0000010e-0000-0000-C000-000000000046");   // IDataObject
            item.BindToHandler(IntPtr.Zero, ref bhid, ref riid, out IntPtr p);
            if (p == IntPtr.Zero) return null;
            try { return (ComDataObject)Marshal.GetObjectForIUnknown(p); } finally { Marshal.Release(p); }
        }
        catch { return null; }
    }

    static bool ActivateForFile(string aumid, string path)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o);
            var arrIid = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");   // IShellItemArray
            if (SHCreateShellItemArrayFromShellItem((IShellItem)o, ref arrIid, out IntPtr arr) != 0) return false;
            try
            {
                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                return mgr.ActivateForFile(aumid, arr, "open", out _) == 0;
            }
            finally { Marshal.Release(arr); }
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ menus

    static Image MenuIcon(OwApp app)
    {
        var img = new Image { Width = 16, Height = 16 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (app.Key.StartsWith("internal:")) img.Source = MacIcons.ForInternal(app.Key);
        else if (!string.IsNullOrEmpty(app.IconSource)) ShellIcons.Load(app.IconSource, 32, false, b => { if (b != null) img.Source = b; });
        else img.Source = MacIcons.GenericApp;
        return img;
    }

    /// <summary>
    /// Finder's "Open With" submenu: the default app first, then the others that can open it, then Other….
    /// With Option (Alt) held it is "Always Open With": the choice also becomes the default for the kind.
    /// </summary>
    public static MenuItem Submenu(string path)
    {
        bool always = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var items = new List<object>();
        var def = DefaultFor(path);
        if (def != null)
        {
            items.Add(Mb.Item($"{def.Name} (default)", () => Choose(path, def, always), icon: MenuIcon(def)));
            items.Add(Mb.Sep());
        }
        foreach (var a in AppsFor(path, recommendedOnly: true).Where(a => def == null || !string.Equals(a.Name, def.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var app = a;
            items.Add(Mb.Item(app.Name, () => Choose(path, app, always), icon: MenuIcon(app)));
        }
        if (items.Count > 0 && items[^1] is not Separator) items.Add(Mb.Sep());
        items.Add(Mb.Item("Other…", () => Finder.AppChooserWindow.Choose(path, always)));
        return Mb.Sub(always ? "Always Open With" : "Open With", items.ToArray());
    }

    static void Choose(string path, OwApp app, bool always)
    {
        if (always) SetDefaultForKind(path, app);
        if (!Open(path, app)) AppCatalog.OpenWith(path);
    }

    // ------------------------------------------------------------------ interop

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] rgelt, out uint fetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(ComDataObject dataObject);
        [PreserveSig] int CreateInvoker(ComDataObject dataObject, out IntPtr invoker);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHAssocEnumHandlers(string extra, int filter, out IEnumAssocHandlers enumHandlers);

    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromShellItem(IShellItem item, ref Guid riid, out IntPtr array);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outStr, ref uint outLen);
}
