using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Glance.Droid;

internal sealed record UpdateInfo(Version Version, string Url, string Page);

/// <summary>
/// Android updates come from GitHub releases tagged «android-vX.Y.Z» (earlier ones were pre-releases).
/// The PC app ignores them: it reads only the release marked «latest», which is always the PC one.
/// </summary>
internal static class Updates
{
    static readonly Assembly Me = typeof(Updates).Assembly;

    public static Version Current { get; } = Me.GetName().Version ?? new Version(0, 0, 0);

    public static string Channel { get; } = Meta("GlanceChannel") ?? "";

    public static string VersionText => Channel.Length > 0 ? $"Glance {Fmt.Ver(Current)} · {Channel}" : $"Glance {Fmt.Ver(Current)}";

    static IReadOnlyList<string> Repos => (Meta("GlanceUpdateRepo") ?? "")
        .Split(new[] { ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z0-9][A-Za-z0-9\-]{0,38}/[A-Za-z0-9._\-]{1,100}$"))
        .ToList();

    static string? Meta(string key) => Me.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>Newest android-v* release newer than this build, or null (no network, nothing new, not configured).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Glance-Android/{Fmt.Ver(Current)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        foreach (var repo in Repos)
        {
            try
            {
                using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=30", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                UpdateInfo? best = null;
                bool any = false;
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) continue;
                    string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                    if (!tag.StartsWith("android-v", StringComparison.OrdinalIgnoreCase)) continue;
                    any = true;
                    if (!Version.TryParse(tag["android-v".Length..], out var v)) continue;
                    string? apk = null;
                    if (rel.TryGetProperty("assets", out var assets))
                        foreach (var a in assets.EnumerateArray())
                        {
                            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                                apk = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        }
                    string page = rel.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
                    if (apk is null || !apk.StartsWith("https://", StringComparison.Ordinal)) continue;
                    if (best is null || v > best.Version) best = new UpdateInfo(v, apk, page);
                }
                if (!any) continue; // this repo has no Android builds: try the next one
                return best is not null && Norm(best.Version) > Norm(Current) ? best : null;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Info($"Android update check {repo}: {e.Message}");
            }
        }
        return null;
    }

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
