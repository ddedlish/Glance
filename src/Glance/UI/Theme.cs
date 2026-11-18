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
    /// <summary>Accent used for text: always readable (≥ 4.5:1) on this theme's surfaces, whatever the accent.</summary>
    public Color AccentText => EnsureContrast(Dark ? Mix(Accent, Color.White, 0.30f) : Mix(Accent, Color.Black, 0.10f), ReferenceBg, 4.5);
    /// <summary>Text on an accent fill: always white (the fill is deepened until white reads well), like Windows 11.</summary>
    public Color OnAccent => Color.White;
    /// <summary>Accent fill for buttons/segments: deep enough that its label reads well (≥ 4.5:1).</summary>
    public Color AccentFill
    {
        get
        {
            var fill = Accent;
            // deepen light accents (mint, graphite, sunset…) until white text on them reads clearly (≥ 4:1)
            for (int i = 0; i < 16 && Contrast(fill, Color.White) < 4.0; i++)
                fill = Mix(fill, Color.Black, 0.07f);
            return fill;
        }
    }

    /// <summary>Approximate background behind text (glass tint over a typical desktop, or the flat surface).</summary>
    Color ReferenceBg => Dark ? Color.FromArgb(34, 36, 46) : Color.FromArgb(238, 240, 245);

    static double RelLum(Color c)
    {
        static double Ch(int v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = RelLum(a), lb = RelLum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static Color EnsureContrast(Color fg, Color bg, double ratio)
    {
        var towards = RelLum(bg) < 0.4 ? Color.White : Color.Black;
        for (int i = 0; i < 14 && Contrast(fg, bg) < ratio; i++) fg = Mix(fg, towards, 0.1f);
        return fg;
    }

    public static Theme Current { get; private set; } = Load();
    public static event Action? Changed;

    public static void Refresh()
    {
        var t = Load();
        if (t.Dark == Current.Dark && t.TaskbarDark == Current.TaskbarDark && t.Accent == Current.Accent && t.Glass == Current.Glass && t.CustomAccent == Current.CustomAccent) return;
        Current = t;
        Changed?.Invoke();
    }

    static int _accentPreset;
    public static readonly (string Name, Color Color)[] AccentPresets =
    {
        ("Системный", Color.Empty), ("Аврора", Color.FromArgb(124, 92, 255)), ("Закат", Color.FromArgb(255, 107, 61)),
        ("Мята", Color.FromArgb(46, 196, 160)), ("Графит", Color.FromArgb(138, 148, 166)),
    };

    /// <summary>Pro accent themes: replaces the Windows accent and lightly tints the glass.</summary>
    public static void SetAccent(int preset)
    {
        _accentPreset = Math.Clamp(preset, 0, AccentPresets.Length - 1);
        Current = Load();
        Changed?.Invoke();
    }

    public bool CustomAccent { get; private init; }

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
        if (_accentPreset > 0) accent = AccentPresets[_accentPreset].Color;
        return new Theme { Dark = !appsLight, TaskbarDark = !sysLight, Accent = accent, Glass = _glassWanted, CustomAccent = _accentPreset > 0 };
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
    public Color GlassTint
    {
        get
        {
            var c = Dark ? Color.FromArgb(178, 18, 20, 28) : Color.FromArgb(185, 246, 247, 250);
            return CustomAccent ? Color.FromArgb(c.A, Mix(c, Accent, Dark ? 0.16f : 0.10f)) : c;
        }
    }

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
        if (Program.SafeMode.On) ms = 1; // safe mode: no motion
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
    /// <summary>The glass buffer being painted (enables ClearType on glass), null otherwise.</summary>
    [ThreadStatic] public static Bitmap? Target;
    [ThreadStatic] public static IntPtr TargetDc;

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
        // Glass: real ClearType, blended onto the actual pixels of the surface under the text (see GlassClearType).
        // Glass: DirectWrite grayscale text straight into the premultiplied buffer (see DWriteText).
        if (Target is not null && TargetDc != IntPtr.Zero && !DWriteText.Failed && DrawDWrite(g, s, f, c, rc, flags)) return;
        if (rc.Width > 0 && rc.Height > 0 && (long)rc.Width * rc.Height <= 1_500_000 && f.Size < 120)
        {
            // Text must never take the app down: on any GDI trouble fall back to GDI+ text below.
            try { AlphaText.Draw(g, s, f, c, rc, flags); return; }
            catch (Exception e) when (!AlphaText.Failed) { AlphaText.Failed = true; Log.Error("AlphaText", e); }
            catch (Exception) { }
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

    /// <summary>Every icon in Glance: drawn as a vector shape (see <see cref="GlyphPath"/>), centred in <paramref name="r"/>.</summary>
    public static void Glyph(Graphics g, char glyph, float px, Color c, RectangleF r)
    {
        var box = new RectangleF(r.X + (r.Width - px) / 2, r.Y + (r.Height - px) / 2, px, px);
        try { GlyphPath(g, glyph, box, c, 0.92f, Math.Max(0.35f, px / 40f)); }
        catch { Text(g, glyph.ToString(), Fonts.Icon(px), c, r, StringAlignment.Center, StringAlignment.Center); }
    }

    static readonly Dictionary<char, (GraphicsPath Path, RectangleF Bounds)> GlyphCache = new();

    static bool DrawDWrite(Graphics g, string s, Font f, Color c, Rectangle rc, TextFormatFlags flags)
    {
        float[] el;
        using (var m = g.Transform) el = m.Elements;
        if (Math.Abs(el[0] - 1) > 0.001 || Math.Abs(el[3] - 1) > 0.001 || Math.Abs(el[1]) > 0.001 || Math.Abs(el[2]) > 0.001) return false;
        float dx = el[4], dy = el[5];
        var cb = g.ClipBounds;
        var clip = RectangleF.Intersect(new RectangleF(cb.X + dx, cb.Y + dy, cb.Width, cb.Height), new RectangleF(0, 0, Target!.Width, Target.Height));
        if (clip.Width <= 0 || clip.Height <= 0) return true;
        g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        return DWriteText.Draw(TargetDc, Target.Size, s, f, c, new RectangleF(rc.X + dx, rc.Y + dy, rc.Width, rc.Height), clip, flags);
    }

    /// <summary>
    /// Vector icon: the Fluent glyph's outline as a path, fitted into <paramref name="r"/> (keeps aspect) and filled
    /// with antialiasing — razor-sharp at any size and on any background (no ClearType fringes or font hinting blur).
    /// <paramref name="fill"/> = share of the box the glyph occupies; <paramref name="bold"/> thickens thin strokes.
    /// </summary>
    public static void GlyphPath(Graphics g, char glyph, RectangleF r, Color c, float fill = 0.56f, float bold = 0)
    {
        if (!GlyphCache.TryGetValue(glyph, out var cached))
        {
            var p0 = new GraphicsPath();
            using (var ff = new FontFamily(Fonts.Icons))
                p0.AddString(glyph.ToString(), ff, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
            GlyphCache[glyph] = cached = (p0, p0.GetBounds());
        }
        using var path = (GraphicsPath)cached.Path.Clone();
        var b = cached.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        float side = Math.Min(r.Width, r.Height) * fill;
        float k = side / Math.Max(b.Width, b.Height);
        using (var m = new Matrix())
        {
            m.Translate(r.X + (r.Width - b.Width * k) / 2, r.Y + (r.Height - b.Height * k) / 2);
            m.Scale(k, k);
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
        }
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var br = new SolidBrush(c)) g.FillPath(br, path);
        if (bold > 0) using (var pen = new Pen(c, bold) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
        g.Restore(st);
    }

    /// <summary>Crisp vector chevron / back arrow (no text glyph).</summary>
    public static void Chevron(Graphics g, RectangleF r, Color c, float stroke, bool left = false)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, h = Math.Min(r.Width, r.Height) * 0.22f;
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float dx = left ? h * 0.5f : -h * 0.5f;
        g.DrawLines(pen, new[] { new PointF(cx + dx, cy - h), new PointF(cx - dx, cy), new PointF(cx + dx, cy + h) });
        g.Restore(st);
    }

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
/// <summary>
/// Sharpest possible text on glass: native ClearType rendered against the real surface under the text.
/// The glass surfaces (tint + cards) are ~70 % opaque, so the pixels already in the buffer, flattened over a
/// typical backdrop colour, are what the text really sits on. GDI draws ClearType onto that, and the result is
/// written back keeping the surface's alpha. Falls back to grayscale where the surface is too transparent,
/// rotated/scaled, or outside the buffer.
/// </summary>
internal static class GlassClearType
{
    public static bool TryDraw(Graphics g, Bitmap target, string s, Font f, Color c, Rectangle rc, TextFormatFlags flags)
    {
        try
        {
            var m = g.Transform;
            var el = m.Elements;
            m.Dispose();
            if (Math.Abs(el[0] - 1) > 0.001 || Math.Abs(el[3] - 1) > 0.001 || Math.Abs(el[1]) > 0.001 || Math.Abs(el[2]) > 0.001) return false;
            int dx = (int)MathF.Round(el[4]), dy = (int)MathF.Round(el[5]);
            var dev = new Rectangle(rc.X + dx, rc.Y + dy, rc.Width, rc.Height);
            var clipW = g.ClipBounds; // world coordinates
            var clip = Rectangle.Round(new RectangleF(clipW.X + dx, clipW.Y + dy, clipW.Width, clipW.Height));
            var area = Rectangle.Intersect(Rectangle.Intersect(dev, clip), new Rectangle(0, 0, target.Width, target.Height));
            if (area.Width <= 0 || area.Height <= 0) return true; // nothing visible
            if ((long)area.Width * area.Height > 600_000) return false;

            g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync); // everything drawn so far must be in the pixels we read
            bool dark = Theme.Current.Dark;
            int bdR = dark ? 24 : 236, bdG = dark ? 26 : 238, bdB = dark ? 34 : 243; // typical blurred backdrop
            var data = target.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                int n = area.Width * 4;
                var px = new byte[n * area.Height];
                for (int y = 0; y < area.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * n, n);
                // average opacity check: too transparent → grayscale path
                long aSum = 0;
                for (int i = 3; i < px.Length; i += 4) aSum += px[i];
                if (aSum < 0.6 * 255 * (px.Length / 4)) return false;

                using var flat = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
                var fd = flat.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                var fp = new byte[px.Length];
                for (int i = 0; i < px.Length; i += 4)
                {
                    int a = px[i + 3], inv = 255 - a;
                    fp[i] = (byte)Math.Min(255, px[i] + inv * bdB / 255);
                    fp[i + 1] = (byte)Math.Min(255, px[i + 1] + inv * bdG / 255);
                    fp[i + 2] = (byte)Math.Min(255, px[i + 2] + inv * bdR / 255);
                    fp[i + 3] = 255;
                }
                for (int y = 0; y < area.Height; y++) System.Runtime.InteropServices.Marshal.Copy(fp, y * n, fd.Scan0 + y * fd.Stride, n);
                flat.UnlockBits(fd);

                using (var fg = Graphics.FromImage(flat))
                    TextRenderer.DrawText(fg, s, f, new Rectangle(dev.X - area.X, dev.Y - area.Y, dev.Width, dev.Height), Color.FromArgb(255, c), flags);

                fd = flat.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                var tp = new byte[px.Length];
                for (int y = 0; y < area.Height; y++) System.Runtime.InteropServices.Marshal.Copy(fd.Scan0 + y * fd.Stride, tp, y * n, n);
                flat.UnlockBits(fd);
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (tp[i] == fp[i] && tp[i + 1] == fp[i + 1] && tp[i + 2] == fp[i + 2]) continue; // untouched by text
                    int a = Math.Max((int)px[i + 3], 200), inv = 255 - a;
                    px[i] = (byte)Math.Clamp(tp[i] - inv * bdB / 255, 0, a);
                    px[i + 1] = (byte)Math.Clamp(tp[i + 1] - inv * bdG / 255, 0, a);
                    px[i + 2] = (byte)Math.Clamp(tp[i + 2] - inv * bdR / 255, 0, a);
                    px[i + 3] = (byte)a;
                }
                for (int y = 0; y < area.Height; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * n, data.Scan0 + y * data.Stride, n);
                return true;
            }
            finally { target.UnlockBits(data); }
        }
        catch { return false; }
    }
}

