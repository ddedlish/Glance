using Android.Graphics;
using AColor = Android.Graphics.Color;
using Path = Android.Graphics.Path;

namespace Glance.Droid;

/// <summary>
/// Pictures for the «Заряд в шторке» notification, drawn in the Liquid Glass style of the app:
/// collapsed — a row of glass chips, each with a little liquid ring and the device glyph;
/// expanded — a glass row per accessory with a coloured tile, the name, and a liquid bar
/// (or three liquid rings — left, right, case — for earbuds). Transparent background: the shade's own
/// light or dark surface shows through, the colours follow the system theme.
/// </summary>
internal static class ShadeArt
{
    public const float CollapsedH = 44, RowH = 46, RowGap = 6, HeadH = 24;
    public const int MaxRows = 5;

    sealed class Ctx
    {
        public required Canvas C;
        public required float S;          // px per dp
        public required Palette P;
        public float Dp(float v) => v * S;
    }

    /// <summary>Pixels per dp for a picture of w×h dp, kept small enough to pass through the notification service.</summary>
    public static float Scale(float density, float wDp, float hDp)
    {
        float max = MathF.Sqrt(1_300_000f / (wDp * hDp * 4f));
        return Math.Clamp(Math.Min(density, max), 1f, 3f);
    }

    public static float ExpandedHeight(int count)
    {
        int rows = Math.Min(count, MaxRows);
        float h = HeadH + rows * RowH + Math.Max(0, rows - 1) * RowGap + 4;
        if (count > MaxRows) h += 20;
        if (count == 0) h = HeadH + RowH;
        return h;
    }

    // ───────── collapsed: chips ─────────

    public static Bitmap Collapsed(IReadOnlyList<Accessory> all, bool dark, float wDp, float density)
    {
        float s = Scale(density, wDp, CollapsedH);
        var bmp = Bitmap.CreateBitmap((int)(wDp * s), (int)(CollapsedH * s), Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bmp);
        var x = new Ctx { C = canvas, S = s, P = Palette.For(dark) };

        if (all.Count == 0)
        {
            Icons.Draw(canvas, DeviceKind.Unknown, new RectF(x.Dp(2), x.Dp(12), x.Dp(22), x.Dp(32)), x.P.Sub, x.Dp(1.6f));
            Text(x, "Нет подключённых аксессуаров", x.Dp(30), x.Dp(27), 13.5f, x.P.Sub, false, wDp - 34);
            return bmp;
        }

        const float chipW = 64, gap = 6, chipH = 36;
        int fit = Math.Max(1, (int)((wDp + gap) / (chipW + gap)));
        bool more = all.Count > fit;
        int shown = more ? Math.Max(1, fit - 1) : all.Count;
        more = more && shown * (chipW + gap) + 46 <= wDp; // a tiny widget cell: the weakest chip alone
        float top = (CollapsedH - chipH) / 2;
        // the weakest first: the one to charge is always visible
        var order = all.OrderBy(a => a.WeakestLevel is int l ? (a.Charging ? 200 + l : l) : 400).ToList();
        for (int i = 0; i < shown; i++)
        {
            var a = order[i];
            var r = new RectF(x.Dp(i * (chipW + gap)), x.Dp(top), x.Dp(i * (chipW + gap) + chipW), x.Dp(top + chipH));
            int? lvl = a.WeakestLevel;
            Glass(x, r, x.Dp(chipH / 2), lvl is null ? x.P.Sub : Palette.Level(lvl, a.Charging));
            float rr = 14;
            LiquidRing(x, r.Left + x.Dp(4 + rr), r.CenterY(), x.Dp(rr), lvl, a.Charging, x.Dp(2.6f), 10.5f, i * 1.3f);
            var icon = new RectF(r.Left + x.Dp(4 + rr * 2 + 5), r.CenterY() - x.Dp(8), r.Left + x.Dp(4 + rr * 2 + 21), r.CenterY() + x.Dp(8));
            Icons.Draw(canvas, a.Kind, icon, Palette.KindColor(a.Kind), x.Dp(1.7f));
        }
        if (more)
        {
            var r = new RectF(x.Dp(shown * (chipW + gap)), x.Dp(top), x.Dp(shown * (chipW + gap) + 46), x.Dp(top + chipH));
            Glass(x, r, x.Dp(chipH / 2), x.P.Sub);
            TextCentered(x, $"+{all.Count - shown}", r.CenterX(), r.CenterY() + x.Dp(5), 14, x.P.Text, true);
        }
        return bmp;
    }

