namespace CopperFS.Cpx4.Reference;

/// <summary>LIFO record storage borrowed from the mount's fixed scratch arena.
/// Copies must not escape their using scope. Payload buffers never live on the CPU stack.</summary>
internal ref struct ScratchFrame
{
    internal Span<byte> Bytes;
    private ref int used;
    private readonly int start, end;
    internal ScratchFrame(Span<byte> arena, ref int used, ref int peak, int length)
    {
        if (length < 0 || used > arena.Length - length) throw new InvalidOperationException("Record scratch budget exceeded.");
        this.used = ref used; start = used; end = used + length; used = end;
        if (end > peak) peak = end; Bytes = arena.Slice(start, length);
    }
    public void Dispose()
    {
        if (used != end) throw new InvalidOperationException("Record scratch frames must unwind in LIFO order.");
        used = start;
    }
}
