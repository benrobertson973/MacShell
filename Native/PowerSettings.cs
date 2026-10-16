using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace MacShell.Native;

/// <summary>
/// Power settings of Windows' active power plan (powrprof): when the display turns off and the computer sleeps, on
/// battery and on the power adapter; asking for the password on wake; energy saver; the power mode; the lid and the
/// power button. And the battery itself: its charge, health and the level over the last day.
/// </summary>
public static class PowerSettings
{
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Status s);

    [StructLayout(LayoutKind.Sequential)]
    public struct Status { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    public record Setting(string Sub, string Id);

    public static readonly Setting DisplayOff = new("7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public static readonly Setting SleepAfter = new("238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    public static readonly Setting HibernateAfter = new("238c9fa8-0aad-41ed-83f4-97be242c8f20", "9d7815a6-7ee4-497e-8888-515a05f02364");
    public static readonly Setting PasswordOnWake = new("fea3413e-7e05-4911-9a71-700331f1c294", "0e796bdb-100d-47d6-a2d5-f7d2daa51f51");
    public static readonly Setting SaverThreshold = new("de830923-a562-41af-a086-e3a2c6bad2da", "e69653ca-cf7f-4f05-aa73-cb833fa90ad4");
    public static readonly Setting SaverDimsScreen = new("de830923-a562-41af-a086-e3a2c6bad2da", "13d09884-f74e-474a-a852-b6bde8ad03a8");
    public static readonly Setting LidAction = new("4f971e89-eebd-4455-a8de-9e59040e7347", "5ca83367-6e45-459f-a27b-476b1d01c936");
    public static readonly Setting PowerButton = new("4f971e89-eebd-4455-a8de-9e59040e7347", "7648efa3-dd9c-4e3e-b566-50f929386280");
    public static readonly Setting UsbSuspend = new("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    /// <summary>Windows 11's power modes.</summary>
    public static readonly (string name, Guid id)[] Modes =
    {
        ("Best Power Efficiency", new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")),
        ("Balanced", Guid.Empty),
        ("Best Performance", new Guid("ded574b5-45a0-4f42-8737-46345c09c238")),
    };

    static Guid ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(p); } finally { LocalFree(p); }
    }

    /// <summary>A setting's value on the power adapter (ac) or on battery: null when Windows doesn't have it.</summary>
    public static uint? Get(Setting s, bool ac)
    {
        try
        {
            Guid scheme = ActiveScheme(), sub = new(s.Sub), id = new(s.Id);
            uint v;
            uint r = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, out v) : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, out v);
            return r == 0 ? v : null;
        }
        catch { return null; }
    }

    /// <summary>Changes a setting of the active plan and puts it into effect: true when Windows allowed it.</summary>
    public static bool Set(Setting s, bool ac, uint value)
    {
        try
        {
            Guid scheme = ActiveScheme(), sub = new(s.Sub), id = new(s.Id);
            uint r = ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, value) : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, value);
            if (r != 0) { App.Log($"power setting {s.Id} ({(ac ? "AC" : "DC")}) = {value}: error {r}"); return false; }
            PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            return true;
        }
        catch (Exception ex) { App.Log("power setting: " + ex.Message); return false; }
    }

    public static Guid PowerMode(bool ac)
    {
        try { return (ac ? PowerGetUserConfiguredACPowerMode(out var g) : PowerGetUserConfiguredDCPowerMode(out g)) == 0 ? g : Guid.Empty; }
        catch { return Guid.Empty; }
    }

    public static bool SetPowerMode(bool ac, Guid mode)
    {
        try { return (ac ? PowerSetUserConfiguredACPowerMode(ref mode) : PowerSetUserConfiguredDCPowerMode(ref mode)) == 0; }
        catch { return false; }
    }

    public static Status Now() { GetSystemPowerStatus(out var s); return s; }

    public static bool HasBattery { get { var s = Now(); return s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100; } }
    public static bool OnAdapter => Now().ACLineStatus == 1;
    /// <summary>Energy saver (Windows' battery saver) is on right now.</summary>
    public static bool SaverOn => Now().SystemStatusFlag == 1;

    // ------------------------------------------------------------------ the battery's health

    public record Health(int DesignMWh, int FullMWh, int Cycles, string Chemistry)
    {
        public int MaximumPercent => DesignMWh > 0 ? (int)Math.Round(100.0 * FullMWh / DesignMWh) : 0;
    }

    public record UsagePoint(DateTime Time, int Percent, bool OnAdapter);

    static (Health health, List<UsagePoint> usage, DateTime at) _report;

    /// <summary>The battery report Windows writes (powercfg /batteryreport): health and the last day's levels.
    /// Takes a second or two - call it off the UI thread; kept for five minutes.</summary>
    public static (Health health, List<UsagePoint> usage) Report()
    {
        if (_report.at > DateTime.Now.AddMinutes(-5) && _report.health != null) return (_report.health, _report.usage);
        string file = Path.Combine(Path.GetTempPath(), $"macshell-battery-{Environment.ProcessId}.xml");
        try
        {
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{file}\"")
                   { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                p.WaitForExit(15000);
            }
            if (!File.Exists(file)) return (null, new List<UsagePoint>());
            var doc = XDocument.Load(file);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var bat = doc.Descendants(ns + "Battery").FirstOrDefault();
            Health health = null;
            if (bat != null)
            {
                int Int(string n) => int.TryParse(bat.Element(ns + n)?.Value, out int v) ? v : 0;
                health = new Health(Int("DesignCapacity"), Int("FullChargeCapacity"), Int("CycleCount"), bat.Element(ns + "Chemistry")?.Value);
            }
            var usage = new List<UsagePoint>();
            int full = Math.Max(1, health?.FullMWh ?? 1);
            foreach (var e in doc.Descendants(ns + "UsageEntry"))
            {
                if (!DateTime.TryParse(e.Attribute("LocalTimestamp")?.Value ?? e.Attribute("Timestamp")?.Value, out var t)) continue;
                if (!int.TryParse(e.Attribute("ChargeCapacity")?.Value, out int cap)) continue;
                int fcc = int.TryParse(e.Attribute("FullChargeCapacity")?.Value, out int f) && f > 0 ? f : full;
                bool ac = e.Attribute("Ac")?.Value == "1";
                usage.Add(new UsagePoint(t, Math.Clamp((int)Math.Round(100.0 * cap / fcc), 0, 100), ac));
            }
            usage = usage.OrderBy(u => u.Time).ToList();
            _report = (health, usage, DateTime.Now);
            return (health, usage);
        }
        catch (Exception ex) { App.Log("battery report: " + ex.Message); return (null, new List<UsagePoint>()); }
        finally { try { File.Delete(file); } catch { } }
    }
}
