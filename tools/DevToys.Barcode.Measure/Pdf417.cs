using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Measure;

/// <summary>
/// The two PDF417 cases of docs/reader-open-issues.md. "bounds": how much of each symbol the reported bounds cover, for a few
/// texts in the four orientations. "pairs": two PDF417 symbols in 48 arrangements (the turn of each, and where the second one
/// is), and whether ZXing's multiple reader alone, and the whole reading, find both.
/// </summary>
internal static class Pdf417
{
    private static readonly string[] Texts = ["Hello PDF417 123", "Beta PDF417 second symbol", "First symbol", "Second", "0123456789 ABCDEFGHIJ abcdefghij"];

    internal static void Run(string[] args)
    {
        string what = args.Length > 0 ? args[0] : "all";
        if (what is "all" or "bounds")
        {
            Bounds();
        }

        if (what is "all" or "background")
        {
            Background();
        }

        if (what is "all" or "pairs")
        {
            Pairs();
        }
    }

    private static void Bounds()
    {
        int partial = 0;
        int total = 0;
        foreach (string text in Texts)
        {
            foreach (RotateMode mode in new[] { RotateMode.None, RotateMode.Rotate90, RotateMode.Rotate180, RotateMode.Rotate270 })
            {
                foreach ((int width, int height, int x, int y) in Placements())
                {
                    using Image<Rgba32> symbol = Render(text, mode);
                    using Image<Rgba32> image = Compose(width, height, (symbol, x, y));
                    Rectangle ink = InkBounds(image, new Rectangle(x, y, symbol.Width, symbol.Height));
                    IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);
                    total++;
                    BarcodeReadResult? result = results.FirstOrDefault(r => r.Text == text);
                    double coverage = result is null ? 0 : Coverage(ink, result.Bounds);
                    if (coverage < 0.9)
                    {
                        partial++;
                        Console.WriteLine($"  {text,-32} {mode,-9} {width}x{height} ink {Show(ink)} got {(result is null ? "nothing" : Show(result.Bounds))} ({coverage:P0})");
                    }
                }
            }
        }

