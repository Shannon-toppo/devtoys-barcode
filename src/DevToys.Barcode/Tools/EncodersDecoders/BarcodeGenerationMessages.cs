using DevToys.Barcode.Helpers;
using ZXing;

namespace DevToys.Barcode.Tools.EncodersDecoders;

/// <summary>
/// The localized text that explains why a barcode could not be generated. Shared by the GUI tool and the command line
/// tool, so that both tell the same thing: the problem, then the rule of the selected type.
/// </summary>
internal static class BarcodeGenerationMessages
{
    /// <summary>The explanation of a failure of <see cref="BarcodeHelper"/> to generate a barcode of <paramref name="format"/>.</summary>
    internal static string GetDescription(BarcodeGenerationFailureReason reason, string? underlyingMessage, BarcodeFormat format)
    {
        string name = BarcodeFormatNames.GetDisplayName(format);
        string rule = GetRule(format);

        return reason switch
        {
            BarcodeGenerationFailureReason.InvalidCharacters => $"{string.Format(BarcodeEncoderDecoder.InvalidCharactersMessage, name)} {rule}",
            BarcodeGenerationFailureReason.WrongLength => $"{string.Format(BarcodeEncoderDecoder.WrongLengthMessage, name)} {rule}",
            BarcodeGenerationFailureReason.BadCheckDigit => $"{BarcodeEncoderDecoder.BadCheckDigitMessage} {rule}",
            BarcodeGenerationFailureReason.TooLong => $"{string.Format(BarcodeEncoderDecoder.TooLongMessage, name)} {rule}",
            _ => GetEncoderRejected(format, underlyingMessage)
        };
    }

    /// <summary>The explanation of an encoder that rejected the text for a reason that has no message of its own.</summary>
    internal static string GetEncoderRejected(BarcodeFormat format, string? underlyingMessage)
        => string.Format(BarcodeEncoderDecoder.EncoderRejectedMessage, BarcodeFormatNames.GetDisplayName(format), underlyingMessage);

    /// <summary>What <paramref name="format"/> accepts.</summary>
    internal static string GetRule(BarcodeFormat format)
        => format switch
        {
            BarcodeFormat.CODE_128 => BarcodeEncoderDecoder.RuleCode128,
            BarcodeFormat.EAN_13 => BarcodeEncoderDecoder.RuleEan13,
            BarcodeFormat.EAN_8 => BarcodeEncoderDecoder.RuleEan8,
            BarcodeFormat.UPC_A => BarcodeEncoderDecoder.RuleUpcA,
            BarcodeFormat.UPC_E => BarcodeEncoderDecoder.RuleUpcE,
            BarcodeFormat.CODE_39 => BarcodeEncoderDecoder.RuleCode39,
            BarcodeFormat.CODE_93 => BarcodeEncoderDecoder.RuleCode93,
            BarcodeFormat.ITF => BarcodeEncoderDecoder.RuleItf,
            BarcodeFormat.CODABAR => BarcodeEncoderDecoder.RuleCodabar,
            BarcodeFormat.DATA_MATRIX => BarcodeEncoderDecoder.RuleDataMatrix,
            BarcodeFormat.PDF_417 => BarcodeEncoderDecoder.RulePdf417,
            BarcodeFormat.AZTEC => BarcodeEncoderDecoder.RuleAztec,
            _ => string.Empty
        };
}