    // ───────── expanded: rows ─────────

    public static Bitmap Expanded(IReadOnlyList<Accessory> all, string head, bool dark, float wDp, float density)
    {
        float hDp = ExpandedHeight(all.Count);
        float s = Scale(density, wDp, hDp);
        var bmp = Bitmap.CreateBitmap((int)(wDp * s), (int)(hDp * s), Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bmp);
        var x = new Ctx { C = canvas, S = s, P = Palette.For(dark) };

        Text(x, head, x.Dp(2), x.Dp(15), 12.5f, x.P.Sub, false, wDp - 4);
        float y = HeadH;
        if (all.Count == 0)
        {
            var r = new RectF(0, x.Dp(y), x.Dp(wDp), x.Dp(y + RowH));
            Glass(x, r, x.Dp(16), x.P.Sub);
            Icons.Draw(canvas, DeviceKind.Unknown, new RectF(r.Left + x.Dp(12), r.CenterY() - x.Dp(11), r.Left + x.Dp(34), r.CenterY() + x.Dp(11)), x.P.Sub, x.Dp(1.6f));
            Text(x, "Подключите наушники, часы или геймпад", r.Left + x.Dp(44), r.CenterY() + x.Dp(5), 13, x.P.Sub, false, wDp - 52);
            return bmp;
        }
        int i = 0;
        foreach (var a in all.Take(MaxRows))
        {
            Row(x, new RectF(0, x.Dp(y), x.Dp(wDp), x.Dp(y + RowH)), a, wDp, i++);
            y += RowH + RowGap;
        }
        if (all.Count > MaxRows)
            Text(x, $"и ещё {all.Count - MaxRows} — откройте Glance", x.Dp(2), x.Dp(y + 10), 12, x.P.Sub, false, wDp - 4);
        return bmp;
    }

    // ───────── home-screen widget: a pane of Liquid Glass with the same rows ─────────

