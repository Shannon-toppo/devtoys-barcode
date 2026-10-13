using System.Diagnostics;
using DevToys.Barcode.Helpers;
using DevToys.Barcode.Measure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Tests;

/// <summary>
/// Reading of degraded pictures. The cases come from the robustness matrix of the measurement tool
/// (<c>tools/DevToys.Barcode.Measure</c>, command <c>matrix</c>), which renders every format, applies one degradation
/// at a few strengths and records whether <see cref="BarcodeHelper.Read(Image{Rgba32}, CancellationToken, IReadOnlyCollection{BarcodeFormat}?)"/>
/// returns the right format and text.
///
/// Rule used to pick the cases: for Code 128 (a flat 1D symbol with narrow bars), EAN-13 (another 1D symbol),
/// Data Matrix and QR Code (2D), and for each of the nine degradations, the mildest and the strongest strength that
/// the matrix shows as reliably read. They are the ones that exercise each step of the reader (right-angle passes,
/// tilted passes, normalisation, noise reduction, enlarging, confirmation of 1D results).
/// The other cells of the matrix, including those that fail, are not tests: they are measurements.
/// </summary>
public class BarcodeReadingRobustnessTests
{
    public static TheoryData<string, string, double> PassingCells => new()
    {
        { "Code128", "rotate", 5.0 },
        { "Code128", "rotate", 45.0 },
        { "EAN-13", "rotate", 5.0 },
        { "EAN-13", "rotate", 45.0 },
        { "DataMatrix", "rotate", 5.0 },
        { "DataMatrix", "rotate", 45.0 },
        { "QR", "rotate", 5.0 },
        { "QR", "rotate", 45.0 },
        { "Code128", "blur", 1.0 },
        { "EAN-13", "blur", 1.0 },
        { "EAN-13", "blur", 2.0 },
        { "DataMatrix", "blur", 1.0 },
        { "DataMatrix", "blur", 3.0 },
        { "QR", "blur", 1.0 },
        { "QR", "blur", 3.0 },
        { "Code128", "noise", 20.0 },
        { "Code128", "noise", 70.0 },
        { "EAN-13", "noise", 20.0 },
        { "EAN-13", "noise", 70.0 },
        { "DataMatrix", "noise", 20.0 },
        { "DataMatrix", "noise", 40.0 },
        { "QR", "noise", 20.0 },
        { "QR", "noise", 40.0 },
        { "Code128", "lowcontrast", 0.5 },
        { "Code128", "lowcontrast", 0.15 },
        { "EAN-13", "lowcontrast", 0.5 },
        { "EAN-13", "lowcontrast", 0.15 },
        { "DataMatrix", "lowcontrast", 0.5 },
        { "DataMatrix", "lowcontrast", 0.15 },
        { "QR", "lowcontrast", 0.5 },
        { "QR", "lowcontrast", 0.3 },
        { "Code128", "lighting", 0.6 },
        { "Code128", "lighting", 0.4 },
        { "EAN-13", "lighting", 0.6 },
        { "EAN-13", "lighting", 0.2 },
        { "DataMatrix", "lighting", 0.6 },
        { "DataMatrix", "lighting", 0.2 },
        { "QR", "lighting", 0.6 },
        { "QR", "lighting", 0.2 },
        { "Code128", "perspective", 0.05 },
        { "Code128", "perspective", 0.2 },
        { "EAN-13", "perspective", 0.05 },
        { "EAN-13", "perspective", 0.2 },
        { "DataMatrix", "perspective", 0.05 },
        { "DataMatrix", "perspective", 0.2 },
        { "QR", "perspective", 0.05 },
        { "QR", "perspective", 0.1 },
        { "Code128", "jpeg", 30.0 },
        { "Code128", "jpeg", 5.0 },
        { "EAN-13", "jpeg", 30.0 },
        { "EAN-13", "jpeg", 5.0 },
        { "DataMatrix", "jpeg", 30.0 },
        { "DataMatrix", "jpeg", 5.0 },
        { "QR", "jpeg", 30.0 },
        { "QR", "jpeg", 5.0 },
        { "Code128", "downscale", 2.0 },
        { "Code128", "downscale", 1.0 },
        { "EAN-13", "downscale", 2.0 },
        { "EAN-13", "downscale", 1.0 },
        { "DataMatrix", "downscale", 2.0 },
        { "DataMatrix", "downscale", 1.5 },
        { "QR", "downscale", 2.0 },
        { "QR", "downscale", 1.5 },
        { "Code128", "background", 1.0 },
        { "Code128", "background", 3.0 },
        { "EAN-13", "background", 1.0 },
        { "EAN-13", "background", 3.0 },
        { "DataMatrix", "background", 1.0 },
        { "QR", "background", 1.0 },
        { "QR", "background", 3.0 },
    };

