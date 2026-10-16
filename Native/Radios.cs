namespace MacShell.Native;

/// <summary>The computer's radios (Wi-Fi, Bluetooth …) through Windows.Devices.Radios: what Windows' own quick
/// settings switch. Blocking calls (a few hundred ms at most) - use them off the UI thread.</summary>
public static class Radios
{
    public enum Kind { Other = 0, WiFi = 1, MobileBroadband = 2, Bluetooth = 3, FM = 4 }
    public enum State { Unknown = 0, On = 1, Off = 2, Disabled = 3 }
    public record Info(string Name, Kind Kind, State State);

    const string IRadioStatics = "5FB6A12E-67CB-46AE-AAE9-65919F86EFF4";

    static List<IntPtr> All()
    {
        IntPtr statics = WinRt.Factory("Windows.Devices.Radios.Radio", IRadioStatics);
        try
        {
            IntPtr op = WinRt.GetPtr(statics, 6);   // GetRadiosAsync
            try
            {
                if (!WinRt.Wait(op, 8000)) return new List<IntPtr>();
                IntPtr vec = WinRt.GetPtr(op, 8);   // GetResults
                try { return WinRt.Items(vec); }
                finally { WinRt.Release(vec); }
            }
            finally { WinRt.Release(op); }
        }
        finally { WinRt.Release(statics); }
    }

    public static List<Info> List()
    {
        var list = new List<Info>();
        try
        {
            foreach (var r in All())
            {
                try { list.Add(new Info(WinRt.GetString(r, 10), (Kind)WinRt.GetInt(r, 11), (State)WinRt.GetInt(r, 9))); }
                catch { }
                finally { WinRt.Release(r); }
            }
        }
        catch { }
        return list;
    }

    /// <summary>The state of the first radio of a kind (null: there is none).</summary>
    public static State? Get(Kind kind) => List().FirstOrDefault(r => r.Kind == kind)?.State;

    /// <summary>Turns every radio of a kind on or off: true when Windows allowed it.</summary>
    public static bool Set(Kind kind, bool on)
    {
        bool ok = false;
        try
        {
            foreach (var r in All())
            {
                try
                {
                    if ((Kind)WinRt.GetInt(r, 11) != kind) continue;
                    IntPtr op = WinRt.CallIntPtr(r, 6, on ? 1 : 2);   // SetStateAsync(On/Off)
                    try { if (WinRt.Wait(op, 8000) && WinRt.GetInt(op, 8) == 1) ok = true; }   // RadioAccessStatus.Allowed
                    finally { WinRt.Release(op); }
                }
                catch { }
                finally { WinRt.Release(r); }
            }
        }
        catch { }
        return ok;
    }
}
