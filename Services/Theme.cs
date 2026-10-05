using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacShell.Services;

/// <summary>
/// macOS semantic colours (NSColor) for light and dark appearance, published as
/// application-level brushes so every window can bind with DynamicResource.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static Color Accent { get; private set; }
    public static Color SelectionColor { get; private set; }
    public static event Action Changed;

    public const string FontStack = "SF Pro Text, SF Pro, .AppleSystemUIFont, Inter, Segoe UI Variable Text, Segoe UI";
    public const string DisplayFontStack = "SF Pro Display, SF Pro, Inter Display, Inter, Segoe UI Variable Display, Segoe UI";
    public const string MonoFontStack = "SF Mono, Menlo, Cascadia Mono, Consolas";

    public static readonly FontFamily Font = new(FontStack);
    public static readonly FontFamily DisplayFont = new(DisplayFontStack);
    public static readonly FontFamily MonoFont = new(MonoFontStack);

    public static readonly (string id, string name, Color light, Color dark)[] Accents =
    {
        ("multicolor", "Multicolor", C("#007AFF"), C("#0A84FF")),
        ("blue", "Blue", C("#007AFF"), C("#0A84FF")),
        ("purple", "Purple", C("#953D96"), C("#A550A7")),
        ("pink", "Pink", C("#F74F9E"), C("#F74F9E")),
        ("red", "Red", C("#E0383E"), C("#FF5257")),
        ("orange", "Orange", C("#F7821B"), C("#F7821B")),
        ("yellow", "Yellow", C("#FFC600"), C("#FFC600")),
        ("green", "Green", C("#62BA46"), C("#62BA46")),
        ("graphite", "Graphite", C("#989898"), C("#8C8C8C")),
    };

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply()
    {
        var s = Settings.Current;
        IsDark = s.Appearance switch { "dark" => true, "auto" => SystemPrefersDark(), _ => false };
        var acc = Accents.FirstOrDefault(a => a.id == s.Accent);
        if (acc.id == null) acc = Accents[1];
        Accent = IsDark ? acc.dark : acc.light;
        SelectionColor = acc.id is "blue" or "multicolor" ? (IsDark ? C("#0059D1") : C("#0063E1")) : Darken(Accent, IsDark ? 0.12 : 0.08);

        var r = Application.Current.Resources;
        r["MacFont"] = Font;
        r["MacDisplayFont"] = DisplayFont;
        r["MacMonoFont"] = MonoFont;
        r["AccentColor"] = Accent;
        r["AccentBrush"] = B(Accent);
        r["AccentSoftBrush"] = B(Color.FromArgb(IsDark ? (byte)0x55 : (byte)0x40, Accent.R, Accent.G, Accent.B));
        r["FocusRingBrush"] = B(Color.FromArgb(0x80, Accent.R, Accent.G, Accent.B));
        r["SelectionBrush"] = B(SelectionColor);
        r["SelectionTextBrush"] = B("#FFFFFF");
        r["TextSelectionBrush"] = B(IsDark ? "#3F638B" : "#B3D7FF");

        if (!IsDark)
        {
            r["LabelBrush"] = B("#D9000000");
            r["SecondaryLabelBrush"] = B("#80000000");
            r["TertiaryLabelBrush"] = B("#42000000");
            r["QuaternaryLabelBrush"] = B("#1A000000");
            r["SeparatorBrush"] = B("#1A000000");
            r["StrongSeparatorBrush"] = B("#26000000");
            r["WindowBackgroundBrush"] = B("#ECECEC");
            r["ContentBackgroundBrush"] = B("#FFFFFF");
            r["AlternateRowBrush"] = B("#F4F5F5");
            r["ToolbarBackgroundBrush"] = B("#F6F6F6");
            r["SidebarBackgroundBrush"] = B("#E9E7E9");
            r["SidebarInactiveBrush"] = B("#EEEDEE");
            r["SidebarTintBrush"] = B("#8CF0EEF0");
            r["SidebarSelectionBrush"] = B("#1A000000");
            r["SidebarSelectionActiveBrush"] = B("#24000000");
            r["UnfocusedSelectionBrush"] = B("#DCDCDC");
            r["HoverBrush"] = B("#0F000000");
            r["PressedBrush"] = B("#1F000000");
            r["ControlBrush"] = B("#FFFFFF");
            r["ControlBorderBrush"] = B("#33000000");
            r["ControlBottomBorderBrush"] = B("#40000000");
            r["TextFieldBrush"] = B("#FFFFFF");
            r["SearchFieldBrush"] = B("#0D000000");
            r["MenuBackgroundBrush"] = B("#F2F1F1F1");
            r["MenuBorderBrush"] = B("#2E000000");
            r["MenuInnerBorderBrush"] = B("#00FFFFFF");
            r["MenuTextBrush"] = B("#DD000000");
            r["MenuDisabledTextBrush"] = B("#40000000");
            r["MenuShortcutBrush"] = B("#73000000");
            r["MenuSeparatorBrush"] = B("#1A000000");
            r["ScrollThumbBrush"] = B("#73000000");
            r["ScrollTrackBrush"] = B("#0A000000");
            r["TooltipBackgroundBrush"] = B("#F5F5F5");
            r["PopoverBackgroundBrush"] = B("#F7F6F6F6");
            r["GroupBoxBrush"] = B("#08000000");
            r["GroupBoxBorderBrush"] = B("#0F000000");
            r["SettingsPaneBrush"] = B("#F5F5F5");
            r["SwitchOffBrush"] = B("#1F000000");
            r["SliderTrackBrush"] = B("#1F000000");
            r["ShadowColor"] = C("#000000");
            r["TitleTextBrush"] = B("#D9000000");
            r["IconTintBrush"] = B(Accent);
            r["DisabledIconBrush"] = B("#33000000");
            r["FolderLabelBrush"] = B("#D9000000");
            r["QuickLookBackgroundBrush"] = B("#F0F0F0F0");
        }
        else
        {
            r["LabelBrush"] = B("#D9FFFFFF");
            r["SecondaryLabelBrush"] = B("#8CFFFFFF");
            r["TertiaryLabelBrush"] = B("#40FFFFFF");
            r["QuaternaryLabelBrush"] = B("#1AFFFFFF");
            r["SeparatorBrush"] = B("#1AFFFFFF");
            r["StrongSeparatorBrush"] = B("#99000000");
            r["WindowBackgroundBrush"] = B("#323232");
            r["ContentBackgroundBrush"] = B("#1E1E1E");
            r["AlternateRowBrush"] = B("#0DFFFFFF");
            r["ToolbarBackgroundBrush"] = B("#2B2B2B");
            r["SidebarBackgroundBrush"] = B("#2A2828");
            r["SidebarInactiveBrush"] = B("#2B2B2B");
            r["SidebarTintBrush"] = B("#8C2A2828");
            r["SidebarSelectionBrush"] = B("#1AFFFFFF");
            r["SidebarSelectionActiveBrush"] = B("#26FFFFFF");
            r["UnfocusedSelectionBrush"] = B("#464646");
            r["HoverBrush"] = B("#14FFFFFF");
            r["PressedBrush"] = B("#26FFFFFF");
            r["ControlBrush"] = B("#5A5A5A");
            r["ControlBorderBrush"] = B("#1AFFFFFF");
            r["ControlBottomBorderBrush"] = B("#1AFFFFFF");
            r["TextFieldBrush"] = B("#0DFFFFFF");
            r["SearchFieldBrush"] = B("#1AFFFFFF");
            r["MenuBackgroundBrush"] = B("#F2282828");
            r["MenuBorderBrush"] = B("#CC000000");
            r["MenuInnerBorderBrush"] = B("#26FFFFFF");
            r["MenuTextBrush"] = B("#E6FFFFFF");
            r["MenuDisabledTextBrush"] = B("#40FFFFFF");
            r["MenuShortcutBrush"] = B("#80FFFFFF");
            r["MenuSeparatorBrush"] = B("#1AFFFFFF");
            r["ScrollThumbBrush"] = B("#80FFFFFF");
            r["ScrollTrackBrush"] = B("#0AFFFFFF");
            r["TooltipBackgroundBrush"] = B("#3A3A3A");
            r["PopoverBackgroundBrush"] = B("#F7323232");
            r["GroupBoxBrush"] = B("#0AFFFFFF");
            r["GroupBoxBorderBrush"] = B("#0FFFFFFF");
            r["SettingsPaneBrush"] = B("#1E1E1E");
            r["SwitchOffBrush"] = B("#26FFFFFF");
            r["SliderTrackBrush"] = B("#26FFFFFF");
            r["ShadowColor"] = C("#000000");
            r["TitleTextBrush"] = B("#D9FFFFFF");
            r["IconTintBrush"] = B(Accent);
            r["DisabledIconBrush"] = B("#33FFFFFF");
            r["FolderLabelBrush"] = B("#D9FFFFFF");
            r["QuickLookBackgroundBrush"] = B("#F0282828");
        }
        Changed?.Invoke();
    }

    public static Color Darken(Color c, double amt) =>
        Color.FromRgb((byte)(c.R * (1 - amt)), (byte)(c.G * (1 - amt)), (byte)(c.B * (1 - amt)));

    public static Brush Res(string key) => Application.Current.Resources[key] as Brush;

    /// <summary>Finder tag colours (label colours).</summary>
    public static readonly (string id, string name, Color color)[] TagColors =
    {
        ("red", "Red", C("#FF5E57")),
        ("orange", "Orange", C("#FFA033")),
        ("yellow", "Yellow", C("#FFD932")),
        ("green", "Green", C("#5BD35B")),
        ("blue", "Blue", C("#3B99FC")),
        ("purple", "Purple", C("#C27CE5")),
        ("gray", "Gray", C("#A4A4A8")),
    };
}
