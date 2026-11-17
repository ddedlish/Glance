using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glance;

/// <summary>A control that paints a background layer its child controls must show through (window tint, settings card…).</summary>
internal interface IGlassLayer
{
    /// <summary>Paints the layer in the control's own client coordinates.</summary>
    void PaintLayer(Graphics g, Size size);
}

/// <summary>
/// Real Liquid Glass: the window gets the DWM blur-behind accent (the desktop/apps behind it are blurred live),
/// and every surface is rendered into a premultiplied 32-bit DIB and copied with its alpha intact — so translucent
/// tints, cards and antialiased corners stay truly see-through instead of turning grey or black.
/// Child controls first render the layers of their ancestors at their own position, so a toggle on a card
/// carries the card's tint and its rounded corners blend perfectly.
/// </summary>
internal static class GlassHost
{
    const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_PRINTCLIENT = 0x0318;

    /// <summary>Handles WM_PAINT / WM_ERASEBKGND for <paramref name="c"/>. Returns true when the message was consumed.</summary>
    public static bool Handle(Control c, ref Message m, AlphaBuffer buf, Action<Graphics>? self)
    {
        if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return true; }
        if (m.Msg != WM_PAINT || m.WParam != IntPtr.Zero) return false; // WM_PAINT with an HDC (DrawToBitmap) → default path
        var hdc = BeginPaint(c.Handle, out var ps);
        try
        {
            var size = c.ClientSize;
            if (size.Width <= 0 || size.Height <= 0 || hdc == IntPtr.Zero) return true;
            bool glass = Theme.Current.Glass;
            // Only the invalid part is rendered and copied (a tall scrolling page repaints just what is visible).
            var dirty = Rectangle.Intersect(Rectangle.FromLTRB(ps.rcPaint.Left, ps.rcPaint.Top, ps.rcPaint.Right, ps.rcPaint.Bottom), new Rectangle(Point.Empty, size));
            if (dirty.IsEmpty) return true;
            using (var g = buf.Begin(size))
            {
                g.SetClip(dirty);
                g.Clear(Color.Transparent);
                bool prev = Painter.SoftText;
                Painter.SoftText = glass; // GDI ClearType would write alpha 0 → see-through text on glass
                try
                {
                    Painter.Prepare(g);
                    PaintAncestors(c, g);
                    if (c is IGlassLayer own) own.PaintLayer(g, size);
                    self?.Invoke(g);
                }
                catch (Exception e) { Log.Error("Paint " + c.GetType().Name, e); }
                finally { Painter.SoftText = prev; }
            }
            buf.Present(hdc, dirty);
        }
        finally { EndPaint(c.Handle, ref ps); }
        return true;
    }

    /// <summary>Paints every ancestor layer (outermost first) translated to <paramref name="c"/>'s position.</summary>
    static void PaintAncestors(Control c, Graphics g)
    {
        var chain = new List<(Control Ctl, Point Offset)>();
        int ox = 0, oy = 0;
        for (Control? cur = c; cur.Parent is Control p; cur = p)
        {
            ox += cur.Left; oy += cur.Top;
            chain.Add((p, new Point(ox, oy)));
        }
        if (chain.Count == 0) return;
        // Outermost window is a plain WinForms form (installer): start from its solid background.
        if (chain[^1].Ctl is not IGlassLayer) g.Clear(Theme.Current.Opaque(chain[^1].Ctl.BackColor));
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Ctl is not IGlassLayer layer) continue;
            var st = g.Save();
            g.TranslateTransform(-chain[i].Offset.X, -chain[i].Offset.Y);
            layer.PaintLayer(g, chain[i].Ctl.ClientSize);
            g.Restore(st);
        }
    }

    // ── DWM blur-behind accent ──
    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);

    /// <summary>Turns the live blur of whatever is behind the window on or off.</summary>
    public static void SetBlur(IntPtr hwnd, bool on)
    {
        if (hwnd == IntPtr.Zero) return;
        var policy = new AccentPolicy { AccentState = on ? 3 /*ACCENT_ENABLE_BLURBEHIND*/ : 0 };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, mem, false);
            var data = new CompositionData { Attribute = 19 /*WCA_ACCENT_POLICY*/, Data = mem, Size = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (Exception e) { Log.Info("Blur: " + e.Message); }
        finally { Marshal.FreeHGlobal(mem); }
    }

    // ── user32 paint ──
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public Native.RECT rcPaint; public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
}