        Console.WriteLine($"bounds: {total - partial}/{total} cover at least 90% of the symbol");
    }

    // The symbol of the matrix on busy backgrounds (the "background" degradation of the matrix, more seeds): the case where
    // ZXing reports only one end of it, found on the reduced picture.
    private static void Background()
    {
        using Image<Rgba32> source = Symbols.Render(Symbols.All.First(x => x.Format == BarcodeFormat.PDF_417));
        string text = Symbols.All.First(x => x.Format == BarcodeFormat.PDF_417).ExpectedText;
        int total = 0;
        int read = 0;
        int partial = 0;
        foreach (int level in new[] { 2, 3 })
        {
            for (int seed = 0; seed < 30; seed++)
            {
                using Image<Rgba32> image = Degrade.Apply("background", level, source, seed);

                // Where Degrade put the symbol: the same draws of the same random generator.
                var random = new Random(seed);
                Degrade.Background(2400, 1600, level, random).Dispose();
                var placed = new Point(random.Next(100, 2400 - source.Width - 100), random.Next(100, 1600 - source.Height - 100));
                Rectangle drawn = InkBounds(source, new Rectangle(0, 0, source.Width, source.Height));
                drawn.Offset(placed);

                total++;
                BarcodeReadResult? result = BarcodeHelper.Read(image).FirstOrDefault(r => r.Text == text);
                if (result is null)
                {
                    continue;
                }

                read++;
                double coverage = Coverage(drawn, result.Bounds);
                double excess = 1 - Coverage(result.Bounds, drawn);
                if (coverage < 0.9 || excess > 0.1)
                {
                    partial++;
                    Console.WriteLine($"  level {level} seed {seed,2}: drawn {Show(drawn)} got {Show(result.Bounds)} ({coverage:P0} of the symbol, {excess:P0} outside)");
                }
            }
        }

        Console.WriteLine($"background: {read}/{total} read, {read - partial} with bounds that cover at least 90% of the symbol and are at most 10% outside it");
    }

    // Pictures of a few shapes, the symbol at a few places in each.
    private static IEnumerable<(int Width, int Height, int X, int Y)> Placements()
    {
        foreach ((int width, int height) in new[] { (1400, 1000), (900, 1200), (2000, 1600) })
        {
            foreach (int x in new[] { 40, 150, 300 })
            {
                foreach (int y in new[] { 40, 200, 400 })
                {
                    yield return (width, height, x, y);
                }
            }
        }
    }

    private static void Pairs()
    {
        int arrangements = 0;
        int missedByMultiple = 0;
        int missedInTheEnd = 0;
        RotateMode[] modes = [RotateMode.None, RotateMode.Rotate90, RotateMode.Rotate180, RotateMode.Rotate270];
        foreach (RotateMode firstMode in modes)
        {
            foreach (RotateMode secondMode in modes)
            {
                // Beside, below, and below on the right.
                foreach ((int dx, int dy) in new[] { (1, 0), (0, 1), (1, 1) })
                {
                    using Image<Rgba32> first = Render("Alpha PDF417 first symbol", firstMode);
                    using Image<Rgba32> second = Render("Beta PDF417 second symbol", secondMode);
                    int x2 = dx == 0 ? 120 : 100 + first.Width + 150;
                    int y2 = dy == 0 ? 160 : 100 + first.Height + 150;
                    int width = Math.Max(100 + first.Width, x2 + second.Width) + 100;
                    int height = Math.Max(100 + first.Height, y2 + second.Height) + 100;
                    using Image<Rgba32> image = Compose(width, height, (first, 100, 100), (second, x2, y2));

                    arrangements++;
                    var luminance = new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(image);
                    var hints = new Dictionary<DecodeHintType, object>
                    {
                        [DecodeHintType.TRY_HARDER] = true,
                        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.PDF_417 },
                    };
                    int multiple = MultipleBarcodeDecoder.Decode(luminance, hints, alsoInverted: true, 4, CancellationToken.None).Select(r => r.Text).Distinct().Count();
                    int read = BarcodeHelper.Read(image).Count(r => r.Format == BarcodeFormat.PDF_417);
                    if (multiple < 2)
                    {
                        missedByMultiple++;
                    }

                    if (read < 2)
                    {
                        missedInTheEnd++;
                    }

                    if (multiple < 2 || read < 2)
                    {
                        Console.WriteLine($"  first {firstMode,-9} second {secondMode,-9} at ({dx},{dy}): multiple reader {multiple}, reading {read}");
                    }
                }
            }
        }

        Console.WriteLine($"pairs: {arrangements} arrangements, second symbol missed by the multiple reader in {missedByMultiple}, by the whole reading in {missedInTheEnd}");
    }

    private static Image<Rgba32> Render(string text, RotateMode mode)
    {
        BarcodeGenerationResult<Image<Rgba32>> result = BarcodeHelper.GenerateImage(text, BarcodeFormat.PDF_417);
        Image<Rgba32> image = result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Reason.ToString());
        if (mode != RotateMode.None)
        {
            image.Mutate(c => c.Rotate(mode));
        }

        return image;
    }

    private static Image<Rgba32> Compose(int width, int height, params (Image<Rgba32> Image, int X, int Y)[] symbols)
    {
        var canvas = new Image<Rgba32>(width, height, Color.White);
        foreach ((Image<Rgba32> image, int x, int y) in symbols)
        {
            canvas.Mutate(c => c.DrawImage(image, new Point(x, y), 1f));
        }

        return canvas;
    }

    private static Rectangle InkBounds(Image<Rgba32> image, Rectangle area)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                if (BarcodeHelper.IsDark(image[x, y]))
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }

    private static double Coverage(Rectangle ink, Rectangle bounds)
    {
        var common = Rectangle.Intersect(ink, bounds);
        return common.IsEmpty ? 0 : (double)common.Width * common.Height / ((double)ink.Width * ink.Height);
    }

    private static string Show(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";
}
