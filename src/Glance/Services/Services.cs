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
