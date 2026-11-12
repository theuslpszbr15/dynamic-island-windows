using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicIsland;

/// <summary>
/// Makes the parts of a window that Windows draws itself (title bar, 1 px border) dark and in the island's colours,
/// so they never take the Windows accent colour (e.g. a purple bar when "Show accent colour on title bars" is on).
/// Windows 11 takes the exact colours; Windows 10 (20H1+) takes the dark mode; older builds ignore it.
/// </summary>
public static class DarkFrame
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

    public static void Apply(Window window, uint caption, uint border)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var c = ColorRef(caption);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref c, sizeof(int));
        var b = ColorRef(border);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref b, sizeof(int));
        var t = ColorRef(0xFFE5E5EA);
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref t, sizeof(int));
    }

    // ARGB → COLORREF (0x00BBGGRR)
    static int ColorRef(uint argb) => (int)(((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF));

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
