namespace MacShell.Native;

/// <summary>
/// Bluetooth devices through Windows.Devices.Enumeration (association endpoints, what Windows Settings lists): the
/// paired ones and whether they're connected, the ones nearby, pairing and forgetting. The radio itself is in
/// <see cref="Radios"/>. Blocking calls - use them off the UI thread.
/// </summary>
public static class Bluetooth
{
    public record Device(string Id, string Name, bool Paired, bool Connected, bool LowEnergy, string Address, uint Major, uint Minor, int? Signal)
    {
        /// <summary>A symbol for the device's kind: headphones, a mouse, a keyboard, a phone, a computer, a speaker …</summary>
        public string Kind
        {
            get
            {
                string n = Name?.ToLowerInvariant() ?? "";
                if (Major == 4 && Minor is 1 or 2 or 6) return "headphones";
                if (Major == 4 && Minor is 5 or 7 or 8 or 10) return "speaker";
                if (Major == 5 && (Minor & 0x10) != 0) return "keyboard";
                if (Major == 5 && (Minor & 0x20) != 0) return "computermouse";
                if (Major == 2) return "iphone";
                if (Major == 1) return "laptop";
                if (Major == 7) return "applewatch";
                if (n.Contains("bud") || n.Contains("pods") || n.Contains("headphone") || n.Contains("headset") || n.Contains("wh-") || n.Contains("wf-")) return "headphones";
                if (n.Contains("mouse") || n.Contains("trackpad")) return "computermouse";
                if (n.Contains("keyboard") || n.Contains("keys")) return "keyboard";
                if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("boom") || n.Contains("flip") || n.Contains("charge")) return "speaker";
                if (n.Contains("phone") || n.Contains("galaxy") || n.Contains("pixel")) return "iphone";
                if (n.Contains("watch")) return "applewatch";
                if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense")) return "gamecontroller";
                return "bluetooth";
            }
        }
    }

    const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    const string IStatics2 = "493B4F34-A84F-45FD-9167-15D1CB1BD1F9";
    static readonly string[] Props =
    {
        "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor", "System.Devices.Aep.SignalStrength",
    };

    static string Aqs(string protocol, bool paired) =>
        $"System.Devices.Aep.ProtocolId:=\"{protocol}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#{(paired ? "True" : "False")}";

    /// <summary>The paired devices (paired) or the ones around that could be paired (!paired), one per device.</summary>
    public static List<Device> Find(bool paired, int timeoutMs = 8000)
    {
        var all = new List<Device>();
        all.AddRange(Query(Aqs(ClassicProtocol, paired), false, timeoutMs));
        all.AddRange(Query(Aqs(LowEnergyProtocol, paired), true, timeoutMs));
        // a device that speaks both Bluetooth Classic and Low Energy has an entry for each: one row, connected if either is
        return all.Where(d => !string.IsNullOrWhiteSpace(d.Name))
                  .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                  .Select(g =>
                  {
                      var main = g.OrderBy(d => d.LowEnergy).First();
                      return main with { Connected = g.Any(d => d.Connected), Signal = g.Max(d => d.Signal) };
                  })
                  .OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static unsafe List<Device> Query(string aqs, bool lowEnergy, int timeoutMs)
    {
        var list = new List<Device>();
        IntPtr st = IntPtr.Zero, props = IntPtr.Zero, op = IntPtr.Zero, coll = IntPtr.Zero;
        try
        {
            st = WinRt.Factory("Windows.Devices.Enumeration.DeviceInformation", IStatics2);
            props = WinRtStrings.Create(Props);
            IntPtr h = WinRt.String(aqs);
            try
            {
                IntPtr r = IntPtr.Zero;
                WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int, IntPtr*, int>)(*(void***)st)[8])(st, h, props, 5 /*AssociationEndpoint*/, &r));
                op = r;
            }
            finally { WinRt.FreeString(h); }
            if (!WinRt.Wait(op, timeoutMs)) return list;
            coll = WinRt.GetPtr(op, 8);
            foreach (var d in WinRt.Items(coll))
            {
                try
                {
                    string id = WinRt.GetString(d, 6), name = WinRt.GetString(d, 7);
                    IntPtr map = WinRt.GetPtr(d, 11);
                    try
                    {
                        bool B(string k) => WinRtProps.Lookup(map, k) is bool b && b;
                        uint U(string k) => WinRtProps.Lookup(map, k) switch { uint u => u, int i => (uint)i, byte y => y, _ => 0 };
                        int? signal = WinRtProps.Lookup(map, "System.Devices.Aep.SignalStrength") switch { int i => i, uint u => (int)u, _ => null };
                        list.Add(new Device(id, name, B("System.Devices.Aep.IsPaired"), B("System.Devices.Aep.IsConnected"), lowEnergy,
                            WinRtProps.Lookup(map, "System.Devices.Aep.DeviceAddress") as string, U("System.Devices.Aep.Bluetooth.Cod.Major"),
                            U("System.Devices.Aep.Bluetooth.Cod.Minor"), signal));
                    }
                    finally { WinRt.Release(map); }
                }
                catch { }
                finally { WinRt.Release(d); }
            }
        }
        catch (Exception ex) { App.Log("bluetooth query: " + ex.Message); }
        finally { WinRt.Release(coll); WinRt.Release(op); WinRt.Release(props); WinRt.Release(st); }
        return list;
    }

    /// <summary>The pairing object of a device (DeviceInformationPairing), or zero.</summary>
    static unsafe IntPtr Pairing(string id)
    {
        IntPtr st = WinRt.Factory("Windows.Devices.Enumeration.DeviceInformation", IStatics2);
        IntPtr h = WinRt.String(id), op = IntPtr.Zero, info = IntPtr.Zero, info2 = IntPtr.Zero;
        try
        {
            IntPtr r = IntPtr.Zero;
            WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int, IntPtr*, int>)(*(void***)st)[7])(st, h, IntPtr.Zero, 5, &r));   // CreateFromIdAsync
            op = r;
            if (!WinRt.Wait(op, 10000)) return IntPtr.Zero;
            info = WinRt.GetPtr(op, 8);
            info2 = WinRt.As(info, "F156A638-7997-48D9-A10C-269D46533F48");   // IDeviceInformation2
            return WinRt.GetPtr(info2, 7);   // get_Pairing
        }
        finally { WinRt.FreeString(h); WinRt.Release(info2); WinRt.Release(info); WinRt.Release(op); WinRt.Release(st); }
    }

    /// <summary>Pairs with a device (Windows asks to confirm a code if the device needs it). 0 paired, 3 already paired, else a failure.</summary>
    public static int Pair(string id)
    {
        try
        {
            IntPtr pairing = Pairing(id);
            if (pairing == IntPtr.Zero) return -1;
            try
            {
                IntPtr op = WinRt.GetPtr(pairing, 8);   // PairAsync()
                try
                {
                    if (!WinRt.Wait(op, 90000)) return -2;
                    IntPtr result = WinRt.GetPtr(op, 8);
                    try { return WinRt.GetInt(result, 6); } finally { WinRt.Release(result); }
                }
                finally { WinRt.Release(op); }
            }
            finally { WinRt.Release(pairing); }
        }
        catch (Exception ex) { App.Log("bluetooth pair: " + ex.Message); return -1; }
    }

    /// <summary>Forgets a device (unpairs it). 0 unpaired, 1 already unpaired, else a failure.</summary>
    public static int Unpair(string id)
    {
        try
        {
            IntPtr pairing = Pairing(id);
            if (pairing == IntPtr.Zero) return -1;
            IntPtr pairing2 = IntPtr.Zero;
            try
            {
                pairing2 = WinRt.As(pairing, "F68612FD-0AEE-4328-85CC-1C742BB1790D");   // IDeviceInformationPairing2
                IntPtr op = WinRt.GetPtr(pairing2, 9);   // UnpairAsync
                try
                {
                    if (!WinRt.Wait(op, 30000)) return -2;
                    IntPtr result = WinRt.GetPtr(op, 8);
                    try { return WinRt.GetInt(result, 6); } finally { WinRt.Release(result); }
                }
                finally { WinRt.Release(op); }
            }
            finally { WinRt.Release(pairing2); WinRt.Release(pairing); }
        }
        catch (Exception ex) { App.Log("bluetooth unpair: " + ex.Message); return -1; }
    }

    public static string PairingError(int status) => status switch
    {
        1 => "The device isn’t ready to pair. Make sure it’s in pairing mode and try again.",
        4 => "The device refused the connection.",
        5 => "The device has too many connections.",
        7 or 8 or 9 => "Pairing didn’t finish: the code wasn’t confirmed.",
        10 => "The device doesn’t offer anything this computer can use.",
        14 => "Pairing was cancelled.",
        15 => "The device is already pairing.",
        -2 => "The device didn’t answer in time.",
        _ => "The device couldn’t be paired.",
    };
}
