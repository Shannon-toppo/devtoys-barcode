using System.Globalization;
using DevToys.Api;
using DevToys.Barcode.Helpers;
using DevToys.Barcode.Tests.Mocks;
using DevToys.Barcode.Tools.EncodersDecoders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace DevToys.Barcode.Tests;

/// <summary>
/// Picking HEIC and HEIF pictures in <see cref="BarcodeEncoderDecoderGuiTool"/>. The tests marked for macOS run the real
/// <c>sips</c>; the others replace it by a script, or only look at the accepted extensions.
/// </summary>
public sealed class BarcodeEncoderDecoderGuiToolHeicTests : IDisposable
{
    private readonly MockFileStorage _fileStorage = new();
    private readonly MockSettingsProvider _settings = new();
    private readonly List<IDisposable> _disposables = [];

    static BarcodeEncoderDecoderGuiToolHeicTests()
    {
        LoggingExtensions.LoggerFactory = NullLoggerFactory.Instance;
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _fileStorage.Dispose();
    }

    // ---------------------------------------------------------------- accepted extensions

    [Fact]
    public void TheFileSelector_AcceptsHeicAndHeif_OnlyWhereTheyCanBeRead()
    {
        Harness supported = CreateHarness(new HeicConverter(_fileStorage) { IsSupported = true });
        Harness unsupported = CreateHarness(new HeicConverter(_fileStorage) { IsSupported = false });

        Assert.Equal([.. PredefinedSupportedImageFormats.ImageFileExtensions, ".heic", ".heif"], supported.FileSelector.AllowedFileExtensions);
        Assert.Equal(PredefinedSupportedImageFormats.ImageFileExtensions, unsupported.FileSelector.AllowedFileExtensions);
    }

    [Fact]
    public void WithTheDefaultConverter_HeicIsAcceptedOnMacOsOnly()
    {
        Harness harness = CreateHarness(null);

        Assert.Equal(OperatingSystem.IsMacOS(), harness.FileSelector.AllowedFileExtensions.Contains(".heic"));
        Assert.Equal(OperatingSystem.IsMacOS(), harness.FileSelector.AllowedFileExtensions.Contains(".heif"));
    }

    // ---------------------------------------------------------------- the real sips

    [MacOsFact]
    public async Task PickedHeic_ListsTheThreeBarcodes_ShowsTheConvertedPicture_AndSelectsCode128()
    {
        Harness harness = CreateHarness(null);
        FileInfo heic = CreateHeicFile(ThreeBarcodes(), "IMG_0001.heic");

        harness.Pick(heic);
        await harness.Tool.WorkTask!;

        Assert.False(harness.MessageBar.IsVisible);
        Assert.True(harness.ResultsGrid.IsVisible);
        Assert.Equal(
            new[] { "Code 128|Hello-123", "Data Matrix|DevToys Barcode", "EAN-13|4006381333931" },
            harness.ResultsGrid.Rows.Select(row => string.Join("|", row.Select(CellText))).Order());
        Assert.Equal(BarcodeFormat.CODE_128, harness.FormatDropDown.SelectedItem!.Value);
        Assert.Equal("Hello-123", harness.InputBox.Text);

        // The picture shown is the converted one, not the file that was picked.
        var shown = Assert.IsAssignableFrom<SandboxedFileReader>(harness.ImageViewer.ImageSource!.Value.Value);
        Assert.EndsWith(".png", shown.FileName);
        Assert.False(harness.ProgressStack.IsVisible);
    }

    [MacOsFact]
    public async Task AFileNameWithASpaceAndQuotes_Works()
    {
        Harness harness = CreateHarness(null);
        FileInfo heic = CreateHeicFile(ThreeBarcodes(), "my \"best\" photo's -dash.HEIC");

        harness.Pick(heic);
        await harness.Tool.WorkTask!;

        Assert.False(harness.MessageBar.IsVisible);
        Assert.Equal(3, harness.ResultsGrid.Rows.Count);
    }

    [MacOsFact]
    public async Task AHeifFile_Works()
    {
        Harness harness = CreateHarness(null);
        FileInfo heif = CreateHeicFile(ThreeBarcodes(), "photo.heif");

        harness.Pick(heif);
        await harness.Tool.WorkTask!;

        Assert.Equal(3, harness.ResultsGrid.Rows.Count);
    }

