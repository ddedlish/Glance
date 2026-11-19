using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Net.Http;

namespace Glance.Droid;

internal enum UpdateStage { Idle, Downloading, Ready, NeedPermission, Installing, Failed }

/// <summary>
/// In-app updates: downloads the APK of a newer android-v* release in the background (progress in the banner and in a
/// notification), checks it (size, package name, a newer versionCode) and installs it over this version through
/// PackageInstaller — Android shows its own «Обновить?» confirmation. When installing from Glance is not allowed yet,
/// opens «Установка неизвестных приложений» and continues as soon as the user comes back.
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
    public static long Done { get; private set; }
    public static long Total { get; private set; }
    public static string Error { get; private set; } = "";
    static string? _file;

    /// <summary>Raised on the main thread whenever the stage or the progress changes.</summary>
    public static event Action? Changed;

    public static float Progress => Total > 0 ? Math.Clamp(Done / (float)Total, 0, 1) : 0;
    public static bool Busy => Stage is UpdateStage.Downloading or UpdateStage.Installing;

    static void Set(UpdateStage s, string? error = null)
    {
        Stage = s;
        if (error is not null) Error = error;
        Android.Util.Log.Info("Glance", $"update: {s}{(error is null ? "" : " — " + error)}");
        Main.Post(() => Changed?.Invoke());
    }

    /// <summary>Self-test: a frozen «downloading» banner without network.</summary>
    public static void Demo(UpdateInfo u, float progress)
    {
        Target = u;
        Total = u.Size > 0 ? u.Size : 12_176_034;
        Done = (long)(Total * progress);
        Set(UpdateStage.Downloading);
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
                Start(u, installAfter: a);
                return;
        }
    }

    static void Reset()
    {
        _cts?.Cancel();
        _cts = null;
        _file = null;
        Done = Total = 0;
        Stage = UpdateStage.Idle;
    }

    static void Start(UpdateInfo u, Activity? installAfter)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Done = 0;
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
                    // the user asked to install: go on at once while Glance is on screen, otherwise wait in the shade
                    if (weak is not null && weak.TryGetTarget(out var act) && MainActivity.Visible && !act.IsFinishing) { Notify(null); Install(act); }
                    else Notify(u);
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception e)
            {
                Log.Error("Android update download", e);
                Set(UpdateStage.Failed, e is HttpRequestException or IOException or TaskCanceledException ? "Нет связи — нажмите, чтобы повторить" : e.Message);
                Main.Post(() => Notify(null));
            }
        });
    }

    static async Task<string> DownloadAsync(UpdateInfo u, CancellationToken ct)
    {
        var dir = new DirectoryInfo(Path.Combine(App.CacheDir!.AbsolutePath, "updates"));
        dir.Create();
        foreach (var old in dir.GetFiles()) try { old.Delete(); } catch { }
        string path = Path.Combine(dir.FullName, $"Glance-Android-{Fmt.Ver(u.Version)}.apk");
        string part = path + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Glance-Android/{Fmt.Ver(Updates.Current)}");
        using var resp = await http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long expected = u.Size > 0 ? u.Size : resp.Content.Headers.ContentLength ?? 0;
        Total = expected;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                Done += n;
                if (expected > 0 && Done > expected + 1024) throw new InvalidDataException("Файл больше ожидаемого");
                Tick();
            }
        }
        long got = new FileInfo(part).Length;
        if (expected > 0 && got != expected) throw new InvalidDataException($"Скачано {got} из {expected} байт — повторите");
        if (got < 1_000_000) throw new InvalidDataException("Файл обновления слишком маленький");
        File.Move(part, path, overwrite: true);
        Android.Util.Log.Info("Glance", $"update: downloaded {got} bytes");
        return path;
    }

    /// <summary>Progress at most ~12 times a second for the banner and once a second for the notification.</summary>
    static long _lastTick;
    static void Tick()
    {
        long now = SystemClock.UptimeMillis();
        if (now - _lastTick < 80) return;
        _lastTick = now;
        Main.Post(() =>
        {
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
        if (theirs <= ours) throw new InvalidDataException($"Версия {info.VersionName} не новее установленной");
    }

    /// <summary>Hands the checked APK to PackageInstaller; asks for «Установка неизвестных приложений» first if needed.</summary>
    public static void Install(Activity a)
    {
        if (_file is null || !File.Exists(_file)) { if (Target is { } t) Start(t, a); return; }
        if (!App.PackageManager!.CanRequestPackageInstalls())
        {
            Set(UpdateStage.NeedPermission);
            try
            {
                var i = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + App.PackageName));
                a.StartActivity(i);
            }
            catch (Exception e) { Log.Error("Android unknown sources", e); }
            return;
        }
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
                int id = installer.CreateSession(p);
                using var session = installer.OpenSession(id);
                using (var src = File.OpenRead(file))
                using (var dst = session.OpenWrite("glance.apk", 0, len)!)
                {
                    src.CopyTo(dst);
                    session.Fsync(dst);
                }
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
                Set(UpdateStage.Idle);
                break;
            case 3: // STATUS_FAILURE_ABORTED: the user said «Отмена»
                Set(UpdateStage.Ready);
                break;
            default:
                Set(UpdateStage.Failed, msg.Length > 0 ? $"Android: {msg}" : "Не удалось установить — нажмите, чтобы повторить");
                break;
        }
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

    static PendingIntent OpenInstall()
    {
        var open = new Intent(App, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        open.PutExtra("install", true);
        return PendingIntent.GetActivity(App, 5, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
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
                    .SetContentText(Total > 0 ? $"{Mb(Done)} из {Mb(Total)} МБ" : $"{Mb(Done)} МБ")!
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

    static Notification Offer(string title, string text) =>
        new Notification.Builder(App, Channel)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownloadDone)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetAutoCancel(true)!
            .SetContentIntent(OpenInstall())!
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
