using System.Drawing.Drawing2D;

namespace Glance;

/// <summary>
/// «Живая жидкость» inside a gauge: translucent liquid up to the level with a bright meniscus. It rocks after
/// a push (a window springs up, the cursor touches it) and settles; while charging it ripples and bubbles rise.
/// Two shapes: a disc (inside a ring gauge) and a horizontal capsule (a level bar, the liquid front is the edge).
/// One instance per gauge, so several gauges move independently (seed shifts the wave phase).
/// </summary>
internal sealed class LiquidFill
{
    public const int SloshMs = 2800;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    long _t0 = -100_000;
    float _power;
    readonly float _seed;

    public LiquidFill(float seed = 0) => _seed = seed;

    /// <summary>Gives the liquid a push; it rocks and settles. delayMs lets several gauges splash one after another.</summary>
    public void Push(float power, int delayMs = 0)
    {
        if (Program.SafeMode.On) return; // safe mode: no motion
        long now = Clock.ElapsedMilliseconds;
        _power = Math.Min(1.4f, Energy(now) + power);
        _t0 = now + Math.Max(0, delayMs);
    }

    /// <summary>True while a push is still visibly rocking the liquid (callers keep repainting).</summary>
    public bool Sloshing => Clock.ElapsedMilliseconds - _t0 < SloshMs;

    float Energy(long now)
    {
        float dt = now - _t0;
        return dt < 0 ? _power : dt < SloshMs ? _power * MathF.Exp(-dt / 700f) : 0;
    }

