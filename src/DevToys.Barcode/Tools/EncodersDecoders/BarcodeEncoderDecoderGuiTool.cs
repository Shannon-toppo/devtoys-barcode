using CommunityToolkit.Diagnostics;
using DevToys.Barcode.Helpers;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace DevToys.Barcode.Tools.EncodersDecoders;

[Export(typeof(IGuiTool))]
[Name("BarcodeEncoderDecoder")]
[ToolDisplayInformation(
    IconFontName = "FluentSystemIcons",
    IconGlyph = '', // ic_fluent_barcode_scanner_20_regular
    GroupName = PredefinedCommonToolGroupNames.EncodersDecoders,
    ResourceManagerAssemblyIdentifier = nameof(DevToysBarcodeResourceManagerAssemblyIdentifier),
    ResourceManagerBaseName = "DevToys.Barcode.Tools.EncodersDecoders.BarcodeEncoderDecoder",
    ShortDisplayTitleResourceName = nameof(BarcodeEncoderDecoder.ShortDisplayTitle),
    LongDisplayTitleResourceName = nameof(BarcodeEncoderDecoder.LongDisplayTitle),
    DescriptionResourceName = nameof(BarcodeEncoderDecoder.Description),
    AccessibleNameResourceName = nameof(BarcodeEncoderDecoder.AccessibleName),
    SearchKeywordsResourceName = nameof(BarcodeEncoderDecoder.SearchKeywords))]
[AcceptedDataTypeName(PredefinedCommonDataTypeNames.Image)]
[AcceptedDataTypeName(PredefinedCommonDataTypeNames.Text)]
internal sealed class BarcodeEncoderDecoderGuiTool : IGuiTool, IDisposable
{
    /// <summary>
    /// The barcode format used to generate an image from a text.
    /// </summary>
    private static readonly SettingDefinition<BarcodeFormat> barcodeFormat
        = new(
            name: $"{nameof(BarcodeEncoderDecoderGuiTool)}.{nameof(barcodeFormat)}",
            defaultValue: BarcodeFormat.CODE_128);

    private enum GridRows
    {
        Settings,
        Content,
        Messages
    }

    private enum LeftGridRows
    {
        Input,
        Results
    }

    private enum RightGridRows
    {
        FileInput,
        Progress,
        ImagePreviewer
    }

    private enum GridColumns
    {
        Stretch
    }

    /// <summary>The text and format of the barcode currently displayed, when it was generated from the text.</summary>
    private sealed record GeneratedBarcode(string Text, BarcodeFormat Format);

    private readonly DisposableSemaphore _semaphore = new();
    private readonly object _progressLock = new();
    private readonly IFileStorage _fileStorage;
    private readonly HeicConverter _heicConverter;
    private readonly ISettingsProvider _settingsProvider;
    private readonly ILogger _logger;
    private readonly IUISelectDropDownList _formatDropDown = SelectDropDownList("barcode-format-drop-down");
    private readonly IUIMultiLineTextInput _inputText = MultiLineTextInput("barcode-input-box");
    private readonly IUIDataGrid _resultsDataGrid = DataGrid("barcode-results-data-grid");
    private readonly IUIFileSelector _fileSelector = FileSelector("barcode-file-selector");
    private readonly IUIStack _progressStack = Stack("barcode-progress-stack");
    private readonly IUIProgressBar _progressBar = ProgressBar("barcode-progress-bar");
    private readonly IUIImageViewer _imageViewer = ImageViewer("barcode-preview");
    private readonly IUIInfoBar _messageInfoBar = InfoBar("barcode-message-info-bar");
    private readonly IUIInfoBar _encodedValueInfoBar = InfoBar("barcode-encoded-value-info-bar");
    private readonly IUIDropDownListItem[] _formatItems;

