using Glance;
using Xunit;

namespace Glance.Tests;

public class ProfilesTests
{
    [Theory]
    [InlineData("JBL Tune 230NC TWS", "JBL", DeviceKind.Earbuds, true)]
    [InlineData("JBL TUNE520BT", "JBL", DeviceKind.Headphones, false)]
    [InlineData("JBL Tune 215BT", "JBL", DeviceKind.Neckband, false)]
    [InlineData("JBL Flip 6", "JBL", DeviceKind.Speaker, false)]
    [InlineData("JBL Quantum 910 Wireless", "JBL", DeviceKind.Headset, false)]
    [InlineData("AirPods Pro", "Apple", DeviceKind.Earbuds, true)]
    [InlineData("AirPods Max", "Apple", DeviceKind.Headphones, false)]
    [InlineData("WH-1000XM5", "Sony", DeviceKind.Headphones, false)]
    [InlineData("WF-1000XM5", "Sony", DeviceKind.Earbuds, true)]
    [InlineData("LE_WH-1000XM4", "Sony", DeviceKind.Headphones, false)]
    [InlineData("Wireless Controller", "Sony", DeviceKind.Gamepad, false)]
    [InlineData("Xbox Wireless Controller", "Microsoft", DeviceKind.Gamepad, false)]
    [InlineData("Galaxy Buds2 Pro (1A2B)", "Samsung", DeviceKind.Earbuds, true)]
    [InlineData("MX Master 3S", "Logitech", DeviceKind.Mouse, false)]
    [InlineData("MX Keys S", "Logitech", DeviceKind.Keyboard, false)]
    [InlineData("Keychron K2", "Keychron", DeviceKind.Keyboard, false)]
    [InlineData("Яндекс Станция Мини", "Яндекс", DeviceKind.Speaker, false)]
    [InlineData("OpenRun Pro", "Shokz", DeviceKind.Neckband, false)]
    public void Recognises_brand_and_kind(string name, string brand, DeviceKind kind, bool hasCase)
    {
        var p = Profiles.Match(name, DeviceKind.Unknown);
        Assert.Equal(brand, p.Brand);
        Assert.Equal(kind, p.Kind);
        Assert.Equal(hasCase, p.HasCase);
    }

    [Fact]
    public void Unknown_name_uses_radio_hint()
    {
        Assert.Equal(DeviceKind.Mouse, Profiles.Match("BT5.0 Device", DeviceKind.Mouse).Kind);
        Assert.Equal(DeviceKind.Earbuds, Profiles.Match("My Earbuds", DeviceKind.Headphones).Kind);
    }

    [Fact]
    public void Earbuds_get_per_bud_source()
    {
        Assert.Equal(BudsSource.AppleContinuity, Profiles.Match("AirPods Pro", DeviceKind.Unknown).Buds);
        Assert.Equal(BudsSource.FastPair, Profiles.Match("JBL Live Pro 2 TWS", DeviceKind.Unknown).Buds);
        Assert.Equal(BudsSource.None, Profiles.Match("JBL Live 770NC", DeviceKind.Unknown).Buds);
    }

    [Fact]
    public void Jbl_model_and_features()
    {
        var p = Profiles.Match("JBL Tour Pro 2", DeviceKind.Unknown);
        Assert.Equal("Tour Pro 2", p.Model);
        Assert.True(p.Anc);
        Assert.True(p.SmartCase);
    }

    [Fact]
    public void Normalizes_le_prefix() => Assert.Equal("WH-1000XM4", Profiles.NormalizeName("LE_WH-1000XM4"));
}

public class AdvertParserTests
{
    [Fact]
    public void Parses_airpods_pro()
    {
        // 07 19 | 01 | model 0E20 | status 20 (not flipped) | pods R=5 L=8 | charge 0100 (case) case=7 | ...
        byte[] d = { 0x07, 0x19, 0x01, 0x0E, 0x20, 0x20, 0x58, 0x47, 0x00, 0x00, 0x00 };
        Assert.True(AppleContinuity.TryParse(d, out var r));
        Assert.Equal(BudsSource.AppleContinuity, r.Vendor);
        Assert.Equal("AirPods Pro", r.Model);
        Assert.Equal(new Part(80, false), r.Left);
        Assert.Equal(new Part(50, false), r.Right);
        Assert.Equal(new Part(70, true), r.Case);
    }

    [Fact]
    public void Apple_flipped_and_unknown_case()
    {
        // status 00 → flipped: left = high nibble
        byte[] d = { 0x07, 0x19, 0x01, 0x14, 0x20, 0x00, 0xA3, 0x1F, 0x00 };
        Assert.True(AppleContinuity.TryParse(d, out var r));
        Assert.Equal(100, r.Left.Level);
        Assert.Equal(30, r.Right.Level);
        Assert.False(r.Case.Known);
        Assert.False(r.Left.Charging);
        Assert.True(r.Right.Charging); // flipped: bit 0 = right
    }

    [Fact]
    public void Rejects_short_or_foreign_apple_data()
    {
        Assert.False(AppleContinuity.TryParse(new byte[] { 0x10, 0x05, 0x01 }, out _));
        Assert.False(AppleContinuity.TryParse(new byte[] { 0x07, 0x19, 0x01, 0x0E, 0x20, 0x20, 0xFF, 0x0F, 0x00 }, out _));
    }

