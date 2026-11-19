using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AColor = Android.Graphics.Color;
using Path = Android.Graphics.Path;

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
/// (earbuds get L / R / case rings), a gear that opens Settings, and banners (permission, Bluetooth off, update).
/// Lives inside a ScrollView: reports its content height in OnMeasure.
/// </summary>
internal sealed class GlanceView : GlassView
{
    readonly Devices _dev;
    readonly Dictionary<string, (float From, float To, long T0)> _tweens = new();

    public string VersionText { get; set; } = "";
    public UpdateInfo? Update { get; set; }
    public event Action? PermissionRequested, UpdateRequested;

    /// <summary>False until «Поверх других приложений» is allowed: a banner offers the connect popup.</summary>
    public bool PopupAllowed { get; set; } = true;
    public event Action? PopupPermissionRequested;
    RectF _popR = new();

    // hit areas
    RectF _heroR = new(), _permR = new(), _updR = new(), _gearR = new();
    long _gearT0 = -10_000;

    /// <summary>The gear in the header: opens Settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>«Низкий заряд» threshold from Settings: the hero says «Пора зарядить» at or below it.</summary>
    public int LowAt { get; set; } = 20;

    public GlanceView(Context c, Devices dev) : base(c)
    {
        _dev = dev;
    }

    // ───────── layout ─────────

    const float Gap = 12, HeroH = 148, CardH = 84, BudsH = 150, BannerH = 64;

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
        if (!PopupAllowed && !_dev.Demo) y += BannerH + Gap;
        if (_dev.MissingPermission || _dev.BluetoothOff || _dev.NoBluetooth) y += BannerH + 28 + Gap;
        if (Hero(list) is not null) y += HeroH + Gap + 28;
        if (list.Count == 0) y += 200;
        else y += 28 + list.Sum(a => (a.HasParts ? BudsH : CardH) + Gap);
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

    bool Animating => Now - _born < 1400 || _tweens.Values.Any(t => Now - t.T0 < 720) || Now - _sloshT0 < SloshMs
        || _cardSlosh.Values.Any(s => Now - s.T0 < SloshMs);

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
        Text(canvas, sub, Dp(17), y + Dp(56), Dp(14), _p.Sub, maxW: w - Dp(17 + 16));
        float gr = Dp(21);
        _gearR = new RectF(w - Dp(Side) - 2 * gr, y + Dp(20) - gr, w - Dp(Side), y + Dp(20) + gr);
        DrawGearButton(canvas, _gearR);
        y += Dp(96 - 28);

