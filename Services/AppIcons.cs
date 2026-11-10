using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>
/// Sharp app and file icons from the shell's image factory, which hands out the largest icon an app ships
/// (up to 256 px) instead of the 32/48 px image lists. Media sessions are looked up by their app id in
/// shell:AppsFolder (Store apps and most browsers register one) and then by the running process's exe.
/// </summary>
static class AppIcons
{
    static readonly Dictionary<string, BitmapSource?> Cache = [];

    /// <summary>The icon of the app behind a media session id ("Spotify.exe", "Chrome", "SpotifyAB…!Spotify").</summary>
    public static BitmapSource? ForAppId(string appId, int px)
    {
        var key = $"app|{appId}|{px}";
        lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;
        var icon = FromParsingName(@"shell:AppsFolder\" + appId, px);
        if (icon == null && ExeOf(appId) is { } exe) icon = ForFile(exe, px);
        lock (Cache) Cache[key] = icon;
        return icon;
    }

    /// <summary>The icon of a file, folder or exe at <paramref name="px"/> pixels (null when the shell has none).</summary>
    public static BitmapSource? ForFile(string path, int px)
    {
        var key = $"file|{path}|{px}";
        lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;
        var icon = FromParsingName(path, px);
        // the shelf can hold many files: don't keep every one of them alive here
        if (!File.Exists(path) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            lock (Cache) Cache[key] = icon;
        return icon;
    }

    // "brave.exe", "Spotify.exe", "C:\…\app.exe" or a bare process name → the exe of a running copy.
    static string? ExeOf(string appId)
    {
        if (appId.Contains('\\') && File.Exists(appId)) return appId;
        var name = appId.Split('!')[0].Split('\\')[^1];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) return null;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } file) return file; }
                catch { }
            }
        }
        return null;
    }

    static BitmapSource? FromParsingName(string name, int px)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
            try
            {
                if (factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hBitmap) != 0)
                    return null;
            }
            finally { Marshal.ReleaseComObject(factory); }
            return ToBitmap(hBitmap);
        }
        catch { return null; }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }

    // The factory's DIB is 32-bit with premultiplied alpha; Imaging.CreateBitmapSourceFromHBitmap drops the
    // alpha (black corners), so the pixels are copied out top-down as Pbgra32.
    static BitmapSource? ToBitmap(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hBitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        // Some icons come without alpha at all (every A = 0): treat them as opaque.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
}
