using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Tests;

public class BarcodeReadingTests
{
    [Fact]
    public void TwoStackedBarcodes_AreBothFound_TopFirst()
    {
        using Image<Rgba32> image = TestImages.Compose(
            800,
            420,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 20),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 20, 220));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(
            new[] { (BarcodeFormat.CODE_128, "Hello-123"), (BarcodeFormat.EAN_13, "4006381333931") },
            results.Select(r => (r.Format, r.Text)));
    }

    [Fact]
    public void StackedBarcodes_AreOrderedByPositionNotByContent()
    {
        // Same two barcodes as above with their places swapped: the order must follow the layout.
        using Image<Rgba32> image = TestImages.Compose(
            800,
            420,
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 20, 20),
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 220));

        Assert.Equal(
            new[] { "4006381333931", "Hello-123" },
            BarcodeHelper.Read(image).Select(r => r.Text));
    }

    [Fact]
    public void SideBySideBarcodes_AreOrderedLeftToRight_EvenWhenTheRightOneIsSlightlyHigher()
    {
        using Image<Rgba32> image = TestImages.Compose(
            1200,
            300,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Left"), 20, 40),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 640, 25));

        Assert.Equal(
            new[] { "Left", "4006381333931" },
            BarcodeHelper.Read(image).Select(r => r.Text));
    }

    [Fact]
    public void ThreeDifferentBarcodes_OneOneDAndOneTwoD_AreAllFound_InReadingOrder()
    {
        using Image<Rgba32> image = TestImages.Compose(
            1100,
            800,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Top-left"), 20, 20),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 20, 600),
            (TestImages.Generate(BarcodeFormat.AZTEC, "Aztec text"), 560, 20));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        // Code 128 at the top left, Aztec (taller) at the top right, EAN-13 at the bottom.
        Assert.Equal(
            new[] { (BarcodeFormat.CODE_128, "Top-left"), (BarcodeFormat.AZTEC, "Aztec text"), (BarcodeFormat.EAN_13, "4006381333931") },
            results.Select(r => (r.Format, r.Text)));
    }

    [Fact]
    public void SixBarcodesInAGrid_AreAllFound_InReadingOrder()
    {
        // More symbols than ZXing's multiple reader finds in one go: the helper looks again around the ones it found.
        using Image<Rgba32> image = TestImages.Compose(
            1500,
            1300,
            (TestImages.Generate(BarcodeFormat.CODE_128, "A1"), 20, 20),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 700, 20),
            (TestImages.Generate(BarcodeFormat.CODE_39, "C3"), 20, 250),
            (TestImages.Generate(BarcodeFormat.DATA_MATRIX, "dm"), 700, 250),
            (TestImages.Generate(BarcodeFormat.AZTEC, "az"), 20, 600),
            (TestImages.Generate(BarcodeFormat.PDF_417, "pdf"), 700, 900));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(
            new[]
            {
                (BarcodeFormat.CODE_128, "A1"),
                (BarcodeFormat.EAN_13, "4006381333931"),
                (BarcodeFormat.CODE_39, "C3"),
                (BarcodeFormat.DATA_MATRIX, "dm"),
                (BarcodeFormat.AZTEC, "az"),
                (BarcodeFormat.PDF_417, "pdf"),
            },
            results.Select(r => (r.Format, r.Text)));
    }

    [Fact]
    public void Bounds_LocateTheSymbolsInTheOriginalImage()
    {
        using Image<Rgba32> image = TestImages.Compose(
            1100,
            800,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Top-left"), 20, 20),
            (TestImages.Generate(BarcodeFormat.AZTEC, "Aztec text"), 560, 280));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        using Image<Rgba32> reference = TestImages.Generate(BarcodeFormat.CODE_128, "Top-left");
        BarcodeReadResult code128 = results.Single(r => r.Format == BarcodeFormat.CODE_128);
        BarcodeReadResult aztec = results.Single(r => r.Format == BarcodeFormat.AZTEC);
        Assert.InRange(code128.X, 20, 20 + reference.Width);
        Assert.InRange(code128.Y, 20, 20 + reference.Height);
        // The bars were measured, not just the scanned row, and the quiet zone is not part of the bounds.
        Assert.InRange(code128.Bounds.Height, reference.Height - 4, reference.Height + 1);
        Assert.InRange(code128.Bounds.Width, reference.Width / 2, reference.Width);
        Assert.InRange(aztec.X, 560, 560 + 483);
        Assert.InRange(aztec.Y, 280, 280 + 483);
    }

    [Fact]
    public void RotatedImageWithTwoStackedBarcodes_IsOrderedInTheRotatedFrame()
    {
        // Rotating a picture of "Code 128 above EAN-13" by 90 degrees clockwise puts the EAN-13 on the left,
        // so it must come first. This checks that positions found in ZXing's rotated frame are mapped back.
        using Image<Rgba32> stacked = TestImages.Compose(
            800,
            420,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 20),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 20, 220));
        using Image<Rgba32> rotated90 = TestImages.Transform(stacked, context => context.Rotate(RotateMode.Rotate90));
        using Image<Rgba32> rotated180 = TestImages.Transform(stacked, context => context.Rotate(RotateMode.Rotate180));
        using Image<Rgba32> rotated270 = TestImages.Transform(stacked, context => context.Rotate(RotateMode.Rotate270));

        Assert.Equal(new[] { "4006381333931", "Hello-123" }, BarcodeHelper.Read(rotated90).Select(r => r.Text));
        Assert.Equal(new[] { "4006381333931", "Hello-123" }, BarcodeHelper.Read(rotated180).Select(r => r.Text));
        Assert.Equal(new[] { "Hello-123", "4006381333931" }, BarcodeHelper.Read(rotated270).Select(r => r.Text));
    }

    [Theory]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    [InlineData(BarcodeFormat.CODE_128)]
    [InlineData(BarcodeFormat.PDF_417)]
    public void SmallSymbolFarFromTheMiddleOfALargeImage_IsFound(BarcodeFormat format)
    {
        // ZXing's Data Matrix and Aztec detectors only look around the middle of the image they are given.
        using Image<Rgba32> image = TestImages.Compose(3000, 2000, (TestImages.Generate(format, "off-centre"), 2300, 1400));

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));

        Assert.Equal(format, result.Format);
        Assert.Equal("off-centre", result.Text);
        Assert.True(result.X > 2300 && result.Y > 1400, $"found at {result.X},{result.Y}");
    }

    [Theory]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    public void LargeSymbolRightUnderOtherBarcodes_IsFound(BarcodeFormat format)
    {
        // About 500 px wide, in the quiet-zone distance below an EAN-13 and a Code 128. The region finder joined the three
        // (what counts as "close" depends on the size of the picture, not of the symbols), a join that holds a barcode that
        // was read was left alone, and ZXing's Data Matrix and Aztec detectors do not find a symbol away from the middle.
        using Image<Rgba32> ean = TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931");
        using Image<Rgba32> symbol = TestImages.Generate(format, "DevToys Barcode");
        Assert.InRange(symbol.Width, 400, 520);
        int eanBottom = 220 + ean.Height;

        using Image<Rgba32> image = TestImages.Compose(
            1400,
            1200,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 20),
            (ean.Clone(), 20, 220),
            (symbol.Clone(), 20, eanBottom));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(
            new[] { (BarcodeFormat.CODE_128, "Hello-123"), (BarcodeFormat.EAN_13, "4006381333931"), (format, "DevToys Barcode") },
            results.Select(r => (r.Format, r.Text)));
        BarcodeReadResult found = results[2];
        Assert.True(new Rectangle(20, eanBottom, symbol.Width, symbol.Height).Contains((int)found.X, (int)found.Y), $"centre {found.X},{found.Y}");
    }

    [Theory]
    [InlineData(BarcodeFormat.DATA_MATRIX, BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC)]
    public void TwoLargeSymbolsCloseTogether_AreBothFound(BarcodeFormat first, BarcodeFormat second)
    {
        // Neither is around the middle of the island they make together, so nothing is read from it until it is split.
        using Image<Rgba32> left = TestImages.Generate(first, "First symbol");
        using Image<Rgba32> right = TestImages.Generate(second, "Second symbol");
        int overlap = 50; // their quiet zones overlap a little: no ink is covered, and the gap between the symbols stays small

        using Image<Rgba32> image = TestImages.Compose(
            1800,
            900,
            (left.Clone(), 20, 40),
            (right.Clone(), 20 + left.Width - overlap, 60));

        Assert.Equal(
            new[] { (first, "First symbol"), (second, "Second symbol") },
            BarcodeHelper.Read(image).Select(r => (r.Format, r.Text)));
    }

    [Fact]
    public void TheSameBarcodeFoundTwice_IsReportedOnce()
    {
        // Deduplication is by format and text (positions are not reliable enough to tell two labels apart),
        // so two identical labels in one picture give a single result.
        using Image<Rgba32> image = TestImages.Compose(
            800,
            420,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Same"), 20, 20),
            (TestImages.Generate(BarcodeFormat.CODE_128, "Same"), 20, 220));

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));
        Assert.Equal("Same", result.Text);
    }

    [Fact]
    public void Results_AreIdenticalAcrossRuns()
    {
        using Image<Rgba32> image = TestImages.Compose(
            1100,
            800,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Top-left"), 20, 20),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 20, 600),
            (TestImages.Generate(BarcodeFormat.AZTEC, "Aztec text"), 560, 20));

        IReadOnlyList<BarcodeReadResult> first = BarcodeHelper.Read(image);
        IReadOnlyList<BarcodeReadResult> second = BarcodeHelper.Read(image);

        Assert.Equal(first, second);
    }

    // (format, input, text read back). Codabar loses its start/stop characters.
    public static TheoryData<BarcodeFormat, string, string> RotationCases => new()
    {
        { BarcodeFormat.CODE_128, "Hello-123", "Hello-123" },
        { BarcodeFormat.EAN_13, "4006381333931", "4006381333931" },
        { BarcodeFormat.CODABAR, "A1234B", "1234" },
        { BarcodeFormat.DATA_MATRIX, "Data matrix", "Data matrix" },
        { BarcodeFormat.AZTEC, "Aztec", "Aztec" },
        { BarcodeFormat.PDF_417, "PDF417", "PDF417" },
    };

    [Theory]
    [MemberData(nameof(RotationCases))]
    public void RotatedBy90_180_And270Degrees_IsStillRead(BarcodeFormat format, string input, string expectedText)
    {
        using Image<Rgba32> generated = TestImages.Generate(format, input);
        using Image<Rgba32> bordered = TestImages.WithBorder(generated);

        foreach (RotateMode mode in new[] { RotateMode.Rotate90, RotateMode.Rotate180, RotateMode.Rotate270 })
        {
            using Image<Rgba32> rotated = TestImages.Transform(bordered, context => context.Rotate(mode));

            BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(rotated));

            Assert.True(
                format == result.Format && expectedText == result.Text,
                $"{mode}: got {result.Format} '{result.Text}', expected {format} '{expectedText}'");
        }
    }

    [Theory]
    [MemberData(nameof(RotationCases))]
    public void WithInvertedColours_IsStillRead(BarcodeFormat format, string input, string expectedText)
    {
        using Image<Rgba32> generated = TestImages.Generate(format, input);
        using Image<Rgba32> bordered = TestImages.WithBorder(generated);
        using Image<Rgba32> inverted = TestImages.Transform(bordered, context => context.Invert());

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(inverted));

        Assert.Equal(format, result.Format);
        Assert.Equal(expectedText, result.Text);
    }

    [Fact]
    public void RotatedAndInverted_IsStillRead()
    {
        using Image<Rgba32> generated = TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123");
        using Image<Rgba32> bordered = TestImages.WithBorder(generated);
        using Image<Rgba32> transformed = TestImages.Transform(bordered, context => context.Rotate(RotateMode.Rotate90).Invert());

        Assert.Equal("Hello-123", Assert.Single(BarcodeHelper.Read(transformed)).Text);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)] // transparent black, what many tools write for "no background"
    [InlineData(255, 255, 255, 0)] // transparent white
    public void BarcodeOnTransparentBackground_IsRead(byte r, byte g, byte b, byte a)
    {
        using Image<Rgba32> generated = TestImages.Generate(BarcodeFormat.CODE_128, "Transparent");
        using Image<Rgba32> bordered = TestImages.WithBorder(generated);
        bordered.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                foreach (ref Rgba32 pixel in accessor.GetRowSpan(y))
                {
                    if (pixel.R > 127)
                    {
                        pixel = new Rgba32(r, g, b, a);
                    }
                }
            }
        });

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(bordered));
        Assert.Equal("Transparent", result.Text);
    }

    [Fact]
    public void ImageWithoutBarcode_GivesAnEmptyList()
    {
        using var blank = new Image<Rgba32>(400, 300, Color.White);
        Assert.Empty(BarcodeHelper.Read(blank));

        // Some structure, but no barcode: diagonal stripes.
        using var stripes = new Image<Rgba32>(400, 300, Color.White);
        stripes.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = ((x + y) / 7) % 3 == 0 ? Color.Black : Color.White;
                }
            }
        });
        Assert.Empty(BarcodeHelper.Read(stripes));
    }

    [Fact]
    public async Task NoBarcodeInStream_GivesAnEmptyList_WithoutThrowing()
    {
        using var blank = new Image<Rgba32>(100, 100, Color.White);
        using MemoryStream stream = TestImages.ToPngStream(blank);

        Assert.Empty(await BarcodeHelper.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_HonoursAnAlreadyCancelledToken()
    {
        using Image<Rgba32> generated = TestImages.Generate(BarcodeFormat.CODE_128, "Hello");
        using MemoryStream stream = TestImages.ToPngStream(generated);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BarcodeHelper.ReadAsync(stream, cts.Token));
    }

    [Fact]
    public void Read_HonoursAnAlreadyCancelledToken()
    {
        using var blank = new Image<Rgba32>(100, 100, Color.White);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => BarcodeHelper.Read(blank, cts.Token));
    }

    [Fact]
    public async Task ReadAsync_WithAStreamThatIsNotAnImage_ThrowsTheImageSharpException()
    {
        // Documented behaviour: only "no barcode" is a normal outcome; the caller decides what a bad file means.
        using var stream = new MemoryStream("this is not an image"u8.ToArray());

        await Assert.ThrowsAsync<UnknownImageFormatException>(() => BarcodeHelper.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Read_DoesNotModifyTheImage()
    {
        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.CODE_128, "Hello");
        using Image<Rgba32> copy = image.Clone();

        BarcodeHelper.Read(image);

        Assert.True(image.Frames.RootFrame.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> a));
        Assert.True(copy.Frames.RootFrame.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> b));
        Assert.True(a.Span.SequenceEqual(b.Span));
    }
}