    /// <summary>The disc inside a ring gauge, filled from the bottom up to <paramref name="level"/> (0–100).</summary>
    public void DrawDisc(Graphics g, RectangleF disc, float level, Color color, bool charging, float s, Theme t)
    {
        if (level <= 0.5f || disc.Width < 8 * s) return;
        long now = Clock.ElapsedMilliseconds;
        float dt = Math.Max(0, now - _t0);
        float push = Energy(now);
        float tilt = push * MathF.Sin(dt / 160f) * 7f * s * disc.Width / (60 * s);   // side-to-side rocking
        float amp = (charging ? 1.7f : 1.1f) * s + push * 2.6f * s;                   // wave height
        float phase = now / (charging ? 300f : 480f) + _seed;
        float surface = disc.Bottom - disc.Height * Math.Clamp(level, 0, 100) / 100f;

        var st = g.Save();
        using (var clip = new GraphicsPath())
        {
            clip.AddEllipse(disc);
            g.SetClip(clip, CombineMode.Intersect);
        }
        // back wave: fainter, out of phase — gives the liquid depth
        using (var back = WavePath(disc, surface - 1.5f * s, amp * 0.9f, -tilt * 0.6f, -phase * 0.85f + 2.2f, s))
        using (var b = new SolidBrush(Theme.Alpha(color, t.Dark ? 46 : 40)))
            g.FillPath(b, back);
        // front wave: gradient from a brighter surface to a deeper bottom
        using (var front = WavePath(disc, surface, amp, tilt, phase, s))
        {
            float topY = surface - amp - Math.Abs(tilt) - 2 * s;
            var top = new RectangleF(disc.X, Math.Min(topY, disc.Bottom - 2 * s), disc.Width, Math.Max(2 * s, disc.Bottom - topY + 2 * s));
            using (var b = new LinearGradientBrush(top, Theme.Alpha(color, t.Dark ? 120 : 105), Theme.Alpha(color, t.Dark ? 62 : 56), LinearGradientMode.Vertical))
                g.FillPath(b, front);
            // meniscus: a thin bright line along the surface
            using var edge = new GraphicsPath();
            edge.AddLines(WavePoints(disc, surface, amp, tilt, phase, s));
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
                float p = ((now + i * 397 + (long)(_seed * 1000)) % (long)period) / period;
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

    /// <summary>
    /// A level bar as a capsule of liquid: filled from the left to <paramref name="level"/> (0–100); the front edge
    /// is a living wave that leans and wobbles after a push, with a bright meniscus and a sheen gliding along the top.
    /// While charging small bubbles drift towards the front.
    /// </summary>
    public void DrawCapsule(Graphics g, RectangleF bar, float level, Color color, bool charging, float s, Theme t)
    {
        if (level <= 0.5f || bar.Width < 8 * s || bar.Height < 2) return;
        long now = Clock.ElapsedMilliseconds;
        float dt = Math.Max(0, now - _t0);
        float push = Energy(now);
        float h = bar.Height, cy = bar.Y + h / 2;
        float lean = push * MathF.Sin(dt / 150f) * 1.15f * h;            // the front leans forward and back
        float amp = (charging ? 0.16f : 0.1f) * h + push * 0.18f * h;     // ripple along the front
        float phase = now / (charging ? 260f : 420f) + _seed;
        float front = bar.X + Math.Max(h * 0.6f, bar.Width * Math.Clamp(level, 0, 100) / 100f);

        PointF[] Edge(float x0, float a, float ph)
        {
            int n = Math.Max(6, (int)(h / Math.Max(1.5f, 2f * s)) + 2);
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float v = i / (float)(n - 1);
                float y = bar.Y - 1 + v * (h + 2);
                float x = x0 + lean * (cy - y) / h + a * MathF.Sin(v * MathF.PI * 1.6f + ph) + a * 0.4f * MathF.Sin(v * MathF.PI * 3.3f - ph * 1.3f);
                pts[i] = new PointF(x, y);
            }
            return pts;
        }

        GraphicsPath Body(PointF[] edge)
        {
            var p = new GraphicsPath();
            p.AddLine(bar.X - 2, bar.Bottom + 1, bar.X - 2, bar.Y - 1);
            p.AddLines(edge);
            p.CloseFigure();
            return p;
        }

        var st = g.Save();
        using (var clip = Painter.Round(bar, h / 2)) g.SetClip(clip, CombineMode.Intersect);

        // back layer: a fainter front slightly ahead and out of phase — depth
        using (var back = Body(Edge(front + 1.2f * s, amp * 0.9f, -phase * 0.8f + 2.1f)))
        using (var b = new SolidBrush(Theme.Alpha(color, t.Dark ? 80 : 70)))
            g.FillPath(b, back);
        // the liquid: brighter at the top, deeper at the bottom
        var edgePts = Edge(front, amp, phase);
        using (var body = Body(edgePts))
        using (var b = new LinearGradientBrush(new RectangleF(bar.X, bar.Y - 1, bar.Width, h + 2), Theme.Mix(color, Color.White, 0.28f), color, 90f))
            g.FillPath(b, body);
        // sheen gliding along the top of the liquid
        float sw = Math.Max(18 * s, bar.Width * 0.22f), span = Math.Max(1, front - bar.X + sw);
        float sx = bar.X - sw + (now / (charging ? 900f : 1600f) + _seed) % 1f * span;
        var sheen = new RectangleF(sx, bar.Y + h * 0.12f, sw, h * 0.32f);
        using (var body = Body(edgePts))
        {
            var st2 = g.Save();
            g.SetClip(body, CombineMode.Intersect);
            using var sb = new LinearGradientBrush(new RectangleF(sheen.X - 1, sheen.Y, sheen.Width + 2, sheen.Height), Color.Transparent, Color.Transparent, 0f);
            sb.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(t.Dark ? 70 : 110, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            using var sp = Painter.Round(sheen, sheen.Height / 2);
            g.FillPath(sb, sp);
            // bubbles drift towards the front while charging
            if (charging)
            {
                using var bb = new SolidBrush(Color.White);
                for (int i = 0; i < 4; i++)
                {
                    float period = 1700 + i * 330;
                    float p = ((now + i * 431 + (long)(_seed * 1000)) % (long)period) / period;
                    float x = bar.X + h * 0.5f + p * (front - bar.X - h * 0.5f);
                    float y = cy + MathF.Sin(i * 2.1f + p * 7f) * h * 0.22f;
                    float r = (0.9f + (i % 2) * 0.5f) * s;
                    bb.Color = Color.FromArgb((int)((t.Dark ? 160 : 210) * MathF.Sin(MathF.PI * p)), 255, 255, 255);
                    g.FillEllipse(bb, x - r, y - r, r * 2, r * 2);
                }
            }
            g.Restore(st2);
        }
        // meniscus along the front edge
        using (var pen = new Pen(Color.FromArgb(t.Dark ? 110 : 170, 255, 255, 255), 1.2f * s))
            g.DrawLines(pen, edgePts);
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

    static GraphicsPath WavePath(RectangleF disc, float surface, float amp, float tilt, float phase, float s)
    {
        var path = new GraphicsPath();
        var pts = WavePoints(disc, surface, amp, tilt, phase, s);
        path.AddLines(pts);
        path.AddLine(pts[^1], new PointF(pts[^1].X, disc.Bottom + 2 * s));
        path.AddLine(new PointF(pts[^1].X, disc.Bottom + 2 * s), new PointF(pts[0].X, disc.Bottom + 2 * s));
        path.CloseFigure();
        return path;
    }
}
