using System.Runtime.InteropServices;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Glance;

internal static class Program
{
    static int Main(string[] args)
    {
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);

        if (Has("--help") || Has("-h"))
        {
            Console.WriteLine($"Glance {AppInfo.VersionText} — заряд Bluetooth-аксессуаров в трее\n");
            Console.WriteLine("  glance               установка (первый запуск) или запуск");
            Console.WriteLine("  glance --install     установить / переустановить");
            Console.WriteLine("  glance --uninstall   удалить (--purge — вместе с настройками)");
            Console.WriteLine("  glance --portable    запустить без установки");
            return 0;
        }

        if (Has("--uninstall"))
        {
            LinuxInstaller.Uninstall(Has("--purge"));
            Console.WriteLine("Glance удалён.");
            return 0;
        }

        bool afterUpdate = Has("--after-update");
        bool autostart = Has("--autostart");

        if (Has("--install") || (!Has("--portable") && !afterUpdate && !autostart && !LinuxInstaller.IsInstalledCopy()))
        {
            var exe = LinuxInstaller.RunInteractive();
            if (exe is null) return 1;
            LinuxInstaller.LaunchDetached(exe);
            return 0;
        }

        UpdateService.CleanupAfterUpdate();
        FileStream? lockHandle = null;
        bool locked = false;
        // After a self-update the previous version is still shutting down: wait for its lock.
        for (int i = 0; i < (afterUpdate ? 40 : 1) && !(locked = LinuxInstaller.TryLock(out lockHandle)); i++) Thread.Sleep(250);
        if (!locked)
        {
            Console.Error.WriteLine("Glance уже запущен.");
            return 0;
        }

        using (lockHandle)
        {
            var loop = new MainLoop();
            var host = new LinuxHost(loop);
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; loop.Post(loop.Quit); });
            using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; loop.Post(loop.Quit); });
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Task", e.Exception); e.SetObserved(); };

            loop.Post(() => _ = StartAsync(host, afterUpdate, loop));
            loop.Run();
            host.Shutdown();
        }
        return 0;
    }

    static async Task StartAsync(LinuxHost host, bool afterUpdate, MainLoop loop)
    {
        // Autostart: let the panel and notification daemon come up first.
        if (Environment.GetCommandLineArgs().Contains("--autostart")) await Task.Delay(4000);
        try { await host.StartAsync(afterUpdate); }
        catch (Exception e) { Log.Error("Start", e); loop.Quit(); }
    }
}
