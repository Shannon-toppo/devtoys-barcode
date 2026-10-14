using ZXing;
using ZXing.Common;
using ZXing.Multi;
using ZXing.Multi.QrCode;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Finds every symbol in a picture the way ZXing's <see cref="GenericMultipleBarcodeReader"/> does, faster on big pictures.
/// </summary>
/// <remarks>
/// <para>
/// ZXing's reader decodes the picture, and when it finds a symbol, decodes the parts of the picture to its left, above it,
/// to its right and below it, and so on around every symbol found in those parts, a few levels deep. Around a 1D barcode
/// on a 12 megapixel picture that is a couple of dozen decodings, one after the other, in one call that cannot be
/// interrupted.
/// </para>
/// <para>
/// Which parts are decoded only depends on where the symbols are, not on the symbols found before (those only decide
/// whether a result is kept: one with the text of an earlier one is not). So here the parts around a symbol are decoded
/// in parallel, and the results are put together in the order ZXing would have found them: the results are the same.
/// The cancellation token is checked before each decoding, and the inverted picture is tried as a separate decoding,
/// as ZXing tries it when given the "also inverted" hint (for the whole picture, at the same time as the picture when
/// that is safe, see <see cref="CanDecodeInvertedInParallel"/>).
/// </para>
/// <para>
/// One deliberate difference: ZXing's PDF417 detector turns the picture by 90, 180 or 270 degrees when it finds nothing
/// upright, and gives the points of the symbol in the coordinates of the turned picture, which ZXing's own reader then
/// takes for coordinates of the picture it decodes (so it also looks for more symbols in the wrong parts of the picture).
/// Here those points are brought back to the coordinates of the picture that was decoded before anything uses them, see
/// <see cref="ToPictureFrame"/>. The text, the format and the order of the results are unchanged.
/// </para>
/// </remarks>
internal static class MultipleBarcodeDecoder
{
    // ZXing's values.
    private const int MaxDepth = 4;
    private const int MinDimensionToRecur = 30;

    /// <summary>A symbol that was found, in the coordinates of the whole picture, and what was found around it.</summary>
    private sealed record Found(Result Result, Found?[] Around);

    /// <summary>
    /// Decodes <paramref name="luminance"/> with <paramref name="hints"/>. Returns the symbols found, possibly none, with
    /// the points of each in the coordinates of <paramref name="luminance"/> (<see cref="ToPictureFrame"/>). Exceptions of
    /// ZXing's readers are let through, as ZXing lets them through.
    /// </summary>
    internal static Result[] Decode(
        LuminanceSource luminance,
        IDictionary<DecodeHintType, object> hints,
        bool alsoInverted,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var image = new BinaryBitmap(new HybridBinarizer(luminance));
        if (hints.TryGetValue(DecodeHintType.POSSIBLE_FORMATS, out object? formats)
            && formats is IList<BarcodeFormat> { Count: 1 } list
            && list[0] == BarcodeFormat.QR_CODE)
        {
            // What ZXing uses for QR codes alone (it does not know of "also inverted").
            cancellationToken.ThrowIfCancellationRequested();
            return new QRCodeMultiReader().decodeMultiple(image, hints) ?? [];
        }

        var context = new Context(
            luminance,
            hints,
            alsoInverted,
            alsoInverted && maxDegreeOfParallelism > 1 && CanDecodeInvertedInParallel(hints),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = maxDegreeOfParallelism });
        Found? root = Find(context, image, 0, 0, 0);

