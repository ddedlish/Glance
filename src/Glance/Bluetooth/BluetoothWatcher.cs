using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Glance;

public sealed record BtEndpoint(string Id, string Name, ulong Address, bool IsLE, bool Connected);

/// <summary>
/// Event-driven connection tracking for paired Bluetooth devices (classic + LE) via
/// association-endpoint DeviceWatchers. Zero CPU while idle – Windows pushes changes.
/// </summary>
internal sealed class BluetoothWatcher : IDisposable
{
    const string PropConnected = "System.Devices.Aep.IsConnected";
    const string PropAddress = "System.Devices.Aep.DeviceAddress";
    static readonly string[] Props = { PropConnected, PropAddress };

    readonly object _gate = new();
    readonly Dictionary<string, DeviceInformation> _infos = new(StringComparer.Ordinal);
    DeviceWatcher? _classic, _le;
    bool _classicDone, _leDone, _disposed;
    int _generation, _restarts;

    public event Action<BtEndpoint, bool>? Endpoint;      // UI thread: (endpoint, initial)
    public event Action<string>? EndpointRemoved;         // UI thread
    public event Action? EnumerationCompleted;            // UI thread

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopCore();
            int gen = ++_generation;
            _classicDone = _leDone = false;
            try
            {
                _classic = Create(BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, gen);
                _le = Create(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, gen);
            }
            catch (Exception e)
            {
                Log.Error("BT watcher start", e);
                ScheduleRestart();
            }
        }
    }

    DeviceWatcher Create(string aqs, bool le, int gen)
    {
        var w = DeviceInformation.CreateWatcher(aqs, Props, DeviceInformationKind.AssociationEndpoint);

        w.Added += (_, info) =>
        {
            BtEndpoint ep; bool initial;
            lock (_gate)
            {
                if (gen != _generation) return;
                _infos[info.Id] = info;
                ep = ToEndpoint(info, le);
                initial = !(le ? _leDone : _classicDone);
            }
            Ui.Post(() => Endpoint?.Invoke(ep, initial));
        };

        w.Updated += (_, upd) =>
        {
            BtEndpoint? ep = null;
            lock (_gate)
            {
                if (gen != _generation) return;
                if (_infos.TryGetValue(upd.Id, out var info))
                {
                    info.Update(upd);
                    ep = ToEndpoint(info, le);
                }
            }
            if (ep is not null) Ui.Post(() => Endpoint?.Invoke(ep, false));
        };

        w.Removed += (_, upd) =>
        {
            lock (_gate)
            {
                if (gen != _generation) return;
                _infos.Remove(upd.Id);
            }
            var id = upd.Id;
            Ui.Post(() => EndpointRemoved?.Invoke(id));
        };

        w.EnumerationCompleted += (_, _) =>
        {
            bool both;
            lock (_gate)
            {
                if (gen != _generation) return;
                if (le) _leDone = true; else _classicDone = true;
                both = _leDone && _classicDone;
                _restarts = 0;
            }
            if (both) Ui.Post(() => EnumerationCompleted?.Invoke());
        };

        w.Stopped += (sender, _) =>
        {
            bool restart;
            lock (_gate) restart = gen == _generation && !_disposed && sender.Status == DeviceWatcherStatus.Aborted;
            if (restart)
            {
                Log.Info("BT watcher aborted – restarting");
                ScheduleRestart();
            }
        };

        w.Start();
        return w;
    }

    static BtEndpoint ToEndpoint(DeviceInformation info, bool le)
    {
        bool connected = info.Properties.TryGetValue(PropConnected, out var c) && c is bool b && b;
        ulong addr = 0;
        if (info.Properties.TryGetValue(PropAddress, out var a) && a is string s)
            ulong.TryParse(s.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
        return new BtEndpoint(info.Id, info.Name ?? "", addr, le, connected);
    }

    void ScheduleRestart()
    {
        int attempt;
        lock (_gate) attempt = ++_restarts;
        var delay = TimeSpan.FromSeconds(Math.Min(120, 2 << Math.Min(attempt, 6)));
        _ = Task.Delay(delay).ContinueWith(_ => Start(), TaskScheduler.Default);
    }

    void StopCore()
    {
        foreach (var w in new[] { _classic, _le })
        {
            if (w is null) continue;
            try
            {
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    w.Stop();
            }
            catch { }
        }
        _classic = _le = null;
        _infos.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generation++;
            StopCore();
        }
    }
}
