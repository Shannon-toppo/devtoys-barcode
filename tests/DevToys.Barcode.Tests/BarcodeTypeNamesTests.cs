using DevToys.Barcode.Helpers;
using ZXing;

namespace DevToys.Barcode.Tests;

public class BarcodeTypeNamesTests
{
    [Fact]
    public void Names_AreDerivedFromTheGenerationFormats_InTheirOrder()
    {
        Assert.Equal(BarcodeHelper.GenerationFormats.Count, BarcodeTypeNames.Names.Count);
        for (int i = 0; i < BarcodeHelper.GenerationFormats.Count; i++)
        {
            Assert.Equal(BarcodeTypeNames.GetName(BarcodeHelper.GenerationFormats[i]), BarcodeTypeNames.Names[i]);
        }
    }

    [Fact]
    public void Names_AreTheOnesDocumented_LowerCaseLettersAndDigits_AndUnique()
    {
        Assert.Equal(
            ["code128", "ean13", "ean8", "upca", "upce", "code39", "code93", "itf", "codabar", "datamatrix", "pdf417", "aztec"],
            BarcodeTypeNames.Names);
        Assert.All(BarcodeTypeNames.Names, name => Assert.Matches("^[a-z0-9]+$", name));
        Assert.Equal(BarcodeTypeNames.Names.Count, BarcodeTypeNames.Names.Distinct().Count());
    }

    [Fact]
    public void EveryName_IsParsedBackToItsFormat()
    {
        foreach (BarcodeFormat format in BarcodeHelper.GenerationFormats)
        {
            Assert.True(BarcodeTypeNames.TryParse(BarcodeTypeNames.GetName(format), out BarcodeFormat parsed));
            Assert.Equal(format, parsed);
        }
    }

    [Theory]
    [InlineData("CODE128", BarcodeFormat.CODE_128)]
    [InlineData("Code 128", BarcodeFormat.CODE_128)]
    [InlineData("EAN-13", BarcodeFormat.EAN_13)]
    [InlineData("ean_8", BarcodeFormat.EAN_8)]
    [InlineData("UPC-A", BarcodeFormat.UPC_A)]
    [InlineData("Data Matrix", BarcodeFormat.DATA_MATRIX)]
    [InlineData("PDF417", BarcodeFormat.PDF_417)]
    [InlineData(" aztec ", BarcodeFormat.AZTEC)]
    public void TryParse_IgnoresCaseAndSeparators(string value, BarcodeFormat expected)
    {
        Assert.True(BarcodeTypeNames.TryParse(value, out BarcodeFormat format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-")]
    [InlineData("qr")]
    [InlineData("qrcode")] // read, but not generated here
    [InlineData("code")]
    [InlineData("ean")]
    [InlineData("ean130")]
    public void TryParse_RejectsWhatIsNotAType(string? value)
    {
        Assert.False(BarcodeTypeNames.TryParse(value, out _));
    }
}
