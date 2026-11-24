using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    /// <summary>Hands the pages this process isn't using back to Windows; they come back on demand.</summary>
    public static void TrimWorkingSet() => SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    public static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    public static bool IsKeyDown(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    public const long WS_EX_TRANSPARENT = 0x20;

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int op);

    /// <summary>The screen area given in physical pixels, shrunk by <paramref name="factor"/> (averaged), as 0xRRGGBB ints.</summary>
    public static (int[] Pixels, int Width, int Height)? CapturePixels(int x, int y, int width, int height, int factor)
    {
        if (width < 4 || height < 4) return null;
        int w = Math.Max(2, width / factor), h = Math.Max(2, height / factor);
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(memory, bitmap);
        try
        {
            SetStretchBltMode(memory, 4); // HALFTONE averages the source pixels
            if (!StretchBlt(memory, 0, 0, w, h, screen, x, y, width, height, 0x40CC0020)) return null; // SRCCOPY | CAPTUREBLT
            SelectObject(memory, old);
            var source = new FormatConvertedBitmap(
                Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()),
                PixelFormats.Bgr32, null, 0);
            var pixels = new int[w * h];
            source.CopyPixels(pixels, w * 4, 0);
            return (pixels, w, h);
        }
        catch { return null; }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    public static bool IsWindowVisibleNow(IntPtr hwnd) => IsWindowVisible(hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    public static List<IntPtr> TopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == processId) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>Longest title among the visible top-level windows of a process.</summary>
    public static string WindowTitle(int processId)
    {
        var best = "";
        var buffer = new System.Text.StringBuilder(512);
        foreach (var hwnd in TopLevelWindows(processId))
        {
            buffer.Clear();
            GetWindowText(hwnd, buffer, buffer.Capacity);
            if (buffer.Length > best.Length) best = buffer.ToString();
        }
        return best;
    }

    [DllImport("user32.dll")]
    static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public const byte VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static void PressKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    public static void PressCtrlV() => PressCtrl(0x56);

    /// <summary>Holds the keys down in order and lets go in reverse (a shortcut like Ctrl+A).</summary>
    public static void PressKeys(IReadOnlyList<byte> keys)
    {
        foreach (var key in keys) keybd_event(key, 0, 0, UIntPtr.Zero);
        for (var i = keys.Count - 1; i >= 0; i--) keybd_event(keys[i], 0, 2, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>The PC is locked: the lock screen (LockApp) or a screensaver is in front, or the input desktop is
    /// Winlogon's (password, UAC), which a normal app can't open.</summary>
    public static bool IsInputDesktopLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100); // DESKTOP_SWITCHDESKTOP
        if (desktop == IntPtr.Zero) return true;
        CloseDesktop(desktop);
        var saver = false;
        if (SystemParametersInfo(0x0072, 0, ref saver, 0) && saver) return true; // SPI_GETSCREENSAVERRUNNING
        if (GetWindowThreadProcessId(GetForegroundWindow(), out var pid) == 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("LockApp", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint flags);

    /// <summary>A click at a physical screen point.</summary>
    public static void Click(int x, int y, bool right = false, bool twice = false)
    {
        SetCursorPos(x, y);
        uint down = right ? 0x0008u : 0x0002u, up = right ? 0x0010u : 0x0004u;
        for (var i = 0; i < (twice ? 2 : 1); i++)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    /// <summary>Mouse wheel where the cursor is; positive scrolls up (120 = one notch).</summary>
    public static void Wheel(int amount) => mouse_event(0x0800, 0, 0, amount, UIntPtr.Zero);

    public static void PressCtrl(byte key)
    {
        const byte control = 0x11;
        keybd_event(control, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        keybd_event(control, 0, 2, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    public static string WindowClass(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(128);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI scale (1.0 = 96 dpi) of the monitor containing the given physical point.</summary>
    public static double MonitorScale(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int i, IntPtr icon, out int index);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int i, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, int mask, out int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr icon);
    }

    [DllImport("shell32.dll", EntryPoint = "#727")]
    static extern int SHGetImageList(int list, ref Guid riid, out IImageList imageList);

    const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_ICON = 0x100;
    const int SHIL_EXTRALARGE = 2, ILD_TRANSPARENT = 1;

    /// <summary>
    /// Shell icon for a file or folder: <paramref name="px"/> pixels from the shell's image factory when it
    /// has one, else the 48px image list (falls back to 32px).
    /// </summary>
    public static ImageSource? ShellIcon(string path, int px = 96)
    {
        if (Services.AppIcons.ForFile(path, px) is { } sharp) return sharp;
        var info = new SHFILEINFO();
        var size = (uint)Marshal.SizeOf<SHFILEINFO>();
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            if (SHGetFileInfo(path, 0, ref info, size, SHGFI_SYSICONINDEX) != IntPtr.Zero)
            {
                var iid = typeof(IImageList).GUID;
                if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) == 0)
                    list.GetIcon(info.iIcon, ILD_TRANSPARENT, out hIcon);
            }
            if (hIcon == IntPtr.Zero && SHGetFileInfo(path, 0, ref info, size, SHGFI_ICON) != IntPtr.Zero)
                hIcon = info.hIcon;
            if (hIcon == IntPtr.Zero) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }
}
