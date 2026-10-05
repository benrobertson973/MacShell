using System.Windows;
using System.Windows.Threading;
using MacShell.Native;
using MacShell.Services;

namespace MacShell;

public partial class App : Application
{
    Mutex _mutex;
    EventWaitHandle _quitEvent, _openEvent;
    static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell", "macshell.log");

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

        int openIdx = Array.IndexOf(args, "--open");
        string openCmd = openIdx >= 0 && openIdx + 1 < e.Args.Length ? e.Args[openIdx + 1] : null;

        _mutex = new Mutex(true, "MacShell.Instance", out bool created);
        if (!created)
        {
            if (openCmd != null)
            {
                try { File.WriteAllText(CommandFile, openCmd); } catch { }
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
            try { if (LoginItem.Enabled) Takeover.LeaveForNextLogin(); else Takeover.Release(); } catch { }
        };

        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MacShell.Quit");
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MacShell.OpenFinder");
        StartSignalWatcher(_quitEvent, () => ShellHost.Quit());
        StartSignalWatcher(_openEvent, () => ShellHost.OpenFinder(null));
        _commandEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MacShell.Command");
        StartSignalWatcher(_commandEvent, () =>
        {
            string cmd = null;
            try { cmd = File.ReadAllText(CommandFile).Trim(); File.Delete(CommandFile); } catch { }
            if (!string.IsNullOrEmpty(cmd)) ShellHost.RunCommand(cmd);
        });

        ShellHost.Start(noTakeover: args.Contains("--windowed"));
        if (openCmd != null) Dispatcher.BeginInvoke(() => ShellHost.RunCommand(openCmd), DispatcherPriority.ApplicationIdle);
    }

    EventWaitHandle _commandEvent;
    static string CommandFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacShell", "command.txt");

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
        try { Takeover.Release(); } catch { }
        base.OnExit(e);
    }
}
