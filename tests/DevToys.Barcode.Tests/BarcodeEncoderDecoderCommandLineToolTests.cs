using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Xml;
using DevToys.Api;
using DevToys.Barcode.Helpers;
using DevToys.Barcode.Tests.Mocks;
using DevToys.Barcode.Tools.EncodersDecoders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;
using ZXing.Common;

namespace DevToys.Barcode.Tests;

/// <summary>
/// Drives <see cref="BarcodeEncoderDecoderCommandLineTool"/> the way the DevToys CLI does: the properties are set, then
/// <c>InvokeAsync</c> is called and the exit code, the standard output and the standard error are looked at. Only the file
/// storage of the host is replaced. The CLI itself (parsing of the command line, loading of the extension) is not run here.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public sealed class BarcodeEncoderDecoderCommandLineToolTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private readonly MockFileStorage _fileStorage = new();
    private readonly BarcodeEncoderDecoderCommandLineTool _tool;

    static BarcodeEncoderDecoderCommandLineToolTests()
    {
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
    }

    public BarcodeEncoderDecoderCommandLineToolTests()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
        _tool = new BarcodeEncoderDecoderCommandLineTool { _fileStorage = _fileStorage };
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _fileStorage.Dispose();
    }

    private string Output => _out.ToString();

    private string Error => _error.ToString();

    private string PathOf(string fileName) => Path.Combine(_fileStorage.AppCacheDirectory, fileName);

    private Task<int> RunAsync(CancellationToken cancellationToken = default)
        => _tool.InvokeAsync(NullLogger.Instance, cancellationToken).AsTask();

    private async Task<int> EncodeAsync(string text, string? type, string? outputFile)
    {
        _tool.Input = text;
        _tool.Type = type ?? _tool.Type;
        _tool.OutputFile = outputFile is null ? null : new FileInfo(PathOf(outputFile));
        return await RunAsync();
    }

    private string SaveImage(Image<Rgba32> image, string fileName)
    {
        using (image)
        {
            string path = PathOf(fileName);
            image.Save(path);
            return path;
        }
    }

    private async Task<IReadOnlyList<BarcodeReadResult>> ReadBackAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await BarcodeHelper.ReadAsync(stream, CancellationToken.None);
    }

    private static Image<Rgba32> ThreeBarcodes()
    {
        Image<Rgba32> dataMatrix = TestImages.Generate(BarcodeFormat.DATA_MATRIX, "DevToys Barcode");
        dataMatrix.Mutate(context => context.Resize(200, 200, KnownResamplers.NearestNeighbor));

        return TestImages.Compose(
            1200,
            1000,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 140, 70),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 140, 370),
            (dataMatrix, 750, 650));
    }

    // ---------------------------------------------------------------- what the host sees

    [Fact]
    public void TheTool_IsExportedForMef_AsTheBarcodeCommand_AndGetsTheFileStorage()
    {
        using var catalog = new AssemblyCatalog(typeof(BarcodeEncoderDecoderCommandLineTool).Assembly);
        using var container = new CompositionContainer(catalog);
        container.ComposeExportedValue<IFileStorage>(_fileStorage);

        Lazy<ICommandLineTool, IDictionary<string, object>> export
            = Assert.Single(container.GetExports<ICommandLineTool, IDictionary<string, object>>());

        // The metadata the CLI reads (CommandLineToolMetadata).
        Assert.Equal("BarcodeEncoderDecoder", export.Metadata["InternalComponentName"]);
        Assert.Equal("barcode", export.Metadata["Name"]);
        Assert.Equal(nameof(BarcodeEncoderDecoder.CommandLineDescription), export.Metadata["DescriptionResourceName"]);
        Assert.Equal("DevToys.Barcode.Tools.EncodersDecoders.BarcodeEncoderDecoder", export.Metadata["ResourceManagerBaseName"]);

        var tool = Assert.IsType<BarcodeEncoderDecoderCommandLineTool>(export.Value);
        Assert.Same(_fileStorage, tool._fileStorage);
    }

    [Fact]
    public void TheOptions_AreDeclaredAsIntended()
    {
        CommandLineOptionAttribute[] options = GetOptions().Select(option => option.Attribute).ToArray();

        Assert.Equal(["input", "outputFile", "showType", "type"], options.Select(option => option.Name).Order());
        CommandLineOptionAttribute input = options.Single(option => option.Name == "input");
        Assert.Equal("i", input.Alias);
        Assert.True(input.IsRequired);
        CommandLineOptionAttribute outputFile = options.Single(option => option.Name == "outputFile");
        Assert.Equal("o", outputFile.Alias);
        Assert.False(outputFile.IsRequired);
        CommandLineOptionAttribute type = options.Single(option => option.Name == "type");
        Assert.Equal("t", type.Alias);
        Assert.False(type.IsRequired);
        CommandLineOptionAttribute showType = options.Single(option => option.Name == "showType");
        Assert.True(string.IsNullOrEmpty(showType.Alias));
        Assert.False(showType.IsRequired);

        // The CLI lower-cases names and aliases: they must still differ.
        string[] spellings = options.SelectMany(option => new[] { option.Name, option.Alias }).Where(name => !string.IsNullOrEmpty(name)).Select(name => name.ToLowerInvariant()).ToArray();
        Assert.Equal(spellings.Length, spellings.Distinct().Count());

        // Types the CLI knows how to parse.
        Assert.Equal(typeof(OneOf.OneOf<FileInfo, string>?), GetOptions().Single(option => option.Attribute.Name == "input").Property.PropertyType);
        Assert.Equal(typeof(FileInfo), GetOptions().Single(option => option.Attribute.Name == "outputFile").Property.PropertyType);
        Assert.Equal(typeof(string), GetOptions().Single(option => option.Attribute.Name == "type").Property.PropertyType);
        Assert.Equal(typeof(bool), GetOptions().Single(option => option.Attribute.Name == "showType").Property.PropertyType);
    }

    [Fact]
    public void TheDefaults_AreCode128_AndNoTypeShown()
    {
        var tool = new BarcodeEncoderDecoderCommandLineTool();

        // The CLI reads the initial value of a property to show it as the default in the help and to use it.
        Assert.Equal("code128", tool.Type);
        Assert.False(tool.ShowType);
        Assert.Null(tool.OutputFile);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheCommandAndEveryOption_HaveADescription_InEnglishAndJapanese(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        var manager = new ResourceManager("DevToys.Barcode.Tools.EncodersDecoders.BarcodeEncoderDecoder", typeof(BarcodeEncoderDecoderCommandLineTool).Assembly);

        var command = typeof(BarcodeEncoderDecoderCommandLineTool).GetCustomAttribute<CommandNameAttribute>()!;
        Assert.False(string.IsNullOrWhiteSpace(manager.GetString(command.DescriptionResourceName, culture)));
        foreach ((_, CommandLineOptionAttribute option) in GetOptions())
        {
            Assert.False(string.IsNullOrWhiteSpace(manager.GetString(option.DescriptionResourceName, culture)), option.Name);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void TheTypeDescription_ListsTheAcceptedNames_AndTheOutputFileDescription_TheExtensions(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        var manager = new ResourceManager("DevToys.Barcode.Tools.EncodersDecoders.BarcodeEncoderDecoder", typeof(BarcodeEncoderDecoderCommandLineTool).Assembly);

        string typeDescription = manager.GetString(nameof(BarcodeEncoderDecoder.TypeOptionDescription), culture)!;
        foreach (string name in BarcodeTypeNames.Names)
        {
            // Whole words: "code128" is not found in "xcode1280".
            Assert.Matches($@"(?<![a-z0-9]){name}(?![a-z0-9])", typeDescription);
        }

        // The names are listed in order, and the first one is the default.
        int[] positions = [.. BarcodeTypeNames.Names.Select(name => typeDescription.IndexOf(name, StringComparison.Ordinal))];
        Assert.Equal(positions.Order(), positions);

        string outputDescription = manager.GetString(nameof(BarcodeEncoderDecoder.OutputFileOptionDescription), culture)!;
        foreach (string extension in BarcodeEncoderDecoderCommandLineTool.OutputExtensions)
        {
            Assert.Contains(extension, outputDescription);
        }
    }

    [Fact]
    public void TheOutputExtensions_AreTheSvg_AndEveryPictureFormatTheHostHas()
    {
        Assert.Equal(".svg", BarcodeEncoderDecoderCommandLineTool.OutputExtensions[0]);
        Assert.Equal(
            PredefinedSupportedImageFormats.ImageFileExtensions.Order(),
            BarcodeEncoderDecoderCommandLineTool.OutputExtensions.Skip(1).Order());
    }

    private static (PropertyInfo Property, CommandLineOptionAttribute Attribute)[] GetOptions()
        => [.. typeof(BarcodeEncoderDecoderCommandLineTool)
            .GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (Property: property, Attribute: property.GetCustomAttribute<CommandLineOptionAttribute>()))
            .Where(pair => pair.Attribute is not null)
            .Select(pair => (pair.Property, pair.Attribute!))];

    // ---------------------------------------------------------------- encoding: PNG and SVG

    [Fact]
    public async Task Encode_Text_ToPng_WritesAPicture_ThatReadsBack_AndNothingToTheConsole()
    {
        int exitCode = await EncodeAsync("Hello-123", null, "hello.png");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Empty, Error);
        Assert.Equal("png", (await Image.IdentifyAsync(PathOf("hello.png")))!.Metadata.DecodedImageFormat!.Name.ToLowerInvariant());
        BarcodeReadResult result = Assert.Single(await ReadBackAsync(PathOf("hello.png")));
        Assert.Equal(BarcodeFormat.CODE_128, result.Format);
        Assert.Equal("Hello-123", result.Text);
    }

    [Fact]
    public async Task Encode_Text_ToSvg_WritesTheSameSvgAsTheGui_ThatDrawsTheBarcode()
    {
        int exitCode = await EncodeAsync("Hello-123", "code128", "hello.svg");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Empty, Error);
        string svg = await File.ReadAllTextAsync(PathOf("hello.svg"));
        Assert.Equal(BarcodeHelper.GenerateSvg("Hello-123", BarcodeFormat.CODE_128).Value, svg);
        Assert.NotEqual(0xEF, (await File.ReadAllBytesAsync(PathOf("hello.svg")))[0]); // no byte order mark

        // Draw the rectangles of the SVG and read the result back.
        using Image<Rgba32> drawn = Rasterize(svg);
        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(drawn));
        Assert.Equal(BarcodeFormat.CODE_128, result.Format);
        Assert.Equal("Hello-123", result.Text);
    }

    [Fact]
    public async Task Encode_ToSvg_WithAnUpperCaseExtension_Works()
    {
        Assert.Equal(0, await EncodeAsync("Hello", null, "HELLO.SVG"));
        Assert.Contains("<svg", await File.ReadAllTextAsync(PathOf("HELLO.SVG")));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".pbm")]
    [InlineData(".tiff")]
    [InlineData(".tga")]
    [InlineData(".webp")]
    public async Task Encode_ToEveryPictureExtensionTheHostSupports_WritesThatFormat_ThatReadsBack(string extension)
    {
        string fileName = "barcode" + extension;
        Assert.Equal(0, await EncodeAsync("Hello-123", null, fileName));

        IImageFormat? format = (await Image.IdentifyAsync(PathOf(fileName)))?.Metadata.DecodedImageFormat;
        Assert.Equal(Configuration.Default.ImageFormatsManager.TryFindFormatByFileExtension(extension, out IImageFormat? expected) ? expected : null, format);
        BarcodeReadResult result = Assert.Single(await ReadBackAsync(PathOf(fileName)));
        Assert.Equal("Hello-123", result.Text);
    }

    [Fact]
    public void TheOutputExtensions_AreAllEncodedByTheHostsImageLibrary()
    {
        Configuration configuration = Configuration.Default;
        foreach (string extension in PredefinedSupportedImageFormats.ImageFileExtensions)
        {
            Assert.True(configuration.ImageFormatsManager.TryFindFormatByFileExtension(extension, out IImageFormat? format), extension);
            Assert.NotNull(configuration.ImageFormatsManager.GetEncoder(format));
        }
    }

    // ---------------------------------------------------------------- encoding: every type

    private static readonly Dictionary<string, (string Text, BarcodeFormat Format, string ReadAs)> Samples
        = new()
        {
            ["code128"] = ("Hello-123", BarcodeFormat.CODE_128, "Hello-123"),
            ["ean13"] = ("400638133393", BarcodeFormat.EAN_13, "4006381333931"),
            ["ean8"] = ("9638507", BarcodeFormat.EAN_8, "96385074"),
            ["upca"] = ("03600029145", BarcodeFormat.UPC_A, "036000291452"),
            ["upce"] = ("0123456", BarcodeFormat.UPC_E, "01234565"),
            ["code39"] = ("HELLO-123", BarcodeFormat.CODE_39, "HELLO-123"),
            ["code93"] = ("HELLO-93", BarcodeFormat.CODE_93, "HELLO-93"),
            ["itf"] = ("12345670", BarcodeFormat.ITF, "12345670"),
            ["codabar"] = ("A123-456B", BarcodeFormat.CODABAR, "123-456"),
            ["datamatrix"] = ("Hello, Data Matrix", BarcodeFormat.DATA_MATRIX, "Hello, Data Matrix"),
            ["pdf417"] = ("Hello, PDF417", BarcodeFormat.PDF_417, "Hello, PDF417"),
            ["aztec"] = ("Hello, Aztec", BarcodeFormat.AZTEC, "Hello, Aztec")
        };

    public static TheoryData<string> TypeNames => [.. BarcodeTypeNames.Names];

    [Fact]
    public void EveryAcceptedTypeName_HasASampleInThisTest_AndTheOtherWayRound()
    {
        // A new generation format must be added to the samples above, so that it is tested like the others.
        Assert.Equal(BarcodeTypeNames.Names.Order(), Samples.Keys.Order());
        Assert.Equal(BarcodeHelper.GenerationFormats, BarcodeTypeNames.Names.Select(name => Samples[name].Format));
    }

    [Theory]
    [MemberData(nameof(TypeNames))]
    public async Task Encode_EveryType_ToPng_AndToSvg_ReadsBackAsThatType(string typeName)
    {
        (string text, BarcodeFormat format, string readAs) = Samples[typeName];

        Assert.Equal(0, await EncodeAsync(text, typeName, "sample.png"));
        BarcodeReadResult fromPng = Assert.Single(await ReadBackAsync(PathOf("sample.png")));
        Assert.Equal(format, fromPng.Format);
        Assert.Equal(readAs, fromPng.Text);

        Assert.Equal(0, await EncodeAsync(text, typeName, "sample.svg"));
        using Image<Rgba32> drawn = Rasterize(await File.ReadAllTextAsync(PathOf("sample.svg")));
        BarcodeReadResult fromSvg = Assert.Single(BarcodeHelper.Read(drawn));
        Assert.Equal(format, fromSvg.Format);
        Assert.Equal(readAs, fromSvg.Text);

        Assert.Equal(string.Empty, Output);
    }

    [Theory]
    [InlineData("EAN13", BarcodeFormat.EAN_13, "4006381333931")]
    [InlineData("Ean-13", BarcodeFormat.EAN_13, "4006381333931")]
    [InlineData("Data Matrix", BarcodeFormat.DATA_MATRIX, "4006381333931")]
    [InlineData("PDF417", BarcodeFormat.PDF_417, "4006381333931")]
    public async Task TheTypeName_IsCaseInsensitive_AndIgnoresSeparators(string typeName, BarcodeFormat expected, string text)
    {
        Assert.Equal(0, await EncodeAsync(text, typeName, "typed.png"));

        Assert.Equal(expected, Assert.Single(await ReadBackAsync(PathOf("typed.png"))).Format);
    }

    [Fact]
    public async Task WithoutAType_Code128IsUsed()
    {
        _tool.Type = null;

        Assert.Equal(0, await EncodeAsync("400638133393", null, "default.png"));

        BarcodeReadResult result = Assert.Single(await ReadBackAsync(PathOf("default.png")));
        Assert.Equal(BarcodeFormat.CODE_128, result.Format);
        Assert.Equal("400638133393", result.Text);
    }

    [Theory]
    [InlineData("qrcode")]
    [InlineData("code")]
    [InlineData("")]
    [InlineData("ean14")]
    public async Task ABadType_ListsTheAcceptedOnes_AndWritesNothing(string typeName)
    {
        int exitCode = await EncodeAsync("123456", typeName, "bad.png");

        Assert.Equal(-1, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Contains(string.Format(BarcodeEncoderDecoder.UnknownTypeMessage, typeName, string.Join(", ", BarcodeTypeNames.Names)), Error);
        foreach (string name in BarcodeTypeNames.Names)
        {
            Assert.Contains(name, Error);
        }

        Assert.False(File.Exists(PathOf("bad.png")));
    }

    // ---------------------------------------------------------------- encoding: the output file

    [Fact]
    public async Task WithoutAnOutputFile_TheHostAsks_AsForTheQrCodeCommand_AndTheAnswerIsUsed()
    {
        string path = PathOf("asked.png");
        _fileStorage.SaveFileAnswer = () => path;

        Assert.Equal(0, await EncodeAsync("Hello", null, null));

        Assert.Equal(BarcodeEncoderDecoderCommandLineTool.OutputExtensions, _fileStorage.RequestedSaveFileTypes);
        Assert.Equal(string.Empty, Output);
        Assert.Equal("Hello", Assert.Single(await ReadBackAsync(path)).Text);
    }

    [Fact]
    public async Task WithoutAnOutputFile_AndNoAnswer_ItIsAnError_ThatSaysWhatToDo()
    {
        _fileStorage.SaveFileAnswer = () => null;

        int exitCode = await EncodeAsync("Hello", null, null);

        Assert.Equal(-1, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Contains(string.Format(BarcodeEncoderDecoder.OutputFileRequiredMessage, string.Join(", ", BarcodeEncoderDecoderCommandLineTool.OutputExtensions)), Error);
        Assert.Contains("--outputfile", Error);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    [Fact]
    public async Task AWrongText_IsReportedBeforeTheHostIsAskedForAFile()
    {
        // No answer is set: asking would fail the test.
        int exitCode = await EncodeAsync("ABC", "ean13", null);

        Assert.Equal(-1, exitCode);
        Assert.Null(_fileStorage.RequestedSaveFileTypes);
        Assert.Contains(BarcodeEncoderDecoder.RuleEan13, Error);
    }

    [Theory]
    [InlineData("barcode.txt")]
    [InlineData("barcode")]
    [InlineData("barcode.heic")]
    [InlineData("barcode.pdf")]
    public async Task AnOutputFile_WithAnExtensionThatIsNotAPicture_IsAnError_ThatListsTheExtensions(string fileName)
    {
        int exitCode = await EncodeAsync("Hello", null, fileName);

        Assert.Equal(-1, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Contains(string.Format(BarcodeEncoderDecoder.UnsupportedOutputFileMessage, PathOf(fileName), string.Join(", ", BarcodeEncoderDecoderCommandLineTool.OutputExtensions)), Error);
        Assert.False(File.Exists(PathOf(fileName)));
    }

    [Theory]
    [InlineData("hello.png")]
    [InlineData("hello.svg")]
    public async Task AnExistingOutputFile_IsReplaced_LikeTheBuiltInCommandsDo(string fileName)
    {
        await File.WriteAllTextAsync(PathOf(fileName), new string('x', 100_000));

        Assert.Equal(0, await EncodeAsync("Hello", null, fileName));

        Assert.True(new FileInfo(PathOf(fileName)).Length < 100_000);
        Assert.Equal(string.Empty, Error);
    }

    [Theory]
    [InlineData("missing-folder/hello.png")]
    [InlineData("missing-folder/hello.svg")]
    public async Task AnOutputFile_InAFolderThatDoesNotExist_IsAnError_NotAnException(string fileName)
    {
        int exitCode = await EncodeAsync("Hello", null, fileName);

        Assert.Equal(-1, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Contains(PathOf(fileName), Error);
    }

    // ---------------------------------------------------------------- encoding: the text

    [Fact]
    public async Task AFileThatIsNotAPicture_IsEncodedByItsText_WithoutTheFinalLineBreak()
    {
        string textFile = PathOf("label.txt");
        await File.WriteAllTextAsync(textFile, "Hello-123" + Environment.NewLine);
        _tool.Input = new FileInfo(textFile);
        _tool.OutputFile = new FileInfo(PathOf("label.png"));

        Assert.Equal(0, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Empty, Error);
        Assert.Equal("Hello-123", Assert.Single(await ReadBackAsync(PathOf("label.png"))).Text);
    }

    [Fact]
    public async Task AFile_WithAnyExtension_IsText_AndAMultiLineTextKeepsItsInnerLineBreaks()
    {
        string textFile = PathOf("lines.dat");
        await File.WriteAllTextAsync(textFile, "first\nsecond\n");
        _tool.Input = new FileInfo(textFile);
        _tool.Type = "datamatrix";
        _tool.OutputFile = new FileInfo(PathOf("lines.png"));

        Assert.Equal(0, await RunAsync());

        Assert.Equal("first\nsecond", Assert.Single(await ReadBackAsync(PathOf("lines.png"))).Text);
    }

    [Fact]
    public async Task ATextThatIsNotAFile_IsEncoded_EvenWhenItLooksLikeAPath()
    {
        Assert.Equal(0, await EncodeAsync("some/folder/notes.txt", null, "path.png"));

        Assert.Equal("some/folder/notes.txt", Assert.Single(await ReadBackAsync(PathOf("path.png"))).Text);
    }

    [Theory]
    [InlineData("https://example.com/logo.png")]
    [InlineData("Photo 1.jpg and Photo 2.jpg\nnext line.png")]
    public async Task ATextThatEndsLikeAPictureName_ButIsAnAddressOrSeveralLines_IsEncoded(string text)
    {
        Assert.Equal(0, await EncodeAsync(text, "datamatrix", "address.png"));

        Assert.Equal(text, Assert.Single(await ReadBackAsync(PathOf("address.png"))).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public async Task AnEmptyText_IsAnError(string text)
    {
        int exitCode = await EncodeAsync(text, null, "empty.png");

        Assert.Equal(-1, exitCode);
        Assert.Equal(string.Empty, Output);
        Assert.Contains(BarcodeEncoderDecoder.EmptyInputMessage, Error);
        Assert.False(File.Exists(PathOf("empty.png")));
    }

    [Fact]
    public async Task AnEmptyTextFile_IsAnError()
    {
        string textFile = PathOf("empty.txt");
        await File.WriteAllTextAsync(textFile, string.Empty);
        _tool.Input = new FileInfo(textFile);
        _tool.OutputFile = new FileInfo(PathOf("empty.png"));

        Assert.Equal(-1, await RunAsync());

        Assert.Contains(BarcodeEncoderDecoder.EmptyInputMessage, Error);
    }

    [Fact]
    public async Task WithNoInput_ItIsAnError()
    {
        _tool.Input = null;

        Assert.Equal(-1, await RunAsync());

        Assert.NotEqual(string.Empty, Error);
        Assert.Equal(string.Empty, Output);
    }

    // ---------------------------------------------------------------- encoding: failures say what the GUI says

    [Theory]
    [InlineData("ean13", "ABC", BarcodeGenerationFailureReason.InvalidCharacters)]
    [InlineData("ean13", "12345", BarcodeGenerationFailureReason.WrongLength)]
    [InlineData("ean13", "4006381333932", BarcodeGenerationFailureReason.BadCheckDigit)]
    [InlineData("upce", "2234565", BarcodeGenerationFailureReason.EncoderRejected)]
    [InlineData("itf", "123", BarcodeGenerationFailureReason.WrongLength)]
    [InlineData("code39", "a*b", BarcodeGenerationFailureReason.InvalidCharacters)]
    [InlineData("code128", "café", BarcodeGenerationFailureReason.InvalidCharacters)]
    public async Task InvalidText_WritesTheProblemAndTheRuleOfTheType_ToTheErrorOutput(string typeName, string text, BarcodeGenerationFailureReason reason)
    {
        BarcodeFormat format = Samples[typeName].Format;
        BarcodeGenerationResult<Image<Rgba32>> expected = BarcodeHelper.GenerateImage(text, format);
        Assert.Equal(reason, expected.Reason); // the case is what it says it is

        int exitCode = await EncodeAsync(text, typeName, "invalid.png");

        Assert.Equal(-1, exitCode);
        Assert.Equal(string.Empty, Output);
        string[] lines = Error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(BarcodeEncoderDecoder.GenerationFailedTitle, lines[0]);
        Assert.Equal(BarcodeGenerationMessages.GetDescription(reason, expected.Message, format), string.Join(Environment.NewLine, lines.Skip(1)));
        if (reason != BarcodeGenerationFailureReason.EncoderRejected)
        {
            Assert.Contains(BarcodeGenerationMessages.GetRule(format), Error);
        }

        Assert.False(File.Exists(PathOf("invalid.png")));
    }

    [Theory]
    [InlineData("ean13", "ABC", nameof(BarcodeEncoderDecoder.InvalidCharactersMessage), nameof(BarcodeEncoderDecoder.RuleEan13))]
    [InlineData("ean8", "12345", nameof(BarcodeEncoderDecoder.WrongLengthMessage), nameof(BarcodeEncoderDecoder.RuleEan8))]
    [InlineData("upca", "036000291453", nameof(BarcodeEncoderDecoder.BadCheckDigitMessage), nameof(BarcodeEncoderDecoder.RuleUpcA))]
    [InlineData("code39", "a*b", nameof(BarcodeEncoderDecoder.InvalidCharactersMessage), nameof(BarcodeEncoderDecoder.RuleCode39))]
    public async Task InvalidText_ContainsTheProblemAndTheRule_AsTheResourceStringsSayThem(string typeName, string text, string problemKey, string ruleKey)
    {
        Assert.Equal(-1, await EncodeAsync(text, typeName, "invalid.svg"));

        string problem = BarcodeEncoderDecoder.ResourceManager.GetString(problemKey)!;
        string rule = BarcodeEncoderDecoder.ResourceManager.GetString(ruleKey)!;
        string name = BarcodeFormatNames.GetDisplayName(Samples[typeName].Format);
        Assert.Contains(string.Format(problem, name), Error);
        Assert.Contains(rule, Error);
    }

    [Fact]
    public async Task ATextThatIsTooLongForTheType_IsReported()
    {
        int exitCode = await EncodeAsync(new string('1', 200), "ean13", "long.png");
        Assert.Equal(-1, exitCode);

        exitCode = await EncodeAsync(new string('A', 5000), "datamatrix", "long.png");
        Assert.Equal(-1, exitCode);
        Assert.Contains(BarcodeEncoderDecoder.RuleDataMatrix, Error);
        Assert.False(File.Exists(PathOf("long.png")));
    }

    // ---------------------------------------------------------------- encoding: a check digit

    [Theory]
    [InlineData("ean13", "400638133393", "4006381333931")]
    [InlineData("ean8", "9638507", "96385074")]
    [InlineData("upca", "03600029145", "036000291452")]
    [InlineData("upce", "0123456", "01234565")]
    public async Task WhenACheckDigitIsAdded_TheEncodedValueIsToldOnTheErrorOutput_AndTheStandardOutputStaysClean(string typeName, string text, string encoded)
    {
        Assert.Equal(0, await EncodeAsync(text, typeName, "digit.png"));

        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Format(BarcodeEncoderDecoder.EncodedValueDescription, encoded) + Environment.NewLine, Error);
        Assert.Equal(encoded, Assert.Single(await ReadBackAsync(PathOf("digit.png"))).Text);
    }

    [Fact]
    public async Task WhenNoCheckDigitIsAdded_NothingIsSaid()
    {
        Assert.Equal(0, await EncodeAsync("4006381333931", "ean13", "whole.svg"));

        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Empty, Error);
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task Read_OneBarcode_WritesItsTextAlone_OnOneLine()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "one.png");
        _tool.Input = new FileInfo(path);

        Assert.Equal(0, await RunAsync());

        Assert.Equal("Hello-123" + Environment.NewLine, Output);
        Assert.Equal(string.Empty, Error);
    }

    [Fact]
    public async Task Read_OneBarcode_WithTheType_WritesTheTypeATabAndTheText()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), "one.png");
        _tool.Input = new FileInfo(path);
        _tool.ShowType = true;

        Assert.Equal(0, await RunAsync());

        Assert.Equal("EAN-13\t4006381333931" + Environment.NewLine, Output);
        Assert.Equal(string.Empty, Error);
    }

    [Fact]
    public async Task Read_SeveralBarcodes_WritesOneLinePerBarcode_InTheOrderOfTheReader()
    {
        string path = SaveImage(ThreeBarcodes(), "three.png");
        _tool.Input = new FileInfo(path);

        Assert.Equal(0, await RunAsync());

        string[] expectedOrder = [.. (await ReadBackAsync(path)).Select(result => result.Text)];
        Assert.Equal(["Hello-123", "4006381333931", "DevToys Barcode"], expectedOrder);
        Assert.Equal(string.Join(Environment.NewLine, expectedOrder) + Environment.NewLine, Output);
        Assert.Equal(string.Empty, Error);
    }

    [Fact]
    public async Task Read_SeveralBarcodes_WithTheTypes_WritesTheTypeATabAndTheTextOnEachLine()
    {
        string path = SaveImage(ThreeBarcodes(), "three.png");
        _tool.Input = new FileInfo(path);
        _tool.ShowType = true;

        Assert.Equal(0, await RunAsync());

        Assert.Equal(
            string.Join(Environment.NewLine, "Code 128\tHello-123", "EAN-13\t4006381333931", "Data Matrix\tDevToys Barcode") + Environment.NewLine,
            Output);
    }

    [Fact]
    public async Task Read_ASingleMultiLineText_IsWrittenAsItIs_SoThatItCanBePiped()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.PDF_417, "first\nsecond"), "lines.png");
        _tool.Input = new FileInfo(path);

        Assert.Equal(0, await RunAsync());

        Assert.Equal("first\nsecond" + Environment.NewLine, Output);
    }

    [Fact]
    public async Task Read_AMultiLineText_WithTheType_IsKeptOnOneLine_WithEscapedLineBreaks()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.PDF_417, "first\r\nsecond\nthird"), "lines.png");
        _tool.Input = new FileInfo(path);
        _tool.ShowType = true;

        Assert.Equal(0, await RunAsync());

        Assert.Equal("PDF417\tfirst\\r\\nsecond\\nthird" + Environment.NewLine, Output);
    }

    [Fact]
    public async Task Read_AMultiLineText_AmongSeveralBarcodes_IsKeptOnOneLine()
    {
        using Image<Rgba32> multiLine = TestImages.Generate(BarcodeFormat.DATA_MATRIX, "first\nsecond");
        multiLine.Mutate(context => context.Resize(200, 200, KnownResamplers.NearestNeighbor));
        string path = SaveImage(
            TestImages.Compose(
                1000,
                800,
                (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 140, 70),
                (multiLine.Clone(), 700, 450)),
            "mixed.png");
        _tool.Input = new FileInfo(path);

        Assert.Equal(0, await RunAsync());

        Assert.Equal(string.Join(Environment.NewLine, "Hello-123", "first\\nsecond") + Environment.NewLine, Output);
    }

    [Fact]
    public async Task Read_WithATypeOption_IgnoresIt()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "one.png");
        _tool.Input = new FileInfo(path);
        _tool.Type = "not a type";

        Assert.Equal(0, await RunAsync());

        Assert.Equal("Hello-123" + Environment.NewLine, Output);
    }

    [Fact]
    public async Task Read_ABarcodeThatIsNotGenerated_QrCode_Works()
    {
        var writer = new ZXing.ImageSharp.BarcodeWriter<Rgba32> { Format = BarcodeFormat.QR_CODE, Options = new EncodingOptions { Width = 300, Height = 300 } };
        string path = SaveImage(writer.Write("https://example.com"), "qr.png");
        _tool.Input = new FileInfo(path);
        _tool.ShowType = true;

        Assert.Equal(0, await RunAsync());

        Assert.Equal("QR Code\thttps://example.com" + Environment.NewLine, Output);
    }

    [Fact]
    public async Task Read_AnImageWithoutABarcode_IsAnError_ThatSaysSo()
    {
        using var image = new Image<Rgba32>(400, 300, Color.White);
        FillBlack(image, 50, 50, 120, 60);
        string path = SaveImage(image.Clone(), "nothing.png");
        _tool.Input = new FileInfo(path);

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Contains(BarcodeEncoderDecoder.NoBarcodeFound, Error);
        Assert.Contains(BarcodeEncoderDecoder.NoBarcodeFoundDescription, Error);
    }

    [Fact]
    public async Task Read_AFileThatIsNotAnImage_ButHasAnImageExtension_IsAnError()
    {
        string path = PathOf("fake.png");
        await File.WriteAllTextAsync(path, "this is not a picture");
        _tool.Input = new FileInfo(path);

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Contains(BarcodeEncoderDecoder.ImageNotReadableTitle, Error);
        Assert.Contains(BarcodeEncoderDecoder.ImageNotReadableDescription, Error);
    }

    [Fact]
    public async Task Read_AFileThatDoesNotExist_IsAnError_ThatSaysWhich()
    {
        string path = PathOf("missing.png");
        _tool.Input = new FileInfo(path);

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Format(BarcodeEncoderDecoder.FileNotFoundMessage, path) + Environment.NewLine, Error);
    }

    [Theory]
    [InlineData("missing.png")]
    [InlineData("MISSING.JPG")]
    [InlineData("photos/missing.webp")]
    [InlineData("missing.heic")]
    public async Task APathToAPicture_ThatDoesNotExist_IsNotEncodedAsText_TheHostGivesATextForIt(string relativePath)
    {
        // The CLI passes a text, not a file, when the path leads to no file: a typo must not become a barcode.
        string path = PathOf(relativePath);
        _tool.Input = path;
        _tool.OutputFile = new FileInfo(PathOf("typo.png"));

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Format(BarcodeEncoderDecoder.FileNotFoundMessage, path) + Environment.NewLine, Error);
        Assert.False(File.Exists(PathOf("typo.png")));
    }

    // ---------------------------------------------------------------- reading: to a file

    [Fact]
    public async Task Read_WithAnOutputFile_WritesTheTextThere_AndNothingToTheConsole()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "one.png");
        _tool.Input = new FileInfo(path);
        _tool.OutputFile = new FileInfo(PathOf("result.txt"));

        Assert.Equal(0, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Equal(string.Empty, Error);
        Assert.Equal("Hello-123", await File.ReadAllTextAsync(PathOf("result.txt")));
    }

    [Fact]
    public async Task Read_WithAnOutputFile_WritesTheLinesOfSeveralBarcodes_AndReplacesTheFile()
    {
        string path = SaveImage(ThreeBarcodes(), "three.png");
        await File.WriteAllTextAsync(PathOf("result.txt"), new string('x', 10_000));
        _tool.Input = new FileInfo(path);
        _tool.OutputFile = new FileInfo(PathOf("result.txt"));
        _tool.ShowType = true;

        Assert.Equal(0, await RunAsync());

        Assert.Equal(
            string.Join(Environment.NewLine, "Code 128\tHello-123", "EAN-13\t4006381333931", "Data Matrix\tDevToys Barcode"),
            await File.ReadAllTextAsync(PathOf("result.txt")));
    }

    [Fact]
    public async Task Read_WithAnOutputFileInAFolderThatDoesNotExist_IsAnError()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "one.png");
        _tool.Input = new FileInfo(path);
        _tool.OutputFile = new FileInfo(PathOf("missing-folder/result.txt"));

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Contains(PathOf("missing-folder/result.txt"), Error);
    }

    [Fact]
    public async Task Read_NothingFound_DoesNotCreateTheOutputFile()
    {
        string path = SaveImage(new Image<Rgba32>(200, 200, Color.White), "blank.png");
        _tool.Input = new FileInfo(path);
        _tool.OutputFile = new FileInfo(PathOf("result.txt"));

        Assert.Equal(-1, await RunAsync());

        Assert.False(File.Exists(PathOf("result.txt")));
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task ACancelledEncoding_ExitsWithAnError_WritesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _tool.Input = "Hello";
        _tool.OutputFile = new FileInfo(PathOf("cancelled.png"));

        Assert.Equal(-1, await RunAsync(cancellation.Token));

        Assert.Equal(string.Empty, Output);
        Assert.False(File.Exists(PathOf("cancelled.png")));
    }

    [Fact]
    public async Task ACancelledReading_ExitsWithAnError_WritesNothing()
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "one.png");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _tool.Input = new FileInfo(path);
        _tool.OutputFile = new FileInfo(PathOf("result.txt"));

        Assert.Equal(-1, await RunAsync(cancellation.Token));

        Assert.Equal(string.Empty, Output);
        Assert.False(File.Exists(PathOf("result.txt")));
    }

    // ---------------------------------------------------------------- HEIC

    [MacOsFact]
    public async Task Read_AHeicPicture_OnMacOS_ReadsTheBarcodes_AndLeavesNoTemporaryFile()
    {
        string png = SaveImage(ThreeBarcodes(), "source.png");
        string heic = PathOf("IMG_0001.HEIC");
        HeicTestSupport.ConvertPngToHeic(png, heic);
        string[] filesBefore = Directory.GetFiles(_fileStorage.AppCacheDirectory);
        _tool.Input = new FileInfo(heic);
        _tool.ShowType = true;

        Assert.Equal(0, await RunAsync());

        Assert.Equal(string.Empty, Error);
        Assert.Equal(
            ["Code 128\tHello-123", "Data Matrix\tDevToys Barcode", "EAN-13\t4006381333931"],
            Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Order());
        Assert.Equal(filesBefore.Order(), Directory.GetFiles(_fileStorage.AppCacheDirectory).Order());
    }

    [MacOsFact]
    public async Task Read_AHeifPicture_OnMacOS_Works()
    {
        string png = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "source.png");
        string heif = PathOf("photo.heif");
        HeicTestSupport.ConvertPngToHeic(png, heif);
        _tool.Input = new FileInfo(heif);

        Assert.Equal(0, await RunAsync());

        Assert.Equal("Hello-123" + Environment.NewLine, Output);
    }

    [MacOsFact]
    public async Task Read_AHeicFileThatIsNotHeic_OnMacOS_IsAnError_AndLeavesNoTemporaryFile()
    {
        string fake = PathOf("fake.heic");
        await File.WriteAllTextAsync(fake, "this is not a picture");
        string[] filesBefore = Directory.GetFiles(_fileStorage.AppCacheDirectory);
        _tool.Input = new FileInfo(fake);

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Contains(BarcodeEncoderDecoder.ImageNotReadableTitle, Error);
        Assert.Equal(filesBefore.Order(), Directory.GetFiles(_fileStorage.AppCacheDirectory).Order());
    }

    [Fact]
    public async Task Read_AHeicPicture_WhereThePlatformCannotReadIt_IsAnError_NotEncodedAsText()
    {
        string heic = PathOf("photo.heic");
        await File.WriteAllBytesAsync(heic, [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63]);
        _tool.HeicConverter = new HeicConverter(_fileStorage) { IsSupported = false };
        _tool.Input = new FileInfo(heic);
        _tool.OutputFile = new FileInfo(PathOf("never.png"));

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Contains(BarcodeEncoderDecoder.ImageNotReadableTitle, Error);
        Assert.False(File.Exists(PathOf("never.png")));
    }

    [UnixFact]
    public async Task Read_AHeicPicture_WithAConverterThatFails_IsAnError_AndLeavesNoTemporaryFile()
    {
        using var fakeSips = new FakeSips("echo broken >&2\nexit 3");
        string heic = PathOf("photo.heic");
        await File.WriteAllTextAsync(heic, "not really heic");
        string[] filesBefore = Directory.GetFiles(_fileStorage.AppCacheDirectory);
        _tool.HeicConverter = new HeicConverter(_fileStorage) { IsSupported = true, ExecutablePath = fakeSips.ScriptPath };
        _tool.Input = new FileInfo(heic);

        Assert.Equal(-1, await RunAsync());

        Assert.Equal(string.Empty, Output);
        Assert.Contains(BarcodeEncoderDecoder.ImageNotReadableDescription, Error);
        Assert.Equal(filesBefore.Order(), Directory.GetFiles(_fileStorage.AppCacheDirectory).Order());
    }

    // ---------------------------------------------------------------- a picture from the host's formats

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    [InlineData(".tiff")]
    [InlineData(".PNG")]
    public async Task Read_APictureInAnyOfTheHostsFormats_IsRead(string extension)
    {
        string path = SaveImage(TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), "picture" + extension);
        _tool.Input = new FileInfo(path);

        Assert.Equal(0, await RunAsync());

        Assert.Equal("Hello-123" + Environment.NewLine, Output);
    }

    // ---------------------------------------------------------------- helpers

    private static void FillBlack(Image<Rgba32> image, int left, int top, int width, int height)
    {
        for (int y = Math.Max(0, top); y < Math.Min(image.Height, top + height); y++)
        {
            for (int x = Math.Max(0, left); x < Math.Min(image.Width, left + width); x++)
            {
                image[x, y] = new Rgba32(0, 0, 0, 255);
            }
        }
    }

    /// <summary>Draws the black rectangles of an SVG made by <see cref="BarcodeHelper.GenerateSvg"/> on a white picture of its size.</summary>
    private static Image<Rgba32> Rasterize(string svg)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        document.Load(reader);

        string[] viewBox = document.DocumentElement!.GetAttribute("viewBox").Split(' ');
        var image = new Image<Rgba32>(int.Parse(viewBox[2], CultureInfo.InvariantCulture), int.Parse(viewBox[3], CultureInfo.InvariantCulture), Color.White);

        foreach (XmlElement rect in document.GetElementsByTagName("rect"))
        {
            // The rectangles inherit the black fill of the root element, unless they say otherwise.
            string fill = rect.GetAttribute("fill").ToLowerInvariant();
            if (fill.Contains("255,255,255", StringComparison.Ordinal) || fill.Contains("fff", StringComparison.Ordinal) || fill.Contains("white", StringComparison.Ordinal))
            {
                continue;
            }

            int x = (int)Math.Round(double.Parse(rect.GetAttribute("x"), CultureInfo.InvariantCulture));
            int y = (int)Math.Round(double.Parse(rect.GetAttribute("y"), CultureInfo.InvariantCulture));
            int width = (int)Math.Round(double.Parse(rect.GetAttribute("width"), CultureInfo.InvariantCulture));
            int height = (int)Math.Round(double.Parse(rect.GetAttribute("height"), CultureInfo.InvariantCulture));
            FillBlack(image, x, y, width, height);
        }

        return image;
    }
}
