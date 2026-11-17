namespace Glance;

/// <summary>Parsers for vendor battery reports of 2.4 GHz mice (pure functions — unit-tested).</summary>
internal static class VendorBattery
{
    /// <summary>
    /// Xenta (VID 1D57, Attack Shark G3 / G3 Pro / X11 / R1 / V6…) receiver push report <c>03 MM 4x ST NN</c>:
    /// ST 01 on battery, 02 idle, 03 charging; NN level 0–100 %. Null for anything else (e.g. link-blink <c>03 E5 01 00 00</c>).
    /// </summary>
    public static (int Level, bool Charging)? ParseXenta(ReadOnlySpan<byte> r)
    {
        if (r.Length < 5 || r[0] != 0x03) return null;
        if (r[2] is not (0x40 or 0x41)) return null;
        if (r[3] is not (0x01 or 0x02 or 0x03)) return null;
        if (r[4] > 100) return null;
        return (r[4], r[3] == 0x03);
    }
}
