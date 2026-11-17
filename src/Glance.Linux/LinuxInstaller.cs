using System.Diagnostics;
using System.Text;

namespace Glance;

/// <summary>
/// The Linux binary is its own installer (no root, no package manager):
/// copies itself to the chosen location, adds a menu entry, icon, autostart entry and ~/.local/bin link.
/// Interactive in a terminal; with zenity/kdialog when started by double-click; silent otherwise.
/// Only ever deletes the files it created.
/// </summary>
internal static class LinuxInstaller
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Home, ".config");
    static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share");

    static string DesktopFile => Path.Combine(DataHome, "applications", "glance.desktop");
    static string AutostartFile => Path.Combine(ConfigHome, "autostart", "glance.desktop");
    static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "glance.png");
    static string BinLink => Path.Combine(Home, ".local", "bin", "glance");
    static string RecordFile => Path.Combine(AppInfo.DataDir, "install-path");
    public static string DefaultDir => Path.Combine(Home, ".local", "opt", "glance");

    public static string? InstalledExe
    {
        get
        {
            try { return File.Exists(RecordFile) ? File.ReadAllText(RecordFile).Trim() : null; }
            catch { return null; }
        }
    }

    public static bool IsInstalledCopy()
    {
        var here = AppInfo.ExePath;
        if (File.Exists(Path.Combine(Path.GetDirectoryName(here) ?? "", "portable.txt"))) return true;
        return InstalledExe is string p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(here), StringComparison.Ordinal);
    }

    // ───────────── Interactive front-ends ─────────────

    /// <summary>Returns the installed executable path, or null if the user cancelled.</summary>
    public static string? RunInteractive()
    {
        bool tty = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (tty) return Tui();
        if (Which("zenity") is string z) return Zenity(z);
        if (Which("kdialog") is string k) return KDialog(k);
        return Install(DefaultDir, autostart: true, menu: true); // nothing to ask with: sensible defaults
    }

    static string? Tui()
    {
        var locations = Locations();
        Console.WriteLine();
        Console.WriteLine("  ╭──────────────────────────────────────────────╮");
        Console.WriteLine($"  │  Glance {AppInfo.VersionText,-10} — заряд аксессуаров в трее  │");
        Console.WriteLine("  ╰──────────────────────────────────────────────╯");
        if (InstalledExe is string old) Console.WriteLine($"  Найдена установленная версия: {old}");
        Console.WriteLine();
        Console.WriteLine("  Куда установить:");
        for (int i = 0; i < locations.Count; i++)
            Console.WriteLine($"   {i + 1}) {locations[i].Path,-40} {locations[i].Note}");
        Console.WriteLine($"   {locations.Count + 1}) Другая папка…");
        Console.Write($"  Выбор [1]: ");
        var choice = Console.ReadLine()?.Trim();
        string dir;
        if (string.IsNullOrEmpty(choice)) dir = locations[0].Path;
        else if (int.TryParse(choice, out int n) && n >= 1 && n <= locations.Count) dir = locations[n - 1].Path;
        else if (int.TryParse(choice, out int m) && m == locations.Count + 1)
        {
            Console.Write("  Путь: ");
            dir = Console.ReadLine()?.Trim() ?? "";
        }
        else { Console.WriteLine("  Отменено."); return null; }

        bool auto = Ask("  Запускать при входе в систему? [Y/n]: ", true);
        bool menu = Ask("  Добавить в меню приложений? [Y/n]: ", true);
        try
        {
            var exe = Install(dir, auto, menu);
            Console.WriteLine($"\n  ✓ Установлено: {exe}");
            Console.WriteLine("  Значки появятся в трее. В GNOME нужно расширение «AppIndicator and KStatusNotifierItem Support».\n");
            return exe;
        }
        catch (Exception e)
        {
            Console.WriteLine($"\n  ✗ Ошибка: {e.Message}\n");
            return null;
        }
    }

    static bool Ask(string q, bool def)
    {
        Console.Write(q);
        var a = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(a)) return def;
        return a is "y" or "yes" or "д" or "да";
    }

    static string? Zenity(string zenity)
    {
        var locs = Locations();
        var args = new List<string> { "--list", "--radiolist", "--title=Установка Glance", "--text=Куда установить Glance?",
            "--column=", "--column=Папка", "--column=", "--width=620", "--height=360" };
        for (int i = 0; i < locs.Count; i++) { args.Add(i == 0 ? "TRUE" : "FALSE"); args.Add(locs[i].Path); args.Add(locs[i].Note); }
        var (code, output) = Run(zenity, args);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return null;
        bool auto = Run(zenity, new() { "--question", "--title=Glance", "--text=Запускать Glance при входе в систему?" }).Code == 0;
        try
        {
            var exe = Install(output.Trim(), auto, true);
            Run(zenity, new() { "--info", "--title=Glance", $"--text=Glance установлен.\n{exe}" });
            return exe;
        }
        catch (Exception e)
        {
            Run(zenity, new() { "--error", "--title=Glance", "--text=" + e.Message });
            return null;
        }
    }

    static string? KDialog(string kdialog)
    {
        var locs = Locations();
        var args = new List<string> { "--title", "Установка Glance", "--radiolist", "Куда установить Glance?" };
        for (int i = 0; i < locs.Count; i++) { args.Add(locs[i].Path); args.Add($"{locs[i].Path}  {locs[i].Note}"); args.Add(i == 0 ? "on" : "off"); }
        var (code, output) = Run(kdialog, args);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return null;
        bool auto = Run(kdialog, new() { "--title", "Glance", "--yesno", "Запускать Glance при входе в систему?" }).Code == 0;
        try { return Install(output.Trim(), auto, true); }
        catch (Exception e) { Run(kdialog, new() { "--error", e.Message }); return null; }
    }

    /// <summary>Home plus every mounted data drive the user can write to.</summary>
    static List<(string Path, string Note)> Locations()
    {
        var list = new List<(string, string)> { (DefaultDir, "рекомендуется, без root") };
        try
        {
            foreach (var line in File.ReadAllLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3) continue;
                string mnt = parts[1].Replace("\\040", " ");
                string fs = parts[2];
                if (!(mnt.StartsWith("/mnt/") || mnt.StartsWith("/media/") || mnt.StartsWith("/run/media/") || mnt == "/data")) continue;
                if (fs is "tmpfs" or "proc" or "sysfs" or "squashfs" or "iso9660" or "overlay") continue;
                if (!CanWrite(mnt)) continue;
                long free = 0;
                try { free = new DriveInfo(mnt).AvailableFreeSpace; } catch { }
                list.Add((Path.Combine(mnt, "glance"), free > 0 ? $"диск {mnt}, свободно {Fmt.Size(free)}" : $"диск {mnt}"));
                if (list.Count >= 8) break;
            }
        }
        catch { }
        return list;
    }

    // ───────────── Install / uninstall ─────────────

    public static string Install(string dirInput, bool autostart, bool menu)
    {
        string dir = NormalizeDir(dirInput);
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "glance");
        string src = AppInfo.ExePath;

        StopRunning();
        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(exe), StringComparison.Ordinal))
        {
            string tmp = exe + ".tmp";
            File.Copy(src, tmp, true);
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, exe, true); // atomic rename; a running old copy keeps its inode
        }

        if (Icons.Png() is byte[] png) { Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!); File.WriteAllBytes(IconFile, png); }
        if (menu) WriteDesktop(DesktopFile, exe, autostart: false); else TryDelete(DesktopFile);
        SetAutostart(autostart, exe);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BinLink)!);
            if (File.Exists(BinLink) || IsSymlink(BinLink))
            {
                var fi = new FileInfo(BinLink);
                if (fi.LinkTarget is not null) File.Delete(BinLink); // replace only our own/any symlink, never a real file
            }
            if (!File.Exists(BinLink)) File.CreateSymbolicLink(BinLink, exe);
        }
        catch (Exception e) { Log.Info($"bin link: {e.Message}"); }

        // Old copy in a different place: remove only the file we put there.
        if (InstalledExe is string old && old != exe && File.Exists(old)) TryDelete(old);
        File.WriteAllText(RecordFile, exe);
        Log.Info($"Installed to {exe}");
        return exe;
    }

    public static void Uninstall(bool removeData)
    {
        StopRunning();
        TryDelete(DesktopFile);
        TryDelete(AutostartFile);
        TryDelete(IconFile);
        try { if (IsSymlink(BinLink)) File.Delete(BinLink); } catch { }
        if (InstalledExe is string exe)
        {
            TryDelete(exe);
            TryDelete(exe + ".old");
            try { var d = Path.GetDirectoryName(exe); if (d is not null && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d, false); } catch { }
        }
        TryDelete(RecordFile);
        if (removeData)
            foreach (var f in new[] { "settings.json", "history.bin", "glance.log", "glance.log.1", "glance.pid" })
                TryDelete(Path.Combine(AppInfo.DataDir, f));
    }

    public static bool AutostartEnabled => File.Exists(AutostartFile);

    public static void SetAutostart(bool enabled, string? exe = null)
    {
        try
        {
            if (enabled) WriteDesktop(AutostartFile, exe ?? InstalledExe ?? AppInfo.ExePath, autostart: true);
            else TryDelete(AutostartFile);
        }
        catch (Exception e) { Log.Error("Autostart", e); }
    }

    static void WriteDesktop(string file, string exe, bool autostart)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=Glance\n");
        sb.Append("Comment=Заряд Bluetooth-аксессуаров в трее\n");
        sb.Append("Exec=").Append(DesktopExec(exe)).Append(autostart ? " --autostart" : "").Append('\n');
        sb.Append("Icon=glance\n");
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Utility;System;\n");
        sb.Append("StartupNotify=false\n");
        if (autostart) sb.Append("X-GNOME-Autostart-enabled=true\nX-GNOME-Autostart-Delay=5\n");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Quotes a path for the Exec key per the Desktop Entry spec (no shell involved).</summary>
    static string DesktopExec(string path)
    {
        if (path.Any(char.IsControl)) throw new ArgumentException("Недопустимый путь");
        var q = new StringBuilder("\"");
        foreach (char ch in path)
        {
            if (ch is '"' or '`' or '$' or '\\') q.Append('\\');
            q.Append(ch);
        }
        q.Append('"');
        // Second layer: the .desktop "string" type escapes backslashes; '%' is a field code marker.
        return q.ToString().Replace("\\", "\\\\").Replace("%", "%%");
    }

    static string NormalizeDir(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Укажите папку");
        string p = input.Trim();
        if (p == "~" || p.StartsWith("~/")) p = Home + p[1..];
        if (!Path.IsPathRooted(p)) throw new ArgumentException("Укажите полный путь");
        p = Path.GetFullPath(p).TrimEnd('/');
        if (!string.Equals(Path.GetFileName(p), "glance", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(p, "glance");
        if (p.Any(char.IsControl)) throw new ArgumentException("Недопустимый путь");
        foreach (var forbidden in new[] { "/usr", "/bin", "/sbin", "/etc", "/lib", "/boot", "/proc", "/sys", "/dev" })
            if (p == forbidden || p.StartsWith(forbidden + "/")) throw new ArgumentException("Системные папки не подходят — выберите папку в домашнем каталоге или на диске");
        return p;
    }

    // ───────────── Single instance (pid file + SIGTERM) ─────────────

    static string PidFile => Path.Combine(AppInfo.DataDir, "glance.pid");

    public static void StopRunning()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid) || pid == Environment.ProcessId) return;
            using var p = Process.GetProcessById(pid);
            if (!p.ProcessName.Contains("glance", StringComparison.OrdinalIgnoreCase)) return; // stale pid reused by something else
            Run("kill", new() { "-TERM", pid.ToString() });
            if (!p.WaitForExit(5000)) p.Kill();
        }
        catch { /* not running */ }
    }

    /// <summary>Takes the instance lock; false when another instance is alive.</summary>
    public static bool TryLock(out FileStream? handle)
    {
        handle = null;
        try
        {
            handle = new FileStream(Path.Combine(AppInfo.DataDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(PidFile, Environment.ProcessId.ToString());
            return true;
        }
        catch (IOException)
        {
            handle?.Dispose();
            handle = null;
            return false;
        }
    }

    // ───────────── helpers ─────────────

    public static void LaunchDetached(string exe)
    {
        try
        {
            if (Which("setsid") is string setsid)
            {
                var psi = new ProcessStartInfo(setsid) { UseShellExecute = false };
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(exe);
                Process.Start(psi);
            }
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception e) { Log.Error("Launch", e); }
    }

    static (int Code, string Output) Run(string file, List<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
        catch { return (-1, ""); }
    }

    static string? Which(string name)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            if (d.Length == 0) continue;
            var f = Path.Combine(d, name);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".glance-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static bool IsSymlink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; } catch { return false; }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path) || IsSymlink(path)) File.Delete(path); } catch { }
    }
}
