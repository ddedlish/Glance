using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AColor = Android.Graphics.Color;

namespace Glance.Droid;

/// <summary>
/// «Окно при подключении»: the moment an accessory connects, a glass card slides up from the bottom over any app
/// (like AirPods on iPhone) with live levels — per bud and the case for earbuds. Tap opens Glance, swipe or wait
/// 7 s to hide. Needs «Поверх других приложений»; without it a heads-up notification is shown instead.
/// </summary>
internal static class ConnectPopup
{
    static PopupView? _view;
    static IWindowManager? _wm;

    public static bool Allowed(Context c) => Android.Provider.Settings.CanDrawOverlays(c);

    public static bool Show(Context ctx, Accessory a)
    {
        if (!Allowed(ctx)) { Android.Util.Log.Info("Glance", "popup: not allowed (overlay permission)"); return false; }
        try
        {
            if (_view is not null && _view.Key == a.Key) { _view.Bump(); return true; }
            Hide(immediate: true);
            var svc = ctx.GetSystemService(Context.WindowService);
            _wm = svc is null ? null : Android.Runtime.Extensions.JavaCast<IWindowManager>(svc);
            if (_wm is null) return false;
            var dm = ctx.Resources!.DisplayMetrics!;
            float d = dm.Density;
            int w = (int)Math.Min(dm.WidthPixels - 24 * d, 440 * d);
            PopupView? view = null;
            view = new PopupView(ctx, a, () => Remove(view!));
            var lp = new WindowManagerLayoutParams(w, (int)(view.CardHeightDp * d + 24 * d),
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.HardwareAccelerated,
                Format.Translucent)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
                Y = (int)(24 * d),
            };
            _wm.AddView(view, lp);
            _view = view;
            Android.Util.Log.Info("Glance", $"popup shown: {a.DisplayName}");
            return true;
        }
        catch (Exception e) { Log.Error("Android popup", e); Android.Util.Log.Error("Glance", "popup failed: " + e); _view = null; return false; }
    }

    /// <summary>Levels changed: redraw the card if it shows this device.</summary>
    public static void Refresh() => _view?.Invalidate();

    public static void Hide(bool immediate = false)
    {
        if (_view is { } v) Remove(v);
    }

    static void Remove(PopupView v)
    {
        if (ReferenceEquals(_view, v)) _view = null;
        Android.Util.Log.Info("Glance", $"popup removed after {SystemClock.UptimeMillis() - v.Born} ms");
        try { _wm?.RemoveViewImmediate(v); } catch { }
    }
}

internal sealed class PopupView : View
{
    readonly Accessory _a;
    readonly Action _remove;
    readonly float _d;
    readonly Palette _p;
    long _t0 = SystemClock.UptimeMillis(), _hideAt;
    bool _leaving;
    long _leaveT0;
    readonly Dictionary<string, (float From, float To, long T0)> _tw = new();

    const int ShowMs = 7000;
    public string Key => _a.Key;
    public long Born => _t0;
    public float CardHeightDp => _a.HasParts || _a.Profile.Buds != BudsSource.None ? 178 : 96;

    public PopupView(Context c, Accessory a, Action remove) : base(c)
    {
        _a = a;
        _remove = remove;
        _d = c.Resources!.DisplayMetrics!.Density;
        _p = Palette.For(UserPrefs.Dark(c));
        _hideAt = _t0 + ShowMs;
        Watchdog();
    }

    /// <summary>Removes the card even if Android stops sending frames to it (screen off, app in background).</summary>
    void Watchdog() => PostDelayed(() =>
    {
        if (!IsAttachedToWindow) return;
        if (Now >= _hideAt + 1200 && _hideAt != long.MaxValue) _remove();
        else { Invalidate(); Watchdog(); }
    }, 1000);

    /// <summary>Shown again for the same device (reconnect, case opened): stay longer and wobble.</summary>
    public void Bump()
    {
        _hideAt = SystemClock.UptimeMillis() + ShowMs;
        _leaving = false;
        Invalidate();
    }

    float Dp(float v) => v * _d;
    static long Now => SystemClock.UptimeMillis();

    float Tween(string key, float target)
    {
        long now = Now;
        // counted from the card's creation, so even a late first frame shows the right numbers
        if (!_tw.TryGetValue(key, out var t)) { t = (0, target, _t0 + 250); _tw[key] = t; }
        else if (Math.Abs(t.To - target) > 0.01f) { t = (Val(t, now), target, now); _tw[key] = t; }
        return Val(t, now);
    }

