using CopperFS.Cpx4.Reference;

namespace CopperCPX.Reference.Tests;

internal class MemoryDevice : IBlockDevice
{
    internal readonly byte[] Bytes;
    public int SectorSize { get; }
    public ulong SectorCount => (ulong)Bytes.Length / (uint)SectorSize;
    internal int Reads, Writes, Flushes, Discards;
    internal readonly List<(ulong First, uint Count)> Discarded = [];
    public bool SupportsDiscard => true;
    public uint MaximumDiscardSectors => 4096;
    public uint DiscardAlignmentSectors => 8;
    internal MemoryDevice(int sector = 512, int megabytes = 4) { SectorSize = sector; Bytes = new byte[megabytes * 1024 * 1024]; }
    internal MemoryDevice(byte[] image, int sector = 512) { SectorSize = sector; Bytes = (byte[])image.Clone(); }
    public virtual void Read(ulong sector, Span<byte> destination)
    { Reads++; Bytes.AsSpan(checked((int)(sector * (uint)SectorSize)), destination.Length).CopyTo(destination); }
    public virtual void Write(ulong sector, ReadOnlySpan<byte> source)
    { Writes++; source.CopyTo(Bytes.AsSpan(checked((int)(sector * (uint)SectorSize)), source.Length)); }
    public virtual void Flush() => Flushes++;
    public void Discard(ulong sector, uint count)
    { Discards++; Discarded.Add((sector, count)); Bytes.AsSpan(checked((int)(sector * (uint)SectorSize)), checked((int)count * SectorSize)).Fill(0xa5); }
}
internal sealed class PowerLoss : IOException;

// One candidate image per run, not an exponential collection of multi-MiB snapshots.
// Every sector write and both sides of every barrier are independently crashable.
internal sealed class CrashDevice : MemoryDevice
{
    internal readonly byte[] Durable;
    // Keep every submitted version. Collapsing by LBA hides crashes which
    // persist an older outstanding write while losing its replacement.
    private readonly List<(ulong Sector, byte[] Bytes)> pending = [];
    internal int PendingVersions => pending.Count;
    internal int[]? PersistenceOrder;
    internal readonly List<string> Events = [];
    internal int Cut = -1, Persistence;
    internal CrashDevice(byte[] image, int sector = 512) : base(image, sector) { Durable = (byte[])image.Clone(); }
    private void Event(string label)
    {
        Events.Add(label); if (Events.Count != Cut) return;
        IEnumerable<(ulong Sector, byte[] Bytes)> selected = PersistenceOrder is not null
            ? PersistenceOrder.Select(i => pending[i])
            : Persistence switch
        { 0 => [], 1 => pending.AsEnumerable().Reverse(), 2 => pending.AsEnumerable().Reverse().Where((_, i) => i % 2 == 0), 3 => pending.TakeLast(1), _ => pending.AsEnumerable().Reverse() };
        foreach (var (sector, bytes) in selected)
        {
            int count = Persistence == 4 ? Math.Min(128, SectorSize) : SectorSize;
            bytes.AsSpan(0, count).CopyTo(Durable.AsSpan(checked((int)(sector * (uint)SectorSize)), count));
        }
        throw new PowerLoss();
    }
    public override void Write(ulong sector, ReadOnlySpan<byte> source)
    {
        if (source.Length % SectorSize != 0) throw new InvalidOperationException("Partial-sector request.");
        for (int offset = 0; offset < source.Length; offset += SectorSize)
        {
            Event($"before write {sector}");
            var bytes = source.Slice(offset, SectorSize).ToArray();
            bytes.CopyTo(Bytes, checked((int)(sector * (uint)SectorSize))); pending.Add((sector, bytes));
            Event($"write {sector}"); sector++;
        }
    }
    public override void Flush()
    {
        Event("before flush");
        foreach (var (sector, bytes) in pending) bytes.CopyTo(Durable, checked((int)(sector * (uint)SectorSize)));
        pending.Clear(); Event("after flush");
    }
}
