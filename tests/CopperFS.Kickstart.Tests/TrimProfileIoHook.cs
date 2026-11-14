using CopperDisk;

namespace CopperFS.Kickstart.Tests;

// Diagnostic counters only; forward every operation to the qualification hook.
internal sealed class TrimProfileIoHook(IAmigaHardfileIoHook inner) : IAmigaHardfileDiscardIoHook
{
    private int reads, writes, scsi, flushes, discards;
    private long readBytes, writeBytes, discardBytes;
    public object Snapshot() => new { Reads = reads, Writes = writes, Scsi = scsi, Flushes = flushes,
        Discards = discards, ReadBytes = readBytes, WriteBytes = writeBytes, DiscardBytes = discardBytes };
    public bool SupportsDiscard => inner is IAmigaHardfileDiscardIoHook { SupportsDiscard: true };
    public uint MaximumDiscardSectors => inner is IAmigaHardfileDiscardIoHook d ? d.MaximumDiscardSectors : 0;
    public bool FlushOnDispose => inner.FlushOnDispose;
    public void ObserveOpen(int unit) => inner.ObserveOpen(unit);
    public void ObserveCommand(ushort command)
    {
        if (command is 2 or 24) reads++;
        if (command is 3 or 25) writes++;
        if (command == 28) scsi++;
        inner.ObserveCommand(command);
    }
    public void Read(FileStream file, long offset, Span<byte> bytes)
    { readBytes += bytes.Length; inner.Read(file, offset, bytes); }
    public void Write(FileStream file, long offset, ReadOnlySpan<byte> bytes)
    { writeBytes += bytes.Length; inner.Write(file, offset, bytes); }
    public void Flush(FileStream file) { flushes++; inner.Flush(file); }
    public void Discard(FileStream file, long offset, int bytes)
    { discards++; discardBytes += bytes; ((IAmigaHardfileDiscardIoHook)inner).Discard(file, offset, bytes); }
}
