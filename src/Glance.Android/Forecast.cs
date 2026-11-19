using Android.OS;

namespace Glance.Droid;

/// <summary>
/// Battery history of the accessories (HistoryStore from Glance.Core — one per process, so the screen and the shade
/// service write the same small file) and the forecast built on it: «хватит на ≈ 2 ч 40 мин · до 19:30» while
/// discharging, «до полной ≈ 45 мин» while charging. Learns from the live slope of this session, and until that has
/// enough data, from how fast the very same device usually drains. Main thread only.
/// </summary>
internal static class Forecast
{
    static HistoryStore? _store;
    static readonly HashSet<string> _live = new(StringComparer.Ordinal); // keys recorded as connected (session tracking)

    static HistoryStore Store
    {
        get
        {
            if (_store is null)
            {
                // the history saves itself through Dispatch.Post: on Android that is the main thread, like every Add
                var main = new Handler(Looper.MainLooper!);
                Dispatch.Post = a => main.Post(() => { try { a(); } catch (Exception e) { Log.Error("Android dispatch", e); } });
                _store = new HistoryStore();
            }
            return _store;
        }
    }

    /// <summary>Stores the levels of what is connected now (a sample only when something changed).</summary>
    public static void Record(Devices? dev)
    {
        if (dev is null || dev.Demo) return;
        try
        {
            var now = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in dev.Connected)
            {
                now.Add(a.Key);
                if (a.WeakestLevel is not int l || a.PartsStale) continue;
                Store.Add(a.Key, l, a.Charging, sessionStart: !_live.Contains(a.Key));
            }
            _live.Clear();
            _live.UnionWith(now);
        }
        catch (Exception e) { Log.Error("Android forecast record", e); }
    }

    public static void Save()
    {
        try { _store?.SaveNow(); } catch (Exception e) { Log.Error("Android forecast save", e); }
    }

    /// <summary>Time until empty (or until full while charging); null while there is not enough history.</summary>
    public static TimeSpan? Estimate(Accessory a, bool demo)
    {
        if (a.WeakestLevel is not int l) return null;
        bool charging = a.Charging;
        if (charging && l >= 100) return null;
        if (demo) return DemoEstimate(a.Kind, l, charging);
        try { return Store.Estimate(a.Key, l, charging); }
        catch (Exception e) { Log.Error("Android forecast", e); return null; }
    }

    /// <summary>Demo mode (self-test, screenshots): typical speeds of such devices.</summary>
    static TimeSpan DemoEstimate(DeviceKind kind, int level, bool charging)
    {
        double rate = charging ? 48 : kind switch
        {
            DeviceKind.Watch => 6.5,
            DeviceKind.Earbuds or DeviceKind.Headphones or DeviceKind.Headset or DeviceKind.Neckband => 11,
            DeviceKind.Gamepad => 9,
            DeviceKind.Speaker => 7.5,
            _ => 3,
        };
        return TimeSpan.FromHours((charging ? 100 - level : level) / rate);
    }

    /// <summary>«2 ч 40 мин», «45 мин», «18 ч», «3 дн» — rounded, a forecast is not a stopwatch.</summary>
    public static string Span(TimeSpan t)
    {
        if (t.TotalHours >= 48) return $"{(int)Math.Round(t.TotalDays)} дн";
        if (t.TotalHours >= 10) return $"{(int)Math.Round(t.TotalHours)} ч";
        int min = Math.Max(5, (int)Math.Round(t.TotalMinutes / 5) * 5);
        int h = min / 60, m = min % 60;
        return h == 0 ? $"{m} мин" : m == 0 ? $"{h} ч" : $"{h} ч {m} мин";
    }

    /// <summary>For a card: «≈ 2 ч 40 мин» or «до 100% · 15 мин».</summary>
    public static string? Short(Accessory a, bool demo) =>
        Estimate(a, demo) is TimeSpan t ? (a.Charging ? $"до 100% · {Span(t)}" : $"≈ {Span(t)}") : null;

    /// <summary>For the hero and notices: («Хватит на ≈ 2 ч 40 мин», « · до 19:30»).</summary>
    public static (string Main, string Clock)? Long(Accessory a, bool demo)
    {
        if (Estimate(a, demo) is not TimeSpan t) return null;
        string main = a.Charging ? $"До полной ≈ {Span(t)}" : $"Хватит на ≈ {Span(t)}";
        string clock = Fmt.ClockAt(t) is string at ? (a.Charging ? $" · к {at}" : $" · до {at}") : "";
        return (main, clock);
    }
}
