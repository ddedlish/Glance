using System.Drawing.Drawing2D;

namespace Glance;

/// <summary>
/// Simple one-window installer: app name, where it goes ("Изменить…" if needed),
/// one switch for autostart and a single "Установить" button. After installing,
/// Glance starts automatically. Updates of an existing install use the same window.
/// </summary>
internal sealed class SetupForm : Form
{
    enum State { Ready, Installing, Done, Failed }

    readonly float _sc;
    readonly string? _existing = Installer.InstalledDirectory;
    readonly Icon? _icon, _bigIcon;
    State _state = State.Ready;
    string _target;
    string _message = "";
    int _percent;
    string? _installedExe;

    readonly ToggleSwitch _autostart;
    readonly FlatButton _action;
    readonly LinkLabel _change, _author;

    public SetupForm()
    {
        _sc = Native.DpiAt(Cursor.Position) / 96f;
        _target = _existing ?? Installer.DefaultDirectory;
        var t = Theme.Current;

        Text = _existing is null ? "Установка Glance" : "Обновление Glance";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        BackColor = t.Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        try { _icon = Icon.ExtractAssociatedIcon(AppInfo.ExePath); Icon = _icon; } catch { }
        try { _bigIcon = Icon.ExtractIcon(AppInfo.ExePath, 0, Px(64)); } catch { }
        ClientSize = new Size(Px(460), Px(300));

        int left = Px(28);

        _change = Link("Изменить папку…", new Point(left, Px(150)));
        _change.LinkClicked += (_, _) => ChangeFolder();

        _autostart = new ToggleSwitch(_sc, true) { Location = new Point(left, Px(190)) };
        _autostart.CheckedChanged += Invalidate;
        Controls.Add(_autostart);

        _action = new FlatButton(_sc, _existing is null ? "Установить" : "Обновить", primary: true);
        _action.Size = new Size(Math.Max(_action.Width, Px(140)), Px(36));
        _action.Location = new Point(ClientSize.Width - Px(28) - _action.Width, ClientSize.Height - Px(56));
        _action.Click += async (_, _) => await OnAction();
        Controls.Add(_action);

        _author = Link("by " + AppInfo.Author, new Point(left, ClientSize.Height - Px(46)));
        _author.LinkClicked += (_, _) => AppInfo.OpenUrl(AppInfo.AuthorUrl);

        AcceptButton = null;
    }

    int Px(float dp) => (int)MathF.Round(dp * _sc);