/// <summary>Premultiplied 32-bit DIB section with a GDI+ view; BitBlt copies colour and alpha to the window.</summary>
internal sealed class AlphaBuffer : IDisposable
{
    IntPtr _dib, _bits, _dc, _old;
    Bitmap? _bmp;
    Size _size;

    public Graphics Begin(Size size)
    {
        if (_bmp is null || size != _size) Recreate(size);
        return Graphics.FromImage(_bmp!);
    }

    public void Present(IntPtr hdc, Rectangle r)
    {
        if (_dc == IntPtr.Zero) return;
        r.Intersect(new Rectangle(Point.Empty, _size));
        if (r.IsEmpty) return;
        BitBlt(hdc, r.X, r.Y, r.Width, r.Height, _dc, r.X, r.Y, 0x00CC0020 /*SRCCOPY*/);
    }

    void Recreate(Size size)
    {
        Free();
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size.Width, biHeight = -size.Height, // top-down
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        _dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection");
        _dc = CreateCompatibleDC(IntPtr.Zero);
        _old = SelectObject(_dc, _dib);
        _bmp = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, _bits);
        _size = size;
    }

    void Free()
    {
        _bmp?.Dispose(); _bmp = null;
        if (_dc != IntPtr.Zero) { SelectObject(_dc, _old); DeleteDC(_dc); _dc = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _size = Size.Empty;
    }

    public void Dispose() => Free();

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}

/// <summary>Plain container that is see-through on glass (shows the layers of its ancestors).</summary>
internal class GlassPanel : Control
{
    readonly AlphaBuffer _buf = new();

    public GlassPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (GlassHost.Handle(this, ref m, _buf, g => OnPaint(new PaintEventArgs(g, ClientRectangle)))) return;
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _buf.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Text label (optionally a link) drawn on glass.</summary>
internal sealed class GlassLabel : GlassPanel
{
    public bool Wrap { get; init; }
    public bool Link { get; init; }
    public StringAlignment Align { get; init; } = StringAlignment.Near;
    bool _hot;

    public GlassLabel(string text, Font font, Color color, bool link = false)
    {
        Text = text; Font = font; ForeColor = color; Link = link;
        if (link) { Cursor = Cursors.Hand; TabStop = true; SetStyle(ControlStyles.Selectable, true); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (Link && e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var f = Link && _hot ? Fonts.Get(Font.FontFamily.Name, Font.Size, FontStyle.Underline) : Font;
        Painter.Text(e.Graphics, Text, f, ForeColor, new RectangleF(0, 0, Width, Height), Align,
            Wrap ? StringAlignment.Near : StringAlignment.Center, Wrap);
    }
}

/// <summary>Scrollable glass viewport with an iOS-style overlay scroll indicator (no opaque native scrollbar).</summary>
internal sealed class GlassScroller : GlassPanel
{
    public GlassPanel Content { get; } = new();
    readonly Indicator? _bar; // null while the base constructor adds Content (Controls.Add runs a layout pass)
    int _offset;

    public GlassScroller()
    {
        Controls.Add(Content);
        _bar = new Indicator(this);
        Controls.Add(_bar);
        _bar.BringToFront();
    }

    public int Offset => _offset;
    int Max => Math.Max(0, Content.Height - ClientSize.Height);

    public void SetContentHeight(int h)
    {
        Content.SetBounds(0, -_offset, ClientSize.Width, h);
        ScrollTo(_offset);
    }

    /// <summary>Jumps to <paramref name="y"/> (drag, keyboard focus, layout).</summary>
    public void ScrollTo(int y)
    {
        _anim?.Cancel();
        _anim = null;
        _target = Math.Clamp(y, 0, Max);
        Apply(_target);
        Settle();
    }

    /// <summary>Final full repaint of the viewport once motion stops — guarantees a perfect picture.</summary>
    void Settle()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var all = new Native.RECT { Left = 0, Top = 0, Right = ClientSize.Width, Bottom = ClientSize.Height };
        RedrawWindow(Handle, ref all, IntPtr.Zero, 0x1 | 0x80 | 0x100);
    }

    /// <summary>Smooth wheel scrolling: eases towards an accumulating target (~60 fps, 220 ms).</summary>
    public void ScrollBy(int dy)
    {
        int from = _offset;
        _target = Math.Clamp((_anim is null ? _offset : _target) + dy, 0, Max);
        int to = _target;
        _anim?.Cancel();
        if (to == from) { _anim = null; return; }
        _anim = Animator.Run(220, p => { if (!IsDisposed) Apply(from + (int)MathF.Round((to - from) * p)); }, () => { _anim = null; Settle(); });
    }

    int _target;
    Animator.Anim? _anim;

    const int WM_SETREDRAW = 0x000B;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, ref Native.RECT rect, IntPtr rgn, uint flags);

