using Tmds.DBus;

namespace Glance;

/// <summary>Linux composition root: UPower → alerts/history → tray + notifications.</summary>
internal sealed class LinuxHost
{
    readonly MainLoop _loop;
    readonly AppSettings _s;
    readonly HistoryStore _h;
    readonly UpdateService _upd = new();
    UPowerMonitor? _upower;
    LinuxTray? _tray;
    Notifier? _notify;
    readonly HashSet<string> _lowAlerted = new(StringComparer.Ordinal), _fullAlerted = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _criticalAt = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _lastPopup = new(StringComparer.Ordinal);
    readonly Debouncer _traySync;
    UpdateService.Release? _pending;
    bool _installing;

    public LinuxHost(MainLoop loop)
    {
        _loop = loop;
        _s = AppSettings.Load();
        _h = new HistoryStore();
        _traySync = new Debouncer(150, () => _ = SyncTrayAsync());
    }

    public async Task StartAsync(bool afterUpdate)
    {
        Connection session;
        try
        {
            session = new Connection(Address.Session);
            await session.ConnectAsync();
        }
        catch (Exception e)
        {
            Log.Error("No D-Bus session bus (Glance needs a desktop session)", e);
            Console.Error.WriteLine("Glance: нет сессии D-Bus — запустите из графического окружения.");
            _loop.Quit();
            return;
        }

        _notify = new Notifier(session, Icons.InstalledIconPath);
        _tray = new LinuxTray(session) { Activated = ShowSummary };
        try { await _tray.StartAsync(); } catch (Exception e) { Log.Error("Tray", e); }

        try
        {
            var system = new Connection(Address.System);
            await system.ConnectAsync();
            _upower = new UPowerMonitor(system, _s);
            _upower.Connected += OnConnected;
            _upower.Disconnected += OnDisconnected;
            _upower.Changed += OnChanged;
            await _upower.StartAsync();
        }
        catch (Exception e)
        {
            Log.Error("UPower", e);
            await _notify.ShowAsync("upower", "Glance", "Служба UPower недоступна — установите пакет upower.", Notifier.Urgency.Normal, null, null);
        }

        _loop.Post(() => { _traySync.Trigger(); RebuildMenu(); });
        if (afterUpdate) _ = _notify.ShowAsync("update", $"Glance обновлён до {AppInfo.VersionText}", "Настройки и история сохранены.", Notifier.Urgency.Low, null, null);
        ScheduleUpdateChecks();
    }

