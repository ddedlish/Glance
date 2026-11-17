namespace Glance;

/// <summary>
/// Glance Pro: battery history for 24 h / 7 / 30 days — a glass chart with charging periods highlighted,
/// average drain, time per charge, discharge cycles, a battery-wear estimate and CSV export.
/// </summary>
internal sealed class HistoryForm : CardForm
{
    const float W = 600, H = 470;
    readonly List<Accessory> _devices;
    int _index, _period = 1; // 0 = 24 h, 1 = 7 days, 2 = 30 days
    string _hot = "";
    readonly Dictionary<string, RectangleF> _btn = new();
    float _reveal = 1;

    public HistoryForm(Accessory? start) : base(true)
    {
        _devices = Svc.R.All.Where(a => Svc.H.Get(a.Key).Count > 1)
            .OrderByDescending(a => a.Connected).ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (start is not null && !_devices.Contains(start) && Svc.H.Get(start.Key).Count > 0) _devices.Insert(0, start);
        _index = start is null ? 0 : Math.Max(0, _devices.IndexOf(start));
        TopMost = false;
        ShowInTaskbar = true;
        Text = "Glance — история заряда";
    }

    public void ShowCentered()
    {
        UseScaleAt(Cursor.Position);
        Relayout();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        Show();
        Activate();
        Animate();
    }

