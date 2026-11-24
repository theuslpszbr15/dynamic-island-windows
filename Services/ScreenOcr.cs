using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Drawing = System.Drawing;

namespace DynamicIsland.Services;

/// <summary>Text on the screen, read by Windows' own OCR (offline, no download, no key).</summary>
public static class ScreenOcr
{
    /// <summary>A part of the screen, in physical pixels.</summary>
    public static Drawing.Bitmap Capture(Drawing.Rectangle area)
    {
        var bitmap = new Drawing.Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.CopyFromScreen(area.Location, Drawing.Point.Empty, area.Size);
        return bitmap;
    }

    static OcrEngine? Engine() =>
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("pt-BR"))
        ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

    public static bool Available => Engine() != null;

    /// <summary>The lines of text in the image, top to bottom; null when Windows has no OCR language installed.</summary>
    public static async Task<string?> ReadAsync(Drawing.Bitmap image) =>
        await RecognizeAsync(image) is { } result ? string.Join('\n', result.Lines.Select(line => line.Text)).Trim() : null;

    /// <summary>Each line of text with where it is (pixels of <paramref name="image"/>); null without an OCR language.</summary>
    public static async Task<List<(string Text, Drawing.RectangleF Box)>?> ReadLinesAsync(Drawing.Bitmap image)
    {
        if (await RecognizeAsync(image, keepScale: true) is not { } found) return null;
        var (result, scale) = found;
        var lines = new List<(string, Drawing.RectangleF)>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            float left = float.MaxValue, top = float.MaxValue, right = 0, bottom = 0;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                left = Math.Min(left, (float)r.X);
                top = Math.Min(top, (float)r.Y);
                right = Math.Max(right, (float)(r.X + r.Width));
                bottom = Math.Max(bottom, (float)(r.Y + r.Height));
            }
            lines.Add((line.Text, new Drawing.RectangleF(left / scale, top / scale, (right - left) / scale, (bottom - top) / scale)));
        }
        return lines;
    }

    static async Task<OcrResult?> RecognizeAsync(Drawing.Bitmap image) => (await RecognizeAsync(image, keepScale: false))?.Result;

    static async Task<(OcrResult Result, float Scale)?> RecognizeAsync(Drawing.Bitmap image, bool keepScale)
    {
        if (Engine() is not { } engine) return null;
        var source = image;
        // small text reads better bigger; the engine has a size limit
        var scale = Math.Min(image.Height < 60 ? 2.0 : 1.0, (double)OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
        if (Math.Abs(scale - 1) > 0.01)
        {
            source = new Drawing.Bitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        }
        try
        {
            var data = source.LockBits(new Drawing.Rectangle(0, 0, source.Width, source.Height), Drawing.Imaging.ImageLockMode.ReadOnly,
                Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            source.UnlockBits(data);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, source.Width, source.Height, BitmapAlphaMode.Premultiplied);
            return (await engine.RecognizeAsync(bitmap), (float)scale);
        }
        finally
        {
            if (!ReferenceEquals(source, image)) source.Dispose();
        }
    }
}
