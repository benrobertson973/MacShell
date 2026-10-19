using System.Windows;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Services;

namespace MacShell;

public partial class App : Application
{
    Mutex _mutex;
    EventWaitHandle _quitEvent, _openEvent;
    static string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell", "macshell.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();

        if (args.Contains("--restore"))
        {
            Takeover.EmergencyRestore();
            Shutdown();
            return;
        }
        if (args.Contains("--quit"))
        {
            if (EventWaitHandle.TryOpenExisting("MacShell.Quit", out var q)) q.Set();
            Shutdown();
            return;
        }

        // the settings probe: every System Settings page rendered offscreen to PNGs (a copy beside the running MacShell;
        // nothing is saved, nothing taken over)
        int probeIdx = Array.IndexOf(args, "--settings-probe");
        if (probeIdx >= 0 && probeIdx + 1 < e.Args.Length)
        {
            LogPath = Path.Combine(e.Args[probeIdx + 1], "probe.log");   // (MacShell's own log is left alone)
            DispatcherUnhandledException += (_, ev) => { Log("settings probe: " + ev.Exception); ev.Handled = true; };
            int pageIdx = Array.IndexOf(args, "--page");
            try { MacShell.Apps.SettingsWindow.Probe(e.Args[probeIdx + 1], args.Contains("--dark"), pageIdx >= 0 && pageIdx + 1 < e.Args.Length ? e.Args[pageIdx + 1] : null); }
            catch (Exception ex) { Log("settings probe: " + ex); }
            Shutdown();
            return;
        }

        // the bars probe: the menu bar and the Dock on made-up displays (off-screen) whose scale isn't the windows' own
        int barsProbe = Array.IndexOf(args, "--bars-probe");
        if (barsProbe >= 0 && barsProbe + 1 < e.Args.Length)
        {
            LogPath = Path.Combine(e.Args[barsProbe + 1], "probe.log");
            DispatcherUnhandledException += (_, ev) => { Log("bars probe: " + ev.Exception); ev.Handled = true; };
            try { MacShell.Shell.BarsProbe.Run(e.Args[barsProbe + 1]); }
            catch (Exception ex) { Log("bars probe: " + ex); }
            Shutdown();
            return;
        }

        // the QuickTime probe: test movies and the screen recorder, silent and offscreen
        int qtProbe = Array.IndexOf(args, "--qt-probe");
        if (qtProbe >= 0 && qtProbe + 1 < e.Args.Length)
        {
            LogPath = Path.Combine(e.Args[qtProbe + 1], "probe.log");
            DispatcherUnhandledException += (_, ev) => { Log("qt probe: " + ev.Exception); ev.Handled = true; };
            try { MacShell.Apps.QuickTime.QuickTimeProbe.Run(e.Args[qtProbe + 1]); }
            catch (Exception ex) { Log("qt probe: " + ex); }
            Shutdown();
            return;
        }

        // the notes probe: Notes run offscreen on a library of their own (a copy beside the running MacShell)
        int notesProbe = Array.IndexOf(args, "--notes-probe");
        if (notesProbe >= 0 && notesProbe + 1 < e.Args.Length)
        {
            LogPath = Path.Combine(e.Args[notesProbe + 1], "probe.log");
            DispatcherUnhandledException += (_, ev) => { Log("notes probe: " + ev.Exception); ev.Handled = true; };
            try { MacShell.Apps.Notes.NotesProbe.Run(e.Args[notesProbe + 1], args.Contains("--dark")); }
            catch (Exception ex) { Log("notes probe: " + ex); }
            Shutdown();
            return;
        }

        // after an update the new exe is started by the old one: wait until the old one has fully quit
        int waitIdx = Array.IndexOf(args, "--wait-for");
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out int oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(20000); } catch { }
        }

        int openIdx = Array.IndexOf(args, "--open");
        string openCmd = openIdx >= 0 && openIdx + 1 < e.Args.Length ? e.Args[openIdx + 1] : null;

        _mutex = new Mutex(true, "MacShell.Instance", out bool created);
        if (!created)
        {
            if (openCmd != null)
            {
                // a file of its own for each command (written whole, then named): commands sent close together all run, in order
                try
                {
                    string tmp = Path.Combine(Path.GetDirectoryName(CommandFile), $"command-{DateTime.UtcNow.Ticks:D19}-{Environment.ProcessId}.tmp");
                    File.WriteAllText(tmp, openCmd);
                    File.Move(tmp, Path.ChangeExtension(tmp, ".txt"));
                }
                catch { }
                if (EventWaitHandle.TryOpenExisting("MacShell.Command", out var c)) c.Set();
            }
            else if (EventWaitHandle.TryOpenExisting("MacShell.OpenFinder", out var o)) o.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Log("FATAL " + ev.ExceptionObject);
            try { NativeMethods.ClipCursorNone(IntPtr.Zero); } catch { }
            try { Takeover.Release(); } catch { }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { NativeMethods.ClipCursorNone(IntPtr.Zero); Takeover.Release(); } catch { } };
        SessionEnding += (_, _) =>
        {
            try { MacShell.Apps.Preview.PreviewWindow.AutoSaveAll(); } catch { }
            try { if (LoginItem.Enabled) Takeover.LeaveForNextLogin(); else Takeover.Release(); } catch { }
        };

        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MacShell.Quit");
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MacShell.OpenFinder");
        StartSignalWatcher(_quitEvent, () => ShellHost.Quit());
        StartSignalWatcher(_openEvent, () => ShellHost.OpenFinder(null));
        _commandEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MacShell.Command");
        StartSignalWatcher(_commandEvent, () =>
        {
            foreach (var f in PendingCommands())
            {
                string cmd;
                try { cmd = File.ReadAllText(f).Trim(); File.Delete(f); } catch { continue; }
                if (!string.IsNullOrEmpty(cmd)) ShellHost.RunCommand(cmd);
            }
        });

        ShellHost.Start(noTakeover: args.Contains("--windowed"));
        if (openCmd != null) Dispatcher.BeginInvoke(() => ShellHost.RunCommand(openCmd), DispatcherPriority.ApplicationIdle);
    }

    EventWaitHandle _commandEvent;
    static string CommandFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell", "command.txt");

    /// <summary>The commands sent (--open) and not yet run, oldest first (command.txt: one from an older MacShell).</summary>
    static List<string> PendingCommands()
    {
        try { return Directory.GetFiles(Path.GetDirectoryName(CommandFile), "command*.txt").OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch { return new List<string>(); }
    }

    void StartSignalWatcher(EventWaitHandle h, Action onSignal)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                h.WaitOne();
                Dispatcher.BeginInvoke(onSignal);
            }
        }) { IsBackground = true };
        t.Start();
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        e.Handled = true; // a shell must never fall over
    }

    public static void Log(string s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.FileDialogTakeover.RestoreAll(); } catch { }
        try { Takeover.Release(); } catch { }
        base.OnExit(e);
    }
}
