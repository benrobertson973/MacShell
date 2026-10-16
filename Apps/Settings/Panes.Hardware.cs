using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace MacShell.Apps;

/// <summary>Battery (Energy), Screen Saver, Mouse, Trackpad, Printers & Scanners and Accessibility.</summary>
public partial class SettingsWindow
{
    static readonly (string top, string bottom) GreenTile = ("#5DD879", "#2FB24C");

    // ================================================================== Battery / Energy

    void BuildBattery()
    {
        bool battery = PowerSettings.HasBattery;
        var rows = new List<UIElement>();
        if (battery && PowerSettings.Get(PowerSettings.SaverThreshold, false) is uint threshold)
            rows.Add(Row("Low Power Mode", PopUp(new[] { ("Never", 0u), ("When the battery is below 20%", 20u), ("Only on Battery", 100u) }, threshold, v => PowerSettings.Set(PowerSettings.SaverThreshold, false, v)),
                PowerSettings.SaverOn ? "On now" : "Saves energy: background activity and brightness are turned down"));
        string[] modeNames = PowerSettings.Modes.Select(m => m.name).ToArray();
        int ModeIndex(bool ac) { var g = PowerSettings.PowerMode(ac); int i = Array.FindIndex(PowerSettings.Modes, m => m.id == g); return i < 0 ? 1 : i; }
        if (battery) rows.Add(Row("Energy Mode on battery", PopUp(modeNames, ModeIndex(false), i => PowerSettings.SetPowerMode(false, PowerSettings.Modes[i].id))));
        rows.Add(Row(battery ? "Energy Mode on power adapter" : "Energy Mode", PopUp(modeNames, ModeIndex(true), i => PowerSettings.SetPowerMode(true, PowerSettings.Modes[i].id))));
        Group(rows);
        if (!battery)
        {
            uint? sleep = PowerSettings.Get(PowerSettings.SleepAfter, true);
            Group(Row("Prevent automatic sleeping when the display is off", Switch(sleep == 0, v => PowerSettings.Set(PowerSettings.SleepAfter, true, v ? 0u : 1800u))));
            _content.Children.Add(ButtonRow(Btn("Options…", () => Open("battery/options")), HelpButton("ms-settings:powersleep")));
            return;
        }
        var st = PowerSettings.Now();
        var holder = new StackPanel();
        _content.Children.Add(holder);
        holder.Children.Add(LoadingRow("Battery health…"));
        Run(PowerSettings.Report, r =>
        {
            holder.Children.Clear();
            var h = r.health;
            string healthText = h == null ? "Unknown" : h.MaximumPercent >= 80 ? "Normal" : "Service Recommended";
            var info = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = "ⓘ", Width = 24, Height = 22, MinWidth = 0, Padding = new Thickness(0), ToolTip = "Details" };
            info.Click += (_, _) => BatterySheet(h);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var ht = Secondary(healthText);
            ht.VerticalAlignment = VerticalAlignment.Center;
            ht.Margin = new Thickness(0, 0, 10, 0);
            right.Children.Add(ht);
            right.Children.Add(info);
            holder.Children.Add(GroupBox(new[] { Row("Battery Health", right) }));
            holder.Children.Add(SectionLabel("Battery Level"));
            holder.Children.Add(GroupBox(new[] { BatteryChart(r.usage, st.BatteryLifePercent) }));
        });
        _content.Children.Add(ButtonRow(Btn("Options…", () => Open("battery/options")), HelpButton("ms-settings:batterysaver")));
    }

    void BatterySheet(PowerSettings.Health h)
    {
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(Tile("battery", GreenTile.top, GreenTile.bottom), "Battery Health", h == null ? "Windows didn’t report the battery’s health." : h.MaximumPercent >= 80 ? "Your battery is functioning normally." : "Your battery’s capacity is noticeably lower than when it was new."));
        if (h != null)
            sp.Children.Add(SheetGroup(
                InfoRow("Maximum Capacity", $"{h.MaximumPercent}%"),
                InfoRow("Full charge", $"{h.FullMWh / 1000.0:0.#} Wh of {h.DesignMWh / 1000.0:0.#} Wh when new"),
                InfoRow("Cycle Count", h.Cycles > 0 ? h.Cycles.ToString() : "—"),
                InfoRow("Chemistry", h.Chemistry ?? "—")));
        Sheet(sp, 420, ("Done", null));
    }

    /// <summary>The battery's level over the last day, as bars (green while charging), like the Mac's.</summary>
    static FrameworkElement BatteryChart(List<PowerSettings.UsagePoint> usage, int now)
    {
        var g = new Grid { Height = 120, Margin = new Thickness(12, 12, 12, 10) };
        var canvas = new Canvas();
        g.Children.Add(canvas);
        var since = DateTime.Now.AddHours(-24);
        var points = usage.Where(u => u.Time <= DateTime.Now).OrderBy(u => u.Time).ToList();
        points.Add(new PowerSettings.UsagePoint(DateTime.Now, now, PowerSettings.OnAdapter));
        // the level at a moment: between the two entries around it (none before the report's first)
        (double level, bool ac)? LevelAt(DateTime t)
        {
            int i = points.FindLastIndex(p => p.Time <= t);
            if (i < 0) return null;
            var a = points[i];
            if (i == points.Count - 1) return (a.Percent, a.OnAdapter);
            var b = points[i + 1];
            double span = (b.Time - a.Time).TotalSeconds;
            double k = span <= 0 ? 0 : (t - a.Time).TotalSeconds / span;
            return (a.Percent + (b.Percent - a.Percent) * k, a.OnAdapter);
        }
        g.SizeChanged += (_, e) =>
        {
            canvas.Children.Clear();
            double w = e.NewSize.Width, h = e.NewSize.Height - 16;
            // guides at 0, 50 and 100%
            foreach (double frac in new[] { 0.0, 0.5, 1.0 })
            {
                var line = new Line { X1 = 0, X2 = w, Y1 = h - frac * h, Y2 = h - frac * h, StrokeThickness = 0.5 };
                line.SetResourceReference(Shape.StrokeProperty, "SeparatorBrush");
                canvas.Children.Add(line);
            }
            int bars = 48;
            double bw = w / bars;
            for (int i = 0; i < bars; i++)
            {
                var lv = LevelAt(since.AddMinutes(i * 30 + 30));
                if (lv == null) continue;
                var (level, ac) = lv.Value;
                double bh = Math.Max(1, level / 100.0 * h);
                var r = new Rectangle { Width = Math.Max(1, bw - 2), Height = bh, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(ac ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0x0A, 0x84, 0xFF)) };
                Canvas.SetLeft(r, i * bw + 1);
                Canvas.SetTop(r, h - bh);
                canvas.Children.Add(r);
            }
            for (int k = 0; k <= 4; k++)
            {
                var t = Txt(since.AddHours(k * 6).ToString("h tt").ToLowerInvariant(), 10, brush: "SecondaryLabelBrush");
                Canvas.SetLeft(t, Math.Min(w - 30, k * w / 4));
                Canvas.SetTop(t, h + 2);
                canvas.Children.Add(t);
            }
        };
        return g;
    }

    void BuildBatteryPage(string sub)
    {
        Title_("Options");
        var rows = new List<UIElement>();
        if (PowerSettings.Get(PowerSettings.SleepAfter, true) is uint acSleep)
            rows.Add(Row("Prevent automatic sleeping on power adapter when the display is off", Switch(acSleep == 0, v => PowerSettings.Set(PowerSettings.SleepAfter, true, v ? 0u : 1800u))));
        if (PowerSettings.HasBattery && PowerSettings.Get(PowerSettings.SleepAfter, false) is uint dcSleep)
            rows.Add(Row("Put the computer to sleep on battery after", PopUp(new[] { ("Never", 0u), ("5 minutes", 300u), ("10 minutes", 600u), ("15 minutes", 900u), ("30 minutes", 1800u), ("1 hour", 3600u) }, dcSleep, v => PowerSettings.Set(PowerSettings.SleepAfter, false, v))));
        if (PowerSettings.Get(VideoPlayback, false) is uint video && PowerSettings.HasBattery)
            rows.Add(Row("Optimize video streaming while on battery", Switch(video == 0, v => PowerSettings.Set(VideoPlayback, false, v ? 0u : 1u)), "Plays video at a lower quality to save energy"));
        if (PowerSettings.HasBattery && PowerSettings.Get(PowerSettings.LidAction, false) is uint lid)
        {
            (string, uint)[] lidOpts = { ("Do nothing", 0u), ("Sleep", 1u), ("Hibernate", 2u), ("Shut down", 3u) };
            rows.Add(Row("When the lid is closed on battery", PopUp(lidOpts, lid, v => PowerSettings.Set(PowerSettings.LidAction, false, v))));
            if (PowerSettings.Get(PowerSettings.LidAction, true) is uint lidAc)
                rows.Add(Row("When the lid is closed on power adapter", PopUp(lidOpts, lidAc, v => PowerSettings.Set(PowerSettings.LidAction, true, v))));
        }
        if (PowerSettings.Get(PowerSettings.UsbSuspend, true) is uint usb)
            rows.Add(Row("Let USB devices sleep when they’re not used", Switch(usb != 0, v => { PowerSettings.Set(PowerSettings.UsbSuspend, true, v ? 1u : 0); PowerSettings.Set(PowerSettings.UsbSuspend, false, v ? 1u : 0); })));
        Group(rows);
        Group(Row("Power plans and advanced options", WinLink("Power Options", "ms-settings:powersleep")));
    }

    static readonly PowerSettings.Setting VideoPlayback = new("9596fb26-9850-41fd-ac3e-f7c3c00afd4b", "10778347-1370-4ee0-8bbd-33bdacaade49");

    // ================================================================== Screen Saver

    void BuildScreenSaver()
    {
        string cur = SysParams.ScreenSaverPath;
        bool active = SysParams.ScreenSaverActive && cur != null;
        var savers = new List<(string path, string name)> { (null, "None") };
        foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.scr"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    try { if (FileVersionInfo.GetVersionInfo(f).FileDescription is { Length: > 0 } d) n = d.Replace(" Screen Saver", "").Replace(" screen saver", "").Trim(); } catch { }
                    if (savers.All(s => !string.Equals(s.name, n, StringComparison.OrdinalIgnoreCase))) savers.Add((f, n));
                }
            }
            catch { }
        }
        var grid = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        foreach (var (path, name) in savers)
        {
            bool sel = path == null ? !active : active && string.Equals(path, cur, StringComparison.OrdinalIgnoreCase);
            var cell = new StackPanel { Margin = new Thickness(6), Width = 124, Cursor = System.Windows.Input.Cursors.Hand };
            var pic = new Border { Height = 76, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(sel ? 3 : 0.5), ClipToBounds = true };
            if (sel) pic.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else pic.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            pic.Background = path == null ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)) : new LinearGradientBrush(SaverColor(name, 0), SaverColor(name, 1), 45);
            if (path != null)
            {
                var icon = new Image { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ShellIcons.Load(path, 64, false, b => { if (b != null) icon.Source = b; });
                pic.Child = icon;
            }
            cell.Children.Add(pic);
            var t = Txt(name, 11, sel ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.TextAlignment = TextAlignment.Center;
            t.Margin = new Thickness(0, 5, 0, 0);
            cell.Children.Add(t);
            string p2 = path;
            cell.MouseLeftButtonUp += (_, _) =>
            {
                SysParams.ScreenSaverPath = p2;
                if (p2 != null && SysParams.ScreenSaverTimeout < 60) SysParams.ScreenSaverTimeout = 600;
                Rebuild();
            };
            grid.Children.Add(cell);
        }
        _content.Children.Add(grid);
        var buttons = new List<FrameworkElement>();
        if (active && cur != null)
        {
            buttons.Add(Btn("Options…", () => { try { Process.Start(new ProcessStartInfo(cur, "/c") { UseShellExecute = false }); } catch { } }));
            buttons.Add(Btn("Preview", () => { try { Process.Start(new ProcessStartInfo(cur, "/s") { UseShellExecute = false }); } catch { } }));
        }
        if (buttons.Count > 0) _content.Children.Add(ButtonRow(buttons.ToArray()));
        Group(NavRow(Tile("lock", DarkTile.top, DarkTile.bottom), "Start Screen Saver when inactive", "lockscreen", active ? $"After {SysParams.ScreenSaverTimeout / 60} min" : "Never"));
    }

    static Color SaverColor(string name, int which)
    {
        int h = Math.Abs(name.GetHashCode()) % 6;
        string[][] pairs = { new[] { "#2C3E80", "#5A2C80" }, new[] { "#0F5132", "#2D7D9A" }, new[] { "#7A2E1F", "#C2410C" }, new[] { "#1E3A8A", "#0EA5E9" }, new[] { "#4C1D95", "#DB2777" }, new[] { "#134E4A", "#65A30D" } };
        return Theme.C(pairs[h][which]);
    }

    // ================================================================== Mouse

    static readonly int[] WheelSteps = { 1, 2, 3, 4, 5, 7, 9, 12 };

    void BuildMouse()
    {
        int speedStep = (int)Math.Round((SysParams.MouseSpeed - 1) / 19.0 * 9);
        int dblStep = Math.Clamp((900 - SysParams.DoubleClickMs) / 100, 0, 7);
        int wheel = SysParams.WheelLines;
        int wheelStep = Array.FindIndex(WheelSteps, w => w >= wheel);
        if (wheelStep < 0) wheelStep = WheelSteps.Length - 1;
        Group(
            Row("Tracking speed", TickSlider(10, speedStep, "Slow", "Fast", v => SysParams.MouseSpeed = 1 + (int)Math.Round(v * 19 / 9.0))),
            Row("Pointer acceleration", Switch(SysParams.PointerAcceleration, v => SysParams.PointerAcceleration = v), "The pointer moves farther when the mouse moves faster"));
        Group(
            Row("Secondary click", PopUp(new[] { "Click Right Side", "Click Left Side" }, SysParams.SwapButtons ? 1 : 0, i => SysParams.SwapButtons = i == 1)),
            Row("Double-click speed", TickSlider(8, dblStep, "Slow", "Fast", v => SysParams.DoubleClickMs = 900 - v * 100)),
            Row("Scrolling speed", TickSlider(WheelSteps.Length, wheelStep, "Slow", "Fast", v => SysParams.WheelLines = WheelSteps[v])));
        _content.Children.Add(ButtonRow(WinLink("Additional Mouse Settings", "ms-settings:mousetouchpad"), HelpButton("ms-settings:mousetouchpad")));
    }

    // ================================================================== Trackpad

    int _trackpadTab;

    void BuildTrackpad()
    {
        var t = SysParams.GetTouchpad();
        if (t == null || !t.Present) { Group(Row("No trackpad found", null)); return; }
        var tabs = Segmented(new[] { "Point & Click", "Scroll & Zoom" }, _trackpadTab, i => { _trackpadTab = i; Rebuild(); });
        tabs.HorizontalAlignment = HorizontalAlignment.Center;
        tabs.Margin = new Thickness(0, 0, 0, 12);
        _content.Children.Add(tabs);
        void Change(Action<SysParams.Touchpad> set) { var now = SysParams.GetTouchpad(); if (now == null) return; set(now); SysParams.SetTouchpad(now); }
        if (_trackpadTab == 0)
        {
            int secondary = t.TwoFingerTap && t.RightClickZone ? 0 : t.TwoFingerTap ? 1 : t.RightClickZone ? 2 : 3;
            Group(
                Row("Tracking speed", TickSlider(10, (int)Math.Round((t.CursorSpeed - 1) / 19.0 * 9), "Slow", "Fast", v => Change(x => x.CursorSpeed = 1 + (int)Math.Round(v * 19 / 9.0)))),
                Row("Tap sensitivity", TickSlider(5, 4 - t.Sensitivity, "Low", "High", v => Change(x => x.Sensitivity = 4 - v))),
                Row("Tap to click", Switch(t.Tap, v => Change(x => x.Tap = v)), "Tap with one finger"),
                Row("Secondary click", PopUp(new[] { "Click or Tap with Two Fingers, or the Bottom Right Corner", "Click or Tap with Two Fingers", "Click in Bottom Right Corner", "Off" }, secondary, i => Change(x =>
                {
                    x.TwoFingerTap = i is 0 or 1;
                    x.RightClickZone = i is 0 or 2;
                }))),
                Row("Tap and drag", Switch(t.TapAndDrag, v => Change(x => x.TapAndDrag = v)), "Double-tap and hold to drag an item"));
        }
        else
        {
            Group(
                Row("Natural scrolling", Switch(!t.ScrollReversed, v => Change(x => x.ScrollReversed = !v)), "Content tracks finger movement"),
                Row("Scroll with two fingers", Switch(t.Pan, v => Change(x => x.Pan = v))),
                Row("Zoom in or out", Switch(t.Zoom, v => Change(x => x.Zoom = v)), "Pinch with two fingers"));
        }
        Group(Row("Leave the trackpad on when a mouse is connected", Switch(t.LeaveOnWithMouse, v => Change(x => x.LeaveOnWithMouse = v))));
        _content.Children.Add(ButtonRow(WinLink("Gestures", "ms-settings:devices-touchpad"), HelpButton("ms-settings:devices-touchpad")));
    }

    // ================================================================== Printers & Scanners

    record Printer(string Name, bool Default, bool Offline, string Status, string Location, string Driver, string Port, bool Network);

    /// <summary>The printers - null when Windows' printing (the Print Spooler service) isn't running.</summary>
    static List<Printer> PrinterList()
    {
        var list = new List<Printer>();
        if (!SpoolerRunning()) return null;
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
            dynamic svc = locator.ConnectServer(".", @"root\CIMV2");
            foreach (dynamic p in svc.ExecQuery("SELECT Name, Default, WorkOffline, PrinterStatus, Location, DriverName, PortName, Network FROM Win32_Printer"))
            {
                int status = p.PrinterStatus is null ? 2 : (int)p.PrinterStatus;
                bool offline = p.WorkOffline is bool o && o;
                string st = offline ? "Offline" : status switch { 3 => "Idle", 4 => "Printing", 5 => "Warming Up", 6 => "Stopped", 7 => "Offline", _ => "Idle" };
                list.Add(new Printer((string)p.Name, p.Default is bool d && d, offline || status == 7, st, p.Location as string, p.DriverName as string, p.PortName as string, p.Network is bool n && n));
            }
        }
        catch { }
        return list.OrderByDescending(p => p.Default).ThenBy(p => p.Name).ToList();
    }

    static bool SpoolerRunning()
    {
        IntPtr scm = OpenSCManager(null, null, 0x0001 /*CONNECT*/);
        if (scm == IntPtr.Zero) return true;
        try
        {
            IntPtr svc = OpenService(scm, "Spooler", 0x0004 /*QUERY_STATUS*/);
            if (svc == IntPtr.Zero) return false;
            try { var st = new int[7]; return QueryServiceStatus(svc, st) && st[1] == 4 /*RUNNING*/; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string machine, string db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll")] static extern bool QueryServiceStatus(IntPtr svc, [Out] int[] status);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr h);

    void BuildPrinters()
    {
        var holder = new StackPanel();
        _content.Children.Add(holder);
        holder.Children.Add(LoadingRow("Printers…"));
        Run(PrinterList, list =>
        {
            holder.Children.Clear();
            if (list == null)
            {
                holder.Children.Add(GroupBox(new[]
                {
                    IconRow(Tile("printer", GrayTile.top, GrayTile.bottom), "Printing is turned off", Btn("Turn On…", () => RunThenRebuild(() => Elevated.Run("cmd.exe", "/c sc config spooler start= auto && net start spooler")), true),
                        "Windows’ Print Spooler service isn’t running, so no printer can be used. Turning it on asks for an administrator.", 28),
                }));
                holder.Children.Add(ButtonRow(HelpButton("ms-settings:printers")));
                return;
            }
            if (list.Count == 0) holder.Children.Add(GroupBox(new[] { Row("No printers are available.", null) }));
            else
            {
                holder.Children.Add(GroupBox(list.Select(p =>
                {
                    var pp = p;
                    var dot = StatusDot(p.Offline ? Gray : Green, p.Status + (p.Default ? " · Default" : ""));
                    var right = new StackPanel { Orientation = Orientation.Horizontal };
                    right.Children.Add(dot);
                    var opt = Btn("Options…", () => PrinterSheet(pp));
                    opt.Margin = new Thickness(10, 0, 0, 0);
                    right.Children.Add(opt);
                    return (UIElement)IconRow(Tile("printer", GrayTile.top, GrayTile.bottom), p.Name, right, p.Location, 28);
                })));
                bool windowsPicks = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Windows", "LegacyDefaultPrinterMode", 0) is int m && m == 0;
                var names = list.Select(p => p.Name).ToArray();
                int sel = Math.Max(0, list.FindIndex(p => p.Default));
                holder.Children.Add(GroupBox(new[]
                {
                    Row("Default printer", PopUp(names, sel, i =>
                    {
                        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Windows", "LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord);
                        SetDefaultPrinter(names[i]);
                        Rebuild();
                    })),
                    Row("Let Windows choose the default printer", Switch(windowsPicks, v => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Windows", "LegacyDefaultPrinterMode", v ? 0 : 1, RegistryValueKind.DWord)), "The one used last in each place"),
                }));
            }
            holder.Children.Add(ButtonRow(WinLink("Add Printer, Scanner or Fax…", "ms-settings:printers"), HelpButton("ms-settings:printers")));
        });
    }

    void PrinterSheet(Printer p)
    {
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(Tile("printer", GrayTile.top, GrayTile.bottom), p.Name, p.Status));
        sp.Children.Add(SheetGroup(
            InfoRow("Location", string.IsNullOrEmpty(p.Location) ? "—" : p.Location),
            InfoRow("Kind", p.Driver ?? "—"),
            InfoRow("Connection", p.Network ? "Network" : p.Port ?? "—")));
        void PrintUi(string args) { try { Process.Start(new ProcessStartInfo("rundll32.exe", $"printui.dll,PrintUIEntry {args} /n \"{p.Name}\"") { UseShellExecute = false }); } catch { } }
        sp.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0),
            Children = { Btn("Open Print Queue…", () => PrintUi("/o")), new Border { Width = 8 }, Btn("Printer Options…", () => PrintUi("/e")), new Border { Width = 8 }, Btn("Print Test Page", () => PrintUi("/k")) },
        });
        var remove = Btn("Remove Printer…", () =>
        {
            if (ShellHost.Alert($"Remove “{p.Name}”?", "It can be added again later.", "Cancel", "Remove") != "Remove") return;
            CloseSheet();
            RunThenRebuild(() => { using var proc = Process.Start(new ProcessStartInfo("rundll32.exe", $"printui.dll,PrintUIEntry /dl /n \"{p.Name}\" /q") { UseShellExecute = false }); proc?.WaitForExit(20000); });
        });
        Sheet(sp, 480, remove, ("Done", null));
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode)] static extern bool SetDefaultPrinter(string name);

    // ================================================================== Accessibility

    void BuildAccessibility()
    {
        Hero(Tile("accessibility", Blue.top, Blue.bottom), "Accessibility", "Personalize this computer in ways that work for you: how it looks, how it reads things out, and how the keyboard and pointer behave.");
        SectionTitle("Vision");
        Group(
            NavRow(Tile("speaker.wave.2", DarkTile.top, DarkTile.bottom), "VoiceOver", "accessibility/voiceover", Process.GetProcessesByName("Narrator").Length > 0 ? "On" : "Off"),
            NavRow(Tile("plus.magnifyingglass", DarkTile.top, DarkTile.bottom), "Zoom", "accessibility/zoom"),
            NavRow(Tile("display", Blue.top, Blue.bottom), "Display", "accessibility/display"),
            NavRow(Tile("speaker.wave.2", GrayTile.top, GrayTile.bottom), "Spoken Content", "accessibility/speech"));
        SectionTitle("Hearing");
        Group(NavRow(Tile("ear", Blue.top, Blue.bottom), "Captions", "accessibility/hearing"));
        SectionTitle("Motor");
        Group(
            NavRow(Tile("keyboard", GrayTile.top, GrayTile.bottom), "Keyboard", "accessibility/keyboard"),
            NavRow(Tile("computermouse", GrayTile.top, GrayTile.bottom), "Pointer Control", "accessibility/pointer"));
    }

    void BuildAccessibilityPage(string sub)
    {
        var s = Settings.Current;
        switch (sub)
        {
            case "display":
                Title_("Display");
                Group(
                    Row("Increase contrast", Switch(s.IncreaseContrast, v => { Settings.Current.IncreaseContrast = v; Save(); Theme.Apply(); }), "Stronger borders and separators in MacShell’s windows"),
                    Row("Reduce motion", Switch(!SysParams.Animations, v => SysParams.Animations = !v), "Windows open and close without animating"),
                    Row("Reduce transparency", Switch(!SysParams.Transparency, v => SysParams.Transparency = !v), "Window backgrounds become solid"));
                SectionTitle("Pointer");
                Group(
                    Row("Pointer size", TickSlider(8, Math.Clamp((SysParams.PointerSize - 1) / 2, 0, 7), "Normal", "Large", v => SysParams.PointerSize = 1 + v * 2)),
                    Row("Show where the pointer is when Ctrl is pressed", Switch(SysParams.PointerSonar, v => SysParams.PointerSonar = v)));
                Group(Row("Text size and more", WinLink("Windows Settings", "ms-settings:easeofaccess-display")));
                break;
            case "zoom":
                Title_("Zoom");
                bool running = Process.GetProcessesByName("Magnify").Length > 0;
                Group(
                    Row("Zoom (Windows Magnifier)", Switch(running, v =>
                    {
                        try
                        {
                            if (v) Process.Start(new ProcessStartInfo("magnify.exe") { UseShellExecute = true });
                            else foreach (var p in Process.GetProcessesByName("Magnify")) p.CloseMainWindow();
                        }
                        catch { }
                    })),
                    Row("Zoom in / out", Txt("Win + Plus  /  Win + Minus", 12, brush: "SecondaryLabelBrush")),
                    Row("Turn off zoom", Txt("Win + Esc", 12, brush: "SecondaryLabelBrush")));
                Group(Row("Zoom style and more", WinLink("Windows Settings", "ms-settings:easeofaccess-magnifier")));
                break;
            case "voiceover":
                Title_("VoiceOver");
                bool narrator = Process.GetProcessesByName("Narrator").Length > 0;
                Group(
                    IconRow(Tile("speaker.wave.2", DarkTile.top, DarkTile.bottom), "VoiceOver", Switch(narrator, v =>
                    {
                        try
                        {
                            if (v) Process.Start(new ProcessStartInfo("narrator.exe") { UseShellExecute = true });
                            else foreach (var p in Process.GetProcessesByName("Narrator")) p.Kill();
                        }
                        catch { }
                        Run(() => { Thread.Sleep(1200); return 0; }, _ => Rebuild());
                    }), "Reads out what’s on the screen (Windows Narrator)", 32),
                    Row("Turn VoiceOver on or off", Txt("Ctrl + Win + Enter", 12, brush: "SecondaryLabelBrush")));
                Group(Row("Voice and verbosity", WinLink("Windows Settings", "ms-settings:easeofaccess-narrator")));
                break;
            case "speech":
                Title_("Spoken Content");
                BuildSpeech();
                break;
            case "hearing":
                Title_("Captions");
                Group(
                    Row("Live Captions", Btn("Start", () => { try { Process.Start(new ProcessStartInfo("livecaptions.exe") { UseShellExecute = true }); } catch { } }), "Captions for any sound playing, and your microphone (Ctrl + Win + L)"),
                    Row("Caption style", WinLink("Windows Settings", "ms-settings:easeofaccess-closedcaptioning")));
                break;
            case "keyboard":
                Title_("Keyboard");
                Group(
                    Row("Sticky Keys", Switch(SysParams.StickyKeys, v => SysParams.StickyKeys = v), "Press modifier keys (Shift, Ctrl …) one at a time"),
                    Row("Turn Sticky Keys on with Shift pressed five times", Switch(SysParams.StickyKeysShortcut, v => SysParams.StickyKeysShortcut = v)),
                    Row("Slow Keys", Switch(SysParams.SlowKeys, v => SysParams.SlowKeys = v), "Keys need to be held for a moment before they type"),
                    Row("Toggle Keys", Switch(SysParams.ToggleKeys, v => SysParams.ToggleKeys = v), "A tone when Caps Lock, Num Lock or Scroll Lock is pressed"));
                Group(Row("More keyboard options", WinLink("Windows Settings", "ms-settings:easeofaccess-keyboard")));
                break;
            case "pointer":
                Title_("Pointer Control");
                int dblStep = Math.Clamp((900 - SysParams.DoubleClickMs) / 100, 0, 7);
                Group(
                    Row("Double-click speed", TickSlider(8, dblStep, "Slow", "Fast", v => SysParams.DoubleClickMs = 900 - v * 100)),
                    Row("Hide the pointer while typing", Switch(SysParams.HidePointerWhileTyping, v => SysParams.HidePointerWhileTyping = v)),
                    Row("Mouse Keys", Switch(SysParams.MouseKeys, v => SysParams.MouseKeys = v), "Move the pointer with the number pad"));
                break;
            default: BuildAccessibility(); break;
        }
    }

    void BuildSpeech()
    {
        // the voices Windows has (SAPI) and its speaking rate
        var voices = new List<(string id, string name)>();
        string current = null;
        try
        {
            dynamic sp = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
            current = (string)sp.Voice.Id;
            foreach (dynamic v in sp.GetVoices()) voices.Add(((string)v.Id, (string)v.GetDescription()));
        }
        catch { }
        int rate = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Speech\Voices", "DefaultTTSRate", 0) is int r ? r : 0;
        var rows = new List<UIElement>();
        if (voices.Count > 0)
        {
            int sel = Math.Max(0, voices.FindIndex(v => string.Equals(v.id, current, StringComparison.OrdinalIgnoreCase)));
            rows.Add(Row("System voice", PopUp(voices.Select(v => v.name).ToArray(), sel, i => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Speech\Voices", "DefaultTokenId", voices[i].id))));
        }
        rows.Add(Row("Speaking rate", TickSlider(11, (rate + 10) / 2, "Slow", "Fast", v => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Speech\Voices", "DefaultTTSRate", v * 2 - 10, RegistryValueKind.DWord))));
        rows.Add(Row("Hear it", Btn("Play Sample", () => Task.Run(() =>
        {
            try { dynamic sp = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")); sp.Speak("This is what your computer’s voice sounds like."); } catch { }
        }))));
        Group(rows);
        Group(Row("Speak selected text", Txt("Narrator: Win + Ctrl + Enter", 12, brush: "SecondaryLabelBrush")));
    }
}
