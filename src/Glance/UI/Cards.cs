namespace Glance;

/// <summary>Shared services for the UI layer (set once by AppHost).</summary>
internal static class Svc
{
    public static AppSettings S { get; private set; } = null!;
    public static HistoryStore H { get; private set; } = null!;
    public static DeviceRegistry R { get; private set; } = null!;

    public static void Init(AppSettings s, HistoryStore h, DeviceRegistry r) { S = s; H = h; R = r; }

    public static int Low => S.LowBatteryThreshold;

    public static TimeSpan? Estimate(Accessory a) =>
        a.Connected && a.Level is int l ? H.Estimate(a.Key, l, a.Charging) : null;

    public static (string L, string R, string C) PartLabels(Accessory a) =>
        a.IsSplitDevice ? ("Левая", "Правая", "") : ("Левый", "Правый", "Кейс");

    public static string Status(Accessory a)
    {
        if (!a.Connected) return $"Не подключено · {Fmt.Ago(a.LastSeen)}";
        string link = a.Link;
        if (a.Charging) return $"Заряжается · {link}";
        if (a.Level is null && !a.HasParts && (a.Wired || a.ViaReceiver)) return $"Подключено · {link}";
        if (a.Level is null && !a.HasParts) return a.BudsPending ? $"{link} · получаем данные…" : $"{link} · нет данных о заряде";
        if (a.BudsPending) return $"{link} · ищем каждый наушник…";
        return $"Подключено · {link}";
    }

    public static string Subtitle(Accessory a)
    {
        var f = Profiles.Features(a.Profile);
        var model = a.BudsModel;
        if (model is not null && !a.Name.Contains(model, StringComparison.OrdinalIgnoreCase))
            f = f.Length > 0 ? $"{f} · {model}" : model;
        return f;
    }

    public static string Tooltip(Accessory a)
    {
        var s = a.Level is null && !a.HasParts
            ? $"{a.DisplayName} — подключено{$" ({a.Link})"}"
            : $"{a.DisplayName} — {Fmt.Percent(a.Level)} · {a.Link}";
        if (a.Charging) s += " ⚡";
        if (a.HasParts)
        {
            var (l, r, c) = PartLabels(a);
            var parts = new List<string>(3);
            if (a.Left.Known) parts.Add($"{l[0]} {a.Left.Level}%");
            if (a.Right.Known) parts.Add($"{r[0]} {a.Right.Level}%");
            if (a.Case.Known && c.Length > 0) parts.Add($"{c} {a.Case.Level}%");
            if (parts.Count > 0) s += "\n" + string.Join(" · ", parts);
        }
        return s;
    }
}

