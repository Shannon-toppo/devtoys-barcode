using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;
using ZXing.Common;

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

    [Fact]
    public void TurnedLuminance_IsTheLuminanceOfTheTurnedPicture()
    {
        // The 90 degree pictures are decoded from the luminance of the upright one, turned, instead of the luminance of
        // the turned picture. Both must be the same, transparent pixels included (sizes that are not a multiple of the tiles).
        var random = new Random(7);
        using var image = new Image<Rgba32>(203, 97);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                foreach (ref Rgba32 pixel in accessor.GetRowSpan(y))
                {
                    pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                }
            }
        });
        using Image<Rgba32> turned = image.Clone(context => context.Rotate(RotateMode.Rotate90));

        LuminanceSource expected = new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(turned);
        LuminanceSource actual = BarcodeHelper.RotateClockwise(new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(image));

        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Matrix, actual.Matrix);
    }

    [Theory]
    [InlineData(22f)]
    [InlineData(-45f)]
    [InlineData(67f)]
    public void TiltedPicture_IsTurnedAsImageSharpTurnsIt(float degrees)
    {
        // The "tilted" attempts turn the picture with a rotation of their own (ImageSharp's is slow): same size and
        // placement, and the same pixels give or take one level, transparent pixels included.
        var random = new Random(3);
        using Image<Rgba32> image = TestImages.WithBorder(TestImages.Generate(BarcodeFormat.CODE_128, "Tilted"), 10);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y += 3)
            {
                foreach (ref Rgba32 pixel in accessor.GetRowSpan(y))
                {
                    pixel.A = (byte)random.Next(256);
                }
            }
        });

        using Image<Rgba32> expected = image.Clone(context => context.Rotate(degrees, KnownResamplers.Triangle).BackgroundColor(Color.White));
        using Image<Rgba32> actual = BarcodeImageRotator.RotateOnWhite(image, degrees, maxDegreeOfParallelism: 2);

        Assert.Equal(expected.Size, actual.Size);
        int differing = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba32 e = expected[x, y];
                Rgba32 a = actual[x, y];
                int difference = new[] { Math.Abs(e.R - a.R), Math.Abs(e.G - a.G), Math.Abs(e.B - a.B), Math.Abs(e.A - a.A) }.Max();
                Assert.True(difference <= 1, $"({x}, {y}): {e} and {a}");
                differing += difference;
            }
        }

        Assert.True(differing < expected.Width * expected.Height / 1000, $"{differing} pixels differ");
    }

    // ZXing's PDF417 detector turns the picture by 90, 180 or 270 degrees when it finds nothing upright, and gives the points
    // of the symbol in the turned picture. ZXing's points are the first and last rows and the first column and the end of
    // the last column of the patterns, so they are within a pixel of the ink (an upright symbol is already one row short).
    private const int InkTolerance = 2;

    private static void AssertBoundsNearInk(Rectangle ink, Rectangle bounds, string context = "")
        => Assert.True(
            Math.Abs(bounds.Left - ink.Left) <= InkTolerance
            && Math.Abs(bounds.Top - ink.Top) <= InkTolerance
            && Math.Abs(bounds.Right - ink.Right) <= InkTolerance
            && Math.Abs(bounds.Bottom - ink.Bottom) <= InkTolerance,
            $"{context}bounds {bounds} are not within {InkTolerance} px of the symbol {ink}");

    [Theory]
    [InlineData(RotateMode.None)]
    [InlineData(RotateMode.Rotate90)]
    [InlineData(RotateMode.Rotate180)]
    [InlineData(RotateMode.Rotate270)]
    public void Pdf417TurnedByARightAngle_ReportsBoundsWhereItWasDrawn(RotateMode mode)
    {
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> turned = TestImages.Transform(symbol, context => context.Rotate(mode));
        using Image<Rgba32> image = TestImages.Compose(2000, 1600, (turned.Clone(), 1200, 300));
        Rectangle ink = TestImages.InkBounds(image);

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));

        Assert.Equal((BarcodeFormat.PDF_417, "Hello PDF417 123"), (result.Format, result.Text));
        AssertBoundsNearInk(ink, result.Bounds, $"{mode}: ");
    }

    [Theory]
    [InlineData(900, 1900, 600, 1200)] // taller than wide
    [InlineData(2400, 700, 1500, 450)] // wider than tall
    [InlineData(1000, 1000, 780, 20)] // square, the symbol against the edges (the position is clamped to the picture)
    public void Pdf417TurnedByARightAngle_InPicturesOfOtherShapes_ReportsBoundsWhereItWasDrawn(int width, int height, int x, int y)
    {
        // Turning the picture by 90 or 270 degrees swaps its sides: a mapping that mixes up width and height lands outside.
        foreach (RotateMode mode in new[] { RotateMode.Rotate90, RotateMode.Rotate180, RotateMode.Rotate270 })
        {
            using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
            using Image<Rgba32> turned = TestImages.Transform(symbol, context => context.Rotate(mode));
            using Image<Rgba32> image = TestImages.Compose(width, height, (turned.Clone(), Math.Min(x, width - turned.Width), Math.Min(y, height - turned.Height)));
            Rectangle ink = TestImages.InkBounds(image);

            BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));

            AssertBoundsNearInk(ink, result.Bounds, $"{width}x{height} {mode}: ");
            Assert.True(new Rectangle(0, 0, width, height).Contains(result.Bounds), $"{width}x{height} {mode}: bounds {result.Bounds} are outside the picture");
        }
    }

    [Theory]
    [InlineData(RotateMode.Rotate90)]
    [InlineData(RotateMode.Rotate180)]
    [InlineData(RotateMode.Rotate270)]
    public void Pdf417TurnedByARightAngle_NextToOtherSymbols_DoesNotHideThem(RotateMode mode)
    {
        // The bounds a turned PDF417 used to get covered the Data Matrix below the Code 128 (and ZXing's own search for more
        // symbols was sent to the wrong parts of the picture), which was then wiped out before it was looked for.
        using Image<Rgba32> pdf417 = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> turned = TestImages.Transform(pdf417, context => context.Rotate(mode));
        using Image<Rgba32> dataMatrix = TestImages.Generate(BarcodeFormat.DATA_MATRIX, "Data matrix below");
        Assert.InRange(dataMatrix.Width, 400, 520);

        using Image<Rgba32> image = TestImages.Compose(
            2000,
            1600,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Top-left"), 100, 100),
            (turned.Clone(), 1200, 300),
            (dataMatrix.Clone(), 100, 900));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(
            new[] { (BarcodeFormat.CODE_128, "Top-left"), (BarcodeFormat.PDF_417, "Hello PDF417 123"), (BarcodeFormat.DATA_MATRIX, "Data matrix below") },
            results.Select(r => (r.Format, r.Text)));
        Assert.True(new Rectangle(100, 900, dataMatrix.Width, dataMatrix.Height).Contains((int)results[2].X, (int)results[2].Y), $"Data Matrix centre {results[2].X},{results[2].Y}");
    }

    [Theory]
    [InlineData(RotateMode.Rotate90)]
    [InlineData(RotateMode.Rotate180)]
    [InlineData(RotateMode.Rotate270)]
    public void Pdf417TurnedByARightAngle_IsOrderedByWhereItIs(RotateMode mode)
    {
        // At the top right, with an EAN-13 below it on the left: ZXing's points put a PDF417 turned by 270 degrees at the bottom,
        // and one turned by 180 degrees below the middle.
        using Image<Rgba32> pdf417 = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> turned = TestImages.Transform(pdf417, context => context.Rotate(mode));
        using Image<Rgba32> image = TestImages.Compose(
            2000,
            1600,
            (turned.Clone(), 1400, 40),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 100, 700),
            (TestImages.Generate(BarcodeFormat.CODE_128, "Bottom"), 100, 1300));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(
            new[] { "Hello PDF417 123", "4006381333931", "Bottom" },
            results.Select(r => r.Text));
    }

    [Theory]
    [InlineData(RotateMode.None)]
    [InlineData(RotateMode.Rotate90)]
    [InlineData(RotateMode.Rotate180)]
    [InlineData(RotateMode.Rotate270)]
    public void TwoPdf417_TheSecondOneFoundInAPartOfThePicture_BothReportBoundsWhereTheyWereDrawn(RotateMode mode)
    {
        // The second symbol is found in a crop (the part of the picture to the right of the first one), whose own size matters.
        using Image<Rgba32> first = TestImages.Generate(BarcodeFormat.PDF_417, "First symbol");
        using Image<Rgba32> second = TestImages.Generate(BarcodeFormat.PDF_417, "Second");
        using Image<Rgba32> turned = TestImages.Transform(second, context => context.Rotate(mode));
        using Image<Rgba32> onlyFirst = TestImages.Compose(1900, 900, (first.Clone(), 60, 80));
        using Image<Rgba32> onlySecond = TestImages.Compose(1900, 900, (turned.Clone(), 1200, 300));
        using Image<Rgba32> image = TestImages.Compose(1900, 900, (first.Clone(), 60, 80), (turned.Clone(), 1200, 300));

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        Assert.Equal(new[] { "First symbol", "Second" }, results.Select(r => r.Text));
        AssertBoundsNearInk(TestImages.InkBounds(onlyFirst), results[0].Bounds, "first: ");
        AssertBoundsNearInk(TestImages.InkBounds(onlySecond), results[1].Bounds, "second: ");
    }

    // The points ZXing's PDF417 detector gives for a symbol that is 480 x 112 px and sits at 116,216 in the picture, when it
    // had to turn the picture to find it (found in generated pictures of two sizes). Each row: picture size, the turn
    // ZXing reports, two corners of the symbol (top left and bottom right of the pattern at either end) in the turned picture.
    [Theory]
    [InlineData(2000, 1600, 0, 116, 216, 596, 327)]
    [InlineData(2000, 1600, 90, 216, 1772, 696, 1883)]
    [InlineData(2000, 1600, 180, 1404, 1272, 1884, 1383)]
    [InlineData(2000, 1600, 270, 904, 116, 1384, 227)]
    [InlineData(901, 1203, 90, 216, 673, 696, 784)]
    [InlineData(901, 1203, 180, 305, 875, 785, 986)]
    [InlineData(901, 1203, 270, 507, 116, 987, 227)]
    public void Pdf417Points_AreBroughtBackToThePictureThatWasDecoded(int width, int height, int orientation, float left, float top, float right, float bottom)
    {
        var raw = new ResultPoint[] { new(left, top), null!, new(right, top), new(right, bottom), null!, new(left, bottom) };

        ResultPoint[] points = MultipleBarcodeDecoder.ToPictureFrame(raw, orientation, width, height);

        Assert.Equal(new[] { false, true, false, false, true, false }, points.Select(p => p is null));
        ResultPoint[] known = points.Where(p => p is not null).ToArray();
        // Upright: x from 116 to 596 (the end of the last pattern), y from 216 to 327 (the last row). Turned by 90 or 270 degrees: the other way round.
        (float expectedLeft, float expectedTop, float expectedRight, float expectedBottom) = orientation is 90 or 270 ? (116f, 216f, 227f, 696f) : (116f, 216f, 596f, 327f);
        Assert.Equal((expectedLeft, expectedTop, expectedRight, expectedBottom), (known.Min(p => p.X), known.Min(p => p.Y), known.Max(p => p.X), known.Max(p => p.Y)));
    }

    [Fact]
    public void Pdf417Points_AreOnlyChangedForPdf417WithAKnownTurn()
    {
        ResultPoint[] raw = [new(300, 20), new(300, 40)];

        // ZXing's 1D readers report an orientation too, with the points already in the picture's coordinates.
        var oneD = new Result("1D", null, raw, BarcodeFormat.CODE_128);
        oneD.putMetadata(ResultMetadataType.ORIENTATION, 180);
        var turned = new Result("PDF", null, raw, BarcodeFormat.PDF_417);
        turned.putMetadata(ResultMetadataType.ORIENTATION, 180);
        var upright = new Result("PDF", null, raw, BarcodeFormat.PDF_417);
        var unknown = new Result("PDF", null, raw, BarcodeFormat.PDF_417);
        unknown.putMetadata(ResultMetadataType.ORIENTATION, "180");
        var other = new Result("PDF", null, raw, BarcodeFormat.PDF_417);
        other.putMetadata(ResultMetadataType.ORIENTATION, 45);

        Assert.Equal(raw, MultipleBarcodeDecoder.ToPictureFrame(oneD, 1000, 500));
        Assert.Equal(raw, MultipleBarcodeDecoder.ToPictureFrame(upright, 1000, 500));
        Assert.Equal(raw, MultipleBarcodeDecoder.ToPictureFrame(unknown, 1000, 500));
        Assert.Equal(raw, MultipleBarcodeDecoder.ToPictureFrame(other, 1000, 500));
        ResultPoint[] mapped = MultipleBarcodeDecoder.ToPictureFrame(turned, 1000, 500);
        Assert.Equal(new[] { (700f, 479f), (700f, 459f) }, mapped.Select(p => (p.X, p.Y)));
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(135f)]
    [InlineData(225f)]
    [InlineData(112f)] // 22 + 90: read in the picture the reader turned by -22 degrees, in which ZXing turns it by 90 degrees itself
    [InlineData(202f)]
    [InlineData(292f)]
    public void Pdf417TiltedAndTurned_IsFoundByATiltedViewOfThePicture_AndReportsBoundsWhereItWasDrawn(float degrees)
    {
        // The reader turns the picture by an angle ZXing does not tolerate to read a tilted symbol, then ZXing may turn that by a right
        // angle: the points are mapped back twice. The corners of the tilted symbol are cut off a few pixels by the resampling, and
        // ZXing's points are the corners of the patterns, so more is allowed than for a symbol that is not tilted.
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> tilted = TestImages.Transform(symbol, context => context.Rotate(degrees).BackgroundColor(Color.White));
        using Image<Rgba32> image = TestImages.Compose(1300, 1000, (tilted.Clone(), 380, 100));
        Rectangle ink = TestImages.InkBounds(image);

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));

        Assert.Equal("Hello PDF417 123", result.Text);
        const int tolerance = 6;
        Assert.True(
            Math.Abs(result.Bounds.Left - ink.Left) <= tolerance
            && Math.Abs(result.Bounds.Top - ink.Top) <= tolerance
            && Math.Abs(result.Bounds.Right - ink.Right) <= tolerance
            && Math.Abs(result.Bounds.Bottom - ink.Bottom) <= tolerance,
            $"{degrees} degrees: bounds {result.Bounds} are not within {tolerance} px of the symbol {ink}");
    }

    [Theory]
    [InlineData(RotateMode.Rotate90, 4f, 2f)]
    [InlineData(RotateMode.Rotate180, 4f, 1.2f)]
    public void Pdf417OnlyFoundInTheReadersOwn90DegreeView_ReportsBoundsWhereItWasDrawn(RotateMode mode, float tilt, float blur)
    {
        // A blurred and slightly tilted symbol that the reader's "enlarged" attempt reads in the picture turned by 90 degrees but not
        // upright (found by trying such pictures: the pixels of a turned picture are binarized in other blocks). In that view the
        // symbol is turned by a right angle again, which ZXing undoes itself, and the bounds go through the point correction, the
        // reduction of the enlarged picture and the turn back by 90 degrees.
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> turned = TestImages.Transform(symbol, context => context.Rotate(mode).Rotate(tilt).BackgroundColor(Color.White));
        using Image<Rgba32> image = TestImages.Compose(1000, 800, (turned.Clone(), 300, 150));
        image.Mutate(context => context.GaussianBlur(blur));
        Rectangle ink = TestImages.InkBounds(image);

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));

        Assert.Equal("Hello PDF417 123", result.Text);
        const int tolerance = 10; // a blurred, tilted and enlarged-then-reduced picture
        Assert.True(
            Math.Abs(result.Bounds.Left - ink.Left) <= tolerance
            && Math.Abs(result.Bounds.Top - ink.Top) <= tolerance
            && Math.Abs(result.Bounds.Right - ink.Right) <= tolerance
            && Math.Abs(result.Bounds.Bottom - ink.Bottom) <= tolerance,
            $"{mode}, {tilt} degrees: bounds {result.Bounds} are not within {tolerance} px of the symbol {ink}");
    }

    public static TheoryData<BarcodeFormat[], bool> InvertedInParallelCases => new()
    {
        { [BarcodeFormat.PDF_417], true },
        { [BarcodeFormat.QR_CODE], true },
        { [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.MAXICODE, BarcodeFormat.PDF_417], true },
        { [BarcodeFormat.CODE_128], false },
        { [BarcodeFormat.PDF_417, BarcodeFormat.EAN_13], false },
        { [BarcodeFormat.AZTEC, BarcodeFormat.MSI], false },
        { [BarcodeFormat.UPC_EAN_EXTENSION], false },
        { [BarcodeFormat.IMB], false },
        { [BarcodeFormat.RSS_14], false },
        { [], false },
    };

    [Theory]
    [MemberData(nameof(InvertedInParallelCases))]
    public void ThePictureAndItsInvertedCopy_AreOnlyDecodedAtTheSameTime_WhenNoReaderAsksForRows(BarcodeFormat[] formats, bool expected)
    {
        var options = new DecodingOptions { PossibleFormats = formats };

        Assert.Equal(expected, MultipleBarcodeDecoder.CanDecodeInvertedInParallel(options.Hints));
    }

    [Fact]
    public void ThePictureAndItsInvertedCopy_AreNotDecodedAtTheSameTime_WithoutARestrictionOfTheFormats()
    {
        // Without a restriction ZXing also runs its 1D readers, which ask the binarizer of the picture for rows (and the inverted
        // copy takes its rows from that binarizer, which keeps the buffers for them in fields).
        Assert.False(MultipleBarcodeDecoder.CanDecodeInvertedInParallel(new DecodingOptions().Hints));
    }

    [Theory]
    [InlineData(new[] { BarcodeFormat.CODE_128 })]
    [InlineData(new[] { BarcodeFormat.CODE_128, BarcodeFormat.PDF_417, BarcodeFormat.QR_CODE })]
    [InlineData(new BarcodeFormat[0])] // no restriction
    public void WhenA1DReaderMayRun_NeverTwoThreadsAskTheSameBinarizerForRows(BarcodeFormat[] formats)
    {
        using Image<Rgba32> image = TestImages.Compose(800, 400, (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 20));
        var watch = new RowWatch();
        var luminance = new WatchedLuminance(new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(image), watch);
        var options = new DecodingOptions { TryHarder = true, PossibleFormats = formats.Length == 0 ? null : formats };
        options.Hints[DecodeHintType.TRY_HARDER_WITHOUT_ROTATION] = true;

        Result[] results = MultipleBarcodeDecoder.Decode(luminance, options.Hints, alsoInverted: true, maxDegreeOfParallelism: 4, CancellationToken.None);

        Assert.Contains(results, r => r.Text == "Hello-123");
        Assert.True(watch.Rows > 0, "no row was asked for: the picture was not read by a 1D reader");
        Assert.Equal(1, watch.MaxInFlight);
    }

    [Theory]
    [InlineData(BarcodeFormat.PDF_417)]
    [InlineData(BarcodeFormat.QR_CODE)]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    [InlineData(BarcodeFormat.MAXICODE)]
    public void TheReadersOfTheOtherFormats_NeverAskForRows(BarcodeFormat format)
    {
        // What makes it safe to decode the picture and its inverted copy at the same time for these formats alone.
        using Image<Rgba32> image = TestImages.Compose(
            800,
            600,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 20),
            (TestImages.Generate(BarcodeFormat.PDF_417, "Stacked"), 20, 200),
            (TestImages.Generate(BarcodeFormat.AZTEC, "Aztec"), 300, 20));
        var watch = new RowWatch();
        var luminance = new WatchedLuminance(new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(image), watch);
        var options = new DecodingOptions { TryHarder = true, PossibleFormats = [format] };
        options.Hints[DecodeHintType.TRY_HARDER_WITHOUT_ROTATION] = true;
        Assert.True(MultipleBarcodeDecoder.CanDecodeInvertedInParallel(options.Hints));

        MultipleBarcodeDecoder.Decode(luminance, options.Hints, alsoInverted: true, maxDegreeOfParallelism: 4, CancellationToken.None);

        Assert.Equal(0, watch.Rows);
    }

    private sealed class RowWatch
    {
        private int inFlight;
        private int maxInFlight;
        private int rows;
        private int waited;

        internal int Rows => rows;

        internal int MaxInFlight => maxInFlight;

        /// <summary>Counts a request for a row, and how many are being served at the same time.</summary>
        internal void Enter()
        {
            Interlocked.Increment(ref rows);
            Record(Interlocked.Increment(ref inFlight));

            // The first request waits for another one for a while, so that two threads that are decoding at the same time are caught
            // doing it (rather than depending on one of them being slower to start). When nothing else asks, it is a one time delay.
            if (Interlocked.Exchange(ref waited, 1) == 0)
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref inFlight) > 1, 500);
                Record(Volatile.Read(ref inFlight));
            }
        }

        internal void Leave() => Interlocked.Decrement(ref inFlight);

        private void Record(int current)
        {
            int seen = Volatile.Read(ref maxInFlight);
            while (current > seen && Interlocked.CompareExchange(ref maxInFlight, current, seen) != seen)
            {
                seen = Volatile.Read(ref maxInFlight);
            }
        }
    }

    /// <summary>A luminance source that tells a <see cref="RowWatch"/> when a row is asked for. (Its crops are not watched.)</summary>
    private sealed class WatchedLuminance(LuminanceSource inner, RowWatch watch) : LuminanceSource(inner.Width, inner.Height)
    {
        public override byte[] Matrix => inner.Matrix;

        public override bool CropSupported => inner.CropSupported;

        public override LuminanceSource crop(int left, int top, int width, int height) => inner.crop(left, top, width, height);

        public override byte[] getRow(int y, byte[] row)
        {
            watch.Enter();
            try
            {
                return inner.getRow(y, row);
            }
            finally
            {
                watch.Leave();
            }
        }
    }
}
