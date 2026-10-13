using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Tests;

internal static class TestImages
{
    /// <summary>Generates a barcode through the helper and fails the test if that does not work.</summary>
    internal static Image<Rgba32> Generate(BarcodeFormat format, string text)
    {
        BarcodeGenerationResult<Image<Rgba32>> result = BarcodeHelper.GenerateImage(text, format);
        Assert.True(result.IsSuccess, $"Could not generate {format} '{text}': {result.Reason} {result.Message}");
        return result.Value;
    }

    /// <summary>Puts <paramref name="symbols"/> on a white canvas at the given top-left positions. Disposes the symbols.</summary>
    internal static Image<Rgba32> Compose(int width, int height, params (Image<Rgba32> Image, int X, int Y)[] symbols)
    {
        var canvas = new Image<Rgba32>(width, height, Color.White);
        foreach ((Image<Rgba32> image, int x, int y) in symbols)
        {
            canvas.Mutate(context => context.DrawImage(image, new Point(x, y), 1f));
            image.Dispose();
        }

        return canvas;
    }

    /// <summary>Returns a copy of <paramref name="image"/> surrounded by a white border.</summary>
    internal static Image<Rgba32> WithBorder(Image<Rgba32> image, int border = 40)
        => Compose(image.Width + (2 * border), image.Height + (2 * border), (image.Clone(), border, border));

    internal static Image<Rgba32> Transform(Image<Rgba32> image, Action<IImageProcessingContext> action)
        => image.Clone(action);

    internal static MemoryStream ToPngStream(Image<Rgba32> image)
    {
        var stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;
        return stream;
    }
}
