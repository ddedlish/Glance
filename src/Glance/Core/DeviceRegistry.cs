namespace Glance;

/// <summary>
/// Single source of truth for accessories. Merges classic + LE endpoints of one physical device,
/// applies readings from all providers with priorities and raises change events only on real changes.
/// UI thread only.
/// </summary>
internal sealed class DeviceRegistry
{
    readonly Dictionary<string, Accessory> _map = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _aepToKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _padMisses = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _rxMisses = new(StringComparer.Ordinal);
    bool _padsSeen, _rxSeen;
    readonly AppSettings _settings;
    readonly HistoryStore _history;

    public event Action<Accessory, bool>? DeviceConnected;   // (device, initialEnumeration)
    public event Action<Accessory>? DeviceDisconnected;
    public event Action<Accessory>? Changed;
    public event Action? ListChanged;

    public DeviceRegistry(AppSettings settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
    }

    public IEnumerable<Accessory> All => _map.Values;
    public IEnumerable<Accessory> ConnectedDevices => _map.Values.Where(a => a.Connected);
    public Accessory? Get(string key) => _map.TryGetValue(key, out var a) ? a : null;

    /// <summary>Restores devices seen in previous sessions (for "был в сети" + last level).</summary>
    public void LoadKnown()
    {
        foreach (var (key, d) in _settings.Devices)
        {
            if (_map.ContainsKey(key)) continue;
            var a = new Accessory(key)
            {
                Name = d.Name,
                CustomName = d.CustomName,
                KindHint = d.Kind,
                LastLevel = d.LastLevel,
                LastSeen = d.LastSeen,
                ViaReceiver = key.StartsWith("RX:", StringComparison.Ordinal),
            };
            a.Profile = Profiles.Match(a.Name, a.KindHint);
            _map[key] = a;
        }
        ListChanged?.Invoke();
    }

    // ───────────────────────────── Bluetooth endpoints ─────────────────────────────

    public void OnEndpoint(BtEndpoint ep, bool initial)
    {
        if (!_aepToKey.TryGetValue(ep.Id, out var key))
        {
            key = FindOrCreate(ep);
            _aepToKey[ep.Id] = key;
        }
        var a = _map[key];
        bool listChanged = false;

        if (!string.IsNullOrWhiteSpace(ep.Name) && ep.Name != a.Name && (a.Name.Length == 0 || !ep.IsLE || a.ClassicAepId is null))
        {
            a.Name = ep.Name;
            a.Profile = Profiles.Match(a.Name, a.KindHint);
            listChanged = true;
        }
        if (ep.IsLE) { a.LeAepId = ep.Id; if (ep.Address != 0) a.LeAddress = ep.Address; }
        else { a.ClassicAepId = ep.Id; if (ep.Address != 0) a.ClassicAddress = ep.Address; }
        a.Endpoints[ep.Id] = ep.Connected;

        bool was = a.Connected;
        bool now = a.Endpoints.Values.Any(c => c);
        if (now && !was)
        {
            a.Connected = true;
            a.ConnectedAt = DateTime.Now;
            a.LastSeen = DateTime.Now;
            a.SystemLevel = null; a.SystemRank = 0; a.SystemCharging = false; a.SystemSource = null;
            ClearParts(a);
            a.SessionStartPending = true;
            Bump(a);
            DeviceConnected?.Invoke(a, initial);
            listChanged = true;
        }
        else if (!now && was)
        {
            MarkDisconnected(a);
            listChanged = true;
        }

        if (listChanged) ListChanged?.Invoke();
    }

    public void OnEndpointRemoved(string aepId)
    {
        if (!_aepToKey.Remove(aepId, out var key) || !_map.TryGetValue(key, out var a)) return;
        a.Endpoints.Remove(aepId);
        if (a.ClassicAepId == aepId) a.ClassicAepId = null;
        if (a.LeAepId == aepId) a.LeAepId = null;
        if (a.Endpoints.Count == 0)
        {
            // Unpaired in Windows: forget completely.
            if (a.Connected) MarkDisconnected(a);
            _map.Remove(key);
            _settings.Devices.Remove(key);
            _settings.Save();
            _history.Remove(key);
        }
        ListChanged?.Invoke();
    }

    /// <summary>After both watchers enumerated: drop remembered devices that are no longer paired.</summary>
    public void PruneUnpaired()
    {
        var stale = _map.Values.Where(a => a.Key.StartsWith("BT:", StringComparison.Ordinal) && a.Endpoints.Count == 0 && !a.Connected).Select(a => a.Key).ToList();
        foreach (var k in stale)
        {
            _map.Remove(k);
            _settings.Devices.Remove(k);
            _history.Remove(k);
        }
        if (stale.Count > 0) { _settings.Save(); ListChanged?.Invoke(); }
    }

