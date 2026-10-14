using System.Collections.Concurrent;
using System.Diagnostics;
using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DevToys.Barcode.Measure;

/// <summary>
/// Writes every result (format, text and bounds, in order) read from about a thousand images, one line per image: the
/// matrix images, the false positive images, pictures holding several symbols in any orientation, and the performance
/// images. Run it before and after a change and compare the two files to see what the change does to the results.
/// </summary>
internal static class Corpus
{
    internal static void Run(string[] args)
    {
        string output = args[0];
        string imagesDirectory = args.Length > 1 ? args[1] : "test-images";
        var items = new List<(string Key, Func<Image<Rgba32>> Make)>();

        // The matrix cells, with the same seeds as the matrix command.
        int index = 0;
        foreach (SymbolSpec symbol in Symbols.All)
        {
            foreach (Degradation degradation in Degradation.All)
            {
                foreach (double strength in degradation.Strengths)
                {
                    bool random = degradation.Name is "noise" or "background" or "blur" or "jpeg";
                    int i = index++;
                    for (int t = 0; t < (random ? 3 : 1); t++)
                    {
                        int seed = 1000 + (i * 10) + t;
                        SymbolSpec s = symbol;
                        Degradation d = degradation;
                        double st = strength;
                        items.Add(($"matrix {s.Name} {d.Name} {st} {t}", () =>
                        {
                            using Image<Rgba32> source = Symbols.Render(s);
                            return Degrade.Apply(d.Name, st, source, seed);
                        }));
                    }
                }
            }
        }

        // The false positive images of "fp 2".
        for (int i = 0; i < 24; i++)
        {
            int seed = i;
            items.Add(($"fp noise {i}", () => FalsePositives.Noise(800, 600, seed, kind: seed % 4)));
            items.Add(($"fp gradient {i}", () => FalsePositives.Gradient(800, 600, seed)));
        }

        foreach (int pitch in new[] { 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 30, 40 })
        {
            for (int direction = 0; direction < 8; direction++)
            {
                int p = pitch, d = direction;
                items.Add(($"fp stripes {p} {d}", () => FalsePositives.Stripes(800, 600, p, d)));
            }
        }

        for (int i = 0; i < 40; i++)
        {
            int seed = i;
            items.Add(($"fp text {i}", () => FalsePositives.TextLike(800, 600, seed)));
            items.Add(($"fp photo {i}", () => FalsePositives.PhotoLike(800, 600, seed)));
        }

        for (int i = 0; i < 20; i++)
        {
            int seed = i;
            items.Add(($"fp grid {i}", () => FalsePositives.Grid(800, 600, seed)));
        }

        // Several symbols, in any of the four orientations, sometimes slightly tilted, sometimes on a textured background.
        for (int i = 0; i < 120; i++)
        {
            int seed = i;
            items.Add(($"composite {seed}", () => Composite(seed)));
        }

        // The performance images.
        items.Add(("perf large-no-barcode", () => Image.Load<Rgba32>(Path.Combine(imagesDirectory, "large-no-barcode.jpg"))));
        items.Add(("perf texture", () => Degrade.Background(4000, 3000, 2, new Random(5))));
        items.Add(("perf ean13", () =>
        {
            Image<Rgba32> background = Degrade.Background(4000, 3000, 1, new Random(6));
            using Image<Rgba32> symbol = Symbols.Render(Symbols.All[1]);
            background.Mutate(c => c.DrawImage(symbol, new Point(2500, 1800), 1f));
            return background;
        }));
        items.Add(("perf datamatrix", () =>
        {
            var canvas = new Image<Rgba32>(4000, 3000, Color.White);
            using Image<Rgba32> symbol = Symbols.Render(Symbols.All[9]);
            canvas.Mutate(c => c.DrawImage(symbol, new Point(3000, 2200), 1f));
            return canvas;
        }));
        items.Add(("three-barcodes.png", () => Image.Load<Rgba32>(Path.Combine(imagesDirectory, "three-barcodes.png"))));

        var lines = new ConcurrentDictionary<string, string>();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
        {
            using Image<Rgba32> image = item.Make();
            string text;
            try
            {
                text = string.Join(" | ", BarcodeHelper.Read(image).Select(r => $"{r.Format}:{r.Text}@{r.Bounds.X},{r.Bounds.Y},{r.Bounds.Width},{r.Bounds.Height}"));
            }
            catch (Exception ex)
            {
                text = "EXCEPTION " + ex.GetType().Name;
            }

            lines[item.Key] = text;
        });

        File.WriteAllLines(output, lines.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}\t{kv.Value}"));
        Console.WriteLine($"{items.Count} images in {sw.Elapsed.TotalSeconds:0}s");
    }

    private static Image<Rgba32> Composite(int seed)
    {
        var random = new Random(seed);
        (int width, int height) = (seed % 10) switch
        {
            0 => (4000, 3000),
            1 or 2 or 3 => (2400, 1800),
            4 or 5 or 6 => (1600, 1200),
            _ => (1100, 800),
        };
        Image<Rgba32> canvas = seed % 4 == 3 ? Degrade.Background(width, height, 1, random) : new Image<Rgba32>(width, height, Color.White);

        // A grid of cells, one symbol in some of them.
        int columns = random.Next(1, 4);
        int rows = random.Next(1, 4);
        int cellWidth = width / columns;
        int cellHeight = height / rows;
        for (int c = 0; c < columns; c++)
        {
            for (int r = 0; r < rows; r++)
            {
                if (random.NextDouble() < 0.25)
                {
                    continue;
                }

                SymbolSpec spec = Symbols.All[random.Next(Symbols.All.Length)];
                using Image<Rgba32> symbol = Symbols.Render(spec);
                int turn = random.Next(4);
                double tilt = random.NextDouble() < 0.2 ? random.Next(-20, 21) : 0;
                symbol.Mutate(x =>
                {
                    if (turn > 0)
                    {
                        x.Rotate((RotateMode)(turn * 90));
                    }

                    if (tilt != 0)
                    {
                        x.Rotate((float)tilt).BackgroundColor(Color.White);
                    }
                });

                double fit = Math.Min(1, Math.Min((cellWidth - 20) / (double)symbol.Width, (cellHeight - 20) / (double)symbol.Height));
                if (fit < 1)
                {
                    symbol.Mutate(x => x.Resize(Math.Max(1, (int)(symbol.Width * fit)), Math.Max(1, (int)(symbol.Height * fit))));
                }

                int x0 = (c * cellWidth) + random.Next(0, Math.Max(1, cellWidth - symbol.Width));
                int y0 = (r * cellHeight) + random.Next(0, Math.Max(1, cellHeight - symbol.Height));
                canvas.Mutate(x => x.DrawImage(symbol, new Point(x0, y0), 1f));
            }
        }

        return canvas;
    }
}
