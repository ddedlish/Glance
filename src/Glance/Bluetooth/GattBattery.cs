using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Glance;

/// <summary>
/// GATT Battery Service (0x180F) for BLE accessories: subscribes to notifications when possible
/// (zero polling), falls back to periodic reads. Supports several battery services
/// (split keyboards report each half separately).
/// </summary>
internal sealed class GattBattery : IDisposable
{
    readonly string _id;
    BluetoothLEDevice? _device;
    readonly List<GattDeviceService> _services = new();
    readonly List<GattCharacteristic> _chars = new();
    int[] _levels = Array.Empty<int>();
    volatile bool _disposed;
    int _reading;

    public event Action<int[]>? Levels; // UI thread
    public bool NeedsPolling { get; private set; } = true;

    public GattBattery(string aepId) => _id = aepId;

    public async Task StartAsync()
    {
        try
        {
            _device = await BluetoothLEDevice.FromIdAsync(_id);
            if (_device is null || _disposed) { Dispose(); return; }

            var sr = await _device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
            if (_disposed) return;
            if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)
            {
                NeedsPolling = false; // nothing to read – Windows PnP value will be used
                return;
            }

            bool allNotify = true;
            foreach (var svc in sr.Services)
            {
                _services.Add(svc);
                var cr = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                if (_disposed) return;
                if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0) continue;
                var ch = cr.Characteristics[0];
                _chars.Add(ch);

                if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    ch.ValueChanged += OnValueChanged;
                    var st = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    if (st != GattCommunicationStatus.Success) allNotify = false;
                }
                else allNotify = false;
            }
            _levels = new int[_chars.Count];
            Array.Fill(_levels, -1);
            NeedsPolling = !allNotify && _chars.Count > 0;
            await ReadAsync();
        }
        catch (Exception e)
        {
            // AccessDenied is normal for some HID devices – Windows keeps exclusive access.
            Log.Info($"GATT battery unavailable: {e.Message}");
            NeedsPolling = false;
        }
    }

    public async Task ReadAsync()
    {
        if (_disposed || _chars.Count == 0 || Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            for (int i = 0; i < _chars.Count; i++)
            {
                var r = await _chars[i].ReadValueAsync(BluetoothCacheMode.Uncached);
                if (_disposed) return;
                if (r.Status == GattCommunicationStatus.Success && TryByte(r.Value, out var b)) _levels[i] = b;
            }
            Publish();
        }
        catch (Exception e) { Log.Info($"GATT read failed: {e.Message}"); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (_disposed) return;
        int idx = _chars.IndexOf(sender);
        if (idx < 0) idx = _chars.FindIndex(c => c.AttributeHandle == sender.AttributeHandle);
        if (idx < 0 || !TryByte(args.CharacteristicValue, out var b)) return;
        _levels[idx] = b;
        Publish();
    }

    void Publish()
    {
        var valid = _levels.Where(l => l is >= 0 and <= 100).ToArray();
        if (valid.Length == 0) return;
        Ui.Post(() => { if (!_disposed) Levels?.Invoke(valid); });
    }

    static bool TryByte(IBuffer? buffer, out int value)
    {
        value = -1;
        if (buffer is null || buffer.Length < 1) return false;
        using var reader = DataReader.FromBuffer(buffer);
        value = reader.ReadByte();
        return value <= 100;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var c in _chars) { try { c.ValueChanged -= OnValueChanged; } catch { } }
        _chars.Clear();
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        _services.Clear();
        try { _device?.Dispose(); } catch { }
        _device = null;
    }
}

/// <summary>Device class from the radio: Class of Device (classic) / GAP Appearance (LE).</summary>
internal static class KindResolver
{
    public static async Task<DeviceKind> ResolveAsync(Accessory a)
    {
        try
        {
            if (a.ClassicAepId is string cid)
            {
                using var d = await BluetoothDevice.FromIdAsync(cid);
                if (d is not null)
                {
                    var k = FromClassOfDevice(d.ClassOfDevice.RawValue);
                    if (k != DeviceKind.Unknown) return k;
                }
            }
            if (a.LeAepId is string lid)
            {
                using var d = await BluetoothLEDevice.FromIdAsync(lid);
                if (d is not null) return FromAppearance(d.Appearance.RawValue);
            }
        }
        catch (Exception e) { Log.Info($"Kind resolve: {e.Message}"); }
        return DeviceKind.Unknown;
    }

    public static DeviceKind FromClassOfDevice(uint raw)
    {
        int major = (int)((raw >> 8) & 0x1F);
        int minor = (int)((raw >> 2) & 0x3F);
        switch (major)
        {
            case 0x02: return DeviceKind.Phone;
            case 0x04:
                return minor switch
                {
                    0x01 or 0x02 => DeviceKind.Headset,
                    0x05 or 0x07 => DeviceKind.Speaker,
                    0x06 => DeviceKind.Headphones,
                    _ => DeviceKind.Headphones,
                };
            case 0x05:
                int kb = (minor >> 4) & 0x3, low = minor & 0xF;
                if (low is 0x1 or 0x2) return DeviceKind.Gamepad;
                if (low is 0x5 or 0x7) return DeviceKind.Pen;
                return kb switch { 1 or 3 => DeviceKind.Keyboard, 2 => DeviceKind.Mouse, _ => DeviceKind.Unknown };
            case 0x07: return DeviceKind.Watch;
            default: return DeviceKind.Unknown;
        }
    }

    public static DeviceKind FromAppearance(ushort raw)
    {
        int cat = raw >> 6, sub = raw & 0x3F;
        return cat switch
        {
            0x01 => DeviceKind.Phone,
            0x03 => DeviceKind.Watch,
            0x0F => sub switch
            {
                1 => DeviceKind.Keyboard,
                2 => DeviceKind.Mouse,
                3 or 4 => DeviceKind.Gamepad,
                5 or 8 => DeviceKind.Pen,
                _ => DeviceKind.Unknown,
            },
            0x21 => DeviceKind.Speaker,
            0x25 => sub switch
            {
                1 => DeviceKind.Earbuds,
                2 => DeviceKind.Headset,
                3 => DeviceKind.Headphones,
                4 => DeviceKind.Neckband,
                _ => DeviceKind.Headphones,
            },
            _ => DeviceKind.Unknown,
        };
    }
}
