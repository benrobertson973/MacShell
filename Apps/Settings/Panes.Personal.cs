using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Microsoft.Win32;

namespace MacShell.Apps;

/// <summary>Notifications, Focus, Lock Screen, Users & Groups, Internet Accounts and Privacy & Security.</summary>
public partial class SettingsWindow
{
    static readonly (string top, string bottom) RedTile = ("#FF6259", "#E8342B"), PurpleTile = ("#7A78F0", "#4C49D6"), DarkTile = ("#3A3A3C", "#141414");

    // ================================================================== Notifications

    const string NotifKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    static int NotifValue(string sub, string name, int fallback)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(sub == null ? NotifKey : NotifKey + "\\" + sub); return k?.GetValue(name) is int v ? v : fallback; }
        catch { return fallback; }
    }

    static void SetNotifValue(string sub, string name, int value)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(sub == null ? NotifKey : NotifKey + "\\" + sub); k.SetValue(name, value, RegistryValueKind.DWord); }
        catch (Exception ex) { App.Log($"notification setting {name}: {ex.Message}"); }
    }

    /// <summary>The apps Windows keeps notification settings for, that this computer still has (named, with icons).</summary>
    static List<(string id, AppEntry app)> NotifyingApps()
    {
        var list = new List<(string, AppEntry)>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(NotifKey);
            foreach (var id in k?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                var app = AppCatalog.FindByParsingName(id);
                if (app != null && list.All(x => x.Item2.Name != app.Name)) list.Add((id, app));
            }
        }
        catch { }
        return list.OrderBy(x => x.Item2.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    void BuildNotifications()
    {
        SectionTitle("Notification Center");
        Group(
            Row("Allow notifications when the screen is locked", Switch(NotifValue(null, "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", 1) != 0, v => SetNotifValue(null, "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", v ? 1 : 0))),
            Row("Allow notifications to play sounds", Switch(NotifValue(null, "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", 1) != 0, v => SetNotifValue(null, "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", v ? 1 : 0))),
            Row("Show notifications for", PopUp(new[] { ("5 seconds", 5), ("7 seconds", 7), ("15 seconds", 15), ("30 seconds", 30), ("1 minute", 60), ("5 minutes", 300) }, SysParams.NotificationSeconds, v => SysParams.NotificationSeconds = v)));
        SectionTitle("Application Notifications");
        var holder = new StackPanel();
        _content.Children.Add(holder);
        void Fill()
        {
            holder.Children.Clear();
            var apps = NotifyingApps();
            if (apps.Count == 0) { holder.Children.Add(GroupBox(new[] { Row("No apps have sent notifications", null) })); return; }
            holder.Children.Add(GroupBox(apps.Select(a =>
            {
                bool on = NotifValue(a.id, "Enabled", 1) != 0;
                bool banners = NotifValue(a.id, "ShowBanner", 1) != 0;
                string value = !on ? "Off" : banners ? "Banners, Sounds, Badges" : "Notification Center, Badges";
                var icon = new Image { Width = 22, Height = 22 };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                ShellIcons.Load(a.app.IconSource, 64, false, b => { if (b != null) icon.Source = b; });
                var row = NavRow(null, a.app.Name, "notifications/" + a.id, value);
                if (row is Border bd && bd.Child is Grid g)
                {
                    // (the app's own icon on the left)
                    g.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
                    foreach (UIElement c in g.Children) Grid.SetColumn(c, Grid.GetColumn(c) + 1);
                    icon.Margin = new Thickness(0, 0, 10, 0);
                    icon.VerticalAlignment = VerticalAlignment.Center;
                    g.Children.Add(icon);
                }
                return (UIElement)row;
            })));
        }
        if (AppCatalog.Apps.Count == 0) { holder.Children.Add(LoadingRow("Apps…")); AppCatalog.Loaded += () => Dispatcher.BeginInvoke(Fill); }
        else Fill();
    }

    void BuildNotificationApp(string id)
    {
        var app = AppCatalog.FindByParsingName(id);
        Title_(app?.Name ?? "Notifications");
        bool on = NotifValue(id, "Enabled", 1) != 0;
        var icon = new Image { Width = 32, Height = 32 };
        if (app != null) ShellIcons.Load(app.IconSource, 64, false, b => { if (b != null) icon.Source = b; });
        Group(IconRow(null, $"Allow notifications", Switch(on, v => { SetNotifValue(id, "Enabled", v ? 1 : 0); Rebuild(); }), null, 32, icon));
        if (!on) return;
        bool banners = NotifValue(id, "ShowBanner", 1) != 0;
        bool center = NotifValue(id, "ShowInActionCenter", 1) != 0;
        int rank = NotifValue(id, "Rank", 0);
        string badgeKey = app?.Name ?? id;
        bool badges = !(Settings.Current.BadgesOff?.Contains(badgeKey) ?? false);
        Group(
            Row("Banner style", Segmented(new[] { "Banners", "None" }, banners ? 0 : 1, i => SetNotifValue(id, "ShowBanner", i == 0 ? 1 : 0)), "Banners show at the top right and go away on their own"),
            Row("Show in Notification Center", Switch(center, v => SetNotifValue(id, "ShowInActionCenter", v ? 1 : 0))),
            Row("Badge application icon", Switch(badges, v =>
            {
                var off = Settings.Current.BadgesOff ??= new List<string>();
                off.Remove(badgeKey);
                if (!v) off.Add(badgeKey);
                Save();
            }), "The number on its icon in the Dock"),
            Row("Priority", PopUp(new[] { ("Normal", 0), ("High", 1), ("Top", 99) }, rank, v => SetNotifValue(id, "Rank", v)), "Higher ones are shown above the rest"));
    }

    // ================================================================== Focus

    void BuildFocus()
    {
        var s = Settings.Current;
        Group(
            IconRow(Tile("moon", PurpleTile.top, PurpleTile.bottom), "Do Not Disturb", Switch(s.DoNotDisturb, v => { DoNotDisturb.Set(v); Rebuild(); }),
                s.DoNotDisturb ? "On - notifications are silenced" : "Silences notifications: MacShell’s banners, and every notification’s sound", 28));
        if (s.DoNotDisturb) Footnote("Mail still collects new mail and the Dock still shows how much; it just doesn’t interrupt you.");
        Group(Row("Windows’ own Do Not Disturb (its banners too)", WinLink("Windows Settings", "ms-settings:notifications"), "Windows keeps its Do Not Disturb for its own Notification Center"));
    }

    void BuildFocusPage(string sub) => BuildFocus();

    // ================================================================== Lock Screen

    void BuildLockScreen()
    {
        (string, int)[] saver = { ("Never", 0), ("For 1 minute", 60), ("For 2 minutes", 120), ("For 5 minutes", 300), ("For 10 minutes", 600), ("For 20 minutes", 1200), ("For 30 minutes", 1800), ("For 1 hour", 3600), ("For 2 hours", 7200), ("For 3 hours", 10800) };
        (string, uint)[] display = { ("Never", 0), ("For 1 minute", 60), ("For 2 minutes", 120), ("For 5 minutes", 300), ("For 10 minutes", 600), ("For 20 minutes", 1200), ("For 30 minutes", 1800), ("For 1 hour", 3600), ("For 2 hours", 7200), ("For 3 hours", 10800) };
        int saverNow = SysParams.ScreenSaverActive && !string.IsNullOrEmpty(SysParams.ScreenSaverPath) ? SysParams.ScreenSaverTimeout : 0;
        var rows = new List<UIElement>
        {
            Row("Start Screen Saver when inactive", PopUp(saver, saverNow, v =>
            {
                if (v == 0) SysParams.ScreenSaverActive = false;
                else { SysParams.ScreenSaverTimeout = v; SysParams.ScreenSaverActive = true; }
            })),
        };
        if (PowerSettings.HasBattery && PowerSettings.Get(PowerSettings.DisplayOff, false) is uint dcOff)
            rows.Add(Row("Turn display off on battery when inactive", PopUp(display, dcOff, v => PowerSettings.Set(PowerSettings.DisplayOff, false, v))));
        if (PowerSettings.Get(PowerSettings.DisplayOff, true) is uint acOff)
            rows.Add(Row(PowerSettings.HasBattery ? "Turn display off on power adapter when inactive" : "Turn display off when inactive", PopUp(display, acOff, v => PowerSettings.Set(PowerSettings.DisplayOff, true, v))));
        Group(rows);
        bool secure = SysParams.ScreenSaverSecure;
        uint? wake = PowerSettings.Get(PowerSettings.PasswordOnWake, true);
        Group(
            Row("Require password after screen saver begins", PopUp(new[] { "Immediately", "Never" }, secure ? 0 : 1, i => SysParams.ScreenSaverSecure = i == 0)),
            wake == null ? null : Row("Require password when waking from sleep", Switch(wake != 0, v => { PowerSettings.Set(PowerSettings.PasswordOnWake, true, v ? 1u : 0); PowerSettings.Set(PowerSettings.PasswordOnWake, false, v ? 1u : 0); })));
        Group(
            Row("Lock screen picture and widgets", WinLink("Windows Settings", "ms-settings:lockscreen")),
            Row("Sign-in options (PIN, fingerprint, face)", WinLink("Sign-in Options", "ms-settings:signinoptions")));
    }

    // ================================================================== Users & Groups

    void BuildUsers()
    {
        var holder = new StackPanel();
        _content.Children.Add(holder);
        holder.Children.Add(LoadingRow("Users…"));
        Run(UserAccounts.All, all =>
        {
            holder.Children.Clear();
            var me = all.FirstOrDefault(a => a.IsCurrent);
            var rows = new List<UIElement>();
            foreach (var a in all.Where(a => a.Enabled).OrderByDescending(a => a.IsCurrent).ThenBy(a => a.FullName ?? a.Name))
            {
                var av = Avatar(a.FullName ?? a.Name, 32, UserAccounts.Picture(a.Sid, 96));
                var info = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = "ⓘ", Width = 24, Height = 22, MinWidth = 0, Padding = new Thickness(0), ToolTip = "Details" };
                var acct = a;
                info.Click += (_, _) => UserSheet(acct);
                string kind = (a.Admin ? "Admin" : "Standard") + (a.MicrosoftAccount ? " · Microsoft account" : "");
                rows.Add(IconRow(null, string.IsNullOrWhiteSpace(a.FullName) ? a.Name : a.FullName, info, kind, 32, av));
            }
            holder.Children.Add(GroupBox(rows));
            holder.Children.Add(ButtonRow(WinLink("Add User…", "ms-settings:otherusers")));
            holder.Children.Add(GroupBox(new[]
            {
                Row("Your account’s picture and info", WinLink("Your Info", "ms-settings:yourinfo")),
                Row("Password", WinLink("Change…", "ms-settings:signinoptions")),
            }));
        });
    }

    void UserSheet(UserAccounts.Account a)
    {
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var av = Avatar(a.FullName ?? a.Name, 48, UserAccounts.Picture(a.Sid, 192));
        av.Margin = new Thickness(0, 0, 12, 0);
        head.Children.Add(av);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Txt(string.IsNullOrWhiteSpace(a.FullName) ? a.Name : a.FullName, 15, FontWeights.Bold));
        names.Children.Add(Txt(a.IsCurrent ? "You" : a.Admin ? "Admin" : "Standard", 12, brush: "SecondaryLabelBrush"));
        head.Children.Add(names);
        sp.Children.Add(head);
        var rows = new List<UIElement>
        {
            InfoRow("Account name", a.Name),
            InfoRow("Type", a.Admin ? "Administrator" : "Standard user"),
            InfoRow("Account", a.MicrosoftAccount ? "Microsoft account" : "Local account"),
        };
        if (!string.IsNullOrEmpty(a.Email)) rows.Add(InfoRow("Email", a.Email));
        rows.Add(Row("Account type and removal", WinLink("Family & Other Users", "ms-settings:otherusers")));
        sp.Children.Add(SheetGroup(rows.ToArray()));
        Sheet(sp, 440, ("Done", null));
    }

    // ================================================================== Internet Accounts

    void BuildInternetAccounts()
    {
        var accounts = Mail.MailService.Accounts.Where(a => a.Provider != Mail.MailProvider.Test).ToList();
        SectionTitle("Mail");
        if (accounts.Count == 0) Group(Row("No accounts", null, "Add an iCloud or Gmail account to use it in Mail"));
        else Group(accounts.Select(a =>
        {
            var icon = a.Provider == Mail.MailProvider.Gmail ? Tile("envelope", "#FF6259", "#E8342B") : Tile("icloud", "#62D0FA", "#1AA7E8");
            var acct = a;
            var details = Btn("Details…", () => AccountSheet(acct));
            return (UIElement)IconRow(icon, a.Title, details, a.Email, 28);
        }));
        _content.Children.Add(ButtonRow(Btn("Add Account…", Mail.MailSetupWindow.Open)));
        SectionTitle("Windows");
        string email = UserAccounts.CurrentEmail;
        Group(
            IconRow(Tile("windows", "#3D9BFF", "#0A6CFF"), UserAccounts.CurrentIsMicrosoftAccount ? "Microsoft account" : "Local account", WinLink("Accounts", "ms-settings:emailandaccounts"), string.IsNullOrEmpty(email) ? "Signed in to Windows" : email, 28));
    }

    void AccountSheet(Mail.MailAccount a)
    {
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(a.Provider == Mail.MailProvider.Gmail ? Tile("envelope", "#FF6259", "#E8342B") : Tile("icloud", "#62D0FA", "#1AA7E8"), a.Title, a.Email));
        sp.Children.Add(SheetGroup(InfoRow("Name", a.Name ?? ""), InfoRow("Email", a.Email), Row("Use with", Txt("Mail", 13, brush: "SecondaryLabelBrush"))));
        var delete = Btn("Delete Account…", () =>
        {
            if (ShellHost.Alert($"Delete the account “{a.Email}”?", "Its mail is removed from this computer (it stays on the server).", "Cancel", "Delete") != "Delete") return;
            Mail.MailService.RemoveAccount(a);
            CloseSheet();
            Rebuild();
        });
        Sheet(sp, 440, delete, ("Done", null));
    }

    // ================================================================== Privacy & Security

    /// <summary>The kinds of access Windows asks for (its consent store): id, name, icon, tile colour.</summary>
    static readonly (string id, string name, string sym, string top, string bottom)[] Privacies =
    {
        ("location", "Location Services", "location.fill", "#3D9BFF", "#0A6CFF"),
        ("contacts", "Contacts", "person.crop.circle", "#A3A3A8", "#7C7C81"),
        ("appointments", "Calendars", "calendar", "#FF6259", "#E8342B"),
        ("picturesLibrary", "Photos", "photo", "#FF9F0A", "#F07F00"),
        ("webcam", "Camera", "camera", "#5DD879", "#2FB24C"),
        ("microphone", "Microphone", "mic", "#FF6259", "#E8342B"),
        ("documentsLibrary", "Files and Folders", "folder", "#3D9BFF", "#0A6CFF"),
        ("graphicsCaptureProgrammatic", "Screen Recording", "record.circle", "#A3A3A8", "#7C7C81"),
        ("userNotificationListener", "Notifications", "bell", "#FF6259", "#E8342B"),
        ("radios", "Radios", "wifi", "#3D9BFF", "#0A6CFF"),
    };

    const string Consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    static bool ConsentAllowed(string path)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(path); return !string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    static bool SystemDenies(string cap)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey($@"{Consent}\{cap}"); return string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static void SetConsent(string path, bool allow)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(path); k.SetValue("Value", allow ? "Allow" : "Deny"); }
        catch (Exception ex) { App.Log($"privacy {path}: {ex.Message}"); }
    }

    void BuildPrivacy()
    {
        Hero(Tile("hand", Blue.top, Blue.bottom), "Privacy & Security", "Control which apps can use your location, camera, microphone and more, and how this computer keeps you safe.");
        SectionTitle("Privacy");
        Group(Privacies.Select(p => (UIElement)NavRow(Tile(p.sym, p.top, p.bottom), p.name, "privacy/" + p.id, ConsentAllowed($@"{Consent}\{p.id}") && !SystemDenies(p.id) ? null : "Off")));
        SectionTitle("Security");
        var holder = new StackPanel();
        _content.Children.Add(holder);
        Run(() => (av: AntivirusStatus(), fw: Firewall.State()), r =>
        {
            holder.Children.Add(GroupBox(new[]
            {
                IconRow(Tile("lock.shield", "#5DD879", "#2FB24C"), "Virus & threat protection", WinLink("Windows Security", "windowsdefender:"), r.av, 28),
                NavRow(Tile("lock.shield", "#8E8E93", "#636366"), "Firewall", "network/firewall", r.fw.AnyOn ? "Active" : "Inactive", null, 28),
                IconRow(Tile("lock", DarkTile.top, DarkTile.bottom), "Device encryption", WinLink("Windows Settings", "ms-settings:deviceencryption"), "Keeps your files unreadable without your password (BitLocker)", 28),
                IconRow(Tile("checkmark", Blue.top, Blue.bottom), "App & browser control", WinLink("Windows Security", "windowsdefender://appbrowser/"), "Which apps from the internet are allowed to open", 28),
            }));
        });
    }

    static string AntivirusStatus()
    {
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
            dynamic svc = locator.ConnectServer(".", @"root\SecurityCenter2");
            var names = new List<string>();
            bool on = false;
            foreach (dynamic p in svc.ExecQuery("SELECT displayName, productState FROM AntiVirusProduct"))
            {
                names.Add((string)p.displayName);
                int state = (int)p.productState;
                if (((state >> 12) & 0xF) is 1 or 3) on = true;   // (the "enabled" nibble)
            }
            return names.Count == 0 ? "No antivirus found" : $"{string.Join(", ", names)} - {(on ? "On" : "Off")}";
        }
        catch { return "Windows Security"; }
    }

    void BuildPrivacyPage(string cap)
    {
        var p = Privacies.FirstOrDefault(x => x.id == cap);
        if (p.id == null) { BuildPrivacy(); return; }
        Title_(p.name);
        bool sys = SystemDenies(cap);
        bool on = ConsentAllowed($@"{Consent}\{cap}") && !sys;
        Group(IconRow(Tile(p.sym, p.top, p.bottom), $"Let apps use {p.name.ToLowerInvariant()}", Switch(on, v => { SetConsent($@"{Consent}\{cap}", v); Rebuild(); }),
            sys ? "Turned off for everyone on this computer by an administrator" : "Apps ask before they use it the first time", 32));
        if (!on) return;
        // apps from the Store: each one on or off (Windows asks them); other apps: all of them together - Windows has
        // no switch for one of those, it only notes when each one used it
        var store = new List<(string name, UIElement row)>();
        var desktop = new Dictionary<string, (string exe, long start, long stop)>(StringComparer.CurrentCultureIgnoreCase);
        bool hasDesktop = false;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{Consent}\{cap}");
            foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (name == "NonPackaged")
                {
                    hasDesktop = true;
                    using var np = root.OpenSubKey("NonPackaged");
                    foreach (var exeKey in np?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var ek = np.OpenSubKey(exeKey);
                        long startT = ek?.GetValue("LastUsedTimeStart") is long s1 ? s1 : 0, stopT = ek?.GetValue("LastUsedTimeStop") is long s2 ? s2 : 0;
                        string exe = exeKey.Replace('#', '\\');
                        string label;
                        try { label = File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).FileDescription is { Length: > 0 } d ? d.Trim() : Path.GetFileNameWithoutExtension(exe); }
                        catch { label = Path.GetFileNameWithoutExtension(exe); }
                        // (one row an app: Discord keeps a folder for every version it was)
                        label = System.Text.RegularExpressions.Regex.Replace(label, @"-\d+(\.\d+)+$", "");
                        if (!desktop.TryGetValue(label, out var had) || Math.Max(startT, stopT) > Math.Max(had.start, had.stop)) desktop[label] = (exe, startT, stopT);
                    }
                    continue;
                }
                var app = AppCatalog.Apps.FirstOrDefault(a => a.ParsingName?.StartsWith(name + "!", StringComparison.OrdinalIgnoreCase) == true);
                string n = app?.Name ?? FriendlyPackage(name);
                if (store.All(x => !string.Equals(x.name, n, StringComparison.CurrentCultureIgnoreCase)))
                    store.Add((n, PrivacyAppRow(n, app?.IconSource, $@"{Consent}\{cap}\{name}")));
            }
        }
        catch { }
        if (store.Count > 0)
        {
            SectionTitle("Apps from the Microsoft Store");
            Group(store.OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.row));
        }
        if (hasDesktop || desktop.Count > 0)
        {
            string dkey = $@"{Consent}\{cap}\NonPackaged";
            SectionTitle("Other apps");
            var rows = new List<UIElement> { Row($"Let other apps use {p.name.ToLowerInvariant()}", Switch(ConsentAllowed(dkey), v => { SetConsent(dkey, v); Rebuild(); }), "Apps that don’t come from the Store - Windows can only allow or stop them all together") };
            foreach (var (label, (exe, start, stop)) in desktop.OrderByDescending(x => Math.Max(x.Value.start, x.Value.stop)))
            {
                var icon = new Image { Width = 22, Height = 22, Source = MacIcons.GenericApp };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                if (File.Exists(exe)) ShellIcons.Load(exe, 64, false, b => { if (b != null) icon.Source = b; });
                string when = stop == 0 && start > 0 ? "Using it now" : stop > 0 ? "Last used " + DateTime.FromFileTime(stop).ToString("MMM d, h:mm tt") : "";
                rows.Add(IconRow(null, label, Secondary(when, 12), null, 22, icon));
            }
            Group(rows);
        }
        if (store.Count == 0 && desktop.Count == 0) Group(Row("No apps have asked yet", null));
        if (cap == "location") Group(Row("Location history and default location", WinLink("Windows Settings", "ms-settings:privacy-location")));
    }

    FrameworkElement PrivacyAppRow(string name, string iconSource, string key)
    {
        var icon = new Image { Width = 22, Height = 22, Source = MacIcons.GenericApp };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        if (!string.IsNullOrEmpty(iconSource)) ShellIcons.Load(iconSource, 64, false, b => { if (b != null) icon.Source = b; });
        return IconRow(null, name, Switch(ConsentAllowed(key), v => SetConsent(key, v)), null, 22, icon);
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Windows Camera".</summary>
    static string FriendlyPackage(string family)
    {
        string n = family.Split('_')[0];
        if (n.Contains('.')) n = n[(n.LastIndexOf('.') + 1)..];
        return System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
    }
}

/// <summary>
/// Focus › Do Not Disturb: MacShell's own banners (new mail) stay away and every notification's sound is turned off
/// (Windows' setting - put back as it was when Do Not Disturb ends).
/// </summary>
public static class DoNotDisturb
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public static bool On => Settings.Current.DoNotDisturb;

    public static void Set(bool on)
    {
        var s = Settings.Current;
        if (s.DoNotDisturb == on) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (on)
            {
                s.DndSoundWas = k.GetValue("NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND") is int v ? v : 1;
                k.SetValue("NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", 0, RegistryValueKind.DWord);
            }
            else k.SetValue("NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", s.DndSoundWas ?? 1, RegistryValueKind.DWord);
        }
        catch { }
        s.DoNotDisturb = on;
        Settings.Save();
        ShellHost.MenuBar?.RefreshFocus();
    }
}
