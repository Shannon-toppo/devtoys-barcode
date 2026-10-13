using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Helpers;

internal static partial class BarcodeHelper
{
    // The largest side of the copy of a big picture on which the extra, slower attempts are made.
    private const int WorkingSize = 1600;

    // Upscaling is only worth it, and affordable, on small pictures.
    private const int MaxUpscaledSize = 1000;
    private const int MaxRegions = 12;

    // A bar that is only a few pixels high is a scan line through noise, not a barcode. (Kept low: the height of the
    // bars of a tilted symbol is hard to measure.)
    private const int MinBarHeight = 14;
    private const int MinUpcEBarHeight = 24;

    // ZXing's ITF reader only accepts the lengths below by default, plus anything longer than the largest one.
    // A valid ITF symbol holding 2 or 4 digits is therefore not read unless we explicitly allow those lengths.
    private static readonly int[] RelaxedItfLengths = [2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24];

    // Formats are decoded by family, because ZXing is much faster when it only looks for one kind of symbol.
    // The default list of each family is what ZXing reads when it is not given any restriction.
    private static readonly BarcodeFormat[] DefaultOneDimensionalFormats
        = [BarcodeFormat.CODE_128, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.ITF, BarcodeFormat.CODABAR, BarcodeFormat.RSS_14, BarcodeFormat.RSS_EXPANDED];

    private static readonly BarcodeFormat[] OneDimensionalFormats
        = [.. DefaultOneDimensionalFormats, BarcodeFormat.MSI, BarcodeFormat.PLESSEY, BarcodeFormat.UPC_EAN_EXTENSION];

    private static readonly BarcodeFormat[] MatrixFormats
        = [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.MAXICODE];

    private static readonly BarcodeFormat[] StackedFormats = [BarcodeFormat.PDF_417];

    [Flags]
    private enum Families
    {
        None = 0,
        OneDimensional = 1,
        Matrix = 2,
        Stacked = 4,
        All = OneDimensional | Matrix | Stacked
    }

    /// <summary>A picture prepared for decoding, and how much larger than the original it is.</summary>
    private sealed class Prepared : IDisposable
    {
        internal Prepared(Image<Rgba32> image, double scale, bool owned)
        {
            Image = image;
            Scale = scale;
            Owned = owned;
        }

        internal Image<Rgba32> Image { get; }

        internal double Scale { get; }

        internal bool Owned { get; }

        public void Dispose()
        {
            if (Owned)
            {
                Image.Dispose();
            }
        }
    }

    /// <summary>One way of looking for barcodes: how to prepare the picture, which angles, which families.</summary>
    private sealed record Pass(
        string Name,
        Families Families,
        double[] Angles,
        Func<Image<Rgba32>, Prepared>? Prepare = null,
        BarcodeFormat[]? OnlyFormats = null,
        bool RelaxItfLengths = false,
        int MaxSize = int.MaxValue,
        bool StackedOncePerRightAngle = false);

    // 1D symbols and PDF417 need the picture rotated by 90 degrees to be read when they are vertical. Both orientations
    // are always tried, so a picture with a horizontal and a vertical barcode gives both. (ZXing's PDF417 detector turns
    // the picture itself, see RunPass.)
    private static readonly Pass MainPass = new("main", Families.All, [0, 90], StackedOncePerRightAngle: true);

    // The factors by which the surroundings of a 1D result are enlarged to confirm it.
    private static readonly double[] ConfirmationFactors = [1.5, 2];

    // The angles (clockwise, in degrees) tried by the "tilted" fallback. With the right-angle passes, a symbol that is
    // tilted by up to about 20 degrees is read at each of them.
    private static readonly double[] TiltAngles = [22, -22, 45, -45, 67, -67];

    // Measurement hook: receives the name and duration in milliseconds of each step of a reading.
    internal static Action<string, long>? Trace { get; set; }

