using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AColor = Android.Graphics.Color;
using Path = Android.Graphics.Path;

namespace Glance.Droid;

/// <summary>User settings kept in SharedPreferences «glance» (next to «shade» and «sounds»).</summary>
internal static class UserPrefs
{
    const string Name = "glance", PrefLowAt = "lowAt", PrefTheme = "theme", PrefNotify = "batteryNotify", PrefAutoUpdate = "autoUpdate";
    public static readonly int[] LowSteps = { 10, 15, 20, 25, 30 };

    static ISharedPreferences? P(Context c) => c.GetSharedPreferences(Name, FileCreationMode.Private);

    /// <summary>«Низкий заряд»: the level at which Glance says «Пора зарядить» and plays the low chime (default 20 %).</summary>
    public static int LowAt(Context c) => Math.Clamp(P(c)?.GetInt(PrefLowAt, 20) ?? 20, 5, 50);
    public static void SetLowAt(Context c, int v) => P(c)?.Edit()?.PutInt(PrefLowAt, v)?.Apply();

    /// <summary>«Уведомления о заряде»: «пора зарядить» and «заряжено полностью» in the shade, once per cycle (default on).</summary>
    public static bool Notify(Context c) => P(c)?.GetBoolean(PrefNotify, true) ?? true;
    public static void SetNotify(Context c, bool on) => P(c)?.Edit()?.PutBoolean(PrefNotify, on)?.Apply();

    /// <summary>«Проверять обновления»: on opening Glance and every few hours from the shade service (default on).</summary>
    public static bool AutoUpdate(Context c) => P(c)?.GetBoolean(PrefAutoUpdate, true) ?? true;
    public static void SetAutoUpdate(Context c, bool on) => P(c)?.Edit()?.PutBoolean(PrefAutoUpdate, on)?.Apply();

    /// <summary>0 — as the system, 1 — dark, 2 — light.</summary>
    public static int Theme(Context c) => Math.Clamp(P(c)?.GetInt(PrefTheme, 0) ?? 0, 0, 2);
    public static void SetTheme(Context c, int v) => P(c)?.Edit()?.PutInt(PrefTheme, v)?.Apply();

