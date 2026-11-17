namespace Glance;

public enum ToastKind { Connected = 0, Charged = 1, Low = 2, Message = 3 }

/// <summary>
/// Connection / alert popup. Layout adapts to the model: earbuds with case → three rings
/// (left, right, case), split keyboards → two rings, everything else → big number + bar.
/// Non-activating, pauses on hover, slides in above the tray.
/// </summary>
internal sealed class ToastForm : CardForm
{
    public ToastKind Kind { get; private set; }
    public Accessory? Device { get; private set; }

    string _title = "", _text = "";
    char _glyph = Glyphs.Bluetooth;
    Action? _onClick;
    readonly System.Windows.Forms.Timer _life = new();
    float _grow = 1;
    Animator.Anim? _anim;
    bool _closing, _hover;
    RectangleF _closeRect, _actionRect, _laterRect;
    int _baseY;
    string? _action;      // sticky notification with buttons (new version available)
    string _hotBtn = "";

    public event Action<ToastForm>? Finished;

    public ToastForm() : base(false)
    {
        _life.Tick += (_, _) => { _life.Stop(); Dismiss(); };
        Cursor = Cursors.Hand;
    }

    public void ShowDevice(Accessory a, ToastKind kind, Action? onClick)
    {
        Device = a;
        Kind = kind;
        _onClick = onClick;
        Present(animateRings: true);
    }

    public void ShowMessage(string title, string text, char glyph, Action? onClick)
    {
        Device = null;
        Kind = ToastKind.Message;
        _title = title; _text = text; _glyph = glyph; _onClick = onClick;
        Present(animateRings: false);
    }

    /// <summary>Notification that stays until the user acts: «Обновить» / «Позже».</summary>
    public void ShowAction(string title, string text, char glyph, string action, Action onAction)
    {
        Device = null;
        Kind = ToastKind.Message;
        _title = title; _text = text; _glyph = glyph; _onClick = onAction; _action = action;
        Present(animateRings: false);
    }

    /// <summary>Same device got new data (e.g. per-bud levels arrived): re-layout in place.</summary>
    public void RefreshContent()
    {
        if (_closing || IsDisposed) return;
        var oldH = Height;
        Relayout();
        if (Height != oldH) { _baseY -= Height - oldH; Top = _baseY; }
        Invalidate();
    }

    public void Upgrade(ToastKind kind)
    {
        if (kind > Kind) Kind = kind;
        RestartLife();
        RefreshContent();
    }

    void Present(bool animateRings)
    {
        UseScaleAt(Cursor.Position);
        Relayout();
        var pos = PlaceNearTray(Size, null);
        _baseY = pos.Y;
        Location = new Point(pos.X, pos.Y + Px(14));
        if (!Visible) Show();
        _anim?.Cancel();
        if (animateRings) _grow = 0;
        _anim = Animator.Run(animateRings ? 650 : 220, p =>
        {
            if (IsDisposed) return;
            Top = _baseY + (int)(Px(14) * (1 - Math.Min(1, p * 2.2f)));
            if (animateRings) { _grow = p; Invalidate(); }
        });
        RestartLife();
    }

    void RestartLife()
    {
        _life.Stop();
        if (_action is not null) return; // sticky
        int secs = Math.Clamp(Svc.S.PopupSeconds, 2, 30);
        if (Kind == ToastKind.Low) secs += 3;
        _life.Interval = secs * 1000;
        if (!_hover) _life.Start();
    }

