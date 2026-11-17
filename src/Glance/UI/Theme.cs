using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;

namespace Glance;

/// <summary>Windows 11 palette that follows the system light/dark mode and accent colour.</summary>
internal sealed class Theme
{
    public bool Dark { get; private init; }
    public bool TaskbarDark { get; private init; }
    public Color Accent { get; private init; }

    /// <summary>Liquid Glass look: vivid gradient backdrops, frosted translucent cards, specular highlights.</summary>
    public bool Glass { get; private init; }

    static bool _glassWanted = true;

    public Color Bg => Glass
        ? (Dark ? Color.FromArgb(18, 20, 28) : Color.FromArgb(236, 240, 248))
        : (Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243));
    public Color Surface => Glass
        ? (Dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255))
        : (Dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(251, 251, 251));
    public Color SurfaceHover => Glass
        ? (Dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(180, 255, 255, 255))
        : (Dark ? Color.FromArgb(52, 52, 52) : Color.FromArgb(245, 245, 245));
    public Color Border => Glass
        ? (Dark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(46, 0, 0, 0))
        : (Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(222, 222, 222));

    /// <summary>Solid equivalent of a (possibly translucent) colour over the window background — for WinForms controls.</summary>
    public Color Opaque(Color c) => c.A == 255 ? c : Color.FromArgb(255, Mix(Bg, Color.FromArgb(255, c), c.A / 255f));
    public Color SurfaceSolid => Opaque(Surface);
    public Color SurfaceHoverSolid => Opaque(SurfaceHover);

    // Vivid status palette (Liquid Glass) — same hues in tray icons and windows.
    public static readonly Color VividGreen = Color.FromArgb(48, 209, 88);
    public static readonly Color VividAmber = Color.FromArgb(255, 159, 10);
    public static readonly Color VividRed = Color.FromArgb(255, 69, 58);
    public static readonly Color VividCyan = Color.FromArgb(100, 210, 255);
    public Color Text => Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(27, 27, 27);
    public Color SubText => Glass
        ? (Dark ? Color.FromArgb(206, 208, 216) : Color.FromArgb(62, 64, 72))
        : (Dark ? Color.FromArgb(166, 166, 166) : Color.FromArgb(96, 96, 96));
    public Color Track => Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
    public Color Good => Dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(16, 124, 16);
    public Color Warn => Dark ? Color.FromArgb(252, 211, 77) : Color.FromArgb(157, 93, 0);
    public Color Bad => Dark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(196, 43, 28);
    public Color AccentText => Dark ? Mix(Accent, Color.White, 0.35f) : Mix(Accent, Color.Black, 0.12f);
    public Color OnAccent => Luma(AccentFill) > 0.6 ? Color.Black : Color.White;
    public Color AccentFill => Dark ? Mix(Accent, Color.White, 0.25f) : Accent;

    public static Theme Current { get; private set; } = Load();
    public static event Action? Changed;

    public static void Refresh()
    {
        var t = Load();
        if (t.Dark == Current.Dark && t.TaskbarDark == Current.TaskbarDark && t.Accent == Current.Accent && t.Glass == Current.Glass) return;
        Current = t;
        Changed?.Invoke();
    }

    /// <summary>Switches between Liquid Glass and the classic flat look.</summary>
    public static void SetGlass(bool glass)
    {
        _glassWanted = glass;
        Refresh();
    }

    static Theme Load()
    {
        bool appsLight = true, sysLight = false;
        var accent = Color.FromArgb(0, 120, 212);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int a) appsLight = a != 0;
            if (k?.GetValue("SystemUsesLightTheme") is int s) sysLight = s != 0;
        }
        catch { }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int c)
            {
                uint u = unchecked((uint)c); // ABGR
                accent = Color.FromArgb((int)(u & 0xFF), (int)((u >> 8) & 0xFF), (int)((u >> 16) & 0xFF));
            }
        }
        catch { }
        return new Theme { Dark = !appsLight, TaskbarDark = !sysLight, Accent = accent, Glass = _glassWanted };
    }

    public Color LevelColor(int? level, bool charging, int low)
    {
        if (level is not int l) return SubText;
        if (charging) return Good;
        if (l <= Math.Max(5, low / 2)) return Bad;
        if (l <= low) return Warn;
        return Text;
    }

    public Color BarColor(int? level, bool charging, int low)
    {
        if (level is not int l) return Track;
        if (Glass)
        {
            if (charging) return VividGreen;
            if (l <= Math.Max(5, low / 2)) return VividRed;
            if (l <= low) return VividAmber;
            return VividGreen;
        }
        if (charging) return Good;
        if (l <= Math.Max(5, low / 2)) return Bad;
        if (l <= low) return Warn;
        return AccentFill;
    }

    /// <summary>
    /// Paints the window backdrop. Glass: a light translucent tint over the live DWM blur of what is behind the
    /// window (wallpaper, apps) — truly see-through. Classic: flat opaque background.
    /// <paramref name="sheen"/> adds a soft specular gradient at the top (fixed-size popups only: it depends on position).
    /// </summary>
    public void PaintBackdrop(Graphics g, Rectangle r, bool sheen = false)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        if (!Glass)
        {
            using var solid = new SolidBrush(Bg);
            g.FillRectangle(solid, r);
            return;
        }
        using (var tint = new SolidBrush(GlassTint)) g.FillRectangle(tint, r);
        if (!sheen) return;
        var top = new RectangleF(r.X, r.Y, r.Width, Math.Max(2, r.Height * 0.45f));
        using var br = new LinearGradientBrush(new RectangleF(top.X, top.Y - 1, top.Width, top.Height + 2),
            Color.FromArgb(Dark ? 20 : 70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
        g.FillRectangle(br, top);
    }

    /// <summary>Tint laid over the blurred background: enough for legible text, still clearly transparent.</summary>
    public Color GlassTint => Dark ? Color.FromArgb(178, 18, 20, 28) : Color.FromArgb(185, 246, 247, 250);

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Alpha(Color c, int a) => Color.FromArgb(Math.Clamp(a, 0, 255), c);

    static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}

