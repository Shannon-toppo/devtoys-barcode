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
/// as ZXing tries it when given the "also inverted" hint (for the whole picture, at the same time as the picture).
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
    /// Decodes <paramref name="luminance"/> with <paramref name="hints"/>. Returns the symbols found, possibly none.
    /// Exceptions of ZXing's readers are let through, as ZXing lets them through.
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

        var context = new Context(luminance, hints, alsoInverted, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = maxDegreeOfParallelism });
        Found? root = Find(context, image, 0, 0, 0);

        var results = new List<Result>();
        var texts = new HashSet<string>(StringComparer.Ordinal);
        Collect(root, results, texts);
        return [.. results];
    }

    private sealed record Context(LuminanceSource Luminance, IDictionary<DecodeHintType, object> Hints, bool AlsoInverted, ParallelOptions Options);

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

        Result? result = depth == 0 && context.AlsoInverted && context.Options.MaxDegreeOfParallelism > 1
            ? DecodeOneWithInvertedInParallel(context, image)
            : DecodeOne(context, image);
        if (result is null)
        {
            return null;
        }

        Result translated = Translate(result, xOffset, yOffset);
        ResultPoint[] points = result.ResultPoints;
        if (points is null || points.Length == 0)
        {
            return new Found(translated, []);
        }

        int width = image.Width;
        int height = image.Height;
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
    /// it: on a big picture each is long. Its result is only used when the picture gives none, as in ZXing.
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
    /// inverted" inverts it (in place, which cannot be done while the picture is decoded).
    /// </summary>
    private sealed class FixedBinarizer(LuminanceSource luminance, BinaryBitmap image, BitMatrix matrix) : Binarizer(luminance)
    {
        public override BitMatrix BlackMatrix => matrix;

        public override BitArray getBlackRow(int y, BitArray row) => image.getBlackRow(y, row);

        public override Binarizer createBinarizer(LuminanceSource source) => new HybridBinarizer(source);
    }

    private static Result Translate(Result result, int xOffset, int yOffset)
    {
        ResultPoint[] points = result.ResultPoints;
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
