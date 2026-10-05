using System.Runtime.InteropServices;
using static MacShell.Native.NativeMethods;

namespace MacShell.Native;

/// <summary>Reads a classic Win32 app's HMENU so it can be mirrored in the global menu bar.</summary>
public class NativeMenuItem
{
    public string Text;
    public string Shortcut;
    public uint Id;
    public bool Separator, Enabled = true, Checked;
    public IntPtr SubMenu;
}

public static class NativeMenu
{
    const uint SMTO_ABORTIFHUNG = 0x2;

    public static List<NativeMenuItem> Read(IntPtr hmenu, IntPtr owner, int indexForInit = -1)
    {
        var list = new List<NativeMenuItem>();
        if (hmenu == IntPtr.Zero) return list;
        if (indexForInit >= 0)
            SendMessageTimeout(owner, WM_INITMENUPOPUP, hmenu, new IntPtr(indexForInit), SMTO_ABORTIFHUNG, 150, out _);
        int n = GetMenuItemCount(hmenu);
        for (uint i = 0; i < n && i < 200; i++)
        {
            var mii = new MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_STRING | MIIM_ID | MIIM_SUBMENU | MIIM_FTYPE | MIIM_STATE,
            };
            if (!GetMenuItemInfo(hmenu, i, true, ref mii)) continue;
            var item = new NativeMenuItem
            {
                Id = mii.wID,
                SubMenu = mii.hSubMenu,
                Separator = (mii.fType & MFT_SEPARATOR) != 0,
                Enabled = (mii.fState & MFS_DISABLED) == 0,
                Checked = (mii.fState & MFS_CHECKED) != 0,
            };
            if (!item.Separator && (mii.fType & (MFT_BITMAP | MFT_OWNERDRAW)) == 0 && mii.cch > 0)
            {
                uint cch = mii.cch + 1;
                IntPtr buf = Marshal.AllocHGlobal((int)cch * 2);
                try
                {
                    mii.fMask = MIIM_STRING;
                    mii.dwTypeData = buf;
                    mii.cch = cch;
                    if (GetMenuItemInfo(hmenu, i, true, ref mii))
                    {
                        string raw = Marshal.PtrToStringUni(buf) ?? "";
                        int tab = raw.IndexOf('\t');
                        string text = tab >= 0 ? raw[..tab] : raw;
                        item.Shortcut = tab >= 0 ? MacGlyphs(raw[(tab + 1)..]) : null;
                        item.Text = StripMnemonic(text);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            if (!item.Separator && string.IsNullOrWhiteSpace(item.Text)) continue;
            list.Add(item);
        }
        // collapse duplicate / leading / trailing separators
        var clean = new List<NativeMenuItem>();
        foreach (var it in list)
        {
            if (it.Separator && (clean.Count == 0 || clean[^1].Separator)) continue;
            clean.Add(it);
        }
        while (clean.Count > 0 && clean[^1].Separator) clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    static string StripMnemonic(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '&')
            {
                if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>"Ctrl+Shift+S" → "⇧⌘S" (MacShell maps ⌘ to Ctrl).</summary>
    public static string MacGlyphs(string accel)
    {
        if (string.IsNullOrWhiteSpace(accel)) return null;
        var parts = accel.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return accel;
        string key = parts[^1];
        bool ctrl = false, shift = false, alt = false;
        foreach (var p in parts.Take(parts.Count - 1))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "strg": case "control": ctrl = true; break;
                case "shift": case "umschalt": shift = true; break;
                case "alt": alt = true; break;
            }
        }
        key = key switch
        {
            "Del" or "Delete" or "Entf" => "⌦",
            "Backspace" => "⌫",
            "Enter" or "Return" => "↩",
            "Esc" or "Escape" => "⎋",
            "Tab" => "⇥",
            "Up" => "↑", "Down" => "↓", "Left" => "←", "Right" => "→",
            "PgUp" or "PageUp" => "⇞", "PgDn" or "PageDown" => "⇟",
            "Home" => "↖", "End" => "↘",
            "Space" => "Space",
            _ => key.Length == 1 ? key.ToUpperInvariant() : key,
        };
        return (alt ? "⌥" : "") + (shift ? "⇧" : "") + (ctrl ? "⌘" : "") + key;
    }
}
