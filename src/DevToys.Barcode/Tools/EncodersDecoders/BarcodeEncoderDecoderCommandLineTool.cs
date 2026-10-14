using DevToys.Barcode.Helpers;
using Microsoft.Extensions.Logging;
using OneOf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace DevToys.Barcode.Tools.EncodersDecoders;

/// <summary>
/// The <c>barcode</c> command of the DevToys CLI. It reads the barcodes of an image, or generates a barcode from a text,
/// with the same helpers as the GUI tool. It follows the built-in <c>qrcode</c> command: <c>--input</c> is a file or a
/// text, <c>--outputFile</c> is where the result goes, messages go to the standard error, and every failure exits with -1.
/// </summary>
[Export(typeof(ICommandLineTool))]
[Name("BarcodeEncoderDecoder")]
[CommandName(
    Name = "barcode",
    ResourceManagerBaseName = "DevToys.Barcode.Tools.EncodersDecoders.BarcodeEncoderDecoder",
    DescriptionResourceName = nameof(BarcodeEncoderDecoder.CommandLineDescription))]
internal sealed class BarcodeEncoderDecoderCommandLineTool : ICommandLineTool
{
    /// <summary>The exit code of a success.</summary>
    internal const int Success = 0;

    /// <summary>The exit code of every failure, as in the built-in commands.</summary>
    internal const int Failure = -1;

    private const string SvgExtension = ".svg";
    private const char TypeSeparator = '\t';

    private HeicConverter? _heicConverter;

#pragma warning disable IDE0044 // Add readonly modifier: MEF sets it, and the unit tests too.
    [Import]
    internal IFileStorage _fileStorage = null!;
#pragma warning restore IDE0044

    [CommandLineOption(
        Name = "input",
        Alias = "i",
        IsRequired = true,
        DescriptionResourceName = nameof(BarcodeEncoderDecoder.InputOptionDescription))]
    internal OneOf<FileInfo, string>? Input { get; set; }

    [CommandLineOption(
        Name = "outputFile",
        Alias = "o",
        DescriptionResourceName = nameof(BarcodeEncoderDecoder.OutputFileOptionDescription))]
    internal FileInfo? OutputFile { get; set; }

    // A string and not an enum, so that the accepted names are the ones of BarcodeHelper.GenerationFormats (see BarcodeTypeNames)
    // and a wrong one is answered with the list of the right ones. The default is shown in the help of the command.
    [CommandLineOption(
        Name = "type",
        Alias = "t",
        DescriptionResourceName = nameof(BarcodeEncoderDecoder.TypeOptionDescription))]
    internal string? Type { get; set; } = BarcodeTypeNames.GetName(BarcodeHelper.GenerationFormats[0]);

    [CommandLineOption(
        Name = "showType",
        DescriptionResourceName = nameof(BarcodeEncoderDecoder.ShowTypeOptionDescription))]
    internal bool ShowType { get; set; }

    /// <summary>For unit tests, which replace the platform and the tool of the converter.</summary>
    internal HeicConverter HeicConverter
    {
        get => _heicConverter ??= new HeicConverter(_fileStorage);
        set => _heicConverter = value;
    }

    /// <summary>The extensions of the files a barcode can be written to: SVG, and the pictures ImageSharp can encode.</summary>
    internal static IReadOnlyList<string> OutputExtensions { get; } = [SvgExtension, .. PredefinedSupportedImageFormats.ImageFileExtensions];

    public async ValueTask<int> InvokeAsync(ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            if (!Input.HasValue)
            {
                WriteError(BarcodeEncoderDecoder.EmptyInputMessage);
                return Failure;
            }

            if (Input.Value.IsT0)
            {
                FileInfo file = Input.Value.AsT0;
                if (!file.Exists)
                {
                    WriteError(string.Format(BarcodeEncoderDecoder.FileNotFoundMessage, file.FullName));
                    return Failure;
                }

                if (IsImageFile(file.Name))
                {
                    return await ReadBarcodesAsync(file, logger, cancellationToken);
                }

                string fileText = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                return await EncodeAsync(fileText, logger, cancellationToken);
            }

            string text = Input.Value.AsT1;
            if (LooksLikeMissingImageFile(text))
            {
                WriteError(string.Format(BarcodeEncoderDecoder.FileNotFoundMessage, Path.GetFullPath(text.Trim())));
                return Failure;
            }

            return await EncodeAsync(text, logger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The barcode command failed.");
            WriteError(string.Format(BarcodeEncoderDecoder.UnexpectedErrorMessage, ex.Message));
            return Failure;
        }
    }

