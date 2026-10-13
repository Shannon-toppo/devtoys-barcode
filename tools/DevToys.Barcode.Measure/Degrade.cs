using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace DevToys.Barcode.Measure;

internal sealed record Degradation(string Name, double[] Strengths, string Unit)
{
    internal static readonly Degradation[] All =
    [
        new("rotate", [5, 15, 30, 45], "deg"),
        new("blur", [1, 2, 3], "sigma px"),
        new("noise", [20, 40, 70], "sd of 255"),
        new("lowcontrast", [0.5, 0.3, 0.15], "contrast factor"),
        new("lighting", [0.6, 0.4, 0.2], "right edge brightness"),
        new("perspective", [0.05, 0.1, 0.2], "right edge shortened by this fraction at each end"),
        new("jpeg", [30, 15, 5], "quality"),
        new("downscale", [2.0, 1.5, 1.0], "narrowest bar px"),
        new("background", [1, 2, 3], "busyness level"),
    ];
}

internal static class Degrade
{
    internal static Image<Rgba32> Apply(string kind, double strength, Image<Rgba32> source, int seed)
    {
        var random = new Random(seed);
        switch (kind)
        {
            case "rotate":
                return Flatten(source.Clone(c => c.Rotate((float)strength)));
            case "blur":
                return source.Clone(c => c.GaussianBlur((float)strength));
            case "noise":
                return AddNoise(source, strength, random);
            case "lowcontrast":
                return Map(source, (value, x, y) => 170 + ((value - 127.5) * strength));
            case "lighting":
                return Map(source, (value, x, y) => value * (1 - ((1 - strength) * x / (double)source.Width)));
            case "perspective":
                return Perspective(source, strength);
            case "jpeg":
                return Jpeg(source, (int)strength);
            case "downscale":
            {
                double factor = strength / Symbols.NarrowestRun(source);
                int width = Math.Max(8, (int)Math.Round(source.Width * factor));
                int height = Math.Max(8, (int)Math.Round(source.Height * factor));
                return source.Clone(c => c.Resize(width, height));
            }

            case "background":
                return OnBusyBackground(source, (int)strength, random);
            default:
                throw new ArgumentException(kind);
        }
    }

    /// <summary>
    /// A viewing angle: the right edge is shortened by <paramref name="shrink"/> of the height at both ends, the left edge is untouched.
    /// </summary>
    private static Image<Rgba32> Perspective(Image<Rgba32> source, double shrink)
    {
        double w = source.Width, h = source.Height;
        double[][] from = [[0, 0], [w, 0], [w, h], [0, h]];
        double[][] to = [[0, 0], [w, shrink * h], [w, h - (shrink * h)], [0, h]];
        double[] hm = Homography(from, to);
        var m = new System.Numerics.Matrix4x4(
            (float)hm[0], (float)hm[3], 0, (float)hm[6],
            (float)hm[1], (float)hm[4], 0, (float)hm[7],
            0, 0, 1, 0,
            (float)hm[2], (float)hm[5], 0, 1);
        return Flatten(source.Clone(c => c.Transform(new ProjectiveTransformBuilder().AppendMatrix(m))));
    }

    /// <summary>Solves for h11..h32 (h33 = 1) such that the homography maps each from point to its to point.</summary>
    private static double[] Homography(double[][] from, double[][] to)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i][0], y = from[i][1], u = to[i][0], v = to[i][1];
            int r = 2 * i;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -u * x; a[r, 7] = -u * y; a[r, 8] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -v * x; a[r + 1, 7] = -v * y; a[r + 1, 8] = v;
        }

        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            for (int k = 0; k < 9; k++)
            {
                (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            }

            for (int r = 0; r < 8; r++)
            {
                if (r == col)
                {
                    continue;
                }

                double factor = a[r, col] / a[col, col];
                for (int k = col; k < 9; k++)
                {
                    a[r, k] -= factor * a[col, k];
                }
            }
        }

        var result = new double[8];
        for (int i = 0; i < 8; i++)
        {
            result[i] = a[i, 8] / a[i, i];
        }

        return result;
    }

    private static Image<Rgba32> Flatten(Image<Rgba32> image)
    {
        var canvas = new Image<Rgba32>(image.Width, image.Height, Color.White);
        canvas.Mutate(c => c.DrawImage(image, 1f));
        image.Dispose();
        return canvas;
    }

    private static Image<Rgba32> Map(Image<Rgba32> source, Func<double, int, int, double> map)
    {
        Image<Rgba32> result = source.Clone();
        result.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    byte value = Clamp(map((row[x].R + row[x].G + row[x].B) / 3.0, x, y));
                    row[x] = new Rgba32(value, value, value, 255);
                }
            }
        });
        return result;
    }

    private static Image<Rgba32> AddNoise(Image<Rgba32> source, double sd, Random random)
    {
        return Map(source, (value, x, y) => value + (Gaussian(random) * sd));
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static Image<Rgba32> Jpeg(Image<Rgba32> source, int quality)
    {
        using var stream = new MemoryStream();
        source.Save(stream, new JpegEncoder { Quality = quality });
        stream.Position = 0;
        return Image.Load<Rgba32>(stream);
    }

    /// <summary>The symbol on a white label, placed on a large busy background.</summary>
    internal static Image<Rgba32> OnBusyBackground(Image<Rgba32> symbol, int level, Random random)
    {
        const int Width = 2400;
        const int Height = 1600;
        Image<Rgba32> canvas = Background(Width, Height, level, random);
        int x = random.Next(100, Width - symbol.Width - 100);
        int y = random.Next(100, Height - symbol.Height - 100);
        canvas.Mutate(c => c.DrawImage(symbol, new Point(x, y), 1f));
        return canvas;
    }

    internal static Image<Rgba32> Background(int width, int height, int level, Random random)
    {
        var canvas = new Image<Rgba32>(width, height, Color.White);
        canvas.ProcessPixelRows(accessor =>
        {
            double[] phase = [random.NextDouble() * 6, random.NextDouble() * 6, random.NextDouble() * 6];
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    // Smooth "photo-like" variation, stronger with the level.
                    double v = 200
                        + (25 * level * Math.Sin((x / 90.0) + phase[0]) * Math.Cos((y / 70.0) + phase[1]))
                        + (10 * level * Math.Sin((x + y) / 23.0 + phase[2]))
                        + (random.NextDouble() - 0.5) * 12 * level;
                    byte b = Clamp(v);
                    row[x] = new Rgba32(b, b, b, 255);
                }
            }
        });

        if (level >= 2)
        {
            // Text-like strokes and blocks.
            int count = level == 2 ? 600 : 2500;
            canvas.ProcessPixelRows(accessor =>
            {
                for (int i = 0; i < count; i++)
                {
                    int x0 = random.Next(width - 60);
                    int y0 = random.Next(height - 20);
                    int w = random.Next(2, level == 2 ? 30 : 60);
                    int h = random.Next(2, level == 2 ? 14 : 20);
                    byte shade = (byte)random.Next(level == 2 ? 80 : 0, level == 2 ? 170 : 110);
                    for (int y = y0; y < y0 + h; y++)
                    {
                        accessor.GetRowSpan(y).Slice(x0, w).Fill(new Rgba32(shade, shade, shade, 255));
                    }
                }
            });
        }

        return canvas;
    }
}
