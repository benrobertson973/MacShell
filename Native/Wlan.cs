using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace MacShell.Native;

/// <summary>
/// Wi-Fi through the Native Wifi API (wlanapi.dll): the adapter, the network it's on, the networks around and the
/// ones it knows, joining, leaving and forgetting. Everything here works without administrator rights.
/// </summary>
public static class Wlan
{
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int size, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")] static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opCode, int size, IntPtr data, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, int flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool secure, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved, out IntPtr xml, ref int flags, out int access);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr handle, ref Guid iface, int flags, string xml, string security, bool overwrite, IntPtr reserved, out int reason);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanDeleteProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr handle, ref Guid iface, IntPtr parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);

    public record Adapter(Guid Id, string Description, int State);
    /// <summary>A network around. Signal 0-100; Security as Windows names it ("WPA2-Personal"); Band "2.4 GHz" / "5 GHz" / "6 GHz".</summary>
    public record Network(string Ssid, string Profile, int Signal, bool Secure, string Security, int Auth, bool Connected, bool Known, string Band);
    public record Connection(string Ssid, string Profile, int Signal, string Bssid, string Phy, double RxMbps, double TxMbps, string Security, string Band, int Channel);
    public record KnownNetwork(string Name, bool AutoJoin, bool PerUser);

