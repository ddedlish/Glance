namespace Glance;

/// <summary>Base for owner-drawn Windows 11 style controls.</summary>
internal abstract class ThemedControl : Control
{
    protected float Sc;
    protected bool Hot, Pressed;

    protected ThemedControl(float scale)
    {
        Sc = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected Theme T => Theme.Current;
    protected int Px(float dp) => (int)MathF.Round(dp * Sc);

    protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hot = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; Focus(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? T.Bg);

    protected void FocusRing(Graphics g)
    {
        if (!Focused || !ShowFocusCues) return;
        Painter.StrokeRound(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f, T.Text, Math.Max(1, Sc));
    }
}

internal sealed class ToggleSwitch : ThemedControl
{
    bool _on;
    float _knob;
    public event Action? CheckedChanged;

    public ToggleSwitch(float scale, bool on) : base(scale)
    {
        _on = on;
        _knob = on ? 1 : 0;
        Size = new Size(Px(40), Px(20));
    }

    public bool Checked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            float from = _knob, to = value ? 1 : 0;
            Animator.Run(140, p => { _knob = from + (to - from) * p; if (!IsDisposed) Invalidate(); });
            CheckedChanged?.Invoke();
        }
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        float rad = r.Height / 2;
        if (_on)
        {
            Painter.FillRound(g, r, rad, Hot ? Theme.Mix(T.AccentFill, T.Text, 0.1f) : T.AccentFill);
        }
        else
        {
            Painter.FillRound(g, r, rad, Hot ? T.SurfaceHover : Theme.Alpha(T.Surface, 0));
            Painter.StrokeRound(g, r, rad, T.SubText, Math.Max(1, Sc));
        }
        float d = (Pressed ? 14 : Hot ? 13 : 12) * Sc;
        float travel = r.Width - 2 * rad;
        float cx = r.X + rad + travel * _knob, cy = r.Y + rad;
        using var b = new SolidBrush(_on ? T.OnAccent : T.SubText);
        g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
        FocusRing(g);
    }
}

internal sealed class Stepper : ThemedControl
{
    int _value;
    readonly int _min, _max, _step;
    readonly Func<int, string> _format;
    int _hotSide; // -1 minus, +1 plus
    public event Action? ValueChanged;

    public Stepper(float scale, int value, int min, int max, int step, Func<int, string> format) : base(scale)
    {
        _value = value; _min = min; _max = max; _step = step; _format = format;
        Size = new Size(Px(132), Px(32));
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int side = e.X < Px(34) ? -1 : e.X > Width - Px(34) ? 1 : 0;
        if (side != _hotSide) { _hotSide = side; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.X < Px(34)) Value -= _step;
        else if (e.X > Width - Px(34)) Value += _step;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Down) Value -= _step;
        if (e.KeyCode is Keys.Right or Keys.Up) Value += _step;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Painter.FillRound(g, r, 5 * Sc, T.Surface);
        Painter.StrokeRound(g, r, 5 * Sc, T.Border, Math.Max(1, Sc));
        var minus = new RectangleF(2 * Sc, 2 * Sc, 30 * Sc, Height - 4 * Sc);
        var plus = new RectangleF(Width - 32 * Sc, 2 * Sc, 30 * Sc, Height - 4 * Sc);
        if (Hot && _hotSide == -1) Painter.FillRound(g, minus, 4 * Sc, T.SurfaceHover);
        if (Hot && _hotSide == 1) Painter.FillRound(g, plus, 4 * Sc, T.SurfaceHover);
        var f = Fonts.Regular(14 * Sc);
        Painter.Text(g, "−", f, _value > _min ? T.Text : T.SubText, minus, StringAlignment.Center);
        Painter.Text(g, "+", f, _value < _max ? T.Text : T.SubText, plus, StringAlignment.Center);
        Painter.Text(g, _format(_value), Fonts.Strong(13 * Sc), T.Text, new RectangleF(minus.Right, 0, plus.Left - minus.Right, Height), StringAlignment.Center);
        if (Focused && ShowFocusCues) Painter.StrokeRound(g, r, 5 * Sc, T.Text, Math.Max(1, Sc));
    }
}

internal sealed class Segmented : ThemedControl
{
    readonly string[] _options;
    int _selected, _hotIdx = -1;
    float[] _widths = Array.Empty<float>();
    public event Action? SelectedChanged;

    public Segmented(float scale, string[] options, int selected) : base(scale)
    {
        _options = options;
        _selected = Math.Clamp(selected, 0, options.Length - 1);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var f = Fonts.Regular(13 * Sc);
        _widths = options.Select(o => Painter.Width(g, o, f) + 24 * Sc).ToArray();
        Size = new Size((int)Math.Ceiling(_widths.Sum() + 4 * Sc), Px(32));
    }