internal static class Fonts
{
    static readonly Dictionary<(string, float, FontStyle), Font> Cache = new();

    public static readonly string Text;
    public static readonly string Semibold;
    public static readonly string Display;
    public static readonly string Icons;
    public static readonly bool SemiboldIsReal;

    static Fonts()
    {
        HashSet<string> installed;
        try
        {
            using var col = new InstalledFontCollection();
            installed = col.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { installed = new HashSet<string>(); }

        string Pick(params string[] names) => names.FirstOrDefault(installed.Contains) ?? names[^1];
        Text = Pick("Segoe UI Variable Text", "Segoe UI");
        Display = Pick("Segoe UI Variable Display", "Segoe UI");
        Semibold = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");
        SemiboldIsReal = !string.Equals(Semibold, "Segoe UI", StringComparison.OrdinalIgnoreCase);
        Icons = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
    }

    public static Font Get(string family, float px, FontStyle style = FontStyle.Regular)
    {
        px = MathF.Max(6, MathF.Round(px * 2) / 2);
        var key = (family, px, style);
        if (!Cache.TryGetValue(key, out var f))
        {
            try { f = new Font(family, px, style, GraphicsUnit.Pixel); }
            catch { f = new Font("Segoe UI", px, style, GraphicsUnit.Pixel); }
            Cache[key] = f;
        }
        return f;
    }

    public static Font Regular(float px) => Get(Text, px);
    public static Font Strong(float px) => SemiboldIsReal ? Get(Semibold, px) : Get(Text, px, FontStyle.Bold);
    public static Font Big(float px) => SemiboldIsReal ? Get(Semibold, px) : Get(Display, px, FontStyle.Bold);
    public static Font Icon(float px) => Get(Icons, px);
}

internal static class Glyphs
{
    public const char Headphone = '', Headset = '', Speaker = '', Mouse = '', Keyboard = '',
        Gamepad = '', Pen = '', Phone = '', Bluetooth = '', Settings = '', Bolt = '',
        Close = '', Download = '', More = '', Warning = '', Check = '', Battery = '',
        Clock = '', Sync = '';

