using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AColor = Android.Graphics.Color;

namespace Glance.Droid;

/// <summary>Soft gradient backdrop with blurred colour blobs (Liquid Glass ground). Static: costs nothing.</summary>
internal sealed class Backdrop : View
{
    public Palette P { get; set; } = Palette.For(true);
    public Backdrop(Context c) : base(c) { }

    protected override void OnDraw(Canvas canvas)
    {
        float w = Width, h = Height;
        using var bg = new Paint();
        bg.SetShader(new LinearGradient(0, 0, 0, h, P.BgTop, P.BgBottom, Shader.TileMode.Clamp!));
        canvas.DrawRect(0, 0, w, h, bg);
        Blob(canvas, w * 0.15f, h * 0.12f, w * 0.75f, P.BlobA);
        Blob(canvas, w * 0.95f, h * 0.38f, w * 0.70f, P.BlobB);
        Blob(canvas, w * 0.25f, h * 0.85f, w * 0.80f, P.BlobC);
    }

    static void Blob(Canvas c, float x, float y, float r, AColor color)
    {
        using var p = new Paint(PaintFlags.AntiAlias);
        p.SetShader(new RadialGradient(x, y, r, color, Palette.Alpha(color, 0), Shader.TileMode.Clamp!));
        c.DrawCircle(x, y, r, p);
    }
}

/// <summary>
/// The whole screen, drawn by hand: header, hero card with the live liquid ring, one glass card per accessory
/// (earbuds get L / R / case rings), the phone itself, and banners (permission, Bluetooth off, update).
/// Lives inside a ScrollView: reports its content height in OnMeasure.
/// </summary>
internal sealed class GlanceView : View
{
    readonly Devices _dev;
    readonly float _d;            // px per dp
    Palette _p = Palette.For(true);
    long _born = SystemClock.UptimeMillis();
    readonly Dictionary<string, (float From, float To, long T0)> _tweens = new();

    public float TopInset { get; set; }
    public float BottomInset { get; set; }
    public string VersionText { get; set; } = "";
    public UpdateInfo? Update { get; set; }
    public event Action? PermissionRequested, UpdateRequested;

    // hit areas
    RectF _heroR = new(), _permR = new(), _updR = new();

    public GlanceView(Context c, Devices dev) : base(c)
    {
        _dev = dev;
        _d = c.Resources!.DisplayMetrics!.Density;
    }

    public void SetPalette(Palette p) { _p = p; Invalidate(); }

    float Dp(float v) => v * _d;
    static long Now => SystemClock.UptimeMillis();

    // ───────── layout ─────────

    const float Side = 16, Gap = 12, HeroH = 148, CardH = 84, BudsH = 150, BannerH = 64;

    Accessory? Hero(IReadOnlyList<Accessory> list)
    {
        var withLevel = list.Where(a => a.WeakestLevel is not null).ToList();
        if (withLevel.Count == 0) return null;
        return withLevel.Where(a => !a.Charging).MinBy(a => a.WeakestLevel) ?? withLevel.MinBy(a => a.WeakestLevel);
    }

    float ContentHeight(IReadOnlyList<Accessory> list)
    {
        float y = TopInset / _d + 96;
        if (Update is not null) y += BannerH + Gap;
        if (_dev.MissingPermission || _dev.BluetoothOff || _dev.NoBluetooth) y += BannerH + 28 + Gap;
        if (Hero(list) is not null) y += HeroH + Gap + 28;
        if (list.Count == 0) y += 200;
        else y += 28 + list.Sum(a => (a.HasParts ? BudsH : CardH) + Gap);
        if (_dev.PhoneLevel is not null) y += 28 + CardH + Gap;
        y += 40;
        return Dp(y) + BottomInset;
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int h = (int)ContentHeight(_dev.Connected);
        if (MeasureSpec.GetMode(heightMeasureSpec) == MeasureSpecMode.Exactly) h = Math.Max(h, MeasureSpec.GetSize(heightMeasureSpec));
        SetMeasuredDimension(w, h);
    }