    [Theory]
    [MemberData(nameof(PassingCells))]
    public void DegradedSymbol_IsStillRead(string symbolName, string degradation, double strength)
    {
        SymbolSpec symbol = Symbols.All.Single(s => s.Name == symbolName);
        using Image<Rgba32> source = Symbols.Render(symbol);
        using Image<Rgba32> degraded = Degrade.Apply(degradation, strength, source, seed: 1001);

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(degraded);

        Assert.Contains(results, r => r.Format == symbol.Format && r.Text == symbol.ExpectedText);
    }

    [Fact]
    public void OneHorizontalAndOneVerticalBarcode_AreBothRead()
    {
        // The 90 degree pass used to run only when the upright one found nothing.
        using Image<Rgba32> vertical = TestImages.Transform(TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), c => c.Rotate(RotateMode.Rotate90));
        using Image<Rgba32> image = TestImages.Compose(
            1000,
            800,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Horizontal-1"), 20, 20),
            (vertical.Clone(), 600, 20));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(
            new[] { (BarcodeFormat.CODE_128, "Horizontal-1"), (BarcodeFormat.EAN_13, "4006381333931") },
            results.OrderBy(r => r.X).Select(r => (r.Format, r.Text)));
    }

    [Fact]
    public void TiltedBarcode_HasBoundsInTheOriginalImage()
    {
        // Found by the "tilted" pass: the bounds have to be mapped back from the rotated picture.
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931");
        using Image<Rgba32> tilted = Degrade.Apply("rotate", 45, symbol, seed: 1);
        using Image<Rgba32> image = TestImages.Compose(1400, 1000, (tilted.Clone(), 700, 300));

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));

        Assert.Equal("4006381333931", result.Text);
        var expected = new Rectangle(700, 300, tilted.Width, tilted.Height);
        Assert.True(expected.Contains((int)result.X, (int)result.Y), $"centre {result.X},{result.Y} is not in {expected}");
        Assert.True(result.Bounds.Width <= tilted.Width + 40 && result.Bounds.Height <= tilted.Height + 40, $"bounds {result.Bounds} are larger than the symbol {tilted.Width}x{tilted.Height}");
    }

    [Fact]
    public void FlatImage_ReadsNothing_Immediately()
    {
        using var flat = new Image<Rgba32>(4000, 3000, Color.White);
        var stopwatch = Stopwatch.StartNew();

        Assert.Empty(BarcodeHelper.Read(flat));

        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    // Lines of text that used to read as UPC-E (the text-like generator of the measurement tool, with the seeds that did).
    [Theory]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(34)]
    [InlineData(38)]
    [InlineData(40)]
    [InlineData(52)]
    [InlineData(65)]
    public void LinesOfText_AreNotReadAsABarcode(int seed)
    {
        using Image<Rgba32> image = FalsePositives.TextLike(800, 600, seed);

        Assert.Empty(BarcodeHelper.Read(image));
    }

    // Stripes that read as EAN-13 until 1D results had to be confirmed by decoding them again once enlarged.
    [Theory]
    [InlineData(2, 10)]
    [InlineData(2, 11)]
    public void Stripes_AreNotReadAsABarcode(int pitch, int direction)
    {
        using Image<Rgba32> image = FalsePositives.Stripes(800, 600, pitch, direction);

        Assert.Empty(BarcodeHelper.Read(image));
    }

    [Fact]
    public void Cancelling_ALongReading_ReturnsPromptly()
    {
        using Image<Rgba32> noise = FalsePositives.Noise(3000, 3000, seed: 3, kind: 1);
        using var cts = new CancellationTokenSource();
        Task reading = Task.Run(() =>
        {
            try
            {
                BarcodeHelper.Read(noise, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        });

        Thread.Sleep(300);
        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();
        reading.Wait();

        // Measured at 0.1 to 0.3 seconds on the slowest pictures; the limit leaves room for a loaded machine.
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"took {stopwatch.ElapsedMilliseconds} ms to return");
    }
}