    void Animate()
    {
        _reveal = 0;
        Animator.Run(650, p => { if (IsDisposed) return; _reveal = p; Invalidate(); });
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle &= ~0x80 /*no TOOLWINDOW: a normal window in Alt+Tab*/; return cp; }
    }

    protected override void Relayout() => Size = new Size(Px(W), Px(H));

    Accessory? Current => _devices.Count == 0 ? null : _devices[Math.Clamp(_index, 0, _devices.Count - 1)];

    long Span => _period switch { 0 => 86400, 1 => 7 * 86400, _ => 30 * 86400 };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc;
        var t = T;
        _btn.Clear();
        Painter.Text(g, "История заряда", Fonts.Strong(17 * s), t.Text, new RectangleF(20 * s, 14 * s, 300 * s, 28 * s));
        Painter.Text(g, "PRO", Fonts.Strong(10 * s), t.AccentText, new RectangleF(190 * s, 20 * s, 40 * s, 18 * s));
        Button(g, "close", new RectangleF((W - 48) * s, 10 * s, 36 * s, 36 * s), Glyphs.Close.ToString(), glyph: true);

        var a = Current;
        if (a is null)
        {
            Painter.Text(g, "Пока нет данных. Glance начнёт записывать историю, как только устройство сообщит заряд.", Fonts.Regular(13 * s), t.SubText,
                new RectangleF(20 * s, 80 * s, (W - 40) * s, 60 * s), StringAlignment.Near, StringAlignment.Near, wrap: true);
            return;
        }

        // device switcher
        if (_devices.Count > 1)
        {
            Button(g, "prev", new RectangleF(20 * s, 52 * s, 32 * s, 32 * s), "‹");
            Button(g, "next", new RectangleF(56 * s, 52 * s, 32 * s, 32 * s), "›");
        }
        float nx = _devices.Count > 1 ? 98 : 20;
        Painter.Glyph(g, Glyphs.For(a.Kind), 16 * s, a.Profile.BrandColor ?? t.AccentText, new RectangleF(nx * s, 52 * s, 24 * s, 32 * s));
        Painter.Text(g, a.DisplayName, Fonts.Strong(14 * s), t.Text, new RectangleF((nx + 28) * s, 52 * s, 240 * s, 32 * s));

        // period
        string[] periods = { "24 ч", "7 дней", "30 дней" };
        float px = (W - 20 - 3 * 76) * s;
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(px + i * 76 * s, 52 * s, 72 * s, 32 * s);
            _btn["p" + i] = r;
            Painter.FillRound(g, r, 16 * s, i == _period ? t.AccentFill : _hot == "p" + i ? t.SurfaceHover : t.Surface);
            Painter.Text(g, periods[i], Fonts.Regular(12.5f * s), i == _period ? t.OnAccent : t.Text, r, StringAlignment.Center);
        }

        // chart
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = now - Span;
        var all = Svc.H.Get(a.Key);
        var chart = new RectangleF(52 * s, 100 * s, (W - 72) * s, 200 * s);
        var card = RectangleF.Inflate(chart, 14 * s, 12 * s);
        card.X -= 18 * s; card.Width += 18 * s;
        Painter.FillRound(g, card, 14 * s, t.Surface);
        Painter.GlassSheen(g, card, 14 * s, t.Dark);
        using (var grid = new Pen(Theme.Alpha(t.Text, 28), Math.Max(1, s)))
            foreach (int lv in new[] { 0, 25, 50, 75, 100 })
            {
                float y = chart.Bottom - lv / 100f * chart.Height;
                g.DrawLine(grid, chart.X, y, chart.Right, y);
                Painter.Text(g, lv + "%", Fonts.Regular(10.5f * s), t.SubText, new RectangleF(chart.X - 44 * s, y - 8 * s, 38 * s, 16 * s), StringAlignment.Far);
            }
        DrawSeries(g, chart, all, from, now, s);
        // time labels
        for (int i = 0; i <= 4; i++)
        {
            long tt = from + Span * i / 4;
            var lt = DateTimeOffset.FromUnixTimeSeconds(tt).ToLocalTime();
            string lab = _period == 0 ? lt.ToString("HH:mm") : lt.ToString("dd.MM");
            float x = chart.X + chart.Width * i / 4;
            Painter.Text(g, lab, Fonts.Regular(10.5f * s), t.SubText, new RectangleF(x - 30 * s, chart.Bottom + 4 * s, 60 * s, 16 * s), StringAlignment.Center);
        }

        // stats
        var st = HistoryStats.Compute(all, from, now);
        float sy = 336 * s, colW = (W - 40) / 4f * s;
        void Stat(int col, string value, string label)
        {
            var r = new RectangleF(20 * s + col * colW, sy, colW - 8 * s, 64 * s);
            Painter.FillRound(g, r, 12 * s, t.Surface);
            Painter.Text(g, value, Fonts.Big(17 * s), t.Text, new RectangleF(r.X + 12 * s, r.Y + 8 * s, r.Width - 16 * s, 26 * s));
            Painter.Text(g, label, Fonts.Regular(11 * s), t.SubText, new RectangleF(r.X + 12 * s, r.Y + 36 * s, r.Width - 16 * s, 20 * s));
        }
        Stat(0, st.DrainPerHour is double d ? $"{d:0.#}%/ч" : "—", "средний расход");
        Stat(1, st.HoursPerCharge is double hpc ? Fmt.Duration(TimeSpan.FromHours(hpc)) : "—", "на одном заряде");
        Stat(2, $"{st.Cycles:0.#}", "циклов разряда");
        Stat(3, st.WearPercent is double w ? $"≈{w:0}%" : "—", st.WearPercent is null ? "износ: нужна неделя данных" : "износ (оценка)");

        Button(g, "csv", new RectangleF(20 * s, (H - 52) * s, 160 * s, 36 * s), "Экспорт в CSV");
    }

    void DrawSeries(Graphics g, RectangleF chart, IReadOnlyList<Sample> all, long from, long now, float s)
    {
        var t = T;
        var pts = new List<(PointF P, bool Charging)>();
        Sample? before = null;
        foreach (var x in all)
        {
            if (x.T < from) { before = x; continue; }
            if (pts.Count == 0 && before is Sample b) pts.Add((Map(chart, from, now, from, b.L), b.IsCharging));
            pts.Add((Map(chart, from, now, x.T, x.L), x.IsCharging));
        }
        if (pts.Count == 0 && before is Sample only) pts.Add((Map(chart, from, now, from, only.L), only.IsCharging));
        if (pts.Count == 0) return;
        pts.Add((new PointF(Math.Min(chart.Right, Map(chart, from, now, now, 0).X), pts[^1].P.Y), pts[^1].Charging));
        // reveal left→right
        float limit = chart.X + chart.Width * _reveal;
        var clip = g.Save();
        g.SetClip(new RectangleF(chart.X - 2, chart.Y - 4, limit - chart.X + 4, chart.Height + 8), System.Drawing.Drawing2D.CombineMode.Intersect);
        // charging periods
        using (var cb = new SolidBrush(Theme.Alpha(Theme.VividCyan, 34)))
            for (int i = 0; i + 1 < pts.Count; i++)
                if (pts[i].Charging) g.FillRectangle(cb, pts[i].P.X, chart.Y, Math.Max(1, pts[i + 1].P.X - pts[i].P.X), chart.Height);
        var line = pts.Select(p => p.P).ToArray();
        if (line.Length >= 2)
        {
            using (var fill = new System.Drawing.Drawing2D.GraphicsPath())
            {
                fill.AddLines(line);
                fill.AddLine(line[^1].X, line[^1].Y, line[^1].X, chart.Bottom);
                fill.AddLine(line[^1].X, chart.Bottom, line[0].X, chart.Bottom);
                fill.CloseFigure();
                using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(chart.X, chart.Y - 1, chart.Width, chart.Height + 2),
                    Theme.Alpha(t.Accent, 90), Theme.Alpha(t.Accent, 0), System.Drawing.Drawing2D.LinearGradientMode.Vertical);
                g.FillPath(br, fill);
            }
            using var pen = new Pen(t.Glass ? Theme.Mix(t.Accent, Color.White, 0.25f) : t.AccentFill, 2.2f * s) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(pen, line);
        }
        g.Restore(clip);
    }

    static PointF Map(RectangleF r, long from, long now, long t, int level) =>
        new(r.X + (t - from) / (float)Math.Max(1, now - from) * r.Width, r.Bottom - Math.Clamp(level, 0, 100) / 100f * r.Height);

    void Button(Graphics g, string id, RectangleF r, string text, bool glyph = false)
    {
        _btn[id] = r;
        Painter.FillRound(g, r, Math.Min(r.Height / 2, 12 * Sc), _hot == id ? T.SurfaceHover : T.Surface);
        if (glyph) Painter.Glyph(g, text[0], 11 * Sc, T.SubText, r);
        else Painter.Text(g, text, text.Length <= 1 ? Fonts.Strong(18 * Sc) : Fonts.Regular(13 * Sc), T.Text, r, StringAlignment.Center);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string hot = _btn.FirstOrDefault(b => b.Value.Contains(e.Location)).Key ?? "";
        if (hot != _hot) { _hot = hot; Invalidate(); Cursor = hot.Length > 0 ? Cursors.Hand : Cursors.Default; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        switch (_btn.FirstOrDefault(b => b.Value.Contains(e.Location)).Key)
        {
            case "close": Close(); break;
            case "prev": _index = (_index - 1 + _devices.Count) % _devices.Count; Animate(); break;
            case "next": _index = (_index + 1) % _devices.Count; Animate(); break;
            case "p0": _period = 0; Animate(); break;
            case "p1": _period = 1; Animate(); break;
            case "p2": _period = 2; Animate(); break;
            case "csv": Export(); break;
        }
    }

    void Export()
    {
        if (Current is not Accessory a) return;
        using var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"Glance — {a.DisplayName}.csv".Replace(':', ' ') };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(d.FileName, HistoryStats.Csv(a.DisplayName, Svc.H.Get(a.Key)), new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Glance"); }
    }

    // drag by the header, Esc closes
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x84 && (int)m.Result == 1)
        {
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            if (p.Y < 46 * Sc && !_btn.Any(b => b.Value.Contains(p))) m.Result = (IntPtr)2;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
}
