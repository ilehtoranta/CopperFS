using System.Reflection;

namespace CopperCPX.Tests;

public class FastAppendWordTests
{
    private static uint Offset(string name) => (uint)typeof(CpxEngine<Ram>).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
    private static object Invoke(CpxEngine<Ram> engine, string method, params object[] args) =>
        typeof(CpxEngine<Ram>).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, args)!;

    [Theory]
    [InlineData(1u, 0xfffffffeu, 1u, false)]
    [InlineData(1u, 0xffffffffu, 1u, false)]
    [InlineData(2u, 0x000007ffu, 1u, true)]
    [InlineData(2u, 0x00000801u, 17u, false)]
    public void AdmissionPreservesPhysicalCarryAndWindowCarry(uint high, uint low, uint length, bool precedingWindow)
    {
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, 512, new Wide(128, 0), 1, metadataPageBytes: 512));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)));
        uint hint = ram.Read32(32 + Offset("FastPointer")), record = 100000, leaf = 101000, bitmap = 102000, group = 103000, state = 104000;
        ulong id = 0x1234567887654321, next = ((ulong)high << 32 | low) + length;
        const uint count = 1;
        ram.Write32(32 + Offset("Active"), 1); ram.Write32(32 + Offset("StagedCount"), 4);
        ram.Write32(hint, 1); ram.Write32(hint + 4, (uint)(id >> 32)); ram.Write32(hint + 8, (uint)id);
        ram.Write32(hint + 12, record); ram.Write32(hint + 16, leaf); ram.Write32(hint + 20, bitmap);
        ulong window = next & ~2047UL; if (precedingWindow) window -= 2048;
        ram.Write32(hint + 24, (uint)(window >> 32)); ram.Write32(hint + 28, (uint)window);
        ram.Write32(hint + 32, group); ram.Write32(hint + 36, state); ram.Write32(hint + 40, 1);
        ram.Write32(record, (uint)(id >> 32)); ram.Write32(record + 4, (uint)id); ram.Write32(record + 12, length * 512);
        ram.Write32(record + 104, high); ram.Write32(record + 108, low); ram.Write32(record + 112, length);
        ram.Write32(group + 68, 1000); ram.Write32(state + 96, 1); ram.Write32(state + 100, 0);
        uint descriptor = 80 + (((uint)next & 65535) >> 11) * 4;
        ram.Write32(group + descriptor, 1000); ram.Write32(group + descriptor + 4, 1000);
        byte[] before = (byte[])ram.Bytes.Clone();
        Assert.False((bool)Invoke(engine, "BeginFastAppend", id ^ (1UL << 32), (ulong)length * 512, 512u));
        Assert.Equal(before, ram.Bytes);
        if (((uint)next & 65535) < 33)
        {
            Assert.False((bool)Invoke(engine, "BeginFastAppend", id, (ulong)length * 512, 512u));
            Assert.Equal(before, ram.Bytes); // A carried address in the reserved prefix remains forbidden.
            return;
        }
        Assert.True((bool)Invoke(engine, "BeginFastAppend", id, (ulong)length * 512, 512u));
        ulong selected = (ulong)ram.Read32(32 + Offset("NewPhysical")) << 32 | ram.Read32(32 + Offset("NewPhysical") + 4);
        Assert.Equal(next, selected);
        Assert.Equal(precedingWindow ? 502u : 500u, ram.Read32(32 + Offset("Phase")));
        if (precedingWindow) return; // Normal cross-window I/O must authenticate its next bitmap.
        ram.Write32(32 + Offset("FileBytes"), 512);
        Assert.Equal(StepResult.Complete, (StepResult)Invoke(engine, "FinishFastAppend"));
        Assert.Equal(0u, ram.Read32(state + 96)); Assert.Equal(uint.MaxValue, ram.Read32(state + 100));
        Assert.Equal(length + count, ram.Read32(record + 112));
        Assert.Equal((length + count) * 512, ram.Read32(record + 12));
        Assert.Equal(999u, ram.Read32(group + 68));
    }

    [Theory] [InlineData(512u)] [InlineData(4096u)]
    public void CachedReadOffsetCarriesIntoHighPhysicalWord(uint unit)
    {
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, unit, new Wide(128, 0), 1, metadataPageBytes: unit));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(unit)));
        ram.Write32(32 + Offset("Mounted"), 1);
        const ulong physical = 0x1fffffffe, id = 0x1234567887654321;
        Invoke(engine, "ArmReadCapability", id, physical, 4 * unit, 4u, (uint)CpxArena.Shift(unit));
        Assert.Equal(CpxError.None, engine.BeginFileRead((Wide)id, Wide.From(3 * unit), 100000, unit));
        Assert.Equal(StepResult.NeedIo, engine.Step());
        Assert.Equal((physical + 3) * unit / 512, (ulong)engine.Request.Sector);
    }
}
