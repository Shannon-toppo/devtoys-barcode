using ZXing;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Up-front validation (and check digit handling) for the formats where the rules are simple.
/// Formats that are not covered here (Data Matrix, PDF417, Aztec) are left to the encoder.
/// </summary>
internal static class BarcodeInputValidator
{
    private const int MaxOneDimensionalLength = 80;
    private const string CodabarDataCharacters = "0123456789-$:/.+";
    private const string CodabarGuardCharacters = "ABCD";

    /// <summary>
    /// Validates <paramref name="text"/> for <paramref name="format"/>.
    /// </summary>
    /// <param name="text">The user input.</param>
    /// <param name="format">The target format.</param>
    /// <param name="normalizedText">The text to give to the encoder: the input, with the check digit appended when it was missing.</param>
    /// <param name="reason">The failure reason when this method returns <c>false</c>.</param>
    /// <param name="message">A short, non-localized description of the problem.</param>
    /// <returns><c>true</c> when the text can be handed to the encoder.</returns>
    internal static bool TryValidate(
        string? text,
        BarcodeFormat format,
        out string normalizedText,
        out BarcodeGenerationFailureReason reason,
        out string? message)
    {
        normalizedText = text ?? string.Empty;
        reason = default;
        message = null;

        if (string.IsNullOrEmpty(text))
        {
            return Fail(BarcodeGenerationFailureReason.EmptyInput, "The input is empty.", out reason, out message);
        }

        switch (format)
        {
            case BarcodeFormat.EAN_13:
                return ValidateDigitsWithCheckDigit(text, 12, format, out normalizedText, out reason, out message);

            case BarcodeFormat.EAN_8:
                return ValidateDigitsWithCheckDigit(text, 7, format, out normalizedText, out reason, out message);

            case BarcodeFormat.UPC_A:
                return ValidateDigitsWithCheckDigit(text, 11, format, out normalizedText, out reason, out message);

            case BarcodeFormat.UPC_E:
                return ValidateDigitsWithCheckDigit(text, 7, format, out normalizedText, out reason, out message);

            case BarcodeFormat.ITF:
                return ValidateItf(text, out reason, out message);

            case BarcodeFormat.CODE_128:
                return ValidateAscii(text, '\0', int.MaxValue, out reason, out message);

            case BarcodeFormat.CODE_39:
                // ZXing's Code 39 encoder switches to "full ASCII" mode for lowercase and control characters,
                // and its reader decodes that. '*' is the start/stop character and corrupts the symbol if it is data.
                return ValidateAscii(text, '*', MaxOneDimensionalLength, out reason, out message);

            case BarcodeFormat.CODE_93:
                return ValidateAscii(text, '\0', MaxOneDimensionalLength, out reason, out message);

            case BarcodeFormat.CODABAR:
                return ValidateCodabar(text, out reason, out message);

            default:
                return true;
        }
    }

    /// <summary>
    /// Computes the GS1 modulo-10 check digit of <paramref name="digitsWithoutCheckDigit"/>
    /// (valid for EAN-13, EAN-8 and UPC-A data, where the rightmost data digit has weight 3).
    /// </summary>
    internal static int ComputeCheckDigit(string digitsWithoutCheckDigit)
    {
        int sum = 0;
        bool weightThree = true;
        for (int i = digitsWithoutCheckDigit.Length - 1; i >= 0; i--)
        {
            int digit = digitsWithoutCheckDigit[i] - '0';
            sum += weightThree ? digit * 3 : digit;
            weightThree = !weightThree;
        }

        return (10 - (sum % 10)) % 10;
    }

    /// <summary>
    /// Expands a 7 digit UPC-E body (number system digit + 6 digits) to its 11 digit UPC-A equivalent.
    /// </summary>
    internal static string ExpandUpcE(string upcE7)
    {
        char numberSystem = upcE7[0];
        string d = upcE7.Substring(1, 6); // d[0]..d[5]
        return d[5] switch
        {
            '0' or '1' or '2' => $"{numberSystem}{d[0]}{d[1]}{d[5]}0000{d[2]}{d[3]}{d[4]}",
            '3' => $"{numberSystem}{d[0]}{d[1]}{d[2]}00000{d[3]}{d[4]}",
            '4' => $"{numberSystem}{d[0]}{d[1]}{d[2]}{d[3]}00000{d[4]}",
            _ => $"{numberSystem}{d[0]}{d[1]}{d[2]}{d[3]}{d[4]}0000{d[5]}"
        };
    }

