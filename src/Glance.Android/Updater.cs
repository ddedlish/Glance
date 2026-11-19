using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Glance.Droid;

internal enum UpdateStage { Idle, Downloading, Ready, NeedPermission, Installing, Failed }

/// <summary>
/// In-app updates: downloads the APK of a newer android-v* release in the background — in several parallel ranged
/// connections that resume on their own after a stall or a dropped connection (progress and speed in the banner and in
/// a notification), checks it (size, package name, a newer versionCode) and installs it over this version through
/// PackageInstaller. With «Автообновление» on, a found version is downloaded and installed by itself: on Android 12+
/// silently when Glance installed the current version, otherwise Android asks once. When installing from Glance is
/// not allowed yet, opens «Установка неизвестных приложений» and continues as soon as the user comes back.
/// </summary>
internal static class Updater
{
    const string Channel = "glance-update";
    const int NotifyId = 3;
    public const string ActionStatus = "io.github.ddedlish.glance.INSTALL_STATUS";

    static readonly Handler Main = new(Looper.MainLooper!);
    static Context App => Application.Context;
    static CancellationTokenSource? _cts;
    static long _lastNotify;

    public static UpdateStage Stage { get; private set; }
    public static UpdateInfo? Target { get; private set; }
    static long _done;
    public static long Done => Interlocked.Read(ref _done);
    /// <summary>Smoothed download speed, bytes per second (0 until measured).</summary>
    public static double Speed { get; private set; }
    static long _speedT, _speedDone;
    static bool _auto; // install by itself as soon as the download is checked
    public static long Total { get; private set; }
    public static string Error { get; private set; } = "";
    static string? _file;

    /// <summary>Raised on the main thread whenever the stage or the progress changes.</summary>
    public static event Action? Changed;

    public static float Progress => Total > 0 ? Math.Clamp(Done / (float)Total, 0, 1) : 0;

    /// <summary>0…1 while installing: copying into the installer session, then Android's own work.</summary>
    public static float InstallProgress { get; private set; }
    public static bool Busy => Stage is UpdateStage.Downloading or UpdateStage.Installing;

    static void Set(UpdateStage s, string? error = null)
    {
        Stage = s;
        if (error is not null) Error = error;
        Android.Util.Log.Info("Glance", $"update: {s}{(error is null ? "" : " — " + error)}");
        Main.Post(() => Changed?.Invoke());
    }

    /// <summary>Self-test: a frozen «downloading» banner without network.</summary>
    public static void Demo(UpdateInfo u, float progress, UpdateStage stage = UpdateStage.Downloading)
    {
        Target = u;
        Total = u.Size > 0 ? u.Size : 12_176_034;
        _done = (long)(Total * progress);
        Speed = 2_400_000;
        InstallProgress = progress;
        Set(stage);
    }

    /// <summary>One tap on the banner or the notification: download → check → install, continuing from where it is.</summary>
    public static void Go(Activity a, UpdateInfo u)
    {
        if (Target is not null && Target.Version != u.Version) Reset();
        Target = u;
        switch (Stage)
        {
            case UpdateStage.Downloading:
            case UpdateStage.Installing:
                return;
            case UpdateStage.Ready:
            case UpdateStage.NeedPermission:
                Install(a);
                return;
            default:
                Start(u, installAfter: a, auto: false);
                return;
        }
    }

    /// <summary>
    /// «Автообновление»: a newer version was found — download it right away in the background (on any network: the APK
    /// is ~12 MB) and install it by itself as soon as it is checked, without waiting for a tap.
    /// </summary>
    public static void Auto(UpdateInfo u, Activity? a)
    {
        if (Target is not null && Target.Version == u.Version)
        {
            if (Stage == UpdateStage.Ready && _file is not null) { _auto = true; InstallAuto(a); }
            if (Stage != UpdateStage.Idle && Stage != UpdateStage.Failed) return;
        }
        else if (Stage != UpdateStage.Idle && Stage != UpdateStage.Failed) return;
        Android.Util.Log.Info("Glance", $"update: auto {Fmt.Ver(u.Version)}");
        Target = u;
        Start(u, a, auto: true);
    }

    /// <summary>Installs the checked APK with no tap: in the app when it is on screen, otherwise from the background.</summary>
    static void InstallAuto(Activity? a)
    {
        if (a is not null && MainActivity.Visible && !a.IsFinishing) { Notify(null); Install(a, user: false); }
        else Install(null, user: false);
    }

