using Android.Bluetooth;
using Android.Content;
using Android.OS;

namespace Glance.Droid;

/// <summary>
/// Battery of speakers and headsets that do not send the HFP battery report (Android shows no level for them): asks the
/// standard BLE Battery Service (0x180F, characteristic 0x2A19) over a low-energy GATT link, then keeps listening for
/// its notifications while the device stays connected. Two quiet attempts per connection; when the device has no such
/// service, the card says «не сообщает заряд» instead of an empty «—».
/// Callbacks come on a binder thread and are posted to the main thread.
/// </summary>
internal sealed class GattBattery
{
    static readonly Java.Util.UUID Service = Java.Util.UUID.FromString("0000180f-0000-1000-8000-00805f9b34fb")!;
    static readonly Java.Util.UUID Level = Java.Util.UUID.FromString("00002a19-0000-1000-8000-00805f9b34fb")!;
    static readonly Java.Util.UUID Cccd = Java.Util.UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;
    const int MaxTries = 2;

    sealed class State
    {
        public BluetoothGatt? Gatt;
        public long NextTry;
        public int Tries;
        public bool Live, NoService;
    }

    readonly Context _ctx;
    readonly Handler _main;
    readonly Action<string, int> _onLevel;
    readonly Dictionary<string, State> _s = new(StringComparer.Ordinal);

    public GattBattery(Context ctx, Handler main, Action<string, int> onLevel)
    {
        _ctx = ctx;
        _main = main;
        _onLevel = onLevel;
    }

    /// <summary>True when every attempt is over and no level came: the device does not report its battery.</summary>
    public bool Failed(string key) => _s.TryGetValue(key, out var s) && !s.Live && s.Gatt is null && (s.NoService || s.Tries >= MaxTries);

    /// <summary>Connected device without a system level: start (or retry later) a Battery Service read.</summary>
    public void Want(BluetoothDevice d, string key)
    {
        if (!_s.TryGetValue(key, out var s)) _s[key] = s = new State();
        if (s.Gatt is not null || s.Live || s.NoService || s.Tries >= MaxTries) return;
        long now = SystemClock.ElapsedRealtime();
        if (now < s.NextTry) return;
        s.Tries++;
        s.NextTry = now + 60_000;
        try
        {
            var g = d.ConnectGatt(_ctx, false, new Callback(this, key), BluetoothTransports.Le);
            s.Gatt = g;
            Android.Util.Log.Info("Glance", $"gatt: battery service try {s.Tries} for {d.Name}");
            _main.PostDelayed(() =>
            {
                if (_s.TryGetValue(key, out var x) && Same(x.Gatt, g) && !x.Live) Close(key, "timeout");
            }, 25_000);
        }
        catch (Exception e)
        {
            Log.Error("Android gatt battery", e);
            s.Gatt = null;
        }
    }

    /// <summary>The device disconnected: close the link and start fresh on its next connection.</summary>
    public void Drop(string key)
    {
        if (!_s.ContainsKey(key)) return;
        Close(key, "disconnected");
        _s.Remove(key);
    }

    void Close(string key, string why)
    {
        if (!_s.TryGetValue(key, out var s)) return;
        var g = s.Gatt;
        s.Gatt = null;
        s.Live = false;
        if (g is null) return;
        Android.Util.Log.Info("Glance", $"gatt: closed ({why})");
        try { g.Disconnect(); } catch { }
        try { g.Close(); } catch { }
    }

    /// <summary>The same Java link (the callback may hand over a different managed wrapper of it).</summary>
    static bool Same(BluetoothGatt? a, BluetoothGatt? b) => a is not null && b is not null && (ReferenceEquals(a, b) || a.Equals(b));

    void OnLost(string key, BluetoothGatt gatt, bool noService)
    {
        if (!_s.TryGetValue(key, out var s) || !Same(s.Gatt, gatt)) { try { gatt.Close(); } catch { } return; }
        if (noService) s.NoService = true;
        Close(key, noService ? "no battery service" : "link lost");
        _onLevel(key, -1); // let the card redraw («не сообщает заряд» once all attempts are over)
    }

    void OnLevel(string key, BluetoothGatt gatt, int level)
    {
        if (!_s.TryGetValue(key, out var s) || !Same(s.Gatt, gatt)) return;
        s.Live = true;
        if (level is >= 0 and <= 100) _onLevel(key, level);
    }

    sealed class Callback : BluetoothGattCallback
    {
        readonly GattBattery _o;
        readonly string _key;

        public Callback(GattBattery owner, string key)
        {
            _o = owner;
            _key = key;
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (gatt is null) return;
            if (newState == ProfileState.Connected && status == GattStatus.Success)
            {
                try { gatt.DiscoverServices(); } catch (Exception e) { Log.Error("Android gatt discover", e); }
            }
            else if (newState == ProfileState.Disconnected) _o._main.Post(() => _o.OnLost(_key, gatt, false));
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (gatt is null) return;
            var ch = status == GattStatus.Success ? gatt.GetService(Service)?.GetCharacteristic(Level) : null;
            if (ch is null) { _o._main.Post(() => _o.OnLost(_key, gatt, true)); return; }
            try { gatt.ReadCharacteristic(ch); } catch (Exception e) { Log.Error("Android gatt read", e); }
        }

#pragma warning disable CS0618, CS0672, CA1422 // the pre-33 callbacks: Android 13+ still calls them with the value set
        public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            if (gatt is null || characteristic is null) return;
            if (status == GattStatus.Success && characteristic.GetValue() is { Length: > 0 } v)
            {
                int level = v[0];
                _o._main.Post(() => _o.OnLevel(_key, gatt, level));
            }
            // keep listening: the level arrives by itself whenever it changes
            try
            {
                gatt.SetCharacteristicNotification(characteristic, true);
                if (characteristic.GetDescriptor(Cccd) is { } d)
                {
                    d.SetValue(new byte[] { 1, 0 });
                    gatt.WriteDescriptor(d);
                }
            }
            catch (Exception e) { Log.Error("Android gatt notify", e); }
        }

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            if (gatt is null || characteristic is null || characteristic.Uuid?.Equals(Level) != true) return;
            if (characteristic.GetValue() is { Length: > 0 } v)
            {
                int level = v[0];
                _o._main.Post(() => _o.OnLevel(_key, gatt, level));
            }
        }
#pragma warning restore CS0618, CS0672, CA1422
    }
}
