using System.Diagnostics;

namespace Glance;

internal sealed class SettingsForm : Form
{
    readonly AppSettings _s;
    readonly Panel _panel;
    float _sc;
    SettingRow? _updateRow;

    public event Func<Task<string>>? CheckUpdatesRequested;

    public SettingsForm(AppSettings settings)
    {
        _s = settings;
        Text = "Glance — настройки";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        ShowIcon = true;
        try { Icon = Icon.ExtractAssociatedIcon(AppInfo.ExePath); } catch { }
        _sc = Native.DpiAt(Cursor.Position) / 96f;
        _panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(_panel);
        ClientSize = new Size(Px(580), Px(720));
        Build();
        Theme.Changed += OnTheme;
    }

    int Px(float dp) => (int)MathF.Round(dp * _sc);

    void OnTheme()
    {
        if (IsDisposed) return;
        Build();
        ApplyChrome();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyChrome();
    }

    void ApplyChrome()
    {
        var t = Theme.Current;
        BackColor = t.Bg;
        _panel.BackColor = t.Bg;
        if (IsHandleCreated) Native.DarkTitleBar(Handle, t.Dark);
        if (_panel.IsHandleCreated) Native.SetWindowTheme(_panel.Handle, t.Dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _sc = e.DeviceDpiNew / 96f;
        Build();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    void Build()
    {
        _panel.SuspendLayout();
        foreach (Control c in _panel.Controls.Cast<Control>().ToList()) { _panel.Controls.Remove(c); c.Dispose(); }
        var t = Theme.Current;
        BackColor = t.Bg;
        _panel.BackColor = t.Bg;
        float sc = _sc;
        int y = Px(20), x = Px(20);
        int w = ClientSize.Width - Px(40) - SystemInformation.VerticalScrollBarWidth;

        void Section(string title)
        {
            var l = new Label
            {
                Text = title,
                Font = Fonts.Strong(14 * sc),
                ForeColor = t.Text,
                BackColor = t.Bg,
                AutoSize = false,
                Location = new Point(x, y + Px(8)),
                Size = new Size(w, Px(26)),
            };
            _panel.Controls.Add(l);
            y += Px(40);
        }

        SettingRow Row(string title, string desc, Control? right)
        {
            var row = new SettingRow(sc, title, desc, right) { Location = new Point(x, y), Width = w };
            _panel.Controls.Add(row);
            y += row.Height + Px(4);
            return row;
        }

        ToggleSwitch Toggle(bool value, Action<bool> set)
        {
            var tg = new ToggleSwitch(sc, value);
            tg.CheckedChanged += () => { set(tg.Checked); _s.NotifyChanged(); };
            return tg;
        }

        Segmented Seg(string[] options, int selected, Action<int> set)
        {
            var sg = new Segmented(sc, options, selected);
            sg.SelectedChanged += () => { set(sg.Selected); _s.NotifyChanged(); };
            return sg;
        }

        // ── Трей ──
        Section("Значки в трее");
        Row("Значки", "Отдельный для каждого устройства или один общий", Seg(new[] { "Для каждого", "Один общий" },
            _s.TrayMode == TrayMode.PerDevice ? 0 : 1, i => _s.TrayMode = i == 0 ? TrayMode.PerDevice : TrayMode.Single));
        Row("Вид значка", "Батарея разряжается вместе с устройством", Seg(new[] { "Батарея", "Проценты", "Батарея + %" },
            (int)_s.IconStyle, i => _s.IconStyle = (IconStyle)i));
        Row("Карточка при наведении", "Заряд каждого наушника и кейса вместо обычной подсказки", Toggle(_s.RichHover, v => _s.RichHover = v));

        // ── Уведомления ──
        Section("Уведомления");
        Row("Окно при подключении", "Показывает заряд сразу после подключения аксессуара", Toggle(_s.ShowConnectPopup, v => _s.ShowConnectPopup = v));
        var dur = new Stepper(sc, _s.PopupSeconds, 3, 15, 1, v => $"{v} с");
        dur.ValueChanged += () => { _s.PopupSeconds = dur.Value; _s.NotifyChanged(); };
        Row("Время показа окна", "", dur);
        Row("Низкий заряд", "Один раз за цикл разряда, без повторов", Toggle(_s.LowBatteryAlerts, v => _s.LowBatteryAlerts = v));
        var low = new Stepper(sc, _s.LowBatteryThreshold, 5, 50, 5, v => $"{v}%");
        low.ValueChanged += () => { _s.LowBatteryThreshold = low.Value; _s.NotifyChanged(); };
        Row("Порог низкого заряда", "", low);
        Row("Критический заряд поверх всего", "Баннер поверх игр и видео: не сворачивает и не перехватывает управление", Toggle(_s.CriticalOverlay, v => _s.CriticalOverlay = v));
        var crit = new Stepper(sc, _s.CriticalThreshold, 3, 20, 1, v => $"{v}%");
        crit.ValueChanged += () => { _s.CriticalThreshold = crit.Value; _s.NotifyChanged(); };
        Row("Порог критического заряда", "", crit);
        Row("Полностью заряжено", "Сообщить, когда устройство на зарядке дошло до 100%", Toggle(_s.FullChargeAlert, v => _s.FullChargeAlert = v));
        Row("Не беспокоить", "Обычные окна молчат в играх и при «Не беспокоить» (критический баннер — нет)", Toggle(_s.RespectFocus, v => _s.RespectFocus = v));

        // ── Наушники ──
        Section("Наушники");
        Row("Заряд каждого наушника и кейса", "AirPods, Beats и модели с Google Fast Pair (JBL, Sony, Pixel…)", Toggle(_s.BudsDetail, v => _s.BudsDetail = v));
        int rssiIdx = _s.RssiThreshold >= -68 ? 0 : _s.RssiThreshold >= -80 ? 1 : 2;
        Row("Дальность поиска", "Ближе — меньше шанс поймать чужие наушники", Seg(new[] { "Близко", "Средне", "Далеко" }, rssiIdx,
            i => _s.RssiThreshold = i switch { 0 => -65, 1 => -75, _ => -88 }));

        // ── Система ──
        Section("Система");
        Row("Запускать вместе с Windows", "Тихий старт через несколько секунд после входа", Toggle(Autostart.IsEnabled(), v => Autostart.Set(v)));
        Row("Горячая клавиша Ctrl + Alt + B", "Открыть панель аксессуаров", Toggle(_s.Hotkey, v => _s.Hotkey = v));
        int pollIdx = _s.PollSeconds <= 30 ? 0 : _s.PollSeconds <= 60 ? 1 : 2;
        Row("Частота опроса Windows", "Реже — ещё меньше нагрузка. Подключения отслеживаются мгновенно всегда", Seg(new[] { "30 с", "1 мин", "3 мин" }, pollIdx,
            i => _s.PollSeconds = i switch { 0 => 30, 1 => 60, _ => 180 }));

        // ── Обновления ──
        Section("Обновления");
        if (AppInfo.UpdatesConfigured)
        {
            Row("Проверять обновления", "Раз в сутки, только GitHub Releases этого проекта", Toggle(_s.CheckUpdates, v => _s.CheckUpdates = v));
            Row("Устанавливать автоматически", "Файл проверяется по SHA-256 перед заменой", Toggle(_s.AutoInstallUpdates, v => _s.AutoInstallUpdates = v));
            var btn = new FlatButton(sc, "Проверить сейчас", primary: true);
            _updateRow = Row($"Версия {AppInfo.VersionText}", "Нажмите, чтобы проверить", btn);
            btn.Click += async (_, _) =>
            {
                if (CheckUpdatesRequested is null || _updateRow is null) return;
                btn.Enabled = false;
                _updateRow.Description = "Проверяем…";
                try { _updateRow.Description = await CheckUpdatesRequested(); }
                catch (Exception ex) { _updateRow.Description = "Ошибка: " + ex.Message; }
                finally { if (!btn.IsDisposed) btn.Enabled = true; }
            };
        }
        else
        {
            Row($"Версия {AppInfo.VersionText}", "Автообновление работает в сборках из GitHub Releases", null);
        }

        // ── Footer ──
        y += Px(10);
        var folder = new LinkLabel
        {
            Text = "Открыть папку с данными и журналом",
            Font = Fonts.Regular(12.5f * sc),
            LinkColor = t.AccentText,
            ActiveLinkColor = t.AccentText,
            BackColor = t.Bg,
            AutoSize = true,
            Location = new Point(x, y),
        };
        folder.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppInfo.DataDir) { UseShellExecute = true }); } catch { }
        };
        _panel.Controls.Add(folder);
        y += Px(28);
        var privacy = new Label
        {
            Text = "Glance не собирает данные. Всё хранится только на этом компьютере.",
            Font = Fonts.Regular(12 * sc),
            ForeColor = t.SubText,
            BackColor = t.Bg,
            AutoSize = false,
            Location = new Point(x, y),
            Size = new Size(w, Px(20)),
        };
        _panel.Controls.Add(privacy);
        y += Px(40);
        // spacer so the last row is not glued to the bottom edge when scrolling
        _panel.Controls.Add(new Label { Location = new Point(x, y), Size = new Size(1, 1), BackColor = t.Bg });

        _panel.ResumeLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnTheme;
        base.Dispose(disposing);
    }
}

