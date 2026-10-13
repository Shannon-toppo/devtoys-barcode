using SixLabors.ImageSharp;
using ZXing;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// A barcode found in an image.
/// </summary>
/// <param name="Format">The format ZXing recognised (for example <see cref="BarcodeFormat.EAN_13"/>).</param>
/// <param name="Text">The decoded text, exactly as ZXing reports it (check digits included, Codabar start/stop characters stripped).</param>
/// <param name="Bounds">
/// Where the symbol is, in pixels of the image that was read. Approximate: it is derived from the points ZXing reports
/// (the corners of a 2D symbol, or the ends of the bars plus a measured height for a 1D symbol).
/// </param>
internal sealed record BarcodeReadResult(BarcodeFormat Format, string Text, Rectangle Bounds)
{
    /// <summary>Horizontal centre of <see cref="Bounds"/>.</summary>
    public float X => Bounds.X + (Bounds.Width / 2f);

    /// <summary>Vertical centre of <see cref="Bounds"/>.</summary>
    public float Y => Bounds.Y + (Bounds.Height / 2f);
}
