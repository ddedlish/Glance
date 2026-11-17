using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Glance;

/// <summary>
/// Hidden top-level window that owns all tray icons and receives shell / system broadcasts
/// (TaskbarCreated after Explorer restarts, theme change, resume from sleep, hotkey).
/// </summary>
internal sealed class TrayHost : NativeWindow, IDisposable
{
    public const int WM_TRAYBASE = 0x8100;
    const int WM_SETTINGCHANGE = 0x001A, WM_POWERBROADCAST = 0x0218, WM_HOTKEY = 0x0312, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;

    readonly uint _taskbarCreated;
    readonly Dictionary<uint, TrayIcon> _icons = new();
    uint _nextId = 1;
    bool _hotkey;

    public event Action? TaskbarRecreated, ThemeChanged, Resumed, HotkeyPressed, DisplayChanged;

    public TrayHost()
    {
        CreateHandle(new CreateParams { Caption = "Glance.TrayHost" });
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreated != 0) Native.ChangeWindowMessageFilterEx(Handle, _taskbarCreated, 1, IntPtr.Zero);
    }

    public TrayIcon CreateIcon(string key)
    {
        var icon = new TrayIcon(this, _nextId++, key);
        _icons[icon.Id] = icon;
        return icon;
    }

    internal void Forget(TrayIcon icon) => _icons.Remove(icon.Id);

    public bool SetHotkey(bool enabled)
    {
        if (enabled == _hotkey) return true;
        if (enabled)
        {
            // Ctrl + Alt + B, no auto-repeat
            _hotkey = Native.RegisterHotKey(Handle, 1, 0x0001 | 0x0002 | 0x4000, (uint)Keys.B);
            if (!_hotkey) Log.Info("Hotkey Ctrl+Alt+B is taken by another app");
            return _hotkey;
        }
        Native.UnregisterHotKey(Handle, 1);
        _hotkey = false;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        int msg = m.Msg;
        if (msg >= WM_TRAYBASE && msg < WM_TRAYBASE + 0x3000)
        {
            uint id = (uint)(msg - WM_TRAYBASE);
            if (_icons.TryGetValue(id, out var icon))
            {
                long lp = m.LParam.ToInt64(), wp = m.WParam.ToInt64();
                int evt = (int)(lp & 0xFFFF);
                var pt = new Point((short)(wp & 0xFFFF), (short)((wp >> 16) & 0xFFFF));
                try { icon.Dispatch(evt, pt); } catch (Exception e) { Log.Error("Tray event", e); }
            }
            return;
        }
        if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            foreach (var i in _icons.Values.ToList()) i.Readd();
            TaskbarRecreated?.Invoke();
        }
        switch (msg)
        {
            case WM_SETTINGCHANGE:
                if (m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet") ThemeChanged?.Invoke();
                break;
            case WM_POWERBROADCAST:
                if (m.WParam.ToInt64() == 0x12 /*PBT_APMRESUMEAUTOMATIC*/) Resumed?.Invoke();
                break;
            case WM_HOTKEY:
                HotkeyPressed?.Invoke();
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                DisplayChanged?.Invoke();
                break;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var i in _icons.Values.ToList()) i.Dispose();
        SetHotkey(false);
        DestroyHandle();
    }
}

