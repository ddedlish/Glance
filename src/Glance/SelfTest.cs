using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;

namespace Glance;

/// <summary>
/// «glance.exe --portable --selftest &lt;dir&gt;» — scripted tour of the real UI on a real Windows desktop (CI VM):
/// demo devices, connect toast, panel, every settings page, widget, both looks (Liquid Glass and Classic).
/// Saves a screenshot per step plus report.txt (timings, errors) and exits: 0 = fine, 1 = an error happened.
/// </summary>
internal sealed partial class AppHost
{
    public int SelfTestExitCode { get; private set; }

    async Task RunSelfTestAsync(string dir)
    {
        var report = new StringBuilder();
        int step = 0, errors = 0;
        void Note(string s) { report.AppendLine(s); Log.Info("Self-test: " + s); }

        void Shot(string name)
        {
            try
            {
                var b = Screen.PrimaryScreen!.Bounds;
                using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                bmp.Save(Path.Combine(dir, $"{++step:00}-{name}.png"), ImageFormat.Png);
            }
            catch (Exception e) { errors++; Note($"Скриншот {name}: {e.Message}"); }
        }

        async Task Do(string name, Action act, int settleMs = 900)
        {
            var sw = Stopwatch.StartNew();
            try { act(); }
            catch (Exception e) { errors++; Note($"ОШИБКА {name}: {e}"); }
            long ms = sw.ElapsedMilliseconds;
            await Task.Delay(settleMs);
            Note($"{name}: {ms} мс");
            Shot(name);
        }

        try
        {
            Note($"Glance {AppInfo.VersionText}, Windows {Environment.OSVersion.Version}, экран {Screen.PrimaryScreen?.Bounds.Size}");
            SeedDemoHistory();
            foreach (var a in DemoDevices()) _r.AddDemo(a);
            SyncTray();

            foreach (bool glass in new[] { true, false })
            {
                string look = glass ? "glass" : "classic";
                _s.LiquidGlass = glass;
                OnSettingsChanged();
                await Task.Delay(400);

                await Do($"{look}-toast", () => _toasts.ShowDevice(_r.Get("DEMO:buds")!, ToastKind.Connected), 1400);
                _toasts.CloseAll();

                await Do($"{look}-panel", () => OpenFlyout(null));
                _flyout?.Close();
                await Task.Delay(400);

                await Do($"{look}-settings", OpenSettings);
                if (_settingsForm is { } sf)
                {
                    foreach (var page in SettingsForm.PageTitles)
                        await Do($"{look}-settings-{Slug(page)}", () => sf.OpenPageForTest(page), 600);
                    sf.Close();
                }
                await Task.Delay(300);

                WidgetForm? w = null;
                await Do($"{look}-widget", () => { w = new WidgetForm(_s); w.ShowWidget(); });
                w?.Close();
            }
        }
        catch (Exception e) { errors++; Note("ОШИБКА самопроверки: " + e); }

        // Errors from the UI itself (missing Bluetooth etc. on a VM are expected and only listed).
        try
        {
            string log = Path.Combine(AppInfo.DataDir, "glance.log");
            if (File.Exists(log))
            {
                var errLines = File.ReadAllLines(log).Where(l => l.Contains(" ERR ")).ToList();
                int ui = errLines.Count(l => l.Contains(" ERR UI") || l.Contains(" ERR Fatal") || l.Contains(" ERR Task") || l.Contains(" ERR Startup") || l.Contains("Exception") && l.Contains("Glance.UI"));
                errors += ui;
                Note($"Ошибок в журнале: {errLines.Count} (из интерфейса: {ui})");
                foreach (var l in errLines.Take(40)) report.AppendLine("  " + l);
                File.Copy(log, Path.Combine(dir, "glance.log"), true);
            }
        }
        catch { }

        Note(errors == 0 ? "ИТОГ: OK" : $"ИТОГ: ошибок {errors}");
        try { File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString(), Encoding.UTF8); } catch { }
        SelfTestExitCode = errors == 0 ? 0 : 1;
        ExitApp();
    }

    /// <summary>
    /// Yesterday's sessions of the demo devices, so the panel and the toast show the forecast learned from
    /// past days («Хватит примерно на 1 ч 48 мин · до 21:40») right after connecting.
    /// </summary>
    void SeedDemoHistory()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), day = now - 26 * 3600;
        void Run(string key, int from, int to, double hours, bool charging)
        {
            int steps = Math.Max(1, Math.Abs(to - from) / 5);
            for (int k = 0; k <= steps; k++)
            {
                byte f = (byte)((charging ? Sample.Charging : 0) | (k == 0 && !charging ? Sample.SessionStart : 0));
                _h.AddRaw(key, new Sample(day + (long)(hours * 3600 * k / steps), (byte)(from + (to - from) * k / steps), f));
            }
        }
        Run("DEMO:mouse", 100, 40, 6, false);   // 10 %/h → 18 % ≈ 1 ч 48 мин
        Run("DEMO:buds", 100, 60, 5, false);    // 8 %/h
        Run("DEMO:pad", 90, 50, 4, false);      // 10 %/h
        Run("DEMO:kbd", 30, 90, 2, true);       // 30 %/h → 87 % → full in ≈ 26 мин
    }

    static string Slug(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    static IEnumerable<Accessory> DemoDevices()
    {
        Accessory Dev(string key, string name, DeviceKind kind, int level, bool charging = false, bool receiver = false, bool wired = false) => new(key)
        {
            Name = name,
            KindHint = kind,
            Profile = Profiles.Match(name, kind),
            Connected = true,
            ConnectedAt = DateTime.Now.AddMinutes(-20),
            LastSeen = DateTime.Now,
            SystemLevel = level,
            SystemCharging = charging,
            SystemRank = 2,
            ViaReceiver = receiver,
            Wired = wired,
            LinkHint = receiver || wired ? null : "Bluetooth",
        };
        var buds = Dev("DEMO:buds", "AirPods Pro", DeviceKind.Earbuds, 64);
        buds.SystemLevel = null;
        buds.Left = new Part(72, false);
        buds.Right = new Part(64, false);
        buds.Case = new Part(41, true);
        buds.PartsAtUtc = DateTime.UtcNow;
        buds.PartsSource = "Demo";
        yield return buds;
        yield return Dev("DEMO:mouse", "Attack Shark G3 Pro", DeviceKind.Mouse, 18, receiver: true);
        yield return Dev("DEMO:kbd", "Keychron K2", DeviceKind.Keyboard, 87, charging: true);
        yield return Dev("DEMO:pad", "Xbox Wireless Controller", DeviceKind.Gamepad, 55);
    }
}
