using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Glance;

public sealed record InstallOptions(string Directory, bool Autostart, bool DesktopShortcut, bool StartMenuShortcut);

/// <summary>
/// The single Glance.exe is its own installer: run from Downloads it installs itself
/// (per-user, any drive, no admin unless the chosen folder needs it), registers in
/// "Apps & features", creates shortcuts, and can uninstall itself cleanly.
/// Never deletes anything it did not create; never deletes folders recursively.
/// </summary>
internal static class Installer
{
    public const string ExitEventName = @"Local\Glance.Exit";
    const string MutexName = @"Local\Glance.SingleInstance";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Glance";
    const string ExeName = "Glance.exe";

    // Characters that are legal in Windows paths but unsafe for cmd.exe – refused to keep uninstall safe.
    static readonly char[] UnsafePathChars = { '%', '^', '&', '|', '<', '>', '!', '"', ';' };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glance");

    public static string? InstalledDirectory
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return k?.GetValue("InstallLocation") as string is { Length: > 0 } s ? s : null;
            }
            catch { return null; }
        }
    }

    public static string? InstalledVersion
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return k?.GetValue("DisplayVersion") as string;
            }
            catch { return null; }
        }
    }

    /// <summary>True when this exe is the installed copy (or a portable copy marked with portable.txt).</summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            string here = Norm(Path.GetDirectoryName(AppInfo.ExePath) ?? "");
            if (File.Exists(Path.Combine(here, "portable.txt"))) return true;
            var inst = InstalledDirectory;
            return inst is not null && string.Equals(Norm(inst), here, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    /// <summary>Normalises a user-chosen folder: always ends with "\Glance", must be a safe local path.</summary>
    public static string NormalizeTarget(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Укажите папку для установки");
        string p = Environment.ExpandEnvironmentVariables(input.Trim());
        if (!Path.IsPathFullyQualified(p)) throw new ArgumentException("Укажите полный путь, например D:\\Glance");
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Сетевые папки не поддерживаются");
        p = Norm(p);
        if (!string.Equals(Path.GetFileName(p), "Glance", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(p, "Glance");
        if (p.IndexOfAny(UnsafePathChars) >= 0) throw new ArgumentException("Путь содержит недопустимые символы (% ^ & | < > ! ; \")");
        if (p.Length > 200) throw new ArgumentException("Слишком длинный путь");
        string win = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (p.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Нельзя устанавливать в папку Windows");
        return p;
    }

    public static long ExeSize
    {
        get { try { return new FileInfo(AppInfo.ExePath).Length; } catch { return 0; } }
    }

    // ───────────────────────────── Install ─────────────────────────────

    public static async Task<string> InstallAsync(InstallOptions o, IProgress<(int Percent, string Step)> progress)
    {
        string dir = NormalizeTarget(o.Directory);
        string target = Path.Combine(dir, ExeName);
        string? previous = InstalledDirectory;

        progress.Report((5, "Закрываем запущенную версию…"));
        await Task.Run(StopRunningInstance);

        progress.Report((20, "Копируем файлы…"));
        await Task.Run(() =>
        {
            try { CopySelfTo(dir); }
            catch (UnauthorizedAccessException) { ElevatedCopy(dir); }
            catch (IOException e) when (e.HResult == unchecked((int)0x80070005)) { ElevatedCopy(dir); }
        });

        progress.Report((60, "Регистрируем в «Установленных приложениях»…"));
        Register(dir, target);

        progress.Report((75, "Создаём ярлыки…"));
        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glance.lnk");
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Glance.lnk");
        if (o.StartMenuShortcut) CreateShortcut(startMenu, target, dir); else TryDeleteFile(startMenu);
        if (o.DesktopShortcut) CreateShortcut(desktop, target, dir); else TryDeleteFile(desktop);

        progress.Report((88, "Настраиваем автозапуск…"));
        Autostart.Set(o.Autostart, target);

        // Moved to a new location: remove the old executable (only our own file, folder only if empty).
        if (previous is not null && !string.Equals(Norm(previous), dir, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(Path.Combine(previous, ExeName));
            TryDeleteFile(Path.Combine(previous, ExeName + ".old"));
            TryDeleteEmptyDir(previous);
        }

        progress.Report((100, "Готово"));
        Log.Info($"Installed {AppInfo.VersionText} to {dir}");
        return target;
    }

    static void CopySelfTo(string dir)
    {
        Directory.CreateDirectory(dir);
        string src = AppInfo.ExePath;
        string dst = Path.Combine(dir, ExeName);
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
        string tmp = dst + ".tmp";
        File.Copy(src, tmp, true);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, dst, true); break; }
            catch (IOException) when (i < 10) { Thread.Sleep(300); } // previous instance still releasing the file
        }
    }

    /// <summary>Copies into a folder that needs admin rights through a UAC-elevated copy of ourselves.</summary>
    static void ElevatedCopy(string dir)
    {
        var psi = new ProcessStartInfo(AppInfo.ExePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--copy-to");
        psi.ArgumentList.Add(dir);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить копирование");
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("Не удалось скопировать файлы в выбранную папку");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Для этой папки нужны права администратора. Выберите другую папку или подтвердите запрос.");
        }
    }

    /// <summary>Entry point of the elevated helper (--copy-to). Returns the process exit code.</summary>
    public static int RunElevatedCopy(string dir)
    {
        try
        {
            CopySelfTo(NormalizeTarget(dir));
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("Elevated copy", e);
            return 1;
        }
    }

    static void Register(string dir, string exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Glance");
        k.SetValue("DisplayVersion", AppInfo.VersionText);
        k.SetValue("Publisher", AppInfo.Author);
        k.SetValue("Contact", AppInfo.Author);
        k.SetValue("URLInfoAbout", AppInfo.AuthorUrl);                                  // «Подробнее» in Apps & features
        k.SetValue("HelpLink", AppInfo.RepoUrl is string repo ? repo + "/issues" : AppInfo.AuthorUrl);
        k.SetValue("Comments", "Заряд аксессуаров в трее · by " + AppInfo.Author);
        k.SetValue("DisplayIcon", $"\"{exe}\",0");
        k.SetValue("InstallLocation", dir);
        k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)Math.Max(1, ExeSize / 1024), RegistryValueKind.DWord);
        if (AppInfo.RepoUrl is string r) k.SetValue("URLUpdateInfo", r + "/releases");
    }

    /// <summary>After a self-update keep "Apps & features" showing the right version.</summary>
    public static void RefreshRegistration()
    {
        try
        {
            if (!IsInstalledCopy()) return;
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true);
            if (k is null) return;
            if (k.GetValue("DisplayVersion") as string != AppInfo.VersionText) k.SetValue("DisplayVersion", AppInfo.VersionText);
            k.SetValue("Publisher", AppInfo.Author);
            k.SetValue("URLInfoAbout", AppInfo.AuthorUrl);
            k.SetValue("EstimatedSize", (int)Math.Max(1, ExeSize / 1024), RegistryValueKind.DWord);
        }
        catch { }
    }

    /// <summary>Asks a running Glance to exit and waits until it released the single-instance mutex.</summary>
    public static void StopRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev))
                using (ev) ev.Set();
            using var m = new Mutex(false, MutexName);
            bool got = false;
            try { got = m.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { got = true; }
            if (got) m.ReleaseMutex();
        }
        catch (Exception e) { Log.Info($"Stop instance: {e.Message}"); }
    }

    public static void Launch(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) }); }
        catch (Exception e) { Log.Error("Launch", e); }
    }

    // ───────────────────────────── Uninstall ─────────────────────────────

    public static void Uninstall(bool removeData)
    {
        StopRunningInstance();
        Autostart.Set(false);
        TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glance.lnk"));
        TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Glance.lnk"));

        string? dir = InstalledDirectory ?? Path.GetDirectoryName(AppInfo.ExePath);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        if (removeData)
        {
            foreach (var f in new[] { "settings.json", "settings.json.tmp", "settings.json.bad", "history.bin", "history.bin.tmp", "glance.log", "glance.log.1" })
                TryDeleteFile(Path.Combine(AppInfo.DataDir, f));
            TryDeleteEmptyDir(AppInfo.DataDir);
        }

        if (dir is null) return;
        dir = Norm(dir);
        if (dir.IndexOfAny(UnsafePathChars) >= 0) return; // never pass unsafe paths to cmd
        foreach (var f in new[] { ExeName + ".old", ExeName + ".tmp", ".glance-update.download", "portable.txt" })
            TryDeleteFile(Path.Combine(dir, f));

        // The running exe can't delete itself: a hidden cmd deletes exactly Glance.exe and the folder if empty.
        string exe = Path.Combine(dir, ExeName);
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        string script = $"/d /c \"\"{ping}\" -n 3 127.0.0.1 >nul & del /f /q \"{exe}\" & rmdir \"{dir}\"\"";
        bool needsAdmin = !CanWrite(dir);
        var psi = new ProcessStartInfo(cmd, script)
        {
            UseShellExecute = needsAdmin,
            Verb = needsAdmin ? "runas" : "",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
        };
        try { Process.Start(psi); }
        catch (Exception e) { Log.Error("Uninstall cleanup", e); }
    }

    static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".glance-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryDeleteEmptyDir(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false); } catch { }
    }

    // ───────────────────────────── Shortcuts (IShellLink) ─────────────────────────────

    static void CreateShortcut(string lnk, string target, string workDir)
    {
        object? obj = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            obj = new ShellLinkCoClass();
            var link = (IShellLinkW)obj;
            link.SetPath(target);
            link.SetWorkingDirectory(workDir);
            link.SetDescription("Заряд аксессуаров в трее · by " + AppInfo.Author);
            link.SetIconLocation(target, 0);
            ((IPersistFile)obj).Save(lnk, true);
        }
        catch (Exception e) { Log.Error("Shortcut " + lnk, e); }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