        if (Update is { } up)
        {
            _updR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(BannerH));
            var ur = _updR;
            Shift(canvas, idx++, () => DrawUpdate(canvas, ur, up));
            y += Dp(BannerH + Gap);
        }
        else _updR = new RectF();

        if (!PopupAllowed && !_dev.Demo)
        {
            _popR = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(BannerH));
            var pr = _popR;
            Shift(canvas, idx++, () => Banner(canvas, pr, "Заряд сразу при подключении", "Разрешите «Поверх других приложений»", _p.Accent, true));
            y += Dp(BannerH + Gap);
        }
        else _popR = new RectF();

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
            TextCentered(canvas, "Подключите наушники, часы", w / 2, cy + Dp(112), Dp(13.5f), _p.Sub, false);
            TextCentered(canvas, "или геймпад по Bluetooth", w / 2, cy + Dp(131), Dp(13.5f), _p.Sub, false);
            y += Dp(200);
        }
        else
        {
            y += Dp(28);
            Section(canvas, "Все устройства", y);
            _budsR.Clear();
            foreach (var a in list)
            {
                float h = a.HasParts ? BudsH : CardH;
                var r = new RectF(Dp(Side), y, w - Dp(Side), y + Dp(h));
                var acc = a;
                if (a.HasParts) _budsR.Add((r, a.Key));
                Shift(canvas, idx++, () => DrawCard(canvas, r, acc));
                y += Dp(h + Gap);
            }
        }


        if (Animating || list.Any(a => a.Charging || a.HasParts && (a.Left.Charging || a.Right.Charging || a.Case.Charging)) || (hero?.Charging ?? false)) PostInvalidateOnAnimation();
        if (!_drawnOnce) { _drawnOnce = true; Android.Util.Log.Info("Glance", $"drawn: {list.Count} devices, demo={_dev.Demo}"); }
    }

    bool _drawnOnce;

    /// <summary>Glass button with a gear in the header; the gear turns when tapped.</summary>
    void DrawGearButton(Canvas c, RectF r)
    {
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = _p.Card }) c.DrawOval(r, p);
        using (var b = new Paint(PaintFlags.AntiAlias) { Color = _p.CardBorder, StrokeWidth = Math.Max(1, Dp(1)) })
        {
            b.SetStyle(Paint.Style.Stroke);
            c.DrawOval(r, b);
        }
        float k = Math.Clamp((Now - _gearT0) / 520f, 0, 1);
        float angle = 90 * (1 - MathF.Pow(1 - k, 3));
        Gear(c, r.CenterX(), r.CenterY(), r.Width() * 0.27f, angle, _p.Text);
        if (k < 1) PostInvalidateOnAnimation();
    }

    float _updShown; // the liquid in the banner flows to the real progress instead of jumping

    /// <summary>
    /// The update banner follows the download: liquid fills the glass from the left with a rippling edge and a sheen
    /// running through it, then «Установить»; Android's own confirmation follows the tap.
    /// </summary>
    void DrawUpdate(Canvas c, RectF r, UpdateInfo up)
    {
        string v = Fmt.Ver(up.Version);
        var st = Updater.Target?.Version == up.Version ? Updater.Stage : UpdateStage.Idle;
        (string title, string sub, AColor tint) = st switch
        {
            UpdateStage.Downloading => ($"Скачивается версия {v}", Updater.Total > 0
                ? $"{Updater.Mb(Updater.Done)} из {Updater.Mb(Updater.Total)} МБ · {(int)(Updater.Progress * 100)}%" : $"{Updater.Mb(Updater.Done)} МБ", _p.Accent),
            UpdateStage.Ready => ($"Версия {v} скачана", "Нажмите, чтобы установить", Palette.Green),
            UpdateStage.Installing => ($"Устанавливается {v}", "Подтвердите обновление", Palette.Green),
            UpdateStage.NeedPermission => ("Разрешите установку", "Glance → «Разрешить из этого источника»", Palette.Amber),
            UpdateStage.Failed => ("Обновление не скачалось", Updater.Error.Length > 0 ? Updater.Error : "Нажмите, чтобы повторить", Palette.Red),
            _ => ($"Доступна версия {v}", "Нажмите, чтобы скачать и установить", _p.Accent),
        };
        if (st != UpdateStage.Downloading)
        {
            _updShown = st is UpdateStage.Ready or UpdateStage.Installing ? 1 : 0;
            Banner(c, r, title, sub, tint, st != UpdateStage.Installing);
            if (st == UpdateStage.Installing) PostInvalidateOnAnimation();
            return;
        }

        float target = Updater.Progress;
        _updShown += (target - _updShown) * 0.18f;
        if (Math.Abs(target - _updShown) < 0.002f) _updShown = target;
        float t = Now / 1000f;
        Glass(c, r, Dp(20));
        c.Save();
        using (var clip = new Path())
        {
            clip.AddRoundRect(r, Dp(20), Dp(20), Path.Direction.Cw!);
            c.ClipPath(clip);
        }
        // liquid with a rippling front edge
        float edge = r.Left + r.Width() * _updShown;
        using (var liquid = new Path())
        {
            liquid.MoveTo(r.Left, r.Top);
            liquid.LineTo(edge, r.Top);
            for (float yy = r.Top; yy <= r.Bottom + 1; yy += Dp(4))
            {
                float k = (yy - r.Top) / r.Height();
                liquid.LineTo(edge + Dp(4) * MathF.Sin(k * 2 * MathF.PI * 1.3f + t * 5f) * Math.Min(1, _updShown * 8), yy);
            }
            liquid.LineTo(r.Left, r.Bottom);
            liquid.Close();
            using var lp = new Paint(PaintFlags.AntiAlias);
            lp.SetShader(new LinearGradient(r.Left, 0, Math.Max(r.Left + 1, edge), 0,
                Palette.Alpha(tint, _p.Dark ? 40 : 30), Palette.Alpha(tint, _p.Dark ? 110 : 85), Shader.TileMode.Clamp!));
            c.DrawPath(liquid, lp);
            // a light sheen sliding through the liquid
            float sx = r.Left + (r.Width() * 1.4f) * ((t * 0.45f) % 1f) - r.Width() * 0.2f;
            if (sx < edge)
            {
                using var sh = new Paint(PaintFlags.AntiAlias);
                sh.SetShader(new LinearGradient(sx - Dp(30), 0, sx + Dp(30), 0,
                    new[] { AColor.Argb(0, 255, 255, 255).ToArgb(), AColor.Argb(_p.Dark ? 60 : 90, 255, 255, 255).ToArgb(), AColor.Argb(0, 255, 255, 255).ToArgb() },
                    null, Shader.TileMode.Clamp!));
                c.ClipPath(liquid);
                c.DrawRect(r, sh);
            }
        }
        c.Restore();
        // a pulsing dot: work in progress
        float pulse = 0.5f + 0.5f * MathF.Sin(t * 6f);
        using (var halo = new Paint(PaintFlags.AntiAlias) { Color = Palette.Alpha(tint, (int)(70 * pulse)) })
            c.DrawCircle(r.Left + Dp(24), r.CenterY(), Dp(5 + 4 * pulse), halo);
        using (var dot = new Paint(PaintFlags.AntiAlias) { Color = tint })
            c.DrawCircle(r.Left + Dp(24), r.CenterY(), Dp(5), dot);
        Text(c, title, r.Left + Dp(42), r.Top + Dp(27), Dp(15.5f), _p.Text, bold: true, maxW: r.Width() - Dp(60));
        Text(c, sub, r.Left + Dp(42), r.Top + Dp(47), Dp(12.5f), _p.Sub, maxW: r.Width() - Dp(60));
        PostInvalidateOnAnimation(); // only while downloading
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
        int? lvl0 = a.Level;
        float pctW = MeasureBig(lvl0 is int l0 ? $"{l0}%" : "—") + Dp(a.Charging ? 34 : 14);
        Text(c, a.DisplayName, tx, r.Top + Dp(37), Dp(16.5f), _p.Text, bold: true, maxW: r.Right - tx - pctW - Dp(12));
        string link = phoneCard ? (a.Charging ? "Заряжается" : a.Profile.Brand) : (a.Charging ? "Заряжается · " : "") + a.Link;
        string feat = phoneCard ? "" : Profiles.Features(a.Profile);
        if (feat.Length > 0) link += " · " + feat;
        Text(c, link, tx, r.Top + Dp(58), Dp(12.5f), _p.Sub, maxW: r.Right - tx - Math.Max(pctW, Dp(74)) - Dp(12)); // clear of the bar

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
            // case: live, remembered («Кейс · 2 ч»), or not reported yet («Откройте кейс»)
            var age = _dev.CaseAge(a.Key);
            string caseLabel = !a.Case.Known ? "Откройте кейс" : age is TimeSpan t ? $"Кейс · {Ago(t)}" : "Кейс";
            var parts = new[] { ("Левый", a.Left), ("Правый", a.Right), (caseLabel, a.Case) };
            for (int i = 0; i < 3; i++)
            {
                var (label, part) = parts[i];
                float cx = r.Left + Dp(16) + cw * i + cw / 2;
                MiniRing(c, cx, top + Dp(22), Dp(20), part, $"{a.Key}:{i}", stale: i == 2 && age is not null);
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

    static string Ago(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)} мин" : t.TotalHours < 48 ? $"{(int)t.TotalHours} ч" : $"{(int)t.TotalDays} дн";

    void MiniRing(Canvas c, float cx, float cy, float rad, Part part, string key, bool stale = false)
    {
        var box = new RectF(cx - rad, cy - rad, cx + rad, cy + rad);
        using var p = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Dp(4.5f), StrokeCap = Paint.Cap.Round, Color = _p.Track };
        p.SetStyle(Paint.Style.Stroke);
        c.DrawOval(box, p);
        if (part.Level is int l)
        {
            float shown = Tween("P:" + key, l);
            var col = stale ? Palette.Alpha(Palette.Level(l, false), 120) : Palette.Level(l, part.Charging);
            // live liquid inside the ring, like the hero: rocks when the card is tapped, bubbles while charging
            float inset = Dp(4.5f);
            string card = key[..key.LastIndexOf(':')];
            var (st0, spow) = _cardSlosh.TryGetValue(card, out var cs) ? cs : (_sloshT0, _sloshPower);
            DrawLiquid(c, new RectF(box.Left + inset, box.Top + inset, box.Right - inset, box.Bottom - inset),
                shown, stale ? Palette.Alpha(col, 150) : col, part.Charging && !stale, st0, stale ? spow * 0.5f : spow);
            p.Color = col;
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
        float d = Dp(92), th = Dp(9);
        var ring = new RectF(r.Left + Dp(18), r.CenterY() - d / 2, r.Left + Dp(18) + d, r.CenterY() + d / 2);
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
        TextCentered(c, $"{(int)MathF.Round(shown)}%", ring.CenterX(), ring.CenterY() + Dp(8), Dp(22), _p.Text, true);

        float tx = ring.Right + Dp(18), tw = r.Right - tx - Dp(14);
        var tile = new RectF(tx, r.Top + Dp(26), tx + Dp(22), r.Top + Dp(48));
        Icons.Draw(c, a.Kind, tile, Palette.KindColor(a.Kind), Dp(2));
        Text(c, a.DisplayName, tx + Dp(28), r.Top + Dp(43), Dp(16), _p.Text, bold: true, maxW: tw - Dp(28));
        string line = a.Charging ? "Заряжается" : lvl <= LowAt ? "Пора зарядить" : lvl <= 40 ? "Разрядится первым" : "Всё в порядке";
        Text(c, line, tx, r.Top + Dp(74), Dp(14.5f), lvl <= LowAt && !a.Charging ? Palette.Red : _p.Text, maxW: tw);
        string sub = a.HasParts ? $"Левый {Fmt.Percent(a.Left.Level)} · Правый {Fmt.Percent(a.Right.Level)}" : a.Link;
        Text(c, sub, tx, r.Top + Dp(98), Dp(12.5f), _p.Sub, maxW: tw);
    }

    void DrawLiquid(Canvas c, RectF disc, float level, AColor color, bool charging, long? sloshT0 = null, float sloshPower = -1)
    {
        if (level <= 0.5f || disc.Width() < Dp(8)) return;
        long now = Now;
        long t0 = sloshT0 ?? _sloshT0;
        float power = sloshPower >= 0 ? sloshPower : _sloshPower;
        float size = Math.Clamp(disc.Width() / Dp(66), 0.3f, 1f); // small rings rock gentler than the hero
        float dt = now - t0;
        float push = dt >= 0 && dt < SloshMs ? power * MathF.Exp(-dt / 700f) : 0;
        float tilt = push * MathF.Sin(Math.Max(0, dt) / 160f) * Dp(9) * size;
        float amp = ((charging ? Dp(2.2f) : Dp(1.4f)) + push * Dp(3.4f)) * MathF.Sqrt(size);
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

    // ───────── touch ─────────

    float _downX, _downY;

    /// <summary>Per earbuds card: when its little rings were last pushed (tap) and how hard.</summary>
    readonly Dictionary<string, (long T0, float Power)> _cardSlosh = new();
    readonly List<(RectF R, string Key)> _budsR = new();

    void SloshCard(string key, float power)
    {
        long now = Now;
        float left = _cardSlosh.TryGetValue(key, out var cs) && now - cs.T0 < SloshMs ? cs.Power * MathF.Exp(-(now - cs.T0) / 700f) : 0;
        _cardSlosh[key] = (now, Math.Min(1.4f, left + power));
        PostInvalidateOnAnimation();
    }

    string? BudsAt(float x, float y)
    {
        foreach (var (r, k) in _budsR) if (r.Contains(x, y)) return k;
        return null;
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.Action)
        {
            case MotionEventActions.Down:
                _downX = e.GetX();
                _downY = e.GetY();
                if (_heroR.Contains(_downX, _downY)) Slosh(0.6f);
                else if (BudsAt(_downX, _downY) is { } bk) SloshCard(bk, 0.7f);
                return true;
            case MotionEventActions.Up:
                float x = e.GetX(), y = e.GetY();
                if (Math.Abs(x - _downX) > Dp(10) || Math.Abs(y - _downY) > Dp(10)) return true;
                if (_permR.Contains(x, y)) PermissionRequested?.Invoke();
                else if (_updR.Contains(x, y)) UpdateRequested?.Invoke();
                else if (_popR.Contains(x, y)) PopupPermissionRequested?.Invoke();
                else if (_heroR.Contains(x, y)) Slosh(0.9f);
                else if (BudsAt(x, y) is { } tapped)
                {
                    SloshCard(tapped, 0.8f);
                    PerformHapticFeedback(FeedbackConstants.ContextClick);
                }
                else if (Grow(_gearR, Dp(8)).Contains(x, y))
                {
                    _gearT0 = Now;
                    PerformHapticFeedback(FeedbackConstants.VirtualKey);
                    Invalidate();
                    SettingsRequested?.Invoke();
                }
                PerformClick();
                return true;
        }
        return true;
    }

    public override bool PerformClick() => base.PerformClick();
}