    public static bool SystemDark(Context c) =>
        ((c.Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;

    public static bool Dark(Context c) => Theme(c) switch { 1 => true, 2 => false, _ => SystemDark(c) };
}

/// <summary>Shared drawing for the hand-drawn screens: glass cards, text, switches, entrance animation.</summary>
internal abstract class GlassView : View
{
    protected readonly float _d;  // px per dp
    protected Palette _p = Palette.For(true);
    protected long _born = SystemClock.UptimeMillis();
    protected const float Side = 16;

    public float TopInset { get; set; }
    public float BottomInset { get; set; }

    protected GlassView(Context c) : base(c) => _d = c.Resources!.DisplayMetrics!.Density;

    public void SetPalette(Palette p) { _p = p; Invalidate(); }

    protected float Dp(float v) => v * _d;
    protected static long Now => SystemClock.UptimeMillis();
    protected static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);
    protected static RectF Grow(RectF r, float by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    /// <summary>0…1 entrance of the item at index i (cards rise one after another).</summary>
    protected float Arrive(int i)
    {
        float ms = Now - _born - 80 - i * 60;
        return Ease(ms / 420f);
    }

    /// <summary>Draws an item with its entrance offset and fade.</summary>
    protected void Shift(Canvas c, int i, Action draw)
    {
        float k = Arrive(i);
        if (k <= 0) return;
        if (k >= 1) { draw(); return; }
        int save = c.SaveLayerAlpha(0, 0, Width, Height, (int)(255 * k));
        c.Translate(0, (1 - k) * Dp(24));
        draw();
        c.RestoreToCount(save);
    }

    protected void Section(Canvas c, string title, float cardTop) =>
        Text(c, title.ToUpperInvariant(), Dp(Side + 6), cardTop - Dp(10), Dp(12), _p.Sub, bold: true, spacing: 0.06f);

    protected void Glass(Canvas c, RectF r, float radius)
    {
        using var p = new Paint(PaintFlags.AntiAlias) { Color = _p.Card };
        c.DrawRoundRect(r, radius, radius, p);
        // specular sheen over the top half
        using var sheen = new Paint(PaintFlags.AntiAlias);
        sheen.SetShader(new LinearGradient(0, r.Top, 0, r.Top + r.Height() * 0.55f,
            AColor.Argb(_p.Dark ? 30 : 110, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
        c.DrawRoundRect(r, radius, radius, sheen);
        using var b = new Paint(PaintFlags.AntiAlias) { Color = _p.CardBorder, StrokeWidth = Math.Max(1, Dp(1)) };
        b.SetStyle(Paint.Style.Stroke);
        var rr = new RectF(r.Left + b.StrokeWidth / 2, r.Top + b.StrokeWidth / 2, r.Right - b.StrokeWidth / 2, r.Bottom - b.StrokeWidth / 2);
        c.DrawRoundRect(rr, radius, radius, b);
    }

    protected void Chevron(Canvas c, float x, float y, AColor color)
    {
        using var p = new Paint(PaintFlags.AntiAlias) { Color = color, StrokeWidth = Dp(2), StrokeCap = Paint.Cap.Round };
        c.DrawLine(x - Dp(4), y - Dp(6), x + Dp(2), y, p);
        c.DrawLine(x + Dp(2), y, x - Dp(4), y + Dp(6), p);
    }

    /// <summary>A gear: eight rounded teeth around a ring, turned by angle degrees.</summary>
    protected static void Gear(Canvas c, float cx, float cy, float r, float angle, AColor color)
    {
        using var p = new Paint(PaintFlags.AntiAlias) { Color = color };
        c.Save();
        c.Rotate(angle, cx, cy);
        for (int i = 0; i < 8; i++)
        {
            c.Save();
            c.Rotate(i * 45, cx, cy);
            c.DrawRoundRect(new RectF(cx - r * 0.2f, cy - r, cx + r * 0.2f, cy - r * 0.55f), r * 0.08f, r * 0.08f, p);
            c.Restore();
        }
        p.SetStyle(Paint.Style.Stroke);
        p.StrokeWidth = r * 0.34f;
        c.DrawCircle(cx, cy, r * 0.62f, p);
        c.Restore();
    }

    /// <summary>A coloured rounded tile with a white glyph drawn by the callback.</summary>
    protected void ColorTile(Canvas c, RectF tile, AColor col, Action<float, float> glyph)
    {
        using (var tp = new Paint(PaintFlags.AntiAlias))
        {
            tp.SetShader(new LinearGradient(tile.Left, tile.Top, tile.Right, tile.Bottom, Palette.Mix(col, AColor.White, 0.2f), Palette.Mix(col, AColor.Black, 0.1f), Shader.TileMode.Clamp!));
            c.DrawRoundRect(tile, Dp(12), Dp(12), tp);
        }
        using (var hi = new Paint(PaintFlags.AntiAlias))
        {
            hi.SetShader(new LinearGradient(0, tile.Top, 0, tile.CenterY(), AColor.Argb(60, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
            c.DrawRoundRect(tile, Dp(12), Dp(12), hi);
        }
        glyph(tile.CenterX(), tile.CenterY());
    }

    /// <summary>Title, subtitle and an iOS-style switch: the knob glides and the track fills with colour.</summary>
    protected void DrawSwitch(Canvas c, RectF r, RectF tile, string title, string subtitle, bool isOn, long t0)
    {
        float tx = tile.Right + Dp(14), sw = Dp(52), sh = Dp(32);
        Text(c, title, tx, r.Top + Dp(32), Dp(16), _p.Text, bold: true, maxW: r.Right - tx - sw - Dp(30));
        Text(c, subtitle, tx, r.Top + Dp(52), Dp(12.5f), _p.Sub, maxW: r.Right - tx - sw - Dp(30));

        float k = Ease((Now - t0) / 260f);
        float on = isOn ? k : 1 - k;
        var track = new RectF(r.Right - Dp(18) - sw, r.CenterY() - sh / 2, r.Right - Dp(18), r.CenterY() + sh / 2);
        using (var t = new Paint(PaintFlags.AntiAlias) { Color = Palette.Mix(_p.Track, Palette.Green, on) })
            c.DrawRoundRect(track, sh / 2, sh / 2, t);
        float kr = sh / 2 - Dp(3);
        float kx = track.Left + Dp(3) + kr + (track.Width() - 2 * (kr + Dp(3))) * on;
        using (var shadow = new Paint(PaintFlags.AntiAlias) { Color = AColor.Argb(50, 0, 0, 0) })
            c.DrawCircle(kx, track.CenterY() + Dp(1), kr, shadow);
        using (var knob = new Paint(PaintFlags.AntiAlias) { Color = AColor.White })
            c.DrawCircle(kx, track.CenterY(), kr, knob);
        if (k < 1) PostInvalidateOnAnimation();
    }

    // ───────── text ─────────

    protected static readonly Typeface Regular = Typeface.Create("sans-serif", TypefaceStyle.Normal)!;
    protected static readonly Typeface Bold = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal)!;

    protected static Paint TextPaint(float size, bool bold)
    {
        var p = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText) { TextSize = size };
        p.SetTypeface(bold ? Bold : Regular);
        return p;
    }

    protected void Text(Canvas c, string s, float x, float baseline, float size, AColor color, bool bold = false, float maxW = 0, float spacing = 0)
    {
        using var p = TextPaint(size, bold);
        p.Color = color;
        if (spacing > 0) p.LetterSpacing = spacing;
        c.DrawText(maxW > 0 ? Fit(s, p, maxW) : s, x, baseline, p);
    }

    protected void TextRight(Canvas c, string s, float right, float baseline, float size, AColor color, bool bold)
    {
        using var p = TextPaint(size, bold);
        p.Color = color;
        p.TextAlign = Paint.Align.Right;
        c.DrawText(s, right, baseline, p);
    }

    protected void TextCentered(Canvas c, string s, float cx, float baseline, float size, AColor color, bool bold, float maxW = 0)
    {
        using var p = TextPaint(size, bold);
        p.Color = color;
        p.TextAlign = Paint.Align.Center;
        c.DrawText(Fit(s, p, maxW > 0 ? maxW : Width - Dp(32)), cx, baseline, p);
    }

    /// <summary>Shortens text with «…» to fit the width.</summary>
    protected static string Fit(string s, Paint p, float maxW)
    {
        if (p.MeasureText(s) <= maxW) return s;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (p.MeasureText(s[..mid].TrimEnd() + "…") <= maxW) lo = mid; else hi = mid - 1;
        }
        return s[..lo].TrimEnd() + "…";
    }

    public override bool PerformClick() => base.PerformClick();
}

/// <summary>
/// «Настройки», drawn by hand in the same Liquid Glass style: the shade notification, sounds, the connect popup,
/// the low-battery threshold, the theme, and «О программе». Opened by the gear in the header; back closes it.
/// </summary>
internal sealed class SettingsView : GlassView
{
    public bool ShadeOn { get; set; } = true;
    public bool SoundsOn { get; set; } = true;
    public bool NotifyOn { get; set; } = true;
    public bool AutoUpdateOn { get; set; } = true;
    public bool PopupAllowed { get; set; } = true;
    public int WidgetCount { get; set; }
    public int LowAt { get; set; } = 20;
    public int Theme { get; set; }
    public string VersionText { get; set; } = "";

    public event Action? BackRequested, PopupRequested, SourceRequested, AuthorRequested, DonateRequested, YooMoneyRequested, WidgetRequested;
    public event Action<bool>? ShadeToggled, SoundsToggled, NotifyToggled, AutoUpdateToggled;
    public event Action<int>? LowAtChanged, ThemeChanged;

    static readonly string[] ThemeNames = { "Системная", "Тёмная", "Светлая" };

    RectF _backR = new(), _shadeR = new(), _soundR = new(), _notifyR = new(), _autoR = new(), _popR = new(), _widgetR = new(), _lowSeg = new(), _themeSeg = new(), _srcR = new(), _authorR = new(), _donateR = new(), _yooR = new();
    long _shadeT0 = -10_000, _soundT0 = -10_000, _notifyT0 = -10_000, _autoT0 = -10_000, _lowT0 = -10_000, _themeT0 = -10_000;
    float _lowFrom, _themeFrom;
    bool _drawnOnce;

    public SettingsView(Context c) : base(c) { }

    /// <summary>Cards rise again (the screen slides in).</summary>
    public void Replay() { _born = Now; Invalidate(); }

    // ───────── layout (dp) ─────────

    const float Gap = 12, RowH = 72, SegCardH = 116, AboutH = 292, Head = 28;

    float ContentHeight() => Dp(TopInset / _d + 20 + 96 + 8 * Head + 6 * (RowH + Gap) + 2 * (SegCardH + Gap) + AboutH + Gap + 40) + BottomInset;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int h = (int)ContentHeight();
        if (MeasureSpec.GetMode(heightMeasureSpec) == MeasureSpecMode.Exactly) h = Math.Max(h, MeasureSpec.GetSize(heightMeasureSpec));
        SetMeasuredDimension(w, h);
    }

    protected override void OnDraw(Canvas canvas)
    {
        float w = Width, y = TopInset + Dp(20);
        int idx = 0;

        // header: a round glass back button and a large title
        _backR = new RectF(Dp(Side), y, Dp(Side) + Dp(40), y + Dp(40));
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = _p.Card }) canvas.DrawOval(_backR, p);
        using (var b = new Paint(PaintFlags.AntiAlias) { Color = _p.CardBorder, StrokeWidth = Math.Max(1, Dp(1)) })
        {
            b.SetStyle(Paint.Style.Stroke);
            canvas.DrawOval(_backR, b);
        }
        using (var ch = new Paint(PaintFlags.AntiAlias) { Color = _p.Text, StrokeWidth = Dp(2.2f), StrokeCap = Paint.Cap.Round })
        {
            float cx = _backR.CenterX() - Dp(1), cy = _backR.CenterY();
            canvas.DrawLine(cx + Dp(3), cy - Dp(7), cx - Dp(4), cy, ch);
            canvas.DrawLine(cx - Dp(4), cy, cx + Dp(3), cy + Dp(7), ch);
        }
        Text(canvas, "Настройки", Dp(16), y + Dp(84), Dp(32), _p.Text, bold: true);
        y += Dp(96);

        // ── shade and sounds
        y += Dp(Head);
        Section(canvas, "Шторка и звуки", y);
        _shadeR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(RowH));
        var sr = _shadeR;
        Shift(canvas, idx++, () => DrawShadeRow(canvas, sr));
        y += Dp(RowH + Gap);
        _soundR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(RowH));
        var snr = _soundR;
        Shift(canvas, idx++, () => DrawSoundRow(canvas, snr));
        y += Dp(RowH + Gap);

        // ── battery notifications
        y += Dp(Head);
        Section(canvas, "Уведомления", y);
        _notifyR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(RowH));
        var nr = _notifyR;
        Shift(canvas, idx++, () => DrawNotifyRow(canvas, nr));
        y += Dp(RowH + Gap);

