using Android.App;
using Android.Widget;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using AColor = Android.Graphics.Color;

namespace Glance.Droid;

[Activity(Label = "Glance", Name = "io.github.ddedlish.glance.MainActivity", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.Material.NoActionBar", LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.UiMode | ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : Activity
{
    Devices? _dev;

    /// <summary>True while the screen is in front: the service then skips the connect popup.</summary>
    public static bool Visible { get; private set; }
    GlanceView? _view;
    SettingsView? _settings;
    ScrollView? _mainScroll, _setScroll;
    Backdrop? _back;
    BackCallback? _backCb;
    bool _settingsShown;
    const string Prefs = "glance";
    const int PermRequest = 7;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Android fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Android task", e.Exception); e.SetObserved(); };

        _dev = new Devices(this);
        var root = new FrameLayout(this);
        _back = new Backdrop(this);
        root.AddView(_back, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        var scroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
        _view = new GlanceView(this, _dev) { VersionText = Updates.VersionText };
        scroll.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _mainScroll = scroll;
        // «Настройки»: a second hand-drawn page on top, slid in from the right by the gear in the header
        _setScroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false, Visibility = ViewStates.Gone };
        _settings = new SettingsView(this) { VersionText = Updates.VersionText };
        _setScroll.AddView(_settings, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(_setScroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(root);

        _view.TopInset = _settings.TopInset = SystemBar("status_bar_height");
        _view.BottomInset = _settings.BottomInset = SystemBar("navigation_bar_height");
        _view.PermissionRequested += AskPermissions;
        _view.UpdateRequested += OpenUpdate;
        _view.PopupAllowed = ConnectPopup.Allowed(this);
        _view.PopupPermissionRequested += OpenOverlaySettings;
        _view.LowAt = UserPrefs.LowAt(this);
        _view.SettingsRequested += () => ShowSettings(true);

        _settings.ShadeOn = GlanceService.Enabled(this);
        _settings.SoundsOn = Sounds.Enabled(this);
        _settings.NotifyOn = UserPrefs.Notify(this);
        _settings.PopupAllowed = ConnectPopup.Allowed(this);
        _settings.LowAt = UserPrefs.LowAt(this);
        _settings.Theme = UserPrefs.Theme(this);
        _settings.BackRequested += () => ShowSettings(false);
        _settings.PopupRequested += OpenOverlaySettings;
        _settings.SourceRequested += () => OpenUrl("https://github.com/ddedlish/Glance");
        _settings.AuthorRequested += () => OpenUrl("https://steamcommunity.com/id/ddedlish/");
        _settings.DonateRequested += () => OpenUrl("https://boosty.to/ddedlish");
        _settings.WidgetCount = GlanceWidget.Count(this);
        _settings.WidgetRequested += () =>
        {
            // the system sheet «Добавить виджет» — or how to do it by hand where the launcher has no such sheet
            if (!GlanceWidget.RequestPin(this))
                Toast.MakeText(this, "Удерживайте пустое место на рабочем столе → «Виджеты» → Glance", ToastLength.Long)?.Show();
        };
        _settings.SoundsToggled += on =>
        {
            Sounds.SetEnabled(this, on);
            if (on) Sounds.Play(this, Cue.Connect, preview: true); // a taste of the chime
        };
        _settings.ShadeToggled += on =>
        {
            GlanceService.SetEnabled(this, on);
            if (on && _dev?.Demo == true) GlanceService.Start(this, true);
            if (on && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
                AskPermissions();
        };
        _settings.NotifyToggled += on =>
        {
            UserPrefs.SetNotify(this, on);
            if (on && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
                AskPermissions();
        };
        _settings.LowAtChanged += v =>
        {
            UserPrefs.SetLowAt(this, v);
            GlanceWidget.Redraw(this);
            if (_view is not null) { _view.LowAt = v; _view.Invalidate(); }
            if (Sounds.Enabled(this)) Sounds.Play(this, Cue.Low, preview: true); // hear what will sound at that level
        };
        _settings.ThemeChanged += t =>
        {
            UserPrefs.SetTheme(this, t);
            ApplyTheme();
            GlanceWidget.Redraw(this);
        };
        _dev.Changed += () => { Forecast.Record(_dev); _view?.RequestLayout(); _view?.Invalidate(); };
        _settings.AutoUpdateOn = UserPrefs.AutoUpdate(this);
        _settings.AutoUpdateToggled += on =>
        {
            UserPrefs.SetAutoUpdate(this, on);
            if (on) { GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.PutLong("lastUpdateCheck", 0)?.Apply(); _ = CheckUpdatesAsync(); }
        };
        Updater.Changed += OnUpdaterChanged;
        Updater.Cleanup();
        if (Updater.Target is { } pending) _view.Update = pending; // a download started earlier keeps going

        if (Intent?.GetBooleanExtra("demo", false) == true) _dev.UseDemo();
        if (Intent?.GetStringExtra("fakeupdate") is { } fake && Version.TryParse(fake, out var fv))
        {
            _view.Update = new UpdateInfo(fv, Intent?.GetStringExtra("fakeurl") ?? "https://github.com/ddedlish/Glance-Releases/releases", "");
            // self-test: the banner mid-download (frozen, no network)
            float fp = Intent?.GetFloatExtra("fakeprogress", -1) ?? -1;
            if (fp >= 0) Updater.Demo(_view.Update, fp, Intent?.GetStringExtra("fakestage") == "installing" ? UpdateStage.Installing : UpdateStage.Downloading);
            else if (Intent?.GetStringExtra("fakeurl") is not null) Updater.Go(this, _view.Update); // a real download and check
        }
        ApplyTheme();
        if (Intent?.GetBooleanExtra("settings", false) == true) ShowSettings(true, animate: false);
        if (Intent?.GetBooleanExtra("widget", false) == true) ShowWidgetPreview(root);

        if (!_dev.Demo && (_dev.MissingPermission || (GlanceService.Enabled(this) && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted))) AskPermissions();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Visible = true;
        _dev?.Start();
        _view?.Replay();
        if (_dev is not null) GlanceService.Start(this, _dev.Demo, Intent?.GetBooleanExtra("popup", false) == true, Intent?.GetBooleanExtra("notice", false) == true);
        if (_view is not null) { _view.PopupAllowed = ConnectPopup.Allowed(this); _view.RequestLayout(); _view.Invalidate(); }
        if (_settings is not null)
        {
            _settings.ShadeOn = GlanceService.Enabled(this); // the shade may have been dismissed from the notification
            _settings.PopupAllowed = ConnectPopup.Allowed(this);
            _settings.WidgetCount = GlanceWidget.Count(this); // back from the home screen with the widget placed
            _settings.Invalidate();
        }
        Updater.Resume(this); // back from «Установка неизвестных приложений»
        if (Intent?.GetBooleanExtra("install", false) == true) { Intent?.RemoveExtra("install"); _ = InstallFromNotice(); }
        else _ = CheckUpdatesAsync();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) Intent = intent;
    }

    protected override void OnDestroy()
    {
        Updater.Changed -= OnUpdaterChanged;
        base.OnDestroy();
    }

    void OnUpdaterChanged()
    {
        if (_view is null) return;
        if (_view.Update is null && Updater.Target is { } t) _view.Update = t;
        _view.Invalidate(); // the banner re-measures itself when it grows its water line
    }

    /// <summary>Tapped «Доступна версия X» / «скачан» in the shade: carry on with the download or the install.</summary>
    async Task InstallFromNotice()
    {
        if (_dev?.Demo == true) return;
        var u = _view?.Update ?? Updater.Target ?? await Updates.CheckAsync();
        if (u is null) return;
        RunOnUiThread(() =>
        {
            if (_view is null || IsFinishing) return;
            _view.Update = u;
            _view.RequestLayout();
            _view.Invalidate();
            Updater.Go(this, u);
        });
    }

    protected override void OnPause()
    {
        Visible = false;
        _dev?.Stop();
        Forecast.Save();
        base.OnPause();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
    }

    bool IsDark => UserPrefs.Dark(this);

    void ApplyTheme()
    {
        var p = Palette.For(IsDark);
        if (_back is not null) { _back.P = p; _back.Invalidate(); }
        _view?.SetPalette(p);
        _settings?.SetPalette(p);
        var w = Window;
        if (w is null) return;
        w.SetStatusBarColor(AColor.Transparent);
        w.SetNavigationBarColor(AColor.Transparent);
#pragma warning disable CA1422, CS0618 // edge-to-edge flags: deprecated in API 30, still honoured
        var flags = SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation;
        if (!p.Dark) flags |= SystemUiFlags.LightStatusBar | SystemUiFlags.LightNavigationBar;
        if (w.DecorView is { } decor) decor.SystemUiVisibility = (StatusBarVisibility)flags;
#pragma warning restore CA1422, CS0618
    }

    /// <summary>Self-test: the home-screen widget in three cell sizes (4×2, 4×3, 2×1) over the backdrop, as the launcher would show it.</summary>
    void ShowWidgetPreview(FrameLayout root)
    {
        if (_dev is null) return;
        float d = Resources?.DisplayMetrics?.Density ?? 2f;
        var col = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        col.SetGravity(GravityFlags.CenterHorizontal);
        col.SetPadding(0, (int)(SystemBar("status_bar_height") + 24 * d), 0, 0);
        col.SetBackgroundColor(Palette.For(IsDark).BgTop);
        int n = 0;
        foreach (var (w, h) in new[] { (300f, 180f), (300f, 250f), (150f, 70f) })
        {
            var iv = new ImageView(this);
            iv.SetImageBitmap(GlanceWidget.Art(this, _dev.Connected, w, h));
            var lp = new LinearLayout.LayoutParams((int)(w * d), (int)(h * d)) { BottomMargin = (int)(16 * d) };
            col.AddView(iv, lp);
            n++;
        }
        root.AddView(col, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        Android.Util.Log.Info("Glance", $"widget preview: {n}");
    }

    // ───────── settings page ─────────

    /// <summary>Slides «Настройки» in from the right (the main page dims and steps back) or out again.</summary>
    void ShowSettings(bool show, bool animate = true)
    {
        if (_setScroll is null || _mainScroll is null || _settings is null || show == _settingsShown) return;
        _settingsShown = show;
        float w = Math.Max(1, _mainScroll.Width > 0 ? _mainScroll.Width : Resources?.DisplayMetrics?.WidthPixels ?? 1080);
        _setScroll.Animate()?.Cancel();
        _mainScroll.Animate()?.Cancel();
        if (show)
        {
            _settings.Replay();
            _setScroll.ScrollTo(0, 0);
            _setScroll.Visibility = ViewStates.Visible;
            if (animate)
            {
                _setScroll.TranslationX = w * 0.35f;
                _setScroll.Alpha = 0;
                _setScroll.Animate()!.TranslationX(0)!.Alpha(1)!.SetDuration(340)!.SetInterpolator(new DecelerateInterpolator(2.2f))!.Start();
                _mainScroll.Animate()!.Alpha(0)!.ScaleX(0.94f)!.ScaleY(0.94f)!.SetDuration(300)!
                    .WithEndAction(new Java.Lang.Runnable(() => { if (_settingsShown && _mainScroll is not null) _mainScroll.Visibility = ViewStates.Invisible; }))!.Start();
            }
            else
            {
                _setScroll.TranslationX = 0;
                _setScroll.Alpha = 1;
                _mainScroll.Visibility = ViewStates.Invisible;
            }
            RegisterBack(true);
        }
        else
        {
            _mainScroll.Visibility = ViewStates.Visible;
            _view?.Replay();
            _mainScroll.Animate()!.Alpha(1)!.ScaleX(1)!.ScaleY(1)!.SetDuration(320)!.SetInterpolator(new DecelerateInterpolator(2f))!.Start();
            _setScroll.Animate()!.TranslationX(w * 0.35f)!.Alpha(0)!.SetDuration(260)!.SetInterpolator(new AccelerateInterpolator(1.5f))!
                .WithEndAction(new Java.Lang.Runnable(() => { if (!_settingsShown && _setScroll is not null) _setScroll.Visibility = ViewStates.Gone; }))!.Start();
            RegisterBack(false);
        }
        Android.Util.Log.Info("Glance", show ? "settings: open" : "settings: closed");
    }

    /// <summary>Back gesture closes Settings: a dispatcher callback where predictive back is on (API 33+), OnBackPressed otherwise.</summary>
    void RegisterBack(bool on)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            var d = OnBackInvokedDispatcher;
            if (on) { _backCb ??= new BackCallback(() => ShowSettings(false)); d.RegisterOnBackInvokedCallback(0, _backCb); }
            else if (_backCb is not null) d.UnregisterOnBackInvokedCallback(_backCb);
        }
        catch (Exception e) { Log.Error("Android back callback", e); }
    }

#pragma warning disable CS0672, CS0618, CA1422 // OnBackPressed: deprecated in API 33, still called unless predictive back is enabled
    public override void OnBackPressed()
    {
        if (_settingsShown) { ShowSettings(false); return; }
        base.OnBackPressed();
    }
#pragma warning restore CS0672, CS0618, CA1422

    sealed class BackCallback : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        readonly Action _act;
        public BackCallback(Action act) => _act = act;
        public void OnBackInvoked() => _act();
    }

    void OpenOverlaySettings()
    {
        try
        {
            var i = new Intent(Android.Provider.Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(i);
        }
        catch (Exception e) { Log.Error("Android overlay settings", e); }
    }

    void OpenUrl(string url)
    {
        try
        {
            var i = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
            i.AddFlags(ActivityFlags.NewTask);
            StartActivity(i);
        }
        catch (Exception e) { Log.Error("Android open url", e); }
    }

    float SystemBar(string name)
    {
        try
        {
            int id = Resources!.GetIdentifier(name, "dimen", "android");
            return id > 0 ? Resources.GetDimensionPixelSize(id) : 0;
        }
        catch { return 0; }
    }

    void AskPermissions()
    {
        var perms = Devices.NeededPermissions.ToList();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) perms.Add("android.permission.POST_NOTIFICATIONS"); // the shade notification
        try { RequestPermissions(perms.ToArray(), PermRequest); }
        catch (Exception e) { Log.Error("Android permissions", e); }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != PermRequest) return;
        _dev?.Stop();
        _dev?.Start();
        if (_dev is not null) GlanceService.Start(this, _dev.Demo);
    }

    async Task CheckUpdatesAsync()
    {
        if (_dev?.Demo == true || _view is null || _view.Update is not null || !UserPrefs.AutoUpdate(this)) return;
        var prefs = GetSharedPreferences(Prefs, FileCreationMode.Private);
        long last = prefs?.GetLong("lastUpdateCheck", 0) ?? 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - last < 3 * 3600) return;
        prefs?.Edit()?.PutLong("lastUpdateCheck", now)?.Apply();
        var u = await Updates.CheckAsync();
        if (u is null || _view is null) return;
        RunOnUiThread(() =>
        {
            if (_view is null) return;
            _view.Update = u;
            _view.RequestLayout();
            _view.Invalidate();
            Updater.Prefetch(u); // on Wi-Fi the APK is ready before the banner is even tapped
        });
    }

    void OpenUpdate()
    {
        if (_view?.Update is not { } u) return;
        // Glance downloads the APK itself, checks it and hands it to Android's installer (same signature: settings stay)
        Updater.Go(this, u);
        _view.Invalidate();
    }
}
