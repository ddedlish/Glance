using System.Diagnostics;

namespace Glance;

/// <summary>Main panel opened from the tray: all accessories, per-bud levels, 24 h history, estimates.</summary>
internal sealed class FlyoutForm : CardForm
{
    const float W = 380, Header = 52, Footer = 56, Gap = 8, Side = 12, Banner = 44;

    readonly List<(Accessory? A, string? Section, RectangleF R)> _items = new();
    RectangleF _btBtn, _gearBtn, _bannerRect;
    int _hot = -1;
    string _hotBtn = "";
    float _scroll, _contentH, _viewH;
    bool _closing;

    public string? PendingUpdate { get; set; }
    public event Action? SettingsRequested, UpdateRequested;
    public event Action<Accessory, Point>? DeviceMenuRequested;

    public FlyoutForm() : base(true) { }

    public void ShowNear(Rectangle? anchor)
    {
        UseScaleAt(anchor?.Location ?? Cursor.Position);
        Relayout();
        var p = PlaceNearTray(Size, anchor);
        Location = new Point(p.X, p.Y + Px(12));
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        int baseY = p.Y;
        Animator.Run(200, k => { if (!IsDisposed) Top = baseY + (int)(Px(12) * (1 - k)); });
    }

    public void Rebuild()
    {
        if (IsDisposed || _closing) return;
        int oldH = Height;
        Relayout();
        Top += oldH - Height; // keep the bottom edge anchored above the taskbar
        Invalidate();
    }