    private static T Timed<T>(string name, Func<T> action)
    {
        if (Trace is not { } trace)
        {
            return action();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        T result = action();
        trace(name, stopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>The extra attempts, in the order they are made when the main pass finds nothing. They run on a reduced copy.</summary>
    private static Pass[] GetFallbackPasses()
        => [
            // Light-on-dark symbols. ZXing's "try inverted" option does not work with the ImageSharp luminance source
            // for 1D barcodes, so invert the pixels ourselves. The other families handle it natively.
            new("inverted", Families.OneDimensional, [0, 90], Prepare: frame => new Prepared(frame.Clone(context => context.Invert()), 1, owned: true)),

            // ITF symbols with a length ZXing does not allow by default (we can generate those).
            new("itf-short", Families.OneDimensional, [0, 90], OnlyFormats: [BarcodeFormat.ITF], RelaxItfLengths: true),

            // Uneven lighting and low contrast.
            new("enhanced", Families.All, [0, 90], Prepare: frame => new Prepared(BarcodeImageEnhancer.Normalize(frame, blurSigma: 0), 1, owned: true)),

            // Noise.
            new("denoised", Families.All, [0, 90], Prepare: frame => new Prepared(BarcodeImageEnhancer.Normalize(frame, blurSigma: 1.6f), 1, owned: true)),

            // Symbols that are tilted by an angle the readers do not tolerate.
            new("tilted", Families.All, TiltAngles),

            // Very small bars.
            new("enlarged", Families.All, [0, 90], Prepare: Enlarge, MaxSize: MaxUpscaledSize),
        ];

    private static Prepared Enlarge(Image<Rgba32> frame)
        => new(frame.Clone(context => context.Resize(frame.Width * 2, frame.Height * 2)), 2, owned: true);

    /// <summary>
    /// Reads every barcode found in the image contained in <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">An image in any format ImageSharp can decode.</param>
    /// <param name="cancellationToken">Observed while loading the image and between decoding attempts.</param>
    /// <param name="possibleFormats">Optional restriction. <c>null</c> or empty means every format ZXing can read.</param>
    /// <returns>The barcodes found, ordered top to bottom then left to right. Empty when there is none.</returns>
    /// <exception cref="UnknownImageFormatException">The stream is not an image.</exception>
    internal static async Task<IReadOnlyList<BarcodeReadResult>> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken,
        IReadOnlyCollection<BarcodeFormat>? possibleFormats = null)
    {
        using Image<Rgba32> bitmap = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
        return Read(bitmap, cancellationToken, possibleFormats);
    }

    /// <summary>
    /// Reads every barcode found in <paramref name="image"/>. The image is not modified or disposed.
    /// </summary>
    /// <inheritdoc cref="ReadAsync(Stream, CancellationToken, IReadOnlyCollection{BarcodeFormat}?)"/>
    internal static IReadOnlyList<BarcodeReadResult> Read(
        Image<Rgba32> image,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<BarcodeFormat>? possibleFormats = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<BarcodeFormat>? filter = possibleFormats is { Count: > 0 } ? [.. possibleFormats] : null;

        // A picture that is one flat colour has nothing to read, however long we look.
        if (IsFlat(image))
        {
            return [];
        }

        // The picture as it is, at full resolution.
        List<BarcodeReadResult> found = Deduplicate(Timed("main", () => RunPass(image, MainPass, filter, cancellationToken)));
        found = Deduplicate([.. found, .. Timed("regions", () => FindInRegions(image, found, filter, cancellationToken))]);
        if (found.Count > 0)
        {
            return Order(found, image.Height);
        }

        // Nothing: try harder, on a reduced copy of big pictures so that the attempts stay affordable.
        double scale = 1;
        Image<Rgba32>? reduced = null;
        int largestSide = Math.Max(image.Width, image.Height);
        if (largestSide > WorkingSize)
        {
            scale = WorkingSize / (double)largestSide;
            cancellationToken.ThrowIfCancellationRequested();
            reduced = Timed("resize", () => image.Clone(context => context.Resize(
                (int)Math.Round(image.Width * scale),
                (int)Math.Round(image.Height * scale))));
        }

        try
        {
            Image<Rgba32> frame = reduced ?? image;
            foreach (Pass pass in GetFallbackPasses())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Math.Max(frame.Width, frame.Height) > pass.MaxSize)
                {
                    continue;
                }

                List<BarcodeReadResult> results = Deduplicate(Timed(pass.Name, () => RunPass(frame, pass, filter, cancellationToken)));
                if (results.Count > 0)
                {
                    if (scale != 1)
                    {
                        results = [.. results.Select(r => r with { Bounds = Scale(r.Bounds, 1 / scale, image.Width, image.Height) })];
                    }

                    return Order(results, image.Height);
                }
            }
        }
        finally
        {
            reduced?.Dispose();
        }

        return [];
    }