    string FindOrCreate(BtEndpoint ep)
    {
        string key = ep.Address != 0 ? $"BT:{ep.Address:X12}" : "AEP:" + ep.Id;
        if (_map.ContainsKey(key)) return key;

        // Dual-mode devices expose a classic AND an LE endpoint (often with different addresses).
        // Merge them into one accessory when the name matches and the other protocol slot is free.
        if (!string.IsNullOrWhiteSpace(ep.Name))
        {
            var twin = _map.Values.FirstOrDefault(a =>
                a.Endpoints.Count > 0 &&
                string.Equals(Profiles.NormalizeName(a.Name), Profiles.NormalizeName(ep.Name), StringComparison.OrdinalIgnoreCase) &&
                (ep.IsLE ? a.LeAepId is null : a.ClassicAepId is null));
            if (twin is not null) return twin.Key;
        }

        var acc = new Accessory(key) { Name = ep.Name };
        var ds = _settings.For(key);
        acc.CustomName = ds.CustomName;
        acc.KindHint = ds.Kind;
        acc.LastLevel = ds.LastLevel;
        acc.LastSeen = ds.LastSeen;
        acc.Profile = Profiles.Match(acc.Name, acc.KindHint);
        _map[key] = acc;
        return key;
    }

    void MarkDisconnected(Accessory a)
    {
        a.Connected = false;
        a.LastSeen = DateTime.Now;
        if (a.Level is int l) a.LastLevel = l;
        a.SystemLevel = null; a.SystemRank = 0; a.SystemCharging = false;
        ClearParts(a);
        a.BudsPending = false;
        Persist(a);
        Bump(a);
        DeviceDisconnected?.Invoke(a);
    }

    static void ClearParts(Accessory a)
    {
        a.Left = a.Right = a.Case = Part.Unknown;
        a.PartsSource = null;
        a.BudsModel = null;
    }

    // ───────────────────────────── Readings ─────────────────────────────

    public void SetKindHint(Accessory a, DeviceKind kind)
    {
        if (a.KindHint == kind) return;
        a.KindHint = kind;
        a.Profile = Profiles.Match(a.Name, kind);
        Persist(a);
        Bump(a);
        ListChanged?.Invoke();
    }

    /// <summary>Battery reported by Windows (HFP / LE HID) for connected devices, keyed by BT address.</summary>
    public void ApplyPnp(IReadOnlyDictionary<ulong, int> levels)
    {
        foreach (var a in _map.Values)
        {
            if (!a.Connected || a.SystemRank > 1) continue;
            int lv;
            if (a.ClassicAddress != 0 && levels.TryGetValue(a.ClassicAddress, out lv)) { }
            else if (a.LeAddress != 0 && levels.TryGetValue(a.LeAddress, out lv)) { }
            else continue;
            SetSystem(a, lv, false, "Windows", 1);
        }
    }

    /// <summary>GATT Battery Service. Several services = split device (left/right halves).</summary>
    public void ApplyGatt(Accessory a, int[] levels)
    {
        if (!a.Connected || levels.Length == 0) return;
        if (levels.Length >= 2)
        {
            var l = new Part(levels[0], false);
            var r = new Part(levels[1], false);
            if (a.Left != l || a.Right != r)
            {
                a.Left = l; a.Right = r; a.PartsAtUtc = DateTime.UtcNow; a.PartsSource = "GATT";
                SetSystem(a, levels.Min(), false, "GATT", 2, force: true);
                return;
            }
        }
        SetSystem(a, levels.Min(), false, "GATT", 2);
    }

