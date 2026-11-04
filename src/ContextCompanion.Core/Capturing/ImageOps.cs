using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Capturing;

/// <summary>
/// Platform-neutral image steps applied to captured pixels before they reach the privacy pipeline: crop to a
/// rectangle and downscale to a maximum long edge. Both return new images with a compact stride and never change
/// their input. <see cref="BgraImage"/> holds straight (not premultiplied) alpha.
/// </summary>
public static class ImageOps
{
    /// <summary>
    /// Copies the pixels inside <paramref name="area"/>, given in the image's own pixel coordinates. The area must lie
    /// entirely inside the image.
    /// </summary>
    public static BgraImage Crop(BgraImage image, ScreenRect area)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (area.IsEmpty || area.X < 0 || area.Y < 0 || area.Right > image.Width || area.Bottom > image.Height)
            throw new ArgumentOutOfRangeException(nameof(area), "The crop rectangle must be non-empty and lie inside the image.");
        int rowBytes = area.Width * 4;
        var pixels = new byte[rowBytes * area.Height];
        for (int y = 0; y < area.Height; y++)
            Buffer.BlockCopy(image.Pixels, (area.Y + y) * image.Stride + area.X * 4, pixels, y * rowBytes, rowBytes);
        return new BgraImage(area.Width, area.Height, rowBytes, pixels);
    }

    /// <summary>
    /// The size an image of <paramref name="width"/> x <paramref name="height"/> has after
    /// <see cref="Downscale"/>: the long edge becomes <paramref name="maxLongEdge"/> and the short edge keeps the
    /// aspect ratio, rounded half up and at least 1. Unchanged when <paramref name="maxLongEdge"/> is 0 or the image
    /// already fits; images are never enlarged.
    /// </summary>
    public static (int Width, int Height) FitLongEdge(int width, int height, int maxLongEdge)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "The size must be positive.");
        ArgumentOutOfRangeException.ThrowIfNegative(maxLongEdge);
        int longEdge = Math.Max(width, height);
        if (maxLongEdge == 0 || longEdge <= maxLongEdge) return (width, height);
        long Scale(int edge) => Math.Max(1, ((long)edge * maxLongEdge * 2 + longEdge) / (2L * longEdge));
        return width >= height ? (maxLongEdge, (int)Scale(height)) : ((int)Scale(width), maxLongEdge);
    }

    /// <summary>
    /// Shrinks the image so its long edge is at most <paramref name="maxLongEdge"/> (0 keeps the original size) with
    /// area averaging: each output pixel is the average of the exact source area it covers, partial source pixels
    /// weighted by the covered fraction. Colour is averaged weighted by alpha (as if premultiplied), so fully
    /// transparent pixels contribute no colour; alpha is averaged by area. Results are rounded half up. Returns the
    /// input itself when no resizing is needed.
    /// </summary>
    public static BgraImage Downscale(BgraImage image, int maxLongEdge)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (dw, dh) = FitLongEdge(image.Width, image.Height, maxLongEdge);
        if (dw == image.Width && dh == image.Height) return image;

        var xs = Footprints(image.Width, dw);
        var ys = Footprints(image.Height, dh);
        long area = (long)image.Width * image.Height; // sum of all weights for one output pixel
        var src = image.Pixels;
        var pixels = new byte[dw * 4 * dh];
        for (int dy = 0; dy < dh; dy++)
        {
            var rowTaps = ys[dy];
            for (int dx = 0; dx < dw; dx++)
            {
                var colTaps = xs[dx];
                long b = 0, g = 0, r = 0, a = 0;
                foreach (var (sy, wy) in rowTaps)
                {
                    int row = sy * image.Stride;
                    foreach (var (sx, wx) in colTaps)
                    {
                        int i = row + sx * 4;
                        long w = (long)wx * wy;
                        long wa = w * src[i + 3];
                        b += wa * src[i];
                        g += wa * src[i + 1];
                        r += wa * src[i + 2];
                        a += wa;
                    }
                }
                int d = (dy * dw + dx) * 4;
                if (a > 0)
                {
                    pixels[d] = (byte)((b + a / 2) / a);
                    pixels[d + 1] = (byte)((g + a / 2) / a);
                    pixels[d + 2] = (byte)((r + a / 2) / a);
                }
                pixels[d + 3] = (byte)((a + area / 2) / area);
            }
        }
        return new BgraImage(dw, dh, dw * 4, pixels);
    }

    /// <summary>
    /// For each of <paramref name="target"/> output positions, the source positions it covers and their integer
    /// weights. In units of 1/<paramref name="target"/> source pixel, source pixel i spans [i*target, (i+1)*target)
    /// and output pixel j spans [j*source, (j+1)*source), so each output's weights add up to <paramref name="source"/>.
    /// </summary>
    private static (int Index, int Weight)[][] Footprints(int source, int target)
    {
        var result = new (int, int)[target][];
        for (int j = 0; j < target; j++)
        {
            long start = (long)j * source, end = start + source;
            int first = (int)(start / target), last = (int)((end - 1) / target);
            var taps = new (int, int)[last - first + 1];
            for (int i = first; i <= last; i++)
            {
                long overlap = Math.Min(end, (long)(i + 1) * target) - Math.Max(start, (long)i * target);
                taps[i - first] = (i, (int)overlap);
            }
            result[j] = taps;
        }
        return result;
    }
}
