using DevToys.Api;

namespace DevToys.Barcode.Tests.Mocks;

internal sealed class MockSandboxedFileReader : SandboxedFileReader
{
    private readonly FileInfo _file;
    private readonly IFileStorage _fileStorage;

    internal MockSandboxedFileReader(FileInfo file, IFileStorage fileStorage)
        : base(file.Name)
    {
        _file = file;
        _fileStorage = fileStorage;
    }

    protected override ValueTask<Stream> OpenReadFileAsync(CancellationToken cancellationToken)
        => new(_fileStorage.OpenReadFile(_file.FullName));
}