    // ───────── animation helpers ─────────

    /// <summary>0…1 entrance of the item at index i (cards rise one after another).</summary>
    float Arrive(int i)
    {
        float ms = Now - _born - 80 - i * 60;
        float t = Math.Clamp(ms / 420f, 0, 1);
        return 1 - MathF.Pow(1 - t, 3);
    }

    /// <summary>Smoothly animated level per key (counts up on open, glides on change).</summary>
    float Tween(string key, float target)
    {
        long now = Now;
        if (!_tweens.TryGetValue(key, out var tw)) { tw = (0, target, now + 120); _tweens[key] = tw; }
        else if (Math.Abs(tw.To - target) > 0.01f) { tw = (Value(tw, now), target, now); _tweens[key] = tw; }
        return Value(tw, now);
    }

    static float Value((float From, float To, long T0) tw, long now)
    {
        float t = Math.Clamp((now - tw.T0) / 700f, 0, 1);
        return tw.From + (tw.To - tw.From) * (1 - MathF.Pow(1 - t, 3));
    }

    bool Animating => Now - _born < 1400 || _tweens.Values.Any(t => Now - t.T0 < 720) || Now - _sloshT0 < SloshMs;

    // ───────── drawing ─────────

    protected override void OnDraw(Canvas canvas)
    {
        var list = _dev.Connected;
        float w = Width, s = _d;
        float y = TopInset + Dp(28);
        int idx = 0;

        // header
        Text(canvas, "Аксессуары", Dp(16), y + Dp(30), Dp(32), _p.Text, bold: true);
        string sub = list.Count == 0 ? VersionText : $"подключено: {list.Count} · {VersionText}";
        Text(canvas, sub, Dp(17), y + Dp(56), Dp(14), _p.Sub);
        y += Dp(96 - 28);

        if (Update is { } up)
        {
            _updR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(BannerH));
            Shift(canvas, idx++, () => Banner(canvas, _updR, $"Доступна версия {up.Version}", "Нажмите, чтобы скачать и установить", _p.Accent, true));
            y += Dp(BannerH + Gap);
        }
        else _updR = new RectF();