    IEnumerable<Accessory> Visible => (_upower?.Devices ?? Enumerable.Empty<Accessory>())
        .Where(a => !_s.For(a.Key).HideInTray).OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase);

    // ───────────── Events ─────────────

    void OnConnected(Accessory a, bool initial)
    {
        if (initial || !_s.ShowConnectPopup || _s.IsQuietNow()) return;
        if (_lastPopup.TryGetValue(a.Key, out var t) && (DateTime.Now - t).TotalSeconds < 90) return;
        _lastPopup[a.Key] = DateTime.Now;
        // Give UPower a moment to report the level.
        _loop.Delay(1500, () =>
        {
            if (!a.Connected) return;
            var feat = Profiles.Features(a.Profile);
            string body = a.Level is int l ? $"Заряд {l}%{(a.Charging ? " · заряжается" : "")}" : "Заряд пока неизвестен";
            if (feat.Length > 0) body += $"\n{feat}";
            _ = _notify!.ShowAsync(a.Key, $"Подключено: {a.DisplayName}", body, Notifier.Urgency.Low, Big(a), a.Level);
        });
    }

    void OnDisconnected(Accessory a)
    {
        var ds = _s.For(a.Key);
        ds.Name = a.Name;
        ds.Kind = a.KindHint;
        ds.LastSeen = a.LastSeen;
        if (a.LastLevel is int l) ds.LastLevel = l;
        _s.Save();
        _traySync.Trigger();
        RebuildMenu();
    }

    void OnChanged(Accessory a)
    {
        if (a.Connected && a.Level is int lv)
        {
            _h.Add(a.Key, lv, a.Charging, a.SessionStartPending);
            a.SessionStartPending = false;
        }
        CheckAlerts(a);
        _traySync.Trigger();
        RebuildMenu();
    }

    void CheckAlerts(Accessory a)
    {
        if (!a.Connected || a.Level is not int lv || _notify is null) return;
        bool ch = a.Charging;

        if (ch || lv > _s.CriticalThreshold + 3) _criticalAt.Remove(a.Key);
        else if (_s.CriticalOverlay && lv <= _s.CriticalThreshold && (!_criticalAt.TryGetValue(a.Key, out var shown) || lv <= shown - 5))
        {
            _criticalAt[a.Key] = lv;
            _lowAlerted.Add(a.Key);
            // Critical urgency: stays until dismissed, shown over fullscreen apps, never takes focus.
            _ = _notify.ShowAsync(a.Key, $"Критический заряд · {lv}%", $"{a.DisplayName} скоро выключится — поставьте на зарядку.", Notifier.Urgency.Critical, Big(a), lv);
            return;
        }

        if (_s.LowBatteryAlerts)
        {
            if (!ch && lv <= _s.LowBatteryThreshold)
            {
                if (!_s.IsQuietNow() && _lowAlerted.Add(a.Key))
                    _ = _notify.ShowAsync(a.Key, $"Низкий заряд · {lv}%", a.DisplayName + EstimateText(a), Notifier.Urgency.Normal, Big(a), lv, 10000);
            }
            else if (ch || lv >= _s.LowBatteryThreshold + 5) _lowAlerted.Remove(a.Key);
        }

        if (_s.FullChargeAlert)
        {
            if (ch && lv >= 100) { if (!_s.IsQuietNow() && _fullAlerted.Add(a.Key)) _ = _notify.ShowAsync(a.Key, "Полностью заряжено", a.DisplayName, Notifier.Urgency.Low, Big(a), 100); }
            else if (!ch) _fullAlerted.Remove(a.Key);
        }
    }

    string EstimateText(Accessory a)
    {
        if (a.Level is not int l) return "";
        var e = _h.Estimate(a.Key, l, a.Charging);
        if (e is not TimeSpan t) return "";
        string at = Fmt.ClockAt(t) is string clock ? (a.Charging ? $" · к {clock}" : $" · до {clock}") : "";
        return (a.Charging ? $"\nДо 100% ≈ {Fmt.Duration(t)}" : $"\nХватит ≈ на {Fmt.Duration(t)}") + at;
    }

    Canvas Big(Accessory a) => BatteryArt.Render(64, a.Level, a.Charging, IconStyle.Battery, _s.LowBatteryThreshold);

    void ShowSummary()
    {
        var list = Visible.ToList();
        string body = list.Count == 0 ? "Нет подключённых аксессуаров"
            : string.Join("\n", list.Select(a => $"{a.DisplayName} — {Fmt.Percent(a.Level)}{(a.Charging ? " ⚡" : "")}{EstimateText(a).Replace("\n", " · ")}"));
        _ = _notify?.ShowAsync("summary", "Аксессуары", body, Notifier.Urgency.Low, null, null, 8000);
    }

    // ───────────── Tray ─────────────

    async Task SyncTrayAsync()
    {
        if (_tray is null) return;
        var list = Visible.Where(a => a.Level is not null).ToList();
        var wanted = new List<(string, IReadOnlyList<Canvas>, string, string, string)>();
        IReadOnlyList<Canvas> Pix(int? l, bool ch) => new[] { 22, 32, 48 }.Select(sz => BatteryArt.Render(sz, l, ch, _s.IconStyle, _s.LowBatteryThreshold)).ToList();

        if (list.Count == 0)
            wanted.Add(("__app", new[] { 22, 32, 48 }.Select(BatteryArt.App).ToList(), "Glance", "Glance", "Нет подключённых аксессуаров"));
        else if (_s.TrayMode == TrayMode.Single)
        {
            var weakest = list.MinBy(a => a.Level ?? 101)!;
            wanted.Add(("__summary", Pix(weakest.Level, weakest.Charging), "Glance", "Аксессуары",
                string.Join("\n", list.Select(a => $"{a.DisplayName} — {Fmt.Percent(a.Level)}"))));
        }
        else
        {
            foreach (var a in list)
                wanted.Add((a.Key, Pix(a.Level, a.Charging), a.DisplayName, $"{a.DisplayName} — {Fmt.Percent(a.Level)}",
                    (a.Charging ? "Заряжается" : "Работает от батареи") + EstimateText(a)));
        }
        try { await _tray.SyncAsync(wanted); }
        catch (Exception e) { Log.Error("Tray sync", e); }
    }

    void RebuildMenu()
    {
        if (_tray is null) return;
        var items = new List<DbusMenu.Item>();
        var list = Visible.ToList();
        if (list.Count == 0) items.Add(new("Нет подключённых аксессуаров", Enabled: false));
        foreach (var a in list) items.Add(new($"{a.DisplayName} — {Fmt.Percent(a.Level)}{(a.Charging ? " ⚡" : "")}", Enabled: false));
        items.Add(new("", Separator: true));

        void Radio(string label, IconStyle st) => items.Add(new(label, () => { _s.IconStyle = st; Changed(); }, Toggle: "radio", Checked: _s.IconStyle == st));
        void Check(string label, bool value, Action<bool> set) => items.Add(new(label, () => { set(!value); Changed(); }, Toggle: "checkmark", Checked: value));

        Radio("Значок: батарея", IconStyle.Battery);
        Radio("Значок: проценты", IconStyle.Number);
        Radio("Значок: батарея + %", IconStyle.BatteryNumber);
        items.Add(new("", Separator: true));
        Check("Отдельный значок для каждого устройства", _s.TrayMode == TrayMode.PerDevice, v => _s.TrayMode = v ? TrayMode.PerDevice : TrayMode.Single);
        Check("Уведомление при подключении", _s.ShowConnectPopup, v => _s.ShowConnectPopup = v);
        Check("Предупреждать о низком заряде", _s.LowBatteryAlerts, v => _s.LowBatteryAlerts = v);
        Check("Критический заряд (поверх всего)", _s.CriticalOverlay, v => _s.CriticalOverlay = v);
        Check("Сообщать о полной зарядке", _s.FullChargeAlert, v => _s.FullChargeAlert = v);
        Check($"Тихие часы ({_s.QuietFrom:00}:00–{_s.QuietTo:00}:00)", _s.QuietHours, v => _s.QuietHours = v);
        Check("Запускать при входе в систему", LinuxInstaller.AutostartEnabled, v => LinuxInstaller.SetAutostart(v));
        if (AppInfo.UpdatesConfigured) Check("Проверять обновления", _s.CheckUpdates, v => _s.CheckUpdates = v);
        items.Add(new("", Separator: true));
        if (_pending is not null) items.Add(new($"Установить версию {_pending.VersionText}", () => _ = InstallUpdateAsync(_pending)));
        items.Add(new($"Glance {AppInfo.VersionText}", Enabled: false));
        items.Add(new("Выход", _loop.Quit));
        _tray.SetMenu(items);
    }

    void Changed()
    {
        _s.NotifyChanged();
        _traySync.Trigger();
        RebuildMenu();
    }

    // ───────────── Updates ─────────────

    void ScheduleUpdateChecks()
    {
        if (!AppInfo.UpdatesConfigured) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(3));
            while (true)
            {
                if (_s.CheckUpdates && DateTime.UtcNow - _s.LastUpdateCheck > TimeSpan.FromHours(23))
                {
                    try
                    {
                        var r = await _upd.CheckAsync();
                        _loop.Post(() =>
                        {
                            _s.LastUpdateCheck = DateTime.UtcNow;
                            _s.Save();
                            if (r is null) return;
                            _pending = r;
                            RebuildMenu();
                            if (_s.SkippedVersion != r.Tag)
                            {
                                _s.SkippedVersion = r.Tag;
                                _ = _notify?.ShowAsync("update", $"Доступна версия {r.VersionText}", "Установите из меню значка Glance.", Notifier.Urgency.Low, null, null);
                            }
                        });
                    }
                    catch (Exception e) { Log.Info($"Update check: {e.Message}"); }
                }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    async Task InstallUpdateAsync(UpdateService.Release r)
    {
        if (_installing) return;
        _installing = true;
        try
        {
            await _upd.InstallAsync(r);
            _loop.Post(_loop.Quit);
        }
        catch (Exception e)
        {
            Log.Error("Update", e);
            _ = _notify?.ShowAsync("update", "Не удалось обновить", e.Message, Notifier.Urgency.Normal, null, null);
        }
        finally { _installing = false; }
    }

    public void Shutdown()
    {
        _s.SaveNow();
        _h.SaveNow();
    }
}
