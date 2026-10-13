using ZXing;

namespace DevToys.Barcode.Helpers;

/// <summary>
/// Human-readable names of the barcode formats. These are the standard symbology names, which are not translated.
/// </summary>
internal static class BarcodeFormatNames
{
    internal static string GetDisplayName(BarcodeFormat format)
        => format switch
        {
            BarcodeFormat.CODE_128 => "Code 128",
            BarcodeFormat.CODE_39 => "Code 39",
            BarcodeFormat.CODE_93 => "Code 93",
            BarcodeFormat.EAN_13 => "EAN-13",
            BarcodeFormat.EAN_8 => "EAN-8",
            BarcodeFormat.UPC_A => "UPC-A",
            BarcodeFormat.UPC_E => "UPC-E",
            BarcodeFormat.UPC_EAN_EXTENSION => "UPC/EAN extension",
            BarcodeFormat.ITF => "ITF",
            BarcodeFormat.CODABAR => "Codabar",
            BarcodeFormat.DATA_MATRIX => "Data Matrix",
            BarcodeFormat.PDF_417 => "PDF417",
            BarcodeFormat.AZTEC => "Aztec",
            BarcodeFormat.QR_CODE => "QR Code",
            BarcodeFormat.MAXICODE => "MaxiCode",
            BarcodeFormat.RSS_14 => "GS1 DataBar",
            BarcodeFormat.RSS_EXPANDED => "GS1 DataBar Expanded",
            BarcodeFormat.MSI => "MSI",
            BarcodeFormat.PLESSEY => "Plessey",
            _ => format.ToString()
        };
}
