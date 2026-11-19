using System.Diagnostics;

namespace Glance;

internal sealed class SettingsForm : Form, IGlassLayer, IMessageFilter
{
    readonly AppSettings _s;
    readonly GlassScroller _scroll;
    readonly AlphaBuffer _buf = new();
    GlassPanel _panel => _scroll.Content;
    float _sc;
    SettingRow? _updateRow;

    public event Func<Task<string>>? CheckUpdatesRequested;
    public event Action? InstallUpdateRequested, HistoryRequested, BackToPanelRequested;
    /// <summary>Version text of an update that is ready to install, if any.</summary>
    public Func<string?> PendingUpdate { get; set; } = () => null;

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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _scroll = new GlassScroller { Dock = DockStyle.Fill };
        Controls.Add(_scroll);
        ClientSize = new Size(Px(580), Px(720));
        Build();
        Theme.Changed += OnTheme;
        Application.AddMessageFilter(this);
    }

    public void PaintLayer(Graphics g, Size size) => Theme.Current.PaintBackdrop(g, new Rectangle(Point.Empty, size));

    protected override void WndProc(ref Message m)
    {
        if (GlassHost.Handle(this, ref m, _buf, null)) return;
        base.WndProc(ref m);
    }

    /// <summary>Mouse wheel anywhere over the window scrolls the settings (child controls do not eat it).</summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x020A || IsDisposed || !Visible) return false;
        if (!Bounds.Contains(Cursor.Position)) return false;
        int delta = (short)((long)m.WParam >> 16);
        _scroll.ScrollBy(-delta * Px(56) / 120);
        return true;
    }

    int Px(float dp) => (int)MathF.Round(dp * _sc);

    static string Lines(int n) => (n % 10 == 1 && n % 100 != 11) ? "строка" : (n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14)) ? "строки" : "строк";

    void OnTheme()
    {
        // Deferred: the change may come from one of this form's own controls, which Build() disposes.
        Ui.Post(() =>
        {
            if (IsDisposed) return;
            Build();
            ApplyChrome();
        });
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
        if (!IsHandleCreated) return;
        Native.DarkTitleBar(Handle, t.Dark);
        GlassHost.SetBlur(Handle, t.Glass);
        Invalidate(true);
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

    string? _page;

    // Esc / Backspace on a page goes back to the tiles; Esc on the tiles closes the window.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.Back && ActiveControl is not TextBox)
        {
            if (_page is not null) { OpenPage(null); return true; }
            if (keyData == Keys.Escape) { Close(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Opens a settings page (null = the tiles home) with a quick slide-in.</summary>
    /// <summary>Self-test: opens a page by its tile title.</summary>
    internal void OpenPageForTest(string? page) => OpenPage(page);
    internal static IEnumerable<string> PageTitles => Pages.Select(p => p.Title);

    void OpenPage(string? page)
    {
        _page = page;
        Build();
        _scroll.ScrollTo(0);
    }

    static readonly (string Title, string Sub, char Glyph, Color Color)[] Pages =
    {
        ("Оформление", "Liquid Glass или классика, цветовые темы, виджет", '\uE771', Color.FromArgb(124, 92, 255)),
        ("Значки в трее", "Вид значка, общий или для каждого устройства", '\uE7F4', Color.FromArgb(0, 132, 255)),
        ("Уведомления", "Подключение, низкий заряд, звуки, тихие часы", '\uEA8F', Color.FromArgb(255, 122, 69)),
        ("Мышь и клавиатура", "Чувствительность, прокрутка, повтор клавиш", '\uE962', Color.FromArgb(46, 196, 160)),
        ("Наушники", "Заряд каждого наушника и кейса", '\uE7F6', Color.FromArgb(236, 72, 153)),
        ("Система", "Автозапуск, горячая клавиша, частота опроса", '\uE713', Color.FromArgb(100, 116, 139)),
        ("Обновления", "Версия, проверка и автоустановка", '\uE895', Color.FromArgb(14, 165, 233)),
        ("Glance Pro", "Виджет, темы, правила устройств, история", '\uE735', Color.FromArgb(245, 158, 11)),
        ("О программе", "Что нового, данные, автор", '\uE946', Color.FromArgb(139, 92, 246)),
    };

    void BuildHome(ref int y, int x, int w, float sc, Theme t)
    {
        // hero header
        // back arrow: closes settings and returns to the accessories panel
        var back = new BackButton(sc) { Location = new Point(x, y + Px(4)) };
        back.Click += (_, _) => { BackToPanelRequested?.Invoke(); Close(); };
        _panel.Controls.Add(back);
        var title = new GlassLabel("Настройки", Fonts.Strong(22 * sc), t.Text) { Location = new Point(x + Px(50), y), Size = new Size(w - Px(50), Px(34)) };
        var sub = new GlassLabel($"Glance {AppInfo.VersionText}" + (Pro.IsActive ? $" · Pro · {Pro.Info!.Owner}" : ""), Fonts.Regular(12.5f * sc), t.SubText)
            { Location = new Point(x + Px(50), y + Px(32)), Size = new Size(w - Px(50), Px(20)) };
        _panel.Controls.Add(title);
        _panel.Controls.Add(sub);
        y += Px(68);
        int gap = Px(10), tw = (w - gap) / 2, th = Px(128);
        for (int i = 0; i < Pages.Length; i++)
        {
            var (name, desc, glyph, color) = Pages[i];
            var tile = new TileButton(sc, name, desc, glyph, color)
            {
                Location = new Point(x + (i % 2) * (tw + gap), y + (i / 2) * (th + gap)),
                Size = new Size(tw, th),
            };
            string page = name;
            tile.Click += (_, _) => Ui.Post(() => OpenPage(page));
            tile.GotFocus += (_, _) => _scroll.EnsureVisible(tile);
            _panel.Controls.Add(tile);
        }
        y += (Pages.Length + 1) / 2 * (th + gap) + Px(14);
    }

    void Build()
    {
        _panel.SuspendLayout();
        foreach (Control c in _panel.Controls.Cast<Control>().ToList()) { _panel.Controls.Remove(c); c.Dispose(); }
        var t = Theme.Current;
        BackColor = t.Bg;
        float sc = _sc;
        int y = Px(20), x = Px(20);
        int w = ClientSize.Width - Px(44);

        // Home = tiles; a page shows only the rows of its section (the builder below runs in full each time,
        // rows of other sections are simply not added).
        string cur = "";
        if (_page is null) BuildHome(ref y, x, w, sc, t);
        else
        {
            var back = new BackButton(sc) { Location = new Point(x, y) };
            back.Click += (_, _) => Ui.Post(() => OpenPage(null));
            _panel.Controls.Add(back);
            var title = new GlassLabel(_page, Fonts.Strong(20 * sc), t.Text) { Location = new Point(x + Px(50), y), Size = new Size(w - Px(50), Px(38)) };
            _panel.Controls.Add(title);
            y += Px(56);
        }

        void Section(string title) => cur = title;

        SettingRow Row(string title, string desc, Control? right)
        {
            if (_page != cur) // not on this page: build nothing visible
            {
                var ghost = new SettingRow(sc, title, desc, right);
                ghost.Dispose();
                return ghost;
            }
            var row = new SettingRow(sc, title, desc, right) { Location = new Point(x, y), Width = w };
            _panel.Controls.Add(row);
            if (right is not null) right.GotFocus += (_, _) => _scroll.EnsureVisible(row);
            y += row.Height + Px(6);
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

        // ── Glance Pro ──
        Section("Glance Pro");
        if (Pro.IsActive)
        {
            Row($"Pro активирован · {Pro.Info!.Owner}", $"Спасибо за поддержку! Ключ от {Pro.Info.Issued:dd.MM.yyyy}", null);
        }
        else
        {
            var buy = new FlatButton(sc, "Купить", primary: true);
            buy.Click += (_, _) => AppInfo.OpenUrl(ProUi.BuyUrl);
            Row("Откройте Glance Pro", string.Join(" · ", new[] { "виджет", "темы и звуки", "правила устройств", "история 30 дней и износ" }), buy);
            var enter = new FlatButton(sc, "Ввести ключ");
            enter.Click += (_, _) => { if (ProUi.Activate(_s)) { _s.NotifyChanged(); Ui.Post(Build); } };
            Row("Уже есть ключ?", "Ключ приходит после покупки на Boosty", enter);
        }
        var tip = new FlatButton(sc, "Поддержать ♥");
        tip.Click += (_, _) => ProUi.Donate();
        Row("Пожертвование — по желанию", "Любая сумма на Boosty. Glance остаётся бесплатным, ничего не блокируется", tip);

        // Pro control: real control when active, otherwise a «Pro» button that explains what it unlocks.
        Control ProOnly(Control real, string feature)
        {
            if (Pro.IsActive) return real;
            real.Dispose();
            var b = new FlatButton(sc, "🔒 Pro");
            b.Click += (_, _) => { ProUi.Prompt(_s, feature); if (Pro.IsActive) Ui.Post(Build); };
            return b;
        }

        // ── Оформление ──
        Section("Оформление");
        Row("Стиль", "Liquid Glass — стекло, блики и яркие цвета. Классика — строгий плоский вид", Seg(new[] { "Liquid Glass", "Классика" },
            _s.LiquidGlass ? 0 : 1, i => _s.LiquidGlass = i == 0));

        Row("Цветовая тема", "Свой акцент интерфейса и оттенок стекла", ProOnly(Seg(Theme.AccentPresets.Select(p => p.Name).ToArray(),
            Math.Clamp(_s.AccentPreset, 0, Theme.AccentPresets.Length - 1), i => _s.AccentPreset = i), "Цветовые темы"));
        Row("Виджет на рабочем столе", "Заряд всех устройств поверх окон. Перетаскивается, двойной клик — панель", ProOnly(Toggle(_s.Widget, v => _s.Widget = v), "Виджет на рабочем столе"));
        var hist = new FlatButton(sc, "Открыть");
        hist.Click += (_, _) => HistoryRequested?.Invoke();
        Row("История и износ", "Графики за 24 ч / 7 / 30 дней, расход, циклы, CSV", ProOnly(hist, "История и износ"));

        // ── Трей ──
        Section("Значки в трее");
        Row("Значки", "Отдельный для каждого устройства или один общий", Seg(new[] { "Для каждого", "Один общий" },
            _s.TrayMode == TrayMode.PerDevice ? 0 : 1, i => _s.TrayMode = i == 0 ? TrayMode.PerDevice : TrayMode.Single));
        IconStyle[] styles = { IconStyle.Ring, IconStyle.Device, IconStyle.Battery, IconStyle.Number, IconStyle.BatteryNumber };
        Row("Вид значка", "Кольцо заряда вокруг устройства, или батарея. Общий значок — столбики всех устройств", Seg(new[] { "Кольцо", "Устройство", "Батарея", "%", "Бат.+%" },
            Math.Max(0, Array.IndexOf(styles, _s.IconStyle)), i => _s.IconStyle = styles[i]));
        Row("Устройства без данных о заряде", "Показывать значок «?» для мышей и клавиатур на приёмниках 2.4 ГГц", Toggle(_s.ShowUnknownInTray, v => _s.ShowUnknownInTray = v));
        Row("Карточка при наведении", "Заряд каждого наушника и кейса вместо обычной подсказки", Toggle(_s.RichHover, v => _s.RichHover = v));

        // ── Уведомления ──
        Section("Уведомления");
        Row("Окно при подключении", "Уведомление при каждом подключении: Bluetooth, 2.4 ГГц и USB", Toggle(_s.ShowConnectPopup, v => _s.ShowConnectPopup = v));
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
        Row("Умные напоминания", "Заранее предупредит, если по истории разряда устройства хватит меньше чем на полтора часа", Toggle(_s.SmartReminders, v => _s.SmartReminders = v));
        Row("Звуки Glance", "Свои наборы: стеклянные колокольчики в Liquid Glass, чёткие сигналы в классике. Ночью и в играх молчат", Toggle(_s.Sounds, v => _s.Sounds = v));
        var vol = new Stepper(sc, _s.SoundVolume, 10, 100, 10, v => $"{v}%");
        vol.ValueChanged += () => { _s.SoundVolume = vol.Value; _s.NotifyChanged(); Sounds.Play(SoundKind.Connected, _s, preview: true); };
        Row("Громкость звуков", "При изменении сразу звучит пример", vol);
        Row("Набор звуков", "Стекло/Классика по стилю, или «Неон» и «Маримба»", ProOnly(Seg(new[] { "По стилю", "Неон", "Маримба" },
            Math.Clamp(_s.SoundPack, 0, 2), i => { _s.SoundPack = i; Sounds.Play(SoundKind.Connected, _s, preview: true); }), "Наборы звуков"));
        Row("Звук при критическом заряде", "Тревожный сигнал вместе с баннером — даже ночью", Toggle(_s.CriticalSound, v => _s.CriticalSound = v));
        Row("Полностью заряжено", "Сообщить, когда устройство на зарядке дошло до 100%", Toggle(_s.FullChargeAlert, v => _s.FullChargeAlert = v));
        var qFrom = new Stepper(sc, _s.QuietFrom, 0, 23, 1, v => $"с {v:00}:00");
        qFrom.ValueChanged += () => { _s.QuietFrom = qFrom.Value; _s.NotifyChanged(); };
        var qTo = new Stepper(sc, _s.QuietTo, 0, 23, 1, v => $"до {v:00}:00");
        qTo.ValueChanged += () => { _s.QuietTo = qTo.Value; _s.NotifyChanged(); };
        Row("Тихие часы", "Ночью молчат окна о низком и полном заряде. Подключения и критический заряд — показываются", Toggle(_s.QuietHours, v => _s.QuietHours = v));
        Row("Начало тихих часов", "", qFrom);
        Row("Конец тихих часов", "", qTo);
        Row("Не беспокоить", "Обычные окна молчат в играх и при «Не беспокоить» (критический баннер — нет)", Toggle(_s.RespectFocus, v => _s.RespectFocus = v));

        // ── Мышь и клавиатура (системные параметры Windows, применяются сразу) ──
        Section("Мышь и клавиатура");
        var inp = SystemInput.Read();
        var ptr = new Stepper(sc, inp.PointerSpeed, 1, 20, 1, v => $"{v} из 20");
        ptr.ValueChanged += () => SystemInput.SetPointerSpeed(ptr.Value);
        Row("Чувствительность мыши", "Скорость указателя Windows (10 — по умолчанию). Действует сразу на все мыши", ptr);
        Row("Повышенная точность указателя", "Ускорение: быстрое движение уводит курсор дальше. В играх обычно выключают", Toggle(inp.EnhancePrecision, v => SystemInput.SetEnhancePrecision(v)));
        var wheel = new Stepper(sc, inp.WheelLines, 1, 20, 1, v => $"{v} {Lines(v)}");
        wheel.ValueChanged += () => SystemInput.SetWheelLines(wheel.Value);
        Row("Прокрутка колёсиком", "", wheel);
        var delay = new Stepper(sc, inp.KeyDelay, 0, 3, 1, v => $"{(v + 1) * 250} мс");
        delay.ValueChanged += () => SystemInput.SetKeyDelay(delay.Value);
        Row("Задержка перед повтором клавиши", "Сколько держать клавишу, прежде чем символ начнёт повторяться", delay);
        var rate = new Stepper(sc, inp.KeyRate, 0, 31, 1, v => $"{SystemInput.RateToCps(v):0} в сек");
        rate.ValueChanged += () => SystemInput.SetKeyRate(rate.Value);
        Row("Скорость повтора клавиши", "Отклик удерживаемой клавиши: сколько символов в секунду", rate);
        var reset = new FlatButton(sc, "Сбросить");
        reset.Click += (_, _) => { SystemInput.ResetDefaults(); Ui.Post(Build); };
        Row("Стандартные значения Windows", "Скорость 10, точность включена, 3 строки, 500 мс, 30 в сек", reset);

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
            Row("Проверять обновления", "Каждые 30 минут, только GitHub Releases этого проекта", Toggle(_s.CheckUpdates, v => _s.CheckUpdates = v));
            Row("Устанавливать автоматически", "Файл проверяется по SHA-256 перед заменой", Toggle(_s.AutoInstallUpdates, v => _s.AutoInstallUpdates = v));
            string? pending = PendingUpdate();
            var btn = new FlatButton(sc, pending is null ? "Проверить сейчас" : $"Обновить до {pending}", primary: true);
            _updateRow = Row($"Версия {AppInfo.VersionText}", pending is null ? "Нажмите, чтобы проверить" : $"Доступна версия {pending}. Настройки и история сохранятся", btn);
            btn.Click += async (_, _) =>
            {
                if (PendingUpdate() is not null) { InstallUpdateRequested?.Invoke(); return; }
                if (CheckUpdatesRequested is null || _updateRow is null) return;
                btn.Enabled = false;
                _updateRow.Description = "Проверяем…";
                try
                {
                    _updateRow.Description = await CheckUpdatesRequested();
                    if (PendingUpdate() is not null && !IsDisposed) { Build(); return; } // button becomes «Обновить до …»
                }
                catch (Exception ex) { _updateRow.Description = "Ошибка: " + ex.Message; }
                finally { if (!btn.IsDisposed) btn.Enabled = true; }
            };
        }
        else
        {
            Row($"Версия {AppInfo.VersionText}", "Автообновление работает в сборках из GitHub Releases", null);
        }

        // ── О программе ──
        Section("О программе");
        var news = new FlatButton(sc, "Открыть");
        news.Click += (_, _) => WhatsNewForm.ShowWindow();
        Row("Что нового", "Описание всех версий и изменений", news);
        var folder = new FlatButton(sc, "Открыть");
        folder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(AppInfo.DataDir) { UseShellExecute = true }); } catch { } };
        Row("Данные и журнал", "Настройки, история заряда и журнал хранятся только на этом компьютере", folder);
        var donate = new FlatButton(sc, "Поддержать ♥", primary: true);
        donate.Click += (_, _) => ProUi.Donate();
        Row("Поддержать автора", "Необязательное пожертвование любой суммой на Boosty — на развитие Glance и версии для Android", donate);
        var author = new FlatButton(sc, "Steam");
        author.Click += (_, _) => AppInfo.OpenUrl(AppInfo.AuthorUrl);
        Row("Автор — ddedlish", "Glance © 2026 ddedlish. Без рекламы, без сбора данных", author);
        y += Px(24);
        _panel.ResumeLayout();
        _scroll.SetContentHeight(y);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnTheme;
            Application.RemoveMessageFilter(this);
            _buf.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Small themed text input dialog.</summary>
internal sealed class InputForm : Form
{
    readonly TextBox _box;

    InputForm(string title, string label, string value, int maxLength)
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
            BackColor = t.SurfaceSolid,
            ForeColor = t.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(Px(20), Px(42)),
            Width = Px(340),
            MaxLength = maxLength,
        };
        Controls.Add(_box);

        // Plain system buttons: this modal dialog stays as simple (and robust) as possible.
        Button Btn(string text, bool primary) => new()
        {
            Text = text,
            AutoSize = false,
            Size = new Size(Px(primary ? 120 : 96), Px(34)),
            FlatStyle = FlatStyle.Flat,
            Font = Fonts.Regular(13 * sc),
            BackColor = primary ? t.AccentFill : t.SurfaceSolid,
            ForeColor = primary ? t.OnAccent : t.Text,
            FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = t.Opaque(t.Border) },
            UseVisualStyleBackColor = false,
        };
        var ok = Btn("Сохранить", true);
        var cancel = Btn("Отмена", false);
        AcceptButton = ok;
        CancelButton = cancel;
        cancel.Location = new Point(ClientSize.Width - Px(20) - cancel.Width, ClientSize.Height - Px(16) - cancel.Height);
        ok.Location = new Point(cancel.Left - Px(8) - ok.Width, cancel.Top);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(ok);
        Controls.Add(cancel);
        HandleCreated += (_, _) => Native.DarkTitleBar(Handle, t.Dark);
        Shown += (_, _) => { Activate(); Native.SetForegroundWindow(Handle); _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(string title, string label, string value, int maxLength = 60)
    {
        using var f = new InputForm(title, label, value, maxLength);
        // No owner: ShowDialog() would otherwise attach the dialog to the active window — the tray panel,
        // which closes a moment later and takes the dialog down with it.
        return f.ShowDialog(NoOwner.Instance) == DialogResult.OK ? f._box.Text.Trim() : null;
    }

    sealed class NoOwner : IWin32Window
    {
        public static readonly NoOwner Instance = new();
        public IntPtr Handle => IntPtr.Zero;
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
            BackColor = t.SurfaceSolid,
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

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Current.SurfaceSolid);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { /* DWM draws the border */ }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Painter.Prepare(e.Graphics);
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            Painter.FillRound(e.Graphics, r, 4, Theme.Current.SurfaceHoverSolid);
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
        public override Color ToolStripDropDownBackground => Theme.Current.SurfaceSolid;
        public override Color MenuBorder => Theme.Current.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Current.SurfaceHoverSolid;
        public override Color ImageMarginGradientBegin => Theme.Current.SurfaceSolid;
        public override Color ImageMarginGradientMiddle => Theme.Current.SurfaceSolid;
        public override Color ImageMarginGradientEnd => Theme.Current.SurfaceSolid;
        public override Color SeparatorDark => Theme.Current.Border;
        public override Color SeparatorLight => Theme.Current.Border;
    }
}
