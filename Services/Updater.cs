using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;

namespace MacShell.Services;

/// <summary>
/// Software Update from GitHub releases (github.com/benrobertson973/MacShell): checks a minute after start
/// and then every 5 minutes, downloads a newer MacShell.exe next to the running one, swaps it in (a running exe
/// can be renamed, not overwritten) and offers to restart.
/// </summary>
public static class Updater
{
    const string Owner = "benrobertson973", Repo = "MacShell";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string VersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";


    /// <summary>Version that has been downloaded and swapped in, waiting for a restart.</summary>
    public static string Staged { get; private set; }
    public static event Action StatusChanged;
    public static string Status { get; private set; } = "";

    static DispatcherTimer _timer;
    static bool _busy, _asked;
    static readonly HttpClient Http = MakeClient(redirects: true);

    static HttpClient MakeClient(bool redirects)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacShell-Updater");
        return c;
    }

    /// <summary>For reading where /releases/latest points (the redirect itself, not the page).</summary>
    static readonly HttpClient NoRedirect = MakeClient(redirects: false);

    /// <summary>The release's notes (one API call, only when there is an update); empty if unavailable.</summary>
    static async Task<string> ReleaseNotes(string tag)
    {
        if (Environment.GetEnvironmentVariable("MACSHELL_UPDATE_BASE") != null) return "Test update.";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}"));
            return doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
    public static void Start()
    {
        CleanUpOld();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => { _timer.Interval = TimeSpan.FromMinutes(5); _ = CheckAsync(userInitiated: false); };   // a minute after start, then every 5 minutes
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
            // github.com/…/releases/latest redirects to …/releases/tag/vX.Y.Z: no API rate limit, so checking every
            // few minutes is fine. (MACSHELL_UPDATE_BASE: test override, a local server standing in for GitHub.)
            string baseUrl = Environment.GetEnvironmentVariable("MACSHELL_UPDATE_BASE") ?? $"https://github.com/{Owner}/{Repo}";
            string tag;
            using (var resp = await NoRedirect.GetAsync(baseUrl + "/releases/latest", HttpCompletionOption.ResponseHeadersRead))
            {
                string loc = resp.Headers.Location?.ToString() ?? "";
                int i = loc.LastIndexOf("/tag/", StringComparison.Ordinal);
                if (i < 0) throw new HttpRequestException("GitHub didn’t say which release is the latest.");
                tag = Uri.UnescapeDataString(loc[(i + 5)..]);
            }
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= Normalize(CurrentVersion))
            {
                SetStatus($"MacShell {VersionText} is up to date.");
                if (userInitiated) ShellHost.ShowAlert("MacShell is up to date", $"Version {VersionText} is the newest version.");
                return;
            }
            string url = $"{baseUrl}/releases/download/{tag}/MacShell.exe";

            SetStatus($"Downloading MacShell {tag.TrimStart('v')}…");
            string exe = Environment.ProcessPath;
            string tmp = exe + ".new";
            long size;
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                size = resp.Content.Headers.ContentLength ?? -1;
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus($"Version {tag.TrimStart('v')} is available, but MacShell’s folder can’t be written to.");
                if (userInitiated || !_asked) { _asked = true; OfferManualDownload(tag); }
                return;
            }
            var fi = new FileInfo(tmp);
            if ((size >= 0 && fi.Length != size) || fi.Length < 100_000 || !StartsWithMZ(tmp)) { File.Delete(tmp); SetStatus("The download was incomplete; MacShell will try again later."); return; }
            string notes = await ReleaseNotes(tag);

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
