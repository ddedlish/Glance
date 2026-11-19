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
    public bool BluetoothOff { get; private set; }
    public bool NoBluetooth { get; private set; }

    /// <summary>The phone's own battery (shown as the last card).</summary>
    public int? PhoneLevel { get; private set; }
    public bool PhoneCharging { get; private set; }

    public Devices(Context ctx) => _ctx = ctx;

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
            f.AddAction(Intent.ActionBatteryChanged);
            try { _ctx.RegisterReceiver(_rx, f, ReceiverFlags.Exported); }
            catch (Exception e) { Log.Error("Android receiver", e); }
        }
        Refresh();
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
        if (i.Action == Intent.ActionBatteryChanged) { ReadPhone(i); Changed?.Invoke(); return; }
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
                if (!connected) continue;
                a.LastSeen = DateTime.Now;
                if (Hidden.Int(d, "getBatteryLevel") is int lvl && lvl is >= 0 and <= 100)
                {
                    a.SystemLevel = lvl;
                    a.LastLevel = lvl;
                    a.SystemSource = "Android";
                }
            }
        }
        catch (Exception e) { Log.Error("Android bonded devices", e); }
        foreach (var a in _all.Values) if (!seen.Contains(a.Key)) a.Connected = false;
        Changed?.Invoke();
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
            var settings = new ScanSettings.Builder().SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
            _scanner.StartScan(null, settings, _scan);
        }
        catch (Exception e) { Log.Error("Android BLE scan", e); _scan = null; }
    }

    void StopScan()
    {
        if (_scan is null) return;
        try { _scanner?.StopScan(_scan); } catch { }
        _scan = null;
    }

    void OnAdvert((byte[]? Apple, byte[]? FastPair, int Rssi) r)
    {
        if (r.Rssi < -68) return; // someone else's earbuds nearby
        BudsAdvert ad;
        if (r.Apple is { } ap && AppleContinuity.TryParse(ap, out var a1)) ad = a1;
        else if (r.FastPair is { } fp && FastPair.TryParse(fp, out var a2)) ad = a2;
        else return;

        var target = _all.Values.Where(a => a.Connected && a.Profile.Buds == ad.Vendor)
            .OrderByDescending(a => ad.Vendor == BudsSource.AppleContinuity ? AppleModels.Affinity(a.DisplayName, ad.Model) : 0)
            .FirstOrDefault();
        if (target is null) return;
        if (ad.Vendor == BudsSource.AppleContinuity && AppleModels.Affinity(target.DisplayName, ad.Model) < 0) return;
        bool changed = target.Left != ad.Left || target.Right != ad.Right || target.Case != ad.Case;
        target.Left = ad.Left;
        target.Right = ad.Right;
        target.Case = ad.Case;
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
