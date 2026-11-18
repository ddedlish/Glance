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
    public sealed record Release(Version Version, string Tag, string Notes, string AssetUrl, string AssetName, string SumsUrl, long Size, string? BlocksUrl = null, string? GzUrl = null)
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
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Glance/{Fmt.Ver(AppInfo.Version)}");
        return h;
    }

    public static string AssetName => "Glance-" + AppInfo.Rid + (OperatingSystem.IsWindows() ? ".exe" : "");

    public async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        if (!AppInfo.UpdatesConfigured) return null;
        // First repository that answers wins (the public releases repo; the code repo may be private → 404).
        byte[]? body = null;
        foreach (var repo in AppInfo.UpdateRepos)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // Conditional request: an unchanged release answers 304, which GitHub does not count against the limit.
            if (ETags.TryGetValue(repo, out var cached)) req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            using var r = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            if (r.StatusCode == System.Net.HttpStatusCode.NotModified && cached.Body is not null) { body = cached.Body; break; }
            if (r.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            {
                // API limit (60 requests/hour per IP, shared with everything on this network): read the
                // public release page redirect instead — no API, no limit.
                Log.Info($"GitHub API limit ({(int)r.StatusCode}) — using the release page");
                var viaPage = await CheckViaReleasePageAsync(repo, ct).ConfigureAwait(false);
                if (viaPage is not null || AppInfo.UpdateRepos[^1] == repo) return viaPage;
                continue;
            }
            r.EnsureSuccessStatusCode();
            body = await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (r.Headers.ETag is { } etag) ETags[repo] = (etag.ToString(), body);
            break;
        }
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        if (Normalize(v) <= Normalize(AppInfo.Version)) return null;

        string? asset = null, sums = null, blocks = null, gz = null;
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
            else if (string.Equals(name, AssetName + ".blocks", StringComparison.OrdinalIgnoreCase)) blocks = url;
            else if (string.Equals(name, AssetName + ".gz", StringComparison.OrdinalIgnoreCase)) gz = url;
        }
        if (asset is null || sums is null || size > MaxAssetBytes) return null;
        string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        return new Release(v, tag, notes, asset, AssetName, sums, size, blocks, gz);
    }

    static readonly Dictionary<string, (string ETag, byte[]? Body)> ETags = new();
    static readonly HttpClient NoRedirect = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Fallback without the API: github.com/{repo}/releases/latest redirects to …/tag/vX.Y.Z; asset URLs follow
    /// the fixed pattern. The file is still verified against SHA256SUMS before anything is replaced.
    /// </summary>
    async Task<Release?> CheckViaReleasePageAsync(string repo, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd($"Glance/{Fmt.Ver(AppInfo.Version)}");
            using var r = await NoRedirect.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var loc = r.Headers.Location?.ToString() ?? "";
            int i = loc.LastIndexOf("/tag/", StringComparison.Ordinal);
            if (i < 0) return null;
            string tag = Uri.UnescapeDataString(loc[(i + 5)..]);
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || Normalize(v) <= Normalize(AppInfo.Version)) return null;
            string bas = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/";
            return new Release(v, tag, "", bas + AssetName, AssetName, bas + "SHA256SUMS", 0, bas + AssetName + ".blocks", bas + AssetName + ".gz");
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info("Release page check: " + e.Message); return null; }
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

        bool delta = false;
        if (r.BlocksUrl is not null)
        {
            try { delta = await DeltaDownloadAsync(r, exe, tmp, expected, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"Delta update failed, full download: {e.Message}"); }
        }

        // Full download, compressed: the .gz is ~2.5× smaller than the program; unpacked on the fly and then
        // verified against SHA256SUMS like any other download.
        if (!delta && r.GzUrl is not null)
        {
            try { delta = await GzDownloadAsync(r.GzUrl, tmp, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"Compressed download failed, plain download: {e.Message}"); TryDelete(tmp); }
        }

        // A shortcut download (delta or .gz) that doesn't match the published hash falls back to the plain file.
        if (delta)
        {
            string quick;
            await using (var qs = File.OpenRead(tmp))
                quick = Convert.ToHexString(await SHA256.HashDataAsync(qs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (quick != expected) { Log.Info("Shortcut download did not match SHA256SUMS — downloading the full file"); TryDelete(tmp); delta = false; }
        }

        try
        {
            if (!delta)
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
                LastDownloadedBytes = total;
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

    static async Task<bool> GzDownloadAsync(string url, string tmp, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        long packed = resp.Content.Headers.ContentLength ?? 0;
        if (packed > MaxAssetBytes) return false;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress);
        await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        var buf = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = await gz.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxAssetBytes) throw new InvalidDataException("Слишком большой файл обновления");
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        LastDownloadedBytes = packed;
        Log.Info($"Compressed update: downloaded {Fmt.Size(packed)} for {Fmt.Size(total)}");
        return true;
    }

    /// <summary>Bytes actually downloaded by the last update (for the "обновлено" message / log).</summary>
    public static long LastDownloadedBytes { get; private set; }

    /// <summary>
    /// Delta update (zsync-style): the release publishes rolling + SHA-256 checksums of every 64 KiB block of the
    /// new file. Blocks already present anywhere in the running executable are copied locally (the bundled .NET
    /// runtime barely changes between versions); only missing ranges are fetched with parallel HTTP Range
    /// requests. The result must match the published SHA-256, otherwise the caller falls back to a full download.
    /// </summary>
    static async Task<bool> DeltaDownloadAsync(Release r, string exe, string tmp, string expected, CancellationToken ct)
    {
        byte[] map = await Http.GetByteArrayAsync(r.BlocksUrl!, ct).ConfigureAwait(false);
        if (map.Length < 17 || map.Length > 1 << 20 || System.Text.Encoding.ASCII.GetString(map, 0, 5) != "GLBM1") return false;
        int bs = BitConverter.ToInt32(map, 5);
        long newLen = BitConverter.ToInt64(map, 9);
        if (bs < 4096 || bs > 1 << 22 || newLen <= 0 || newLen > MaxAssetBytes) return false;
        int n = (int)((newLen + bs - 1) / bs);
        if (map.Length != 17 + n * 12) return false;
        var weak = new uint[n];
        var strong = new ulong[n];
        for (int i = 0; i < n; i++) { weak[i] = BitConverter.ToUInt32(map, 17 + i * 12); strong[i] = BitConverter.ToUInt64(map, 21 + i * 12); }

        byte[] local = await File.ReadAllBytesAsync(exe, ct).ConfigureAwait(false);
        var src = new long[n];
        Array.Fill(src, -1);
        int full = newLen % bs == 0 ? n : n - 1; // the short last block is just downloaded
        var index = new Dictionary<uint, List<int>>();
        var filter = new bool[65536];
        for (int i = 0; i < full; i++)
        {
            if (!index.TryGetValue(weak[i], out var l)) index[weak[i]] = l = new List<int>(1);
            l.Add(i);
            filter[weak[i] & 0xFFFF] = true;
        }

        await Task.Run(() =>
        {
            int L = bs;
            if (local.Length < L) return;
            int a = 0, b = 0;
            void Init(int k) { a = 0; b = 0; for (int i = 0; i < L; i++) { a += local[k + i]; b += (L - i) * local[k + i]; } a &= 0xFFFF; b &= 0xFFFF; }
            int k = 0;
            Init(0);
            Span<byte> hash = stackalloc byte[32];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool matched = false;
                if (filter[a])
                {
                    uint s = (uint)a | ((uint)b << 16);
                    if (index.TryGetValue(s, out var cands))
                    {
                        SHA256.HashData(local.AsSpan(k, L), hash);
                        ulong st = BitConverter.ToUInt64(hash);
                        foreach (var j in cands)
                            if (strong[j] == st && src[j] < 0) { src[j] = k; matched = true; }
                    }
                }
                if (matched)
                {
                    k += L;
                    if (k + L > local.Length) break;
                    Init(k);
                    continue;
                }
                if (k + L >= local.Length) break;
                int xo = local[k], xi = local[k + L];
                a = (a - xo + xi) & 0xFFFF;
                b = (b - L * xo + a) & 0xFFFF;
                k++;
            }
        }, ct).ConfigureAwait(false);

        // Missing blocks → merged ranges of at most 2 MiB, fetched 4 at a time.
        var ranges = new List<(long Start, long End)>();
        for (int i = 0; i < n; i++)
        {
            if (src[i] >= 0) continue;
            long s0 = (long)i * bs, e0 = Math.Min(newLen, s0 + bs) - 1;
            if (ranges.Count > 0 && ranges[^1].End + 1 == s0 && e0 - ranges[^1].Start < 2 << 20) ranges[^1] = (ranges[^1].Start, e0);
            else ranges.Add((s0, e0));
        }
        long missing = ranges.Sum(x => x.End - x.Start + 1);
        if (missing > newLen * (r.GzUrl is not null ? 0.4 : 0.9)) return false; // big change → the compressed full download is smaller

        // Resolve the redirect once (signed CDN URL), then range-request it directly.
        string url = r.AssetUrl;
        using (var probe = new HttpRequestMessage(HttpMethod.Get, r.AssetUrl))
        {
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var pr = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (pr.StatusCode != System.Net.HttpStatusCode.PartialContent) return false;
            var final = pr.RequestMessage?.RequestUri?.ToString();
            if (final is not null && IsTrustedUrl(final)) url = final;
        }

        var parts = new byte[ranges.Count][];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(ranges.Select(async (rg, idx) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(rg.Start, rg.End);
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new InvalidDataException("Range not supported");
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length != rg.End - rg.Start + 1) throw new InvalidDataException("Short range");
                parts[idx] = bytes;
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        // Assemble.
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            int ri = 0;
            for (int i = 0; i < n; i++)
            {
                long s0 = (long)i * bs;
                int len = (int)(Math.Min(newLen, s0 + bs) - s0);
                if (src[i] >= 0) { await fs.WriteAsync(local.AsMemory((int)src[i], len), ct).ConfigureAwait(false); continue; }
                while (ri < ranges.Count && ranges[ri].End < s0) ri++;
                var rg = ranges[ri];
                await fs.WriteAsync(parts[ri].AsMemory((int)(s0 - rg.Start), len), ct).ConfigureAwait(false);
            }
        }

        string actual;
        await using (var check = File.OpenRead(tmp))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected) { TryDelete(tmp); return false; }
        LastDownloadedBytes = missing + map.Length;
        Log.Info($"Delta update: downloaded {Fmt.Size(LastDownloadedBytes)} of {Fmt.Size(newLen)}");
        return true;
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