    static void Reset()
    {
        _cts?.Cancel();
        _cts = null;
        _file = null;
        _done = Total = 0;
        _auto = false;
        Stage = UpdateStage.Idle;
    }

    static void Start(UpdateInfo u, Activity? installAfter, bool auto)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _done = 0;
        Speed = 0;
        _speedT = 0;
        _auto = auto;
        Total = u.Size;
        Set(UpdateStage.Downloading, "");
        var weak = installAfter is null ? null : new WeakReference<Activity>(installAfter);
        _ = Task.Run(async () =>
        {
            try
            {
                string path = await DownloadAsync(u, cts.Token).ConfigureAwait(false);
                Verify(path, u);
                _file = path;
                Set(UpdateStage.Ready);
                Main.Post(() =>
                {
                    Activity? act = null;
                    weak?.TryGetTarget(out act);
                    // «Автообновление»: straight on to installing, on screen or not
                    if (_auto) InstallAuto(act);
                    // the user asked to install: go on at once while Glance is on screen, otherwise wait in the shade
                    else if (act is not null && MainActivity.Visible && !act.IsFinishing) { Notify(null); Install(act); }
                    else Notify(u);
                });
            }
            catch (System.OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Error("Android update download", e);
                Set(UpdateStage.Failed, e is HttpRequestException or IOException or TaskCanceledException or Java.IO.IOException ? "Нет связи — нажмите, чтобы повторить" : e.Message);
                Main.Post(() => Notify(null));
            }
        });
    }

    const int MaxParts = 6;
    const long MinPart = 1_000_000;
    const int StallMs = 15_000;

    static async Task<string> DownloadAsync(UpdateInfo u, CancellationToken ct)
    {
        var dir = new DirectoryInfo(Path.Combine(App.CacheDir!.AbsolutePath, "updates"));
        dir.Create();
        foreach (var old in dir.GetFiles()) try { old.Delete(); } catch { }
        string path = Path.Combine(dir.FullName, $"Glance-Android-{Fmt.Ver(u.Version)}.apk");
        string part = path + ".part";
        long t0 = SystemClock.ElapsedRealtime();

        using (var http = NewHttp(redirects: false))
        {
            Uri url;
            long total;
            HttpResponseMessage? whole;
            try { (url, total, whole) = await ProbeAsync(http, new Uri(u.Url), ct).ConfigureAwait(false); }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not InvalidDataException)
            {
                // the ranged path did not work here: the plain single download as before
                Android.Util.Log.Info("Glance", $"update: probe failed ({e.Message}), single stream");
                using var plain = NewHttp(redirects: true);
                using var resp = await plain.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await SingleAsync(resp, part, u.Size > 0 ? u.Size : resp.Content.Headers.ContentLength ?? 0, ct).ConfigureAwait(false);
                return Finish(part, path, u.Size, t0, 1);
            }
            if (u.Size > 0 && total > 0 && total != u.Size) { whole?.Dispose(); throw new InvalidDataException($"Размер файла {total} вместо {u.Size} — повторите"); }
            long expected = total > 0 ? total : u.Size;
            Total = expected;
            if (whole is not null || expected < 2 * MinPart)
            {
                // the server does not give pieces (or the file is tiny): one stream
                using var resp = whole ?? await GetRangeAsync(http, url, 0, expected - 1, ct).ConfigureAwait(false);
                await SingleAsync(resp, part, expected, ct).ConfigureAwait(false);
                return Finish(part, path, expected, t0, 1);
            }

            // several connections at once, each for its own piece of the file: much faster where one connection is slow
            int parts = (int)Math.Clamp(expected / MinPart, 2, MaxParts);
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(expected);
            using var all = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var shared = new UrlBox(url, new Uri(u.Url));
            long chunk = expected / parts;
            var tasks = new Task[parts];
            for (int k = 0; k < parts; k++)
            {
                long from = k * chunk, to = k == parts - 1 ? expected - 1 : (k + 1) * chunk - 1;
                tasks[k] = Task.Run(async () =>
                {
                    try { await PieceAsync(http, shared, part, from, to, all.Token).ConfigureAwait(false); }
                    catch { all.Cancel(); throw; } // one piece gave up: stop the others too
                });
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch
            {
                ct.ThrowIfCancellationRequested();
                throw tasks.Select(t => t.Exception?.InnerException).FirstOrDefault(e => e is not null and not System.OperationCanceledException)
                    ?? new IOException("Соединение оборвалось");
            }
            return Finish(part, path, expected, t0, parts);
        }
    }

    /// <summary>The download link; GitHub's signed CDN link expires after a few minutes, so a retry asks for a fresh one.</summary>
    sealed class UrlBox(Uri url, Uri origin)
    {
        public volatile Uri Url = url;
        public readonly Uri Origin = origin;
    }

    static HttpClient NewHttp(bool redirects)
    {
        var http = redirects ? new HttpClient() : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        http.Timeout = Timeout.InfiniteTimeSpan; // stalls are caught per read instead
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Glance-Android/{Fmt.Ver(Updates.Current)}");
        return http;
    }

    /// <summary>
    /// Follows the redirects to the real file with a one-byte ranged request: gives the final link and the size, or —
    /// when the server ignores ranges — the full response to read as one stream.
    /// </summary>
    static async Task<(Uri url, long total, HttpResponseMessage? whole)> ProbeAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallMs);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                resp.Dispose();
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is long len && len > 0)
            {
                resp.Dispose();
                return (url, len, null);
            }
            if (!resp.IsSuccessStatusCode) { resp.Dispose(); throw new HttpRequestException($"HTTP {code}"); }
            return (url, resp.Content.Headers.ContentLength ?? 0, resp);
        }
        throw new HttpRequestException("Слишком много перенаправлений");
    }

    static async Task<HttpResponseMessage> GetRangeAsync(HttpClient http, Uri url, long from, long to, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(from, to);
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange?.From != from)
        {
            int code = (int)resp.StatusCode;
            resp.Dispose();
            throw new HttpRequestException($"HTTP {code} для части файла");
        }
        return resp;
    }

    /// <summary>One piece of the file; after a stall (no data for 15 s) or a dropped connection it carries on from where it stopped.</summary>
    static async Task PieceAsync(HttpClient http, UrlBox link, string file, long from, long to, CancellationToken ct)
    {
        long pos = from;
        int fails = 0;
        var buf = new byte[64 * 1024];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        while (pos <= to)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            HttpResponseMessage? resp = null;
            try
            {
                stall.CancelAfter(StallMs);
                resp = await GetRangeAsync(http, link.Url, pos, to, stall.Token).ConfigureAwait(false);
                // a blocked read of the native stream may ignore the token: closing the response wakes it
                var r = resp;
                using var wake = stall.Token.Register(() => { try { r.Dispose(); } catch { } });
                using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                fs.Position = pos;
                int n;
                while (pos <= to && (n = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, to - pos + 1)), stall.Token).ConfigureAwait(false)) > 0)
                {
                    fs.Write(buf, 0, n);
                    pos += n;
                    Interlocked.Add(ref _done, n);
                    fails = 0;
                    stall.CancelAfter(StallMs);
                    Tick();
                }
                if (pos <= to) throw new IOException("Соединение оборвалось");
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not InvalidDataException && ++fails <= 6)
            {
                Android.Util.Log.Info("Glance", $"update: piece {from} retry {fails} at {pos - from}/{to - from + 1}: {e.GetType().Name} {e.Message}");
                await Task.Delay(300 * fails, ct).ConfigureAwait(false);
                // the signed link may have expired: ask for a fresh one (keep the old one if that fails too)
                try
                {
                    var (fresh, _, whole) = await ProbeAsync(http, link.Origin, ct).ConfigureAwait(false);
                    whole?.Dispose();
                    if (whole is null) link.Url = fresh;
                }
                catch (Exception pe) when (!ct.IsCancellationRequested) { Android.Util.Log.Info("Glance", "update: relink " + pe.Message); }
            }
            finally { resp?.Dispose(); }
        }
        fs.Flush();
    }

    static async Task SingleAsync(HttpResponseMessage resp, string part, long expected, CancellationToken ct)
    {
        Total = expected;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallMs * 2);
        using var wake = stall.Token.Register(() => { try { resp.Dispose(); } catch { } });
        await using var src = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using var dst = File.Create(part);
        var buf = new byte[128 * 1024];
        int n;
        while ((n = await src.ReadAsync(buf, stall.Token).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), stall.Token).ConfigureAwait(false);
            Interlocked.Add(ref _done, n);
            if (expected > 0 && Done > expected + 1024) throw new InvalidDataException("Файл больше ожидаемого");
            stall.CancelAfter(StallMs * 2);
            Tick();
        }
    }

    static string Finish(string part, string path, long expected, long t0, int parts)
    {
        long got = new FileInfo(part).Length;
        if (expected > 0 && got != expected) throw new InvalidDataException($"Скачано {got} из {expected} байт — повторите");
        if (got < 1_000_000) throw new InvalidDataException("Файл обновления слишком маленький");
        File.Move(part, path, overwrite: true);
        double sec = Math.Max(0.001, (SystemClock.ElapsedRealtime() - t0) / 1000.0);
        Android.Util.Log.Info("Glance", $"update: downloaded {got} bytes in {sec:0.0} s, {parts} connection(s), {got / sec / 1048576:0.00} MB/s");
        return path;
    }

    /// <summary>«2,4 МБ/с» or «640 КБ/с»; empty until the speed is known.</summary>
    public static string SpeedText => Speed < 1 ? ""
        : Speed >= 1048576 ? $"{Mb((long)Speed)} МБ/с"
        : $"{Math.Max(1, (int)(Speed / 1024))} КБ/с";

    /// <summary>Progress at most ~12 times a second for the banner and once a second for the notification.</summary>
    static long _lastTick;
    static void Tick()
    {
        long now = SystemClock.UptimeMillis();
        if (now - _lastTick < 80) return;
        _lastTick = now;
        Main.Post(() =>
        {
            if (Stage == UpdateStage.Downloading)
            {
                long t = SystemClock.UptimeMillis(), d = Done;
                if (_speedT == 0) { _speedT = t; _speedDone = d; }
                else if (t - _speedT >= 500)
                {
                    double inst = (d - _speedDone) * 1000.0 / (t - _speedT);
                    Speed = Speed <= 0 ? inst : Speed * 0.7 + inst * 0.3;
                    _speedT = t;
                    _speedDone = d;
                }
            }
            Changed?.Invoke();
            if (SystemClock.UptimeMillis() - _lastNotify > 1000) Notify(null);
        });
    }

    /// <summary>The file must be Glance itself and newer than this build; Android checks the signature on install.</summary>
    static void Verify(string path, UpdateInfo u)
    {
        var pm = App.PackageManager!;
#pragma warning disable CS0618, CA1422 // VersionCode: deprecated in API 28, LongVersionCode used where available
        var info = pm.GetPackageArchiveInfo(path, 0) ?? throw new InvalidDataException("Файл повреждён — повторите");
        if (info.PackageName != App.PackageName) throw new InvalidDataException("Это не Glance");
        var mine = pm.GetPackageInfo(App.PackageName!, 0)!;
        long theirs = OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
        long ours = OperatingSystem.IsAndroidVersionAtLeast(28) ? mine.LongVersionCode : mine.VersionCode;
#pragma warning restore CS0618, CA1422
        Android.Util.Log.Info("Glance", $"update: verify {info.PackageName} {info.VersionName} ({theirs}) over {ours}");
        if (theirs <= ours) throw new InvalidDataException($"{info.VersionName} не новее этой");
    }

    /// <summary>
    /// Hands the checked APK to PackageInstaller; asks for «Установка неизвестных приложений» first if needed
    /// (only on a tap — by itself it just shows «Разрешите установку»). a is null when installing from the background.
    /// </summary>
    public static void Install(Activity? a, bool user = true)
    {
        if (_file is null || !File.Exists(_file)) { if (Target is { } t) Start(t, a, auto: !user); return; }
        if (!App.PackageManager!.CanRequestPackageInstalls())
        {
            if (a is null || !user)
            {
                Set(a is null ? UpdateStage.Ready : UpdateStage.NeedPermission);
                if (a is null) Notify(Target);
                return;
            }
            Set(UpdateStage.NeedPermission);
            try
            {
                var i = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + App.PackageName));
                a.StartActivity(i);
            }
            catch (Exception e) { Log.Error("Android unknown sources", e); }
            return;
        }
        InstallProgress = 0;
        Set(UpdateStage.Installing);
        string file = _file;
        _ = Task.Run(() =>
        {
            try
            {
                var installer = App.PackageManager!.PackageInstaller;
                var p = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
                p.SetAppPackageName(App.PackageName);
                long len = new FileInfo(file).Length;
                p.SetSize(len);
                // Android 12+: when Glance installed the current version itself, the update goes in without the extra
                // «Обновить?» dialog (USER_ACTION_NOT_REQUIRED = 2); called by reflection to stay safe on every binding
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                    try { p.Class.GetMethod("setRequireUserAction", Java.Lang.Integer.Type)?.Invoke(p, Java.Lang.Integer.ValueOf(2)); }
                    catch (Exception e) { Android.Util.Log.Info("Glance", "update: setRequireUserAction " + e.Message); }
                int id = installer.CreateSession(p);
                // after the update the new version tells about itself (and reopens if Glance was on screen)
                App.GetSharedPreferences("glance", FileCreationMode.Private)?.Edit()?
                    .PutString("updateInstalling", Target is { } tv ? Fmt.Ver(tv.Version) : "?")?
                    .PutBoolean("updateReopen", MainActivity.Visible)?.Apply();
                using var session = installer.OpenSession(id);
                using (var src = File.OpenRead(file))
                using (var dst = session.OpenWrite("glance.apk", 0, len)!)
                {
                    var buf = new byte[256 * 1024];
                    long copied = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        copied += n;
                        InstallProgress = 0.85f * copied / Math.Max(1, len); // the last 15 % is Android's own check
                        Tick();
                    }
                    session.Fsync(dst);
                }
                InstallProgress = 0.9f;
                Tick();
                var back = new Intent(App, typeof(InstallReceiver));
                back.SetAction(ActionStatus);
                // Mutable: PackageInstaller fills in the status and the confirmation intent
                var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
                var pi = PendingIntent.GetBroadcast(App, 4, back, flags)!;
                session.Commit(pi.IntentSender!);
                Android.Util.Log.Info("Glance", $"update: session {id} committed ({len} bytes)");
            }
            catch (Exception e)
            {
                Log.Error("Android update install", e);
                Set(UpdateStage.Failed, "Не удалось установить — нажмите, чтобы повторить");
            }
        });
    }

    /// <summary>After «Установка неизвестных приложений»: continue on its own when the permission is now on.</summary>
    public static void Resume(Activity a)
    {
        if (Stage == UpdateStage.NeedPermission && App.PackageManager!.CanRequestPackageInstalls()) Install(a);
    }

    internal static void OnStatus(Context c, Intent intent)
    {
        int status = intent.GetIntExtra("android.content.pm.extra.STATUS", -999);
        string msg = intent.GetStringExtra("android.content.pm.extra.STATUS_MESSAGE") ?? "";
        Android.Util.Log.Info("Glance", $"update: install status {status} {msg}");
        switch (status)
        {
            case -1: // STATUS_PENDING_USER_ACTION: Android's own «Обновить это приложение?»
                if (!MainActivity.Visible)
                {
                    // installing by itself in the background, but Android wants a tap: wait for it in the shade
                    Set(UpdateStage.Ready);
                    Notify(Target);
                    break;
                }
#pragma warning disable CS0618, CA1422 // GetParcelableExtra(string): deprecated in API 33, still works
                if (intent.GetParcelableExtra("android.intent.extra.INTENT") is Intent confirm)
#pragma warning restore CS0618, CA1422
                {
                    confirm.AddFlags(ActivityFlags.NewTask);
                    try { c.StartActivity(confirm); }
                    catch (Exception e) { Log.Error("Android update confirm", e); Set(UpdateStage.Ready); }
                }
                break;
            case 0: // STATUS_SUCCESS: the new version replaces this process
                InstallProgress = 1;
                Set(UpdateStage.Idle);
                break;
            case 3: // STATUS_FAILURE_ABORTED: the user said «Отмена»
                ForgetInstalling();
                Set(UpdateStage.Ready);
                break;
            default:
                ForgetInstalling();
                Set(UpdateStage.Failed, msg.Length > 0 ? $"Android: {msg}" : "Не удалось установить — нажмите, чтобы повторить");
                break;
        }
    }

    static void ForgetInstalling() =>
        App.GetSharedPreferences("glance", FileCreationMode.Private)?.Edit()?.Remove("updateInstalling")?.Remove("updateReopen")?.Apply();

    /// <summary>
    /// The new version has just replaced the old one (MY_PACKAGE_REPLACED): if Glance installed it, reopen Glance when it
    /// was on screen (possible with «Окно при подключении» allowed), otherwise say «Glance обновлён до X» in the shade.
    /// </summary>
    public static void AfterUpdate(Context c)
    {
        var prefs = c.GetSharedPreferences("glance", FileCreationMode.Private);
        string from = prefs?.GetString("updateInstalling", "") ?? "";
        bool reopen = prefs?.GetBoolean("updateReopen", false) ?? false;
        if (from.Length == 0) return;
        ForgetInstalling();
        string v = Fmt.Ver(Updates.Current);
        Android.Util.Log.Info("Glance", $"update: installed {v} (reopen {reopen})");
        if (reopen && Android.Provider.Settings.CanDrawOverlays(c))
            try
            {
                var open = new Intent(c, typeof(MainActivity));
                open.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                c.StartActivity(open);
                return;
            }
            catch (Exception e) { Log.Error("Android reopen after update", e); }
        try { Nm()?.Notify(NotifyId, Offer($"Glance обновлён до {v}", "Нажмите, чтобы открыть", install: false)); }
        catch (Exception e) { Log.Error("Android updated notice", e); }
    }

    /// <summary>Removes downloaded APKs that are already installed (or older).</summary>
    public static void Cleanup()
    {
        if (Busy || Stage == UpdateStage.Ready) return;
        try
        {
            var dir = new DirectoryInfo(Path.Combine(App.CacheDir!.AbsolutePath, "updates"));
            if (dir.Exists) foreach (var f in dir.GetFiles()) try { f.Delete(); } catch { }
        }
        catch { }
    }

    // ───────── notification: progress while downloading, «Доступна версия X» from the service ─────────

    static NotificationManager? Nm()
    {
        var nm = App.GetSystemService(Context.NotificationService) as NotificationManager;
        if (nm is not null && nm.GetNotificationChannel(Channel) is null)
        {
            var ch = new NotificationChannel(Channel, "Обновления Glance", NotificationImportance.Low) { Description = "Новая версия и ход скачивания" };
            ch.SetShowBadge(false);
            nm.CreateNotificationChannel(ch);
        }
        return nm;
    }

    static PendingIntent OpenInstall(bool install = true)
    {
        var open = new Intent(App, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        if (install) open.PutExtra("install", true);
        return PendingIntent.GetActivity(App, install ? 5 : 6, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>Shows the stage in the shade; ready: «нажмите, чтобы установить» (ready is the version to offer).</summary>
    static void Notify(UpdateInfo? ready)
    {
        _lastNotify = SystemClock.UptimeMillis();
        try
        {
            var nm = Nm();
            if (nm is null || Target is not { } t) return;
            if (Stage == UpdateStage.Downloading)
            {
                var n = new Notification.Builder(App, Channel)
                    .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)!
                    .SetContentTitle($"Скачивается Glance {Fmt.Ver(t.Version)}")!
                    .SetContentText((Total > 0 ? $"{Mb(Done)} из {Mb(Total)} МБ" : $"{Mb(Done)} МБ") + (SpeedText.Length > 0 ? " · " + SpeedText : ""))!
                    .SetProgress(1000, (int)(Progress * 1000), Total <= 0)!
                    .SetOngoing(true)!
                    .SetOnlyAlertOnce(true)!
                    .SetShowWhen(false)!
                    .SetContentIntent(OpenInstall())!
                    .Build();
                nm.Notify(NotifyId, n);
            }
            else if (ready is not null && Stage == UpdateStage.Ready)
                nm.Notify(NotifyId, Offer($"Glance {Fmt.Ver(t.Version)} скачан", "Нажмите, чтобы установить"));
            else nm.Cancel(NotifyId);
        }
        catch (Exception e) { Log.Error("Android update notify", e); }
    }

    static Notification Offer(string title, string text, bool install = true) =>
        new Notification.Builder(App, Channel)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownloadDone)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetAutoCancel(true)!
            .SetContentIntent(OpenInstall(install))!
            .Build()!;

    /// <summary>From the service: a newer version exists (once per version).</summary>
    public static void Announce(UpdateInfo u)
    {
        var prefs = App.GetSharedPreferences("glance", FileCreationMode.Private);
        string v = Fmt.Ver(u.Version);
        if (prefs?.GetString("updateAnnounced", "") == v || Busy || Stage == UpdateStage.Ready) return;
        prefs?.Edit()?.PutString("updateAnnounced", v)?.Apply();
        try { Nm()?.Notify(NotifyId, Offer($"Доступна версия Glance {v}", "Нажмите, чтобы скачать и установить")); }
        catch (Exception e) { Log.Error("Android update announce", e); }
        Android.Util.Log.Info("Glance", $"update: announced {v}");
    }

    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
}

/// <summary>Receives PackageInstaller's answer for the update session.</summary>
[BroadcastReceiver(Name = "io.github.ddedlish.glance.InstallReceiver", Exported = false)]
public sealed class InstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        Updater.OnStatus(context, intent);
    }
}