/// <summary>The device card used in the panel and hover popup.</summary>
internal static class DeviceCard
{
    public static float Height(Accessory a, bool detailed)
    {
        float h = 66;
        if (a.Connected && a.HasParts) h += 34;
        if (detailed && HasHistory(a)) h += 30;
        return h;
    }

    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Charging: a bright band sweeps along the filled part of the bar (energy flowing in).</summary>
    static void Shimmer(Graphics g, RectangleF fill)
    {
        if (fill.Width < 4) return;
        float k = Clock.ElapsedMilliseconds % 1400 / 1400f;
        float bw = Math.Max(10, fill.Width * 0.45f);
        float x = fill.X - bw + (fill.Width + bw) * k;
        var band = new RectangleF(x, fill.Y, bw, fill.Height);
        var st = g.Save();
        using (var clip = Painter.Round(fill, fill.Height / 2)) g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(band.X - 1, band.Y, band.Width + 2, band.Height), Color.Transparent, Color.Transparent, 0f))
        {
            var blend = new System.Drawing.Drawing2D.ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(190, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            br.InterpolationColors = blend;
            g.FillRectangle(br, band);
        }
        g.Restore(st);
    }

    /// <summary>At least an hour of samples with some movement, otherwise the sparkline is just noise.</summary>
    static bool HasHistory(Accessory a)
    {
        if (!a.Connected) return false;
        var h = Svc.H.Get(a.Key);
        if (h.Count < 3) return false;
        long span = h[^1].T - h[0].T;
        int min = h.Min(x => x.L), max = h.Max(x => x.L);
        return span >= 3600 && max - min >= 3;
    }

    public static void Draw(Graphics g, RectangleF r, Accessory a, float s, bool hot, bool detailed, float grow = 1)
    {
        var t = Theme.Current;
        float cr = (t.Glass ? 12 : 8) * s;
        Painter.FillRound(g, r, cr, hot ? t.SurfaceHover : t.Surface);
        Painter.GlassSheen(g, r, cr, t.Dark);
        Painter.StrokeRound(g, r, cr, t.Border, Math.Max(1, s));

        bool dim = !a.Connected;
        var text = dim ? t.SubText : t.Text;
        var brand = a.Profile.BrandColor ?? t.Accent;

        // icon bubble
        var ic = new RectangleF(r.X + 12 * s, r.Y + 13 * s, 40 * s, 40 * s);
        Painter.FillRound(g, ic, 20 * s, Theme.Alpha(brand, dim ? 25 : 45));
        Painter.Glyph(g, Glyphs.For(a.Kind), 18 * s, dim ? t.SubText : (a.Profile.BrandColor ?? t.AccentText), ic);

        float x0 = r.X + 64 * s, right = r.Right - 12 * s;

        // percent + mini bar
        // Disconnected devices show no stale percentage; connected ones without data show «—» and no bar.
        int? lvl = a.Connected ? a.Level : null;
        var pctFont = Fonts.Big(18 * s);
        float pw = a.Connected ? Math.Max(58 * s, Painter.Width(g, "100%", pctFont) + 14 * s) : 30 * s;
        if (a.Connected)
        {
            // counts up when the panel opens and glides to a new value when the level changes
            float tween = lvl is int lg ? LevelTween.Value(a.Key, lg) * grow : 0;
            int? shown = lvl is not null ? (int)MathF.Round(tween) : null;
            Painter.Text(g, Fmt.Percent(shown), pctFont, t.LevelColor(lvl, a.Charging, Svc.Low), new RectangleF(right - pw, r.Y + 11 * s, pw, 26 * s), StringAlignment.Far);
            if (a.Charging) // the bolt «breathes» while charging
                Painter.Glyph(g, Glyphs.Bolt, 12 * s, Theme.Alpha(t.Good, (int)(175 + 80 * MathF.Sin(Clock.ElapsedMilliseconds / 260f))), new RectangleF(right - pw - 16 * s, r.Y + 15 * s, 16 * s, 18 * s));
            if (lvl is not null)
            {
                var bar = new RectangleF(right - 44 * s, r.Y + 41 * s, 44 * s, 4 * s);
                Painter.Bar(g, bar, tween / 100f, t.BarColor(lvl, a.Charging, Svc.Low), Theme.Alpha(t.Track, 160));
                if (a.Charging) Shimmer(g, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * tween / 100f), bar.Height));
            }
        }

        // name + status
        float textW = right - pw - 20 * s - x0;
        Painter.Text(g, a.DisplayName, Fonts.Strong(14 * s), text, new RectangleF(x0, r.Y + 12 * s, textW, 20 * s));
        var sub = Svc.Status(a);
        var feat = Svc.Subtitle(a);
        if (feat.Length > 0 && a.Connected && !a.BudsPending && a.Level is not null) sub = $"{sub} · {feat}";
        Painter.Text(g, sub, Fonts.Regular(12 * s), t.SubText, new RectangleF(x0, r.Y + 33 * s, textW + pw - 30 * s, 18 * s));

        float y = r.Y + 66 * s;

        // parts row: L / R / Case
        if (a.Connected && a.HasParts)
        {
            var (ln, rn, cn) = Svc.PartLabels(a);
            var parts = new List<(string, Part)> { (ln, a.Left), (rn, a.Right) };
            if (cn.Length > 0 && (a.Profile.HasCase || a.Case.Known)) parts.Add((cn, a.Case));
            float colW = (right - x0 + 10 * s) / parts.Count;
            for (int i = 0; i < parts.Count; i++)
            {
                var (label, p) = parts[i];
                float cx = x0 + i * colW;
                var lf = Fonts.Regular(11.5f * s);
                var vColor = a.PartsStale ? t.SubText : t.LevelColor(p.Level, p.Charging, Svc.Low);
                Painter.Text(g, label, lf, t.SubText, new RectangleF(cx, y - 2 * s, colW - 10 * s, 16 * s));
                string v = p.Known ? $"{p.Level}%" : "—";
                var vf = Fonts.Strong(11.5f * s);
                Painter.Text(g, v, vf, vColor, new RectangleF(cx, y - 2 * s, colW - 12 * s, 16 * s), StringAlignment.Far);
                if (p.Charging)
                {
                    float vw = Painter.Width(g, v, vf);
                    Painter.Glyph(g, Glyphs.Bolt, 10 * s, t.Good, new RectangleF(cx + colW - 12 * s - vw - 14 * s, y - 2 * s, 12 * s, 16 * s));
                }
                Painter.Bar(g, new RectangleF(cx, y + 17 * s, colW - 12 * s, 4 * s), (p.Level ?? 0) / 100f,
                    a.PartsStale ? t.Track : t.BarColor(p.Level, p.Charging, Svc.Low), Theme.Alpha(t.Track, 160));
            }
            y += 34 * s;
        }

        // history row: 24h sparkline + estimate
        if (detailed && HasHistory(a))
        {
            var hist = Svc.H.Get(a.Key);
            {
                var est = Svc.Estimate(a);
                string estText = est is TimeSpan e ? (a.Charging ? $"до 100% ≈ {Fmt.Duration(e)}" : $"хватит ≈ на {Fmt.Duration(e)}") : "за сутки";
                var ef = Fonts.Regular(11.5f * s);
                float ew = Painter.Width(g, estText, ef) + 6 * s;
                Painter.Sparkline(g, new RectangleF(x0, y, right - x0 - ew - 10 * s, 18 * s), hist, t.AccentText, s);
                Painter.Text(g, estText, ef, est is null ? t.SubText : t.Text, new RectangleF(right - ew, y, ew, 18 * s), StringAlignment.Far);
            }
        }
    }
}