    /// <summary>
    /// Moves the content with redraw frozen, then repaints the viewport (all rows) synchronously in one go:
    /// every row appears at its new place in the same frame. Each row repaints only its visible part.
    /// </summary>
    void Apply(int v)
    {
        v = Math.Clamp(v, 0, Max);
        _offset = v;
        if (Content.Top != -v)
        {
            if (IsHandleCreated)
            {
                SendMessageW(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                Content.Top = -v;
                SendMessageW(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                var all = new Native.RECT { Left = 0, Top = 0, Right = ClientSize.Width, Bottom = ClientSize.Height };
                RedrawWindow(Handle, ref all, IntPtr.Zero, 0x1 /*INVALIDATE*/ | 0x80 /*ALLCHILDREN*/ | 0x100 /*UPDATENOW*/);
            }
            else Content.Top = -v;
        }
        if (_bar is null) return;
        _bar.Visible = Max > 0;
        _bar.Invalidate();
    }

    public void EnsureVisible(Control c)
    {
        int top = c.Top, bottom = c.Bottom, pad = (int)(12 * DeviceDpi / 96f);
        if (top - pad < _offset) ScrollBy(top - pad - _offset);
        else if (bottom + pad > _offset + ClientSize.Height) ScrollBy(bottom + pad - ClientSize.Height - _offset);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = (int)(12 * DeviceDpi / 96f);
        Content.Width = ClientSize.Width;
        _bar?.SetBounds(ClientSize.Width - w, 0, w, ClientSize.Height);
        if (_anim is null) ScrollTo(_offset);
    }

    sealed class Indicator : GlassPanel
    {
        readonly GlassScroller _host;
        bool _hot, _drag;
        int _dragY, _dragOffset;

        public Indicator(GlassScroller host) { _host = host; }

        RectangleF Thumb()
        {
            float vh = _host.ClientSize.Height, ch = Math.Max(1, _host.Content.Height);
            float h = Math.Max(28 * DeviceDpi / 96f, vh * vh / ch);
            float y = (vh - h) * (_host.Max == 0 ? 0 : _host._offset / (float)_host.Max);
            float w = (_hot || _drag ? 6 : 4) * DeviceDpi / 96f;
            return new RectangleF(Width - w - 3 * DeviceDpi / 96f, y + 2, w, h - 4);
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var t = Thumb();
            if (!t.Contains(t.X, e.Y)) _host.ScrollTo((int)((e.Y - t.Height / 2) / Math.Max(1, _host.ClientSize.Height - t.Height) * _host.Max));
            _drag = true; _dragY = e.Y; _dragOffset = _host._offset; Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_drag) return;
            var t = Thumb();
            float track = Math.Max(1, _host.ClientSize.Height - t.Height);
            _host.ScrollTo(_dragOffset + (int)((e.Y - _dragY) / track * _host.Max));
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; Capture = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_host.Max == 0) return;
            var t = Thumb();
            Painter.FillRound(e.Graphics, t, t.Width / 2, Theme.Alpha(Theme.Current.Text, _hot || _drag ? 130 : 80));
        }
    }
}
