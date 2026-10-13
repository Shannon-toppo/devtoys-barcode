using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace DevToys.Barcode.Helpers;

internal static partial class BarcodeHelper
{
    // Smallest width (1D) or side (2D) we aim for, in pixels. Modules are scaled by a whole number.
    private const int TargetSize = 480;
    private const int MinModuleSize = 2;
    private const int MinOneDimensionalHeight = 100;
    private const int MaxOneDimensionalHeight = 240;
    private const int TwoDimensionalQuietZone = 4; // modules

    private static readonly Regex TooLongMessage
        = new(@"too (many|large|big|long)|less than \d+|can't find a symbol|data too", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The formats the tool can generate, in the order they should be offered. The first one is the default.
    /// QR Code is intentionally absent: DevToys has a dedicated tool for it. Reading is not limited to this list.
    /// </summary>
    internal static IReadOnlyList<BarcodeFormat> GenerationFormats { get; }
        = [
            BarcodeFormat.CODE_128,
            BarcodeFormat.EAN_13,
            BarcodeFormat.EAN_8,
            BarcodeFormat.UPC_A,
            BarcodeFormat.UPC_E,
            BarcodeFormat.CODE_39,
            BarcodeFormat.CODE_93,
            BarcodeFormat.ITF,
            BarcodeFormat.CODABAR,
            BarcodeFormat.DATA_MATRIX,
            BarcodeFormat.PDF_417,
            BarcodeFormat.AZTEC
        ];

    /// <summary>
    /// Gets whether <paramref name="format"/> is a matrix or stacked (2D) symbology.
    /// </summary>
    internal static bool IsTwoDimensional(BarcodeFormat format)
        => format is BarcodeFormat.DATA_MATRIX or BarcodeFormat.PDF_417 or BarcodeFormat.AZTEC or BarcodeFormat.QR_CODE or BarcodeFormat.MAXICODE;

    /// <summary>
    /// Generates a barcode as a raster image (white background, black modules, quiet zone included, no text).
    /// </summary>
    /// <remarks>The caller owns <see cref="BarcodeGenerationResult{T}.Value"/> and must dispose it.</remarks>
    internal static BarcodeGenerationResult<Image<Rgba32>> GenerateImage(string? text, BarcodeFormat format)
    {
        return Generate<Image<Rgba32>>(
            text,
            format,
            (matrix, options) =>
            {
                var writer = new ZXing.ImageSharp.BarcodeWriter<Rgba32>
                {
                    Format = format,
                    Options = options
                };
                return writer.Write(matrix);
            });
    }

    /// <summary>
    /// Generates a barcode as an SVG document. It has the same modules, quiet zone and proportions as <see cref="GenerateImage"/>.
    /// </summary>
    internal static BarcodeGenerationResult<string> GenerateSvg(string? text, BarcodeFormat format)
    {
        return Generate<string>(
            text,
            format,
            (matrix, options) =>
            {
                var writer = new BarcodeWriterSvg
                {
                    Format = format,
                    Renderer = new SvgRenderer(),
                    Options = options
                };
                return writer.Write(matrix).Content;
            });
    }

    private static BarcodeGenerationResult<T> Generate<T>(
        string? text,
        BarcodeFormat format,
        Func<BitMatrix, EncodingOptions, T> render)
        where T : class
    {
        if (!GenerationFormats.Contains(format))
        {
            return BarcodeGenerationResult<T>.Failure(BarcodeGenerationFailureReason.EncoderRejected, $"{format} cannot be generated.");
        }

        if (!BarcodeInputValidator.TryValidate(text, format, out string encodedText, out BarcodeGenerationFailureReason reason, out string? message))
        {
            return BarcodeGenerationResult<T>.Failure(reason, message);
        }

        try
        {
            (BitMatrix matrix, EncodingOptions options) = Encode(encodedText, format);
            T value = render(matrix, options);
            return BarcodeGenerationResult<T>.Success(value, encodedText);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // ZXing signals every problem with ArgumentException or WriterException (and sometimes others).
            BarcodeGenerationFailureReason failureReason
                = TooLongMessage.IsMatch(ex.Message)
                ? BarcodeGenerationFailureReason.TooLong
                : BarcodeGenerationFailureReason.EncoderRejected;
            return BarcodeGenerationResult<T>.Failure(failureReason, ex.Message);
        }
    }

    /// <summary>
    /// Encodes <paramref name="text"/> into the final module matrix, sized so that every module is a whole number of
    /// pixels, and returns the options to give to the renderers. The matrix is encoded once and handed to both the
    /// image and the SVG renderer, which guarantees that they draw exactly the same thing. The options carry the exact
    /// size of the matrix, so a renderer never has to stretch it (which would leave gaps).
    /// </summary>
    private static (BitMatrix Matrix, EncodingOptions Options) Encode(string text, BarcodeFormat format)
    {
        bool twoDimensional = IsTwoDimensional(format);
        var options = new EncodingOptions
        {
            // Never draw the human-readable text: ZXing only does it in the SVG renderer, not in the image one.
            PureBarcode = true
        };

        if (twoDimensional)
        {
            options.Margin = format == BarcodeFormat.PDF_417 ? 0 : TwoDimensionalQuietZone;

            if (text.Any(c => c > 127))
            {
                options.Hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
                if (format == BarcodeFormat.PDF_417)
                {
                    options.Hints[EncodeHintType.PDF417_AUTO_ECI] = true;
                }
            }

            if (format == BarcodeFormat.DATA_MATRIX)
            {
                options.Hints[EncodeHintType.DATA_MATRIX_SHAPE] = ZXing.Datamatrix.Encoder.SymbolShapeHint.FORCE_SQUARE;
            }
        }

        var writer = new MultiFormatWriter();

        // The smallest rendering: one pixel per module. This also validates the content.
        BitMatrix unit = writer.encode(text, format, 1, 1, options.Hints);

        int width;
        int height;
        if (twoDimensional)
        {
            int scale = Math.Max(MinModuleSize, (int)Math.Ceiling(TargetSize / (double)Math.Max(unit.Width, unit.Height)));
            width = unit.Width * scale;
            height = unit.Height * scale;

            if (format == BarcodeFormat.PDF_417)
            {
                // The PDF417 encoder adds its margin in pixels after scaling the symbol: make it a quiet zone
                // of TwoDimensionalQuietZone modules.
                options.Hints[EncodeHintType.MARGIN] = TwoDimensionalQuietZone * scale;
            }
        }
        else
        {
            int scale = Math.Max(MinModuleSize, (int)Math.Ceiling(TargetSize / (double)unit.Width));
            width = unit.Width * scale;
            height = Math.Clamp(width / 4, MinOneDimensionalHeight, MaxOneDimensionalHeight);
        }

        BitMatrix matrix = writer.encode(text, format, width, height, options.Hints);
        options.Width = matrix.Width;
        options.Height = matrix.Height;
        return (matrix, options);
    }
}
