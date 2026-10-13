using DevToys.Api;
using DevToys.Barcode.Helpers;
using DevToys.Barcode.Tests.Mocks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace DevToys.Barcode.Tests;

public sealed class HeicConverterTests : IDisposable
{
    private readonly MockFileStorage _fileStorage = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _fileStorage.Dispose();
    }

    // ---------------------------------------------------------------- accepted extensions and platform

    [Fact]
    public void WhereHeicIsSupported_TheAcceptedExtensions_AreTheHostsImageListPlusHeicAndHeif()
    {
        var converter = new HeicConverter(_fileStorage) { IsSupported = true };

        Assert.Equal(
            [.. PredefinedSupportedImageFormats.ImageFileExtensions, ".heic", ".heif"],
            converter.GetAcceptedExtensions());
        Assert.True(converter.NeedsConversion("IMG_0001.HEIC"));
        Assert.True(converter.NeedsConversion("photo.heif"));
        Assert.False(converter.NeedsConversion("photo.png"));
        Assert.False(converter.NeedsConversion("heic"));
    }

    [Fact]
    public void WhereHeicIsNotSupported_TheAcceptedExtensions_AreExactlyTheHostsImageList()
    {
        var converter = new HeicConverter(_fileStorage) { IsSupported = false };

        Assert.Equal(PredefinedSupportedImageFormats.ImageFileExtensions, converter.GetAcceptedExtensions());
        Assert.DoesNotContain(converter.GetAcceptedExtensions(), extension => extension.Trim('.').Equals("heic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(converter.GetAcceptedExtensions(), extension => extension.Trim('.').Equals("heif", StringComparison.OrdinalIgnoreCase));
        Assert.False(converter.NeedsConversion("IMG_0001.heic"));
    }

    [Fact]
    public void ByDefault_HeicIsSupportedOnMacOsOnly()
        => Assert.Equal(OperatingSystem.IsMacOS(), new HeicConverter(_fileStorage).IsSupported);

    [Fact]
    public async Task OnAnUnsupportedPlatform_ConvertingFails_WithoutStartingAnything()
    {
        var converter = new HeicConverter(_fileStorage) { IsSupported = false, ExecutablePath = "/nonexistent/sips" };

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1, 2, 3]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Exception);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    // ---------------------------------------------------------------- the real sips

    [MacOsFact]
    public async Task ARealHeicPicture_IsConvertedToAReadablePng_AndTheTemporaryHeicFileIsRemoved()
    {
        using Stream heic = File.OpenRead(CreateHeicFile(BarcodeFormat.EAN_13, "4006381333931"));
        var converter = new HeicConverter(_fileStorage);

        HeicConversionResult result = await converter.ConvertToPngAsync(heic, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal(".png", result.PngFile!.Extension);
        Assert.True(result.PngFile.Length > 0);
        Assert.Equal([result.PngFile.FullName], Directory.GetFiles(_fileStorage.AppCacheDirectory));

        using Image<Rgba32> converted = Image.Load<Rgba32>(result.PngFile.FullName);
        BarcodeReadResult read = Assert.Single(BarcodeHelper.Read(converted));
        Assert.Equal(BarcodeFormat.EAN_13, read.Format);
        Assert.Equal("4006381333931", read.Text);
    }

    [MacOsFact]
    public async Task AFileThatIsNotHeic_FailsWithTheExitCode_LeavesNoFileAndNoProcess()
    {
        var converter = new HeicConverter(_fileStorage);

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream("this is not a picture"u8.ToArray()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("sips", result.Failure);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
        Assert.False(HeicTestSupport.AnyProcessMentions(_fileStorage.AppCacheDirectory));
    }

    [MacOsFact]
    public async Task AnEmptyStream_Fails_WithoutThrowing()
    {
        var converter = new HeicConverter(_fileStorage);

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    // ---------------------------------------------------------------- failure paths, with a script instead of sips

    [UnixFact]
    public async Task ASipsThatIsMissing_IsAFailure_NotAnException()
    {
        HeicConverter converter = CreateFakeConverter(new FakeSips("exit 0"));
        converter.ExecutablePath = "/nonexistent/folder/sips";

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Exception);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    [UnixFact]
    public async Task ANonZeroExitCode_IsAFailure_AndTheMessageOfTheToolIsKept()
    {
        HeicConverter converter = CreateFakeConverter(new FakeSips("echo 'Error 13: not a HEIC' >&2\nexit 13"));

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("13", result.Failure);
        Assert.Contains("not a HEIC", result.Failure);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    [UnixFact]
    public async Task ExitCodeZeroWithAnEmptyOutputFile_IsAFailure()
    {
        HeicConverter converter = CreateFakeConverter(new FakeSips("exit 0"));

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    [UnixFact]
    public async Task ExitCodeZeroWithNoOutputFile_IsAFailure()
    {
        HeicConverter converter = CreateFakeConverter(new FakeSips("rm -f \"$6\"\nexit 0"));

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [UnixFact]
    public async Task ATool_ThatWritesALotToItsOutputs_DoesNotBlock()
    {
        // More than a pipe holds, on both outputs: the tool would wait for ever if nobody read them.
        HeicConverter converter = CreateFakeConverter(new FakeSips(
            "head -c 1000000 /dev/zero | tr '\\0' x\nhead -c 1000000 /dev/zero | tr '\\0' y >&2\necho png > \"$6\""));

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure);
    }

    [UnixFact]
    public async Task TheArgumentsArePassedOneByOne_WithAbsolutePaths_AndTheInputHoldsTheSourceContent()
    {
        using var fake = new FakeSips("printf '%s\\n' \"$@\" > \"$(dirname \"$0\")/data\"\ncp \"$4\" \"$(dirname \"$0\")/input-copy\"\necho png > \"$6\"");
        HeicConverter converter = CreateFakeConverter(fake);

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1, 2, 3, 4]), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure);
        string[] arguments = File.ReadAllLines(fake.DataFile);
        Assert.Equal(6, arguments.Length);
        Assert.Equal(["-s", "format", "png"], arguments[..3]);
        Assert.True(Path.IsPathRooted(arguments[3]) && arguments[3].EndsWith(".heic", StringComparison.Ordinal));
        Assert.Equal("--out", arguments[4]);
        Assert.Equal(result.PngFile!.FullName, arguments[5]);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fake.ScriptPath)!, "input-copy")));
        Assert.False(File.Exists(arguments[3])); // the temporary input is removed
    }

    [UnixFact]
    public async Task AToolThatRunsTooLong_IsKilledAfterTheTimeout_AndIsAFailure()
    {
        FakeSips fake = FakeSips.Sleeping();
        HeicConverter converter = CreateFakeConverter(fake);
        converter.Timeout = TimeSpan.FromMilliseconds(600);

        HeicConversionResult result = await converter.ConvertToPngAsync(new MemoryStream([1]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("did not finish", result.Failure);
        int processId = await fake.WaitForProcessIdAsync();
        Assert.False(HeicTestSupport.IsAlive(processId));
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    [UnixFact]
    public async Task Cancelling_KillsTheTool_ThrowsOperationCanceled_AndLeavesNoFile()
    {
        FakeSips fake = FakeSips.Sleeping();
        HeicConverter converter = CreateFakeConverter(fake);
        using var cancellation = new CancellationTokenSource();

        Task<HeicConversionResult> conversion = converter.ConvertToPngAsync(new MemoryStream([1]), cancellation.Token);
        int processId = await fake.WaitForProcessIdAsync();
        Assert.True(HeicTestSupport.IsAlive(processId));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion);
        Assert.False(HeicTestSupport.IsAlive(processId));
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    [UnixFact]
    public async Task AToken_CancelledBeforeTheStart_StartsNothing()
    {
        FakeSips fake = FakeSips.Sleeping();
        HeicConverter converter = CreateFakeConverter(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertToPngAsync(new MemoryStream([1]), new CancellationToken(canceled: true)));

        Assert.False(File.Exists(fake.DataFile));
        Assert.Empty(Directory.GetFiles(_fileStorage.AppCacheDirectory));
    }

    // ---------------------------------------------------------------- helpers

    private readonly string _heicPath = Path.Combine(Path.GetTempPath(), "devtoys-barcode-source-" + Guid.NewGuid().ToString("N") + ".heic");

    /// <summary>Makes a HEIC picture of a barcode with sips, from a PNG made by the helper. The folder of the temporary files is left clean.</summary>
    private string CreateHeicFile(BarcodeFormat format, string text)
    {
        string png = Path.ChangeExtension(_heicPath, ".png");
        using (Image<Rgba32> image = TestImages.WithBorder(TestImages.Generate(format, text)))
        {
            image.SaveAsPng(png);
        }

        HeicTestSupport.ConvertPngToHeic(png, _heicPath);
        File.Delete(png);
        _disposables.Add(new DeleteOnDispose(_heicPath));
        return _heicPath;
    }

    private HeicConverter CreateFakeConverter(FakeSips fake)
    {
        _disposables.Add(fake);
        return new HeicConverter(_fileStorage) { IsSupported = true, ExecutablePath = fake.ScriptPath, Timeout = TimeSpan.FromSeconds(20) };
    }

    private sealed class DeleteOnDispose(string path) : IDisposable
    {
        public void Dispose() => File.Delete(path);
    }
}
