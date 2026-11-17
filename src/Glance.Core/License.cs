using System.Security.Cryptography;
using System.Text;

namespace Glance;

/// <summary>
/// Glance Pro licences. A key is the owner's name and issue date signed with the author's private ECDSA P-256
/// key (kept offline, never in the program or the repository); Glance only carries the public key, so a key
/// cannot be forged or edited — changing one letter breaks the signature. Works fully offline.
/// Format: <c>GLANCE-&lt;base64url payload&gt;.&lt;base64url signature&gt;</c>, payload <c>GL1|name|yyyyMMdd</c>.
/// </summary>
public static class License
{
    const string Prefix = "GLANCE-";
    const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE77cOHic3cfhEDLaTMyzCdOm/HiLFwZg/NzFiij/nKua3v1+L1DSvbRCSHpQDTD0tjirKx80f8EUp9847QMnJ5w==";

    public sealed record Info(string Owner, DateTime Issued);

    /// <summary>Validates a key against the built-in public key; null when invalid.</summary>
    public static Info? Verify(string? key) => Verify(key, Convert.FromBase64String(PublicKey));

    internal static Info? Verify(string? key, byte[] publicKeyDer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var parts = key[Prefix.Length..].Split('.');
            if (parts.Length != 2) return null;
            byte[] payload = FromB64Url(parts[0]), sig = FromB64Url(parts[1]);
            if (payload.Length is < 6 or > 300 || sig.Length != 64) return null;
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
            if (!ec.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            var f = Encoding.UTF8.GetString(payload).Split('|');
            if (f.Length != 3 || f[0] != "GL1") return null;
            if (!DateTime.TryParseExact(f[2], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return null;
            return new Info(f[1], d);
        }
        catch { return null; }
    }

    /// <summary>Creates a key (author only: needs the private key in PKCS#8 PEM).</summary>
    public static string Issue(string owner, string privateKeyPem, DateTime? issued = null)
    {
        owner = owner.Replace("|", "/").Trim();
        if (owner.Length is 0 or > 80) throw new ArgumentException("Имя покупателя: от 1 до 80 символов");
        var payload = Encoding.UTF8.GetBytes($"GL1|{owner}|{(issued ?? DateTime.Now):yyyyMMdd}");
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        var sig = ec.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Prefix + ToB64Url(payload) + "." + ToB64Url(sig);
    }

    static string ToB64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>Current Pro state of this installation.</summary>
public static class Pro
{
    public static License.Info? Info { get; private set; }
    public static bool IsActive => Info is not null;
    public static event Action? Changed;

    /// <summary>Applies a key (from settings or typed by the user). Returns true when it is valid.</summary>
    public static bool Apply(string? key)
    {
        var info = License.Verify(key);
        bool changed = (info is null) != (Info is null) || info?.Owner != Info?.Owner;
        Info = info;
        if (changed) Changed?.Invoke();
        return info is not null;
    }
}
