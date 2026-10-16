using System.Runtime.InteropServices;

namespace MacShell.Native;

/// <summary>
/// The displays as they are now, for System Settings › Displays: each one's name (from the display itself), whether it's
/// built in, its place on the desktop, resolution, refresh rate, scale and rotation. READ ONLY: nothing here changes a
/// display's mode, colours, gamma or anything else (no SetDisplayConfig, no ChangeDisplaySettings) - see the no-dithering rule.
/// </summary>
public static class DisplayInfo
{
    public record Display(string Name, bool BuiltIn, bool Main, RECT Bounds, int Width, int Height, double RefreshHz, double Scale, int Rotation, string GdiName, string Connection);

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PathSource { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] struct Rational { public uint Num, Den; }
    [StructLayout(LayoutKind.Sequential)]
    struct PathTarget { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public Rational refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource source; public PathTarget target; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct ModeInfo { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetName
    {
        public uint type, size; public LUID adapterId; public uint id;
        public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public uint type, size; public LUID adapterId; public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName request);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode dm);

    public static List<Display> All()
    {
        var list = new List<Display>();
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return list;
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var tn = new TargetName { type = 2, size = (uint)Marshal.SizeOf<TargetName>(), adapterId = p.target.adapterId, id = p.target.id };
                DisplayConfigGetDeviceInfo(ref tn);
                var sn = new SourceName { type = 1, size = (uint)Marshal.SizeOf<SourceName>(), adapterId = p.source.adapterId, id = p.source.id };
                DisplayConfigGetDeviceInfo(ref sn);
                var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettingsW(sn.gdiName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm)) continue;
                uint tech = p.target.outputTechnology;
                bool builtIn = tech is 0x80000000 or 11 or 13;
                var bounds = new RECT(dm.dmPositionX, dm.dmPositionY, dm.dmPositionX + (int)dm.dmPelsWidth, dm.dmPositionY + (int)dm.dmPelsHeight);
                double hz = p.target.refreshRate.Den > 0 ? p.target.refreshRate.Num / (double)p.target.refreshRate.Den : dm.dmDisplayFrequency;
                var center = new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
                string name = string.IsNullOrWhiteSpace(tn.friendly) ? (builtIn ? "Built-in Display" : "Display") : tn.friendly;
                if (builtIn && (string.IsNullOrWhiteSpace(tn.friendly) || tn.friendly.All(c => char.IsUpper(c) || char.IsDigit(c)))) name = "Built-in Display";
                list.Add(new Display(name, builtIn, dm.dmPositionX == 0 && dm.dmPositionY == 0, bounds, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight,
                    Math.Round(hz, 2), ScreenGdi.ScaleAt(center), (int)p.target.rotation switch { 2 => 90, 3 => 180, 4 => 270, _ => 0 }, sn.gdiName, Connection(tech)));
            }
        }
        catch { }
        return list.OrderByDescending(d => d.Main).ThenBy(d => d.Bounds.Left).ToList();
    }

    static string Connection(uint tech) => tech switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 11 => "Internal (eDP)", 13 => "Internal", 0x80000000 => "Internal",
        15 => "Miracast", 16 => "Indirect", _ => "Other",
    };
}
