using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Glance.Droid;

/// <summary>
/// Accessories seen by the phone: bonded Bluetooth devices that are connected (level from the system battery
/// report), per-bud levels of AirPods / Fast Pair earbuds from BLE adverts, and the phone itself.
/// Everything runs on the main thread; Bluetooth callbacks are posted to it.
/// </summary>
internal sealed class Devices
{
    readonly Context _ctx;
    readonly Handler _main = new(Looper.MainLooper!);
    readonly Dictionary<string, Accessory> _all = new(StringComparer.Ordinal);
    Receiver? _rx;
    BudsScan? _scan;
    BluetoothLeScanner? _scanner;

    public event Action? Changed;
    public bool Demo { get; private set; }

    /// <summary>Runs for the shade notification while the app is closed: balanced BLE scanning (updates within seconds, little battery).</summary>
    public bool Background { get; init; }
    public bool BluetoothOff { get; private set; }
    public bool NoBluetooth { get; private set; }

    /// <summary>The phone's own battery (shown as the last card).</summary>
    public int? PhoneLevel { get; private set; }
    public bool PhoneCharging { get; private set; }

    public Devices(Context ctx)
    {
        _ctx = ctx;
        _gatt = new GattBattery(ctx, _main, OnGattLevel);
    }

    readonly GattBattery _gatt;
    readonly HashSet<string> _bonded = new(StringComparer.Ordinal);

    /// <summary>
    /// Paired with the phone but not connected right now: shown under «Можно подключить», the most recently used first.
    /// </summary>
    public IReadOnlyList<Accessory> Available => _all.Values.Where(a => !a.Connected && _bonded.Contains(a.Key))
        .OrderByDescending(a => a.LastSeen)
        .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(8).ToList();

    /// <summary>The device a tap is connecting right now (cleared when it connects or after a timeout).</summary>
    public string? Connecting { get; private set; }

    /// <summary>Connected, but reports no battery anywhere (no system level, no Battery Service): «не сообщает заряд».</summary>
    public bool Silent(Accessory a) => !Demo && a.Level is null && !a.HasParts && _gatt.Failed(a.Key);

    public IReadOnlyList<Accessory> Connected => _all.Values.Where(a => a.Connected)
        .OrderByDescending(a => a.Level.HasValue || a.HasParts)
        .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    // ───────── permissions ─────────

    public static string[] NeededPermissions => Build.VERSION.SdkInt >= BuildVersionCodes.S
        ? new[] { "android.permission.BLUETOOTH_CONNECT", "android.permission.BLUETOOTH_SCAN" }
        : new[] { "android.permission.ACCESS_FINE_LOCATION" };

    public bool CanConnect => Build.VERSION.SdkInt < BuildVersionCodes.S || Granted("android.permission.BLUETOOTH_CONNECT");
    bool CanScan => Build.VERSION.SdkInt >= BuildVersionCodes.S ? Granted("android.permission.BLUETOOTH_SCAN") : Granted("android.permission.ACCESS_FINE_LOCATION");
    bool Granted(string p) => _ctx.CheckSelfPermission(p) == Permission.Granted;
    public bool MissingPermission => !Demo && !CanConnect;

    // ───────── lifecycle ─────────

