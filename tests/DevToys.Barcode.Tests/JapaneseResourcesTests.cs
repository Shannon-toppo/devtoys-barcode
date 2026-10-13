using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using DevToys.Barcode.Tools.EncodersDecoders;

namespace DevToys.Barcode.Tests;

public class JapaneseResourcesTests
{
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    [Fact]
    public void JapaneseUICulture_ReturnsJapaneseStrings_AndCoversEveryNeutralKey()
    {
        // Set the culture explicitly and restore it afterwards, so the result does not depend on the
        // default culture configured by other test classes or on the order in which tests run.
        CultureInfo japanese = new("ja-JP");
        CultureInfo previousUICulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = japanese;

            string japaneseTitle = BarcodeEncoderDecoder.LongDisplayTitle;
            string japaneseNotFound = BarcodeEncoderDecoder.NoBarcodeFound;

            Assert.Matches(@"[぀-ヿ一-鿿]", japaneseTitle);
            Assert.Matches(@"[぀-ヿ一-鿿]", japaneseNotFound);
            Assert.NotEqual(NeutralString(nameof(BarcodeEncoderDecoder.LongDisplayTitle)), japaneseTitle);
            Assert.NotEqual(NeutralString(nameof(BarcodeEncoderDecoder.NoBarcodeFound)), japaneseNotFound);

            ResourceSet? neutralSet = BarcodeEncoderDecoder.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
            ResourceSet? japaneseSet = BarcodeEncoderDecoder.ResourceManager.GetResourceSet(japanese, createIfNotExists: true, tryParents: false);
            Assert.NotNull(neutralSet);
            Assert.NotNull(japaneseSet);

            var problems = new List<string>();
            int checkedKeys = 0;

            foreach (DictionaryEntry entry in neutralSet!)
            {
                string key = (string)entry.Key;
                string neutralText = (string)entry.Value!;
                checkedKeys++;

                string? japaneseText = japaneseSet!.GetString(key);
                if (japaneseText is null)
                {
                    problems.Add($"{key}: missing from the ja-JP resources");
                    continue;
                }

                if (!PlaceholdersOf(neutralText).SequenceEqual(PlaceholdersOf(japaneseText)))
                {
                    problems.Add($"{key}: placeholders differ (neutral {string.Join(",", PlaceholdersOf(neutralText))}, ja-JP {string.Join(",", PlaceholdersOf(japaneseText))})");
                }
            }

            Assert.True(checkedKeys > 0, "The neutral resources were empty.");
            Assert.Empty(problems);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    private static string NeutralString(string name)
        => BarcodeEncoderDecoder.ResourceManager.GetString(name, CultureInfo.InvariantCulture)
           ?? throw new InvalidOperationException($"The neutral resource '{name}' does not exist.");

    private static int[] PlaceholdersOf(string text)
        => PlaceholderPattern.Matches(text)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();
}
