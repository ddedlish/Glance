using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glance;

/// <summary>
/// Self-update from GitHub Releases (HTTPS only).
/// Release assets: Glance-win-x64.exe, Glance-win-x86.exe, Glance-linux-x64, Glance-linux-arm64 and SHA256SUMS.
/// The download is verified against SHA256SUMS before it replaces anything. The running executable
/// is renamed to *.old (allowed on Windows and Linux), the verified file takes its place, the new
/// version starts with --after-update and the caller exits.
/// </summary>
internal sealed class UpdateService
{
    public sealed record Release(Version Version, string Tag, string Notes, string AssetUrl, string AssetName, string SumsUrl, long Size)
    {
        public string VersionText => Fmt.Ver(Version);
    }

    const long MaxAssetBytes = 400L * 1024 * 1024;
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Glance/{AppInfo.VersionText}");
        return h;
    }

    public static string AssetName => "Glance-" + AppInfo.Rid + (OperatingSystem.IsWindows() ? ".exe" : "");

    public async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        if (!AppInfo.UpdatesConfigured) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.UpdateRepo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        await using var st = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(st, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        if (Normalize(v) <= Normalize(AppInfo.Version)) return null;

        string? asset = null, sums = null;
        long size = 0;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null || !IsTrustedUrl(url)) continue;
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                asset = url;
                size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            }
            else if (string.Equals(name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase)) sums = url;
        }
        if (asset is null || sums is null || size > MaxAssetBytes) return null;
        string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        return new Release(v, tag, notes, asset, AssetName, sums, size);
    }

    static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>Downloads, verifies and swaps the executable, then launches the new version. Caller must exit afterwards.</summary>
    public async Task InstallAsync(Release r, CancellationToken ct = default)
    {
        string exe = AppInfo.ExePath;
        string dir = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("Не найден путь программы");
        string tmp = Path.Combine(dir, ".glance-update.download");

        string expected = await ExpectedHashAsync(r, ct).ConfigureAwait(false);

        try
        {
            using (var resp = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is long len && len > MaxAssetBytes) throw new InvalidDataException("Слишком большой файл обновления");
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buf = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxAssetBytes) throw new InvalidDataException("Слишком большой файл обновления");
                    await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            string actual;
            await using (var fs = File.OpenRead(tmp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
                throw new InvalidDataException("Контрольная сумма не совпала — файл повреждён или подменён");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        string old = exe + ".old";
        TryDelete(old);
        File.Move(exe, old);
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add("--after-update");
        Process.Start(psi);
    }

    static async Task<string> ExpectedHashAsync(Release r, CancellationToken ct)
    {
        string text = await Http.GetStringAsync(r.SumsUrl, ct).ConfigureAwait(false);
        if (text.Length > 64 * 1024) throw new InvalidDataException("Некорректный SHA256SUMS");
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            var name = parts[1].Trim().TrimStart('*');
            if (!string.Equals(name, r.AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = parts[0].ToLowerInvariant();
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return hash;
        }
        throw new InvalidDataException("В SHA256SUMS нет записи для " + r.AssetName);
    }

    public static void CleanupAfterUpdate()
    {
        string dir = Path.GetDirectoryName(AppInfo.ExePath) ?? ".";
        for (int i = 0; i < 6; i++)
        {
            bool ok = TryDelete(AppInfo.ExePath + ".old");
            TryDelete(Path.Combine(dir, ".glance-update.download"));
            if (ok) return;
            Thread.Sleep(300);
        }
    }

    static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }
}
