using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace MacShell.Services;

/// <summary>
/// "Open MacShell at login". A Task Scheduler logon task starts MacShell as soon as the user signs in, alongside
/// Explorer — the Run key used before only fires after Explorer's desktop is up (plus Windows' startup delay),
/// so the Windows desktop showed for a few seconds first. The task runs at normal priority (scheduled tasks
/// default to below normal) and has no time limit.
/// </summary>
public static class LoginItem
{
    const string TaskName = "MacShell";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Cached so it can be read instantly while Windows is shutting down.</summary>
    public static bool Enabled { get; private set; }

    static string ExePath => Environment.ProcessPath;

    /// <summary>On startup: move an old Run-key entry to the task, and repoint the task if MacShell.exe moved.</summary>
    public static void Initialize()
    {
        bool runKey = HasRunKey();
        Enabled = runKey;
        Task.Run(() =>
        {
            string taskExe = QueryTaskExe();
            if (taskExe != null) Enabled = true;
            if (!Enabled) return;
            if (taskExe == null || !string.Equals(taskExe, ExePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!CreateTask()) return;   // keep the Run key if the task can't be made
            }
            if (runKey) DeleteRunKey();
        });
    }

    public static void Set(bool on)
    {
        Enabled = on;
        if (on)
        {
            if (!CreateTask()) SetRunKey();   // fallback: the old way
        }
        else
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            DeleteRunKey();
        }
    }

    static bool CreateTask()
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts MacShell when you sign in.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(ExePath)}</Command></Exec></Actions>
</Task>";
        string file = Path.Combine(Path.GetTempPath(), "macshell-task.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>The exe the logon task starts, or null when there is no task.</summary>
    static string QueryTaskExe()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim('"') : "";
        }
        catch { return null; }
    }

    static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static bool HasRunKey()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("MacShell") != null; } catch { return false; }
    }

    static void SetRunKey()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.SetValue("MacShell", $"\"{ExePath}\""); } catch { }
    }

    static void DeleteRunKey()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue("MacShell", false); } catch { }
    }
}
