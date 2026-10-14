using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;
using ZXing.Common;
using ZXing.PDF417;

namespace DevToys.Barcode.Tests;

public class Pdf417EndFinderTests
{
    private static readonly int[] StartPattern = [0, 1, 4, 5];
    private static readonly int[] StopPattern = [2, 3, 6, 7];

    public static TheoryData<string, RotateMode, bool> Cases()
    {
        var data = new TheoryData<string, RotateMode, bool>();
        foreach (string text in new[] { "Hello PDF417 123", "Beta PDF417 second symbol", "0123456789 ABCDEFGHIJ abcdefghij 0123456789" })
        {
            foreach (RotateMode mode in new[] { RotateMode.None, RotateMode.Rotate90, RotateMode.Rotate180, RotateMode.Rotate270 })
            {
                data.Add(text, mode, true);
                data.Add(text, mode, false);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OneEndMissing_IsFoundWhereZXingFindsItInTheWholeSymbol(string text, RotateMode mode, bool startKnown)
    {
        // ZXing gives all eight points for a clean symbol: drop one end, and it must come back where ZXing had it.
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, text);
        using Image<Rgba32> turned = TestImages.Transform(symbol, context => context.Rotate(mode));
        using Image<Rgba32> image = TestImages.Compose(turned.Width + 200, turned.Height + 160, (turned.Clone(), 120, 70));
        (ResultPoint[] full, BitMatrix matrix) = Detect(image);
        Assert.All(full, p => Assert.NotNull(p));

        ResultPoint[] partial = (ResultPoint[])full.Clone();
        foreach (int i in startKnown ? StopPattern : StartPattern)
        {
            partial[i] = null!;
        }

        ResultPoint[] completed = Pdf417EndFinder.Complete(partial, matrix);

        // Within a module (4 px in generated symbols).
        const float tolerance = 3f;
        for (int i = 0; i < 8; i++)
        {
            Assert.True(
                Math.Abs(completed[i].X - full[i].X) <= tolerance && Math.Abs(completed[i].Y - full[i].Y) <= tolerance,
                $"{mode}, point {i}: {completed[i]} instead of {full[i]}");
        }
    }

    [Fact]
    public void BothEndsKnown_OrNone_PointsAreUnchanged()
    {
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> image = TestImages.WithBorder(symbol, 60);
        (ResultPoint[] full, BitMatrix matrix) = Detect(image);
        var none = new ResultPoint[8];

        Assert.Same(full, Pdf417EndFinder.Complete(full, matrix));
        Assert.Same(none, Pdf417EndFinder.Complete(none, matrix));
    }

    [Fact]
    public void OtherEndNotThere_PointsAreUnchanged()
    {
        // The end that is known, and nothing where the rest of the symbol should be: the points are not made up.
        using Image<Rgba32> symbol = TestImages.Generate(BarcodeFormat.PDF_417, "Hello PDF417 123");
        using Image<Rgba32> image = TestImages.WithBorder(symbol, 60);
        (ResultPoint[] full, BitMatrix matrix) = Detect(image);
        int cut = (int)Math.Ceiling(full[4].X) + 6;
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = cut; x < matrix.Width; x++)
            {
                matrix[x, y] = false;
            }
        }

        ResultPoint[] partial = (ResultPoint[])full.Clone();
        foreach (int i in StopPattern)
        {
            partial[i] = null!;
        }

        Assert.Same(partial, Pdf417EndFinder.Complete(partial, matrix));
    }

    /// <summary>ZXing's points for the PDF417 symbol of <paramref name="image"/>, in its coordinates, and the black and white picture they were found in.</summary>
    private static (ResultPoint[] Points, BitMatrix Matrix) Detect(Image<Rgba32> image)
    {
        var bitmap = new BinaryBitmap(new HybridBinarizer(new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(image)));
        Result? result = new PDF417Reader().decode(bitmap, new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true });
        Assert.NotNull(result);
        return (MultipleBarcodeDecoder.ToPictureFrame(result, image.Width, image.Height), bitmap.BlackMatrix);
    }
}
