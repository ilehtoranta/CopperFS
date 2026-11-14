#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;
public struct NativeCpxMemory : ICpxMemory
{
    public uint Reserved;
    public byte Read8(uint p)=>APTR.ReadUInt8(APTR.FromPointer(p),0);
    public uint Read32(uint p)=>APTR.ReadUInt32(APTR.FromPointer(p),0);
    public void Write8(uint p,byte v)=>APTR.WriteUInt8(APTR.FromPointer(p),0,v);
    public void Write32(uint p,uint v)=>APTR.WriteUInt32(APTR.FromPointer(p),0,v);
}
#endif
