using System.Runtime.InteropServices;

namespace Glance;

internal static class Native
{
    // ── DWM ──
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void RoundCorners(IntPtr hwnd, bool small = false)
    {
        int v = small ? 3 : 2; // DWMWCP_ROUNDSMALL : DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, 33, ref v, sizeof(int)); } catch { }
    }

    public static void SetBorderColor(IntPtr hwnd, Color c)
    {
        int v = c.R | (c.G << 8) | (c.B << 16);
        try { DwmSetWindowAttribute(hwnd, 34, ref v, sizeof(int)); } catch { }
    }

    public static void DarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); } catch { }
    }

    public static void Mica(IntPtr hwnd)
    {
        int v = 2; // DWMSBT_MAINWINDOW
        try { DwmSetWindowAttribute(hwnd, 38, ref v, sizeof(int)); } catch { }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    // ── Shell tray ──
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    public const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_POPUPOPEN = 0x406, NIN_POPUPCLOSE = 0x407, WM_CONTEXTMENU = 0x7B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    // ── user32 ──
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr filter);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    // ── psapi ──
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>DPI of the monitor containing <paramref name="pt"/> (physical coordinates).</summary>
    public static int DpiAt(Point pt)
    {
        try
        {
            var mon = MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
            if (GetDpiForMonitor(mon, 0, out var x, out _) == 0 && x > 0) return (int)x;
        }
        catch { }
        return 96;
    }

    /// <summary>True when a fullscreen game, presentation or Focus/Do-not-disturb is active.</summary>
    public static bool UserIsBusy()
    {
        try
        {
            if (SHQueryUserNotificationState(out int s) == 0)
                return s is 2 /*BUSY*/ or 3 /*D3D fullscreen*/ or 4 /*presentation*/ or 6 /*quiet time*/;
        }
        catch { }
        return false;
    }

    public static void TrimWorkingSet()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
        }
        catch { }
    }
}
