using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MacShell.Services;

/// <summary>
/// The weather in the menu bar: the temperature (°F) and the sky, from Open-Meteo (free, no account), every 15 minutes
/// (a minute after a failure; soon after waking up or getting back online).
/// Where: the place chosen in Settings › Control Center, else roughly where this computer is from its internet address
/// (ipapi.co, else geojs.io), looked up again every 6 hours. Nothing is fetched while the weather is out of the menu bar.
/// </summary>
public static class Weather
{
    public record Hour(DateTime Time, double Temp, int Code, bool IsDay);

    public static double? Temperature { get; private set; }
    public static double? FeelsLike { get; private set; }
    public static double? High { get; private set; }
    public static double? Low { get; private set; }
    public static int Code { get; private set; }
    public static bool IsDay { get; private set; } = true;
    /// <summary>The next hours, from this one.</summary>
    public static IReadOnlyList<Hour> Hours { get; private set; } = Array.Empty<Hour>();
    public static string Place { get; private set; }
    public static DateTime Updated { get; private set; }
    public static string Error { get; private set; }
    /// <summary>The weather is known and recent enough to show (not hours old: offline since).</summary>
    public static bool Current => Temperature != null && DateTime.Now - Updated < TimeSpan.FromHours(3);
    public static event Action Changed;