    static float Val((float From, float To, long T0) t, long now)
    {
        float k = Math.Clamp((now - t.T0) / 650f, 0, 1);
        return t.From + (t.To - t.From) * (1 - MathF.Pow(1 - k, 3));
    }

    protected override void OnDraw(Canvas canvas)
    {
        long now = Now;
        if (!_leaving && now >= _hideAt) { _leaving = true; _leaveT0 = now; }
        float h = Dp(CardHeightDp);
        // slide up with a soft overshoot; slide down when leaving
        float inK = Math.Clamp((now - _t0) / 420f, 0, 1);
        float back = 1.6f;
        float ease = 1 + (back + 1) * MathF.Pow(inK - 1, 3) + back * MathF.Pow(inK - 1, 2);
        float dy = (1 - ease) * (h + Dp(30));
        int alpha = 255;
        if (_leaving)
        {
            float k = Math.Clamp((now - _leaveT0) / 300f, 0, 1);
            dy = k * k * (h + Dp(30));
            alpha = (int)(255 * (1 - k));
            if (k >= 1) { Post(_remove); return; }
        }
        int save = canvas.SaveLayerAlpha(0, 0, Width, Height, alpha);
        canvas.Translate(0, dy + _dragY);
        var r = new RectF(Dp(2), Dp(2), Width - Dp(2), Dp(2) + h);
        DrawCard(canvas, r, now);
        canvas.RestoreToCount(save);
        PostInvalidateOnAnimation();
    }

