using System.Diagnostics;

namespace Glance;

/// <summary>Main panel opened from the tray: all accessories, per-bud levels, 24 h history, estimates.</summary>
internal sealed class FlyoutForm : CardForm
{
    const float W = 380, Header = 52, Footer = 56, Gap = 8, Side = 12, Banner = 44;

    readonly List<(Accessory? A, string? Section, RectangleF R)> _items = new();
    RectangleF _btBtn, _gearBtn, _bannerRect;
    int _hot = -1;
    string _hotBtn = "";
    float _scroll, _contentH, _viewH, _scrollTarget;
    Animator.Anim? _scrollAnim;
    bool _closing;

    public string? PendingUpdate { get; set; }
    public event Action? SettingsRequested, UpdateRequested;
    /// <summary>The update icon in the footer: check now (or install when an update is waiting).</summary>
    public event Func<Task>? CheckUpdateRequested;
    float _openT = 1, _spin;
    bool _checking;
    RectangleF _updBtn;

    /// <summary>0…1 entrance progress of the card at <paramref name="i"/> (staggered by 45 ms).</summary>
    float Arrive(int i)
    {
        if (_openT >= 1) return 1;
        float ms = _openT * 560 - i * 45;
        return Animator.EaseOut(Math.Clamp(ms / 300f, 0, 1));
    }
    public event Action<Accessory, Point>? DeviceMenuRequested;

    public FlyoutForm() : base(true) { }

    public void ShowNear(Rectangle? anchor)
    {
        UseScaleAt(anchor?.Location ?? Cursor.Position);
        Relayout();
        var p = PlaceNearTray(Size, anchor);
        Location = new Point(p.X, p.Y + Px(12));
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        int baseY = p.Y;
        Location = new Point(p.X, p.Y + Px(22));
        Animator.Run(380, k => { if (!IsDisposed) Top = baseY + (int)MathF.Round(Px(22) * (1 - k)); }, null, Animator.Spring);
        // Cards arrive one after another and their levels count up.
        _openT = 0;
        Animator.Run(560, k => { if (IsDisposed) return; _openT = k; Invalidate(); }, () => _openT = 1, Animator.Linear);
        Slosh(1f); // the panel springs up — the liquid in the hero ring splashes and settles
    }

    public void Rebuild()
    {
        if (IsDisposed || _closing) return;
        int oldH = Height;
        Relayout();
        Top += oldH - Height; // keep the bottom edge anchored above the taskbar
        Invalidate();
    }