    static readonly HttpClient Http = MakeClient();
    static DispatcherTimer _timer;
    static bool _busy;

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MacShell/1.0");
        return c;
    }

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Soon(TimeSpan.FromSeconds(10)); };
        NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) Soon(TimeSpan.FromSeconds(3)); };
        _ = RefreshAsync();
    }

    /// <summary>Fetched again after <paramref name="delay"/> (from any thread).</summary>
    static void Soon(TimeSpan delay) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    });

    /// <summary>The weather put back in the menu bar: fetched now if it's old.</summary>
    public static void Shown()
    {
        if (_timer != null && !Hidden && (Temperature == null || DateTime.Now - Updated > TimeSpan.FromMinutes(15))) _ = RefreshAsync();
    }

    static bool Hidden => Settings.Current.MenuBarHidden.ContainsKey("weather");

    static int _gen;      // (one more each time the place changes: what's fetched for the place before is let go)
    static bool _again;   // (asked for while fetching: fetched again after)

    public static async Task RefreshAsync()
    {
        if (Hidden) return;
        if (_busy) { _again = true; return; }
        _busy = true;
        int gen = _gen;
        try
        {
            var loc = await LocationAsync();
            if (gen != _gen) return;
            if (loc == null) { Error = "Your location couldn’t be found. Choose a place in Settings › Control Center."; return; }
            var (lat, lon, place) = loc.Value;
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.####", CultureInfo.InvariantCulture) +
                         "&longitude=" + lon.ToString("0.####", CultureInfo.InvariantCulture) +
                         "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
                         "&hourly=temperature_2m,weather_code,is_day&forecast_hours=7" +
                         "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1" +
                         "&temperature_unit=fahrenheit&timezone=auto";
            string json = await Http.GetStringAsync(url);
            if (gen != _gen) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            Temperature = cur.GetProperty("temperature_2m").GetDouble();
            FeelsLike = Num(cur, "apparent_temperature");
            Code = cur.GetProperty("weather_code").GetInt32();
            IsDay = !cur.TryGetProperty("is_day", out var d) || d.GetInt32() == 1;
            High = Low = null;
            if (root.TryGetProperty("daily", out var daily))
            {
                High = First(daily, "temperature_2m_max");
                Low = First(daily, "temperature_2m_min");
            }
            var hours = new List<Hour>();
            if (root.TryGetProperty("hourly", out var h) && h.TryGetProperty("time", out var times))
            {
                var temps = h.GetProperty("temperature_2m");
                var codes = h.GetProperty("weather_code");
                h.TryGetProperty("is_day", out var days);
                for (int i = 0; i < times.GetArrayLength(); i++)
                {
                    if (temps[i].ValueKind != JsonValueKind.Number || codes[i].ValueKind != JsonValueKind.Number) continue;
                    if (!DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    bool day = days.ValueKind != JsonValueKind.Array || days[i].ValueKind != JsonValueKind.Number || days[i].GetInt32() == 1;
                    hours.Add(new Hour(t, temps[i].GetDouble(), codes[i].GetInt32(), day));
                }
            }
            Hours = hours;
            Place = place;
            Updated = DateTime.Now;
            Error = null;
        }
        catch (Exception ex)
        {
            if (gen == _gen) Error = ex is HttpRequestException or TaskCanceledException ? "The weather couldn’t be loaded. Are you online?" : "The weather couldn’t be loaded.";
        }
        finally
        {
            _busy = false;
            if (_again || gen != _gen) { _again = false; _ = RefreshAsync(); }
            else
            {
                // (the next look in 15 minutes - or in 1, after a failure)
                if (_timer != null) { _timer.Stop(); _timer.Interval = TimeSpan.FromMinutes(Error == null ? 15 : 1); _timer.Start(); }
                Changed?.Invoke();
            }
        }
    }

    static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static double? First(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.Number ? a[0].GetDouble() : null;

    /// <summary>The place chosen in Settings, else roughly where this computer is (from its internet address).</summary>
    static async Task<(double lat, double lon, string place)?> LocationAsync()
    {
        var s = Settings.Current;
        if (s.WeatherLat is double la && s.WeatherLon is double lo) return (la, lo, s.WeatherPlace);
        if (s.WeatherAutoLat is double a && s.WeatherAutoLon is double b && DateTime.UtcNow - s.WeatherAutoAt < TimeSpan.FromHours(6))
            return (a, b, s.WeatherAutoPlace);
        var found = await LookUpAsync("https://ipapi.co/json/") ?? await LookUpAsync("https://get.geojs.io/v1/ip/geo.json");
        if (found is { } f)
        {
            s.WeatherAutoLat = f.lat;
            s.WeatherAutoLon = f.lon;
            s.WeatherAutoPlace = f.place;
            s.WeatherAutoAt = DateTime.UtcNow;
            Settings.Save(notify: false);
            return f;
        }
        // (offline, or both refused: the last place found, however old)
        return s.WeatherAutoLat is double a2 && s.WeatherAutoLon is double b2 ? (a2, b2, s.WeatherAutoPlace) : null;
    }

    /// <summary>Where this computer is, from an IP-location service's JSON ("latitude"/"longitude": numbers or text).</summary>
    static async Task<(double lat, double lon, string place)?> LookUpAsync(string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var r = doc.RootElement;
            if (!Coord(r, "latitude", out double lat) || !Coord(r, "longitude", out double lon)) return null;
            string city = r.TryGetProperty("city", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            string region = r.TryGetProperty("region", out var rg) && rg.ValueKind == JsonValueKind.String ? rg.GetString() : null;
            return (lat, lon, string.Join(", ", new[] { city, region }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()));
        }
        catch { return null; }
    }

    static bool Coord(JsonElement r, string name, out double v)
    {
        v = 0;
        if (!r.TryGetProperty(name, out var e)) return false;
        if (e.ValueKind == JsonValueKind.Number) { v = e.GetDouble(); return true; }
        return e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>Places called <paramref name="name"/> - a city ("Austin", "Springfield, Illinois", "Springfield, IL")
    /// or a ZIP code - best match first: a name for showing, and where.</summary>
    public static async Task<List<(string name, double lat, double lon)>> SearchAsync(string name)
    {
        var found = new List<(string label, double lat, double lon, string city, string code)>();
        try
        {
            string term = name.Split(',')[0].Trim();
            bool zip = term.Length > 0 && term.All(char.IsDigit);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(term)}&count=20&language=en&format=json"));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return new();
            string hint = name.Contains(',') ? name[(name.IndexOf(',') + 1)..].Trim() : null;
            foreach (var r in results.EnumerateArray())
            {
                string city = r.GetProperty("name").GetString();
                string admin = r.TryGetProperty("admin1", out var a1) ? a1.GetString() : null;
                string code = r.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
                string country = r.TryGetProperty("country", out var co) ? co.GetString() : code;
                string label = string.Join(", ", new[] { city, admin, code == "US" ? null : country }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                if (hint?.Length > 0 && !label.Contains(hint, StringComparison.OrdinalIgnoreCase) && !string.Equals(code, hint, StringComparison.OrdinalIgnoreCase)
                    && !(code == "US" && string.Equals(UsState(hint), admin, StringComparison.OrdinalIgnoreCase))) continue;
                if (found.Any(x => x.label == label)) continue;
                found.Add((label, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), city, code));
            }
            // a ZIP code: America's (the same number is a postal code in other countries too); a name: the places
            // called that, not only sounding like it (misspelled: the near ones, then)
            var cmp = CultureInfo.InvariantCulture.CompareInfo;
            if (zip && found.Any(f => f.code == "US")) found = found.Where(f => f.code == "US").ToList();
            else if (!zip && found.Any(f => cmp.IsPrefix(f.city, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)))
                found = found.Where(f => cmp.IsPrefix(f.city, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)).ToList();
        }
        catch { }
        return found.Select(f => (f.label, f.lat, f.lon)).ToList();
    }

    /// <summary>A US state's name from its postal abbreviation ("TX" → "Texas").</summary>
    static string UsState(string abbr) => abbr.ToUpperInvariant() switch
    {
        "AL" => "Alabama", "AK" => "Alaska", "AZ" => "Arizona", "AR" => "Arkansas", "CA" => "California", "CO" => "Colorado",
        "CT" => "Connecticut", "DE" => "Delaware", "DC" => "District of Columbia", "FL" => "Florida", "GA" => "Georgia",
        "HI" => "Hawaii", "ID" => "Idaho", "IL" => "Illinois", "IN" => "Indiana", "IA" => "Iowa", "KS" => "Kansas",
        "KY" => "Kentucky", "LA" => "Louisiana", "ME" => "Maine", "MD" => "Maryland", "MA" => "Massachusetts",
        "MI" => "Michigan", "MN" => "Minnesota", "MS" => "Mississippi", "MO" => "Missouri", "MT" => "Montana",
        "NE" => "Nebraska", "NV" => "Nevada", "NH" => "New Hampshire", "NJ" => "New Jersey", "NM" => "New Mexico",
        "NY" => "New York", "NC" => "North Carolina", "ND" => "North Dakota", "OH" => "Ohio", "OK" => "Oklahoma",
        "OR" => "Oregon", "PA" => "Pennsylvania", "RI" => "Rhode Island", "SC" => "South Carolina", "SD" => "South Dakota",
        "TN" => "Tennessee", "TX" => "Texas", "UT" => "Utah", "VT" => "Vermont", "VA" => "Virginia", "WA" => "Washington",
        "WV" => "West Virginia", "WI" => "Wisconsin", "WY" => "Wyoming", "PR" => "Puerto Rico",
        _ => null,
    };

    /// <summary>A place chosen (no place: where this computer is, again), and the weather fetched for it.</summary>
    public static void SetPlace(string name, double? lat, double? lon)
    {
        var s = Settings.Current;
        _gen++;
        s.WeatherPlace = lat != null ? name : null;
        s.WeatherLat = lat;
        s.WeatherLon = lon;
        if (lat == null) s.WeatherAutoAt = default;   // (looked up afresh)
        Temperature = FeelsLike = High = Low = null;
        Hours = Array.Empty<Hour>();
        Place = null;
        Error = null;
        Settings.Save();
        Changed?.Invoke();
        _ = RefreshAsync();
    }

    /// <summary>The place shown: the one chosen, else the one found (null: not yet known).</summary>
    public static string PlaceName => Settings.Current.WeatherLat != null ? Settings.Current.WeatherPlace : Place ?? Settings.Current.WeatherAutoPlace;
    public static bool Automatic => Settings.Current.WeatherLat == null;

    /// <summary>"72°" (rounded; never "-0°").</summary>
    public static string Degrees(double? f) => f is double v ? (int)Math.Round(v, MidpointRounding.AwayFromZero) + "°" : "--°";

    /// <summary>The glyph for a WMO weather code, by day or night.</summary>
    public static string Symbol(int code, bool day) => code switch
    {
        0 => day ? "sun.max" : "moon.outline",
        1 or 2 => day ? "cloud.sun" : "cloud.moon",
        3 => "cloud",
        45 or 48 => "cloud.fog",
        51 or 53 or 55 or 56 or 57 => "cloud.drizzle",
        61 or 63 or 66 or 80 or 81 => "cloud.rain",
        65 or 67 or 82 => "cloud.heavyrain",
        71 or 73 or 75 or 77 or 85 or 86 => "cloud.snow",
        95 or 96 or 99 => "cloud.bolt.rain",
        _ => "cloud",
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly Clear", 2 => "Partly Cloudy", 3 => "Cloudy",
        45 or 48 => "Foggy",
        51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing Drizzle",
        61 => "Light Rain", 63 => "Rain", 65 => "Heavy Rain", 66 or 67 => "Freezing Rain",
        71 => "Light Snow", 73 => "Snow", 75 => "Heavy Snow", 77 => "Snow Grains",
        80 or 81 => "Rain Showers", 82 => "Heavy Showers", 85 or 86 => "Snow Showers",
        95 => "Thunderstorms", 96 or 99 => "Thunderstorms with Hail",
        _ => "",
    };
}