    /// <summary>Whether the luminance of the picture hardly varies (sampled, so it is quick).</summary>
    private static bool IsFlat(Image<Rgba32> image)
    {
        int min = 255;
        int max = 0;
        int stepX = Math.Max(1, image.Width / 256);
        int stepY = Math.Max(1, image.Height / 256);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y += stepY)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x += stepX)
                {
                    int luma = Luma(row[x]);
                    min = Math.Min(min, luma);
                    max = Math.Max(max, luma);
                }
            }
        });
        return max - min < 12;
    }

    private static Rectangle Scale(Rectangle bounds, double factor, int maxWidth, int maxHeight)
    {
        int left = Math.Clamp((int)Math.Round(bounds.Left * factor), 0, maxWidth - 1);
        int top = Math.Clamp((int)Math.Round(bounds.Top * factor), 0, maxHeight - 1);
        int right = Math.Clamp((int)Math.Round(bounds.Right * factor), left + 1, maxWidth);
        int bottom = Math.Clamp((int)Math.Round(bounds.Bottom * factor), top + 1, maxHeight);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// ZXing has two limits that make it miss symbols in a picture holding several of them: its multiple barcode
    /// reader stops recursing after a few levels, and its Data Matrix and Aztec detectors only look around the
    /// middle of the picture they get. So also decode, one by one, the islands of dark content that are not a symbol
    /// found already.
    /// </summary>
    /// <remarks>
    /// An island is whatever dark content is close to other dark content, and "close" is a fraction of the size of the
    /// picture, so a symbol next to a barcode that was read is one island with it. Such a symbol is looked for in what
    /// remains once the symbols already read are wiped out, and the picture is looked at again after every round that
    /// reads something, as the first symbol read may have been the one that kept a second one off the middle of a crop.
    /// </remarks>
    private static List<BarcodeReadResult> FindInRegions(
        Image<Rgba32> image,
        List<BarcodeReadResult> found,
        HashSet<BarcodeFormat>? filter,
        CancellationToken cancellationToken)
    {
        var results = new List<BarcodeReadResult>();
        long imageArea = (long)image.Width * image.Height;
        var finder = new BarcodeRegionFinder(image);

        // Where the symbols read so far are, slightly enlarged because the bounds that ZXing gives are a little short.
        var explained = new List<Rectangle>();
        AddExplained(found.Select(f => f.Bounds));

        var tried = new HashSet<Rectangle>();
        var empty = new HashSet<Rectangle>(); // tried, and nothing was read (or too big to be worth it)
        int attempts = 0;
        bool progress = true;
        while (progress && attempts < MaxRegions)
        {
            progress = false;
            List<Rectangle> wiped = [.. explained];

            // The islands, then, for the ones that nothing was read from, the parts they split into when "close" gets stricter.
            int radius = finder.DefaultRadius;
            List<Rectangle> regions = finder.FindRegions(explained, radius);
            while (regions.Count > 0)
            {
                var unread = new List<Rectangle>();
                foreach (Rectangle region in regions)
                {
                    if (!tried.Add(region))
                    {
                        if (empty.Contains(region))
                        {
                            unread.Add(region);
                        }

                        continue;
                    }

                    // On a big picture, a region that covers most of it would only repeat the decoding that was just done.
                    if (imageArea > 1_000_000 && (long)region.Width * region.Height > imageArea * 0.6)
                    {
                        empty.Add(region);
                        unread.Add(region);
                        continue;
                    }

                    if (attempts++ >= MaxRegions)
                    {
                        return results;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    int padding = Math.Max(16, Math.Max(region.Width, region.Height) / 5);
                    using Image<Rgba32> crop = image.Clone(context => context.Crop(region));
                    using var canvas = new Image<Rgba32>(region.Width + (2 * padding), region.Height + (2 * padding), Color.White);
                    canvas.Mutate(context => context.DrawImage(crop, new Point(padding, padding), 1f));
                    foreach (Rectangle done in wiped.Where(w => w.IntersectsWith(region)))
                    {
                        WipeOut(canvas, new Rectangle(done.X - region.X + padding, done.Y - region.Y + padding, done.Width, done.Height));
                    }

                    List<BarcodeReadResult> read = RunPass(canvas, MainPass, filter, cancellationToken);
                    foreach (BarcodeReadResult result in read)
                    {
                        Rectangle b = result.Bounds;
                        Rectangle bounds = new(b.X - padding + region.X, b.Y - padding + region.Y, b.Width, b.Height);
                        results.Add(result with { Bounds = bounds });
                        AddExplained([bounds]);
                        progress = true;
                    }

                    if (read.Count == 0)
                    {
                        empty.Add(region);
                        unread.Add(region);
                    }
                }

                // Two symbols that are close to each other are one island, and nothing is read from it when neither of
                // them is around its middle. Split it, and look at the parts.
                if (radius == 1 || unread.Count == 0)
                {
                    break;
                }

                radius = Math.Max(1, radius / 2);
                regions = [.. finder.FindRegions(explained, radius).Where(part => unread.Any(u => u.Contains(part)))];
            }
        }

        return results;

        void AddExplained(IEnumerable<Rectangle> bounds)
        {
            foreach (Rectangle b in bounds.Where(b => !b.IsEmpty))
            {
                int margin = (Math.Max(b.Width, b.Height) / 50) + 2;
                explained.Add(Rectangle.Inflate(b, margin, margin));
            }
        }
    }

    /// <summary>Paints <paramref name="area"/> white.</summary>
    private static void WipeOut(Image<Rgba32> image, Rectangle area)
    {
        area = Rectangle.Intersect(area, new Rectangle(0, 0, image.Width, image.Height));
        image.ProcessPixelRows(accessor =>
        {
            for (int y = area.Top; y < area.Bottom; y++)
            {
                accessor.GetRowSpan(y).Slice(area.Left, area.Width).Fill(new Rgba32(255, 255, 255, 255));
            }
        });
    }

    /// <summary>
    /// Decodes <paramref name="frame"/> as <paramref name="pass"/> says. The bounds of the results are in the
    /// coordinates of <paramref name="frame"/>. The angles and the families are decoded in parallel: they are independent.
    /// </summary>
    private static List<BarcodeReadResult> RunPass(Image<Rgba32> frame, Pass pass, HashSet<BarcodeFormat>? filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using Prepared prepared = pass.Prepare is null ? new Prepared(frame, 1, owned: false) : pass.Prepare(frame);
        Image<Rgba32> image = prepared.Image;

        // One work item per angle and per family. The view of an angle is shared by its items.
        var views = new Dictionary<double, View>();
        var items = new List<(double Angle, Families Family)>();
        foreach (double angle in pass.Angles)
        {
            views[angle] = new View(image, angle, views.TryGetValue(0, out View? upright) ? upright : null);

            // Every family is tried at every angle: matrix symbols are rotation invariant in theory, but ZXing's Aztec and
            // Data Matrix detectors read some small ones only when the picture is turned.
            Families families = pass.Families;

            // ZXing's PDF417 detector tries the picture turned by 90, 180 and 270 degrees itself whenever it finds
            // nothing, so an angle that is a right angle away from one decoded already mostly repeats it. Not entirely:
            // the turned picture is binarized on a different grid, which reads some degraded symbols. That is left to
            // the slower attempts, on the reduced picture; at full resolution PDF417 is the most expensive family.
            if (pass.StackedOncePerRightAngle
                && pass.Angles.TakeWhile(a => a != angle).Any(a => Math.Abs(Math.IEEERemainder(angle - a, 90)) < 1e-9))
            {
                families &= ~Families.Stacked;
            }

            foreach (Families family in new[] { Families.OneDimensional, Families.Matrix, Families.Stacked })
            {
                if (families.HasFlag(family))
                {
                    items.Add((angle, family));
                }
            }
        }

        var perItem = new List<BarcodeReadResult>[items.Count];
        try
        {
            Parallel.For(
                0,
                items.Count,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)) },
                i =>
                {
                    (double angle, Families family) = items[i];
                    View view = views[angle];
                    var list = new List<BarcodeReadResult>();
                    foreach (BarcodeReadResult result in DecodeFamilies(view, family, pass, filter, prepared.Scale, cancellationToken))
                    {
                        Rectangle bounds = angle == 0 ? result.Bounds : MapToUnrotated(result.Bounds, angle, view.Luminance.Width, view.Luminance.Height, image.Width, image.Height);
                        list.Add(result with { Bounds = prepared.Scale == 1 ? bounds : Scale(bounds, 1 / prepared.Scale, frame.Width, frame.Height) });
                    }

                    perItem[i] = list;
                });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            foreach (View view in views.Values)
            {
                view.Dispose();
            }
        }

        return [.. perItem.Where(list => list is not null).SelectMany(list => list)];
    }

    /// <summary>
    /// The picture of a pass turned clockwise by one of its angles, and its luminance, which is what ZXing decodes. Each is
    /// made once, by the first work item that needs it: the luminance by every item, the picture only to measure and
    /// confirm a 1D result.
    /// </summary>
    private sealed class View : IDisposable
    {
        private readonly double angle;
        private readonly Lazy<Image<Rgba32>> picture;
        private readonly Lazy<LuminanceSource> luminance;

        internal View(Image<Rgba32> image, double angle, View? upright)
        {
            this.angle = angle;
            picture = angle == 0
                ? new Lazy<Image<Rgba32>>(image)
                : new Lazy<Image<Rgba32>>(() => Timed($"  rotate {angle}", () => Rotate(image, angle)), LazyThreadSafetyMode.ExecutionAndPublication);

            // Turning by a right angle only moves the pixels, so the luminance of the upright picture can be turned
            // instead of the picture: the result is the same, and is much cheaper to get.
            luminance = new Lazy<LuminanceSource>(
                () => angle == 90 && upright is not null
                    ? Timed("  rotate luminance 90", () => RotateClockwise(upright.Luminance))
                    : Timed($"  luminance {angle}", () => new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(Picture)),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Image<Rgba32> Picture => picture.Value;

        internal LuminanceSource Luminance => luminance.Value;

        public void Dispose()
        {
            if (angle != 0 && picture.IsValueCreated)
            {
                picture.Value.Dispose();
            }
        }
    }

    /// <summary>Luminance values computed already (ZXing's own luminance sources compute them from a picture).</summary>
    private sealed class Luminances : BaseLuminanceSource
    {
        internal Luminances(int width, int height)
            : base(width, height)
        {
        }

        internal byte[] Values => luminances;

        protected override LuminanceSource CreateLuminanceSource(byte[] newLuminances, int width, int height)
            => new Luminances(width, height) { luminances = newLuminances };
    }

    /// <summary>The luminance of the picture turned clockwise by 90 degrees, as <see cref="RotateMode.Rotate90"/> turns it.</summary>
    internal static LuminanceSource RotateClockwise(LuminanceSource source)
    {
        byte[] values = source.Matrix;
        int width = source.Width;
        int height = source.Height;
        var rotated = new Luminances(height, width);
        byte[] target = rotated.Values;

        // The pixel at (x, y) goes to (height - 1 - y, x). By tiles, so that both arrays are read and written in cache-sized blocks.
        const int Tile = 64;
        for (int y0 = 0; y0 < height; y0 += Tile)
        {
            int y1 = Math.Min(height, y0 + Tile);
            for (int x0 = 0; x0 < width; x0 += Tile)
            {
                int x1 = Math.Min(width, x0 + Tile);
                for (int y = y0; y < y1; y++)
                {
                    int column = height - 1 - y;
                    int row = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        target[(x * height) + column] = values[row + x];
                    }
                }
            }
        }

        return rotated;
    }

    private static Image<Rgba32> Rotate(Image<Rgba32> image, double angle)
        => angle == 90
            ? image.Clone(context => context.Rotate(RotateMode.Rotate90))
            : image.Clone(context => context.Rotate((float)angle, KnownResamplers.Triangle).BackgroundColor(Color.White));

    /// <summary>
    /// Gives the rectangle of the original picture (<paramref name="originalWidth"/> x <paramref name="originalHeight"/>)
    /// that corresponds to <paramref name="bounds"/> in the picture turned clockwise by <paramref name="angle"/>
    /// (<paramref name="rotatedWidth"/> x <paramref name="rotatedHeight"/>).
    /// </summary>
    private static Rectangle MapToUnrotated(Rectangle bounds, double angle, int rotatedWidth, int rotatedHeight, int originalWidth, int originalHeight)
    {
        double radians = angle * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double rotatedCenterX = rotatedWidth / 2.0;
        double rotatedCenterY = rotatedHeight / 2.0;
        double originalCenterX = originalWidth / 2.0;
        double originalCenterY = originalHeight / 2.0;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((int px, int py) in new[] { (bounds.Left, bounds.Top), (bounds.Right, bounds.Top), (bounds.Right, bounds.Bottom), (bounds.Left, bounds.Bottom) })
        {
            double dx = px - rotatedCenterX;
            double dy = py - rotatedCenterY;
            double x = originalCenterX + (dx * cos) + (dy * sin);
            double y = originalCenterY - (dx * sin) + (dy * cos);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        int left = Math.Clamp((int)Math.Floor(minX), 0, originalWidth - 1);
        int top = Math.Clamp((int)Math.Floor(minY), 0, originalHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(maxX), left + 1, originalWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(maxY), top + 1, originalHeight);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>Runs ZXing on <paramref name="view"/> for one family. Results are checked, and have bounds in the coordinates of the view.</summary>
    private static List<BarcodeReadResult> DecodeFamilies(
        View view,
        Families family,
        Pass pass,
        HashSet<BarcodeFormat>? filter,
        double scale,
        CancellationToken cancellationToken)
    {
        var results = new List<BarcodeReadResult>();
        (BarcodeFormat[] defaults, BarcodeFormat[] members) = family switch
        {
            Families.OneDimensional => (DefaultOneDimensionalFormats, OneDimensionalFormats),
            Families.Matrix => (MatrixFormats, MatrixFormats),
            _ => (StackedFormats, StackedFormats),
        };

        // Without a restriction a family looks for its default formats. With one, for those that belong to it.
        BarcodeFormat[] formats = filter is null ? defaults : members.Where(filter.Contains).ToArray();
        if (pass.OnlyFormats is not null)
        {
            formats = formats.Where(pass.OnlyFormats.Contains).ToArray();
        }

        if (formats.Length == 0)
        {
            return results;
        }

        cancellationToken.ThrowIfCancellationRequested();
        LuminanceSource luminance = view.Luminance;
        foreach (Result result in Timed($"  zxing {family} {luminance.Width}x{luminance.Height}", () => Decode(luminance, formats, pass.RelaxItfLengths, tryInverted: family != Families.OneDimensional)))
        {
            if (string.IsNullOrEmpty(result.Text))
            {
                continue;
            }

            var candidate = new BarcodeReadResult(result.BarcodeFormat, result.Text, GetBounds(view, result));
            if (family != Families.OneDimensional || IsGenuineOneDimensional(view.Picture, candidate, pass.RelaxItfLengths, scale, cancellationToken))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private static Result[] Decode(LuminanceSource luminance, BarcodeFormat[] formats, bool relaxItfLengths, bool tryInverted = false)
    {
        var barcodeReader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options =
            {
                TryHarder = true,

                // Only Data Matrix, Aztec, QR and PDF417 do something of this, and it makes 1D decoding twice as slow.
                TryInverted = tryInverted,
                PossibleFormats = formats
            }
        };

        // With "try harder", ZXing's 1D readers also decode the picture turned by 90 degrees whenever they find nothing on
        // it. Every pass decodes its pictures at right angle pairs itself (0 and 90, and each tilted angle has one about
        // 90 degrees away), and a result is confirmed in the orientation it was read in, so that would only decode every
        // orientation twice. (Only the 1D readers know of this hint.)
        barcodeReader.Options.Hints[DecodeHintType.TRY_HARDER_WITHOUT_ROTATION] = true;

        if (relaxItfLengths)
        {
            barcodeReader.Options.Hints[DecodeHintType.ALLOWED_LENGTHS] = RelaxedItfLengths;
        }

        try
        {
            return barcodeReader.DecodeMultiple(luminance) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // ZXing signals malformed data with exceptions that it does not always catch itself (a GS1 DataBar
            // Expanded decoder throws on noise, for instance). Nothing could be read.
            return [];
        }
    }

    /// <summary>
    /// ZXing's 1D readers accept a pattern found on one single scan line, which is how lines of text, textures and
    /// noise sometimes read as a barcode. A real barcode has bars that are tall, and decodes again when the picture
    /// around it is enlarged (which changes the pixels, and so the scan lines, that the readers see).
    /// </summary>
    private static bool IsGenuineOneDimensional(Image<Rgba32> image, BarcodeReadResult result, bool relaxItfLengths, double scale, CancellationToken cancellationToken)
    {
        Rectangle b = result.Bounds;

        // In the pixels of the original picture: the picture may have been enlarged to be decoded.
        double height = b.Height / scale;
        double width = b.Width / scale;
        // UPC-E has so little structure (six digits and a check digit) that it is what lines of text read as most often.
        if (height < (result.Format == BarcodeFormat.UPC_E ? MinUpcEBarHeight : MinBarHeight))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Generous, because some readers insist on a wide quiet zone (UPC-E: 42 pixels with 7 pixel modules).
        int horizontalMargin = Math.Max(16, b.Width / 3);
        int verticalMargin = 3;
        var area = Rectangle.Intersect(
            new Rectangle(b.X - horizontalMargin, b.Y - verticalMargin, b.Width + (2 * horizontalMargin), b.Height + (2 * verticalMargin)),
            new Rectangle(0, 0, image.Width, image.Height));
        if (area.Width < 8 || area.Height < 4)
        {
            return false;
        }

        // Whether a given factor lets ZXing read a symbol is partly luck (blurred ones are read at 1.5 but not at 2, for
        // instance), so a couple of factors are tried. A pattern that was only a lucky scan line is read at none.
        using Image<Rgba32> crop = image.Clone(context => context.Crop(area));
        foreach (double factor in ConfirmationFactors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Image<Rgba32> enlarged = crop.Clone(context => context.Resize((int)(area.Width * factor), (int)(area.Height * factor)));
            if (Decode(new ZXing.ImageSharp.ImageSharpLuminanceSource<Rgba32>(enlarged), [result.Format], relaxItfLengths).Any(r => r.BarcodeFormat == result.Format && r.Text == result.Text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes the results that have the same format and text as an earlier one.</summary>
    private static List<BarcodeReadResult> Deduplicate(IEnumerable<BarcodeReadResult> results)
    {
        var unique = new List<BarcodeReadResult>();
        var seen = new HashSet<(BarcodeFormat, string)>();

        foreach (BarcodeReadResult result in results)
        {
            if (seen.Add((result.Format, result.Text)))
            {
                unique.Add(result);
            }
        }

        return unique;
    }

    private static IReadOnlyList<BarcodeReadResult> Order(List<BarcodeReadResult> unique, int imageHeight)
    {
        if (unique.Count < 2)
        {
            return unique;
        }

        // Top to bottom, then left to right. Symbols whose vertical positions are within 5% of the image height
        // of the first symbol of a row are considered to be on the same row, so that tiny differences between
        // side-by-side barcodes do not decide the order.
        float rowTolerance = Math.Max(1f, imageHeight * 0.05f);
        List<BarcodeReadResult> byY = unique.OrderBy(r => r.Y).ThenBy(r => r.X).ThenBy(r => r.Format).ThenBy(r => r.Text, StringComparer.Ordinal).ToList();

        var ordered = new List<BarcodeReadResult>(byY.Count);
        var row = new List<BarcodeReadResult>();
        float rowStartY = 0;
        foreach (BarcodeReadResult result in byY)
        {
            if (row.Count > 0 && result.Y - rowStartY > rowTolerance)
            {
                FlushRow();
            }

            if (row.Count == 0)
            {
                rowStartY = result.Y;
            }

            row.Add(result);
        }

        FlushRow();
        return ordered;

        void FlushRow()
        {
            ordered.AddRange(row.OrderBy(r => r.X).ThenBy(r => r.Y).ThenBy(r => r.Format).ThenBy(r => r.Text, StringComparer.Ordinal));
            row.Clear();
        }
    }

    private static Rectangle GetBounds(View view, Result result)
    {
        // ZXing can leave null entries in the points of some symbols (PDF417 with unreadable rows).
        ResultPoint[]? points = result.ResultPoints?.Where(p => p is not null).ToArray();
        if (points is null || points.Length == 0)
        {
            return Rectangle.Empty;
        }

        int left = (int)Math.Floor(points.Min(p => p.X));
        int right = (int)Math.Ceiling(points.Max(p => p.X));
        int top = (int)Math.Floor(points.Min(p => p.Y));
        int bottom = (int)Math.Ceiling(points.Max(p => p.Y));

        // A 1D barcode is reported as the two ends of the row that was scanned (not even the whole symbol: the last
        // bars are left out), which can be anywhere between the top and the bottom of the bars. Measure the real
        // extent of the bars instead.
        if (points.Length == 2 && bottom - top <= 1)
        {
            Image<Rgba32> image = view.Picture;
            (top, bottom, int module, int threshold) = MeasureBars(image, left, right, (top + bottom) / 2);
            left = ExtendHorizontally(image, left, -1, top, bottom, 6 * module, threshold);
            right = ExtendHorizontally(image, right, 1, top, bottom, 6 * module, threshold);
        }

        return new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// Moves outwards from <paramref name="start"/> while there are dark pixels between <paramref name="top"/> and
    /// <paramref name="bottom"/> at most <paramref name="maxGap"/> columns apart, and returns the last such column.
    /// </summary>
    private static int ExtendHorizontally(Image<Rgba32> image, int start, int direction, int top, int bottom, int maxGap, int threshold)
    {
        int lastDark = start;
        for (int x = start + direction; x >= 0 && x < image.Width && Math.Abs(x - lastDark) <= maxGap; x += direction)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (Luma(image[x, y]) < threshold)
                {
                    lastDark = x;
                    break;
                }
            }
        }

        return lastDark;
    }

    /// <summary>
    /// Walks up and down from <paramref name="scanRow"/> for as long as the pixels between <paramref name="left"/> and
    /// <paramref name="right"/> keep the same dark/light pattern, and returns the first and last row of that span,
    /// the width of the narrowest bar or space in pixels, and the luminance that separates bars from spaces.
    /// </summary>
    private static (int Top, int Bottom, int Module, int Threshold) MeasureBars(Image<Rgba32> image, int left, int right, int scanRow)
    {
        left = Math.Clamp(left, 0, image.Width - 1);
        right = Math.Clamp(right, left, image.Width - 1);
        scanRow = Math.Clamp(scanRow, 0, image.Height - 1);

        int width = right - left + 1;
        int[] luma = new int[width];
        int min = 255;
        int max = 0;
        for (int i = 0; i < width; i++)
        {
            luma[i] = Luma(image[left + i, scanRow]);
            min = Math.Min(min, luma[i]);
            max = Math.Max(max, luma[i]);
        }

        // The middle of the range of this row, so that low contrast or dark pictures are measured too.
        int threshold = (min + max) / 2;
        if (max - min < 16)
        {
            return (scanRow, scanRow, 1, threshold);
        }

        var reference = new bool[width];
        for (int i = 0; i < width; i++)
        {
            reference[i] = luma[i] < threshold;
        }

        double referenceMean = luma.Average();
        double referenceNorm = Math.Sqrt(luma.Sum(v => (v - referenceMean) * (v - referenceMean)));

        // The bars of a tilted symbol shift sideways from one row to the next: follow them along a few slopes
        // (up to about 17 degrees) and keep the longest.
        int top = scanRow;
        int bottom = scanRow;
        foreach (double slope in new[] { 0, -0.1, 0.1, -0.2, 0.2, -0.3, 0.3 })
        {
            int t = scanRow;
            while (t > 0 && RowMatches(image, left, t - 1, luma, referenceMean, referenceNorm, (int)Math.Round(slope * (t - 1 - scanRow))))
            {
                t--;
            }

            int b = scanRow;
            while (b < image.Height - 1 && RowMatches(image, left, b + 1, luma, referenceMean, referenceNorm, (int)Math.Round(slope * (b + 1 - scanRow))))
            {
                b++;
            }

            if (b - t > bottom - top)
            {
                top = t;
                bottom = b;
            }
        }

        int module = width;
        int run = 1;
        for (int i = 1; i < width; i++)
        {
            if (reference[i] == reference[i - 1])
            {
                run++;
            }
            else
            {
                module = Math.Min(module, run);
                run = 1;
            }
        }

        return (top, bottom, Math.Max(1, module), threshold);
    }

    /// <summary>
    /// Whether the row looks like the reference row (shifted sideways by <paramref name="shift"/> pixels): their
    /// luminance is strongly correlated. Anti-aliasing and blur make an exact comparison of dark and light pixels too brittle.
    /// </summary>
    private static bool RowMatches(Image<Rgba32> image, int left, int row, int[] reference, double referenceMean, double referenceNorm, int shift)
    {
        double sum = 0;
        double sumSquares = 0;
        double sumProducts = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            int x = Math.Clamp(left + i + shift, 0, image.Width - 1);
            double value = Luma(image[x, row]);
            sum += value;
            sumSquares += value * value;
            sumProducts += value * (reference[i] - referenceMean);
        }

        double mean = sum / reference.Length;
        double norm = Math.Sqrt(Math.Max(0, sumSquares - (reference.Length * mean * mean)));
        if (norm < 1e-6 || referenceNorm < 1e-6)
        {
            return false;
        }

        return sumProducts / (norm * referenceNorm) >= 0.75;
    }

    private static int Luma(Rgba32 pixel)
        // Transparent pixels count as white, like ZXing's luminance source does when reading.
        => pixel.A < 128 ? 255 : ((pixel.R * 299) + (pixel.G * 587) + (pixel.B * 114)) / 1000;

    internal static bool IsDark(Rgba32 pixel) => Luma(pixel) < 128;
}