/// <summary>
/// Shell_NotifyIcon wrapper with a stable GUID per device: Windows 11 remembers
/// "show in taskbar" per GUID, so once promoted the icon stays visible across restarts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    readonly TrayHost _host;
    readonly Guid _guid;
    bool _useGuid = true, _added, _richHover, _disposed;
    Icon? _icon;
    string _tip = "";

    public uint Id { get; }
    public string Key { get; }

    public event Action<Point>? Clicked, MenuRequested;
    public event Action? HoverOpened, HoverClosed;

    public TrayIcon(TrayHost host, uint id, string key)
    {
        _host = host;
        Id = id;
        Key = key;
        _guid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("Glance.tray.v1." + key)));
    }

    public void Set(Icon icon, string tip, bool richHover)
    {
        if (_disposed) { icon.Dispose(); return; }
        var old = _icon;
        _icon = icon;
        _tip = tip.Length > 127 ? tip[..126] + "…" : tip;
        _richHover = richHover;
        if (_added)
        {
            var d = Build();
            if (!Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref d)) { _added = false; Add(); }
        }
        else Add();
        old?.Dispose();
    }

    void Add()
    {
        if (_icon is null) return;
        if (_useGuid)
        {
            // A stale icon with our GUID may survive a crash – remove it first.
            var del = Build();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref del);
        }
        var d = Build();
        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, ref d))
        {
            if (!_useGuid) return;
            // GUID is bound to another exe path (app was moved) – fall back to id-based icon.
            _useGuid = false;
            d = Build();
            if (!Native.Shell_NotifyIconW(Native.NIM_ADD, ref d)) return;
        }
        d.uVersion = 4; // NOTIFYICON_VERSION_4
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref d);
        _added = true;
    }

    public void Readd()
    {
        _added = false;
        Add();
    }

    Native.NOTIFYICONDATAW Build()
    {
        uint flags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        if (!_richHover) flags |= Native.NIF_SHOWTIP;
        if (_useGuid) flags |= Native.NIF_GUID;
        return new Native.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _host.Handle,
            uID = Id,
            uFlags = flags,
            uCallbackMessage = (uint)(TrayHost.WM_TRAYBASE + Id),
            hIcon = _icon?.Handle ?? IntPtr.Zero,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
            guidItem = _useGuid ? _guid : Guid.Empty,
        };
    }

    public Rectangle? GetRect()
    {
        if (!_added) return null;
        var id = new Native.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(),
            hWnd = _useGuid ? IntPtr.Zero : _host.Handle,
            uID = _useGuid ? 0 : Id,
            guidItem = _useGuid ? _guid : Guid.Empty,
        };
        if (Native.Shell_NotifyIconGetRect(ref id, out var r) != 0) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    internal void Dispatch(int evt, Point pt)
    {
        switch (evt)
        {
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                Clicked?.Invoke(pt);
                break;
            case Native.WM_CONTEXTMENU:
                MenuRequested?.Invoke(pt);
                break;
            case Native.NIN_POPUPOPEN:
                HoverOpened?.Invoke();
                break;
            case Native.NIN_POPUPCLOSE:
                HoverClosed?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var d = Build();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref d);
            _added = false;
        }
        _host.Forget(this);
        _icon?.Dispose();
        _icon = null;
    }
}

/// <summary>
/// Renders crisp per-device tray icons at the real tray size:
/// a draining battery (default), plain percent, or battery with the percent inside.
/// </summary>
internal static class IconRenderer
{
    public static int Size => Math.Max(16, Native.GetSystemMetrics(49 /*SM_CXSMICON*/));

    static bool Dark => Theme.Current.TaskbarDark;
    static Color Fg => Dark ? Color.White : Color.FromArgb(24, 24, 24);
    static Color Halo => Dark ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(170, 255, 255, 255);
    static Color LowColor => Dark ? Color.FromArgb(255, 99, 99) : Color.FromArgb(196, 43, 28);
    static Color WarnColor => Dark ? Color.FromArgb(252, 200, 60) : Color.FromArgb(176, 106, 0);
    static Color ChargeColor => Dark ? Color.FromArgb(108, 214, 95) : Color.FromArgb(16, 124, 16);

    static Color FillFor(int? level, bool charging, int low)
    {
        if (charging) return ChargeColor;
        if (level is int l && l <= Math.Max(5, low / 2)) return LowColor;
        if (level is int l2 && l2 <= low) return WarnColor;
        return Fg;
    }

    const int Supersample = 8;

