using System.Diagnostics;
using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DevToys.Barcode.Measure;

/// <summary>Counts the barcodes reported on generated images that contain none.</summary>
internal static class FalsePositives
{
    internal static void Run(string[] args)
    {
        int perCategory = args.Length > 0 ? int.Parse(args[0]) : 1;
        var images = new List<(string Category, string Name, Func<Image<Rgba32>> Make)>();

        for (int i = 0; i < 12 * perCategory; i++)
        {
            int seed = i;
            images.Add(("noise", $"binary-{i}", () => Noise(800, 600, seed, kind: seed % 4)));
        }

        for (int i = 0; i < 12 * perCategory; i++)
        {
            int seed = i;
            images.Add(("gradient", $"gradient-{i}", () => Gradient(800, 600, seed)));
        }

        // Stripes at various pitches, directions and duty cycles: the classic 1D false positive.
        int[] pitches = [1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 30, 40];
        foreach (int pitch in pitches)
        {
            for (int direction = 0; direction < 4 * perCategory; direction++)
            {
                int p = pitch;
                int d = direction;
                images.Add(("stripes", $"stripes-{pitch}-{direction}", () => Stripes(800, 600, p, d)));
            }
        }

        for (int i = 0; i < 20 * perCategory; i++)
        {
            int seed = i;
            images.Add(("text-like", $"text-{i}", () => TextLike(800, 600, seed)));
        }

        for (int i = 0; i < 20 * perCategory; i++)
        {
            int seed = i;
            images.Add(("photo-like", $"photo-{i}", () => PhotoLike(800, 600, seed)));
        }

        for (int i = 0; i < 10 * perCategory; i++)
        {
            int seed = i;
            images.Add(("grid", $"grid-{i}", () => Grid(800, 600, seed)));
        }

        var hits = new List<(string Category, string Name, string Format, string Text)>();
        var sync = new object();
        var timings = new List<(string Name, long Ms)>();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(images, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
        {
            using Image<Rgba32> image = item.Make();
            var watch = Stopwatch.StartNew();
            IReadOnlyList<BarcodeReadResult> found = BarcodeHelper.Read(image);
            lock (sync)
            {
                timings.Add((item.Name, watch.ElapsedMilliseconds));
            }
            lock (sync)
            {
                foreach (BarcodeReadResult r in found)
                {
                    hits.Add((item.Category, item.Name, r.Format.ToString(), $"{r.Text}  bounds {r.Bounds.Width}x{r.Bounds.Height}"));
                }
            }
        });

        Console.WriteLine($"{images.Count} images without barcode, {sw.Elapsed.TotalSeconds:0.0}s; slowest: {string.Join(", ", timings.OrderByDescending(t => t.Ms).Take(5).Select(t => $"{t.Name} {t.Ms} ms"))}");
        Console.WriteLine($"images with at least one report: {hits.Select(h => h.Name).Distinct().Count()}");
        Console.WriteLine("by format:");
        foreach (var g in hits.GroupBy(h => h.Format).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {g.Key,-14} {g.Count()}  ({string.Join(", ", g.GroupBy(h => h.Category).Select(c => $"{c.Key}:{c.Count()}"))})");
        }

        Console.WriteLine("by category (images with a hit / images):");
        foreach (var g in images.GroupBy(i => i.Category))
        {
            int hit = hits.Where(h => h.Category == g.Key).Select(h => h.Name).Distinct().Count();
            Console.WriteLine($"  {g.Key,-11} {hit}/{g.Count()}");
        }

        foreach (var h in hits.OrderBy(h => h.Format).ThenBy(h => h.Name).Take(60))
        {
            Console.WriteLine($"    {h.Name,-16} {h.Format,-13} {h.Text}");
        }
    }

    internal static Image<Rgba32> Noise(int w, int h, int seed, int kind)
    {
        var random = new Random(seed);
        var image = new Image<Rgba32>(w, h);
        int block = kind == 3 ? 3 : 1;
        image.ProcessPixelRows(a =>
        {
            for (int y = 0; y < a.Height; y++)
            {
                Span<Rgba32> row = a.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    byte v = kind switch
                    {
                        0 => (byte)(random.Next(2) * 255),
                        1 => (byte)random.Next(256),
                        2 => (byte)(random.Next(100) < 8 ? 0 : 255), // salt and pepper on white
                        _ => (byte)((BlockHash(seed, x / block, y / block) & 1) * 255),
                    };
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }
        });
        return image;
    }

