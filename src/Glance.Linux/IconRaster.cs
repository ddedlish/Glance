namespace Glance;

/// <summary>
/// Dependency-free antialiased rasterizer (4×4 supersampling) for tray icons and notification images.
/// System.Drawing is not available on Linux, and pulling a graphics library would mean extra installs.
/// </summary>
internal sealed class Canvas
{
    public readonly int W, H;
    readonly float[] _a, _r, _g, _b; // premultiplied, 0..1
    const int SS = 4;

    public Canvas(int w, int h)
    {
        W = w; H = h;
        _a = new float[w * h]; _r = new float[w * h]; _g = new float[w * h]; _b = new float[w * h];
    }

    public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
    {
        public Rgba WithAlpha(byte a) => this with { A = a };
    }

    /// <summary>Composites a colour over every pixel covered by <paramref name="inside"/> within the bounding box.</summary>
    public void Fill(Func<float, float, bool> inside, Rgba c, float x0 = 0, float y0 = 0, float x1 = float.MaxValue, float y1 = float.MaxValue)
    {
        int px0 = Math.Max(0, (int)MathF.Floor(x0)), py0 = Math.Max(0, (int)MathF.Floor(y0));
        int px1 = Math.Min(W - 1, (int)MathF.Ceiling(Math.Min(x1, W))), py1 = Math.Min(H - 1, (int)MathF.Ceiling(Math.Min(y1, H)));
        float ca = c.A / 255f, cr = c.R / 255f * ca, cg = c.G / 255f * ca, cb = c.B / 255f * ca;
        for (int y = py0; y <= py1; y++)
        for (int x = px0; x <= px1; x++)
        {
            int hits = 0;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
                if (inside(x + (sx + 0.5f) / SS, y + (sy + 0.5f) / SS)) hits++;
            if (hits == 0) continue;
            float cov = hits / (float)(SS * SS);
            int i = y * W + x;
            float sa = ca * cov, inv = 1 - sa;
            _r[i] = cr * cov + _r[i] * inv;
            _g[i] = cg * cov + _g[i] * inv;
            _b[i] = cb * cov + _b[i] * inv;
            _a[i] = sa + _a[i] * inv;
        }
    }

