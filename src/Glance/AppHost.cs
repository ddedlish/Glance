namespace Glance;

/// <summary>
/// Composition root: wires providers → registry → tray / popups / alerts.
/// Everything here runs on the UI thread.
/// </summary>
internal sealed class AppHost : ApplicationContext
{
    readonly AppSettings _s;
    readonly HistoryStore _h;
    readonly DeviceRegistry _r;
    readonly TrayHost _tray;
    readonly BluetoothWatcher _bt = new();
    readonly BudsScanner _scanner = new();
    readonly BudsMatcher _matcher = new();
    readonly GamepadProvider? _pads;
    readonly BatteryPoller _poller;
    readonly ToastManager _toasts = new();
    readonly UpdateService _upd = new();

    readonly Dictionary<string, GattBattery> _gatt = new(StringComparer.Ordinal);
    readonly Dictionary<string, TrayIcon> _icons = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _iconSig = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _lastPopup = new(StringComparer.Ordinal);
    System.Windows.Forms.Timer? _chargeTimer;
    int _chargePhase;
    readonly HashSet<string> _lowAlerted = new(StringComparer.Ordinal), _fullAlerted = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _criticalShownAt = new(StringComparer.Ordinal);
    CriticalOverlay? _overlay;

    TrayIcon? _appIcon;
    FlyoutForm? _flyout;
    HoverCard? _hover;
    SettingsForm? _settingsForm;
    long _flyoutClosedAt;
    bool _exiting, _installing;
    UpdateService.Release? _pending;
    EventWaitHandle? _showEvent, _exitEvent;
    RegisteredWaitHandle? _showWait, _exitWait;
    System.Threading.Timer? _updateTimer;

    const string SummaryKey = "__summary", AppKey = "__app";

