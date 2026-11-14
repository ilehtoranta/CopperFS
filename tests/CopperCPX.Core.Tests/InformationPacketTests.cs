using System.Text;
using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class InformationPacketTests
{
    private const uint Fib=86000;
    private static string Name(DispatcherDriver d,uint fib=Fib)=>Encoding.Latin1.GetString(d.Ram.Bytes,(int)fib+9,d.Ram.Read8(fib+8));
    private static ulong Pair(DispatcherDriver d,uint p)=>((ulong)d.Ram.Read32(p)<<32)|d.Ram.Read32(p+4);
    [Theory][InlineData(512)][InlineData(4096)]
    public void EnumeratesSplitDirectoriesNamesCommentsWideSizesAndAliases(int unit)
    {
        using var d=new DispatcherDriver(unit); var v=new Cpx4Volume<Disk>(new Disk{Bytes=d.Image},new byte[36864]);
        var expected=new HashSet<string>();
        for(int n=0;n<40;n++) { string name=$"file{n:D2}"; expected.Add(name); v.Create(Encoding.ASCII.GetBytes(name),out _); }
        v.Create(new byte[]{0xe9,0xc4},out ulong latin); expected.Add("éÄ"); v.SetComment(latin,new byte[]{0xc0,0xff});
        v.Truncate(latin,0x100000123UL); v.SetProtection(latin,16); v.SetDate(latin,new(123,42,51));
        v.Create(Encoding.Latin1.GetBytes(new string('é',100)),out _); expected.Add(new string('é',100));
        v.Create(Encoding.ASCII.GetBytes(new string('q',108)),out _);
        v.CreateUtf8(1,"日本語"u8,false,out _); v.Link(latin,1,"linked"u8); expected.Add("linked");
        d.Execute(new(ActionKind.Remount));
        Assert.Equal((uint.MaxValue,0u),d.Packet(26408,0,Fib>>2)); Assert.Equal("CopperCPX4",Name(d));
        var names=new HashSet<string>(); int aliases=0;
        while(true)
        {
            var answer=d.Packet(26409,0,Fib>>2); if(answer.Error==232) break; Assert.Equal((uint.MaxValue,0u),answer);
            string name=Name(d); Assert.True(names.Add(name));
            if(name.StartsWith("__u"))
            {
                aliases++; uint locked=d.Packet(8,0,d.Bstr(name),0xfffffffe).Result; Assert.NotEqual(0u,locked);
                Assert.Equal((uint.MaxValue,0u),d.Packet(23,locked,(Fib+512)>>2)); Assert.Equal(name,Name(d,Fib+512)); d.Packet(15,locked);
            }
            else Assert.Contains(name,expected);
            if(name is "éÄ" or "linked")
            {
                Assert.Equal(0x100000123UL,Pair(d,Fib+228)); Assert.Equal(0u,d.Ram.Read32(Fib+124));
                Assert.Equal(0UL,Pair(d,Fib+236)); Assert.Equal(16u,d.Ram.Read32(Fib+116));
                Assert.Equal(new byte[]{2,0xc0,0xff},d.Ram.Bytes[((int)Fib+144)..((int)Fib+147)]);
                Assert.Equal(123u,d.Ram.Read32(Fib+132)); Assert.Equal(42u,d.Ram.Read32(Fib+136)); Assert.Equal(51u,d.Ram.Read32(Fib+140));
            }
        }
        Assert.Equal(expected.Count+2,names.Count); Assert.Equal(2,aliases); Assert.Equal((0u,232u),d.Packet(24,0,Fib>>2));
        foreach(string name in new[]{"éÄ","linked"})
        {
            uint l=d.Packet(8,0,d.Bstr(name),0xfffffffe).Result; Assert.NotEqual(0u,l);
            Assert.Equal((uint.MaxValue,0u),d.Packet(26408,l,Fib>>2)); Assert.Equal(name,Name(d));
            uint copy=d.Packet(19,l).Result; d.Packet(15,l); Assert.Equal((uint.MaxValue,0u),d.Packet(23,copy,Fib>>2)); Assert.Equal(name,Name(d)); d.Packet(15,copy);
            Assert.Equal((uint.MaxValue,0u),d.Packet(1005,DispatcherDriver.FileHandle>>2,0,d.Bstr(name))); uint h=d.Ram.Read32(DispatcherDriver.FileHandle+36);
            Assert.Equal((uint.MaxValue,0u),d.Packet(26410,h,Fib>>2)); Assert.Equal(name,Name(d)); d.Packet(1007,h);
        }
        Assert.False(d.Dispatcher.HasOpenResources); new IndependentImage(d.Image);
    }
    [Fact] public void TokensAreBoundedReleasedAndRejectStaleOrForeignCursors()
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file"));
        uint[] keys=new uint[16];
        for(uint n=0;n<16;n++) { uint f=Fib+n*300; d.Packet(23,0,f>>2); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,f>>2)); keys[n]=d.Ram.Read32(f); }
        uint overflow=Fib+16*300; d.Packet(23,0,overflow>>2); Assert.Equal((0u,103u),d.Packet(24,0,overflow>>2));
        // EOF retires the slot. A stale key cannot acquire its later incarnation.
        Assert.Equal((0u,232u),d.Packet(24,0,Fib>>2)); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,overflow>>2));
        d.Ram.Write32(Fib,keys[0]); Assert.Equal((0u,211u),d.Packet(24,0,Fib>>2));
        // Re-examination resets and retires this FIB's own cursor only.
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,overflow>>2)); Assert.Equal(0u,d.Ram.Read32(overflow));
        uint root=d.Packet(19).Result; d.Packet(23,root,Fib>>2); Assert.Equal((uint.MaxValue,0u),d.Packet(24,root,Fib>>2));
        uint stale=d.Ram.Read32(Fib); d.Packet(15,root); root=d.Packet(19).Result; d.Ram.Write32(Fib,stale);
        Assert.Equal((0u,211u),d.Packet(24,root,Fib>>2)); d.Packet(15,root);
        for(uint n=1;n<16;n++) Assert.Equal((0u,232u),d.Packet(24,0,(Fib+n*300)>>2));
        d.Ram.Write32(Fib,0); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,Fib>>2));
        stale=d.Ram.Read32(Fib); d.Dispatcher.Initialize(0x1234,0x5678);
        Assert.Equal((0u,211u),d.Packet(24,0,Fib>>2)); d.Ram.Write32(Fib,0); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,Fib>>2));
        Assert.NotEqual(stale,d.Ram.Read32(Fib));
    }
    [Fact] public void EnumerationReseeksCurrentRootAfterSplitsAndDeletes()
    {
        using var d=new DispatcherDriver(512); for(int n=0;n<8;n++) d.Execute(new(ActionKind.Create,$"old{n}"));
        d.Packet(23,0,Fib>>2); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,Fib>>2)); var seen=new HashSet<string>{Name(d)};
        for(int n=0;n<24;n++) d.Execute(new(ActionKind.Create,$"new{n}"));
        string deleted=Enumerable.Range(0,8).Select(n=>$"old{n}").First(n=>!seen.Contains(n)); d.Execute(new(ActionKind.Delete,deleted));
        for(int n=0;n<40;n++)
        { var answer=d.Packet(24,0,Fib>>2); if(answer.Error==232) break; Assert.Equal((uint.MaxValue,0u),answer); Assert.True(seen.Add(Name(d))); Assert.NotEqual(deleted,Name(d)); }
        Assert.Equal(uint.MaxValue,d.Ram.Read32(Fib)); new IndependentImage(d.Image);
    }
    [Fact] public void VolumeInformationAndAttributesHaveCheckedBuffersAndConsistentUnits()
    {
        using var d=new DispatcherDriver(4096); d.Execute(new(ActionKind.Create,"file")); d.Execute(new(ActionKind.Write,"file",Data:new byte[9000]));
        Assert.Equal((uint.MaxValue,0u),d.Packet(25,Fib>>2));
        uint total=d.Ram.Read32(Fib+12),used=d.Ram.Read32(Fib+16); Assert.Equal(1024u,total); Assert.InRange(used,1u,total);
        Assert.Equal(4096u,d.Ram.Read32(Fib+20)); Assert.Equal(CpxArena.Magic,d.Ram.Read32(Fib+24)); Assert.Equal(82u,d.Ram.Read32(Fib+8));
        Assert.Equal((uint.MaxValue,0u),d.Packet(26,0,(Fib+100)>>2)); Assert.Equal(d.Ram.Bytes[(int)Fib..((int)Fib+36)],d.Ram.Bytes[((int)Fib+100)..((int)Fib+136)]);
        foreach(uint attribute in new uint[]{0,2,3,4,6,7})
        {
            Assert.Equal((uint.MaxValue,0u),d.Packet(26407,attribute,Fib+200,8));
            if(attribute==6) Assert.Equal((ulong)total,Pair(d,Fib+200)); if(attribute==7) Assert.Equal((ulong)used,Pair(d,Fib+200));
        }
        Assert.Equal((uint.MaxValue,0u),d.Packet(26407,1,Fib,8)); Assert.Equal(30u,d.Ram.Read32(Fib));
        byte[] before=d.Ram.Bytes[(int)Fib..((int)Fib+260)];
        Assert.Equal((0u,120u),d.Packet(26407,2,Fib,4));
        Assert.Equal((0u,115u),d.Packet(26407,2,uint.MaxValue-3,8)); Assert.Equal((0u,211u),d.Packet(26,123,Fib>>2));
        Assert.Equal((0u,115u),d.Packet(25,0x3fffffff)); Assert.Equal(before,d.Ram.Bytes[(int)Fib..((int)Fib+260)]);
    }
    [Fact] public void InvalidStoredUtf8DoesNotPublishPartialFibOrLeakCursor()
    {
        using var d=new DispatcherDriver(512); string longName=new('x',100); d.Execute(new(ActionKind.Create,longName));
        var image=new IndependentImage(d.Image);
        ulong namePage=image.Metadata.Single(at=>CopperFS.Cpx4.Reference.Layout.U16(d.Image.AsSpan((int)at*512),4)==17);
        Span<byte> page=d.Image.AsSpan((int)namePage*512,1024); page[68]=0xc0; CopperFS.Cpx4.Reference.Layout.Seal(page);
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,Fib>>2)); byte[] before=d.Ram.Bytes[(int)Fib..((int)Fib+260)];
        for(int n=0;n<20;n++) { Assert.Equal((0u,213u),d.Packet(24,0,Fib>>2)); Assert.Equal(before,d.Ram.Bytes[(int)Fib..((int)Fib+260)]); }
        page[68]=(byte)'x'; CopperFS.Cpx4.Reference.Layout.Seal(page);
        Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,Fib>>2)); Assert.Equal(longName,Name(d));
    }
    [Fact] public void EmptyAndNonDirectoryEnumerationReleaseTheirAdmissionSlots()
    {
        using var d=new DispatcherDriver(512); uint empty=d.Packet(22,0,d.Bstr("empty")).Result;
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,empty,Fib>>2)); Assert.Equal((0u,232u),d.Packet(24,empty,Fib>>2)); d.Packet(15,empty);
        d.Execute(new(ActionKind.Create,"file")); uint file=d.Packet(8,0,d.Bstr("file"),0xfffffffe).Result;
        for(int n=0;n<20;n++) { Assert.Equal((uint.MaxValue,0u),d.Packet(23,file,Fib>>2)); Assert.Equal((0u,212u),d.Packet(24,file,Fib>>2)); }
        d.Packet(15,file); Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,Fib>>2)); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,Fib>>2));
    }
    [Fact] public void ReadOnlyMountReportsWriteProtectedVolume()
    {
        using var d=new DispatcherDriver(512); var disk=new Disk{Bytes=d.Image}; var e=new CpxEngine<Ram>(d.Ram,32);
        Assert.Equal(CpxError.None,e.Initialize(512,512,Wide.From(8192),2,true)); e.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref e,d.Ram,disk);
        d.Dispatcher=new(d.Ram,32); d.Dispatcher.Initialize(0x1234,0x5678);
        Assert.Equal((uint.MaxValue,0u),d.Packet(25,Fib>>2)); Assert.Equal(80u,d.Ram.Read32(Fib+8));
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,Fib>>2)); Assert.Equal((0u,232u),d.Packet(24,0,Fib>>2));
    }
}
