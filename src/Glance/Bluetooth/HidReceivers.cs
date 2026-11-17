using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Glance;

public sealed record ReceiverReading(string Id, string Name, DeviceKind Kind, int? Level, bool Charging, bool Wired = false);

/// <summary>
/// Wireless mice / keyboards / headsets on 2.4 GHz USB receivers ("2.4G Wireless Device", Unifying, Bolt,
/// Lightspeed…). Windows shows no battery for them. We detect the receiver (so the device is listed as
/// connected) and read the battery when the device publishes it through standard HID usages:
/// Generic Device Controls → Battery Strength (0x06/0x20) or Battery System → Absolute State Of Charge /
/// Remaining Capacity (0x85/0x66, 0x85/0x64). Read-only, shared access; Bluetooth HID is skipped
/// (handled by the Bluetooth providers).
/// </summary>
internal static class HidReceivers
{
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    const int HidP_Input = 0, HidP_Feature = 2;
    const int ValueCapsSize = 72;

    static readonly Regex WirelessName = new(@"2[.,]4\s?G|wireless|receiver|dongle|unifying|\bbolt\b|lightspeed|\bnano\b|\bRF\b|беспровод|приёмник|приемник|hyperspeed|speedfusion", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, char[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool HidD_GetSerialNumberString(SafeFileHandle h, char[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    sealed class Group
    {
        public string Name = "";
        public DeviceKind Kind = DeviceKind.Unknown;
        public bool Wireless;
        public int? Level;
        public bool Charging;
        public bool Xenta;
        public readonly List<string> Diag = new();
    }

    static readonly HashSet<string> Logged = new();

    public static List<ReceiverReading> Read()
    {
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new List<ReceiverReading>();
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; i < 1024 && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                string? path = DevicePath(set, ref data);
                if (path is null) continue;
                // Bluetooth HID (classic {00001124…} / LE) is handled by the Bluetooth providers.
                if (path.Contains("{00001124", StringComparison.OrdinalIgnoreCase) || path.Contains("bthledevice", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("_vid&0002", StringComparison.OrdinalIgnoreCase)) continue;
                // USB only (touchpads / I2C keyboards of laptops have no "vid_" in the path)
                if (!path.Contains("hid#vid_", StringComparison.OrdinalIgnoreCase)) continue;
                try { Inspect(path, groups); }
                catch (Exception e) { Log.Info($"HID receiver: {e.Message}"); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        foreach (var (key, g) in groups)
            if (g.Diag.Count > 0 && Logged.Add(key + g.Level))
                Log.Info($"HID {key} «{g.Name}» {g.Kind} {(g.Wireless ? "wireless" : "wired")} level={g.Level?.ToString() ?? "—"}: {string.Join("; ", g.Diag)}");

        // Wireless (receiver) and wired USB mice/keyboards; wired ones are listed as «USB», never as 2.4 GHz.
        return groups.Where(g => g.Value.Kind is DeviceKind.Mouse or DeviceKind.Keyboard or DeviceKind.Headset or DeviceKind.Gamepad
                                 && (g.Value.Wireless || g.Value.Kind is DeviceKind.Mouse or DeviceKind.Keyboard))
            .Select(g => new ReceiverReading("RX:" + g.Key, g.Value.Name, g.Value.Kind, g.Value.Level, g.Value.Charging, Wired: !g.Value.Wireless))
            .ToList();
    }

    static void Inspect(string path, Dictionary<string, Group> groups)
    {
        // Mouse/keyboard collections are opened exclusively by Windows: zero-access handles still give attributes and caps.
        using var q = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (q.IsInvalid) return;
        var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(q, ref attr)) return;
        string product = HidString(q, HidD_GetProductString) ?? "";
        string serial = HidString(q, HidD_GetSerialNumberString) ?? "";
        string key = $"{attr.VendorID:X4}:{attr.ProductID:X4}:{serial}";
        if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group();
        if (product.Length > 0 && (g.Name.Length == 0 || product.Length < g.Name.Length)) g.Name = product;
        // The receiver itself is named «2.4G…», «…Receiver», «…Dongle». A wired keyboard's secondary collection
        // with a stray word in its name must not make it «2.4 GHz»: judge by the primary (shortest) name.
        g.Wireless = WirelessName.IsMatch(g.Name) || g.Level is not null && g.Wireless || g.Xenta;
        if (attr.VendorID == XentaVid) { g.Xenta = true; g.Wireless = true; }
        if (attr.VendorID == 0x046D && product.Contains("Receiver", StringComparison.OrdinalIgnoreCase)) g.Wireless = true; // Logitech

        if (!HidD_GetPreparsedData(q, out var pp)) return;
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) return;
            if (attr.VendorID == XentaVid || g.Diag.Count < 8)
                g.Diag.Add($"{MiCol(path)} {caps.UsagePage:X4}/{caps.Usage:X2} in{caps.InputReportByteLength} f{caps.FeatureReportByteLength} «{product}»");
            if (caps.UsagePage == 0x01)
            {
                var k = caps.Usage switch { 0x02 => DeviceKind.Mouse, 0x06 => DeviceKind.Keyboard, 0x04 or 0x05 => DeviceKind.Gamepad, _ => DeviceKind.Unknown };
                // a combo receiver exposes both: show it as the keyboard set
                if (attr.VendorID == XentaVid) { if (k == DeviceKind.Mouse || g.Kind == DeviceKind.Unknown) g.Kind = DeviceKind.Mouse; } // its keyboard collection is for macros
                else if (k == DeviceKind.Keyboard || (k != DeviceKind.Unknown && g.Kind == DeviceKind.Unknown)) g.Kind = k;
            }
            else if (caps.UsagePage == 0x0B && g.Kind == DeviceKind.Unknown) g.Kind = DeviceKind.Headset; // telephony = headset

            if (g.Level is null) TryBattery(path, pp, caps, g);
            // Xenta battery frames come on a non-mouse/keyboard collection (vendor page on most receivers).
            bool kbOrMouse = caps.UsagePage == 0x01 && caps.Usage is 0x02 or 0x06;
            if (g.Level is null && attr.VendorID == XentaVid && !kbOrMouse && caps.InputReportByteLength is >= 5 and <= 65)
                TryXenta(path, caps.InputReportByteLength, key, product, g);
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    static void TryBattery(string path, IntPtr pp, HIDP_CAPS caps, Group g)
    {
        foreach (var (type, count, len) in new[] { (HidP_Feature, caps.NumberFeatureValueCaps, caps.FeatureReportByteLength), (HidP_Input, caps.NumberInputValueCaps, caps.InputReportByteLength) })
        {
            if (count == 0 || len == 0) continue;
            ushort n = count;
            var buf = new byte[ValueCapsSize * n];
            if (HidP_GetValueCaps(type, buf, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
            for (int i = 0; i < n; i++)
            {
                int o = i * ValueCapsSize;
                ushort page = BitConverter.ToUInt16(buf, o);
                byte reportId = buf[o + 2];
                ushort link = BitConverter.ToUInt16(buf, o + 6);
                bool isRange = buf[o + 12] != 0;
                int lmin = BitConverter.ToInt32(buf, o + 40), lmax = BitConverter.ToInt32(buf, o + 44);
                ushort usage = BitConverter.ToUInt16(buf, o + 56);
                if (isRange) continue;
                bool strength = page == 0x06 && usage == 0x20;
                bool soc = page == 0x85 && (usage == 0x65 || usage == 0x66 || usage == 0x64);
                if (!strength && !soc) continue;

                var report = new byte[len];
                report[0] = reportId;
                using var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return;
                bool ok = type == HidP_Feature ? HidD_GetFeature(h, report, report.Length) : HidD_GetInputReport(h, report, report.Length);
                if (!ok) continue;
                if (HidP_GetUsageValue(type, page, link, usage, out uint v, pp, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) continue;
                int pct = soc && usage != 0x64 ? (int)v : lmax > lmin ? (int)Math.Round(100.0 * ((int)v - lmin) / (lmax - lmin)) : (int)v;
                if (pct is < 0 or > 100) continue;
                g.Level = pct;
                g.Wireless = true; // something that reports a battery is a wireless device
                return;
            }
        }
    }

    // ───────────── Vendor protocols ─────────────

    /// <summary>Xenta chipset (VID 1D57): Attack Shark G3 / G3 Pro, X11, R1, X3, V6… — receiver 1D57:FA60/FA65, cable FA55/FA61/2155/2224.</summary>
    const ushort XentaVid = 0x1D57;
    static readonly Regex GenericReceiverName = new(@"^(LXDDZ\s+)?2[.,]4\s?G(\s+Wireless)?(\s+Device|\s+Receiver|\s+Mouse)?$|^USB DEVICE$|^$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Dictionary<string, (int Level, bool Charging, DateTime At)> LastVendor = new();
    static readonly object VendorLock = new();

    /// <summary>
    /// These mice report no standard battery field. Their receiver pushes a vendor input report (id 3) every ~2 s:
    /// <c>03 MM 4x ST NN</c> — MM model byte, ST 01 on battery / 02 idle / 03 charging, NN level 0–100 %.
    /// We only listen (read-only handle, nothing is ever written to the mouse) for up to ~4.5 s and accept a frame
    /// only after seeing it twice in the exact format. While the mouse sleeps the last level is kept for 30 min.
    /// </summary>
    static void TryXenta(string path, int reportLen, string key, string product, Group g)
    {
        const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        (int Level, bool Charging)? found = null;
        try
        {
            using var h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) g.Diag.Add($"{MiCol(path)} open failed ({Marshal.GetLastWin32Error()})");
            else
            {
                using var fs = new FileStream(h, FileAccess.Read, 0, isAsync: true);
                var buf = new byte[Math.Max(reportLen, 8)];
                var deadline = DateTime.UtcNow.AddMilliseconds(4500);
                (int, bool)? prev = null;
                for (int i = 0; i < 12 && found is null; i++)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    using var cts = new CancellationTokenSource(left);
                    int n;
                    try { n = fs.ReadAsync(buf, 0, reportLen, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { break; }
                    if (n < 5) continue;
                    if (g.Diag.Count < 24) g.Diag.Add("rx " + Convert.ToHexString(buf, 0, Math.Min(n, 8)));
                    var frame = VendorBattery.ParseXenta(buf.AsSpan(0, n));
                    if (frame is null) continue;
                    if (prev == frame) found = frame; // sent twice in a row: confirmed
                    prev = frame;
                }
                // A single valid frame is still good when the window closed before the repeat.
                found ??= prev;
            }
        }
        catch (Exception e) { g.Diag.Add($"{MiCol(path)} read failed: {e.Message}"); }

        lock (VendorLock)
        {
            if (found is (int lvl, bool ch)) LastVendor[key] = (lvl, ch, DateTime.UtcNow);
            else if (LastVendor.TryGetValue(key, out var last) && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(30)) found = (last.Level, last.Charging);
        }
        if (found is not (int level, bool charging)) return;
        g.Level = level;
        g.Charging = charging;
        g.Wireless = true;
        if (g.Kind == DeviceKind.Unknown) g.Kind = DeviceKind.Mouse;
        if (GenericReceiverName.IsMatch(product.Trim()) && (g.Name.Length == 0 || GenericReceiverName.IsMatch(g.Name))) g.Name = "Attack Shark";
    }

    static string MiCol(string path)
    {
        var mi = Regex.Match(path, @"mi_\d+", RegexOptions.IgnoreCase).Value;
        var col = Regex.Match(path, @"col\d+", RegexOptions.IgnoreCase).Value;
        return (mi + " " + col).Trim();
    }

    static string? DevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required < 6 || required > 4096) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
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
}
