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
    public static void Start(Context c, bool demo, bool popup = false, bool notice = false)
    {
        if (!Enabled(c)) return;
        // Android 14 lets a connectedDevice service start only with the Bluetooth permission granted.
        if (!demo && Build.VERSION.SdkInt >= BuildVersionCodes.S && c.CheckSelfPermission("android.permission.BLUETOOTH_CONNECT") != Permission.Granted) return;
        try
        {
            var i = new Intent(c, typeof(GlanceService));
            i.PutExtra("demo", demo);
            i.PutExtra("popup", popup);
            i.PutExtra("notice", notice);
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
        if (!demo) ScheduleUpdateCheck();
        // self-test: show the connect popup for the demo earbuds
        if (demo && intent?.GetBooleanExtra("popup", false) == true)
            _main.PostDelayed(() =>
            {
                var b = _dev?.Connected.FirstOrDefault(a => a.HasParts);
                Android.Util.Log.Info("Glance", $"announce test: {(b is null ? "no earbuds" : b.DisplayName)}");
                if (b is not null) { Announce(b, force: true); Sounds.Play(this, Cue.Connect); }
            }, 900);
        // self-test: «пора зарядить» for the weakest demo device and «заряжено» for the charging one
        if (demo && intent?.GetBooleanExtra("notice", false) == true)
            _main.PostDelayed(() =>
            {
                var low = _dev?.Connected.Where(a => !a.Charging && a.WeakestLevel is not null).MinBy(a => a.WeakestLevel);
                var full = _dev?.Connected.FirstOrDefault(a => a.Charging && a.Level is not null);
                Android.Util.Log.Info("Glance", $"notice test: {low?.DisplayName ?? "-"} / {full?.DisplayName ?? "-"}");
                if (full is not null) Notice(full, low: false);
                if (low is not null) Notice(low, low: true);
            }, 900);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _dev?.Stop();
        _dev = null;
        GlanceWidget.Push(this, null); // no live levels without the service: the widget says how to bring them back
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
                if (!a.Charging && w <= LowAt) { if (_low.Add(a.Key) && !fresh) { cue ??= Cue.Low; Notice(a, low: true); } }
                else if (a.Charging || w >= LowAt + 5)
                {
                    if (_low.Remove(a.Key)) Clear(a.Key, lowOnly: true); // on the charger: «пора зарядить» is done
                }
            }
            if (a.Level is int l)
            {
                if (a.Charging && l >= 100) { if (_full.Add(a.Key) && !fresh) { cue = Cue.Full; Notice(a, low: false); } }
                else if (!a.Charging || l <= 95) _full.Remove(a.Key);
            }
        }
        if (chime && cue is Cue c) Sounds.Play(this, c);
    }

    // ───────── «пора зарядить» / «заряжено полностью»: one notification per device and cycle ─────────

    const string BatteryChannel = "glance-battery";
    readonly Dictionary<string, bool> _posted = new(StringComparer.Ordinal); // key → was it «low»

    static int NoticeId(string key)
    {
        int h = 17;
        foreach (char ch in key) h = unchecked(h * 31 + ch);
        return 1000 + (h & 0x7FFF);
    }

    void Notice(Accessory a, bool low)
    {
        if (!UserPrefs.Notify(this)) { Android.Util.Log.Info("Glance", $"notice: {(low ? "low" : "full")} {a.DisplayName} skipped (off)"); return; }
        try
        {
            var nm = GetSystemService(NotificationService) as NotificationManager;
            if (nm is null) return;
            if (nm.GetNotificationChannel(BatteryChannel) is null)
            {
                // High: it peeks over the screen for a moment; silent itself — Glance plays its own soft chime (and keeps quiet in «Не беспокоить»)
                var ch = new NotificationChannel(BatteryChannel, "Низкий заряд и полная зарядка", NotificationImportance.High)
                { Description = "Пора зарядить аксессуар или он уже заряжен — один раз за цикл" };
                ch.EnableVibration(false);
                ch.SetSound(null, null);
                nm.CreateNotificationChannel(ch);
            }
            int lvl = (low ? a.WeakestLevel : a.Level) ?? 0;
            // short title and text: the collapsed notification shows them whole; the details are in the expanded view
            string title = low ? $"{a.DisplayName} — {lvl}%" : $"{a.DisplayName} заряжен";
            string text = low ? (a.HasParts ? $"Пора зарядить · {Parts(a)}%" : "Пора зарядить") : "Можно снять с зарядки";
            string more = low
                ? $"{(a.HasParts ? $"Пора зарядить: {Parts(a)}%." : $"Пора зарядить: осталось {lvl}%.")} Предупреждение на {LowAt}% и ниже — порог меняется в настройках Glance."
                : $"Заряд {lvl}% — можно снять с зарядки.";
            var open = new Intent(this, typeof(MainActivity));
            open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            var n = new Notification.Builder(this, BatteryChannel)
                .SetSmallIcon(StatusIcon(lvl, !low))!
                .SetContentTitle(title)!
                .SetContentText(text)!
                .SetStyle(new Notification.BigTextStyle().BigText(more))!
                .SetColor(LevelColor(lvl, !low).ToArgb())!
                .SetAutoCancel(true)!
                .SetCategory(low ? Notification.CategoryReminder : Notification.CategoryStatus)!
                .SetVisibility(NotificationVisibility.Public)!
                .SetContentIntent(PendingIntent.GetActivity(this, 3, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))!
                .Build();
            nm.Notify(NoticeId(a.Key), n); // the same slot per device: «заряжено» replaces an old «пора зарядить»
            _posted[a.Key] = low;
            Android.Util.Log.Info("Glance", $"notice: {(low ? "low" : "full")} {a.DisplayName} {lvl}%");
        }
        catch (Exception e) { Log.Error("Android battery notice", e); Android.Util.Log.Error("Glance", "notice failed: " + e.Message); }
    }

    /// <summary>Removes the device's notice (only a «пора зарядить» one when lowOnly).</summary>
    void Clear(string key, bool lowOnly)
    {
        if (!_posted.TryGetValue(key, out bool wasLow) || (lowOnly && !wasLow)) return;
        _posted.Remove(key);
        try { (GetSystemService(NotificationService) as NotificationManager)?.Cancel(NoticeId(key)); } catch { }
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

    // ───────── updates while Glance is closed: «Доступна версия X» in the shade, once per version ─────────

    bool _updScheduled;

    void ScheduleUpdateCheck()
    {
        if (_updScheduled) return;
        _updScheduled = true;
        _main.PostDelayed(CheckUpdate, 90_000); // not right at boot: let the phone settle first
    }

    void CheckUpdate()
    {
        _updScheduled = false;
        if (_dev is null) return; // the service has stopped
        ScheduleNext();
        if (!UserPrefs.AutoUpdate(this) || MainActivity.Visible || Updater.Busy) return;
        var prefs = GetSharedPreferences(Prefs, FileCreationMode.Private);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - (prefs?.GetLong("lastUpdateCheck", 0) ?? 0) < 6 * 3600) return;
        prefs?.Edit()?.PutLong("lastUpdateCheck", now)?.Apply();
        _ = Task.Run(async () =>
        {
            var u = await Updates.CheckAsync().ConfigureAwait(false);
            if (u is not null) _main.Post(() => { Updater.Announce(u); Updater.Prefetch(u); });
        });
    }

    void ScheduleNext()
    {
        _updScheduled = true;
        _main.PostDelayed(CheckUpdate, 3600_000L); // hourly look at the clock; the network is asked at most every 6 hours
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
        GlanceWidget.Push(this, _dev?.Connected ?? (IReadOnlyList<Accessory>)Array.Empty<Accessory>()); // the home-screen widget follows the shade
        var nm = GetSystemService(NotificationService) as NotificationManager;
        try { nm?.Notify(NotifyId, n); }
        catch (Exception e)
        {
            // the drawn card did not get through (too large for this device?): plain text from now on
            Log.Error("Android shade notify", e);
            if (!_plain)
            {
                _plain = true;
                try { nm?.Notify(NotifyId, Compose(out _)); } catch (Exception e2) { Log.Error("Android shade notify plain", e2); }
            }
        }
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

        bool dark = ((Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo) & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes;
        key += dark ? "\nD" : "\nL";
        var (small, big) = _plain ? default : Art(all, weakest, text, dark);

        var b = new Notification.Builder(this, Channel)
            .SetSmallIcon(StatusIcon(iconLevel, iconCharging))!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetShowWhen(false)!
            .SetCategory(Notification.CategoryStatus)!
            .SetVisibility(NotificationVisibility.Public)!
            .SetColor(LevelColor(iconLevel, iconCharging).ToArgb())!
            .SetContentIntent(openPi)!
            .AddAction(new Notification.Action.Builder((Icon?)null, new Java.Lang.String("Убрать из шторки"), hidePi).Build())!;
        if (small is not null && big is not null)
            b.SetStyle(new Notification.DecoratedCustomViewStyle())!.SetCustomContentView(small)!.SetCustomBigContentView(big);
        else b.SetStyle(inbox);
        return b.Build()!;
    }

    bool _plain;

    /// <summary>
    /// The Liquid Glass card for the shade (ShadeArt): glass chips with liquid rings when collapsed, a glass row per
    /// accessory when expanded. Null when the layout is missing or drawing fails — then the plain list is shown.
    /// </summary>
    (Android.Widget.RemoteViews?, Android.Widget.RemoteViews?) Art(IReadOnlyList<Accessory> all, Accessory? weakest, string text, bool dark)
    {
        try
        {
            var res = Resources;
            int layout = res?.GetIdentifier("glance_shade", "layout", PackageName) ?? 0;
            int img = res?.GetIdentifier("shade_art", "id", PackageName) ?? 0;
            if (res is null || layout == 0 || img == 0) return (null, null);
            float density = res.DisplayMetrics?.Density ?? 2f;
            // the room the shade gives a custom view: Android 12+ indents it past the app icon (and the expand button
            // when collapsed); before that only the card padding. Drawn to this width, the picture is not scaled down.
            float sw = res.Configuration?.ScreenWidthDp ?? 360;
            bool s31 = OperatingSystem.IsAndroidVersionAtLeast(31);
            float w = Math.Clamp(s31 ? sw * 0.6f : sw - 64, 210, 440);
            float wSmall = Math.Clamp(s31 ? w - 30 : w, 190, 440);
            string head = all.Count == 0 ? "Glance следит за зарядом" : text;
            string a11y = all.Count == 0 ? text : string.Join("; ", all.Select(Line));

            var small = new Android.Widget.RemoteViews(PackageName, layout);
            small.SetImageViewBitmap(img, ShadeArt.Collapsed(all, dark, wSmall, density));
            small.SetContentDescription(img, a11y);
            var big = new Android.Widget.RemoteViews(PackageName, layout);
            big.SetImageViewBitmap(img, ShadeArt.Expanded(all, head, dark, w, density));
            big.SetContentDescription(img, a11y);
            return (small, big);
        }
        catch (Exception e)
        {
            Log.Error("Android shade art", e);
            return (null, null);
        }
    }

    /// <summary>Light ↔ dark theme switched: redraw the card in the new colours.</summary>
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        _last = "";
        Schedule();
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
            // short enough to fit one line of the shade: «AirPods Pro — Л 72 · П 64 · К 41%»
            return $"{a.DisplayName} — {Parts(a)}%";
        }
        if (a.Level is int lv) return $"{a.DisplayName} — {lv}%{(a.Charging ? " ⚡" : "")}";
        return $"{a.DisplayName} — {a.Link}, заряд не передаётся";
    }

    /// <summary>«Л 72 · П 64 · К 41» (without the % sign).</summary>
    static string Parts(Accessory a)
    {
        var parts = new List<string>();
        if (a.Left.Level is int l) parts.Add($"Л {l}{(a.Left.Charging ? "⚡" : "")}");
        if (a.Right.Level is int r) parts.Add($"П {r}{(a.Right.Charging ? "⚡" : "")}");
        if (a.Case.Level is int c) parts.Add($"К {c}{(a.Case.Charging ? "⚡" : "")}");
        return string.Join(" · ", parts);
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
