using ZXing;
using ZXing.Common;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Finds the end of a PDF417 symbol that ZXing did not report. ZXing's detector gives eight points: the outer and inner
/// edges of the start pattern on its top and bottom rows (0, 1, 4, 5), and those of the stop pattern (2, 3, 6, 7). When it
/// decodes a symbol from one pattern and the row indicators, the points of the other pattern are <c>null</c>, and the bounds
/// made of the points cover only that one pattern: a sixth of the width of a small symbol, and they leave the rest of it to be
/// decoded again in the parts of the picture around it.
/// </summary>
/// <remarks>
/// <para>
/// The rows of a PDF417 symbol are columns of codewords of 17 modules each, between the start pattern (17 modules, starting
/// with a bar of 8) and the stop pattern (18 modules, starting with a bar of 7). Every codeword starts with a bar and ends with
/// a space, and its bars are at most 6 modules wide. So from the inner edge of the pattern that was found, there is an edge
/// from light to dark across the whole height of the symbol every 17 modules, and the first such edge followed by a bar of at
/// least 7 modules is the other pattern.
/// </para>
/// <para>
/// The walk looks for each edge around where it is expected, and measures the module again from where it found it, so that
/// a module width that is a little off does not add up over the columns. It works on ZXing's black and white picture, the one
/// the symbol was decoded from. When an edge is not found (a damaged symbol, or one ZXing read on a busy background where the
/// other end is lost), the points are left as they are.
/// </para>
/// </remarks>
internal static class Pdf417EndFinder
{
    private const int StartModules = 17;
    private const int StopModules = 18;
    private const int CodewordModules = 17;

    // 30 data columns and the two row indicators, the most a PDF417 symbol has, and one more.
    private const int MaxSteps = 33;

    // How much more of the height of the symbol must be dark just after an edge than just before it.
    private const double EdgeScore = 0.5;

    // How much of the first 7 modules after an edge must be dark for it to be the start of the start or stop pattern.
    private const double PatternBar = 0.9;

    /// <summary>
    /// The eight points of a PDF417 result, with those of the missing pattern found in <paramref name="matrix"/> (in which the
    /// points are) when only one pattern was reported. Otherwise <paramref name="points"/>.
    /// </summary>
    internal static ResultPoint[] Complete(ResultPoint[] points, BitMatrix matrix)
    {
        if (points.Length != 8)
        {
            return points;
        }

        bool hasStart = points[0] is not null && points[1] is not null && points[4] is not null && points[5] is not null;
        bool hasStop = points[2] is not null && points[3] is not null && points[6] is not null && points[7] is not null;
        if (hasStart == hasStop)
        {
            return points;
        }

        (ResultPoint outerTop, ResultPoint outerBottom, ResultPoint innerTop, ResultPoint innerBottom)
            = hasStart ? (points[0], points[1], points[4], points[5]) : (points[2], points[3], points[6], points[7]);

        // From the outer to the inner edge of the pattern: towards the other end.
        double inwardX = ((innerTop.X + innerBottom.X) - (outerTop.X + outerBottom.X)) / 2.0;
        double inwardY = ((innerTop.Y + innerBottom.Y) - (outerTop.Y + outerBottom.Y)) / 2.0;
        double patternWidth = Math.Sqrt((inwardX * inwardX) + (inwardY * inwardY));
        double module = patternWidth / (hasStart ? StartModules : StopModules);
        if (module < 1)
        {
            return points;
        }

        // Positions along the rows are measured from the inner edge of the known pattern, increasing from the start pattern
        // towards the stop pattern (so they are negative when the stop pattern is the one known).
        double direction = hasStart ? 1 : -1;
        var band = new Band(matrix, innerTop, innerBottom, direction * inwardX / patternWidth, direction * inwardY / patternWidth);

        double edge = 0;
        for (int step = 1; step <= MaxSteps; step++)
        {
            double expected = direction * step * CodewordModules * module;
            double? found = band.FindEdge(expected, module);
            if (found is not { } position)
            {
                return points;
            }

            edge = position;
            module = Math.Abs(edge) / (step * CodewordModules);
            // A bar of 7 modules or more: the other pattern, which starts here. A bar of 6 and the space after it are still
            // mostly dark over 7 modules (5.8 of the 6.6 looked at, more once blurred), so nearly all of it must be.
            if (band.Darkness(edge + (0.2 * module), edge + (6.8 * module)) >= PatternBar
                && band.Darkness(edge + (6.25 * module), edge + (6.75 * module)) >= 0.5)
            {
                var completed = (ResultPoint[])points.Clone();
                if (hasStart)
                {
                    completed[6] = band.Point(edge, onTop: true);
                    completed[7] = band.Point(edge, onTop: false);
                    completed[2] = band.Point(edge + (StopModules * module), onTop: true);
                    completed[3] = band.Point(edge + (StopModules * module), onTop: false);
                }
                else
                {
                    completed[0] = band.Point(edge, onTop: true);
                    completed[1] = band.Point(edge, onTop: false);
                    completed[4] = band.Point(edge + (StartModules * module), onTop: true);
                    completed[5] = band.Point(edge + (StartModules * module), onTop: false);
                }

                return completed;
            }
        }

        return points;
    }

    /// <summary>
    /// The rows of the symbol, between the top and bottom points of the inner edge of the pattern that was found, moved along
    /// the rows by a position: how much of it is dark there.
    /// </summary>
    private sealed class Band(BitMatrix matrix, ResultPoint top, ResultPoint bottom, double alongX, double alongY)
    {
        // The pixels looked at down the height of the symbol, at most.
        private const int MaxSamples = 32;

        private readonly int samples = Math.Clamp((int)Math.Abs(bottom.Y - top.Y) + (int)Math.Abs(bottom.X - top.X), 3, MaxSamples);

        internal ResultPoint Point(double position, bool onTop)
        {
            ResultPoint origin = onTop ? top : bottom;
            return new ResultPoint((float)(origin.X + (position * alongX)), (float)(origin.Y + (position * alongY)));
        }

        /// <summary>
        /// Near <paramref name="expected"/> (within 2 modules), the position where the band goes from light to dark most
        /// clearly, or <c>null</c> when it nowhere does clearly enough.
        /// </summary>
        internal double? FindEdge(double expected, double module)
        {
            double best = double.NaN;
            double bestScore = EdgeScore;
            double increment = Math.Max(0.25, module / 8);
            for (double position = expected - (2 * module); position <= expected + (2 * module); position += increment)
            {
                double score = Darkness(position + (0.2 * module), position + (0.8 * module))
                    - Darkness(position - (0.8 * module), position - (0.2 * module));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = position;
                }
            }

            return double.IsNaN(best) ? null : best;
        }

        /// <summary>The share of dark pixels of the band between two positions (outside the picture counts as light).</summary>
        internal double Darkness(double from, double to)
        {
            int columns = Math.Max(2, (int)Math.Ceiling(to - from));
            int dark = 0;
            for (int c = 0; c < columns; c++)
            {
                double position = from + ((c + 0.5) * (to - from) / columns);
                for (int s = 0; s < samples; s++)
                {
                    double f = (s + 0.5) / samples;
                    int x = (int)Math.Floor(top.X + (f * (bottom.X - top.X)) + (position * alongX));
                    int y = (int)Math.Floor(top.Y + (f * (bottom.Y - top.Y)) + (position * alongY));
                    if (x >= 0 && y >= 0 && x < matrix.Width && y < matrix.Height && matrix[x, y])
                    {
                        dark++;
                    }
                }
            }

            return dark / (double)(columns * samples);
        }
    }
}