/// <summary>Borderless rounded Windows 11 style popup base.</summary>
internal abstract class CardForm : Form, IGlassLayer
{
    readonly AlphaBuffer _buf = new();
    readonly bool _activate;
    protected float Sc { get; private set; } = 1f;

    protected CardForm(bool activate)
    {
        _activate = activate;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Current.Bg;
    }

    protected Theme T => Theme.Current;
    protected int Px(float dp) => (int)MathF.Round(dp * Sc);

    protected void UseScaleAt(Point screenPoint) => Sc = Native.DpiAt(screenPoint) / 96f;

    protected override bool ShowWithoutActivation => !_activate;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/;
            if (!_activate) cp.ExStyle |= 0x08000000 /*NOACTIVATE*/;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
        ApplyChrome();
    }

    void ApplyChrome()
    {
        Native.SetBorderColor(Handle, T.Glass ? (T.Dark ? Color.FromArgb(92, 94, 104) : Color.FromArgb(200, 202, 210)) : T.Opaque(T.Border));
        GlassHost.SetBlur(Handle, T.Glass);
    }

    protected override void WndProc(ref Message m)
    {
        if (GlassHost.Handle(this, ref m, _buf, g => OnPaint(new PaintEventArgs(g, ClientRectangle)))) return;
        base.WndProc(ref m);
    }

    public void PaintLayer(Graphics g, Size size) => T.PaintBackdrop(g, new Rectangle(Point.Empty, size), sheen: true);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _buf.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Sc = e.DeviceDpiNew / 96f;
        Relayout();
    }

    protected virtual void Relayout() => Invalidate();

    public void ApplyTheme()
    {
        BackColor = T.Bg;
        if (IsHandleCreated) ApplyChrome();
        Relayout();
        Invalidate();
    }


    /// <summary>Position near the tray: above the taskbar, horizontally centred on the anchor when known.</summary>
    protected Point PlaceNearTray(Size size, Rectangle? anchor)
    {
        var refPt = anchor?.Location ?? Cursor.Position;
        var screen = Screen.FromPoint(refPt);
        var wa = screen.WorkingArea;
        var b = screen.Bounds;
        int margin = Px(12);
        int x = anchor is Rectangle a ? a.Left + a.Width / 2 - size.Width / 2 : wa.Right - size.Width - margin;
        x = Math.Clamp(x, wa.Left + margin, wa.Right - size.Width - margin);
        int y;
        if (wa.Top > b.Top) y = wa.Top + margin;                 // taskbar on top
        else if (anchor is Rectangle a2 && a2.Top < wa.Bottom - Px(40)) y = a2.Top - size.Height - margin; // overflow area
        else y = wa.Bottom - size.Height - margin;
        y = Math.Clamp(y, wa.Top + margin, Math.Max(wa.Top + margin, wa.Bottom - size.Height - margin));
        return new Point(x, y);
    }

    protected static Graphics MeasureGraphics(out Bitmap holder)
    {
        holder = new Bitmap(1, 1);
        var g = Graphics.FromImage(holder);
        Painter.Prepare(g);
        return g;
    }
}

/// <summary>Smoothly glides displayed levels to new values (700 ms ease-out) instead of jumping.</summary>
internal static class LevelTween
{
    const int Ms = 700;
    static readonly Dictionary<string, (float From, int To, long T0)> Map = new();
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    public static float Value(string key, int target)
    {
        long now = Clock.ElapsedMilliseconds;
        if (!Map.TryGetValue(key, out var e)) { Map[key] = (target, target, now - Ms); return target; }
        if (e.To != target) { e = (Current(e, now), target, now); Map[key] = e; }
        return Current(e, now);
    }

    public static bool Animating
    {
        get { long now = Clock.ElapsedMilliseconds; foreach (var e in Map.Values) if (now - e.T0 < Ms) return true; return false; }
    }

    static float Current((float From, int To, long T0) e, long now)
    {
        float k = Animator.EaseOut(Math.Clamp((now - e.T0) / (float)Ms, 0, 1));
        return e.From + (e.To - e.From) * k;
    }
}
