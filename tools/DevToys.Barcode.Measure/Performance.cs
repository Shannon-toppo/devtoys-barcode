using System.Diagnostics;
using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Measure;

/// <summary>Reading times on large images (run in Release).</summary>
internal static class Performance
{
    internal static void Run(string[] args)
    {
        string imagesDirectory = args.Length > 0 ? args[0] : "test-images";
        int runs = args.Length > 1 ? int.Parse(args[1]) : 3;

        var cases = new List<(string Name, Func<Image<Rgba32>> Make)>
        {
            ("6000x4500 JPEG, no barcode (test-images)", () => Image.Load<Rgba32>(Path.Combine(imagesDirectory, "large-no-barcode.jpg"))),
            ("4000x3000 blank", () => new Image<Rgba32>(4000, 3000, Color.White)),
            ("4000x3000 photo-like texture", () => Degrade.Background(4000, 3000, 2, new Random(5))),
            ("4000x3000 smooth texture, one small EAN-13", () =>
            {
                Image<Rgba32> background = Degrade.Background(4000, 3000, 1, new Random(6));
                using Image<Rgba32> symbol = Symbols.Render(Symbols.All[1]);
                background.Mutate(c => c.DrawImage(symbol, new Point(2500, 1800), 1f));
                return background;
            }),
            ("4000x3000 one small Data Matrix on white", () =>
            {
                var canvas = new Image<Rgba32>(4000, 3000, Color.White);
                using Image<Rgba32> symbol = Symbols.Render(Symbols.All[9]);
                canvas.Mutate(c => c.DrawImage(symbol, new Point(3000, 2200), 1f));
                return canvas;
            }),
        };

        // Warm up (JIT) on something small.
        using (var warm = new Image<Rgba32>(300, 200, Color.White))
        {
            BarcodeHelper.Read(warm);
        }

        Console.WriteLine($"| image | runs (ms) | median ms | found |");
        Console.WriteLine("|---|---|---|---|");
        foreach ((string name, Func<Image<Rgba32>> make) in cases)
        {
            using Image<Rgba32> image = make();
            var times = new List<long>();
            string found = string.Empty;
            var steps = new System.Collections.Concurrent.ConcurrentDictionary<string, List<long>>();
            for (int i = 0; i < runs; i++)
            {
                BarcodeHelper.Trace = (n, ms) => steps.GetOrAdd(n, _ => []).Add(ms);
                var sw = Stopwatch.StartNew();
                IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);
                times.Add(sw.ElapsedMilliseconds);
                found = string.Join(", ", results.Select(r => $"{r.Format}:{r.Text}"));
            }

            BarcodeHelper.Trace = null;
            Console.WriteLine($"| {name} | {string.Join(" / ", times)} | {times.Order().ElementAt(times.Count / 2)} | {(found.Length == 0 ? "-" : found)} |");
            Console.WriteLine("    steps (median ms): " + string.Join(", ", steps.Select(kv => $"{kv.Key} {kv.Value.Order().ElementAt(kv.Value.Count / 2)}")));
        }

