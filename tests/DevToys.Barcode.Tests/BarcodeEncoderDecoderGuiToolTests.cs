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
/// Drives <see cref="BarcodeEncoderDecoderGuiTool"/> through its UI elements, the way DevToys' own tool tests do:
/// the elements are the real DevToys.Api implementations, only the host services (settings, file storage) are mocked.
/// What cannot be checked this way is how the elements are drawn.
/// </summary>
public sealed class BarcodeEncoderDecoderGuiToolTests : IDisposable
{
    private readonly MockFileStorage _fileStorage = new();
    private readonly MockSettingsProvider _settings = new();
    private readonly BarcodeEncoderDecoderGuiTool _tool;
    private readonly UIToolView _view;
    private readonly IUIMultiLineTextInput _inputBox;
    private readonly IUIFileSelector _fileSelector;
    private readonly IUIImageViewer _imageViewer;
    private readonly IUISelectDropDownList _formatDropDown;
    private readonly IUIDataGrid _resultsGrid;
    private readonly IUIInfoBar _messageBar;
    private readonly IUIInfoBar _encodedValueBar;

    static BarcodeEncoderDecoderGuiToolTests()
    {
        LoggingExtensions.LoggerFactory = NullLoggerFactory.Instance;
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");
    }

    public BarcodeEncoderDecoderGuiToolTests()
    {
        _tool = CreateTool();
        _view = _tool.View;
        _inputBox = Get<IUIMultiLineTextInput>(_view, "barcode-input-box");
        _fileSelector = Get<IUIFileSelector>(_view, "barcode-file-selector");
        _imageViewer = Get<IUIImageViewer>(_view, "barcode-preview");
        _formatDropDown = Get<IUISelectDropDownList>(_view, "barcode-format-drop-down");
        _resultsGrid = Get<IUIDataGrid>(_view, "barcode-results-data-grid");
        _messageBar = Get<IUIInfoBar>(_view, "barcode-message-info-bar");
        _encodedValueBar = Get<IUIInfoBar>(_view, "barcode-encoded-value-info-bar");
    }

    public void Dispose()
    {
        _tool.Dispose();
        _fileStorage.Dispose();
    }

    // ---------------------------------------------------------------- construction and format list

    [Fact]
    public void Construction_AndReadingView_DoNotThrow_AndExposeAllElements()
    {
        Assert.NotNull(_view);
        Assert.NotNull(Get<IUISetting>(_view, "barcode-format-setting"));
        Assert.NotNull(Get<IUIProgressBar>(_view, "barcode-progress-bar"));
        Assert.Equal(string.Empty, _inputBox.Text);
        Assert.Null(_imageViewer.ImageSource);
        Assert.False(_resultsGrid.IsVisible);
        Assert.False(_messageBar.IsVisible);
        Assert.False(_encodedValueBar.IsVisible);

        // The view can be built more than once.
        Assert.NotNull(_tool.View);
    }

    [Fact]
    public void FormatDropDown_ListsTheGenerationFormatsInOrder_AndDefaultsToCode128()
    {
        Assert.Equal(
            new[] { "Code 128", "EAN-13", "EAN-8", "UPC-A", "UPC-E", "Code 39", "Code 93", "ITF", "Codabar", "Data Matrix", "PDF417", "Aztec" },
            _formatDropDown.Items!.Select(item => item.Text));
        Assert.Equal(BarcodeHelper.GenerationFormats, _formatDropDown.Items!.Select(item => (BarcodeFormat)item.Value!));
        Assert.Equal(BarcodeFormat.CODE_128, _formatDropDown.SelectedItem!.Value);
    }

    // ---------------------------------------------------------------- text to image