internal static class AlphaText
{
    /// <summary>Set after the first failure so the error is logged once.</summary>
    public static bool Failed;
    // Coverage → alpha curves: light text on dark glass gets a touch more weight (thin strokes stay solid),
    // dark text on light glass a touch less (avoids a bold, smeared look).
    static readonly byte[] GammaLight = Curve(0.95), GammaDark = Curve(1.0); // close to native weight, not bolder
    [ThreadStatic] static byte[]? _gammaTs;
    static byte[] _gamma { get => _gammaTs ?? GammaLight; set => _gammaTs = value; }

    static byte[] Curve(double g)
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, g));
        return t;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr dc, int color);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr CreateFontIndirectW(ref LOGFONTW lf);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int DrawTextW(IntPtr dc, string text, int len, ref RECT r, uint format);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct LOGFONTW
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    static readonly Dictionary<(string, float, FontStyle), IntPtr> GrayFonts = new();

    static IntPtr GrayFont(Font f)
    {
        var k = (f.Name, f.Size, f.Style);
        if (GrayFonts.TryGetValue(k, out var h)) return h;
        // Built by hand (Font.ToLogFont can't fill a struct with a string field). Glance fonts are in pixels.
        float px = f.Unit == GraphicsUnit.Pixel ? f.Size : f.SizeInPoints * 96f / 72f;
        var lf = new LOGFONTW
        {
            lfHeight = -(int)MathF.Round(px),
            lfWeight = f.Bold ? 700 : f.Name.Contains("Semibold", StringComparison.OrdinalIgnoreCase) ? 600 : 400,
            lfItalic = (byte)(f.Italic ? 1 : 0),
            lfUnderline = (byte)(f.Underline ? 1 : 0),
            lfStrikeOut = (byte)(f.Strikeout ? 1 : 0),
            lfCharSet = 1, // DEFAULT_CHARSET (Cyrillic, symbols)
            lfQuality = 4, // ANTIALIASED_QUALITY: hinted grayscale
            lfFaceName = f.Name.Length > 31 ? f.Name[..31] : f.Name,
        };
        h = CreateFontIndirectW(ref lf);
        GrayFonts[k] = h;
        return h;
    }

    static void GrayText(IntPtr hdc, string s, Font f, Size size, TextFormatFlags flags)
    {
        var font = GrayFont(f);
        var old = SelectObject(hdc, font);
        SetBkMode(hdc, 1 /*TRANSPARENT*/);
        SetTextColor(hdc, 0xFFFFFF);
        var r = new RECT { R = size.Width, B = size.Height };
        DrawTextW(hdc, s, s.Length, ref r, (uint)flags & 0x0002FFFF);
        SelectObject(hdc, old);
    }

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
        // GDI grayscale antialiasing (ANTIALIASED_QUALITY) with full hinting: glyph stems snap to whole pixels,
        // so text on glass is as sharp as standard Windows grayscale text (ClearType colour can't be composited
        // over an unknown blurred background, and averaging it softened the edges).
        bool darkText = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;
        using var mask = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using (var mg = Graphics.FromImage(mask))
        {
            mg.Clear(Color.Black);
            IntPtr hdc = mg.GetHdc();
            try { GrayText(hdc, s, f, size, flags); }
            finally { mg.ReleaseHdc(hdc); }
        }
        _gamma = darkText ? GammaDark : GammaLight;
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
                    int a = _gamma[Math.Max(src[x + 2], Math.Max(src[x + 1], src[x]))];
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
