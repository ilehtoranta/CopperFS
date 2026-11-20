using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;

namespace CopperNova.Tests;

public class NamespaceTests
{
    [Fact]
    public void MultipleFreeSlotsSurviveRemountAndSustainedChurn()
    {
        var h=new Harness(NovaImage.FormatPacked(2*1024*1024,reusableObjects:true)); h.Mount();
        foreach(string name in new[]{"first","second","survivor"}) h.Close(h.Open(name));
        h.Flush();
        var original=NovaImage.CheckPacked(h.Disk);
        uint Id(string name)=>original.Entries.Single(e=>e.Path=="/"+name).Slot;
        void Delete(string name) {
            h.Bstr(Harness.NameAt,name);
            Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        }
        Delete("first"); Delete("second"); h.Flush(); h.InhibitAndRemount();
        for(int i=0;i<1030;i++) { h.Close(h.Open("temporary")); Delete("temporary"); }
        h.Close(h.Open("x")); h.Close(h.Open("y")); h.Flush(); h.InhibitAndRemount();
        var final=NovaImage.CheckPacked(h.Disk);
        Assert.Equal(Id("second"),final.Entries.Single(e=>e.Path=="/x").Slot);
        Assert.Equal(Id("first"),final.Entries.Single(e=>e.Path=="/y").Slot);
        Assert.Equal(Id("survivor"),final.Entries.Single(e=>e.Path=="/survivor").Slot);
    }

    [Fact]
    public void MoveEnumerateDeleteAndReuseAcrossColdMounts()
    {
        var h = new Harness(NovaImage.FormatPacked(2 * 1024 * 1024, reusableObjects: true));
        h.Mount();
        uint Name(string value) { h.Bstr(Harness.NameAt, value); return Harness.NameAt >> 2; }
        uint Mk(string value) => h.Packet(22, 0, Name(value));
        uint Delete(string value) => h.Packet(16, 0, Name(value));
        uint Move(string from, string to) {
            h.Bstr(Harness.SecondName, to);
            return h.Packet(17, 0, Name(from), 0, Harness.SecondName >> 2);
        }
        void Unlock(uint l) { Assert.NotEqual(0u,l); Assert.Equal(uint.MaxValue,h.Packet(15,l)); }
        Unlock(Mk("a")); Unlock(Mk("b")); Unlock(Mk("a/sub"));
        uint firstSector=0, reusedSector=0;
        h.BeforeIo=io=>{ if(io.Kind==CopperCPX.IoKind.Write&&io.Bytes==4096) firstSector=io.Sector.Low; };
        uint file = h.Open("a/sub/x");
        h.Write(file, new byte[4096]); h.Close(file);
        Assert.True(firstSector>=1394); h.BeforeIo=null;
        h.Bstr(Harness.SecondName,"old comment");
        Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Name("a/sub/x"),Harness.SecondName>>2));
        h.Flush();
        Assert.Equal(0u, Move("a","a/sub/bad"));
        Assert.Equal(0u, Delete("a")); Assert.Equal(216u,h.Engine.DosError);
        Assert.Equal(uint.MaxValue,Move("a/sub","b/sub"));
        Assert.Equal(uint.MaxValue,Move("b/sub/x","a/moved"));
        uint held = h.Packet(8,0,Name("a/moved"),0xfffffffe);
        Assert.Equal(0u,Delete("a/moved")); Assert.Equal(202u,h.Engine.DosError); Unlock(held);
        h.Flush(); h.InhibitAndRemount();
        uint dir = h.Packet(8,0,Name("a"),0xfffffffe), fib = Harness.Data >> 2;
        Assert.Equal(uint.MaxValue,h.Packet(23,dir,fib));
        Assert.Equal(uint.MaxValue,h.Packet(24,dir,fib));
        Assert.Equal("moved",System.Text.Encoding.ASCII.GetString(h.Memory.Bytes,(int)Harness.Data+9,5));
        Assert.Equal(4096u,h.Memory.Read32(Harness.Data+124));
        Assert.Equal(0u,h.Packet(24,dir,fib)); Assert.Equal(232u,h.Engine.DosError);
        Unlock(dir);
        var before = NovaImage.CheckPacked(h.Disk);
        var previous = before.Entries.Single(e=>e.Path=="/a/moved");
        Assert.Equal("old comment",previous.Comment);
        Assert.Equal(uint.MaxValue,Delete("a/moved"));
        h.Flush(); h.InhibitAndRemount();
        var empty = NovaImage.CheckPacked(h.Disk);
        Assert.Equal(before.FreeSectors+8,empty.FreeSectors);
        h.BeforeIo=io=>{ if(io.Kind==CopperCPX.IoKind.Write&&io.Bytes==4096) reusedSector=io.Sector.Low; };
        uint replacement=h.Open("b/reused"); h.Write(replacement,new byte[4096]); h.Close(replacement);
        h.Flush(); h.InhibitAndRemount();
        var after=NovaImage.CheckPacked(h.Disk);
        var reused=after.Entries.Single(e=>e.Path=="/b/reused");
        Assert.Equal(firstSector,reusedSector);
        Assert.Equal(previous.Slot,reused.Slot); Assert.Null(reused.Comment);
        Assert.Equal(before.FreeSectors,after.FreeSectors);
    }
}