    private SandboxedFileReader? _selectedFile;
    private SandboxedFileReader? _convertedFile;
    private CancellationTokenSource? _cancellationTokenSource;
    private BarcodeFormat _selectedFormat;
    private GeneratedBarcode? _displayedBarcode;
    private bool _ignoreTextChange;
    private BarcodeFormat? _formatSetAfterReading;

    [ImportingConstructor]
    public BarcodeEncoderDecoderGuiTool(ISettingsProvider settingsProvider, IFileStorage fileStorage)
        : this(settingsProvider, fileStorage, new HeicConverter(fileStorage))
    {
    }

    // For unit tests, which replace the converter's platform and tool.
    internal BarcodeEncoderDecoderGuiTool(ISettingsProvider settingsProvider, IFileStorage fileStorage, HeicConverter heicConverter)
    {
        _settingsProvider = settingsProvider;
        _fileStorage = fileStorage;
        _heicConverter = heicConverter;
        _logger = this.Log();

        _formatItems = BarcodeHelper.GenerationFormats.Select(format => Item(BarcodeFormatNames.GetDisplayName(format), format)).ToArray();
        _selectedFormat = GetSavedFormat();
        _formatDropDown
            .WithItems(_formatItems)
            .Select(FindFormatItem(_selectedFormat))
            .OnItemSelected(OnFormatSelected);

        _progressBar.StartIndeterminateProgress();
    }

    // For unit tests.
    internal Task? WorkTask { get; private set; }

    // For unit tests: the task that shows the progress row after a delay, how long it waits, and a gate to pause it
    // between the end of the delay and the moment the row is shown.
    internal Task? ProgressTask { get; private set; }

    internal TimeSpan ProgressDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    internal Func<Task>? BeforeShowingProgress { get; set; }

