using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using MacShell.Native;

namespace MacShell.Services;

/// <summary>
/// Red Dock badges, like macOS. Sources, strongest first:
///  1. the app's own badge (Windows badge notifications — e.g. WhatsApp "2");
///  2. an unread count in a window title — "(3) Discord";
///  3. notifications the app has waiting in Notification Center (optional, off by default — Windows keeps
///     old notifications around for days, so they are not a reliable "unread" count).
/// Windows keeps 1 and 3 in the per-user notification database; it is read (read-only, counts only —
/// never message content) with the SQLite library that ships inside Windows.
/// </summary>
public static class Badges
{
    public static event Action Changed;
    static Dictionary<string, (string badge, int toasts)> _byId = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> _shortcutIds = new(StringComparer.OrdinalIgnoreCase);
    static DispatcherTimer _timer;
    static string _last = "";
    static bool _busy;
    static readonly Regex TitleCount = new(@"^\s*\((\d{1,4})\+?\)\s", RegexOptions.Compiled);

    public static void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        WindowTracker.AppsChanged += () => Changed?.Invoke();   // title counts
        Poll();
    }

    static void Poll()
    {
        if (_busy) return;
        if (!Settings.Current.DockShowBadges)
        {
            if (_byId.Count > 0) { _byId = new(StringComparer.OrdinalIgnoreCase); _last = ""; Changed?.Invoke(); }
            return;
        }
        _busy = true;
        Task.Run(Read).ContinueWith(t =>
        {
            _busy = false;
            if (t.IsFaulted || t.Result == null) return;
            var map = t.Result;
            string sig = string.Join(";", map.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.badge}/{k.Value.toasts}"));
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _byId = map;
                if (sig != _last) { _last = sig; Changed?.Invoke(); }
            });
        });
    }

    /// <summary>Badge text for a Dock app (null = no badge).</summary>
    public static string For(string key, string target, RunningApp running)
    {
        if (!Settings.Current.DockShowBadges) return null;
        var ids = new List<string>();
        if (key != null)
        {
            if (key.StartsWith("app:")) ids.Add(key[4..]);
            else if (key.StartsWith("aumid:")) ids.Add(key[6..]);
        }
        if (target != null && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (!_shortcutIds.TryGetValue(target, out var sid)) _shortcutIds[target] = sid = NativeMethods.GetShortcutAppId(target);
            if (sid != null) ids.Add(sid);
        }
        if (running != null) ids.AddRange(running.Windows.Select(w => w.Aumid).Where(a => !string.IsNullOrEmpty(a)));

        // 1. explicit badge
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var v) && !string.IsNullOrEmpty(v.badge))
            {
                string b = v.badge.Trim();
                if (int.TryParse(b, out int n)) { if (n > 0) return Format(n); }
                else if (b is "alert" or "attention" or "error") return "!";
                else if (b is "newMessage" or "activity" or "alarm") return "•";
            }
        // 2. unread count in a window title
        int best = 0;
        if (running != null)
            foreach (var w in running.Windows)
            {
                var m = TitleCount.Match(w.Title ?? "");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > best) best = n;
            }
        if (best > 0) return Format(best);
        // 3. waiting notifications
        if (Settings.Current.BadgeCountNotifications)
        {
            int toasts = ids.Distinct(StringComparer.OrdinalIgnoreCase).Sum(id => _byId.TryGetValue(id, out var v) ? v.toasts : 0);
            if (toasts > 0) return Format(toasts);
        }
        return null;
    }

    static string Format(int n) => n > 999 ? "999+" : n.ToString();

    // ------------------------------------------------------------------ database

    const int SQLITE_OPEN_READONLY = 0x1, SQLITE_OPEN_URI = 0x40, SQLITE_ROW = 100;
    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static string Str(IntPtr stmt, int col, bool blob = false)
    {
        IntPtr p = blob ? sqlite3_column_blob(stmt, col) : sqlite3_column_text(stmt, col);
        int n = sqlite3_column_bytes(stmt, col);
        if (p == IntPtr.Zero || n <= 0) return null;
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return Encoding.UTF8.GetString(b);
    }

    static Dictionary<string, (string badge, int toasts)> Read()
    {
        string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");
        if (!File.Exists(db)) return null;
        IntPtr h = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Z("file:" + db.Replace('\\', '/') + "?mode=ro"), out h, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero) != 0) return null;
            sqlite3_busy_timeout(h, 250);
            const string sql = "SELECT h.PrimaryId, n.Type, CASE WHEN n.Type = 'badge' THEN n.Payload END, n.ExpiryTime " +
                               "FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type IN ('badge','toast')";
            if (sqlite3_prepare_v2(h, Z(sql), -1, out st, IntPtr.Zero) != 0) return null;
            long now = DateTime.UtcNow.ToFileTimeUtc();
            var map = new Dictionary<string, (string badge, int toasts)>(StringComparer.OrdinalIgnoreCase);
            while (sqlite3_step(st) == SQLITE_ROW)
            {
                string id = Str(st, 0);
                string type = Str(st, 1);
                if (string.IsNullOrEmpty(id) || type == null) continue;
                map.TryGetValue(id, out var cur);
                if (type == "badge")
                {
                    var m = Regex.Match(Str(st, 2, blob: true) ?? "", "value\\s*=\\s*\"([^\"]*)\"");
                    if (m.Success) cur.badge = m.Groups[1].Value;
                }
                else
                {
                    long expiry = sqlite3_column_int64(st, 3);
                    if (expiry > 0 && expiry < now) continue;   // expired notifications linger in the table
                    cur.toasts++;
                }
                map[id] = cur;
            }
            return map;
        }
        catch { return null; }
        finally
        {
            if (st != IntPtr.Zero) sqlite3_finalize(st);
            if (h != IntPtr.Zero) sqlite3_close(h);
        }
    }
}
