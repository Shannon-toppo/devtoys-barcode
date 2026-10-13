using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;

namespace DevToys.Barcode.Measure;

internal sealed record SymbolSpec(string Name, BarcodeFormat Format, string Input, string ExpectedText);

internal static class Symbols
{
    internal static readonly SymbolSpec[] All =
    [
        new("Code128", BarcodeFormat.CODE_128, "Hello-123", "Hello-123"),
        new("EAN-13", BarcodeFormat.EAN_13, "4006381333931", "4006381333931"),
        new("EAN-8", BarcodeFormat.EAN_8, "96385074", "96385074"),
        new("UPC-A", BarcodeFormat.UPC_A, "036000291452", "036000291452"),
        new("UPC-E", BarcodeFormat.UPC_E, "01234565", "01234565"),
        new("Code39", BarcodeFormat.CODE_39, "HELLO-123", "HELLO-123"),
        new("Code93", BarcodeFormat.CODE_93, "HELLO-123", "HELLO-123"),
        new("ITF", BarcodeFormat.ITF, "12345670", "12345670"),
        new("Codabar", BarcodeFormat.CODABAR, "A123456B", "123456"),
        new("DataMatrix", BarcodeFormat.DATA_MATRIX, "Hello 123", "Hello 123"),
        new("PDF417", BarcodeFormat.PDF_417, "Hello PDF417 123", "Hello PDF417 123"),
        new("Aztec", BarcodeFormat.AZTEC, "Hello 123", "Hello 123"),
        new("QR", BarcodeFormat.QR_CODE, "https://example.com", "https://example.com"),
    ];

    internal static Image<Rgba32> Render(SymbolSpec spec)
    {
        if (spec.Format == BarcodeFormat.QR_CODE)
        {
            var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 4 };
            BitMatrix unit = new MultiFormatWriter().encode(spec.Input, spec.Format, 1, 1, hints);
            int scale = Math.Max(2, (int)Math.Ceiling(480.0 / unit.Width));
            BitMatrix matrix = new MultiFormatWriter().encode(spec.Input, spec.Format, unit.Width * scale, unit.Height * scale, hints);
            var writer = new ZXing.ImageSharp.BarcodeWriter<Rgba32> { Format = spec.Format, Options = new EncodingOptions { Width = matrix.Width, Height = matrix.Height } };
            return writer.Write(matrix);
        }

        BarcodeGenerationResult<Image<Rgba32>> result = BarcodeHelper.GenerateImage(spec.Input, spec.Format);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException($"{spec.Name}: {result.Reason}");
    }

    /// <summary>The width in pixels of the narrowest bar or module, measured on the middle row.</summary>
    internal static int NarrowestRun(Image<Rgba32> image)
    {
        int y = image.Height / 2;
        int min = int.MaxValue;
        int run = 1;
        for (int x = 1; x < image.Width; x++)
        {
            if (BarcodeHelper.IsDark(image[x, y]) == BarcodeHelper.IsDark(image[x - 1, y]))
            {
                run++;
            }
            else
            {
                min = Math.Min(min, run);
                run = 1;
            }
        }

        return min == int.MaxValue ? 1 : min;
    }
}
