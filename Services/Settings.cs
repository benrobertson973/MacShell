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
    public bool LaunchpadOnWinKey { get; set; } = true;          // legacy (superseded by WinKeyAction)
    /// <summary>What tapping the Windows key alone does: "nothing" (default), "launchpad" or "start".</summary>
    public string WinKeyAction { get; set; } = "nothing";
    public bool DockShowBadges { get; set; } = true;
    /// <summary>Also count notifications waiting in Notification Center (off: they are often old/stale).</summary>
    public bool BadgeCountNotifications { get; set; } = false;
    public bool ReplaceAltTab { get; set; } = true;
    public bool AltSpaceSpotlight { get; set; } = true;
    public bool SoundEffects { get; set; } = true;
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
