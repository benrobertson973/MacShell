using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace MacShell.Native;

/// <summary>The computer's user accounts (netapi32): names, pictures, administrators, Microsoft accounts.</summary>
public static class UserAccounts
{
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserEnum(string server, int level, int filter, out IntPtr buf, int prefMax, out int read, out int total, ref int resume);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetInfo(string server, string user, int level, out IntPtr buf);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetLocalGroups(string server, string user, int level, int flags, out IntPtr buf, int prefMax, out int read, out int total);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buf);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    public record Account(string Name, string FullName, bool Admin, bool Enabled, bool MicrosoftAccount, string Email, string Sid, bool IsCurrent);

    static string AdminGroupName()
    {
        try { return new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount)).Value.Split('\\').Last(); }
        catch { return "Administrators"; }
    }

    /// <summary>The accounts people sign in with (enabled ones first; Windows' own hidden accounts left out).</summary>
    public static List<Account> All()
    {
        var list = new List<Account>();
        try
        {
            int resume = 0;
            if (NetUserEnum(null, 0, 2 /*FILTER_NORMAL_ACCOUNT*/, out IntPtr buf, -1, out int read, out _, ref resume) != 0) return list;
            var names = new List<string>();
            try { for (int i = 0; i < read; i++) names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * IntPtr.Size))); }
            finally { NetApiBufferFree(buf); }
            string adminGroup = AdminGroupName();
            string me = Environment.UserName;
            foreach (var n in names)
            {
                if (n is "DefaultAccount" or "WDAGUtilityAccount" or "defaultuser0") continue;
                var a = Get(n, adminGroup);
                if (a == null) continue;
                if (!a.Enabled && n is "Administrator" or "Guest") continue;
                list.Add(a with { IsCurrent = string.Equals(n, me, StringComparison.OrdinalIgnoreCase) });
            }
        }
        catch { }
        return list.OrderByDescending(a => a.IsCurrent).ThenByDescending(a => a.Enabled).ThenBy(a => a.FullName ?? a.Name).ToList();
    }

    static Account Get(string user, string adminGroup)
    {
        string full = null, sid = null, email = null;
        bool enabled = true, msa = false;
        // USER_INFO_23: name, full name, comment, flags, sid
        if (NetUserGetInfo(null, user, 23, out IntPtr b23) == 0)
        {
            try
            {
                full = Marshal.PtrToStringUni(Marshal.ReadIntPtr(b23 + IntPtr.Size));
                int flags = Marshal.ReadInt32(b23 + IntPtr.Size * 3);
                enabled = (flags & 0x2) == 0;   // UF_ACCOUNTDISABLE
                IntPtr psid = Marshal.ReadIntPtr(b23 + IntPtr.Size * 4);
                if (psid != IntPtr.Zero && ConvertSidToStringSid(psid, out IntPtr s)) { sid = Marshal.PtrToStringUni(s); LocalFree(s); }
            }
            finally { NetApiBufferFree(b23); }
        }
        // USER_INFO_24: a Microsoft account (internet identity) and its address
        if (NetUserGetInfo(null, user, 24, out IntPtr b24) == 0)
        {
            try
            {
                msa = Marshal.ReadInt32(b24) != 0;
                if (msa) email = Marshal.PtrToStringUni(Marshal.ReadIntPtr(b24 + 16));
            }
            finally { NetApiBufferFree(b24); }
        }
        bool admin = false;
        if (NetUserGetLocalGroups(null, user, 0, 1 /*LG_INCLUDE_INDIRECT*/, out IntPtr gb, -1, out int gread, out _) == 0)
        {
            try
            {
                for (int i = 0; i < gread; i++)
                    if (string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(gb + i * IntPtr.Size)), adminGroup, StringComparison.OrdinalIgnoreCase)) admin = true;
            }
            finally { NetApiBufferFree(gb); }
        }
        return new Account(user, string.IsNullOrWhiteSpace(full) ? null : full, admin, enabled, msa, email, sid, false);
    }

    static Account _current;
    static Account Current => _current ??= Get(Environment.UserName, AdminGroupName()) ?? new Account(Environment.UserName, null, false, true, false, null, null, true);

    public static string CurrentFullName => Current.FullName;
    public static bool CurrentIsMicrosoftAccount => Current.MicrosoftAccount;
    public static string CurrentEmail => Current.Email;
    public static bool CurrentIsAdmin => Current.Admin;

    /// <summary>A user's account picture (the file Windows keeps at about this size), or null.</summary>
    public static string Picture(string sid, int size)
    {
        if (sid == null) return null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            if (k == null) return null;
            int[] sizes = { 32, 40, 48, 64, 96, 192, 208, 240, 424, 448, 1080 };
            foreach (int s in sizes.OrderBy(s => s < size ? 10000 + size - s : s - size))
                if (k.GetValue("Image" + s) is string p && File.Exists(p)) return p;
        }
        catch { }
        return null;
    }

    public static string CurrentPicture(int size)
    {
        try { return Picture(WindowsIdentity.GetCurrent().User?.Value, size); } catch { return null; }
    }
}
