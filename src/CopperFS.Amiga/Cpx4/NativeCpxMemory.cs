#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;
public struct NativeCpxMemory : ICpxMemory
{
    public uint Reserved;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public uint Read16(uint p)=>APTR.ReadUInt16(APTR.FromPointer(p),0);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Write16(uint p,uint v)=>APTR.WriteUInt16(APTR.FromPointer(p),0,(ushort)v);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint p)=>APTR.ReadUInt8(APTR.FromPointer(p),0);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint p)=>APTR.ReadUInt32(APTR.FromPointer(p),0);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Write8(uint p,byte v)=>APTR.WriteUInt8(APTR.FromPointer(p),0,v);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Write32(uint p,uint v)=>APTR.WriteUInt32(APTR.FromPointer(p),0,v);
}
#endif