    List<(Accessory? A, string? Section)> Entries()
    {
        var list = new List<(Accessory?, string?)>();
        var connected = Svc.R.ConnectedDevices.Where(a => !IsServiceEntry(a))
            .OrderByDescending(a => a.Level.HasValue || a.HasParts)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var a in connected) list.Add((a, null));
        var recent = Svc.R.All
            .Where(a => !a.Connected && !IsServiceEntry(a) && a.LastLevel.HasValue && (DateTime.Now - a.LastSeen).TotalDays < 30)
            .OrderByDescending(a => a.LastSeen).Take(6).ToList();
        if (recent.Count > 0)
        {
            // collapsed by default: one line «Недавно · 4 ›», opens on click
            list.Add((null, RecentTag));
            if (_recentOpen) foreach (var a in recent) list.Add((a, null));
        }
        _recentCount = recent.Count;
        return list;
    }

    const string RecentTag = "\u0001recent";
    bool _recentOpen;
    int _recentCount;

    /// <summary>Apple «Find My» beacons and similar service entries are not accessories.</summary>
    static bool IsServiceEntry(Accessory a) =>
        a.Name.Contains("Find My", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Локатор", StringComparison.OrdinalIgnoreCase);

    protected override void Relayout()
    {
        _items.Clear();
        float y = Header;
        var entries = Entries();
        _hero = PickHero();
        if (_hero is not null)
        {
            _items.Add((null, HeroTag, new RectangleF(Side, y, W - 2 * Side, HeroH)));
            y += HeroH + Gap;
        }
        foreach (var (a, section) in entries)
        {
            float h = section == RecentTag ? 34 : section is not null ? 26 : DeviceCard.Height(a!, true);
            if (a is not null && a.Key == _expKey) h += Expand * _expand; // hovered card opens a strip below
            _items.Add((a, section, new RectangleF(Side, y, W - 2 * Side, h)));
            y += h + Gap;
        }
        if (entries.Count == 0) y += 150;
        // Room for the hover strip is reserved at the end: opening it pushes the cards below down into this
        // space, so the panel never resizes or jumps and nothing is covered.
        else y += Expand * (_expKey is null ? 1 : 1 - _expand);
        _contentH = y - Header;
        float extra = Footer + (PendingUpdate is not null ? Banner : 0);
        var wa = Screen.FromPoint(Location.IsEmpty ? Cursor.Position : Location).WorkingArea;
        float maxView = wa.Height / Sc * 0.85f - Header - extra;
        _viewH = Math.Min(_contentH, Math.Max(120, maxView));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - _viewH));
        Size = new Size(Px(W), Px(Header + _viewH + extra));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc;
        var t = T;

        // header
        Painter.Text(g, "Аксессуары", Fonts.Strong(16 * s), t.Text, new RectangleF(16 * s, 14 * s, 200 * s, 26 * s));
        if (Pro.IsActive)
        {
            var badge = new RectangleF(16 * s + Painter.Width(g, "Аксессуары", Fonts.Strong(16 * s)) + 8 * s, 18 * s, 34 * s, 18 * s);
            Painter.FillRound(g, badge, 9 * s, t.AccentFill);
            Painter.Text(g, "PRO", Fonts.Strong(10 * s), t.OnAccent, badge, StringAlignment.Center);
        }
        int count = Svc.R.ConnectedDevices.Count();
        Painter.Text(g, Summary(count), Fonts.Regular(12 * s), t.SubText,
            new RectangleF(W * s - 176 * s, 16 * s, 160 * s, 22 * s), StringAlignment.Far);

        // list (clipped)
        var view = new RectangleF(0, Header * s, W * s, _viewH * s);
        var state = g.Save();
        g.SetClip(view);
        if (_items.Count == 0)
        {
            var c = new RectangleF(0, Header * s + 10 * s, W * s, 140 * s);
            Painter.Glyph(g, Glyphs.Bluetooth, 30 * s, t.SubText, new RectangleF(c.X, c.Y + 10 * s, c.Width, 40 * s));
            Painter.Text(g, "Нет подключённых аксессуаров", Fonts.Strong(14 * s), t.Text, new RectangleF(c.X, c.Y + 60 * s, c.Width, 22 * s), StringAlignment.Center);
            Painter.Text(g, "Подключите наушники, мышь или геймпад по Bluetooth", Fonts.Regular(12 * s), t.SubText, new RectangleF(c.X + 20 * s, c.Y + 84 * s, c.Width - 40 * s, 36 * s), StringAlignment.Center, StringAlignment.Near, wrap: true);
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var (a, section, r0) = _items[i];
            var r = Scaled(r0);
            float arr = Arrive(i);
            if (arr < 1) r.Offset(0, (1 - arr) * 16 * s);
            if (r.Bottom < view.Top || r.Top > view.Bottom) continue;
            if (section == HeroTag) DrawHero(g, r, s, Arrive(i + 3));
            else if (section == RecentTag)
            {
                bool hot = _hotRecent;
                if (hot) Painter.FillRound(g, r, 10 * s, t.SurfaceHover);
                Painter.Text(g, $"Недавно · {_recentCount}", Fonts.Strong(12.5f * s), t.SubText, new RectangleF(r.X + 6 * s, r.Y, r.Width - 40 * s, r.Height));
                var ch = new RectangleF(r.Right - 30 * s, r.Y, 24 * s, r.Height);
                var st2 = g.Save();
                g.TranslateTransform(ch.X + ch.Width / 2, ch.Y + ch.Height / 2);
                g.RotateTransform(_recentOpen ? 90 : 0);
                g.TranslateTransform(-(ch.X + ch.Width / 2), -(ch.Y + ch.Height / 2));
                Painter.Chevron(g, ch, t.SubText, 1.8f * s);
                g.Restore(st2);
            }
            else if (section is not null)
                Painter.Text(g, section, Fonts.Strong(12 * s), t.SubText, new RectangleF(r.X + 4 * s, r.Y + 4 * s, r.Width, r.Height - 4 * s));
            else
            {
                DeviceCard.Draw(g, r, a!, s, i == _hot, true, Arrive(i + 2));
                if (i == _hot && t.Glass) CursorLight(g, r, s);
                if (a!.Key == _expKey && _expand > 0.05f)
                {
                    var st2 = g.Save();
                    g.SetClip(RectangleF.Intersect(view, r));
                    var dots = MoreRect(r);
                    var c = Theme.Alpha(t.Text, (int)(255 * Math.Clamp((_expand - 0.3f) / 0.7f, 0, 1)));
                    if (_hotMore) Painter.FillRound(g, dots, dots.Height / 2, t.SurfaceHover);
                    Painter.Glyph(g, Glyphs.More, 18 * s, c, new RectangleF(dots.X + 6 * s, dots.Y, 26 * s, dots.Height));
                    Painter.Text(g, "Действия", Fonts.Regular(12.5f * s), c, new RectangleF(dots.X + 34 * s, dots.Y, dots.Width - 38 * s, dots.Height));
                    g.Restore(st2);
                }
            }
        }
        DrawRipples(g, s);
        g.Restore(state);
        if (LevelTween.Animating || _ripples.Count > 0) EnsureTick();

        // scroll hint
        if (_contentH > _viewH)
        {
            float trackH = _viewH * s, thumbH = Math.Max(24 * s, trackH * _viewH / _contentH);
            float thumbY = Header * s + (trackH - thumbH) * (_scroll / Math.Max(1, _contentH - _viewH));
            Painter.FillRound(g, new RectangleF(W * s - 5 * s, thumbY, 3 * s, thumbH), 1.5f * s, Theme.Alpha(t.SubText, 120));
        }

        float y = (Header + _viewH) * s;

        // update banner
        if (PendingUpdate is not null)
        {
            _bannerRect = new RectangleF(Side * s, y + 4 * s, (W - 2 * Side) * s, (Banner - 8) * s);
            var fill = _hotBtn == "banner" ? Theme.Mix(t.AccentFill, Color.White, 0.12f) : t.AccentFill;
            Painter.FillRound(g, _bannerRect, 10 * s, fill);
            Painter.GlassSheen(g, _bannerRect, 10 * s, t.Dark);
            Painter.Glyph(g, Glyphs.Download, 14 * s, t.OnAccent, new RectangleF(_bannerRect.X + 8 * s, _bannerRect.Y, 24 * s, _bannerRect.Height));
            Painter.Text(g, $"Доступна версия {PendingUpdate}", Fonts.Strong(13 * s), t.OnAccent,
                new RectangleF(_bannerRect.X + 38 * s, _bannerRect.Y, _bannerRect.Width - 140 * s, _bannerRect.Height));
            Painter.Text(g, "Обновить ›", Fonts.Strong(13 * s), t.OnAccent,
                new RectangleF(_bannerRect.Right - 104 * s, _bannerRect.Y, 94 * s, _bannerRect.Height), StringAlignment.Far);
            y += Banner * s;
        }
        else _bannerRect = RectangleF.Empty;

        // footer
        using (var pen = new Pen(t.Border, Math.Max(1, s))) g.DrawLine(pen, 0, y, W * s, y);
        var btFont = Fonts.Regular(14.5f * s);
        float btW = 40 * s + Painter.Width(g, "Bluetooth", btFont) + 14 * s;
        _btBtn = new RectangleF(Side * s, y + 10 * s, btW, 36 * s);
        _gearBtn = new RectangleF((W - Side - 38) * s, y + 9 * s, 38 * s, 38 * s);
        _updBtn = new RectangleF(_gearBtn.X - 44 * s, _gearBtn.Y, 38 * s, 38 * s);
        if (_hotBtn == "upd") Painter.FillRound(g, RectangleF.Inflate(_updBtn, -2 * s, -2 * s), 17 * s, t.SurfaceHover);
        DrawUpdateIcon(g, _updBtn, s);
        if (_hotBtn == "bt") Painter.FillRound(g, _btBtn, 18 * s, t.SurfaceHover);
        if (_hotBtn == "gear") Painter.FillRound(g, RectangleF.Inflate(_gearBtn, -2 * s, -2 * s), 17 * s, t.SurfaceHover); // round, snug
        Painter.Glyph(g, Glyphs.Bluetooth, 16 * s, t.Text, new RectangleF(_btBtn.X + 8 * s, _btBtn.Y, 28 * s, _btBtn.Height));
        Painter.Text(g, "Bluetooth", btFont, t.Text, new RectangleF(_btBtn.X + 40 * s, _btBtn.Y, _btBtn.Width - 42 * s, _btBtn.Height));
        Painter.Glyph(g, Glyphs.Settings, 20 * s, t.Text, _gearBtn);
    }

    /// <summary>The «⋯ Действия» button in the strip that opens under a hovered card (covers nothing).</summary>
    RectangleF MoreRect(RectangleF card) => new(card.Right - 124 * Sc, card.Bottom - (Expand - 2) * Sc, 114 * Sc, (Expand - 8) * Sc);
    bool _hotMore;

    const float Expand = 36;
    string? _expKey;
    float _expand;
    Animator.Anim? _expAnim;

    /// <summary>Smoothly opens the strip under the hovered card and closes the previous one.</summary>
    void SetExpanded(string? key)
    {
        if (key == _expKey && _expAnim is null) return;
        _expAnim?.Cancel();
        if (key != _expKey) { _expKey = key; _expand = 0; }
        if (key is null) { Rebuild(); return; }
        float from = _expand;
        _expAnim = Animator.Run(150, p => { if (IsDisposed) return; _expand = from + (1 - from) * p; Rebuild(); }, () => _expAnim = null);
    }

    // ───────────── Hero: the device that needs attention first ─────────────

    const string HeroTag = "\u0001hero";
    const float HeroH = 112;
    Accessory? _hero;

    /// <summary>The weakest discharging device (or the one charging) — shown big at the top.</summary>
    static Accessory? PickHero()
    {
        var withLevel = Svc.R.ConnectedDevices.Where(a => a.WeakestLevel is not null).ToList();
        if (withLevel.Count == 0) return null;
        return withLevel.Where(a => !a.Charging).MinBy(a => a.WeakestLevel) ?? withLevel.MinBy(a => a.WeakestLevel);
    }

    /// <summary>Big glass card with an animated ring gauge, the name, and what the level means in time.</summary>
    void DrawHero(Graphics g, RectangleF r, float s, float grow)
    {
        if (_hero is not Accessory a || a.WeakestLevel is not int lvl) return;
        var t = T;
        Painter.FillRound(g, r, 16 * s, t.Surface);
        Painter.GlassSheen(g, r, 16 * s, t.Dark);
        Painter.StrokeRound(g, r, 16 * s, t.Border, Math.Max(1, s));

        // ring gauge
        float d = 76 * s, th = 8 * s;
        var ring = new RectangleF(r.X + 18 * s, r.Y + (r.Height - d) / 2, d, d);
        float shown = LevelTween.Value("H:" + a.Key, lvl) * grow;
        var color = t.BarColor(lvl, a.Charging, Svc.Low);
        using (var track = new Pen(Theme.Alpha(t.Track, 170), th) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            g.DrawEllipse(track, ring);
        if (shown > 0.5f)
        {
            float sweep = 360f * shown / 100f;
            if (t.Glass)
                using (var glow = new Pen(Theme.Alpha(color, 60), th * 2.2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawArc(glow, ring, -90, sweep);
            using var arc = new Pen(color, th) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawArc(arc, ring, -90, sweep);
        }
        DrawLiquid(g, RectangleF.Inflate(ring, -(th / 2 + 3 * s), -(th / 2 + 3 * s)), shown, color, a.Charging, s);
        Painter.Text(g, $"{(int)MathF.Round(shown)}%", Fonts.Big(19 * s), t.Text, ring, StringAlignment.Center);
        if (a.Charging)
            Painter.Glyph(g, Glyphs.Bolt, 11 * s, Theme.Alpha(t.Good, (int)(175 + 80 * MathF.Sin(Clock.ElapsedMilliseconds / 260f))),
                new RectangleF(ring.X, ring.Bottom - 26 * s, ring.Width, 16 * s));

        // text
        float tx = ring.Right + 18 * s, tw = r.Right - tx - 14 * s;
        Painter.Glyph(g, Glyphs.For(a.Kind), 13 * s, a.Profile.BrandColor ?? t.AccentText, new RectangleF(tx - 2 * s, r.Y + 18 * s, 18 * s, 22 * s));
        Painter.Text(g, a.DisplayName, Fonts.Strong(15 * s), t.Text, new RectangleF(tx + 20 * s, r.Y + 18 * s, tw - 20 * s, 22 * s));
        string line = a.Charging
            ? (Svc.Estimate(a) is TimeSpan c ? $"Заряжается · до полного ~{Fmt.Duration(c)}" : "Заряжается")
            : (Svc.Estimate(a) is TimeSpan e ? $"Хватит примерно на {Fmt.Duration(e)}" : "Считаем, на сколько хватит…");
        Painter.Text(g, line, Fonts.Regular(13 * s), t.Text, new RectangleF(tx, r.Y + 46 * s, tw, 20 * s));
        string hint = a.Charging ? a.Link : lvl <= Svc.Low ? "Пора зарядить" : lvl <= 40 ? $"{a.Link} · разрядится первым" : $"{a.Link} · всё в порядке";
        Painter.Text(g, hint, Fonts.Regular(12 * s), lvl <= Svc.Low && !a.Charging ? Theme.VividRed : t.SubText, new RectangleF(tx, r.Y + 70 * s, tw, 18 * s));
    }

    // ───────────── Liquid inside the hero ring ─────────────

    const int SloshMs = 2800;
    long _sloshT0 = -100_000;
    float _sloshPower;
    bool _hotHero;

    /// <summary>Gives the liquid a push (panel opened, cursor touched or clicked the hero card); it rocks and settles.</summary>
    void Slosh(float power)
    {
        long now = Clock.ElapsedMilliseconds;
        float left = now - _sloshT0 < SloshMs ? _sloshPower * MathF.Exp(-(now - _sloshT0) / 700f) : 0;
        _sloshPower = Math.Min(1.4f, left + power);
        _sloshT0 = now;
        EnsureTick();
    }

    bool Sloshing => Clock.ElapsedMilliseconds - _sloshT0 < SloshMs;

    /// <summary>
    /// The disc inside the ring is filled with translucent «liquid» up to the level: two waves with a bright
    /// meniscus, rocking after a push and rippling with rising bubbles while charging.
    /// </summary>
    void DrawLiquid(Graphics g, RectangleF disc, float level, Color color, bool charging, float s)
    {
        if (level <= 0.5f || disc.Width < 8 * s) return;
        var t = T;
        long now = Clock.ElapsedMilliseconds;
        float dt = now - _sloshT0;
        float push = dt < SloshMs ? _sloshPower * MathF.Exp(-dt / 700f) : 0;
        float tilt = push * MathF.Sin(dt / 160f) * 7f * s;            // side-to-side rocking
        float amp = (charging ? 1.7f : 1.1f) * s + push * 2.6f * s;    // wave height
        float phase = now / (charging ? 300f : 480f);
        float surface = disc.Bottom - disc.Height * Math.Clamp(level, 0, 100) / 100f;

        var st = g.Save();
        using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
        {
            clip.AddEllipse(disc);
            g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
        }
        // back wave: fainter, out of phase — gives the liquid depth
        using (var back = WavePath(disc, surface - 1.5f * s, amp * 0.9f, -tilt * 0.6f, -phase * 0.85f + 2.2f, s))
        using (var b = new SolidBrush(Theme.Alpha(color, t.Dark ? 46 : 40)))
            g.FillPath(b, back);
        // front wave: gradient from a brighter surface to a deeper bottom
        using (var front = WavePath(disc, surface, amp, tilt, phase, s))
        {
            var top = new RectangleF(disc.X, Math.Min(surface - amp - Math.Abs(tilt) - 2 * s, disc.Bottom - 2 * s), disc.Width,
                disc.Bottom - (surface - amp - Math.Abs(tilt) - 2 * s) + 2 * s);
            if (top.Height < 2 * s) top.Height = 2 * s;
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(top, Theme.Alpha(color, t.Dark ? 120 : 105), Theme.Alpha(color, t.Dark ? 62 : 56),
                       System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                g.FillPath(b, front);
            // meniscus: a thin bright line along the surface
            using var edge = new System.Drawing.Drawing2D.GraphicsPath();
            var pts = WavePoints(disc, surface, amp, tilt, phase, s);
            edge.AddLines(pts);
            using var pen = new Pen(Color.FromArgb(t.Dark ? 90 : 150, 255, 255, 255), 1.3f * s);
            g.DrawPath(pen, edge);
        }
        // bubbles rise while charging
        if (charging)
        {
            float cx = disc.X + disc.Width / 2;
            using var bb = new SolidBrush(Color.White);
            for (int i = 0; i < 5; i++)
            {
                float period = 1500 + i * 260;
                float p = ((now + i * 397) % (long)period) / period;
                float y = disc.Bottom - 3 * s - p * (disc.Bottom - surface);
                if (y < surface + 2 * s) continue;
                float x = cx + (i - 2) * disc.Width * 0.13f + MathF.Sin(i * 1.7f + p * 6.3f) * 2.2f * s;
                float r = (1.1f + (i % 3) * 0.55f) * s;
                bb.Color = Color.FromArgb((int)((t.Dark ? 150 : 200) * (1 - p * 0.8f)), 255, 255, 255);
                g.FillEllipse(bb, x - r, y - r, r * 2, r * 2);
            }
        }
        g.Restore(st);
    }

    static PointF[] WavePoints(RectangleF disc, float surface, float amp, float tilt, float phase, float s)
    {
        float step = Math.Max(1.5f, 2.5f * s), x0 = disc.Left - 2 * s, x1 = disc.Right + 2 * s;
        int n = (int)MathF.Ceiling((x1 - x0) / step) + 1;
        var pts = new PointF[n];
        float cx = disc.X + disc.Width / 2, half = Math.Max(1, disc.Width / 2);
        for (int i = 0; i < n; i++)
        {
            float x = Math.Min(x1, x0 + i * step);
            float u = (x - disc.Left) / Math.Max(1, disc.Width);
            float y = surface + tilt * (x - cx) / half
                + amp * MathF.Sin(u * MathF.PI * 2.6f + phase)
                + amp * 0.35f * MathF.Sin(u * MathF.PI * 5.1f - phase * 1.4f);
            pts[i] = new PointF(x, y);
        }
        return pts;
    }

    static System.Drawing.Drawing2D.GraphicsPath WavePath(RectangleF disc, float surface, float amp, float tilt, float phase, float s)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var pts = WavePoints(disc, surface, amp, tilt, phase, s);
        path.AddLines(pts);
        path.AddLine(pts[^1], new PointF(pts[^1].X, disc.Bottom + 2 * s));
        path.AddLine(new PointF(pts[^1].X, disc.Bottom + 2 * s), new PointF(pts[0].X, disc.Bottom + 2 * s));
        path.CloseFigure();
        return path;
    }

    bool HitHero(Point p)
    {
        if (p.Y < Header * Sc || p.Y > (Header + _viewH) * Sc) return false;
        foreach (var it in _items)
            if (it.Section == HeroTag) return Scaled(it.R).Contains(p);
        return false;
    }

    // ───────────── «wow» layer: cursor light, ripples, live ticking ─────────────

    Point _mouse;
    readonly List<(PointF C, RectangleF Clip, float Radius, long T0)> _ripples = new();
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    System.Windows.Forms.Timer? _tick;

    /// <summary>Header line: how many are connected and which one needs charging first.</summary>
    static string Summary(int count)
    {
        if (count == 0) return "";
        var weakest = Svc.R.ConnectedDevices.Where(a => a.WeakestLevel is not null && !a.Charging).MinBy(a => a.WeakestLevel);
        string head = $"подключено: {count}";
        return weakest is not null && weakest.WeakestLevel <= 50 ? $"{head} · {weakest.DisplayName.Split(' ')[0]} {weakest.WeakestLevel}%" : head;
    }

    /// <summary>Liquid Glass: a soft specular light that follows the cursor across the hovered card.</summary>
    void CursorLight(Graphics g, RectangleF card, float s)
    {
        if (!card.Contains(_mouse)) return;
        float rad = 120 * s;
        var st = g.Save();
        using (var clip = Painter.Round(card, 12 * s)) g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            path.AddEllipse(_mouse.X - rad, _mouse.Y - rad, rad * 2, rad * 2);
            using var br = new System.Drawing.Drawing2D.PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(T.Dark ? 34 : 70, 255, 255, 255),
                SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
            };
            g.FillPath(br, path);
        }
        g.Restore(st);
    }

    /// <summary>Material-style ripple from the click point, clipped to the card/button that was pressed.</summary>
    void AddRipple(Point p, RectangleF clip)
    {
        _ripples.Add((p, clip, Math.Max(clip.Width, clip.Height) * 1.1f, Clock.ElapsedMilliseconds));
        EnsureTick();
    }

    void DrawRipples(Graphics g, float s)
    {
        long now = Clock.ElapsedMilliseconds;
        _ripples.RemoveAll(r => now - r.T0 > 520);
        foreach (var (c, clip, radius, t0) in _ripples)
        {
            float k = Animator.EaseOut(Math.Clamp((now - t0) / 520f, 0, 1));
            float rr = radius * k;
            var st = g.Save();
            using (var cp = Painter.Round(clip, Math.Min(12 * s, clip.Height / 2))) g.SetClip(cp, System.Drawing.Drawing2D.CombineMode.Intersect);
            using var b = new SolidBrush(Theme.Alpha(T.Dark ? Color.White : T.Accent, (int)(60 * (1 - k))));
            g.FillEllipse(b, c.X - rr, c.Y - rr, rr * 2, rr * 2);
            g.Restore(st);
        }
    }

    /// <summary>
    /// ~30 fps repaint only while something is alive on screen: ripples, a level that is tweening,
    /// or a device that is charging (its bar shimmers). Stops by itself — zero cost when idle.
    /// </summary>
    void EnsureTick()
    {
        if (_tick is null)
        {
            _tick = new System.Windows.Forms.Timer { Interval = 33 };
            _tick.Tick += (_, _) =>
            {
                if (IsDisposed || !Visible) { _tick!.Stop(); return; }
                bool alive = _ripples.Count > 0 || LevelTween.Animating || _openT < 1 || Sloshing || Svc.R.ConnectedDevices.Any(a => a.Charging);
                if (!alive) { _tick!.Stop(); return; }
                Invalidate();
            };
        }
        if (!_tick.Enabled) _tick.Start();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) EnsureTick();
    }

    /// <summary>Circular-arrows icon; spins while checking; accent dot when a new version is waiting.</summary>
    void DrawUpdateIcon(Graphics g, RectangleF r, float s)
    {
        var t = T;
        var st = g.Save();
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        g.TranslateTransform(cx, cy);
        g.RotateTransform(_spin);
        g.TranslateTransform(-cx, -cy);
        Painter.Glyph(g, Glyphs.Sync, 18 * s, PendingUpdate is not null ? t.AccentText : t.Text, r);
        g.Restore(st);
        if (PendingUpdate is not null)
        {
            var dot = new RectangleF(r.Right - 12 * s, r.Y + 6 * s, 8 * s, 8 * s);
            using var b = new SolidBrush(Theme.VividRed);
            g.FillEllipse(b, dot);
        }
    }

    async Task RunUpdateCheckAsync()
    {
        if (_checking || CheckUpdateRequested is null) return;
        _checking = true;
        Animator.Anim? spin = null;
        void Loop() => spin = Animator.Run(900, p => { if (IsDisposed) return; _spin = p * 360; Invalidate(); }, () => { if (_checking && !IsDisposed) Loop(); else { _spin = 0; Invalidate(); } }, Animator.Linear);
        Loop();
        try { await CheckUpdateRequested(); }
        catch (Exception e) { Log.Error("Flyout update check", e); }
        finally { _checking = false; }
    }

    RectangleF Scaled(RectangleF r) => new(r.X * Sc, (r.Y - _scroll) * Sc, r.Width * Sc, r.Height * Sc);

    bool _hotRecent;

    bool HitRecent(Point p)
    {
        if (p.Y < Header * Sc || p.Y > (Header + _viewH) * Sc) return false;
        foreach (var it in _items)
            if (it.Section == RecentTag && Scaled(it.R).Contains(p)) return true;
        return false;
    }

    int HitCard(Point p)
    {
        if (p.Y < Header * Sc || p.Y > (Header + _viewH) * Sc) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].A is not null && Scaled(_items[i].R).Contains(p)) return i;
        return -1;
    }

    string HitButton(Point p) =>
        _btBtn.Contains(p) ? "bt" : _gearBtn.Contains(p) ? "gear" : _updBtn.Contains(p) ? "upd" : (!_bannerRect.IsEmpty && _bannerRect.Contains(p)) ? "banner" : "";

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool lightMoved = _hot >= 0 && T.Glass;
        _mouse = e.Location;
        if (lightMoved) Invalidate();
        bool hr = HitRecent(e.Location);
        if (hr != _hotRecent) { _hotRecent = hr; Cursor = hr ? Cursors.Hand : Cursors.Default; Invalidate(); }
        bool hh = HitHero(e.Location);
        if (hh && !_hotHero) Slosh(0.55f); // touching the hero card makes the liquid wobble
        _hotHero = hh;
        int hot = HitCard(e.Location);
        string btn = HitButton(e.Location);
        bool more = hot >= 0 && _expand > 0.5f && _items[hot].A?.Key == _expKey && MoreRect(Scaled(_items[hot].R)).Contains(e.Location);
        if (hot != _hot || btn != _hotBtn || more != _hotMore) { _hot = hot; _hotBtn = btn; _hotMore = more; Invalidate(); }
        if (hot >= 0 && _items[hot].A is Accessory ha) SetExpanded(ha.Key);
        Cursor = hot >= 0 || btn.Length > 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hotHero = false;
        if (_hot != -1 || _hotBtn.Length > 0) { _hot = -1; _hotBtn = ""; Invalidate(); }
        if (!MenuOpen) SetExpanded(null);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        string b = HitButton(e.Location);
        var rect = b switch { "bt" => _btBtn, "gear" => _gearBtn, "upd" => _updBtn, "banner" => _bannerRect, _ => RectangleF.Empty };
        if (rect.IsEmpty && HitCard(e.Location) is int ci and >= 0) rect = Scaled(_items[ci].R);
        if (rect.IsEmpty && HitHero(e.Location)) Slosh(0.9f);
        if (!rect.IsEmpty) AddRipple(e.Location, rect);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        switch (HitButton(e.Location))
        {
            case "bt":
                OpenBluetoothSettings();
                CloseAnimated();
                return;
            case "gear":
                SettingsRequested?.Invoke();
                CloseAnimated();
                return;
            case "upd":
                if (PendingUpdate is not null) { UpdateRequested?.Invoke(); CloseAnimated(); }
                else _ = RunUpdateCheckAsync();
                return;
            case "banner":
                UpdateRequested?.Invoke();
                CloseAnimated();
                return;
        }
        if (HitRecent(e.Location)) { _recentOpen = !_recentOpen; _openT = 1; Rebuild(); return; }
        int i = HitCard(e.Location);
        if (i >= 0 && _items[i].A is Accessory a)
        {
            // The menu drops down from the «⋯» button.
            var dots = MoreRect(Scaled(_items[i].R));
            DeviceMenuRequested?.Invoke(a, PointToScreen(new Point((int)dots.Right, (int)dots.Bottom + Px(2))));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_contentH <= _viewH) return;
        // Smooth: ease towards an accumulating target instead of jumping 48 px per notch.
        float from = _scroll;
        _scrollTarget = Math.Clamp((_scrollAnim is null ? _scroll : _scrollTarget) - e.Delta / 120f * 56, 0, _contentH - _viewH);
        float to = _scrollTarget;
        _scrollAnim?.Cancel();
        _scrollAnim = Animator.Run(200, p => { if (IsDisposed) return; _scroll = from + (to - from) * p; Invalidate(); }, () => _scrollAnim = null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) CloseAnimated();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!MenuOpen) CloseAnimated();
    }

    public bool MenuOpen { get; set; }

    public void CloseAnimated()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        int y0 = Top;
        Animator.Run(120, k => { if (!IsDisposed) Top = y0 + (int)(Px(10) * k); }, () => { if (!IsDisposed) Close(); });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick?.Dispose();
        base.Dispose(disposing);
    }

    public static void OpenBluetoothSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception e) { Log.Error("Open BT settings", e); }
    }
}