    private static bool ValidateDigitsWithCheckDigit(
        string text,
        int lengthWithoutCheckDigit,
        BarcodeFormat format,
        out string normalizedText,
        out BarcodeGenerationFailureReason reason,
        out string? message)
    {
        normalizedText = text;

        if (!text.All(char.IsAsciiDigit))
        {
            return Fail(BarcodeGenerationFailureReason.InvalidCharacters, $"{format} only accepts digits.", out reason, out message);
        }

        if (text.Length != lengthWithoutCheckDigit && text.Length != lengthWithoutCheckDigit + 1)
        {
            return Fail(
                BarcodeGenerationFailureReason.WrongLength,
                $"{format} needs {lengthWithoutCheckDigit} digits (check digit computed) or {lengthWithoutCheckDigit + 1} digits (check digit included), but got {text.Length}.",
                out reason,
                out message);
        }

        // For UPC-E the check digit is computed on the UPC-A equivalent. The encoder itself rejects number
        // systems other than 0 and 1, so leave those to it instead of reporting a misleading check digit error.
        string body = text.Substring(0, lengthWithoutCheckDigit);
        if (format == BarcodeFormat.UPC_E && body[0] != '0' && body[0] != '1')
        {
            reason = default;
            message = null;
            return true;
        }

        int expected = ComputeCheckDigit(format == BarcodeFormat.UPC_E ? ExpandUpcE(body) : body);
        if (text.Length == lengthWithoutCheckDigit)
        {
            normalizedText = body + expected;
        }
        else if (text[^1] - '0' != expected)
        {
            return Fail(BarcodeGenerationFailureReason.BadCheckDigit, $"The check digit should be {expected}.", out reason, out message);
        }

        reason = default;
        message = null;
        return true;
    }

    private static bool ValidateItf(string text, out BarcodeGenerationFailureReason reason, out string? message)
    {
        if (!text.All(char.IsAsciiDigit))
        {
            return Fail(BarcodeGenerationFailureReason.InvalidCharacters, "ITF only accepts digits.", out reason, out message);
        }

        if (text.Length % 2 != 0)
        {
            return Fail(BarcodeGenerationFailureReason.WrongLength, "ITF needs an even number of digits.", out reason, out message);
        }

        if (text.Length > MaxOneDimensionalLength)
        {
            return Fail(BarcodeGenerationFailureReason.TooLong, $"ITF is limited to {MaxOneDimensionalLength} digits.", out reason, out message);
        }

        reason = default;
        message = null;
        return true;
    }

    private static bool ValidateAscii(
        string text,
        char forbidden,
        int maxLength,
        out BarcodeGenerationFailureReason reason,
        out string? message)
    {
        foreach (char c in text)
        {
            if (c > 127 || (forbidden != '\0' && c == forbidden))
            {
                return Fail(BarcodeGenerationFailureReason.InvalidCharacters, $"The character '{c}' cannot be encoded.", out reason, out message);
            }
        }

        if (text.Length > maxLength)
        {
            return Fail(BarcodeGenerationFailureReason.TooLong, $"The input is limited to {maxLength} characters.", out reason, out message);
        }

        reason = default;
        message = null;
        return true;
    }

    private static bool ValidateCodabar(string text, out BarcodeGenerationFailureReason reason, out string? message)
    {
        // Either bare data, or data wrapped in a start and a stop character taken from A, B, C, D.
        string data = text;
        if (text.Length >= 2 && CodabarGuardCharacters.Contains(text[0]) && CodabarGuardCharacters.Contains(text[^1]))
        {
            data = text.Substring(1, text.Length - 2);
            if (data.Length == 0)
            {
                return Fail(BarcodeGenerationFailureReason.WrongLength, "Codabar needs data between the start and stop characters.", out reason, out message);
            }
        }

        foreach (char c in data)
        {
            if (!CodabarDataCharacters.Contains(c))
            {
                return Fail(
                    BarcodeGenerationFailureReason.InvalidCharacters,
                    $"The character '{c}' cannot be encoded. Codabar accepts digits and - $ : / . + between optional A-D start and stop characters.",
                    out reason,
                    out message);
            }
        }

        reason = default;
        message = null;
        return true;
    }

    private static bool Fail(
        BarcodeGenerationFailureReason failureReason,
        string failureMessage,
        out BarcodeGenerationFailureReason reason,
        out string? message)
    {
        reason = failureReason;
        message = failureMessage;
        return false;
    }
}
