using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailKit.Security;
using MacShell.Services;

namespace MacShell.Apps.Mail;

public enum MailProvider { ICloud, Gmail, Test }

/// <summary>
/// A mail account: iCloud or Gmail, signed in with an app-specific password (iCloud only takes those; Gmail's own
/// sign-in would need a Google developer project, whose sign-ins expire weekly). The password is kept encrypted for
/// the Windows user (DPAPI) and never shown again.
/// </summary>
public sealed class MailAccount
{
    public string Id { get; set; }
    public MailProvider Provider { get; set; }
    public string Email { get; set; }
    /// <summary>The name on mail sent from it.</summary>
    public string Name { get; set; }
    /// <summary>The app password, encrypted for this Windows user (base64).</summary>
    public string Secret { get; set; }
    /// <summary>Diagnostics only (provider Test): a mail server on this computer, without TLS.</summary>
    public int TestImapPort { get; set; }
    public int TestSmtpPort { get; set; }

    [JsonIgnore] public string Title => Provider switch { MailProvider.ICloud => "iCloud", MailProvider.Gmail => "Gmail", _ => "Test" };

    [JsonIgnore]
    public string Password
    {
        get
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Secret ?? ""), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => Secret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value ?? ""), Entropy, DataProtectionScope.CurrentUser));
    }
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MacShell.Mail");

    [JsonIgnore]
    public (string host, int port, SecureSocketOptions tls) Imap => Provider switch
    {
        MailProvider.ICloud => ("imap.mail.me.com", 993, SecureSocketOptions.SslOnConnect),
        MailProvider.Gmail => ("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect),
        _ => ("127.0.0.1", TestImapPort, SecureSocketOptions.None),
    };

    [JsonIgnore]
    public (string host, int port, SecureSocketOptions tls) Smtp => Provider switch
    {
        MailProvider.ICloud => ("smtp.mail.me.com", 587, SecureSocketOptions.StartTls),
        MailProvider.Gmail => ("smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect),
        _ => ("127.0.0.1", TestSmtpPort, SecureSocketOptions.None),
    };

    /// <summary>Gmail keeps a copy of what's sent by itself; iCloud's Sent mailbox gets one from the app.</summary>
    [JsonIgnore] public bool ServerSavesSent => Provider == MailProvider.Gmail;
}

/// <summary>A mailbox on the server.</summary>
public sealed class MailFolderInfo
{
    public string FullName { get; set; }
    public string Name { get; set; }
    /// <summary>inbox | drafts | sent | junk | trash | archive | all | flagged | null (a mailbox of the user's own).</summary>
    public string Role { get; set; }
    public uint UidValidity { get; set; }
    public int Unread { get; set; }
    public int Total { get; set; }
    /// <summary>Older messages exist on the server than the ones kept here ("Load more").</summary>
    public bool HasOlder { get; set; }
    /// <summary>What the server says of it (\Sent, \Trash …), for diagnostics.</summary>
    [JsonIgnore] public string ServerFlags { get; set; }
}

/// <summary>One message as the list shows it (its whole text is fetched when it's opened, and kept).</summary>
public sealed class MailMessageInfo
{
    public string AccountId { get; set; }
    public string Folder { get; set; }
    public uint Uid { get; set; }
    public string MessageId { get; set; }
    public string Subject { get; set; }
    public string FromName { get; set; }
    public string FromAddress { get; set; }
    public string To { get; set; }
    public string Cc { get; set; }
    public DateTimeOffset Date { get; set; }
    public string Preview { get; set; }
    public bool Seen { get; set; }
    public bool Flagged { get; set; }
    public bool Answered { get; set; }
    public bool Draft { get; set; }
    public bool HasAttachments { get; set; }
    public long Size { get; set; }
    public string InReplyTo { get; set; }
    public string References { get; set; }

    [JsonIgnore] public string Key => $"{AccountId}|{Folder}|{Uid}";
    [JsonIgnore] public string Sender => string.IsNullOrWhiteSpace(FromName) ? (FromAddress ?? "") : FromName;
}

/// <summary>
/// What Mail keeps on this computer, in %APPDATA%\MacShell\Mail: the accounts, and per account its mailboxes, the
/// messages listed in each (newest first) and the whole messages already opened (.eml).
/// </summary>
public static class MailStore
{
    public static readonly string Root = Path.Combine(Settings.DataDirectory, "Mail");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static List<MailAccount> LoadAccounts()
    {
        try
        {
            string f = Path.Combine(Root, "accounts.json");
            return File.Exists(f) ? JsonSerializer.Deserialize<List<MailAccount>>(File.ReadAllText(f)) ?? new() : new();
        }
        catch { return new(); }
    }

    public static void SaveAccounts(List<MailAccount> accounts) =>
        WriteAtomic(Path.Combine(Root, "accounts.json"), JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true }));

    static string AccountDir(string accountId) => Path.Combine(Root, accountId);

    /// <summary>A mailbox's folder on disk: its name made safe for a file name, plus a hash (names can differ only in case).</summary>
    public static string FolderDir(string accountId, string fullName)
    {
        string safe = new string(fullName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '[' || c == ']' ? '_' : c).ToArray());
        if (safe.Length > 40) safe = safe[..40];
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(fullName)))[..8];
        return Path.Combine(AccountDir(accountId), safe + "-" + hash);
    }

    public static List<MailFolderInfo> LoadFolders(string accountId) =>
        Read<List<MailFolderInfo>>(Path.Combine(AccountDir(accountId), "folders.json")) ?? new();

    public static void SaveFolders(string accountId, List<MailFolderInfo> folders) =>
        Write(Path.Combine(AccountDir(accountId), "folders.json"), folders);

    public static List<MailMessageInfo> LoadMessages(string accountId, string folder) =>
        Read<List<MailMessageInfo>>(Path.Combine(FolderDir(accountId, folder), "messages.json")) ?? new();

    public static void SaveMessages(string accountId, string folder, List<MailMessageInfo> messages) =>
        Write(Path.Combine(FolderDir(accountId, folder), "messages.json"), messages);

    public static string MessageFile(MailMessageInfo m) => Path.Combine(FolderDir(m.AccountId, m.Folder), m.Uid + ".eml");

    public static void ForgetFolder(string accountId, string folder)
    {
        try { Directory.Delete(FolderDir(accountId, folder), true); } catch { }
    }

    public static void ForgetAccount(string accountId)
    {
        try { Directory.Delete(AccountDir(accountId), true); } catch { }
    }

    static T Read<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : null; }
        catch { return null; }
    }

    static void Write<T>(string file, T value) => WriteAtomic(file, JsonSerializer.Serialize(value, Json));

    /// <summary>Written beside, then swapped in: a crash never leaves half a file.</summary>
    static void WriteAtomic(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, file, overwrite: true);
        }
        catch { }
    }
}
