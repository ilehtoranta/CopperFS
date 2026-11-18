using System.Reflection;
using System.Text;
using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class LookupObjectTests
{
    private static readonly MethodInfo Begin = typeof(CpxEngine<Ram>).GetMethod("BeginLookupObject", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo Entry = typeof(CpxEngine<Ram>).GetProperty("LookupObjectEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(512, false)] [InlineData(512, true)] [InlineData(4096, false)] [InlineData(4096, true)]
    public void FusedLookupMatchesSeparateReadsForColdWarmLongAndAliasNames(int unit, bool catalogue)
    {
        var disk = new MemoryDevice(); var ram = new Ram(new byte[1000000]);
        Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, unit, new byte[49152], 12345, true, catalogue);
        var engine = new CpxEngine<Ram>(ram, 32);
        engine.Initialize(512, (uint)unit, disk.SectorCount, 1, metadataPageBytes: (uint)unit);
        engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes((uint)unit));
        engine.EnableShadowVolume(900000, 32768, catalogue ? 5u : 4u);
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine, ram, disk);
        string[] names = ["F\u00edle.bin", new string('n', 100) + "\u00e9"];
        foreach (string name in names)
        {
            byte[] encoded = Encoding.Latin1.GetBytes(name); encoded.CopyTo(ram.Bytes, 100000);
            Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, (uint)encoded.Length));
            EngineTests.Drive(ref engine, ram, disk);
        }
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        foreach (string name in names)
        {
            byte[] encoded = Encoding.Latin1.GetBytes(name); encoded.CopyTo(ram.Bytes, 100000);
            engine.BeginLookup(Wide.From(1), 100000, (uint)encoded.Length); EngineTests.Drive(ref engine, ram, disk);
            Assert.True(engine.LookupFound);
            Wide id = engine.LookupId;
            Wide entry = new(ram.Read32(engine.TreeRecord + 8), ram.Read32(engine.TreeRecord + 12));
            engine.BeginOperation(CpxOperation.ReadObject, id); EngineTests.Drive(ref engine, ram, disk);
            byte[] expected = ram.Bytes.AsSpan((int)engine.ObjectRecord, 256).ToArray();
            foreach (bool cold in new[] { false, true })
            foreach (string spelling in new[] { name.ToLowerInvariant(), $"__u{(ulong)entry:x16}" })
            {
                if (cold) { engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk); }
                encoded = Encoding.Latin1.GetBytes(spelling); encoded.CopyTo(ram.Bytes, 100000);
                Assert.Equal(CpxError.None, (CpxError)Begin.Invoke(engine, [Wide.From(1), 100000u, (uint)encoded.Length])!);
                byte[] pending = (byte[])ram.Bytes.Clone();
                Assert.Equal(CpxError.Busy, engine.BeginOperation(CpxOperation.ReadObject, Wide.From(1)));
                Assert.Equal(pending, ram.Bytes);
                EngineTests.Drive(ref engine, ram, disk);
                Assert.True(engine.LookupFound); Assert.Equal(id, engine.LookupId); Assert.True(engine.ObjectFound);
                Assert.Equal(entry, (Wide)Entry.GetValue(engine)!);
                Assert.Equal(expected, ram.Bytes.AsSpan((int)engine.ObjectRecord, 256).ToArray());
            }
            // An ordinary public lookup must still finish as a lookup.
            encoded = Encoding.Latin1.GetBytes(name); encoded.CopyTo(ram.Bytes, 100000);
            engine.BeginLookup(Wide.From(1), 100000, (uint)encoded.Length); EngineTests.Drive(ref engine, ram, disk);
            Assert.True(engine.LookupFound); Assert.False(engine.ObjectFound);
        }
        "missing"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, (CpxError)Begin.Invoke(engine, [Wide.From(1), 100000u, 7u])!);
        EngineTests.Drive(ref engine, ram, disk); Assert.False(engine.LookupFound); Assert.False(engine.ObjectFound);
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CancellationBeforeLookupCannotChangeTheNextNamespaceOperation(bool cold)
    {
        var disk = new MemoryDevice(); var ram = new Ram(new byte[1000000]);
        Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, true);
        var engine = new CpxEngine<Ram>(ram, 32); engine.Initialize(512, 512, disk.SectorCount, 1, metadataPageBytes: 512);
        engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)); engine.EnableShadowVolume(900000, 32768, 5);
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine, ram, disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        if (cold) { engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk); }
        Assert.Equal(CpxError.None, (CpxError)Begin.Invoke(engine, [Wide.From(1), 100000u, 4u])!);
        engine.Cancel(); Assert.Equal(StepResult.Failed, engine.Step()); Assert.Equal(CpxError.Cancelled, engine.Error);
        Assert.Equal(CpxError.NotMounted, engine.BeginCreate(Wide.From(1), 100000, 4));
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4));
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step();
            if (step == StepResult.Failed) { Assert.Equal(CpxError.AlreadyExists, engine.Error); new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check(); return; }
            Assert.NotEqual(StepResult.Complete, step);
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); engine.CompleteIo(io.Token, io.Bytes, true);
        }
        Assert.Fail("The duplicate creation did not terminate.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RejectedLookupCannotChangeTheNextNamespaceOperation(bool cold)
    {
        var disk = new MemoryDevice(); var ram = new Ram(new byte[1000000]);
        Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, true);
        var engine = new CpxEngine<Ram>(ram, 32); engine.Initialize(512, 512, disk.SectorCount, 1, metadataPageBytes: 512);
        engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)); engine.EnableShadowVolume(900000, 32768, 5);
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine, ram, disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        Wide file = engine.MutatedObject;
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        if (cold) { engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk); }
        Assert.Equal(CpxError.None, (CpxError)Begin.Invoke(engine, [file, 100000u, 4u])!);
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step(); Assert.NotEqual(StepResult.Complete, step);
            if (step == StepResult.Failed) { Assert.Equal(CpxError.WrongType, engine.Error); break; }
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); engine.CompleteIo(io.Token, io.Bytes, true);
        }
        Assert.Equal(CpxError.WrongType, engine.Error); Assert.True(engine.IsMounted);
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4));
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step(); Assert.NotEqual(StepResult.Complete, step);
            if (step == StepResult.Failed) { Assert.Equal(CpxError.AlreadyExists, engine.Error); new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check(); return; }
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); engine.CompleteIo(io.Token, io.Bytes, true);
        }
        Assert.Fail("The duplicate creation did not terminate.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CancellationAtLookupToObjectTransitionKeepsTheExistingBoundary(bool cold)
    {
        var disk = new MemoryDevice(); var ram = new Ram(new byte[1000000]);
        Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, true);
        var engine = new CpxEngine<Ram>(ram, 32); engine.Initialize(512, 512, disk.SectorCount, 1, metadataPageBytes: 512);
        engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)); engine.EnableShadowVolume(900000, 32768, 5);
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine, ram, disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        if (cold) { engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk); }
        // One phase per visit makes the transition observable to the caller.
        ram.Write32(32 + 4016, ram.Read32(32 + 4016) & ~2u);
        Assert.Equal(CpxError.None, (CpxError)Begin.Invoke(engine, [Wide.From(1), 100000u, 4u])!);
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step(); Assert.NotEqual(StepResult.Failed, step); Assert.NotEqual(StepResult.Complete, step);
            if (ram.Read32(32 + 60) == (uint)CpxOperation.ReadObject)
            {
                engine.Cancel(); Assert.Equal(StepResult.Failed, engine.Step()); Assert.Equal(CpxError.Cancelled, engine.Error);
                new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check(); return;
            }
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); engine.CompleteIo(io.Token, io.Bytes, true);
        }
        Assert.Fail("Lookup did not reach object validation.");
    }
}
