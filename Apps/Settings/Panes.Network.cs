using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Native;
using MacShell.Services;
using MacShell.Shell;

namespace MacShell.Apps;

/// <summary>Wi-Fi, Bluetooth and Network: joining and forgetting networks, pairing devices, each connection's
/// addresses, VPN, proxies and the firewall.</summary>
public partial class SettingsWindow
{
    static readonly (string top, string bottom) Blue = ("#3D9BFF", "#0A6CFF");

    // ================================================================== Wi-Fi

    void BuildWifi()
    {
        var adapter = Wlan.Primary;
        if (adapter == null)
        {
            Hero(Tile("wifi", Blue.top, Blue.bottom), "Wi-Fi", "This computer has no Wi-Fi hardware (or it’s turned off in Device Manager).");
            return;
        }
        bool? on = Wlan.RadioOn();
        var sw = Switch(on == true, v => RunThenRebuild(() =>
        {
            Radios.Set(Radios.Kind.WiFi, v);
            if (v) { Thread.Sleep(1500); Wlan.Scan(); Thread.Sleep(2000); } else Thread.Sleep(300);
        }, () => ShellHost.MenuBar?.RefreshNetwork()));
        Group(IconRow(Tile("wifi", Blue.top, Blue.bottom), "Wi-Fi", sw, on == true ? null : "Off", 32));
        if (on != true) return;

        var holder = new StackPanel();
        _content.Children.Add(holder);
        holder.Children.Add(LoadingRow("Looking for networks…"));
        void Load(bool scan) => Run(() =>
        {
            if (scan) { Wlan.Scan(); Thread.Sleep(2500); }
            return (cur: Wlan.Current(), nets: Wlan.Networks());
        }, r => FillWifi(holder, r.cur, r.nets));
        Load(false);
        Load(true);
        Every(TimeSpan.FromSeconds(10), () => Load(false));
        var buttons = ButtonRow(Btn("Advanced…", WifiAdvanced), HelpButton("ms-settings:network-wifi"));
        _content.Children.Add(buttons);
    }

    void BuildWifiPage(string sub) => BuildWifi();