    public AppHost(bool autostart, bool afterUpdate)
    {
        _s = AppSettings.Load();
        Pro.Apply(_s.LicenseKey);
        Pro.Changed += () => Ui.Post(() => { Theme.SetAccent(Pro.IsActive ? _s.AccentPreset : 0); SyncWidget(); _flyout?.Invalidate(); });
        Theme.SetAccent(Pro.IsActive ? _s.AccentPreset : 0);
        Theme.SetGlass(_s.LiquidGlass);
        _h = new HistoryStore();
        _r = new DeviceRegistry(_s, _h);
        Svc.Init(_s, _h, _r);
        _tray = new TrayHost();

        try { _pads = new GamepadProvider(); }
        catch (Exception e) { Log.Info($"Gamepad API unavailable: {e.Message}"); }
        _poller = new BatteryPoller(_pads);

        Wire();
        _r.LoadKnown();
        SyncTray();
        _tray.SetHotkey(_s.Hotkey);
        Autostart.Repair();
        Installer.RefreshRegistration();

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Ui.Post(() => OpenFlyout(null)), null, Timeout.Infinite, false);
        }
        catch (Exception e) { Log.Error("Show event", e); }
        try
        {
            // The installer / uninstaller asks us to quit before replacing or removing the exe.
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ExitEventName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Ui.Post(ExitApp), null, Timeout.Infinite, true);
        }
        catch (Exception e) { Log.Error("Exit event", e); }

        // On autostart give the system a few seconds to settle (less contention at logon).
        Ui.Delay(autostart ? 5000 : 10, StartMonitoring);
        if (afterUpdate)
            Ui.Delay(1500, () =>
            {
                string what = string.IsNullOrWhiteSpace(_s.UpdateNotes) ? "Настройки и история сохранены." : _s.UpdateNotes!;
                _s.UpdateNotes = null;
                _s.Save();
                _toasts.ShowMessage($"Glance обновлён до {AppInfo.VersionText}", what + " Нажмите — что нового.", Glyphs.Check, WhatsNewForm.ShowWindow);
            });

        ScheduleUpdateChecks();
        Ui.Delay(1500, SyncWidget);
        Ui.Delay(30_000, TrimMemory);
        Log.Info($"Started {AppInfo.VersionText}");
    }

    void Wire()
    {
        _bt.Endpoint += (ep, initial) => _r.OnEndpoint(ep, initial);
        _bt.EndpointRemoved += id => _r.OnEndpointRemoved(id);
        _bt.EnumerationCompleted += () => { _r.PruneUnpaired(); RefreshPolling(); };

        _r.DeviceConnected += OnConnected;
        _r.DeviceDisconnected += OnDisconnected;
        _r.Changed += OnChanged;
        _r.ListChanged += () => { SyncTray(); _flyout?.Rebuild(); _widget?.Refresh(true); };

        _poller.Result += (pnp, pads, rx) =>
        {
            _r.ApplyPnp(pnp);
            _r.ApplyGamepads(pads);
            _r.ApplyReceivers(rx);
            foreach (var g in _gatt.Values) if (g.NeedsPolling) _ = g.ReadAsync();
        };
        if (_pads is not null) _pads.Changed += () => { _poller.ReadSoon(300); Ui.Post(RefreshPolling); };

        _scanner.Seen += OnAdvert;

        _tray.ThemeChanged += Theme.Refresh;
        Theme.Changed += () =>
        {
            _iconSig.Clear();
            SyncTray();
            _toasts.ApplyTheme();
            _flyout?.ApplyTheme();
        };
        _tray.TaskbarRecreated += () => { _iconSig.Clear(); SyncTray(); };
        _tray.DisplayChanged += () => { _iconSig.Clear(); SyncTray(); };
        _tray.DevicesChanged += () => _poller.ReadSoon(1500, 6000); // receiver plugged / unplugged
        _tray.Resumed += () => Ui.Delay(4000, () => { _bt.Start(); _poller.ReadSoon(500, 5000); });
        _tray.HotkeyPressed += () => { if (_flyout is not null) _flyout.CloseAnimated(); else OpenFlyout(null); };

        _toasts.DeviceClicked = _ => OpenFlyout(null);
        _s.Changed += OnSettingsChanged;
    }

    void StartMonitoring()
    {
        _bt.Start();
        RefreshPolling();
        _poller.ReadSoon(1500);
    }

    void RefreshPolling()
    {
        bool any = _r.ConnectedDevices.Any() || (_pads?.Any ?? false);
        _poller.SetEnabled(any, _s.PollSeconds);
    }

    // ───────────────────────────── Device lifecycle ─────────────────────────────

    void OnConnected(Accessory a, bool initial)
    {
        RefreshPolling();
        _poller.ReadSoon(300, 3000, 10_000);
        _ = ResolveAndStartAsync(a);
        UpdateBudsTargets(a);
        if (!initial && _s.ShowConnectPopup) SchedulePopup(a);
        SyncTray();
    }

    void OnDisconnected(Accessory a)
    {
        if (_gatt.Remove(a.Key, out var g)) g.Dispose();
        _matcher.Forget(a.Key);
        if (_s.ShowConnectPopup && !a.Wired) Sounds.Play(SoundKind.Disconnected, _s);
        UpdateBudsTargets(null);
        RefreshPolling();
        SyncTray();
        _flyout?.Rebuild();
        _toasts.Refresh(a);
    }

    async Task ResolveAndStartAsync(Accessory a)
    {
        try
        {
            if (a.KindHint == DeviceKind.Unknown)
            {
                var k = await KindResolver.ResolveAsync(a);
                if (k != DeviceKind.Unknown && a.Connected)
                {
                    _r.SetKindHint(a, k);
                    UpdateBudsTargets(a);
                }
            }
            if (a.LeAepId is string id && a.Connected && !_gatt.ContainsKey(a.Key))
            {
                var g = new GattBattery(id);
                _gatt[a.Key] = g;
                g.Levels += lv => _r.ApplyGatt(a, lv);
                await g.StartAsync();
            }
        }
        catch (Exception e) { Log.Error("Resolve", e); }
    }

    void UpdateBudsTargets(Accessory? justConnected)
    {
        bool apple = false, fp = false;
        if (_s.BudsDetail)
        {
            foreach (var a in _r.ConnectedDevices)
            {
                if (a.Profile.Buds == BudsSource.AppleContinuity) apple = true;
                else if (a.Profile.Buds == BudsSource.FastPair) fp = true;
            }
        }
        _scanner.SetTargets(apple, fp);

        if (justConnected is { Connected: true } j && _s.BudsDetail && j.Profile.Buds != BudsSource.None && !j.HasParts)
        {
            _scanner.Kick();
            _r.SetPending(j, true);
            var since = j.ConnectedAt;
            Ui.Delay(30_000, () => { if (j.ConnectedAt == since && j.BudsPending) _r.SetPending(j, false); });
        }
    }

    void OnAdvert(BudsAdvert adv)
    {
        var targets = _r.ConnectedDevices.Where(a => a.Profile.Buds != BudsSource.None).ToList();
        if (targets.Count == 0) return;
        foreach (var (t, x) in _matcher.Match(adv, targets, _s.RssiThreshold))
            _r.ApplyBuds(t, x);
        if (targets.All(t => t.HasParts && (DateTime.UtcNow - t.PartsAtUtc).TotalSeconds < 10))
            _scanner.Satisfied();
    }

    void OnChanged(Accessory a)
    {
        if (a.Connected && a.Level is int lv)
        {
            _h.Add(a.Key, lv, a.Charging, a.SessionStartPending);
            a.SessionStartPending = false;
        }
        CheckAlerts(a);
        _widget?.Refresh(false);
        SyncTray();
        _flyout?.Invalidate();
        _hover?.RefreshDevice(a);
        _toasts.Refresh(a);
    }

    // ───────────────────────────── Popups & alerts ─────────────────────────────

    void SchedulePopup(Accessory a)
    {
        // Every connection gets its window; only a reconnect within a few seconds (radio flapping) is merged.
        if (_lastPopup.TryGetValue(a.Key, out var last) && (DateTime.Now - last).TotalSeconds < 8) return;
        var since = a.ConnectedAt;
        Ui.Delay(1200, () => TryPopup(a, since, 0));
    }

    void TryPopup(Accessory a, DateTime since, int attempt)
    {
        if (!a.Connected || a.ConnectedAt != since) return;
        if (Pro.IsActive && _s.For(a.Key).Muted) return; // Pro rule: this device stays silent
        bool hasData = a.Level is not null;
        if (!hasData && attempt < 3)
        {
            Ui.Delay(1200, () => TryPopup(a, since, attempt + 1));
            return;
        }
        // Every connection is announced (USB, 2.4 GHz and Bluetooth alike, with or without battery data).
        // The window is silent and never takes focus, so quiet hours don't hide it; only fullscreen games do.
        if (_s.RespectFocus && Native.UserIsBusy()) return;
        _lastPopup[a.Key] = DateTime.Now;
        _toasts.ShowDevice(a, ToastKind.Connected);
        Sounds.Play(SoundKind.Connected, _s);
    }

    void CheckAlerts(Accessory a)
    {
        if (!a.Connected) return;
        int? weakest = a.WeakestLevel;
        if (weakest is not int lv) return;
        bool charging = a.Charging;

        var rules = _s.For(a.Key);
        bool muted = Pro.IsActive && rules.Muted;
        int lowAt = Pro.IsActive && rules.LowThreshold is int own ? own : _s.LowBatteryThreshold;
        if (_s.LowBatteryAlerts && !muted)
        {
            if (!charging && lv <= lowAt)
            {
                if (!_lowAlerted.Contains(a.Key) && !(_s.RespectFocus && Native.UserIsBusy()) && !_s.IsQuietNow())
                {
                    _lowAlerted.Add(a.Key);
                    _toasts.ShowDevice(a, ToastKind.Low);
                    Sounds.Play(SoundKind.Low, _s);
                }
            }
            else if (charging || lv >= lowAt + 5) _lowAlerted.Remove(a.Key);
        }

        CheckCritical(a, lv, charging);

        if (_s.FullChargeAlert && !muted)
        {
            bool full = a.HasParts && !a.IsSplitDevice
                ? a.Left is { Level: >= 100, Charging: true } && a.Right is { Level: >= 100, Charging: true }
                : a.SystemCharging && a.Level >= 100;
            if (full)
            {
                if (_fullAlerted.Add(a.Key) && !(_s.RespectFocus && Native.UserIsBusy()) && !_s.IsQuietNow())
                {
                    _toasts.ShowDevice(a, ToastKind.Charged);
                    Sounds.Play(SoundKind.Charged, _s);
                }
            }
            else if (!charging) _fullAlerted.Remove(a.Key);
        }
    }

    /// <summary>
    /// Critical level: click-through banner on top of games/fullscreen video, never takes focus.
    /// Shown at the threshold and again for every further 5 % drop; reset by charging.
    /// </summary>
    void CheckCritical(Accessory a, int level, bool charging)
    {
        if (charging || level > _s.CriticalThreshold + 3)
        {
            _criticalShownAt.Remove(a.Key);
            return;
        }
        if (!_s.CriticalOverlay || level > _s.CriticalThreshold) return;
        if (_criticalShownAt.TryGetValue(a.Key, out var shown) && level > shown - 5) return;
        _criticalShownAt[a.Key] = level;
        ShowCritical(a);
    }

    void ShowCritical(Accessory a)
    {
        _overlay?.FadeOut();
        var o = new CriticalOverlay(a);
        o.FormClosed += (_, _) => { if (_overlay == o) _overlay = null; };
        _overlay = o;
        o.Present();
        if (_s.CriticalSound) Sounds.Play(SoundKind.Critical, _s, preview: !_s.Sounds); // critical plays even with other sounds off
    }

    // ───────────────────────────── Tray ─────────────────────────────

    void SyncTray()
    {
        if (_exiting) return;
        var visible = _r.ConnectedDevices
            .Where(a => (a.Level is not null || a.HasParts || (_s.ShowUnknownInTray && IsAccessoryKind(a.Kind) && !a.Wired)) && !_s.For(a.Key).HideInTray)
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var want = new List<(string Key, Accessory A)>();
        if (_s.TrayMode == TrayMode.PerDevice) want.AddRange(visible.Select(a => (a.Key, a)));
        else if (visible.Count > 0) want.Add((SummaryKey, visible.MinBy(a => a.WeakestLevel ?? 101)!));

        foreach (var key in _icons.Keys.Where(k => want.All(w => w.Key != k)).ToList())
        {
            _icons[key].Dispose();
            _icons.Remove(key);
            _iconSig.Remove(key);
        }

        bool dark = Theme.Current.TaskbarDark;
        int size = IconRenderer.Size;
        foreach (var (key, a) in want)
        {
            if (!_icons.TryGetValue(key, out var icon))
            {
                icon = _tray.CreateIcon(key);
                WireIcon(icon);
                _icons[key] = icon;
            }
            string tip = key == SummaryKey ? string.Join("\n", visible.Take(4).Select(Svc.Tooltip)) : Svc.Tooltip(a);
            if (_pending is not null) tip = UpdateTip + "\n" + tip; // first line: survives the 128-char tooltip limit
            int phase = a.Charging ? _chargePhase : -1;
            string sig = $"{a.WeakestLevel}|{a.Charging}|{phase}|{tip}|{_s.IconStyle}|{a.Kind}|{_s.RichHover}|{_s.LowBatteryThreshold}|{dark}|{size}";
            if (_iconSig.TryGetValue(key, out var old) && old == sig) continue;
            icon.Set(IconRenderer.Device(a.WeakestLevel, a.Charging, _s.IconStyle, _s.LowBatteryThreshold, a.Kind, phase), tip, _s.RichHover);
            _iconSig[key] = sig;
        }

        // Animated tray icon while something is charging (one cheap redraw every 0.7 s, stops when done).
        bool anyCharging = want.Any(w => w.A.Charging);
        if (anyCharging && _chargeTimer is null)
        {
            _chargeTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _chargeTimer.Tick += (_, _) => { _chargePhase = (_chargePhase + 1) % 3; SyncTray(); };
        }
        if (_chargeTimer is not null && _chargeTimer.Enabled != anyCharging)
        {
            if (anyCharging) _chargeTimer.Start(); else { _chargeTimer.Stop(); _chargePhase = 0; }
        }

        if (want.Count == 0)
        {
            if (_appIcon is null)
            {
                _appIcon = _tray.CreateIcon(AppKey);
                WireIcon(_appIcon);
                _iconSig.Remove(AppKey);
            }
            string sig = $"app|{dark}|{size}|{_s.RichHover}";
            if (!_iconSig.TryGetValue(AppKey, out var old) || old != sig)
            {
                _appIcon.Set(IconRenderer.App(), "Glance — нет подключённых аксессуаров", _s.RichHover);
                _iconSig[AppKey] = sig;
            }
        }
        else if (_appIcon is not null)
        {
            _appIcon.Dispose();
            _appIcon = null;
            _iconSig.Remove(AppKey);
        }
    }

    static bool IsAccessoryKind(DeviceKind k) => k is DeviceKind.Mouse or DeviceKind.Keyboard or DeviceKind.Gamepad or DeviceKind.Headset
        or DeviceKind.Headphones or DeviceKind.Earbuds or DeviceKind.Neckband or DeviceKind.Speaker or DeviceKind.Pen;

    void WireIcon(TrayIcon icon)
    {
        icon.Clicked += _ =>
        {
            if (_flyout is not null) { _flyout.CloseAnimated(); return; }
            OpenFlyout(icon);
        };
        icon.MenuRequested += pt => ShowTrayMenu(icon, pt);
        icon.HoverOpened += () => ShowHover(icon);
        icon.HoverClosed += HideHover;
    }

    void ShowHover(TrayIcon icon)
    {
        if (!_s.RichHover || _flyout is not null) return;
        HideHover();
        var list = icon.Key is SummaryKey or AppKey
            ? _r.ConnectedDevices.Where(a => a.Level is not null || a.HasParts || IsAccessoryKind(a.Kind)).OrderBy(a => a.DisplayName).Take(4).ToList()
            : _r.Get(icon.Key) is Accessory a ? new List<Accessory> { a } : new List<Accessory>();
        _hover = new HoverCard(list, icon.GetRect());
        var h = _hover;
        h.FormClosed += (_, _) => { if (_hover == h) _hover = null; };
        if (h.IsDisposed || list.Count == 0) { if (_hover == h) _hover = null; return; }
        try { h.ShowNear(); }
        catch (ObjectDisposedException) { if (_hover == h) _hover = null; return; }
        if (list.Any(x => x.Profile.Buds != BudsSource.None)) _scanner.Kick();
    }

    void HideHover()
    {
        var h = _hover;
        _hover = null;
        if (h is { IsDisposed: false }) h.Close();
    }

    void OpenFlyout(TrayIcon? anchor)
    {
        if (_flyout is not null) { _flyout.Activate(); return; }
        if (Environment.TickCount64 - _flyoutClosedAt < 300) return; // the click that just closed it
        HideHover();
        var f = new FlyoutForm { PendingUpdate = _pending?.VersionText };
        f.SettingsRequested += OpenSettings;
        f.UpdateRequested += () => { if (_pending is not null) _ = InstallUpdateAsync(_pending); };
        f.CheckUpdateRequested += async () =>
        {
            var msg = await CheckUpdatesAsync(manual: true);
            if (_pending is null) _toasts.ShowMessage("Обновлений нет", msg, Glyphs.Check);
        };
        f.DeviceMenuRequested += (a, pt) => ShowDeviceMenu(a, pt, f);
        f.FormClosed += (_, _) =>
        {
            if (_flyout == f) _flyout = null;
            _flyoutClosedAt = Environment.TickCount64;
            _scanner.SetLive(false);
            Ui.Delay(10_000, TrimMemory);
        };
        _flyout = f;
        _scanner.SetLive(true);
        f.ShowNear((anchor ?? _icons.Values.FirstOrDefault() ?? _appIcon)?.GetRect());
    }

    void ShowTrayMenu(TrayIcon icon, Point pt)
    {
        HideHover();
        var m = Menus.Create();
        if (_r.Get(icon.Key) is Accessory a)
        {
            m.Header($"{a.DisplayName} — {Fmt.Percent(a.Level)}");
            m.Item("Переименовать…", () => Ui.Post(() => Rename(a)));
            m.Item("Скрыть из трея", () => { _s.For(a.Key).HideInTray = true; _s.Save(); SyncTray(); });
            m.Separator();
        }
        m.Item("Открыть панель", () => OpenFlyout(icon));
        if (_pending is not null) m.Item($"Установить версию {_pending.VersionText}", () => _ = InstallUpdateAsync(_pending));
        var hidden = _r.All.Where(x => _s.For(x.Key).HideInTray && x.Connected).ToList();
        foreach (var x in hidden) m.Item($"Показать в трее: {x.DisplayName}", () => { _s.For(x.Key).HideInTray = false; _s.Save(); SyncTray(); });
        var w = m.Item("Виджет на рабочем столе" + (Pro.IsActive ? "" : " · Pro"), () =>
        {
            if (!Pro.IsActive) { ProUi.Prompt(_s, "Виджет на рабочем столе"); return; }
            _s.Widget = !_s.Widget; _s.Save(); SyncWidget();
        });
        w.Checked = Pro.IsActive && _s.Widget;
        if (!Pro.IsActive) m.Item("Glance Pro…", () => Ui.Post(() => ProUi.Prompt(_s)));
        m.Item("Bluetooth", FlyoutForm.OpenBluetoothSettings);
        m.Item("Что нового", () => Ui.Post(WhatsNewForm.ShowWindow));
        m.Item("Настройки Glance", OpenSettings);
        m.Separator();
        m.Item("Выход", ExitApp);
        Native.SetForegroundWindow(_tray.Handle);
        m.Show(pt, ToolStripDropDownDirection.AboveLeft);
    }

    void ShowDeviceMenu(Accessory a, Point screenPt, FlyoutForm owner)
    {
        var m = Menus.Create();
        var ds = _s.For(a.Key);
        m.Item("Переименовать…", () => Ui.Post(() => Rename(a))); // after the menu has closed
        if (ds.HideInTray) m.Item("Показывать в трее", () => { ds.HideInTray = false; _s.Save(); SyncTray(); });
        else m.Item("Скрыть из трея", () => { ds.HideInTray = true; _s.Save(); SyncTray(); });
        if (!a.Connected) m.Item("Убрать из списка", () => _r.Forget(a));
        m.Separator();
        m.Header(Pro.IsActive ? "Правила устройства" : "Правила устройства · Pro");
        string th = ds.LowThreshold is int own ? $"{own}%" : $"как везде ({_s.LowBatteryThreshold}%)";
        m.Item($"Порог низкого заряда: {th}", () =>
        {
            if (!Pro.IsActive) { Ui.Post(() => ProUi.Prompt(_s, "Правила устройства")); return; }
            int?[] steps = { null, 10, 20, 30, 40 };
            int idx = Array.IndexOf(steps, ds.LowThreshold);
            ds.LowThreshold = steps[(idx + 1) % steps.Length];
            _lowAlerted.Remove(a.Key);
            _s.Save();
        });
        var mute = m.Item("Без уведомлений", () =>
        {
            if (!Pro.IsActive) { Ui.Post(() => ProUi.Prompt(_s, "Правила устройства")); return; }
            ds.Muted = !ds.Muted; _s.Save();
        });
        mute.Checked = Pro.IsActive && ds.Muted;
        m.Item("История и износ…", () => Ui.Post(() => OpenHistory(a)));
        m.Separator();
        m.Item("Bluetooth", FlyoutForm.OpenBluetoothSettings);
        owner.MenuOpen = true;
        m.Closed += (_, _) =>
        {
            owner.MenuOpen = false;
            Ui.Delay(50, () => { if (!owner.IsDisposed && Form.ActiveForm != owner) owner.CloseAnimated(); });
        };
        m.Show(screenPt, ToolStripDropDownDirection.BelowLeft);
    }

    bool _renaming;

    void Rename(Accessory a) => Rename(a, 0);

    void Rename(Accessory a, int attempt)
    {
        if (_renaming) return;
        _renaming = true;
        try
        {
            if (_flyout is { IsDisposed: false } fl && attempt < 4)
            {
                // let the panel finish closing first, then open the dialog on its own
                fl.CloseAnimated();
                Ui.Delay(260, () => Rename(a, attempt + 1));
                return;
            }
            var name = InputForm.Ask("Переименовать", $"Имя для «{a.Name}»", a.DisplayName);
            if (name is null) return;
            _s.For(a.Key).CustomName = name.Length == 0 || name == a.Name ? null : name;
            _s.Save();
            _r.Touch(a);
            _iconSig.Clear();
            SyncTray();
        }
        catch (Exception e) { Log.Error("Rename", e); }
        finally { _renaming = false; }
    }

    void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        var f = new SettingsForm(_s) { PendingUpdate = () => _pending?.VersionText };
        f.CheckUpdatesRequested += () => CheckUpdatesAsync(manual: true);
        f.InstallUpdateRequested += () => { if (_pending is not null) _ = InstallUpdateAsync(_pending); };
        f.HistoryRequested += () => OpenHistory(null);
        f.FormClosed += (_, _) => { _settingsForm = null; Ui.Delay(10_000, TrimMemory); };
        _settingsForm = f;
        f.Show();
        f.Activate();
    }

    void OnSettingsChanged()
    {
        Theme.SetAccent(Pro.IsActive ? _s.AccentPreset : 0);
        Theme.SetGlass(_s.LiquidGlass);
        SyncWidget();
        _tray.SetHotkey(_s.Hotkey);
        RefreshPolling();
        UpdateBudsTargets(null);
        _iconSig.Clear();
        SyncTray();
        ScheduleUpdateChecks();
    }

    // ───────────────────────────── Updates ─────────────────────────────

    void ScheduleUpdateChecks()
    {
        _updateTimer?.Dispose();
        _updateTimer = null;
        if (!_s.CheckUpdates || !AppInfo.UpdatesConfigured) return;
        // 15 s after start, then every 30 minutes (2 GitHub requests an hour — far below its limit).
        _updateTimer = new System.Threading.Timer(_ => Ui.Post(() => _ = CheckUpdatesAsync(false)), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30));
    }

    async Task<string> CheckUpdatesAsync(bool manual)
    {
        try
        {
            var r = await _upd.CheckAsync();
            _s.LastUpdateCheck = DateTime.UtcNow;
            _s.Save();
            if (r is null) return $"Установлена последняя версия ({AppInfo.VersionText})";
            bool isNew = _pending?.Tag != r.Tag;
            _pending = r;
            if (_flyout is not null) { _flyout.PendingUpdate = r.VersionText; _flyout.Rebuild(); }
            if (isNew) { _iconSig.Clear(); SyncTray(); } // tooltip line «Доступно обновление»
            if (!manual && _s.AutoInstallUpdates)
            {
                // Visible even in auto mode: a notification now, «что нового» after the restart.
                _toasts.ShowMessage($"Найдена версия {r.VersionText}", "Устанавливаем — Glance перезапустится через несколько секунд.", Glyphs.Download);
                await Task.Delay(4000);
                await InstallUpdateAsync(r, silent: true);
                return "";
            }
            // Remind: at once for a new version, then at most once a day until it is installed.
            if (manual || _s.SkippedVersion != r.Tag || DateTime.UtcNow - _s.UpdateRemindedAt > TimeSpan.FromHours(20))
            {
                _s.SkippedVersion = r.Tag;
                _s.UpdateRemindedAt = DateTime.UtcNow;
                _s.Save();
                Sounds.Play(SoundKind.Update, _s);
                _toasts.ShowAction($"Доступна версия {r.VersionText}", UpdateSummary(r), Glyphs.Download, "Обновить",
                    () => _ = InstallUpdateAsync(r));
            }
            return $"Доступна версия {r.VersionText}";
        }
        catch (Exception e)
        {
            Log.Error("Update check", e);
            return "Не удалось проверить: " + e.Message;
        }
    }

    /// <summary>First item of the release notes, so the notification says what is new.</summary>
    static string UpdateSummary(UpdateService.Release r)
    {
        var line = (r.Notes ?? "").Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("**", StringComparison.Ordinal));
        string tail = "Настройки и история сохранятся.";
        if (string.IsNullOrEmpty(line)) return "Скачается только изменившаяся часть. " + tail;
        if (line.Length > 110) line = line[..107].TrimEnd() + "…";
        return line + " " + tail;
    }

    string UpdateTip => $"⬆ Доступно обновление {_pending?.VersionText}";

    async Task InstallUpdateAsync(UpdateService.Release r, bool silent = false)
    {
        if (_installing) return;
        _installing = true;
        try
        {
            if (!silent) _toasts.ShowMessage($"Обновление до {r.VersionText}", "Загружаем и проверяем файл…", Glyphs.Sync);
            _s.UpdateNotes = UpdateSummary(r).Replace("Настройки и история сохранятся.", "").Replace("Скачается только изменившаяся часть.", "").Trim();
            _s.Save();
            await _upd.InstallAsync(r);
            ExitApp();
        }
        catch (Exception e)
        {
            Log.Error("Update install", e);
            _toasts.ShowMessage("Не удалось обновить", e.Message, Glyphs.Warning);
        }
        finally { _installing = false; }
    }

    // ───────────────────────────── Housekeeping ─────────────────────────────

    void TrimMemory()
    {
        if (_flyout is not null || _hover is not null || _settingsForm is not null || _overlay is not null || _toasts.Visible) return;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        Native.TrimWorkingSet();
    }

    // ───────────────────────────── Pro ─────────────────────────────

    WidgetForm? _widget;

    void SyncWidget()
    {
        if (_exiting) return;
        bool want = Pro.IsActive && _s.Widget;
        if (want && _widget is null)
        {
            _widget = new WidgetForm(_s);
            _widget.OpenPanel += () => OpenFlyout(null);
            _widget.FormClosed += (_, _) => _widget = null;
            _widget.ShowWidget();
        }
        else if (!want && _widget is not null) { _widget.Close(); _widget = null; }
        else _widget?.ApplyTheme();
    }

    void OpenHistory(Accessory? a)
    {
        _flyout?.CloseAnimated();
        if (!Pro.IsActive) { ProUi.Prompt(_s, "История и износ"); return; }
        new HistoryForm(a).ShowCentered();
    }

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            try
            {
                _r.PersistAll();
                _s.SaveNow();
                _h.SaveNow();
            }
            catch (Exception e) { Log.Error("Save on exit", e); }

            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            _exitWait?.Unregister(null);
            _exitEvent?.Dispose();
            _updateTimer?.Dispose();
            _toasts.CloseAll();
            _overlay?.Close();
            _flyout?.Close();
            _hover?.Close();
            _settingsForm?.Close();
            foreach (var g in _gatt.Values) g.Dispose();
            _gatt.Clear();
            _scanner.Dispose();
            _bt.Dispose();
            _poller.Dispose();
            _pads?.Dispose();
            foreach (var i in _icons.Values.ToList()) i.Dispose();
            _icons.Clear();
            _appIcon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
