using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using AColor = Android.Graphics.Color;

namespace Glance.Droid;

/// <summary>
/// «Glance в шторке»: a quiet always-on notification with every connected accessory (per-bud levels too) and
/// the phone. Its status-bar icon is a live ring with the weakest level as a number, like the tray icon on Windows.
/// A foreground service of type connectedDevice keeps the Bluetooth listeners alive while the app is closed.
/// </summary>
[Service(Name = "io.github.ddedlish.glance.GlanceService", Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class GlanceService : Service
{
    const string Channel = "glance-shade";
    const int NotifyId = 1;
    public const string ActionHide = "io.github.ddedlish.glance.HIDE_SHADE";
    const string Prefs = "glance", PrefShade = "shade";

    Devices? _dev;
    readonly Handler _main = new(Looper.MainLooper!);
    string _last = "";
    bool _pending;

    // ───────── on / off (shared with the app screen) ─────────

    public static bool Enabled(Context c) => c.GetSharedPreferences(Prefs, FileCreationMode.Private)?.GetBoolean(PrefShade, true) ?? true;

    public static void SetEnabled(Context c, bool on)
    {
        c.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.PutBoolean(PrefShade, on)?.Apply();
        if (on) Start(c, false); else c.StopService(new Intent(c, typeof(GlanceService)));
    }

    /// <summary>Starts the shade notification when it is switched on and Android allows it (Bluetooth permission).</summary>
    public static void Start(Context c, bool demo)
    {
        if (!Enabled(c)) return;
        // Android 14 lets a connectedDevice service start only with the Bluetooth permission granted.
        if (!demo && Build.VERSION.SdkInt >= BuildVersionCodes.S && c.CheckSelfPermission("android.permission.BLUETOOTH_CONNECT") != Permission.Granted) return;
        try
        {
            var i = new Intent(c, typeof(GlanceService));
            i.PutExtra("demo", demo);
            c.StartForegroundService(i);
        }
        catch (Exception e) { Log.Error("Android shade start", e); }
    }

    // ───────── service ─────────

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionHide)
        {
            GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.PutBoolean(PrefShade, false)?.Apply();
            Shutdown();
            return StartCommandResult.NotSticky;
        }

        EnsureChannel();
        bool demo = intent?.GetBooleanExtra("demo", false) ?? false;
        // Android requires startForeground right away after startForegroundService.
        if (!GoForeground(Compose(out _))) { StopSelf(); return StartCommandResult.NotSticky; }

        if (_dev is null || _dev.Demo != demo)
        {
            _dev?.Stop();
            _dev = new Devices(this) { Background = true };
            _dev.Changed += Schedule;
            if (demo) _dev.UseDemo(); else _dev.Start();
        }
        else _dev.Refresh();
        Publish();
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _dev?.Stop();
        _dev = null;
        base.OnDestroy();
    }

    void Shutdown()
    {
        _dev?.Stop();
        _dev = null;
        try { StopForeground(StopForegroundFlags.Remove); } catch { }
        StopSelf();
    }

    bool GoForeground(Notification n)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotifyId, n, ForegroundService.TypeConnectedDevice);
            else StartForeground(NotifyId, n);
            return true;
        }
        catch (Exception e) { Log.Error("Android shade foreground", e); return false; }
    }

    /// <summary>Adverts can arrive many times a second: coalesce into one update.</summary>
    void Schedule()
    {
        if (_pending) return;
        _pending = true;
        _main.PostDelayed(() => { _pending = false; Publish(); }, 400);
    }

    void Publish()
    {
        var n = Compose(out string key);
        if (key == _last) return;
        _last = key;
        try { (GetSystemService(NotificationService) as NotificationManager)?.Notify(NotifyId, n); }
        catch (Exception e) { Log.Error("Android shade notify", e); }
        Android.Util.Log.Info("Glance", "shade: " + key.Replace('\n', ' '));
    }

    void EnsureChannel()
    {
        var nm = GetSystemService(NotificationService) as NotificationManager;
        if (nm is null || nm.GetNotificationChannel(Channel) is not null) return;
        var ch = new NotificationChannel(Channel, "Заряд в шторке", NotificationImportance.Default) // Default: the ring stays in the status bar; sound and vibration are off
        {
            Description = "Постоянное уведомление с зарядом подключённых аксессуаров",
        };
        ch.SetShowBadge(false);
        ch.EnableVibration(false);
        ch.SetSound(null, null);
        nm.CreateNotificationChannel(ch);
    }

    // ───────── content ─────────

    Notification Compose(out string key)
    {
        var list = _dev?.Connected.Where(a => a.Level is not null || a.HasParts).ToList() ?? new List<Accessory>();
        var all = _dev?.Connected ?? (IReadOnlyList<Accessory>)Array.Empty<Accessory>();
        int? phone = _dev?.PhoneLevel;
        bool phoneCharging = _dev?.PhoneCharging ?? false;

        var weakest = list.Where(a => !a.Charging).MinBy(a => a.WeakestLevel) ?? list.MinBy(a => a.WeakestLevel);
        string title, text;
        if (all.Count == 0)
        {
            title = "Нет подключённых аксессуаров";
            text = phone is int p0 ? $"Телефон {p0}%{(phoneCharging ? " ⚡" : "")}" : "Подключите наушники, часы или геймпад";
        }
        else
        {
            title = string.Join(" · ", all.Take(3).Select(Short)) + (all.Count > 3 ? $" · ещё {all.Count - 3}" : "");
            text = weakest is { WeakestLevel: int wl } w
                ? (w.Charging ? $"{w.DisplayName} заряжается" : wl <= 20 ? $"{w.DisplayName}: пора зарядить" : $"Первым разрядится {w.DisplayName}")
                : "Подключено, заряд не передаётся";
            if (phone is int p1) text += $" · телефон {p1}%";
        }

        var inbox = new Notification.InboxStyle();
        foreach (var a in all) inbox.AddLine(Line(a));
        if (phone is int p2) inbox.AddLine($"Телефон — {p2}%{(phoneCharging ? " ⚡" : "")}");
        inbox.SetBigContentTitle(all.Count == 0 ? title : $"Аксессуары: {all.Count}");

        int? iconLevel = weakest?.WeakestLevel;
        bool iconCharging = weakest?.Charging ?? false;
        key = $"{title}\n{text}\n{string.Join("|", all.Select(Line))}\n{phone}{phoneCharging}\n{iconLevel}{iconCharging}";

        var open = new Intent(this, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        var openPi = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var hide = new Intent(this, typeof(GlanceService));
        hide.SetAction(ActionHide);
        var hidePi = PendingIntent.GetService(this, 1, hide, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var b = new Notification.Builder(this, Channel)
            .SetSmallIcon(StatusIcon(iconLevel, iconCharging))!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetStyle(inbox)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetShowWhen(false)!
            .SetCategory(Notification.CategoryStatus)!
            .SetVisibility(NotificationVisibility.Public)!
            .SetColor(LevelColor(iconLevel, iconCharging).ToArgb())!
            .SetContentIntent(openPi)!
            .AddAction(new Notification.Action.Builder((Icon?)null, new Java.Lang.String("Убрать из шторки"), hidePi).Build())!;
        return b.Build()!;
    }

    static string Short(Accessory a)
    {
        string name = a.DisplayName.Length > 14 ? a.DisplayName[..13].TrimEnd() + "…" : a.DisplayName;
        return a.WeakestLevel is int l ? $"{name} {l}%{(a.Charging ? "⚡" : "")}" : name;
    }

    static string Line(Accessory a)
    {
        if (a.HasParts)
        {
            var parts = new List<string>();
            // short enough to fit one line of the shade: «AirPods Pro — Л 72 · П 64 · К 41%»
            if (a.Left.Level is int l) parts.Add($"Л {l}{(a.Left.Charging ? "⚡" : "")}");
            if (a.Right.Level is int r) parts.Add($"П {r}{(a.Right.Charging ? "⚡" : "")}");
            if (a.Case.Level is int c) parts.Add($"К {c}{(a.Case.Charging ? "⚡" : "")}");
            return $"{a.DisplayName} — {string.Join(" · ", parts)}%";
        }
        if (a.Level is int lv) return $"{a.DisplayName} — {lv}%{(a.Charging ? " ⚡" : "")}";
        return $"{a.DisplayName} — {a.Link}, заряд не передаётся";
    }

    static AColor LevelColor(int? level, bool charging) =>
        charging ? Palette.Green : level is int l && l <= 20 ? Palette.Red : level is int m && m <= 40 ? Palette.Amber : AColor.Argb(255, 100, 170, 255);

    /// <summary>
    /// Status-bar icon (Android paints it as a white mask): a ring filled to the weakest level with the number
    /// inside; a bolt when charging, an empty ring with a dot when nothing reports a level.
    /// </summary>
    Icon StatusIcon(int? level, bool charging)
    {
        int px = (int)Math.Round(24 * (Resources?.DisplayMetrics?.Density ?? 2f));
        var bmp = Bitmap.CreateBitmap(px, px, Bitmap.Config.Argb8888!)!;
        using var c = new Canvas(bmp);
        float s = px / 24f, th = 2.4f * s;
        var box = new RectF(th / 2 + s, th / 2 + s, px - th / 2 - s, px - th / 2 - s);
        using var p = new Paint(PaintFlags.AntiAlias) { StrokeWidth = th, StrokeCap = Paint.Cap.Round, Color = AColor.Argb(90, 255, 255, 255) };
        p.SetStyle(Paint.Style.Stroke);
        c.DrawOval(box, p);
        p.Color = AColor.White;
        if (level is int l)
        {
            c.DrawArc(box, -90, 360f * Math.Clamp(l, 0, 100) / 100f, false, p);
            if (charging)
            {
                using var bolt = new Android.Graphics.Path();
                bolt.MoveTo(13.2f * s, 5.5f * s); bolt.LineTo(8f * s, 13f * s); bolt.LineTo(11.5f * s, 13f * s);
                bolt.LineTo(10.8f * s, 18.5f * s); bolt.LineTo(16f * s, 11f * s); bolt.LineTo(12.5f * s, 11f * s); bolt.Close();
                using var f = new Paint(PaintFlags.AntiAlias) { Color = AColor.White };
                c.DrawPath(bolt, f);
            }
            else
            {
                using var t = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText) { Color = AColor.White, TextAlign = Paint.Align.Center };
                t.SetTypeface(Typeface.Create("sans-serif-condensed", TypefaceStyle.Bold));
                string txt = l >= 100 ? "99" : l.ToString();
                t.TextSize = 10.5f * s;
                c.DrawText(txt, px / 2f, px / 2f + 3.8f * s, t);
            }
        }
        else
        {
            using var f = new Paint(PaintFlags.AntiAlias) { Color = AColor.White };
            c.DrawCircle(px / 2f, px / 2f, 2.6f * s, f);
        }
        return Icon.CreateWithBitmap(bmp)!;
    }
}

/// <summary>Brings the shade notification back after a reboot or an app update (if it is switched on).</summary>
[BroadcastReceiver(Name = "io.github.ddedlish.glance.BootReceiver", Exported = true, Enabled = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        GlanceService.Start(context, false);
    }
}