    public static Func<float, float, bool> RoundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        return (px, py) =>
        {
            if (px < x || py < y || px > x + w || py > y + h) return false;
            float cx = Math.Clamp(px, x + r, x + w - r), cy = Math.Clamp(py, y + r, y + h - r);
            float dx = px - cx, dy = py - cy;
            return dx * dx + dy * dy <= r * r;
        };
    }

    public void FillRoundRect(float x, float y, float w, float h, float r, Rgba c) =>
        Fill(RoundRect(x, y, w, h, r), c, x - 1, y - 1, x + w + 1, y + h + 1);

    public void StrokeRoundRect(float x, float y, float w, float h, float r, float t, Rgba c)
    {
        var outer = RoundRect(x, y, w, h, r);
        var inner = RoundRect(x + t, y + t, w - 2 * t, h - 2 * t, Math.Max(0, r - t));
        Fill((px, py) => outer(px, py) && !inner(px, py), c, x - 1, y - 1, x + w + 1, y + h + 1);
    }

    public void FillPolygon(IReadOnlyList<(float X, float Y)> pts, Rgba c)
    {
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        Fill((px, py) =>
        {
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                var (xi, yi) = pts[i];
                var (xj, yj) = pts[j];
                if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }, c, minX - 1, minY - 1, maxX + 1, maxY + 1);
    }

    // 5×7 digits + '%' and '?'
    static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" },
        ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
        ['2'] = new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" },
        ['3'] = new[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" },
        ['4'] = new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" },
        ['5'] = new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" },
        ['6'] = new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" },
        ['7'] = new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" },
        ['8'] = new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" },
        ['9'] = new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" },
        ['?'] = new[] { "01110", "10001", "00001", "00010", "00100", "00000", "00100" },
    };

    /// <summary>Draws digits centred in the box, each cell slightly rounded, with an optional halo.</summary>
    public void Text(string s, float cx, float cy, float height, Rgba fill, Rgba? halo = null)
    {
        float cell = height / 7f, gap = cell * 0.8f;
        float width = s.Length * 5 * cell + (s.Length - 1) * gap;
        float x0 = cx - width / 2, y0 = cy - height / 2;
        foreach (var pass in halo is Rgba h ? new[] { (h, cell * 0.35f), (fill, 0f) } : new[] { (fill, 0f) })
        {
            float grow = pass.Item2;
            float x = x0;
            foreach (char ch in s)
            {
                if (Font.TryGetValue(ch, out var rows))
                    for (int r = 0; r < 7; r++)
                    for (int c = 0; c < 5; c++)
                        if (rows[r][c] == '1')
                            FillRoundRect(x + c * cell - grow, y0 + r * cell - grow, cell + 2 * grow, cell + 2 * grow, cell * 0.3f + grow, pass.Item1);
                x += 5 * cell + gap;
            }
        }
    }

    /// <summary>SNI pixmap format: ARGB32, network byte order.</summary>
    public byte[] ToArgbBigEndian()
    {
        var o = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            var (r, g, b, a) = Unpremultiply(i);
            o[i * 4] = a; o[i * 4 + 1] = r; o[i * 4 + 2] = g; o[i * 4 + 3] = b;
        }
        return o;
    }

    /// <summary>Notification image-data format: RGBA bytes.</summary>
    public byte[] ToRgba()
    {
        var o = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            var (r, g, b, a) = Unpremultiply(i);
            o[i * 4] = r; o[i * 4 + 1] = g; o[i * 4 + 2] = b; o[i * 4 + 3] = a;
        }
        return o;
    }

    (byte, byte, byte, byte) Unpremultiply(int i)
    {
        float a = _a[i];
        if (a <= 0.0001f) return (0, 0, 0, 0);
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return (B(_r[i] / a), B(_g[i] / a), B(_b[i] / a), B(a));
    }
}

/// <summary>Battery icons for the Linux tray / notifications (same look as on Windows).</summary>
internal static class BatteryArt
{
    static readonly Canvas.Rgba White = new(255, 255, 255);
    static readonly Canvas.Rgba Halo = new(0, 0, 0, 150);
    static readonly Canvas.Rgba Red = new(255, 99, 99);
    static readonly Canvas.Rgba Amber = new(252, 200, 60);
    static readonly Canvas.Rgba Green = new(108, 214, 95);
    static readonly Canvas.Rgba Yellow = new(255, 230, 80);

    static Canvas.Rgba FillFor(int? level, bool charging, int low) =>
        charging ? Green : level is int l && l <= Math.Max(5, low / 2) ? Red : level is int l2 && l2 <= low ? Amber : White;

