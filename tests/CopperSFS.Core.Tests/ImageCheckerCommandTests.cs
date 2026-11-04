using System.Diagnostics;
using System.Text.Json;
using CopperSFS.Bench;
using CopperSFS.Check;

namespace CopperSFS.Tests;

public sealed class ImageCheckerCommandTests
{
    private static async Task<(int Exit, string Output)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(SfsImageChecker).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    [Fact]
    public async Task ChecksValidImageRejectsCorruptionAndNeverWritesTheInput()
    {
        var path = Path.Combine(Path.GetTempPath(), "CopperFS-check-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var image = StudyFixture.Empty(512, 128 * 1024);
            await File.WriteAllBytesAsync(path, image);
            var result = await Run(path);
            Assert.Equal(0, result.Exit);
            using var report = JsonDocument.Parse(result.Output);
            Assert.True(report.RootElement.GetProperty("FreeBlocks").GetUInt32() > 0);
            Assert.Equal(image, await File.ReadAllBytesAsync(path));
            image[0] ^= 255;
            await File.WriteAllBytesAsync(path, image);
            result = await Run(path);
            Assert.Equal(1, result.Exit);
            Assert.Equal(image, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingArgumentsReturnUsageError()
    {
        var result = await Run();
        Assert.Equal(2, result.Exit);
        Assert.Contains("Usage:", result.Output);
    }
}