    public void Start()
    {
        if (_rx is null)
        {
            _rx = new Receiver(i => _main.Post(() => OnBroadcast(i)));
            var f = new IntentFilter();
            f.AddAction("android.bluetooth.device.action.BATTERY_LEVEL_CHANGED");
            f.AddAction(BluetoothDevice.ActionAclConnected);
            f.AddAction(BluetoothDevice.ActionAclDisconnected);
            f.AddAction(BluetoothAdapter.ActionStateChanged);
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(33)) _ctx.RegisterReceiver(_rx, f, ReceiverFlags.Exported);
                else _ctx.RegisterReceiver(_rx, f);
            }
            catch (Exception e) { Log.Error("Android receiver", e); }
        }
        Refresh();
        if (_scan is null) _burstUntil = DateTime.UtcNow.AddSeconds(30); // the first half-minute listens at full speed
        StartScan();
    }

    public void Stop()
    {
        StopScan();
        if (_rx is not null)
        {
            try { _ctx.UnregisterReceiver(_rx); } catch { }
            _rx = null;
        }
    }

    void OnBroadcast(Intent i)
    {
        if (i.Action == Intent.ActionBatteryChanged) return; // the phone's own battery is not shown
        if (i.Action == BluetoothDevice.ActionAclConnected) Burst(); // earbuds just out of the case: catch the case level at once
        // The battery report and connection changes arrive a moment before the hidden getters update.
        Refresh();
        _main.PostDelayed(Refresh, 1500);
    }

    // ───────── system battery of bonded devices ─────────

    public void Refresh()
    {
        if (Demo) { Changed?.Invoke(); return; }
        try
        {
            using var sticky = _ctx.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            if (sticky is not null) ReadPhone(sticky);
        }
        catch { }

        var mgr = _ctx.GetSystemService(Context.BluetoothService) as BluetoothManager;
        var adapter = mgr?.Adapter;
        NoBluetooth = adapter is null;
        BluetoothOff = adapter is not null && !adapter.IsEnabled;
        if (adapter is null || !adapter.IsEnabled || !CanConnect)
        {
            foreach (var a in _all.Values) a.Connected = false;
            Changed?.Invoke();
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var d in adapter.BondedDevices ?? (ICollection<BluetoothDevice>)Array.Empty<BluetoothDevice>())
            {
                string? addr = d.Address;
                if (string.IsNullOrEmpty(addr)) continue;
                string key = "BT:" + addr;
                seen.Add(key);
                _bonded.Add(key);
                bool connected = Hidden.Bool(d, "isConnected") ?? (Hidden.Int(d, "getBatteryLevel") is int probe && probe >= 0);
                if (!_all.TryGetValue(key, out var a))
                {
                    string name = d.Name ?? addr;
                    a = new Accessory(key) { Name = name, KindHint = KindOf(d) };
                    a.Profile = Profiles.Match(name, a.KindHint);
                    _all[key] = a;
                }
                if (connected && !a.Connected) a.ConnectedAt = DateTime.Now;
                a.Connected = connected;
                if (connected && Connecting == key) Connecting = null;
                if (!connected) { _gatt.Drop(key); continue; }
                a.LastSeen = DateTime.Now;
                // show the remembered case level right away, before the first earbud broadcast
                if (a.Profile.Buds != BudsSource.None && !a.Case.Known && LastCase(key) is { } lc) a.Case = new Part(lc.Level, false);
                if (Hidden.Int(d, "getBatteryLevel") is int lvl && lvl is >= 0 and <= 100)
                {
                    a.SystemLevel = lvl;
                    a.LastLevel = lvl;
                    a.SystemSource = "Android";
                }
                // speakers and headsets without the HFP battery report: ask their BLE Battery Service (0x180F)
                else if (a.Profile.Buds == BudsSource.None && a.SystemSource != "GATT") _gatt.Want(d, key);
            }
        }
        catch (Exception e) { Log.Error("Android bonded devices", e); }
        _bonded.IntersectWith(seen);
        foreach (var a in _all.Values) if (!seen.Contains(a.Key)) { a.Connected = false; _gatt.Drop(a.Key); }
        Changed?.Invoke();
    }

    void OnGattLevel(string key, int level)
    {
        if (level < 0) { Changed?.Invoke(); return; } // attempts over: redraw «не сообщает заряд»
        if (!_all.TryGetValue(key, out var a) || !a.Connected) return;
        if (a.SystemSource is "Android" && a.SystemLevel is not null) return; // the system report wins
        a.SystemLevel = level;
        a.LastLevel = level;
        a.SystemSource = "GATT";
        a.LastSeen = DateTime.Now;
        Changed?.Invoke();
    }

    // ───────── connecting a paired device ─────────

    /// <summary>
    /// A tap on a paired device: asks the A2DP and headset profiles to connect it (works where the system allows apps
    /// to); when it does not, opens Bluetooth settings with the device in the list.
    /// </summary>
    public void Connect(string key, Android.App.Activity act)
    {
        if (Demo || !_all.TryGetValue(key, out var acc) || acc.Connected) return;
        var adapter = (_ctx.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;
        if (adapter is null || !adapter.IsEnabled || !CanConnect) { OpenBluetoothSettings(act); return; }
        Connecting = key;
        Changed?.Invoke();
        var weak = new WeakReference<Android.App.Activity>(act);
        bool any = false;
        int pending = 2;
        void Done(bool ok)
        {
            any |= ok;
            if (--pending > 0) return;
            Android.Util.Log.Info("Glance", $"connect: {acc.DisplayName} requested={any}");
            if (!any)
            {
                Connecting = null;
                Changed?.Invoke();
                if (weak.TryGetTarget(out var a) && !a.IsFinishing) OpenBluetoothSettings(a);
                return;
            }
            // give it a moment; if it still is not connected, the settings page lets the user finish
            _main.PostDelayed(() =>
            {
                if (Connecting != key) return;
                Connecting = null;
                Refresh();
                if (!acc.Connected && weak.TryGetTarget(out var a) && !a.IsFinishing && MainActivity.Visible) OpenBluetoothSettings(a);
            }, 12_000);
        }
        try
        {
            var dev = adapter.GetRemoteDevice(key[3..]);
            foreach (var profile in new[] { ProfileType.A2dp, ProfileType.Headset })
            {
                var listener = new ProfileListener((type, proxy) =>
                {
                    bool ok = false;
                    try
                    {
                        if (proxy is Java.Lang.Object jo)
                        {
                            var m = jo.Class.GetMethod("connect", Java.Lang.Class.FromType(typeof(BluetoothDevice)));
                            ok = m?.Invoke(jo, dev)?.ToString() == "true";
                        }
                    }
                    catch (Exception e) { Android.Util.Log.Info("Glance", $"connect {type}: {e.GetType().Name} {e.Message}"); }
                    try { adapter.CloseProfileProxy(type, proxy); } catch { }
                    _main.Post(() => Done(ok));
                });
                if (!adapter.GetProfileProxy(_ctx, listener, profile)) Done(false);
            }
        }
        catch (Exception e)
        {
            Log.Error("Android connect", e);
            Connecting = null;
            Changed?.Invoke();
            OpenBluetoothSettings(act);
        }
    }

    public static void OpenBluetoothSettings(Android.App.Activity act)
    {
        try { act.StartActivity(new Intent(Android.Provider.Settings.ActionBluetoothSettings)); }
        catch (Exception e) { Log.Error("Android bluetooth settings", e); }
    }

    sealed class ProfileListener(Action<ProfileType, IBluetoothProfile> connected) : Java.Lang.Object, IBluetoothProfileServiceListener
    {
        bool _done;
        public void OnServiceConnected(ProfileType profile, IBluetoothProfile? proxy)
        {
            if (_done || proxy is null) return;
            _done = true;
            connected(profile, proxy);
        }
        public void OnServiceDisconnected(ProfileType profile) { }
    }

    void ReadPhone(Intent i)
    {
        int level = i.GetIntExtra(BatteryManager.ExtraLevel, -1), scale = i.GetIntExtra(BatteryManager.ExtraScale, 100);
        int status = i.GetIntExtra(BatteryManager.ExtraStatus, -1);
        if (level >= 0 && scale > 0) PhoneLevel = (int)Math.Round(level * 100.0 / scale);
        PhoneCharging = status == (int)BatteryStatus.Charging || status == (int)BatteryStatus.Full;
    }

    /// <summary>Radio class → kind (raw class bits: major 0x1F00, minor bits below).</summary>
    static DeviceKind KindOf(BluetoothDevice d)
    {
        int dc;
        try { dc = d.BluetoothClass is { } c ? (int)c.DeviceClass : 0; } catch { return DeviceKind.Unknown; }
        switch (dc & 0x1F00)
        {
            case 0x0400: // audio/video
                return (dc & 0x1FFC) switch
                {
                    0x0404 or 0x0408 => DeviceKind.Headset,
                    0x0414 or 0x041C => DeviceKind.Speaker,
                    _ => DeviceKind.Headphones,
                };
            case 0x0500: // peripheral
                if ((dc & 0x3C) is 0x04 or 0x08) return DeviceKind.Gamepad;
                if ((dc & 0xC0) == 0x40) return DeviceKind.Keyboard;
                if ((dc & 0xC0) is 0x80 or 0xC0) return DeviceKind.Mouse;
                return DeviceKind.Unknown;
            case 0x0700: return DeviceKind.Watch;
            case 0x0200: return DeviceKind.Phone;
            default: return DeviceKind.Unknown;
        }
    }

    // ───────── per-bud levels from BLE adverts (AirPods, Fast Pair) ─────────

    readonly Dictionary<string, DateTime> _caseAt = new(StringComparer.Ordinal);

    /// <summary>When the shown case level was last reported live; null when it is live right now.</summary>
    public TimeSpan? CaseAge(string key) =>
        _caseAt.TryGetValue(key, out var at) && DateTime.UtcNow - at > TimeSpan.FromSeconds(90) ? DateTime.UtcNow - at : null;

    readonly Dictionary<string, (int Level, DateTime At)> _caseSaved = new(StringComparer.Ordinal);

    void SaveCase(string key, int level)
    {
        _caseAt[key] = DateTime.UtcNow;
        // adverts arrive several times a second: write to disk only when the level changes or once a minute
        if (_caseSaved.TryGetValue(key, out var last) && last.Level == level && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(1)) return;
        _caseSaved[key] = (level, DateTime.UtcNow);
        try
        {
            _ctx.GetSharedPreferences("glance", FileCreationMode.Private)?.Edit()
                ?.PutInt("case:" + key, level)?.PutLong("caseAt:" + key, DateTimeOffset.UtcNow.ToUnixTimeSeconds())?.Apply();
        }
        catch { }
    }

    (int Level, DateTime At)? LastCase(string key)
    {
        try
        {
            var p = _ctx.GetSharedPreferences("glance", FileCreationMode.Private);
            if (p is null || !p.Contains("case:" + key)) return null;
            var at = DateTimeOffset.FromUnixTimeSeconds(p.GetLong("caseAt:" + key, 0)).UtcDateTime;
            if (DateTime.UtcNow - at > TimeSpan.FromDays(3)) return null;
            _caseAt[key] = at;
            return (p.GetInt("case:" + key, 0), at);
        }
        catch { return null; }
    }

    static readonly ParcelUuid FastPairUuid = ParcelUuid.FromString("0000fe2c-0000-1000-8000-00805f9b34fb")!;

    void StartScan()
    {
        if (Demo || _scan is not null || !CanScan) return;
        try
        {
            var mgr = _ctx.GetSystemService(Context.BluetoothService) as BluetoothManager;
            if (mgr?.Adapter is not { IsEnabled: true } adapter) return;
            _scanner = adapter.BluetoothLeScanner;
            if (_scanner is null) return;
            _scan = new BudsScan(r => _main.Post(() => OnAdvert(r)));
            bool slow = Background && DateTime.UtcNow >= _burstUntil;
            var settings = new ScanSettings.Builder().SetScanMode(slow ? Android.Bluetooth.LE.ScanMode.Balanced : Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
            // Filtered scan: Android keeps it running with the screen off (unfiltered scans are paused then),
            // and only earbud adverts wake the app.
            var filters = new List<ScanFilter>
            {
                new ScanFilter.Builder().SetManufacturerData(0x004C, Array.Empty<byte>())!.Build()!,
                new ScanFilter.Builder().SetServiceData(FastPairUuid, Array.Empty<byte>())!.Build()!,
            };
            _scanner.StartScan(filters, settings, _scan);
        }
        catch (Exception e) { Log.Error("Android BLE scan", e); _scan = null; }
    }

    DateTime _burstUntil;

    /// <summary>
    /// In the background the scan is «balanced» (adverts every few seconds). Right after an accessory connects the
    /// case is still open and reports its charge — listen at full speed for a minute so it shows within a second.
    /// </summary>
    void Burst()
    {
        if (Demo || !Background) return;
        bool wasSlow = DateTime.UtcNow >= _burstUntil;
        _burstUntil = DateTime.UtcNow.AddSeconds(60);
        if (wasSlow && _scan is not null) { StopScan(); StartScan(); }
        _main.PostDelayed(() =>
        {
            if (_scan is not null && DateTime.UtcNow >= _burstUntil) { StopScan(); StartScan(); } // back to balanced
        }, 61_000);
    }

    // ───────── steady levels: one stray advert must not make a bud jump 100 → 97 → 100 ─────────

    /// <summary>
    /// Earbuds send several adverts a second, and not all of them are fresh: the case (in a pocket nearby) keeps
    /// advertising the levels it saw when the buds left it — «100 %» long after the bud in the ear is at 97 %.
    /// A bud that is not charging can only lose charge, so:
    /// • a lower value is shown once it has come twice among the last adverts (one corrupt packet is ignored);
    /// • a higher value while not charging is a stale packet — shown only if nothing lower came for a while;
    /// • on the charger the same holds the other way round; other changes need a majority.
    /// The very first value is shown at once.
    /// </summary>
    sealed class PartFilter
    {
        readonly Queue<(Part P, DateTime At)> _q = new();
        public Part Shown = Part.Unknown;

        public Part Feed(Part p)
        {
            var now = DateTime.UtcNow;
            _q.Enqueue((p, now));
            while (_q.Count > 8 || (_q.Count > 0 && now - _q.Peek().At > TimeSpan.FromSeconds(30))) _q.Dequeue();
            if (p == Shown) return Shown;
            if (!Shown.Known && p.Known) return Shown = p;

            int mine = 0, cur = 0, lower = 0, higher = 0;
            foreach (var (x, _) in _q)
            {
                if (x == p) mine++; else if (x == Shown) cur++;
                if (x.Known && p.Known)
                {
                    if (x.Level < p.Level) lower++;
                    else if (x.Level > p.Level) higher++;
                }
            }
            if (p.Known && Shown.Known && p.Charging == Shown.Charging)
            {
                bool natural = p.Charging ? p.Level > Shown.Level : p.Level < Shown.Level;
                if (natural) { if (mine >= 2) Shown = p; }
                // against the natural direction: a stale packet unless the whole window agrees
                else if (_q.Count >= 6 && (p.Charging ? higher : lower) == 0 && mine >= 4) Shown = p;
                return Shown;
            }
            if (mine >= 2 && mine > cur) Shown = p; // charging started / stopped, bud taken out or put away
            return Shown;
        }
    }

    readonly Dictionary<string, PartFilter> _filters = new(StringComparer.Ordinal);

    Part Steady(string key, Part p)
    {
        if (!_filters.TryGetValue(key, out var f)) _filters[key] = f = new PartFilter();
        return f.Feed(p);
    }

    void StopScan()
    {
        if (_scan is null) return;
        try { _scanner?.StopScan(_scan); } catch { }
        _scan = null;
    }

    void OnAdvert((byte[]? Apple, byte[]? FastPair, int Rssi) r)
    {
        // Earbuds in the ears with the phone in a pocket are often -75…-85 dBm; neighbours' buds are filtered
        // by model (Apple) and by «only connected devices of the same protocol» below.
        if (r.Rssi < -88) return;
        BudsAdvert ad;
        if (r.Apple is { } ap && AppleContinuity.TryParse(ap, out var a1)) ad = a1;
        else if (r.FastPair is { } fp && FastPair.TryParse(fp, out var a2)) ad = a2;
        else return;
        // Fast Pair adverts carry no model: be stricter about distance for them.
        if (ad.Vendor == BudsSource.FastPair && r.Rssi < -78) return;

        var target = _all.Values.Where(a => a.Connected && a.Profile.Buds == ad.Vendor)
            .OrderByDescending(a => ad.Vendor == BudsSource.AppleContinuity ? AppleModels.Affinity(a.DisplayName, ad.Model) : 0)
            .FirstOrDefault();
        if (target is null) return;
        if (ad.Vendor == BudsSource.AppleContinuity && AppleModels.Affinity(target.DisplayName, ad.Model) < 0) return;
        // Someone else's earbuds: their levels disagree with what Android reports for ours.
        if (target.SystemLevel is int sys && (ad.Left.Level ?? ad.Right.Level) is int bud
            && Math.Abs(Math.Min(ad.Left.Level ?? bud, ad.Right.Level ?? bud) - sys) > 25) return;
        // The case reports its charge only while it is open or a bud sits in it; the rest of the time adverts say
        // «unknown». Keep the last known case level (like iOS does) instead of blanking it.
        // The case reports its charge only while it is open or a bud sits in it (JBL, Sony and others send
        // «unknown» once the buds are in the ears). Remember the last real value — across restarts — and show it
        // marked with its age, like iOS does.
        Part caseNow;
        if (ad.Case.Known) { caseNow = Steady(target.Key + ":C", ad.Case); SaveCase(target.Key, caseNow.Level ?? ad.Case.Level!.Value); }
        else caseNow = target.Case.Known ? target.Case with { Charging = false } : LastCase(target.Key) is { } lc ? new Part(lc.Level, false) : Part.Unknown;
        var left = Steady(target.Key + ":L", ad.Left);
        var right = Steady(target.Key + ":R", ad.Right);
        bool changed = target.Left != left || target.Right != right || target.Case != caseNow;
        target.Left = left;
        target.Right = right;
        target.Case = caseNow;
        target.PartsAtUtc = DateTime.UtcNow;
        target.PartsSource = ad.Vendor == BudsSource.AppleContinuity ? "Apple" : "Fast Pair";
        target.BudsModel = ad.Model;
        if (changed) Changed?.Invoke();
    }

    // ───────── demo (self-test on the emulator, screenshots) ─────────

    public void UseDemo()
    {
        Demo = true;
        _all.Clear();
        Accessory Dev(string key, string name, DeviceKind kind, int? level, bool charging = false) => new(key)
        {
            Name = name,
            KindHint = kind,
            Profile = Profiles.Match(name, kind),
            Connected = true,
            ConnectedAt = DateTime.Now.AddMinutes(-20),
            LastSeen = DateTime.Now,
            SystemLevel = level,
            SystemCharging = charging,
        };
        var buds = Dev("BT:DEMO:BUDS", "AirPods Pro", DeviceKind.Earbuds, null);
        buds.Left = new Part(72, false);
        buds.Right = new Part(64, false);
        buds.Case = new Part(41, true);
        buds.PartsAtUtc = DateTime.UtcNow;
        buds.PartsSource = "Apple";
        foreach (var a in new[]
                 {
                     buds,
                     Dev("BT:DEMO:WATCH", "Galaxy Watch6", DeviceKind.Watch, 18),
                     Dev("BT:DEMO:KBD", "Keychron K2", DeviceKind.Keyboard, 87, true),
                     Dev("BT:DEMO:PAD", "Xbox Wireless Controller", DeviceKind.Gamepad, 55),
                     Dev("BT:DEMO:SPK", "JBL Flip 6", DeviceKind.Speaker, 34),
                 })
            _all[a.Key] = a;
        foreach (var (key, name, kind, ago) in new[] { ("BT:DEMO:SONY", "WH-1000XM5", DeviceKind.Headphones, 30), ("BT:DEMO:CAR", "Car Audio", DeviceKind.Speaker, 300) })
        {
            var off = Dev(key, name, kind, null);
            off.Connected = false;
            off.LastSeen = DateTime.Now.AddMinutes(-ago);
            off.LastLevel = ago < 60 ? 62 : null;
            _all[key] = off;
            _bonded.Add(key);
        }
        PhoneLevel = 76;
        PhoneCharging = false;
        Changed?.Invoke();
    }

    sealed class Receiver : BroadcastReceiver
    {
        readonly Action<Intent> _on;
        public Receiver(Action<Intent> on) => _on = on;
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent is not null) _on(intent);
        }
    }

    sealed class BudsScan : ScanCallback
    {
        readonly Action<(byte[]?, byte[]?, int)> _on;
        public BudsScan(Action<(byte[]?, byte[]?, int)> on) => _on = on;
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            var rec = result?.ScanRecord;
            if (rec is null) return;
            byte[]? apple = rec.GetManufacturerSpecificData(0x004C);
            byte[]? fp = rec.GetServiceData(FastPairUuid);
            if (apple is null && fp is null) return;
            _on((apple, fp, result!.Rssi));
        }
    }
}

/// <summary>Hidden-but-public BluetoothDevice getters (isConnected, getBatteryLevel), called by reflection.</summary>
internal static class Hidden
{
    static string? Call(Java.Lang.Object o, string method)
    {
        try
        {
            var m = o.Class.GetMethod(method);
            return m?.Invoke(o)?.ToString();
        }
        catch { return null; }
    }

    public static bool? Bool(Java.Lang.Object o, string method) => bool.TryParse(Call(o, method), out var b) ? b : null;
    public static int? Int(Java.Lang.Object o, string method) => int.TryParse(Call(o, method), out var i) ? i : null;
}
