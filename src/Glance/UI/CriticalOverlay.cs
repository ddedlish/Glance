using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Glance;

/// <summary>
/// Critical-battery banner shown on top of whatever is running — including borderless/windowed
/// games, video players and presentations — WITHOUT taking focus or input:
/// per-pixel-alpha layered window, click-through (WS_EX_TRANSPARENT), no-activate, never in the taskbar.
/// The game keeps keyboard, mouse and fullscreen; the banner fades out by itself.
/// Exclusive-fullscreen (legacy DirectX) games can't be drawn over without injecting into them,
/// which Glance deliberately never does; there the alert appears as soon as you Alt+Tab.
/// </summary>
internal sealed class CriticalOverlay : Form
{
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;

    readonly Accessory _a;
    readonly System.Windows.Forms.Timer _life = new();
    float _sc = 1, _alpha, _drain = 1, _pulse;
    long _lastTopmost;
    Animator.Anim? _loop;
    bool _closing;

    public CriticalOverlay(Accessory a)
    {
        _a = a;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        _life.Tick += (_, _) => { _life.Stop(); FadeOut(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // Click-through: never hit-testable.
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTTRANSPARENT); return; }
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
        base.WndProc(ref m);
    }

    /// <summary>Shows the banner at the top centre of the monitor the user is looking at (foreground window).</summary>
    public void Present()
    {
        IntPtr fg = GetForegroundWindow();
        var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var b = screen.Bounds; // bounds, not working area: fullscreen apps cover the taskbar
        _sc = Native.DpiAt(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2)) / 96f;
        var size = new Size(Px(360), Px(_a.HasParts ? 86 : 70));
        Bounds = new Rectangle(b.Left + (b.Width - size.Width) / 2, b.Top + Px(28), size.Width, size.Height);
        Show();
        Render();
        Animator.Run(220, p => { _alpha = p; Render(); });
        Animator.Run(900, p => { _drain = 1 - p; Render(); });
        StartPulse();
        _life.Interval = 7000;
        _life.Start();
    }

    void StartPulse()
    {
        _loop = Animator.Run(1400, p => { _pulse = MathF.Sin(p * MathF.PI); Render(); KeepOnTop(); }, () =>
        {
            if (!_closing && !IsDisposed) StartPulse();
        });
    }

    void KeepOnTop()
    {
        // Some games re-assert their own topmost window; nudge ours back once a second (cheap, no activation).
        long now = Environment.TickCount64;
        if (now - _lastTopmost < 1000 || !IsHandleCreated) return;
        _lastTopmost = now;
        SetWindowPos(Handle, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 /*NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW*/);
    }

    public void FadeOut()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        _life.Stop();
        _loop?.Cancel();
        float start = _alpha;
        Animator.Run(300, p => { _alpha = start * (1 - p); Render(); }, () => { if (!IsDisposed) Close(); });
    }

    int Px(float dp) => (int)MathF.Round(dp * _sc);

    void Render()
    {
        if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0) return;
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Draw(g);
        }
        Push(bmp, (byte)Math.Clamp((int)(_alpha * 255), 0, 255));
    }

    void Draw(Graphics g)
    {
        float s = _sc, W = Width, H = Height;
        var red = Color.FromArgb(255, 84, 84);
        var pill = new RectangleF(1, 1, W - 2, H - 2);
        float radius = 18 * s;

        // glass card + pulsing red edge
        Painter.FillRound(g, pill, radius, Color.FromArgb(236, 26, 26, 28));
        Painter.StrokeRound(g, pill, radius, Color.FromArgb((int)(110 + 120 * _pulse), red), 1.6f * s);

        // draining battery
        int? lvl = _a.WeakestLevel;
        var batt = new RectangleF(18 * s, (H - 26 * s) / 2, 48 * s, 26 * s);
        float shown = (lvl ?? 0) + (100 - (lvl ?? 0)) * _drain; // animates from full down to the real value
        IconRenderer.DrawBigBattery(g, batt, (int)Math.Round(shown), false, Color.White, Color.FromArgb((int)(200 + 55 * _pulse), red), Color.FromArgb(50, 255, 255, 255));

        float x = batt.Right + 16 * s, w = W - x - 18 * s;
        Painter.Text(g, $"Критический заряд · {Fmt.Percent(lvl)}", Fonts.Strong(15 * s), red, new RectangleF(x, 12 * s, w, 22 * s));
        Painter.Text(g, _a.DisplayName, Fonts.Regular(13 * s), Color.White, new RectangleF(x, 34 * s, w, 20 * s));
        if (_a.HasParts)
        {
            var (ln, rn, cn) = Svc.PartLabels(_a);
            var parts = new List<string>(3);
            if (_a.Left.Known) parts.Add($"{ln} {_a.Left.Level}%");
            if (_a.Right.Known) parts.Add($"{rn} {_a.Right.Level}%");
            if (_a.Case.Known && cn.Length > 0) parts.Add($"{cn} {_a.Case.Level}%");
            Painter.Text(g, string.Join("  ·  ", parts), Fonts.Regular(12 * s), Color.FromArgb(200, 255, 255, 255), new RectangleF(x, 56 * s, w, 18 * s));
        }
    }

    void Push(Bitmap bmp, byte alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBmp);
            var size = new Size(bmp.Width, bmp.Height);
            var src = Point.Empty;
            var dst = Location;
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _life.Dispose(); _loop?.Cancel(); }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