    public static Canvas Render(int size, int? level, bool charging, IconStyle style, int low)
    {
        var c = new Canvas(size, size);
        float u = size / 16f;
        switch (style)
        {
            case IconStyle.Number:
            {
                string s = level is int l ? Math.Min(l, 100).ToString() : "?";
                float h = s.Length >= 3 ? size * 0.42f : size * 0.6f;
                c.Text(s, size / 2f, size * 0.42f, h, level is int lw && lw <= low && !charging ? Red : White, Halo);
                float barH = Math.Max(2, size / 8f);
                c.FillRoundRect(1, size - barH - 0.5f, size - 2, barH, barH / 2, White.WithAlpha(70));
                float frac = level is int lv ? Math.Clamp(lv / 100f, 0.06f, 1f) : 0;
                if (frac > 0) c.FillRoundRect(1, size - barH - 0.5f, (size - 2) * frac, barH, barH / 2, FillFor(level, charging, low));
                break;
            }
            case IconStyle.BatteryNumber:
            {
                float bw = size - 3 * u, bh = size * 0.74f, bx = 0.5f, by = (size - bh) / 2;
                c.FillRoundRect(bx, by, bw, bh, 2.4f * u, new Canvas.Rgba(0, 0, 0, 120));
                float frac = level is int l ? Math.Clamp(l / 100f, 0.05f, 1f) : 0;
                if (frac > 0)
                {
                    var outer = Canvas.RoundRect(bx, by, bw, bh, 2.4f * u);
                    float lim = bx + bw * frac;
                    c.Fill((x, y) => x <= lim && outer(x, y), FillFor(level, charging, low).WithAlpha(level is int lw && lw <= low ? (byte)200 : (byte)110), bx, by, lim, by + bh);
                }
                c.StrokeRoundRect(bx, by, bw, bh, 2.4f * u, Math.Max(1, u), White);
                c.FillRoundRect(bx + bw + 0.5f * u, by + bh * 0.32f, 1.6f * u, bh * 0.36f, 0.5f * u, White);
                string s = level is int lv ? Math.Min(lv, 100).ToString() : "?";
                c.Text(s, bx + bw / 2, by + bh / 2, s.Length >= 3 ? bh * 0.5f : bh * 0.66f, White, Halo);
                break;
            }
            default:
            {
                float stroke = Math.Max(1, 1.2f * u), tip = 1.8f * u;
                float bw = size - tip - 1.6f * u, bh = size * 0.52f, bx = 0.5f * u, by = (size - bh) / 2;
                c.StrokeRoundRect(bx - 0.8f * u, by - 0.8f * u, bw + 1.6f * u, bh + 1.6f * u, 3f * u, 0.9f * u, Halo);
                c.StrokeRoundRect(bx, by, bw, bh, 2.2f * u, stroke, White);
                c.FillRoundRect(bx + bw + 0.5f * u, by + bh * 0.3f, tip, bh * 0.4f, 0.8f * u, White);
                float pad = stroke + 0.9f * u;
                float frac = level is int l ? Math.Clamp(l / 100f, 0.06f, 1f) : 0;
                if (frac > 0) c.FillRoundRect(bx + pad, by + pad, (bw - 2 * pad) * frac, bh - 2 * pad, Math.Max(0.6f, 2.2f * u - pad), FillFor(level, charging, low));
                if (level is null) c.Text("?", bx + bw / 2, by + bh / 2, bh * 0.7f, White, Halo);
                if (charging) Bolt(c, bx + bw / 2, by + bh / 2, bh * 0.9f);
                break;
            }
        }
        return c;
    }

    static void Bolt(Canvas c, float cx, float cy, float h)
    {
        float w = h * 0.62f, x = cx - w / 2, y = cy - h / 2;
        var pts = new List<(float, float)>
        {
            (x + w * 0.62f, y), (x + w * 0.05f, y + h * 0.56f), (x + w * 0.45f, y + h * 0.56f),
            (x + w * 0.32f, y + h), (x + w * 0.95f, y + h * 0.40f), (x + w * 0.55f, y + h * 0.40f),
        };
        c.FillPolygon(pts.Select(p => (p.Item1 + 0.6f, p.Item2 + 0.6f)).ToList(), Halo);
        c.FillPolygon(pts, Yellow);
    }

    /// <summary>App icon (when nothing is connected): Bluetooth-like rune.</summary>
    public static Canvas App(int size)
    {
        var c = new Canvas(size, size);
        float u = size / 16f;
        c.StrokeRoundRect(2 * u, 4 * u, 11 * u, 8 * u, 2 * u, Math.Max(1, 1.2f * u), White);
        c.FillRoundRect(13.4f * u, 6.6f * u, 1.6f * u, 2.8f * u, 0.6f * u, White);
        c.Text("?", 7.5f * u, 8 * u, 4.6f * u, White);
        return c;
    }
}
