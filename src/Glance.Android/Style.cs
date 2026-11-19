using Android.Graphics;
using AColor = Android.Graphics.Color;
using Path = Android.Graphics.Path;

namespace Glance.Droid;

/// <summary>Liquid Glass palette for dark and light system theme (same hues as the Windows app).</summary>
internal sealed class Palette
{
    public bool Dark { get; init; }
    public AColor BgTop { get; init; }
    public AColor BgBottom { get; init; }
    public AColor BlobA { get; init; }
    public AColor BlobB { get; init; }
    public AColor BlobC { get; init; }
    public AColor Text { get; init; }
    public AColor Sub { get; init; }
    public AColor Card { get; init; }
    public AColor CardBorder { get; init; }
    public AColor Track { get; init; }
    public AColor Accent { get; init; }

    public static readonly AColor Green = AColor.Argb(255, 48, 209, 88);
    public static readonly AColor Amber = AColor.Argb(255, 255, 159, 10);
    public static readonly AColor Red = AColor.Argb(255, 255, 69, 58);

    public static Palette For(bool dark) => dark
        ? new Palette
        {
            Dark = true,
            BgTop = AColor.Argb(255, 11, 13, 24),
            BgBottom = AColor.Argb(255, 20, 16, 38),
            BlobA = AColor.Argb(150, 76, 70, 230),
            BlobB = AColor.Argb(120, 20, 170, 200),
            BlobC = AColor.Argb(90, 236, 72, 153),
            Text = AColor.Argb(255, 246, 247, 252),
            Sub = AColor.Argb(255, 160, 166, 186),
            Card = AColor.Argb(34, 255, 255, 255),
            CardBorder = AColor.Argb(46, 255, 255, 255),
            Track = AColor.Argb(60, 255, 255, 255),
            Accent = AColor.Argb(255, 100, 170, 255),
        }
        : new Palette
        {
            Dark = false,
            BgTop = AColor.Argb(255, 236, 240, 250),
            BgBottom = AColor.Argb(255, 226, 230, 246),
            BlobA = AColor.Argb(110, 130, 140, 255),
            BlobB = AColor.Argb(100, 90, 210, 230),
            BlobC = AColor.Argb(80, 255, 150, 200),
            Text = AColor.Argb(255, 22, 24, 34),
            Sub = AColor.Argb(255, 98, 104, 124),
            Card = AColor.Argb(170, 255, 255, 255),
            CardBorder = AColor.Argb(220, 255, 255, 255),
            Track = AColor.Argb(40, 20, 30, 60),
            Accent = AColor.Argb(255, 0, 103, 192),
        };

    public static AColor Alpha(AColor c, int a) => AColor.Argb(Math.Clamp(a, 0, 255), c.R, c.G, c.B);

    public static AColor Mix(AColor a, AColor b, float t) => AColor.Argb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>Ring/bar colour: green, amber at ≤20 %, red at ≤10 %, green while charging.</summary>
    public static AColor Level(int? level, bool charging)
    {
        if (level is not int l) return AColor.Argb(255, 140, 146, 166);
        if (charging) return Green;
        if (l <= 10) return Red;
        if (l <= 20) return Amber;
        return Green;
    }

    public static AColor KindColor(DeviceKind k) => k switch
    {
        DeviceKind.Mouse => AColor.Argb(255, 20, 184, 166),
        DeviceKind.Keyboard => AColor.Argb(255, 139, 92, 246),
        DeviceKind.Earbuds or DeviceKind.Headphones or DeviceKind.Neckband or DeviceKind.Headset => AColor.Argb(255, 236, 72, 153),
        DeviceKind.Gamepad => AColor.Argb(255, 34, 197, 94),
        DeviceKind.Speaker => AColor.Argb(255, 249, 115, 22),
        DeviceKind.Pen => AColor.Argb(255, 245, 158, 11),
        DeviceKind.Phone => AColor.Argb(255, 59, 130, 246),
        DeviceKind.Watch => AColor.Argb(255, 14, 165, 233),
        _ => AColor.Argb(255, 100, 116, 139),
    };
}

