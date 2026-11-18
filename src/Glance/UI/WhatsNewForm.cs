namespace Glance;

/// <summary>
/// «Что нового»: every Glance version with its changes, newest first, in a glass window with smooth scrolling.
/// Opens from the update notification, from Settings → Обновления and from the tray menu.
/// </summary>
internal sealed class WhatsNewForm : CardForm
{
    const float W = 540, H = 580, Head = 70, Side = 22;
    readonly IReadOnlyList<Changelog.Entry> _entries;
    float _scroll, _target, _contentH;
    Animator.Anim? _anim;
    bool _hotClose;
    RectangleF _close;
    static WhatsNewForm? _open;

    WhatsNewForm() : base(true)
    {
        _entries = Changelog.Parse(Changelog.Bundled() ?? "").Take(30).ToList();
        TopMost = false;
        ShowInTaskbar = true;
        Text = "Glance — что нового";
        KeyPreview = true;
    }

    public static void ShowWindow()
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        var f = new WhatsNewForm();
        _open = f;
        f.FormClosed += (_, _) => _open = null;
        f.UseScaleAt(Cursor.Position);
        f.Relayout();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + (wa.Height - f.Height) / 2);
        f.Show();
        f.Activate();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle &= ~0x80; return cp; } // normal window (Alt+Tab)
    }

    protected override void Relayout() => Size = new Size(Px(W), Px(H));

    static bool IsCurrent(string v) => System.Version.TryParse(v.Split(' ')[0], out var x) && x.ToString(3) == AppInfo.Version.ToString(3);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc;
        var t = T;
        Painter.Text(g, "Что нового", Fonts.Strong(18 * s), t.Text, new RectangleF(Side * s, 16 * s, 300 * s, 28 * s));
        Painter.Text(g, $"Установлена версия {AppInfo.VersionText}", Fonts.Regular(12 * s), t.SubText, new RectangleF(Side * s, 42 * s, 360 * s, 20 * s));
        _close = new RectangleF((W - 50) * s, 12 * s, 36 * s, 36 * s);
        if (_hotClose) Painter.FillRound(g, _close, 18 * s, t.SurfaceHover);
        Painter.Glyph(g, Glyphs.Close, 11 * s, t.SubText, _close);

        var view = new RectangleF(0, Head * s, Width, Height - Head * s);
        var st = g.Save();
        g.SetClip(view);
        float y = Head * s - _scroll + 4 * s;
        float textW = (W - Side * 2 - 28) * s;
        foreach (var en in _entries)
        {
            // measure the card
            float h = 44 * s;
            var heights = new List<float>();
            foreach (var l in en.Lines)
            {
                float lw = textW - l.Level * 16 * s;
                float lh = TextRenderer.MeasureText(l.Text, Fonts.Regular(13 * s), new Size((int)lw, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                heights.Add(lh);
                h += lh + 6 * s;
            }
            h += 8 * s;
            var card = new RectangleF(Side * s, y, (W - Side * 2) * s, h);
            if (card.Bottom > view.Top && card.Top < view.Bottom)
            {
                bool cur = IsCurrent(en.Version);
                Painter.FillRound(g, card, 14 * s, t.Surface);
                Painter.GlassSheen(g, card, 14 * s, t.Dark);
                if (cur) Painter.StrokeRound(g, card, 14 * s, t.AccentFill, 1.5f * s);
                Painter.Text(g, en.Version, Fonts.Strong(15 * s), t.Text, new RectangleF(card.X + 16 * s, card.Y + 12 * s, 160 * s, 22 * s));
                if (cur)
                {
                    float vw = Painter.Width(g, en.Version, Fonts.Strong(15 * s));
                    var chip = new RectangleF(card.X + 24 * s + vw, card.Y + 14 * s, 78 * s, 18 * s);
                    Painter.FillRound(g, chip, 9 * s, t.AccentFill);
                    Painter.Text(g, "у вас сейчас", Fonts.Regular(10.5f * s), t.OnAccent, chip, StringAlignment.Center);
                }
                float ly = card.Y + 42 * s;
                for (int i = 0; i < en.Lines.Count; i++)
                {
                    var l = en.Lines[i];
                    float lx = card.X + 16 * s + l.Level * 16 * s;
                    using (var dot = new SolidBrush(l.Level == 0 ? t.AccentText : t.SubText))
                        g.FillEllipse(dot, lx, ly + 7 * s, 5 * s, 5 * s);
                    Painter.Text(g, l.Text, Fonts.Regular(13 * s), l.Level == 0 ? t.Text : t.SubText,
                        new RectangleF(lx + 12 * s, ly, textW - l.Level * 16 * s, heights[i] + 2 * s), StringAlignment.Near, StringAlignment.Near, wrap: true);
                    ly += heights[i] + 6 * s;
                }
            }
            y += h + 10 * s;
        }
        if (_entries.Count == 0)
            Painter.Text(g, "Список изменений недоступен.", Fonts.Regular(13 * s), t.SubText, new RectangleF(Side * s, view.Top + 10 * s, 300 * s, 24 * s));
        _contentH = y + _scroll - Head * s + 12 * s;
        g.Restore(st);

        // fade the top edge of the list under the header
        using var fade = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, view.Top - 1, Width, 14 * s + 2), Theme.Alpha(t.Glass ? t.GlassTint : t.Bg, _scroll > 1 ? 200 : 0), Color.Transparent, 90f);
        g.FillRectangle(fade, 0, view.Top, Width, 14 * s);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float max = Math.Max(0, _contentH - (Height - Head * Sc));
        float from = _scroll;
        _target = Math.Clamp((_anim is null ? _scroll : _target) - e.Delta / 120f * 70 * Sc, 0, max);
        float to = _target;
        _anim?.Cancel();
        _anim = Animator.Run(220, p => { if (IsDisposed) return; _scroll = from + (to - from) * p; Invalidate(); }, () => _anim = null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hot = _close.Contains(e.Location);
        if (hot != _hotClose) { _hotClose = hot; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_close.Contains(e.Location)) Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x84 && (int)m.Result == 1) // drag by the header
        {
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            if (p.Y < Head * Sc && !_close.Contains(p)) m.Result = (IntPtr)2;
        }
    }
}
