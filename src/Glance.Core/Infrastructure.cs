using System.Runtime.InteropServices;

namespace Glance;

/// <summary>Static application facts (cross-platform).</summary>
internal static class AppInfo
{
    public const string Name = "Glance";

    // ── Author ──
    public const string Author = "ddedlish";
    public const string AuthorUrl = "https://steamcommunity.com/id/ddedlish/";

    /// <summary>
    /// GitHub repository "owner/name" that publishes releases. Injected at build time by GitHub Actions
    /// (-p:GlanceUpdateRepo=${{ github.repository }}), so forks update from their own releases and
    /// local builds simply have updates switched off. Nothing to edit by hand.
    /// </summary>
    public static string UpdateRepo { get; } = ReadUpdateRepo();

    public static bool UpdatesConfigured => UpdateRepo.Length > 0;

    public static string? RepoUrl => UpdatesConfigured ? $"https://github.com/{UpdateRepo}" : null;

    static string ReadUpdateRepo()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            var value = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "GlanceUpdateRepo")?.Value ?? "";
            // owner/name with GitHub-safe characters only
            return System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9\-]{0,38}/[A-Za-z0-9._\-]{1,100}$") ? value : "";
        }
        catch { return ""; }
    }

    /// <summary>Opens an https link in the default browser (constant, trusted URLs only).</summary>
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(url);
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception e) { Log.Info($"Open URL: {e.Message}"); }
    }

    // Version of the app (entry assembly), not of this shared library.
    public static Version Version { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Release channel from AssemblyMetadata «GlanceChannel» (e.g. «Beta»), empty for stable builds.</summary>
    public static string Channel { get; } = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "GlanceChannel")?.Value ?? "";

    public static string VersionText => Channel.Length > 0 ? $"{Fmt.Ver(Version)} {Channel}" : Fmt.Ver(Version);

    public static string DataDir { get; } = CreateDataDir();

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Glance.exe" : "glance");

    /// <summary>Runtime identifier of this build: win-x64, win-x86, win-arm64, linux-x64, linux-arm64.</summary>
    public static string Rid
    {
        get
        {
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            return (OperatingSystem.IsWindows() ? "win-" : "linux-") + arch;
        }
    }

    static string CreateDataDir()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dir = Path.Combine(root, OperatingSystem.IsWindows() ? Name : "glance");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// Host-provided "run on the main thread" hook. Windows wires it to the WinForms context,
/// Linux to its own main loop. All model mutations happen on that thread.
/// </summary>
internal static class Dispatch
{
    public static Action<Action> Post { get; set; } = a => ThreadPool.QueueUserWorkItem(_ => a());
}

/// <summary>Coalesces bursts of calls into one, executed on the main thread after a quiet period.</summary>
internal sealed class Debouncer : IDisposable
{
    readonly Action _action;
    readonly int _delayMs;
    readonly System.Threading.Timer _timer;
    int _pending;

    public Debouncer(int delayMs, Action action)
    {
        _delayMs = delayMs;
        _action = action;
        _timer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _pending, 0) == 1) Dispatch.Post(_action);
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Trigger()
    {
        Interlocked.Exchange(ref _pending, 1);
        try { _timer.Change(_delayMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    /// <summary>Throttle variant: schedules once, does not postpone an already pending run.</summary>
    public void TriggerOnce()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            try { _timer.Change(_delayMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    public void Cancel()
    {
        Interlocked.Exchange(ref _pending, 0);
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Tiny rolling log in the data folder. Never throws.</summary>
internal static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppInfo.DataDir, "glance.log");

    public static void Info(string message) => Write("INF", message);
    public static void Error(string message, Exception? e = null) => Write("ERR", e is null ? message : $"{message}: {e}");
    public static void Error(Exception e) => Write("ERR", e.ToString());

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    File.Delete(FilePath + ".1");
                    File.Move(FilePath, FilePath + ".1");
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}

/// <summary>Russian formatting helpers.</summary>
internal static class Fmt
{
    public static string Percent(int? level) => level is int l ? $"{l}%" : "—";

    public static string Ver(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    public static string Ago(DateTime when)
    {
        if (when == default) return "давно";
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return "только что";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин назад";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} ч назад";
        if (d.TotalDays < 2) return "вчера";
        return $"{(int)d.TotalDays} дн назад";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "меньше минуты";
        int h = (int)t.TotalHours, m = t.Minutes;
        if (h == 0) return $"{m} мин";
        return m == 0 ? $"{h} ч" : $"{h} ч {m} мин";
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} ТБ",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} МБ",
        _ => $"{bytes / 1024} КБ",
    };
}
