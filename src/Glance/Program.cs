using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Glance;

internal static class Program
{
    public const string ShowEventName = @"Local\Glance.ShowFlyout";
    const string MutexName = @"Local\Glance.SingleInstance";

    [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(uint flags);

    [STAThread]
    static int Main(string[] args)
    {
        // DLL-planting hardening: the installer is typically started from Downloads,
        // so native libraries are resolved from System32 only.
        try { SetDefaultDllDirectories(0x00000800 /*LOAD_LIBRARY_SEARCH_SYSTEM32*/); } catch { }

        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        string? After(string a)
        {
            int i = Array.FindIndex(args, x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // ── Elevated helper used by the installer for folders that need admin rights ──
        if (After("--copy-to") is string copyDir) return Installer.RunElevatedCopy(copyDir);

        InitUi();

        // ── Author's Pro key generator (needs the private key file, which never ships) ──
        if (Has("--keygen")) { Application.Run(new KeyGenForm()); return 0; }

        // ── Uninstall (Settings → Apps → Glance → Delete) ──
        if (Has("--uninstall"))
        {
            bool removeData = false;
            if (!Has("--quiet"))
            {
                using var f = new UninstallForm();
                if (f.ShowDialog() != DialogResult.OK) return 0;
                removeData = f.RemoveData;
            }
            Installer.Uninstall(removeData);
            return 0;
        }

        bool afterUpdate = Has("--after-update");
        bool autostart = Has("--autostart");

        // ── First run from Downloads: simple installer (portable use: --portable or portable.txt next to the exe) ──
        if (!Has("--portable") && !afterUpdate && !autostart && !Installer.IsInstalledCopy())
        {
            using var setup = new SetupForm();
            setup.ShowDialog(); // installs and launches the installed copy
            return 0;
        }

        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; }

        if (!owned)
        {
            if (!afterUpdate)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                    Native.AllowSetForegroundWindow(-1);
                    ev.Set();
                }
                catch { }
            }
            return 0;
        }

        try
        {
            UpdateService.CleanupAfterUpdate();
            using var host = new AppHost(autostart, afterUpdate);
            Application.Run(host);
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("Startup", e);
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    static void InitUi()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Task", e.Exception); e.SetObserved(); };
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Ui.Init();
    }
}