    List<(Accessory? A, string? Section)> Entries()
    {
        var list = new List<(Accessory?, string?)>();
        var connected = Svc.R.ConnectedDevices
            .OrderByDescending(a => a.Level.HasValue || a.HasParts)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var a in connected) list.Add((a, null));
        var recent = Svc.R.All
            .Where(a => !a.Connected && a.LastLevel.HasValue && (DateTime.Now - a.LastSeen).TotalDays < 30)
            .OrderByDescending(a => a.LastSeen).Take(6).ToList();
        if (recent.Count > 0)
        {
            list.Add((null, "Недавно"));
            foreach (var a in recent) list.Add((a, null));
        }
        return list;
    }

    protected override void Relayout()
    {
        _items.Clear();
        float y = Header;
        var entries = Entries();
        foreach (var (a, section) in entries)
        {
            float h = section is not null ? 26 : DeviceCard.Height(a!, true);
            _items.Add((a, section, new RectangleF(Side, y, W - 2 * Side, h)));
            y += h + Gap;
        }
        if (entries.Count == 0) y += 150;
        _contentH = y - Header;
        float extra = Footer + (PendingUpdate is not null ? Banner : 0);
        var wa = Screen.FromPoint(Location.IsEmpty ? Cursor.Position : Location).WorkingArea;
        float maxView = wa.Height / Sc * 0.85f - Header - extra;
        _viewH = Math.Min(_contentH, Math.Max(120, maxView));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - _viewH));
        Size = new Size(Px(W), Px(Header + _viewH + extra));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc;
        var t = T;

        // header
        Painter.Text(g, "Аксессуары", Fonts.Strong(16 * s), t.Text, new RectangleF(16 * s, 14 * s, 200 * s, 26 * s));
        int count = Svc.R.ConnectedDevices.Count();
        Painter.Text(g, count == 0 ? "" : $"подключено: {count}", Fonts.Regular(12 * s), t.SubText,
            new RectangleF(W * s - 176 * s, 16 * s, 160 * s, 22 * s), StringAlignment.Far);

        // list (clipped)
        var view = new RectangleF(0, Header * s, W * s, _viewH * s);
        var state = g.Save();
        g.SetClip(view);
        if (_items.Count == 0)
        {
            var c = new RectangleF(0, Header * s + 10 * s, W * s, 140 * s);
            Painter.Glyph(g, Glyphs.Bluetooth, 30 * s, t.SubText, new RectangleF(c.X, c.Y + 10 * s, c.Width, 40 * s));
            Painter.Text(g, "Нет подключённых аксессуаров", Fonts.Strong(14 * s), t.Text, new RectangleF(c.X, c.Y + 60 * s, c.Width, 22 * s), StringAlignment.Center);
            Painter.Text(g, "Подключите наушники, мышь или геймпад по Bluetooth", Fonts.Regular(12 * s), t.SubText, new RectangleF(c.X + 20 * s, c.Y + 84 * s, c.Width - 40 * s, 36 * s), StringAlignment.Center, StringAlignment.Near, wrap: true);
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var (a, section, r0) = _items[i];
            var r = Scaled(r0);
            if (r.Bottom < view.Top || r.Top > view.Bottom) continue;
            if (section is not null)
                Painter.Text(g, section, Fonts.Strong(12 * s), t.SubText, new RectangleF(r.X + 4 * s, r.Y + 4 * s, r.Width, r.Height - 4 * s));
            else
            {
                DeviceCard.Draw(g, r, a!, s, i == _hot, true);
                if (i == _hot)
                {
                    var dots = MoreRect(r);
                    if (_hotMore) Painter.FillRound(g, dots, dots.Height / 2, t.SurfaceHover);
                    Painter.Glyph(g, Glyphs.More, 18 * s, t.Text, dots);
                }
            }
        }
        g.Restore(state);

        // scroll hint
        if (_contentH > _viewH)
        {
            float trackH = _viewH * s, thumbH = Math.Max(24 * s, trackH * _viewH / _contentH);
            float thumbY = Header * s + (trackH - thumbH) * (_scroll / Math.Max(1, _contentH - _viewH));
            Painter.FillRound(g, new RectangleF(W * s - 5 * s, thumbY, 3 * s, thumbH), 1.5f * s, Theme.Alpha(t.SubText, 120));
        }

        float y = (Header + _viewH) * s;

        // update banner
        if (PendingUpdate is not null)
        {
            _bannerRect = new RectangleF(Side * s, y + 4 * s, (W - 2 * Side) * s, (Banner - 8) * s);
            var fill = _hotBtn == "banner" ? Theme.Mix(t.AccentFill, Color.White, 0.12f) : t.AccentFill;
            Painter.FillRound(g, _bannerRect, 10 * s, fill);
            Painter.GlassSheen(g, _bannerRect, 10 * s, t.Dark);
            Painter.Glyph(g, Glyphs.Download, 14 * s, t.OnAccent, new RectangleF(_bannerRect.X + 8 * s, _bannerRect.Y, 24 * s, _bannerRect.Height));
            Painter.Text(g, $"Доступна версия {PendingUpdate}", Fonts.Strong(13 * s), t.OnAccent,
                new RectangleF(_bannerRect.X + 38 * s, _bannerRect.Y, _bannerRect.Width - 140 * s, _bannerRect.Height));
            Painter.Text(g, "Обновить ›", Fonts.Strong(13 * s), t.OnAccent,
                new RectangleF(_bannerRect.Right - 104 * s, _bannerRect.Y, 94 * s, _bannerRect.Height), StringAlignment.Far);
            y += Banner * s;
        }
        else _bannerRect = RectangleF.Empty;

        // footer
        using (var pen = new Pen(t.Border, Math.Max(1, s))) g.DrawLine(pen, 0, y, W * s, y);
        var btFont = Fonts.Regular(14.5f * s);
        float btW = 40 * s + Painter.Width(g, "Bluetooth", btFont) + 14 * s;
        _btBtn = new RectangleF(Side * s, y + 10 * s, btW, 36 * s);
        _gearBtn = new RectangleF((W - Side - 38) * s, y + 9 * s, 38 * s, 38 * s);
        if (_hotBtn == "bt") Painter.FillRound(g, _btBtn, 18 * s, t.SurfaceHover);
        if (_hotBtn == "gear") Painter.FillRound(g, RectangleF.Inflate(_gearBtn, -2 * s, -2 * s), 17 * s, t.SurfaceHover); // round, snug
        Painter.Glyph(g, Glyphs.Bluetooth, 16 * s, t.Text, new RectangleF(_btBtn.X + 8 * s, _btBtn.Y, 28 * s, _btBtn.Height));
        Painter.Text(g, "Bluetooth", btFont, t.Text, new RectangleF(_btBtn.X + 40 * s, _btBtn.Y, _btBtn.Width - 42 * s, _btBtn.Height));
        Painter.Glyph(g, Glyphs.Settings, 20 * s, t.Text, _gearBtn);
    }

    /// <summary>The «⋯» button in the bottom-right corner of a card.</summary>
    RectangleF MoreRect(RectangleF card) => new(card.Right - 40 * Sc, card.Bottom - 30 * Sc, 32 * Sc, 26 * Sc);
    bool _hotMore;

    RectangleF Scaled(RectangleF r) => new(r.X * Sc, (r.Y - _scroll) * Sc, r.Width * Sc, r.Height * Sc);

    int HitCard(Point p)
    {
        if (p.Y < Header * Sc || p.Y > (Header + _viewH) * Sc) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].A is not null && Scaled(_items[i].R).Contains(p)) return i;
        return -1;
    }

    string HitButton(Point p) =>
        _btBtn.Contains(p) ? "bt" : _gearBtn.Contains(p) ? "gear" : (!_bannerRect.IsEmpty && _bannerRect.Contains(p)) ? "banner" : "";

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = HitCard(e.Location);
        string btn = HitButton(e.Location);
        bool more = hot >= 0 && MoreRect(Scaled(_items[hot].R)).Contains(e.Location);
        if (hot != _hot || btn != _hotBtn || more != _hotMore) { _hot = hot; _hotBtn = btn; _hotMore = more; Invalidate(); }
        Cursor = hot >= 0 || btn.Length > 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != -1 || _hotBtn.Length > 0) { _hot = -1; _hotBtn = ""; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        switch (HitButton(e.Location))
        {
            case "bt":
                OpenBluetoothSettings();
                CloseAnimated();
                return;
            case "gear":
                SettingsRequested?.Invoke();
                CloseAnimated();
                return;
            case "banner":
                UpdateRequested?.Invoke();
                CloseAnimated();
                return;
        }
        int i = HitCard(e.Location);
        if (i >= 0 && _items[i].A is Accessory a)
        {
            // The menu drops down from the «⋯» button.
            var dots = MoreRect(Scaled(_items[i].R));
            DeviceMenuRequested?.Invoke(a, PointToScreen(new Point((int)dots.Right, (int)dots.Bottom + Px(2))));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_contentH <= _viewH) return;
        _scroll = Math.Clamp(_scroll - e.Delta / 120f * 48, 0, _contentH - _viewH);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) CloseAnimated();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!MenuOpen) CloseAnimated();
    }

    public bool MenuOpen { get; set; }

    public void CloseAnimated()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        int y0 = Top;
        Animator.Run(120, k => { if (!IsDisposed) Top = y0 + (int)(Px(10) * k); }, () => { if (!IsDisposed) Close(); });
    }

    public static void OpenBluetoothSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception e) { Log.Error("Open BT settings", e); }
    }
}