/// <summary>Rich hover card over a tray icon (replaces the plain one-line tooltip).</summary>
internal sealed class HoverCard : CardForm
{
    readonly List<Accessory> _devices;
    readonly Rectangle? _anchor;
    readonly System.Windows.Forms.Timer _guard = new() { Interval = 250 };
    readonly long _born = Environment.TickCount64;

    public HoverCard(List<Accessory> devices, Rectangle? anchor) : base(false)
    {
        _devices = devices;
        _anchor = anchor;
        _guard.Tick += (_, _) => Guard();
    }

    public void ShowNear()
    {
        UseScaleAt(_anchor?.Location ?? Cursor.Position);
        Relayout();
        Location = PlaceNearTray(Size, _anchor);
        Show();
        _guard.Start();
    }

    // Safety net: hide if the shell never sends NIN_POPUPCLOSE.
    void Guard()
    {
        if (IsDisposed) return;
        var cursor = Cursor.Position;
        bool overAnchor = _anchor is Rectangle a && Rectangle.Inflate(a, Px(4), Px(4)).Contains(cursor);
        if (!overAnchor || Environment.TickCount64 - _born > 15_000) Close();
    }

    protected override void Relayout()
    {
        float h = 12;
        if (_devices.Count == 0) h += 40;
        foreach (var a in _devices) h += DeviceCard.Height(a, false) + 6;
        h += 6;
        Size = new Size(Px(330), Px(h));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc, y = 12 * s;
        if (_devices.Count == 0)
        {
            Painter.Text(g, "Glance — нет подключённых аксессуаров", Fonts.Regular(12.5f * s), T.SubText, new RectangleF(14 * s, y, Width - 28 * s, 30 * s));
            return;
        }
        foreach (var a in _devices)
        {
            float h = DeviceCard.Height(a, false) * s;
            DeviceCard.Draw(g, new RectangleF(10 * s, y, Width - 20 * s, h), a, s, false, false);
            y += h + 6 * s;
        }
    }

    public void RefreshDevice(Accessory a)
    {
        if (IsDisposed || !_devices.Contains(a)) return;
        var oldH = Height;
        Relayout();
        Top -= Height - oldH;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _guard.Dispose();
        base.Dispose(disposing);
    }
}
