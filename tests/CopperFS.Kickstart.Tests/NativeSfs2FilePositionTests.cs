using System.Buffers.Binary;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class Sfs2PositionFactAttribute : FactAttribute
{
    public Sfs2PositionFactAttribute()
    {
        foreach (var name in new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERFS_SFS2_TRST_HANDLER" })
            if (!File.Exists(Environment.GetEnvironmentVariable(name))) Skip = $"Supply {name} for native SFS2 file-position tests.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    private sealed class JournalTrace : WritableHook
    {
        public bool SawRedo, SawUndo;
        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            for (var at = 0; at + 512 <= source.Length; at += 512)
            {
                var id = BinaryPrimitives.ReadUInt32BigEndian(source[at..]);
                SawRedo |= id == 0x54525354;
                SawUndo |= id == 0x4353554E;
            }
            base.Write(backing, offset, source);
        }
    }

    [Sfs2PositionFact]
    public async Task NativeSfs2FilePositionAfterTrstExhaustion()
    {
        var directory = Directory.CreateTempSubdirectory("CopperFS-Arena-").FullName;
        try
        {
            var handler = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
            var trace = new JournalTrace();
            var image = await Run(directory, "sfs2-trst", "arena", handler, ReadMedia.Sfs2(), true,
                "Sfs2FilePositionProbe::TestArena", storage: trace);
            Assert.True(trace.SawRedo);
            Assert.False(trace.SawUndo);
            var report = SfsImageChecker.Check(new MemoryStream(image));
            Assert.Equal(Hash(Enumerable.Range(0, 1041).Select(i => (byte)(i * 17 + 3)).ToArray()),
                report.Entries.Single(e => e.Path == "/NativeArena").Sha256);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Sfs2PositionFact]
    public async Task NativeSfs2FilePositionAcrossFourGiB()
    {
        var directory = Directory.CreateTempSubdirectory("CopperFS-Wide-").FullName;
        try
        {
            var handler = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
            var path = Path.Combine(directory, "wide.hdf");
            NativeWideFixture.Create(path);
            await BootMedia(directory, "sfs2-trst", "wide", handler, path, true, "Sfs2FilePositionProbe::TestWide");
            NativeWideFixture.Verify(path);
            var hook = new WritableHook();
            await BootMedia(directory, "sfs2-trst", "reopen", handler, path, false, "Sfs2FilePositionProbe::ReopenWide", storage: hook);
            Assert.Equal(0, hook.Writes);
            NativeWideFixture.Verify(path);
        }
        finally { Directory.Delete(directory, true); }
    }
}
