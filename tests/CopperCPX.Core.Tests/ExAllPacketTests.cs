using System.Text;
using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class ExAllPacketTests
{
    internal const uint Control=85000,Output=100000;
    internal static (uint Result,uint Error) All(DispatcherDriver d,uint bytes=512,uint type=6,uint control=Control,uint locked=0,uint output=Output)
        =>d.Packet(1033,locked,output,bytes,type,control);
    internal static (uint Result,uint Error) End(DispatcherDriver d,uint control=Control,uint locked=0)
        =>d.Packet(1035,locked,0xffffffff,0xffffffff,0xffffffff,control);
    internal static string Text(DispatcherDriver d,uint p)
    {
        if(p==0) return "";
        var bytes=new List<byte>(); for(int n=0;n<256;n++) { byte b=d.Ram.Read8(p+(uint)n); if(b==0) return Encoding.Latin1.GetString(bytes.ToArray()); bytes.Add(b); }
        throw new InvalidDataException("Unterminated ExAll string");
    }
    internal static List<(uint Address,string Name)> Entries(DispatcherDriver d,uint control=Control,uint output=Output,uint bytes=512,uint header=36)
    {
        var entries=new List<(uint,string)>(); uint count=d.Ram.Read32(control),p=output;
        for(uint n=0;n<count;n++)
        {
            Assert.Equal(0u,p&1); Assert.InRange(p,output,output+bytes-header);
            uint name=d.Ram.Read32(p+4); Assert.InRange(name,p+header,output+bytes-1);
            entries.Add((p,Text(d,name))); uint next=d.Ram.Read32(p);
            if(n+1<count) Assert.InRange(next,p+header+2,output+bytes-header); else Assert.Equal(0u,next);
            p=next;
        }
        return entries;
    }
    private static void Files(DispatcherDriver d,int count)
    { for(int n=0;n<count;n++) Assert.Equal(Outcome.Success,d.Execute(new(ActionKind.Create,$"file{n:D2}")).Outcome); }

    [Theory][InlineData(512)][InlineData(4096)]
    public void AllLevelsUseAlignedPointersAndReturnFinalEntriesAlongsideEof(int unit)
    {
        using var d=new DispatcherDriver(unit); Files(d,3);
        Assert.Equal((uint.MaxValue,0u),d.Packet(28,0,0,d.Bstr("file01"),d.Bstr("é comment",DispatcherDriver.TargetAddress)));
        Assert.Equal((uint.MaxValue,0u),d.Packet(21,0,0,d.Bstr("file01"),16));
        d.Ram.Write32(84000,123); d.Ram.Write32(84004,42); d.Ram.Write32(84008,51);
        Assert.Equal((uint.MaxValue,0u),d.Packet(34,0,0,d.Bstr("file01"),84000));
        d.Execute(new(ActionKind.Write,"file01",Data:[1,2,3]));
        for(uint type=1;type<=7;type++)
        {
            d.Ram.Write32(Control+4,0); Array.Fill(d.Ram.Bytes,(byte)0xa5,(int)Output-2,1028);
            Assert.Equal((0u,232u),All(d,1024,type)); Assert.Equal(3u,d.Ram.Read32(Control));
            uint header=type<=4?4+4*type:type==5?32u:type==6?36u:40u;
            var entries=Entries(d,bytes:1024,header:header); Assert.Equal(new[]{"file00","file01","file02"},entries.Select(e=>e.Name).Order());
            uint p=entries.Single(e=>e.Name=="file01").Address;
            if(type>=2) Assert.Equal(0xfffffffdu,d.Ram.Read32(p+8));
            if(type>=3) Assert.Equal(3u,d.Ram.Read32(p+12));
            if(type>=4) Assert.Equal(16u,d.Ram.Read32(p+16));
            if(type>=5) { Assert.Equal(123u,d.Ram.Read32(p+20)); Assert.Equal(42u,d.Ram.Read32(p+24)); Assert.Equal(51u,d.Ram.Read32(p+28)); }
            if(type>=6) { Assert.Equal("é comment",Text(d,d.Ram.Read32(p+32))); Assert.Equal(0u,d.Ram.Read32(entries.Single(e=>e.Name=="file00").Address+32)); }
            Assert.Equal(uint.MaxValue,d.Ram.Read32(Control+4)); Assert.Equal((0u,232u),All(d,1024,type)); Assert.Equal(0u,d.Ram.Read32(Control));
            Assert.Equal(0xa5,d.Ram.Read8(Output-1)); Assert.Equal(0xa5,d.Ram.Read8(Output+1024));
        }
        new IndependentImage(d.Image);
    }
    [Fact] public void ExactCapacitySmallBuffersAndEndNeverSkipAnEntryOrTouchEndBuffer()
    {
        using var d=new DispatcherDriver(512); Files(d,3);
        Assert.Equal((0u,103u),All(d,13,1)); Assert.Equal(0u,d.Ram.Read32(Control));
        d.Ram.Write32(Control+4,0); var seen=new HashSet<string>();
        for(int n=0;n<3;n++)
        {
            var answer=All(d,16,1,output:Output+(uint)n*100);
            Assert.Equal(n==2?(0u,232u):(uint.MaxValue,0u),answer);
            Assert.Single(Entries(d,output:Output+(uint)n*100,bytes:16,header:8));
            Assert.True(seen.Add(Text(d,d.Ram.Read32(Output+(uint)n*100+4))));
        }
        d.Ram.Write32(Control+4,0); Assert.Equal((uint.MaxValue,0u),All(d,16,1));
        byte[] before=(byte[])d.Ram.Bytes.Clone(); Assert.Equal((uint.MaxValue,0u),End(d));
        Assert.Equal(before.AsSpan((int)Output,512).ToArray(),d.Ram.Bytes.AsSpan((int)Output,512).ToArray());
        Assert.Equal(uint.MaxValue,d.Ram.Read32(Control+4)); Assert.Equal((uint.MaxValue,0u),End(d));
    }
    [Fact] public void ExNextAndExAllShareSixteenSlotsAndRejectForeignAndStaleTokens()
    {
        using var d=new DispatcherDriver(512); Files(d,3); uint[] keys=new uint[8];
        for(uint n=0;n<8;n++)
        {
            uint c=Control+n*20,f=87000+n*300;
            Assert.Equal((uint.MaxValue,0u),All(d,16,1,c)); keys[n]=d.Ram.Read32(c+4);
            Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,f>>2)); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,f>>2));
        }
        Assert.Equal((0u,103u),All(d,16,1,Control+160));
        Assert.Equal((uint.MaxValue,0u),End(d)); Assert.Equal((uint.MaxValue,0u),All(d,16,1,Control+160));
        d.Ram.Write32(Control+4,keys[0]); Assert.Equal((0u,211u),All(d,16,1));
        d.Ram.Write32(Control+180+4,keys[1]); Assert.Equal((0u,211u),End(d,Control+180));
        // A FIB with a forged key/control address cannot consume an ExAll slot.
        d.Ram.Write32(Control+20,keys[1]); Assert.Equal((0u,211u),d.Packet(24,0,(Control+20)>>2));
        for(uint n=1;n<8;n++) Assert.Equal((uint.MaxValue,0u),End(d,Control+n*20));
        Assert.Equal((uint.MaxValue,0u),End(d,Control+160));
        for(uint n=0;n<8;n++) Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,(87000+n*300)>>2));
        for(uint n=0;n<16;n++) { uint c=Control+n*20; d.Ram.Write32(c+4,0); Assert.Equal((uint.MaxValue,0u),All(d,16,1,c)); }
        for(uint n=0;n<16;n++) Assert.Equal((uint.MaxValue,0u),End(d,Control+n*20));
    }
    [Fact] public void CopiedLocksContinueAndOwnCleanupAfterOriginalUnlock()
    {
        using var d=new DispatcherDriver(512); Files(d,3); uint first=d.Packet(19).Result,second=d.Packet(19,first).Result;
        Assert.Equal((uint.MaxValue,0u),All(d,16,1,locked:first)); uint key=d.Ram.Read32(Control+4);
        Assert.Equal((uint.MaxValue,0u),d.Packet(15,first));
        Assert.Equal((uint.MaxValue,0u),All(d,16,1,locked:second)); Assert.Equal(key,d.Ram.Read32(Control+4));
        Assert.Equal((uint.MaxValue,0u),d.Packet(15,second));
        uint replacement=d.Packet(19).Result; Assert.Equal((0u,211u),All(d,16,1,locked:replacement));
        d.Ram.Write32(Control+4,0); Assert.Equal((uint.MaxValue,0u),All(d,16,1,locked:replacement));
        Assert.Equal((uint.MaxValue,0u),End(d,locked:replacement)); Assert.Equal((uint.MaxValue,0u),d.Packet(15,replacement));
    }
    [Fact] public void MutationReseeksCurrentTreeAndLegacyAliasesRemainUsable()
    {
        using var d=new DispatcherDriver(512); Files(d,8);
        var v=new Cpx4Volume<Disk>(new Disk{Bytes=d.Image},new byte[36864]);
        v.CreateUtf8(1,"日本語"u8,false,out _); v.Create(Encoding.Latin1.GetBytes("éÄ"),out _);
        d.Execute(new(ActionKind.Remount));
        Assert.Equal((uint.MaxValue,0u),All(d,64,1)); var names=Entries(d,bytes:64,header:8).Select(e=>e.Name).ToHashSet();
        string removed=Enumerable.Range(0,8).Select(n=>$"file{n:D2}").First(x=>!names.Contains(x)); d.Execute(new(ActionKind.Delete,removed));
        for(int n=0;n<28;n++) d.Execute(new(ActionKind.Create,$"new{n}"));
        for(int n=0;n<100;n++)
        {
            var answer=All(d,64,1); foreach(var e in Entries(d,bytes:64,header:8)) { Assert.True(names.Add(e.Name)); Assert.NotEqual(removed,e.Name); }
            if(answer.Error==232) break; Assert.Equal((uint.MaxValue,0u),answer);
        }
        Assert.Equal(uint.MaxValue,d.Ram.Read32(Control+4)); Assert.Contains("éÄ",names);
        string alias=Assert.Single(names,n=>n.StartsWith("__u")); var locked=d.Packet(8,0,d.Bstr(alias),0xfffffffe); Assert.Equal(0u,locked.Error); d.Packet(15,locked.Result);
        new IndependentImage(d.Image);
    }
    [Fact] public void InvalidTypesPointersAndFiltersDoNotLeakOrSilentlyIgnoreArguments()
    {
        using var d=new DispatcherDriver(512); Files(d,3);
        foreach(uint type in new uint[]{0,8,uint.MaxValue}) Assert.Equal((0u,115u),All(d,type:type));
        Assert.Equal(0u,d.Ram.Read32(Control+4));
        Assert.Equal((0u,115u),All(d,output:Output+1)); Assert.Equal((0u,115u),All(d,output:uint.MaxValue-3));
        Assert.Equal((0u,115u),All(d,output:Control)); Assert.Equal((0u,115u),All(d,control:Control+1));
        Assert.Equal((0u,211u),All(d,locked:123));
        foreach(uint offset in new uint[]{8,12})
        {
            d.Ram.Write32(Control+offset,0x1234); Assert.Equal((0u,209u),All(d)); Assert.Equal(0u,d.Ram.Read32(Control+4));
            d.Ram.Write32(Control+offset,0);
        }
        for(int n=0;n<20;n++)
        {
            d.Ram.Write32(Control+4,0); Assert.Equal((uint.MaxValue,0u),All(d,16,1));
            d.Ram.Write32(Control+12,1234); Assert.Equal((0u,115u),All(d,16,1)); d.Ram.Write32(Control+12,0);
        }
        d.Ram.Write32(Control+4,0); Assert.Equal((uint.MaxValue,0u),All(d,16,1));
        // Reset-to-zero, then fallback, must retire the abandoned cursor too.
        d.Ram.Write32(Control+4,0); d.Ram.Write32(Control+8,1234); Assert.Equal((0u,209u),All(d)); d.Ram.Write32(Control+8,0);
        for(uint n=0;n<16;n++) Assert.Equal((uint.MaxValue,0u),All(d,16,1,Control+n*20));
    }
    [Fact] public void EmptyWrongTypeAndCorruptNameFailuresRetireCursors()
    {
        using var d=new DispatcherDriver(512); Assert.Equal((0u,232u),All(d));
        Files(d,1); uint locked=d.Packet(8,0,d.Bstr("file00"),0xfffffffe).Result;
        for(int n=0;n<20;n++) { d.Ram.Write32(Control+4,0); Assert.Equal((0u,212u),All(d,locked:locked)); }
        d.Packet(15,locked); d.Execute(new(ActionKind.Create,new string('x',100)));
        var image=new IndependentImage(d.Image); ulong at=image.Metadata.Single(p=>Layout.U16(d.Image.AsSpan((int)p*512),4)==17);
        Span<byte> page=d.Image.AsSpan((int)at*512,1024); page[68]=0xc0; Layout.Seal(page);
        for(int n=0;n<20;n++) { d.Ram.Write32(Control+4,0); Assert.Equal((0u,213u),All(d)); }
        page[68]=(byte)'x'; Layout.Seal(page); d.Ram.Write32(Control+4,0);
        Assert.Equal((0u,232u),All(d)); Assert.Equal(2u,d.Ram.Read32(Control));
    }
    [Fact] public void IoFailureRetiresAnExistingCursorAndSerialExhaustionNeverWraps()
    {
        using var d=new DispatcherDriver(512); Files(d,3);
        Assert.Equal((uint.MaxValue,0u),All(d,16,1)); uint key=d.Ram.Read32(Control+4);
        uint p=DispatcherDriver.PacketAddress;
        d.Ram.Write32(p+8,1033); d.Ram.Write32(p+20,0); d.Ram.Write32(p+24,Output);
        d.Ram.Write32(p+28,16); d.Ram.Write32(p+32,1); d.Ram.Write32(p+36,Control);
        Assert.Equal(CpxError.None,d.Dispatcher.BeginPacket(p)); var io=InhibitLifecycleTests.NextIo(d);
        d.Dispatcher.CompleteIo(io.Token,0,false); Assert.Equal((0u,213u),InhibitLifecycleTests.Finish(d));
        Assert.Equal(uint.MaxValue,d.Ram.Read32(Control+4)); Assert.Equal(0u,d.Ram.Read32(Control));
        Assert.Equal((uint.MaxValue,0u),d.Packet(31)); d.Ram.Write32(Control+4,key); Assert.Equal((0u,211u),All(d));
        d.Ram.Write32(32+512+112,uint.MaxValue-1); d.Ram.Write32(Control+4,0);
        for(int n=0;n<20;n++) Assert.Equal((0u,103u),All(d));
        Assert.Equal(0u,d.Ram.Read32(Control+4));
        Assert.Equal(uint.MaxValue-1,d.Ram.Read32(32+512+112));
    }
}