    public void Dismiss()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        _life.Stop();
        _anim?.Cancel();
        int startY = Top;
        Animator.Run(160, p => { if (!IsDisposed) Top = startY + (int)(Px(16) * p); }, () =>
        {
            Finished?.Invoke(this);
            if (!IsDisposed) Close();
        });
    }

    // ───────────── Layout ─────────────

    bool RingLayout(out int count)
    {
        count = 0;
        if (Device is not Accessory a) return false;
        if (a.HasParts) count = a.IsSplitDevice ? 2 : (a.Profile.HasCase || a.Case.Known ? 3 : 2);
        else if (a.Kind == DeviceKind.Earbuds) count = 1;
        return count > 0;
    }

    /// <summary>Connected without any battery data (USB keyboard, sleeping 2.4 GHz mouse): compact card, no «—» gauge.</summary>
    bool NoData() => Device is Accessory d && d.Level is null && !d.HasParts && d.Kind != DeviceKind.Earbuds;

    string? Footer()
    {
        if (Device is not Accessory a) return null;
        if (!a.Connected) return "Устройство отключено";
        if (Svc.Estimate(a) is TimeSpan e) return a.Charging ? $"До полного заряда примерно {Fmt.Duration(e)}" : $"Хватит примерно на {Fmt.Duration(e)}";
        if (a.BudsPending) return "Получаем заряд каждого наушника…";
        if (a.Wired) return "Подключено по USB-кабелю";
        if (a.ViaReceiver && a.Level is null) return "Через приёмник 2.4 ГГц · устройство не передаёт заряд";
        if (a.Profile.Buds != BudsSource.None && !a.HasParts && Svc.S.BudsDetail && a.Kind == DeviceKind.Earbuds)
            return "Модель не передаёт заряд каждого наушника — показан общий";
        if (a.PartsSource == "Apple") return "AirPods сообщают заряд с шагом 10%";
        return null;
    }

    protected override void Relayout()
    {
        float h = 16 + 40 + 16; // header
        if (Kind == ToastKind.Message)
        {
            using var g = MeasureGraphics(out var holder);
            using (holder)
            {
                var sz = g.MeasureString(_text, Fonts.Regular(13 * Sc), (int)(Px(360) - Px(32)));
                h = 16 + 22 + 4 + Math.Min(sz.Height / Sc, 90) + 16;
                if (_action is not null) h += 48;
            }
        }
        else
        {
            h += RingLayout(out _) ? 96 : NoData() ? 0 : 46;
            if (Footer() is not null) h += 26;
            h += 12;
        }
        Size = new Size(Px(360), Px(h));
    }

    // ───────────── Paint ─────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        float s = Sc, W = Width;
        var t = T;

        // close button (visible on hover)
        _closeRect = new RectangleF(W - 36 * s, 8 * s, 28 * s, 28 * s);
        if (_hover)
        {
            Painter.FillRound(g, _closeRect, 6 * s, t.SurfaceHover);
            Painter.Glyph(g, Glyphs.Close, 10 * s, t.SubText, _closeRect);
        }

        if (Kind == ToastKind.Message)
        {
            var gr = new RectangleF(16 * s, 16 * s, 22 * s, 22 * s);
            Painter.Glyph(g, _glyph, 16 * s, t.AccentText, gr);
            Painter.Text(g, _title, Fonts.Strong(14.5f * s), t.Text, new RectangleF(46 * s, 15 * s, W - 90 * s, 24 * s));
            float textBottom = Height - (_action is not null ? 64 : 12) * s;
            Painter.Text(g, _text, Fonts.Regular(13 * s), t.SubText, new RectangleF(16 * s, 42 * s, W - 32 * s, textBottom - 42 * s), StringAlignment.Near, StringAlignment.Near, wrap: true);
            if (_action is not null)
            {
                float by = Height - 52 * s, bh = 36 * s;
                _actionRect = new RectangleF(W - 16 * s - 150 * s, by, 150 * s, bh);
                _laterRect = new RectangleF(_actionRect.X - 8 * s - 96 * s, by, 96 * s, bh);
                var fill = _hotBtn == "action" ? Theme.Mix(t.AccentFill, Color.White, 0.12f) : t.AccentFill;
                Painter.FillRound(g, _actionRect, 10 * s, fill);
                Painter.GlassSheen(g, _actionRect, 10 * s, t.Dark);
                Painter.Text(g, _action, Fonts.Strong(13 * s), t.OnAccent, _actionRect, StringAlignment.Center);
                Painter.FillRound(g, _laterRect, 10 * s, _hotBtn == "later" ? t.SurfaceHover : t.Surface);
                Painter.Text(g, "Позже", Fonts.Regular(13 * s), t.Text, _laterRect, StringAlignment.Center);
            }
            return;
        }
        if (Device is not Accessory a) return;

        var accent = Kind switch
        {
            ToastKind.Low => t.Bad,
            ToastKind.Charged => t.Good,
            _ => a.Profile.BrandColor ?? t.Accent,
        };

        // header
        var ic = new RectangleF(16 * s, 16 * s, 40 * s, 40 * s);
        Painter.FillRound(g, ic, 20 * s, Theme.Alpha(accent, 50));
        char glyph = Kind switch { ToastKind.Low => Glyphs.Warning, ToastKind.Charged => Glyphs.Check, _ => Glyphs.For(a.Kind) };
        Painter.Glyph(g, glyph, 18 * s, Kind == ToastKind.Connected && a.Profile.BrandColor is Color bc ? bc : (Kind == ToastKind.Connected ? t.AccentText : accent), ic);

        string kicker = Kind switch
        {
            ToastKind.Low => $"Низкий заряд · {Fmt.Percent(a.WeakestLevel)}",
            ToastKind.Charged => "Полностью заряжено",
            _ => a.Connected ? $"Подключено · {a.Link}" : "Отключено",
        };
        var feat = Svc.Subtitle(a);
        if (feat.Length > 0 && Kind == ToastKind.Connected) kicker += " · " + feat;
        Painter.Text(g, a.DisplayName, Fonts.Strong(15 * s), t.Text, new RectangleF(66 * s, 16 * s, W - 66 * s - 40 * s, 22 * s));
        Painter.Text(g, kicker, Fonts.Regular(12.5f * s), Kind == ToastKind.Low ? t.Bad : t.SubText, new RectangleF(66 * s, 38 * s, W - 66 * s - 16 * s, 18 * s));

        float y = 72 * s;
        if (RingLayout(out int n))
        {
            var items = new List<(string, Part)>();
            if (n == 1) items.Add(("Общий", new Part(a.Level, a.Charging)));
            else
            {
                var (ln, rn, cn) = Svc.PartLabels(a);
                items.Add((ln, a.Left));
                items.Add((rn, a.Right));
                if (n == 3) items.Add((cn, a.Case));
            }
            float ring = 64 * s;
            float slot = (W - 32 * s) / items.Count;
            for (int i = 0; i < items.Count; i++)
            {
                var (label, p) = items[i];
                float cx = 16 * s + slot * i + slot / 2;
                var rr = new RectangleF(cx - ring / 2, y, ring, ring);
                float? frac = p.Level is int l ? l / 100f * _grow : null;
                Painter.Ring(g, rr, 6 * s, frac, t.BarColor(p.Level, p.Charging, Svc.Low), t.Track);
                string v = p.Level is int lv ? $"{(int)Math.Round(lv * _grow)}" : "—";
                var vf = Fonts.Big(18 * s);
                Painter.Text(g, v, vf, p.Known ? t.Text : t.SubText, new RectangleF(rr.X, rr.Y + (p.Charging ? 6 * s : 0), rr.Width, rr.Height - (p.Charging ? 6 * s : 0)), StringAlignment.Center);
                if (p.Charging) Painter.Glyph(g, Glyphs.Bolt, 11 * s, t.Good, new RectangleF(rr.X, rr.Y + 10 * s, rr.Width, 14 * s));
                Painter.Text(g, label, Fonts.Regular(12 * s), t.SubText, new RectangleF(cx - slot / 2, y + ring + 6 * s, slot, 18 * s), StringAlignment.Center);
            }
            y += 96 * s;
        }
        else if (!NoData())
        {
            int? lvl = a.Level;
            int? shown = lvl is int x ? (int)Math.Round(x * _grow) : null;
            // Big draining battery + number
            var batt = new RectangleF(16 * s, y + 2 * s, 74 * s, 34 * s);
            IconRenderer.DrawBigBattery(g, batt, shown, a.Charging, t.Text, t.BarColor(lvl, a.Charging, Svc.Low), t.Track);
            var pf = Fonts.Big(30 * s);
            Painter.Text(g, Fmt.Percent(shown), pf, t.LevelColor(lvl, a.Charging, Svc.Low), new RectangleF(batt.Right + 16 * s, y - 2 * s, 140 * s, 42 * s));
            if (Svc.Estimate(a) is null && a.SystemSource is string src)
                Painter.Text(g, src == "Windows" ? "по данным Windows" : src == "GATT" ? "Bluetooth LE" : "контроллер", Fonts.Regular(12 * s), t.SubText,
                    new RectangleF(W - 150 * s, y + 10 * s, 134 * s, 18 * s), StringAlignment.Far);
            y += 46 * s;
        }

        if (Footer() is string f)
        {
            Painter.Text(g, f, Fonts.Regular(12 * s), t.SubText, new RectangleF(16 * s, y + 2 * s, W - 32 * s, 18 * s));
        }
    }

    // ───────────── Input ─────────────

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        _life.Stop();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hotBtn = "";
        if (!_closing && _action is null) _life.Start();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string hot = _actionRect.Contains(e.Location) ? "action" : _laterRect.Contains(e.Location) ? "later" : "";
        if (hot != _hotBtn) { _hotBtn = hot; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) { Dismiss(); return; }
        if (_action is not null)
        {
            // Sticky: only the buttons (or the close cross) act; a stray click elsewhere does nothing.
            if (_actionRect.Contains(e.Location)) { _onClick?.Invoke(); Dismiss(); }
            else if (_laterRect.Contains(e.Location) || (_hover && _closeRect.Contains(e.Location))) Dismiss();
            return;
        }
        if (!_closeRect.Contains(e.Location)) _onClick?.Invoke();
        Dismiss();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _life.Dispose(); _anim?.Cancel(); }
        base.Dispose(disposing);
    }
}

