using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glance;

/// <summary>
/// Battery of Sony controllers (DualSense, DualSense Edge, DualShock 4) read straight from their HID
/// input reports – Windows itself does not expose it. Opened in shared read mode, so games and Steam
/// keep working. Layout follows the Linux hid-playstation driver.
/// USB and Bluetooth "full" reports are supported; in BT "basic" mode (before any game/Steam
/// switched the controller to full mode) the report carries no battery and is skipped.
/// </summary>
internal static class HidGamepads
{
    const ushort SonyVid = 0x054C;
    static readonly HashSet<ushort> DualSense = new() { 0x0CE6, 0x0DF2 };
    static readonly HashSet<ushort> DualShock4 = new() { 0x05C4, 0x09CC, 0x0BA0 };

    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, char[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool HidD_GetSerialNumberString(SafeFileHandle h, char[] buffer, uint length);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    public static List<GamepadReading> Read()
    {
        var list = new List<GamepadReading>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; i < 512 && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                string? path = DevicePath(set, ref data);
                if (path is null) continue;
                // Cheap pre-filter on the path before opening anything.
                if (path.IndexOf("vid_054c", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("054c", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var r = ReadOne(path);
                    if (r is not null && seenSerials.Add(r.Id)) list.Add(r);
                }
                catch (Exception e) { Log.Info($"HID read: {e.Message}"); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    static string? DevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required < 6 || required > 4096) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static GamepadReading? ReadOne(string path)
    {
        ushort pid;
        string serial, product;
        using (var q = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (q.IsInvalid) return null;
            var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(q, ref attr) || attr.VendorID != SonyVid) return null;
            pid = attr.ProductID;
            if (!DualSense.Contains(pid) && !DualShock4.Contains(pid)) return null;
            serial = HidString(q, HidD_GetSerialNumberString) ?? path;
            product = HidString(q, HidD_GetProductString) ?? (DualSense.Contains(pid) ? "DualSense Wireless Controller" : "Wireless Controller");
        }

        using var h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) return null; // opened exclusively by another tool
        using var fs = new FileStream(h, FileAccess.Read, 0, isAsync: true);
        var buf = new byte[1024];
        // Up to 3 reports: the first may be a short "basic" one.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int n;
            using (var cts = new CancellationTokenSource(400))
            {
                try { n = fs.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return null; } // idle controller over BT sends nothing
            }
            if (n <= 0) return null;
            var parsed = DualSense.Contains(pid) ? ParseDualSense(buf.AsSpan(0, n)) : ParseDualShock4(buf.AsSpan(0, n));
            if (parsed is { } p)
                return new GamepadReading("HID:" + serial, product, p.Item1, p.Item2);
        }
        return null;
    }

    delegate bool HidStringFn(SafeFileHandle h, char[] buffer, uint length);

    static string? HidString(SafeFileHandle h, HidStringFn fn)
    {
        var b = new char[128];
        if (!fn(h, b, (uint)(b.Length * 2))) return null;
        int len = Array.IndexOf(b, '\0');
        var s = new string(b, 0, len < 0 ? b.Length : len).Trim();
        return s.Length > 0 ? s : null;
    }

    // DualSense: status byte = data[52] after the report ID (USB 0x01: index 53; BT 0x31: one extra byte → 54).
    // Low nibble 0..10 capacity, high nibble 0 discharging / 1 charging / 2 full.
    static (int, bool)? ParseDualSense(ReadOnlySpan<byte> r)
    {
        int idx;
        if (r[0] == 0x01 && r.Length >= 64) idx = 53;
        else if (r[0] == 0x31 && r.Length >= 78) idx = 54;
        else return null;
        int cap = r[idx] & 0x0F, st = r[idx] >> 4;
        return st switch
        {
            0x0 => (Math.Min(cap * 10 + 5, 100), false),
            0x1 => (Math.Min(cap * 10 + 5, 100), true),
            0x2 => (100, true),
            _ => null, // error / temperature states
        };
    }

    // DualShock 4: status = data[29] after the report ID (USB 0x01: index 30; BT 0x11: two extra bytes → 32).
    // Low nibble capacity (0..10, 11 = full on cable), bit 4 = cable connected.
    static (int, bool)? ParseDualShock4(ReadOnlySpan<byte> r)
    {
        int idx;
        if (r[0] == 0x01 && r.Length >= 64) idx = 30;
        else if (r[0] == 0x11 && r.Length >= 78) idx = 32;
        else return null;
        int cap = r[idx] & 0x0F;
        bool cable = (r[idx] & 0x10) != 0;
        if (cable) return cap >= 10 ? (100, true) : (cap * 10 + 5, true);
        return (Math.Min(cap * 10 + 5, 100), false);
    }
}
