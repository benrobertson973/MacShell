using System.IO;
using System.Net.Sockets;
using System.Windows;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MacShell.Apps.Mail;

/// <summary>
/// One account's connection to its server. Every use of the IMAP connection waits its turn. The mailboxes and their
/// newest messages are kept in step with the server (every minute, and on Get Mail); a whole message is fetched when
/// it's opened, and kept; flags, moves and deletes are made on the server; mail is sent over SMTP.
/// </summary>
public sealed class MailSync
{
    public MailAccount Account { get; }
    public List<MailFolderInfo> Folders { get; private set; }
    /// <summary>Why the last sync failed (shown beside the account); null when it worked.</summary>
    public string Error { get; private set; }
    public bool Busy { get; private set; }
    /// <summary>The mailbox the user is looking at: kept in step every minute, like the Inbox.</summary>
    public string Watching { get; set; }
    /// <summary>Anything changed (raised on the UI thread).</summary>
    public event Action Changed;
    /// <summary>New unread mail arrived in the Inbox (not on the first sync).</summary>
    public event Action<List<MailMessageInfo>> NewMail;

    const int Batch = 200;   // messages fetched at first per mailbox, and per "load more"
    readonly Dictionary<string, List<MailMessageInfo>> _messages = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly SemaphoreSlim _wake = new(0, 1);
    CancellationTokenSource _stop;
    ImapClient _imap;
    DateTime _foldersAt;
    bool _synced;

    public MailSync(MailAccount account)
    {
        Account = account;
        Folders = MailStore.LoadFolders(account.Id);
    }

    public MailFolderInfo Folder(string fullName) => Folders.FirstOrDefault(f => f.FullName == fullName);
    public MailFolderInfo FolderByRole(string role) => Folders.FirstOrDefault(f => f.Role == role);

    /// <summary>A mailbox's messages, newest first (a list that's replaced, never changed, when the server changes it).</summary>
    public List<MailMessageInfo> Messages(string folder)
    {
        lock (_messages)
        {
            if (!_messages.TryGetValue(folder, out var list)) _messages[folder] = list = MailStore.LoadMessages(Account.Id, folder);
            return list;
        }
    }

    void SetMessages(string folder, List<MailMessageInfo> list)
    {
        lock (_messages) _messages[folder] = list;
        MailStore.SaveMessages(Account.Id, folder, list);
    }

    // ------------------------------------------------------------------ the loop

