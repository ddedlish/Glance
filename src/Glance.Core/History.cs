namespace Glance;

/// <summary>Battery sample. Stored only when something changes, so history is tiny.</summary>
public readonly record struct Sample(long T, byte L, byte F)
{
    public const byte Charging = 1, SessionStart = 2;
    public bool IsCharging => (F & Charging) != 0;
    public bool IsSessionStart => (F & SessionStart) != 0;
}

/// <summary>
/// Compact battery history (binary file) + discharge-rate estimator
/// ("хватит примерно на 3 ч 20 мин"). All calls on the UI thread.
/// </summary>
internal sealed class HistoryStore
{
    const int MaxPerDevice = 6000; // ≈ a month of changes per device (Pro history view)
    static readonly byte[] Magic = "GLH1"u8.ToArray();
    static string FilePath => Path.Combine(AppInfo.DataDir, "history.bin");

    readonly Dictionary<string, List<Sample>> _data = new(StringComparer.Ordinal);
    Debouncer? _saver;

    public HistoryStore() => Load();

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public IReadOnlyList<Sample> Get(string key) =>
        _data.TryGetValue(key, out var l) ? l : Array.Empty<Sample>();

    public void Add(string key, int level, bool charging, bool sessionStart)
    {
        if (!_data.TryGetValue(key, out var list)) _data[key] = list = new List<Sample>();
        byte flags = (byte)((charging ? Sample.Charging : 0) | (sessionStart ? Sample.SessionStart : 0));
        byte l = (byte)Math.Clamp(level, 0, 100);
        if (!sessionStart && list.Count > 0)
        {
            var last = list[^1];
            if (last.L == l && last.IsCharging == charging) return;
        }
        list.Add(new Sample(Now, l, flags));
        if (list.Count > MaxPerDevice) list.RemoveRange(0, list.Count - MaxPerDevice);
        ScheduleSave();
    }

    /// <summary>Test hook: inserts a sample with an explicit timestamp.</summary>
    internal void AddRaw(string key, Sample s)
    {
        if (!_data.TryGetValue(key, out var list)) _data[key] = list = new List<Sample>();
        list.Add(s);
    }

    public void Remove(string key)
    {
        if (_data.Remove(key)) ScheduleSave();
    }

    /// <summary>
    /// Time until empty (discharging) or full (charging), from a least-squares fit over the
    /// current monotonic segment of the current session. Null when data is insufficient.
    /// </summary>
    public TimeSpan? Estimate(string key, int level, bool charging)
    {
        var s = Get(key);
        if (s.Count < 2) return null;
        long now = Now;
        var xs = new List<double>();
        var ys = new List<double>();
        int prev = -1;
        for (int i = s.Count - 1; i >= 0; i--)
        {
            var x = s[i];
            if (x.IsCharging != charging) break;
            if (now - x.T > 12 * 3600) break;
            if (prev >= 0)
            {
                // Walking back in time: when discharging older values must be >= newer ones.
                if (!charging && x.L < prev) break;
                if (charging && x.L > prev) break;
            }
            xs.Add((x.T - now) / 3600.0);
            ys.Add(x.L);
            prev = x.L;
            if (x.IsSessionStart) break;
        }
        if (xs.Count < 2) return null;
        double span = xs[0] - xs[^1];
        double delta = Math.Abs(ys[0] - ys[^1]);
        if (span < 0.25 || delta < 5) return null;

        double mx = xs.Average(), my = ys.Average(), num = 0, den = 0;
        for (int i = 0; i < xs.Count; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
        if (den <= 0) return null;
        double slope = num / den; // % per hour
        double hours = charging ? (slope > 0.1 ? (100 - level) / slope : -1) : (slope < -0.1 ? level / -slope : -1);
        if (hours <= 0 || hours > 300) return null;
        return TimeSpan.FromHours(hours);
    }

    void ScheduleSave() => (_saver ??= new Debouncer(30_000, SaveNow)).TriggerOnce();

    public void SaveNow()
    {
        _saver?.Cancel();
        try
        {
            var tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Magic);
                w.Write(_data.Count);
                foreach (var (key, list) in _data)
                {
                    w.Write(key);
                    w.Write(list.Count);
                    foreach (var x in list) { w.Write(x.T); w.Write(x.L); w.Write(x.F); }
                }
            }
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log.Error("History save", e); }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var fs = File.OpenRead(FilePath);
            using var r = new BinaryReader(fs);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) return;
            int n = r.ReadInt32();
            for (int i = 0; i < n && i < 10_000; i++)
            {
                var key = r.ReadString();
                int c = r.ReadInt32();
                if (c < 0 || c > MaxPerDevice * 2) return;
                var list = new List<Sample>(c);
                for (int j = 0; j < c; j++) list.Add(new Sample(r.ReadInt64(), r.ReadByte(), r.ReadByte()));
                _data[key] = list;
            }
        }
        catch (Exception e)
        {
            Log.Error("History load", e);
            _data.Clear();
        }
    }
}