    /// <summary>
    /// A hash of the block that is the same in every process (<see cref="HashCode"/> is seeded per process, which made
    /// these pictures, and so what was read from them, differ from one run to the next).
    /// </summary>
    private static uint BlockHash(int seed, int x, int y)
    {
        uint h = (uint)seed * 0x9E3779B1u;
        h = (h ^ (uint)x) * 0x85EBCA77u;
        h = (h ^ (uint)y) * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x27D4EB2Fu;
        return h ^ (h >> 13);
    }

    internal static Image<Rgba32> Gradient(int w, int h, int seed)
    {
        var random = new Random(seed);
        double angle = random.NextDouble() * Math.PI;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        bool radial = seed % 3 == 0;
        double steps = seed % 2 == 0 ? 256 : random.Next(3, 20);
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(a =>
        {
            for (int y = 0; y < a.Height; y++)
            {
                Span<Rgba32> row = a.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    double t = radial
                        ? Math.Sqrt(Math.Pow(x - (w / 2.0), 2) + Math.Pow(y - (h / 2.0), 2)) / (w / 2.0)
                        : (((x * cos) + (y * sin)) / (w * Math.Abs(cos) + h * Math.Abs(sin)));
                    t = Math.Clamp(t, 0, 1);
                    byte v = (byte)(Math.Floor(t * steps) / steps * 255);
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }
        });
        return image;
    }

    internal static Image<Rgba32> Stripes(int w, int h, int pitch, int direction)
    {
        double duty = direction % 2 == 0 ? 0.5 : 0.25;
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(a =>
        {
            for (int y = 0; y < a.Height; y++)
            {
                Span<Rgba32> row = a.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    int coordinate = (direction / 2) % 2 == 0 ? x : x + y;
                    bool dark = (coordinate % (pitch * 2)) < (pitch * 2 * duty);
                    if (direction >= 4)
                    {
                        dark = (y % (pitch * 2)) < (pitch * 2 * duty);
                    }

                    byte v = dark ? (byte)0 : (byte)255;
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }
        });
        return image;
    }

    internal static Image<Rgba32> TextLike(int w, int h, int seed)
    {
        var random = new Random(seed);
        var image = new Image<Rgba32>(w, h, Color.White);
        int lineHeight = random.Next(10, 28);
        image.ProcessPixelRows(a =>
        {
            for (int top = 20; top + lineHeight < h - 20; top += lineHeight * 2)
            {
                int x = 30;
                while (x < w - 60)
                {
                    int wordLength = random.Next(10, 70);
                    for (int c = 0; c < wordLength && x < w - 30; c += 6)
                    {
                        int glyphHeight = random.Next(lineHeight / 2, lineHeight);
                        int glyphWidth = random.Next(1, 4);
                        for (int y = top + lineHeight - glyphHeight; y < top + lineHeight; y++)
                        {
                            a.GetRowSpan(y).Slice(x, glyphWidth).Fill(new Rgba32(0, 0, 0, 255));
                        }

                        x += random.Next(3, 7);
                    }

                    x += random.Next(8, 20);
                }
            }
        });
        return image;
    }

    internal static Image<Rgba32> PhotoLike(int w, int h, int seed)
    {
        var random = new Random(seed);
        Image<Rgba32> image = Degrade.Background(w, h, 1 + (seed % 3), random);
        if (seed % 4 == 0)
        {
            image.Mutate(c => c.GaussianBlur(1.5f));
        }

        return image;
    }

    internal static Image<Rgba32> Grid(int w, int h, int seed)
    {
        int cell = 4 + (seed * 3);
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(a =>
        {
            for (int y = 0; y < a.Height; y++)
            {
                Span<Rgba32> row = a.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    byte v = (((x / cell) + (y / cell)) % 2 == 0) ? (byte)0 : (byte)255;
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }
        });
        return image;
    }
}
