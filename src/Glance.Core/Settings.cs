using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance;

public enum TrayMode { PerDevice, Single }
/// <summary>Tray icon look: draining battery (default), plain percent, or battery with digits inside.</summary>
public enum IconStyle { Battery, Number, BatteryNumber, Device }

public sealed class DeviceSettings
{
    public string Name { get; set; } = "";
    public string? CustomName { get; set; }
    public bool HideInTray { get; set; }
    public DeviceKind Kind { get; set; }
    public int? LastLevel { get; set; }
    public DateTime LastSeen { get; set; }
    // Pro: per-device rules
    public int? LowThreshold { get; set; }
    public bool Muted { get; set; }
}

public sealed class AppSettings
{
    // Tray
    public TrayMode TrayMode { get; set; } = TrayMode.PerDevice;
    public IconStyle IconStyle { get; set; } = IconStyle.Device;
    /// <summary>One-time upgrades of stored settings (1 = device-shaped tray icons in 2.4.0).</summary>
    public int Migrations { get; set; } // absent in old files → 0 → upgraded on load
    public bool RichHover { get; set; } = true;
    public bool LiquidGlass { get; set; } = true;
    public bool ShowUnknownInTray { get; set; } = true;

    // Notifications
    public bool ShowConnectPopup { get; set; } = true;
    public int PopupSeconds { get; set; } = 6;
    public bool LowBatteryAlerts { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool FullChargeAlert { get; set; } = true;
    public bool RespectFocus { get; set; } = true;

    // Critical level: small click-through overlay that never steals focus (works over games)
    public bool CriticalOverlay { get; set; } = true;
    public int CriticalThreshold { get; set; } = 10;
    public bool CriticalSound { get; set; } = true;
    /// <summary>Glance's own sounds (glass or classic set) for connect, disconnect, low, full and updates.</summary>
    public bool Sounds { get; set; } = true;
    /// <summary>Early «скоро сядет» warning from the discharge history (&lt; 1.5 h left).</summary>
    public bool SmartReminders { get; set; } = true;
    /// <summary>Last version whose «Что нового» was shown automatically (major releases).</summary>
    public string? SeenVersion { get; set; }
    /// <summary>Pro sound pack: 0 = follows the style (Glass/Classic), 1 = Неон, 2 = Маримба.</summary>
    public int SoundPack { get; set; }

    // Pro
    public string? LicenseKey { get; set; }
    /// <summary>Accent preset: 0 = system accent, 1 Аврора, 2 Закат, 3 Мята, 4 Графит (Pro).</summary>
    public int AccentPreset { get; set; }
    public bool Widget { get; set; }
    public int WidgetX { get; set; } = int.MinValue;
    public int WidgetY { get; set; } = int.MinValue;
    public int SoundVolume { get; set; } = 60;

    // Quiet hours: low / full / connect popups stay silent at night (critical still shows)
    public bool QuietHours { get; set; }
    public int QuietFrom { get; set; } = 23;
    public int QuietTo { get; set; } = 8;

    public bool IsQuietNow()
    {
        if (!QuietHours) return false;
        int h = DateTime.Now.Hour;
        return QuietFrom <= QuietTo ? h >= QuietFrom && h < QuietTo : h >= QuietFrom || h < QuietTo;
    }

    // Earbuds
    public bool BudsDetail { get; set; } = true;
    public int RssiThreshold { get; set; } = -75;

    // System
    public int PollSeconds { get; set; } = 60;
    public bool Hotkey { get; set; } = true;

    // Updates
    public bool CheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
    public DateTime LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public DateTime UpdateRemindedAt { get; set; }
    /// <summary>«Что нового» of the version being installed, shown once after the restart.</summary>
    public string? UpdateNotes { get; set; }

    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    /// <summary>Raised (on the UI thread) after the user changed something in the settings window.</summary>
    public event Action? Changed;

    static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    Debouncer? _saver;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s is not null)
                {
                    if (s.Migrations < 1) { s.IconStyle = IconStyle.Device; s.Migrations = 1; }
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Settings load", e);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new AppSettings { Migrations = 1 };
    }

    void Normalize()
    {
        Devices ??= new();
        PopupSeconds = Math.Clamp(PopupSeconds, 2, 30);
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 5, 50);
        RssiThreshold = Math.Clamp(RssiThreshold, -95, -40);
        CriticalThreshold = Math.Clamp(CriticalThreshold, 3, 25);
        if (CriticalThreshold >= LowBatteryThreshold) CriticalThreshold = Math.Max(3, LowBatteryThreshold - 5);
        if (!Enum.IsDefined(IconStyle)) IconStyle = IconStyle.Battery;
        QuietFrom = Math.Clamp(QuietFrom, 0, 23);
        QuietTo = Math.Clamp(QuietTo, 0, 23);
        PollSeconds = Math.Clamp(PollSeconds, 15, 600);
    }

    public DeviceSettings For(string key)
    {
        if (!Devices.TryGetValue(key, out var d)) Devices[key] = d = new DeviceSettings();
        return d;
    }

    public void NotifyChanged()
    {
        Normalize();
        Changed?.Invoke();
        Save();
    }

    /// <summary>Debounced save; the write itself runs on the main thread.</summary>
    public void Save() => (_saver ??= new Debouncer(3000, SaveNow)).Trigger();

    public void SaveNow()
    {
        _saver?.Cancel();
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log.Error("Settings save", e); }
    }
}
