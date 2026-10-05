using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using MacShell.Native;

namespace MacShell.Services;

public class AppEntry
{
    public string Name { get; set; }
    public string ParsingName { get; set; }
    public string TargetPath { get; set; }
    public bool IsPackaged => ParsingName?.Contains('!') == true;
    public string IconSource => "shell:AppsFolder\\" + ParsingName;
    public override string ToString() => Name;
}

/// <summary>Everything in shell:AppsFolder (Start menu apps, Store apps, PWAs).</summary>
public static class AppCatalog
{
    public static List<AppEntry> Apps { get; private set; } = new();
    public static bool IsLoaded { get; private set; }
    public static event Action Loaded;
    static readonly Regex Junk = new(@"\b(uninstall|uninstaller|readme|read me|release notes|documentation|license|website|help|manual|changelog)\b", RegexOptions.IgnoreCase);
    static readonly string[] JunkExt = { ".url", ".html", ".htm", ".chm", ".txt", ".pdf", ".rtf", ".md", ".ini", ".log", ".xml" };

    public static Task LoadAsync()
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            var list = new List<AppEntry>();
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    try
                    {
                        string name = item.Name;
                        string path = item.Path;
                        string target = null;
                        try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                        name = Regex.Replace(name, @"\s{2,}", " ").Trim();
                        if (Junk.IsMatch(name)) continue;
                        string probe = target ?? path;
                        if (JunkExt.Any(e => probe.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                        if (probe.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new AppEntry { Name = name, ParsingName = path, TargetPath = ResolveKnownFolder(target) });
                    }
                    catch { }
                }
            }
            catch { }
            // de-duplicate by identity, not by name: two different apps may share a name (e.g. PWAs from two browsers)
            list = list.GroupBy(a => a.ParsingName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                       .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                Apps = list;
                _exeCache.Clear();
                IsLoaded = true;
                Loaded?.Invoke();
                tcs.TrySetResult();
            });
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    static string ResolveKnownFolder(string p)
    {
        if (string.IsNullOrEmpty(p) || !p.StartsWith("{")) return p;
        int end = p.IndexOf('}');
        if (end < 0 || !Guid.TryParse(p[1..end], out var g)) return p;
        string root = NativeMethods.GetKnownFolder(g);
        return root == null ? p : root + p[(end + 1)..];
    }

    public static AppEntry FindByParsingName(string pn) =>
        pn == null ? null : Apps.FirstOrDefault(a => string.Equals(a.ParsingName, pn, StringComparison.OrdinalIgnoreCase));

    static readonly Dictionary<string, AppEntry> _exeCache = new(StringComparer.OrdinalIgnoreCase);

    public static AppEntry FindByExe(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        if (_exeCache.TryGetValue(exe, out var cached)) return cached;
        var match = Apps.FirstOrDefault(a => string.Equals(a.TargetPath, exe, StringComparison.OrdinalIgnoreCase));
        if (match == null && exe.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
        {
            // packaged app exe: ...\WindowsApps\<Name>_<Version>_<Arch>_<Resource>_<PublisherId>\app.exe → family "<Name>_<PublisherId>"
            string folder = exe.Split('\\').SkipWhile(s => !s.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            var parts = folder?.Split('_');
            if (parts is { Length: >= 5 })
            {
                string family = parts[0] + "_" + parts[^1] + "!";
                match = Apps.FirstOrDefault(a => a.ParsingName != null && a.ParsingName.StartsWith(family, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (match == null)
        {
            // Squirrel-style installs (Claude, Discord, Slack, Teams…): the shortcut targets <root>\app.exe
            // but the app runs from <root>\app-1.2.3\app.exe — same app.
            string name = Path.GetFileName(exe);
            match = Apps.FirstOrDefault(a => a.TargetPath != null
                && string.Equals(Path.GetFileName(a.TargetPath), name, StringComparison.OrdinalIgnoreCase)
                && exe.StartsWith(Path.GetDirectoryName(a.TargetPath) + "\\", StringComparison.OrdinalIgnoreCase));
        }
        if (IsLoaded) _exeCache[exe] = match;
        return match;
    }

    public static AppEntry FindByName(params string[] names)
    {
        foreach (var n in names)
        {
            var a = Apps.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (a != null) return a;
        }
        return null;
    }

    public static string DefaultBrowserExe()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string progId = k?.GetValue("ProgId") as string;
            if (progId == null) return null;
            using var c = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            string cmd = c?.GetValue(null) as string;
            if (cmd == null) return null;
            var m = Regex.Match(cmd, "^\"([^\"]+)\"|^(\\S+)");
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        catch { return null; }
    }

    /// <summary>Launches a dock/launchpad target (AppsFolder parsing name, exe/file path, or internal:*).</summary>
    public static bool Launch(string target, string args = null)
    {
        if (string.IsNullOrEmpty(target)) return false;
        if (target.StartsWith("internal:")) { ShellHost.OpenInternal(target); return true; }
        try
        {
            ProcessStartInfo psi;
            if (File.Exists(target) || Directory.Exists(target))
                psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "", WorkingDirectory = Path.GetDirectoryName(target) ?? "" };
            else
                psi = new ProcessStartInfo("shell:AppsFolder\\" + target) { UseShellExecute = true };
            Process.Start(psi);
            Settings.AddRecentApp(target);
            return true;
        }
        catch (Exception ex)
        {
            ShellHost.ShowAlert("The application can’t be opened.", ex.Message);
            return false;
        }
    }

    public static void OpenFile(string path)
    {
        // the app chosen in MacShell, if any; otherwise Windows decides
        if (File.Exists(path) && MacShell.Services.OpenWith.OpenDefault(path)) return;   // MacShell's own default (Get Info / Always Open With / Preview for pictures)
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            if (File.Exists(path)) Settings.AddRecentDoc(path);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1155)
        {
            OpenWith(path);
        }
        catch (Exception ex)
        {
            ShellHost.ShowAlert($"The document “{Path.GetFileName(path)}” could not be opened.", ex.Message);
        }
    }

    public static void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"")); } catch { }
    }

    public static void OpenFilesWith(AppEntryOrTarget app, IEnumerable<string> files)
    {
        string exe = app.ExePath;
        var list = files.ToList();
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, string.Join(" ", list.Select(f => $"\"{f}\""))) { UseShellExecute = false });
                return;
            }
            catch { }
        }
        foreach (var f in list) OpenFile(f);
    }
}

public record AppEntryOrTarget(string Target, string ExePath);
