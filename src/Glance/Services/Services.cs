using Microsoft.Win32;

namespace Glance;

/// <summary>HKCU Run entry that respects the Task Manager "Startup apps" switch.</summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "Glance";

    static string Command(string exe) => $"\"{exe}\" --autostart";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(ValueName) is not string { Length: > 0 }) return false;
            using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // first byte odd (03/07) = disabled by the user in Task Manager
            if (a?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1) return false;
            return true;
        }
        catch { return false; }
    }

    public static void Set(bool enabled, string? exePath = null)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                k.SetValue(ValueName, Command(exePath ?? AppInfo.ExePath));
                using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                a?.DeleteValue(ValueName, false);
            }
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception e) { Log.Error("Autostart", e); }
    }

    /// <summary>Exe was moved or updated – keep the Run entry pointing at the current path.</summary>
    public static void Repair()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            var cmd = Command(AppInfo.ExePath);
            if (k?.GetValue(ValueName) is string cur && cur.Length > 0 && !string.Equals(cur, cmd, StringComparison.OrdinalIgnoreCase)
                && Installer.IsInstalledCopy())
                k.SetValue(ValueName, cmd);
        }
        catch { }
    }
}

/// <summary>
/// Windows mouse / keyboard parameters (the same as in Settings → Bluetooth & devices), read and written through
/// SystemParametersInfo for the current user. Glance does not touch the device firmware (sensor DPI stays as set
/// on the mouse); these are the system pointer speed, acceleration, wheel and key-repeat settings.
/// </summary>
internal static class SystemInput
{
    public readonly record struct State(int PointerSpeed, bool EnhancePrecision, int WheelLines, int KeyDelay, int KeyRate);

    const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004, SPI_GETKEYBOARDSPEED = 0x000A, SPI_SETKEYBOARDSPEED = 0x000B,
        SPI_GETKEYBOARDDELAY = 0x0016, SPI_SETKEYBOARDDELAY = 0x0017, SPI_GETWHEELSCROLLLINES = 0x0068, SPI_SETWHEELSCROLLLINES = 0x0069,
        SPI_GETMOUSESPEED = 0x0070, SPI_SETMOUSESPEED = 0x0071;
    const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfoW(uint action, uint uiParam, ref int pvParam, uint winIni);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfoW(uint action, uint uiParam, int[] pvParam, uint winIni);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfoW(uint action, uint uiParam, IntPtr pvParam, uint winIni);

    public static State Read()
    {
        int speed = 10, lines = 3, delay = 1, rate = 31;
        var acc = new int[3];
        try
        {
            SystemParametersInfoW(SPI_GETMOUSESPEED, 0, ref speed, 0);
            SystemParametersInfoW(SPI_GETMOUSE, 0, acc, 0);
            SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, ref lines, 0);
            SystemParametersInfoW(SPI_GETKEYBOARDDELAY, 0, ref delay, 0);
            SystemParametersInfoW(SPI_GETKEYBOARDSPEED, 0, ref rate, 0);
        }
        catch (Exception e) { Log.Info("Input read: " + e.Message); }
        return new State(Math.Clamp(speed, 1, 20), acc[2] != 0, Math.Clamp(lines < 0 ? 3 : lines, 1, 20), Math.Clamp(delay, 0, 3), Math.Clamp(rate, 0, 31));
    }

    static void Apply(string what, Func<bool> f)
    {
        try { if (!f()) Log.Info($"Input {what}: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"); }
        catch (Exception e) { Log.Info($"Input {what}: {e.Message}"); }
    }

    public static void SetPointerSpeed(int v) => Apply("speed", () => SystemParametersInfoW(SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(v, 1, 20), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    public static void SetEnhancePrecision(bool on) => Apply("precision", () => SystemParametersInfoW(SPI_SETMOUSE, 0, on ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    public static void SetWheelLines(int v) => Apply("wheel", () => SystemParametersInfoW(SPI_SETWHEELSCROLLLINES, (uint)Math.Clamp(v, 1, 20), IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    public static void SetKeyDelay(int v) => Apply("delay", () => SystemParametersInfoW(SPI_SETKEYBOARDDELAY, (uint)Math.Clamp(v, 0, 3), IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    public static void SetKeyRate(int v) => Apply("rate", () => SystemParametersInfoW(SPI_SETKEYBOARDSPEED, (uint)Math.Clamp(v, 0, 31), IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));

    /// <summary>Approximate characters per second for the 0–31 repeat-rate setting (≈2.5 … 30).</summary>
    public static double RateToCps(int v) => 2.5 + Math.Clamp(v, 0, 31) * (30 - 2.5) / 31;

    public static void ResetDefaults()
    {
        SetPointerSpeed(10);
        SetEnhancePrecision(true);
        SetWheelLines(3);
        SetKeyDelay(1);
        SetKeyRate(31);
    }
}