        // ── connect popup
        y += Dp(Head);
        Section(canvas, "Окно при подключении", y);
        _popR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(RowH));
        var pr = _popR;
        Shift(canvas, idx++, () => DrawPopupRow(canvas, pr));
        y += Dp(RowH + Gap);

        // ── home-screen widget
        y += Dp(Head);
        Section(canvas, "Рабочий стол", y);
        _widgetR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(RowH));
        var wr = _widgetR;
        Shift(canvas, idx++, () => DrawWidgetRow(canvas, wr));
        y += Dp(RowH + Gap);

        // ── low battery threshold
        y += Dp(Head);
        Section(canvas, "Низкий заряд", y);
        var lowR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(SegCardH));
        _lowSeg = new RectF(lowR.Left + Dp(14), lowR.Top + Dp(66), lowR.Right - Dp(14), lowR.Top + Dp(102));
        int lowIdx = Math.Max(0, Array.IndexOf(UserPrefs.LowSteps, LowAt));
        Shift(canvas, idx++, () =>
        {
            Glass(canvas, lowR, Dp(22));
            Text(canvas, "Предупреждать при заряде", lowR.Left + Dp(18), lowR.Top + Dp(30), Dp(16), _p.Text, bold: true, maxW: lowR.Width() - Dp(36));
            Text(canvas, $"Звук и «Пора зарядить» на {LowAt}% и ниже", lowR.Left + Dp(18), lowR.Top + Dp(50), Dp(12.5f), _p.Sub, maxW: lowR.Width() - Dp(36));
            Segmented(canvas, _lowSeg, UserPrefs.LowSteps.Select(v => $"{v}%").ToArray(), lowIdx, _lowFrom, _lowT0, LevelTint(LowAt));
        });
        y += Dp(SegCardH + Gap);

        // ── theme
        y += Dp(Head);
        Section(canvas, "Оформление", y);
        var thR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(SegCardH));
        _themeSeg = new RectF(thR.Left + Dp(14), thR.Top + Dp(66), thR.Right - Dp(14), thR.Top + Dp(102));
        Shift(canvas, idx++, () =>
        {
            Glass(canvas, thR, Dp(22));
            Text(canvas, "Тема", thR.Left + Dp(18), thR.Top + Dp(30), Dp(16), _p.Text, bold: true);
            string now = Theme == 0 ? $"Как в системе — сейчас {(_p.Dark ? "тёмная" : "светлая")}" : Theme == 1 ? "Всегда тёмная" : "Всегда светлая";
            Text(canvas, now, thR.Left + Dp(18), thR.Top + Dp(50), Dp(12.5f), _p.Sub, maxW: thR.Width() - Dp(36));
            Segmented(canvas, _themeSeg, ThemeNames, Theme, _themeFrom, _themeT0, _p.Accent);
        });
        y += Dp(SegCardH + Gap);

        // ── updates
        y += Dp(Head);
        Section(canvas, "Обновления", y);
        _autoR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(RowH));
        var aur = _autoR;
        Shift(canvas, idx++, () => DrawAutoRow(canvas, aur));
        y += Dp(RowH + Gap);

        // ── about
        y += Dp(Head);
        Section(canvas, "О программе", y);
        var ab = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(AboutH));
        _authorR = new RectF(ab.Left, ab.Top + Dp(84), ab.Right, ab.Top + Dp(136));
        _donateR = new RectF(ab.Left, ab.Top + Dp(136), ab.Right, ab.Top + Dp(188));
        _yooR = new RectF(ab.Left, ab.Top + Dp(188), ab.Right, ab.Top + Dp(240));
        _srcR = new RectF(ab.Left, ab.Bottom - Dp(52), ab.Right, ab.Bottom);
        Shift(canvas, idx++, () => DrawAbout(canvas, ab));

        if (Now - _born < 3200) PostInvalidateOnAnimation(); // entrance, then the little liquid in «О программе» settles; still screen costs nothing
        if (!_drawnOnce) { _drawnOnce = true; Android.Util.Log.Info("Glance", "drawn: settings"); }
    }

    static AColor LevelTint(int lowAt) => lowAt <= 10 ? Palette.Red : lowAt <= 20 ? Palette.Amber : AColor.Argb(255, 255, 159, 10);

    /// <summary>Glass track with a pill that glides to the chosen item; a light sheen runs across it after a change.</summary>
    void Segmented(Canvas c, RectF r, string[] items, int sel, float from, long t0, AColor tint)
    {
        int n = items.Length;
        float h = r.Height(), seg = r.Width() / n;
        using (var t = new Paint(PaintFlags.AntiAlias) { Color = _p.Track }) c.DrawRoundRect(r, h / 2, h / 2, t);

        float k = Ease((Now - t0) / 340f);
        float pos = from + (sel - from) * k;
        // the pill stretches a little while it travels, like a drop of liquid
        float stretch = MathF.Sin(k * MathF.PI) * Math.Min(1, Math.Abs(sel - from)) * seg * 0.18f;
        var pill = new RectF(r.Left + pos * seg + Dp(3) - stretch / 2, r.Top + Dp(3), r.Left + (pos + 1) * seg - Dp(3) + stretch / 2, r.Bottom - Dp(3));
        float pr = pill.Height() / 2;
        using (var shadow = new Paint(PaintFlags.AntiAlias) { Color = Palette.Alpha(tint, _p.Dark ? 90 : 70) })
            c.DrawRoundRect(new RectF(pill.Left, pill.Top + Dp(1.5f), pill.Right, pill.Bottom + Dp(1.5f)), pr, pr, shadow);
        using (var p = new Paint(PaintFlags.AntiAlias))
        {
            p.SetShader(new LinearGradient(0, pill.Top, 0, pill.Bottom, Palette.Mix(tint, AColor.White, 0.22f), Palette.Mix(tint, AColor.Black, 0.08f), Shader.TileMode.Clamp!));
            c.DrawRoundRect(pill, pr, pr, p);
        }
        float since = (Now - t0) / 700f;
        if (since < 1)
        {
            float x = pill.Left + (pill.Width() + Dp(40)) * Ease(since) - Dp(20);
            using var sh = new Paint(PaintFlags.AntiAlias);
            sh.SetShader(new LinearGradient(x - Dp(20), 0, x + Dp(20), 0,
                new[] { AColor.Argb(0, 255, 255, 255).ToArgb(), AColor.Argb(150, 255, 255, 255).ToArgb(), AColor.Argb(0, 255, 255, 255).ToArgb() }, null, Shader.TileMode.Clamp!));
            c.Save();
            using (var clip = new Path())
            {
                clip.AddRoundRect(pill, pr, pr, Path.Direction.Cw!);
                c.ClipPath(clip);
            }
            c.DrawRect(pill, sh);
            c.Restore();
            PostInvalidateOnAnimation();
        }
        for (int i = 0; i < n; i++)
        {
            // the label under the pill turns white as the pill arrives
            float under = Math.Clamp(1 - Math.Abs(pos - i), 0, 1);
            var col = Palette.Mix(_p.Text, AColor.White, under);
            TextCentered(c, items[i], r.Left + seg * i + seg / 2, r.CenterY() + Dp(5), Dp(13.5f), col, under > 0.5f, seg - Dp(8));
        }
    }

    void DrawShadeRow(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(16), r.Left + Dp(56), r.Top + Dp(56));
        ColorTile(c, tile, _p.Accent, (cx, cy) =>
        {
            // the status-bar ring, in miniature
            using var rp = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Dp(2.6f), StrokeCap = Paint.Cap.Round, Color = AColor.Argb(110, 255, 255, 255) };
            rp.SetStyle(Paint.Style.Stroke);
            var rb = new RectF(cx - Dp(10), cy - Dp(10), cx + Dp(10), cy + Dp(10));
            c.DrawOval(rb, rp);
            rp.Color = AColor.White;
            c.DrawArc(rb, -90, 250, false, rp);
        });
        DrawSwitch(c, r, tile, "Заряд в шторке", ShadeOn ? "Всегда, даже в фоне" : "Выключено", ShadeOn, _shadeT0);
    }

    /// <summary>«Звуки»: a speaker whose waves ripple out for a moment after the switch is flipped.</summary>
    void DrawSoundRow(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(16), r.Left + Dp(56), r.Top + Dp(56));
        ColorTile(c, tile, AColor.Argb(255, 255, 94, 135), (_, cy) =>
        {
            float cx = tile.Left + Dp(15);
            using (var sp = new Paint(PaintFlags.AntiAlias) { Color = AColor.White })
            {
                using var path = new Path();
                path.MoveTo(cx - Dp(6), cy - Dp(3.5f));
                path.LineTo(cx - Dp(2), cy - Dp(3.5f));
                path.LineTo(cx + Dp(3), cy - Dp(8));
                path.LineTo(cx + Dp(3), cy + Dp(8));
                path.LineTo(cx - Dp(2), cy + Dp(3.5f));
                path.LineTo(cx - Dp(6), cy + Dp(3.5f));
                path.Close();
                c.DrawPath(path, sp);
            }
            using var wp = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Dp(2), StrokeCap = Paint.Cap.Round };
            wp.SetStyle(Paint.Style.Stroke);
            float since = (Now - _soundT0) / 1000f;
            for (int i = 0; i < 2; i++)
            {
                float rr = Dp(6 + i * 5);
                int alpha = SoundsOn ? 255 : 90;
                if (SoundsOn && since < 1.2f)
                {
                    float ph = Math.Clamp(since * 2.4f - i * 0.35f, 0, 1);
                    alpha = (int)(255 * (0.35f + 0.65f * MathF.Sin(ph * MathF.PI * 0.5f)));
                    rr += Dp(2) * MathF.Sin(ph * MathF.PI);
                }
                wp.Color = AColor.Argb(alpha, 255, 255, 255);
                c.DrawArc(new RectF(cx + Dp(3) - rr, cy - rr, cx + Dp(3) + rr, cy + rr), -45, 90, false, wp);
            }
            if (!SoundsOn)
            {
                wp.Color = AColor.White;
                c.DrawLine(cx - Dp(7), cy - Dp(9), cx + Dp(13), cy + Dp(9), wp);
            }
        });
        DrawSwitch(c, r, tile, "Звуки", SoundsOn ? "Подключение и заряд" : "Выключено", SoundsOn, _soundT0);
        if (Now - _soundT0 < 1300) PostInvalidateOnAnimation();
    }

    /// <summary>«О заряде»: a bell that swings and rings for a moment after the switch is flipped on.</summary>
    void DrawNotifyRow(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(16), r.Left + Dp(56), r.Top + Dp(56));
        float since = (Now - _notifyT0) / 1000f;
        ColorTile(c, tile, AColor.Argb(255, 255, 159, 10), (cx, cy) =>
        {
            // damped swing around the bell's hook
            float swing = NotifyOn && since < 1.1f ? 22f * MathF.Exp(-since * 3.2f) * MathF.Sin(since * 26f) : 0;
            c.Save();
            c.Rotate(swing, cx, cy - Dp(10));
            using var p = new Paint(PaintFlags.AntiAlias) { Color = NotifyOn ? AColor.White : AColor.Argb(150, 255, 255, 255) };
            using (var bell = new Path())
            {
                bell.MoveTo(cx - Dp(9), cy + Dp(5));
                bell.CubicTo(cx - Dp(7), cy + Dp(2), cx - Dp(8), cy - Dp(9), cx, cy - Dp(9));
                bell.CubicTo(cx + Dp(8), cy - Dp(9), cx + Dp(7), cy + Dp(2), cx + Dp(9), cy + Dp(5));
                bell.Close();
                c.DrawPath(bell, p);
            }
            c.DrawRoundRect(new RectF(cx - Dp(10), cy + Dp(4), cx + Dp(10), cy + Dp(6.5f)), Dp(1.2f), Dp(1.2f), p);
            c.DrawCircle(cx, cy - Dp(10), Dp(1.8f), p);
            c.Restore();
            // the clapper swings the other way, a little behind
            float back = -swing * 0.6f;
            c.Save();
            c.Rotate(back, cx, cy - Dp(10));
            c.DrawCircle(cx, cy + Dp(9), Dp(2.4f), p);
            c.Restore();
            if (!NotifyOn)
            {
                using var x = new Paint(PaintFlags.AntiAlias) { Color = AColor.White, StrokeWidth = Dp(2), StrokeCap = Paint.Cap.Round };
                c.DrawLine(cx - Dp(11), cy - Dp(11), cx + Dp(11), cy + Dp(11), x);
            }
        });
        // short: the subtitle shares the row with the switch on narrow phones
        string sub = !NotifyOn ? "Выключено" : ShadeOn ? $"Ниже {LowAt}% и 100%" : "Включите шторку";
        DrawSwitch(c, r, tile, "О заряде", sub, NotifyOn, _notifyT0);
        if (Now - _notifyT0 < 1200) PostInvalidateOnAnimation();
    }

    /// <summary>«Автообновление»: an arrow that drops into a tray when switched on.</summary>
    void DrawAutoRow(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(16), r.Left + Dp(56), r.Top + Dp(56));
        float since = (Now - _autoT0) / 700f;
        ColorTile(c, tile, AColor.Argb(255, 52, 199, 89), (cx, cy) =>
        {
            using var p = new Paint(PaintFlags.AntiAlias) { Color = AutoUpdateOn ? AColor.White : AColor.Argb(150, 255, 255, 255), StrokeWidth = Dp(2.4f), StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
            p.SetStyle(Paint.Style.Stroke);
            // the arrow falls in from above once after switching on
            float drop = AutoUpdateOn && since < 1 ? -Dp(10) * (1 - Ease(since)) : 0;
            c.Save();
            c.ClipRect(tile);
            c.DrawLine(cx, cy - Dp(9) + drop, cx, cy + Dp(3) + drop, p);
            using (var head = new Path())
            {
                head.MoveTo(cx - Dp(5), cy - Dp(2) + drop);
                head.LineTo(cx, cy + Dp(3) + drop);
                head.LineTo(cx + Dp(5), cy - Dp(2) + drop);
                c.DrawPath(head, p);
            }
            c.Restore();
            using (var tray = new Path())
            {
                tray.MoveTo(cx - Dp(9), cy + Dp(3));
                tray.LineTo(cx - Dp(9), cy + Dp(9));
                tray.LineTo(cx + Dp(9), cy + Dp(9));
                tray.LineTo(cx + Dp(9), cy + Dp(3));
                c.DrawPath(tray, p);
            }
        });
        DrawSwitch(c, r, tile, "Автообновление", AutoUpdateOn ? "Искать новые версии" : "Выключено", AutoUpdateOn, _autoT0);
        if (Now - _autoT0 < 800) PostInvalidateOnAnimation();
    }

    /// <summary>The connect popup: allowed (green check) or a hint to allow «Поверх других приложений».</summary>
    void DrawPopupRow(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(16), r.Left + Dp(56), r.Top + Dp(56));
        ColorTile(c, tile, AColor.Argb(255, 94, 92, 230), (cx, cy) =>
        {
            // a phone with a small card sliding up from its bottom edge
            using var p = new Paint(PaintFlags.AntiAlias) { Color = AColor.White, StrokeWidth = Dp(2) };
            p.SetStyle(Paint.Style.Stroke);
            c.DrawRoundRect(new RectF(cx - Dp(8), cy - Dp(12), cx + Dp(8), cy + Dp(12)), Dp(3), Dp(3), p);
            p.SetStyle(Paint.Style.Fill);
            c.DrawRoundRect(new RectF(cx - Dp(5.5f), cy + Dp(3), cx + Dp(5.5f), cy + Dp(9)), Dp(1.5f), Dp(1.5f), p);
        });
        float tx = tile.Right + Dp(14);
        Text(c, "Заряд сразу", tx, r.Top + Dp(32), Dp(16), _p.Text, bold: true, maxW: r.Right - tx - Dp(44));
        Text(c, PopupAllowed ? "Включено" : "Нужно разрешение", tx, r.Top + Dp(52), Dp(12.5f),
            PopupAllowed ? _p.Sub : Palette.Amber, maxW: r.Right - tx - Dp(44));
        if (PopupAllowed)
        {
            using var ok = new Paint(PaintFlags.AntiAlias) { Color = Palette.Green, StrokeWidth = Dp(2.4f), StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
            ok.SetStyle(Paint.Style.Stroke);
            float x = r.Right - Dp(26), y = r.CenterY();
            using var path = new Path();
            path.MoveTo(x - Dp(6), y);
            path.LineTo(x - Dp(1.5f), y + Dp(5));
            path.LineTo(x + Dp(7), y - Dp(5));
            c.DrawPath(path, ok);
        }
        else Chevron(c, r.Right - Dp(24), r.CenterY(), _p.Sub);
    }

    /// <summary>«Виджет Glance»: placed (green check) or a tap to put it on the home screen.</summary>
    void DrawWidgetRow(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(16), r.Left + Dp(56), r.Top + Dp(56));
        ColorTile(c, tile, AColor.Argb(255, 48, 176, 199), (cx, cy) =>
        {
            // a small glass pane with a ring and two liquid bars
            using var p = new Paint(PaintFlags.AntiAlias) { Color = AColor.White, StrokeWidth = Dp(1.8f), StrokeCap = Paint.Cap.Round };
            p.SetStyle(Paint.Style.Stroke);
            c.DrawRoundRect(new RectF(cx - Dp(12), cy - Dp(9), cx + Dp(12), cy + Dp(9)), Dp(4), Dp(4), p);
            c.DrawCircle(cx - Dp(5.5f), cy, Dp(3.6f), p);
            p.StrokeWidth = Dp(2.2f);
            c.DrawLine(cx + Dp(1), cy - Dp(2.5f), cx + Dp(8), cy - Dp(2.5f), p);
            c.DrawLine(cx + Dp(1), cy + Dp(3), cx + Dp(5), cy + Dp(3), p);
        });
        float tx = tile.Right + Dp(14);
        Text(c, "Виджет Glance", tx, r.Top + Dp(32), Dp(16), _p.Text, bold: true, maxW: r.Right - tx - Dp(44));
        Text(c, WidgetCount > 0 ? (WidgetCount == 1 ? "На рабочем столе" : $"На рабочем столе: {WidgetCount}") : "Добавить на рабочий стол",
            tx, r.Top + Dp(52), Dp(12.5f), _p.Sub, maxW: r.Right - tx - Dp(44));
        if (WidgetCount > 0)
        {
            using var ok = new Paint(PaintFlags.AntiAlias) { Color = Palette.Green, StrokeWidth = Dp(2.4f), StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
            ok.SetStyle(Paint.Style.Stroke);
            float x = r.Right - Dp(26), y = r.CenterY();
            using var path = new Path();
            path.MoveTo(x - Dp(6), y);
            path.LineTo(x - Dp(1.5f), y + Dp(5));
            path.LineTo(x + Dp(7), y - Dp(5));
            c.DrawPath(path, ok);
        }
        else Chevron(c, r.Right - Dp(24), r.CenterY(), _p.Sub);
    }

    void DrawAbout(Canvas c, RectF r)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(18), r.Left + Dp(64), r.Top + Dp(66));
        ColorTile(c, tile, _p.Accent, (cx, cy) =>
        {
            // Glance's ring with a little liquid inside
            using var rp = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Dp(3.2f), StrokeCap = Paint.Cap.Round, Color = AColor.Argb(110, 255, 255, 255) };
            rp.SetStyle(Paint.Style.Stroke);
            var rb = new RectF(cx - Dp(13), cy - Dp(13), cx + Dp(13), cy + Dp(13));
            c.DrawOval(rb, rp);
            rp.Color = AColor.White;
            c.DrawArc(rb, -90, 270, false, rp);
            float level = 0.55f + 0.06f * MathF.Sin(Now / 520f);
            c.Save();
            using (var clip = new Path())
            {
                clip.AddCircle(cx, cy, Dp(8.5f), Path.Direction.Cw!);
                c.ClipPath(clip);
            }
            using var lp = new Paint(PaintFlags.AntiAlias) { Color = AColor.Argb(200, 255, 255, 255) };
            float top = cy + Dp(8.5f) - Dp(17) * level;
            using var wave = new Path();
            wave.MoveTo(cx - Dp(9), top);
            for (float x = -9; x <= 9.01f; x += 1.5f) wave.LineTo(cx + Dp(x), top + Dp(1.2f) * MathF.Sin(x / 2.6f + Now / 300f));
            wave.LineTo(cx + Dp(9), cy + Dp(9));
            wave.LineTo(cx - Dp(9), cy + Dp(9));
            wave.Close();
            c.DrawPath(wave, lp);
            c.Restore();
        });
        float tx = tile.Right + Dp(14);
        Text(c, "Glance для Android", tx, r.Top + Dp(37), Dp(16.5f), _p.Text, bold: true, maxW: r.Right - tx - Dp(16));
        Text(c, VersionText, tx, r.Top + Dp(58), Dp(12.5f), _p.Sub, maxW: r.Right - tx - Dp(16));

        // link rows: the author's profile, two ways to donate, the source code
        LinkRow(c, r, r.Top + Dp(84), "Автор — ddedlish", "Профиль в Steam", AColor.Argb(255, 102, 192, 244), Glyph.Person);
        LinkRow(c, r, r.Top + Dp(136), "Поддержать автора", "Boosty, любая сумма", AColor.Argb(255, 255, 94, 135), Glyph.Heart);
        LinkRow(c, r, r.Top + Dp(188), "Перевод на ЮMoney", "Любая сумма, с карты", AColor.Argb(255, 139, 63, 253), Glyph.Wallet);
        LinkRow(c, r, r.Top + Dp(240), "Исходный код", "GitHub · ddedlish/Glance", _p.Accent, Glyph.Code);
    }

    enum Glyph { Person, Heart, Wallet, Code }

    /// <summary>A divider, a small coloured glyph, title + subtitle and a chevron; the heart beats softly.</summary>
    void LinkRow(Canvas c, RectF card, float top, string title, string sub, AColor col, Glyph g)
    {
        using (var div = new Paint { Color = _p.CardBorder, StrokeWidth = Math.Max(1, Dp(0.8f)) })
            c.DrawLine(card.Left + Dp(18), top, card.Right - Dp(18), top, div);
        float cx = card.Left + Dp(34), cy = top + Dp(26);
        using (var bg = new Paint(PaintFlags.AntiAlias) { Color = Palette.Alpha(col, _p.Dark ? 60 : 46) })
            c.DrawCircle(cx, cy, Dp(15), bg);
        using var p = new Paint(PaintFlags.AntiAlias) { Color = col, StrokeWidth = Dp(2), StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
        switch (g)
        {
            case Glyph.Person:
                c.DrawCircle(cx, cy - Dp(3.5f), Dp(3.6f), p);
                c.DrawArc(new RectF(cx - Dp(7), cy + Dp(1.5f), cx + Dp(7), cy + Dp(13)), 180, 180, true, p);
                break;
            case Glyph.Heart:
            {
                bool alive = Now - _born < 6000; // a few beats after the page opens, then a still screen costs nothing
                float beat = alive ? 1 + 0.08f * MathF.Max(0, MathF.Sin(Now / 1000f * MathF.PI * 2.2f)) * MathF.Max(0, MathF.Sin(Now / 1000f * MathF.PI * 1.1f)) : 1;
                float k = Dp(6.5f) * beat;
                using var h = new Path();
                h.MoveTo(cx, cy + k * 0.95f);
                h.CubicTo(cx - k * 1.6f, cy - k * 0.1f, cx - k * 0.9f, cy - k * 1.25f, cx, cy - k * 0.45f);
                h.CubicTo(cx + k * 0.9f, cy - k * 1.25f, cx + k * 1.6f, cy - k * 0.1f, cx, cy + k * 0.95f);
                h.Close();
                c.DrawPath(h, p);
                if (alive) PostInvalidateOnAnimation();
                break;
            }
            case Glyph.Wallet:
            {
                // a wallet: body, flap line and a clasp
                p.SetStyle(Paint.Style.Stroke);
                p.StrokeWidth = Dp(1.8f);
                var body = new RectF(cx - Dp(7.5f), cy - Dp(5.5f), cx + Dp(7.5f), cy + Dp(6));
                c.DrawRoundRect(body, Dp(2.2f), Dp(2.2f), p);
                c.DrawLine(cx - Dp(7.5f), cy - Dp(2), cx + Dp(4), cy - Dp(2), p);
                c.DrawRoundRect(new RectF(cx + Dp(2), cy - Dp(0.5f), cx + Dp(7.5f), cy + Dp(3.5f)), Dp(1.2f), Dp(1.2f), p);
                p.SetStyle(Paint.Style.Fill);
                c.DrawCircle(cx + Dp(4.6f), cy + Dp(1.5f), Dp(0.9f), p);
                break;
            }
            default:
                p.SetStyle(Paint.Style.Stroke);
                c.DrawLine(cx - Dp(2.5f), cy - Dp(5), cx - Dp(7), cy, p);
                c.DrawLine(cx - Dp(7), cy, cx - Dp(2.5f), cy + Dp(5), p);
                c.DrawLine(cx + Dp(2.5f), cy - Dp(5), cx + Dp(7), cy, p);
                c.DrawLine(cx + Dp(7), cy, cx + Dp(2.5f), cy + Dp(5), p);
                break;
        }
        float tx = card.Left + Dp(60);
        Text(c, title, tx, top + Dp(23), Dp(15), _p.Text, bold: true, maxW: card.Right - tx - Dp(36));
        Text(c, sub, tx, top + Dp(41), Dp(12), _p.Sub, maxW: card.Right - tx - Dp(36));
        Chevron(c, card.Right - Dp(24), top + Dp(26), _p.Sub);
    }

    // ───────── touch ─────────

    float _downX, _downY;

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.Action)
        {
            case MotionEventActions.Down:
                _downX = e.GetX();
                _downY = e.GetY();
                return true;
            case MotionEventActions.Up:
                float x = e.GetX(), y = e.GetY();
                if (Math.Abs(x - _downX) > Dp(10) || Math.Abs(y - _downY) > Dp(10)) return true;
                if (Grow(_backR, Dp(10)).Contains(x, y)) BackRequested?.Invoke();
                else if (_shadeR.Contains(x, y))
                {
                    ShadeOn = !ShadeOn;
                    _shadeT0 = Now;
                    Tick();
                    ShadeToggled?.Invoke(ShadeOn);
                }
                else if (_soundR.Contains(x, y))
                {
                    SoundsOn = !SoundsOn;
                    _soundT0 = Now;
                    Tick();
                    SoundsToggled?.Invoke(SoundsOn);
                }
                else if (_notifyR.Contains(x, y))
                {
                    NotifyOn = !NotifyOn;
                    _notifyT0 = Now;
                    Tick();
                    NotifyToggled?.Invoke(NotifyOn);
                }
                else if (_autoR.Contains(x, y))
                {
                    AutoUpdateOn = !AutoUpdateOn;
                    _autoT0 = Now;
                    Tick();
                    AutoUpdateToggled?.Invoke(AutoUpdateOn);
                }
                else if (_popR.Contains(x, y)) PopupRequested?.Invoke();
                else if (_widgetR.Contains(x, y)) { Tick(); WidgetRequested?.Invoke(); }
                else if (Grow(_lowSeg, Dp(6)).Contains(x, y))
                {
                    int i = Pick(_lowSeg, x, UserPrefs.LowSteps.Length);
                    int cur = Math.Max(0, Array.IndexOf(UserPrefs.LowSteps, LowAt));
                    if (i != cur)
                    {
                        _lowFrom = Glide(cur, _lowFrom, _lowT0);
                        _lowT0 = Now;
                        LowAt = UserPrefs.LowSteps[i];
                        Tick();
                        LowAtChanged?.Invoke(LowAt);
                    }
                }
                else if (Grow(_themeSeg, Dp(6)).Contains(x, y))
                {
                    int i = Pick(_themeSeg, x, ThemeNames.Length);
                    if (i != Theme)
                    {
                        _themeFrom = Glide(Theme, _themeFrom, _themeT0);
                        _themeT0 = Now;
                        Theme = i;
                        Tick();
                        ThemeChanged?.Invoke(Theme);
                    }
                }
                else if (_srcR.Contains(x, y)) SourceRequested?.Invoke();
                else if (_authorR.Contains(x, y)) AuthorRequested?.Invoke();
                else if (_donateR.Contains(x, y)) DonateRequested?.Invoke();
                else if (_yooR.Contains(x, y)) YooMoneyRequested?.Invoke();
                PerformClick();
                return true;
        }
        return true;
    }

    /// <summary>Where the pill is right now (a tap mid-glide starts from there).</summary>
    static float Glide(int sel, float from, long t0) => from + (sel - from) * Ease((Now - t0) / 340f);

    static int Pick(RectF seg, float x, int n) => Math.Clamp((int)((x - seg.Left) / (seg.Width() / n)), 0, n - 1);

    void Tick()
    {
        PerformHapticFeedback(FeedbackConstants.VirtualKey);
        Invalidate();
    }
}
