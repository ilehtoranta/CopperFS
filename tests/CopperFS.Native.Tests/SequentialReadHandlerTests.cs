using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amiga;
using Copper68k;
using CopperStart.Dos;
using CopperStart.Exec.Tests;

namespace CopperFS.Native.Tests;

public sealed class SequentialHandlerTheoryAttribute : TheoryAttribute
{
    public SequentialHandlerTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_SFS2_HANDLER")))
            Skip = "Set COPPERFS_FFS_HANDLER and COPPERFS_SFS2_HANDLER to freshly built handlers with maps.";
    }
}

// Synthetic media is authored here from the maintained format layouts. The
// CopperStart instruction replay bus executes the actual standalone HUNK;
// Exec copy/device calls are service doubles, not full-ROM qualification.
public sealed class SequentialReadHandlerTests
{
    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsSustainedFourKiBReadsDrainWarmupThenReadDirectAndRestoreSmallReadAhead(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream();
        for (var position = 0u; position < 65536; position += 4096) f.Read(position, 4096);
        Assert.Equal(16u, f.Bus.ReadLong(Fixture.File + 104));
        f.Reads.Clear(); f.ReadTargets.Clear();
        f.Read(65536, 4096);
        Assert.Equal(new[] { (1152u, 8u) }, f.Reads);
        Assert.Equal(new[] { Fixture.Buffer }, f.ReadTargets);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 1644));
        f.Reads.Clear(); f.Read(69632, 512);
        Assert.Contains(f.Reads, r => r.Count == 128);
        var reads = f.Reads.Count;
        f.Read(70144, 512); Assert.Equal(reads, f.Reads.Count);
        f.Read(70656, 4096);
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.File + 104));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FfsAdaptiveDirectReadKeepsErrorPrefixAndResetsAfterInvalidation(bool m68020, bool stable)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream();
        var end = stable ? 131072u : 65536u;
        for (var position = 0u; position < end; position += 4096) f.Read(position, 4096);
        f.Bus.WriteLong(Fixture.State + 1628, 2048); f.BadBlock = 1024 + end / 512 + 4;
        f.Read(end, 4096, expected: 2048, error: DOS.Error.DiskNotValidated);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 1644));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 104));
        f.BadBlock = uint.MaxValue; f.Bus.WriteLong(Fixture.State + 1628, 65536);
        f.Invoke("InvalidateDirectoryCursors", Fixture.State);
        f.Read(end, 4096);
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.File + 104));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveHintsAreIndependentAcrossHandlesAndDiscontinuities(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream();
        for (var position = 0u; position < 65536; position += 4096) f.Read(position, 4096);
        f.Read(0, 4096, file: Fixture.Other);
        Assert.Equal(16u, f.Bus.ReadLong(Fixture.File + 104));
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.Other + 104));
        f.Read(4096, 4096);
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.File + 104));
        f.Reads.Clear(); f.Read(8192, 4096);
        Assert.DoesNotContain(f.Reads, r => r.Count == 8);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FfsAdaptiveSamplingRetainsDirectOnlyWhenMeasuredCheaper(bool m68020, bool slow)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(); f.DevicePenaltyTicks = slow ? 50000u : 0u;
        for (var position = 0u; position < 131072; position += 4096) f.Read(position, 4096);
        Assert.Equal(slow ? 3u : 2u, f.Bus.ReadLong(Fixture.File + 108));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 1644));
        f.Reads.Clear(); f.ReadTargets.Clear(); f.Read(131072, 4096);
        if (!slow) Assert.Equal(new[] { Fixture.Buffer }, f.ReadTargets);
        else Assert.DoesNotContain(f.Reads, r => r.Count == 8);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveUnavailableClockKeepsCachingAndSeekResetsHints(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(clock: false);
        for (var position = 0u; position < 81920; position += 4096) f.Read(position, 4096);
        Assert.Equal(0u, f.ClockCalls);
        Assert.DoesNotContain(f.Reads, read => read.Count == 8);
        f.Bus.WriteLong(0x6008, (uint)DosPacketAction.Seek); f.Bus.WriteLong(0x6014, Fixture.File);
        f.Bus.WriteLong(0x6018, 0); f.Bus.WriteLong(0x601C, uint.MaxValue);
        f.Invoke("Dispatch", Fixture.State, 0x6000);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 104));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveWarmupLatencySpikesAvoidAllDirectProbes(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(); f.DevicePenaltyTicks = 50000;
        for (var position = 0u; position < 98304; position += 4096) f.Read(position, 4096);
        Assert.Equal(3u, f.Bus.ReadLong(Fixture.File + 108));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 128));
        Assert.DoesNotContain(f.ReadTargets, t => t == Fixture.Buffer);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsCachedDecisionSurvivesTableBoundariesWithoutRestartingCostSampling(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(); f.DevicePenaltyTicks = 50000;
        for (var position = 0u; position < 65536; position += 4096) f.Read(position, 4096);
        Assert.Equal(3u, f.Bus.ReadLong(Fixture.File + 108));
        var calls = f.ClockCalls;
        f.DevicePenaltyTicks = 0;
        for (var position = 65536u; position < 262144; position += 4096) f.Read(position, 4096);
        Assert.Equal(calls, f.ClockCalls);
        Assert.Equal(3u, f.Bus.ReadLong(Fixture.File + 108));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 128));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsLearnedDirectReadsStillObserveLogicalRamImages(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream();
        for (var position = 0u; position < 131072; position += 4096) f.Read(position, 4096);
        Assert.Equal(2u, f.Bus.ReadLong(Fixture.File + 108));
        const uint ram = 0xC0000, replacement = 0xD0000;
        f.Bus.Memory.AsSpan((int)ram, 16384).Clear();
        f.Bus.Memory.AsSpan((int)replacement, 4096).Fill(0xD3);
        for (var index = 0u; index < 8; index++) f.Bus.WriteLong(ram + (1280 + index) * 4, replacement + index * 512);
        f.Bus.WriteLong(Fixture.State + 40, ram);
        f.Reads.Clear(); f.Read(131072, 4096, replacement: true);
        Assert.Empty(f.Reads);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 1644));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveAliasFailureDoesNotModifyTheUncompletedOutput(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream();
        for (var position = 0u; position < 131072; position += 4096) f.Read(position, 4096);
        var before = f.Bus.Memory.AsSpan((int)Fixture.File + 100, 116).ToArray();
        f.BadBlock = 1025; f.Bus.WriteLong(Fixture.State + 12, uint.MaxValue);
        f.Bus.WriteLong(Fixture.File + 12, 512);
        f.Bus.WriteLong(0x6008, (uint)DosPacketAction.Read); f.Bus.WriteLong(0x6014, Fixture.File);
        f.Bus.WriteLong(0x6018, Fixture.File + 100); f.Bus.WriteLong(0x601C, 4096);
        f.Invoke("Dispatch", Fixture.State, 0x6000);
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(0x600C));
        Assert.Equal((uint)DOS.Error.DiskNotValidated, f.Bus.ReadLong(0x6010));
        Assert.Equal(before, f.Bus.Memory.AsSpan((int)Fixture.File + 100, 116).ToArray());
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveCostSamplingInitializesAnAlreadySeededCursor(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(); f.Read(0, 512);
        f.Read(0, 4096); f.Read(4096, 4096);
        Assert.InRange(f.Bus.ReadLong(Fixture.File + 144), 1u, uint.MaxValue - 1);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveSeek64KeepsAnOutputAliasingPrivateHints(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream();
        for (var position = 0u; position < 131072; position += 4096) f.Read(position, 4096);
        f.Bus.WriteLong(0x8800, 0); f.Bus.WriteLong(0x8804, 0);
        f.Bus.WriteLong(0x6008, 26400); // Published ACTION_SEEK64 packet.
        f.Bus.WriteLong(0x6014, Fixture.File); f.Bus.WriteLong(0x6018, 0x8800);
        f.Bus.WriteLong(0x601C, uint.MaxValue); f.Bus.WriteLong(0x6020, Fixture.File + 100);
        f.Invoke("Dispatch", Fixture.State, 0x6000);
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(0x600C)); Assert.Equal(0u, f.Bus.ReadLong(0x6010));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 100));
        Assert.Equal(131072u, f.Bus.ReadLong(Fixture.File + 104));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 12));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsAdaptiveDispatcherNeverChangesAnotherFormatsPrivateCursor(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Bus.WriteLong(Fixture.State + 20, 0x53465302);
        f.Bus.WriteLong(Fixture.File + 100, 1234); f.Bus.WriteLong(Fixture.File + 104, 16);
        f.Bus.WriteLong(Fixture.File + 108, 0x12345678);
        f.Bus.WriteLong(0x6014, Fixture.File); f.Bus.WriteLong(0x6018, Fixture.Buffer);
        f.Bus.WriteLong(0x601C, 4096);
        var before = f.Bus.Memory.AsSpan((int)Fixture.File + 100, 52).ToArray();
        Assert.Equal(0u, f.Invoke("TryDispatchCachedFfsRead", Fixture.State, 0x6000));
        Assert.Equal(before, f.Bus.Memory.AsSpan((int)Fixture.File + 100, 52).ToArray());
    }

    [SequentialHandlerTheory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public void FfsReadClockIsConstructedOnlyForNormalFastRamMounts(bool m68020, bool fast, bool bypass)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(clock: false);
        f.Bus.WriteLong(Fixture.State + 1644, bypass ? 3u : 0u);
        const uint exec = 0xE00000;
        var created = 0; var opened = 0; var closed = 0; var deleted = 0;
        uint request = 0;
        // A previous SFS mount may have used these reservation records.
        f.Bus.Memory.AsSpan((int)Fixture.State + 2056, 40).Fill(0xCC);
        f.Bus.WriteLong(Fixture.State + 2096, 0xC0FEBABE);
        f.Bus.RegisterGateway(exec - 534, cpu => cpu.D[0] = fast ? 5u : 3u);
        f.Bus.RegisterGateway(exec - 654, cpu =>
        {
            created++; Assert.Equal(40u, cpu.D[0]); cpu.D[0] = 0x7900;
        });
        f.Bus.RegisterGateway(exec - 444, cpu =>
        {
            opened++; request = cpu.A[1];
            Assert.Equal(Fixture.State + 2056, request);
            Assert.Equal(0u, f.Bus.ReadLong(request));
            Assert.Equal(0u, f.Bus.ReadLong(request + 4));
            Assert.Equal(5, f.Bus.Memory[request + 8]);
            Assert.Equal(0x7800u, f.Bus.ReadLong(request + 14));
            Assert.Equal(40, f.Bus.ReadWord(request + 18));
            Assert.Equal(0u, f.Bus.ReadLong(request + 36));
            Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 2052));
            Assert.Equal(0xC0FEBABEu, f.Bus.ReadLong(Fixture.State + 2096));
            cpu.D[0] = 0;
        });
        f.Bus.RegisterGateway(exec - 450, cpu => { closed++; Assert.Equal(request, cpu.A[1]); });
        f.Bus.RegisterGateway(exec - 660, cpu => deleted++);
        f.Invoke("InitializeWriteTimer", Fixture.State, 0x7800);
        Assert.Equal(0, created);
        Assert.Equal(fast && !bypass ? 1 : 0, opened);
        Assert.Equal(fast && !bypass ? request : 0u, f.Bus.ReadLong(Fixture.State + 2024));
        f.Invoke("ReleaseSmart", Fixture.State);
        Assert.Equal(opened, closed);
        Assert.Equal(0, deleted);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 2024));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsReadClockOpenFailureKeepsCachingWithoutAllocatingOrClosingADevice(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(clock: false);
        const uint exec = 0xE00000;
        f.Bus.RegisterGateway(exec - 534, cpu => cpu.D[0] = 5);
        f.Bus.RegisterGateway(exec - 654, cpu => throw new InvalidOperationException("Unexpected IO allocation"));
        f.Bus.RegisterGateway(exec - 444, cpu => cpu.D[0] = 5);
        f.Bus.RegisterGateway(exec - 450, cpu => throw new InvalidOperationException("Unexpected close after failed open"));
        f.Bus.RegisterGateway(exec - 660, cpu => throw new InvalidOperationException("Unexpected delete of inline request"));
        f.Invoke("InitializeWriteTimer", Fixture.State, 0x7800);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 2024));
        f.Invoke("ReleaseSmart", Fixture.State);
        f.Read(0, 4096); f.Read(4096, 4096);
        Assert.Equal(0u, f.ClockCalls);
        Assert.DoesNotContain(f.ReadTargets, target => target == Fixture.Buffer);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsPartialPacketsRetainReadAheadAcrossSectorBoundariesAndOddDestinations(bool m68020)
    {
        var f=new Fixture(false,m68020,cacheBlocks:128);
        f.Read(0,64); var reads=f.Reads.Count;
        foreach(var length in new[]{1u,3u,17u,63u,64u,65u,127u,255u,511u})
        {
            f.Read(512+length,length);
            f.Read(1024+length,length,alignment:1);
        }
        Assert.Equal(reads,f.Reads.Count);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsPartialRetainedRunClipsEofAndKeepsSeekErrors(bool m68020)
    {
        var f=new Fixture(false,m68020,cacheBlocks:128);
        f.Read(0,64);
        f.Bus.WriteLong(Fixture.File+60,17);
        f.Read(8,64,expected:9);
        f.Read(17,64,expected:0);
        f.Read(18,64,expected:-1,error:DOS.Error.SeekError);
        Assert.Equal(0u,f.Bus.ReadLong(Fixture.File+56));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ScsiBypassRetainsReadAheadForConsecutiveSectorPackets(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020, cacheBlocks: 128, bypass: true);
        f.Bus.WriteLong(Fixture.State + 1644, 3); // Explicit scsi.device policy.
        f.Read(0, 64); var reads=f.Reads.Count;
        for(var position=512u;position<4096;position+=512) f.Read(position,512);
        Assert.Equal(reads,f.Reads.Count);
        f.Reads.Clear(); f.Read(4096,8192);
        Assert.Equal((sfs2 ? 48u : 28u,16u),Assert.Single(f.Reads));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2MetadataDoesNotReadAnUnrelatedFailingNeighbor(bool m68020)
    {
        var f = new Fixture(true,m68020,cacheBlocks:128);
        f.BadBlock=39; // No requested metadata or payload occupies this block.
        f.Read(0,512);
        Assert.NotEqual(0u,f.Bus.ReadLong(Fixture.File+56));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void LargeFfsReadDoesNotPrefetchItsShortPointerTableTails(bool m68020)
    {
        var f=new Fixture(false,m68020,cacheBlocks:128);
        f.Read(0,512); f.Reads.Clear();
        f.Bus.WriteLong(Fixture.State+12,uint.MaxValue);
        f.Read(71*512,8192,expected:9*512);
        Assert.DoesNotContain(f.Reads,r=>r.Count>8);
        Assert.Contains((91u,1u),f.Reads);
        Assert.Contains((100u,8u),f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void PayloadBypassRequiresCopperHdfAndAnExplicitCompleteEnvironment(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020);
        const uint name = 0x7600, environment = 0x7800;
        foreach (var device in new[] { "copperhdf.device", "scsi.device", "uaehf.device", "trackdisk.device", "copperhdf.device.extra" })
        foreach (var tableSize in new[] { 17u, 18u })
        foreach (var control in new[] { 0u, 0x43504301u, 0x43504302u, 0x43504303u })
        {
            Encoding.ASCII.GetBytes(device + "\0").CopyTo(f.Bus.Memory, (int)name);
            f.Bus.WriteLong(environment, tableSize); f.Bus.WriteLong(environment + 72, control);
            f.Bus.WriteLong(Fixture.State + 1644, 1);
            f.Invoke("ConfigurePayloadCache", Fixture.State, name, environment);
            Assert.Equal(tableSize != 18 ? 0u : device == "copperhdf.device" && control == 0x43504301 ? 1u :
                device == "scsi.device" && control == 0x43504302 ? 3u : 0u,
                f.Bus.ReadLong(Fixture.State + 1644));
        }
        f.Invoke("ConfigurePayloadCache", Fixture.State, name);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.State + 1644));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void PayloadBypassReadsWholeBlocksDirectlyButRetainsPartialSectorCaching(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020, cacheBlocks: 128, bypass: true);
        f.Read(0, 64); var reads = f.Reads.Count;
        for (var position = 64u; position < 512; position += 64) f.Read(position, 64);
        Assert.Equal(reads, f.Reads.Count);
        f.Reads.Clear(); f.Read(512, 512);
        Assert.Equal(0x9000u, f.ReadTargets[^1]);
        f.Read(1024, 4096);
        Assert.Equal(0x9000u, f.ReadTargets[^1]);
        Assert.Equal(new[] { (sfs2 ? 41u : 21u, 1u), (sfs2 ? 42u : 22u, 8u) }, f.Reads);
        f.Reads.Clear(); f.Read(71 * 512, 2048);
        Assert.Equal(new[] { (sfs2 ? 111u : 91u, 1u), (sfs2 ? 160u : 100u, 3u) },
            f.Reads.Where(r => r.Block >= (sfs2 ? 40u : 20u)).ToArray());
        f.Read(80 * 512 - 17, 512, expected: 17);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void PayloadBypassRetainsMaxTransferFailurePrefixAndInvalidation(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020, cacheBlocks: 128, bypass: true);
        f.Read(0, 512); f.Bus.WriteLong(Fixture.State + 1628, 1024);
        f.BadBlock = sfs2 ? 44u : 24u;
        f.Read(0, 4096, expected: 2048, error: DOS.Error.DiskNotValidated);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        f.BadBlock = uint.MaxValue; f.Read(0, 4096);
        f.Read(3, 1030, alignment: 1);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void PayloadBypassKeepsSfs2PendingTransactionImagesVisible(bool m68020)
    {
        var f = new Fixture(true, m68020, cacheBlocks: 128, bypass: true);
        f.Read(0, 512);
        const uint batch = 0x80000;
        f.Bus.WriteLong(Fixture.State + 1756, batch);
        f.Bus.WriteLong(batch, 1); f.Bus.WriteLong(batch + 4, 1);
        f.Bus.WriteLong(batch + 8, 40);
        f.Bus.Memory.AsSpan((int)batch + 12, 512).Fill(0xD3);
        f.Reads.Clear(); f.Read(0, 512, replacement: true);
        f.Read(3, 64, alignment: 1, replacement: true);
        Assert.Empty(f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2MountOwnedInputObjectSurvivesGenerationWrapAndConcurrentOpen(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        var cache = f.Bus.ReadLong(Fixture.State + 2032);
        uint first = 0;
        f.OpenForInput("F", whileOpen: file =>
        {
            first = file;
            Assert.Equal(0, f.FileAllocations);
            f.Read(0, 64, file);
            f.Bus.WriteLong(cache, uint.MaxValue);
            f.Invoke("InvalidateDirectoryCursors", Fixture.State);
            Assert.Equal(64u, f.Bus.ReadLong(file + 12));
            Assert.Equal(0u, f.Bus.ReadLong(file + 56));
            f.OpenForInput("F", handle: 0x6700, whileOpen: other =>
            {
                Assert.NotEqual(file, other);
                Assert.Equal(1, f.FileAllocations);
                f.Read(0, 64, other);
                Assert.Equal(64u, f.Bus.ReadLong(file + 12));
                f.Read(64, 32, file);
                Assert.Equal(64u, f.Bus.ReadLong(other + 12));
            });
            f.Read(64, 64, file);
            f.Bus.WriteLong(file + 212, uint.MaxValue);
        });
        Assert.Equal(1, f.FileFrees);
        f.OpenForInput("F", whileOpen: file =>
        {
            Assert.Equal(first, file);
            Assert.Equal(0u, f.Bus.ReadLong(file + 12));
            Assert.Equal(0u, f.Bus.ReadLong(file + 56));
            Assert.Equal(0u, f.Bus.ReadLong(file + 212));
        });
        Assert.Equal(1, f.FileAllocations);
        Assert.Equal(1, f.FileFrees);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2ReopenedFileReadsFromTheVerifiedObjectButStillChecksTheExtent(bool m68020)
    {
        var f = new Fixture(true, m68020, bypass: true);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        f.OpenForInput('F');
        f.OpenForInput("F", whileOpen: file =>
        {
            f.Reads.Clear();
            f.Read(0, 512, file);
            Assert.Equal(new[] { (6u, 1u), (40u, 1u) }, f.Reads);
        });
        f.OpenForInput("F", whileOpen: file =>
        {
            f.ToggleExtentChecksum();
            f.Read(0, 512, file, expected: -1, error: DOS.Error.DiskNotValidated);
        });
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2ReadAfterOpenHonorsStagingAndGenerationChanges(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        f.OpenForInput("F", whileOpen: file =>
        {
            const uint batch = 0x80000;
            f.Bus.WriteLong(Fixture.State + 1756, batch);
            f.Bus.WriteLong(batch, 1); f.Bus.WriteLong(batch + 4, 1); f.Bus.WriteLong(batch + 8, 10);
            f.Read(0, 64, file, expected: -1, error: DOS.Error.DiskNotValidated);
            f.Bus.WriteLong(Fixture.State + 1756, 0);
            f.Invoke("InvalidateDirectoryCursors", Fixture.State);
            f.ChangeFirstBlockAndSize();
            f.Read(0, 64, file, expected: 17, replacement: true);
        });
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2FirstReadChecksObjectTypeAndSizeBeforeReadingPayload(bool m68020)
    {
        foreach (var oversized in new[] { false, true })
        {
            var f = new Fixture(true, m68020);
            f.MakeSfs2SoftLink(oversized);
            f.Read(0, 65536, expected: -1, error: oversized ? DOS.Error.DiskNotValidated : DOS.Error.ObjectWrongType);
            Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
            if (!oversized)
            {
                f.EnableFixedDiskOpens();
                f.RemountSfs2();
                f.OpenForInput('F');
                f.OpenForInput("F", whileOpen: file => f.Read(0, 65536, file, expected: -1, error: DOS.Error.ObjectWrongType));
            }
            Assert.DoesNotContain(f.Reads, read => read.Block is >= 40 and < 112);
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void VerifiedSfs2ObjectYieldsToCorruptStagedReplacement(bool m68020)
    {
        var f = new Fixture(true, m68020, bypass: true);
        const uint first = 0x82000, stride = 532, slots = 16;
        f.Bus.WriteLong(Fixture.State + 44, first);
        f.Bus.WriteLong(Fixture.State + 48, slots);
        f.Bus.WriteLong(Fixture.State + 2016, first + (slots - 1) * stride);
        for (uint i = 0; i < slots; i++)
        {
            var entry = first + i * stride;
            f.Bus.WriteLong(entry, i + 1 == slots ? 0 : entry + stride);
            f.Bus.WriteLong(entry + 4, uint.MaxValue);
            f.Bus.WriteLong(entry + 8, i == 0 ? 0 : entry - stride);
        }
        f.Read(0, 512);
        f.Bus.WriteLong(Fixture.File + 56, 0);
        f.Read(0, 512);
        var objectEntry = f.Bus.ReadLong(Fixture.State + 1760 + 10 * 4);
        Assert.NotEqual(0u, objectEntry);
        Assert.Equal(~0x4F424A43u, f.Bus.ReadLong(objectEntry + 16));

        const uint batch = 0x80000;
        f.Bus.WriteLong(Fixture.State + 1756, batch);
        f.Bus.WriteLong(batch, 1); f.Bus.WriteLong(batch + 4, 1); f.Bus.WriteLong(batch + 8, 10);
        f.Bus.Memory.AsSpan((int)objectEntry + 20, 512).CopyTo(f.Bus.Memory.AsSpan((int)batch + 12));
        f.Bus.Memory[batch + 12 + 100] ^= 1;
        f.Bus.WriteLong(Fixture.File + 56, 0);
        f.Read(0, 512, expected: -1, error: DOS.Error.DiskNotValidated);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void PayloadBypassPreservesFfsMetadataLruAcrossDirectReads(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128, bypass: true);
        f.RetainFfsHeader();
        f.Read(0, 512);
        f.Bus.WriteLong(Fixture.File + 56, 0);
        f.Read(512, 512);
        Assert.Equal(new[] { (20u, 1u), (21u, 1u) }, f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataMissReadsIntoOwnedLruAndDoesNotRetryAFailure(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 1);
        const uint entry = 0x81000;
        f.Bus.WriteLong(Fixture.State + 44, entry);
        f.Bus.WriteLong(Fixture.State + 48, 1);
        f.Bus.WriteLong(Fixture.State + 2016, entry);
        f.Bus.WriteLong(entry + 4, uint.MaxValue);
        f.Read(0, 512);
        var headerRead = f.Reads.FindIndex(request => request == (10u, 1u));
        Assert.True(headerRead >= 0);
        Assert.Equal(entry + 20, f.ReadTargets[headerRead]);
        Assert.Equal(10u, f.Bus.ReadLong(entry + 4));

        f.BadBlock = 11;
        f.Read(72 * 512, 512, expected: -1, error: DOS.Error.DiskNotValidated);
        Assert.Equal(1, f.Reads.Count(request => request == (11u, 1u)));
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(entry + 4));
        f.BadBlock = uint.MaxValue;
        f.Read(72 * 512, 512);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataBorrowDeclinesCallerOutputAliasingItsCacheImage(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 1);
        const uint entry = 0x81000;
        f.Bus.WriteLong(Fixture.State + 44, entry);
        f.Bus.WriteLong(Fixture.State + 48, 1);
        f.Bus.WriteLong(Fixture.State + 2016, entry);
        f.Bus.WriteLong(entry + 4, uint.MaxValue);
        f.ReadIntoPrivateCache(entry);
        var headerRead = f.Reads.FindIndex(request => request == (10u, 1u));
        Assert.True(headerRead >= 0);
        Assert.NotEqual(entry + 20, f.ReadTargets[headerRead]);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void CachedFfsSpansPreserveEverySectorLengthAndCallerGuards(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Read(0, 512);
        var reads = f.Reads.Count;
        for (var length = 1024u; length <= 4096; length += 512)
        {
            f.Read(512, length);
            f.Read(512, length, alignment: 1);
        }
        Assert.Equal(reads, f.Reads.Count);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void CachedFfsRunYieldsToRamImagesAndRejectsAnUnmappedSuffix(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Read(0, 512); var reads = f.Reads.Count;
        const uint table = 0x80000, images = 0x81000;
        f.Bus.WriteLong(Fixture.State + 40, table);
        for (var i = 0u; i < 8; i++)
        {
            f.Bus.WriteLong(table + (21 + i) * 4, images + i * 512);
            f.Bus.Memory.AsSpan((int)(images + i * 512), 512).Fill(0xD3);
        }
        f.Read(512, 4096, replacement: true);
        Assert.Equal(reads, f.Reads.Count);
        f.Bus.WriteLong(Fixture.State + 40, 0);
        f.Bus.WriteLong(0xE20000 + ExecLayout.MemHeader.Upper, Fixture.Buffer + 4095);
        f.Read(512, 4096, expected: -1, error: DOS.Error.DiskNotValidated);
        Assert.Equal(reads, f.Reads.Count);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void CachedFfsRunKeepsTheScratchAliasPath(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Read(0, 512);
        f.ReadIntoScratch();
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void CachedFfsCompletionKeepsPacketAliasesOnTheOriginalPath(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Read(0, 512); f.ReadIntoPacket();
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void CachedFfsCompletionRetainsTheOwnedObjectTraversalBoundary(bool m68020)
    {
        foreach (var preceding in new[] { 4095u, 4096u })
        {
            var f = new Fixture(false, m68020, cacheBlocks: 128); f.Read(0, 512);
            f.PrecedeFileWithLocks(preceding);
            f.Read(0, 1024, expected: preceding == 4095 ? 1024 : -1,
                error: preceding == 4095 ? DOS.Error.None : DOS.Error.InvalidLock);
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false, 64u, false)] [InlineData(true, 64u, false)]
    [InlineData(false, 512u, false)] [InlineData(true, 512u, false)]
    [InlineData(false, 64u, true)] [InlineData(true, 64u, true)]
    [InlineData(false, 512u, true)] [InlineData(true, 512u, true)]
    [InlineData(false, 1024u, false)] [InlineData(true, 1024u, false)]
    [InlineData(false, 4096u, false)] [InlineData(true, 4096u, false)]
    public void FfsCursorWritebackStillRestoresWordsWhenReadDestinationAliasesThePrivateRecord(bool m68020, uint length, bool bypass)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128, bypass: bypass);
        f.Read(0, 512);
        f.ReadIntoCursor(length);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsNextTableUsesMetadataAlreadyPresentInTheDataReadAheadWindow(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Read(0, 512);
        var reads = f.Reads.Count;
        Assert.Equal(11u, f.Bus.ReadLong(Fixture.File + 96));
        f.Read(72 * 512, 512);
        Assert.Equal(reads, f.Reads.Count);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 96));
        Assert.Equal(0x12345678u, f.Bus.ReadLong(Fixture.File + 88));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FfsMetadataReadAheadKeepsLargeReadCacheStateAndMetadataAcrossPolicies(bool m68020, bool bypass)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128, bypass: bypass);
        f.EnableFfsMetadataReadAhead(); f.Read(0, 64);
        f.Read(0, 8192);
        Assert.Equal(1, f.Reads.Count(r => r == (200u, 1u)));
        // The existing direct large-read path invalidates the payload window.
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(Fixture.State + 12));
        var reads = f.Reads.Count;
        f.Read(72 * 512, 4096);
        Assert.Equal(1, f.Reads.Count(r => r == (200u, 1u)));
        Assert.DoesNotContain(f.Reads.Skip(reads), r => r.Block == 200);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataReadAheadDefersFutureErrorsWithoutRetryingWithinThePacket(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead(); f.BadBlock = 200;
        f.Read(0, 8192);
        Assert.Equal(1, f.Reads.Count(r => r == (200u, 1u)));
        f.Read(72 * 512, 512, expected: -1, error: DOS.Error.DiskNotValidated);
        Assert.Equal(2, f.Reads.Count(r => r == (200u, 1u)));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        f.BadBlock = uint.MaxValue; f.Read(72 * 512, 512);
    }

    [SequentialHandlerTheory]
    [InlineData(false, 64u)] [InlineData(true, 64u)]
    [InlineData(false, 512u)] [InlineData(true, 512u)]
    [InlineData(false, 4096u)] [InlineData(true, 4096u)]
    public void FfsSmallReadsKeepTheirExistingReadAheadWithoutMetadataSpeculation(bool m68020, uint length)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead(); f.Read(0, length);
        Assert.DoesNotContain(f.Reads, r => r.Block == 200);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FfsSectorPacketCacheHitsKeepReadAheadAcrossBothPolicies(bool m68020, bool bypass)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128, bypass: bypass);
        f.Bus.WriteLong(Fixture.State + 1644, bypass ? 3u : 0u);
        f.Read(0, 512);
        var reads = f.Reads.Count;
        for (var position = 512u; position < 8192; position += 512) f.Read(position, 512);
        Assert.Equal(reads, f.Reads.Count);
        f.Read(72 * 512, 512);
        Assert.Equal(reads, f.Reads.Count);
    }

    [SequentialHandlerTheory]
    [InlineData(false, 1u)] [InlineData(true, 1u)]
    [InlineData(false, 2u)] [InlineData(true, 2u)]
    public void FfsSectorPacketStillUsesTheDeviceForOtherBypassPolicies(bool m68020, uint policy)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.Read(0, 512);
        f.Bus.WriteLong(Fixture.State + 1644, policy);
        f.Reads.Clear(); f.ReadTargets.Clear();
        f.Read(512, 512);
        Assert.Single(f.Reads);
        Assert.Equal(1u, f.Reads[0].Count);
        Assert.Equal(new[] { Fixture.Buffer }, f.ReadTargets);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FfsDiagnosticFourKiBBypassRetainsMetadataAndDefersFutureErrors(bool m68020, bool badFuture)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead();
        f.Bus.WriteLong(Fixture.State + 1644, 3);
        if (badFuture) f.BadBlock = 200;
        f.Read(0, 4096);
        Assert.Single(f.Reads.Where(read => read.Block == 200));
        Assert.DoesNotContain(f.Reads, read => read.Count == 128);
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(Fixture.State + 12));
        f.Read(72 * 512, 4096, expected: badFuture ? -1 : 4096,
            error: badFuture ? DOS.Error.DiskNotValidated : DOS.Error.None);
        Assert.Equal(badFuture ? 2 : 1, f.Reads.Count(read => read.Block == 200));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FfsDiagnosticFourKiBBypassKeepsChunkLimitsAndErrorPrefixesWithoutAdaptiveHints(bool m68020, bool fail)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsSequentialStream(clock: false);
        f.Bus.WriteLong(Fixture.State + 1644, 3);
        f.Read(0, 4096); f.Read(4096, 4096);
        f.Bus.WriteLong(Fixture.State + 1628, 2048);
        if (fail) f.BadBlock = 1044;
        f.Reads.Clear(); f.ReadTargets.Clear();
        f.Read(8192, 4096, expected: fail ? 2048 : 4096,
            error: fail ? DOS.Error.DiskNotValidated : DOS.Error.None);
        Assert.Equal(new[] { (1040u, 4u), (1044u, 4u) }, f.Reads);
        Assert.Equal(new[] { Fixture.Buffer, Fixture.Buffer + 2048 }, f.ReadTargets);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 104));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 108));
        Assert.Equal(3u, f.Bus.ReadLong(Fixture.State + 1644));
        if (fail) Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataReadAheadAlsoRunsWithAnAlreadySeededOpenCursor(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead(); f.Read(0, 512);
        Assert.DoesNotContain(f.Reads, r => r.Block == 200);
        f.Read(0, 8192);
        Assert.Equal(1, f.Reads.Count(r => r == (200u, 1u)));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataReadAheadDeclinesAFileRecordAlias(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead(); f.Read(0, 512);
        f.ReadIntoCursor(8192, position: 0);
        Assert.DoesNotContain(f.Reads, r => r.Block == 200);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsFixedMediaInquiryRequiresCompleteSuccessfulNonremovableDirectAccessIdentity(bool m68020)
    {
        for (var response = 0; response < 8; response++)
        {
            var f = new Fixture(false, m68020);
            Assert.Equal(response == 0 ? 1u : 0u, f.ProbeFixedScsi(response));
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataReadAheadDoesNotTrustAFutureChecksum(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead(corrupt: true); f.Read(0, 8192);
        f.Read(72 * 512, 512, expected: -1, error: DOS.Error.DiskNotValidated);
        Assert.Equal(1, f.Reads.Count(r => r == (200u, 1u)));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataReadAheadLeavesFourPagesAndStopsAtItsFixedBudget(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead(chain: true); f.Read(0, 8192);
        Assert.Equal(28, f.Reads.Count(r => r.Block >= 200));
        Assert.DoesNotContain(f.Reads, r => r.Block == 228);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void FfsMetadataReadAheadDeclinesAnUndmaableSlotWithoutSpeculativeIo(bool m68020)
    {
        var f = new Fixture(false, m68020, cacheBlocks: 128);
        f.EnableFfsMetadataReadAhead();
        f.Bus.WriteLong(Fixture.State + 1636, 0x7FFFE); // Payload fits; metadata slots do not.
        f.Read(0, 64); f.Read(0, 8192);
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(Fixture.State + 12));
        Assert.DoesNotContain(f.Reads, r => r.Block <= 200 && 200 - r.Block < r.Count);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ReadPacketRoutingPreservesInvalidHandleResults(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020);
        f.Read(0, 512, file: 0x5400, expected: -1, error: DOS.Error.InvalidLock);
        Assert.Empty(f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void InhibitedSmartReadsKeepTheGeneralDecoderNoDiskResult(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.Bus.WriteLong(Fixture.State + 1744, 1); // Native mount's inhibit word.
        f.Read(0, 512, expected: -1, error: DOS.Error.NoDisk);
        Assert.Empty(f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void InhibitedSfs2RejectsOpenButClosesOwnedFileAndReleasesItsReservation(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        f.Bus.WriteLong(Fixture.State + 1744, 1);
        f.OpenForInput('F', DOS.Error.NoDisk);
        const uint reservations = Fixture.State + 2052, packet = 0x6000;
        f.Bus.WriteLong(reservations, 2);
        f.Bus.WriteLong(reservations + 4, 8);  // File's node
        f.Bus.WriteLong(reservations + 36, 9); // Unrelated owner
        f.Bus.WriteLong(packet + 8, (uint)DosPacketAction.End);
        f.Bus.WriteLong(packet + 20, Fixture.File);
        f.Invoke("Dispatch", Fixture.State, packet);
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(packet + 12));
        Assert.Equal(0u, f.Bus.ReadLong(packet + 16));
        Assert.Equal(1u, f.Bus.ReadLong(reservations));
        Assert.Equal(0u, f.Bus.ReadLong(reservations + 4));
        Assert.Equal(9u, f.Bus.ReadLong(reservations + 36));
        f.Invoke("Dispatch", Fixture.State, packet);
        Assert.Equal(0u, f.Bus.ReadLong(packet + 12));
        Assert.Equal((uint)DOS.Error.InvalidLock, f.Bus.ReadLong(packet + 16));
        Assert.Empty(f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void PacketsBatchReadsAndRetainTraversalAcrossScratchReuseAndInterleavedHandles(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020);
        f.Read(0, 4096); var reads = f.Reads.Count;
        f.Bus.Memory.AsSpan((int)Fixture.State + 64, 1024).Fill(0xCC);
        f.Read(4096, 4096);
        Assert.Equal(reads + 4, f.Reads.Count); // Four MaxTransfer-sized data I/Os; no metadata.
        Assert.All(f.Reads.Skip(reads), r => Assert.Equal(2u, r.Count));
        f.Read(0, 512, Fixture.Other);
        reads = f.Reads.Count; f.Read(8192, 4096);
        Assert.Equal(reads + 4, f.Reads.Count);
        Assert.Equal(sfs2 ? 8u : 10u, f.Bus.ReadLong(Fixture.File + 56));
        Assert.Equal(0x12345678u, f.Bus.ReadLong(Fixture.File + 88));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 92));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void FragmentedExtentsBackwardSeeksEofAndInvalidationKeepPacketSemantics(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020);
        f.Read(71 * 512, 2048); // Cross a pointer table / extent and a physical gap.
        Assert.Contains(f.Reads, r => r.Block == (sfs2 ? 111u : 91u) && r.Count == 1);
        Assert.Contains(f.Reads, r => r.Block == (sfs2 ? 160u : 100u) && r.Count == 2);
        f.Read(3, 1030, alignment: 1); f.Read(0, 4096);
        f.Read(80 * 512 - 17, 512, expected: 17); f.Read(80 * 512, 512, expected: 0);
        Assert.NotEqual(0u, f.Bus.ReadLong(Fixture.File + 56));
        f.Invoke("InvalidateDirectoryCursors", Fixture.State);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        Assert.Equal(0x12345678u, f.Bus.ReadLong(Fixture.File + 88));
        f.ChangeFirstBlockAndSize();
        f.Read(0, 512, expected: 17, replacement: true);
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void DeviceFailureClearsCursorAndReportsOnlyCompletedNativeTransfers(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020);
        f.Read(0, 512);
        f.BadBlock = sfs2 ? 44u : 24u;
        f.Read(0, 4096, expected: 2048, error: DOS.Error.DiskNotValidated);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        f.BadBlock = uint.MaxValue; f.Read(0, 4096);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2RetainedExtentServesWideAlignedPacketThroughEof(bool m68020)
    {
        var f = new Fixture(true, m68020, wide: true, cacheBlocks: 128, bypass: true);
        f.Read(0, 512);
        f.Reads.Clear();
        const ulong position = 0xFFFF0200UL;
        f.Read(position, 65536);
        var first = 40u + (uint)(position >> 9);
        Assert.Equal(first, f.Reads[0].Block);
        Assert.Equal(128L, f.Reads.Sum(r => (long)r.Count));
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.File + 92));
        f.Read(position + 65536, 65536, expected: 0);
        f.Read(position + 65537, 512, expected: -1, error: DOS.Error.SeekError);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2RetainedExtentRestoresAliasedCursor(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.Read(0, 512);
        f.ReadIntoCursor(512);
        f.ReadIntoCursor(64);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2PartialReadWithinWideExtentHandlesFailureRetryAndEof(bool m68020)
    {
        var f = new Fixture(true, m68020, wide: true);
        f.Read(0, 64);
        const ulong start = 0x100000000UL;
        f.BadBlock = 40 + (uint)(start >> 9);
        f.Read(start + 7, 64, alignment: 1, expected: -1, error: DOS.Error.DiskNotValidated);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        f.BadBlock = uint.MaxValue;
        f.Read(start + 7, 64, alignment: 1);
        f.Read(start + 448, 64);
        f.Read(start + 495, 64, expected: 17);
        f.Read(start + 513, 64, expected: -1, error: DOS.Error.SeekError);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2PersistentCursorRetainsWideFileSizeAndPositionAcrossFourGiB(bool m68020)
    {
        var f = new Fixture(true, m68020, wide: true);
        f.Read(0xFFFFFFFDul, 1030, expected: 515);
        var reads = f.Reads.Count;
        f.Read(0x100000000ul, 512);
        Assert.Equal(reads + 1, f.Reads.Count);
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.File + 68)); // Cached file size high word.
        Assert.Equal(1u, f.Bus.ReadLong(Fixture.File + 92)); // File position high word.
        Assert.Equal(0x12345678u, f.Bus.ReadLong(Fixture.File + 88));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void SmallAlignedPacketsRetainReadAheadWhileLargePacketsUseDirectTransfers(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020, cacheBlocks: 128);
        f.Read(0, 512); var reads = f.Reads.Count;
        f.Read(512, 512); f.Read(1024, 4096); f.Read(5120, 4096);
        Assert.Equal(reads, f.Reads.Count);
        f.Reads.Clear();
        f.Read(0, 8192);
        Assert.Equal((sfs2 ? 40u : 20u, 16u), Assert.Single(f.Reads));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void FailedSpeculativeNeighborDoesNotBreakASmallAlignedRead(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020, cacheBlocks: 128);
        f.Read(0, 512);
        f.BadBlock = sfs2 ? 44u : 24u;
        f.Bus.WriteLong(Fixture.State + 12, uint.MaxValue);
        f.Read(0, 512);
        Assert.Equal((sfs2 ? 40u : 20u, 1u), f.Reads[^1]);
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(Fixture.State + 12));
        Assert.NotEqual(0u, f.Bus.ReadLong(Fixture.File + 56));
    }

    [SequentialHandlerTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void CachedReadFailureReturnsOnlyCompletedFallbackTransfersAndClearsCursor(bool sfs2, bool m68020)
    {
        var f = new Fixture(sfs2, m68020, cacheBlocks: 128);
        f.Read(0, 512);
        f.BadBlock = sfs2 ? 44u : 24u;
        f.Bus.WriteLong(Fixture.State + 12, uint.MaxValue);
        f.Bus.WriteLong(Fixture.State + 1628, 1024);
        f.Read(0, 4096, expected: 2048, error: DOS.Error.DiskNotValidated);
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(Fixture.State + 12));
        f.BadBlock = uint.MaxValue; f.Read(0, 4096);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2MountedDirectReadsHonorDeviceFailureAndRemountedDmaLimits(bool m68020)
    {
        var f = new Fixture(true, m68020, cacheBlocks: 128);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        f.Read(0, 512);
        f.Reads.Clear(); f.ReadTargets.Clear();
        f.BadBlock = 40;
        f.Read(0, 8192, expected: -1, error: DOS.Error.DiskNotValidated);
        Assert.Equal((40u, 16u), Assert.Single(f.Reads));
        Assert.Equal(Fixture.Buffer, Assert.Single(f.ReadTargets));
        Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));
        f.BadBlock = uint.MaxValue;
        f.Read(0, 8192);

        // Split at MaxTransfer even when the caller remains DMA-eligible.
        f.Bus.WriteLong(Fixture.State + 1628, 1024);
        f.RemountSfs2();
        f.Read(0, 512);
        f.Reads.Clear(); f.ReadTargets.Clear();
        f.Read(0, 8192);
        Assert.Equal(8, f.Reads.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal((40u + (uint)i * 2, 2u), f.Reads[i]);
            Assert.Equal(Fixture.Buffer + (uint)i * 1024, f.ReadTargets[i]);
        }

        // A hole in the replacement mask excludes the caller from DMA.
        f.Bus.WriteLong(Fixture.State + 1636, 0xFFFF7FFEu);
        f.RemountSfs2();
        f.Read(0, 512);
        f.Reads.Clear(); f.ReadTargets.Clear();
        f.Read(0, 8192);
        Assert.Equal(8, f.Reads.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal((40u + (uint)i * 2, 2u), f.Reads[i]);
            Assert.Equal(f.Bus.ReadLong(Fixture.State + 8), f.ReadTargets[i]);
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2MountedLargeReadKeepsTheHighDeviceOffset(bool m68020)
    {
        var f = new Fixture(true, m68020, wide: true, cacheBlocks: 128);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        f.Read(0, 512);
        f.Reads.Clear(); f.ReadTargets.Clear();
        const ulong position = 0xFFFFE000;
        f.Read(position, 8192);
        Assert.Equal((40u + (uint)(position >> 9), 16u), Assert.Single(f.Reads));
        Assert.Equal(Fixture.Buffer, Assert.Single(f.ReadTargets));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2InputPathOwnsItsKeyAndHonorsLocksTransactionsAndInvalidation(bool m68020)
    {
        var f = new Fixture(true, m68020, wide: true);
        f.EnableFixedDiskOpens();
        const ulong size = 0x100000200UL;
        Assert.Equal(size, f.OpenForInput('F'));
        f.Reads.Clear();
        Assert.Equal(size, f.OpenForInput('F'));
        Assert.Empty(f.Reads);
        f.OpenForInput('G', DOS.Error.ObjectNotFound); // Same guest address and length.

        f.Bus.WriteLong(Fixture.File + 28, uint.MaxValue);
        f.OpenForInput('F', DOS.Error.ObjectInUse);
        f.Bus.WriteLong(Fixture.File + 28, 0);

        // The same name relative to another lock must take the native resolver.
        f.Bus.WriteLong(Fixture.Other + 16, 0);
        f.OpenForInput('F', DOS.Error.ObjectWrongType, (Fixture.Other + 20) >> 2);

        const uint batch = 0x80000;
        f.Bus.WriteLong(Fixture.State + 1756, batch);
        f.Bus.WriteLong(batch, 1); f.Bus.WriteLong(batch + 4, 1); f.Bus.WriteLong(batch + 8, 10);
        f.OpenForInput('F', DOS.Error.DiskNotValidated);
        f.Bus.WriteLong(Fixture.State + 1756, 0);
        Assert.Equal(size, f.OpenForInput('F'));

        f.Invoke("InvalidateDirectoryCursors", Fixture.State);
        f.ChangeFirstBlockAndSize();
        Assert.Equal(17ul, f.OpenForInput('F'));

        var cache = f.Bus.ReadLong(Fixture.State + 2032);
        f.Bus.WriteLong(cache, uint.MaxValue);
        f.Invoke("InvalidateDirectoryCursors", Fixture.State);
        f.Reads.Clear();
        Assert.Equal(17ul, f.OpenForInput('F'));
        Assert.NotEmpty(f.Reads);
        f.Bus.WriteLong(Fixture.State + 2032, 0); // Optional mount cache unavailable.
        f.Reads.Clear();
        Assert.Equal(17ul, f.OpenForInput('F'));
        Assert.NotEmpty(f.Reads);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2FirstOpenFillsAndVerifiesMetadataSlotsWithoutRetryingFailedIo(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        const uint head = 0x82000, tail = head + 532, hash = Fixture.State + 1760;
        f.Bus.WriteLong(Fixture.State + 44, head); f.Bus.WriteLong(Fixture.State + 2016, tail);
        f.Bus.WriteLong(Fixture.State + 48, 2); f.Bus.WriteLong(Fixture.State + 12, uint.MaxValue);
        f.Bus.WriteLong(head, tail); f.Bus.WriteLong(head + 4, uint.MaxValue);
        f.Bus.WriteLong(tail + 8, head); f.Bus.WriteLong(tail + 4, 12);
        f.Bus.WriteLong(hash + 12 * 4, tail); f.Bus.WriteLong(tail + 16, ~0x4F424A43u);

        f.Reads.Clear(); f.ReadTargets.Clear(); f.BadBlock = 10;
        f.OpenForInput('F', DOS.Error.DiskNotValidated);
        Assert.Equal((10u, 1u), Assert.Single(f.Reads));
        Assert.Equal(tail + 20, Assert.Single(f.ReadTargets));
        Assert.Equal(0u, f.Bus.ReadLong(hash + 12 * 4));
        Assert.Equal(uint.MaxValue, f.Bus.ReadLong(tail + 4));
        Assert.Equal(0u, f.Bus.ReadLong(tail + 16));

        f.BadBlock = uint.MaxValue;
        f.ToggleObjectChecksum();
        f.OpenForInput('F', DOS.Error.DiskNotValidated);
        Assert.Equal(0u, f.Bus.ReadLong(tail + 16));
        f.ToggleObjectChecksum();
        f.RemountSfs2();
        f.Reads.Clear(); f.ReadTargets.Clear();
        Assert.Equal(40960ul, f.OpenForInput('F'));
        Assert.Equal(new[] { (10u, 1u), (7u, 1u) }, f.Reads);
        foreach (var target in f.ReadTargets) Assert.True(target == head + 20 || target == tail + 20);
        Assert.Equal(~0x4F424A43u, f.Bus.ReadLong(f.Bus.ReadLong(hash + 10 * 4) + 16));
        Assert.Equal(~0x4E444320u, f.Bus.ReadLong(f.Bus.ReadLong(hash + 7 * 4) + 16));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2FirstOpenUsesScratchWhenMaskExcludesTheCache(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        const uint entry = 0x82000;
        f.Bus.WriteLong(Fixture.State + 44, entry); f.Bus.WriteLong(Fixture.State + 2016, entry);
        f.Bus.WriteLong(Fixture.State + 48, 1); f.Bus.WriteLong(entry + 4, uint.MaxValue);
        f.Bus.WriteLong(Fixture.State + 1636, 0xFFF7FFFE); // Excludes bit 19 of the slot address.
        f.Bus.WriteLong(Fixture.State + 12, uint.MaxValue);
        f.ReadTargets.Clear();
        Assert.Equal(40960ul, f.OpenForInput('F'));
        Assert.NotEmpty(f.ReadTargets);
        Assert.All(f.ReadTargets, target => Assert.Equal(Fixture.State + 64, target));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2MountBatchesNearbyMetadataAndFallsBackOnAnUnreadableNeighbor(bool m68020)
    {
        foreach (var failNeighbor in new[] { false, true })
        {
            var f = new Fixture(true, m68020, cacheBlocks: 128);
            f.EnableFixedDiskOpens(cacheSlots: 16);

            if (failNeighbor) f.BadBlock = 4; // Neither a root nor the required clean marker.
            f.RemountSfs2();
            Assert.Contains((0u, 8u), f.Reads);
            var nodeEntry = f.Bus.ReadLong(Fixture.State + 1760 + 7 * 4);
            Assert.Equal(!failNeighbor, nodeEntry != 0);
            if (nodeEntry != 0) Assert.Equal(0u, f.Bus.ReadLong(nodeEntry + 16));
            f.Reads.Clear();
            Assert.Equal(40960ul, f.OpenForInput('F'));
            Assert.Equal(failNeighbor, f.Reads.Contains((7u, 1u)));
            Assert.Contains((10u, 1u), f.Reads); // Child container outside the prefix.

            f.Reads.Clear();
            f.Read(0, 4096);
            Assert.Equal(failNeighbor, f.Reads.Contains((6u, 1u))); // Extent tree in the mount prefix.

            const uint batch = 0x80000;
            f.Bus.WriteLong(Fixture.State + 1756, batch);
            f.Bus.WriteLong(batch, 1); f.Bus.WriteLong(batch + 4, 1); f.Bus.WriteLong(batch + 8, 6);
            f.Invoke("InvalidateDirectoryCursors", Fixture.State);
            f.Read(0, 4096, expected: -1, error: DOS.Error.DiskNotValidated);
            f.Bus.WriteLong(batch + 8, 7);
            f.OpenForInput('F', DOS.Error.DiskNotValidated);
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2OpenChecksTheWholeBstrAndAHandleInAnotherMemoryRegion(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        const uint first = 0xE20000, second = 0xE20100;
        f.Bus.WriteLong(second, f.Bus.ReadLong(first));
        f.Bus.WriteLong(first, second);
        f.Bus.WriteLong(first + ExecLayout.MemHeader.Upper, 0x6502);
        f.Bus.WriteLong(second + ExecLayout.MemHeader.Lower, 0x6600);
        f.Bus.WriteLong(second + ExecLayout.MemHeader.Upper, 0x1000000);
        Assert.Equal(40960ul, f.OpenForInput('F'));
        f.Bus.WriteLong(first + ExecLayout.MemHeader.Upper, 0x6501);
        f.OpenForInput('F', DOS.Error.InvalidComponentName);
        f.Bus.WriteLong(first + ExecLayout.MemHeader.Upper, 0x6502);
        f.Bus.WriteLong(second + ExecLayout.MemHeader.Lower, 0x6604);
        f.OpenForInput('F', DOS.Error.BadNumber);
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2ReopenChecksTheLengthAndTailOfAnOwnedBstr(bool m68020)
    {
        foreach (var name in new[] { "abc", "abcd", "abcde", "abcdef", "abcdefg", "abcdefgh", "abcdefghijklmnopq" })
        {
            var f = new Fixture(true, m68020);
            f.SetSfs2FileName(name);
            f.EnableFixedDiskOpens();
            Assert.Equal(40960ul, f.OpenForInput(name));
            f.Reads.Clear();
            Assert.Equal(40960ul, f.OpenForInput(name));
            Assert.Empty(f.Reads);
            f.OpenForInput(name[..^1] + "x", DOS.Error.ObjectNotFound);
            f.OpenForInput(name[..^1], DOS.Error.ObjectNotFound);
            if (name.Length >= 8) f.OpenForInput(name[..3] + "x" + name[4..], DOS.Error.ObjectNotFound);
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2RetainedExtentChecksCallerAndScratchInSeparateMemoryRegions(bool m68020)
    {
        foreach (var scratchAfterCaller in new[] { false, true })
        {
            var f = new Fixture(true, m68020, cacheBlocks: 128);
            var scratch = scratchAfterCaller ? 0x24000u : Fixture.State + 64;
            f.Bus.WriteLong(Fixture.State + 1648, scratch);
            f.Read(0, 512);
            f.Read(0, 8192); // One mapped region, in either address order.

            const uint first = 0xE20000, second = 0xE20100;
            f.Bus.WriteLong(second, f.Bus.ReadLong(first));
            f.Bus.WriteLong(first, second);
            f.Bus.WriteLong(first + ExecLayout.MemHeader.Upper, scratchAfterCaller ? Fixture.Buffer + 8192 : scratch + 1024);
            f.Bus.WriteLong(second + ExecLayout.MemHeader.Lower, scratchAfterCaller ? scratch : Fixture.Buffer);
            f.Bus.WriteLong(second + ExecLayout.MemHeader.Upper, scratchAfterCaller ? scratch + 1024 : Fixture.Buffer + 8192);
            f.Read(0, 8192);
            f.Read(8192, 64);

            var scratchRegion = scratchAfterCaller ? second : first;
            f.Bus.WriteLong(scratchRegion + ExecLayout.MemHeader.Upper, scratch + 1023);
            f.Reads.Clear();
            f.Read(0, 8192, expected: -1, error: DOS.Error.DiskNotValidated);
            Assert.Empty(f.Reads);
            Assert.Equal(0u, f.Bus.ReadLong(Fixture.File + 56));

            f.Bus.WriteLong(scratchRegion + ExecLayout.MemHeader.Upper, scratch + 1024);
            f.Read(0, 512);
            var callerRegion = scratchAfterCaller ? first : second;
            f.Bus.WriteLong(callerRegion + ExecLayout.MemHeader.Upper, Fixture.Buffer + 8191);
            f.Reads.Clear();
            f.Read(0, 8192, expected: -1, error: DOS.Error.DiskNotValidated);
            Assert.Empty(f.Reads);
        }
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2RemountDiscardsVerifiedMetadataAndOpenFileExtents(bool m68020)
    {
        var f = new Fixture(true, m68020, cacheBlocks: 128);
        f.EnableFixedDiskOpens(cacheSlots: 16);
        f.RemountSfs2();
        Assert.Equal(40960ul, f.OpenForInput('F'));
        f.Read(0, 4096);

        f.ChangeFirstBlockAndSize();
        f.RemountSfs2();
        Assert.Equal(17ul, f.OpenForInput('F'));
        f.Read(0, 64, expected: 17, replacement: true);

        f.ToggleCleanChecksum();
        f.RemountSfs2(DOS.Error.DiskNotValidated);
        f.OpenForInput('F', DOS.Error.NoDisk);
        f.ToggleCleanChecksum();
        f.RemountSfs2();
        Assert.Equal(17ul, f.OpenForInput('F'));
    }

    [SequentialHandlerTheory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2FirstLookupUsesOnlyCurrentMountedRoot(bool m68020)
    {
        var f = new Fixture(true, m68020);
        f.EnableFixedDiskOpens();
        f.RemountSfs2();
        f.Reads.Clear();
        Assert.Equal(40960ul, f.OpenForInput('F'));
        Assert.DoesNotContain(f.Reads, read => read.Block is 0 or 255 or 5 or 3);

        // The overlay changes the clean marker while the cached path and root are valid.
        const uint batch = 0x80000;
        f.Bus.WriteLong(Fixture.State + 1756, batch);
        f.Bus.WriteLong(batch, 1); f.Bus.WriteLong(batch + 4, 1); f.Bus.WriteLong(batch + 8, 5);
        f.OpenForInput('F', DOS.Error.DiskNotValidated);
        f.Bus.WriteLong(Fixture.State + 1756, 0);

        f.RemountSfs2();
        f.Invoke("InvalidateDirectoryCursors", Fixture.State);
        f.RemoveRootChildren();
        f.OpenForInput('F', DOS.Error.ObjectNotFound);
    }

    private sealed class Fixture
    {
        internal const uint State = 0x2000, File = 0x5000, Other = 0x5200;
        internal const uint Buffer = 0x9000;
        private const uint Packet = 0x6000, Request = 0x7000, Cache = 0x40000;
        private const uint Load = 0x100000, Exec = 0xE00000;
        internal readonly NativeParityBus Bus;
        internal readonly List<(uint Block, uint Count)> Reads = [];
        internal readonly List<uint> ReadTargets = [];
        internal uint BadBlock = uint.MaxValue;
        internal uint DevicePenaltyTicks;
        internal uint ClockCalls;
        private uint clockBias = 0xFFFFF000;
        private readonly Dictionary<uint, byte[]> disk = [];
        private readonly string map;
        private readonly bool sfs2;
        private readonly bool wide;
        private readonly M68kCpuModel model;
        private Action<M68kCpuState>? scsiInquiry;

        internal Fixture(bool sfs2, bool m68020, bool wide = false, uint cacheBlocks = 1, bool bypass = false)
        {
            this.sfs2 = sfs2; this.wide = wide; model = m68020 ? M68kCpuModel.M68020 : M68kCpuModel.M68000;
            var path = Environment.GetEnvironmentVariable(sfs2 ? "COPPERFS_SFS2_HANDLER" : "COPPERFS_FFS_HANDLER")!;
            map = System.IO.File.ReadAllText(path + ".map");
            Bus = NativeParityBus.LoadHunk(System.IO.File.ReadAllBytes(path), Load);
            var execSlot = Regex.Match(map, @"(?m)^([0-9A-Fa-f]{8})\s+4\s+_ExecBase\r?$");
            Assert.True(execSlot.Success);
            Bus.WriteLong(Load + uint.Parse(execSlot.Groups[1].Value, NumberStyles.HexNumber), Exec);
            Bus.WriteLong(4, Exec);
            var list = Exec + ExecLayout.ExecBase.MemList; const uint memory = 0xE20000;
            Bus.WriteLong(list, memory); Bus.WriteLong(memory, list + 4);
            Bus.WriteLong(memory + ExecLayout.MemHeader.Lower, 0x400);
            Bus.WriteLong(memory + ExecLayout.MemHeader.Upper, 0x1000000);
            foreach (var lvo in new uint[] { 624, 630 })
                Bus.RegisterGateway(Exec - lvo, cpu => Bus.Memory.AsSpan((int)cpu.A[0], (int)cpu.D[0])
                    .CopyTo(Bus.Memory.AsSpan((int)cpu.A[1], (int)cpu.D[0])));
            Bus.RegisterGateway(Exec - 456, cpu =>
            {
                var request = cpu.A[1]; var command = Bus.ReadWord(request + ExecLayout.IORequest.Command);
                if (command == 28 && scsiInquiry is not null) { scsiInquiry(cpu); return; }
                Assert.True(command is 2 or 24);
                var offset = (ulong)Bus.ReadLong(request + ExecLayout.IOStdReq.Offset);
                if (command == 24) offset |= (ulong)Bus.ReadLong(request + ExecLayout.IOStdReq.Actual) << 32;
                var block = (uint)(offset / 512);
                var count = Bus.ReadLong(request + ExecLayout.IOStdReq.Length) / 512;
                var target = Bus.ReadLong(request + ExecLayout.IOStdReq.Data);
                Reads.Add((block, count));
                clockBias = unchecked(clockBias + DevicePenaltyTicks);
                ReadTargets.Add(target);
                var failed = BadBlock >= block && BadBlock - block < count;
                if (!failed)
                    for (var i = 0u; i < count; i++)
                    {
                        if (wide && block + i >= 40 && block + i - 40 < 0x800001)
                            for (var j = 0u; j < 512; j++)
                                Bus.Memory[target + i * 512 + j] = Pattern((ulong)(block + i - 40) * 512 + j);
                        else if (disk.TryGetValue(block + i, out var contents)) contents.CopyTo(Bus.Memory, (int)(target + i * 512));
                        else Bus.Memory.AsSpan((int)(target + i * 512), 512).Clear();
                    }
                Bus.Memory[request + ExecLayout.IORequest.Error] = failed ? (byte)5 : (byte)0;
                Bus.WriteLong(request + ExecLayout.IOStdReq.Actual, failed ? 0 : count * 512);
            });
            Bus.WriteLong(State + 4, Request); Bus.WriteLong(State + 8, Cache);
            Bus.WriteLong(State + 12, uint.MaxValue); Bus.WriteLong(State + 20, sfs2 ? 0x53465302u : 0x444F5301u);
            Bus.WriteLong(State + 32, File);
            Bus.WriteLong(State + 1644, bypass ? 1u : 0u);
            Bus.WriteLong(State + 1600, wide ? 0x1000000u : 256u); Bus.WriteLong(State + 1604, 512);
            Bus.WriteLong(State + 1608, 2); Bus.WriteLong(State + 1612, 512);
            Bus.WriteLong(State + 1628, cacheBlocks == 1 ? 1024u : 65536u); Bus.WriteLong(State + 1632, 1);
            Bus.WriteLong(State + 1636, 0xFFFFFFFE); Bus.WriteLong(State + 1656, cacheBlocks); Bus.WriteLong(State + 1660, cacheBlocks * 512);
            Bus.WriteLong(File, Other);
            foreach (var file in new[] { File, Other })
            {
                Bus.WriteLong(file + 4, sfs2 ? 8u : 10u); Bus.WriteLong(file + 8, 80 * 512);
                Bus.WriteLong(file + 16, 1); Bus.WriteLong(file + 88, 0x12345678);
            }
            if (sfs2) Sfs2Image(); else FfsImage();
        }

        internal int FileAllocations { get; private set; }
        internal int FileFrees { get; private set; }

        internal void EnableFixedDiskOpens(uint cacheSlots = 0)
        {
            Bus.WriteLong(State + 1640, 1);
            Bus.WriteLong(State + 2032, 0x88000);
            uint allocated = 0;
            Bus.RegisterGateway(Exec - 198, cpu =>
            {
                if (allocated < cacheSlots)
                {
                    Assert.Equal(532u, cpu.D[0]);
                    Assert.Equal((uint)global::Amiga.Exec.MemoryFlags.Public, cpu.D[1]);
                    var address = 0x82000 + allocated++ * 532;
                    Bus.Memory.AsSpan((int)address, 532).Fill(0xA5);
                    cpu.D[0] = address;
                    return;
                }
                Assert.Equal(216u, cpu.D[0]); // The existing DOS file object.
                FileAllocations++;
                Bus.Memory.AsSpan(0x5400, 216).Clear();
                cpu.D[0] = 0x5400;
            });
            Bus.RegisterGateway(Exec - 210, cpu =>
            {
                Assert.True(cpu.A[1] is File or Other or 0x5400u);
                Assert.Equal(216u, cpu.D[0]);
                FileFrees++;
            });
            if (cacheSlots == 0) return;
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.MoreCache);
            Bus.WriteLong(Packet + 20, cacheSlots);
            Invoke("Dispatch", State, Packet);
            Assert.Equal(0u, Bus.ReadLong(Packet + 16));
            Assert.Equal(cacheSlots, allocated);
            for (uint bucket = 0; bucket < 64; bucket++)
                Assert.Equal(0u, Bus.ReadLong(State + 1760 + bucket * 4));
        }

        internal void RemountSfs2(DOS.Error expected = DOS.Error.None)
        {
            Bus.WriteLong(State + 1744, 1);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.Inhibit);
            Bus.WriteLong(Packet + 20, 0);
            Invoke("Dispatch", State, Packet);
            Assert.Equal((uint)expected, Bus.ReadLong(Packet + 16));
            Assert.Equal(expected == DOS.Error.None ? uint.MaxValue : 0u, Bus.ReadLong(Packet + 12));
        }

        internal void ToggleCleanChecksum() => disk[5][4] ^= 1;
        internal void ToggleExtentChecksum() => disk[6][4] ^= 1;

        internal void RemoveRootChildren()
        {
            Put(disk[3], 40, 0);
            Seal(disk[3], true);
            Bus.WriteLong(State + 12, uint.MaxValue);
        }

        internal void ToggleObjectChecksum() => disk[10][4] ^= 1;

        internal void MakeSfs2SoftLink(bool oversized)
        {
            var record = disk[10];
            record[50] = 64;
            if (oversized) Put(record, 40, 0x10000);
            Seal(record, true);
        }

        internal void SetSfs2FileName(string name)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(disk[10], 51);
            Seal(disk[10], true);
        }

        internal ulong OpenForInput(char name, DOS.Error error = DOS.Error.None, uint directoryLock = 0) =>
            OpenForInput(name.ToString(), error, directoryLock);

        internal ulong OpenForInput(string name, DOS.Error error = DOS.Error.None, uint directoryLock = 0,
            Action<uint>? whileOpen = null, uint handle = 0x6600)
        {
            const uint bstr = 0x6500;
            Bus.Memory[bstr] = checked((byte)name.Length);
            Encoding.ASCII.GetBytes(name).CopyTo(Bus.Memory, (int)bstr + 1);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.FindInput);
            Bus.WriteLong(Packet + 20, handle >> 2); Bus.WriteLong(Packet + 24, directoryLock); Bus.WriteLong(Packet + 28, bstr >> 2);
            Invoke("Dispatch", State, Packet);
            Assert.Equal((uint)error, Bus.ReadLong(Packet + 16));
            Assert.Equal(error == DOS.Error.None ? uint.MaxValue : 0u, Bus.ReadLong(Packet + 12));
            if (error != DOS.Error.None) return 0;
            var file = Bus.ReadLong(handle + 36);
            var size = ((ulong)Bus.ReadLong(file + 88) << 32) | Bus.ReadLong(file + 8);
            whileOpen?.Invoke(file);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.End);
            Bus.WriteLong(Packet + 20, file);
            Invoke("Dispatch", State, Packet);
            Assert.Equal(uint.MaxValue, Bus.ReadLong(Packet + 12));
            return size;
        }

        internal void Read(ulong position, uint length, uint file = File, uint alignment = 0,
            int? expected = null, DOS.Error error = DOS.Error.None, bool replacement = false)
        {
            Bus.WriteLong(file + 12, (uint)position); Bus.WriteLong(file + 92, (uint)(position >> 32));
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.Read); Bus.WriteLong(Packet + 20, file);
            Bus.WriteLong(Packet + 24, Buffer + alignment); Bus.WriteLong(Packet + 28, length);
            Bus.Memory.AsSpan((int)Buffer, (int)Math.Max(8192u, length) + 8).Fill(0xA7);
            Invoke("Dispatch", State, Packet);
            var count = expected ?? (int)length;
            Assert.Equal(count, unchecked((int)Bus.ReadLong(Packet + 12)));
            Assert.Equal((uint)error, Bus.ReadLong(Packet + 16));
            Assert.Equal(position + (uint)Math.Max(count, 0),
                ((ulong)Bus.ReadLong(file + 92) << 32) | Bus.ReadLong(file + 12));
            for (var i = 0; i < Math.Max(count, 0); i++)
                Assert.Equal(replacement ? (byte)0xD3 : Pattern(position + (uint)i), Bus.Memory[Buffer + alignment + i]);
            Assert.Equal(0xA7, Bus.Memory[Buffer + alignment + length]);
        }

        internal void ReadIntoCursor(uint length, uint position = 512)
        {
            uint[] offsets = sfs2 ? [56, 60, 64, 68, 72, 76, 80, 84, 96, 100, 104] : [56, 60, 64, 68, 72, 76, 80, 84, 96];
            var values = offsets.Select(offset => Bus.ReadLong(File + offset)).ToArray();
            Bus.WriteLong(File + 12, position); Bus.WriteLong(File + 92, 0);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.Read); Bus.WriteLong(Packet + 20, File);
            Bus.WriteLong(Packet + 24, File + 56); Bus.WriteLong(Packet + 28, length);
            Invoke("Dispatch", State, Packet);
            Assert.Equal(length, Bus.ReadLong(Packet + 12)); Assert.Equal(0u, Bus.ReadLong(Packet + 16));
            Assert.Equal(values, offsets.Select(offset => Bus.ReadLong(File + offset)).ToArray());
        }

        internal void ReadIntoScratch()
        {
            const uint destination = State + 64;
            Bus.WriteLong(File + 12, 512); Bus.WriteLong(File + 92, 0);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.Read); Bus.WriteLong(Packet + 20, File);
            Bus.WriteLong(Packet + 24, destination); Bus.WriteLong(Packet + 28, 1024);
            Invoke("Dispatch", State, Packet);
            // The legacy scratch-alias path overwrites its header while
            // delivering the first sector, then reports that completed prefix.
            Assert.Equal(512u, Bus.ReadLong(Packet + 12)); Assert.Equal((uint)DOS.Error.DiskNotValidated, Bus.ReadLong(Packet + 16));
            for (var i = 0u; i < 512; i++) Assert.Equal(Pattern(512 + i), Bus.Memory[destination + i]);
            Assert.Equal(0u, Bus.ReadLong(File + 56));
        }

        internal void ReadIntoPacket()
        {
            const uint target = Packet + 20, length = 1024;
            Bus.WriteLong(File + 12, 512); Bus.WriteLong(File + 92, 0);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.Read); Bus.WriteLong(Packet + 20, File);
            Bus.WriteLong(Packet + 24, target); Bus.WriteLong(Packet + 28, length);
            Invoke("Dispatch", State, Packet);
            Assert.Equal(length, Bus.ReadLong(Packet + 12)); Assert.Equal(0u, Bus.ReadLong(Packet + 16));
            Assert.Equal(512 + length, Bus.ReadLong(File + 12));
            for (var i = 0u; i < length; i++) Assert.Equal(Pattern(512 + i), Bus.Memory[target + i]);
        }

        internal void PrecedeFileWithLocks(uint count)
        {
            const uint arena = 0x400000, stride = 216;
            Bus.WriteLong(State + 32, arena);
            for (var i = 0u; i < count; i++)
                Bus.WriteLong(arena + i * stride, i + 1 == count ? File : arena + (i + 1) * stride);
        }

        internal void ReadIntoPrivateCache(uint entry)
        {
            var destination = entry + 20;
            Bus.WriteLong(File + 12, 0); Bus.WriteLong(File + 92, 0);
            Bus.WriteLong(Packet + 8, (uint)DosPacketAction.Read); Bus.WriteLong(Packet + 20, File);
            Bus.WriteLong(Packet + 24, destination); Bus.WriteLong(Packet + 28, 512);
            Bus.Memory.AsSpan((int)destination, 513).Fill(0xA7);
            Invoke("Dispatch", State, Packet);
            Assert.Equal(512u, Bus.ReadLong(Packet + 12)); Assert.Equal(0u, Bus.ReadLong(Packet + 16));
            for (var i = 0u; i < 512; i++) Assert.Equal(Pattern(i), Bus.Memory[destination + i]);
            Assert.Equal(0xA7, Bus.Memory[destination + 512]);
        }

        internal void RetainFfsHeader()
        {
            const uint entry = 0x81000;
            Bus.WriteLong(State + 44, entry); Bus.WriteLong(State + 48, 1);
            Bus.WriteLong(State + 2016, entry); Bus.WriteLong(State + 1760 + 10 * 4, entry);
            Bus.WriteLong(entry + 4, 10);
            disk[10].CopyTo(Bus.Memory, (int)entry + 20);
        }

        internal void EnableFfsMetadataReadAhead(bool corrupt = false, bool chain = false)
        {
            const uint first = 0x82000, stride = 532, slots = 32;
            Bus.WriteLong(State + 1640, 1);
            Bus.WriteLong(State + 44, first); Bus.WriteLong(State + 48, slots);
            Bus.WriteLong(State + 2016, first + (slots - 1) * stride);
            for (var i = 0u; i < slots; i++)
            {
                var entry = first + i * stride;
                Bus.WriteLong(entry, i + 1 == slots ? 0 : entry + stride);
                Bus.WriteLong(entry + 4, uint.MaxValue);
                Bus.WriteLong(entry + 8, i == 0 ? 0 : entry - stride);
            }
            disk[200] = disk[11]; disk.Remove(11);
            Put(disk[200], 4, 200); Seal(disk[200]);
            Put(disk[10], 504, 200); Seal(disk[10]);
            if (corrupt) disk[200][20] ^= 1;
            if (chain)
            {
                Bus.WriteLong(State + 1600, 4096);
                Put(disk[10], 324, 31 * 72 * 512); Seal(disk[10]);
                for (var block = 200u; block < 230; block++)
                {
                    if (block != 200) disk[block] = (byte[])disk[200].Clone();
                    Put(disk[block], 4, block); Put(disk[block], 504, block == 229 ? 0u : block + 1);
                    Seal(disk[block]);
                }
            }
        }

        internal void EnableFfsSequentialStream(bool clock = true)
        {
            EnableFfsMetadataReadAhead();
            Bus.WriteLong(State + 1600, 4096);
            Bus.WriteLong(File + 8, 1048576); Bus.WriteLong(Other + 8, 1048576);
            for (var table = 0u; table < 29; table++)
            {
                var key = table == 0 ? 10u : 127 + table;
                var image = table == 0 ? disk[10] : Block(key, 16);
                disk[key] = image;
                var count = Math.Min(72u, 2048 - table * 72);
                Put(image, 4, key); Put(image, 8, count);
                Put(image, 500, table == 0 ? 0u : 10u);
                Put(image, 504, table == 28 ? 0u : 128 + table);
                Put(image, 508, unchecked((uint)-3));
                if (table == 0) Put(image, 324, 1048576);
                for (var slot = 0u; slot < count; slot++)
                    Put(image, 308 - (int)slot * 4, 1024 + table * 72 + slot);
                Seal(image);
            }
            Payload(1024, 2048, 0);
            if (!clock) return;
            const uint timer = 0xE40000, timerRequest = 0x7600;
            Bus.WriteLong(State + 2024, timerRequest); Bus.WriteLong(timerRequest + 20, timer);
            Bus.WriteWord(timer + 20, 39);
            Bus.RegisterGateway(timer - 60, cpu =>
            {
                ClockCalls++;
                Bus.WriteLong(cpu.A[0], 0);
                Bus.WriteLong(cpu.A[0] + 4, unchecked(clockBias + (uint)(cpu.Cycles / 10)));
                cpu.D[0] = 709379;
            });
        }

        internal uint ProbeFixedScsi(int response)
        {
            scsiInquiry = cpu =>
            {
                var request = cpu.A[1]; Assert.Equal(28u, Bus.ReadWord(request + ExecLayout.IORequest.Command));
                Assert.Equal(30u, Bus.ReadLong(request + ExecLayout.IOStdReq.Length));
                var command = Bus.ReadLong(request + ExecLayout.IOStdReq.Data);
                var cdb = Bus.ReadLong(command + 12); var data = Bus.ReadLong(command);
                Assert.Equal(new byte[] { 0x12, 0, 0, 0, 36, 0 }, Bus.Memory.AsSpan((int)cdb, 6).ToArray());
                Assert.Equal(1u, Bus.Memory[command + 20]);
                Bus.Memory.AsSpan((int)data, 36).Clear();
                Bus.Memory[data] = response == 2 ? (byte)5 : response == 3 ? (byte)0x20 : (byte)0;
                Bus.Memory[data + 1] = response == 1 ? (byte)0x80 : (byte)0;
                Bus.WriteLong(command + 8, response == 4 ? 0u : 36u);
                Bus.WriteWord(command + 18, response == 5 ? (ushort)0 : (ushort)6);
                Bus.Memory[command + 21] = response == 6 ? (byte)2 : (byte)0;
                Bus.Memory[request + ExecLayout.IORequest.Error] = response == 7 ? (byte)5 : (byte)0;
            };
            return Invoke("ProbeFixedScsi", State, State + 64);
        }

        internal uint Invoke(string method, uint a0, uint a1 = 0, uint a2 = 0)
        {
            var owner = method == "TryDispatchCachedFfsRead" ? "NativeFileSystemPlatform" : "NativeAmigaFileSystem";
            var symbol = "CopperStart.Dos." + owner + "::" + method;
            var match = Regex.Match(map, @"(?m)^([0-9A-Fa-f]{8})\s+\d+\s+" + Regex.Escape(symbol) + @"\r?$");
            Assert.True(match.Success, symbol);
            const uint stack = 0xF00000, returned = 0xF10000;
            Bus.WriteLong(stack, returned);
            using var cpu = M68kCoreFactory.Default.Create(model, Bus);
            cpu.Reset(Load + uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), stack);
            // The third APTR uses D0 after the two public address arguments.
            cpu.State.A[0] = a0; cpu.State.A[1] = a1; cpu.State.D[0] = a2; cpu.State.A[6] = Exec;
            for (var step = 0; step < 5_000_000 && cpu.State.ProgramCounter != returned; step++)
            { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted); }
            Assert.Equal(returned, cpu.State.ProgramCounter); Assert.Equal(stack + 4, cpu.State.A[7]);
            return cpu.State.D[0];
        }

        private static byte Pattern(ulong position) => (byte)(position / 512 * 31 + position % 512 * 17 + 3);
        private byte[] Block(uint block, uint type)
        { var b = new byte[512]; disk[block] = b; Put(b, 0, type); return b; }
        private static void Put(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), value);
        private static void Seal(byte[] b, bool sfs2 = false)
        {
            var at = sfs2 ? 4 : 20; Put(b, at, 0); uint sum = sfs2 ? 2u : 0;
            for (var i = 0; i < 512; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i)));
            Put(b, at, unchecked(0u - sum));
        }
        private void Payload(uint first, uint count, uint start)
        {
            for (var i = 0u; i < count; i++)
                disk[first + i] = Enumerable.Range(0, 512).Select(n => Pattern((start + i) * 512 + (uint)n)).ToArray();
        }
        private void FfsImage()
        {
            var header = Block(10, 2); Put(header, 4, 10); Put(header, 8, 72);
            Put(header, 324, 80 * 512); Put(header, 504, 11); Put(header, 508, unchecked((uint)-3));
            var extension = Block(11, 16); Put(extension, 4, 11); Put(extension, 8, 8);
            Put(extension, 500, 10); Put(extension, 508, unchecked((uint)-3));
            for (var i = 0; i < 72; i++) Put(header, 308 - i * 4, 20u + (uint)i);
            for (var i = 0; i < 8; i++) Put(extension, 308 - i * 4, 100u + (uint)i);
            Seal(header); Seal(extension); Payload(20, 72, 0); Payload(100, 8, 72);
        }
        private byte[] SfsBlock(uint block, uint type)
        { var b = Block(block, type); Put(b, 8, block); return b; }
        private void Sfs2Image()
        {
            var count = wide ? 0x1000000u : 256u;
            foreach (var n in new[] { 0u, count - 1 })
            {
                var b = SfsBlock(n, 0x53465302); b[13] = 4; Put(b, 48, count); Put(b, 52, 512);
                Put(b, 104, 3); Put(b, 108, 6); Put(b, 112, 7);
            }
            var root = SfsBlock(3, 0x4F424A43); Put(root, 28, 1); Put(root, 40, 10); root[50] = 128; root[51] = (byte)'V';
            SfsBlock(5, 0x54524F4B);
            var file = SfsBlock(10, 0x4F424A43); Put(file, 12, 1); Put(file, 28, 8);
            Put(file, 36, 40); Put(file, 40, wide ? 0x10000u : 0u);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(44), wide ? (ushort)512 : (ushort)(80 * 512)); file[51] = (byte)'F';
            var nodes = SfsBlock(7, 0x4E444320); Put(nodes, 12, 1); Put(nodes, 16, 1); Put(nodes, 20, 3); Put(nodes, 90, 10);
            var tree = SfsBlock(6, 0x424E4443); tree[13] = wide ? (byte)1 : (byte)2; tree[14] = 1; tree[15] = 16;
            Put(tree, 16, 40); Put(tree, 20, wide ? 0u : 160u); Put(tree, 24, 0x80000008); Put(tree, 28, wide ? 0x800001u : 72u);
            Put(tree, 32, 160); Put(tree, 40, 40); Put(tree, 44, 8);
            foreach (var b in disk.Values) Seal(b, true);
            Payload(40, 72, 0); Payload(160, 8, 72);
        }

        internal void ChangeFirstBlockAndSize()
        {
            disk[200] = Enumerable.Repeat((byte)0xD3, 512).ToArray();
            var file = disk[10];
            if (sfs2)
            {
                Put(file, 36, 200); Put(file, 40, 0); BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(44), 17); Seal(file, true);
                var tree = disk[6]; Put(tree, 32, 200); Put(tree, 36, 0); Put(tree, 40, 0x80000008); Put(tree, 44, 1); Seal(tree, true);
            }
            else { Put(file, 324, 17); Put(file, 308, 200); Seal(file); }
            Bus.WriteLong(State + 12, uint.MaxValue);
        }
    }
}