    void DrawCard(Canvas c, RectF r, long now)
    {
        float rad = Dp(28);
        using (var shadow = new Paint(PaintFlags.AntiAlias) { Color = AColor.Argb(_p.Dark ? 120 : 50, 0, 0, 0) })
        {
            shadow.SetMaskFilter(new BlurMaskFilter(Dp(10), BlurMaskFilter.Blur.Normal!));
            c.DrawRoundRect(new RectF(r.Left + Dp(4), r.Top + Dp(6), r.Right - Dp(4), r.Bottom + Dp(4)), rad, rad, shadow);
        }
        using (var bg = new Paint(PaintFlags.AntiAlias))
        {
            bg.SetShader(new LinearGradient(0, r.Top, 0, r.Bottom,
                _p.Dark ? AColor.Argb(245, 40, 42, 62) : AColor.Argb(250, 255, 255, 255),
                _p.Dark ? AColor.Argb(245, 24, 24, 40) : AColor.Argb(250, 238, 241, 252), Shader.TileMode.Clamp!));
            c.DrawRoundRect(r, rad, rad, bg);
        }
        // a glint sweeps across the glass once the card has arrived («перелив»)
        float gk = ((now - _t0 - 350) % 2600) / 900f;
        if (gk is > 0 and < 1)
        {
            float x = r.Left + (r.Width() + Dp(160)) * gk - Dp(80);
            using var g = new Paint(PaintFlags.AntiAlias);
            g.SetShader(new LinearGradient(x - Dp(70), r.Top, x + Dp(70), r.Bottom,
                new[] { AColor.Argb(0, 255, 255, 255).ToArgb(), AColor.Argb(_p.Dark ? 34 : 90, 255, 255, 255).ToArgb(), AColor.Argb(0, 255, 255, 255).ToArgb() },
                null, Shader.TileMode.Clamp!));
            c.DrawRoundRect(r, rad, rad, g);
        }
        using (var b = new Paint(PaintFlags.AntiAlias) { Color = _p.Dark ? AColor.Argb(60, 255, 255, 255) : AColor.Argb(255, 255, 255, 255), StrokeWidth = Dp(1) })
        {
            b.SetStyle(Paint.Style.Stroke);
            c.DrawRoundRect(r, rad, rad, b);
        }

        // header: tile, name, status, overall level
        var tile = new RectF(r.Left + Dp(18), r.Top + Dp(18), r.Left + Dp(62), r.Top + Dp(62));
        var col = Palette.KindColor(_a.Kind);
        using (var tp = new Paint(PaintFlags.AntiAlias))
        {
            tp.SetShader(new LinearGradient(tile.Left, tile.Top, tile.Right, tile.Bottom, Palette.Mix(col, AColor.White, 0.18f), Palette.Mix(col, AColor.Black, 0.12f), Shader.TileMode.Clamp!));
            c.DrawRoundRect(tile, Dp(13), Dp(13), tp);
        }
        Icons.Draw(c, _a.Kind, new RectF(tile.Left + Dp(9), tile.Top + Dp(9), tile.Right - Dp(9), tile.Bottom - Dp(9)), AColor.White, Dp(2));

        float tx = tile.Right + Dp(14);
        int? lvl = _a.Level;
        string pct = lvl is int l ? $"{(int)MathF.Round(Tween("all", l))}%" : "";
        using var big = Txt(Dp(24), true, _p.Text);
        big.TextAlign = Paint.Align.Right;
        float pw = pct.Length > 0 ? big.MeasureText(pct) + Dp(8) : 0;
        if (pct.Length > 0) c.DrawText(pct, r.Right - Dp(20), r.Top + Dp(48), big);
        using var name = Txt(Dp(17), true, _p.Text);
        c.DrawText(Fit(_a.DisplayName, name, r.Right - Dp(20) - pw - tx), tx, r.Top + Dp(37), name);
        using var sub = Txt(Dp(13), false, _p.Sub);
        string status = (_a.Charging ? "Заряжается · " : "Подключено · ") + _a.Link;
        c.DrawText(Fit(status, sub, r.Right - Dp(20) - pw - tx), tx, r.Top + Dp(57), sub);

        if (CardHeightDp < 120) return;

        // earbuds: left / right / case rings, filled live as the adverts arrive
        var parts = new[] { ("Левый", _a.Left, "l"), ("Правый", _a.Right, "r"), ("Кейс", _a.Case, "c") };
        float top = r.Top + Dp(82), cw = (r.Width() - Dp(36)) / 3f;
        for (int i = 0; i < 3; i++)
        {
            var (label, part, key) = parts[i];
            float cx = r.Left + Dp(18) + cw * i + cw / 2, cy = top + Dp(30), rr = Dp(26);
            var box = new RectF(cx - rr, cy - rr, cx + rr, cy + rr);
            using var ring = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Dp(6), StrokeCap = Paint.Cap.Round, Color = _p.Track };
            ring.SetStyle(Paint.Style.Stroke);
            c.DrawOval(box, ring);
            using var num = Txt(Dp(16), true, _p.Text);
            num.TextAlign = Paint.Align.Center;
            if (part.Level is int pl)
            {
                float shown = Tween(key, pl);
                ring.Color = Palette.Level(pl, part.Charging);
                c.DrawArc(box, -90, 360 * shown / 100f, false, ring);
                c.DrawText($"{(int)MathF.Round(shown)}", cx, cy + Dp(6), num);
                if (part.Charging) Icons.Bolt(c, new RectF(cx + rr - Dp(4), cy - rr - Dp(6), cx + rr + Dp(10), cy - rr + Dp(8)), Palette.Green);
            }
            else
            {
                // waiting for the earbuds' broadcast: a dot travels around the ring
                float a0 = (Now % 1200) / 1200f * 360f;
                ring.Color = Palette.Alpha(_p.Sub, 160);
                c.DrawArc(box, a0 - 90, 40, false, ring);
                num.Color = _p.Sub;
                c.DrawText("—", cx, cy + Dp(6), num);
            }
            using var lab = Txt(Dp(12.5f), false, _p.Sub);
            lab.TextAlign = Paint.Align.Center;
            c.DrawText(label, cx, cy + rr + Dp(20), lab);
        }
    }

    static Paint Txt(float size, bool bold, AColor color)
    {
        var p = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText) { TextSize = size, Color = color };
        p.SetTypeface(Typeface.Create(bold ? "sans-serif-medium" : "sans-serif", TypefaceStyle.Normal));
        return p;
    }

    static string Fit(string s, Paint p, float maxW)
    {
        if (maxW <= 0) return "";
        if (p.MeasureText(s) <= maxW) return s;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (p.MeasureText(s[..mid].TrimEnd() + "…") <= maxW) lo = mid; else hi = mid - 1;
        }
        return s[..lo].TrimEnd() + "…";
    }

    // ───────── touch: tap opens Glance, swipe down hides ─────────

    float _downY, _dragY;

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.Action)
        {
            case MotionEventActions.Down:
                _downY = e.RawY;
                _hideAt = long.MaxValue; // held: don't hide under the finger
                return true;
            case MotionEventActions.Move:
                _dragY = Math.Max(0, e.RawY - _downY);
                Invalidate();
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                if (_dragY > Dp(40)) { _leaving = true; _leaveT0 = Now; }
                else if (e.Action == MotionEventActions.Up && Math.Abs(e.RawY - _downY) < Dp(10))
                {
                    try
                    {
                        var i = new Intent(Context, typeof(MainActivity));
                        i.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
                        Context!.StartActivity(i);
                    }
                    catch (Exception ex) { Log.Error("Android popup open", ex); }
                    _leaving = true; _leaveT0 = Now;
                }
                else _hideAt = Now + 3000;
                _dragY = 0;
                Invalidate();
                return true;
        }
        return true;
    }
}
