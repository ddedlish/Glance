using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace Glance.Droid;

/// <summary>
/// «Glance» on the home screen: a resizable Liquid Glass pane with the charge of every connected accessory
/// (left, right and case for earbuds), weakest first. Drawn by ShadeArt as one picture for the cell size;
/// GlanceService pushes a fresh picture whenever the levels change, so the widget follows the shade in real time
/// and costs nothing when no widget is placed. Tapping it opens Glance.
/// </summary>
[BroadcastReceiver(Name = "io.github.ddedlish.glance.GlanceWidget", Label = "Glance", Exported = true)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/glance_widget_info")]
public sealed class GlanceWidget : AppWidgetProvider
{
    static IReadOnlyList<Accessory>? _last;
    static string? _note;
    static float _phase;

    /// <summary>New levels from the service (null: the service stopped, the widget says how to bring it back).</summary>
    public static void Push(Context c, IReadOnlyList<Accessory>? all)
    {
        _last = all?.ToList();
        _note = all is null ? "Включите «Заряд в шторке» в Glance" : null;
        Redraw(c);
    }

    /// <summary>Redraws every placed widget (theme switched, levels changed). Quietly does nothing when there are none.</summary>
    public static void Redraw(Context c)
    {
        try
        {
            var mgr = AppWidgetManager.GetInstance(c);
            var ids = mgr?.GetAppWidgetIds(new ComponentName(c, Java.Lang.Class.FromType(typeof(GlanceWidget))));
            if (mgr is null || ids is null || ids.Length == 0) return;
            _phase += 0.9f; // the liquid and the blobs move on a little with every update: the pane looks alive
            foreach (int id in ids) Draw(c, mgr, id);
        }
        catch (Exception e) { Log.Error("Android widget redraw", e); }
    }

    /// <summary>Number of Glance widgets on the home screen (for «Настройки»).</summary>
    public static int Count(Context c)
    {
        try { return AppWidgetManager.GetInstance(c)?.GetAppWidgetIds(new ComponentName(c, Java.Lang.Class.FromType(typeof(GlanceWidget))))?.Length ?? 0; }
        catch { return 0; }
    }

    /// <summary>Asks the launcher to place the widget (Android 8+: the system «Добавить виджет» sheet). False when it cannot.</summary>
    public static bool RequestPin(Context c)
    {
        try
        {
            var mgr = AppWidgetManager.GetInstance(c);
            if (mgr is null || !mgr.IsRequestPinAppWidgetSupported) return false;
            return mgr.RequestPinAppWidget(new ComponentName(c, Java.Lang.Class.FromType(typeof(GlanceWidget))), null, null);
        }
        catch (Exception e) { Log.Error("Android widget pin", e); return false; }
    }

    /// <summary>Cell size in dp: the width of the portrait cell and its height (launchers report min/max pairs).</summary>
    static (float w, float h) Size(AppWidgetManager mgr, int id)
    {
        try
        {
            var o = mgr.GetAppWidgetOptions(id);
            int w = o?.GetInt(AppWidgetManager.OptionAppwidgetMinWidth) ?? 0;
            int h = o?.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight) ?? 0;
            if (w > 0 && h > 0) return (w, h);
        }
        catch { }
        return (300, 150);
    }

    /// <summary>The picture for one size and theme (also used by the self-test to show the widget on screen).</summary>
    public static Android.Graphics.Bitmap Art(Context c, IReadOnlyList<Accessory> all, float wDp, float hDp, string? note = null) =>
        ShadeArt.Widget(all, UserPrefs.Dark(c), wDp, hDp, c.Resources?.DisplayMetrics?.Density ?? 2f, UserPrefs.LowAt(c), note, _phase);

    static void Draw(Context c, AppWidgetManager mgr, int id)
    {
        int layout = c.Resources?.GetIdentifier("glance_widget", "layout", c.PackageName) ?? 0;
        int img = c.Resources?.GetIdentifier("widget_art", "id", c.PackageName) ?? 0;
        if (layout == 0 || img == 0) return;
        var (w, h) = Size(mgr, id);
        var all = _last ?? Array.Empty<Accessory>();
        string? note = _note ?? (_last is null ? (GlanceService.Enabled(c) ? "Обновляется…" : "Включите «Заряд в шторке» в Glance") : null);
        var rv = new RemoteViews(c.PackageName, layout);
        rv.SetImageViewBitmap(img, Art(c, all, w, h, note));
        rv.SetContentDescription(img, note ?? (all.Count == 0 ? "Нет подключённых аксессуаров"
            : string.Join("; ", all.Select(a => a.WeakestLevel is int l ? $"{a.DisplayName} {l}%" : a.DisplayName))));
        var open = new Intent(c, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        rv.SetOnClickPendingIntent(img, PendingIntent.GetActivity(c, 4, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        mgr.UpdateAppWidget(id, rv);
        Android.Util.Log.Info("Glance", $"widget: {id} {w}x{h} {all.Count}");
    }

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (int id in appWidgetIds)
            try { Draw(context, appWidgetManager, id); } catch (Exception e) { Log.Error("Android widget update", e); }
        // just placed or the process was gone: the service brings the live levels within a moment
        if (_last is null) GlanceService.Start(context, false);
    }

    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        // resized: redraw sharp for the new cell (rows ↔ chips)
        if (context is null || appWidgetManager is null) return;
        try { Draw(context, appWidgetManager, appWidgetId); } catch (Exception e) { Log.Error("Android widget resize", e); }
    }
}
