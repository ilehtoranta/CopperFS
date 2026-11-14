using CopperCPX.Tests;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Reference.Tests;

// Adapted from this repository's Prototype.Tests/AdmissionTests.Exhausted
// (MIT). Transfer allocated reserve units to a reachable file, preserving the
// ownership oracle; do not manufacture exhaustion by leaking allocations.
internal static class ExhaustedVolume
{
    internal static byte[] Create(bool rootOwner=false)
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        Assert.Equal(MutationResult.Success,v.Create("full"u8,out ulong id));
        if(rootOwner) Assert.Equal(MutationResult.Success,v.SetOwner(1,0x1234abcd));
        int length=checked((int)v.Check().FreeUnits*512);
        Assert.Contains(v.Append(id,0,new byte[length],out _),new[]{MutationResult.Success,MutationResult.DiskFull});
        var l=v.Geometry; Assert.Equal(0UL,v.Check().FreeUnits);
        Span<byte> reserve=disk.Bytes.AsSpan((int)(l.Reserve*512),l.PageBytes);
        int count=U16(reserve,64); Assert.True(count>=12);
        Span<byte> leaf=disk.Bytes.AsSpan((int)(l.DataStart*512),l.PageBytes),obj=leaf.Slice(320,256);
        Assert.Equal(id,U64(obj,0)); Assert.Equal(2,obj[85]);
        Span<byte> extents=disk.Bytes.AsSpan((int)(U64(obj,24)*512),l.PageBytes); int n=U16(extents,48); Assert.Equal(0,U16(extents,50)); Assert.True(n<40);
        ulong logical=DivideUp(U64(obj,8),512);
        for(int i=0;i<count;)
        {
            ulong start=U64(reserve,72+i*8); int run=1;
            while(i+run<count&&U64(reserve,72+(i+run)*8)==start+(uint)run*l.Q) run++;
            Assert.True(n<40); Span<byte> extra=extents.Slice(64+n++*24,24); extra.Clear();
            Put64(extra,0,logical); Put64(extra,8,start); Put32(extra,16,checked((uint)((uint)run*l.Q))); logical+=(uint)run*l.Q; i+=run;
        }
        Put16(extents,48,(ushort)n); Seal(extents);
        Put64(obj,8,logical*512); Put64(obj,16,U64(obj,16)+(uint)count*l.Q); Seal(leaf);
        reserve[64..].Clear(); Seal(reserve);
        var check=new Cpx4Volume<Disk>(disk,new byte[36864]); Assert.Equal(0,check.AvailableReservePages); check.Check(); new IndependentImage(disk.Bytes);
        return disk.Bytes;
    }
}
