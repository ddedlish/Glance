namespace Glance;

/// <summary>
/// Glance Pro desktop widget: a small glass panel with every connected accessory and its level, always on top,
/// never steals focus. Drag it anywhere (position is remembered); double-click opens the full panel.
/// Hides itself while a fullscreen game or presentation is running.
/// </summary>
internal sealed class WidgetForm : CardForm
{
    const float Tile = 74, Pad = 10, Head = 26;
    readonly AppSettings _s;
    readonly System.Windows.Forms.Timer _busy = new() { Interval = 3000 };
    public event Action? OpenPanel;
    bool _hover, _hotClose;
    RectangleF _close;

    public WidgetForm(AppSettings s) : base(false)
    {
        _s = s;
        _busy.Tick += (_, _) =>
        {
            bool hide = Native.UserIsBusy();
            if (hide && Visible) Hide();
            else if (!hide && !Visible && !IsDisposed) Show();
        };
    }

    public void ShowWidget()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var pt = _s.WidgetX == int.MinValue ? new Point(wa.Right - 340, wa.Top + 40) : new Point(_s.WidgetX, _s.WidgetY);
        UseScaleAt(pt);
        Relayout();
        var scr = Screen.FromPoint(pt).WorkingArea;
        pt.X = Math.Clamp(pt.X, scr.Left, Math.Max(scr.Left, scr.Right - Width));
        pt.Y = Math.Clamp(pt.Y, scr.Top, Math.Max(scr.Top, scr.Bottom - Height));
        Location = pt;
        Show();
        _busy.Start();
    }

    public void Refresh(bool relayout)
    {
        if (IsDisposed) return;
        if (relayout) Relayout();
        Invalidate();
    }

    List<Accessory> Devices() => Svc.R.ConnectedDevices
        .Where(a => !_s.For(a.Key).HideInTray && (a.WeakestLevel is not null))
        .OrderBy(a => a.WeakestLevel ?? 101)
        .Take(6).ToList();

    protected override void Relayout()
    {
        int n = Math.Max(1, Devices().Count);
        // a horizontal strip of ring gauges — compact, glanceable
        Size = new Size(Px(Pad * 2 + n * Tile + (n == 1 && Devices().Count == 0 ? 100 : 0)), Px(Head + Pad + Tile + 18));
    }

    /// <summary>Hide the widget (× on hover, or right-click → «Скрыть виджет»).</summary>
    void HideWidget()
    {
        _s.Widget = false;
        _s.Save();
        _s.NotifyChanged(); // the host closes the widget
        if (!IsDisposed) Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc;
        var t = T;
        Painter.Text(g, "Glance", Fonts.Strong(11.5f * s), t.SubText, new RectangleF(Pad * s + 2 * s, 6 * s, 120 * s, 18 * s));
        _close = new RectangleF(Width - 28 * s, 5 * s, 22 * s, 22 * s);
        if (_hover)
        {
            if (_hotClose) Painter.FillRound(g, _close, 11 * s, t.SurfaceHover);
            Painter.Glyph(g, Glyphs.Close, 9 * s, _hotClose ? t.Text : t.SubText, _close);
        }
        var list = Devices();
        if (list.Count == 0)
        {
            Painter.Text(g, "Нет устройств с зарядом", Fonts.Regular(12 * s), t.SubText, new RectangleF(Pad * s, Head * s, Width - 2 * Pad * s, Tile * s), StringAlignment.Center);
            return;
        }
        float x = Pad * s;
        foreach (var a in list)
        {
            var tile = new RectangleF(x + 3 * s, Head * s, (Tile - 6) * s, (Tile + 12) * s);
            Painter.FillRound(g, tile, 16 * s, t.Surface);
            Painter.GlassSheen(g, tile, 16 * s, t.Dark);
            int lvl = a.WeakestLevel ?? 0;
            float shown = LevelTween.Value("W:" + a.Key, lvl);
            var color = t.BarColor(lvl, a.Charging, Svc.Low);
            var ring = new RectangleF(tile.X + (tile.Width - 46 * s) / 2, tile.Y + 8 * s, 46 * s, 46 * s);
            float th = 4.5f * s;
            using (var tr = new Pen(Theme.Alpha(t.Track, 170), th)) g.DrawEllipse(tr, ring);
            using (var arc = new Pen(color, th) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                if (shown > 0.5f) g.DrawArc(arc, ring, -90, 360f * shown / 100f);
            Painter.GlyphPath(g, Glyphs.For(a.Kind), RectangleF.Inflate(ring, -11 * s, -11 * s), DeviceCard.KindColor(a.Kind), 1f, 0.4f * s);
            if (a.Charging)
                Painter.Glyph(g, Glyphs.Bolt, 9 * s, t.Good, new RectangleF(ring.Right - 12 * s, ring.Y - 2 * s, 14 * s, 14 * s));
            Painter.Text(g, $"{(int)MathF.Round(shown)}%", Fonts.Strong(12.5f * s), t.LevelColor(lvl, a.Charging, Svc.Low), new RectangleF(tile.X, ring.Bottom + 3 * s, tile.Width, 18 * s), StringAlignment.Center);
            x += Tile * s;
        }
        if (LevelTween.Animating) Ui.Delay(33, () => { if (!IsDisposed) Invalidate(); });
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // leaving to the non-client (drag) area still counts as inside
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) { _hover = false; _hotClose = false; Invalidate(); }
    }

    const int WM_NCHITTEST = 0x84, HTCAPTION = 2, WM_NCLBUTTONDBLCLK = 0xA3, WM_EXITSIZEMOVE = 0x232;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            bool hotClose = _close.Contains(p);
            if (!_hover || hotClose != _hotClose) { _hover = true; _hotClose = hotClose; Invalidate(); TrackLeave(); }
            m.Result = hotClose ? (IntPtr)1 /*HTCLIENT: the × is clickable*/ : (IntPtr)HTCAPTION; // drag from anywhere else
            return;
        }
        if (m.Msg == 0x2A2 /*WM_NCMOUSELEAVE*/ || m.Msg == 0x2A3 /*WM_MOUSELEAVE*/)
        {
            if (!Bounds.Contains(Cursor.Position)) { _hover = false; _hotClose = false; Invalidate(); }
        }
        if (m.Msg == 0xA5 /*WM_NCRBUTTONUP*/)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть панель", null, (_, _) => OpenPanel?.Invoke());
            menu.Items.Add("Скрыть виджет", null, (_, _) => Ui.Post(HideWidget));
            menu.Closed += (_, _) => Ui.Post(menu.Dispose);
            menu.Show(Cursor.Position);
            return;
        }
        if (m.Msg == WM_NCLBUTTONDBLCLK) { OpenPanel?.Invoke(); return; }
        if (m.Msg == WM_EXITSIZEMOVE) { _s.WidgetX = Left; _s.WidgetY = Top; _s.Save(); }
        base.WndProc(ref m);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _close.Contains(e.Location)) Ui.Post(HideWidget);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);

    void TrackLeave()
    {
        var tme = new TRACKMOUSEEVENT { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 | 0x10 /*LEAVE|NONCLIENT*/, hwndTrack = Handle };
        TrackMouseEvent(ref tme);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _busy.Dispose();
        base.Dispose(disposing);
    }
}