    static FrameworkElement LoadingRow(string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 10) };
        sp.Children.Add(Spinner());
        var t = Secondary(text, 12);
        t.Margin = new Thickness(8, 0, 0, 0);
        sp.Children.Add(t);
        return GroupBox(new[] { sp });
    }

    /// <summary>The round "?" button at the bottom of a pane: the matching Windows help/settings page.</summary>
    static Button HelpButton(string uri)
    {
        var b = new Button { Content = "?", Style = (Style)Application.Current.Resources["MacButton"], Width = 22, Height = 22, MinWidth = 0, Padding = new Thickness(0), ToolTip = "Help" };
        b.Click += (_, _) => OpenUri(uri);
        return b;
    }

    void FillWifi(StackPanel holder, Wlan.Connection cur, List<Wlan.Network> nets)
    {
        holder.Children.Clear();
        var connected = nets.FirstOrDefault(n => n.Connected);
        if (cur != null || connected != null)
        {
            string ssid = cur?.Ssid ?? connected.Ssid;
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var details = Btn("Details…", () => WifiDetails(ssid));
            var row = IconRow(null, ssid, details, null, 26, WifiCircle(cur?.Signal ?? connected?.Signal ?? 100, true));
            ((StackPanel)((Grid)row).Children[0]).Children.Add(StatusDot(Green, "Connected"));
            holder.Children.Add(GroupBox(new[] { row }));
        }
        var known = nets.Where(n => n.Known && !n.Connected).ToList();
        if (known.Count > 0)
        {
            holder.Children.Add(SectionLabel("Known Networks"));
            holder.Children.Add(GroupBox(known.Select(n => NetworkRow(n, true)).ToArray()));
        }
        holder.Children.Add(SectionLabel("Other Networks"));
        var others = nets.Where(n => !n.Known && !n.Connected).Select(n => NetworkRow(n, false)).ToList();
        if (others.Count == 0) others.Add(Row("No other networks found", null));
        others.Add(ButtonRow(Btn("Other…", WifiJoinDialog.ShowHidden)));
        holder.Children.Add(GroupBox(others));
    }

    static TextBlock SectionLabel(string s)
    {
        var t = Txt(s, 13, FontWeights.Bold);
        t.Margin = new Thickness(4, 12, 0, 8);
        return t;
    }

    static FrameworkElement WifiCircle(int signal, bool joined)
    {
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13) };
        if (joined) circle.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        else circle.SetResourceReference(Border.BackgroundProperty, "QuaternaryLabelBrush");
        var glyph = new WifiGlyph { Width = 15, Height = 12, Level = WifiGlyph.LevelFor(signal) };
        if (joined) glyph.Foreground = Brushes.White; else glyph.SetResourceReference(WifiGlyph.ForegroundProperty, "LabelBrush");
        circle.Child = glyph;
        return circle;
    }

    /// <summary>A network around: its signal, a lock if it has a password; Connect (shown with the pointer over it), and
    /// for a known one, its "…" menu (Auto-Join, Forget This Network…).</summary>
    FrameworkElement NetworkRow(Wlan.Network n, bool known)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var connect = Btn("Connect", () => JoinFromSettings(n));
        connect.Visibility = Visibility.Hidden;
        right.Children.Add(connect);
        if (n.Secure)
        {
            var lockIcon = new SymbolIcon { Symbol = "lock", Width = 12, Height = 12, StrokeWidth = 2, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lockIcon.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
            right.Children.Add(lockIcon);
        }
        if (known)
        {
            var more = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = "⋯", Width = 26, Height = 22, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0), ToolTip = "Options" };
            more.Click += (_, _) =>
            {
                var auto = Wlan.Known().FirstOrDefault(k => k.Name == n.Profile)?.AutoJoin ?? true;
                var cm = Mb.Context(
                    Mb.Item("Auto-Join", () => RunThenRebuild(() => Wlan.SetAutoJoin(n.Profile, !auto)), isChecked: auto),
                    Mb.Sep(),
                    Mb.Item("Forget This Network…", () => ForgetNetwork(n.Profile)));
                cm.PlacementTarget = more;
                cm.IsOpen = true;
            };
            right.Children.Add(more);
        }
        var row = IconRow(null, n.Ssid, right, null, 26, WifiCircle(n.Signal, false));
        row.ToolTip = $"{n.Security}{(n.Band != null ? " · " + n.Band : "")} · signal {n.Signal}%";
        var wrap = new Border { Child = row, Background = Brushes.Transparent };
        wrap.MouseEnter += (_, _) => connect.Visibility = Visibility.Visible;
        wrap.MouseLeave += (_, _) => connect.Visibility = Visibility.Hidden;
        return wrap;
    }

    void JoinFromSettings(Wlan.Network n)
    {
        if (n.Known && !string.IsNullOrEmpty(n.Profile))
            RunThenRebuild(() =>
            {
                string err = Wlan.Join(n.Ssid, n.Auth, n.Cipher, null, true, false, n.Profile);
                if (err != null) Dispatcher.BeginInvoke(() => ShellHost.ShowAlert($"The Wi-Fi network “{n.Ssid}” couldn’t be joined.", err));
            }, () => ShellHost.MenuBar?.RefreshNetwork());
        else WifiJoinDialog.Join(n);
    }

    void ForgetNetwork(string profile)
    {
        if (ShellHost.Alert($"Forget Wi-Fi network “{profile}”?", "This computer will no longer join this Wi-Fi network, and its password will be removed.", "Cancel", "Forget") != "Forget") return;
        RunThenRebuild(() => Wlan.Forget(profile), () => ShellHost.MenuBar?.RefreshNetwork());
    }

    /// <summary>The joined network: auto-join, its addresses (TCP/IP, DNS) and the hardware - Forget This Network….</summary>
    void WifiDetails(string ssid)
    {
        var cur = Wlan.Current();
        var ni = WifiInterface();
        var kn = Wlan.Known().FirstOrDefault(k => k.Name == (cur?.Profile ?? ssid));
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(Tile("wifi", Blue.top, Blue.bottom), ssid, "Connected"));
        if (kn != null)
            sp.Children.Add(SheetGroup(Row("Automatically join this network", Switch(kn.AutoJoin, v => Run(() => Wlan.SetAutoJoin(kn.Name, v), _ => { })))));
        sp.Children.Add(SheetLabel("TCP/IP"));
        sp.Children.Add(SheetGroup(AddressRows(ni).ToArray()));
        sp.Children.Add(SheetLabel("DNS"));
        sp.Children.Add(SheetGroup(DnsRows(ni).ToArray()));
        sp.Children.Add(SheetLabel("Hardware"));
        var hw = new List<UIElement>();
        if (ni != null) hw.Add(InfoRow("MAC address", MacText(ni)));
        if (cur != null)
        {
            hw.Add(InfoRow("Standard", cur.Phy));
            if (cur.Band != null) hw.Add(InfoRow("Channel", $"{cur.Channel} ({cur.Band})"));
            hw.Add(InfoRow("Security", cur.Security));
            hw.Add(InfoRow("Signal", $"{cur.Signal}%"));
            hw.Add(InfoRow("Transmit rate", $"{cur.TxMbps:0} Mbit/s"));
        }
        sp.Children.Add(SheetGroup(hw.ToArray()));
        var forget = Btn("Forget This Network…", () => { CloseSheet(); ForgetNetwork(cur?.Profile ?? ssid); });
        Sheet(sp, 480, forget, ("OK", null));
    }

    static TextBlock SheetLabel(string s)
    {
        var t = Txt(s, 12, FontWeights.Bold);
        t.Margin = new Thickness(4, 10, 0, 6);
        return t;
    }

    /// <summary>Advanced…: every network this computer knows (Auto-Join, Remove), and its Wi-Fi address.</summary>
    void WifiAdvanced()
    {
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(Tile("wifi", Blue.top, Blue.bottom), "Known Networks", "Networks this computer has joined. They’re joined again automatically when Auto-Join is on."));
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            var known = Wlan.Known();
            if (known.Count == 0) { list.Children.Add(SheetGroup(Row("No known networks", null))); return; }
            list.Children.Add(SheetGroup(known.Select(k =>
            {
                var auto = CheckBox("Auto-Join", k.AutoJoin, v => Wlan.SetAutoJoin(k.Name, v));
                var remove = Btn("Remove", () =>
                {
                    if (ShellHost.Alert($"Forget Wi-Fi network “{k.Name}”?", "This computer will no longer join this Wi-Fi network.", "Cancel", "Forget") != "Forget") return;
                    Wlan.Forget(k.Name);
                    Fill();
                });
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                right.Children.Add(auto);
                remove.Margin = new Thickness(12, 0, 0, 0);
                right.Children.Add(remove);
                return (UIElement)Row(k.Name, right);
            }).ToArray()));
        }
        Fill();
        sp.Children.Add(list);
        var ni = WifiInterface();
        if (ni != null) sp.Children.Add(SheetGroup(InfoRow("Wi-Fi address", MacText(ni))));
        Sheet(sp, 500, ("Done", null));
    }

    static NetworkInterface WifiInterface()
    {
        var a = Wlan.Primary;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && (a == null || n.Description == a.Description))
                ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch { return null; }
    }

    static string MacText(NetworkInterface ni)
    {
        try { return string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("x2"))); }
        catch { return "—"; }
    }

    static IEnumerable<UIElement> AddressRows(NetworkInterface ni)
    {
        if (ni == null) { yield return InfoRow("IP address", "—"); yield break; }
        IPInterfaceProperties ip = null;
        try { ip = ni.GetIPProperties(); } catch { }
        bool dhcp = false;
        try { dhcp = ip?.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { }
        yield return InfoRow("Configure IPv4", dhcp ? "Using DHCP" : "Manually");
        var v4 = ip?.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
        yield return InfoRow("IP address", v4?.Address.ToString() ?? "—");
        yield return InfoRow("Subnet mask", v4?.IPv4Mask?.ToString() ?? "—");
        var gw = ip?.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
        yield return InfoRow("Router", gw?.Address.ToString() ?? "—");
        var v6 = ip?.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal);
        if (v6 != null) yield return InfoRow("IPv6 address", v6.Address.ToString());
    }

    IEnumerable<UIElement> DnsRows(NetworkInterface ni)
    {
        IPInterfaceProperties ip = null;
        try { ip = ni?.GetIPProperties(); } catch { }
        var dns = ip?.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork || !a.IsIPv6SiteLocal).Select(a => a.ToString()).ToList() ?? new List<string>();
        yield return InfoRow("DNS servers", dns.Count > 0 ? string.Join("\n", dns) : "—");
        if (ni != null)
        {
            string name = ni.Name;
            yield return Row("Use other DNS servers", Btn("Edit…", () => EditDns(name, dns)), "Windows asks for an administrator to change them");
        }
    }

    /// <summary>The DNS servers of a connection, set (Windows asks for an administrator): typed, one a line; empty: automatic.</summary>
    void EditDns(string iface, List<string> current)
    {
        var box = new TextBox { Style = (Style)Application.Current.Resources["MacTextField"], AcceptsReturn = true, Height = 90, VerticalContentAlignment = VerticalAlignment.Top, Text = string.Join("\n", current), TextWrapping = TextWrapping.NoWrap };
        var sp = new StackPanel();
        sp.Children.Add(SheetHeader(Tile("network", Blue.top, Blue.bottom), "DNS Servers", $"For “{iface}”. One a line - leave it empty to get them automatically."));
        sp.Children.Add(box);
        Sheet(sp, 420, ("Cancel", null), ("OK", () =>
        {
            var servers = box.Text.Split(new[] { '\r', '\n', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (servers.Any(s => !IPAddress.TryParse(s, out _))) { ShellHost.ShowAlert("That isn’t a DNS server address.", string.Join(", ", servers.Where(s => !IPAddress.TryParse(s, out _)))); return false; }
            var v4 = servers.Where(s => IPAddress.Parse(s).AddressFamily == AddressFamily.InterNetwork).ToList();
            string cmd = v4.Count == 0
                ? $"interface ipv4 set dnsservers name=\"{iface}\" source=dhcp"
                : $"interface ipv4 set dnsservers name=\"{iface}\" source=static address={v4[0]} register=primary validate=no" +
                  string.Concat(v4.Skip(1).Select((s, i) => $" & netsh interface ipv4 add dnsservers name=\"{iface}\" address={s} index={i + 2} validate=no"));
            RunThenRebuild(() => Elevated.Run("cmd.exe", $"/c netsh {cmd}"));
            return true;
        }));
    }

    // ================================================================== Bluetooth

    void BuildBluetooth()
    {
        var holder = new StackPanel();
        _content.Children.Add(holder);
        holder.Children.Add(LoadingRow("Bluetooth…"));
        Run(() => (state: Radios.Get(Radios.Kind.Bluetooth), paired: Bluetooth.Find(true, 6000)), r => FillBluetooth(holder, r.state, r.paired));
    }

    void FillBluetooth(StackPanel holder, Radios.State? state, List<Bluetooth.Device> paired)
    {
        holder.Children.Clear();
        if (state == null)
        {
            holder.Children.Add(GroupBox(new[] { IconRow(Tile("bluetooth", Blue.top, Blue.bottom), "Bluetooth", null, "This computer has no Bluetooth (or it’s turned off in Device Manager).", 32) }));
            return;
        }
        bool on = state == Radios.State.On;
        var sw = Switch(on, v => RunThenRebuild(() => { Radios.Set(Radios.Kind.Bluetooth, v); Thread.Sleep(800); }));
        holder.Children.Add(GroupBox(new[] { IconRow(Tile("bluetooth", Blue.top, Blue.bottom), "Bluetooth", sw,
            on ? $"This computer is discoverable as “{Environment.MachineName}” while Bluetooth Settings is open." : "Off", 32) }));
        if (!on) return;
        holder.Children.Add(SectionLabel("My Devices"));
        if (paired.Count == 0) holder.Children.Add(GroupBox(new[] { Row("No devices", null, "Devices you pair appear here") }));
        else holder.Children.Add(GroupBox(paired.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).Select(d => DeviceRow(d, true)).ToArray()));

        var nearbyHead = new Grid();
        nearbyHead.Children.Add(SectionLabel("Nearby Devices"));
        var spin = Spinner();
        spin.HorizontalAlignment = HorizontalAlignment.Right;
        spin.VerticalAlignment = VerticalAlignment.Center;
        spin.Margin = new Thickness(0, 6, 8, 0);
        nearbyHead.Children.Add(spin);
        holder.Children.Add(nearbyHead);
        var nearby = new StackPanel();
        holder.Children.Add(nearby);
        void Search() => Run(() => Bluetooth.Find(false, 9000), list =>
        {
            nearby.Children.Clear();
            nearby.Children.Add(GroupBox(list.Count == 0 ? new UIElement[] { Row("Looking for devices…", null, "Put the device in pairing mode") } : list.OrderBy(d => d.Name).Select(d => DeviceRow(d, false)).ToArray()));
        });
        nearby.Children.Add(GroupBox(new[] { Row("Looking for devices…", null, "Put the device in pairing mode") }));
        Search();
        Every(TimeSpan.FromSeconds(12), Search);
        // (connections come and go: the paired list is read again now and then)
        Every(TimeSpan.FromSeconds(6), () => Run(() => Bluetooth.Find(true, 6000), list =>
        {
            if (list.Select(d => d.Name + d.Connected).SequenceEqual(paired.Select(d => d.Name + d.Connected))) return;
            Rebuild();
        }));
        _content.Children.Add(ButtonRow(Btn("Bluetooth Devices in Windows", () => OpenUri("ms-settings:bluetooth")), HelpButton("ms-settings:bluetooth")));
    }

    FrameworkElement DeviceRow(Bluetooth.Device d, bool paired)
    {
        var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14) };
        if (paired && d.Connected) circle.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        else circle.SetResourceReference(Border.BackgroundProperty, "QuaternaryLabelBrush");
        var icon = new SymbolIcon { Symbol = d.Kind, Width = 15, Height = 15, StrokeWidth = 1.9 };
        if (paired && d.Connected) icon.Foreground = Brushes.White; else icon.SetResourceReference(SymbolIcon.ForegroundProperty, "LabelBrush");
        circle.Child = icon;
        UIElement right;
        if (paired)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var st = Secondary(d.Connected ? "Connected" : "Not Connected", 12);
            st.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(st);
            var info = new Button { Style = (Style)Application.Current.Resources["MacButton"], Content = "ⓘ", Width = 24, Height = 22, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0), ToolTip = "Options" };
            info.Click += (_, _) => DeviceSheet(d);
            sp.Children.Add(info);
            right = sp;
        }
        else right = Btn("Connect", () => PairDevice(d));
        return IconRow(null, d.Name, right, null, 28, circle);
    }

    void PairDevice(Bluetooth.Device d) => RunThenRebuild(() =>
    {
        int status = Bluetooth.Pair(d.Id);
        if (status is not (0 or 3)) Dispatcher.BeginInvoke(() => ShellHost.ShowAlert($"“{d.Name}” couldn’t be connected.", Bluetooth.PairingError(status)));
    });

    void DeviceSheet(Bluetooth.Device d)
    {
        var sp = new StackPanel();
        var circle = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 12, 0) };
        circle.SetResourceReference(Border.BackgroundProperty, "QuaternaryLabelBrush");
        circle.Child = new SymbolIcon { Symbol = d.Kind, Width = 22, Height = 22, StrokeWidth = 1.8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(circle);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Txt(d.Name, 15, FontWeights.Bold));
        names.Children.Add(Txt(d.Connected ? "Connected" : "Not Connected", 12, brush: "SecondaryLabelBrush"));
        head.Children.Add(names);
        sp.Children.Add(head);
        var rows = new List<UIElement>
        {
            InfoRow("Address", FormatAddress(d.Address)),
            InfoRow("Type", d.LowEnergy ? "Bluetooth Low Energy" : "Bluetooth"),
        };
        if (d.Signal is int s) rows.Add(InfoRow("Signal", $"{s} dBm"));
        rows.Add(Row("Connection and sound options", WinLink("Windows Bluetooth Settings", "ms-settings:bluetooth")));
        sp.Children.Add(SheetGroup(rows.ToArray()));
        var forget = Btn("Forget This Device…", () =>
        {
            if (ShellHost.Alert($"Forget “{d.Name}”?", "It will have to be paired again to be used with this computer.", "Cancel", "Forget Device") != "Forget Device") return;
            CloseSheet();
            RunThenRebuild(() => Bluetooth.Unpair(d.Id));
        });
        Sheet(sp, 440, forget, ("Done", null));
    }

    static string FormatAddress(string a)
    {
        if (string.IsNullOrEmpty(a)) return "—";
        string hex = new string(a.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToUpperInvariant() : a;
    }

    // ================================================================== Network

    void BuildNetwork()
    {
        var holder = new StackPanel();
        _content.Children.Add(holder);
        holder.Children.Add(LoadingRow("Network…"));
        Run(() => (ifaces: Interfaces(), vpns: Vpn.List(), fw: Firewall.State()), r =>
        {
            holder.Children.Clear();
            var rows = new List<UIElement>();
            foreach (var ni in r.ifaces)
            {
                bool up = ni.OperationalStatus == OperationalStatus.Up;
                string sym = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "network";
                string value = up ? "Connected" : "Not Connected";
                rows.Add(ServiceRow(sym, ServiceName(ni), "network/if:" + ni.Id, up, value));
            }
            foreach (var v in r.vpns)
                rows.Add(ServiceRow("lock.shield", v.Name, "network/vpn:" + v.Name, v.Connected, v.Connected ? "Connected" : "Not Connected"));
            holder.Children.Add(GroupBox(rows));
            holder.Children.Add(GroupBox(new[] { NavRow(Tile("lock.shield", "#8E8E93", "#636366"), "Firewall", "network/firewall", r.fw.AnyOn ? "Active" : "Inactive") }));
            holder.Children.Add(GroupBox(new[] { NavRow(Tile("globe", Blue.top, Blue.bottom), "Proxies", "network/proxies", Proxy.Read().Summary) }));
            holder.Children.Add(ButtonRow(Btn("Add VPN…", () => OpenUri("ms-settings:network-vpn")), HelpButton("ms-settings:network-status")));
        });
    }

    /// <summary>A connection (Wi-Fi, Ethernet, a VPN): its icon, a green or gray dot for whether it's connected, and its page.</summary>
    FrameworkElement ServiceRow(string sym, string name, string target, bool up, string value)
    {
        var row = NavRow(Tile(sym, Blue.top, Blue.bottom), name, target, null);
        if (row is Border b && b.Child is Grid g && g.Children.Count > 1 && g.Children[^2] is StackPanel right)
            right.Children.Insert(0, StatusDot(up ? Green : Gray, value));
        return row;
    }

    /// <summary>The connections worth listing: Wi-Fi and Ethernet adapters (not the loopback, tunnels or virtual switches).</summary>
    static List<NetworkInterface> Interfaces()
    {
        try
        {
            // (Wi-Fi adapters left behind by old drivers stay listed, "down": only the ones the Wi-Fi service has are real)
            var present = new HashSet<string>(Wlan.Adapters().Select(a => a.Description));
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Ppp)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || present.Contains(n.Description))
                .Where(n => !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up).ThenBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .ToList();
        }
        catch { return new List<NetworkInterface>(); }
    }

    static string ServiceName(NetworkInterface ni) => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : ni.Name.StartsWith("Ethernet", StringComparison.OrdinalIgnoreCase) ? ni.Name : $"{ni.Name}";

    void BuildNetworkPage(string sub)
    {
        if (sub == "firewall") { BuildFirewall(); return; }
        if (sub == "proxies") { BuildProxies(); return; }
        if (sub.StartsWith("vpn:")) { BuildVpn(sub[4..]); return; }
        if (sub.StartsWith("if:"))
        {
            var ni = Interfaces().FirstOrDefault(n => n.Id == sub[3..]);
            if (ni == null) { Group(Row("This connection isn’t there anymore", null)); return; }
            Title_(ServiceName(ni));
            bool up = ni.OperationalStatus == OperationalStatus.Up;
            string sym = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "network";
            var head = IconRow(Tile(sym, Blue.top, Blue.bottom), ServiceName(ni), null, null, 32);
            ((StackPanel)((Grid)head).Children[0]).Children.Add(StatusDot(up ? Green : Gray, up ? "Connected" : "Not Connected"));
            Group(head);
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                Group(Row("Wi-Fi networks, joining and known networks", Btn("Wi-Fi Settings…", () => Open("wifi"))));
            SectionTitle("TCP/IP");
            Group(AddressRows(ni));
            SectionTitle("DNS");
            Group(DnsRows(ni));
            SectionTitle("Hardware");
            Group(InfoRow("MAC address", MacText(ni)), InfoRow("Adapter", ni.Description), InfoRow("Speed", ni.Speed > 0 ? FormatSpeed(ni.Speed) : "—"));
            string metered = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "ms-settings:network-wifi" : "ms-settings:network-ethernet";
            Group(Row("Metered connection, IP assignment and more", WinLink("Windows Settings", metered)));
        }
    }

    static string FormatSpeed(long bps) => bps >= 1_000_000_000 ? $"{bps / 1_000_000_000.0:0.#} Gbit/s" : $"{bps / 1_000_000.0:0} Mbit/s";

    void BuildFirewall()
    {
        Title_("Firewall");
        var st = Firewall.State();
        var head = IconRow(Tile("lock.shield", "#8E8E93", "#636366"), "Firewall", null, st.AnyOn
            ? "The firewall is on: apps and services can’t be reached from the network unless allowed."
            : "The firewall is off: anything on the network can try to reach this computer.", 32);
        Group(head);
        Group(
            Row("Private networks (home, work)", StatusDot(st.Private ? Green : Red, st.Private ? "On" : "Off")),
            Row("Public networks", StatusDot(st.Public ? Green : Red, st.Public ? "On" : "Off")),
            Row("Domain networks", StatusDot(st.Domain ? Green : Gray, st.Domain ? "On" : "Off")));
        Footnote("Turning the firewall on or off, and which apps get through, belong to Windows Security - it asks for an administrator.");
        Group(
            Row("Turn the firewall on or off", WinLink("Windows Security", "windowsdefender://network/")),
            Row("Apps allowed through the firewall", Btn("Options…", () => { try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps") { UseShellExecute = true }); } catch { } })));
    }

    void BuildProxies()
    {
        Title_("Proxies");
        var p = Proxy.Read();
        var auto = Field(p.AutoConfigUrl, 260, "http://…/proxy.pac");
        var server = Field(p.Server, 200, "proxy.example.com");
        var port = Field(p.Port, 60, "8080");
        var bypass = new TextBox { Style = (Style)Application.Current.Resources["MacTextField"], Text = p.Bypass, Height = 56, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top };
        var useAuto = Switch(p.AutoConfig, _ => { });
        var useWeb = Switch(p.Enabled, _ => { });
        var srv = new StackPanel { Orientation = Orientation.Horizontal };
        srv.Children.Add(server);
        var colon = Secondary(":");
        colon.Margin = new Thickness(4, 0, 4, 0);
        colon.VerticalAlignment = VerticalAlignment.Center;
        srv.Children.Add(colon);
        srv.Children.Add(port);
        Group(
            Row("Automatic proxy configuration", useAuto),
            Row("URL", auto),
            Row("Web and secure web proxy (HTTP, HTTPS)", useWeb),
            Row("Server", srv));
        SectionTitle("Bypass proxy settings for these hosts and domains");
        Group(new Border { Child = bypass, Margin = new Thickness(12, 8, 12, 8) });
        var useLocal = Switch(p.BypassLocal, _ => { });
        Group(Row("Don’t use the proxy for local addresses", useLocal));
        // (all of it is read and set when Apply is pressed, as on a Mac)
        var apply = Btn("Apply", () =>
        {
            var np = new Proxy.Settings
            {
                AutoConfig = useAuto.IsChecked == true, AutoConfigUrl = auto.Text.Trim(),
                Enabled = useWeb.IsChecked == true, Server = server.Text.Trim(), Port = port.Text.Trim(),
                Bypass = bypass.Text.Trim(), BypassLocal = useLocal.IsChecked == true,
            };
            if (np.Enabled && np.Server.Length == 0) { ShellHost.ShowAlert("Type the proxy server’s address.", "Or turn the web proxy off."); return; }
            if (np.AutoConfig && np.AutoConfigUrl.Length == 0) { ShellHost.ShowAlert("Type the address of the proxy configuration file.", "Or turn automatic proxy configuration off."); return; }
            Proxy.Write(np);
            Rebuild();
        }, true);
        _content.Children.Add(ButtonRow(apply));
    }

    void BuildVpn(string name)
    {
        Title_(name);
        var v = Vpn.List().FirstOrDefault(x => x.Name == name);
        if (v == null) { Group(Row("This VPN isn’t there anymore", null)); return; }
        var head = IconRow(Tile("lock.shield", Blue.top, Blue.bottom), name, null, null, 32);
        ((StackPanel)((Grid)head).Children[0]).Children.Add(StatusDot(v.Connected ? Green : Gray, v.Connected ? "Connected" : "Not Connected"));
        Group(head);
        Group(
            Row(v.Connected ? "Disconnect from this VPN" : "Connect to this VPN", Btn(v.Connected ? "Disconnect" : "Connect", () => RunThenRebuild(() => { if (v.Connected) Vpn.Disconnect(name); else Vpn.Connect(name); }), true)),
            InfoRow("Server", v.Server ?? "—"));
        Group(Row("Sign-in info, type and more", WinLink("Windows VPN Settings", "ms-settings:network-vpn")));
    }
}

/// <summary>A command that needs an administrator: Windows asks (the UAC prompt) and runs it hidden. False if it was refused.</summary>
public static class Elevated
{
    public static bool Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
            p?.WaitForExit(30000);
            return p != null && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }   // (cancelled)
        catch (Exception ex) { App.Log($"elevated {exe}: {ex.Message}"); return false; }
    }
}

/// <summary>Windows Firewall's state for each kind of network (read through HNetCfg.FwPolicy2: no administrator needed).</summary>
public static class Firewall
{
    public record Status(bool Domain, bool Private, bool Public) { public bool AnyOn => Domain || Private || Public; }

    public static Status State()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
            bool On(int profile) { try { return policy.FirewallEnabled[profile]; } catch { return false; } }
            return new Status(On(1), On(2), On(4));
        }
        catch { return new Status(false, false, false); }
    }
}

