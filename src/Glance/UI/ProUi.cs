namespace Glance;

/// <summary>Glance Pro: the «what you get» dialog, key activation and the author's key generator.</summary>
internal static class ProUi
{
    public const string BuyUrl = "https://boosty.to/ddedlish";

    /// <summary>Optional donation: the same Boosty page, any amount, unlocks nothing and asks for nothing.</summary>
    public static void Donate() => AppInfo.OpenUrl(BuyUrl);

    /// <summary>The second way to donate: a YooMoney transfer, any amount, from a card or a wallet.</summary>
    public const string YooMoneyUrl = "https://yoomoney.ru/to/4100119618332005";
    public static void DonateYooMoney() => AppInfo.OpenUrl(YooMoneyUrl);

    public static readonly string[] Features =
    {
        "Виджет на рабочем столе с зарядом всех устройств",
        "Цветовые темы и дополнительные наборы звуков",
        "Правила для каждого устройства: свой порог, без уведомлений",
        "История заряда за 30 дней, графики, CSV и оценка износа",
    };

    /// <summary>Shown when a locked Pro feature is clicked.</summary>
    public static void Prompt(AppSettings s, string? feature = null)
    {
        using var f = new ProForm(feature);
        var r = f.ShowDialog(NoOwner.Instance);
        if (r == DialogResult.Yes) AppInfo.OpenUrl(BuyUrl);
        else if (r == DialogResult.Retry) Activate(s);
    }