    LinkLabel Link(string text, Point at)
    {
        var t = Theme.Current;
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Font = Fonts.Regular(12.5f * _sc),
            LinkColor = t.AccentText,
            ActiveLinkColor = t.AccentText,
            VisitedLinkColor = t.AccentText,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = t.Bg,
            Location = at,
        };
        Controls.Add(l);
        return l;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle, Theme.Current.Dark);
    }

    void ChangeFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Выберите папку или диск — внутри будет создана папка Glance",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _target = Installer.NormalizeTarget(dlg.SelectedPath);
            _message = "";
        }
        catch (Exception e) { _message = e.Message; }
        Invalidate();
    }

    async Task OnAction()
    {
        switch (_state)
        {
            case State.Installing:
                return;
            case State.Done:
                Close();
                return;
        }

        string dir;
        try { dir = Installer.NormalizeTarget(_target); }
        catch (Exception e) { _message = e.Message; _state = State.Failed; Invalidate(); return; }

        _state = State.Installing;
        _message = "";
        _action.Enabled = _change.Enabled = _autostart.Enabled = false;
        _action.Text = "Устанавливаем…";
        _action.Invalidate();
        Invalidate();

        var progress = new Progress<(int Percent, string Step)>(p => { _percent = p.Percent; _message = p.Step; Invalidate(); });
        try
        {
            _installedExe = await Installer.InstallAsync(new InstallOptions(dir, _autostart.Checked, DesktopShortcut: false, StartMenuShortcut: true), progress);
            _state = State.Done;
            _message = "";
            Installer.Launch(_installedExe);
            _action.Text = "Готово";
            _action.Enabled = true;
            _change.Visible = false;
            _autostart.Visible = false;
        }
        catch (Exception e)
        {
            Log.Error("Install", e);
            _state = State.Failed;
            _message = e.Message;
            _action.Text = "Повторить";
            _action.Enabled = _change.Enabled = _autostart.Enabled = true;
        }
        _action.Invalidate();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && _state != State.Installing) Close();
        if (e.KeyCode == Keys.Enter) _ = OnAction();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_state == State.Installing) { e.Cancel = true; return; } // never leave a half-copied install
        DialogResult = _state == State.Done ? DialogResult.OK : DialogResult.Cancel;
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Painter.Prepare(g);
        var t = Theme.Current;
        float s = _sc, W = ClientSize.Width, left = 28 * s;
        g.Clear(t.Bg);

        // thin accent line on top
        using (var br = new LinearGradientBrush(new RectangleF(0, 0, W, 4 * s), t.Accent, Theme.Mix(t.Accent, Color.FromArgb(120, 60, 220), 0.6f), LinearGradientMode.Horizontal))
            g.FillRectangle(br, 0, 0, W, 4 * s);

        // app identity
        var iconRect = new Rectangle(Px(28), Px(28), Px(56), Px(56));
        if (_bigIcon is not null) g.DrawIcon(_bigIcon, iconRect);
        else if (_icon is not null) g.DrawIcon(_icon, iconRect);
        Painter.Text(g, "Glance", Fonts.Big(24 * s), t.Text, new RectangleF(100 * s, 28 * s, 300 * s, 32 * s));
        string sub = _existing is null
            ? $"Заряд аксессуаров в трее · v{AppInfo.VersionText}"
            : $"Обновление {Installer.InstalledVersion ?? "?"} → {AppInfo.VersionText}";
        Painter.Text(g, sub, Fonts.Regular(13 * s), t.SubText, new RectangleF(100 * s, 62 * s, W - 120 * s, 20 * s));

        switch (_state)
        {
            case State.Ready:
            case State.Failed:
                Painter.Text(g, "Папка установки", Fonts.Regular(12 * s), t.SubText, new RectangleF(left, 104 * s, W - 2 * left, 18 * s));
                Painter.Text(g, _target, Fonts.Regular(13.5f * s), t.Text, new RectangleF(left, 122 * s, W - 2 * left, 22 * s));
                Painter.Text(g, "Запускать вместе с Windows", Fonts.Regular(13.5f * s), t.Text,
                    new RectangleF(_autostart.Right + 12 * s, _autostart.Top - 2 * s, W - _autostart.Right - left, _autostart.Height + 4 * s));
                if (_message.Length > 0)
                    Painter.Text(g, _message, Fonts.Regular(12 * s), t.Bad, new RectangleF(left, 220 * s, W - 2 * left, 34 * s), StringAlignment.Near, StringAlignment.Near, wrap: true);
                break;

            case State.Installing:
                Painter.Text(g, _message, Fonts.Regular(13.5f * s), t.Text, new RectangleF(left, 128 * s, W - 2 * left, 22 * s));
                Painter.Bar(g, new RectangleF(left, 160 * s, W - 2 * left, 6 * s), _percent / 100f, t.AccentFill, t.Track);
                break;

            case State.Done:
                var c = new RectangleF(left, 116 * s, 36 * s, 36 * s);
                Painter.FillRound(g, c, 18 * s, t.Good);
                Painter.Glyph(g, Glyphs.Check, 15 * s, Color.White, c);
                Painter.Text(g, "Glance установлен и запущен", Fonts.Strong(15 * s), t.Text, new RectangleF(left + 50 * s, 116 * s, W - left * 2 - 50 * s, 22 * s));
                Painter.Text(g, "Значок появится в трее рядом с часами.", Fonts.Regular(12.5f * s), t.SubText, new RectangleF(left + 50 * s, 138 * s, W - left * 2 - 50 * s, 18 * s));
                Painter.Text(g, "Не видно? Нажмите стрелку ˄ в трее или включите значок: Параметры → Персонализация → Панель задач.",
                    Fonts.Regular(12 * s), t.SubText, new RectangleF(left, 176 * s, W - 2 * left, 40 * s), StringAlignment.Near, StringAlignment.Near, wrap: true);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _icon?.Dispose(); _bigIcon?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Uninstall confirmation in the same style.</summary>
internal sealed class UninstallForm : Form
{
    readonly ToggleSwitch _data;

    public bool RemoveData => _data.Checked;

    public UninstallForm()
    {
        float s = Native.DpiAt(Cursor.Position) / 96f;
        int Px(float d) => (int)MathF.Round(d * s);
        var t = Theme.Current;
        Text = "Удаление Glance";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = t.Bg;
        ClientSize = new Size(Px(460), Px(210));
        try { Icon = Icon.ExtractAssociatedIcon(AppInfo.ExePath); } catch { }

        Controls.Add(new Label { Text = "Удалить Glance с этого компьютера?", Font = Fonts.Strong(15 * s), ForeColor = t.Text, BackColor = t.Bg, AutoSize = false, Location = new Point(Px(24), Px(20)), Size = new Size(Px(410), Px(28)) });
        Controls.Add(new Label { Text = "Программа, ярлыки и автозапуск будут удалены.", Font = Fonts.Regular(13 * s), ForeColor = t.SubText, BackColor = t.Bg, AutoSize = false, Location = new Point(Px(24), Px(54)), Size = new Size(Px(410), Px(22)) });
        _data = new ToggleSwitch(s, false) { Location = new Point(Px(24), Px(98)) };
        Controls.Add(_data);
        Controls.Add(new Label { Text = "Также удалить настройки и историю заряда", Font = Fonts.Regular(13 * s), ForeColor = t.Text, BackColor = t.Bg, AutoSize = false, Location = new Point(Px(76), Px(96)), Size = new Size(Px(360), Px(24)) });

        var ok = new FlatButton(s, "Удалить", primary: true);
        var cancel = new FlatButton(s, "Отмена");
        ok.Location = new Point(ClientSize.Width - Px(24) - ok.Width, ClientSize.Height - Px(52));
        cancel.Location = new Point(ok.Left - Px(10) - cancel.Width, ok.Top);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        HandleCreated += (_, _) => Native.DarkTitleBar(Handle, t.Dark);
    }
}
