using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Apps;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Shell;

/// <summary>
/// The menu bar weather's drop-down: the place, the temperature and the sky, today's high and low, the next hours; and
/// Change Location… (a city or a ZIP code) / Use My Location.
/// </summary>
public class WeatherPopover : Popover
{
    static WeatherPopover _instance;
    static DateTime _closedAt;
    public static bool IsOpen => _instance != null;

    public static void Toggle(MenuBarWindow.StatusButton anchor)
    {
        if (_instance != null) { _instance.Close(); return; }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;
        _instance = new WeatherPopover(anchor);
        _instance.Show();
        _instance.Activate();
    }

    readonly StackPanel _root = new() { Margin = new Thickness(4, 2, 4, 2) };

    WeatherPopover(MenuBarWindow.StatusButton anchor) : base(260)
    {
        Title = "Weather";
        Card.Child = _root;
        Build();
        Weather.Changed += Build;
        Closed += (_, _) => { Weather.Changed -= Build; _instance = null; _closedAt = DateTime.Now; };
        PlaceUnder(anchor, alignRightEdge: false);
        if (!Weather.Current) _ = Weather.RefreshAsync();
    }

    void Build()
    {
        _root.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 4, 0) };
        string place = Weather.PlaceName;
        head.Children.Add(T(string.IsNullOrEmpty(place) ? "Weather" : place, 13, FontWeights.SemiBold));
        if (Weather.Automatic && !string.IsNullOrEmpty(place))
        {
            var arrow = new SymbolIcon { Symbol = "location.fill", Width = 10, Height = 10, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Your location (about where this computer is)" };
            arrow.SetResourceReference(SymbolIcon.ForegroundProperty, "SecondaryLabelBrush");
            head.Children.Add(arrow);
        }
        _root.Children.Add(head);

        if (Weather.Current)
        {
            var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 8, 0, 0) };
            big.Children.Add(new SymbolIcon { Symbol = Weather.Symbol(Weather.Code, Weather.IsDay), Width = 36, Height = 36, StrokeWidth = 1.5, VerticalAlignment = VerticalAlignment.Center });
            var temp = T(Weather.Degrees(Weather.Temperature), 40, FontWeights.Light);
            temp.Margin = new Thickness(10, -6, 0, -4);
            temp.VerticalAlignment = VerticalAlignment.Center;
            big.Children.Add(temp);
            _root.Children.Add(big);
            var sky = T(Weather.Describe(Weather.Code), 13);
            sky.Margin = new Thickness(6, 6, 0, 0);
            _root.Children.Add(sky);
            string more = $"H:{Weather.Degrees(Weather.High)}  L:{Weather.Degrees(Weather.Low)}";
            if (Weather.FeelsLike is double f && Math.Abs(f - Weather.Temperature.Value) >= 2) more += $"   Feels like {Weather.Degrees(f)}";
            var hl = T(more, 12, brush: "SecondaryLabelBrush");
            hl.Margin = new Thickness(6, 2, 0, 0);
            _root.Children.Add(hl);
            if (Weather.Hours.Count > 1) _root.Children.Add(HoursRow());
        }
        else
        {
            var msg = T(Weather.Error ?? "Getting the weather…", 12, brush: "SecondaryLabelBrush");
            msg.TextWrapping = TextWrapping.Wrap;
            msg.Margin = new Thickness(6, 6, 6, 4);
            _root.Children.Add(msg);
            if (Weather.Error != null) _root.Children.Add(Link("Try Again", () => _ = Weather.RefreshAsync()));
        }

        _root.Children.Add(Separator());
        _root.Children.Add(Link("Change Location…", () => { Close(); ChangeLocation(null); }));
        if (!Weather.Automatic) _root.Children.Add(Link("Use My Location", () => Weather.SetPlace(null, null, null)));
        if (Weather.Updated != default)
        {
            var when = T("Updated " + Weather.Updated.ToString(Weather.Updated.Date == DateTime.Today ? "h:mm tt" : "MMM d, h:mm tt"), 11, brush: "TertiaryLabelBrush");
            when.Margin = new Thickness(8, 4, 0, 2);
            _root.Children.Add(when);
        }
    }

    /// <summary>Now and the next five hours: the hour, the sky, the temperature.</summary>
    UIElement HoursRow()
    {
        var hours = Weather.Hours.Take(6).ToList();
        var grid = new UniformGrid { Columns = hours.Count, Rows = 1, Margin = new Thickness(0, 10, 0, 4) };
        for (int i = 0; i < hours.Count; i++)
        {
            var h = hours[i];
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var label = T(i == 0 ? "Now" : h.Time.ToString("htt").ToUpperInvariant(), 11, FontWeights.Medium, "SecondaryLabelBrush");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(label);
            // (now: as it is now - this hour's forecast was made before it began)
            col.Children.Add(new SymbolIcon { Symbol = i == 0 ? Weather.Symbol(Weather.Code, Weather.IsDay) : Weather.Symbol(h.Code, h.IsDay), Width = 18, Height = 18, StrokeWidth = 1.7, Margin = new Thickness(0, 5, 0, 5), HorizontalAlignment = HorizontalAlignment.Center });
            var t = T(Weather.Degrees(i == 0 ? Weather.Temperature : h.Temp), 12, FontWeights.Medium);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(t);
            grid.Children.Add(col);
        }
        return grid;
    }

    static ImageSource Icon => MacIcons.Tile("cloud.sun", "#5DB6FF", "#1A73E8");

    /// <summary>Asks for a city or a ZIP code and uses it for the weather (several places of that name: which one).</summary>
    public static async void ChangeLocation(Window owner, Action done = null)
    {
        string typed = TextPrompt.Ask(owner, "Weather Location", "Type a city or a ZIP code, like “Austin”, “Springfield, IL” or “10001”.", Weather.Automatic ? "" : Settings.Current.WeatherPlace)?.Trim();
        if (string.IsNullOrEmpty(typed)) return;
        List<(string name, double lat, double lon)> found;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try { found = await Weather.SearchAsync(typed); }
        finally { Mouse.OverrideCursor = null; }
        if (found.Count == 0)
        {
            new MacAlert($"“{typed}” wasn’t found.", "Check the spelling or your internet connection. For a common name, add the state: “Springfield, IL”.", new[] { "OK" }, Icon).ShowDialog();
            return;
        }
        var pick = found[0];
        if (found.Count > 1)
        {
            var choices = found.Take(6).ToList();
            var a = new MacAlert("Which one?", $"More than one place is called “{typed}”.", choices.Select(c => c.name).Append("Cancel").ToArray(), Icon, defaultButton: 0);
            a.ShowDialog();
            int i = choices.FindIndex(c => c.name == a.Result);
            if (i < 0) return;
            pick = choices[i];
        }
        Weather.SetPlace(pick.name, pick.lat, pick.lon);
        done?.Invoke();
    }
}
