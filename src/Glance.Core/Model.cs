namespace Glance;

public enum DeviceKind { Unknown, Earbuds, Headphones, Headset, Neckband, Speaker, Mouse, Keyboard, Gamepad, Pen, Watch, Phone }

/// <summary>Where per-earbud battery data can come from.</summary>
public enum BudsSource { None, AppleContinuity, FastPair }

/// <summary>One battery compartment (left bud, right bud, case, keyboard half…).</summary>
public readonly record struct Part(int? Level, bool Charging)
{
    public bool Known => Level.HasValue;
    public static readonly Part Unknown = new(null, false);
}

/// <summary>Per-model knowledge used for layout and behaviour.</summary>
public sealed record DeviceProfile
{
    public string Brand { get; init; } = "";
    public string Model { get; init; } = "";
    public DeviceKind Kind { get; init; } = DeviceKind.Unknown;
    public bool HasCase { get; init; }
    public bool Anc { get; init; }
    public bool SmartCase { get; init; }
    public BudsSource Buds { get; init; }
    public Color? BrandColor { get; init; }

    public static readonly DeviceProfile Generic = new();
}

/// <summary>Battery data broadcast by earbuds (AirPods proximity pairing or Google Fast Pair).</summary>
public readonly record struct BudsAdvert(BudsSource Vendor, Part Left, Part Right, Part Case, string? Model)
{
    public ulong Address { get; init; }
    public short Rssi { get; init; }
    public DateTime AtUtc { get; init; }
}

/// <summary>Live state of one accessory. Mutated only on the UI thread.</summary>
public sealed class Accessory
{
    public Accessory(string key) => Key = key;

    public string Key { get; }
    public string Name { get; set; } = "";
    public string? CustomName { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? (Name.Length > 0 ? Name : "Устройство") : CustomName!;

    // Identity
    public string? ClassicAepId { get; set; }
    public string? LeAepId { get; set; }
    public ulong ClassicAddress { get; set; }
    public ulong LeAddress { get; set; }
    public string? GamepadId { get; set; }
    /// <summary>Connected through a 2.4 GHz USB receiver (not Bluetooth).</summary>
    public bool ViaReceiver { get; set; }
    public Dictionary<string, bool> Endpoints { get; } = new(StringComparer.Ordinal);

    // Classification
    public DeviceKind KindHint { get; set; }
    public DeviceProfile Profile { get; set; } = DeviceProfile.Generic;
    public DeviceKind Kind => Profile.Kind;

    // Connection
    public bool Connected { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime LastSeen { get; set; }

    // Battery from the system (HFP via Windows, GATT Battery Service, gamepad API)
    public int? SystemLevel { get; set; }
    public bool SystemCharging { get; set; }
    public int SystemRank { get; set; }   // 0 none, 1 Windows PnP, 2 GATT/Gamepad (more precise)
    public string? SystemSource { get; set; }

    // Compartments (earbuds L/R/case, split keyboards)
    public Part Left { get; set; }
    public Part Right { get; set; }
    public Part Case { get; set; }
    public DateTime PartsAtUtc { get; set; }
    public string? PartsSource { get; set; }
    public string? BudsModel { get; set; }
    public bool BudsPending { get; set; }

    // Last known level while disconnected (persisted)
    public int? LastLevel { get; set; }

    // Bookkeeping
    public bool SessionStartPending { get; set; }
    public int Version { get; set; }

    public bool HasParts => Left.Known || Right.Known || Case.Known;
    public bool PartsStale => HasParts && (PartsSource is "Apple" or "Fast Pair") && (DateTime.UtcNow - PartsAtUtc).TotalMinutes > 5;

    /// <summary>Overall level: system value, else the weaker earbud.</summary>
    public int? Level
    {
        get
        {
            if (SystemLevel.HasValue) return SystemLevel;
            if (Left.Known && Right.Known) return Math.Min(Left.Level!.Value, Right.Level!.Value);
            return Left.Level ?? Right.Level;
        }
    }

    /// <summary>The lowest of overall and individual buds – what matters for "will it die soon".</summary>
    public int? WeakestLevel
    {
        get
        {
            int? w = Level;
            foreach (var p in new[] { Left, Right })
                if (p.Level is int l && (w is null || l < w)) w = l;
            return w;
        }
    }

    public bool Charging
    {
        get
        {
            if (SystemCharging) return true;
            if (Left.Known && Right.Known) return Left.Charging && Right.Charging;
            if (Left.Known) return Left.Charging;
            if (Right.Known) return Right.Charging;
            return false;
        }
    }

    public bool IsSplitDevice => Kind == DeviceKind.Keyboard;
}
