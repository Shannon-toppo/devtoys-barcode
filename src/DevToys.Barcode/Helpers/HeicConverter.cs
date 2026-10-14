using System.ComponentModel;
using System.Diagnostics;

namespace DevToys.Barcode.Helpers;

/// <summary>The outcome of <see cref="HeicConverter.ConvertToPngAsync"/>: the PNG file, or why there is none.</summary>
internal sealed record HeicConversionResult(FileInfo? PngFile, string? Failure = null, Exception? Exception = null)
{
    internal bool IsSuccess => PngFile is not null;
}

/// <summary>
/// Converts HEIC and HEIF pictures (iPhone photos) to PNG with the system tool <c>sips</c>, because ImageSharp cannot
/// read them. Only macOS has that tool, and it is the only platform where such pictures are accepted.
/// </summary>
internal sealed class HeicConverter
{
    private const string SipsPath = "/usr/bin/sips";
    private const int MaxFailureLength = 500;

    private static readonly string[] HeicExtensions = [".heic", ".heif"];

    private readonly IFileStorage _fileStorage;

    internal HeicConverter(IFileStorage fileStorage)
    {
        _fileStorage = fileStorage;
    }

    /// <summary>Whether pictures can be converted. The one place where the platform is checked.</summary>
    internal bool IsSupported { get; set; } = OperatingSystem.IsMacOS();

    // For unit tests, which replace the tool by a script and shorten the time allowed.
    internal string ExecutablePath { get; set; } = SipsPath;

    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The extensions the file selector accepts: the host's image formats, plus HEIC and HEIF where they can be read.</summary>
    internal string[] GetAcceptedExtensions()
        => IsSupported
            ? [.. PredefinedSupportedImageFormats.ImageFileExtensions, .. HeicExtensions]
            : PredefinedSupportedImageFormats.ImageFileExtensions;

    /// <summary>Whether a file of this name has to be converted before it can be read.</summary>
    internal bool NeedsConversion(string fileName)
        => IsSupported && HasHeicExtension(fileName);

    /// <summary>Whether a file of this name is a HEIC or HEIF picture, whether or not this platform can read it.</summary>
    internal static bool HasHeicExtension(string fileName)
        => HeicExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Converts the picture in <paramref name="source"/> to a PNG in a self-destroying temporary file. A failure is
    /// returned, not thrown. Cancelling stops the tool, and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    internal async Task<HeicConversionResult> ConvertToPngAsync(Stream source, CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return new HeicConversionResult(null, "HEIC and HEIF pictures can only be read on macOS.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        FileInfo? input = null;
        FileInfo? output = null;
        bool succeeded = false;

        try
        {
            // The picked file is a stream and not necessarily a path the tool can open: give it a file.
            input = _fileStorage.CreateSelfDestroyingTempFile("heic");
            output = _fileStorage.CreateSelfDestroyingTempFile("png");

            await using (FileStream inputStream = _fileStorage.OpenWriteFile(input.FullName, replaceIfExist: true))
            {
                await source.CopyToAsync(inputStream, cancellationToken);
            }

            // The paths are absolute, so none can be taken for an option.
            string[] arguments = ["-s", "format", "png", input.FullName, "--out", output.FullName];
            (int exitCode, string text, bool timedOut) = await RunAsync(arguments, cancellationToken);

            output.Refresh();
            if (timedOut)
            {
                return new HeicConversionResult(null, $"sips did not finish within {Timeout.TotalSeconds:0} seconds and was stopped.");
            }

            if (exitCode != 0)
            {
                return new HeicConversionResult(null, $"sips exited with code {exitCode}. {text}");
            }

            if (!output.Exists || output.Length == 0)
            {
                return new HeicConversionResult(null, $"sips did not write a picture. {text}");
            }

            succeeded = true;
            return new HeicConversionResult(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HeicConversionResult(null, $"Unable to convert the picture: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(input);
            if (!succeeded)
            {
                TryDelete(output);
            }
        }
    }

    private async Task<(int ExitCode, string Text, bool TimedOut)> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start(); // Throws Win32Exception when the tool is missing.

        try
        {
            process.StandardInput.Close();

            // Both outputs are read to the end while the tool runs, so that it can never block on a full pipe.
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            try
            {
                await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), standardOutput, standardError).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await KillAsync(process);
                return (-1, string.Empty, TimedOut: true);
            }

            string text = (await standardError + " " + await standardOutput).Trim();
            if (text.Length > MaxFailureLength)
            {
                text = text[..MaxFailureLength];
            }

            return (process.ExitCode, text, TimedOut: false);
        }
        finally
        {
            // Also reached when the user cancelled: the tool must not be left running.
            await KillAsync(process);
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            // It has already exited, or it cannot be stopped: nothing more to do.
        }
    }

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
