using System.Xml;
using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace DevToys.Barcode.Tests;

public class BarcodeGenerationTests
{
    [Fact]
    public void GenerationFormats_AreOfferedInTheAgreedOrder_AndExcludeQrCode()
    {
        Assert.Equal(
            new[]
            {
                BarcodeFormat.CODE_128, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
                BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.ITF, BarcodeFormat.CODABAR,
                BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417, BarcodeFormat.AZTEC
            },
            BarcodeHelper.GenerationFormats);
        Assert.DoesNotContain(BarcodeFormat.QR_CODE, BarcodeHelper.GenerationFormats);
    }

    [Fact]
    public void GenerateImage_QrCode_IsRejected()
    {
        BarcodeGenerationResult<Image<Rgba32>> result = BarcodeHelper.GenerateImage("hello", BarcodeFormat.QR_CODE);

        Assert.False(result.IsSuccess);
        Assert.Equal(BarcodeGenerationFailureReason.EncoderRejected, result.Reason);
    }

    // (format, input, format and text expected when reading the image back with an unrestricted reader)
    // Known normalisations are asserted explicitly:
    //  - EAN/UPC: the computed check digit is part of the text.
    //  - Codabar: ZXing strips the start/stop characters when reading.
    //  - UPC-A is reported as UPC-A (not EAN-13) by an unrestricted reader.
    public static TheoryData<BarcodeFormat, string, BarcodeFormat, string> RoundTripCases => new()
    {
        { BarcodeFormat.CODE_128, "Hello-123 abc", BarcodeFormat.CODE_128, "Hello-123 abc" },
        { BarcodeFormat.CODE_128, "12345678901234", BarcodeFormat.CODE_128, "12345678901234" },
        { BarcodeFormat.EAN_13, "400638133393", BarcodeFormat.EAN_13, "4006381333931" },
        { BarcodeFormat.EAN_13, "4006381333931", BarcodeFormat.EAN_13, "4006381333931" },
        { BarcodeFormat.EAN_8, "9638507", BarcodeFormat.EAN_8, "96385074" },
        { BarcodeFormat.EAN_8, "96385074", BarcodeFormat.EAN_8, "96385074" },
        { BarcodeFormat.UPC_A, "03600029145", BarcodeFormat.UPC_A, "036000291452" },
        { BarcodeFormat.UPC_A, "036000291452", BarcodeFormat.UPC_A, "036000291452" },
        { BarcodeFormat.UPC_E, "0123456", BarcodeFormat.UPC_E, "01234565" },
        { BarcodeFormat.UPC_E, "01234565", BarcodeFormat.UPC_E, "01234565" },
        { BarcodeFormat.CODE_39, "HELLO-123 $/+%.", BarcodeFormat.CODE_39, "HELLO-123 $/+%." },
        { BarcodeFormat.CODE_39, "hello", BarcodeFormat.CODE_39, "hello" }, // full ASCII mode
        { BarcodeFormat.CODE_93, "HELLO-123 $/+%.", BarcodeFormat.CODE_93, "HELLO-123 $/+%." },
        { BarcodeFormat.CODE_93, "Hello", BarcodeFormat.CODE_93, "Hello" }, // full ASCII mode
        { BarcodeFormat.ITF, "12345670", BarcodeFormat.ITF, "12345670" },
        { BarcodeFormat.ITF, "1234", BarcodeFormat.ITF, "1234" }, // needs the relaxed ITF length attempt
        { BarcodeFormat.CODABAR, "123456", BarcodeFormat.CODABAR, "123456" },
        { BarcodeFormat.CODABAR, "A123-456B", BarcodeFormat.CODABAR, "123-456" }, // start/stop characters are dropped
        { BarcodeFormat.DATA_MATRIX, "Hello, Data Matrix 123", BarcodeFormat.DATA_MATRIX, "Hello, Data Matrix 123" },
        { BarcodeFormat.DATA_MATRIX, "こんにちは世界", BarcodeFormat.DATA_MATRIX, "こんにちは世界" },
        { BarcodeFormat.PDF_417, "Hello, PDF417 123", BarcodeFormat.PDF_417, "Hello, PDF417 123" },
        { BarcodeFormat.PDF_417, "こんにちは世界", BarcodeFormat.PDF_417, "こんにちは世界" },
        { BarcodeFormat.AZTEC, "Hello, Aztec 123", BarcodeFormat.AZTEC, "Hello, Aztec 123" },
        { BarcodeFormat.AZTEC, "こんにちは世界", BarcodeFormat.AZTEC, "こんにちは世界" },
    };

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_ImageIsReadBackWithExpectedFormatAndText(
        BarcodeFormat format, string input, BarcodeFormat expectedFormat, string expectedText)
    {
        using Image<Rgba32> image = TestImages.Generate(format, input);

        IReadOnlyList<BarcodeReadResult> results = BarcodeHelper.Read(image);

        BarcodeReadResult result = Assert.Single(results);
        Assert.Equal(expectedFormat, result.Format);
        Assert.Equal(expectedText, result.Text);
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public async Task RoundTrip_ImageInPngStream_IsReadBackAsync(
        BarcodeFormat format, string input, BarcodeFormat expectedFormat, string expectedText)
    {
        using Image<Rgba32> image = TestImages.Generate(format, input);
        using MemoryStream stream = TestImages.ToPngStream(image);

        IReadOnlyList<BarcodeReadResult> results = await BarcodeHelper.ReadAsync(stream, CancellationToken.None);

        BarcodeReadResult result = Assert.Single(results);
        Assert.Equal(expectedFormat, result.Format);
        Assert.Equal(expectedText, result.Text);
    }

    [Fact]
    public void RoundTrip_UpcA_IsReportedAsEan13WithLeadingZero_WhenReaderIsRestrictedToEan13()
    {
        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.UPC_A, "03600029145");

        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image, possibleFormats: [BarcodeFormat.EAN_13]));

        Assert.Equal(BarcodeFormat.EAN_13, result.Format);
        Assert.Equal("0036000291452", result.Text);
    }

    [Fact]
    public void Read_RestrictedToAnotherFormat_FindsNothing()
    {
        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.CODE_128, "Hello");

        Assert.Empty(BarcodeHelper.Read(image, possibleFormats: [BarcodeFormat.AZTEC]));
        Assert.Single(BarcodeHelper.Read(image, possibleFormats: [BarcodeFormat.AZTEC, BarcodeFormat.CODE_128]));
    }

    [Theory]
    [InlineData("400638133393", "4006381333931")]
    [InlineData("03600029145", "036000291452")] // 11 digits: UPC-A
    public void EncodedText_HasComputedCheckDigit_WhenMissing(string input, string expected)
    {
        BarcodeFormat format = input.Length == 12 ? BarcodeFormat.EAN_13 : BarcodeFormat.UPC_A;

        BarcodeGenerationResult<string> result = BarcodeHelper.GenerateSvg(input, format);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.EncodedText);
    }

    [Theory]
    [InlineData(BarcodeFormat.EAN_13, "400638133393", "4006381333931")]
    [InlineData(BarcodeFormat.EAN_8, "9638507", "96385074")]
    [InlineData(BarcodeFormat.UPC_A, "03600029145", "036000291452")]
    [InlineData(BarcodeFormat.UPC_E, "0123456", "01234565")]
    public void CheckDigit_IsComputedWhenMissing_AndAcceptedWhenRight(BarcodeFormat format, string withoutCheck, string withCheck)
    {
        BarcodeGenerationResult<Image<Rgba32>> computed = BarcodeHelper.GenerateImage(withoutCheck, format);
        BarcodeGenerationResult<Image<Rgba32>> given = BarcodeHelper.GenerateImage(withCheck, format);
        using Image<Rgba32>? computedImage = computed.Value;
        using Image<Rgba32>? givenImage = given.Value;

        Assert.True(computed.IsSuccess, computed.Message);
        Assert.True(given.IsSuccess, given.Message);
        Assert.Equal(withCheck, computed.EncodedText);
        Assert.Equal(withCheck, given.EncodedText);

        // What we compute is what ZXing's encoder computes: the decoded text carries the same digit.
        BarcodeReadResult read = Assert.Single(BarcodeHelper.Read(computedImage!));
        Assert.Equal(withCheck, read.Text);
    }

    [Fact]
    public void UpcE_ComputedCheckDigit_MatchesTheOneZxingComputes_ForEveryExpansionRule()
    {
        // ZXing's own encoder is the oracle: encode the 7 digit body directly (it computes the check digit),
        // read the image back, and compare with what the helper reports for the same body.
        var random = new Random(1234);
        var bodies = new List<string>();
        for (int lastDigit = 0; lastDigit <= 9; lastDigit++)
        {
            for (int numberSystem = 0; numberSystem <= 1; numberSystem++)
            {
                bodies.Add($"{numberSystem}{random.Next(0, 10000):D4}{random.Next(0, 10)}{lastDigit}");
                bodies.Add($"{numberSystem}{random.Next(0, 10000):D4}{random.Next(0, 10)}{lastDigit}");
            }
        }

        foreach (string body in bodies)
        {
            var rawWriter = new ZXing.ImageSharp.BarcodeWriter<Rgba32>
            {
                Format = BarcodeFormat.UPC_E,
                Options = new ZXing.Common.EncodingOptions { Width = 400, Height = 100, PureBarcode = true }
            };
            using Image<Rgba32> raw = rawWriter.Write(body);
            string zxingText = Assert.Single(BarcodeHelper.Read(raw)).Text;

            BarcodeGenerationResult<string> helper = BarcodeHelper.GenerateSvg(body, BarcodeFormat.UPC_E);

            Assert.True(helper.IsSuccess, $"{body}: {helper.Message}");
            Assert.Equal(zxingText, helper.EncodedText);
        }
    }

    [Theory]
    [InlineData(BarcodeFormat.EAN_13, "4006381333932")]
    [InlineData(BarcodeFormat.EAN_8, "96385075")]
    [InlineData(BarcodeFormat.UPC_A, "036000291453")]
    [InlineData(BarcodeFormat.UPC_E, "01234566")]
    public void CheckDigit_IsRejectedWhenWrong(BarcodeFormat format, string input)
    {
        BarcodeGenerationResult<Image<Rgba32>> image = BarcodeHelper.GenerateImage(input, format);
        BarcodeGenerationResult<string> svg = BarcodeHelper.GenerateSvg(input, format);

        AssertFailure(image, BarcodeGenerationFailureReason.BadCheckDigit);
        AssertFailure(svg, BarcodeGenerationFailureReason.BadCheckDigit);
    }

    // Each reason is reachable.
    public static TheoryData<BarcodeFormat, string?, BarcodeGenerationFailureReason> FailureCases => new()
    {
        { BarcodeFormat.CODE_128, "", BarcodeGenerationFailureReason.EmptyInput },
        { BarcodeFormat.EAN_13, null, BarcodeGenerationFailureReason.EmptyInput },
        { BarcodeFormat.AZTEC, "", BarcodeGenerationFailureReason.EmptyInput },

        { BarcodeFormat.EAN_13, "40063813339A", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.EAN_8, "9638 07", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.UPC_A, "0360002914-", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.UPC_E, "012345X", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.ITF, "12A4", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.CODE_128, "café", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.CODE_39, "こんにちは", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.CODE_39, "A*B", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.CODE_93, "café", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.CODABAR, "12x4", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.CODABAR, "A12", BarcodeGenerationFailureReason.InvalidCharacters },

        { BarcodeFormat.EAN_13, "12345", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.EAN_13, "12345678901234", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.EAN_8, "123456", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.UPC_A, "1234567890", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.UPC_E, "123456", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.ITF, "123", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.CODABAR, "AB", BarcodeGenerationFailureReason.WrongLength },

        { BarcodeFormat.ITF, "12345678901234567890123456789012345678901234567890123456789012345678901234567890123456", BarcodeGenerationFailureReason.TooLong },
        { BarcodeFormat.CODE_39, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", BarcodeGenerationFailureReason.TooLong },
        { BarcodeFormat.CODE_93, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", BarcodeGenerationFailureReason.TooLong },
        { BarcodeFormat.DATA_MATRIX, TooLongDigits(3200), BarcodeGenerationFailureReason.TooLong },
        { BarcodeFormat.AZTEC, TooLongDigits(4200), BarcodeGenerationFailureReason.TooLong },
        { BarcodeFormat.PDF_417, TooLongDigits(3000), BarcodeGenerationFailureReason.TooLong },

        // UPC-E only exists for number systems 0 and 1: left to the encoder.
        { BarcodeFormat.UPC_E, "2234565", BarcodeGenerationFailureReason.EncoderRejected },
        { BarcodeFormat.UPC_E, "22345653", BarcodeGenerationFailureReason.EncoderRejected },
    };

    private static string TooLongDigits(int length) => new('7', length);

    [Theory]
    [MemberData(nameof(FailureCases))]
    public void InvalidInput_ReturnsFailureWithReason_ForImageAndSvg(BarcodeFormat format, string? input, BarcodeGenerationFailureReason reason)
    {
        AssertFailure(BarcodeHelper.GenerateImage(input, format), reason);
        AssertFailure(BarcodeHelper.GenerateSvg(input, format), reason);
    }

    [Fact]
    public void EncoderRejected_CarriesTheUnderlyingMessage()
    {
        BarcodeGenerationResult<string> result = BarcodeHelper.GenerateSvg("2234565", BarcodeFormat.UPC_E);

        Assert.Equal(BarcodeGenerationFailureReason.EncoderRejected, result.Reason);
        Assert.Contains("Number system", result.Message);
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_128, "Hello World 123")]
    [InlineData(BarcodeFormat.EAN_13, "4006381333931")]
    [InlineData(BarcodeFormat.EAN_8, "96385074")]
    [InlineData(BarcodeFormat.UPC_A, "036000291452")]
    [InlineData(BarcodeFormat.UPC_E, "01234565")]
    [InlineData(BarcodeFormat.CODE_39, "HELLO")]
    [InlineData(BarcodeFormat.CODE_93, "HELLO")]
    [InlineData(BarcodeFormat.ITF, "12345670")]
    [InlineData(BarcodeFormat.CODABAR, "A1234B")]
    public void OneDimensionalImages_AreWideAndShort(BarcodeFormat format, string input)
    {
        using Image<Rgba32> image = TestImages.Generate(format, input);

        Assert.True(image.Width >= 400, $"width {image.Width}");
        Assert.InRange(image.Height, 100, 240);
        Assert.True(image.Width >= 2 * image.Height, $"{image.Width}x{image.Height} is not wide and short");
    }

    [Theory]
    [InlineData(BarcodeFormat.DATA_MATRIX)]
    [InlineData(BarcodeFormat.AZTEC)]
    public void MatrixImages_AreSquare(BarcodeFormat format)
    {
        using Image<Rgba32> image = TestImages.Generate(format, "Hello, 2D world");

        Assert.Equal(image.Width, image.Height);
        Assert.True(image.Width >= 400, $"side {image.Width}");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Images_ContainOnlyOpaqueBlackAndWhitePixels(BarcodeFormat format, string input, BarcodeFormat expectedFormat, string expectedText)
    {
        // A renderer that stretches the module matrix leaves transparent pixels behind.
        Assert.NotNull(expectedText); // (unused data columns shared with the round trip cases)
        Assert.True(expectedFormat != BarcodeFormat.QR_CODE);
        using Image<Rgba32> image = TestImages.Generate(format, input);

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 pixel = image[x, y];
                Assert.True(pixel == new Rgba32(255, 255, 255, 255) || pixel == new Rgba32(0, 0, 0, 255), $"({x},{y}) is {pixel}");
            }
        }
    }

    [Fact]
    public void Pdf417Image_KeepsItsNaturalWideProportions()
    {
        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.PDF_417, "Hello, PDF417");

        Assert.True(image.Width > image.Height);
        Assert.True(image.Width >= 400);
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_128, "Hello")]
    [InlineData(BarcodeFormat.EAN_13, "4006381333931")]
    [InlineData(BarcodeFormat.CODABAR, "A1234B")]
    [InlineData(BarcodeFormat.DATA_MATRIX, "Hello")]
    [InlineData(BarcodeFormat.PDF_417, "Hello")]
    [InlineData(BarcodeFormat.AZTEC, "Hello")]
    public void Images_HaveAWhiteQuietZone_AndNoTextRow(BarcodeFormat format, string input)
    {
        using Image<Rgba32> image = TestImages.Generate(format, input);

        // First and last columns, and first row, are quiet zone. (1D barcodes have no vertical quiet zone: the
        // bars run the full height of the image.)
        for (int y = 0; y < image.Height; y++)
        {
            Assert.Equal(new Rgba32(255, 255, 255, 255), image[0, y]);
            Assert.Equal(new Rgba32(255, 255, 255, 255), image[image.Width - 1, y]);
        }

        if (BarcodeHelper.IsTwoDimensional(format))
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(new Rgba32(255, 255, 255, 255), image[x, 0]);
                Assert.Equal(new Rgba32(255, 255, 255, 255), image[x, image.Height - 1]);
            }
        }
        else
        {
            // No human readable text under the bars: the bottom row looks like the top row.
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(image[x, 0], image[x, image.Height - 1]);
            }
        }
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_128, "Hello")]
    [InlineData(BarcodeFormat.AZTEC, "Hello")]
    public void Svg_IsWellFormedXml(BarcodeFormat format, string input)
    {
        BarcodeGenerationResult<string> result = BarcodeHelper.GenerateSvg(input, format);

        Assert.True(result.IsSuccess);
        var document = new XmlDocument { XmlResolver = null };
        // ZXing writes a DOCTYPE referencing the SVG DTD: allow it but never fetch it.
        using var reader = XmlReader.Create(new StringReader(result.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        document.Load(reader);
        Assert.Equal("svg", document.DocumentElement!.LocalName);
        Assert.Empty(document.GetElementsByTagName("text"));
        Assert.NotEmpty(document.GetElementsByTagName("rect"));
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_128, "Hello World 123")]
    [InlineData(BarcodeFormat.EAN_13, "4006381333931")]
    [InlineData(BarcodeFormat.UPC_E, "0123456")]
    [InlineData(BarcodeFormat.CODE_39, "HELLO")]
    [InlineData(BarcodeFormat.ITF, "12345670")]
    [InlineData(BarcodeFormat.CODABAR, "A1234B")]
    [InlineData(BarcodeFormat.DATA_MATRIX, "Hello")]
    [InlineData(BarcodeFormat.PDF_417, "Hello")]
    [InlineData(BarcodeFormat.AZTEC, "Hello")]
    public void Svg_HasTheSameSizeAsTheImage(BarcodeFormat format, string input)
    {
        using Image<Rgba32> image = TestImages.Generate(format, input);
        BarcodeGenerationResult<string> svg = BarcodeHelper.GenerateSvg(input, format);

        Assert.True(svg.IsSuccess);
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(svg.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        document.Load(reader);
        string[] viewBox = document.DocumentElement!.GetAttribute("viewBox").Split(' ');
        Assert.Equal(image.Width, int.Parse(viewBox[2]));
        Assert.Equal(image.Height, int.Parse(viewBox[3]));
    }

    private static void AssertFailure<T>(BarcodeGenerationResult<T> result, BarcodeGenerationFailureReason expected)
        where T : class
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Null(result.EncodedText);
        Assert.Equal(expected, result.Reason);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }
}
