using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Glance;

/// <summary>
/// Reads the battery level Windows itself shows in Settings → Bluetooth
/// (DEVPKEY_Bluetooth_Battery, filled from HFP AT commands / LE HID battery).
/// One SetupAPI pass over Bluetooth device nodes takes ~1–3 ms.
/// </summary>
internal static class PnpBattery
{
    // {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2 — DEVPROP_TYPE_BYTE
    static readonly Guid BatteryFmt = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    // DEVPKEY_Bluetooth_DeviceAddress {2BD67D8B-8BEB-48D5-87E0-6CDA3428040A}, 1 — string
    static readonly Guid AddressFmt = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A");

    static readonly string[] Enumerators = { "BTHENUM", "BTHLE", "BTHLEDEVICE" };
    static readonly Regex AddrInId = new(@"(?<![0-9A-F\-])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    const uint DEVPROP_TYPE_BYTE = 0x3, DEVPROP_TYPE_STRING = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint propType, byte[]? buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] id, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static Dictionary<ulong, int> ReadAll()
    {
        var result = new Dictionary<ulong, int>();
        foreach (var e in Enumerators)
        {
            try { Scan(e, result); }
            catch (Exception ex) { Log.Error("PnP " + e, ex); }
        }
        return result;
    }

    static void Scan(string enumerator, Dictionary<ulong, int> result)
    {
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var batteryKey = new DEVPROPKEY { fmtid = BatteryFmt, pid = 2 };
            var addressKey = new DEVPROPKEY { fmtid = AddressFmt, pid = 1 };
            var buf = new byte[256];
            var idBuf = new char[512];

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDevicePropertyW(set, ref data, ref batteryKey, out var type, buf, (uint)buf.Length, out var req, 0)) continue;
                if (type != DEVPROP_TYPE_BYTE || req < 1) continue;
                int level = buf[0];
                if (level > 100) continue;

                ulong addr = 0;
                if (SetupDiGetDevicePropertyW(set, ref data, ref addressKey, out var t2, buf, (uint)buf.Length, out var req2, 0)
                    && t2 == DEVPROP_TYPE_STRING && req2 >= 2)
                {
                    var s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)Math.Min(req2, (uint)buf.Length)).TrimEnd('\0');
                    ulong.TryParse(s.Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
                }
                if (addr == 0 && SetupDiGetDeviceInstanceIdW(set, ref data, idBuf, (uint)idBuf.Length, out _))
                {
                    int n = Array.IndexOf(idBuf, '\0');
                    if (n < 0) n = idBuf.Length;
                    var id = new string(idBuf, 0, n);
                    var matches = AddrInId.Matches(id);
                    if (matches.Count > 0)
                        ulong.TryParse(matches[^1].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
                }
                if (addr == 0) continue;
                // Several nodes per device (HFP, A2DP…) – keep the freshest non-zero value.
                if (!result.TryGetValue(addr, out var prev) || level > 0 && prev == 0) result[addr] = level;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