/// <summary>Device glyphs drawn as strokes on a 24×24 grid (no icon font on Android).</summary>
internal static class Icons
{
    public static void Draw(Canvas c, DeviceKind kind, RectF box, AColor color, float stroke)
    {
        using var p = new Paint(PaintFlags.AntiAlias) { Color = color, StrokeWidth = stroke, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
        p.SetStyle(Paint.Style.Stroke);
        float s = Math.Min(box.Width(), box.Height()) / 24f;
        c.Save();
        c.Translate(box.CenterX() - 12 * s, box.CenterY() - 12 * s);
        c.Scale(s, s);
        p.StrokeWidth = stroke / s;
        using var path = new Path();
        switch (kind)
        {
            case DeviceKind.Earbuds:
            case DeviceKind.Headphones:
            case DeviceKind.Headset:
            case DeviceKind.Neckband:
                path.MoveTo(4, 15);
                path.LineTo(4, 12);
                path.CubicTo(4, 7.6f, 7.6f, 4, 12, 4);
                path.CubicTo(16.4f, 4, 20, 7.6f, 20, 12);
                path.LineTo(20, 15);
                c.DrawPath(path, p);
                c.DrawRoundRect(new RectF(3.5f, 13.5f, 8, 20), 2, 2, p);
                c.DrawRoundRect(new RectF(16, 13.5f, 20.5f, 20), 2, 2, p);
                break;
            case DeviceKind.Mouse:
                c.DrawRoundRect(new RectF(6.5f, 3, 17.5f, 21), 5.5f, 5.5f, p);
                c.DrawLine(12, 6.5f, 12, 9.5f, p);
                break;
            case DeviceKind.Keyboard:
                c.DrawRoundRect(new RectF(2.5f, 6, 21.5f, 18), 2.5f, 2.5f, p);
                for (int i = 0; i < 4; i++) c.DrawPoint(6.5f + i * 3.7f, 10, p);
                c.DrawLine(8, 14.2f, 16, 14.2f, p);
                break;
            case DeviceKind.Gamepad:
                path.MoveTo(7, 7);
                path.LineTo(17, 7);
                path.CubicTo(20, 7, 22, 10, 22, 14);
                path.CubicTo(22, 17.5f, 19.5f, 18.5f, 17.5f, 16.5f);
                path.LineTo(15.5f, 14.5f);
                path.LineTo(8.5f, 14.5f);
                path.LineTo(6.5f, 16.5f);
                path.CubicTo(4.5f, 18.5f, 2, 17.5f, 2, 14);
                path.CubicTo(2, 10, 4, 7, 7, 7);
                path.Close();
                c.DrawPath(path, p);
                c.DrawLine(7.5f, 9.5f, 7.5f, 12.5f, p);
                c.DrawLine(6, 11, 9, 11, p);
                c.DrawPoint(16.5f, 10, p);
                c.DrawPoint(15, 12, p);
                break;
            case DeviceKind.Speaker:
                c.DrawRoundRect(new RectF(6, 2.5f, 18, 21.5f), 3, 3, p);
                c.DrawCircle(12, 14.5f, 3.5f, p);
                c.DrawPoint(12, 7, p);
                break;
            case DeviceKind.Watch:
                c.DrawRoundRect(new RectF(6.5f, 6.5f, 17.5f, 17.5f), 3.5f, 3.5f, p);
                c.DrawLine(9, 6.5f, 9.5f, 2.5f, p);
                c.DrawLine(15, 6.5f, 14.5f, 2.5f, p);
                c.DrawLine(9, 17.5f, 9.5f, 21.5f, p);
                c.DrawLine(15, 17.5f, 14.5f, 21.5f, p);
                c.DrawLine(12, 12, 12, 9.5f, p);
                c.DrawLine(12, 12, 14, 13, p);
                break;
            case DeviceKind.Phone:
                c.DrawRoundRect(new RectF(7, 2.5f, 17, 21.5f), 2.5f, 2.5f, p);
                c.DrawLine(10.5f, 18.5f, 13.5f, 18.5f, p);
                break;
            case DeviceKind.Pen:
                path.MoveTo(4, 20);
                path.LineTo(5, 15.5f);
                path.LineTo(16, 4.5f);
                path.LineTo(19.5f, 8);
                path.LineTo(8.5f, 19);
                path.Close();
                c.DrawPath(path, p);
                break;
            default: // Bluetooth rune
                path.MoveTo(7, 7.5f);
                path.LineTo(17, 16.5f);
                path.LineTo(12, 21);
                path.LineTo(12, 3);
                path.LineTo(17, 7.5f);
                path.LineTo(7, 16.5f);
                c.DrawPath(path, p);
                break;
        }
        c.Restore();
    }

    /// <summary>Lightning bolt (charging), filled.</summary>
    public static void Bolt(Canvas c, RectF box, AColor color)
    {
        using var p = new Paint(PaintFlags.AntiAlias) { Color = color };
        float s = Math.Min(box.Width(), box.Height()) / 24f;
        c.Save();
        c.Translate(box.CenterX() - 12 * s, box.CenterY() - 12 * s);
        c.Scale(s, s);
        using var path = new Path();
        path.MoveTo(13.5f, 2);
        path.LineTo(5, 13.5f);
        path.LineTo(11, 13.5f);
        path.LineTo(10, 22);
        path.LineTo(19, 10);
        path.LineTo(13, 10);
        path.Close();
        c.DrawPath(path, p);
        c.Restore();
    }
}
