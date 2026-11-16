namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Keep geometry outside operation/recovery fields. In particular the final
    // 16 bytes of the mount header already carry ownership/lookup/replay state.
    // CPX4 and CPX5 features 1..3 retain their original values.
    private bool SmallMetadata => P == 512;
    private uint BitmapWindowShift => SmallMetadata ? 11u : 12u;
    private uint BitmapWindowMask => SmallMetadata ? 2047u : 4095u;
    private uint BitmapWindowUnits => BitmapWindowMask + 1;
    private uint BitmapPayloadBytes => BitmapWindowUnits >> 3;
    private uint GroupMapCount => SmallMetadata ? 32u : 16u;
    private uint GroupMetadataPages => GroupMapCount + 1;
    private uint GroupPayloadBytes => SmallMetadata ? 144u : 80u;
    private uint ReservePayloadBytes => SmallMetadata ? 264u : 520u;
}
