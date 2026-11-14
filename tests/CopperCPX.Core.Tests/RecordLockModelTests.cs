using CopperCPX.Qualification;

namespace CopperCPX.Tests;

public class RecordLockModelTests
{
    private sealed record Grant(int Owner, int File, ulong Offset, ulong Length, uint Mode);

    [Theory] [InlineData(0x12345678u)] [InlineData(0xc0ffeeu)] [InlineData(0x7654321u)] [InlineData(0xffffffffu)]
    public void GeneratedClassicAndWideRangesMatchAnIndependentCooperativeLockModel(uint seed)
    {
        using var d = new DispatcherDriver(512);
        var grants = new List<Grant>(); uint[] handles = new uint[4];
        for (int n = 0; n < handles.Length; n++) handles[n] = RecordLockPacketTests.Open(d, "file" + n % 2);
        uint random = seed;
        uint Next() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return random; }
        for (int step = 0; step < 512; step++)
        {
            int owner = (int)(Next() % 4), action = (int)(Next() % 10);
            if (action == 0)
            {
                RecordLockPacketTests.Close(d, handles[owner]); grants.RemoveAll(x => x.Owner == owner);
                handles[owner] = RecordLockPacketTests.Open(d, "file" + owner % 2); continue;
            }
            bool wide = (Next() & 1) != 0;
            ulong offset = Next() % 24, length = Next() % 12;
            if (wide) offset += (Next() % 3) * 0x100000000ul;
            uint mode = Next() % 4, error = 0, packetAction = wide ? 26402u : 2008u;
            if (action < 4)
            {
                // Mix exact releases with misses; recursive identical grants are independent.
                var owned = grants.Where(x => x.Owner == owner && (wide || x.Offset <= uint.MaxValue)).ToArray();
                if (owned.Length != 0 && (Next() & 1) != 0)
                { var selected = owned[Next() % (uint)owned.Length]; offset = selected.Offset; length = selected.Length; }
                packetAction = wide ? 26403u : 2009u;
                int index = grants.FindIndex(x => x.Owner == owner && x.Offset == offset && x.Length == length);
                if (index < 0) error = 240; else grants.RemoveAt(index);
            }
            else
            {
                bool conflict = grants.Any(x => x.Owner != owner && x.File == owner % 2 &&
                    ((x.Mode & mode & 2) == 0) && x.Length != 0 && length != 0 &&
                    offset < x.Offset + x.Length && x.Offset < offset + length);
                if (conflict) error = (mode & 1) != 0 ? 241u : 242u;
                else if (grants.Count == 32) error = 103;
                else grants.Add(new(owner, owner % 2, offset, length, mode));
            }
            (uint Result, uint Error) actual;
            if (wide)
            {
                d.Ram.Write32(92000, (uint)(offset >> 32)); d.Ram.Write32(92004, (uint)offset);
                d.Ram.Write32(92008, (uint)(length >> 32)); d.Ram.Write32(92012, (uint)length);
                actual = d.Packet(packetAction, handles[owner], 92000, 92008, mode);
            }
            else actual = d.Packet(packetAction, handles[owner], (uint)offset, (uint)length, mode);
            Assert.True(actual == (error == 0 ? uint.MaxValue : 0u, error),
                $"seed={seed}; step={step}; packet={packetAction}; owner={owner}; offset={offset}; length={length}; mode={mode}; expectedError={error}; actual={actual}");
            Assert.False(d.Dispatcher.Busy);
        }
        foreach (uint handle in handles) RecordLockPacketTests.Close(d, handle);
        Assert.False(d.Dispatcher.HasOpenResources);
    }
}