/// <summary>VPN connections (Windows' phone books) - listed, connected and disconnected with rasdial / rasphone.</summary>
public static class Vpn
{
    public record Entry(string Name, string Server, bool Connected);

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        var connected = ConnectedNames();
        foreach (var pbk in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Network\Connections\Pbk\rasphone.pbk"),
        })
        {
            try
            {
                if (!File.Exists(pbk)) continue;
                string name = null, server = null;
                foreach (var raw in File.ReadAllLines(pbk))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        if (name != null && list.All(e => e.Name != name)) list.Add(new Entry(name, server, connected.Contains(name)));
                        name = line[1..^1]; server = null;
                    }
                    else if (line.StartsWith("PhoneNumber=")) server = line[12..];
                }
                if (name != null && list.All(e => e.Name != name)) list.Add(new Entry(name, server, connected.Contains(name)));
            }
            catch { }
        }
        return list;
    }

    static HashSet<string> ConnectedNames()
    {
        var set = new HashSet<string>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("rasdial.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var lines = o.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            // "Connected to" / the names / "Command completed successfully."
            for (int i = 1; i < lines.Count - 1; i++) set.Add(lines[i]);
        }
        catch { }
        return set;
    }

    /// <summary>Connects with the saved sign-in (rasdial); without one, Windows' own sign-in window asks (rasphone).</summary>
    public static void Connect(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("rasdial.exe", $"\"{name}\"") { UseShellExecute = false, CreateNoWindow = true });
            p.WaitForExit(30000);
            if (p.ExitCode != 0) Process.Start(new ProcessStartInfo("rasphone.exe", $"-d \"{name}\"") { UseShellExecute = false });
        }
        catch { }
    }

    public static void Disconnect(string name)
    {
        try { using var p = Process.Start(new ProcessStartInfo("rasdial.exe", $"\"{name}\" /disconnect") { UseShellExecute = false, CreateNoWindow = true }); p.WaitForExit(15000); } catch { }
    }
}

