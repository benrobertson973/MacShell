using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace MacShell.Services;

/// <summary>
/// The menu bar's timer: set from its "00:00" item - for a length of time, or to ring at a time of day (an alarm) -
/// it counts down there; at zero an alarm rings (a looping Windows alarm sound, until stopped - at most 5 minutes).
/// Eating mode instead starts the minute over and over, with a soft chime each time, until it's ended.
/// A running or paused timer is kept in the settings, so it survives MacShell restarting (an update): it carries on,
/// or rings if it ended while MacShell was away (up to 5 minutes ago).
/// </summary>
public static class CountdownTimer
{
    public enum State { Idle, Running, Paused, Ringing }

    public static State Status { get; private set; }
    /// <summary>What the timer was set to (its progress, and Repeat).</summary>
    public static TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);
    /// <summary>When an alarm rings (local time); null for a timer that runs for a length of time.</summary>
    public static DateTime? RingsAt { get; private set; }
    public static bool IsAlarm => RingsAt != null;
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(9);
    /// <summary>Eating mode: the timer starts over every this long, with a soft chime, until it's ended.</summary>
    public static TimeSpan? RepeatEvery { get; private set; }
    public static bool IsEating => RepeatEvery != null;
    /// <summary>Eating mode: the minutes gone by.</summary>
    public static int Cycles { get; private set; }
    public static readonly TimeSpan EatingMinute = TimeSpan.FromMinutes(1);
    /// <summary>State changes, and every second while it counts (and the flashing while it rings).</summary>
    public static event Action Changed;
    /// <summary>Eating mode: a minute is up (its chime is playing).</summary>
    public static event Action Chimed;

    static DateTime _endsUtc;     // Running
    static TimeSpan _left;        // Paused
    static DateTime _rangUtc;     // Ringing
    static string _shown;
    static bool _flash;
    static DispatcherTimer _tick;
    static readonly TimeSpan RingFor = TimeSpan.FromMinutes(5);

    /// <summary>The alarm sounds offered: Windows' own (Windows\Media\Alarm01-10.wav).</summary>
    public static readonly string[] Sounds = Enumerable.Range(1, 10).Select(i => $"Alarm{i:00}").ToArray();
    public static string SoundName(string sound) => sound != null && sound.StartsWith("Alarm") && int.TryParse(sound[5..], out int n) ? $"Alarm {n}" : sound;

    public static TimeSpan Remaining => Status switch
    {
        State.Running => _endsUtc > DateTime.UtcNow ? _endsUtc - DateTime.UtcNow : TimeSpan.Zero,
        State.Paused => _left,
        _ => TimeSpan.Zero,
    };

    /// <summary>What the menu bar shows: "00:00" when idle, else the time left ("04:59", "1:05:00").</summary>
    public static string Text => Format(Remaining);

    /// <summary>On and off twice a second while the alarm rings.</summary>
    public static bool Flash => Status == State.Ringing && _flash;

    public static string Format(TimeSpan t)
    {
        long s = Math.Max(0, (long)Math.Ceiling(t.TotalSeconds - 0.001));   // (a timer just started for 5:00 shows 05:00)
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    /// <summary>At startup: a timer that was running (or paused) when MacShell last quit.</summary>
    public static void Initialize()
    {
        var s = Settings.Current;
        if (s.TimerSeconds >= 1) Duration = TimeSpan.FromSeconds(s.TimerSeconds);
        if (s.TimerRepeatSeconds is double every && every >= 1)
        {
            RepeatEvery = TimeSpan.FromSeconds(every);
            Duration = RepeatEvery.Value;
            Cycles = s.TimerCycles;
        }
        if (s.TimerEndsUtc is DateTime end)
        {
            end = DateTime.SpecifyKind(end, DateTimeKind.Utc);
            RingsAt = s.TimerIsAlarm ? end.ToLocalTime() : null;
            if (RepeatEvery is TimeSpan minute && DateTime.UtcNow - end < RingFor)
            {
                // eating mode carries on, from the minute it's in now
                while (end <= DateTime.UtcNow) end += minute;
                _endsUtc = end;
                SetStatus(State.Running);
            }
            else if (RepeatEvery != null) { RepeatEvery = null; SetStatus(State.Idle); Persist(); }
            else if (end > DateTime.UtcNow) { _endsUtc = end; SetStatus(State.Running); }
            else if (DateTime.UtcNow - end < RingFor) Ring();
            else { SetStatus(State.Idle); Persist(); }
        }
        else if (s.TimerPausedSeconds is double p && p > 0)
        {
            _left = TimeSpan.FromSeconds(p);
            SetStatus(State.Paused);
        }
    }

    public static void Start(TimeSpan length)
    {
        if (length < TimeSpan.FromSeconds(1)) return;
        StopSound();
        RingsAt = null;
        RepeatEvery = null;
        Duration = length;
        Settings.Current.TimerSeconds = length.TotalSeconds;
        _endsUtc = DateTime.UtcNow + length;
        SetStatus(State.Running);
        Persist();
    }

    /// <summary>An alarm: rings at <paramref name="time"/> (local time, in the future).</summary>
    public static void StartAt(DateTime time)
    {
        var length = time - DateTime.Now;
        if (length < TimeSpan.FromSeconds(1)) return;
        StopSound();
        RingsAt = time;
        RepeatEvery = null;
        Duration = length;
        _endsUtc = time.ToUniversalTime();
        SetStatus(State.Running);
        Persist();
    }

    /// <summary>The alarm off, and on again in 9 minutes.</summary>
    public static void Snooze() => StartAt(DateTime.Now + SnoozeFor);

    /// <summary>Diagnostics: a paused timer put back as it was (<paramref name="left"/> of <paramref name="length"/>).</summary>
    public static void RestorePaused(TimeSpan left, TimeSpan length)
    {
        StopSound();
        RingsAt = null;
        RepeatEvery = null;
        Cycles = 0;
        Duration = length;
        _left = left;
        SetStatus(State.Paused);
        Persist();
    }

    /// <summary>Eating mode: a minute, a soft chime, and the next minute - over and over until it's ended (Cancel).</summary>
    public static void StartEating(TimeSpan? every = null)
    {
        StopSound();
        RingsAt = null;
        RepeatEvery = every ?? EatingMinute;
        Duration = RepeatEvery.Value;
        Cycles = 0;
        _endsUtc = DateTime.UtcNow + RepeatEvery.Value;
        SetStatus(State.Running);
        Persist();
    }

    static void NextMinute(TimeSpan every)
    {
        Cycles++;
        _endsUtc += every;
        if (_endsUtc <= DateTime.UtcNow) _endsUtc = DateTime.UtcNow + every;   // (after the computer slept: a fresh minute)
        PlayChime();
        Persist();
        _shown = Text;
        Chimed?.Invoke();
        Changed?.Invoke();
    }

    public static void Pause()
    {
        if (Status != State.Running) return;
        _left = Remaining;
        RingsAt = null;   // (paused, it's a length of time again)
        SetStatus(State.Paused);
        Persist();
    }

    public static void Resume()
    {
        if (Status != State.Paused) return;
        _endsUtc = DateTime.UtcNow + _left;
        SetStatus(State.Running);
        Persist();
    }

    public static void AddMinute()
    {
        if (Status == State.Running) _endsUtc += TimeSpan.FromMinutes(1);
        else if (Status == State.Paused) _left += TimeSpan.FromMinutes(1);
        else return;
        Duration += TimeSpan.FromMinutes(1);
        if (RingsAt != null) RingsAt += TimeSpan.FromMinutes(1);
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Cancels a running or paused timer, or stops the alarm.</summary>
    public static void Cancel()
    {
        StopSound();
        RingsAt = null;
        RepeatEvery = null;
        Cycles = 0;
        SetStatus(State.Idle);
        Persist();
    }

    /// <summary>The alarm off, and the same timer again.</summary>
    public static void Repeat() => Start(Duration);

    /// <summary>
    /// What was typed in the timer's field. Without AM/PM it's a length of time: "5:00" (5 minutes), "1:30:00", "5"
    /// (minutes), "90 sec", "1h 30m", "1h30". With AM/PM (or "noon", "midnight", "at …") it's a time of day to ring at:
    /// "5:30pm", "5:30 am", "7a" - then <paramref name="at"/> is set.
    /// </summary>
    public static bool TryParse(string text, DateTime now, out TimeSpan length, out DateTime? at)
    {
        length = TimeSpan.Zero;
        at = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text.Trim().ToLowerInvariant(), @"(?<=[a-z])\.|\.(?=[a-z])", "");   // ("p.m." → "pm"; "2.5" stays)
        bool atTime = t.StartsWith("at ");
        if (atTime) t = t[3..].Trim();
        if (atTime || t is "noon" or "midday" or "midnight" || Regex.IsMatch(t, @"\d\s*(am|pm|a|p)$"))
        {
            if (!TryParseTimeOfDay(t, now, Settings.Current.Clock24Hour, out var when)) return false;
            at = when;
            length = when - now;
            return true;
        }
        double seconds;
        var colon = Regex.Match(t, @"^(\d+):(\d{1,2})(?::(\d{1,2}))?$");
        if (colon.Success)
        {
            // m:ss, or h:mm:ss
            int a = int.Parse(colon.Groups[1].Value), b = int.Parse(colon.Groups[2].Value);
            if (colon.Groups[3].Success)
            {
                int c = int.Parse(colon.Groups[3].Value);
                if (b > 59 || c > 59) return false;
                seconds = a * 3600 + b * 60 + c;
            }
            else
            {
                if (b > 59) return false;
                seconds = a * 60 + b;
            }
        }
        else
        {
            // "5", "2.5", "90 sec", "1 hr 15 min", "1h30" (a number without a unit: minutes - or after hours, minutes;
            // after minutes, seconds)
            string compact = Regex.Replace(t, @"[\s,]+|and", "");
            var parts = Regex.Matches(compact, @"(\d+(?:\.\d+)?)([a-z]*)");
            if (parts.Count == 0 || parts.Sum(p => p.Length) != compact.Length) return false;
            seconds = 0;
            string last = null;
            foreach (Match p in parts)
            {
                double n = double.Parse(p.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string unit = p.Groups[2].Value switch
                {
                    "" => last switch { "h" => "m", "m" => "s", _ => "m" },
                    "h" or "hr" or "hrs" or "hour" or "hours" => "h",
                    "m" or "min" or "mins" or "minute" or "minutes" => "m",
                    "s" or "sec" or "secs" or "second" or "seconds" => "s",
                    _ => null,
                };
                if (unit == null) return false;
                seconds += n * (unit == "h" ? 3600 : unit == "m" ? 60 : 1);
                last = unit;
            }
        }
        if (seconds < 1 || seconds >= 100 * 3600) return false;
        length = TimeSpan.FromSeconds(Math.Round(seconds));
        return true;
    }

    /// <summary>
    /// A time of day typed for an alarm, as its next occurrence after <paramref name="now"/>: "4:30 PM", "4:30pm", "4pm",
    /// "4p", "430pm", "16:30", "noon", "midnight". Without AM/PM, "4:30" is whichever 4:30 comes first (with a 24-hour
    /// clock: 04:30).
    /// </summary>
    public static bool TryParseTimeOfDay(string text, DateTime now, bool clock24, out DateTime when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().ToLowerInvariant().Replace(".", "").Replace(" ", "");
        if (t is "noon" or "midday") t = "12pm";
        else if (t == "midnight") t = "12am";
        var m = Regex.Match(t, @"^(?<h>\d{1,2})(?::?(?<m>\d{2}))?(?<ap>am|pm|a|p)?$");
        if (!m.Success) return false;
        int h = int.Parse(m.Groups["h"].Value);
        int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
        if (min > 59) return false;
        var hours = new List<int>();
        string ap = m.Groups["ap"].Value;
        if (ap.Length > 0)
        {
            if (h < 1 || h > 12) return false;
            hours.Add(h % 12 + (ap[0] == 'p' ? 12 : 0));
        }
        else
        {
            if (h > 23) return false;
            hours.Add(h);
            if (!clock24 && h >= 1 && h <= 12) hours.Add((h + 12) % 24);
        }
        var best = DateTime.MaxValue;
        foreach (int hh in hours)
        {
            var d = now.Date.AddHours(hh).AddMinutes(min);
            if (d <= now) d = d.AddDays(1);
            if (d < best) best = d;
        }
        when = best;
        return true;
    }

    static void Ring()
    {
        _rangUtc = DateTime.UtcNow;
        SetStatus(State.Ringing);
        Persist();
        PlaySoundFile(Settings.Current.TimerSound, loop: true);
    }

    static void SetStatus(State s)
    {
        Status = s;
        _flash = s == State.Ringing;
        bool ticking = s is State.Running or State.Ringing;
        if (ticking && _tick == null)
        {
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tick.Tick += (_, _) => Tick();
        }
        if (ticking) _tick.Start(); else _tick?.Stop();
        _shown = Text;
        Changed?.Invoke();
    }

    static void Tick()
    {
        if (Status == State.Running)
        {
            if (Remaining <= TimeSpan.Zero)
            {
                if (RepeatEvery is TimeSpan every) NextMinute(every); else Ring();
                return;
            }
            if (Text != _shown) { _shown = Text; Changed?.Invoke(); }
        }
        else if (Status == State.Ringing)
        {
            var rang = DateTime.UtcNow - _rangUtc;
            if (rang > RingFor) { Cancel(); return; }
            bool flash = rang.TotalMilliseconds % 1000 < 500;
            if (flash != _flash) { _flash = flash; Changed?.Invoke(); }
        }
    }

    static void Persist()
    {
        var s = Settings.Current;
        s.TimerEndsUtc = Status == State.Running ? _endsUtc : null;
        s.TimerPausedSeconds = Status == State.Paused ? _left.TotalSeconds : null;
        s.TimerIsAlarm = Status == State.Running && RingsAt != null;
        s.TimerRepeatSeconds = Status is State.Running or State.Paused && RepeatEvery != null ? RepeatEvery.Value.TotalSeconds : null;
        s.TimerCycles = Cycles;
        Settings.Save(notify: false);
    }

    // ------------------------------------------------------------------ the alarm sound

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(string file, IntPtr module, uint flags);
    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_LOOP = 0x0008, SND_FILENAME = 0x00020000;

    static string SoundFile(string sound)
    {
        string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        string file = Path.Combine(media, (sound ?? "Alarm01") + ".wav");
        return File.Exists(file) ? file : Path.Combine(media, "Alarm01.wav");
    }

    static void PlaySoundFile(string sound, bool loop) =>
        PlaySound(SoundFile(sound), IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_FILENAME | (loop ? SND_LOOP : 0));

    /// <summary>A sound played once, to hear it before choosing it (not while the alarm rings).</summary>
    public static void Preview(string sound)
    {
        if (Status != State.Ringing) PlaySoundFile(sound, loop: false);
    }

    static void StopSound() => PlaySound(null, IntPtr.Zero, 0);

    // ------------------------------------------------------------------ eating mode's chime

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    static extern bool PlaySoundMemory(IntPtr wav, IntPtr module, uint flags);
    const uint SND_MEMORY = 0x0004;
    static IntPtr _chime;   // (made once, and kept where Windows can play it from while MacShell carries on)

    /// <summary>A soft "ding": quiet (about a fifth of full volume) and short, not an alarm.</summary>
    public static void PlayChime()
    {
        if (_chime == IntPtr.Zero)
        {
            byte[] wav = MakeChime();
            _chime = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, _chime, wav.Length);
        }
        PlaySoundMemory(_chime, IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_MEMORY);
    }

    /// <summary>A small bell: A5 with two overtones that fade sooner, 1.3 seconds, peaking at 22% of full scale.</summary>
    static byte[] MakeChime()
    {
        const int rate = 44100;
        int n = (int)(rate * 1.3);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double v = Math.Sin(2 * Math.PI * 880 * t) * Math.Exp(-t / 0.42)
                     + 0.32 * Math.Sin(2 * Math.PI * 1760 * t) * Math.Exp(-t / 0.22)
                     + 0.12 * Math.Sin(2 * Math.PI * 2640 * t) * Math.Exp(-t / 0.12);
            double attack = Math.Min(1, t / 0.006);   // (no click at the start)
            w.Write((short)Math.Round(v / 1.44 * attack * 0.22 * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}