    public UIToolView View
        => new(
            isScrollable: true,
            Grid()
                .RowLargeSpacing()

                .Rows(
                    (GridRows.Settings, Auto),
                    (GridRows.Content, new UIGridLength(1, UIGridUnitType.Fraction)),
                    (GridRows.Messages, Auto))

                .Columns(
                    (GridColumns.Stretch, new UIGridLength(1, UIGridUnitType.Fraction)))

                .Cells(
                    Cell(
                        GridRows.Settings,
                        GridColumns.Stretch,

                        Stack()
                            .Vertical()
                            .SmallSpacing()

                            .WithChildren(
                                Label()
                                    .Text(BarcodeEncoderDecoder.ConfigurationTitle),

                                Setting("barcode-format-setting")
                                    .Icon("FluentSystemIcons", '') // ic_fluent_barcode_scanner_24_regular
                                    .Title(BarcodeEncoderDecoder.FormatTitle)
                                    .Description(BarcodeEncoderDecoder.FormatDescription)
                                    .InteractiveElement(_formatDropDown))),

                    Cell(
                        GridRows.Content,
                        GridColumns.Stretch,

                        SplitGrid()
                            .Vertical()
                            .LeftPaneLength(new UIGridLength(4, UIGridUnitType.Fraction))
                            .RightPaneLength(new UIGridLength(2, UIGridUnitType.Fraction))

                            .WithLeftPaneChild(
                                Grid()
                                    .RowLargeSpacing()

                                    .Rows(
                                        // A barcode holds little text: a fixed height keeps the box from filling a tall window.
                                        (LeftGridRows.Input, new UIGridLength(180, UIGridUnitType.Pixel)),
                                        (LeftGridRows.Results, Auto))

                                    .Columns(
                                        (GridColumns.Stretch, new UIGridLength(1, UIGridUnitType.Fraction)))

                                    .Cells(
                                        Cell(
                                            LeftGridRows.Input,
                                            GridColumns.Stretch,

                                            _inputText
                                                .Title(BarcodeEncoderDecoder.InputText)
                                                .AlwaysWrap()
                                                .CanCopyWhenEditable()
                                                .OnTextChanged(OnInputTextChanged)),

                                        Cell(
                                            LeftGridRows.Results,
                                            GridColumns.Stretch,

                                            _resultsDataGrid
                                                .Title(BarcodeEncoderDecoder.ResultsTitle)
                                                .WithColumns(
                                                    BarcodeEncoderDecoder.ResultsFormatColumn,
                                                    BarcodeEncoderDecoder.ResultsTextColumn)
                                                .ForbidSelectItem()
                                                .Hide())))

                            .WithRightPaneChild(
                                Grid()
                                    .RowLargeSpacing()

                                    .Rows(
                                        (RightGridRows.FileInput, Auto),
                                        (RightGridRows.Progress, Auto),
                                        (RightGridRows.ImagePreviewer, new UIGridLength(1, UIGridUnitType.Fraction)))

                                    .Columns(
                                        (GridColumns.Stretch, new UIGridLength(1, UIGridUnitType.Fraction)))

                                    .Cells(
                                        Cell(
                                            RightGridRows.FileInput,
                                            GridColumns.Stretch,

                                            _fileSelector
                                                .CanSelectOneFile()
                                                .LimitFileTypesTo(_heicConverter.GetAcceptedExtensions())
                                                .OnFilesSelected(OnFileSelectedAsync)),

                                        Cell(
                                            RightGridRows.Progress,
                                            GridColumns.Stretch,

                                            _progressStack
                                                .AlignHorizontally(UIHorizontalAlignment.Center)
                                                .Horizontal()
                                                .SmallSpacing()
                                                .Hide()

                                                .WithChildren(
                                                    Label().Text(BarcodeEncoderDecoder.ReadingBarcodes),
                                                    _progressBar,
                                                    Button("barcode-cancel-button")
                                                        .Icon("FluentSystemIcons", '') // ic_fluent_dismiss
                                                        .OnClick(OnCancelButtonClicked))),

                                        Cell(
                                            RightGridRows.ImagePreviewer,
                                            GridColumns.Stretch,

                                            _imageViewer
                                                .Title(BarcodeEncoderDecoder.ImageWithBarcode))))),

                    Cell(
                        GridRows.Messages,
                        GridColumns.Stretch,

                        Stack()
                            .Vertical()
                            .SmallSpacing()

                            .WithChildren(
                                _messageInfoBar
                                    .NonClosable()
                                    .Close()
                                    .Hide(),

                                _encodedValueInfoBar
                                    .Informational()
                                    .NonClosable()
                                    .HideIcon()
                                    .Close()
                                    .Hide()))));

    public async void OnDataReceived(string dataTypeName, object? parsedData)
    {
        if (dataTypeName == PredefinedCommonDataTypeNames.Text && parsedData is string text)
        {
            _inputText.Text(text); // This will trigger a conversion.
        }
        else if (dataTypeName == PredefinedCommonDataTypeNames.Image && parsedData is Image<Rgba32> image)
        {
            try
            {
                FileInfo temporaryFile = _fileStorage.CreateSelfDestroyingTempFile("png");

                using (image)
                {
                    using Stream fileStream = _fileStorage.OpenWriteFile(temporaryFile.FullName, replaceIfExist: true);
                    await image.SaveAsPngAsync(fileStream);
                }

                _fileSelector.WithFiles(SandboxedFileReader.FromFileInfo(temporaryFile)); // This will trigger a conversion.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to receive the image.");
                ShowMessage(BarcodeEncoderDecoder.ImageNotReadableTitle, BarcodeEncoderDecoder.ImageNotReadableDescription, isWarning: false);
            }
        }
    }

    public void Dispose()
    {
        _selectedFile?.Dispose();
        _convertedFile?.Dispose();
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _semaphore.Dispose();
    }