    public int Selected
    {
        get => _selected;
        set
        {
            if (value == _selected || value < 0 || value >= _options.Length) return;
            _selected = value;
            Invalidate();
            SelectedChanged?.Invoke();
        }
    }

    int Hit(int x)
    {
        float cx = 2 * Sc;
        for (int i = 0; i < _widths.Length; i++) { if (x >= cx && x < cx + _widths[i]) return i; cx += _widths[i]; }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = Hit(e.X);
        if (h != _hotIdx) { _hotIdx = h; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = Hit(e.X);
        if (h >= 0) Selected = h;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Selected = Math.Max(0, _selected - 1);
        if (e.KeyCode == Keys.Right) Selected = Math.Min(_options.Length - 1, _selected + 1);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Painter.FillRound(g, r, 5 * Sc, T.Surface);
        Painter.StrokeRound(g, r, 5 * Sc, T.Border, Math.Max(1, Sc));
        float x = 2 * Sc;
        var f = Fonts.Regular(13 * Sc);
        for (int i = 0; i < _options.Length; i++)
        {
            var seg = new RectangleF(x, 2 * Sc, _widths[i], Height - 4 * Sc);
            if (i == _selected) Painter.FillRound(g, seg, 4 * Sc, T.AccentFill);
            else if (Hot && i == _hotIdx) Painter.FillRound(g, seg, 4 * Sc, T.SurfaceHover);
            Painter.Text(g, _options[i], i == _selected ? Fonts.Strong(13 * Sc) : f, i == _selected ? T.OnAccent : T.Text, seg, StringAlignment.Center);
            x += _widths[i];
        }
        if (Focused && ShowFocusCues) Painter.StrokeRound(g, r, 5 * Sc, T.Text, Math.Max(1, Sc));
    }
}

internal sealed class FlatButton : ThemedControl
{
    readonly bool _primary;

    public FlatButton(float scale, string text, bool primary = false) : base(scale)
    {
        _primary = primary;
        Text = text;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        Size = new Size((int)Math.Ceiling(Painter.Width(g, text, Fonts.Regular(13 * Sc)) + 32 * Sc), Px(32));
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Color bg = _primary ? (Hot ? Theme.Mix(T.AccentFill, T.Text, 0.1f) : T.AccentFill) : (Pressed ? T.Bg : Hot ? T.SurfaceHover : T.Surface);
        Painter.FillRound(g, r, 5 * Sc, Enabled ? bg : T.Surface);
        if (!_primary) Painter.StrokeRound(g, r, 5 * Sc, T.Border, Math.Max(1, Sc));
        Painter.Text(g, Text, Fonts.Regular(13 * Sc), !Enabled ? T.SubText : _primary ? T.OnAccent : T.Text, r, StringAlignment.Center);
        if (Focused && ShowFocusCues) Painter.StrokeRound(g, r, 5 * Sc, T.Text, Math.Max(1, Sc));
    }
}

/// <summary>Settings card row: title + description on the left, a control on the right.</summary>
internal sealed class SettingRow : Control
{
    readonly float _sc;
    readonly string _title;
    string _desc;
    readonly Control? _right;

    public SettingRow(float scale, string title, string desc, Control? right)
    {
        _sc = scale; _title = title; _desc = desc; _right = right;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = (int)MathF.Round((desc.Length > 0 ? 76 : 52) * scale);
        if (right is not null) Controls.Add(right);
    }

    public string Description
    {
        get => _desc;
        set { _desc = value; Invalidate(); }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_right is not null)
            _right.Location = new Point(Width - (int)(16 * _sc) - _right.Width, (Height - _right.Height) / 2);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? Theme.Current.Bg);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        var t = Theme.Current;
        float s = _sc;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Painter.FillRound(g, r, 6 * s, t.Surface);
        Painter.StrokeRound(g, r, 6 * s, t.Border, Math.Max(1, s));
        // Child control is drawn by WinForms; its background matches the card.
        float textW = Width - 32 * s - (_right?.Width ?? 0) - 12 * s;
        if (_desc.Length > 0)
        {
            Painter.Text(g, _title, Fonts.Regular(14 * s), t.Text, new RectangleF(16 * s, 12 * s, textW, 22 * s));
            Painter.Text(g, _desc, Fonts.Regular(12 * s), t.SubText, new RectangleF(16 * s, 34 * s, textW, 34 * s), StringAlignment.Near, StringAlignment.Near, wrap: true);
        }
        else Painter.Text(g, _title, Fonts.Regular(14 * s), t.Text, new RectangleF(16 * s, 0, textW, Height));
    }

    public override Color BackColor
    {
        get => Theme.Current.Surface;
        set { }
    }
}
