using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class UpscalerTests
{
    static byte[] Solid(int w, int h, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 4] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = a;
        }
        return pixels;
    }

    [Fact]
    public void Solid_colour_stays_exact_when_enlarged_and_sharpened()
    {
        var big = Upscaler.Resize(Solid(3, 2, 10, 120, 200, 255), 3, 2, 12, 8);
        Assert.Equal(Solid(12, 8, 10, 120, 200, 255), big);
        Assert.Equal(big, Upscaler.Sharpen(big, 12, 8, 0.45));
    }

    [Fact]
    public void Transparent_edges_stay_valid_premultiplied_alpha()
    {
        // opaque white on the left half, fully transparent on the right
        var src = new byte[4 * 4];
        for (var i = 0; i < 2; i++) src[i * 4] = src[i * 4 + 1] = src[i * 4 + 2] = src[i * 4 + 3] = 255;
        var big = Upscaler.Sharpen(Upscaler.Resize(src, 4, 1, 16, 1), 16, 1, 0.45);
        for (var i = 0; i < 16; i++)
            for (var c = 0; c < 3; c++)
                Assert.True(big[i * 4 + c] <= big[i * 4 + 3]);
        Assert.Equal(255, big[3]);
        Assert.Equal(0, big[15 * 4 + 3]);
    }
}