    // ---------------------------------------------------------------- encoding

    private async ValueTask<int> EncodeAsync(string input, ILogger logger, CancellationToken cancellationToken)
    {
        // A trailing line break is almost always an accident of copy and paste or of a text file, and no barcode wants it.
        string text = input.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
        {
            WriteError(BarcodeEncoderDecoder.EmptyInputMessage);
            return Failure;
        }

        if (!BarcodeTypeNames.TryParse(Type ?? BarcodeTypeNames.Names[0], out BarcodeFormat format))
        {
            WriteError(string.Format(BarcodeEncoderDecoder.UnknownTypeMessage, Type, string.Join(", ", BarcodeTypeNames.Names)));
            return Failure;
        }

        if (OutputFile is not null && !IsOutputFileSupported(OutputFile.FullName))
        {
            WriteError(string.Format(BarcodeEncoderDecoder.UnsupportedOutputFileMessage, OutputFile.FullName, string.Join(", ", OutputExtensions)));
            return Failure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The text is checked before the file is asked for, so a wrong text does not leave an empty file behind.
        BarcodeGenerationResult<Image<Rgba32>> generated = Generate(text, format);
        try
        {
            if (!generated.IsSuccess)
            {
                WriteError(BarcodeEncoderDecoder.GenerationFailedTitle);
                WriteError(BarcodeGenerationMessages.GetDescription(generated.Reason.Value, generated.Message, format));
                return Failure;
            }

            return await WriteBarcodeAsync(text, format, generated, logger, cancellationToken);
        }
        finally
        {
            generated.Value?.Dispose();
        }
    }

    private async ValueTask<int> WriteBarcodeAsync(
        string text,
        BarcodeFormat format,
        BarcodeGenerationResult<Image<Rgba32>> generated,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? path = OutputFile?.FullName ?? await AskForOutputFileAsync();
        if (path is null)
        {
            WriteError(string.Format(BarcodeEncoderDecoder.OutputFileRequiredMessage, string.Join(", ", OutputExtensions)));
            return Failure;
        }

        if (!IsOutputFileSupported(path))
        {
            WriteError(string.Format(BarcodeEncoderDecoder.UnsupportedOutputFileMessage, path, string.Join(", ", OutputExtensions)));
            return Failure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (Path.GetExtension(path).Equals(SvgExtension, StringComparison.OrdinalIgnoreCase))
            {
                // The image was only made to check the text: the same text gives an SVG.
                BarcodeGenerationResult<string> svg = BarcodeHelper.GenerateSvg(text, format);
                if (!svg.IsSuccess)
                {
                    WriteError(BarcodeEncoderDecoder.GenerationFailedTitle);
                    WriteError(BarcodeGenerationMessages.GetDescription(svg.Reason.Value, svg.Message, format));
                    return Failure;
                }

                await File.WriteAllTextAsync(path, svg.Value, cancellationToken);
            }
            else
            {
                await generated.Value!.SaveAsync(path, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError(ex, "Unable to write the barcode.");
            WriteError(string.Format(BarcodeEncoderDecoder.OutputFileNotWrittenMessage, path, ex.Message));
            return Failure;
        }

        // The result is a file, and the standard output stays empty. A check digit that was added is worth knowing.
        if (generated.EncodedText != text)
        {
            WriteError(string.Format(BarcodeEncoderDecoder.EncodedValueDescription, generated.EncodedText));
        }

        return Success;
    }

    private static BarcodeGenerationResult<Image<Rgba32>> Generate(string text, BarcodeFormat format)
    {
        try
        {
            return BarcodeHelper.GenerateImage(text, format);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return BarcodeGenerationResult<Image<Rgba32>>.Failure(BarcodeGenerationFailureReason.EncoderRejected, ex.Message);
        }
    }

    /// <summary>Asks for the file in the way the host provides, as the built-in commands do. Null when none is given.</summary>
    private async ValueTask<string?> AskForOutputFileAsync()
    {
        using FileStream? picked = await _fileStorage.PickSaveFileAsync([.. OutputExtensions]);
        return picked?.Name;
    }

    private static bool IsOutputFileSupported(string path)
        => OutputExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- reading

    private async ValueTask<int> ReadBarcodesAsync(FileInfo file, ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<BarcodeReadResult> results;
        FileInfo? converted = null;

        try
        {
            string imagePath = file.FullName;
            if (HeicConverter.NeedsConversion(file.Name))
            {
                using (Stream source = _fileStorage.OpenReadFile(file.FullName))
                {
                    HeicConversionResult conversion = await HeicConverter.ConvertToPngAsync(source, cancellationToken);
                    if (!conversion.IsSuccess)
                    {
                        logger.LogWarning(conversion.Exception, "Unable to convert a HEIC or HEIF picture: {Reason}", conversion.Failure);
                        WriteImageNotReadable();
                        return Failure;
                    }

                    converted = conversion.PngFile;
                    imagePath = converted!.FullName;
                }
            }

            using Stream stream = _fileStorage.OpenReadFile(imagePath);
            results = await BarcodeHelper.ReadAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Not an image, a damaged one, or a file that cannot be opened.
            logger.LogWarning(ex, "Unable to read a picture.");
            WriteImageNotReadable();
            return Failure;
        }
        finally
        {
            TryDelete(converted);
        }

        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("{Count} barcode(s) found.", results.Count);

        if (results.Count == 0)
        {
            WriteError(BarcodeEncoderDecoder.NoBarcodeFound);
            WriteError(BarcodeEncoderDecoder.NoBarcodeFoundDescription);
            return Failure;
        }

        string output = FormatResults(results, ShowType);
        try
        {
            await WriteOutputAsync(output, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError(ex, "Unable to write the result.");
            WriteError(string.Format(BarcodeEncoderDecoder.OutputFileNotWrittenMessage, OutputFile!.FullName, ex.Message));
            return Failure;
        }

        return Success;
    }

    /// <summary>
    /// One line per barcode, in the order of the reader. A single barcode without its type is written exactly as it was
    /// read, line breaks included, so that it can be piped as it is. When there are several lines (several barcodes, or
    /// the types), a line break inside a text would break the lines: it is written as \r and \n.
    /// </summary>
    internal static string FormatResults(IReadOnlyList<BarcodeReadResult> results, bool showType)
    {
        if (results.Count == 1 && !showType)
        {
            return results[0].Text;
        }

        return string.Join(
            Environment.NewLine,
            results.Select(result
                => showType
                ? BarcodeFormatNames.GetDisplayName(result.Format) + TypeSeparator + EscapeLineBreaks(result.Text)
                : EscapeLineBreaks(result.Text)));
    }

    private static string EscapeLineBreaks(string text)
        => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>The standard output, or the file when there is one (replaced if it exists, no final line break).</summary>
    private async Task WriteOutputAsync(string output, CancellationToken cancellationToken)
    {
        if (OutputFile is null)
        {
            Console.Out.WriteLine(output);
        }
        else
        {
            await File.WriteAllTextAsync(OutputFile.FullName, output, cancellationToken);
        }
    }

    // ---------------------------------------------------------------- the input

    private bool IsImageFile(string fileName)
        => HeicConverter.GetAcceptedExtensions().Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
           || HeicConverter.HasHeicExtension(fileName);

    /// <summary>
    /// The host gives a text when the path of a file does not lead to one, so a mistyped path to a picture would be
    /// encoded as a text without a word. A one line text that ends like the name of a picture is taken for a path (unless
    /// it is an address, such as https://example.com/logo.png).
    /// </summary>
    private bool LooksLikeMissingImageFile(string text)
    {
        string path = text.Trim();
        return path.Length is > 0 and <= 1024
            && path.IndexOfAny(['\r', '\n']) < 0
            && !path.Contains("://", StringComparison.Ordinal)
            && IsImageFile(path)
            && !File.Exists(path);
    }

    // ---------------------------------------------------------------- messages

    private static void WriteImageNotReadable()
    {
        WriteError(BarcodeEncoderDecoder.ImageNotReadableTitle);
        WriteError(BarcodeEncoderDecoder.ImageNotReadableDescription);
    }

    private static void WriteError(string message)
        => Console.Error.WriteLine(message);

    private static void TryDelete(FileInfo? file)
    {
        try
        {
            file?.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A self-destroying file is removed later anyway.
        }
    }
}