    [Fact]
    public async Task TypingValidText_ShowsAGeneratedImage_InTheSelectedFormat()
    {
        _inputBox.Text("Hello-123");
        await _tool.WorkTask!;

        using Image<Rgba32> image = CloneDisplayedImage();
        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));
        Assert.Equal(BarcodeFormat.CODE_128, result.Format);
        Assert.Equal("Hello-123", result.Text);
        Assert.False(_messageBar.IsVisible);
        Assert.False(_encodedValueBar.IsVisible);
        Assert.False(_resultsGrid.IsVisible);
        Assert.Equal("Hello-123", _inputBox.Text); // never rewritten
    }

    [Fact]
    public async Task EmptyText_ClearsTheImageAndTheMessages()
    {
        SelectFormatAsUser(BarcodeFormat.EAN_13);
        _inputBox.Text("12345");
        await _tool.WorkTask!;
        Assert.True(_messageBar.IsOpened);

        _inputBox.Text(string.Empty);
        await _tool.WorkTask!;

        Assert.Null(_imageViewer.ImageSource);
        Assert.False(_messageBar.IsVisible);
        Assert.False(_encodedValueBar.IsVisible);
        Assert.False(_imageViewer.CustomActionPerFileExtensionOnSaving.ContainsKey(".svg"));
    }

    [Fact]
    public async Task ATrailingLineBreakInTheText_IsIgnored()
    {
        SelectFormatAsUser(BarcodeFormat.EAN_13);
        _inputBox.Text("4006381333931\r\n");
        await _tool.WorkTask!;

        Assert.False(_messageBar.IsVisible);
        Assert.False(_encodedValueBar.IsVisible);
        Assert.IsAssignableFrom<Image>(_imageViewer.ImageSource!.Value.Value);
    }

    [Fact]
    public async Task CheckDigitAddedByTheHelper_IsShownInAnInfoBar_AndTheTextIsNotRewritten()
    {
        SelectFormatAsUser(BarcodeFormat.EAN_13);
        _inputBox.Text("400638133393");
        await _tool.WorkTask!;

        Assert.Equal("400638133393", _inputBox.Text);
        Assert.True(_encodedValueBar.IsVisible);
        Assert.True(_encodedValueBar.IsOpened);
        Assert.Contains("4006381333931", _encodedValueBar.Description);
        Assert.False(_messageBar.IsVisible);

        // With the check digit typed, there is nothing to tell.
        _inputBox.Text("4006381333931");
        await _tool.WorkTask!;
        Assert.False(_encodedValueBar.IsVisible);
    }

    public static TheoryData<BarcodeFormat, string, BarcodeGenerationFailureReason> FailureCases => new()
    {
        { BarcodeFormat.EAN_13, "40063813339A", BarcodeGenerationFailureReason.InvalidCharacters },
        { BarcodeFormat.EAN_13, "12345", BarcodeGenerationFailureReason.WrongLength },
        { BarcodeFormat.EAN_13, "4006381333932", BarcodeGenerationFailureReason.BadCheckDigit },
        { BarcodeFormat.ITF, new string('1', 82), BarcodeGenerationFailureReason.TooLong },
        { BarcodeFormat.UPC_E, "2234565", BarcodeGenerationFailureReason.EncoderRejected },
    };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task InvalidText_ClearsTheImage_AndExplainsTheProblemAndTheRule(
        BarcodeFormat format, string input, BarcodeGenerationFailureReason reason)
    {
        SelectFormatAsUser(format);
        string name = BarcodeFormatNames.GetDisplayName(format);

        _inputBox.Text("1234567890128".Substring(0, 12)); // a valid-looking value first so that there is an image to clear
        await _tool.WorkTask!;
        _inputBox.Text(input);
        await _tool.WorkTask!;

        Assert.Null(_imageViewer.ImageSource);
        Assert.True(_messageBar.IsVisible);
        Assert.True(_messageBar.IsOpened);
        Assert.Equal(UIInfoBarSeverity.Error, _messageBar.Severity);
        Assert.Equal(BarcodeEncoderDecoder.GenerationFailedTitle, _messageBar.Title);
        Assert.False(_encodedValueBar.IsVisible);

        string expectedStart = reason switch
        {
            BarcodeGenerationFailureReason.InvalidCharacters => string.Format(BarcodeEncoderDecoder.InvalidCharactersMessage, name),
            BarcodeGenerationFailureReason.WrongLength => string.Format(BarcodeEncoderDecoder.WrongLengthMessage, name),
            BarcodeGenerationFailureReason.BadCheckDigit => BarcodeEncoderDecoder.BadCheckDigitMessage,
            BarcodeGenerationFailureReason.TooLong => string.Format(BarcodeEncoderDecoder.TooLongMessage, name),
            _ => string.Format(BarcodeEncoderDecoder.EncoderRejectedMessage, name, "Number system must be 0 or 1")
        };
        Assert.StartsWith(expectedStart, _messageBar.Description);

        if (reason != BarcodeGenerationFailureReason.EncoderRejected)
        {
            // The message says what the format requires.
            string rule = format == BarcodeFormat.EAN_13 ? BarcodeEncoderDecoder.RuleEan13 : BarcodeEncoderDecoder.RuleItf;
            Assert.EndsWith(rule, _messageBar.Description);
        }
    }

    [Fact]
    public async Task ErrorIsReplacedByTheImage_WhenTheTextIsCorrected()
    {
        SelectFormatAsUser(BarcodeFormat.EAN_13);
        _inputBox.Text("12345");
        await _tool.WorkTask!;
        Assert.True(_messageBar.IsVisible);

        _inputBox.Text("400638133393");
        await _tool.WorkTask!;

        Assert.False(_messageBar.IsVisible);
        Assert.IsAssignableFrom<Image>(_imageViewer.ImageSource!.Value.Value);
    }

    [Fact]
    public async Task ChangingTheFormat_RegeneratesFromTheCurrentText_AndSavesTheChoice()
    {
        _inputBox.Text("4006381333931");
        await _tool.WorkTask!;
        using (Image<Rgba32> code128 = CloneDisplayedImage())
        {
            Assert.Equal(BarcodeFormat.CODE_128, Assert.Single(BarcodeHelper.Read(code128)).Format);
        }

        SelectFormatAsUser(BarcodeFormat.EAN_13);
        await _tool.WorkTask!;

        using Image<Rgba32> ean13 = CloneDisplayedImage();
        Assert.Equal(BarcodeFormat.EAN_13, Assert.Single(BarcodeHelper.Read(ean13)).Format);
        Assert.Equal(BarcodeFormat.EAN_13, _settings.GetSetting(FormatSetting()));
    }

    [Fact]
    public async Task ChangingTheFormat_ToOneThatRejectsTheText_ShowsTheError()
    {
        _inputBox.Text("Hello");
        await _tool.WorkTask!;

        SelectFormatAsUser(BarcodeFormat.EAN_13);
        await _tool.WorkTask!;

        Assert.Null(_imageViewer.ImageSource);
        Assert.True(_messageBar.IsVisible);
    }

    [Fact]
    public void ChangingTheFormat_WithNoText_GeneratesNothing()
    {
        Task? before = _tool.WorkTask;

        SelectFormatAsUser(BarcodeFormat.AZTEC);

        Assert.Same(before, _tool.WorkTask);
        Assert.Null(_imageViewer.ImageSource);
    }

    [Fact]
    public void TheFormatChoice_IsRestoredByANewInstance_AndAnUnknownSavedFormatFallsBackToCode128()
    {
        SelectFormatAsUser(BarcodeFormat.DATA_MATRIX);

        using BarcodeEncoderDecoderGuiTool second = CreateTool();
        Assert.Equal(BarcodeFormat.DATA_MATRIX, Get<IUISelectDropDownList>(second.View, "barcode-format-drop-down").SelectedItem!.Value);

        _settings.SetSetting(FormatSetting(), BarcodeFormat.QR_CODE);
        using BarcodeEncoderDecoderGuiTool third = CreateTool();
        Assert.Equal(BarcodeFormat.CODE_128, Get<IUISelectDropDownList>(third.View, "barcode-format-drop-down").SelectedItem!.Value);
    }

    // ---------------------------------------------------------------- SVG

    [Fact]
    public async Task SaveAsSvg_IsOnlyAvailableForAGeneratedBarcode_AndWritesTheDisplayedOne()
    {
        Assert.False(_imageViewer.CustomActionPerFileExtensionOnSaving.ContainsKey(".svg"));

        SelectFormatAsUser(BarcodeFormat.EAN_13);
        _inputBox.Text("400638133393");
        await _tool.WorkTask!;

        Func<FileStream, ValueTask> save = _imageViewer.CustomActionPerFileExtensionOnSaving[".svg"];
        FileInfo target = _fileStorage.CreateFile("svg");
        await save(_fileStorage.OpenWriteFile(target.FullName, replaceIfExist: true));

        string svg = await File.ReadAllTextAsync(target.FullName);
        BarcodeGenerationResult<string> expected = BarcodeHelper.GenerateSvg("400638133393", BarcodeFormat.EAN_13);
        Assert.Equal(expected.Value, svg);
        Assert.Contains("<svg", svg);
    }

    [Fact]
    public async Task SaveAsSvg_WritesNothing_AfterAPictureWasPicked_EvenWithTheOldHandler()
    {
        _inputBox.Text("Hello");
        await _tool.WorkTask!;
        Func<FileStream, ValueTask> oldHandler = _imageViewer.CustomActionPerFileExtensionOnSaving[".svg"];

        PickFile(CreatePngFile(BarcodeFormat.EAN_13, "4006381333931"));
        await _tool.WorkTask!;

        Assert.False(_imageViewer.CustomActionPerFileExtensionOnSaving.ContainsKey(".svg"));
        FileInfo target = _fileStorage.CreateFile("svg");
        await oldHandler(_fileStorage.OpenWriteFile(target.FullName, replaceIfExist: true));
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public async Task SaveAsSvg_WritesNothing_AfterAFailedGeneration()
    {
        _inputBox.Text("Hello");
        await _tool.WorkTask!;
        Func<FileStream, ValueTask> oldHandler = _imageViewer.CustomActionPerFileExtensionOnSaving[".svg"];

        SelectFormatAsUser(BarcodeFormat.EAN_13);
        await _tool.WorkTask!; // "Hello" is not an EAN-13

        Assert.False(_imageViewer.CustomActionPerFileExtensionOnSaving.ContainsKey(".svg"));
        FileInfo target = _fileStorage.CreateFile("svg");
        await oldHandler(_fileStorage.OpenWriteFile(target.FullName, replaceIfExist: true));
        Assert.Equal(0, target.Length);
    }

    // ---------------------------------------------------------------- image to text

    [Fact]
    public async Task PickedPngWithABarcode_FillsTheText_SetsTheFormat_ListsTheResult_AndKeepsThePickedImage()
    {
        PickFile(CreatePngFile(BarcodeFormat.EAN_13, "400638133393"));
        await _tool.WorkTask!;

        Assert.Equal("4006381333931", _inputBox.Text);
        Assert.Equal(BarcodeFormat.EAN_13, _formatDropDown.SelectedItem!.Value);
        Assert.IsAssignableFrom<SandboxedFileReader>(_imageViewer.ImageSource!.Value.Value); // not replaced by a generated image
        Assert.True(_resultsGrid.IsVisible);
        IUIDataGridRow row = Assert.Single(_resultsGrid.Rows);
        Assert.Equal(new[] { "EAN-13", "4006381333931" }, row.Select(CellText));
        Assert.False(_messageBar.IsVisible);
        Assert.False(_imageViewer.CustomActionPerFileExtensionOnSaving.ContainsKey(".svg"));
        Assert.Equal(0, _settings.WriteCount); // the detected format is not saved as the user's choice
    }

    [Fact]
    public async Task TheDetectedFormat_IsNotSaved_EvenWhenTheHostNotifiesTheSelectionLate()
    {
        PickFile(CreatePngFile(BarcodeFormat.EAN_13, "400638133393"));
        await _tool.WorkTask!;
        Task readingTask = _tool.WorkTask!;

        // DevToys 2.0.9 notifies the selection made by the tool after the reading has ended.
        await _formatDropDown.OnItemSelectedAction!(_formatDropDown.SelectedItem);

        Assert.Equal(0, _settings.WriteCount);
        Assert.Same(readingTask, _tool.WorkTask); // no generation was started
        Assert.IsAssignableFrom<SandboxedFileReader>(_imageViewer.ImageSource!.Value.Value);

        // A choice made by the user afterwards is saved.
        SelectFormatAsUser(BarcodeFormat.AZTEC);
        await _tool.WorkTask!;
        Assert.Equal(BarcodeFormat.AZTEC, _settings.GetSetting(FormatSetting()));

        SelectFormatAsUser(BarcodeFormat.EAN_13);
        await _tool.WorkTask!;
        Assert.Equal(BarcodeFormat.EAN_13, _settings.GetSetting(FormatSetting()));
    }

    [Fact]
    public async Task EditingTheText_AfterAPicture_RegeneratesInTheDetectedFormat_AndClearsTheResults()
    {
        PickFile(CreatePngFile(BarcodeFormat.CODABAR, "A1234B"));
        await _tool.WorkTask!;
        Assert.Equal(BarcodeFormat.CODABAR, _formatDropDown.SelectedItem!.Value);
        Assert.Equal("1234", _inputBox.Text);

        _inputBox.Text("5678");
        await _tool.WorkTask!;

        Assert.False(_resultsGrid.IsVisible);
        Assert.Empty(_resultsGrid.Rows);
        using Image<Rgba32> image = CloneDisplayedImage();
        BarcodeReadResult result = Assert.Single(BarcodeHelper.Read(image));
        Assert.Equal(BarcodeFormat.CODABAR, result.Format);
        Assert.Equal("5678", result.Text);
    }

    [Fact]
    public async Task SeveralBarcodes_AreAllListed_AndTheFirstOneFillsTheText()
    {
        using Image<Rgba32> composed = TestImages.Compose(
            800,
            420,
            (TestImages.Generate(BarcodeFormat.CODE_128, "Hello-123"), 20, 20),
            (TestImages.Generate(BarcodeFormat.EAN_13, "4006381333931"), 20, 220));
        PickFile(SavePng(composed));
        await _tool.WorkTask!;

        Assert.Equal("Hello-123", _inputBox.Text);
        Assert.Equal(BarcodeFormat.CODE_128, _formatDropDown.SelectedItem!.Value);
        Assert.Equal(
            new[] { "Code 128|Hello-123", "EAN-13|4006381333931" },
            _resultsGrid.Rows.Select(row => string.Join("|", row.Select(CellText))));
    }

    [Fact]
    public async Task AFormatThatCannotBeGenerated_IsListedButLeavesTheDropDownAlone()
    {
        SelectFormatAsUser(BarcodeFormat.AZTEC);
        var qrWriter = new ZXing.ImageSharp.BarcodeWriter<Rgba32> { Format = BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 300, Height = 300, Margin = 4 } };
        using Image<Rgba32> qr = qrWriter.Write("https://example.com");
        PickFile(SavePng(qr));
        await _tool.WorkTask!;

        Assert.Equal("https://example.com", _inputBox.Text);
        Assert.Equal(BarcodeFormat.AZTEC, _formatDropDown.SelectedItem!.Value);
        Assert.Equal(new[] { "QR Code", "https://example.com" }, Assert.Single(_resultsGrid.Rows).Select(CellText));
    }

    [Fact]
    public async Task AnImageWithoutBarcode_ClearsTheText_AndShowsTheNotFoundMessage()
    {
        _inputBox.Text("previous text");
        await _tool.WorkTask!;

        using var blank = new Image<Rgba32>(300, 200, Color.White);
        PickFile(SavePng(blank));
        await _tool.WorkTask!;

        Assert.Equal(string.Empty, _inputBox.Text);
        Assert.False(_resultsGrid.IsVisible);
        Assert.True(_messageBar.IsVisible);
        Assert.True(_messageBar.IsOpened);
        Assert.Equal(UIInfoBarSeverity.Warning, _messageBar.Severity);
        Assert.Equal(BarcodeEncoderDecoder.NoBarcodeFound, _messageBar.Title);
        Assert.IsAssignableFrom<SandboxedFileReader>(_imageViewer.ImageSource!.Value.Value);
    }

    [Fact]
    public async Task AFileThatIsNotAnImage_ShowsAnError_WithoutThrowing()
    {
        FileInfo file = _fileStorage.CreateFile("png");
        await File.WriteAllTextAsync(file.FullName, "this is not a picture");

        PickFile(file);
        await _tool.WorkTask!;

        Assert.Equal(string.Empty, _inputBox.Text);
        Assert.True(_messageBar.IsVisible);
        Assert.Equal(UIInfoBarSeverity.Error, _messageBar.Severity);
        Assert.Equal(BarcodeEncoderDecoder.ImageNotReadableTitle, _messageBar.Title);
    }

    [Fact]
    public async Task AnActionStartedWhileReading_CancelsTheReading()
    {
        // A large picture without any barcode takes a while to read.
        using Image<Rgba32> noise = CreateNoise(2000, 2000);
        PickFile(SavePng(noise));
        Task reading = _tool.WorkTask!;

        _inputBox.Text("typed while reading");
        Task generating = _tool.WorkTask!;
        await generating;
        await reading; // never throws: cancellation is handled

        Assert.NotSame(reading, generating);
        Assert.Equal("typed while reading", _inputBox.Text);
        Assert.False(_resultsGrid.IsVisible);
        Assert.False(_messageBar.IsVisible);
        Assert.IsAssignableFrom<Image>(_imageViewer.ImageSource!.Value.Value);
    }

    [Fact]
    public async Task QuickReading_NeverShowsTheProgress()
    {
        IUIStack progress = Get<IUIStack>(_view, "barcode-progress-stack");
        Assert.False(progress.IsVisible);

        using var blank = new Image<Rgba32>(300, 200, Color.White);
        PickFile(SavePng(blank));
        await _tool.WorkTask!;

        Assert.False(progress.IsVisible);
    }

    [Fact]
    public async Task SlowReading_ShowsTheProgress_UntilItIsCancelledByTheUser()
    {
        IUIStack progress = Get<IUIStack>(_view, "barcode-progress-stack");
        IUIButton cancelButton = Get<IUIButton>(_view, "barcode-cancel-button");
        PickFile(SavePng(CreateNoise(2500, 2500)));
        Task reading = _tool.WorkTask!;

        for (int i = 0; i < 40 && !progress.IsVisible; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(progress.IsVisible, "the progress is shown when reading takes more than a moment");

        await cancelButton.OnClickAction!();
        Assert.False(progress.IsVisible);

        await reading; // finishes without an exception, and without touching the UI
        Assert.False(progress.IsVisible);
        Assert.Equal(string.Empty, _inputBox.Text);
        Assert.False(_messageBar.IsVisible);
    }

    [Fact]
    public async Task ProgressRow_IsNotShownAfterItsReadingHasEnded()
    {
        // The delay before showing the progress ends, then the reading finishes, and only then does the code that
        // shows the row get to run. The row must stay hidden.
        IUIStack progress = Get<IUIStack>(_view, "barcode-progress-stack");
        var delayEnded = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _tool.ProgressDelay = TimeSpan.Zero;
        _tool.BeforeShowingProgress = async () =>
        {
            delayEnded.SetResult();
            await release.Task;
        };

        using var blank = new Image<Rgba32>(300, 200, Color.White);
        PickFile(SavePng(blank));
        await delayEnded.Task;
        await _tool.WorkTask!; // the reading is over, and the row was hidden
        Assert.False(progress.IsVisible);

        release.SetResult();
        await _tool.ProgressTask!;

        Assert.False(progress.IsVisible);
    }

    [Fact]
    public async Task ProgressRow_OfAReplacedReading_IsNotShown()
    {
        IUIStack progress = Get<IUIStack>(_view, "barcode-progress-stack");
        var delayEnded = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _tool.ProgressDelay = TimeSpan.Zero;
        _tool.BeforeShowingProgress = async () =>
        {
            delayEnded.TrySetResult();
            await release.Task;
        };

        PickFile(SavePng(CreateNoise(1500, 1500)));
        Task reading = _tool.WorkTask!;
        Task progressOfReading = _tool.ProgressTask!;
        await delayEnded.Task;

        _tool.BeforeShowingProgress = null;
        _inputBox.Text("replaces the reading"); // cancels it and hides the row
        await _tool.WorkTask!;
        release.SetResult();
        await progressOfReading;
        await reading;

        Assert.False(progress.IsVisible);
    }

    [Fact]
    public async Task ProgressIsHidden_WhenSlowReadingEndsNormally()
    {
        IUIStack progress = Get<IUIStack>(_view, "barcode-progress-stack");
        PickFile(SavePng(CreateNoise(1500, 1500)));

        await _tool.WorkTask!;

        Assert.False(progress.IsVisible);
        Assert.True(_messageBar.IsVisible); // nothing found
    }

    // ---------------------------------------------------------------- data received (paste, Smart Detection)

    [Fact]
    public async Task ReceivedText_GeneratesABarcode()
    {
        _tool.OnDataReceived(PredefinedCommonDataTypeNames.Text, "Pasted");
        await _tool.WorkTask!;

        Assert.Equal("Pasted", _inputBox.Text);
        Assert.IsAssignableFrom<Image>(_imageViewer.ImageSource!.Value.Value);
    }

    [Fact]
    public async Task ReceivedImage_IsShownAndDecoded()
    {
        Task? before = _tool.WorkTask;
        Image<Rgba32> image = TestImages.Generate(BarcodeFormat.CODE_39, "PASTED-IMAGE");

        _tool.OnDataReceived(PredefinedCommonDataTypeNames.Image, image); // async void: wait for the work to start
        for (int i = 0; i < 100 && ReferenceEquals(_tool.WorkTask, before); i++)
        {
            await Task.Delay(50);
        }

        await _tool.WorkTask!;

        Assert.Equal("PASTED-IMAGE", _inputBox.Text);
        Assert.Equal(BarcodeFormat.CODE_39, _formatDropDown.SelectedItem!.Value);
        Assert.IsAssignableFrom<SandboxedFileReader>(_imageViewer.ImageSource!.Value.Value);
    }

    // ---------------------------------------------------------------- helpers

    private static Image<Rgba32> CreateNoise(int width, int height)
    {
        var noise = new Image<Rgba32>(width, height);
        var random = new Random(7);
        noise.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                foreach (ref Rgba32 pixel in accessor.GetRowSpan(y))
                {
                    byte value = (byte)(random.Next(2) * 255);
                    pixel = new Rgba32(value, value, value, 255);
                }
            }
        });
        return noise;
    }

    private BarcodeEncoderDecoderGuiTool CreateTool() => new(_settings, _fileStorage);

    // The setting definition is private, and DevToys prefixes setting names with the name of the calling assembly,
    // so a copy built in the test project would not have the same name.
    private static SettingDefinition<BarcodeFormat> FormatSetting()
        => (SettingDefinition<BarcodeFormat>)typeof(BarcodeEncoderDecoderGuiTool)
            .GetField("barcodeFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

    private static T Get<T>(UIToolView view, string id)
        where T : class
        => (T)view.GetChildElementById(id)!;

    /// <summary>Selects a format the way the user does (as opposed to the tool doing it after reading a picture).</summary>
    private void SelectFormatAsUser(BarcodeFormat format)
        => _formatDropDown.Select(_formatDropDown.Items!.Single(item => (BarcodeFormat)item.Value! == format));

    private void PickFile(FileInfo file)
        => _fileSelector.WithFiles(new MockSandboxedFileReader(file, _fileStorage));

    private FileInfo CreatePngFile(BarcodeFormat format, string text)
    {
        using Image<Rgba32> image = TestImages.Generate(format, text);
        return SavePng(image);
    }

    private FileInfo SavePng(Image<Rgba32> image)
    {
        FileInfo file = _fileStorage.CreateFile("png");
        image.SaveAsPng(file.FullName);
        return file;
    }

    private Image<Rgba32> CloneDisplayedImage()
    {
        object? source = _imageViewer.ImageSource?.Value;
        Image image = Assert.IsAssignableFrom<Image>(source);
        return ((Image<Rgba32>)image).Clone();
    }

    private static string CellText(IUIDataGridCell cell)
        => ((IUILabel)cell.UIElement!).Text!;
}
