using System.Runtime.InteropServices;

namespace MacShell.Native;

/// <summary>
/// Sound devices for System Settings › Sound (Core Audio): every output and input with its kind (Built-in, USB,
/// Bluetooth, HDMI …), each one's volume and mute, the output's balance, and an input's level (the microphone is
/// listened to only while the level is shown).
/// </summary>
public static class AudioDevices
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
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

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long def, out long min);
        int Start();
        int Stop();
        int Reset();
    }

    public record Device(string Id, string Name, string Type, bool IsDefault);

    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    static readonly PROPERTYKEY FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static readonly PROPERTYKEY EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    static readonly PROPERTYKEY FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    /// <summary>The working outputs (input: false) or inputs, the default marked.</summary>
    public static List<Device> List(bool input)
    {
        var list = new List<Device>();
        try
        {
            var en = Enumerator();
            int flow = input ? 1 : 0;
            string defId = null;
            if (en.GetDefaultAudioEndpoint(flow, 1, out var def) == 0 && def != null) def.GetId(out defId);
            if (en.EnumAudioEndpoints(flow, 1 /*ACTIVE*/, out var col) != 0 || col == null) return list;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0 || dev == null) continue;
                dev.GetId(out string id);
                string name = id, bus = "", type;
                int form = 10;
                if (dev.OpenPropertyStore(0, out var store) == 0 && store != null)
                {
                    name = ReadString(store, FriendlyName) ?? id;
                    bus = ReadString(store, EnumeratorName) ?? "";
                    form = ReadUInt(store, FormFactor) ?? 10;
                    Marshal.ReleaseComObject(store);
                }
                type = bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ? "Bluetooth"
                    : bus.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB"
                    : form == 9 ? (name.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase) ? "DisplayPort" : "HDMI")
                    : form == 0 ? "Network"
                    : (bus.Equals("ROOT", StringComparison.OrdinalIgnoreCase) || bus.Equals("SWD", StringComparison.OrdinalIgnoreCase)) && !name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ? "Virtual"
                    : "Built-in";
                list.Add(new Device(id, name, type, id == defId));
            }
        }
        catch { }
        return list;
    }

    static string ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        var k = key;
        if (store.GetValue(ref k, out PROPVARIANT pv) != 0 || pv.vt != 31 || pv.pointerValue == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(pv.pointerValue); } finally { Marshal.FreeCoTaskMem(pv.pointerValue); }
    }

    static int? ReadUInt(IPropertyStore store, PROPERTYKEY key)
    {
        var k = key;
        if (store.GetValue(ref k, out PROPVARIANT pv) != 0 || pv.vt != 19 /*VT_UI4*/) return null;
        return (int)(long)pv.pointerValue;
    }

    static IMMDevice Get(string id)
    {
        try { return Enumerator().GetDevice(id, out var d) == 0 ? d : null; } catch { return null; }
    }

    static T Activate<T>(IMMDevice dev) where T : class
    {
        if (dev == null) return null;
        try
        {
            var iid = typeof(T).GUID;
            dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object o);
            return o as T;
        }
        catch { return null; }
    }

    public static double? Volume(string id) => Activate<IAudioEndpointVolume>(Get(id)) is { } v && v.GetMasterVolumeLevelScalar(out float f) == 0 ? f : null;

    public static void SetVolume(string id, double value)
    {
        if (Activate<IAudioEndpointVolume>(Get(id)) is not { } v) return;
        var g = Guid.Empty;
        v.SetMasterVolumeLevelScalar((float)Math.Clamp(value, 0, 1), ref g);
        if (value > 0) v.SetMute(false, ref g);
    }

    public static bool Muted(string id) => Activate<IAudioEndpointVolume>(Get(id)) is { } v && v.GetMute(out bool m) == 0 && m;

    public static void SetMuted(string id, bool mute)
    {
        if (Activate<IAudioEndpointVolume>(Get(id)) is not { } v) return;
        var g = Guid.Empty;
        v.SetMute(mute, ref g);
    }

    /// <summary>The output's balance, -1 (left) to 1 (right) - null when it hasn't two channels.</summary>
    public static double? Balance(string id)
    {
        if (Activate<IAudioEndpointVolume>(Get(id)) is not { } v || v.GetChannelCount(out uint n) != 0 || n < 2) return null;
        v.GetChannelVolumeLevelScalar(0, out float l);
        v.GetChannelVolumeLevelScalar(1, out float r);
        float max = Math.Max(l, r);
        if (max <= 0.0001f) return 0;
        return l >= r ? -(1 - r / max) : 1 - l / max;
    }

    /// <summary>Sets the balance by turning one side down (the louder side keeps the master volume).</summary>
    public static void SetBalance(string id, double balance)
    {
        if (Activate<IAudioEndpointVolume>(Get(id)) is not { } v || v.GetChannelCount(out uint n) != 0 || n < 2) return;
        v.GetMasterVolumeLevelScalar(out float master);
        var g = Guid.Empty;
        float l = (float)(balance > 0 ? master * (1 - balance) : master), r = (float)(balance < 0 ? master * (1 + balance) : master);
        v.SetChannelVolumeLevelScalar(0, Math.Clamp(l, 0, 1), ref g);
        v.SetChannelVolumeLevelScalar(1, Math.Clamp(r, 0, 1), ref g);
    }

    /// <summary>An input's level, read while it's listened to (a shared capture stream that nothing is kept from).</summary>
    public sealed class InputMeter : IDisposable
    {
        IAudioClient _client;
        IAudioMeterInformation _meter;

        public InputMeter(string id)
        {
            var dev = Get(id);
            _meter = Activate<IAudioMeterInformation>(dev);
            _client = Activate<IAudioClient>(dev);
            if (_client == null) return;
            try
            {
                if (_client.GetMixFormat(out IntPtr fmt) != 0) { _client = null; return; }
                try
                {
                    if (_client.Initialize(0 /*shared*/, 0, 2_000_000, 0, fmt, IntPtr.Zero) != 0 || _client.Start() != 0) _client = null;
                }
                finally { Marshal.FreeCoTaskMem(fmt); }
            }
            catch { _client = null; }
        }

        /// <summary>0-1 (the loudest sample since it was last read).</summary>
        public double Peak => _meter != null && _meter.GetPeakValue(out float p) == 0 ? p : 0;

        public void Dispose()
        {
            try { _client?.Stop(); } catch { }
            if (_client != null) Marshal.ReleaseComObject(_client);
            if (_meter != null) Marshal.ReleaseComObject(_meter);
            _client = null; _meter = null;
        }
    }

    /// <summary>Windows' alert ("Default Beep") sound: the file it plays.</summary>
    public static string AlertSound
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\.Default\.Current");
                return Environment.ExpandEnvironmentVariables(k?.GetValue("") as string ?? "");
            }
            catch { return ""; }
        }
        set
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"AppEvents\Schemes\Apps\.Default\.Default\.Current");
                k.SetValue("", value ?? "", Microsoft.Win32.RegistryValueKind.ExpandString);
            }
            catch { }
        }
    }

    /// <summary>Plays a sound file once (an alert sound being chosen).</summary>
    public static void Play(string file)
    {
        try { if (File.Exists(file)) PlaySound(file, IntPtr.Zero, 0x0001 | 0x0002 | 0x00020000); } catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(string file, IntPtr hmod, uint flags);
}
