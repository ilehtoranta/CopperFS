using System.Buffers.Binary;
using CopperDisk;

namespace CopperSFS.Sfs2Reference;

internal sealed class NativeRedoCut : Exception;

// A bounded verification fixture: persist every preceding write, then interrupt
// immediately after the original handler publishes its native TRFA marker.
internal sealed class NativeRedoCapture : IAmigaHardfileIoHook
{
    internal bool Captured;
    internal long Writes;
    public bool FlushOnDispose => !Captured;
    public void Read(FileStream backing, long offset, Span<byte> target)
    { backing.Position = offset; backing.ReadExactly(target); }
    public void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
    {
        backing.Position = offset; backing.Write(source); Writes++;
        if (offset != 5 * 512 || source.Length != 512 || BinaryPrimitives.ReadUInt32BigEndian(source) != 0x54524641 ||
            BinaryPrimitives.ReadUInt32BigEndian(source[12..]) == 0) return;
        backing.Flush(true); Captured = true; throw new NativeRedoCut();
    }
    public void Flush(FileStream backing) => backing.Flush(true);
}
