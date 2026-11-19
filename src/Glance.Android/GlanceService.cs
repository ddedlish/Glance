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
/// a popup the moment one connects. Its status-bar icon is a live ring with the weakest level as a number, like the tray icon on Windows.
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
    public static void Start(Context c, bool demo, bool popup = false)
    {
        if (!Enabled(c)) return;
        // Android 14 lets a connectedDevice service start only with the Bluetooth permission granted.
        if (!demo && Build.VERSION.SdkInt >= BuildVersionCodes.S && c.CheckSelfPermission("android.permission.BLUETOOTH_CONNECT") != Permission.Granted) return;
        try
        {
            var i = new Intent(c, typeof(GlanceService));
            i.PutExtra("demo", demo);
            i.PutExtra("popup", popup);
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
            _known = null; // the first snapshot is not «just connected»
            _dev = new Devices(this) { Background = true };
            _dev.Changed += OnChanged;
            if (demo) _dev.UseDemo(); else _dev.Start();
        }
        else _dev.Refresh();
        Publish();
        Watch();
        // self-test: show the connect popup for the demo earbuds
        if (demo && intent?.GetBooleanExtra("popup", false) == true)
            _main.PostDelayed(() =>
            {
                var b = _dev?.Connected.FirstOrDefault(a => a.HasParts);
                Android.Util.Log.Info("Glance", $"announce test: {(b is null ? "no earbuds" : b.DisplayName)}");
                if (b is not null) { Announce(b, force: true); Sounds.Play(this, Cue.Connect); }
            }, 900);
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

    // ───────── «just connected»: popup over any app, live ─────────

    HashSet<string>? _known;

    void OnChanged()
    {
        Watch();
        ConnectPopup.Refresh(); // the card on screen follows the levels live, without the 400 ms coalescing
        Schedule();
    }

    /// <summary>Finds accessories that have just connected and announces them at once.</summary>
    void Watch()
    {
        if (_dev is null) return;
        var now = _dev.Connected.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        if (_known is null) { _known = now; Levels(chime: false); return; }
        bool came = false;
        foreach (var a in _dev.Connected)
            if (!_known.Contains(a.Key)) { Announce(a); came = true; }
        bool left = _known.Any(k => !now.Contains(k));
        _known = now;
        if (came) Sounds.Play(this, Cue.Connect);
        else if (left) Sounds.Play(this, Cue.Disconnect);
        Levels(chime: !came);
    }

    // ───────── sound cues: low battery and fully charged, once per cycle ─────────

    /// <summary>«Низкий заряд» threshold from Settings (default 20 %).</summary>
    int LowAt => UserPrefs.LowAt(this);
    readonly HashSet<string> _low = new(StringComparer.Ordinal), _full = new(StringComparer.Ordinal), _seen = new(StringComparer.Ordinal);

    void Levels(bool chime)
    {
        if (_dev is null) return;
        Cue? cue = null;
        foreach (var a in _dev.Connected)
        {
            // a device seen for the first time (or after a restart) only sets the state: no chime for old news
            bool fresh = _seen.Add(a.Key);
            if (a.WeakestLevel is int w)
            {
                if (!a.Charging && w <= LowAt) { if (_low.Add(a.Key) && !fresh) cue ??= Cue.Low; }
                else if (a.Charging || w >= LowAt + 5) _low.Remove(a.Key); // charged again: next drop chimes again
            }
            if (a.Level is int l)
            {
                if (a.Charging && l >= 100) { if (_full.Add(a.Key) && !fresh) cue = Cue.Full; }
                else if (!a.Charging || l <= 95) _full.Remove(a.Key);
            }
        }
        if (chime && cue is Cue c) Sounds.Play(this, c);
    }

    void Announce(Accessory a, bool force = false)
    {
        if (!force && MainActivity.Visible) return; // the app itself is on screen
        Android.Util.Log.Info("Glance", $"announce: {a.DisplayName}");
        if (ConnectPopup.Show(this, a)) return;
        // no «Поверх других приложений»: a heads-up notification that peeks for a few seconds instead
        try
        {
            var nm = GetSystemService(NotificationService) as NotificationManager;
            if (nm is null) return;
            if (nm.GetNotificationChannel(ConnectChannel) is null)
            {
                var ch = new NotificationChannel(ConnectChannel, "Подключение аксессуара", NotificationImportance.High) { Description = "Заряд сразу при подключении" };
                ch.EnableVibration(false);
                ch.SetSound(null, null);
                nm.CreateNotificationChannel(ch);
            }
            var open = new Intent(this, typeof(MainActivity));
            open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            var n = new Notification.Builder(this, ConnectChannel)
                .SetSmallIcon(StatusIcon(a.WeakestLevel, a.Charging))!
                .SetContentTitle($"{a.DisplayName} — подключено")!
                .SetContentText(Line(a))!
                .SetAutoCancel(true)!
                .SetTimeoutAfter(8000)!
                .SetCategory(Notification.CategoryStatus)!
                .SetContentIntent(PendingIntent.GetActivity(this, 2, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))!
                .Build();
            nm.Notify(2, n);
        }
        catch (Exception e) { Log.Error("Android connect notice", e); Android.Util.Log.Error("Glance", "connect notice failed: " + e); }
    }

    const string ConnectChannel = "glance-connect";

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

        var weakest = list.Where(a => !a.Charging).MinBy(a => a.WeakestLevel) ?? list.MinBy(a => a.WeakestLevel);
        string title, text;
        if (all.Count == 0)
        {
            title = "Нет подключённых аксессуаров";
            text = "Подключите наушники, часы или геймпад";
        }
        else
        {
            title = string.Join(" · ", all.Take(3).Select(Short)) + (all.Count > 3 ? $" · ещё {all.Count - 3}" : "");
            text = weakest is { WeakestLevel: int wl } w
                ? (w.Charging ? $"{w.DisplayName} заряжается" : wl <= LowAt ? $"{w.DisplayName}: пора зарядить" : $"Первым разрядится {w.DisplayName}")
                : "Подключено, заряд не передаётся";
        }

        var inbox = new Notification.InboxStyle();
        foreach (var a in all) inbox.AddLine(Line(a));
        inbox.SetBigContentTitle(all.Count == 0 ? title : $"Аксессуары: {all.Count}");

        int? iconLevel = weakest?.WeakestLevel;
        bool iconCharging = weakest?.Charging ?? false;
        key = $"{title}\n{text}\n{string.Join("|", all.Select(Line))}\n{iconLevel}{iconCharging}";

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