/// <summary>Small themed text input dialog.</summary>
internal sealed class InputForm : Form
{
    readonly TextBox _box;

    InputForm(string title, string label, string value)
    {
        var t = Theme.Current;
        float sc = Native.DpiAt(Cursor.Position) / 96f;
        int Px(float d) => (int)MathF.Round(d * sc);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = t.Bg;
        ClientSize = new Size(Px(380), Px(150));

        Controls.Add(new Label { Text = label, ForeColor = t.SubText, BackColor = t.Bg, Font = Fonts.Regular(12.5f * sc), Location = new Point(Px(20), Px(16)), Size = new Size(Px(340), Px(20)) });
        _box = new TextBox
        {
            Text = value,
            Font = Fonts.Regular(14 * sc),
            BackColor = t.Surface,
            ForeColor = t.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(Px(20), Px(42)),
            Width = Px(340),
            MaxLength = 60,
        };
        Controls.Add(_box);

        var ok = new FlatButton(sc, "Сохранить", primary: true);
        var cancel = new FlatButton(sc, "Отмена");
        cancel.Location = new Point(ClientSize.Width - Px(20) - cancel.Width, ClientSize.Height - Px(16) - cancel.Height);
        ok.Location = new Point(cancel.Left - Px(8) - ok.Width, cancel.Top);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };
        HandleCreated += (_, _) => Native.DarkTitleBar(Handle, t.Dark);
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(string title, string label, string value)
    {
        using var f = new InputForm(title, label, value);
        return f.ShowDialog() == DialogResult.OK ? f._box.Text.Trim() : null;
    }
}

