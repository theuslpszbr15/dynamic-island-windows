using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>
/// Enlarges small album covers and icons once, ahead of time, so they stay crisp on screen. Browsers and many
/// players hand Windows a 96–150 px cover and apps often ship only 32/48 px icons; stretched by the renderer
/// those turn soft and blocky. Here they are resampled with a Catmull-Rom bicubic filter (sharp edges, no
/// ringing to speak of) in premultiplied alpha, so transparent icon edges don't grow dark fringes, plus a
/// light unsharp mask when the enlargement is large.
/// </summary>
static class Upscaler
{
    /// <summary>
    /// <paramref name="source"/> at least <paramref name="px"/> pixels on its longer side. Sources that are
    /// already large enough come back unchanged (the renderer's own downscale is fine).
    /// </summary>
    public static BitmapSource Fit(BitmapSource source, int px)
    {
        var longer = Math.Max(source.PixelWidth, source.PixelHeight);
        if (px <= 0 || longer <= 0 || longer >= px * 0.95) return source;
        try
        {
            var scale = (double)px / longer;
            var w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            var h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            var pixels = Read(source, out var sw, out var sh);
            var result = Resize(pixels, sw, sh, w, h);
            if (scale >= 1.75) result = Sharpen(result, w, h, scale >= 3 ? 0.45 : 0.3);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, result, w * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch { return source; }
    }

    static byte[] Read(BitmapSource source, out int w, out int h)
    {
        BitmapSource pbgra = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        w = pbgra.PixelWidth;
        h = pbgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        pbgra.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    // Catmull-Rom (cubic convolution, a = -0.5).
    static double Kernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
        if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
        return 0;
    }

    /// <summary>Separable bicubic resize of Pbgra32 pixels (two passes: rows, then columns).</summary>
    internal static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        var temp = new double[dw * sh * 4];
        var (xIndex, xWeight) = Taps(sw, dw);
        for (var y = 0; y < sh; y++)
            for (var x = 0; x < dw; x++)
                for (var c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (var t = 0; t < 4; t++)
                        sum += src[(y * sw + xIndex[x * 4 + t]) * 4 + c] * xWeight[x * 4 + t];
                    temp[(y * dw + x) * 4 + c] = sum;
                }

        var dst = new byte[dw * dh * 4];
        var (yIndex, yWeight) = Taps(sh, dh);
        Span<double> px = stackalloc double[4];
        for (var y = 0; y < dh; y++)
            for (var x = 0; x < dw; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (var t = 0; t < 4; t++)
                        sum += temp[(yIndex[y * 4 + t] * dw + x) * 4 + c] * yWeight[y * 4 + t];
                    px[c] = sum;
                }
                WritePremultiplied(dst, (y * dw + x) * 4, px);
            }
        return dst;
    }

    // For each output column/row: the 4 source samples it reads and their normalised weights.
    static (int[] Index, double[] Weight) Taps(int source, int target)
    {
        var index = new int[target * 4];
        var weight = new double[target * 4];
        var ratio = (double)source / target;
        for (var i = 0; i < target; i++)
        {
            var center = (i + 0.5) * ratio - 0.5;
            var left = (int)Math.Floor(center) - 1;
            double total = 0;
            for (var t = 0; t < 4; t++)
            {
                var s = left + t;
                index[i * 4 + t] = Math.Clamp(s, 0, source - 1);
                total += weight[i * 4 + t] = Kernel(center - s);
            }
            for (var t = 0; t < 4; t++) weight[i * 4 + t] /= total;
        }
        return (index, weight);
    }

    // Bicubic overshoots a little: clamp alpha first, then colour to alpha (premultiplied stays valid).
    static void WritePremultiplied(byte[] dst, int at, ReadOnlySpan<double> bgra)
    {
        var a = Math.Clamp(bgra[3], 0, 255);
        dst[at + 3] = (byte)Math.Round(a);
        for (var c = 0; c < 3; c++) dst[at + c] = (byte)Math.Round(Math.Clamp(bgra[c], 0, a));
    }

    /// <summary>Unsharp mask with a 3×3 blur: brings back the edge contrast an enlargement softens.</summary>
    internal static byte[] Sharpen(byte[] src, int w, int h, double amount)
    {
        var dst = new byte[src.Length];
        Span<double> px = stackalloc double[4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var at = (y * w + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    double blur = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var sx = Math.Clamp(x + dx, 0, w - 1);
                            var sy = Math.Clamp(y + dy, 0, h - 1);
                            blur += src[(sy * w + sx) * 4 + c] * (dx == 0 && dy == 0 ? 4 : dx == 0 || dy == 0 ? 2 : 1);
                        }
                    blur /= 16;
                    px[c] = src[at + c] + (src[at + c] - blur) * amount;
                }
                WritePremultiplied(dst, at, px);
            }
        return dst;
    }
}