    static T WithHandle<T>(Func<IntPtr, T> f, T fallback)
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return fallback;
            try { return f(h); } finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch { return fallback; }
    }

    /// <summary>The Wi-Fi adapters (usually one). State: 1 connected, 4 disconnected, 0 not ready, 5-7 joining.</summary>
    public static List<Adapter> Adapters() => WithHandle(h =>
    {
        var list = new List<Adapter>();
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var p) != 0) return list;
        try
        {
            int n = Marshal.ReadInt32(p);
            for (int i = 0; i < n; i++)
            {
                IntPtr e = p + 8 + i * 532;
                var id = Marshal.PtrToStructure<Guid>(e);
                list.Add(new Adapter(id, Marshal.PtrToStringUni(e + 16, 256).TrimEnd('\0'), Marshal.ReadInt32(e + 16 + 512)));
            }
        }
        finally { WlanFreeMemory(p); }
        return list;
    }, new List<Adapter>());

    public static Adapter Primary => Adapters().FirstOrDefault();

    /// <summary>The adapter's radio as Windows' Wi-Fi switch sets it: on (null when there's no adapter); hardware off counts as off.</summary>
    public static bool? RadioOn()
    {
        var a = Primary;
        if (a == null) return null;
        return WithHandle<bool?>(h =>
        {
            var id = a.Id;
            if (WlanQueryInterface(h, ref id, 4 /*radio_state*/, IntPtr.Zero, out _, out var d, out _) != 0) return null;
            try
            {
                int phys = Marshal.ReadInt32(d);
                bool any = false;
                for (int i = 0; i < phys; i++)
                {
                    int sw = Marshal.ReadInt32(d + 4 + i * 12 + 4), hw = Marshal.ReadInt32(d + 4 + i * 12 + 8);
                    if (sw == 1 && hw == 1) any = true;
                }
                return any;
            }
            finally { WlanFreeMemory(d); }
        }, null);
    }

    public static Connection Current()
    {
        var a = Primary;
        if (a == null || a.State != 1) return null;
        return WithHandle(h =>
        {
            var id = a.Id;
            if (WlanQueryInterface(h, ref id, 7 /*current_connection*/, IntPtr.Zero, out _, out var d, out _) != 0) return null;
            try
            {
                string profile = Marshal.PtrToStringUni(d + 8, 256).TrimEnd('\0');
                IntPtr assoc = d + 8 + 512;
                string ssid = Ssid(assoc);
                var mac = new byte[6];
                Marshal.Copy(assoc + 40, mac, 0, 6);
                int phy = Marshal.ReadInt32(assoc + 48);
                int signal = Marshal.ReadInt32(assoc + 56);
                uint rx = (uint)Marshal.ReadInt32(assoc + 60), tx = (uint)Marshal.ReadInt32(assoc + 64);
                IntPtr sec = assoc + 68;
                bool secure = Marshal.ReadInt32(sec) != 0;
                int auth = Marshal.ReadInt32(sec + 8);
                string bssid = string.Join(":", mac.Select(b => b.ToString("x2")));
                var bss = BssList(h, id).FirstOrDefault(b => b.bssid == bssid);
                return new Connection(ssid, profile, signal, bssid, PhyName(phy), rx / 1000.0, tx / 1000.0, secure ? SecurityName(auth) : "None",
                                      Band(bss.freq), Channel(bss.freq));
            }
            finally { WlanFreeMemory(d); }
        }, null);
    }

    static string Ssid(IntPtr dot11Ssid)
    {
        int len = Math.Clamp(Marshal.ReadInt32(dot11Ssid), 0, 32);
        var b = new byte[len];
        Marshal.Copy(dot11Ssid + 4, b, 0, len);
        return Encoding.UTF8.GetString(b);
    }

    static List<(string ssid, string bssid, int rssi, uint freq)> BssList(IntPtr h, Guid id)
    {
        var list = new List<(string, string, int, uint)>();
        if (WlanGetNetworkBssList(h, ref id, IntPtr.Zero, 1 /*infrastructure*/, false, IntPtr.Zero, out var p) != 0) return list;
        try
        {
            int n = Marshal.ReadInt32(p + 4);
            for (int i = 0; i < n; i++)
            {
                IntPtr e = p + 8 + i * 360;
                var mac = new byte[6];
                Marshal.Copy(e + 40, mac, 0, 6);
                list.Add((Ssid(e), string.Join(":", mac.Select(b => b.ToString("x2"))), Marshal.ReadInt32(e + 56), (uint)Marshal.ReadInt32(e + 92)));
            }
        }
        finally { WlanFreeMemory(p); }
        return list;
    }

    /// <summary>Asks the adapter to look for networks again (the list is fresh a few seconds later).</summary>
    public static void Scan()
    {
        var a = Primary;
        if (a == null) return;
        WithHandle(h => { var id = a.Id; return WlanScan(h, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }, 0);
    }

    /// <summary>The networks around, one per name (strongest first), hidden ones left out.</summary>
    public static List<Network> Networks()
    {
        var a = Primary;
        if (a == null) return new List<Network>();
        return WithHandle(h =>
        {
            var id = a.Id;
            var raw = new List<Network>();
            if (WlanGetAvailableNetworkList(h, ref id, 0, IntPtr.Zero, out var p) != 0) return raw;
            var bands = BssList(h, id).GroupBy(b => b.ssid).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.rssi).First().freq);
            try
            {
                int n = Marshal.ReadInt32(p);
                for (int i = 0; i < n; i++)
                {
                    // WLAN_AVAILABLE_NETWORK (628 bytes): profile name 0, SSID 512, … signal 604, security on 608, auth 612, flags 620
                    IntPtr e = p + 8 + i * 628;
                    string profile = Marshal.PtrToStringUni(e, 256).TrimEnd('\0');
                    string ssid = Ssid(e + 512);
                    if (ssid.Length == 0) continue;
                    int signal = Marshal.ReadInt32(e + 604);
                    bool secure = Marshal.ReadInt32(e + 608) != 0;
                    int auth = Marshal.ReadInt32(e + 612);
                    int flags = Marshal.ReadInt32(e + 620);
                    bands.TryGetValue(ssid, out uint freq);
                    raw.Add(new Network(ssid, profile, signal, secure, secure ? SecurityName(auth) : "None", auth, (flags & 1) != 0, (flags & 2) != 0 || profile.Length > 0, Band(freq)));
                }
            }
            finally { WlanFreeMemory(p); }
            return raw.GroupBy(x => x.Ssid).Select(g =>
            {
                var best = g.OrderByDescending(x => x.Known).ThenByDescending(x => x.Signal).First();
                return best with { Connected = g.Any(x => x.Connected), Signal = g.Max(x => x.Signal), Known = g.Any(x => x.Known), Profile = g.Select(x => x.Profile).FirstOrDefault(s => s.Length > 0) ?? "" };
            }).OrderByDescending(x => x.Connected).ThenByDescending(x => x.Signal).ToList();
        }, new List<Network>());
    }

    /// <summary>The networks this computer remembers (Windows' Wi-Fi profiles), in Windows' order.</summary>
    public static List<KnownNetwork> Known()
    {
        var a = Primary;
        if (a == null) return new List<KnownNetwork>();
        return WithHandle(h =>
        {
            var id = a.Id;
            var list = new List<KnownNetwork>();
            if (WlanGetProfileList(h, ref id, IntPtr.Zero, out var p) != 0) return list;
            try
            {
                int n = Marshal.ReadInt32(p);
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = p + 8 + i * 516;
                    string name = Marshal.PtrToStringUni(e, 256).TrimEnd('\0');
                    int flags = Marshal.ReadInt32(e + 512);
                    list.Add(new KnownNetwork(name, ProfileXml(h, id, name)?.Contains("<connectionMode>auto</connectionMode>") ?? true, (flags & 2) != 0));
                }
            }
            finally { WlanFreeMemory(p); }
            return list;
        }, new List<KnownNetwork>());
    }

    static string ProfileXml(IntPtr h, Guid id, string name)
    {
        int flags = 0;
        if (WlanGetProfile(h, ref id, name, IntPtr.Zero, out var x, ref flags, out _) != 0) return null;
        try { return Marshal.PtrToStringUni(x); } finally { WlanFreeMemory(x); }
    }

    /// <summary>Joins a network this computer knows (by its profile name): Windows reports how it went later.</summary>
    public static int Connect(string profile)
    {
        var a = Primary;
        if (a == null) return -1;
        return WithHandle(h =>
        {
            var id = a.Id;
            // WLAN_CONNECTION_PARAMETERS: mode (profile = 0), profile name, SSID, BSSID list, BSS type (infrastructure = 1), flags
            IntPtr name = Marshal.StringToHGlobalUni(profile);
            IntPtr cp = Marshal.AllocHGlobal(48);
            try
            {
                for (int i = 0; i < 48; i += 4) Marshal.WriteInt32(cp + i, 0);
                Marshal.WriteInt32(cp, 0);
                Marshal.WriteIntPtr(cp + 8, name);
                Marshal.WriteInt32(cp + 32, 1);
                return WlanConnect(h, ref id, cp, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(cp); Marshal.FreeHGlobal(name); }
        }, -1);
    }

    public static int Disconnect()
    {
        var a = Primary;
        if (a == null) return -1;
        return WithHandle(h => { var id = a.Id; return WlanDisconnect(h, ref id, IntPtr.Zero); }, -1);
    }

    /// <summary>Forgets a network (deletes its profile).</summary>
    public static int Forget(string profile)
    {
        var a = Primary;
        if (a == null) return -1;
        return WithHandle(h => { var id = a.Id; return WlanDeleteProfile(h, ref id, profile, IntPtr.Zero); }, -1);
    }

    /// <summary>Remembers a network (a new profile, its password included) so it can be joined.</summary>
    public static int AddProfile(string ssid, int auth, string password, bool autoJoin, out int reason)
    {
        int r = 0;
        var a = Primary;
        if (a == null) { reason = 0; return -1; }
        string xml = MakeProfileXml(ssid, auth, password, autoJoin);
        int result = WithHandle(h => { var id = a.Id; return WlanSetProfile(h, ref id, 0, xml, null, true, IntPtr.Zero, out r); }, -1);
        reason = r;
        return result;
    }

    /// <summary>Switches a known network's automatic joining.</summary>
    public static int SetAutoJoin(string profile, bool auto)
    {
        var a = Primary;
        if (a == null) return -1;
        return WithHandle(h =>
        {
            var id = a.Id;
            string xml = ProfileXml(h, id, profile);
            if (xml == null) return -1;
            string want = auto ? "auto" : "manual";
            if (xml.Contains("<connectionMode>")) xml = System.Text.RegularExpressions.Regex.Replace(xml, "<connectionMode>\\w+</connectionMode>", $"<connectionMode>{want}</connectionMode>");
            else xml = xml.Replace("</connectionType>", $"</connectionType>\n\t<connectionMode>{want}</connectionMode>");
            return WlanSetProfile(h, ref id, 0, xml, null, true, IntPtr.Zero, out _);
        }, -1);
    }

    static string MakeProfileXml(string ssid, int auth, string password, bool autoJoin)
    {
        string hex = string.Concat(Encoding.UTF8.GetBytes(ssid).Select(b => b.ToString("X2")));
        string name = SecurityElement.Escape(ssid);
        (string a, string e) = auth switch
        {
            9 => ("WPA3SAE", "AES"),
            7 => ("WPA2PSK", "AES"),
            4 => ("WPAPSK", "AES"),
            10 => ("OWE", "AES"),
            _ => ("open", "none"),
        };
        string key = password == null || a is "open" or "OWE" ? "" :
            $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>";
        return "<?xml version=\"1.0\"?>\n<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
               $"<name>{name}</name><SSIDConfig><SSID><hex>{hex}</hex><name>{name}</name></SSID></SSIDConfig>" +
               $"<connectionType>ESS</connectionType><connectionMode>{(autoJoin ? "auto" : "manual")}</connectionMode>" +
               $"<MSM><security><authEncryption><authentication>{a}</authentication><encryption>{e}</encryption><useOneX>false</useOneX></authEncryption>{key}</security></MSM>" +
               "</WLANProfile>";
    }

    public static bool NeedsPassword(int auth) => auth is 2 or 4 or 7 or 9;
    public static bool IsEnterprise(int auth) => auth is 3 or 6 or 8 or 11;

    public static string SecurityName(int auth) => auth switch
    {
        1 => "None",
        2 => "WEP",
        3 => "WPA Enterprise",
        4 => "WPA Personal",
        6 => "WPA2 Enterprise",
        7 => "WPA2 Personal",
        8 or 11 => "WPA3 Enterprise",
        9 => "WPA3 Personal",
        10 => "Enhanced Open",
        _ => "Secure",
    };

    static string PhyName(int phy) => phy switch
    {
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "802.11n", 8 => "802.11ac", 10 => "802.11ax", 11 => "802.11be", _ => "802.11",
    };

    public static string Band(uint kHz) => kHz == 0 ? null : kHz < 3_000_000 ? "2.4 GHz" : kHz < 5_930_000 ? "5 GHz" : "6 GHz";

    static int Channel(uint kHz)
    {
        if (kHz == 0) return 0;
        int mhz = (int)(kHz / 1000);
        if (mhz == 2484) return 14;
        if (mhz < 3000) return (mhz - 2407) / 5;
        if (mhz >= 5955) return (mhz - 5950) / 5;
        return (mhz - 5000) / 5;
    }
}
