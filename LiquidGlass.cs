using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>
/// "Liquid glass": what is behind the island, lightly blurred, bent like a lens along the rim (content from
/// just outside the edge is pulled in, with a touch of colour fringing) and slightly more vivid.
/// Computed on the CPU at half resolution from a screen capture.
/// </summary>
static class LiquidGlass
{
    const int Scale = 2;

    /// <param name="x">Island bounds on screen, physical pixels.</param>
    /// <param name="radius">Corner radius in physical pixels.</param>
    /// <param name="flatTop">The top edge touches the screen edge (notch), so it doesn't refract.</param>
    public static BitmapSource? Render(int x, int y, int width, int height, double radius, bool flatTop, double dpiScale)
    {
        int ow = width / Scale, oh = height / Scale;
        if (ow < 4 || oh < 4) return null;

        // The refraction samples a little outside the glass, so capture a margin around it.
        var band = (float)Math.Min(Math.Min(ow, oh) * 0.42, 15 * dpiScale / Scale);
        var reach = band * 1.15f;
        var pad = (int)Math.Ceiling(reach + 3);
        if (Native.CapturePixels(x - pad * Scale, y - pad * Scale, (ow + 2 * pad) * Scale, (oh + 2 * pad) * Scale, Scale) is not { } shot)
            return null;
        var (src, sw, sh) = shot;
        // The rim shows the background sharp and bent; the middle is frosted so the island's text stays readable.
        var frost = (int[])src.Clone();
        BoxBlur(src, sw, sh, 1);
        BoxBlur(src, sw, sh, 1);
        for (var i = 0; i < 3; i++) BoxBlur(frost, sw, sh, 3);

        var output = new int[ow * oh];
        float hx = ow / 2f, hy = oh / 2f;
        var r = (float)Math.Min(radius / Scale, Math.Min(hx, hy));
        for (var py = 0; py < oh; py++)
        {
            for (var px = 0; px < ow; px++)
            {
                float cx = px + 0.5f - hx, cy = py + 0.5f - hy;
                float ax = MathF.Abs(cx), ay = MathF.Abs(cy);
                float qx = ax - (hx - r), qy = ay - (hy - r);
                if (flatTop && cy < 0) qy = -1e6f;

                // Signed distance to the rounded rectangle (negative inside) and the outward normal.
                float ox = MathF.Max(qx, 0), oy = MathF.Max(qy, 0);
                var depth = r - MathF.Sqrt(ox * ox + oy * oy) - MathF.Min(MathF.Max(qx, qy), 0);
                float nx, ny;
                if (qx > 0 && qy > 0)
                {
                    var len = MathF.Sqrt(qx * qx + qy * qy);
                    (nx, ny) = (qx / len, qy / len);
                }
                else if (qx > qy) (nx, ny) = (1, 0);
                else (nx, ny) = (0, 1);
                nx *= MathF.Sign(cx);
                ny *= MathF.Sign(cy);

                var t = depth < band ? 1 - MathF.Max(depth, 0) / band : 0;
                var bend = reach * t * t;
                // 1 at the rim, 0 a band and a half inside it
                var rim = Math.Clamp(1 - MathF.Max(depth, 0) / (band * 1.5f), 0, 1);
                rim *= rim;
                float sx = px + pad, sy = py + pad;
                float Channel(float k, int shift) =>
                    Sample(frost, sw, sh, sx + nx * bend * k, sy + ny * bend * k, shift) * (1 - rim)
                    + Sample(src, sw, sh, sx + nx * bend * k, sy + ny * bend * k, shift) * rim;
                output[py * ow + px] = Vivid(Channel(1.12f, 16), Channel(1f, 8), Channel(0.88f, 0));
            }
        }

        var bitmap = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, output, ow * 4);
        bitmap.Freeze();
        return bitmap;
    }

    static float Sample(int[] src, int w, int h, float x, float y, int shift)
    {
        x = Math.Clamp(x - 0.5f, 0, w - 1.001f);
        y = Math.Clamp(y - 0.5f, 0, h - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        float C(int ix, int iy) => (src[iy * w + ix] >> shift) & 0xFF;
        var top = C(x0, y0) + (C(x0 + 1, y0) - C(x0, y0)) * fx;
        var bottom = C(x0, y0 + 1) + (C(x0 + 1, y0 + 1) - C(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    // A little more saturation and light, as glass seems to gather it.
    static int Vivid(float r, float g, float b)
    {
        var luma = 0.299f * r + 0.587f * g + 0.114f * b;
        static int Clamp(float v) => (int)Math.Clamp(v, 0, 255);
        int R = Clamp((luma + (r - luma) * 1.25f) * 1.05f), G = Clamp((luma + (g - luma) * 1.25f) * 1.05f), B = Clamp((luma + (b - luma) * 1.25f) * 1.05f);
        return unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
    }

    /// <summary>Box blur of the given radius in place; repeated passes approach a Gaussian.</summary>
    static void BoxBlur(int[] px, int w, int h, int radius)
    {
        var tmp = new int[px.Length];
        Pass(px, tmp, w, h, 1, w, radius);
        Pass(tmp, px, w, h, w, h, radius);

        static void Pass(int[] from, int[] to, int w, int h, int step, int length, int radius)
        {
            var lines = step == 1 ? h : w;
            var taps = radius * 2 + 1;
            for (var line = 0; line < lines; line++)
            {
                var start = step == 1 ? line * w : line;
                for (var i = 0; i < length; i++)
                {
                    int R = 0, G = 0, B = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var c = from[start + Math.Clamp(i + k, 0, length - 1) * step];
                        R += (c >> 16) & 0xFF;
                        G += (c >> 8) & 0xFF;
                        B += c & 0xFF;
                    }
                    to[start + i * step] = (R / taps << 16) | (G / taps << 8) | B / taps;
                }
            }
        }
    }
}