/// <summary>Themed context menus with rounded corners.</summary>
internal static class Menus
{
    static readonly Font MenuFont = new(Fonts.Text, 9.75f);
    static readonly Font MenuFontBold = new(Fonts.Text, 9.75f, FontStyle.Bold);

    public static ContextMenuStrip Create()
    {
        var t = Theme.Current;
        var m = new ContextMenuStrip
        {
            Renderer = new ThemedRenderer(),
            ShowImageMargin = false,
            BackColor = t.Surface,
            ForeColor = t.Text,
            Font = MenuFont,
            Padding = new Padding(2, 4, 2, 4),
        };
        m.Opened += (_, _) => Native.RoundCorners(m.Handle);
        m.Closed += (_, _) => Ui.Post(() => m.Dispose()); // after the click handler ran
        return m;
    }

    public static ToolStripMenuItem Item(this ContextMenuStrip m, string text, Action onClick, bool enabled = true)
    {
        var it = new ToolStripMenuItem(text) { Enabled = enabled, Padding = new Padding(8, 4, 8, 4) };
        it.Click += (_, _) =>
        {
            try { onClick(); } catch (Exception e) { Log.Error("Menu", e); }
        };
        m.Items.Add(it);
        return it;
    }

    public static void Header(this ContextMenuStrip m, string text)
    {
        var it = new ToolStripMenuItem(text) { Enabled = false, Padding = new Padding(8, 4, 8, 4) };
        it.Font = MenuFontBold;
        m.Items.Add(it);
    }

    public static void Separator(this ContextMenuStrip m) => m.Items.Add(new ToolStripSeparator());

    sealed class ThemedRenderer : ToolStripProfessionalRenderer
    {
        public ThemedRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Current.Surface);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { /* DWM draws the border */ }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Painter.Prepare(e.Graphics);
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            Painter.FillRound(e.Graphics, r, 4, Theme.Current.SurfaceHover);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var t = Theme.Current;
            e.TextColor = e.Item.Enabled ? t.Text : (e.Item.Font.Bold ? t.Text : t.SubText);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Theme.Current.Border);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            Painter.Glyph(e.Graphics, Glyphs.Check, 10, Theme.Current.Text, new RectangleF(r.X, r.Y, r.Width, r.Height));
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Current.Surface;
        public override Color MenuBorder => Theme.Current.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Current.SurfaceHover;
        public override Color ImageMarginGradientBegin => Theme.Current.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Current.Surface;
        public override Color ImageMarginGradientEnd => Theme.Current.Surface;
        public override Color SeparatorDark => Theme.Current.Border;
        public override Color SeparatorLight => Theme.Current.Border;
    }
}
