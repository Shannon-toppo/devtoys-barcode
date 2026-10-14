using DevToys.Api;

namespace DevToys.Barcode.Tests.Mocks;

/// <summary>An <see cref="IFileStorage"/> that works on the local disk, in a temporary folder.</summary>
internal sealed class MockFileStorage : IFileStorage, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "devtoys-barcode-tests-" + Guid.NewGuid().ToString("N"));

    public MockFileStorage()
    {
        Directory.CreateDirectory(_directory);
    }

    public string AppCacheDirectory => _directory;

    public bool FileExists(string relativeOrAbsoluteFilePath) => File.Exists(relativeOrAbsoluteFilePath);

    public FileStream OpenReadFile(string relativeOrAbsoluteFilePath)
        => new(relativeOrAbsoluteFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public FileStream OpenWriteFile(string relativeOrAbsoluteFilePath, bool replaceIfExist)
        => new(relativeOrAbsoluteFilePath, replaceIfExist ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);

    /// <summary>
    /// What the user types when asked for a file to save, in the command line: a path, or null for nothing. When it is
    /// not set, asking is not expected and fails the test.
    /// </summary>
    internal Func<string?>? SaveFileAnswer { get; set; }

    /// <summary>The file types of the last request to save a file, or null when there was none.</summary>
    internal string[]? RequestedSaveFileTypes { get; private set; }

    public ValueTask<FileStream?> PickSaveFileAsync(params string[] fileTypes)
    {
        RequestedSaveFileTypes = fileTypes;
        if (SaveFileAnswer is null)
        {
            throw new NotSupportedException();
        }

        string? path = SaveFileAnswer();
        return ValueTask.FromResult<FileStream?>(path is null ? null : File.OpenWrite(path));
    }

    public ValueTask<SandboxedFileReader> PickOpenFileAsync(params string[] fileTypes) => throw new NotSupportedException();

    public ValueTask<SandboxedFileReader[]> PickOpenFilesAsync(params string[] fileTypes) => throw new NotSupportedException();

    public ValueTask<string> PickFolderAsync() => throw new NotSupportedException();

    public FileInfo CreateSelfDestroyingTempFile(string? desiredFileExtension = null)
        => CreateFile(desiredFileExtension);

    /// <summary>Creates an empty file in the temporary folder.</summary>
    internal FileInfo CreateFile(string? extension)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + (string.IsNullOrEmpty(extension) ? string.Empty : "." + extension.TrimStart('.')));
        File.Create(path).Dispose();
        return new FileInfo(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