    /// <summary>
    /// The «Glance» widget picture for a w×h dp cell: a frosted glass pane with the app's colour blobs, a header
    /// («Аксессуары · 5» and who runs out first) and a glass row per accessory, weakest first — as many as fit.
    /// Too low for a row: the shade chips instead. <paramref name="note"/> replaces the content (no data yet).
    /// </summary>
    public static Bitmap Widget(IReadOnlyList<Accessory> all, bool dark, float wDp, float hDp, float density, int lowAt, string? note, float phase)
    {
        wDp = Math.Clamp(wDp, 110, 720);
        hDp = Math.Clamp(hDp, 48, 720);
        // at most ~1.2 MB: the picture travels to the launcher through Binder, like the shade card
        float s = Math.Clamp(Math.Min(density, MathF.Sqrt(1_200_000f / (wDp * hDp * 4f))), 1f, 3f);
        var bmp = Bitmap.CreateBitmap((int)(wDp * s), (int)(hDp * s), Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bmp);
        var x = new Ctx { C = canvas, S = s, P = Palette.For(dark) };
        var pane = new RectF(x.Dp(1), x.Dp(1), x.Dp(wDp - 1), x.Dp(hDp - 1));
        float rad = x.Dp(Math.Min(24, hDp / 2.4f));
        Pane(x, pane, rad, phase);

        const float pad = 10;
        float contentW = wDp - 2 * pad;
        bool compact = hDp < 96 || wDp < 200;
        var order = all.OrderBy(a => a.WeakestLevel is int l ? (a.Charging ? 200 + l : l) : 400).ToList();

        if (note is not null || all.Count == 0)
        {
            string msg = note ?? "Нет подключённых аксессуаров";
            float cy = pane.CenterY();
            float ic = Math.Min(26, hDp * 0.32f);
            Icons.Draw(canvas, DeviceKind.Unknown, new RectF(x.Dp(pad + 4), cy - x.Dp(ic / 2), x.Dp(pad + 4 + ic), cy + x.Dp(ic / 2)), x.P.Sub, x.Dp(1.7f));
            float tx = pad + 4 + ic + 10;
            if (!compact) Text(x, "Glance", x.Dp(tx), cy - x.Dp(4), 14, x.P.Text, true, wDp - tx - pad);
            Text(x, msg, x.Dp(tx), compact ? cy + x.Dp(5) : cy + x.Dp(13), 12, x.P.Sub, false, wDp - tx - pad);
            return bmp;
        }

        if (compact)
        {
            // a short or narrow cell: the row of chips with liquid rings, centred in the pane
            using var chips = Collapsed(order, dark, contentW, density);
            float chH = contentW * chips.Height / Math.Max(1f, chips.Width);
            float top = (hDp - chH) / 2;
            using var bp = new Paint(PaintFlags.FilterBitmap | PaintFlags.AntiAlias);
            canvas.DrawBitmap(chips, null, new RectF(x.Dp(pad), x.Dp(top), x.Dp(pad + contentW), x.Dp(top + chH)), bp);
            return bmp;
        }

        // header: the count on the left, who needs the charger on the right
        const float headH = 30;
        float rowsH = hDp - headH - pad;
        int fit = Math.Max(1, (int)((rowsH + RowGap) / (RowH + RowGap)));
        int shown = Math.Min(fit, order.Count);
        var weakest = order.FirstOrDefault(a => !a.Charging && a.WeakestLevel is not null);
        string right = order.Count > shown ? $"ещё {order.Count - shown} — в Glance"
            : weakest is { WeakestLevel: int wl } ? (wl <= lowAt ? $"{weakest.DisplayName}: пора зарядить" : $"Первым разрядится {weakest.DisplayName}")
            : order.Any(a => a.Charging) ? "Заряжается" : "";
        AColor rightCol = weakest is { WeakestLevel: int w2 } && w2 <= lowAt && order.Count <= shown ? Palette.Level(w2, false) : x.P.Sub;
        // a tiny Glance ring before the title
        float ry = x.Dp(19), rx = x.Dp(pad + 9);
        using (var rp = new Paint(PaintFlags.AntiAlias) { StrokeWidth = x.Dp(2.2f), StrokeCap = Paint.Cap.Round, Color = x.P.Track })
        {
            rp.SetStyle(Paint.Style.Stroke);
            var rb = new RectF(rx - x.Dp(6), ry - x.Dp(6), rx + x.Dp(6), ry + x.Dp(6));
            canvas.DrawOval(rb, rp);
            rp.Color = weakest is null ? x.P.Accent : Palette.Level(weakest.WeakestLevel, false);
            canvas.DrawArc(rb, -90, 360f * Math.Clamp(weakest?.WeakestLevel ?? 75, 3, 100) / 100f, false, rp);
        }
        string title = $"Аксессуары · {order.Count}";
        using (var tp = Font(x, 13.5f, true))
        {
            tp.Color = x.P.Text;
            float titleW = tp.MeasureText(title) / s;
            canvas.DrawText(title, x.Dp(pad + 21), x.Dp(24), tp);
            float rw = wDp - pad - (pad + 21 + titleW + 10);
            if (rw > 40 && right.Length > 0)
            {
                using var sp = Font(x, 11.5f, false);
                sp.Color = rightCol;
                sp.TextAlign = Paint.Align.Right;
                canvas.DrawText(Fit(sp, right, x.Dp(rw)), x.Dp(wDp - pad - 2), x.Dp(24), sp);
            }
        }
        // rows, centred in the space left below the header
        float used = shown * RowH + Math.Max(0, shown - 1) * RowGap;
        float y = headH + Math.Max(0, (rowsH - used) / 2);
        for (int i = 0; i < shown; i++)
        {
            canvas.Save();
            canvas.Translate(x.Dp(pad), 0);
            Row(x, new RectF(0, x.Dp(y), x.Dp(contentW), x.Dp(y + RowH)), order[i], contentW, i + (int)(phase * 3));
            canvas.Restore();
            y += RowH + RowGap;
        }
        return bmp;
    }

