namespace Glance;

/// <summary>
/// Apple "proximity pairing" advertisement (AirPods / Beats).
/// Layout after company ID 0x004C: 07 19 | prefix | model(2) | status | pods | flags+case | lid…
/// Values are 0–10 (×10 %), 15 = not available.
/// </summary>
internal static class AppleContinuity
{
    public static bool TryParse(ReadOnlySpan<byte> d, out BudsAdvert result)
    {
        result = default;
        if (d.Length < 9 || d[0] != 0x07 || d[1] != 0x19) return false;

        ushort model = (ushort)((d[3] << 8) | d[4]);
        bool flip = (d[5] & 0x20) == 0;           // which nibble is the left bud
        int hi = d[6] >> 4, lo = d[6] & 0x0F;
        int left = flip ? hi : lo;
        int right = flip ? lo : hi;
        int caseV = d[7] & 0x0F;
        int chg = d[7] >> 4;
        bool cL = (chg & (flip ? 0b010 : 0b001)) != 0;
        bool cR = (chg & (flip ? 0b001 : 0b010)) != 0;
        bool cC = (chg & 0b100) != 0;

        static int? V(int v) => v <= 10 ? v * 10 : null;
        var l = new Part(V(left), cL && V(left) is not null);
        var r = new Part(V(right), cR && V(right) is not null);
        var c = new Part(V(caseV), cC && V(caseV) is not null);
        if (!l.Known && !r.Known && !c.Known) return false;

        result = new BudsAdvert(BudsSource.AppleContinuity, l, r, c, AppleModels.Name(model));
        return true;
    }
}

/// <summary>
/// Google Fast Pair (service 0xFE2C) "battery notification" inside the account-key advertisement.
/// Used by JBL, Sony, Pixel Buds, Nothing, Xiaomi, OnePlus, Soundcore and many others.
/// Field header = 0bLLLLTTTT; battery type 0b0011 (show UI) or 0b0100 (hide UI), 3 bytes: L, R, Case.
/// Each byte: bit7 = charging, bits0-6 = 0..100, 0x7F = unknown.
/// </summary>
internal static class FastPair
{
    public static bool TryParse(ReadOnlySpan<byte> p, out BudsAdvert result)
    {
        result = default;
        if (p.Length < 5) return false;            // 3 bytes = model ID only (pairing mode)
        int i = 1;                                  // skip version/flags byte
        while (i < p.Length)
        {
            int header = p[i];
            int len = header >> 4, type = header & 0x0F;
            if (i + 1 + len > p.Length) break;
            if ((type == 0b0011 || type == 0b0100) && len == 3)
            {
                var l = Decode(p[i + 1]);
                var r = Decode(p[i + 2]);
                var c = Decode(p[i + 3]);
                if (!l.Known && !r.Known && !c.Known) return false;
                result = new BudsAdvert(BudsSource.FastPair, l, r, c, null);
                return true;
            }
            i += 1 + len;
        }
        return false;
    }

    static Part Decode(byte b)
    {
        int v = b & 0x7F;
        return v > 100 ? Part.Unknown : new Part(v, (b & 0x80) != 0);
    }
}

/// <summary>
/// Earbud advertisements use random addresses, so they are matched to the connected device by
/// signal strength (smoothed), model affinity and agreement with the Windows-reported level,
/// with stickiness to avoid flapping between neighbours' earbuds.
/// </summary>
internal sealed class BudsMatcher
{
    sealed class Candidate { public BudsAdvert Adv; public double Rssi; }

    readonly Dictionary<ulong, Candidate> _cands = new();
    readonly Dictionary<string, ulong> _sticky = new(StringComparer.Ordinal);

    public List<(Accessory Target, BudsAdvert Adv)> Match(BudsAdvert adv, IReadOnlyList<Accessory> targets, int threshold)
    {
        if (_cands.TryGetValue(adv.Address, out var c)) { c.Adv = adv; c.Rssi = c.Rssi * 0.6 + adv.Rssi * 0.4; }
        else _cands[adv.Address] = new Candidate { Adv = adv, Rssi = adv.Rssi };

        var now = DateTime.UtcNow;
        foreach (var k in _cands.Where(p => (now - p.Value.Adv.AtUtc).TotalSeconds > 30).Select(p => p.Key).ToList())
            _cands.Remove(k);

        var result = new List<(Accessory, BudsAdvert)>();
        foreach (var vendor in new[] { BudsSource.AppleContinuity, BudsSource.FastPair })
        {
            var tlist = targets.Where(t => t.Profile.Buds == vendor).ToList();
            if (tlist.Count == 0) continue;
            var clist = _cands.Values.Where(x => x.Adv.Vendor == vendor && (now - x.Adv.AtUtc).TotalSeconds <= 20 && x.Rssi >= threshold).ToList();
            if (clist.Count == 0) continue;

            var used = new HashSet<ulong>();
            var assigned = new HashSet<string>(StringComparer.Ordinal);

            // 1. Keep sticky pairs unless a clearly better candidate exists.
            foreach (var t in tlist)
            {
                if (!_sticky.TryGetValue(t.Key, out var addr)) continue;
                var sc = clist.FirstOrDefault(x => x.Adv.Address == addr);
                if (sc is null || used.Contains(addr)) continue;
                double best = clist.Where(x => !used.Contains(x.Adv.Address)).Select(x => Score(t, x)).DefaultIfEmpty(double.MinValue).Max();
                if (best - Score(t, sc) < 12)
                {
                    used.Add(addr); assigned.Add(t.Key);
                    result.Add((t, sc.Adv));
                }
            }

            // 2. Greedy assignment of the rest by score.
            var pairs = (from t in tlist where !assigned.Contains(t.Key)
                         from x in clist where !used.Contains(x.Adv.Address)
                         select (t, x, s: Score(t, x))).OrderByDescending(p => p.s).ToList();
            foreach (var (t, x, _) in pairs)
            {
                if (assigned.Contains(t.Key) || used.Contains(x.Adv.Address)) continue;
                assigned.Add(t.Key); used.Add(x.Adv.Address);
                _sticky[t.Key] = x.Adv.Address;
                result.Add((t, x.Adv));
            }
        }
        return result;
    }

    static double Score(Accessory t, Candidate c)
    {
        double s = c.Rssi;
        if (c.Adv.Vendor == BudsSource.AppleContinuity) s += AppleModels.Affinity(t.Name, c.Adv.Model);
        if (t.SystemLevel is int sys)
        {
            int? best = null;
            foreach (var p in new[] { c.Adv.Left, c.Adv.Right })
                if (p.Level is int l) { int d = Math.Abs(l - sys); if (best is null || d < best) best = d; }
            if (best is int diff) s -= diff > 25 ? 40 : diff * 0.5;
        }
        return s;
    }

    public void Forget(string key) => _sticky.Remove(key);
}
