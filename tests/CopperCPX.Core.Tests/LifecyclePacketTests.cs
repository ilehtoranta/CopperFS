using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class LifecyclePacketTests
{
    private const uint Shared=0xfffffffe,Exclusive=uint.MaxValue,Fh=DispatcherDriver.FileHandle;
    private static uint Open(DispatcherDriver d,string name="file",uint action=1004,uint fh=Fh)
    { Assert.Equal((uint.MaxValue,0u),d.Packet(action,fh>>2,0,d.Bstr(name))); return d.Ram.Read32(fh+36); }
    private static uint Lock(DispatcherDriver d,string name="file",uint mode=Shared)
    { var r=d.Packet(8,0,d.Bstr(name),mode); Assert.Equal(0u,r.Error); Assert.NotEqual(0u,r.Result); return r.Result; }
    private static void Close(DispatcherDriver d,uint h)=>Assert.Equal((uint.MaxValue,0u),d.Packet(1007,h));
    private static void Unlock(DispatcherDriver d,uint l)=>Assert.Equal((uint.MaxValue,0u),d.Packet(15,l));

    [Theory][InlineData(Shared)][InlineData(Exclusive)]
    public void ConversionTransfersOwnershipWithoutTruncatingAndPreservesMode(uint mode)
    {
        using var d=new DispatcherDriver(512); uint h=Open(d); d.Ram.Write8(90000,42);
        Assert.Equal((1u,0u),d.Packet(87,h,90000,1)); Close(d,h); uint l=Lock(d,mode:mode);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1026,Fh>>2,l)); h=d.Ram.Read32(Fh+36);
        Assert.Equal(mode,d.Ram.Read32(h+16)); Assert.Equal(0u,d.Ram.Read32((l<<2)+12));
        Assert.Equal((0u,211u),d.Packet(15,l)); Assert.Equal((1u,0u),d.Packet(82,h,91000,1)); Assert.Equal(42,d.Ram.Read8(91000));
        if(mode==Exclusive) Assert.Equal((0u,202u),d.Packet(1030,h));
        else { uint duplicate=d.Packet(1030,h).Result; Assert.NotEqual(0u,duplicate); Unlock(d,duplicate); }
        Close(d,h); Assert.False(d.Dispatcher.HasOpenResources); new IndependentImage(d.Image);
    }
    [Fact]
    public void ConversionExhaustionAndWrongTypeLeaveLockAndCallerHandleUntouched()
    {
        using var d=new DispatcherDriver(512); uint[] handles=new uint[32];
        for(int n=0;n<32;n++) handles[n]=Open(d,fh:Fh+(uint)n*64);
        uint locked=Lock(d); const uint output=87000; d.Ram.Write32(output+36,0x12345678);
        Assert.Equal((0u,103u),d.Packet(1026,output>>2,locked)); Assert.Equal(0x12345678u,d.Ram.Read32(output+36));
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,locked,88000>>2));
        Close(d,handles[31]); Assert.Equal((uint.MaxValue,0u),d.Packet(1026,output>>2,locked)); Close(d,d.Ram.Read32(output+36));
        for(int n=0;n<31;n++) Close(d,handles[n]);
        uint directory=d.Packet(22,0,d.Bstr("dir")).Result; Assert.NotEqual(0u,directory);
        d.Ram.Write32(output+36,0x12345678); Assert.Equal((0u,212u),d.Packet(1026,output>>2,directory));
        Assert.Equal(0x12345678u,d.Ram.Read32(output+36)); Unlock(d,directory); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void ConversionRejectsReadProtectionWithoutConsumingTheLock()
    {
        using var d=new DispatcherDriver(512); Close(d,Open(d));
        Assert.Equal((uint.MaxValue,0u),d.Packet(21,0,0,d.Bstr("file"),8)); uint l=Lock(d);
        Assert.Equal((0u,224u),d.Packet(1026,Fh>>2,l)); Unlock(d,l); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Theory][InlineData(0u)][InlineData(1u)]
    public void ChangeModeUsesBptrAndRefusesConflictingOwners(uint kind)
    {
        using var d=new DispatcherDriver(512); uint h=Open(d),l;
        if(kind==0) { Close(d,h); l=Lock(d); } else l=0;
        uint bptr=kind==0?l:Fh>>2,other=Lock(d);
        Assert.Equal((0u,202u),d.Packet(1028,kind,bptr,Exclusive)); Unlock(d,other);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1028,kind,bptr,1006));
        Assert.Equal((0u,202u),d.Packet(8,0,d.Bstr("file"),Shared));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1028,kind,bptr,1005)); other=Lock(d); Unlock(d,other);
        Assert.Equal((0u,115u),d.Packet(1028,kind,bptr,123));
        if(kind==0) Unlock(d,l); else Close(d,h);
        Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void ParentOfFilesHandlesAndHardLinksUsesTheOpenedEntry()
    {
        using var d=new DispatcherDriver(512); var disk=new Disk { Bytes=d.Image }; var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        v.Create(1,"one"u8,true,out ulong one); v.Create(1,"two"u8,true,out ulong two);
        v.Create(one,"file"u8,false,out ulong file); v.Link(file,two,"linked"u8); d.Execute(new(ActionKind.Remount));
        uint h=Open(d,"two/linked"),l=Lock(d,"one/file");
        foreach(var item in new[]{(Action:29u,Value:l,Name:"one"),(Action:1031u,Value:h,Name:"two")})
        {
            var parent=d.Packet(item.Action,item.Value); Assert.Equal(0u,parent.Error); Assert.NotEqual(0u,parent.Result);
            Assert.Equal((uint.MaxValue,0u),d.Packet(23,parent.Result,88000>>2));
            Assert.Equal(item.Name,System.Text.Encoding.Latin1.GetString(d.Ram.Bytes,88009,d.Ram.Read8(88008)));
            uint root=d.Packet(29,parent.Result).Result; Assert.NotEqual(0u,root); Assert.Equal((0u,0u),d.Packet(29,root));
            Unlock(d,root); Unlock(d,parent.Result);
        }
        Unlock(d,l); Close(d,h); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void CopyFromHandleExhaustionDoesNotConsumeTheHandle()
    {
        using var d=new DispatcherDriver(512); uint h=Open(d); uint[] locks=new uint[32];
        for(int n=0;n<32;n++) { var r=d.Packet(1030,h); Assert.Equal(0u,r.Error); locks[n]=r.Result; }
        Assert.Equal((0u,103u),d.Packet(1030,h)); Assert.Equal((0u,0u),d.Packet(82,h,90000,1));
        foreach(uint l in locks) Unlock(d,l); Close(d,h); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void SoftwareProtectionHasKeySemanticsAndDoesNotChangePersistentMedia()
    {
        using var d=new DispatcherDriver(512); uint h=Open(d); byte[] before=(byte[])d.Image.Clone();
        Assert.Equal((uint.MaxValue,0u),d.Packet(1023,1,123)); Assert.True(d.Dispatcher.IsWriteProtected);
        Assert.Equal((0u,214u),d.Packet(1023,1,123)); Assert.Equal((0u,210u),d.Packet(1023,0,456));
        Assert.Equal((uint.MaxValue,214u),d.Packet(87,h,90000,1)); Assert.Equal((uint.MaxValue,214u),d.Packet(1022,h,50,uint.MaxValue));
        Assert.Equal((0u,214u),d.Packet(16,0,d.Bstr("file"))); Assert.Equal((0u,214u),d.Packet(28,0,0,d.Bstr("file"),d.Bstr("comment",82500)));
        Assert.Equal((uint.MaxValue,0u),d.Packet(25,88000>>2)); Assert.Equal(80u,d.Ram.Read32(88008));
        Assert.Equal((0u,0u),d.Packet(82,h,90000,1)); Assert.Equal((uint.MaxValue,0u),d.Packet(27)); Assert.Equal(before,d.Image);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1023,0,123)); Assert.False(d.Dispatcher.IsWriteProtected);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1023,1,0)); Assert.Equal((uint.MaxValue,0u),d.Packet(1023,0,999));
        Close(d,h); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void FixedScratchIsNotReportedAsAnExpandableCache()
    {
        using var d=new DispatcherDriver(512); byte[] before=(byte[])d.Image.Clone();
        foreach(uint delta in new[]{0u,1u,0xffffffffu,0x80000000u,0x7fffffffu}) Assert.Equal((0u,0u),d.Packet(18,delta));
        Assert.Equal(before,d.Image); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void SameLockPacketUsesBooleanNotTheDosLibraryReturnConvention()
    {
        using var d=new DispatcherDriver(512); Close(d,Open(d)); uint a=Lock(d),b=Lock(d);
        Assert.Equal((uint.MaxValue,0u),d.Packet(40,a,b)); Assert.Equal((0u,0u),d.Packet(40,a,0));
        Assert.Equal((0u,211u),d.Packet(40,a,123)); Unlock(d,a); Unlock(d,b);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FailedReadOrProtectionBarrierCannotPublishControlState(bool protect)
    {
        using var d=new DispatcherDriver(512); Close(d,Open(d)); uint locked=Lock(d);
        d.Ram.Write32(Fh+36,0x12345678);
        uint packet=DispatcherDriver.PacketAddress; d.Ram.Write32(packet+8,protect?1023u:1026u);
        d.Ram.Write32(packet+20,protect?1u:Fh>>2); d.Ram.Write32(packet+24,protect?123u:locked);
        Assert.Equal(CpxError.None,d.Dispatcher.BeginPacket(packet)); bool injected=false;
        for(int n=0;n<1000;n++)
        {
            d.Dispatcher=new(d.Ram,32); StepResult step=d.Dispatcher.Step();
            if(step==StepResult.Complete) break;
            if(step!=StepResult.NeedIo) continue;
            var io=d.Dispatcher.Request;
            if(protect&&io.Kind!=IoKind.Flush)
            {
                EngineDriver.Service(io,d.Ram,new Disk { Bytes=d.Image }); d.Dispatcher.CompleteIo(io.Token,io.Bytes,true); continue;
            }
            Assert.Equal(protect?IoKind.Flush:IoKind.Read,io.Kind);
            Assert.Equal(CpxError.StaleCompletion,d.Dispatcher.CompleteIo(io.Token+1,io.Bytes,true));
            Assert.Equal(CpxError.Busy,d.Dispatcher.BeginPacket(packet)); Assert.False(d.Dispatcher.IsWriteProtected);
            Assert.Equal(CpxError.Io,d.Dispatcher.CompleteIo(io.Token,0,false)); injected=true;
        }
        Assert.True(injected); Assert.False(d.Dispatcher.Busy); Assert.Equal(213u,d.Dispatcher.DosError);
        Assert.False(d.Dispatcher.IsWriteProtected); Assert.Equal(0x12345678u,d.Ram.Read32(Fh+36));
        Assert.Equal(1u,d.Ram.Read32((locked<<2)-8)); Unlock(d,locked); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void ProtectedFlushSubmitsOnlyThePersistenceBarrier()
    {
        using var d=new DispatcherDriver(512); Assert.Equal((uint.MaxValue,0u),d.Packet(1023,1));
        uint packet=DispatcherDriver.PacketAddress; d.Ram.Write32(packet+8,27); Assert.Equal(CpxError.None,d.Dispatcher.BeginPacket(packet));
        int flushes=0;
        for(int n=0;n<20;n++)
        {
            d.Dispatcher=new(d.Ram,32); var step=d.Dispatcher.Step(); if(step==StepResult.Complete) break;
            if(step!=StepResult.NeedIo) continue; var io=d.Dispatcher.Request; Assert.Equal(IoKind.Flush,io.Kind); flushes++;
            Assert.Equal(CpxError.None,d.Dispatcher.CompleteIo(io.Token,0,true));
        }
        Assert.Equal(1,flushes); Assert.False(d.Dispatcher.Busy); Assert.Equal(0u,d.Dispatcher.DosError);
    }
}