    [MacOsFact]
    public async Task AHeicFileThatIsNotHeic_ShowsTheError_LeavesNoFileAndNoProcess()
    {
        Harness harness = CreateHarness(null);
        FileInfo fake = _fileStorage.CreateFile("heic");
        await File.WriteAllTextAsync(fake.FullName, "this is not a picture");
        harness.InputBox.Text("previous");
        await harness.Tool.WorkTask!;
        string[] filesBefore = Directory.GetFiles(_fileStorage.AppCacheDirectory);

        harness.Pick(fake);
        await harness.Tool.WorkTask!;

        Assert.Equal(string.Empty, harness.InputBox.Text);
        Assert.True(harness.MessageBar.IsVisible);
        Assert.Equal(UIInfoBarSeverity.Error, harness.MessageBar.Severity);
        Assert.Equal(BarcodeEncoderDecoder.ImageNotReadableTitle, harness.MessageBar.Title);
        Assert.Equal(BarcodeEncoderDecoder.ImageNotReadableDescription, harness.MessageBar.Description);
        Assert.Null(harness.ImageViewer.ImageSource); // not the previous picture
        Assert.False(harness.ResultsGrid.IsVisible);
        Assert.False(harness.ProgressStack.IsVisible);
        Assert.Equal(filesBefore.Order(), Directory.GetFiles(_fileStorage.AppCacheDirectory).Order());
        Assert.False(HeicTestSupport.AnyProcessMentions(_fileStorage.AppCacheDirectory));
    }

    [MacOsFact]
    public async Task AfterAFailedHeic_APngCanBePicked()
    {
        Harness harness = CreateHarness(null);
        FileInfo fake = _fileStorage.CreateFile("heic");
        await File.WriteAllTextAsync(fake.FullName, "this is not a picture");
        harness.Pick(fake);
        await harness.Tool.WorkTask!;
        Assert.True(harness.MessageBar.IsVisible);

        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.EAN_13, "400638133393");
        FileInfo png = _fileStorage.CreateFile("png");
        image.SaveAsPng(png.FullName);
        harness.Pick(png);
        await harness.Tool.WorkTask!;