        if (_dev.MissingPermission || _dev.BluetoothOff || _dev.NoBluetooth)
        {
            y += Dp(28);
            _permR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(BannerH));
            if (_dev.MissingPermission)
                Shift(canvas, idx++, () => Banner(canvas, _permR, "Разрешите доступ к Bluetooth", "Нужно, чтобы видеть заряд аксессуаров", Palette.Amber, true));
            else
            {
                _permR = new RectF();
                var r = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(BannerH));
                Shift(canvas, idx++, () => Banner(canvas, r, _dev.NoBluetooth ? "Bluetooth недоступен" : "Bluetooth выключен",
                    _dev.NoBluetooth ? "На этом устройстве нет Bluetooth" : "Включите его в быстрых настройках", Palette.Amber, false));
            }
            y += Dp(BannerH + Gap);
        }
        else _permR = new RectF();

        var hero = Hero(list);
        if (hero is not null)
        {
            y += Dp(28);
            Section(canvas, "Зарядить первым", y);
            _heroR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(HeroH));
            var hr = _heroR;
            Shift(canvas, idx++, () => DrawHero(canvas, hr, hero));
            y += Dp(HeroH + Gap);
        }
        else _heroR = new RectF();

        if (list.Count == 0)
        {
            float cy = y + Dp(40);
            var icon = new RectF(w / 2 - Dp(26), cy, w / 2 + Dp(26), cy + Dp(52));
            Icons.Draw(canvas, DeviceKind.Unknown, icon, _p.Sub, Dp(1.8f));
            TextCentered(canvas, "Нет подключённых аксессуаров", w / 2, cy + Dp(86), Dp(17), _p.Text, true);
            TextCentered(canvas, "Подключите наушники, часы или геймпад по Bluetooth", w / 2, cy + Dp(112), Dp(13.5f), _p.Sub, false);
            y += Dp(200);
        }
        else
        {
            y += Dp(28);
            Section(canvas, "Все устройства", y);
            foreach (var a in list)
            {
                float h = a.HasParts ? BudsH : CardH;
                var r = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(h));
                var acc = a;
                Shift(canvas, idx++, () => DrawCard(canvas, r, acc));
                y += Dp(h + Gap);
            }
        }

        if (_dev.PhoneLevel is int pl)
        {
            y += Dp(28);
            Section(canvas, "Этот телефон", y);
            var r = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(CardH));
            var phone = new Accessory("PHONE")
            {
                Name = Build.Model ?? "Телефон",
                Profile = new DeviceProfile { Kind = DeviceKind.Phone, Brand = Build.Manufacturer ?? "" },
                Connected = true,
                SystemLevel = pl,
                SystemCharging = _dev.PhoneCharging,
            };
            Shift(canvas, idx++, () => DrawCard(canvas, r, phone, phoneCard: true));
        }

        if (Animating || list.Any(a => a.Charging) || (hero?.Charging ?? false)) PostInvalidateOnAnimation();
        if (!_drawnOnce) { _drawnOnce = true; Android.Util.Log.Info("Glance", $"drawn: {list.Count} devices, demo={_dev.Demo}"); }
    }

    bool _drawnOnce;

    /// <summary>Draws an item with its entrance offset and fade.</summary>
    void Shift(Canvas c, int i, Action draw)
    {
        float k = Arrive(i);
        if (k <= 0) return;
        if (k >= 1) { draw(); return; }
        int save = c.SaveLayerAlpha(0, 0, Width, Height, (int)(255 * k));
        c.Translate(0, (1 - k) * Dp(24));
        draw();
        c.RestoreToCount(save);
    }

    void Section(Canvas c, string title, float cardTop) =>
        Text(c, title.ToUpperInvariant(), Dp(Side + 6), cardTop - Dp(10), Dp(12), _p.Sub, bold: true, spacing: 0.06f);

    void Glass(Canvas c, RectF r, float radius)
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

    void Banner(Canvas c, RectF r, string title, string sub, AColor tint, bool chevron)
    {
        Glass(c, r, Dp(20));
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = Palette.Alpha(tint, _p.Dark ? 46 : 36) })
            c.DrawRoundRect(r, Dp(20), Dp(20), p);
        using (var dot = new Paint(PaintFlags.AntiAlias) { Color = tint })
            c.DrawCircle(r.Left + Dp(24), r.CenterY(), Dp(5), dot);
        Text(c, title, r.Left + Dp(42), r.Top + Dp(27), Dp(15.5f), _p.Text, bold: true, maxW: r.Width() - Dp(80));
        Text(c, sub, r.Left + Dp(42), r.Top + Dp(47), Dp(12.5f), _p.Sub, maxW: r.Width() - Dp(80));
        if (chevron) Chevron(c, r.Right - Dp(24), r.CenterY(), _p.Sub);
    }

    void Chevron(Canvas c, float x, float y, AColor color)
    {
        using var p = new Paint(PaintFlags.AntiAlias) { Color = color, StrokeWidth = Dp(2), StrokeCap = Paint.Cap.Round };
        c.DrawLine(x - Dp(4), y - Dp(6), x + Dp(2), y, p);
        c.DrawLine(x + Dp(2), y, x - Dp(4), y + Dp(6), p);
    }

    void Tile(Canvas c, RectF r, DeviceKind kind)
    {
        var col = Palette.KindColor(kind);
        using var p = new Paint(PaintFlags.AntiAlias);
        p.SetShader(new LinearGradient(r.Left, r.Top, r.Right, r.Bottom, Palette.Mix(col, AColor.White, 0.18f), Palette.Mix(col, AColor.Black, 0.12f), Shader.TileMode.Clamp!));
        c.DrawRoundRect(r, Dp(13), Dp(13), p);
        using var hi = new Paint(PaintFlags.AntiAlias);
        hi.SetShader(new LinearGradient(0, r.Top, 0, r.CenterY(), AColor.Argb(70, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
        c.DrawRoundRect(r, Dp(13), Dp(13), hi);
        var inner = new RectF(r.Left + r.Width() * 0.2f, r.Top + r.Height() * 0.2f, r.Right - r.Width() * 0.2f, r.Bottom - r.Height() * 0.2f);
        Icons.Draw(c, kind, inner, AColor.White, Dp(2));
    }

    void DrawCard(Canvas c, RectF r, Accessory a, bool phoneCard = false)
    {
        Glass(c, r, Dp(22));
        var tile = new RectF(r.Left + Dp(16), r.Top + Dp(18), r.Left + Dp(64), r.Top + Dp(66));
        Tile(c, tile, a.Kind);

        float tx = tile.Right + Dp(14);
        float pctW = Dp(70);
        Text(c, a.DisplayName, tx, r.Top + Dp(37), Dp(16.5f), _p.Text, bold: true, maxW: r.Right - tx - pctW - Dp(12));
        string link = phoneCard ? (a.Charging ? "Заряжается" : a.Profile.Brand) : (a.Charging ? "Заряжается · " : "") + a.Link;
        string feat = phoneCard ? "" : Profiles.Features(a.Profile);
        if (feat.Length > 0) link += " · " + feat;
        Text(c, link, tx, r.Top + Dp(58), Dp(12.5f), _p.Sub, maxW: r.Right - tx - pctW - Dp(12));

        int? lvl = a.Level;
        float shown = lvl is int l ? Tween("C:" + a.Key, l) : 0;
        var col = Palette.Level(lvl, a.Charging);
        string pct = lvl is null ? "—" : $"{(int)MathF.Round(shown)}%";
        var pctColor = lvl is int l2 && l2 <= 20 && !a.Charging ? col : _p.Text;
        TextRight(c, pct, r.Right - Dp(18), r.Top + Dp(40), Dp(22), pctColor, true);
        if (a.Charging) Icons.Bolt(c, new RectF(r.Right - Dp(18) - Dp(22) - MeasureBig(pct), r.Top + Dp(22), r.Right - Dp(18) - MeasureBig(pct) - Dp(4), r.Top + Dp(42)), Palette.Green);

        // bar
        var bar = new RectF(r.Right - Dp(18) - Dp(56), r.Top + Dp(52), r.Right - Dp(18), r.Top + Dp(58));
        Bar(c, bar, shown / 100f, col, a.Charging);

        if (a.HasParts)
        {
            float top = r.Top + Dp(80), cw = (r.Width() - Dp(32)) / 3f;
            var parts = new[] { ("Левый", a.Left), ("Правый", a.Right), ("Кейс", a.Case) };
            for (int i = 0; i < 3; i++)
            {
                var (label, part) = parts[i];
                float cx = r.Left + Dp(16) + cw * i + cw / 2;
                MiniRing(c, cx, top + Dp(22), Dp(20), part, $"{a.Key}:{i}");
                TextCentered(c, label, cx, top + Dp(62), Dp(12), _p.Sub, false);
            }
        }
    }

    float MeasureBig(string s)
    {
        using var p = TextPaint(Dp(22), true);
        return p.MeasureText(s);
    }

    void Bar(Canvas c, RectF r, float frac, AColor col, bool charging)
    {
        float rad = r.Height() / 2;
        using (var t = new Paint(PaintFlags.AntiAlias) { Color = _p.Track }) c.DrawRoundRect(r, rad, rad, t);
        frac = Math.Clamp(frac, 0, 1);
        if (frac <= 0.001f) return;
        var f = new RectF(r.Left, r.Top, r.Left + Math.Max(r.Height(), r.Width() * frac), r.Bottom);
        using var p = new Paint(PaintFlags.AntiAlias) { Color = col };
        c.DrawRoundRect(f, rad, rad, p);
        if (charging)
        {
            // a highlight runs along the bar while charging
            float k = (Now % 1600) / 1600f;
            float x = f.Left + (f.Width() + Dp(30)) * k - Dp(15);
            using var sh = new Paint(PaintFlags.AntiAlias);
            sh.SetShader(new LinearGradient(x - Dp(15), 0, x + Dp(15), 0,
                new[] { AColor.Argb(0, 255, 255, 255).ToArgb(), AColor.Argb(170, 255, 255, 255).ToArgb(), AColor.Argb(0, 255, 255, 255).ToArgb() }, null, Shader.TileMode.Clamp!));
            c.Save();
            c.ClipRect(f);
            c.DrawRoundRect(f, rad, rad, sh);
            c.Restore();
        }
    }

    void MiniRing(Canvas c, float cx, float cy, float rad, Part part, string key)
    {
        var box = new RectF(cx - rad, cy - rad, cx + rad, cy + rad);
        using var p = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Dp(4.5f), StrokeCap = Paint.Cap.Round, Color = _p.Track };
        p.SetStyle(Paint.Style.Stroke);
        c.DrawOval(box, p);
        if (part.Level is int l)
        {
            float shown = Tween("P:" + key, l);
            p.Color = Palette.Level(l, part.Charging);
            c.DrawArc(box, -90, 360 * shown / 100f, false, p);
            TextCentered(c, $"{(int)MathF.Round(shown)}", cx, cy + Dp(5), Dp(14), _p.Text, true);
            if (part.Charging) Icons.Bolt(c, new RectF(cx + rad - Dp(4), cy - rad - Dp(6), cx + rad + Dp(10), cy - rad + Dp(8)), Palette.Green);
        }
        else TextCentered(c, "—", cx, cy + Dp(5), Dp(14), _p.Sub, true);
    }

    // ───────── hero: liquid ring ─────────

    const int SloshMs = 2800;
    long _sloshT0 = SystemClock.UptimeMillis() + 150;
    float _sloshPower = 1f;

    /// <summary>Cards rise again and the liquid splashes (app brought back to the screen).</summary>
    public void Replay()
    {
        _born = Now;
        Slosh(1f);
    }

    /// <summary>Gives the liquid a push; it rocks and settles.</summary>
    public void Slosh(float power)
    {
        long now = Now;
        float left = now - _sloshT0 < SloshMs ? _sloshPower * MathF.Exp(-(now - _sloshT0) / 700f) : 0;
        _sloshPower = Math.Min(1.4f, left + power);
        _sloshT0 = now;
        PostInvalidateOnAnimation();
    }

    void DrawHero(Canvas c, RectF r, Accessory a)
    {
        if (a.WeakestLevel is not int lvl) return;
        Glass(c, r, Dp(26));
        float d = Dp(104), th = Dp(10);
        var ring = new RectF(r.Left + Dp(20), r.CenterY() - d / 2, r.Left + Dp(20) + d, r.CenterY() + d / 2);
        float shown = Tween("H:" + a.Key, lvl);
        var col = Palette.Level(lvl, a.Charging);

        using (var glow = new Paint(PaintFlags.AntiAlias))
        {
            glow.SetShader(new RadialGradient(ring.CenterX(), ring.CenterY(), d * 0.75f, Palette.Alpha(col, _p.Dark ? 70 : 50), Palette.Alpha(col, 0), Shader.TileMode.Clamp!));
            c.DrawCircle(ring.CenterX(), ring.CenterY(), d * 0.75f, glow);
        }
        var arcBox = new RectF(ring.Left + th / 2, ring.Top + th / 2, ring.Right - th / 2, ring.Bottom - th / 2);
        using (var track = new Paint(PaintFlags.AntiAlias) { StrokeWidth = th, Color = _p.Track })
        {
            track.SetStyle(Paint.Style.Stroke);
            c.DrawOval(arcBox, track);
        }
        float inset = th + Dp(4);
        DrawLiquid(c, new RectF(ring.Left + inset, ring.Top + inset, ring.Right - inset, ring.Bottom - inset), shown, col, a.Charging);
        if (shown > 0.5f)
        {
            using var arc = new Paint(PaintFlags.AntiAlias) { StrokeWidth = th, StrokeCap = Paint.Cap.Round, Color = col };
            arc.SetStyle(Paint.Style.Stroke);
            c.DrawArc(arcBox, -90, 360 * shown / 100f, false, arc);
        }
        TextCentered(c, $"{(int)MathF.Round(shown)}%", ring.CenterX(), ring.CenterY() + Dp(9), Dp(25), _p.Text, true);

        float tx = ring.Right + Dp(20), tw = r.Right - tx - Dp(16);
        var tile = new RectF(tx, r.Top + Dp(26), tx + Dp(22), r.Top + Dp(48));
        Icons.Draw(c, a.Kind, tile, Palette.KindColor(a.Kind), Dp(2));
        Text(c, a.DisplayName, tx + Dp(28), r.Top + Dp(43), Dp(17), _p.Text, bold: true, maxW: tw - Dp(28));
        string line = a.Charging ? "Заряжается" : lvl <= 20 ? "Пора зарядить" : lvl <= 40 ? "Разрядится первым" : "Всё в порядке";
        Text(c, line, tx, r.Top + Dp(74), Dp(14.5f), lvl <= 20 && !a.Charging ? Palette.Red : _p.Text, maxW: tw);
        string sub = a.HasParts ? $"Левый {Fmt.Percent(a.Left.Level)} · Правый {Fmt.Percent(a.Right.Level)}" : a.Link;
        Text(c, sub, tx, r.Top + Dp(98), Dp(12.5f), _p.Sub, maxW: tw);
    }

    void DrawLiquid(Canvas c, RectF disc, float level, AColor color, bool charging)
    {
        if (level <= 0.5f || disc.Width() < Dp(8)) return;
        long now = Now;
        float dt = now - _sloshT0;
        float push = dt >= 0 && dt < SloshMs ? _sloshPower * MathF.Exp(-dt / 700f) : 0;
        float tilt = push * MathF.Sin(Math.Max(0, dt) / 160f) * Dp(9);
        float amp = (charging ? Dp(2.2f) : Dp(1.4f)) + push * Dp(3.4f);
        float phase = now / (charging ? 300f : 480f);
        float surface = disc.Bottom - disc.Height() * Math.Clamp(level, 0, 100) / 100f;

        c.Save();
        using (var clip = new Path())
        {
            clip.AddOval(disc, Path.Direction.Cw!);
            c.ClipPath(clip);
        }
        using (var back = Wave(disc, surface - Dp(2), amp * 0.9f, -tilt * 0.6f, -phase * 0.85f + 2.2f))
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = Palette.Alpha(color, _p.Dark ? 60 : 70) })
            c.DrawPath(back, p);
        using (var front = Wave(disc, surface, amp, tilt, phase))
        using (var p = new Paint(PaintFlags.AntiAlias))
        {
            p.SetShader(new LinearGradient(0, surface - amp - Math.Abs(tilt), 0, disc.Bottom,
                Palette.Alpha(color, _p.Dark ? 150 : 170), Palette.Alpha(color, _p.Dark ? 80 : 95), Shader.TileMode.Clamp!));
            c.DrawPath(front, p);
        }
        using (var edge = WaveLine(disc, surface, amp, tilt, phase))
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = AColor.Argb(_p.Dark ? 110 : 170, 255, 255, 255), StrokeWidth = Dp(1.4f) })
        {
            p.SetStyle(Paint.Style.Stroke);
            c.DrawPath(edge, p);
        }
        if (charging)
        {
            using var bb = new Paint(PaintFlags.AntiAlias);
            float cx = disc.CenterX();
            for (int i = 0; i < 6; i++)
            {
                float period = 1500 + i * 240;
                float k = ((now + i * 397) % (long)period) / period;
                float y = disc.Bottom - Dp(4) - k * (disc.Bottom - surface);
                if (y < surface + Dp(3)) continue;
                float x = cx + (i - 2.5f) * disc.Width() * 0.12f + MathF.Sin(i * 1.7f + k * 6.3f) * Dp(3);
                float rad = Dp(1.4f + (i % 3) * 0.7f);
                bb.Color = AColor.Argb((int)(200 * (1 - k * 0.8f)), 255, 255, 255);
                c.DrawCircle(x, y, rad, bb);
            }
        }
        c.Restore();
    }

    Path WaveLine(RectF disc, float surface, float amp, float tilt, float phase)
    {
        var path = new Path();
        float step = Dp(2.5f), x0 = disc.Left - Dp(2), x1 = disc.Right + Dp(2);
        float cx = disc.CenterX(), half = Math.Max(1, disc.Width() / 2);
        bool first = true;
        for (float x = x0; x <= x1 + 0.01f; x += step)
        {
            float u = (x - disc.Left) / Math.Max(1, disc.Width());
            float y = surface + tilt * (x - cx) / half
                + amp * MathF.Sin(u * MathF.PI * 2.6f + phase)
                + amp * 0.35f * MathF.Sin(u * MathF.PI * 5.1f - phase * 1.4f);
            if (first) { path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
        }
        return path;
    }

    Path Wave(RectF disc, float surface, float amp, float tilt, float phase)
    {
        var path = WaveLine(disc, surface, amp, tilt, phase);
        path.LineTo(disc.Right + Dp(2), disc.Bottom + Dp(2));
        path.LineTo(disc.Left - Dp(2), disc.Bottom + Dp(2));
        path.Close();
        return path;
    }

    // ───────── text ─────────

    static readonly Typeface Regular = Typeface.Create("sans-serif", TypefaceStyle.Normal)!;
    static readonly Typeface Bold = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal)!;

    static Paint TextPaint(float size, bool bold) => new(PaintFlags.AntiAlias | PaintFlags.SubpixelText)
    {
        TextSize = size,
        Typeface = bold ? Bold : Regular,
    };

    void Text(Canvas c, string s, float x, float baseline, float size, AColor color, bool bold = false, float maxW = 0, float spacing = 0)
    {
        using var p = TextPaint(size, bold);
        p.Color = color;
        if (spacing > 0) p.LetterSpacing = spacing;
        c.DrawText(maxW > 0 ? Fit(s, p, maxW) : s, x, baseline, p);
    }

    void TextRight(Canvas c, string s, float right, float baseline, float size, AColor color, bool bold)
    {
        using var p = TextPaint(size, bold);
        p.Color = color;
        p.TextAlign = Paint.Align.Right;
        c.DrawText(s, right, baseline, p);
    }

    void TextCentered(Canvas c, string s, float cx, float baseline, float size, AColor color, bool bold)
    {
        using var p = TextPaint(size, bold);
        p.Color = color;
        p.TextAlign = Paint.Align.Center;
        c.DrawText(Fit(s, p, Width - Dp(32)), cx, baseline, p);
    }

    /// <summary>Shortens text with «…» to fit the width.</summary>
    static string Fit(string s, Paint p, float maxW)
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
                if (_heroR.Contains(_downX, _downY)) Slosh(0.6f);
                return true;
            case MotionEventActions.Up:
                float x = e.GetX(), y = e.GetY();
                if (Math.Abs(x - _downX) > Dp(10) || Math.Abs(y - _downY) > Dp(10)) return true;
                if (_permR.Contains(x, y)) PermissionRequested?.Invoke();
                else if (_updR.Contains(x, y)) UpdateRequested?.Invoke();
                else if (_heroR.Contains(x, y)) Slosh(0.9f);
                PerformClick();
                return true;
        }
        return true;
    }

    public override bool PerformClick() => base.PerformClick();
}
