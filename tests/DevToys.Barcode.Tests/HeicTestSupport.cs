using System.Diagnostics;

namespace DevToys.Barcode.Tests;

/// <summary>A test that needs macOS and its <c>sips</c> tool. xUnit 2 has no dynamic skip, so it is skipped when discovered.</summary>
internal sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Needs macOS: HEIC pictures are converted with /usr/bin/sips.";
        }
    }
}

/// <summary>A test that replaces <c>sips</c> by a shell script: it runs on macOS and Linux, not on Windows.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs /bin/sh.";
        }
    }
}

/// <summary>Helpers that make HEIC files with <c>sips</c> and look at the processes of the machine.</summary>
internal static class HeicTestSupport
{
    /// <summary>Writes a HEIC file converted from <paramref name="pngPath"/>.</summary>
    internal static void ConvertPngToHeic(string pngPath, string heicPath)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-s", "format", "heic", pngPath, "--out", heicPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && new FileInfo(heicPath).Length > 0, $"sips could not make a HEIC file: {error}");
    }

    /// <summary>Whether any process of the machine has <paramref name="text"/> in its command line.</summary>
    internal static bool AnyProcessMentions(string text)
        => RunAndRead("/bin/ps", "-axww", "-o", "command=").Contains(text, StringComparison.Ordinal);

    internal static bool IsAlive(int processId)
        => RunAndRead("/bin/ps", "-p", processId.ToString(), "-o", "pid=").Trim().Length > 0;

    private static string RunAndRead(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

/// <summary>A shell script standing in for <c>sips</c>, in a folder of its own that is deleted with it.</summary>
internal sealed class FakeSips : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "devtoys-barcode-fake-sips-" + Guid.NewGuid().ToString("N"));

    /// <param name="body">
    /// The commands of the script. Its arguments are those of the real tool: $4 is the input and $6 the output file.
    /// </param>
    internal FakeSips(string body)
    {
        Directory.CreateDirectory(_directory);
        ScriptPath = Path.Combine(_directory, "sips");
        File.WriteAllText(ScriptPath, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal string ScriptPath { get; }

    /// <summary>The file a script can write its process id or its arguments to.</summary>
    internal string DataFile => Path.Combine(_directory, "data");

    /// <summary>A script that records its process id, then waits for a long time.</summary>
    internal static FakeSips Sleeping()
    {
        // The directory is not known before the script exists, so the data file is found from the script's own path.
        return new FakeSips("echo $$ > \"$(dirname \"$0\")/data\"\nexec sleep 60");
    }

    /// <summary>Waits until the script has recorded its process id, and returns it.</summary>
    internal async Task<int> WaitForProcessIdAsync()
    {
        for (int i = 0; i < 200; i++)
        {
            if (File.Exists(DataFile) && int.TryParse(File.ReadAllText(DataFile).Trim(), out int id))
            {
                return id;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The fake sips did not start.");
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