        Assert.False(harness.MessageBar.IsVisible);
        Assert.Equal("4006381333931", harness.InputBox.Text);
    }

    // ---------------------------------------------------------------- a script instead of sips

    [UnixFact]
    public async Task ASipsThatIsMissing_ShowsTheError_WithoutThrowing()
    {
        HeicConverter converter = CreateFakeConverter(new FakeSips("exit 0"));
        converter.ExecutablePath = "/nonexistent/folder/sips";
        Harness harness = CreateHarness(converter);

        harness.Pick(CreateFile("heic", [1, 2, 3]));
        await harness.Tool.WorkTask!;

        Assert.True(harness.MessageBar.IsVisible);
        Assert.Equal(BarcodeEncoderDecoder.ImageNotReadableTitle, harness.MessageBar.Title);
        Assert.Null(harness.ImageViewer.ImageSource);
    }

    [UnixFact]
    public async Task ASipsThatTimesOut_ShowsTheError_AndIsKilled()
    {
        FakeSips fake = FakeSips.Sleeping();
        HeicConverter converter = CreateFakeConverter(fake);
        converter.Timeout = TimeSpan.FromMilliseconds(600);
        Harness harness = CreateHarness(converter);

        harness.Pick(CreateFile("heic", [1, 2, 3]));
        await harness.Tool.WorkTask!;

        Assert.True(harness.MessageBar.IsVisible);
        Assert.Equal(BarcodeEncoderDecoder.ImageNotReadableTitle, harness.MessageBar.Title);
        Assert.False(HeicTestSupport.IsAlive(await fake.WaitForProcessIdAsync()));
        Assert.False(harness.ProgressStack.IsVisible);
    }

    [UnixFact]
    public async Task AnActionStartedDuringTheConversion_CancelsIt_AndKillsSips()
    {
        FakeSips fake = FakeSips.Sleeping();
        Harness harness = CreateHarness(CreateFakeConverter(fake));
        harness.Pick(CreateFile("heic", [1, 2, 3]));
        Task conversion = harness.Tool.WorkTask!;
        int processId = await fake.WaitForProcessIdAsync();
        Assert.True(HeicTestSupport.IsAlive(processId));

        harness.InputBox.Text("typed during the conversion");
        await harness.Tool.WorkTask!;
        await conversion; // never throws: the cancellation is handled

        Assert.False(HeicTestSupport.IsAlive(processId));
        Assert.Equal("typed during the conversion", harness.InputBox.Text);
        Assert.False(harness.MessageBar.IsVisible);
        Assert.False(harness.ProgressStack.IsVisible);
        Assert.IsAssignableFrom<Image>(harness.ImageViewer.ImageSource!.Value.Value);
    }

    [UnixFact]
    public async Task TheConversion_CountsAsPartOfTheReading_ForTheProgressAndTheCancelButton()
    {
        FakeSips fake = FakeSips.Sleeping();
        Harness harness = CreateHarness(CreateFakeConverter(fake));
        harness.Tool.ProgressDelay = TimeSpan.FromMilliseconds(50);
        harness.Pick(CreateFile("heic", [1, 2, 3]));
        Task conversion = harness.Tool.WorkTask!;
        int processId = await fake.WaitForProcessIdAsync();

        for (int i = 0; i < 100 && !harness.ProgressStack.IsVisible; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(harness.ProgressStack.IsVisible, "the progress is shown while sips works");

        await harness.CancelButton.OnClickAction!();
        Assert.False(harness.ProgressStack.IsVisible);
        await conversion;

        Assert.False(HeicTestSupport.IsAlive(processId));
        Assert.False(harness.ProgressStack.IsVisible);
        Assert.False(harness.MessageBar.IsVisible);
        Assert.Equal(string.Empty, harness.InputBox.Text);
    }

    [UnixFact]
    public async Task APngPickedDuringTheConversion_ReplacesIt_AndIsRead()
    {
        FakeSips fake = FakeSips.Sleeping();
        Harness harness = CreateHarness(CreateFakeConverter(fake));
        harness.Pick(CreateFile("heic", [1, 2, 3]));
        Task conversion = harness.Tool.WorkTask!;
        int processId = await fake.WaitForProcessIdAsync();

        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.EAN_13, "400638133393");
        FileInfo png = _fileStorage.CreateFile("png");
        image.SaveAsPng(png.FullName);
        harness.Pick(png);
        await harness.Tool.WorkTask!;
        await conversion;

        Assert.False(HeicTestSupport.IsAlive(processId));
        Assert.Equal("4006381333931", harness.InputBox.Text);
        Assert.False(harness.MessageBar.IsVisible);
        var shown = Assert.IsAssignableFrom<SandboxedFileReader>(harness.ImageViewer.ImageSource!.Value.Value);
        Assert.Equal(png.Name, shown.FileName);
    }

    [UnixFact]
    public async Task ASuccessfulConversion_ByAScript_IsReadLikeAnyPng()
    {
        // The script "converts" by copying a PNG it was given, so that the path through the tool is checked on any system.
        using Image<Rgba32> image = TestImages.Generate(BarcodeFormat.CODE_39, "FROM-SCRIPT");
        FileInfo source = _fileStorage.CreateFile("png");
        image.SaveAsPng(source.FullName);
        string sourcePath = Path.Combine(Path.GetTempPath(), "devtoys-barcode-script-source-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(source.FullName, sourcePath);
        _disposables.Add(new DeleteOnDispose(sourcePath));
        Harness harness = CreateHarness(CreateFakeConverter(new FakeSips($"cp '{sourcePath}' \"$6\"")));

        harness.Pick(CreateFile("heic", [1, 2, 3]));
        await harness.Tool.WorkTask!;

        Assert.Equal("FROM-SCRIPT", harness.InputBox.Text);
        Assert.Equal(BarcodeFormat.CODE_39, harness.FormatDropDown.SelectedItem!.Value);
        var shown = Assert.IsAssignableFrom<SandboxedFileReader>(harness.ImageViewer.ImageSource!.Value.Value);
        Assert.EndsWith(".png", shown.FileName);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class Harness
    {
        internal required BarcodeEncoderDecoderGuiTool Tool { get; init; }
        internal required IUIMultiLineTextInput InputBox { get; init; }
        internal required IUIFileSelector FileSelector { get; init; }
        internal required IUIImageViewer ImageViewer { get; init; }
        internal required IUISelectDropDownList FormatDropDown { get; init; }
        internal required IUIDataGrid ResultsGrid { get; init; }
        internal required IUIInfoBar MessageBar { get; init; }
        internal required IUIStack ProgressStack { get; init; }
        internal required IUIButton CancelButton { get; init; }
        internal required MockFileStorage FileStorage { get; init; }

        internal void Pick(FileInfo file)
            => FileSelector.WithFiles(new MockSandboxedFileReader(file, FileStorage));
    }

    /// <param name="converter">The converter of the tool, or null for the default one (the real <c>sips</c> on macOS).</param>
    private Harness CreateHarness(HeicConverter? converter)
    {
        BarcodeEncoderDecoderGuiTool tool = converter is null
            ? new BarcodeEncoderDecoderGuiTool(_settings, _fileStorage)
            : new BarcodeEncoderDecoderGuiTool(_settings, _fileStorage, converter);
        _disposables.Add(tool);
        UIToolView view = tool.View;

        return new Harness
        {
            Tool = tool,
            InputBox = Get<IUIMultiLineTextInput>(view, "barcode-input-box"),
            FileSelector = Get<IUIFileSelector>(view, "barcode-file-selector"),
            ImageViewer = Get<IUIImageViewer>(view, "barcode-preview"),
            FormatDropDown = Get<IUISelectDropDownList>(view, "barcode-format-drop-down"),
            ResultsGrid = Get<IUIDataGrid>(view, "barcode-results-data-grid"),
            MessageBar = Get<IUIInfoBar>(view, "barcode-message-info-bar"),
            ProgressStack = Get<IUIStack>(view, "barcode-progress-stack"),
            CancelButton = Get<IUIButton>(view, "barcode-cancel-button"),
            FileStorage = _fileStorage
        };
    }

    private HeicConverter CreateFakeConverter(FakeSips fake)
    {
        _disposables.Add(fake);
        return new HeicConverter(_fileStorage) { IsSupported = true, ExecutablePath = fake.ScriptPath, Timeout = TimeSpan.FromSeconds(20) };
    }

    private static T Get<T>(UIToolView view, string id)
        where T : class
        => (T)view.GetChildElementById(id)!;

    private static string CellText(IUIDataGridCell cell)
        => ((IUILabel)cell.UIElement!).Text!;

    private FileInfo CreateFile(string extension, byte[] content)
    {
        FileInfo file = _fileStorage.CreateFile(extension);
        File.WriteAllBytes(file.FullName, content);
        return file;
    }

    /// <summary>A Code 128, an EAN-13 and a Data Matrix symbol on one picture, as in test-images/three-barcodes.png.</summary>
    private static Image<Rgba32> ThreeBarcodes()
    {
        // The generated Data Matrix is large: a smaller one is closer to a barcode in a photograph.
        Image<Rgba32> dataMatrix = TestImages.Generate(BarcodeFormat.DATA_MATRIX, "DevToys Barcode");
        dataMatrix.Mutate(context => context.Resize(200, 200, KnownResamplers.NearestNeighbor));

        return TestImages.Compose(
            1200,
            1000,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 140, 70),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 140, 370),
            (dataMatrix, 750, 650));
    }

    /// <summary>Saves the picture as a HEIC file with sips, under the given name, in the folder of the mock storage.</summary>
    private FileInfo CreateHeicFile(Image<Rgba32> picture, string fileName)
    {
        using (picture)
        {
            string png = Path.Combine(Path.GetTempPath(), "devtoys-barcode-source-" + Guid.NewGuid().ToString("N") + ".png");
            picture.SaveAsPng(png);
            _disposables.Add(new DeleteOnDispose(png));

            string heic = Path.Combine(_fileStorage.AppCacheDirectory, fileName);
            HeicTestSupport.ConvertPngToHeic(png, heic);
            return new FileInfo(heic);
        }
    }

    private sealed class DeleteOnDispose(string path) : IDisposable
    {
        public void Dispose() => File.Delete(path);
    }
}