/// <summary>One toast at a time: device toasts replace/upgrade, messages queue.</summary>
internal sealed class ToastManager
{
    ToastForm? _current;
    readonly Queue<Action> _queue = new();

    public bool Visible => _current is { IsDisposed: false };

    public Action<Accessory>? DeviceClicked;

    public void ShowDevice(Accessory a, ToastKind kind)
    {
        if (_current is { IsDisposed: false } cur && cur.Device == a)
        {
            cur.Upgrade(kind);
            return;
        }
        Replace(f => f.ShowDevice(a, kind, () => DeviceClicked?.Invoke(a)));
    }

    public void ShowMessage(string title, string text, char glyph, Action? onClick = null)
    {
        if (Visible) { _queue.Enqueue(() => ShowMessage(title, text, glyph, onClick)); return; }
        Replace(f => f.ShowMessage(title, text, glyph, onClick));
    }

    /// <summary>Sticky notification (new version): stays above the taskbar until «Обновить» or «Позже».</summary>
    public void ShowAction(string title, string text, char glyph, string action, Action onAction)
    {
        if (Visible) { _queue.Enqueue(() => ShowAction(title, text, glyph, action, onAction)); return; }
        Replace(f => f.ShowAction(title, text, glyph, action, onAction));
    }

    void Replace(Action<ToastForm> show)
    {
        var old = _current;
        var f = new ToastForm();
        f.Finished += OnFinished;
        _current = f;
        if (old is { IsDisposed: false })
        {
            old.Finished -= OnFinished;
            old.Dismiss();
        }
        show(f);
    }

    void OnFinished(ToastForm f)
    {
        if (_current != f) return;
        _current = null;
        if (_queue.Count > 0) Ui.Post(_queue.Dequeue());
    }

    public void Refresh(Accessory a)
    {
        if (_current is { IsDisposed: false } cur && cur.Device == a) cur.RefreshContent();
    }

    public void ApplyTheme()
    {
        if (_current is { IsDisposed: false } cur) cur.ApplyTheme();
    }

    public void CloseAll()
    {
        _queue.Clear();
        if (_current is { IsDisposed: false } cur) cur.Close();
        _current = null;
    }
}
