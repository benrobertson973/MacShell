using System.Runtime.InteropServices;

namespace MacShell.Native;

/// <summary>Master volume via Windows Core Audio.</summary>
public static class AudioVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IPropertyStore store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    // undocumented but stable since Windows 7: what the Sound control panel uses to switch the default device
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(string id, IntPtr format);
        int GetDeviceFormat(string id, int def, IntPtr format);
        int ResetDeviceFormat(string id);
        int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string id, int def, IntPtr defaultPeriod, IntPtr minPeriod);
        int SetProcessingPeriod(string id, IntPtr period);
        int GetShareMode(string id, IntPtr mode);
        int SetShareMode(string id, IntPtr mode);
        int GetPropertyValue(string id, IntPtr key, IntPtr value);
        int SetPropertyValue(string id, IntPtr key, IntPtr value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        int SetEndpointVisibility(string id, int visible);
    }

    public record Device(string Id, string Name, bool IsDefault);

    /// <summary>Active playback devices, with the current default marked.</summary>
    public static List<Device> OutputDevices()
    {
        var list = new List<Device>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            string defId = null;
            if (en.GetDefaultAudioEndpoint(0, 1, out var def) == 0 && def != null) def.GetId(out defId);
            if (en.EnumAudioEndpoints(0 /*render*/, 1 /*ACTIVE*/, out var col) != 0 || col == null) return list;
            col.GetCount(out uint n);
            var nameKey = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0 || dev == null) continue;
                dev.GetId(out string id);
                string name = id;
                if (dev.OpenPropertyStore(0, out var store) == 0 && store != null)
                {
                    if (store.GetValue(ref nameKey, out PROPVARIANT pv) == 0 && pv.vt == 31 && pv.pointerValue != IntPtr.Zero)
                    {
                        name = Marshal.PtrToStringUni(pv.pointerValue);
                        Marshal.FreeCoTaskMem(pv.pointerValue);
                    }
                    Marshal.ReleaseComObject(store);
                }
                list.Add(new Device(id, name, id == defId));
            }
        }
        catch { }
        return list;
    }

    public static void SetDefaultDevice(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            pc.SetDefaultEndpoint(id, 0);   // console
            pc.SetDefaultEndpoint(id, 1);   // multimedia
            pc.SetDefaultEndpoint(id, 2);   // communications
        }
        catch { }
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDB, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float levelDB);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDB, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint channel, out float levelDB);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume Endpoint()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out var dev) != 0 || dev == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object o);
            return o as IAudioEndpointVolume;
        }
        catch { return null; }
    }

    public static double? Get()
    {
        var ep = Endpoint();
        if (ep == null) return null;
        return ep.GetMasterVolumeLevelScalar(out float v) == 0 ? v : null;
    }

    public static void Set(double v)
    {
        var ep = Endpoint();
        if (ep == null) return;
        var g = Guid.Empty;
        ep.SetMasterVolumeLevelScalar((float)Math.Clamp(v, 0, 1), ref g);
        if (v > 0) ep.SetMute(false, ref g);
    }

    public static bool IsMuted()
    {
        var ep = Endpoint();
        return ep != null && ep.GetMute(out bool m) == 0 && m;
    }

    public static void SetMute(bool mute)
    {
        var ep = Endpoint();
        if (ep == null) return;
        var g = Guid.Empty;
        ep.SetMute(mute, ref g);
    }
}

/// <summary>Built-in display brightness through WMI (laptops only).</summary>
public static class Brightness
{
    public static int? Get()
    {
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
            dynamic svc = locator.ConnectServer(".", "root\\WMI");
            foreach (dynamic m in svc.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                return (int)m.CurrentBrightness;
        }
        catch { }
        return null;
    }

    public static void Set(int level)
    {
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
            dynamic svc = locator.ConnectServer(".", "root\\WMI");
            foreach (dynamic m in svc.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods"))
                m.WmiSetBrightness(1, Math.Clamp(level, 0, 100));
        }
        catch { }
    }
}

/// <summary>Static machine information for "About This Mac".</summary>
public static class MachineInfo
{
    static string Reg(string key, string value)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value)?.ToString()?.Trim();
        }
        catch { return null; }
    }

    public static string Cpu
    {
        get
        {
            string s = Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "Unknown";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\((R|TM|tm|r)\)|\bCPU\b|@\s*[\d.]+\s*GHz|\bProcessor\b|\d+-Core", "");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        }
    }

    static bool Junk(string s) => string.IsNullOrWhiteSpace(s) || s.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase)
        || s.Contains("System Product", StringComparison.OrdinalIgnoreCase) || s.Contains("Default string", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marketing-style model name, e.g. "HP Victus" (BIOS family with vendor codes stripped).</summary>
    public static string Model
    {
        get
        {
            string fam = Reg(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemFamily");
            string prod = Reg(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
            if (!Junk(fam))
            {
                fam = System.Text.RegularExpressions.Regex.Replace(fam, @"^\S*_\S*\s+", "").Trim();   // "103C_5335M7 HP Victus" → "HP Victus"
                if (fam.Length is > 1 and <= 28) return fam;
            }
            if (!Junk(prod) && prod.Length <= 28) return prod;
            return "Windows PC";
        }
    }

    /// <summary>Secondary line under the model (full product name).</summary>
    public static string ModelDetail
    {
        get
        {
            string prod = Reg(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
            if (!Junk(prod) && prod != Model) return prod;
            return Manufacturer;
        }
    }
    public static string Manufacturer => Reg(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer") ?? "";
    public static string Serial
    {
        get
        {
            try
            {
                dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
                dynamic svc = locator.ConnectServer(".", "root\\CIMV2");
                foreach (dynamic b in svc.ExecQuery("SELECT SerialNumber FROM Win32_BIOS")) return ((string)b.SerialNumber)?.Trim();
            }
            catch { }
            return "Unavailable";
        }
    }

    public static string MemoryGB
    {
        get
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            NativeMethods.GlobalMemoryStatusEx(ref m);
            return Math.Round(m.ullTotalPhys / 1024.0 / 1024 / 1024) + " GB";
        }
    }

    public static string OsName
    {
        get
        {
            string name = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
            string build = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber") ?? "";
            if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return name;
        }
    }

    public static string OsVersion
    {
        get
        {
            string disp = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "";
            string build = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber") ?? "";
            string ubr = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR") ?? "";
            return $"{disp} ({build}{(ubr.Length > 0 ? "." + ubr : "")})".Trim();
        }
    }

    public static string StartupDisk => Finder.FinderLocation.DisplayName(Path.GetPathRoot(Environment.SystemDirectory));
}