    /// <summary>
    /// Renders the tray icon at 8× the tray size and downsamples with high-quality bicubic filtering:
    /// crisp, evenly antialiased edges and digits at every DPI (16–32 px), no blur.
    /// </summary>
    public static Icon Device(int? level, bool charging, IconStyle style, int low)
    {
        int sz = Size, big = sz * Supersample;
        using var hi = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(hi))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            Painter.SoftText = true;
            try
            {
                if (Theme.Current.Glass)
                {
                    if (style == IconStyle.Number) GlassNumber(g, big, level, charging, low);
                    else GlassBattery(g, big, level, charging, low, digits: style == IconStyle.BatteryNumber);
                }
                else switch (style)
                {
                    case IconStyle.Number: DrawNumber(g, big, level, charging, low); break;
                    case IconStyle.BatteryNumber: DrawBatteryNumber(g, big, level, charging, low); break;
                    default: DrawBattery(g, big, level, charging, low); break;
                }
            }
            finally { Painter.SoftText = false; }
        }
        using var bmp = Downsample(hi, sz);
        return ToIcon(bmp);
    }

    static Bitmap Downsample(Bitmap src, int size)
    {
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY); // no dark fringes at the edges
        g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return dst;
    }

    // ───────────── Liquid Glass ─────────────

    static Color GlassFill(int? level, bool charging, int low)
    {
        if (level is null) return Color.FromArgb(150, 150, 160);
        if (charging) return Theme.VividCyan;
        if (level <= Math.Max(5, low / 2)) return Theme.VividRed;
        if (level <= low) return Theme.VividAmber;
        return Theme.VividGreen;
    }

    static Color Lighten(Color c, float t) => Theme.Mix(c, Color.White, t);

    /// <summary>Glass battery: frosted translucent body, glossy vivid level, specular rim, optional fitted digits.</summary>
    static void GlassBattery(Graphics g, int S, int? level, bool charging, int low, bool digits)
    {
        float u = S / 16f;
        bool dark = Dark;
        // Digits variant: the battery fills the whole icon and the level becomes a glossy bar along the bottom,
        // so the number sits on dark glass with maximum contrast and size (readable even at 16 px).
        var body = digits ? new RectangleF(0.3f * u, 1.3f * u, 13.9f * u, 13.4f * u) : new RectangleF(0.6f * u, 3.7f * u, 12.7f * u, 8.6f * u);
        float rad = digits ? 3.2f * u : body.Height * 0.34f;
        var fill = GlassFill(level, charging, low);

        // soft contrast halo so the icon reads on any taskbar colour / wallpaper
        using (var halo = new Pen(dark ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(140, 255, 255, 255), 1.6f * u) { LineJoin = LineJoin.Round })
        using (var hp = Painter.Round(body, rad)) g.DrawPath(halo, hp);

        // frosted glass body
        using (var bp = Painter.Round(body, rad))
        using (var gb = new LinearGradientBrush(body, dark ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0),
                                                       dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0), LinearGradientMode.Vertical))
            g.FillPath(gb, bp);

        // glossy vivid level
        float pad = 1.25f * u;
        var inner = RectangleF.Inflate(body, -pad, -pad);
        if (digits) pad = 1.1f * u;
        inner = RectangleF.Inflate(body, -pad, -pad);
        float frac = level is int l ? Math.Clamp(l / 100f, 0.07f, 1f) : 0;
        if (frac > 0)
        {
            var lv = digits
                ? new RectangleF(inner.X, inner.Bottom - 2.7f * u, inner.Width * frac, 2.7f * u)
                : new RectangleF(inner.X, inner.Y, inner.Width * frac, inner.Height);
            float lr = digits ? lv.Height / 2 : Math.Max(0.5f * u, rad - pad);
            using var lp = Painter.Round(lv, lr);
            using (var lg = new LinearGradientBrush(new RectangleF(lv.X, lv.Y - 1, lv.Width, lv.Height + 2), Lighten(fill, 0.35f), fill, LinearGradientMode.Vertical))
                g.FillPath(lg, lp);
            var state = g.Save();
            g.SetClip(lp);
            using (var sheen = new LinearGradientBrush(new RectangleF(lv.X, lv.Y - 1, lv.Width, lv.Height * 0.55f + 2),
                       Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillRectangle(sheen, lv.X, lv.Y, lv.Width, lv.Height * 0.55f);
            g.Restore(state);
        }

        // rim + specular top edge
        var rimColor = dark ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(215, 25, 25, 30);
        using (var pen = new Pen(rimColor, 1.05f * u))
        using (var rp = Painter.Round(RectangleF.Inflate(body, -0.5f * u, -0.5f * u), rad))
            g.DrawPath(pen, rp);
        using (var spec = new Pen(Color.FromArgb(dark ? 130 : 200, 255, 255, 255), 0.55f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(spec, body.X + rad, body.Y + 1.35f * u, body.Right - rad, body.Y + 1.35f * u);

        // tip
        using (var tb = new SolidBrush(rimColor))
        using (var tp = Painter.Round(new RectangleF(body.Right + 0.4f * u, body.Y + body.Height * 0.34f, (digits ? 1.2f : 1.7f) * u, body.Height * 0.32f), 0.6f * u))
            g.FillPath(tb, tp);

        if (digits)
        {
            string s = level is int lvv ? Math.Clamp(lvv, 0, 100).ToString() : "?";
            var box = new RectangleF(inner.X + 0.25f * u, inner.Y + 0.35f * u, inner.Width - 0.5f * u, inner.Height - 3.6f * u);
            var tc = level is int lw && lw <= low && !charging ? Lighten(fill, 0.15f) : (dark ? Color.White : Color.FromArgb(20, 20, 24));
            FitText(g, s, box, tc, dark ? Color.FromArgb(170, 0, 0, 0) : Color.FromArgb(150, 255, 255, 255), 0.5f * u);
        }
        else
        {
            if (level is null) FitText(g, "?", RectangleF.Inflate(inner, -inner.Width * 0.3f, -inner.Height * 0.12f), Color.White, Color.FromArgb(150, 0, 0, 0), 0.5f * u);
            if (charging) Bolt(g, RectangleF.Inflate(body, 0, -0.2f * u));
        }
    }

    /// <summary>Big fitted digits (always the whole number, 100 included) over a glossy capsule level bar.</summary>
    static void GlassNumber(Graphics g, int S, int? level, bool charging, int low)
    {
        float u = S / 16f;
        var fill = GlassFill(level, charging, low);
        string s = level is int l ? Math.Clamp(l, 0, 100).ToString() : "?";
        var textColor = level is int lw && lw <= low && !charging ? fill : (Dark ? Color.White : Color.FromArgb(20, 20, 24));
        var shadow = Dark ? Color.FromArgb(170, 0, 0, 0) : Color.FromArgb(170, 255, 255, 255);
        FitText(g, s, new RectangleF(0.2f * u, 0.4f * u, 15.6f * u, 10.6f * u), textColor, shadow, 0.6f * u);

        var bar = new RectangleF(1.0f * u, 12.4f * u, 14f * u, 3.0f * u);
        using (var bp = Painter.Round(bar, bar.Height / 2))
        using (var bg = new SolidBrush(Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(55, 0, 0, 0)))
            g.FillPath(bg, bp);
        float frac = level is int lv ? Math.Clamp(lv / 100f, 0.08f, 1f) : 0;
        if (frac > 0)
        {
            var f = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * frac), bar.Height);
            using var fp = Painter.Round(f, bar.Height / 2);
            using var lg = new LinearGradientBrush(new RectangleF(f.X, f.Y - 1, f.Width, f.Height + 2), Lighten(fill, 0.4f), fill, LinearGradientMode.Vertical);
            g.FillPath(lg, fp);
        }
    }

    static readonly string DigitFamily = PickDigitFamily();

    static string PickDigitFamily()
    {
        try
        {
            using var col = new InstalledFontCollection();
            var names = col.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var n in new[] { "Bahnschrift SemiBold SemiConden", "Bahnschrift SemiBold Condensed", "Bahnschrift SemiBold", "Segoe UI Variable Display Semib", "Segoe UI Semibold" })
                if (names.Contains(n)) return n;
        }
        catch { }
        return "Segoe UI";
    }

    /// <summary>Scales the text outline to fill <paramref name="box"/> exactly — never clipped, as large as possible.</summary>
    static void FitText(Graphics g, string s, RectangleF box, Color fill, Color shadow, float shadowOffset)
    {
        using var path = new GraphicsPath();
        using var ff = new FontFamily(DigitFamily);
        var style = DigitFamily == "Segoe UI" ? FontStyle.Bold : FontStyle.Regular;
        path.AddString(s, ff, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        float sc = Math.Min(box.Width / b.Width, box.Height / b.Height);
        using (var m = new Matrix())
        {
            m.Translate(box.X + (box.Width - b.Width * sc) / 2, box.Y + (box.Height - b.Height * sc) / 2);
            m.Scale(sc, sc);
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
        }
        if (shadow.A > 0)
        {
            using var sp = (GraphicsPath)path.Clone();
            using (var sm = new Matrix()) { sm.Translate(0, shadowOffset); sp.Transform(sm); }
            using (var spen = new Pen(shadow, shadowOffset * 1.6f) { LineJoin = LineJoin.Round }) g.DrawPath(spen, sp);
            using (var sb = new SolidBrush(shadow)) g.FillPath(sb, sp);
        }
        using var fb = new SolidBrush(fill);
        g.FillPath(fb, path);
    }

    // ───────────── Classic ─────────────

    /// <summary>Horizontal battery that visibly drains; bolt when charging.</summary>
    static void DrawBattery(Graphics g, int sz, int? level, bool charging, int low)
    {
        float u = sz / 16f;
        float stroke = Math.Max(1f, MathF.Round(1.15f * u * 2) / 2);
        float tipW = Math.Max(1.5f, 1.8f * u);
        var body = new RectangleF(0.5f * u + stroke / 2, sz * 0.24f, sz - tipW - 1.4f * u - stroke, sz * 0.52f);
        float radius = 2.2f * u;

        using (var halo = new Pen(Halo, stroke + 1.6f * u))
        using (var hp = Painter.Round(body, radius))
            g.DrawPath(halo, hp);
        using (var pen = new Pen(Fg, stroke))
        using (var path = Painter.Round(body, radius))
            g.DrawPath(pen, path);
        using (var tip = new SolidBrush(Fg))
        using (var tp = Painter.Round(new RectangleF(body.Right + 0.6f * u, body.Y + body.Height * 0.3f, tipW, body.Height * 0.4f), 0.8f * u))
            g.FillPath(tip, tp);

        float pad = stroke + 0.9f * u;
        var inner = RectangleF.Inflate(body, -pad + stroke / 2, -pad + stroke / 2);
        float frac = level is int l ? Math.Clamp(l / 100f, 0.06f, 1f) : 0;
        if (frac > 0)
        {
            using var fill = new SolidBrush(FillFor(level, charging, low));
            using var fp = Painter.Round(new RectangleF(inner.X, inner.Y, inner.Width * frac, inner.Height), Math.Max(0.6f, radius - pad));
            g.FillPath(fill, fp);
        }
        if (level is null)
            Painter.Text(g, "?", Fonts.Get(Fonts.Text, sz * 0.5f, FontStyle.Bold), Fg, body, StringAlignment.Center);
        if (charging) Bolt(g, body);
    }

    /// <summary>Battery outline filled to level with the number on top (large, readable from 20 px).</summary>
    static void DrawBatteryNumber(Graphics g, int sz, int? level, bool charging, int low)
    {
        float u = sz / 16f;
        float stroke = Math.Max(1f, u);
        float tipW = Math.Max(1.2f, 1.4f * u);
        var body = new RectangleF(stroke / 2, sz * 0.12f, sz - tipW - 0.8f * u - stroke, sz * 0.76f);
        float radius = 2.4f * u;
        var fillColor = FillFor(level, charging, low);

        using (var bg = new SolidBrush(Theme.Alpha(Halo, 120)))
        using (var bp = Painter.Round(body, radius))
            g.FillPath(bg, bp);
        float frac = level is int l ? Math.Clamp(l / 100f, 0.05f, 1f) : 0;
        if (frac > 0)
        {
            var state = g.Save();
            using (var clip = Painter.Round(body, radius)) g.SetClip(clip);
            using var fill = new SolidBrush(Theme.Alpha(fillColor, level <= low ? 210 : 115));
            g.FillRectangle(fill, body.X, body.Y, body.Width * frac, body.Height);
            g.Restore(state);
        }
        using (var pen = new Pen(Fg, stroke))
        using (var path = Painter.Round(body, radius))
            g.DrawPath(pen, path);
        using (var tip = new SolidBrush(Fg))
            g.FillRectangle(tip, body.Right + 0.5f * u, body.Y + body.Height * 0.32f, tipW, body.Height * 0.36f);

        string s = level is int lv ? Math.Clamp(lv, 0, 100).ToString() : "?";
        FitText(g, s, RectangleF.Inflate(body, -body.Width * 0.08f, -body.Height * 0.16f), Fg, Halo, 0.5f * u);
        if (charging && sz >= 24) Bolt(g, new RectangleF(body.Right - body.Height * 0.55f, body.Y, body.Height * 0.55f, body.Height * 0.55f));
    }

    static void DrawNumber(Graphics g, int sz, int? level, bool charging, int low)
    {
        string s = level is int l ? Math.Clamp(l, 0, 100).ToString() : "?";
        float barH = Math.Max(2f, sz / 8f);
        bool isLow = level is int l0 && l0 <= low && !charging;
        FitText(g, s, new RectangleF(sz * 0.02f, sz * 0.03f, sz * 0.96f, sz - barH - sz * 0.1f), isLow ? LowColor : Fg, Halo, sz / 32f);

        float frac = level is int lv ? Math.Clamp(lv / 100f, 0.06f, 1f) : 0;
        using var track = new SolidBrush(Theme.Alpha(Fg, 70));
        g.FillRectangle(track, 1, sz - barH, sz - 2, barH);
        using var fill = new SolidBrush(FillFor(level, charging, low));
        g.FillRectangle(fill, 1, sz - barH, (sz - 2) * frac, barH);
    }

    static void OutlinedText(Graphics g, string s, float px, RectangleF r, Color fill, Color outline)
    {
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var ff = new FontFamily(Fonts.Text);
        path.AddString(s, ff, (int)FontStyle.Bold, px, r, sf);
        using (var pen = new Pen(outline, Math.Max(1.5f, px / 6f)) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, path);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
    }

    static void Bolt(Graphics g, RectangleF r)
    {
        // Simple vector lightning bolt centred in r, with a contrasting outline.
        float w = r.Height * 0.62f, h = r.Height * 0.9f;
        float x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) / 2;
        var pts = new[]
        {
            new PointF(x + w * 0.62f, y), new PointF(x + w * 0.05f, y + h * 0.56f), new PointF(x + w * 0.45f, y + h * 0.56f),
            new PointF(x + w * 0.32f, y + h), new PointF(x + w * 0.95f, y + h * 0.40f), new PointF(x + w * 0.55f, y + h * 0.40f),
        };
        using var path = new GraphicsPath();
        path.AddPolygon(pts);
        using (var pen = new Pen(Dark ? Color.FromArgb(20, 20, 20) : Color.White, Math.Max(1f, r.Height / 8f)) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, path);
        using var b = new SolidBrush(Dark ? Color.FromArgb(255, 230, 80) : Color.FromArgb(230, 160, 0));
        g.FillPath(b, path);
    }

    public static Icon App()
    {
        int sz = Size;
        using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Painter.SoftText = true;
            try { Painter.Glyph(g, Glyphs.Bluetooth, sz * 0.82f, Fg, new RectangleF(0, 0, sz, sz)); }
            finally { Painter.SoftText = false; }
        }
        return ToIcon(bmp);
    }

    /// <summary>Large battery picture for popups (drawn with the same visual language as the tray).</summary>
    public static void DrawBigBattery(Graphics g, RectangleF r, int? level, bool charging, Color outline, Color fill, Color track)
    {
        float stroke = Math.Max(1.5f, r.Height / 10f);
        float tipW = r.Height * 0.16f;
        var body = new RectangleF(r.X + stroke / 2, r.Y + stroke / 2, r.Width - tipW - stroke * 1.5f, r.Height - stroke);
        float radius = r.Height * 0.22f;
        using (var tb = new SolidBrush(track))
        using (var tp = Painter.Round(body, radius))
            g.FillPath(tb, tp);
        float pad = stroke * 1.4f;
        var inner = RectangleF.Inflate(body, -pad, -pad);
        float frac = level is int l ? Math.Clamp(l / 100f, 0.04f, 1f) : 0;
        if (frac > 0)
        {
            using var fb = new SolidBrush(fill);
            using var fp = Painter.Round(new RectangleF(inner.X, inner.Y, inner.Width * frac, inner.Height), Math.Max(1, radius - pad));
            g.FillPath(fb, fp);
        }
        using (var pen = new Pen(outline, stroke))
        using (var path = Painter.Round(body, radius))
            g.DrawPath(pen, path);
        using (var tipB = new SolidBrush(outline))
        using (var tipP = Painter.Round(new RectangleF(body.Right + stroke * 0.6f, body.Y + body.Height * 0.3f, tipW * 0.7f, body.Height * 0.4f), tipW * 0.25f))
            g.FillPath(tipB, tipP);
        if (charging) Bolt(g, body);
    }

    static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { Native.DestroyIcon(h); }
    }
}