    public static char For(DeviceKind k) => k switch
    {
        DeviceKind.Earbuds or DeviceKind.Headphones or DeviceKind.Neckband => Headphone,
        DeviceKind.Headset => Headset,
        DeviceKind.Speaker => Speaker,
        DeviceKind.Mouse => Mouse,
        DeviceKind.Keyboard => Keyboard,
        DeviceKind.Gamepad => Gamepad,
        DeviceKind.Pen => Pen,
        DeviceKind.Phone => Phone,
        _ => Bluetooth,
    };
}

/// <summary>One shared 60 fps timer that runs only while an animation is active.</summary>
internal static class Animator
{
    public sealed class Anim
    {
        internal long Start; internal int Duration; internal Action<float> Step = _ => { }; internal Action? Done; internal bool Cancelled; internal Func<float, float> Ease = EaseOut;
        public void Cancel() => Cancelled = true;
    }

    static readonly List<Anim> Active = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static System.Windows.Forms.Timer? _timer;

    public static Anim Run(int ms, Action<float> step, Action? done = null, Func<float, float>? ease = null)
    {
        var a = new Anim { Start = Clock.ElapsedMilliseconds, Duration = Math.Max(1, ms), Step = step, Done = done, Ease = ease ?? EaseOut };
        Active.Add(a);
        if (_timer is null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.Enabled)
        {
            // 1 ms system timer resolution only while something animates: even ~60 fps frames instead of 15.6/31 ms jitter.
            try { timeBeginPeriod(1); _hiRes = true; } catch { }
            _timer.Start();
        }
        step(0);
        return a;
    }

    static void Tick()
    {
        long now = Clock.ElapsedMilliseconds;
        foreach (var a in Active.ToArray())
        {
            if (a.Cancelled) { Active.Remove(a); continue; }
            float t = Math.Clamp((now - a.Start) / (float)a.Duration, 0, 1);
            try { a.Step(a.Ease(t)); } catch (Exception e) { Log.Error("Anim", e); a.Cancelled = true; }
            if (t >= 1)
            {
                Active.Remove(a);
                try { a.Done?.Invoke(); } catch (Exception e) { Log.Error("Anim done", e); }
            }
        }
        if (Active.Count == 0)
        {
            _timer?.Stop();
            if (_hiRes) { try { timeEndPeriod(1); } catch { } _hiRes = false; }
        }
    }

    static bool _hiRes;
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    public static float EaseOut(float t) => 1 - MathF.Pow(1 - t, 3);

    /// <summary>Spring-like ease with a small overshoot (≈6 %) — for things that "land" (popups, cards).</summary>
    public static float Spring(float t)
    {
        const float c1 = 1.25f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }

    public static float Linear(float t) => t;
}

/// <summary>GDI+ drawing primitives in the Windows 11 style.</summary>
internal static class Painter
{
    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = SoftText ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
        g.CompositingQuality = CompositingQuality.HighQuality;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color c)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, RectangleF r, float radius, Color c, float width = 1)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var rr = RectangleF.Inflate(r, -width / 2, -width / 2);
        using var p = Round(rr, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }

    public static void Bar(Graphics g, RectangleF r, float fraction, Color fill, Color track)
    {
        FillRound(g, r, r.Height / 2, track);
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.001f) return;
        float w = Math.Max(r.Height, r.Width * fraction);
        FillRound(g, new RectangleF(r.X, r.Y, w, r.Height), r.Height / 2, fill);
    }

    public static void Ring(Graphics g, RectangleF r, float thickness, float? fraction, Color fill, Color track)
    {
        var rr = RectangleF.Inflate(r, -thickness / 2, -thickness / 2);
        using (var pt = new Pen(track, thickness)) g.DrawEllipse(pt, rr);
        if (fraction is float f && f > 0.002f)
        {
            using var pf = new Pen(fill, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pf, rr, -90, 360 * Math.Min(f, 1f));
        }
    }

    /// <summary>
    /// When true, text is drawn with GDI+ antialiasing (needed on transparent bitmaps: tray icons, layered banner).
    /// Otherwise text uses GDI/ClearType via TextRenderer — noticeably sharper on opaque window surfaces.
    /// </summary>
    [ThreadStatic] public static bool SoftText;

    /// <summary>Liquid Glass specular highlight: soft white sheen over the top of a rounded shape.</summary>
    public static void GlassSheen(Graphics g, RectangleF r, float radius, bool dark)
    {
        if (!Theme.Current.Glass || r.Height < 4) return;
        var top = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.55f);
        var state = g.Save();
        using (var clip = Round(r, radius)) g.SetClip(clip);
        using (var br = new LinearGradientBrush(new RectangleF(top.X, top.Y - 1, top.Width, top.Height + 2),
                   Color.FromArgb(dark ? 34 : 120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
            g.FillRectangle(br, top);
        g.Restore(state);
        // bright 1px rim on the upper edge
        using var pen = new Pen(Color.FromArgb(dark ? 70 : 160, 255, 255, 255), 1f);
        using var rim = Round(RectangleF.Inflate(r, -0.5f, -0.5f), radius);
        var st2 = g.Save();
        g.SetClip(new RectangleF(r.X, r.Y, r.Width, Math.Min(r.Height, radius * 1.6f + 2)));
        g.DrawPath(pen, rim);
        g.Restore(st2);
    }

    public static void Text(Graphics g, string s, Font f, Color c, RectangleF r,
        StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center, bool wrap = false)
    {
        if (string.IsNullOrEmpty(s) || r.Width <= 1) return;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        flags |= wrap ? TextFormatFlags.WordBreak : (TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        flags |= h switch { StringAlignment.Center => TextFormatFlags.HorizontalCenter, StringAlignment.Far => TextFormatFlags.Right, _ => TextFormatFlags.Left };
        flags |= v switch { StringAlignment.Center => TextFormatFlags.VerticalCenter, StringAlignment.Far => TextFormatFlags.Bottom, _ => TextFormatFlags.Top };
        var rc = Rectangle.FromLTRB((int)MathF.Floor(r.Left), (int)MathF.Floor(r.Top), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom));
        if (!SoftText)
        {
            TextRenderer.DrawText(g, s, f, rc, Color.FromArgb(255, c), flags | TextFormatFlags.PreserveGraphicsClipping);
            return;
        }
        if (rc.Width > 0 && rc.Height > 0 && (long)rc.Width * rc.Height <= 1_500_000 && f.Size < 120)
        {
            AlphaText.Draw(g, s, f, c, rc, flags);
            return;
        }
        using var sf = new StringFormat(wrap ? 0 : StringFormatFlags.NoWrap)
        {
            Alignment = h,
            LineAlignment = v,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, r, sf);
    }

    public static float Width(Graphics g, string s, Font f)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        // Same GDI metrics in both modes: glass text is rendered by GDI too (see AlphaText).
        return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    public static void Glyph(Graphics g, char glyph, float px, Color c, RectangleF r) =>
        Text(g, glyph.ToString(), Fonts.Icon(px), c, r, StringAlignment.Center, StringAlignment.Center);

    public static void Sparkline(Graphics g, RectangleF r, IReadOnlyList<Sample> samples, Color color, float scale)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long from = now - 24 * 3600;
        var pts = new List<PointF>();
        int? carried = null;
        foreach (var s in samples)
        {
            if (s.T < from) { carried = s.L; continue; }
            if (pts.Count == 0 && carried is int c) pts.Add(new PointF(r.X, r.Bottom - c / 100f * r.Height));
            pts.Add(new PointF(r.X + (s.T - from) / (float)(now - from) * r.Width, r.Bottom - s.L / 100f * r.Height));
        }
        if (pts.Count == 0 && carried is int only) pts.Add(new PointF(r.X, r.Bottom - only / 100f * r.Height));
        if (pts.Count == 0) return;
        pts.Add(new PointF(r.Right, pts[^1].Y)); // level holds until now
        if (pts.Count < 2) return;

        using (var fillPath = new GraphicsPath())
        {
            fillPath.AddLines(pts.ToArray());
            fillPath.AddLine(pts[^1].X, pts[^1].Y, r.Right, r.Bottom);
            fillPath.AddLine(r.Right, r.Bottom, pts[0].X, r.Bottom);
            fillPath.CloseFigure();
            using var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Theme.Alpha(color, 70), Theme.Alpha(color, 0), LinearGradientMode.Vertical);
            g.FillPath(br, fillPath);
        }
        using var pen = new Pen(color, 1.5f * scale) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts.ToArray());
    }
}