    [Fact]
    public void Parses_fast_pair_battery_after_account_key_filter()
    {
        byte[] p =
        {
            0x00,                                       // flags
            0x40, 0xDE, 0xAD, 0xBE, 0xEF,               // account key filter, len 4
            0x11, 0x42,                                 // salt, len 1
            0x33, 0x55, 0xC0, 0x7F,                     // battery: L 85, R 64 charging, case unknown
        };
        Assert.True(FastPair.TryParse(p, out var r));
        Assert.Equal(new Part(85, false), r.Left);
        Assert.Equal(new Part(64, true), r.Right);
        Assert.False(r.Case.Known);
    }

    [Fact]
    public void Fast_pair_model_id_only_is_ignored() =>
        Assert.False(FastPair.TryParse(new byte[] { 0x12, 0x34, 0x56 }, out _));

    [Fact]
    public void Fast_pair_truncated_field_is_safe() =>
        Assert.False(FastPair.TryParse(new byte[] { 0x00, 0x33, 0x55 }, out _));
}

public class MatcherTests
{
    static Accessory Target(string name, BudsSource src) =>
        new("BT:" + name) { Name = name, Connected = true, Profile = new DeviceProfile { Kind = DeviceKind.Earbuds, Buds = src } };

    static BudsAdvert Adv(ulong addr, short rssi, string? model, int l) =>
        new BudsAdvert(BudsSource.AppleContinuity, new Part(l, false), new Part(l, false), Part.Unknown, model)
        { Address = addr, Rssi = rssi, AtUtc = DateTime.UtcNow };

    [Fact]
    public void Picks_strong_and_model_matching_candidate()
    {
        var m = new BudsMatcher();
        var t = Target("AirPods Pro", BudsSource.AppleContinuity);
        m.Match(Adv(1, -80, "AirPods 2", 40), new[] { t }, -90);           // neighbour's, weaker, other model
        var res = m.Match(Adv(2, -55, "AirPods Pro 2", 70), new[] { t }, -90);
        Assert.Single(res);
        Assert.Equal(2UL, res[0].Adv.Address);
    }

    [Fact]
    public void Ignores_candidates_below_threshold()
    {
        var m = new BudsMatcher();
        var res = m.Match(Adv(3, -95, "AirPods Pro", 70), new[] { Target("AirPods Pro", BudsSource.AppleContinuity) }, -75);
        Assert.Empty(res);
    }
}

public class HistoryTests
{
    [Fact]
    public void Estimates_remaining_time_from_discharge()
    {
        var h = new HistoryStore();
        string key = "TEST:" + Guid.NewGuid();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        h.AddRaw(key, new Sample(now - 2 * 3600, 100, Sample.SessionStart));
        h.AddRaw(key, new Sample(now - 3600, 90, 0));
        h.AddRaw(key, new Sample(now, 80, 0));
        var e = h.Estimate(key, 80, charging: false);
        Assert.NotNull(e);
        Assert.InRange(e!.Value.TotalHours, 7.5, 8.5); // 10 %/h → 8 h left
        h.Remove(key);
    }

    [Fact]
    public void No_estimate_without_enough_data()
    {
        var h = new HistoryStore();
        string key = "TEST:" + Guid.NewGuid();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        h.AddRaw(key, new Sample(now - 60, 50, Sample.SessionStart));
        h.AddRaw(key, new Sample(now, 49, 0));
        Assert.Null(h.Estimate(key, 49, false));
        h.Remove(key);
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(30, "меньше минуты")]
    [InlineData(45 * 60, "45 мин")]
    [InlineData(2 * 3600, "2 ч")]
    [InlineData(3 * 3600 + 20 * 60, "3 ч 20 мин")]
    public void Duration(int seconds, string expected) => Assert.Equal(expected, Fmt.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Percent() { Assert.Equal("42%", Fmt.Percent(42)); Assert.Equal("—", Fmt.Percent(null)); }
}

public class VendorBatteryTests
{
    [Theory]
    [InlineData(new byte[] { 0x03, 0xE5, 0x40, 0x01, 0x57 }, 87, false)]
    [InlineData(new byte[] { 0x03, 0x55, 0x40, 0x03, 0x64 }, 100, true)]
    [InlineData(new byte[] { 0x03, 0x24, 0x41, 0x02, 0x0A, 0x00, 0x00 }, 10, false)]
    public void Xenta_battery_frames(byte[] frame, int level, bool charging)
    {
        var r = VendorBattery.ParseXenta(frame);
        Assert.NotNull(r);
        Assert.Equal(level, r!.Value.Level);
        Assert.Equal(charging, r.Value.Charging);
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0xE5, 0x01, 0x00, 0x00 })] // link blink
    [InlineData(new byte[] { 0x02, 0xE5, 0x40, 0x01, 0x50 })] // other report
    [InlineData(new byte[] { 0x03, 0xE5, 0x40, 0x01, 0xC8 })] // >100
    [InlineData(new byte[] { 0x03, 0xE5, 0x40 })]
    public void Xenta_rejects_noise(byte[] frame) => Assert.Null(VendorBattery.ParseXenta(frame));

    [Fact]
    public void Attack_Shark_is_a_mouse()
    {
        var p = Profiles.Match("ATTACK SHARK G3 PRO", DeviceKind.Unknown);
        Assert.Equal(DeviceKind.Mouse, p.Kind);
    }
}
