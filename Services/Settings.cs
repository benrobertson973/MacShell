using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacShell.Services;

public class PinnedApp
{
    public string Name { get; set; }
    /// <summary>"internal:finder", an AppsFolder parsing name, or an exe path.</summary>
    public string Target { get; set; }
    public string ExePath { get; set; }
}

public class AppSettings
{
    public string Appearance { get; set; } = "light";        // light | dark | auto
    public string Accent { get; set; } = "blue";
    public string Wallpaper { get; set; } = "gen:sonoma";
    public double DockIconSize { get; set; } = 48;
    public bool DockMagnification { get; set; } = true;
    public double DockMagnifiedSize { get; set; } = 80;
    public bool DockAutoHide { get; set; } = false;
    public bool DockShowIndicators { get; set; } = true;
    public bool DockShowRecents { get; set; } = true;
    public bool MinimizeIntoAppIcon { get; set; } = true;
    public List<PinnedApp> DockApps { get; set; }
    public bool ClockShowSeconds { get; set; }
    public bool Clock24Hour { get; set; }
    public bool ClockShowDate { get; set; } = true;
    public bool ClockShowDay { get; set; } = true;
    public bool ShowBatteryPercent { get; set; } = false;
    public double DesktopIconSize { get; set; } = 64;
    public bool DesktopUseStacks { get; set; }
    /// <summary>Free-form desktop icon positions (top-left, DIPs) keyed by full path.</summary>
    public Dictionary<string, double[]> DesktopPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowHiddenFiles { get; set; }
    public bool FinderShowPathBar { get; set; } = true;
    public bool FinderShowStatusBar { get; set; } = true;
    public bool FinderShowSidebar { get; set; } = true;
    public bool FinderFoldersOnTop { get; set; }
    public bool FinderShowExtensions { get; set; } = true;
    public string FinderDefaultView { get; set; } = "icons";
    public double FinderIconSize { get; set; } = 64;
    public string FinderNewWindowTarget { get; set; } = "recents";
    public Dictionary<string, string> FolderViews { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SidebarFavorites { get; set; }
    public List<string> RecentApps { get; set; } = new();
    public List<string> RecentDocs { get; set; } = new();
    public bool HideWindowsTaskbar { get; set; } = true;
    /// <summary>Show running apps' notification-area (tray) icons in the menu bar, like macOS menu extras.</summary>
    public bool MenuBarTrayIcons { get; set; } = true;
    /// <summary>Also set the Windows wallpaper to MacShell's (seamless boot). Off by default: it replaces the user's Windows wallpaper.</summary>
    public bool SyncWindowsWallpaper { get; set; } = false;
    /// <summary>Open With defaults chosen in MacShell: extension (".png") → app key (see Services/OpenWith.cs).</summary>
    public Dictionary<string, string> OpenWithDefaults { get; set; } = new();
    /// <summary>Get Info › Open with for single documents: full path → app key.</summary>
    public Dictionary<string, string> OpenWithFiles { get; set; } = new();
    public bool LaunchpadOnWinKey { get; set; } = true;          // legacy (superseded by WinKeyAction)
    /// <summary>What tapping the Windows key alone does: "nothing" (default), "launchpad" or "start".</summary>
    public string WinKeyAction { get; set; } = "nothing";
    public bool DockShowBadges { get; set; } = true;
    /// <summary>Also count notifications waiting in Notification Center (off: they are often old/stale).</summary>
    public bool BadgeCountNotifications { get; set; } = false;
    public bool ReplaceAltTab { get; set; } = true;
    public bool AltSpaceSpotlight { get; set; } = true;
    public bool SoundEffects { get; set; } = true;
    /// <summary>Menu bar items taken out of the menu bar: "weather", "timer", "battery", "wifi", "sound", "spotlight", or an app's
    /// icons ("app:discord.exe") → the name shown in Settings.</summary>
    public Dictionary<string, string> MenuBarHidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The timer's last length in seconds: what the timer offers next time (see Services/CountdownTimer.cs).</summary>
    public double TimerSeconds { get; set; } = 300;
    /// <summary>A running timer's end (UTC), or a paused one's time left: a timer survives MacShell restarting.</summary>
    public DateTime? TimerEndsUtc { get; set; }
    public double? TimerPausedSeconds { get; set; }
    /// <summary>The running timer rings at a time of day (an alarm) rather than after a length of time.</summary>
    public bool TimerIsAlarm { get; set; }
    /// <summary>What was last typed in the timer's field ("5:00", "5:30pm"): offered again next time.</summary>
    public string TimerText { get; set; } = "5:00";
    /// <summary>Eating mode (the timer starts over every this many seconds, with a soft chime) and its minutes so far.</summary>
    public double? TimerRepeatSeconds { get; set; }
    public int TimerCycles { get; set; }
    /// <summary>The weather's place, chosen in Settings (null: automatic), and the automatic one last found (and when).</summary>
    public string WeatherPlace { get; set; }
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }
    public string WeatherAutoPlace { get; set; }
    public double? WeatherAutoLat { get; set; }
    public double? WeatherAutoLon { get; set; }
    public DateTime WeatherAutoAt { get; set; }
    /// <summary>Mail: a banner and a sound when new mail arrives; Mail added to the Dock once (its first time).</summary>
    public bool MailNotify { get; set; } = true;
    public bool MailPinned { get; set; }
    /// <summary>Mail's window: the sidebar's and the message list's widths, where it was and how big (DIPs: left, top,
    /// width, height - its size before it was zoomed, if it was), and whether it was zoomed; a new message's size.</summary>
    public double MailSidebarWidth { get; set; } = 210;
    public double MailListWidth { get; set; } = 330;
    public double[] MailWindowBounds { get; set; }
    public bool MailWindowZoomed { get; set; }
    public double[] MailComposeSize { get; set; }
    /// <summary>Where eating mode's little window was dragged to (DIPs); null: the top right.</summary>
    public double? EatingHudLeft { get; set; }
    public double? EatingHudTop { get; set; }
    /// <summary>The alarm played when the timer ends: a sound in Windows\Media (without ".wav").</summary>
    public string TimerSound { get; set; } = "Alarm01";
}

