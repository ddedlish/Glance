using Windows.Gaming.Input;
using Windows.System.Power;

namespace Glance;

public sealed record GamepadReading(string Id, string Name, int Level, bool Charging, string? Link = null);

/// <summary>Xbox-compatible wireless controllers via Windows.Gaming.Input battery reports.</summary>
internal sealed class GamepadProvider : IDisposable
{
    public event Action? Changed; // any thread

    public GamepadProvider()
    {
        Gamepad.GamepadAdded += OnChanged;
        Gamepad.GamepadRemoved += OnChanged;
    }

    void OnChanged(object? sender, Gamepad e) => Changed?.Invoke();

    public bool Any
    {
        get { try { return Gamepad.Gamepads.Count > 0; } catch { return false; } }
    }

    public List<GamepadReading> Read()
    {
        var list = new List<GamepadReading>();
        try
        {
            foreach (var gp in Gamepad.Gamepads)
            {
                var rep = gp.TryGetBatteryReport();
                if (rep is null || rep.Status == BatteryStatus.NotPresent) continue; // wired
                int? full = rep.FullChargeCapacityInMilliwattHours, rem = rep.RemainingCapacityInMilliwattHours;
                if (full is not > 0 || rem is null) continue;
                int level = (int)Math.Round(100.0 * rem.Value / full.Value);
                var raw = RawGameController.FromGameController(gp);
                string name = raw?.DisplayName is { Length: > 0 } n ? n : "Геймпад";
                string id = raw?.NonRoamableId is { Length: > 0 } i ? i : name;
                // a wireless Xbox pad that is not a Bluetooth device here is on the Xbox Wireless adapter
                list.Add(new GamepadReading(id, name, Math.Clamp(level, 0, 100), rep.Status == BatteryStatus.Charging, raw?.IsWireless == true ? "адаптер Xbox" : "USB"));
            }
        }
        catch (Exception e) { Log.Info($"Gamepad read: {e.Message}"); }
        try { list.AddRange(HidGamepads.Read()); }
        catch (Exception e) { Log.Info($"HID gamepads: {e.Message}"); }
        return list;
    }

    public void Dispose()
    {
        try
        {
            Gamepad.GamepadAdded -= OnChanged;
            Gamepad.GamepadRemoved -= OnChanged;
        }
        catch { }
    }
}

/// <summary>
/// Low-frequency battery poller. Runs only while something is connected; work happens on the
/// thread pool (SetupAPI pass ≈ 1–3 ms) and results are posted to the UI thread.
/// </summary>
internal sealed class BatteryPoller : IDisposable
{
    readonly System.Threading.Timer _timer;
    readonly GamepadProvider? _pads;
    int _busy;
    bool _disposed;

    public event Action<Dictionary<ulong, int>, List<GamepadReading>, List<ReceiverReading>>? Result; // UI thread

    public BatteryPoller(GamepadProvider? pads)
    {
        _pads = pads;
        _timer = new System.Threading.Timer(_ => ReadNow(), null, Timeout.Infinite, Timeout.Infinite);
        HidReceivers.VendorChanged += OnVendor;
    }

    void OnVendor() => ReadSoon(300);

    public void SetEnabled(bool enabled, int periodSeconds)
    {
        if (_disposed) return;
        int p = Math.Max(15, periodSeconds) * 1000;
        _timer.Change(enabled ? p : Timeout.Infinite, enabled ? p : Timeout.Infinite);
    }

    public void ReadSoon(params int[] delaysMs)
    {
        foreach (var d in delaysMs)
            _ = Task.Delay(d).ContinueWith(_ => ReadNow(), TaskScheduler.Default);
    }

    void ReadNow()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var pnp = PnpBattery.ReadAll();
            var pads = _pads?.Read() ?? SafeHid();
            List<ReceiverReading> rx;
            try { rx = HidReceivers.Read(); } catch (Exception e) { Log.Info($"Receivers: {e.Message}"); rx = new List<ReceiverReading>(); }
            Ui.Post(() => { if (!_disposed) Result?.Invoke(pnp, pads, rx); });
        }
        catch (Exception e) { Log.Error("Poll", e); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static List<GamepadReading> SafeHid()
    {
        try { return HidGamepads.Read(); }
        catch { return new List<GamepadReading>(); }
    }

    public void Dispose()
    {
        _disposed = true;
        HidReceivers.VendorChanged -= OnVendor;
        _timer.Dispose();
    }
}
