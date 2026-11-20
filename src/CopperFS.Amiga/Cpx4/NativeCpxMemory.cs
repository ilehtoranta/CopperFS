#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;
public struct NativeCpxMemory : ICpxMemory
{
    public uint Reserved;
    public void Copy(uint destination,uint source,uint bytes)
    {
        if(destination==source||bytes==0) return;
        bool disjoint=destination<source?bytes<=source-destination:bytes<=destination-source;
        if(bytes>=64&&disjoint)
        {
            if(((destination|source|bytes)&3)==0) Exec.CopyMemQuick(APTR.FromPointer(source),APTR.FromPointer(destination),bytes);
            else Exec.CopyMem(APTR.FromPointer(source),APTR.FromPointer(destination),bytes);
            return;
        }
        CpxMemoryCopy.Forward(ref this,destination,source,bytes);
    }
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