public static class Settings
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    static System.Windows.Threading.DispatcherTimer _saveTimer;

    public static AppSettings Current { get; private set; } = new();
    public static string DataDirectory => Dir;
    public static event Action Changed;

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new AppSettings();
        }
        catch { Current = new AppSettings(); }
        Current.FolderViews = new(Current.FolderViews ?? new(), StringComparer.OrdinalIgnoreCase);
        Current.Tags = new(Current.Tags ?? new(), StringComparer.OrdinalIgnoreCase);
        Current.DesktopPositions = new(Current.DesktopPositions ?? new(), StringComparer.OrdinalIgnoreCase);
        Current.MenuBarHidden = new(Current.MenuBarHidden ?? new(), StringComparer.OrdinalIgnoreCase);
        Current.RecentApps ??= new();
        Current.RecentDocs ??= new();
    }

    /// <summary>Persists settings (debounced) and notifies listeners.</summary>
    public static void Save(bool notify = true)
    {
        if (notify) Changed?.Invoke();
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Opts));
        }
        catch { }
    }

    public static void AddRecentApp(string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        Current.RecentApps.Remove(target);
        Current.RecentApps.Insert(0, target);
        if (Current.RecentApps.Count > 10) Current.RecentApps.RemoveRange(10, Current.RecentApps.Count - 10);
        Save(false);
    }

    public static void AddRecentDoc(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Current.RecentDocs.Remove(path);
        Current.RecentDocs.Insert(0, path);
        if (Current.RecentDocs.Count > 20) Current.RecentDocs.RemoveRange(20, Current.RecentDocs.Count - 20);
        Save(false);
    }
}
