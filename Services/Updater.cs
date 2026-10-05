using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;

namespace MacShell.Services;

/// <summary>
/// Software Update from GitHub releases (github.com/benrobertson973/MacShell). Release builds — made by the
/// GitHub Actions workflow when a version tag is pushed — check a few minutes after start and then every few
/// hours, download a newer MacShell.exe next to the running one, swap it in (a running exe can be renamed,
/// not overwritten) and offer to restart. Development builds made on this PC never update themselves.
/// </summary>
public static class Updater
{
    const string Owner = "benrobertson973", Repo = "MacShell";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string VersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>"release" for builds made by GitHub Actions, "dev" for builds made locally.</summary>
    public static bool IsReleaseBuild => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "UpdateChannel" && a.Value == "release");

    /// <summary>Version that has been downloaded and swapped in, waiting for a restart.</summary>
    public static string Staged { get; private set; }
    public static event Action StatusChanged;
    public static string Status { get; private set; } = "";

    static DispatcherTimer _timer;
    static bool _busy, _asked;
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacShell-Updater");
        return c;
    }

    public static void Start()
    {
        CleanUpOld();
        if (!IsReleaseBuild) { SetStatus("Development build — updates are installed by hand."); return; }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timer.Tick += (_, _) => { _timer.Interval = TimeSpan.FromHours(4); _ = CheckAsync(userInitiated: false); };
        _timer.Start();
    }

    static void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    /// <summary>Removes the previous exe left behind by the last update.</summary>
    static void CleanUpOld()
    {
        try { var old = Environment.ProcessPath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
    }

    public static async Task CheckAsync(bool userInitiated)
    {
        if (_busy) return;
        if (Staged != null) { if (userInitiated) AskRestart(); return; }
        _busy = true;
        try
        {
            SetStatus("Checking for updates…");
            // MACSHELL_UPDATE_API: test override (a local server standing in for GitHub)
            string api = Environment.GetEnvironmentVariable("MACSHELL_UPDATE_API") ?? $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(api));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= Normalize(CurrentVersion))
            {
                SetStatus($"MacShell {VersionText} is up to date.");
                if (userInitiated) ShellHost.ShowAlert("MacShell is up to date", $"Version {VersionText} is the newest version.");
                return;
            }
            if (!IsReleaseBuild)
            {
                SetStatus($"Version {tag.TrimStart('v')} is on GitHub (this is a development build).");
                if (userInitiated) ShellHost.ShowAlert("Development build", $"Version {tag.TrimStart('v')} is available on GitHub, but development builds aren’t updated automatically.");
                return;
            }
            string url = null; long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), "MacShell.exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.GetProperty("size").GetInt64();
                }
            if (url == null) { SetStatus("The newest release has no MacShell.exe."); return; }
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            SetStatus($"Downloading MacShell {tag.TrimStart('v')}…");
            string exe = Environment.ProcessPath;
            string tmp = exe + ".new";
            try
            {
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs);
                }
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus($"Version {tag.TrimStart('v')} is available, but MacShell’s folder can’t be written to.");
                if (userInitiated || !_asked) { _asked = true; OfferManualDownload(tag); }
                return;
            }
            var fi = new FileInfo(tmp);
            if (fi.Length != size || !StartsWithMZ(tmp)) { File.Delete(tmp); SetStatus("The download was incomplete; MacShell will try again later."); return; }

            // swap: the running exe can be renamed but not overwritten
            File.Move(exe, exe + ".old", true);
            try { File.Move(tmp, exe); }
            catch { File.Move(exe + ".old", exe, true); throw; }
            Staged = tag.TrimStart('v');
            SetStatus($"MacShell {Staged} is installed — restart MacShell to use it.");
            _notes = notes;
            AskRestart();
        }
        catch (Exception ex)
        {
            SetStatus("Couldn’t check for updates (no internet?).");
            if (userInitiated) ShellHost.ShowAlert("Couldn’t check for updates", ex.Message);
        }
        finally { _busy = false; }
    }

    static string _notes = "";

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool StartsWithMZ(string path)
    {
        try { using var f = File.OpenRead(path); return f.ReadByte() == 'M' && f.ReadByte() == 'Z'; } catch { return false; }
    }

    static void AskRestart()
    {
        string what = string.IsNullOrWhiteSpace(_notes) ? "" : "\n\n" + (_notes.Length > 400 ? _notes[..400] + "…" : _notes);
        if (ShellHost.Alert($"MacShell {Staged} is ready", $"Restart MacShell now to start using it? Your apps stay open.{what}", "Later", "Restart Now") == "Restart Now")
            Restart();
    }

    static void OfferManualDownload(string tag)
    {
        if (ShellHost.Alert($"MacShell {tag.TrimStart('v')} is available", "MacShell can’t update itself where it’s installed. Download the new version from GitHub?", "Later", "Download") == "Download")
            try { Process.Start(new ProcessStartInfo(ReleasesPage + "/latest") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Starts the new exe (it waits for this process to exit) and quits.</summary>
    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false }); }
        catch { return; }
        ShellHost.Quit();
    }
}
