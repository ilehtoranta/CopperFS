namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Keep geometry outside operation/recovery fields. In particular the final
    // 16 bytes of the mount header already carry ownership/lookup/replay state.
    // CPX4 and CPX5 features 1..3 retain their original values.
    private bool SmallMetadata => (memory.Read32(Arena + (PageBytes))) == 512;
    private uint BitmapWindowShift => ((memory.Read32(Arena + (PageBytes))) == 512) ? 11u : 12u;
    private uint BitmapWindowMask => ((memory.Read32(Arena + (PageBytes))) == 512) ? 2047u : 4095u;
    private uint BitmapWindowUnits => (((memory.Read32(Arena + (PageBytes))) == 512) ? 2047u : 4095u) + 1;
    private uint BitmapPayloadBytes => ((((memory.Read32(Arena + (PageBytes))) == 512) ? 2047u : 4095u) + 1) >> 3;
    private uint GroupMapCount => ((memory.Read32(Arena + (PageBytes))) == 512) ? 32u : 16u;
    private uint GroupMetadataPages => (((memory.Read32(Arena + (PageBytes))) == 512) ? 32u : 16u) + 1;
    private uint GroupPayloadBytes => ((memory.Read32(Arena + (PageBytes))) == 512) ? 144u : 80u;
    private uint ReservePayloadBytes => ((memory.Read32(Arena + (PageBytes))) == 512) ? 264u : 520u;
}