    public void Start()
    {
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        Task.Run(async () =>
        {
            var lastFull = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                // pushed (new mail, a change in the Inbox): just the Inbox, at once; else everything, every minute or two
                bool quick = _quick && DateTime.UtcNow - lastFull < TimeSpan.FromMinutes(2);
                _quick = false;
                try
                {
                    await SyncAllAsync(ct, inboxOnly: quick);
                    if (!quick) lastFull = DateTime.UtcNow;
                }
                catch (OperationCanceledException) { break; }
                catch { }
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(Pushing ? 120 : 60), ct); } catch (OperationCanceledException) { break; }
            }
        });
        Task.Run(() => PushLoopAsync(ct));
    }

    public void Stop()
    {
        _stop?.Cancel();
        try { _imap?.Dispose(); } catch { }
        try { _openImap?.Dispose(); } catch { }
    }

    volatile bool _quick;

    /// <summary>Get Mail: sync now instead of at the next minute (<paramref name="inboxOnly"/>: just the Inbox - what push asks for).</summary>
    public void Wake(bool inboxOnly = false)
    {
        if (inboxOnly) _quick = true;
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { }
    }

    void Raise() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    // ------------------------------------------------------------------ push (IMAP IDLE)

    /// <summary>The Inbox is being watched over its own connection: new mail comes the moment it arrives.</summary>
    public bool Pushing { get; private set; }
    CancellationTokenSource _kick;

    /// <summary>The watch started over (after the computer slept, or the network came back: the old connection is dead).</summary>
    public void KickPush()
    {
        try { _kick?.Cancel(); } catch { }
    }

    /// <summary>
    /// Push: a second connection rests in the Inbox (IMAP IDLE) - the server tells it at once when mail arrives, is
    /// deleted or changes - and wakes the sync to fetch it. The IDLE is renewed every 9 minutes (servers drop quiet
    /// connections); a lost connection is made again, sooner after a kick. A server without IDLE is asked every 30 s.
    /// </summary>
    async Task PushLoopAsync(CancellationToken ct)
    {
        int backoff = 5;
        while (!ct.IsCancellationRequested)
        {
            ImapClient c = null;
            var kick = _kick = new CancellationTokenSource();
            try
            {
                c = await ConnectAsync(Account, ct);
                var inbox = c.Inbox;
                inbox.CountChanged += (_, _) => Wake(inboxOnly: true);
                inbox.MessageExpunged += (_, _) => Wake(inboxOnly: true);
                inbox.MessageFlagsChanged += (_, _) => Wake(inboxOnly: true);
                await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                bool idle = c.Capabilities.HasFlag(ImapCapabilities.Idle);
                Pushing = idle;
                backoff = 5;
                while (!ct.IsCancellationRequested && !kick.IsCancellationRequested && c.IsConnected)
                {
                    if (idle)
                    {
                        using var done = CancellationTokenSource.CreateLinkedTokenSource(kick.Token);
                        done.CancelAfter(TimeSpan.FromMinutes(9));
                        await c.IdleAsync(done.Token, ct);
                    }
                    else
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, kick.Token);
                        try { await Task.Delay(TimeSpan.FromSeconds(30), wait.Token); } catch (OperationCanceledException) { break; }
                        await c.NoOpAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { }
            finally
            {
                Pushing = false;
                try { c?.Dispose(); } catch { }
            }
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(kick.IsCancellationRequested ? 1 : backoff), ct); } catch (OperationCanceledException) { break; }
            if (!kick.IsCancellationRequested) backoff = Math.Min(backoff * 2, 120);
        }
    }

    async Task SyncAllAsync(CancellationToken ct, bool inboxOnly = false)
    {
        await _gate.WaitAsync(ct);
        Busy = true;
        Raise();
        try
        {
            var c = await ImapAsync(ct);
            if (Folders.Count == 0 || (!inboxOnly && DateTime.UtcNow - _foldersAt > TimeSpan.FromMinutes(10)))
            {
                await RefreshFoldersAsync(c, ct);
                _foldersAt = DateTime.UtcNow;
            }
            var inbox = FolderByRole("inbox");
            var before = inbox != null ? Messages(inbox.FullName).Select(m => m.Uid).ToHashSet() : new HashSet<uint>();
            var targets = Folders.Where(f => inboxOnly ? f.Role == "inbox" : f.Role is "inbox" or "drafts" or "sent").Select(f => f.FullName).ToList();
            if (!inboxOnly && Watching != null && Folder(Watching) != null && !targets.Contains(Watching)) targets.Add(Watching);
            foreach (var t in targets) await SyncFolderLockedAsync(c, t, false, ct);
            // the other mailboxes: how many unread
            if (!inboxOnly)
                foreach (var f in Folders.Where(f => !targets.Contains(f.FullName)))
                {
                    try
                    {
                        var mf = await c.GetFolderAsync(f.FullName, ct);
                        await mf.StatusAsync(StatusItems.Unread | StatusItems.Count, ct);
                        f.Unread = mf.Unread;
                        f.Total = mf.Count;
                    }
                    catch (ImapCommandException) { }
                }
            MailStore.SaveFolders(Account.Id, Folders);
            if (inbox != null && _synced)
            {
                // (new: after the newest there was - not an old unread one fetched out of turn)
                uint top = before.Count > 0 ? before.Max() : 0;
                var fresh = Messages(inbox.FullName).Where(m => m.Uid > top && !m.Seen).ToList();
                if (fresh.Count > 0) Application.Current?.Dispatcher.BeginInvoke(() => NewMail?.Invoke(fresh));
            }
            _synced = true;
            Error = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            Busy = false;
            _gate.Release();
            Raise();
        }
    }

    void Fail(Exception ex)
    {
        Error = Describe(ex);
        try { _imap?.Dispose(); } catch { }
        _imap = null;
    }

    public static string Describe(Exception ex) => ex switch
    {
        AuthenticationException => "The password wasn’t accepted. Use an app-specific password.",
        SocketException or IOException or SslHandshakeException => "Can’t reach the mail server. Are you online?",
        TimeoutException => "The mail server didn’t answer.",
        _ => ex.Message,
    };

    // ------------------------------------------------------------------ the connection

    async Task<ImapClient> ImapAsync(CancellationToken ct)
    {
        if (_imap is { IsConnected: true, IsAuthenticated: true }) return _imap;
        try { _imap?.Dispose(); } catch { }
        _imap = null;
        _imap = await ConnectAsync(Account, ct);
        return _imap;
    }

    static async Task<ImapClient> ConnectAsync(MailAccount a, CancellationToken ct)
    {
        var c = new ImapClient { Timeout = 60_000 };
        var (host, port, tls) = a.Imap;
        await c.ConnectAsync(host, port, tls, ct);
        try { await c.AuthenticateAsync(a.Email, a.Password, ct); }
        catch (AuthenticationException) when (a.Provider == MailProvider.ICloud && a.Email.Contains('@'))
        {
            // (iCloud also knows an account by the name before the @)
            await c.AuthenticateAsync(a.Email[..a.Email.IndexOf('@')], a.Password, ct);
        }
        return c;
    }

    /// <summary>Signing in for the account sheet: null when IMAP and SMTP both take the password, else why not.</summary>
    public static async Task<string> CheckAsync(MailAccount a, CancellationToken ct)
    {
        try
        {
            using (var c = await ConnectAsync(a, ct)) await c.DisconnectAsync(true, ct);
            using var smtp = new SmtpClient { Timeout = 60_000 };
            var (host, port, tls) = a.Smtp;
            await smtp.ConnectAsync(host, port, tls, ct);
            if (smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication)) await smtp.AuthenticateAsync(a.Email, a.Password, ct);
            await smtp.DisconnectAsync(true, ct);
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Runs one change on the server in turn with the sync; when it fails, the mailboxes are synced again.</summary>
    async Task RunAsync(Func<ImapClient, CancellationToken, Task> work)
    {
        var ct = _stop?.Token ?? CancellationToken.None;
        await _gate.WaitAsync(ct);
        try
        {
            var c = await ImapAsync(ct);
            await work(c, ct);
            Error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex); Wake(); }
        finally
        {
            _gate.Release();
            Raise();
        }
    }

    static async Task<IMailFolder> OpenAsync(ImapClient c, string fullName, FolderAccess access, CancellationToken ct)
    {
        var f = await c.GetFolderAsync(fullName, ct);
        if (!f.IsOpen || (access == FolderAccess.ReadWrite && f.Access != FolderAccess.ReadWrite)) await f.OpenAsync(access, ct);
        return f;
    }

    // ------------------------------------------------------------------ mailboxes

    async Task RefreshFoldersAsync(ImapClient c, CancellationToken ct)
    {
        var ns = c.PersonalNamespaces.Count > 0 ? c.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var list = await c.GetFoldersAsync(ns, StatusItems.None, false, ct);
        var inbox = c.Inbox;
        var usable = list.Where(f => (f.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0).ToList();
        // A role once only: first what the server says a mailbox is for (\Sent, \Trash …), then for the roles still
        // missing, the usual names - a mailbox merely called "Deleted Messages" (a Gmail label) is not the Trash.
        var roles = new Dictionary<IMailFolder, string>();
        var taken = new HashSet<string>();
        foreach (var f in usable) if (ServerRole(f, inbox) is { } r && taken.Add(r)) roles[f] = r;
        foreach (var f in usable) if (!roles.ContainsKey(f) && NameRole(f) is { } r && taken.Add(r)) roles[f] = r;
        var result = new List<MailFolderInfo>();
        foreach (var f in usable)
        {
            string role = roles.GetValueOrDefault(f);
            var old = Folder(f.FullName);
            result.Add(new MailFolderInfo
            {
                FullName = f.FullName, Name = role != null ? RoleName(role, Account.Provider) : f.Name, Role = role,
                UidValidity = old?.UidValidity ?? 0, Unread = old?.Unread ?? 0, Total = old?.Total ?? 0, HasOlder = old?.HasOlder ?? false,
                ServerFlags = f.Attributes.ToString(),
            });
        }
        if (!result.Any(r => r.Role == "inbox")) result.Insert(0, new MailFolderInfo { FullName = inbox.FullName, Name = "Inbox", Role = "inbox" });
        string[] order = { "inbox", "drafts", "sent", "junk", "trash", "archive", "all", "flagged" };
        Folders = result.OrderBy(r => r.Role != null ? Array.IndexOf(order, r.Role) : order.Length)
                        .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        MailStore.SaveFolders(Account.Id, Folders);
    }

    /// <summary>What the server says a mailbox is for (IMAP special-use: \Sent, \Trash …; INBOX is always the Inbox).</summary>
    static string ServerRole(IMailFolder f, IMailFolder inbox)
    {
        var a = f.Attributes;
        if (f == inbox || string.Equals(f.FullName, "INBOX", StringComparison.OrdinalIgnoreCase) || a.HasFlag(FolderAttributes.Inbox)) return "inbox";
        if (a.HasFlag(FolderAttributes.Drafts)) return "drafts";
        if (a.HasFlag(FolderAttributes.Sent)) return "sent";
        if (a.HasFlag(FolderAttributes.Junk)) return "junk";
        if (a.HasFlag(FolderAttributes.Trash)) return "trash";
        if (a.HasFlag(FolderAttributes.Archive)) return "archive";
        if (a.HasFlag(FolderAttributes.All)) return "all";
        if (a.HasFlag(FolderAttributes.Flagged)) return "flagged";
        return null;
    }

    /// <summary>A mailbox's role by its name (for servers that don't say).</summary>
    static string NameRole(IMailFolder f)
    {
        return f.Name.ToLowerInvariant() switch
        {
            "drafts" => "drafts",
            "sent" or "sent messages" or "sent mail" or "sent items" => "sent",
            "junk" or "spam" => "junk",
            "trash" or "deleted messages" or "deleted items" or "bin" => "trash",
            "archive" => "archive",
            _ => null,
        };
    }

    static string RoleName(string role, MailProvider p) => role switch
    {
        "inbox" => "Inbox", "drafts" => "Drafts", "sent" => "Sent", "junk" => "Junk", "trash" => "Trash",
        "archive" => "Archive", "all" => "All Mail", "flagged" => p == MailProvider.Gmail ? "Starred" : "Flagged", _ => role,
    };

    // ------------------------------------------------------------------ messages

    /// <summary>A mailbox brought up to date; <paramref name="older"/>: also the batch before the oldest kept (Load More).</summary>
    public async Task SyncFolderAsync(string fullName, bool older = false)
    {
        var ct = _stop?.Token ?? CancellationToken.None;
        await _gate.WaitAsync(ct);
        Busy = true;
        Raise();
        try
        {
            var c = await ImapAsync(ct);
            await SyncFolderLockedAsync(c, fullName, older, ct);
            MailStore.SaveFolders(Account.Id, Folders);
            Error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            Busy = false;
            _gate.Release();
            Raise();
        }
    }

    async Task SyncFolderLockedAsync(ImapClient c, string fullName, bool older, CancellationToken ct)
    {
        var info = Folder(fullName);
        if (info == null) return;
        var took = System.Diagnostics.Stopwatch.StartNew();
        var folder = await c.GetFolderAsync(fullName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var cached = Messages(fullName);
        if (info.UidValidity != 0 && folder.UidValidity != info.UidValidity)
        {
            // the server renumbered this mailbox: start it over
            cached = new List<MailMessageInfo>();
            MailStore.ForgetFolder(Account.Id, fullName);
        }
        info.UidValidity = folder.UidValidity;

        var all = await folder.SearchAsync(SearchQuery.All, ct);   // every message, oldest first
        var unseen = await folder.SearchAsync(SearchQuery.NotSeen, ct);
        var present = all.Select(u => u.Id).ToHashSet();
        var keep = cached.Where(m => present.Contains(m.Uid)).ToList();
        uint newest = keep.Count > 0 ? keep.Max(m => m.Uid) : 0;
        var arrivals = all.Where(u => u.Id > newest).ToList();
        if (keep.Count > 0 && arrivals.Count > Batch) keep.Clear();   // (long away: just the newest)
        var want = keep.Count == 0 ? all.Skip(Math.Max(0, all.Count - Batch)).ToList() : arrivals;
        var local = keep.Select(m => m.Uid).Concat(want.Select(u => u.Id)).ToHashSet();
        if (older && keep.Count > 0)
        {
            // Load More: the batch before the newest ones kept without a gap (not before an old unread one kept)
            uint oldest = WindowStart(all, local);
            var before = all.Where(u => u.Id < oldest && !local.Contains(u.Id)).ToList();
            want.AddRange(before.Skip(Math.Max(0, before.Count - Batch)));
            local.UnionWith(want.Select(u => u.Id));
        }
        // unread mail further back than that, too (the newest 300): Show Only Unread shows what the unread count says
        want.AddRange(unseen.Where(u => !local.Contains(u.Id)).OrderByDescending(u => u.Id).Take(300));

        var fetched = new List<MailMessageInfo>();
        if (want.Count > 0)
        {
            var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
                      | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText | MessageSummaryItems.References;
            // newest first, 50 at a time: a mailbox's first time (slow) fills the list in as they come, not all at the end
            var batches = want.Distinct().OrderByDescending(u => u.Id).Chunk(50).ToList();
            for (int i = 0; i < batches.Count; i++)
            {
                foreach (var s in await folder.FetchAsync(batches[i].ToList(), new FetchRequest(items), ct))
                    fetched.Add(Info(s, fullName));
                if (i == batches.Count - 1) break;
                SetMessages(fullName, keep.Concat(fetched).GroupBy(m => m.Uid).Select(g => g.Last())
                                          .OrderByDescending(m => m.Date).ThenByDescending(m => m.Uid).ToList());
                Raise();
            }
        }
        // flags of the ones already here (read on the phone, flagged elsewhere …)
        if (keep.Count > 0)
        {
            var flags = new Dictionary<uint, MessageFlags>();
            foreach (var chunk in keep.Select(m => new UniqueId(m.Uid)).Chunk(500))
                foreach (var s in await folder.FetchAsync(chunk.ToList(), new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags), ct))
                    if (s.Flags is MessageFlags f) flags[s.UniqueId.Id] = f;
            keep = keep.Select(m => flags.TryGetValue(m.Uid, out var f) ? WithFlags(m, f) : m).ToList();
        }
        var merged = keep.Concat(fetched).GroupBy(m => m.Uid).Select(g => g.Last())
                         .OrderByDescending(m => m.Date).ThenByDescending(m => m.Uid).ToList();
        foreach (var gone in cached.Where(m => !present.Contains(m.Uid)))
            try { File.Delete(MailStore.MessageFile(gone)); } catch { }
        info.Total = all.Count;
        info.Unread = unseen.Count;
        info.HasOlder = all.Count > 0 && WindowStart(all, merged.Select(m => m.Uid).ToHashSet()) > all[0].Id;
        SetMessages(fullName, merged);
        if (took.Elapsed.TotalSeconds > 5)
            global::MacShell.App.Log($"mail: {Account.Provider} \"{info.Name}\" took {took.Elapsed.TotalSeconds:0.0}s ({fetched.Count} fetched, {merged.Count} kept)");
    }

    /// <summary>The oldest of the newest messages kept with no gap (<paramref name="all"/>: the server's, oldest first) -
    /// older ones kept besides are unread ones fetched out of turn.</summary>
    static uint WindowStart(IList<UniqueId> all, HashSet<uint> local)
    {
        uint start = uint.MaxValue;
        for (int i = all.Count - 1; i >= 0 && local.Contains(all[i].Id); i--) start = all[i].Id;
        return start;
    }

    MailMessageInfo Info(IMessageSummary s, string folder)
    {
        var env = s.Envelope;
        var from = env?.From?.Mailboxes.FirstOrDefault();
        var m = new MailMessageInfo
        {
            AccountId = Account.Id, Folder = folder, Uid = s.UniqueId.Id,
            MessageId = env?.MessageId, Subject = env?.Subject ?? "",
            FromName = from?.Name, FromAddress = from?.Address,
            To = Addresses(env?.To), Cc = Addresses(env?.Cc),
            Date = env?.Date ?? s.InternalDate ?? DateTimeOffset.Now,
            Preview = Squash(s.PreviewText),
            HasAttachments = s.Attachments?.Any() == true,
            Size = s.Size ?? 0,
            InReplyTo = env?.InReplyTo,
            References = s.References is { Count: > 0 } r ? string.Join(" ", r) : null,
        };
        return WithFlags(m, s.Flags ?? MessageFlags.None);
    }

    static MailMessageInfo WithFlags(MailMessageInfo m, MessageFlags f)
    {
        m.Seen = f.HasFlag(MessageFlags.Seen);
        m.Flagged = f.HasFlag(MessageFlags.Flagged);
        m.Answered = f.HasFlag(MessageFlags.Answered);
        m.Draft = f.HasFlag(MessageFlags.Draft);
        return m;
    }

    public static string Addresses(InternetAddressList list) =>
        list == null || list.Count == 0 ? null : string.Join(", ", list.Mailboxes.Select(b => string.IsNullOrWhiteSpace(b.Name) ? b.Address : $"{b.Name} <{b.Address}>"));

    static string Squash(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 240 ? s[..240] : s;
    }

    // opening a message has a connection of its own: it never waits behind a sync (a big mailbox's first one takes a while)
    readonly SemaphoreSlim _openGate = new(1, 1);
    ImapClient _openImap;

    /// <summary>The whole message: kept on disk after the first time.</summary>
    public async Task<MimeMessage> GetMessageAsync(MailMessageInfo m)
    {
        string file = MailStore.MessageFile(m);
        if (File.Exists(file))
            try { return await MimeMessage.LoadAsync(file); } catch { }
        MimeMessage msg = null;
        var ct = _stop?.Token ?? CancellationToken.None;
        try { await _openGate.WaitAsync(ct); } catch (OperationCanceledException) { return null; }
        try
        {
            // (twice at most: a connection left idle is often dropped by the server, and then a new one is made)
            for (int attempt = 0; attempt < 2 && msg == null && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    if (_openImap is not { IsConnected: true, IsAuthenticated: true })
                    {
                        try { _openImap?.Dispose(); } catch { }
                        _openImap = await ConnectAsync(Account, ct);
                    }
                    var f = await OpenAsync(_openImap, m.Folder, FolderAccess.ReadOnly, ct);
                    msg = await f.GetMessageAsync(new UniqueId(m.Uid), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (MessageNotFoundException) { break; }
                catch
                {
                    try { _openImap?.Dispose(); } catch { }
                    _openImap = null;
                }
            }
        }
        finally { _openGate.Release(); }
        if (msg != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await msg.WriteToAsync(file + ".tmp");
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch { }
        }
        return msg;
    }

    // ------------------------------------------------------------------ changes (made here at once, then on the server)

    public Task SetFlagAsync(IReadOnlyList<MailMessageInfo> msgs, MessageFlags flag, bool on)
    {
        foreach (var m in msgs)
        {
            if (flag == MessageFlags.Seen) m.Seen = on;
            if (flag == MessageFlags.Flagged) m.Flagged = on;
            if (flag == MessageFlags.Answered) m.Answered = on;
        }
        foreach (var g in msgs.GroupBy(m => m.Folder))
        {
            var f = Folder(g.Key);
            if (f != null && flag == MessageFlags.Seen) f.Unread = Math.Max(0, f.Unread + (on ? -g.Count() : g.Count()));
            SetMessages(g.Key, Messages(g.Key).ToList());
        }
        Raise();
        return RunAsync(async (c, ct) =>
        {
            foreach (var g in msgs.GroupBy(m => m.Folder))
            {
                var f = await OpenAsync(c, g.Key, FolderAccess.ReadWrite, ct);
                await f.StoreAsync(g.Select(m => new UniqueId(m.Uid)).ToList(), new StoreFlagsRequest(on ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true }, ct);
            }
        });
    }

    /// <summary>Into another mailbox (archive, junk, trash, or one of the user's).</summary>
    public Task MoveAsync(IReadOnlyList<MailMessageInfo> msgs, string dest)
    {
        if (Folder(dest) == null) return Task.CompletedTask;
        Remove(msgs);
        var destInfo = Folder(dest);
        destInfo.Total += msgs.Count;
        destInfo.Unread += msgs.Count(m => !m.Seen);
        Raise();
        return RunAsync(async (c, ct) =>
        {
            var to = await c.GetFolderAsync(dest, ct);
            foreach (var g in msgs.GroupBy(m => m.Folder))
            {
                var f = await OpenAsync(c, g.Key, FolderAccess.ReadWrite, ct);
                await f.MoveToAsync(g.Select(m => new UniqueId(m.Uid)).ToList(), to, ct);
            }
        });
    }

    /// <summary>Deleted for good (already in the Trash, or a draft replaced).</summary>
    public Task EraseAsync(IReadOnlyList<MailMessageInfo> msgs)
    {
        Remove(msgs);
        Raise();
        return RunAsync(async (c, ct) =>
        {
            foreach (var g in msgs.GroupBy(m => m.Folder))
            {
                var f = await OpenAsync(c, g.Key, FolderAccess.ReadWrite, ct);
                var uids = g.Select(m => new UniqueId(m.Uid)).ToList();
                await f.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                if (c.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await f.ExpungeAsync(uids, ct); else await f.ExpungeAsync(ct);
            }
        });
    }

    void Remove(IReadOnlyList<MailMessageInfo> msgs)
    {
        foreach (var g in msgs.GroupBy(m => m.Folder))
        {
            var uids = g.Select(m => m.Uid).ToHashSet();
            SetMessages(g.Key, Messages(g.Key).Where(m => !uids.Contains(m.Uid)).ToList());
            var f = Folder(g.Key);
            if (f != null)
            {
                f.Total = Math.Max(0, f.Total - g.Count());
                f.Unread = Math.Max(0, f.Unread - g.Count(m => !m.Seen));
            }
        }
    }

    /// <summary>A message put in a mailbox by role (a draft into Drafts, a sent one into Sent).</summary>
    public async Task<bool> AppendAsync(string role, MimeMessage msg, MessageFlags flags)
    {
        var f = FolderByRole(role);
        if (f == null) return false;
        bool ok = false;
        await RunAsync(async (c, ct) =>
        {
            var folder = await c.GetFolderAsync(f.FullName, ct);
            await folder.AppendAsync(new AppendRequest(msg, flags), ct);
            ok = true;
        });
        if (ok) _ = SyncFolderAsync(f.FullName);
        return ok;
    }

    /// <summary>Sent over SMTP; iCloud's Sent mailbox gets its copy from here (Gmail keeps one itself).</summary>
    public async Task SendAsync(MimeMessage msg, CancellationToken ct = default)
    {
        using (var smtp = new SmtpClient { Timeout = 120_000 })
        {
            var (host, port, tls) = Account.Smtp;
            await smtp.ConnectAsync(host, port, tls, ct);
            if (smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication)) await smtp.AuthenticateAsync(Account.Email, Account.Password, ct);
            await smtp.SendAsync(msg, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        if (!Account.ServerSavesSent) await AppendAsync("sent", msg, MessageFlags.Seen);
        else if (FolderByRole("sent") is { } sent) _ = SyncFolderAsync(sent.FullName);
    }
}