/// <summary>Rich hover card over a tray icon (replaces the plain one-line tooltip).</summary>
internal sealed class HoverCard : CardForm
{
    readonly List<Accessory> _devices;
    readonly Rectangle? _anchor;
    readonly System.Windows.Forms.Timer _guard = new() { Interval = 250 };
    readonly long _born = Environment.TickCount64;

    public HoverCard(List<Accessory> devices, Rectangle? anchor) : base(false)
    {
        _devices = devices;
        _anchor = anchor;
        _guard.Tick += (_, _) => Guard();
    }

    public void ShowNear()
    {
        UseScaleAt(_anchor?.Location ?? Cursor.Position);
        Relayout();
        Location = PlaceNearTray(Size, _anchor);
        Show();
        _guard.Start();
    }

    // Safety net: hide if the shell never sends NIN_POPUPCLOSE.
    void Guard()
    {
        if (IsDisposed) return;
        var cursor = Cursor.Position;
        bool overAnchor = _anchor is Rectangle a && Rectangle.Inflate(a, Px(4), Px(4)).Contains(cursor);
        if (!overAnchor || Environment.TickCount64 - _born > 15_000) Close();
    }

    protected override void Relayout()
    {
        float h = 12;
        if (_devices.Count == 0) h += 40;
        foreach (var a in _devices) h += DeviceCard.Height(a, false) + 6;
        h += 6;
        Size = new Size(Px(330), Px(h));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc, y = 12 * s;
        if (_devices.Count == 0)
        {
            Painter.Text(g, "Glance — нет подключённых аксессуаров", Fonts.Regular(12.5f * s), T.SubText, new RectangleF(14 * s, y, Width - 28 * s, 30 * s));
            return;
        }
        foreach (var a in _devices)
        {
            float h = DeviceCard.Height(a, false) * s;
            DeviceCard.Draw(g, new RectangleF(10 * s, y, Width - 20 * s, h), a, s, false, false);
            y += h + 6 * s;
        }
    }

    public void RefreshDevice(Accessory a)
    {
        if (IsDisposed || !_devices.Contains(a)) return;
        var oldH = Height;
        Relayout();
        Top -= Height - oldH;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _guard.Dispose();
        base.Dispose(disposing);
    }
}