/// <summary>Statistics for the Pro history view (pure: unit-tested).</summary>
public static class HistoryStats
{
    public sealed record Result(double? DrainPerHour, double? HoursPerCharge, double Cycles, double? WearPercent, int Segments);

    /// <summary>
    /// Discharge segments = runs of falling level without charging (≥ 10 % drop, ≥ 20 min). Average drain is
    /// %/h over them; cycles = total discharged % / 100; wear compares the median drain rate of the latest third
    /// of segments with the earliest third (needs ≥ 6 segments over ≥ 7 days) — a faster drain for the same use
    /// means the battery holds less.
    /// </summary>
    public static Result Compute(IReadOnlyList<Sample> samples, long from, long now)
    {
        var segs = new List<(long T0, double Rate, int Drop)>();
        int i = 0;
        var xs = samples.Where(x => x.T >= from && x.T <= now).ToList();
        double discharged = 0;
        while (i < xs.Count)
        {
            int j = i;
            while (j + 1 < xs.Count && !xs[j + 1].IsCharging && !xs[j + 1].IsSessionStart && xs[j + 1].L <= xs[j].L && xs[j + 1].T - xs[j].T < 6 * 3600) j++;
            if (j > i && !xs[i].IsCharging)
            {
                int drop = xs[i].L - xs[j].L;
                double hours = (xs[j].T - xs[i].T) / 3600.0;
                discharged += drop;
                if (drop >= 10 && hours >= 1 / 3.0) segs.Add((xs[i].T, drop / hours, drop));
            }
            i = j + 1;
        }
        double? rate = null;
        if (segs.Count > 0)
        {
            double w = segs.Sum(x => x.Drop);
            rate = segs.Sum(x => x.Rate * x.Drop) / w;
        }
        double? wear = null;
        if (segs.Count >= 6 && segs[^1].T0 - segs[0].T0 >= 7 * 86400)
        {
            int third = segs.Count / 3;
            double early = Median(segs.Take(third).Select(x => x.Rate)), late = Median(segs.Skip(segs.Count - third).Select(x => x.Rate));
            if (early > 0) wear = Math.Clamp((late / early - 1) * 100, 0, 80);
        }
        return new Result(rate, rate is > 0 ? 100 / rate : null, discharged / 100.0, wear, segs.Count);
    }

    static double Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>CSV export (UTF-8 with BOM so Excel opens Cyrillic correctly).</summary>
    public static string Csv(string device, IReadOnlyList<Sample> samples)
    {
        var sb = new System.Text.StringBuilder("﻿Устройство;Время;Заряд, %;Зарядка\r\n");
        foreach (var x in samples)
            sb.Append(device.Replace(';', ',')).Append(';')
              .Append(DateTimeOffset.FromUnixTimeSeconds(x.T).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
              .Append(x.L).Append(';').Append(x.IsCharging ? "да" : "нет").Append("\r\n");
        return sb.ToString();
    }
}
