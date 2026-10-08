using System.Runtime.InteropServices;
using System.Windows;
using MimeKit;
using MacShell.Services;

namespace MacShell.Apps.Mail;

/// <summary>A mailbox in the sidebar: one account's (Folder), or every account's of a role (AccountId null: All Inboxes …).</summary>
public sealed record Mailbox(string AccountId, string Folder, string Role, string Title);

/// <summary>
/// Mail's accounts and their syncs. It runs from the moment MacShell starts (when there are accounts), so the Dock
/// icon's badge shows the unread mail in the inboxes and new mail is announced with a banner, also while Mail's window
/// is closed.
/// </summary>
public static class MailService
{
    public static List<MailAccount> Accounts { get; private set; } = new();
    static readonly Dictionary<string, MailSync> Syncs = new();
    /// <summary>Anything changed: mailboxes, messages, counts (UI thread).</summary>
    public static event Action Changed;
    /// <summary>New unread mail (UI thread).</summary>
    public static event Action<List<MailMessageInfo>> NewMail;
    static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Accounts = MailStore.LoadAccounts();
        foreach (var a in Accounts) Begin(a);
        Changed += () => Badges.Refresh();
        // after sleep, or when the network comes back, push's connections are dead: made again at once, and mail checked
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) Revive(); };
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) Revive(); };
    }

    static void Revive()
    {
        foreach (var s in Syncs.Values.ToList()) { s.KickPush(); s.Wake(); }
    }

    static void Begin(MailAccount a)
    {
        var s = new MailSync(a);
        s.Changed += () => Changed?.Invoke();
        s.NewMail += list => { NewMail?.Invoke(list); Announce(list); };
        Syncs[a.Id] = s;
        s.Start();
    }

    public static MailSync Sync(string accountId) => accountId != null && Syncs.TryGetValue(accountId, out var s) ? s : null;
    public static IEnumerable<MailSync> All => Accounts.Select(a => Sync(a.Id)).Where(s => s != null);

    public static void AddAccount(MailAccount a)
    {
        Start();
        Accounts.Add(a);
        MailStore.SaveAccounts(Accounts);
        Begin(a);
        Changed?.Invoke();
    }

    public static void RemoveAccount(MailAccount a)
    {
        if (Syncs.Remove(a.Id, out var s)) s.Stop();
        Accounts.RemoveAll(x => x.Id == a.Id);
        MailStore.SaveAccounts(Accounts);
        MailStore.ForgetAccount(a.Id);
        Changed?.Invoke();
    }

    public static void GetMail() { foreach (var s in All) s.Wake(); }

    /// <summary>Unread mail in every account's Inbox (the Dock badge).</summary>
    public static int UnreadInboxes => All.Sum(s => s.FolderByRole("inbox")?.Unread ?? 0);

    // ------------------------------------------------------------------ what a mailbox shows

    public static List<MailMessageInfo> Messages(Mailbox box)
    {
        if (box == null) return new();
        if (box.AccountId != null)
            return Sync(box.AccountId)?.Messages(box.Folder) ?? new();
        IEnumerable<MailMessageInfo> list;
        if (box.Role == "flagged")
            list = All.SelectMany(s => s.Folders.Where(f => f.Role is not ("trash" or "junk" or "all")).SelectMany(f => s.Messages(f.FullName))).Where(m => m.Flagged);
        else
            list = All.SelectMany(s => s.FolderByRole(box.Role) is { } f ? s.Messages(f.FullName) : Enumerable.Empty<MailMessageInfo>());
        return list.OrderByDescending(m => m.Date).ToList();
    }

    public static (int total, int unread) Counts(Mailbox box)
    {
        if (box == null) return (0, 0);
        if (box.AccountId != null)
        {
            var f = Sync(box.AccountId)?.Folder(box.Folder);
            return (f?.Total ?? 0, f?.Unread ?? 0);
        }
        if (box.Role == "flagged") { var l = Messages(box); return (l.Count, l.Count(m => !m.Seen)); }
        var fs = All.Select(s => s.FolderByRole(box.Role)).Where(f => f != null).ToList();
        return (fs.Sum(f => f.Total), fs.Sum(f => f.Unread));
    }

    public static MailAccount AccountOf(MailMessageInfo m) => Accounts.FirstOrDefault(a => a.Id == m?.AccountId);

    // ------------------------------------------------------------------ new mail

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(string file, IntPtr module, uint flags);

    static void Announce(List<MailMessageInfo> fresh)
    {
        if (!Settings.Current.MailNotify) return;
        try
        {
            string snd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Email.wav");
            if (File.Exists(snd)) PlaySound(snd, IntPtr.Zero, 0x0001 | 0x0002 | 0x00020000);   // ASYNC | NODEFAULT | FILENAME
        }
        catch { }
        MailBanner.Show(fresh);
    }

    // ------------------------------------------------------------------ replying, forwarding

    public static string Quote(string text) =>
        string.Join("\n", (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.StartsWith(">") ? ">" + l : "> " + l));

    /// <summary>The message's text for quoting or forwarding (its plain text, or its HTML made plain).</summary>
    public static string PlainText(MimeMessage msg)
    {
        if (msg == null) return "";
        if (!string.IsNullOrWhiteSpace(msg.TextBody)) return msg.TextBody.TrimEnd();
        string html = msg.HtmlBody ?? "";
        html = System.Text.RegularExpressions.Regex.Replace(html, @"(?is)<(script|style|head)[^>]*>.*?</\1>", "");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"(?i)<br\s*/?>|</p>|</div>|</tr>|</li>|</h\d>", "\n");
        html = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        return System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n").Trim();
    }
}
