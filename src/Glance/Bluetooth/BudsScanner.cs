using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Glance;

/// <summary>
/// Duty-cycled passive BLE scanner for earbud battery broadcasts. Runs only while compatible
/// earbuds are connected; hardware byte-pattern filters keep the callback rate tiny.
/// Normal mode: ~8 s scan per minute, stopping early once data arrived. Live mode (panel open):
/// continuous.
/// </summary>
internal sealed class BudsScanner : IDisposable
{
    const int WindowMs = 8000;
    readonly object _gate = new();
    readonly System.Threading.Timer _timer;
    readonly Dictionary<ulong, long> _lastPost = new();
    BluetoothLEAdvertisementWatcher? _apple, _fastPair;
    bool _wantApple, _wantFp, _scanning, _live, _disposed;
    DateTime _boostUntil;

    public event Action<BudsAdvert>? Seen; // UI thread

    public BudsScanner() => _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);

    public void SetTargets(bool apple, bool fastPair)
    {
        lock (_gate)
        {
            if (_disposed || (apple == _wantApple && fastPair == _wantFp)) return;
            _wantApple = apple; _wantFp = fastPair;
            StopWatchers();
            _scanning = false;
            if (apple || fastPair) { _boostUntil = DateTime.UtcNow.AddMinutes(2); StartWindow(); }
            else _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>A target device just connected: scan now and more often for two minutes.</summary>
    public void Kick()
    {
        lock (_gate)
        {
            if (_disposed || !(_wantApple || _wantFp)) return;
            _boostUntil = DateTime.UtcNow.AddMinutes(2);
            if (!_scanning) StartWindow();
        }
    }

    public void SetLive(bool live)
    {
        lock (_gate)
        {
            _live = live;
            if (live && !_scanning && (_wantApple || _wantFp) && !_disposed) StartWindow();
        }
    }

    /// <summary>All targets got fresh data – stop the current window early (unless live).</summary>
    public void Satisfied()
    {
        lock (_gate)
        {
            if (!_scanning || _live) return;
            StopWatchers();
            ScheduleNext();
        }
    }

    void StartWindow()
    {
        try
        {
            if (_wantApple) StartWatcher(ref _apple, 0xFF, new byte[] { 0x4C, 0x00, 0x07, 0x19 });
            if (_wantFp) StartWatcher(ref _fastPair, 0x16, new byte[] { 0x2C, 0xFE });
            _scanning = true;
            _timer.Change(WindowMs, Timeout.Infinite);
        }
        catch (Exception e)
        {
            Log.Info($"BLE scan unavailable: {e.Message}");
            _scanning = false;
            _timer.Change(120_000, Timeout.Infinite);
        }
    }

    void ScheduleNext()
    {
        _scanning = false;
        int delay = _live ? 500 : DateTime.UtcNow < _boostUntil ? 10_000 : 60_000;
        _timer.Change(delay, Timeout.Infinite);
    }

    void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_scanning)
            {
                if (_live) { _timer.Change(WindowMs, Timeout.Infinite); return; }
                StopWatchers();
                ScheduleNext();
            }
            else if (_wantApple || _wantFp) StartWindow();
        }
    }

    void StartWatcher(ref BluetoothLEAdvertisementWatcher? w, byte dataType, byte[] pattern)
    {
        if (w is null)
        {
            w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            w.AdvertisementFilter.BytePatterns.Add(new BluetoothLEAdvertisementBytePattern(dataType, 0, ToBuffer(pattern)));
            w.SignalStrengthFilter.InRangeThresholdInDBm = -95;
            w.SignalStrengthFilter.OutOfRangeThresholdInDBm = -100;
            w.SignalStrengthFilter.OutOfRangeTimeout = TimeSpan.FromSeconds(3);
            w.SignalStrengthFilter.SamplingInterval = TimeSpan.FromMilliseconds(500);
            w.Received += OnReceived;
            w.Stopped += OnStopped;
        }
        if (w.Status != BluetoothLEAdvertisementWatcherStatus.Started) w.Start();
    }

    void StopWatchers()
    {
        foreach (var w in new[] { _apple, _fastPair })
        {
            try { if (w is not null && w.Status == BluetoothLEAdvertisementWatcherStatus.Started) w.Stop(); }
            catch { }
        }
    }

    void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
            Log.Info($"BLE watcher stopped: {args.Error}");
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        try
        {
            BudsAdvert? parsed = null;
            foreach (var md in e.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != 0x004C) continue;
                var d = Bytes(md.Data);
                if (AppleContinuity.TryParse(d, out var x)) { parsed = x; break; }
            }
            if (parsed is null)
            {
                foreach (var ds in e.Advertisement.DataSections)
                {
                    if (ds.DataType != 0x16) continue;
                    var d = Bytes(ds.Data);
                    if (d.Length > 2 && d[0] == 0x2C && d[1] == 0xFE && FastPair.TryParse(d.AsSpan(2), out var x)) { parsed = x; break; }
                }
            }
            if (parsed is not BudsAdvert adv) return;

            long now = Environment.TickCount64;
            lock (_lastPost)
            {
                if (_lastPost.TryGetValue(e.BluetoothAddress, out var t) && now - t < 900) return;
                if (_lastPost.Count > 256) _lastPost.Clear();
                _lastPost[e.BluetoothAddress] = now;
            }
            adv = adv with { Address = e.BluetoothAddress, Rssi = e.RawSignalStrengthInDBm, AtUtc = DateTime.UtcNow };
            Ui.Post(() => Seen?.Invoke(adv));
        }
        catch (Exception ex) { Log.Error("Adv parse", ex); }
    }

    static byte[] Bytes(IBuffer? buffer)
    {
        if (buffer is null || buffer.Length == 0) return Array.Empty<byte>();
        var b = new byte[buffer.Length];
        using var r = DataReader.FromBuffer(buffer);
        r.ReadBytes(b);
        return b;
    }

    static IBuffer ToBuffer(byte[] data)
    {
        using var w = new DataWriter();
        w.WriteBytes(data);
        return w.DetachBuffer();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            StopWatchers();
        }
        _timer.Dispose();
    }
}
