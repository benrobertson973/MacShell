using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Path = System.IO.Path;

namespace MacShell.Apps;

/// <summary>Appearance, Wallpaper, Desktop & Dock, Control Center, Spotlight, Finder and MacShell.</summary>
public partial class SettingsWindow
{
    // ------------------------------------------------------------------ Appearance

    void BuildAppearance()
    {
        var s = Settings.Current;
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label) in new[] { ("auto", "Auto"), ("light", "Light"), ("dark", "Dark") })
        {
            var cell = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
            var thumb = new Border { Width = 68, Height = 44, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(s.Appearance == id ? 3 : 0.5), ClipToBounds = true };
            if (s.Appearance == id) thumb.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else thumb.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            thumb.Child = AppearanceThumb(id);
            cell.Children.Add(thumb);
            var t = Txt(label, 11, s.Appearance == id ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 4, 0, 0);
            cell.Children.Add(t);
            string pick = id;
            cell.MouseLeftButtonUp += (_, _) => SetAppearance(pick);
            choices.Children.Add(cell);
        }
        var accents = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, name, light, dark) in Theme.Accents)
        {
            var c = Theme.IsDark ? dark : light;
            var dot = new Grid { Width = 18, Height = 18, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = name };
            if (id == "multicolor")
                dot.Children.Add(new Ellipse { Fill = new LinearGradientBrush(new GradientStopCollection { new(Theme.C("#FF5F57"), 0), new(Theme.C("#FEBC2E"), 0.33), new(Theme.C("#28C840"), 0.66), new(Theme.C("#0A84FF"), 1) }, 45) });
            else dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Theme.Darken(c, 0.2)), StrokeThickness = 0.5 });
            if (Settings.Current.Accent == id) dot.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White });
            string pick = id;
            dot.MouseLeftButtonUp += (_, _) => { Settings.Current.Accent = pick; Save(); Theme.Apply(); };
            accents.Children.Add(dot);
        }
        Group(Row("Appearance", choices), Row("Accent color", accents));
        Group(Row("Windows apps follow this appearance", Switch(s.SyncWindowsAppearance, v =>
            {
                Settings.Current.SyncWindowsAppearance = v; Save();
                if (v) Theme.SyncWindows();
            }), "Light and Dark also switch Windows’ own apps (File Explorer, Settings, Notepad …) to light or dark mode"));
        Group(Row("Show scroll bars", PopUp(new[] { ("Automatically based on mouse or trackpad", "auto"), ("When scrolling", "scrolling"), ("Always", "always") }, s.ScrollBars ?? "auto",
            v => { Settings.Current.ScrollBars = v; Save(); Theme.ApplyScrollBars(); })));
    }

    void SetAppearance(string mode)
    {
        Settings.Current.Appearance = mode;
        Save();
        if (Settings.Current.SyncWindowsAppearance && mode != "auto") Theme.SyncWindows();
        Theme.Apply();
    }

    static UIElement AppearanceThumb(string id)
    {
        Brush Bg(bool dark) => new SolidColorBrush(dark ? Theme.C("#1E1E1E") : Theme.C("#F5F5F5"));
        Brush Win(bool dark) => new SolidColorBrush(dark ? Theme.C("#3A3A3C") : Colors.White);
        var g = new Grid();
        if (id == "auto")
        {
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new Border { Background = Bg(false) }; var r = new Border { Background = Bg(true) };
            Grid.SetColumn(r, 1);
            g.Children.Add(l); g.Children.Add(r);
            var w = new Border { Margin = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(3), Background = new LinearGradientBrush(Colors.White, Theme.C("#3A3A3C"), 0) };
            Grid.SetColumnSpan(w, 2);
            g.Children.Add(w);
        }
        else
        {
            bool dark = id == "dark";
            g.Children.Add(new Border { Background = Bg(dark) });
            g.Children.Add(new Border { Margin = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(3), Background = Win(dark) });
            g.Children.Add(new Border { Margin = new Thickness(10, 8, 44, 8), CornerRadius = new CornerRadius(3, 0, 0, 3), Background = new SolidColorBrush(dark ? Theme.C("#2A2A2C") : Theme.C("#E5E5EA")) });
        }
        return g;
    }

    // ------------------------------------------------------------------ Wallpaper

    void BuildWallpaper()
    {
        var cur = Settings.Current.Wallpaper;
        var head = new Grid { Margin = new Thickness(0, 4, 0, 16) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var preview = new Border { Width = 180, Height = 112, CornerRadius = new CornerRadius(8), Background = new ImageBrush(Wallpaper.Image) { Stretch = Stretch.UniformToFill }, BorderThickness = new Thickness(0.5) };
        preview.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        head.Children.Add(preview);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        string curName = Wallpaper.BuiltIn.FirstOrDefault(w => w.id == cur).name ?? (cur == "windows" ? "Windows Wallpaper" : Path.GetFileNameWithoutExtension(cur));
        info.Children.Add(Txt(curName, 15, FontWeights.SemiBold));
        info.Children.Add(Txt(cur.StartsWith("gen:") ? "Dynamic Wallpaper" : "Picture", 11, brush: "SecondaryLabelBrush"));
        var add = Btn("Add Photo…", () =>
        {
            var r = Finder.FilePanel.ShowModal(this, new Finder.FilePanelRequest
            {
                Title = "Choose a picture for your desktop", Prompt = "Choose", AppKey = "macshell:wallpaper",
                Folder = Settings.Current.FilePanelPlaces.ContainsKey("macshell:wallpaper") ? null : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Filters = { "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.jfif;*.heic)", "All files (*.*)" },
            });
            if (r?.Paths.Count > 0) SetWallpaper(r.Paths[0]);
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 10, 0, 0);
        info.Children.Add(add);
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        _content.Children.Add(head);

        SectionTitle("Dynamic Wallpapers");
        var grid = new WrapPanel();
        foreach (var (id, name) in Wallpaper.BuiltIn) grid.Children.Add(WallTile(id, name, cur == id));
        _content.Children.Add(grid);
        SectionTitle("Pictures");
        var pics = new WrapPanel();
        if (Wallpaper.CurrentWindowsWallpaper() != null) pics.Children.Add(WallTile("windows", "Windows Wallpaper", cur == "windows"));
        if (!cur.StartsWith("gen:") && cur != "windows" && File.Exists(cur)) pics.Children.Add(WallTile(cur, Path.GetFileNameWithoutExtension(cur), true));
        try
        {
            foreach (var f in Directory.EnumerateFiles(@"C:\Windows\Web\Wallpaper", "*.jpg", SearchOption.AllDirectories).Take(8))
                pics.Children.Add(WallTile(f, Path.GetFileNameWithoutExtension(f), cur == f));
        }
        catch { }
        _content.Children.Add(pics);
    }

    FrameworkElement WallTile(string id, string name, bool selected)
    {
        var sp = new StackPanel { Width = 116, Margin = new Thickness(0, 0, 12, 12), Cursor = Cursors.Hand };
        var b = new Border { Height = 72, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(selected ? 3 : 0.5), ClipToBounds = true };
        if (selected) b.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else b.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var img = new Image { Stretch = Stretch.UniformToFill };
        b.Child = img;
        _pending++;
        Dispatcher.BeginInvoke(() => { _pending--; img.Source = Wallpaper.Thumbnail(id); }, System.Windows.Threading.DispatcherPriority.Background);
        sp.Children.Add(b);
        var t = Txt(name, 11, brush: "SecondaryLabelBrush");
        t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap;
        t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(t);
        sp.MouseLeftButtonUp += (_, _) => SetWallpaper(id);
        return sp;
    }

    void SetWallpaper(string id)
    {
        Settings.Current.Wallpaper = id;
        Settings.SaveNow();
        Mouse.OverrideCursor = Cursors.Wait;
        try { Wallpaper.Load(); } finally { Mouse.OverrideCursor = null; }
        Rebuild();
    }

    // ------------------------------------------------------------------ Desktop & Dock

    void BuildDock()
    {
        var s = Settings.Current;
        SectionTitle("Dock");
        Group(
            Row("Size", LabeledSlider(24, 96, s.DockIconSize, "Small", "Large", v => { Settings.Current.DockIconSize = Math.Round(v); Save(); })),
            Row("Magnification", LabeledSlider(24, 128, s.DockMagnifiedSize, "Off", "Large", v => { Settings.Current.DockMagnifiedSize = Math.Round(v); Settings.Current.DockMagnification = v > Settings.Current.DockIconSize + 2; Save(); })),
            Row("Automatically hide and show the Dock", Switch(s.DockAutoHide, v => { Settings.Current.DockAutoHide = v; Save(); })),
            Row("Animate opening applications", Switch(s.DockAnimateOpening, v => { Settings.Current.DockAnimateOpening = v; Save(); })),
            Row("Show indicators for open applications", Switch(s.DockShowIndicators, v => { Settings.Current.DockShowIndicators = v; Save(); })),
            Row("Show notification badges", Switch(s.DockShowBadges, v => { Settings.Current.DockShowBadges = v; Save(); }), "Red counts on app icons"),
            Row("Also count notifications in Notification Center", Switch(s.BadgeCountNotifications, v => { Settings.Current.BadgeCountNotifications = v; Save(); }), "Off: badges show only the unread counts apps set themselves"));
        SectionTitle("Desktop");
        Group(
            Row("Icon size", PopUp(new[] { "Small", "Medium", "Large" }, s.DesktopIconSize <= 48 ? 0 : s.DesktopIconSize >= 88 ? 2 : 1, i => { Settings.Current.DesktopIconSize = i switch { 0 => 48, 2 => 88, _ => 64 }; Save(); })),
            Row("Use stacks", Switch(s.DesktopUseStacks, v => { Settings.Current.DesktopUseStacks = v; Save(); }), "Files are grouped by kind on the right of the desktop"),
            Row("Show hidden files", Switch(s.ShowHiddenFiles, v => { Settings.Current.ShowHiddenFiles = v; Save(); })));
        var browser = DefaultBrowserName();
        Group(Row("Default web browser", WinLink(browser ?? "Choose…", "ms-settings:defaultapps"), browser == null ? null : "Windows lets only its own Settings change the default browser"));
        SectionTitle("Windows");
        Group(
            Row("Drag windows to screen edges to tile", Switch(SysParams.WindowSnapping, v => SysParams.WindowSnapping = v), "Windows Snap: drag a window to an edge or a corner to fill that part of the screen"),
            Row("Show tiling layouts on a window’s maximize button", Switch(SysParams.SnapLayoutsFlyout, v => SysParams.SnapLayoutsFlyout = v), "In Windows apps"));
        SectionTitle("Mission Control");
        Group(
            Row("Mission Control", Secondary("Win + ↑", 12), "Shows all open windows"),
            Row("Show Desktop", Secondary("Win + D", 12)),
            Row("Application windows", Secondary("Right-click an app in the Dock", 12)));
        Group(ButtonRow(Btn("Hot Corners…", ShowHotCorners)));
    }

    static string DefaultBrowserName()
    {
        try
        {
            string exe = AppCatalog.DefaultBrowserExe();
            if (string.IsNullOrEmpty(exe)) return null;
            var app = AppCatalog.FindByExe(exe);
            return app?.Name ?? WindowTracker.FriendlyExeName(exe);
        }
        catch { return null; }
    }

    static readonly (string name, string value)[] CornerActions =
    {
        ("–", ""), ("Mission Control", "missioncontrol"), ("Desktop", "desktop"), ("Notification Center", "notifications"), ("Launchpad", "launchpad"),
        ("-", "-"), ("Start Screen Saver", "screensaver"), ("Put Display to Sleep", "displaysleep"), ("-", "-"), ("Lock Screen", "lock"),
    };

    void ShowHotCorners()
    {
        var corners = Settings.Current.HotCorners;
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition());
        var screen = new Border { Width = 150, Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(16, 6, 16, 6), BorderThickness = new Thickness(0.5), ClipToBounds = true };
        screen.Background = new ImageBrush(Wallpaper.Image) { Stretch = Stretch.UniformToFill };
        screen.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(screen, 1); Grid.SetRowSpan(screen, 2);
        g.Children.Add(screen);
        var options = CornerActions.Where(a => a.value != "-").ToArray();
        for (int i = 0; i < 4; i++)
        {
            int corner = i;
            var names = CornerActions.Select(a => a.name).ToArray();
            int sel = Array.FindIndex(CornerActions, a => a.value == (corners[corner] ?? ""));
            var pop = PopUp(names, Math.Max(0, sel), idx =>
            {
                Settings.Current.HotCorners[corner] = CornerActions[idx].value == "-" ? "" : CornerActions[idx].value;
                Save();
            });
            pop.MinWidth = 150;
            pop.HorizontalAlignment = corner % 2 == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            pop.VerticalAlignment = corner < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            Grid.SetColumn(pop, corner % 2 == 0 ? 0 : 2);
            Grid.SetRow(pop, corner < 2 ? 0 : 1);
            g.Children.Add(pop);
        }
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(null, "Hot Corners", "Move the pointer into a corner of the screen to do one of these"));
        sp.Children.Add(g);
        Sheet(sp, 520, ("Done", null));
    }

    // ------------------------------------------------------------------ Control Center

    void BuildControlCenter()
    {
        var s = Settings.Current;
        SectionTitle("Menu Bar");
        Group(
            MenuBarRow("Weather", "weather"),
            MenuBarRow("Timer", "timer"),
            MenuBarRow("Battery", "battery"),
            MenuBarRow("Wi‑Fi", "wifi"),
            MenuBarRow("Sound", "sound"),
            MenuBarRow("Spotlight", "spotlight"));
        SectionTitle("App Icons in the Menu Bar");
        var apps = Shell.MenuBarWindow.TrayApps();
        if (apps.Count == 0) Group(Row("No app icons right now", null, "Apps running in the background show their icons in the menu bar"));
        else Group(apps.Select(a => MenuBarRow(a.name, a.key, a.image)).ToArray());
        Footnote("Tip: hold Alt and drag an icon out of the menu bar to take it out.");
        SectionTitle("Weather");
        Group(WeatherLocationRow(),
              Row("Temperature", PopUp(new[] { "Fahrenheit (°F)", "Celsius (°C)" }, s.Celsius ? 1 : 0, i => { Settings.Current.Celsius = i == 1; Save(); Weather.Refresh(); })));
        SectionTitle("Clock");
        Group(
            Row("Show date", Switch(s.ClockShowDate, v => { Settings.Current.ClockShowDate = v; Save(); })),
            Row("Show the day of the week", Switch(s.ClockShowDay, v => { Settings.Current.ClockShowDay = v; Save(); })),
            Row("Use a 24-hour clock", Switch(s.Clock24Hour, v => { Settings.Current.Clock24Hour = v; Save(); })),
            Row("Display the time with seconds", Switch(s.ClockShowSeconds, v => { Settings.Current.ClockShowSeconds = v; Save(); })));
        if (PowerSettings.HasBattery)
        {
            SectionTitle("Battery");
            Group(Row("Show Percentage", Switch(s.ShowBatteryPercent, v => { Settings.Current.ShowBatteryPercent = v; Save(); })));
        }
        SectionTitle("Timer");
        Group(
            Row("When the timer ends", PopUp(CountdownTimer.Sounds.Select(CountdownTimer.SoundName).ToArray(), Math.Max(0, Array.IndexOf(CountdownTimer.Sounds, s.TimerSound)), i =>
            {
                Settings.Current.TimerSound = CountdownTimer.Sounds[i]; Save();
                CountdownTimer.Preview(CountdownTimer.Sounds[i]);
            }), "Click 00:00 in the menu bar, then type 5:00 for 5 minutes, or a time like 5:30pm"));
    }

    /// <summary>The weather's place - where this computer is, or a city chosen - kept up to date while it shows.</summary>
    UIElement WeatherLocationRow()
    {
        var holder = new Border();
        void Fill()
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (!Weather.Automatic)
            {
                var mine = Btn("Use My Location", () => Weather.SetPlace(null, null, null));
                mine.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(mine);
            }
            buttons.Children.Add(Btn("Change…", () => Shell.WeatherPopover.ChangeLocation(this)));
            string place = Weather.PlaceName;
            string sub = Weather.Automatic
                ? (string.IsNullOrEmpty(place) ? "Where this computer is (from its internet connection)" : $"Where this computer is: about {place}")
                : place;
            holder.Child = Row("Location", buttons, sub);
        }
        Fill();
        Weather.Changed += Fill;
        OnLeave(() => Weather.Changed -= Fill);
        return holder;
    }

    /// <summary>A "show in the menu bar" switch for one menu bar item (an app's: with its icon).</summary>
    static FrameworkElement MenuBarRow(string name, string key, ImageSource icon = null)
    {
        var sw = Switch(!Shell.MenuBarWindow.IsHidden(key), v => Shell.MenuBarWindow.SetHidden(key, name, !v));
        return icon != null ? IconRow(icon, name, sw, iconSize: 16) : Row(name, sw);
    }

    // ------------------------------------------------------------------ Spotlight

    static readonly (string group, string label, string sub)[] SpotlightKinds =
    {
        ("Applications", "Applications", null),
        ("Documents", "Documents", "Files found by the Windows Search index"),
        ("Folders", "Folders", null),
        ("Calculator", "Calculator", "Sums like 12*7"),
        ("System Settings", "System Settings", null),
        ("System", "MacShell Items", "Finder, Launchpad, Lock Screen, Sleep …"),
        ("Web", "Web Search", "Search the web for what was typed"),
    };

    void BuildSpotlight()
    {
        var s = Settings.Current;
        Group(
            Row("Keyboard shortcut", Secondary("Win + Space", 12), "Opens Spotlight from anywhere"),
            Row("Also use Alt + Space", Switch(s.AltSpaceSpotlight, v => { Settings.Current.AltSpaceSpotlight = v; Save(); }), "Alt sits where ⌘ is on a Mac keyboard"));
        SectionTitle("Search results");
        Group(SpotlightKinds.Select(k => Row(k.label, Switch(!s.SpotlightHidden.Contains(k.group), v =>
        {
            Settings.Current.SpotlightHidden.Remove(k.group);
            if (!v) Settings.Current.SpotlightHidden.Add(k.group);
            Save();
        }), k.sub)).ToArray());
        Footnote("Only the kinds of results switched on are shown in Spotlight.");
        Group(ButtonRow(Btn("Spotlight Privacy…", ShowSpotlightPrivacy)));
    }

    void ShowSpotlightPrivacy()
    {
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            var folders = Settings.Current.SpotlightExcluded;
            if (folders.Count == 0) list.Children.Add(GroupBox(new[] { Row("No folders", null, "Folders added here are left out of Spotlight’s results") }));
            else list.Children.Add(GroupBox(folders.Select(f =>
            {
                string folder = f;
                var remove = Btn("Remove", () => { Settings.Current.SpotlightExcluded.Remove(folder); Save(); Fill(); });
                return IconRow(MacIcons.Folder(MacIcons.FolderGlyphFor(folder)), Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder, remove, folder);
            })));
        }
        Fill();
        var add = Btn("Add Folder…", () =>
        {
            var r = Finder.FilePanel.ShowModal(this, new Finder.FilePanelRequest { Title = "Choose a folder for Spotlight to leave out", Prompt = "Choose", Folders = true, AppKey = "macshell:spotlight" });
            if (r?.Paths.Count > 0 && !Settings.Current.SpotlightExcluded.Contains(r.Paths[0], StringComparer.OrdinalIgnoreCase))
            {
                Settings.Current.SpotlightExcluded.Add(r.Paths[0]);
                Save();
                Fill();
            }
        });
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(PaneIcon(Panes.First(p => p.id == "spotlight")), "Spotlight Privacy", "Spotlight leaves files in these folders out of its results"));
        sp.Children.Add(list);
        Sheet(sp, 480, add, ("Done", null));
    }

    // ------------------------------------------------------------------ Finder

    void BuildFinder()
    {
        var s = Settings.Current;
        string[] targets = { "recents", "home", "desktop", "documents", "downloads" };
        string[] targetNames = { "Recents", Environment.UserName, "Desktop", "Documents", "Downloads" };
        string[] views = { "icons", "list", "columns", "gallery" };
        string[] viewNames = { "Icons", "List", "Columns", "Gallery" };
        SectionTitle("General");
        Group(
            Row("New Finder windows show", PopUp(targetNames, Math.Max(0, Array.IndexOf(targets, s.FinderNewWindowTarget)), i => { Settings.Current.FinderNewWindowTarget = targets[i]; Save(); })),
            Row("Default view", PopUp(viewNames, Math.Max(0, Array.IndexOf(views, s.FinderDefaultView)), i => { Settings.Current.FinderDefaultView = views[i]; Save(); })));
        SectionTitle("Open and Save");
        Group(Row("Use Mac-style Open and Save windows in all apps", Switch(s.MacFilePanels, v => { Settings.Current.MacFilePanels = v; Save(); }),
            "In place of the Windows file window. When an app needs its own, right-click the files and choose Show Windows Dialog"));
        SectionTitle("Advanced");
        Group(
            Row("Show all filename extensions", Switch(s.ShowExtensions, v => { Settings.Current.ShowExtensions = v; Save(); foreach (var w in Finder.FinderWindow.All) w.Reload(true); })),
            Row("Keep folders on top", Switch(s.FinderFoldersOnTop, v => { Settings.Current.FinderFoldersOnTop = v; Save(); foreach (var w in Finder.FinderWindow.All) w.Reload(true); })),
            Row("Show hidden files", Switch(s.ShowHiddenFiles, v => { Settings.Current.ShowHiddenFiles = v; Save(); foreach (var w in Finder.FinderWindow.All) w.Reload(true); })),
            Row("Show path bar", Switch(s.FinderShowPathBar, v => { Settings.Current.FinderShowPathBar = v; Save(); })),
            Row("Show status bar", Switch(s.FinderShowStatusBar, v => { Settings.Current.FinderShowStatusBar = v; Save(); })));
        SectionTitle("Recents");
        if (s.RecentsIgnoredTypes.Count == 0)
            Group(Row("Ignored kinds of file", Secondary("None", 12), "Right-click an item in Recents (Finder’s, or the Dock’s) › Ignore “.png” Files to leave that kind of file out"));
        else
            Group(s.RecentsIgnoredTypes.OrderBy(t => t).Select(t =>
            {
                string type = t;
                return Row(t.Length == 0 ? "Files with no extension" : $"“{t}” files", Btn("Show Again", () => { Finder.FinderWindow.IgnoreInRecents(type, false); Rebuild(); }), "Left out of Recents");
            }).ToArray());
        SectionTitle("Tags");
        Group(Row("Tagged items", Secondary($"{s.Tags.Count} items", 12), "Tags are stored by MacShell and shown in the Finder sidebar"));
    }

    // ------------------------------------------------------------------ MacShell

    void BuildMacShell()
    {
        var s = Settings.Current;
        var intro = Txt("MacShell replaces the Windows desktop with a macOS-style environment. Your Windows apps keep running inside it: they appear in the Dock, the menu bar, Mission Control and the App Switcher.", 12, brush: "SecondaryLabelBrush");
        intro.Margin = new Thickness(4, 0, 0, 12);
        _content.Children.Add(intro);
        SectionTitle("Takeover");
        Group(
            Row("Hide the Windows taskbar", Switch(s.HideWindowsTaskbar, v => { Settings.Current.HideWindowsTaskbar = v; Save(); if (v == Takeover.TaskbarTemporarilyShown) Takeover.ToggleTaskbar(); })),
            Row("Show app icons in the menu bar", Switch(s.MenuBarTrayIcons, v =>
            {
                Settings.Current.MenuBarTrayIcons = v; Save();
                if (v) TrayHost.Start(ShellHost.MenuBar.TrayIconRect); else TrayHost.Stop();
            }), "Icons of apps running in the background (the Windows system tray)"),
            Row("Use this wallpaper for Windows too", Switch(s.SyncWindowsWallpaper, v =>
            {
                Settings.Current.SyncWindowsWallpaper = v; Save();
                if (v) Wallpaper.Load();
            }), "Windows shows its wallpaper for a moment while you sign in"),
            Row("Tapping the Windows key", PopUp(new[] { "Does nothing", "Opens Launchpad", "Opens the Start menu" },
                s.WinKeyAction switch { "launchpad" => 1, "start" => 2, _ => 0 },
                i => { Settings.Current.WinKeyAction = i switch { 1 => "launchpad", 2 => "start", _ => "nothing" }; Save(); })),
            Row("Replace Alt + Tab with the App Switcher", Switch(s.ReplaceAltTab, v => { Settings.Current.ReplaceAltTab = v; Save(); })),
            Row("Open MacShell at login", Switch(LoginItem.Enabled, v => Task.Run(() => LoginItem.Set(v)))));
        SectionTitle("Windows");
        var toggleTb = new Button { Content = Takeover.TaskbarTemporarilyShown ? "Hide Taskbar" : "Show Taskbar", Style = (Style)Application.Current.Resources["MacButton"] };
        toggleTb.Click += (_, _) => { Takeover.ToggleTaskbar(); toggleTb.Content = Takeover.TaskbarTemporarilyShown ? "Hide Taskbar" : "Show Taskbar"; };
        Group(
            Row("Windows taskbar & system tray", toggleTb, "Temporarily bring back the taskbar to reach tray icons"),
            Row("Windows Settings", WinLink("Open", "ms-settings:"), "Everything Windows keeps to itself"),
            Row("Quit MacShell", Btn("Return to Windows…", ShellHost.ConfirmExitToWindows), "Restores the taskbar and desktop. Emergency shortcut: Ctrl + Alt + Shift + Q"));
    }
}
