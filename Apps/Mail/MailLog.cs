using MacShell.Services;

namespace MacShell.Apps.Mail;

/// <summary>
/// Mail's own log (%APPDATA%\MacShell\mail.log): when push heard of new mail, when it was announced, and how long
/// checks and downloads took - for finding out why mail was slow. Kept short (the older half goes past 256 KB).
/// </summary>
public static class MailLog
{
    static readonly string FilePath = Path.Combine(Settings.DataDirectory, "mail.log");
    static readonly object Lock = new();

    public static void Write(string s)
    {
        lock (Lock)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    var lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {s}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