        foreach (int index in new[] { 0, 2, 3 })
        {
            Console.WriteLine($"cancellation latency, {cases[index].Name}:");
            MeasureCancellation(cases[index].Make);
        }
    }

    /// <summary>Cost of each building block of the reader on each image (single run each, Release).</summary>
    internal static void Breakdown(string[] args)
    {
        string imagesDirectory = args.Length > 0 ? args[0] : "test-images";
        var images = new (string Name, Func<Image<Rgba32>> Make)[]
        {
            ("6000x4500 JPEG no barcode", () => Image.Load<Rgba32>(Path.Combine(imagesDirectory, "large-no-barcode.jpg"))),
            ("4000x3000 blank", () => new Image<Rgba32>(4000, 3000, Color.White)),
            ("4000x3000 texture", () => Degrade.Background(4000, 3000, 2, new Random(5))),
        };
        using (var warm = new Image<Rgba32>(300, 200, Color.White))
        {
            BarcodeHelper.Read(warm);
        }

        foreach ((string name, Func<Image<Rgba32>> make) in images)
        {
            using Image<Rgba32> image = make();
            Console.WriteLine(name);
            static long Time(Action action)
            {
                var sw = Stopwatch.StartNew();
                action();
                return sw.ElapsedMilliseconds;
            }

            var reader = new ZXing.ImageSharp.BarcodeReader<Rgba32> { AutoRotate = false, Options = { TryHarder = true, TryInverted = true } };
            Console.WriteLine($"  region finder                      {Time(() => BarcodeRegionFinder.FindRegions(image)),6} ms");
            Console.WriteLine($"  ZXing DecodeMultiple upright       {Time(() => reader.DecodeMultiple(image)),6} ms");
            Console.WriteLine($"  ZXing Decode (single) upright      {Time(() => reader.Decode(image)),6} ms");
            Image<Rgba32>? rotated = null;
            Console.WriteLine($"  Rotate90 clone                     {Time(() => rotated = image.Clone(c => c.Rotate(RotateMode.Rotate90))),6} ms");
            Console.WriteLine($"  ZXing DecodeMultiple rotated90     {Time(() => reader.DecodeMultiple(rotated!)),6} ms");
            rotated!.Dispose();
            Image<Rgba32>? inverted = null;
            Console.WriteLine($"  Invert clone                       {Time(() => inverted = image.Clone(c => c.Invert())),6} ms");
            inverted!.Dispose();
            var oneD = new ZXing.ImageSharp.BarcodeReader<Rgba32> { AutoRotate = false, Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.EAN_13, BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.ITF, BarcodeFormat.CODABAR, BarcodeFormat.UPC_A, BarcodeFormat.EAN_8, BarcodeFormat.UPC_E, BarcodeFormat.CODE_93] } };
            Console.WriteLine($"  ZXing DecodeMultiple 1D formats    {Time(() => oneD.DecodeMultiple(image)),6} ms");
            var matrix = new ZXing.ImageSharp.BarcodeReader<Rgba32> { AutoRotate = false, Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.QR_CODE] } };
            Console.WriteLine($"  ZXing DecodeMultiple QR/DM/Aztec   {Time(() => matrix.DecodeMultiple(image)),6} ms");
            var pdf = new ZXing.ImageSharp.BarcodeReader<Rgba32> { AutoRotate = false, Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.PDF_417] } };
            Console.WriteLine($"  ZXing DecodeMultiple PDF417        {Time(() => pdf.DecodeMultiple(image)),6} ms");
            Image<Rgba32>? small = null;
            Console.WriteLine($"  Resize to 1600 long side           {Time(() => small = image.Clone(c => c.Resize(new ResizeOptions { Size = new Size(1600, 1600), Mode = ResizeMode.Max }))),6} ms");
            Console.WriteLine($"  ZXing DecodeMultiple on 1600px     {Time(() => reader.DecodeMultiple(small!)),6} ms");
            small!.Dispose();
        }
    }

    private static void MeasureCancellation(Func<Image<Rgba32>> make)
    {
        using Image<Rgba32> image = make();
        var latencies = new List<long>();
        for (int delayMs = 20; delayMs <= 1200; delayMs += 40)
        {
            using var cts = new CancellationTokenSource();
            var task = Task.Run(() =>
            {
                try
                {
                    BarcodeHelper.Read(image, cts.Token);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
            });
            Thread.Sleep(delayMs);
            var sw = Stopwatch.StartNew();
            cts.Cancel();
            bool cancelled = task.Result;
            if (cancelled)
            {
                latencies.Add(sw.ElapsedMilliseconds);
            }
        }

        latencies.Sort();
        Console.WriteLine($"  {latencies.Count} cancellations: median {latencies[latencies.Count / 2]} ms, 90th percentile {latencies[(int)(latencies.Count * 0.9)]} ms, max {latencies[^1]} ms");
    }
}