    /// <summary>The widget's own pane: deep (or pale) glass with soft colour blobs, a sheen across the top and a light rim.</summary>
    static void Pane(Ctx x, RectF r, float rad, float phase)
    {
        var c = x.C;
        var P = x.P;
        using var clip = new Path();
        clip.AddRoundRect(r, rad, rad, Path.Direction.Cw!);
        c.Save();
        c.ClipPath(clip);
        using (var p = new Paint(PaintFlags.AntiAlias))
        {
            p.SetShader(new LinearGradient(0, r.Top, 0, r.Bottom, Palette.Alpha(P.BgTop, P.Dark ? 236 : 232), Palette.Alpha(P.BgBottom, P.Dark ? 240 : 236), Shader.TileMode.Clamp!));
            c.DrawRect(r, p);
            // the same blobs as the app background; they drift a little with every update, so the widget looks alive
            float w = r.Width(), h = r.Height(), m = Math.Max(w, h);
            void Blob(float cx, float cy, float rr, AColor col)
            {
                p.SetShader(new RadialGradient(cx, cy, rr, new[] { col.ToArgb(), Palette.Alpha(col, 0).ToArgb() }, null, Shader.TileMode.Clamp!));
                c.DrawCircle(cx, cy, rr, p);
            }
            Blob(r.Left + w * (0.12f + 0.06f * MathF.Sin(phase)), r.Top + h * 0.1f, m * 0.55f, P.BlobA);
            Blob(r.Left + w * (0.92f - 0.05f * MathF.Cos(phase * 0.7f)), r.Top + h * 0.95f, m * 0.5f, P.BlobB);
            Blob(r.Left + w * 0.78f, r.Top + h * (0.08f + 0.06f * MathF.Sin(phase * 1.3f)), m * 0.36f, P.BlobC);
            // the glass: a milky veil, a sheen across the upper part and a diagonal glint
            p.SetShader(null);
            p.Color = P.Card;
            c.DrawRect(r, p);
            p.SetShader(new LinearGradient(0, r.Top, 0, r.Top + h * 0.45f, AColor.Argb(P.Dark ? 30 : 80, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
            c.DrawRect(r, p);
            float gx = r.Left + w * (0.25f + 0.5f * ((phase * 0.37f) % 1f));
            p.SetShader(new LinearGradient(gx - m * 0.18f, r.Top, gx + m * 0.18f, r.Bottom,
                new[] { AColor.Argb(0, 255, 255, 255).ToArgb(), AColor.Argb(P.Dark ? 16 : 40, 255, 255, 255).ToArgb(), AColor.Argb(0, 255, 255, 255).ToArgb() }, null, Shader.TileMode.Clamp!));
            c.DrawRect(r, p);
        }
        c.Restore();
        using var b = new Paint(PaintFlags.AntiAlias) { StrokeWidth = Math.Max(1, x.Dp(1)) };
        b.SetStyle(Paint.Style.Stroke);
        b.SetShader(new LinearGradient(0, r.Top, 0, r.Bottom, P.CardBorder.ToArgb(), Palette.Alpha(P.CardBorder, P.Dark ? 20 : 120).ToArgb(), Shader.TileMode.Clamp!));
        var br = new RectF(r.Left + b.StrokeWidth / 2, r.Top + b.StrokeWidth / 2, r.Right - b.StrokeWidth / 2, r.Bottom - b.StrokeWidth / 2);
        c.DrawRoundRect(br, rad, rad, b);
    }

    static void Row(Ctx x, RectF r, Accessory a, float wDp, int index)
    {
        var c = x.C;
        int? lvl = a.WeakestLevel;
        var col = lvl is null ? x.P.Sub : Palette.Level(lvl, a.Charging);
        Glass(x, r, x.Dp(16), col);

        // tile with the device glyph, like the cards in the app
        var tile = new RectF(r.Left + x.Dp(9), r.CenterY() - x.Dp(14), r.Left + x.Dp(37), r.CenterY() + x.Dp(14));
        var kc = Palette.KindColor(a.Kind);
        using (var p = new Paint(PaintFlags.AntiAlias))
        {
            p.SetShader(new LinearGradient(tile.Left, tile.Top, tile.Right, tile.Bottom, Palette.Mix(kc, AColor.White, 0.2f), Palette.Mix(kc, AColor.Black, 0.12f), Shader.TileMode.Clamp!));
            c.DrawRoundRect(tile, x.Dp(9), x.Dp(9), p);
            p.SetShader(new LinearGradient(0, tile.Top, 0, tile.CenterY(), AColor.Argb(80, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
            c.DrawRoundRect(tile, x.Dp(9), x.Dp(9), p);
        }
        var inner = new RectF(tile.Left + x.Dp(6), tile.Top + x.Dp(6), tile.Right - x.Dp(6), tile.Bottom - x.Dp(6));
        Icons.Draw(c, a.Kind, inner, AColor.White, x.Dp(1.6f));

        float tx = tile.Right + x.Dp(10);
        bool buds = a.HasParts;
        float rightW = buds ? 3 * 30 + 4 : 70;
        float textW = (r.Width() / x.S) - (tx - r.Left) / x.S - rightW - 8;
        Text(x, a.DisplayName, tx, r.CenterY() - x.Dp(2), 14, x.P.Text, true, textW);
        string sub = a.Charging ? "Заряжается" : lvl is null ? $"{a.Link} · заряд не передаётся" : a.Link;
        Text(x, sub, tx, r.CenterY() + x.Dp(14), 11.5f, a.Charging ? Palette.Green : x.P.Sub, false, textW);

        if (buds)
        {
            // three liquid rings: left, right, case
            var parts = new[] { ("Л", a.Left), ("П", a.Right), ("К", a.Case) };
            for (int k = 0; k < 3; k++)
            {
                float cx = r.Right - x.Dp(9 + 14 + (2 - k) * 30);
                var (label, part) = parts[k];
                LiquidRing(x, cx, r.CenterY() - x.Dp(4), x.Dp(13), part.Level, part.Charging, x.Dp(2.4f), 9.5f, index * 2.1f + k * 1.7f);
                TextCentered(x, label, cx, r.CenterY() + x.Dp(19), 9.5f, x.P.Sub, true);
            }
        }
        else
        {
            string pct = lvl is int l ? $"{l}%" : "—";
            float right = r.Right - x.Dp(12);
            TextRight(x, pct, right, r.CenterY() + x.Dp(2), 16, lvl is int l2 && l2 <= 20 && !a.Charging ? col : x.P.Text, true);
            if (a.Charging)
            {
                using var tp = Font(x, 16, true);
                float pw = tp.MeasureText(pct);
                Icons.Bolt(c, new RectF(right - pw - x.Dp(17), r.CenterY() - x.Dp(12), right - pw - x.Dp(3), r.CenterY() + x.Dp(2)), Palette.Green);
            }
            var bar = new RectF(right - x.Dp(56), r.CenterY() + x.Dp(9), right, r.CenterY() + x.Dp(16));
            LiquidBar(x, bar, lvl, col, a.Charging, index * 1.9f);
        }
    }

    // ───────── pieces ─────────

    /// <summary>A pane of glass tinted by the level colour: a soft gradient, a light top edge and a hairline border.</summary>
    static void Glass(Ctx x, RectF r, float rad, AColor tint)
    {
        var c = x.C;
        bool d = x.P.Dark;
        using var p = new Paint(PaintFlags.AntiAlias);
        p.SetShader(new LinearGradient(r.Left, r.Top, r.Right, r.Bottom,
            Palette.Alpha(tint, d ? 40 : 30), d ? AColor.Argb(16, 255, 255, 255) : AColor.Argb(10, 20, 30, 60), Shader.TileMode.Clamp!));
        c.DrawRoundRect(r, rad, rad, p);
        // sheen across the upper half
        p.SetShader(new LinearGradient(0, r.Top, 0, r.CenterY(), AColor.Argb(d ? 26 : 70, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
        c.DrawRoundRect(r, rad, rad, p);
        p.SetShader(null);
        p.SetStyle(Paint.Style.Stroke);
        p.StrokeWidth = Math.Max(1, x.Dp(0.8f));
        p.Color = d ? AColor.Argb(46, 255, 255, 255) : AColor.Argb(30, 20, 30, 60);
        var b = new RectF(r.Left + p.StrokeWidth / 2, r.Top + p.StrokeWidth / 2, r.Right - p.StrokeWidth / 2, r.Bottom - p.StrokeWidth / 2);
        c.DrawRoundRect(b, rad, rad, p);
    }

    /// <summary>A ring with liquid inside up to the level, a wave on its surface and the number on top.</summary>
    static void LiquidRing(Ctx x, float cx, float cy, float rad, int? level, bool charging, float th, float textSp, float phase)
    {
        var c = x.C;
        var box = new RectF(cx - rad + th / 2, cy - rad + th / 2, cx + rad - th / 2, cy + rad - th / 2);
        using var p = new Paint(PaintFlags.AntiAlias) { StrokeWidth = th, StrokeCap = Paint.Cap.Round, Color = x.P.Track };
        p.SetStyle(Paint.Style.Stroke);
        c.DrawOval(box, p);
        if (level is not int l) { TextCentered(x, "—", cx, cy + x.Dp(textSp * 0.36f), textSp, x.P.Sub, true); return; }
        var col = Palette.Level(l, charging);
        float inset = th + x.Dp(1.2f);
        var disc = new RectF(cx - rad + inset, cy - rad + inset, cx + rad - inset, cy + rad - inset);
        Liquid(x, disc, l, col, charging, phase, oval: true);
        p.Color = col;
        if (l > 0) c.DrawArc(box, -90, 360f * Math.Clamp(l, 0, 100) / 100f, false, p);
        TextCentered(x, l >= 100 ? "100" : l.ToString(), cx, cy + x.Dp(textSp * 0.36f), l >= 100 ? textSp * 0.82f : textSp, x.P.Text, true);
        if (charging) Icons.Bolt(c, new RectF(cx + rad - x.Dp(7), cy - rad - x.Dp(3), cx + rad + x.Dp(5), cy - rad + x.Dp(9)), Palette.Green);
    }

    /// <summary>A thin glass tube filled with liquid; the surface ripples at the front edge.</summary>
    static void LiquidBar(Ctx x, RectF r, int? level, AColor col, bool charging, float phase)
    {
        var c = x.C;
        float rad = r.Height() / 2;
        using (var t = new Paint(PaintFlags.AntiAlias) { Color = x.P.Track }) c.DrawRoundRect(r, rad, rad, t);
        if (level is not int l || l <= 0) return;
        float edge = r.Left + Math.Max(r.Height(), r.Width() * Math.Clamp(l, 0, 100) / 100f);
        c.Save();
        using (var clip = new Path()) { clip.AddRoundRect(r, rad, rad, Path.Direction.Cw!); c.ClipPath(clip); }
        using (var liquid = new Path())
        {
            liquid.MoveTo(r.Left, r.Top);
            for (float yy = r.Top; yy <= r.Bottom + 0.5f; yy += Math.Max(1, x.Dp(1)))
            {
                float k = (yy - r.Top) / r.Height();
                liquid.LineTo(edge + x.Dp(1.6f) * MathF.Sin(k * MathF.PI * 2 + phase), yy);
            }
            liquid.LineTo(r.Left, r.Bottom);
            liquid.Close();
            using var p = new Paint(PaintFlags.AntiAlias);
            p.SetShader(new LinearGradient(r.Left, 0, edge, 0, Palette.Alpha(col, 150), col, Shader.TileMode.Clamp!));
            c.DrawPath(liquid, p);
            // glossy highlight along the top of the liquid
            p.SetShader(new LinearGradient(0, r.Top, 0, r.CenterY(), AColor.Argb(120, 255, 255, 255), AColor.Argb(0, 255, 255, 255), Shader.TileMode.Clamp!));
            c.DrawPath(liquid, p);
        }
        c.Restore();
    }

    /// <summary>Liquid in a disc: back wave, front wave with depth gradient, a bright surface line, bubbles while charging.</summary>
    static void Liquid(Ctx x, RectF disc, int level, AColor color, bool charging, float phase, bool oval)
    {
        if (level <= 0 || disc.Width() < 4) return;
        var c = x.C;
        float surface = disc.Bottom - disc.Height() * Math.Clamp(level, 0, 100) / 100f;
        float amp = Math.Max(0.6f, disc.Width() * 0.045f);
        c.Save();
        using (var clip = new Path()) { clip.AddOval(disc, Path.Direction.Cw!); c.ClipPath(clip); }
        bool d = x.P.Dark;
        using (var back = Wave(disc, surface - amp * 0.8f, amp * 0.9f, phase + 2.2f, true))
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = Palette.Alpha(color, d ? 60 : 70) })
            c.DrawPath(back, p);
        using (var front = Wave(disc, surface, amp, phase, true))
        using (var p = new Paint(PaintFlags.AntiAlias))
        {
            p.SetShader(new LinearGradient(0, surface - amp, 0, disc.Bottom, Palette.Alpha(color, d ? 160 : 180), Palette.Alpha(color, d ? 85 : 100), Shader.TileMode.Clamp!));
            c.DrawPath(front, p);
        }
        using (var line = Wave(disc, surface, amp, phase, false))
        using (var p = new Paint(PaintFlags.AntiAlias) { Color = AColor.Argb(d ? 120 : 190, 255, 255, 255), StrokeWidth = Math.Max(1, x.Dp(0.9f)) })
        {
            p.SetStyle(Paint.Style.Stroke);
            c.DrawPath(line, p);
        }
        if (charging)
        {
            using var bb = new Paint(PaintFlags.AntiAlias) { Color = AColor.Argb(200, 255, 255, 255) };
            for (int i = 0; i < 3; i++)
            {
                float k = (i * 0.37f + phase * 0.11f) % 1f;
                float yy = disc.Bottom - disc.Height() * 0.12f - k * Math.Max(0, disc.Bottom - surface - disc.Height() * 0.2f);
                if (yy < surface + x.Dp(1.5f)) continue;
                c.DrawCircle(disc.CenterX() + (i - 1) * disc.Width() * 0.22f, yy, x.Dp(0.9f + (i % 2) * 0.5f), bb);
            }
        }
        c.Restore();
    }

    static Path Wave(RectF disc, float surface, float amp, float phase, bool closed)
    {
        var path = new Path();
        float step = Math.Max(1, disc.Width() / 24f);
        bool first = true;
        for (float xx = disc.Left - step; xx <= disc.Right + step; xx += step)
        {
            float u = (xx - disc.Left) / Math.Max(1, disc.Width());
            float yy = surface + amp * MathF.Sin(u * MathF.PI * 2.4f + phase) + amp * 0.35f * MathF.Sin(u * MathF.PI * 5f - phase * 1.3f);
            if (first) { path.MoveTo(xx, yy); first = false; } else path.LineTo(xx, yy);
        }
        if (closed)
        {
            path.LineTo(disc.Right + step, disc.Bottom + step);
            path.LineTo(disc.Left - step, disc.Bottom + step);
            path.Close();
        }
        return path;
    }

    // ───────── text ─────────

    static Paint Font(Ctx x, float sp, bool bold)
    {
        var p = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText) { TextSize = x.Dp(sp) };
        p.SetTypeface(Typeface.Create(bold ? "sans-serif-medium" : "sans-serif", TypefaceStyle.Normal));
        return p;
    }

    static string Fit(Paint p, string s, float maxPx)
    {
        if (maxPx <= 0) return "";
        if (p.MeasureText(s) <= maxPx) return s;
        while (s.Length > 1 && p.MeasureText(s + "…") > maxPx) s = s[..^1];
        return s.TrimEnd() + "…";
    }

    static void Text(Ctx x, string s, float px, float baseline, float sp, AColor col, bool bold, float maxWDp)
    {
        using var p = Font(x, sp, bold);
        p.Color = col;
        x.C.DrawText(Fit(p, s, x.Dp(maxWDp)), px, baseline, p);
    }

    static void TextCentered(Ctx x, string s, float cx, float baseline, float sp, AColor col, bool bold)
    {
        using var p = Font(x, sp, bold);
        p.Color = col;
        p.TextAlign = Paint.Align.Center;
        x.C.DrawText(s, cx, baseline, p);
    }

    static void TextRight(Ctx x, string s, float right, float baseline, float sp, AColor col, bool bold)
    {
        using var p = Font(x, sp, bold);
        p.Color = col;
        p.TextAlign = Paint.Align.Right;
        x.C.DrawText(s, right, baseline, p);
    }
}
