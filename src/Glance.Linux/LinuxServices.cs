using System.Collections.Concurrent;
using System.Globalization;
using Glance.DBus;
using Tmds.DBus;

namespace Glance;

/// <summary>Single-threaded main loop: every model mutation runs here (D-Bus callbacks arrive on the thread pool).</summary>
internal sealed class MainLoop
{
    readonly BlockingCollection<Action> _queue = new();
    volatile bool _quit;

    public MainLoop() => Dispatch.Post = Post;

    public void Post(Action a)
    {
        if (!_quit) try { _queue.Add(a); } catch (InvalidOperationException) { }
    }

    public void Delay(int ms, Action a) => _ = Task.Delay(ms).ContinueWith(_ => Post(a), TaskScheduler.Default);

    public void Run()
    {
        // Awaits started on the main thread resume on it, so state never needs locks.
        SynchronizationContext.SetSynchronizationContext(new LoopContext(this));
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception e) { Log.Error("Main loop", e); }
            if (_quit) break;
        }
    }

    public void Quit()
    {
        _quit = true;
        _queue.CompleteAdding();
    }

    sealed class LoopContext : SynchronizationContext
    {
        readonly MainLoop _loop;
        public LoopContext(MainLoop loop) => _loop = loop;
        public override void Post(SendOrPostCallback d, object? state) => _loop.Post(() => d(state));
        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>Desktop notifications with an inline battery image; critical ones never steal focus.</summary>
internal sealed class Notifier
{
    readonly INotifications? _n;
    readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
    readonly string _iconPath;

    public Notifier(Connection session, string iconPath)
    {
        _iconPath = iconPath;
        try { _n = session.CreateProxy<INotifications>("org.freedesktop.Notifications", new ObjectPath("/org/freedesktop/Notifications")); }
        catch (Exception e) { Log.Info($"Notifications unavailable: {e.Message}"); }
    }

    public enum Urgency : byte { Low = 0, Normal = 1, Critical = 2 }

    /// <summary>Shows or replaces the notification identified by <paramref name="key"/>.</summary>
    public async Task ShowAsync(string key, string summary, string body, Urgency urgency, Canvas? image, int? level, int timeoutMs = 6000)
    {
        if (_n is null) return;
        var hints = new Dictionary<string, object>
        {
            ["urgency"] = (byte)urgency,
            ["category"] = "device",
            ["desktop-entry"] = "glance",
        };
        if (level is int l) hints["value"] = Math.Clamp(l, 0, 100); // progress bar on KDE / XFCE / Cinnamon
        if (urgency == Urgency.Critical) hints["sound-name"] = "battery-caution";
        if (image is not null)
            hints["image-data"] = (image.W, image.H, image.W * 4, true, 8, 4, image.ToRgba());
        try
        {
            _ids.TryGetValue(key, out var replaces);
            uint id = await _n.NotifyAsync("Glance", replaces, File.Exists(_iconPath) ? _iconPath : "battery", summary, body,
                Array.Empty<string>(), hints, urgency == Urgency.Critical ? 0 : timeoutMs);
            Dispatch.Post(() => _ids[key] = id);
        }
        catch (Exception e) { Log.Info($"Notify: {e.Message}"); }
    }
}

/// <summary>
/// UPower (system bus) knows every peripheral battery the kernel or BlueZ exposes: Bluetooth headsets
/// (BlueZ Battery1), Logitech Unifying/Bolt receivers (HID++), DualSense/DualShock (hid-playstation),
/// Xbox pads (xpadneo/xone), Nintendo pads (hid-nintendo), Apple Magic devices, many 2.4 GHz mice and
/// keyboards. Fully event-driven: no polling at all.
/// </summary>
internal sealed class UPowerMonitor
{
    readonly Connection _system;
    readonly Dictionary<string, (Accessory Acc, IDisposable? Watch)> _byPath = new(StringComparer.Ordinal);
    readonly AppSettings _settings;
    bool _initial = true;

    public event Action<Accessory, bool>? Connected;
    public event Action<Accessory>? Disconnected;
    public event Action<Accessory>? Changed;

    public UPowerMonitor(Connection system, AppSettings settings)
    {
        _system = system;
        _settings = settings;
    }

    public IEnumerable<Accessory> Devices => _byPath.Values.Select(v => v.Acc).Where(a => a.Connected);

    public async Task StartAsync()
    {
        var up = _system.CreateProxy<IUPower>("org.freedesktop.UPower", new ObjectPath("/org/freedesktop/UPower"));
        await up.WatchDeviceAddedAsync(p => _ = AddAsync(p, false), e => Log.Info($"UPower added: {e.Message}"));
        await up.WatchDeviceRemovedAsync(p => Dispatch.Post(() => Remove(p.ToString())), e => Log.Info($"UPower removed: {e.Message}"));
        foreach (var p in await up.EnumerateDevicesAsync()) await AddAsync(p, true);
        Dispatch.Post(() => _initial = false);
    }

    async Task AddAsync(ObjectPath path, bool initial)
    {
        try
        {
            var dev = _system.CreateProxy<IUPowerDevice>("org.freedesktop.UPower", path);
            var props = await dev.GetAllAsync();
            if (!IsPeripheral(props)) return;
            var watch = await dev.WatchPropertiesAsync(ch =>
            {
                var changed = ch.Changed.ToDictionary(kv => kv.Key, kv => kv.Value);
                Dispatch.Post(() => Apply(path.ToString(), changed));
            });
            Dispatch.Post(() => Add(path.ToString(), props, watch, initial || _initial));
        }
        catch (Exception e) { Log.Info($"UPower device {path}: {e.Message}"); }
    }

    static bool IsPeripheral(IDictionary<string, object> p)
    {
        // PowerSupply=false means "not powering this computer": peripherals, not laptop batteries/AC/UPS.
        if (p.TryGetValue("PowerSupply", out var ps) && ps is bool b && b) return false;
        uint type = Get<uint>(p, "Type");
        return type is not (0 or 1 or 3); // unknown, line power, UPS
    }

    void Add(string path, IDictionary<string, object> props, IDisposable watch, bool initial)
    {
        if (_byPath.ContainsKey(path)) { watch.Dispose(); return; }
        string serial = Get<string>(props, "Serial") ?? "";
        string key = LooksLikeMac(serial) ? "BT:" + serial.Replace(":", "").ToUpperInvariant() : "UP:" + (Get<string>(props, "NativePath") ?? path);
        var a = new Accessory(key);
        var ds = _settings.For(key);
        a.CustomName = ds.CustomName;
        a.LastLevel = ds.LastLevel;
        _byPath[path] = (a, watch);
        Apply(path, props, raise: false);
        bool present = !(props.TryGetValue("IsPresent", out var pr) && pr is bool pb && !pb);
        if (present) SetConnected(a, true, initial);
    }

    void SetConnected(Accessory a, bool connected, bool initial)
    {
        if (a.Connected == connected) return;
        a.Connected = connected;
        a.LastSeen = DateTime.Now;
        if (connected)
        {
            a.ConnectedAt = DateTime.Now;
            a.SessionStartPending = true;
            Connected?.Invoke(a, initial);
            Changed?.Invoke(a);
        }
        else
        {
            if (a.Level is int l) a.LastLevel = l;
            Disconnected?.Invoke(a);
        }
    }

    void Apply(string path, IDictionary<string, object> p, bool raise = true)
    {
        if (!_byPath.TryGetValue(path, out var entry)) return;
        var a = entry.Acc;
        bool changed = false;

        string? model = Get<string>(p, "Model");
        if (model is not null)
        {
            string vendor = Get<string>(p, "Vendor") ?? "";
            string name = model.Length > 0 ? model : vendor;
            if (vendor.Length > 0 && model.Length > 0 && !model.Contains(vendor, StringComparison.OrdinalIgnoreCase) && vendor.Length < 20)
                name = $"{vendor} {model}";
            if (name.Length > 0 && name != a.Name) { a.Name = name; changed = true; }
        }
        if (p.ContainsKey("Type") || model is not null)
        {
            var hint = KindFromType(Get<uint>(p, "Type"));
            if (hint != DeviceKind.Unknown) a.KindHint = hint;
            a.Profile = Profiles.Match(a.Name, a.KindHint) with { Buds = BudsSource.None };
        }

        if (p.ContainsKey("Percentage") || p.ContainsKey("BatteryLevel"))
        {
            int? level = LevelFrom(p, a.SystemLevel);
            if (level != a.SystemLevel) { a.SystemLevel = level; a.SystemRank = 1; a.SystemSource = "UPower"; changed = true; }
        }
        if (p.ContainsKey("State"))
        {
            uint st = Get<uint>(p, "State");
            bool charging = st is 1 or 4 or 5;
            if (charging != a.SystemCharging) { a.SystemCharging = charging; changed = true; }
        }
        if (raise && p.TryGetValue("IsPresent", out var present) && present is bool isP)
        {
            // Wireless pads/mice behind a receiver stay listed but go "not present" when switched off.
            SetConnected(a, isP, false);
            if (!isP) return;
        }
        if (a.Level is int l) { a.LastLevel = l; a.LastSeen = DateTime.Now; }
        if (changed && raise) Changed?.Invoke(a);
    }

    void Remove(string path)
    {
        if (!_byPath.Remove(path, out var entry)) return;
        entry.Watch?.Dispose();
        SetConnected(entry.Acc, false, false);
    }

    static int? LevelFrom(IDictionary<string, object> p, int? current)
    {
        // Coarse devices report BatteryLevel instead of a percentage.
        uint bl = p.TryGetValue("BatteryLevel", out var blv) && blv is uint u ? u : 1;
        switch (bl)
        {
            case 3: return 15;  // low
            case 4: return 5;   // critical
            case 6: return 55;  // normal
            case 7: return 85;  // high
            case 8: return 100; // full
        }
        if (p.TryGetValue("Percentage", out var pv) && pv is double d && d >= 0)
            return (int)Math.Round(Math.Clamp(d, 0, 100));
        return current;
    }

    static DeviceKind KindFromType(uint t) => t switch
    {
        5 => DeviceKind.Mouse,
        6 => DeviceKind.Keyboard,
        8 => DeviceKind.Phone,
        12 => DeviceKind.Gamepad,
        13 => DeviceKind.Pen,
        14 => DeviceKind.Mouse,      // touchpad
        17 => DeviceKind.Headset,
        18 => DeviceKind.Speaker,
        19 => DeviceKind.Headphones,
        21 => DeviceKind.Headphones, // other audio
        26 => DeviceKind.Watch,      // wearable
        _ => DeviceKind.Unknown,
    };

    static bool LooksLikeMac(string s) =>
        s.Length == 17 && s.Split(':').Length == 6 && s.Replace(":", "").All(Uri.IsHexDigit);

    static T? Get<T>(IDictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) && v is T t ? t : default;
}

internal static class Icons
{
    public static string InstalledIconPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "icons", "hicolor", "256x256", "apps", "glance.png");

    public static byte[]? Png()
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream("glance.png");
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

internal static class Num
{
    public static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
}
