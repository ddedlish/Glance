namespace Glance;

/// <summary>
/// Glance Pro desktop widget: a small glass panel with every connected accessory and its level, always on top,
/// never steals focus. Drag it anywhere (position is remembered); double-click opens the full panel.
/// Hides itself while a fullscreen game or presentation is running.
/// </summary>
internal sealed class WidgetForm : CardForm
{
    const float W = 236, Row = 38, Pad = 10;
    readonly AppSettings _s;
    readonly System.Windows.Forms.Timer _busy = new() { Interval = 3000 };
    public event Action? OpenPanel;

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
        var pt = _s.WidgetX == int.MinValue ? new Point(wa.Right - 260, wa.Top + 40) : new Point(_s.WidgetX, _s.WidgetY);
        UseScaleAt(pt);
        Relayout();
        // keep it on a visible screen
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
        .Where(a => !_s.For(a.Key).HideInTray)
        .OrderByDescending(a => a.Level.HasValue || a.HasParts)
        .ThenBy(a => a.WeakestLevel ?? 101)
        .Take(8).ToList();

    protected override void Relayout()
    {
        int n = Math.Max(1, Devices().Count);
        Size = new Size(Px(W), Px(Pad * 2 + 22 + n * Row));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc;
        var t = T;
        Painter.Text(g, "Glance", Fonts.Strong(12 * s), t.SubText, new RectangleF(Pad * s + 2 * s, Pad * s - 2 * s, 120 * s, 20 * s));
        Painter.Text(g, "PRO", Fonts.Strong(10 * s), t.AccentText, new RectangleF((W - Pad - 40) * s, Pad * s - 2 * s, 40 * s, 20 * s), StringAlignment.Far);
        float y = (Pad + 22) * s;
        var list = Devices();
        if (list.Count == 0)
        {
            Painter.Text(g, "Нет подключённых устройств", Fonts.Regular(12.5f * s), t.SubText, new RectangleF(Pad * s, y, (W - 2 * Pad) * s, Row * s));
            return;
        }
        foreach (var a in list)
        {
            var row = new RectangleF(Pad * s, y, (W - 2 * Pad) * s, (Row - 4) * s);
            Painter.FillRound(g, row, 10 * s, t.Surface);
            Painter.GlassSheen(g, row, 10 * s, t.Dark);
            Painter.Glyph(g, Glyphs.For(a.Kind), 14 * s, a.Profile.BrandColor ?? t.AccentText, new RectangleF(row.X + 6 * s, row.Y, 24 * s, row.Height));
            int? lvl = a.WeakestLevel;
            float pctW = 48 * s;
            Painter.Text(g, a.DisplayName, Fonts.Regular(12.5f * s), t.Text, new RectangleF(row.X + 32 * s, row.Y + 2 * s, row.Width - 32 * s - pctW - 6 * s, row.Height - 10 * s));
            string pct = lvl is int l ? $"{(int)MathF.Round(LevelTween.Value("W:" + a.Key, l))}%" : a.Link;
            Painter.Text(g, pct, lvl is null ? Fonts.Regular(11 * s) : Fonts.Strong(13 * s), lvl is null ? t.SubText : t.LevelColor(lvl, a.Charging, Svc.Low),
                new RectangleF(row.Right - pctW - 8 * s, row.Y + 2 * s, pctW, row.Height - 10 * s), StringAlignment.Far);
            if (lvl is int lv)
                Painter.Bar(g, new RectangleF(row.X + 32 * s, row.Bottom - 8 * s, row.Width - 40 * s, 3 * s), lv / 100f, t.BarColor(lvl, a.Charging, Svc.Low), Theme.Alpha(t.Track, 140));
            if (a.Charging)
                Painter.Glyph(g, Glyphs.Bolt, 10 * s, t.Good, new RectangleF(row.Right - pctW - 22 * s, row.Y + 2 * s, 14 * s, row.Height - 10 * s));
            y += Row * s;
        }
        if (LevelTween.Animating) Ui.Delay(33, () => { if (!IsDisposed) Invalidate(); });
    }

    const int WM_NCHITTEST = 0x84, HTCAPTION = 2, WM_NCLBUTTONDBLCLK = 0xA3, WM_EXITSIZEMOVE = 0x232;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTCAPTION; return; } // drag from anywhere
        if (m.Msg == WM_NCLBUTTONDBLCLK) { OpenPanel?.Invoke(); return; }
        if (m.Msg == WM_EXITSIZEMOVE) { _s.WidgetX = Left; _s.WidgetY = Top; _s.Save(); }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _busy.Dispose();
        base.Dispose(disposing);
    }
}
