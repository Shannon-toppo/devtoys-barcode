using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Finds the separate "islands" of dark content in an image (a symbol, a block of text, a picture...), so that each one
/// can be decoded on its own.
/// </summary>
/// <remarks>
/// ZXing's Data Matrix and Aztec detectors start from the middle of the image they are given and its multiple barcode
/// reader gives up after a few nested symbols. Handing the detectors a crop that contains just one island, with white
/// all around it, avoids both limits.
/// </remarks>
internal static class BarcodeRegionFinder
{
    private const int TargetGridSize = 200; // cells along the longest side
    private const int MinRegionSize = 16; // pixels
    private const double MaxCoverage = 0.9; // regions covering more of the image than that are the image itself
    private const double MergeDistance = 0.03; // of the longest side: closer islands are one region

    /// <summary>
    /// Returns the bounding rectangles of the islands of dark pixels, largest first.
    /// </summary>
    internal static List<Rectangle> FindRegions(Image<Rgba32> image)
    {
        int cellSize = Math.Max(4, (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)TargetGridSize));
        int gridWidth = (image.Width + cellSize - 1) / cellSize;
        int gridHeight = (image.Height + cellSize - 1) / cellSize;

        bool[] dark = new bool[gridWidth * gridHeight];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowStart = (y / cellSize) * gridWidth;
                for (int x = 0; x < row.Length; x++)
                {
                    int cell = rowStart + (x / cellSize);
                    if (!dark[cell] && BarcodeHelper.IsDark(row[x]))
                    {
                        dark[cell] = true;
                    }
                }
            }
        });

        // Group the dark cells that are close to each other: dilate, then label connected components.
        int radius = Math.Max(1, (int)Math.Ceiling(Math.Max(image.Width, image.Height) * MergeDistance / cellSize));
        bool[] dilated = Dilate(dark, gridWidth, gridHeight, radius);

        int[] labels = new int[dilated.Length];
        var regions = new List<Rectangle>();
        var queue = new Queue<int>();
        int label = 0;
        for (int start = 0; start < dilated.Length; start++)
        {
            if (!dilated[start] || labels[start] != 0)
            {
                continue;
            }

            label++;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            labels[start] = label;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int cx = cell % gridWidth;
                int cy = cell / gridWidth;

                if (dark[cell])
                {
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);
                }

                TryEnqueue(cx - 1, cy);
                TryEnqueue(cx + 1, cy);
                TryEnqueue(cx, cy - 1);
                TryEnqueue(cx, cy + 1);
            }

            if (maxX < 0)
            {
                continue;
            }

            var bounds = Rectangle.Intersect(
                new Rectangle(minX * cellSize, minY * cellSize, (maxX - minX + 1) * cellSize, (maxY - minY + 1) * cellSize),
                new Rectangle(0, 0, image.Width, image.Height));
            if (bounds.Width >= MinRegionSize
                && bounds.Height >= MinRegionSize
                && (long)bounds.Width * bounds.Height < MaxCoverage * image.Width * image.Height)
            {
                regions.Add(bounds);
            }
        }

        return regions.OrderByDescending(r => (long)r.Width * r.Height).ThenBy(r => r.Y).ThenBy(r => r.X).ToList();

        void TryEnqueue(int cx, int cy)
        {
            if (cx < 0 || cy < 0 || cx >= gridWidth || cy >= gridHeight)
            {
                return;
            }

            int cell = (cy * gridWidth) + cx;
            if (dilated[cell] && labels[cell] == 0)
            {
                labels[cell] = label;
                queue.Enqueue(cell);
            }
        }
    }

    private static bool[] Dilate(bool[] source, int width, int height, int radius)
    {
        bool[] horizontal = new bool[source.Length];
        for (int y = 0; y < height; y++)
        {
            int lastDark = int.MinValue / 2;
            for (int x = 0; x < width; x++)
            {
                if (source[(y * width) + x])
                {
                    lastDark = x;
                }

                horizontal[(y * width) + x] = x - lastDark <= radius;
            }

            lastDark = int.MaxValue / 2;
            for (int x = width - 1; x >= 0; x--)
            {
                if (source[(y * width) + x])
                {
                    lastDark = x;
                }

                horizontal[(y * width) + x] |= lastDark - x <= radius;
            }
        }

        bool[] result = new bool[source.Length];
        for (int x = 0; x < width; x++)
        {
            int lastDark = int.MinValue / 2;
            for (int y = 0; y < height; y++)
            {
                if (horizontal[(y * width) + x])
                {
                    lastDark = y;
                }

                result[(y * width) + x] = y - lastDark <= radius;
            }

            lastDark = int.MaxValue / 2;
            for (int y = height - 1; y >= 0; y--)
            {
                if (horizontal[(y * width) + x])
                {
                    lastDark = y;
                }

                result[(y * width) + x] |= lastDark - y <= radius;
            }
        }

        return result;
    }
}
