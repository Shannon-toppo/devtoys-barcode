using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Pre-processing that helps ZXing on degraded pictures: removes uneven lighting and stretches low contrast.
/// </summary>
internal static class BarcodeImageEnhancer
{
    private const int WindowRadiusInCells = 3;
    private const int MinimumRange = 24;

    /// <summary>
    /// Returns a gray copy of <paramref name="source"/> where, around every pixel, the lightest local level is white and
    /// the darkest local level is black. A blur removes sensor or compression noise first, when asked.
    /// </summary>
    internal static Image<Rgba32> Normalize(Image<Rgba32> source, float blurSigma)
    {
        Image<Rgba32> result = blurSigma > 0 ? source.Clone(context => context.GaussianBlur(blurSigma)) : source.Clone();
        int width = result.Width;
        int height = result.Height;
        int cell = Math.Max(4, (int)Math.Round(Math.Max(width, height) / 100.0));
        int gridWidth = (width + cell - 1) / cell;
        int gridHeight = (height + cell - 1) / cell;

        // Luminance, and the lightest and darkest value of each cell.
        byte[] gray = new byte[width * height];
        byte[] cellMax = new byte[gridWidth * gridHeight];
        byte[] cellMin = new byte[gridWidth * gridHeight];
        Array.Fill(cellMin, (byte)255);
        result.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowStart = y * width;
                int cellRow = (y / cell) * gridWidth;
                for (int x = 0; x < row.Length; x++)
                {
                    byte value = Luma(row[x]);
                    gray[rowStart + x] = value;
                    int index = cellRow + (x / cell);
                    if (value > cellMax[index])
                    {
                        cellMax[index] = value;
                    }

                    if (value < cellMin[index])
                    {
                        cellMin[index] = value;
                    }
                }
            }
        });

        // The local white and black levels: the extremes over a window of cells, then smoothed.
        float[] white = Smooth(Extreme(cellMax, gridWidth, gridHeight, WindowRadiusInCells, max: true), gridWidth, gridHeight);
        float[] black = Smooth(Extreme(cellMin, gridWidth, gridHeight, WindowRadiusInCells, max: false), gridWidth, gridHeight);

        result.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowStart = y * width;
                float gy = Math.Clamp((y / (float)cell) - 0.5f, 0, gridHeight - 1);
                int y0 = (int)gy;
                int y1 = Math.Min(y0 + 1, gridHeight - 1);
                float fy = gy - y0;
                for (int x = 0; x < row.Length; x++)
                {
                    float gx = Math.Clamp((x / (float)cell) - 0.5f, 0, gridWidth - 1);
                    int x0 = (int)gx;
                    int x1 = Math.Min(x0 + 1, gridWidth - 1);
                    float fx = gx - x0;
                    float hi = Bilinear(white, gridWidth, x0, x1, y0, y1, fx, fy);
                    float lo = Bilinear(black, gridWidth, x0, x1, y0, y1, fx, fy);
                    float range = hi - lo;
                    byte value = gray[rowStart + x];
                    byte output = range < MinimumRange
                        ? (value < 80 ? (byte)0 : (byte)255)
                        : (byte)Math.Clamp((int)((value - lo) * 255f / range), 0, 255);
                    row[x] = new Rgba32(output, output, output, 255);
                }
            }
        });

        return result;
    }

    private static byte Luma(Rgba32 pixel)
        => pixel.A < 128 ? (byte)255 : (byte)(((pixel.R * 299) + (pixel.G * 587) + (pixel.B * 114)) / 1000);

    private static float Bilinear(float[] grid, int gridWidth, int x0, int x1, int y0, int y1, float fx, float fy)
    {
        float top = grid[(y0 * gridWidth) + x0] + ((grid[(y0 * gridWidth) + x1] - grid[(y0 * gridWidth) + x0]) * fx);
        float bottom = grid[(y1 * gridWidth) + x0] + ((grid[(y1 * gridWidth) + x1] - grid[(y1 * gridWidth) + x0]) * fx);
        return top + ((bottom - top) * fy);
    }

    /// <summary>Maximum (or minimum) over a square window, computed as two 1D passes.</summary>
    private static float[] Extreme(byte[] values, int width, int height, int radius, bool max)
    {
        float[] horizontal = new float[values.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float best = values[(y * width) + x];
                for (int k = Math.Max(0, x - radius); k <= Math.Min(width - 1, x + radius); k++)
                {
                    float v = values[(y * width) + k];
                    best = max ? Math.Max(best, v) : Math.Min(best, v);
                }

                horizontal[(y * width) + x] = best;
            }
        }

        float[] result = new float[values.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float best = horizontal[(y * width) + x];
                for (int k = Math.Max(0, y - radius); k <= Math.Min(height - 1, y + radius); k++)
                {
                    float v = horizontal[(k * width) + x];
                    best = max ? Math.Max(best, v) : Math.Min(best, v);
                }

                result[(y * width) + x] = best;
            }
        }

        return result;
    }

    /// <summary>A 3x3 box blur.</summary>
    private static float[] Smooth(float[] values, int width, int height)
    {
        float[] result = new float[values.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        int yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < width && yy < height)
                        {
                            sum += values[(yy * width) + xx];
                            count++;
                        }
                    }
                }

                result[(y * width) + x] = sum / count;
            }
        }

        return result;
    }
}
