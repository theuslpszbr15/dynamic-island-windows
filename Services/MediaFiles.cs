using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>
/// Which files are pictures and which are videos (the Imagens / Vídeos tabs and the viewer), and small previews of
/// them. Previews come from Windows itself (the same thumbnails Explorer shows), so a format only shows a picture if
/// Windows can read it; nothing here opens or runs the file.
/// </summary>
public static class MediaFiles
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff", ".avif" };
    /// <summary>Videos, and gifs (they move, so they live with the videos).</summary>
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".3gp", ".wmv", ".gif" };
    /// <summary>What the viewer plays itself; the rest (mov, avi…) opens with "Abrir com…" if Windows can't play it.</summary>
    static readonly HashSet<string> Playable = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mkv", ".webm", ".wmv", ".gif", ".mov" };

    public static bool IsImage(string name) => Images.Contains(Path.GetExtension(name));
    public static bool IsVideo(string name) => Videos.Contains(Path.GetExtension(name));
    public static bool IsGif(string name) => Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase);
    public static bool CanPlay(string name) => Playable.Contains(Path.GetExtension(name));

    static readonly System.Collections.Concurrent.BlockingCollection<(string Path, int Size, TaskCompletionSource<BitmapSource?> Done)> Queue = [];
    static Thread? _worker;

    /// <summary><see cref="Thumbnail"/> off the UI thread, one at a time on a thread of its own (shell thumbnail
    /// handlers expect a single-threaded apartment).</summary>
    public static Task<BitmapSource?> ThumbnailAsync(string path, int size)
    {
        var done = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Queue)
        {
            if (_worker == null)
            {
                _worker = new Thread(() =>
                {
                    foreach (var (p, s, d) in Queue.GetConsumingEnumerable()) d.TrySetResult(Thumbnail(p, s));
                }) { IsBackground = true, Name = "Thumbnails" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }
        Queue.Add((path, size, done));
        return done.Task;
    }

    /// <summary>A preview about <paramref name="size"/> pixels wide, or null (no thumbnail handler, file gone).</summary>
    public static BitmapSource? Thumbnail(string path, int size)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
            try
            {
                // BIGGERSIZEOK | THUMBNAILONLY: a real preview, never the file type's icon
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0x1 | 0x8, out var bitmap) != 0 || bitmap == IntPtr.Zero)
                    return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally { DeleteObject(bitmap); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception ex)
        {
            Log.Warn("MediaFiles.Thumbnail", ex.Message);
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int Width, Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);
}