/// <summary>
/// Crisp text on transparent surfaces (glass windows, tray icons, the overlay banner). GDI+ antialiasing looks
/// soft ("soapy"), and GDI ClearType writes no alpha, so the text is rendered by GDI with full hinting and
/// ClearType into an opaque mask, and the per-subpixel coverage is turned into premultiplied colour + alpha.
/// The result keeps the sharp stems of normal Windows text on any background. Bitmaps are cached (LRU).
/// </summary>
internal static class AlphaText
{
    const int MaxEntries = 256;
    static readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp)>> Map = new();
    static readonly LinkedList<(string Key, Bitmap Bmp)> Lru = new();
    static readonly object Gate = new();

    public static void Draw(Graphics g, string s, Font f, Color c, Rectangle rc, TextFormatFlags flags)
    {
        string key = string.Concat(s, "\u0001", f.Name, "|", f.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", ((int)f.Style).ToString(),
            "\u0001", c.ToArgb().ToString(), "\u0001", rc.Width.ToString(), "x", rc.Height.ToString(), "\u0001", ((int)flags).ToString());
        Bitmap bmp;
        lock (Gate)
        {
            if (Map.TryGetValue(key, out var node)) { Lru.Remove(node); Lru.AddFirst(node); bmp = node.Value.Bmp; }
            else
            {
                bmp = Render(s, f, c, rc.Size, flags);
                Map[key] = Lru.AddFirst((key, bmp));
                while (Lru.Count > MaxEntries)
                {
                    var last = Lru.Last!;
                    Lru.RemoveLast();
                    Map.Remove(last.Value.Key);
                    last.Value.Bmp.Dispose();
                }
            }
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImage(bmp, rc, 0, 0, rc.Width, rc.Height, GraphicsUnit.Pixel);
            g.Restore(st);
        }
    }

    static Bitmap Render(string s, Font f, Color c, Size size, TextFormatFlags flags)
    {
        // Light text: white on black. Dark text: black on white (GDI picks the matching ClearType gamma).
        bool darkText = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;
        using var mask = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using (var mg = Graphics.FromImage(mask))
        {
            mg.Clear(darkText ? Color.White : Color.Black);
            TextRenderer.DrawText(mg, s, f, new Rectangle(Point.Empty, size), darkText ? Color.Black : Color.White, darkText ? Color.White : Color.Black, flags);
        }
        var dst = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(Point.Empty, size);
        var sd = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int n = size.Width * 4;
            var src = new byte[n];
            var row = new byte[n];
            int ca = c.A;
            for (int y = 0; y < size.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0 + y * sd.Stride, src, 0, n);
                for (int x = 0; x < n; x += 4)
                {
                    int cb = src[x], cg = src[x + 1], cr = src[x + 2];
                    if (darkText) { cb = 255 - cb; cg = 255 - cg; cr = 255 - cr; }
                    // Grayscale coverage from the hinted ClearType raster: GDI-sharp stems, no colour fringes
                    // (subpixel colour cannot be composited correctly over an unknown, blurred background).
                    int a = (cr + cg + cb + 1) / 3;
                    if (a < 3) { row[x] = row[x + 1] = row[x + 2] = row[x + 3] = 0; continue; }
                    a = a * ca / 255;
                    row[x] = (byte)(c.B * a / 255);
                    row[x + 1] = (byte)(c.G * a / 255);
                    row[x + 2] = (byte)(c.R * a / 255);
                    row[x + 3] = (byte)a;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, dd.Scan0 + y * dd.Stride, n);
            }
        }
        finally { mask.UnlockBits(sd); dst.UnlockBits(dd); }
        return dst;
    }
}