    /// <summary>Asks for a key; returns true when Pro got activated.</summary>
    public static bool Activate(AppSettings s)
    {
        var key = InputForm.Ask("Glance Pro", "Вставьте ключ Glance Pro (GLANCE-…)", "", maxLength: 1000); // keys are ~130 chars
        if (key is null) return false;
        if (!Pro.Apply(key))
        {
            MessageBox.Show("Ключ не подошёл. Проверьте, что он скопирован целиком.", "Glance Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Pro.Apply(s.LicenseKey);
            return false;
        }
        s.LicenseKey = key.Trim();
        s.Save();
        Sounds.Play(SoundKind.Charged, s, preview: true);
        MessageBox.Show($"Glance Pro активирован. Спасибо за поддержку, {Pro.Info!.Owner}!", "Glance Pro", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    sealed class NoOwner : IWin32Window
    {
        public static readonly NoOwner Instance = new();
        public IntPtr Handle => IntPtr.Zero;
    }

    /// <summary>Simple, robust dialog (system buttons) listing what Pro adds.</summary>
    sealed class ProForm : Form
    {
        public ProForm(string? feature)
        {
            var t = Theme.Current;
            float sc = Native.DpiAt(Cursor.Position) / 96f;
            int Px(float d) => (int)MathF.Round(d * sc);
            Text = "Glance Pro";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            BackColor = t.Bg;
            ClientSize = new Size(Px(440), Px(300));
            int y = Px(18);
            Controls.Add(new Label { Text = feature is null ? "Glance Pro" : $"«{feature}» — в Glance Pro", Font = Fonts.Strong(16 * sc), ForeColor = t.Text, BackColor = t.Bg, Location = new Point(Px(20), y), Size = new Size(Px(400), Px(28)) });
            y += Px(36);
            Controls.Add(new Label { Text = "Бесплатная версия остаётся полной: заряд, уведомления, значки, критический баннер. Pro добавляет:", Font = Fonts.Regular(12.5f * sc), ForeColor = t.SubText, BackColor = t.Bg, Location = new Point(Px(20), y), Size = new Size(Px(400), Px(38)) });
            y += Px(44);
            foreach (var line in Features)
            {
                Controls.Add(new Label { Text = "✦  " + line, Font = Fonts.Regular(13 * sc), ForeColor = t.Text, BackColor = t.Bg, Location = new Point(Px(24), y), Size = new Size(Px(396), Px(24)) });
                y += Px(26);
            }
            Button Btn(string text, DialogResult res, bool primary, int width) => new()
            {
                Text = text, DialogResult = res, AutoSize = false, Size = new Size(Px(width), Px(34)), FlatStyle = FlatStyle.Flat,
                Font = Fonts.Regular(13 * sc), BackColor = primary ? t.AccentFill : t.SurfaceSolid, ForeColor = primary ? t.OnAccent : t.Text,
                FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = t.Opaque(t.Border) }, UseVisualStyleBackColor = false,
            };
            var buy = Btn("Купить на Boosty", DialogResult.Yes, true, 150);
            var key = Btn("Ввести ключ", DialogResult.Retry, false, 120);
            var close = Btn("Закрыть", DialogResult.Cancel, false, 100);
            close.Location = new Point(ClientSize.Width - Px(20) - close.Width, ClientSize.Height - Px(16) - close.Height);
            key.Location = new Point(close.Left - Px(8) - key.Width, close.Top);
            buy.Location = new Point(key.Left - Px(8) - buy.Width, close.Top);
            Controls.AddRange(new Control[] { buy, key, close });
            AcceptButton = buy;
            CancelButton = close;
            HandleCreated += (_, _) => Native.DarkTitleBar(Handle, t.Dark);
            Shown += (_, _) => { Activate(); Native.SetForegroundWindow(Handle); };
        }
    }
}

/// <summary>
/// Author-only key generator: <c>Glance.exe --keygen</c>. Needs the private key file (glance-private.key),
/// which never ships with the program — without it this window can't create anything.
/// </summary>
internal sealed class KeyGenForm : Form
{
    readonly TextBox _name, _key;
    readonly Label _file;
    string? _pem;
    static string PathFile => Path.Combine(AppInfo.DataDir, "keygen.path");

    public KeyGenForm()
    {
        var t = Theme.Current;
        float sc = Native.DpiAt(Cursor.Position) / 96f;
        int Px(float d) => (int)MathF.Round(d * sc);
        Text = "Glance Pro — выпуск ключей";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = t.Bg;
        ClientSize = new Size(Px(560), Px(300));
        Label L(string text, int y) { var l = new Label { Text = text, ForeColor = t.SubText, BackColor = t.Bg, Font = Fonts.Regular(12.5f * sc), Location = new Point(Px(20), y), Size = new Size(Px(520), Px(20)) }; Controls.Add(l); return l; }
        _file = L("Закрытый ключ: не выбран", Px(14));
        var pick = new Button { Text = "Выбрать файл ключа…", Location = new Point(Px(20), Px(38)), Size = new Size(Px(200), Px(30)), FlatStyle = FlatStyle.System };
        pick.Click += (_, _) => PickKey();
        Controls.Add(pick);
        L("Имя покупателя (как на Boosty)", Px(84));
        _name = new TextBox { Location = new Point(Px(20), Px(106)), Width = Px(520), Font = Fonts.Regular(14 * sc), MaxLength = 80 };
        Controls.Add(_name);
        var make = new Button { Text = "Создать ключ", Location = new Point(Px(20), Px(146)), Size = new Size(Px(160), Px(32)), FlatStyle = FlatStyle.System };
        Controls.Add(make);
        _key = new TextBox { Location = new Point(Px(20), Px(190)), Size = new Size(Px(520), Px(60)), Multiline = true, ReadOnly = true, Font = Fonts.Regular(12 * sc) };
        Controls.Add(_key);
        var copy = new Button { Text = "Копировать", Location = new Point(Px(20), Px(258)), Size = new Size(Px(140), Px(30)), FlatStyle = FlatStyle.System };
        copy.Click += (_, _) => { if (_key.Text.Length > 0) Clipboard.SetText(_key.Text); };
        Controls.Add(copy);
        make.Click += (_, _) =>
        {
            if (_pem is null) { MessageBox.Show("Сначала выберите файл закрытого ключа (glance-private.key)."); return; }
            try
            {
                var key = License.Issue(_name.Text, _pem);
                if (License.Verify(key) is null) { MessageBox.Show("Этот закрытый ключ не подходит к Glance (не тот файл)."); return; }
                _key.Text = key;
                Clipboard.SetText(key);
            }
            catch (Exception e) { MessageBox.Show(e.Message); }
        };
        AcceptButton = make;
        try { if (File.Exists(PathFile)) LoadKey(File.ReadAllText(PathFile).Trim()); } catch { }
    }

    void PickKey()
    {
        using var d = new OpenFileDialog { Title = "Файл закрытого ключа Glance", Filter = "Ключ (*.key;*.pem)|*.key;*.pem|Все файлы|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (LoadKey(d.FileName)) try { File.WriteAllText(PathFile, d.FileName); } catch { }
    }

    bool LoadKey(string path)
    {
        try
        {
            var pem = File.ReadAllText(path);
            if (!pem.Contains("PRIVATE KEY")) return false;
            _pem = pem;
            _file.Text = "Закрытый ключ: " + path;
            return true;
        }
        catch { return false; }
    }
}