    private BarcodeFormat GetSavedFormat()
    {
        try
        {
            BarcodeFormat saved = _settingsProvider.GetSetting(barcodeFormat);
            if (BarcodeHelper.GenerationFormats.Contains(saved))
            {
                return saved;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.FormatException or OverflowException)
        {
            // The saved value is not a format name: use the default.
        }

        return barcodeFormat.DefaultValue;
    }

    private IUIDropDownListItem? FindFormatItem(BarcodeFormat format)
        => _formatItems.FirstOrDefault(item => item.Value is BarcodeFormat itemFormat && itemFormat == format);

    private void OnFormatSelected(IUIDropDownListItem? item)
    {
        if (item?.Value is not BarcodeFormat format)
        {
            return;
        }

        _selectedFormat = format;

        // The format was set by the code after reading a picture: neither remember it as the user's choice, nor
        // replace the picture by a generated one. The host may notify this selection later than the call to Select,
        // and more than once, so it is recognized by its value and not by a flag held during the call.
        if (_formatSetAfterReading == format)
        {
            return;
        }

        _formatSetAfterReading = null;
        _settingsProvider.SetSetting(barcodeFormat, format);

        if (!string.IsNullOrWhiteSpace(_inputText.Text))
        {
            StartGenerate(_inputText.Text);
        }
    }

    private void OnInputTextChanged(string text)
    {
        if (!_ignoreTextChange)
        {
            StartGenerate(text);
        }
    }

    private void OnCancelButtonClicked()
    {
        _cancellationTokenSource?.Cancel();
        HideProgress();
    }

    private ValueTask OnFileSelectedAsync(SandboxedFileReader[] files)
    {
        Guard.HasSizeEqualTo(files, 1);

        CancelRunningWork();
        ClearMessages();
        ClearResults();
        SetSvgExportAvailable(null);

        _selectedFile?.Dispose();
        _selectedFile = files[0];
        _convertedFile?.Dispose();
        _convertedFile = null;

        if (_heicConverter.NeedsConversion(_selectedFile.FileName))
        {
            // The picture is shown once it is converted, as part of the reading.
            _imageViewer.Clear();
            StartDecode(_selectedFile);
            return ValueTask.CompletedTask;
        }

        try
        {
            _imageViewer.WithPickedFile(_selectedFile, false);
        }
        catch (ArgumentException ex)
        {
            // The picked file has an extension the image viewer does not display.
            _logger.LogWarning(ex, "The image viewer cannot display '{FileName}'.", _selectedFile.FileName);
            _imageViewer.Clear();
            ShowMessage(BarcodeEncoderDecoder.ImageNotReadableTitle, BarcodeEncoderDecoder.ImageNotReadableDescription, isWarning: false);
            return ValueTask.CompletedTask;
        }

        StartDecode(_selectedFile);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnSaveAsSvgAsync(FileStream fileStream)
    {
        await using (fileStream)
        {
            // Only a barcode generated from the text can be saved as SVG: not a picked picture, not after an error.
            GeneratedBarcode? displayed = _displayedBarcode;
            if (displayed is null)
            {
                return;
            }

            BarcodeGenerationResult<string> svg = BarcodeHelper.GenerateSvg(displayed.Text, displayed.Format);
            if (!svg.IsSuccess)
            {
                return;
            }

            // DevToys opens the chosen file without truncating it: replacing a longer file would leave its end behind.
            fileStream.SetLength(0);
            using var fileWriter = new StreamWriter(fileStream, leaveOpen: true);
            await fileWriter.WriteAsync(svg.Value);
        }
    }

    private CancellationToken CancelRunningWork()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        HideProgress();
        return _cancellationTokenSource.Token;
    }

    private void StartGenerate(string text)
    {
        CancellationToken cancellationToken = CancelRunningWork();
        WorkTask = GenerateAsync(text, _selectedFormat, cancellationToken);
    }

    private void StartDecode(SandboxedFileReader file)
    {
        CancellationToken cancellationToken = CancelRunningWork();
        WorkTask = DecodeAsync(file, cancellationToken);
    }

    private async Task GenerateAsync(string input, BarcodeFormat format, CancellationToken cancellationToken)
    {
        try
        {
            await TaskSchedulerAwaiter.SwitchOffMainThreadAsync(cancellationToken);

            using (await _semaphore.WaitAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A trailing line break is almost always an accident of copy and paste, and no barcode wants it.
                string text = input.TrimEnd('\r', '\n');

                ClearMessages();
                ClearResults();

                if (string.IsNullOrWhiteSpace(text))
                {
                    SetSvgExportAvailable(null);
                    _imageViewer.Clear();
                    return;
                }

                BarcodeGenerationResult<Image<Rgba32>> result = BarcodeHelper.GenerateImage(text, format);

                cancellationToken.ThrowIfCancellationRequested();

                if (result.IsSuccess)
                {
                    // Do not dispose the image. Will be disposed by the image viewer when the image is replaced.
                    _imageViewer.WithImage(result.Value, disposeAutomatically: true);
                    SetSvgExportAvailable(new GeneratedBarcode(text, format));

                    if (result.EncodedText != text)
                    {
                        ShowEncodedValue(result.EncodedText);
                    }
                }
                else
                {
                    SetSvgExportAvailable(null);
                    _imageViewer.Clear();
                    ShowGenerationFailure(result.Reason.Value, result.Message, format);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer action replaced this one.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to generate a {Format} barcode.", format);
            SetSvgExportAvailable(null);
            _imageViewer.Clear();
            ShowMessage(
                BarcodeEncoderDecoder.GenerationFailedTitle,
                BarcodeGenerationMessages.GetEncoderRejected(format, ex.Message),
                isWarning: false);
        }
    }

    private async Task DecodeAsync(SandboxedFileReader file, CancellationToken cancellationToken)
    {
        // Show the progress only if reading takes a noticeable time, to avoid flickering.
        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ProgressTask = ShowProgressAfterDelayAsync(progressCancellation.Token);

        try
        {
            await TaskSchedulerAwaiter.SwitchOffMainThreadAsync(cancellationToken);

            using (await _semaphore.WaitAsync(cancellationToken))
            {
                SandboxedFileReader readable = file;
                if (_heicConverter.NeedsConversion(file.FileName))
                {
                    SandboxedFileReader? converted = await ConvertAndShowAsync(file, cancellationToken);
                    if (converted is null)
                    {
                        SetText(string.Empty);
                        ClearResults();
                        ShowMessage(BarcodeEncoderDecoder.ImageNotReadableTitle, BarcodeEncoderDecoder.ImageNotReadableDescription, isWarning: false);
                        return;
                    }

                    readable = converted;
                }

                using Stream stream = await readable.GetNewAccessToFileContentAsync(cancellationToken);

                IReadOnlyList<BarcodeReadResult> results = await BarcodeHelper.ReadAsync(stream, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (results.Count == 0)
                {
                    SetText(string.Empty);
                    ClearResults();
                    ShowMessage(BarcodeEncoderDecoder.NoBarcodeFound, BarcodeEncoderDecoder.NoBarcodeFoundDescription, isWarning: true);
                    return;
                }

                ClearMessages();
                ShowResults(results);
                SetText(results[0].Text);

                // Reflect the type of barcode that was found, if that is a type we can generate.
                IUIDropDownListItem? item = FindFormatItem(results[0].Format);
                if (item is not null && _formatDropDown.SelectedItem != item)
                {
                    _formatSetAfterReading = results[0].Format;
                    _formatDropDown.Select(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer action replaced this one, or the user cancelled.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read barcodes from '{FileName}'.", file.FileName);
            SetText(string.Empty);
            ClearResults();
            ShowMessage(BarcodeEncoderDecoder.ImageNotReadableTitle, BarcodeEncoderDecoder.ImageNotReadableDescription, isWarning: false);
        }
        finally
        {
            // Whatever happens next, the progress of this reading must not be shown any more. If the reading was
            // replaced, the progress row belongs to the newer one: leave it alone.
            lock (_progressLock)
            {
                progressCancellation.Cancel();
                if (!cancellationToken.IsCancellationRequested)
                {
                    _progressStack.Hide();
                }
            }
        }
    }

    /// <summary>
    /// Converts a HEIC or HEIF picture, shows the result in the image viewer and returns it, or returns null (after
    /// logging why) when the picture cannot be converted.
    /// </summary>
    private async Task<SandboxedFileReader?> ConvertAndShowAsync(SandboxedFileReader file, CancellationToken cancellationToken)
    {
        using Stream source = await file.GetNewAccessToFileContentAsync(cancellationToken);
        HeicConversionResult result = await _heicConverter.ConvertToPngAsync(source, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(result.Exception, "Unable to convert '{FileName}': {Reason}", file.FileName, result.Failure);
            return null;
        }

        SandboxedFileReader converted = SandboxedFileReader.FromFileInfo(result.PngFile!);

        // Taking the lock makes the check and the change one step with respect to a newer action, which cancels the
        // token and then takes the lock to hide the progress: it cannot be answered by this older picture.
        lock (_progressLock)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                converted.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _convertedFile = converted;
            _imageViewer.WithPickedFile(converted, false);
        }

        return converted;
    }

    private async Task ShowProgressAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ProgressDelay, cancellationToken);

            if (BeforeShowingProgress is { } gate)
            {
                await gate();
            }

            // The check and the Show are atomic with respect to HideProgress and to the end of the reading, which
            // cancel the token before they hide the row. So the row cannot be shown after its reading is over.
            lock (_progressLock)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _progressStack.Show();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Reading was quick, or it was replaced.
        }
    }

    /// <summary>Hides the progress row. Callers cancel the reading it belongs to first.</summary>
    private void HideProgress()
    {
        lock (_progressLock)
        {
            _progressStack.Hide();
        }
    }

    private void SetText(string text)
    {
        _ignoreTextChange = true;
        try
        {
            _inputText.Text(text);
        }
        finally
        {
            _ignoreTextChange = false;
        }
    }

    private void SetSvgExportAvailable(GeneratedBarcode? barcode)
    {
        _displayedBarcode = barcode;

        // Without this handler the image viewer does not offer SVG when saving.
        if (barcode is null)
        {
            _imageViewer.RemoveManuallyHandleSaveAs("svg");
        }
        else
        {
            _imageViewer.ManuallyHandleSaveAs("svg", OnSaveAsSvgAsync);
        }
    }

    private void ShowResults(IReadOnlyList<BarcodeReadResult> results)
    {
        _resultsDataGrid.WithRows(
            results
                .Select(result => Row(result, BarcodeFormatNames.GetDisplayName(result.Format), result.Text))
                .ToArray());
        _resultsDataGrid.Show();
    }

    private void ClearResults()
    {
        _resultsDataGrid.WithRows();
        _resultsDataGrid.Hide();
    }

    private void ShowGenerationFailure(BarcodeGenerationFailureReason reason, string? underlyingMessage, BarcodeFormat format)
        => ShowMessage(
            BarcodeEncoderDecoder.GenerationFailedTitle,
            BarcodeGenerationMessages.GetDescription(reason, underlyingMessage, format),
            isWarning: false);

    private void ShowMessage(string title, string description, bool isWarning)
    {
        _messageInfoBar.Title(title).Description(description);
        if (isWarning)
        {
            _messageInfoBar.Warning();
        }
        else
        {
            _messageInfoBar.Error();
        }

        _messageInfoBar.Show().Open();
    }

    private void ShowEncodedValue(string encodedText)
    {
        _encodedValueInfoBar
            .Title(BarcodeEncoderDecoder.EncodedValueTitle)
            .Description(string.Format(BarcodeEncoderDecoder.EncodedValueDescription, encodedText))
            .Show()
            .Open();
    }

    private void ClearMessages()
    {
        _messageInfoBar.Close().Hide();
        _encodedValueInfoBar.Close().Hide();
    }
}
