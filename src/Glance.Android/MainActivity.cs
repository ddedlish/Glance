using Android.App;
using Android.Widget;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AColor = Android.Graphics.Color;

namespace Glance.Droid;

[Activity(Label = "Glance", Name = "io.github.ddedlish.glance.MainActivity", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.Material.NoActionBar", LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.UiMode | ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : Activity
{
    Devices? _dev;
    GlanceView? _view;
    Backdrop? _back;
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
        SetContentView(root);

        _view.TopInset = SystemBar("status_bar_height");
        _view.BottomInset = SystemBar("navigation_bar_height");
        _view.PermissionRequested += AskPermissions;
        _view.UpdateRequested += OpenUpdate;
        _view.ShadeOn = GlanceService.Enabled(this);
        _view.ShadeToggled += on =>
        {
            GlanceService.SetEnabled(this, on);
            if (on && _dev?.Demo == true) GlanceService.Start(this, true);
            if (on && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
                AskPermissions();
        };
        _dev.Changed += () => { _view?.RequestLayout(); _view?.Invalidate(); };

        if (Intent?.GetBooleanExtra("demo", false) == true) _dev.UseDemo();
        if (Intent?.GetStringExtra("fakeupdate") is { } fake && Version.TryParse(fake, out var fv))
            _view.Update = new UpdateInfo(fv, "https://github.com/ddedlish/Glance-Releases/releases", "");
        ApplyTheme();

        if (!_dev.Demo && (_dev.MissingPermission || (GlanceService.Enabled(this) && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted))) AskPermissions();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _dev?.Start();
        _view?.Replay();
        if (_dev is not null) GlanceService.Start(this, _dev.Demo);
        if (_view is not null) { _view.ShadeOn = GlanceService.Enabled(this); _view.Invalidate(); }
        _ = CheckUpdatesAsync();
    }

    protected override void OnPause()
    {
        _dev?.Stop();
        base.OnPause();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
    }

    bool IsDark => ((Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;

    void ApplyTheme()
    {
        var p = Palette.For(IsDark);
        if (_back is not null) { _back.P = p; _back.Invalidate(); }
        _view?.SetPalette(p);
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
        if (_dev?.Demo == true || _view is null || _view.Update is not null) return;
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
        });
    }

    void OpenUpdate()
    {
        if (_view?.Update is not { } u) return;
        try
        {
            // The browser downloads the APK; Android then offers to install it over this version (same signature).
            var i = new Intent(Intent.ActionView, Android.Net.Uri.Parse(u.Url));
            i.AddFlags(ActivityFlags.NewTask);
            StartActivity(i);
        }
        catch (Exception e) { Log.Error("Android update open", e); }
    }
}