        var results = new List<Result>();
        var texts = new HashSet<string>(StringComparer.Ordinal);
        Collect(root, results, texts);
        return [.. results];
    }

    private sealed record Context(LuminanceSource Luminance, IDictionary<DecodeHintType, object> Hints, bool AlsoInverted, bool InvertedInParallel, ParallelOptions Options);

    // The readers that work on the black and white picture as a whole (BinaryBitmap.BlackMatrix). Every other reader of
    // ZXing's MultiFormatReader scans rows of the picture (BinaryBitmap.getBlackRow).
    private static readonly BarcodeFormat[] FormatsReadWithoutRows
        = [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.MAXICODE, BarcodeFormat.PDF_417];

    /// <summary>
    /// Whether the picture and its inverted copy can be decoded at the same time with <paramref name="hints"/>: when none
    /// of the readers that may run scans rows. The inverted copy takes its rows from the binarizer of the picture
    /// (<see cref="FixedBinarizer"/>), and ZXing's <see cref="GlobalHistogramBinarizer"/> (so <see cref="HybridBinarizer"/>
    /// too) keeps the buffers it makes a row in as fields: two threads asking it for rows would write in each other's.
    /// </summary>
    /// <remarks>
    /// Only the 1D readers (and the Intelligent Mail reader) ask for rows; QR Code, Data Matrix, Aztec, MaxiCode and PDF417
    /// use the black and white matrix, which is made before the two are decoded and only read. A restriction to those formats
    /// is the only case allowed: without one, or with any other format, ZXing's reader also runs the 1D readers.
    /// </remarks>
    internal static bool CanDecodeInvertedInParallel(IDictionary<DecodeHintType, object> hints)
        => hints.TryGetValue(DecodeHintType.POSSIBLE_FORMATS, out object? formats)
            && formats is IEnumerable<BarcodeFormat> list
            && list.Any()
            && list.All(FormatsReadWithoutRows.Contains);

    /// <summary>ZXing's results in the order its reader adds them: a symbol, then what is around it, left, above, right and below.</summary>
    private static void Collect(Found? found, List<Result> results, HashSet<string> texts)
    {
        if (found is null)
        {
            return;
        }

        if (texts.Add(found.Result.Text))
        {
            results.Add(found.Result);
        }

        foreach (Found? around in found.Around)
        {
            Collect(around, results, texts);
        }
    }

    private static Found? Find(Context context, BinaryBitmap image, int xOffset, int yOffset, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        Result? result = depth == 0 && context.InvertedInParallel
            ? DecodeOneWithInvertedInParallel(context, image)
            : DecodeOne(context, image);
        if (result is null)
        {
            return null;
        }

        int width = image.Width;
        int height = image.Height;

        // In the coordinates of this picture (or crop), before they are used to translate the result or to choose the parts to decode next.
        ResultPoint[] points = ToPictureFrame(result, width, height);
        Result translated = Translate(result, points, xOffset, yOffset);
        if (points.Length == 0)
        {
            return new Found(translated, []);
        }

        float minX = width;
        float minY = height;
        float maxX = 0f;
        float maxY = 0f;
        foreach (ResultPoint point in points)
        {
            if (point is null)
            {
                continue;
            }

            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        var parts = new List<Func<Found?>>(4);
        if (minX > MinDimensionToRecur)
        {
            parts.Add(() => Find(context, image.crop(0, 0, (int)minX, height), xOffset, yOffset, depth + 1));
        }

        if (minY > MinDimensionToRecur)
        {
            parts.Add(() => Find(context, image.crop(0, 0, width, (int)minY), xOffset, yOffset, depth + 1));
        }

        if (maxX < width - MinDimensionToRecur)
        {
            parts.Add(() => Find(context, image.crop((int)maxX, 0, width - (int)maxX, height), xOffset + (int)maxX, yOffset, depth + 1));
        }

        if (maxY < height - MinDimensionToRecur)
        {
            parts.Add(() => Find(context, image.crop(0, (int)maxY, width, height - (int)maxY), xOffset, yOffset + (int)maxY, depth + 1));
        }

        var around = new Found?[parts.Count];
        try
        {
            Parallel.For(0, parts.Count, context.Options, i => around[i] = parts[i]());
        }
        catch (AggregateException ex)
        {
            // As if the parts had been decoded one after the other: the reading stops with the exception.
            throw ex.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? new OperationCanceledException(context.Options.CancellationToken);
        }

        return new Found(translated, around);
    }

    /// <summary>One decoding, as ZXing's <see cref="MultiFormatReader"/> does it, with the inverted picture tried separately.</summary>
    private static Result? DecodeOne(Context context, BinaryBitmap image)
    {
        context.Options.CancellationToken.ThrowIfCancellationRequested();
        Result? result = new MultiFormatReader().decode(image, context.Hints);
        if (result is null && context.AlsoInverted)
        {
            context.Options.CancellationToken.ThrowIfCancellationRequested();
            image.BlackMatrix.flip();
            result = new MultiFormatReader().decode(image, context.Hints);
        }

        return result;
    }

    /// <summary>
    /// Like <see cref="DecodeOne"/>, with the inverted picture decoded at the same time as the picture rather than after
    /// it: on a big picture each is long. Its result is only used when the picture gives none, as in ZXing. Only for hints
    /// that <see cref="CanDecodeInvertedInParallel"/> allows.
    /// </summary>
    private static Result? DecodeOneWithInvertedInParallel(Context context, BinaryBitmap image)
    {
        context.Options.CancellationToken.ThrowIfCancellationRequested();
        var flipped = (BitMatrix)image.BlackMatrix.Clone();
        flipped.flip();
        var inverted = new BinaryBitmap(new FixedBinarizer(context.Luminance, image, flipped));

        (Result? Result, Exception? Error) normal = default;
        (Result? Result, Exception? Error) other = default;
        Parallel.Invoke(
            context.Options,
            () => normal = Attempt(() => new MultiFormatReader().decode(image, context.Hints)),
            () => other = Attempt(() => new MultiFormatReader().decode(inverted, context.Hints)));

        // What ZXing would have done: the inverted picture is only decoded (and its exceptions only matter) when the picture gives nothing.
        if (normal.Error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(normal.Error);
        }

        if (normal.Result is not null)
        {
            return normal.Result;
        }

        if (other.Error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(other.Error);
        }

        return other.Result;

        static (Result?, Exception?) Attempt(Func<Result?> decode)
        {
            try
            {
                return (decode(), null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }
    }

    /// <summary>
    /// Gives a black and white picture made already: the picture of the whole luminance, inverted, as ZXing's "also
    /// inverted" inverts it (in place, which cannot be done while the picture is decoded). Its rows are those of the
    /// picture, from the same binarizer, which is not safe to use from two threads: see <see cref="CanDecodeInvertedInParallel"/>.
    /// </summary>
    private sealed class FixedBinarizer(LuminanceSource luminance, BinaryBitmap image, BitMatrix matrix) : Binarizer(luminance)
    {
        public override BitMatrix BlackMatrix => matrix;

        public override BitArray getBlackRow(int y, BitArray row) => image.getBlackRow(y, row);

        public override Binarizer createBinarizer(LuminanceSource source) => new HybridBinarizer(source);
    }

    /// <summary>
    /// The points of <paramref name="result"/> in the coordinates of the picture that was decoded, which is
    /// <paramref name="width"/> x <paramref name="height"/>. ZXing gives them so for every symbol but PDF417: its detector turns
    /// the black and white picture by 90, 180 or 270 degrees when it finds nothing upright, and gives the points in the turned
    /// picture. The turn it applied is in the metadata of the result (the orientation of the symbol in the picture). The
    /// orientation of another format is not a turn of its points: ZXing's 1D readers bring their points back themselves.
    /// </summary>
    /// <remarks>
    /// This is where the correction is made, because it needs the size of the picture that was decoded (a crop has its own),
    /// and the points must be right before they are translated by the offset of the crop and before they choose the parts
    /// around the symbol that are decoded next. What the callers do afterwards (the bounds, the reader's own 90 degree and tilted
    /// views) then works with points in the frame of the picture it decoded, as for any other format.
    /// A point is a pixel column or row, except for the end of the pattern on the right, which is the column after its last
    /// pixel. Turning keeps the numbers as they are, so the mapping is exact for them and is checked against the ink of
    /// generated symbols, in square and non-square pictures and against the edges: the right edge of a symbol that was turned by
    /// 90 or 270 degrees is a pixel short, as the bottom edge of an upright one already is.
    /// </remarks>
    internal static ResultPoint[] ToPictureFrame(Result result, int width, int height)
    {
        ResultPoint[] points = result.ResultPoints ?? [];
        int orientation = 0;
        if (result.BarcodeFormat == BarcodeFormat.PDF_417
            && result.ResultMetadata is { } metadata
            && metadata.TryGetValue(ResultMetadataType.ORIENTATION, out object? value)
            && value is int degrees)
        {
            orientation = degrees;
        }

        return ToPictureFrame(points, orientation, width, height);
    }

    /// <summary>Brings points found in the picture ZXing's PDF417 detector turned by <paramref name="orientation"/> degrees back to the picture <paramref name="width"/> x <paramref name="height"/>.</summary>
    internal static ResultPoint[] ToPictureFrame(ResultPoint[] points, int orientation, int width, int height)
    {
        Func<float, float, (float X, float Y)>? map = orientation switch
        {
            90 => (x, y) => (width - 1 - y, x),
            180 => (x, y) => (width - x, height - 1 - y),
            270 => (x, y) => (y, height - x),
            _ => null,
        };
        if (map is null)
        {
            return points;
        }

        var mapped = new ResultPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] is { } point)
            {
                (float x, float y) = map(point.X, point.Y);
                mapped[i] = new ResultPoint(x, y);
            }
        }

        return mapped;
    }

    private static Result Translate(Result result, ResultPoint[] points, int xOffset, int yOffset)
    {
        var translated = new ResultPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] is { } point)
            {
                translated[i] = new ResultPoint(point.X + xOffset, point.Y + yOffset);
            }
        }

        var copy = new Result(result.Text, result.RawBytes, result.NumBits, translated, result.BarcodeFormat);
        copy.putAllMetadata(result.ResultMetadata);
        return copy;
    }
}
