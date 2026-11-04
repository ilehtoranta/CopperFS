using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using CopperSFS.Check;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class SfsImageCheckerTests
{
    [Fact]
    public void ReadsCompleteManifestWithoutChangingImage()
    {
        var image = Image(); var before = image.ToArray();
        var report = ReliabilityFixture.Inspect(image);
        Assert.Equal(186u, report.FreeBlocks);
        Assert.Equal(new[] { "/", "/Docs", "/Docs/Nested", "/Hello" }, report.Entries.Select(e => e.Path));
        var file = Assert.Single(report.Entries, e => e.Path == "/Hello");
        Assert.Equal(1300u, file.Size); Assert.Equal("greeting", file.Comment);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)).ToArray())), file.Sha256);
        Assert.Equal(before, image);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("secondary-root")]
    [InlineData("free-live-block")]
    [InlineData("leaked-block")]
    [InlineData("free-count")]
    [InlineData("shared-data")]
    [InlineData("orphan-node")]
    [InlineData("orphan-extent")]
    [InlineData("extent-cycle")]
    [InlineData("missing-extent-owner")]
    [InlineData("wrong-extent-owner")]
    [InlineData("directory-cycle")]
    [InlineData("directory-parent")]
    [InlineData("node-pointer")]
    [InlineData("admin-bit")]
    [InlineData("admin-orphan")]
    [InlineData("admin-overlap")]
    [InlineData("admin-size")]
    [InlineData("hash-omission")]
    [InlineData("unterminated-name")]
    [InlineData("outside-partition")]
    [InlineData("dirty-journal")]
    [InlineData("unsupported-link")]
    public void RejectsDeliberateStructuralDamageEvenWhenChecksumIsRepaired(string fault)
    {
        var image = Image();
        var changed = fault switch
        {
            "checksum" or "directory-cycle" or "directory-parent" or "unterminated-name" or "unsupported-link" => 7,
            "secondary-root" => Blocks - 1,
            "free-live-block" or "leaked-block" => 9,
            "free-count" => 2,
            "orphan-node" or "node-pointer" => 6,
            "admin-bit" or "admin-orphan" or "admin-overlap" or "admin-size" => 10,
            "hash-omission" => 3,
            "dirty-journal" => 4,
            _ => 5
        };
        var block = Block(image, changed);
        switch (fault)
        {
            case "checksum": block[60] ^= 1; break;
            case "secondary-root": U32(block, 48, 123); break;
            case "free-live-block": block[12 + 40 / 8] |= 0x80; break;
            case "leaked-block": block[12 + 50 / 8] &= (byte)(0xFF ^ (0x80 >> (50 % 8))); break;
            case "free-count": U32(block, Bytes - 28, 999); break;
            case "shared-data": U32(block, 20, 44); U32(block, 52, 40); break;
            case "orphan-node": U32(block, 60, 7); break;
            case "orphan-extent": U16(block, 12, 4); U32(block, 58, 50); U16(block, 70, 1); break;
            case "extent-cycle": U32(block, 20, 40); break;
            case "missing-extent-owner": U32(block, 24, 0); break;
            case "wrong-extent-owner": U32(block, 24, 0x80000004); break;
            case "directory-cycle": U32(block, 16, 7); break;
            case "directory-parent": U32(block, 12, 99); break;
            case "node-pointer": U32(block, 40, 8); break;
            case "admin-bit": U32(block, 28, U32(block, 28) & ~(0x80000000u >> 6)); break;
            case "admin-orphan": U32(block, 28, U32(block, 28) | 0x80000000u); break;
            case "admin-overlap": U32(block, 32, 1); break;
            case "admin-size": block[20] = 8; break;
            case "hash-omission": block[16..].Clear(); break;
            case "unterminated-name": block[49..].Fill(65); break;
            case "outside-partition": U32(block, 20, Blocks + 1); break;
            case "dirty-journal": U32(block, 0, 0x54524641); break;
            case "unsupported-link": block[80] |= 64; break;
        }
        if (fault != "checksum") Seal(block);
        Assert.Throws<InvalidDataException>(() => ReliabilityFixture.Inspect(image));
    }

    [Fact]
    public void FileByteDamageChangesManifestEvenWithUnchangedMetadata()
    {
        var image = Image(); var before = ReliabilityFixture.Inspect(image);
        Block(image, 40)[19] ^= 0x5A;
        var after = ReliabilityFixture.Inspect(image);
        Assert.NotEqual(before.Entries.Single(e => e.Path == "/Hello").Sha256,
            after.Entries.Single(e => e.Path == "/Hello").Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandLineReportsCleanImagesAndRejectsDirtyImagesWithoutWriting(bool dirty)
    {
        var image = Image();
        if (dirty) { U32(Block(image, 4), 0, 0x54524641); Seal(Block(image, 4)); }
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, image);
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(SfsImageChecker).Assembly.Location);
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(dirty ? 1 : 0, process.ExitCode);
            if (dirty) { Assert.Contains("TROK", stderr); Assert.Empty(stdout); }
            else
            {
                var report = JsonSerializer.Deserialize<ImageReport>(stdout)!;
                Assert.Equal(4, report.Entries.Count); Assert.Empty(stderr);
            }
            Assert.Equal(image, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}

