using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Turns a picture by any angle onto a white background, as
/// <c>Rotate(degrees, KnownResamplers.Triangle).BackgroundColor(Color.White)</c> does, with about a fifth of the processor time.
/// </summary>
/// <remarks>
/// ImageSharp's affine transform is generic: every pixel goes through a sampling window, vectors and pixel format
/// conversions, about 0.7 s of processor time for a 1600 x 1200 picture, and the "tilted" attempts turn the picture six
/// times. The bilinear ("triangle") rotation needs at most four source pixels per pixel. The size and the placement of the result are ImageSharp's (its own rotation
/// matrix), and the arithmetic follows its steps (premultiplied alpha, a rounding to bytes, then the white background),
/// so that the result differs from ImageSharp's by at most one level on a few pixels.
/// </remarks>
internal static class BarcodeImageRotator
{
    internal static Image<Rgba32> RotateOnWhite(Image<Rgba32> source, float degrees, int maxDegreeOfParallelism)
    {
        var processor = new RotateProcessor(degrees, KnownResamplers.Triangle, source.Size);
        Matrix3x2.Invert(processor.TransformMatrix, out Matrix3x2 inverse);
        Size size = processor.DestinationSize;

        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        var pixels = new Rgba32[sourceWidth * sourceHeight];
        source.CopyPixelDataTo(pixels);

        var rotated = new Rgba32[size.Width * size.Height];
        const int RowsPerBlock = 32;
        Parallel.For(
            0,
            (size.Height + RowsPerBlock - 1) / RowsPerBlock,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            block =>
            {
                int end = Math.Min(size.Height, (block + 1) * RowsPerBlock);
                for (int y = block * RowsPerBlock; y < end; y++)
                {
                    int rowStart = y * size.Width;
                    for (int x = 0; x < size.Width; x++)
                    {
                        rotated[rowStart + x] = Sample(pixels, sourceWidth, sourceHeight, Vector2.Transform(new Vector2(x, y), inverse));
                    }
                }
            });

        return Image.LoadPixelData<Rgba32>(rotated, size.Width, size.Height);
    }

    /// <summary>The pixel at <paramref name="point"/> of the source, on white.</summary>
    private static Rgba32 Sample(Rgba32[] pixels, int width, int height, Vector2 point)
    {
        // ImageSharp takes the pixels within one pixel of the point (clamped to the picture), weighted by the triangle
        // function: only the two rows and the two columns around the point weigh something.
        int top = Math.Clamp((int)MathF.Floor(point.Y - 1), 0, height - 1);
        int bottom = Math.Clamp((int)MathF.Ceiling(point.Y + 1), 0, height - 1);
        int left = Math.Clamp((int)MathF.Floor(point.X - 1), 0, width - 1);
        int right = Math.Clamp((int)MathF.Ceiling(point.X + 1), 0, width - 1);
        if (top == bottom || left == right)
        {
            // Off the picture: transparent, so the background.
            return new Rgba32(255, 255, 255, 255);
        }

        int y0 = Math.Max(top, (int)MathF.Floor(point.Y));
        int y1 = Math.Min(bottom, (int)MathF.Floor(point.Y) + 1);
        int x0 = Math.Max(left, (int)MathF.Floor(point.X));
        int x1 = Math.Min(right, (int)MathF.Floor(point.X) + 1);

        Vector4 sum = Vector4.Zero;
        for (int y = y0; y <= y1; y++)
        {
            float wy = Triangle(y - point.Y);
            if (wy == 0)
            {
                continue;
            }

            int rowStart = y * width;
            for (int x = x0; x <= x1; x++)
            {
                float wx = Triangle(x - point.X);
                if (wx == 0)
                {
                    continue;
                }

                Rgba32 source = pixels[rowStart + x];
                Vector4 pixel = new Vector4(source.R, source.G, source.B, source.A) / 255f;
                if (source.A != 255)
                {
                    pixel = new Vector4(pixel.X * pixel.W, pixel.Y * pixel.W, pixel.Z * pixel.W, pixel.W);
                }

                sum += pixel * wx * wy;
            }
        }

        // ImageSharp stores the rotated pixel (not premultiplied), then lays it on white.
        if (sum.W > 0)
        {
            sum = new Vector4(sum.X / sum.W, sum.Y / sum.W, sum.Z / sum.W, sum.W);
        }

        var rotated = new Rgba32(sum);
        if (rotated.A == 255)
        {
            return rotated;
        }

        float alpha = rotated.A / 255f;
        return new Rgba32(OnWhite(rotated.R, alpha), OnWhite(rotated.G, alpha), OnWhite(rotated.B, alpha), 255);
    }

    private static float Triangle(float distance)
    {
        distance = Math.Abs(distance);
        return distance < 1 ? 1 - distance : 0;
    }

    private static byte OnWhite(byte value, float alpha)
        => (byte)Math.Clamp((int)(((value / 255f * alpha) + (1 - alpha)) * 255f + 0.5f), 0, 255);
}