    public void ApplyGamepads(List<GamepadReading> pads)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pads)
        {
            // Prefer attaching to the Bluetooth device with the same name (Xbox controller over BT).
            var a = _map.Values.FirstOrDefault(x => x.GamepadId == p.Id)
                ?? _map.Values.FirstOrDefault(x => x.Connected && x.GamepadId is null && x.Key.StartsWith("BT:") &&
                                                   (x.Kind == DeviceKind.Gamepad || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)));
            if (a is null)
            {
                var key = "GP:" + p.Id;
                if (!_map.TryGetValue(key, out a))
                {
                    a = new Accessory(key) { Name = p.Name, KindHint = DeviceKind.Gamepad };
                    var ds = _settings.For(key);
                    a.CustomName = ds.CustomName;
                    a.Profile = Profiles.Match(p.Name, DeviceKind.Gamepad) with { Kind = DeviceKind.Gamepad };
                    _map[key] = a;
                }
            }
            a.GamepadId = p.Id;
            a.LinkHint = p.Link;
            seen.Add(a.Key);
            if (a.Key.StartsWith("GP:") && !a.Connected)
            {
                a.Connected = true; a.ConnectedAt = a.LastSeen = DateTime.Now; a.SessionStartPending = true;
                Bump(a);
                DeviceConnected?.Invoke(a, !_padsSeen); // already plugged in at startup → no popup
                ListChanged?.Invoke();
            }
            SetSystem(a, p.Level, p.Charging, "Gamepad", 2);
        }
        _padsSeen = true;

        foreach (var k in seen) _padMisses.Remove(k);
        foreach (var a in _map.Values.Where(x => x.Key.StartsWith("GP:") && x.Connected && !seen.Contains(x.Key)).ToList())
        {
            // HID reads can time out once; require two consecutive misses before calling it disconnected.
            int miss = _padMisses.TryGetValue(a.Key, out var m) ? m + 1 : 1;
            _padMisses[a.Key] = miss;
            if (miss < 2) continue;
            _padMisses.Remove(a.Key);
            MarkDisconnected(a);
            ListChanged?.Invoke();
        }
    }

    /// <summary>2.4 GHz receivers: listed as connected while the receiver is plugged in; battery when the device reports it.</summary>
    public void ApplyReceivers(List<ReceiverReading> list)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in list)
        {
            if (!_map.TryGetValue(r.Id, out var a))
            {
                a = new Accessory(r.Id) { Name = r.Name, KindHint = r.Kind, ViaReceiver = true };
                var ds = _settings.For(r.Id);
                a.CustomName = ds.CustomName;
                a.LastLevel = ds.LastLevel;
                a.Profile = Profiles.Match(r.Name, r.Kind) with { Buds = BudsSource.None };
                if (a.Profile.Kind == DeviceKind.Unknown) a.Profile = a.Profile with { Kind = r.Kind };
                _map[r.Id] = a;
            }
            if (a.Name != r.Name && r.Name.Length > 0)
            {
                a.Name = r.Name; // e.g. «2.4G Wireless Device» → «Attack Shark» once the vendor battery is recognised
                a.Profile = Profiles.Match(r.Name, r.Kind) with { Buds = BudsSource.None };
                if (a.Profile.Kind == DeviceKind.Unknown) a.Profile = a.Profile with { Kind = r.Kind };
                ListChanged?.Invoke();
            }
            if (a.Wired != r.Wired) { a.Wired = r.Wired; ListChanged?.Invoke(); }
            seen.Add(r.Id);
            _rxMisses.Remove(r.Id);
            if (!a.Connected)
            {
                a.Connected = true; a.ConnectedAt = a.LastSeen = DateTime.Now; a.SessionStartPending = true;
                Bump(a);
                DeviceConnected?.Invoke(a, !_rxSeen);
                ListChanged?.Invoke();
            }
            if (r.Level is int lv) SetSystem(a, lv, r.Charging, "Receiver", 2);
        }
        _rxSeen = true;
        foreach (var a in _map.Values.Where(x => x.Key.StartsWith("RX:") && x.Connected && !seen.Contains(x.Key)).ToList())
        {
            int miss = _rxMisses.TryGetValue(a.Key, out var m) ? m + 1 : 1;
            _rxMisses[a.Key] = miss;
            if (miss < 2) continue;
            _rxMisses.Remove(a.Key);
            MarkDisconnected(a);
            ListChanged?.Invoke();
        }
    }

    public void ApplyBuds(Accessory a, BudsAdvert adv)
    {
        if (!a.Connected) return;
        bool changed = a.Left != adv.Left || a.Right != adv.Right || a.Case != adv.Case || a.BudsModel != adv.Model || a.BudsPending || a.PartsStale;
        a.PartsAtUtc = DateTime.UtcNow;
        if (!changed) return;
        a.Left = adv.Left; a.Right = adv.Right; a.Case = adv.Case;
        a.BudsModel = adv.Model;
        a.PartsSource = adv.Vendor == BudsSource.AppleContinuity ? "Apple" : "Fast Pair";
        a.BudsPending = false;
        Bump(a);
    }

    public void SetPending(Accessory a, bool pending)
    {
        if (a.BudsPending == pending) return;
        a.BudsPending = pending;
        Bump(a);
    }

    public void Touch(Accessory a)
    {
        var ds = _settings.For(a.Key);
        a.CustomName = ds.CustomName;
        Bump(a);
        ListChanged?.Invoke();
    }

    public void Forget(Accessory a)
    {
        if (a.Connected) return;
        _map.Remove(a.Key);
        foreach (var id in a.Endpoints.Keys) _aepToKey.Remove(id);
        _settings.Devices.Remove(a.Key);
        _settings.Save();
        _history.Remove(a.Key);
        ListChanged?.Invoke();
    }

    void SetSystem(Accessory a, int level, bool charging, string source, int rank, bool force = false)
    {
        level = Math.Clamp(level, 0, 100);
        if (!force && a.SystemLevel == level && a.SystemCharging == charging && a.SystemRank == rank) return;
        a.SystemLevel = level;
        a.SystemCharging = charging;
        a.SystemRank = rank;
        a.SystemSource = source;
        Bump(a);
    }

    void Persist(Accessory a)
    {
        var d = _settings.For(a.Key);
        d.Name = a.Name;
        d.Kind = a.KindHint;
        d.LastSeen = a.LastSeen;
        if (a.Level is int l) d.LastLevel = l;
        else if (a.LastLevel is int ll) d.LastLevel = ll;
        _settings.Save();
    }

    void Bump(Accessory a)
    {
        a.Version++;
        if (a.Connected && a.Level is int l)
        {
            a.LastLevel = l;
            a.LastSeen = DateTime.Now;
        }
        Changed?.Invoke(a);
    }

    public void PersistAll()
    {
        foreach (var a in _map.Values.Where(x => x.Connected)) Persist(a);
    }
}
