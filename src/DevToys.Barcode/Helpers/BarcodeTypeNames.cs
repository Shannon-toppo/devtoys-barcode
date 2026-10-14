using ZXing;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// The names a person types on the command line to choose the type of a barcode to generate (<c>code128</c>,
/// <c>ean13</c>, <c>datamatrix</c>...). They are derived from <see cref="BarcodeHelper.GenerationFormats"/> and the display
/// names, so there is no second list to keep up to date: the display name without spaces, hyphens and so on, in lower case.
/// </summary>
internal static class BarcodeTypeNames
{
    /// <summary>The accepted names, in the order of <see cref="BarcodeHelper.GenerationFormats"/>. The first one is the default.</summary>
    internal static IReadOnlyList<string> Names { get; } = [.. BarcodeHelper.GenerationFormats.Select(GetName)];

    /// <summary>Gets the name to type for <paramref name="format"/>.</summary>
    internal static string GetName(BarcodeFormat format)
        => Normalize(BarcodeFormatNames.GetDisplayName(format));

    /// <summary>
    /// Finds the format a typed name stands for. The comparison ignores the case and everything that is not a letter or a
    /// digit, so <c>EAN-13</c>, <c>ean_13</c> and <c>ean13</c> are the same.
    /// </summary>
    internal static bool TryParse(string? value, out BarcodeFormat format)
    {
        string normalized = Normalize(value);
        foreach (BarcodeFormat candidate in BarcodeHelper.GenerationFormats)
        {
            if (normalized.Length > 0 && GetName(candidate) == normalized)
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    private static string Normalize(string? value)
        => value is null ? string.Empty : new string([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
