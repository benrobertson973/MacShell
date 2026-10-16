using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace MacShell.Apps;

/// <summary>Sound, Displays, General (About, Software Update, Storage, Login Items, Language & Region, Date & Time,
/// Sharing) and Keyboard.</summary>
public partial class SettingsWindow
{
    static readonly (string top, string bottom) GrayTile = ("#A3A3A8", "#7C7C81");

    /// <summary>A slider that sets its value a moment after it moves (dragging doesn't flood Windows with changes).</summary>
    static Slider ThrottledSlider(double min, double max, double value, Action<double> set, double width = 220)
    {
        var s = new Slider { Style = (Style)Application.Current.Resources["MacSlider"], Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center };
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) => { t.Stop(); set(s.Value); };
        s.ValueChanged += (_, _) => { if (!t.IsEnabled) t.Start(); };
        return s;
    }

    /// <summary>A slider between two small icons (volume, brightness).</summary>
    static FrameworkElement IconSlider(string low, string high, Slider s)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        SymbolIcon Icon(string sym)
        {
            var i = new SymbolIcon { Symbol = sym, Width = 13, Height = 13, StrokeWidth = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            i.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
            return i;
        }
        sp.Children.Add(Icon(low));
        sp.Children.Add(s);
        sp.Children.Add(Icon(high));
        return sp;
    }

    // ================================================================== Sound

    bool _soundInput;

    void BuildSound()
    {
        var s = Settings.Current;
        SectionTitle("Sound Effects");
        string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        var files = Directory.Exists(media) ? Directory.GetFiles(media, "*.wav").OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.CurrentCultureIgnoreCase).ToArray() : Array.Empty<string>();
        string cur = AudioDevices.AlertSound;
        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        int sel = Array.FindIndex(files, f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
        if (sel < 0) { names.Insert(0, string.IsNullOrEmpty(cur) ? "None" : Path.GetFileNameWithoutExtension(cur)); sel = 0; }
        var alertRows = new List<UIElement>();
        if (files.Length > 0)
            alertRows.Add(Row("Alert sound", PopUp(names.ToArray(), sel, i =>
            {
                int fi = names.Count > files.Length ? i - 1 : i;
                if (fi < 0 || fi >= files.Length) return;
                AudioDevices.AlertSound = files[fi];
                AudioDevices.Play(files[fi]);
            }), "Played when something needs your attention"));
        alertRows.Add(Row("Play user interface sound effects", Switch(s.SoundEffects, v => { Settings.Current.SoundEffects = v; Save(); }), "Moving to the Trash, screenshots …"));
        Group(alertRows);

        SectionTitle("Output & Input");
        var tabs = Segmented(new[] { "Output", "Input" }, _soundInput ? 1 : 0, i => { _soundInput = i == 1; Rebuild(); });
        ((FrameworkElement)tabs).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)tabs).Margin = new Thickness(0, 0, 0, 10);
        _content.Children.Add(tabs);
        var devices = AudioDevices.List(_soundInput);
        if (devices.Count == 0) { Group(Row(_soundInput ? "No input devices" : "No output devices", null)); return; }
        Group(DeviceTable(devices));
        var def = devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
        if (!_soundInput)
        {
            var vol = ThrottledSlider(0, 1, AudioDevices.Volume(def.Id) ?? 0.5, v => { AudioDevices.SetVolume(def.Id, v); ShellHost.MenuBar?.UpdateSoundIcon(); });
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(IconSlider("speaker.0", "speaker.wave.2", vol));
            var mute = CheckBox("Mute", AudioDevices.Muted(def.Id), v => { AudioDevices.SetMuted(def.Id, v); ShellHost.MenuBar?.UpdateSoundIcon(); });
            mute.Margin = new Thickness(10, 0, 0, 0);
            mute.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(mute);
            var rows = new List<UIElement> { Row("Output volume", right) };
            if (AudioDevices.Balance(def.Id) is double bal)
                rows.Add(Row("Balance", LabeledSlider(-1, 1, bal, "left", "right", v => AudioDevices.SetBalance(def.Id, Math.Abs(v) < 0.04 ? 0 : v), 260)));
            Group(rows);
        }
        else
        {
            var vol = ThrottledSlider(0, 1, AudioDevices.Volume(def.Id) ?? 0.5, v => AudioDevices.SetVolume(def.Id, v));
            var meter = new LevelMeter();
            Group(Row("Input volume", IconSlider("mic", "mic", vol)), Row("Input level", meter));
            // (the microphone is listened to only while this tab is showing)
            var m = new AudioDevices.InputMeter(def.Id);
            OnLeave(m.Dispose);
            Every(TimeSpan.FromMilliseconds(70), () => meter.Level = m.Peak);
        }
        _content.Children.Add(ButtonRow(Btn("Volume Mixer…", ShellHost.OpenWindowsSoundFlyout), HelpButton("ms-settings:sound")));
    }

    /// <summary>The Mac's device list: Name and Type, the device in use highlighted; a click uses another.</summary>
    FrameworkElement DeviceTable(List<AudioDevices.Device> devices)
    {
        var sp = new StackPanel { Margin = new Thickness(6, 6, 6, 6) };
        Grid Line(string a, string b, bool head)
        {
            var g = new Grid { Height = head ? 22 : 26 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            var ta = head ? Txt(a, 11, FontWeights.SemiBold, "SecondaryLabelBrush") : Txt(a, 13);
            var tb = head ? Txt(b, 11, FontWeights.SemiBold, "SecondaryLabelBrush") : Txt(b, 13, brush: "SecondaryLabelBrush");
            ta.TextWrapping = tb.TextWrapping = TextWrapping.NoWrap;
            ta.TextTrimming = TextTrimming.CharacterEllipsis;
            ta.VerticalAlignment = tb.VerticalAlignment = VerticalAlignment.Center;
            ta.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetColumn(tb, 1);
            g.Children.Add(ta); g.Children.Add(tb);
            return g;
        }
        sp.Children.Add(Line("Name", "Type", true));
        foreach (var d in devices)
        {
            var g = Line(d.Name, d.Type, false);
            var row = new Border { CornerRadius = new CornerRadius(5), Child = g, Background = Brushes.Transparent };
            if (d.IsDefault)
            {
                row.SetResourceReference(Border.BackgroundProperty, "SelectionBrush");
                foreach (TextBlock t in g.Children) t.Foreground = Brushes.White;
            }
            else
            {
                string id = d.Id;
                row.MouseLeftButtonUp += (_, _) => { AudioVolume.SetDefaultDevice(id); Rebuild(); ShellHost.MenuBar?.UpdateSoundIcon(); };
            }
            sp.Children.Add(row);
        }
        return sp;
    }

    /// <summary>The Mac's input level: a row of small bars lit up to the level.</summary>
    sealed class LevelMeter : FrameworkElement
    {
        double _level;
        public double Level { get => _level; set { _level = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
        public LevelMeter() { Width = 230; Height = 12; }
        protected override void OnRender(DrawingContext dc)
        {
            const int n = 15;
            double gap = 3, w = (ActualWidth - gap * (n - 1)) / n;
            // (a gentle curve: speech fills about half)
            int lit = (int)Math.Round(Math.Sqrt(_level) * n);
            var off = (Brush)FindResource("QuaternaryLabelBrush");
            for (int i = 0; i < n; i++)
            {
                Brush b = i < lit ? new SolidColorBrush(i >= n - 2 ? Color.FromRgb(0xFF, 0x3B, 0x30) : i >= n - 5 ? Color.FromRgb(0xFF, 0xCC, 0x00) : (Color)FindResource("AccentColor")) : off;
                dc.DrawRoundedRectangle(b, null, new Rect(i * (w + gap), 2, w, ActualHeight - 4), 2, 2);
            }
        }
    }

    // ================================================================== Displays

    string _display;

    void BuildDisplays()
    {
        var displays = DisplayInfo.All();
        if (displays.Count == 0) { Group(Row("No display information", null)); return; }
        var d = displays.FirstOrDefault(x => x.GdiName == _display) ?? displays[0];
        // the displays, drawn with the wallpaper - a click shows one's settings
        var pics = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 16) };
        int maxW = displays.Max(x => x.Bounds.Width);
        foreach (var x in displays)
        {
            double pw = 150.0 * x.Bounds.Width / maxW, ph = pw * x.Bounds.Height / Math.Max(1, x.Bounds.Width);
            var cell = new StackPanel { Margin = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand };
            var pic = new Border { Width = pw, Height = ph, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(x == d ? 3 : 1), Background = new ImageBrush(Wallpaper.Image) { Stretch = Stretch.UniformToFill }, VerticalAlignment = VerticalAlignment.Bottom };
            if (x == d) pic.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else pic.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            cell.Children.Add(pic);
            var t = Txt(x.Name, 12, x == d ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 6, 0, 0);
            cell.Children.Add(t);
            string id = x.GdiName;
            cell.MouseLeftButtonUp += (_, _) => { _display = id; Rebuild(); };
            pics.Children.Add(cell);
        }
        _content.Children.Add(pics);
        var rows = new List<UIElement>();
        if (displays.Count > 1) rows.Add(InfoRow("Use as", d.Main ? "Main display" : "Extended display"));
        rows.Add(InfoRow("Resolution", $"{d.Width} × {d.Height}"));
        rows.Add(InfoRow("Looks like", $"{Math.Round(d.Width / d.Scale)} × {Math.Round(d.Height / d.Scale)} ({d.Scale * 100:0}% scale)"));
        rows.Add(InfoRow("Refresh rate", $"{d.RefreshHz:0.##} Hz"));
        if (d.Rotation != 0) rows.Add(InfoRow("Rotation", $"{d.Rotation}°"));
        rows.Add(InfoRow("Connection", d.Connection));
        Group(rows);
        if (d.BuiltIn && Brightness.Get() is int bright)
        {
            var b = ThrottledSlider(0, 100, bright, v => Brightness.Set((int)Math.Round(v)));
            var bRows = new List<UIElement> { Row("Brightness", IconSlider("sun", "sun.max", b)) };
            if (PowerSettings.Get(AdaptiveBrightness, PowerSettings.OnAdapter) is uint ab)
                bRows.Add(Row("Automatically adjust brightness", Switch(ab != 0, v =>
                {
                    PowerSettings.Set(AdaptiveBrightness, true, v ? 1u : 0);
                    PowerSettings.Set(AdaptiveBrightness, false, v ? 1u : 0);
                })));
            Group(bRows);
        }
        Group(Row("Resolution, scale, refresh rate, HDR and arrangement", WinLink("Display Settings", "ms-settings:display")));

        var t2 = Settings.Current.ScreenTrim is { Length: 4 } st ? st : new int[4];
        SectionTitle("Screen Edges");
        Group(
            Row("Cut off the edges of the screen", Btn("Set Up…", () => Shell.ScreenEdgesWindow.Open(firstRun: false)),
                "For a screen whose edges you can’t fully see - a TV, or a covered or broken edge. The menu bar, the Dock and your windows stay inside what’s left."),
            Row("Left", TrimField(0, t2[0])),
            Row("Top", TrimField(1, t2[1])),
            Row("Right", TrimField(2, t2[2])),
            Row("Bottom", TrimField(3, t2[3])));
    }

    /// <summary>The power setting "Enable adaptive brightness" (displays with a light sensor).</summary>
    static readonly PowerSettings.Setting AdaptiveBrightness = new("7516b95f-f776-4464-8c53-06167f40cc99", "fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

    /// <summary>How much of one edge is cut off (pixels): typed, and used when Return is pressed or the field is left.</summary>
    static FrameworkElement TrimField(int side, int value)
    {
        var box = Field(value.ToString(), 64);
        box.TextAlignment = TextAlignment.Right;
        void Commit()
        {
            int.TryParse(box.Text.Trim(), out int v);
            v = Math.Max(0, v);
            var cur = Settings.Current.ScreenTrim is { Length: 4 } c ? (int[])c.Clone() : new int[4];
            if (cur[side] != v)
            {
                cur[side] = v;
                Settings.Current.ScreenTrim = cur.Any(n => n > 0) ? cur : null;
                Settings.Save(notify: false);
                ShellHost.ApplyScreenTrim();
            }
            var used = ShellHost.TrimPx;   // (what applies: no more than leaves most of the screen)
            box.Text = (side switch { 0 => used.Left, 1 => used.Top, 2 => used.Right, _ => used.Bottom }).ToString();
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostKeyboardFocus += (_, _) => Commit();
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(box);
        var px = Secondary("px");
        px.VerticalAlignment = VerticalAlignment.Center;
        px.Margin = new Thickness(6, 0, 0, 0);
        sp.Children.Add(px);
        return sp;
    }

    // ================================================================== General

    void BuildGeneral()
    {
        Hero(Tile("gear", GrayTile.top, GrayTile.bottom), "General", "Manage your overall setup and preferences for this computer, such as software updates, languages, login items and more.");
        Group(
            NavRow(Tile("laptop", GrayTile.top, GrayTile.bottom), "About", "general/about"),
            NavRow(Tile("arrow.triangle.2.circlepath", GrayTile.top, GrayTile.bottom), "Software Update", "general/update"),
            NavRow(Tile("internaldrive", GrayTile.top, GrayTile.bottom), "Storage", "general/storage"));
        Group(
            NavRow(Tile("list", GrayTile.top, GrayTile.bottom), "Login Items", "general/login"),
            NavRow(Tile("globe", Blue.top, Blue.bottom), "Language & Region", "general/language"),
            NavRow(Tile("clock", Blue.top, Blue.bottom), "Date & Time", "general/datetime"),
            NavRow(Tile("person.2", Blue.top, Blue.bottom), "Sharing", "general/sharing"));
        Group(
            IconRow(Tile("airdrop", Blue.top, Blue.bottom), "Nearby Sharing", WinLink("Windows Settings", "ms-settings:crossdevice"), "Share files with computers and phones nearby"),
            IconRow(Tile("arrow.clockwise", GrayTile.top, GrayTile.bottom), "Transfer or Reset", WinLink("Recovery", "ms-settings:recovery"), "Reset this computer, or go back to an earlier version of Windows"));
    }

    void BuildGeneralPage(string sub)
    {
        switch (sub)
        {
            case "about": Title_("About"); BuildAbout(); break;
            case "update": Title_("Software Update"); BuildSoftwareUpdate(); break;
            case "storage": Title_("Storage"); BuildStorage(); break;
            case "login": Title_("Login Items"); BuildLoginItems(); break;
            case "language": Title_("Language & Region"); BuildLanguage(); break;
            case "datetime": Title_("Date & Time"); BuildDateTime(); break;
            case "sharing": Title_("Sharing"); BuildSharing(); break;
            default: BuildGeneral(); break;
        }
    }

    void BuildAbout()
    {
        var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 16) };
        var img = new Image { Source = MacIcons.Laptop(Wallpaper.Image), Width = 170 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        head.Children.Add(img);
        var n = Txt(MachineInfo.Model, 20, FontWeights.Bold);
        n.HorizontalAlignment = HorizontalAlignment.Center; n.Margin = new Thickness(0, 12, 0, 0);
        head.Children.Add(n);
        var md = Txt(MachineInfo.ModelDetail, 12, brush: "SecondaryLabelBrush");
        md.HorizontalAlignment = HorizontalAlignment.Center;
        head.Children.Add(md);
        _content.Children.Add(head);
        var info = Group(InfoRow("Name", Environment.MachineName), InfoRow("Chip", MachineInfo.Cpu), InfoRow("Memory", MachineInfo.MemoryGB), InfoRow("Serial number", "…"), InfoRow("Windows", $"{MachineInfo.OsName} {MachineInfo.OsVersion}"), InfoRow("MacShell", Updater.VersionText));
        // (the serial number comes from WMI: a moment later)
        Run(() => MachineInfo.Serial, serial =>
        {
            if (info.Child is StackPanel sp && sp.Children.OfType<Grid>().FirstOrDefault(g => g.Children[0] is StackPanel l && l.Children[0] is TextBlock t && t.Text == "Serial number") is { } row && row.Children[1] is TextBox tb) tb.Text = serial;
        });
        _content.Children.Add(ButtonRow(Btn("System Report…", () => { try { Process.Start(new ProcessStartInfo("msinfo32.exe") { UseShellExecute = true }); } catch { } })));
        SectionTitle("Displays");
        Group(DisplayInfo.All().Select(d => (UIElement)InfoRow(d.Name, $"{d.Width} × {d.Height} · {d.RefreshHz:0.##} Hz")).DefaultIfEmpty(Row("No displays", null)));
        SectionTitle("Storage");
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
        Group(InfoRow(FinderLocation.DisplayName(sys.RootDirectory.FullName), $"{Size(sys.AvailableFreeSpace)} available of {Size(sys.TotalSize)}"), Row("All drives", Btn("Storage Settings…", () => Open("general/storage"))));
    }

    static string Size(long bytes) => bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.##} TB" : bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.#} GB" : bytes >= 1_000_000 ? $"{bytes / 1e6:0.#} MB" : $"{Math.Max(0, bytes / 1000)} KB";

    void BuildSoftwareUpdate()
    {
        var status = string.IsNullOrEmpty(Updater.Status) ? "MacShell checks for updates every few minutes and installs them when it’s restarted." : Updater.Status;
        Group(IconRow(MacIcons.Finder, $"MacShell {Updater.VersionText}", Btn("Check Now", () => RunThenRebuild(() => Updater.CheckAsync(userInitiated: true).Wait(20000))), status, 32));
        if (Updater.Staged != null) Group(Row("An update is ready", Btn("Restart MacShell", Updater.Restart, true), "It takes a few seconds; your apps keep running."));
        Group(IconRow(Tile("windows", "#3D9BFF", "#0A6CFF"), "Windows Update", WinLink("Windows Update", "ms-settings:windowsupdate"), "Updates for Windows, drivers and Microsoft apps", 32));
    }

    void BuildStorage()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            long total = drive.TotalSize, free = drive.AvailableFreeSpace, used = total - free;
            string name = FinderLocation.DisplayName(drive.RootDirectory.FullName);
            var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
            var top = new Grid();
            top.Children.Add(Txt(name, 13, FontWeights.SemiBold));
            var avail = Secondary($"{Size(free)} available of {Size(total)}", 12);
            avail.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(avail);
            sp.Children.Add(top);
            var bar = new Grid { Height = 16, Margin = new Thickness(0, 8, 0, 4) };
            var back = new Border { CornerRadius = new CornerRadius(4) };
            back.SetResourceReference(Border.BackgroundProperty, "QuaternaryLabelBrush");
            bar.Children.Add(back);
            var fill = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
            double frac = total > 0 ? (double)used / total : 0;
            fill.Background = new SolidColorBrush(frac > 0.9 ? Color.FromRgb(0xFF, 0x3B, 0x30) : frac > 0.75 ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0x0A, 0x84, 0xFF));
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * frac;
            bar.Children.Add(fill);
            sp.Children.Add(bar);
            var leg = Secondary($"{Size(used)} used", 11);
            sp.Children.Add(leg);
            Group(sp);
        }
        SectionTitle("Recommendations");
        var rb = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        NativeMethods.SHQueryRecycleBin(null, ref rb);
        Group(
            Row("Trash", Btn("Empty Trash…", () => { FileOps.EmptyTrash(); Rebuild(); }), rb.i64NumItems > 0 ? $"{rb.i64NumItems:N0} items, {Size(rb.i64Size)}" : "Empty"),
            Row("Downloads", Btn("Show in Finder", () => ShellHost.OpenInternal("internal:downloads")), "Files you downloaded"),
            Row("Free up space automatically", WinLink("Storage Sense", "ms-settings:storagesense"), "Windows can empty the Trash and remove temporary files for you"));
    }

    // ------------------------------------------------------------------ Login Items

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    record LoginEntry(string Name, string Command, string Kind, bool Enabled, string IconSource)
    {
        /// <summary>The name shown: "MicrosoftEdgeAutoLaunch_BEA8…" is "Microsoft Edge" (its app's own name).</summary>
        public string Display
        {
            get
            {
                var m = System.Text.RegularExpressions.Regex.Match(Name, @"^(.+?)(AutoLaunch)?_[0-9A-Fa-f]{12,}$");
                if (!m.Success) return Name;
                try { if (File.Exists(IconSource) && FileVersionInfo.GetVersionInfo(IconSource).FileDescription is { Length: > 0 } d) return d.Trim(); } catch { }
                return System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "(?<=[a-z])(?=[A-Z])", " ");
            }
        }
    }

    static List<LoginEntry> LoginEntries()
    {
        var list = new List<LoginEntry>();
        static bool Approved(RegistryKey root, string key, string name)
        {
            using var k = root.OpenSubKey(key);
            return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;   // 02 on, 03 off
        }
        static string Exe(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables(cmd ?? "").Trim();
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); return e > 0 ? cmd[1..e] : cmd.Trim('"'); }
            int x = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return x > 0 ? cmd[..(x + 4)] : cmd.Split(' ')[0];
        }
        foreach (var (root, scope) in new[] { (Registry.CurrentUser, "user"), (Registry.LocalMachine, "all") })
        {
            using var k = root.OpenSubKey(RunKey);
            if (k == null) continue;
            foreach (var name in k.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string cmd = k.GetValue(name) as string;
                list.Add(new LoginEntry(name, cmd, "run:" + scope, Approved(root, ApprovedRun, name), Exe(cmd)));
            }
        }
        foreach (var (dir, scope) in new[] { (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "user"), (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "all") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                list.Add(new LoginEntry(Path.GetFileNameWithoutExtension(f), f, "folder:" + scope, Approved(scope == "user" ? Registry.CurrentUser : Registry.LocalMachine, ApprovedFolder, Path.GetFileName(f)), f));
        }
        return list.OrderBy(e => e.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void SetLoginEnabled(LoginEntry e, bool on)
    {
        string key = e.Kind.StartsWith("run") ? ApprovedRun : ApprovedFolder;
        string name = e.Kind.StartsWith("run") ? e.Name : Path.GetFileName(e.Command);
        using var k = Registry.CurrentUser.CreateSubKey(key);
        var data = new byte[12];
        data[0] = (byte)(on ? 2 : 3);
        if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(name, data, RegistryValueKind.Binary);
    }

    static void RemoveLogin(LoginEntry e)
    {
        if (e.Kind == "run:user") { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue(e.Name, false); }
        else if (e.Kind == "folder:user") FileOps.MoveToTrash(new[] { e.Command });
    }

    void BuildLoginItems()
    {
        SectionTitle("Open at Login");
        var rows = new List<UIElement>
        {
            IconRow(MacIcons.Finder, "MacShell", Switch(LoginItem.Enabled, v => Task.Run(() => LoginItem.Set(v))), "Takes over the desktop every time you sign in", 22),
        };
        foreach (var e in LoginEntries())
        {
            bool mine = e.Kind.EndsWith(":user");
            var icon = new Image { Width = 22, Height = 22 };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            icon.Source = MacIcons.GenericApp;
            if (!string.IsNullOrEmpty(e.IconSource)) ShellIcons.Load(e.IconSource, 64, false, b => { if (b != null) icon.Source = b; });
            UIElement control;
            if (mine)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var entry = e;
                sp.Children.Add(Switch(e.Enabled, v => SetLoginEnabled(entry, v)));
                var minus = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = "−", Width = 24, Height = 22, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0), ToolTip = "Remove" };
                minus.Click += (_, _) => { RemoveLogin(entry); Rebuild(); };
                sp.Children.Add(minus);
                control = sp;
            }
            else control = StatusDot(e.Enabled ? Green : Gray, e.Enabled ? "For all users" : "Off - for all users");
            rows.Add(IconRow(null, e.Display, control, null, 22, icon));
        }
        Group(rows);
        _content.Children.Add(ButtonRow(Btn("Add App…", () =>
        {
            var r = FilePanel.ShowModal(this, new FilePanelRequest { Title = "Choose an app to open at login", Prompt = "Add", AppKey = "macshell:loginitems", Folder = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Filters = { "Apps (*.exe;*.lnk)", "All files (*.*)" } });
            if (r?.Paths.Count > 0)
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                foreach (var p in r.Paths) k.SetValue(Path.GetFileNameWithoutExtension(p), $"\"{p}\"");
                Rebuild();
            }
        }), WinLink("Startup Apps", "ms-settings:startupapps")));
        Footnote("Apps set to open at login for every user of this computer are changed by an administrator, in Windows’ Startup Apps.");
    }

    // ------------------------------------------------------------------ Language & Region

    const string Intl = @"Control Panel\International";

    static string IntlValue(string name) { using var k = Registry.CurrentUser.OpenSubKey(Intl); return k?.GetValue(name) as string; }

    static void SetIntl(string name, string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Intl)) k.SetValue(name, value);
        SysParams.Broadcast("intl");
        CultureInfo.CurrentCulture.ClearCachedData();
    }

    void BuildLanguage()
    {
        SectionTitle("Preferred Languages");
        var langs = new List<string>();
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"); if (k?.GetValue("Languages") is string[] l) langs.AddRange(l); } catch { }
        if (langs.Count == 0) langs.Add(CultureInfo.CurrentUICulture.Name);
        var rows = langs.Select((l, i) =>
        {
            string name;
            try { var c = CultureInfo.GetCultureInfo(l); name = c.DisplayName; } catch { name = l; }
            return (UIElement)Row(name, null, i == 0 ? "Primary" : null);
        }).ToList();
        rows.Add(ButtonRow(WinLink("Add or Remove Languages", "ms-settings:regionlanguage")));
        Group(rows);

        // (one per country, named in English - a region made from a culture's name can come out in that culture's own script)
        var regions = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => { try { return new RegionInfo(c.Name).TwoLetterISORegionName; } catch { return null; } })
            .Where(code => code is { Length: 2 } && char.IsLetter(code[0])).Distinct()
            .Select(code => { try { return new RegionInfo(code); } catch { return null; } }).Where(r => r != null)
            .OrderBy(r => r.EnglishName, StringComparer.OrdinalIgnoreCase).ToList();
        string geo = null;
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo"); geo = k?.GetValue("Name") as string; } catch { }
        geo ??= RegionInfo.CurrentRegion.TwoLetterISORegionName;
        int rsel = Math.Max(0, regions.FindIndex(r => r.TwoLetterISORegionName == geo));
        int fdow = int.TryParse(IntlValue("iFirstDayOfWeek"), out int f) ? f : 6;   // 0 Monday … 6 Sunday
        string[] days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        string[] dateFormats = { "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d.M.yyyy", "dd.MM.yyyy" };
        string curDate = IntlValue("sShortDate") ?? CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        var dateNames = dateFormats.Select(p => DateTime.Today.ToString(p, CultureInfo.InvariantCulture)).ToList();
        int dsel = Array.IndexOf(dateFormats, curDate);
        if (dsel < 0) { dateNames.Insert(0, DateTime.Today.ToString(curDate, CultureInfo.InvariantCulture)); dsel = 0; }
        bool metric = IntlValue("iMeasure") == "0";
        Group(
            Row("Region", PopUp(regions.Select(r => r.EnglishName).ToArray(), rsel, i => { SetUserGeoName(regions[i].TwoLetterISORegionName); Rebuild(); })),
            InfoRow("Calendar", CultureInfo.CurrentCulture.Calendar is GregorianCalendar ? "Gregorian" : CultureInfo.CurrentCulture.Calendar.GetType().Name.Replace("Calendar", "")),
            Row("Temperature", Segmented(new[] { "Celsius (°C)", "Fahrenheit (°F)" }, Settings.Current.Celsius ? 0 : 1, i => { Settings.Current.Celsius = i == 0; Save(); Weather.Refresh(); })),
            Row("Measurement system", Segmented(new[] { "Metric", "US" }, metric ? 0 : 1, i => SetIntl("iMeasure", i == 0 ? "0" : "1"))),
            Row("First day of week", PopUp(days, fdow, i => SetIntl("iFirstDayOfWeek", i.ToString()))),
            Row("Date format", PopUp(dateNames.ToArray(), dsel, i =>
            {
                int fi = dateNames.Count > dateFormats.Length ? i - 1 : i;
                if (fi >= 0) { SetIntl("sShortDate", dateFormats[fi]); Rebuild(); }
            })),
            InfoRow("Number format", (1234567.89).ToString("N2", CultureInfo.CurrentCulture)));
        Footnote($"{DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern, CultureInfo.CurrentCulture)} at {DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture)}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetUserGeoName(string geoName);

    // ------------------------------------------------------------------ Date & Time

    void BuildDateTime()
    {
        var s = Settings.Current;
        string syncType = null;
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"); syncType = k?.GetValue("Type") as string; } catch { }
        bool autoTime = syncType is "NTP" or "AllSync" or "NT5DS";
        int tzStart = 3;
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\tzautoupdate"); tzStart = k?.GetValue("Start") is int v ? v : 3; } catch { }
        bool autoZone = tzStart != 4;
        var now = Txt(DateTime.Now.ToString("F", CultureInfo.CurrentCulture), 13, brush: "SecondaryLabelBrush");
        Every(TimeSpan.FromSeconds(1), () => now.Text = DateTime.Now.ToString("F", CultureInfo.CurrentCulture));
        Group(
            Row("Set time and date automatically", Switch(autoTime, v => RunThenRebuild(() =>
                Elevated.Run("cmd.exe", v ? "/c w32tm /config /syncfromflags:manual /manualpeerlist:time.windows.com /update && sc config w32time start= auto && net start w32time & w32tm /resync"
                                          : "/c w32tm /config /syncfromflags:NO /update"))), "Windows asks for an administrator"),
            Row("Date and time", now),
            Row("Set the time now", Btn("Sync Now", () => RunThenRebuild(() => Elevated.Run("cmd.exe", "/c net start w32time & w32tm /resync /force"))), "From time.windows.com"));
        var zones = TimeZoneInfo.GetSystemTimeZones().ToList();
        var local = TimeZoneInfo.Local;
        int zsel = Math.Max(0, zones.FindIndex(z => z.Id == local.Id));
        Group(
            Row("Set time zone automatically using your current location", Switch(autoZone, v => RunThenRebuild(() =>
                Elevated.Run("cmd.exe", $"/c sc config tzautoupdate start= {(v ? "demand" : "disabled")}"))), "Windows asks for an administrator"),
            Row("Time zone", PopUp(zones.Select(z => z.DisplayName).ToArray(), zsel, i =>
            {
                string id = zones[i].Id;
                RunThenRebuild(() =>
                {
                    using var p = Process.Start(new ProcessStartInfo("tzutil.exe", $"/s \"{id}\"") { UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(10000);
                    TimeZoneInfo.ClearCachedData();
                });
            })));
        Group(Row("24-hour time", Switch(s.Clock24Hour, v =>
        {
            Settings.Current.Clock24Hour = v; Save();
            SetIntl("sShortTime", v ? "HH:mm" : "h:mm tt");
            SetIntl("sTimeFormat", v ? "HH:mm:ss" : "h:mm:ss tt");
        }), "The menu bar’s clock and Windows’ own"));
    }

    // ------------------------------------------------------------------ Sharing

    void BuildSharing()
    {
        Group(
            Row("Computer name", Btn("Edit…", RenameComputer), Environment.MachineName),
            InfoRow("Local hostname", System.Net.Dns.GetHostName()));
        Footnote("Computers on your local network can reach this one by its name.");
        SectionTitle("Content & Media");
        Group(
            IconRow(Tile("folder", Blue.top, Blue.bottom), "File Sharing", WinLink("Advanced Sharing", "ms-settings:network-advancedsettings"), "Share folders with other computers on your network"),
            IconRow(Tile("airdrop", Blue.top, Blue.bottom), "Nearby Sharing", WinLink("Windows Settings", "ms-settings:crossdevice"), "Send files to computers and phones nearby"));
        SectionTitle("Advanced");
        Group(IconRow(Tile("display", Blue.top, Blue.bottom), "Remote Desktop", WinLink("Windows Settings", "ms-settings:remotedesktop"), "Let other computers use this one over the network"));
    }

    void RenameComputer()
    {
        var box = Field(Environment.MachineName, 280);
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(Tile("laptop", GrayTile.top, GrayTile.bottom), "Computer Name", "Letters, numbers and hyphens, up to 15 characters. Windows asks for an administrator; the new name is used after a restart."));
        sp.Children.Add(box);
        Sheet(sp, 420, ("Cancel", null), ("Rename", () =>
        {
            string name = box.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9-]{1,15}$") || name.All(char.IsDigit)) { ShellHost.ShowAlert("That name can’t be used.", "Use letters, numbers and hyphens, up to 15 characters (not only numbers)."); return false; }
            if (name == Environment.MachineName) return true;
            Run(() => Elevated.Run("powershell.exe", $"-NoProfile -Command Rename-Computer -NewName '{name}' -Force"), ok =>
            {
                if (ok) ShellHost.ShowAlert($"This computer will be called “{name}”.", "The new name is used after you restart.");
            });
            return true;
        }));
    }

    // ================================================================== Keyboard

    void BuildKeyboard()
    {
        // key repeat: 8 steps from slow to fast (Windows' 0-31), and the delay: 4 steps from long to short (Windows' 3-0)
        int speed = SysParams.KeyRepeatSpeed, delay = SysParams.KeyRepeatDelay;
        int speedStep = (int)Math.Round(speed / 31.0 * 7);
        Group(
            Row("Key repeat rate", TickSlider(8, speedStep, "Slow", "Fast", v => SysParams.KeyRepeatSpeed = (int)Math.Round(v * 31 / 7.0))),
            Row("Delay until repeat", TickSlider(4, 3 - delay, "Long", "Short", v => SysParams.KeyRepeatDelay = 3 - v)));
        Group(Row("Keyboard shortcuts", Btn("Keyboard Shortcuts…", () => Open("keyboard/shortcuts")), "MacShell’s, and how ⌘ maps to this keyboard"));
        SectionTitle("Text Input");
        var sources = System.Windows.Input.InputLanguageManager.Current.AvailableInputLanguages?.Cast<CultureInfo>().ToList() ?? new List<CultureInfo>();
        Group(Row("Input Sources", WinLink("Edit…", "ms-settings:keyboard"), sources.Count == 0 ? CultureInfo.CurrentCulture.DisplayName : string.Join(", ", sources.Select(c => c.DisplayName))));
        SectionTitle("Dictation");
        Group(Row("Dictation", Txt("Win + H", 13, brush: "SecondaryLabelBrush"), "Speak instead of typing in any text field (Windows voice typing)"));
    }

    void BuildKeyboardPage(string sub)
    {
        Title_("Keyboard Shortcuts");
        var note = Txt("On a PC keyboard, ⌘ (Command) is Ctrl inside MacShell’s own apps (Finder, Notes, QuickTime …); shortcuts for the whole computer use the Windows key.", 12, brush: "SecondaryLabelBrush");
        note.Margin = new Thickness(4, 0, 0, 10);
        _content.Children.Add(note);
        (string section, (string, string)[] items)[] groups =
        {
            ("Launchpad, Dock & Mission Control", new[] { ("Launchpad", "the Dock’s icon (Win key: MacShell settings)"), ("Mission Control", "Win + ↑"), ("Show Desktop", "Win + D"), ("App Switcher (⌘Tab)", "Alt + Tab  or  Win + Tab"), ("Force Quit", "Ctrl + Alt + Esc") }),
            ("Spotlight", new[] { ("Spotlight", "Win + Space  or  Alt + Space") }),
            ("Screenshots", new[] { ("Screenshot (entire screen)", "Win + Shift + 3"), ("Screenshot (selected portion)", "Win + Shift + 4"), ("Screenshot and recording options", "Win + Shift + 5"), ("Stop screen recording", "Ctrl + Win + Esc") }),
            ("MacShell", new[] { ("New Finder window", "Win + E"), ("System Settings", "Win + ,"), ("Return to Windows", "Ctrl + Alt + Shift + Q") }),
            ("Finder", new[] { ("Open", "Ctrl + O  /  Ctrl + ↓"), ("Rename", "Return"), ("Quick Look", "Space"), ("Move to Trash", "Delete  /  Ctrl + Backspace"), ("Get Info", "Ctrl + I"), ("New Folder", "Ctrl + Shift + N"), ("New Tab", "Ctrl + T"), ("Views", "Ctrl + 1 – 4"), ("Enclosing Folder", "Ctrl + ↑"), ("Go to Folder", "Ctrl + Shift + G"), ("Show hidden files", "Ctrl + Shift + .") }),
        };
        foreach (var (section, items) in groups)
        {
            SectionTitle(section);
            Group(items.Select(i => (UIElement)Row(i.Item1, Txt(i.Item2, 12, brush: "SecondaryLabelBrush"))));
        }
    }
}
