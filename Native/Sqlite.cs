using System.Runtime.InteropServices;
using System.Text;

namespace MacShell.Native;

/// <summary>A SQLite database, through the SQLite library that ships inside Windows (winsqlite3.dll).</summary>
public sealed class Sqlite : IDisposable
{
    const int OPEN_READONLY = 0x1, OPEN_READWRITE = 0x2, ROW = 100;
    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_errmsg(IntPtr db);

    IntPtr _db;
    Sqlite(IntPtr db) { _db = db; }

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Opens a database file (writable: a copy whose journal SQLite may need to replay).</summary>
    public static Sqlite Open(string path, bool writable)
    {
        if (sqlite3_open_v2(Z(path), out var db, writable ? OPEN_READWRITE : OPEN_READONLY, IntPtr.Zero) != 0)
        {
            string err = db != IntPtr.Zero ? Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) : "can't open";
            if (db != IntPtr.Zero) sqlite3_close(db);
            throw new IOException("The database couldn’t be opened: " + err);
        }
        sqlite3_busy_timeout(db, 2000);
        return new Sqlite(db);
    }

    /// <summary>The rows a query returns, each a column name → value (long, double, string, byte[] or null).</summary>
    public List<Dictionary<string, object>> Query(string sql)
    {
        var rows = new List<Dictionary<string, object>>();
        if (sqlite3_prepare_v2(_db, Z(sql), -1, out var st, IntPtr.Zero) != 0)
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) + " in: " + sql);
        try
        {
            int n = sqlite3_column_count(st);
            var names = Enumerable.Range(0, n).Select(i => Marshal.PtrToStringUTF8(sqlite3_column_name(st, i))).ToArray();
            while (sqlite3_step(st) == ROW)
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    switch (sqlite3_column_type(st, i))
                    {
                        case 1: row[names[i]] = sqlite3_column_int64(st, i); break;
                        case 2: row[names[i]] = sqlite3_column_double(st, i); break;
                        case 3:
                            {
                                int len = sqlite3_column_bytes(st, i);
                                var p = sqlite3_column_text(st, i);
                                row[names[i]] = p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p, len);
                                break;
                            }
                        case 4:
                            {
                                int len = sqlite3_column_bytes(st, i);
                                var p = sqlite3_column_blob(st, i);
                                var b = new byte[len];
                                if (len > 0 && p != IntPtr.Zero) Marshal.Copy(p, b, 0, len);
                                row[names[i]] = b;
                                break;
                            }
                        default: row[names[i]] = null; break;
                    }
                }
                rows.Add(row);
            }
        }
        finally { sqlite3_finalize(st); }
        return rows;
    }

    public HashSet<string> Columns(string table) =>
        new(Query($"PRAGMA table_info({table})").Select(r => r["name"] as string).Where(s => s != null), StringComparer.OrdinalIgnoreCase);

    public bool HasTable(string table) => Query($"SELECT name FROM sqlite_master WHERE type='table' AND name='{table.Replace("'", "''")}'").Count > 0;

    public void Dispose()
    {
        if (_db != IntPtr.Zero) { sqlite3_close(_db); _db = IntPtr.Zero; }
    }
}