/// <summary>The proxy for this user's apps (WinINet: browsers and most apps follow it).</summary>
public static class Proxy
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public class Settings
    {
        public bool AutoConfig, Enabled, BypassLocal;
        public string AutoConfigUrl = "", Server = "", Port = "", Bypass = "";
        public string Summary => AutoConfig ? "Automatic" : Enabled ? $"{Server}{(Port.Length > 0 ? ":" + Port : "")}" : "Off";
    }

    public static Settings Read()
    {
        var s = new Settings();
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            s.Enabled = (k?.GetValue("ProxyEnable") as int?) == 1;
            string server = k?.GetValue("ProxyServer") as string ?? "";
            // "host:port" or "http=host:port;https=…" (the first one is shown)
            string first = server.Split(';').FirstOrDefault() ?? "";
            if (first.Contains('=')) first = first[(first.IndexOf('=') + 1)..];
            int colon = first.LastIndexOf(':');
            s.Server = colon > 0 ? first[..colon] : first;
            s.Port = colon > 0 ? first[(colon + 1)..] : "";
            s.AutoConfigUrl = k?.GetValue("AutoConfigURL") as string ?? "";
            s.AutoConfig = s.AutoConfigUrl.Length > 0;
            var over = (k?.GetValue("ProxyOverride") as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            s.BypassLocal = over.Remove("<local>");
            s.Bypass = string.Join(", ", over);
        }
        catch { }
        return s;
    }

    public static void Write(Settings s)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue("ProxyEnable", s.Enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        if (s.Server.Length > 0) k.SetValue("ProxyServer", s.Port.Length > 0 ? $"{s.Server}:{s.Port}" : s.Server);
        var over = s.Bypass.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (s.BypassLocal) over.Add("<local>");
        k.SetValue("ProxyOverride", string.Join(";", over));
        if (s.AutoConfig && s.AutoConfigUrl.Length > 0) k.SetValue("AutoConfigURL", s.AutoConfigUrl);
        else k.DeleteValue("AutoConfigURL", false);
        // apps pick it up now
        InternetSetOption(IntPtr.Zero, 39 /*SETTINGS_CHANGED*/, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /*REFRESH*/, IntPtr.Zero, 0);
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll")]
    static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int length);
}
