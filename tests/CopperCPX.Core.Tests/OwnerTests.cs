using System.Text;
using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
namespace CopperCPX.Tests;

public class OwnerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static void Change(IBlockDevice disk,ulong id,uint owner,int unit=512)
    {
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram,unit);
        Assert.Equal(CpxError.None,e.BeginOwner(new Wide((uint)(id>>32),(uint)id),owner));
        for(int n=0;n<100000;n++)
        {
            e=new(ram,32); var step=e.Step(); Assert.InRange(e.TransactionImageCount,0u,6u);
            if(step==StepResult.Complete) return;
            Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
            var io=e.Request; Assert.Equal(StepResult.NeedIo,e.Step()); Assert.Equal(io,e.Request);
            Assert.Equal(CpxError.Busy,e.BeginOwner(Wide.From(1),1));
            Service(disk,ram,io); Assert.Equal(CpxError.None,e.CompleteIo(io.Token,io.Bytes,true));
        }
        throw new InvalidDataException("Owner continuation did not terminate.");
    }
    private static void Service(IBlockDevice disk,Ram ram,CpxIo io)
    {
        ulong sector=((ulong)io.Sector.High<<32)|io.Sector.Low;
        if(io.Kind==IoKind.Read) disk.Read(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
        else if(io.Kind==IoKind.Write) disk.Write(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
    }
    private static string Comment<T>(ref Cpx4Volume<T> v,ulong id) where T:IBlockDevice
    { byte[] text=new byte[160]; int n=v.ReadCommentUtf8(id,text); return Encoding.UTF8.GetString(text,0,n); }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void NativeAndReferencePreserveCommentsLabelsAndCanonicalAttributeVersions(int unit)
    {
        var disk=EngineTests.Fresh(unit); var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        v.Put("file"u8,"payload"u8); Assert.True(v.TryGetInfo("file"u8,out var info)); ulong id=info.Id;
        Assert.Equal(0u,v.ReadOwner(id)); Assert.False(v.OwnershipEnabled);
        string comment=new('é',79); Assert.Equal(MutationResult.Success,v.SetComment(id,Encoding.Latin1.GetBytes(comment)));
        VolumeLabelTests.Change(disk,"ÄWork",unit:unit);
        foreach(uint owner in new[]{0x1234abcdu,0xffff0000u,0x0000ffffu,uint.MaxValue,0u})
        {
            Change(disk,id,owner,unit); v=new(disk,new byte[36864]); Assert.Equal(owner,v.ReadOwner(id)); Assert.Equal(comment,Comment(ref v,id));
            var image=new IndependentImage(disk.Bytes); Assert.True(image.OwnershipEnabled); Assert.Equal(owner,image.OwnerInfo[id]); Assert.Equal("ÄWork",image.Label);
            Assert.Equal("payload"u8.ToArray(),image.Read(id)); v.Check();
        }
        Assert.Equal(MutationResult.Success,v.SetOwner(id,0x8001fedc));
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram,unit);
        Assert.Equal(CpxError.None,e.BeginComment(Wide.From((uint)id),0,0)); EngineTests.Drive(ref e,ram,disk);
        v=new(disk,new byte[36864]); Assert.Equal(0x8001fedcu,v.ReadOwner(id)); Assert.Equal("",Comment(ref v,id));
        Change(disk,id,0,unit); var cleared=new IndependentImage(disk.Bytes); Assert.Equal(0UL,IndependentImage.L(cleared.Objects[id],48)); Assert.True(cleared.OwnershipEnabled);
        VolumeLabelTests.Change(disk,"Renamed",unit:unit); Assert.True(new IndependentImage(disk.Bytes).OwnershipEnabled);
        VolumeLabelTests.Change(disk,"Fresh",format:true,unit:unit); Assert.False(new IndependentImage(disk.Bytes).OwnershipEnabled);
    }
    [Fact] public void DosOwnerIsSharedByHardLinksAndReturnedByEveryInformationFamily()
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file")); d.Execute(new(ActionKind.HardLink,"file",Target:"alias"));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1036,0,0,d.Bstr("alias"),0x89abcdef));
        Assert.Equal((uint.MaxValue,0u),d.Packet(28,0,0,d.Bstr("file"),d.Bstr("é",DispatcherDriver.TargetAddress)));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1036,0,0,d.Bstr(""),0x12340005));
        foreach(string name in new[]{"file","alias",""})
        {
            uint owner=name==""?0x12340005u:0x89abcdefu;
            uint locked=d.Packet(8,0,d.Bstr(name),0xfffffffe).Result; Assert.NotEqual(0u,locked);
            foreach(uint packet in new[]{23u,26408u})
            { Assert.Equal((uint.MaxValue,0u),d.Packet(packet,locked,86000>>2)); Assert.Equal(owner,d.Ram.Read32(86224)); }
            d.Packet(15,locked);
            if(name=="") continue;
            d.Packet(1005,DispatcherDriver.FileHandle>>2,0,d.Bstr(name)); uint handle=d.Ram.Read32(DispatcherDriver.FileHandle+36);
            foreach(uint packet in new[]{1034u,26410u})
            { Assert.Equal((uint.MaxValue,0u),d.Packet(packet,handle,86000>>2)); Assert.Equal(owner,d.Ram.Read32(86224)); }
            d.Packet(1007,handle);
        }
        foreach(uint next in new[]{24u,26409u})
        {
            d.Packet(23,0,86000>>2); int count=0;
            while(d.Packet(next,0,86000>>2).Error==0) { Assert.Equal(0x89abcdefu,d.Ram.Read32(86224)); count++; }
            Assert.Equal(2,count);
        }
        Assert.Equal((0u,103u),ExAllPacketTests.All(d,41,7)); Assert.Equal(0u,d.Ram.Read32(85000));
        Assert.Equal((0u,232u),ExAllPacketTests.All(d,512,7));
        foreach(var entry in ExAllPacketTests.Entries(d,header:40))
        { Assert.Equal(0x89abcdefu,d.Ram.Read32(entry.Address+36)); Assert.Equal("é",ExAllPacketTests.Text(d,d.Ram.Read32(entry.Address+32))); }
        d.Execute(new(ActionKind.Remount)); var image=new IndependentImage(d.Image); Assert.True(image.OwnershipEnabled); Assert.Equal(0x12340005u,image.OwnerInfo[1]);
        Assert.Equal(Outcome.Success,d.Execute(new(ActionKind.Delete,"alias")).Outcome);
        Assert.Equal(0x89abcdefu,new IndependentImage(d.Image).OwnerInfo[image.Names[(1,"file")]]);
        Assert.Equal(Outcome.Success,d.Execute(new(ActionKind.Delete,"file")).Outcome); new Cpx4Volume<Disk>(new Disk{Bytes=d.Image},new byte[36864]).Check();
    }
    [Fact] public void DosErrorsAndDefaultDirectoryOwnershipDoNotIntroduceIdentityPolicy()
    {
        using var d=new DispatcherDriver(512); Assert.Equal((0u,205u),d.Packet(1036,0,0,d.Bstr("missing"),1));
        Assert.Equal((0u,211u),d.Packet(1036,0,123,d.Bstr(""),1));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1036,0,0,d.Bstr(""),uint.MaxValue));
        uint directory=d.Packet(22,0,d.Bstr("dir")).Result; Assert.NotEqual(0u,directory);
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,directory,86000>>2)); Assert.Equal(0u,d.Ram.Read32(86224));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1036,0,directory,d.Bstr(""),0x00020003)); d.Packet(15,directory);
        d.Packet(1023,1); Assert.Equal((0u,214u),d.Packet(1036,0,0,d.Bstr("dir"),3)); d.Packet(1023,0);
        d.Packet(31,1); Assert.Equal((0u,226u),d.Packet(1036,0,0,d.Bstr("dir"),3)); d.Packet(31,0);
        Assert.Equal(0x00020003u,new IndependentImage(d.Image).OwnerInfo[new IndependentImage(d.Image).Names[(1,"dir")]]);
    }
    [Fact] public void FirstOwnerRefusesExhaustedReserveWithoutPublishingFeature()
    {
        var disk=new MemoryDevice(ExhaustedVolume.Create()); var before=new IndependentImage(disk.Bytes); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram); var generation=e.Transaction;
        Assert.Equal(CpxError.None,e.BeginOwner(Wide.From(1),0x12345678));
        for(int n=0;n<20000;n++)
        {
            var result=e.Step(); if(result==StepResult.Failed) break; Assert.NotEqual(StepResult.Complete,result);
            if(result!=StepResult.NeedIo) continue; var io=e.Request; Service(disk,ram,io); e.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Equal(CpxError.ReserveExhausted,e.Error); Assert.True(Wide.Equal(generation,e.Transaction));
        var after=new IndependentImage(disk.Bytes); Assert.False(after.OwnershipEnabled); Assert.Equal(before.Objects[1],after.Objects[1]); Assert.Equal(before.Metadata,after.Metadata);
        new Cpx4Volume<MemoryDevice>(disk,new byte[36864]).Check();
    }
    [Fact] public void ExistingOwnershipCanChangeAndReleaseItsPageWithNoFreeReserve()
    {
        var disk=new MemoryDevice(ExhaustedVolume.Create(rootOwner:true));
        Change(disk,1,0x8000ffff); var volume=new Cpx4Volume<MemoryDevice>(disk,new byte[36864]); Assert.Equal(0,volume.AvailableReservePages); Assert.Equal(0x8000ffffu,volume.ReadOwner(1));
        Change(disk,1,0); volume=new(disk,new byte[36864]); Assert.Equal(1,volume.AvailableReservePages); Assert.Equal(0u,volume.ReadOwner(1)); volume.Check(); new IndependentImage(disk.Bytes);
    }
    private static void Mutate(IBlockDevice disk,ulong id,int operation)
    {
        if(operation<3||operation==5) { Change(disk,id,operation is 0 or 5?0x1234abcdu:operation==1?0xfedc9876u:0); return; }
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        if(operation==3) Assert.Equal(CpxError.None,e.BeginComment(Wide.From((uint)id),0,0));
        else { "file"u8.CopyTo(ram.Bytes.AsSpan(90000)); Assert.Equal(CpxError.None,e.BeginDelete(Wide.From(1),90000,4)); }
        EngineTests.Drive(ref e,ram,disk);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void InvalidCommittedOwnerOrFeatureWithResignedChecksumsIsRejectedBeforeHomeWrites(int damage)
    {
        var original=EngineTests.Fresh(512); var trace=new CrashDevice(original.Bytes); Change(trace,1,0x1234abcd);
        int third=trace.Events.Select((s,n)=>(s,n)).Where(x=>x.s=="after flush").Skip(2).First().n+1;
        var cut=new CrashDevice(original.Bytes){Cut=third}; Assert.Throws<PowerLoss>(()=>Change(cut,1,0x1234abcd));
        var bytes=cut.Durable; const int log=1048576,p=1024;
        int count=(int)Layout.U32(bytes,log+96); Assert.InRange(count,2,6); bool changed=false;
        for(int i=0;i<count;i++)
        {
            var page=bytes.AsSpan(log+(5+i)*p,p); uint type=Layout.U16(page,4);
            if(damage<4&&type==18)
            {
                if(damage==0) Layout.Put16(page,64,3);
                if(damage==1) Layout.Put32(page,68,0);
                if(damage==2) Layout.Put16(page,74,159);
                if(damage==3) Layout.Put16(page,72,2);
            }
            else if(damage>=4&&type==3)
            {
                if(damage==6) { Layout.Put16(page,52,128); page.Slice(192,8).Clear(); }
                else Layout.Put32(page,damage==4?192:196,2);
            }
            else continue;
            changed=true; Layout.Seal(page); Layout.Put32(bytes,log+p+64+i*32+20,Layout.U32(page,8));
        }
        Assert.True(changed); var descriptor=bytes.AsSpan(log+p,p); Layout.Seal(descriptor);
        uint digest=Layout.Crc(descriptor.Slice(64,count*32));
        foreach(int at in new[]{log,log+69*p}) { Layout.Put32(bytes,at+100,digest); Layout.Seal(bytes.AsSpan(at,p)); }
        var disk=new MemoryDevice(bytes); var ram=new Ram(new byte[100000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,Wide.From(8192),1); engine.BeginOperation(CpxOperation.Mount);
        FileMutationTests.DriveFailure(ref engine,ram,disk,CpxError.Corrupt); Assert.Equal(0,disk.Writes);
        Assert.Throws<InvalidDataException>(()=>MountReference(new MemoryDevice(bytes)));
    }
    private static void MountReference(MemoryDevice disk) { var volume=new Cpx4Volume<MemoryDevice>(disk,new byte[36864]); volume.Check(); }
    [Fact] public void ExistingOwnedAttributeCannotReplayWithoutItsPreviouslyCommittedFeature()
    {
        var original=EngineTests.Fresh(512); Change(original,1,0x1234abcd);
        var trace=new CrashDevice(original.Bytes); Change(trace,1,0x89abcdef);
        int third=trace.Events.Select((s,n)=>(s,n)).Where(x=>x.s=="after flush").Skip(2).First().n+1;
        var cut=new CrashDevice(original.Bytes){Cut=third}; Assert.Throws<PowerLoss>(()=>Change(cut,1,0x89abcdef));
        var state=cut.Durable.AsSpan(65536+3072,1024); Layout.Put16(state,52,128); state.Slice(192,8).Clear(); Layout.Seal(state);
        var disk=new MemoryDevice(cut.Durable); var ram=new Ram(new byte[100000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,Wide.From(8192),1); engine.BeginOperation(CpxOperation.Mount);
        FileMutationTests.DriveFailure(ref engine,ram,disk,CpxError.Corrupt); Assert.Equal(0,disk.Writes);
        Assert.Throws<InvalidDataException>(()=>MountReference(new MemoryDevice(cut.Durable)));
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void CancellationAtEachOwnerBarrierQuiescesAndRecoveryReturnsWholeMetadata(int barrier)
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file"));
        uint packet=DispatcherDriver.PacketAddress;
        d.Ram.Write32(packet+8,1036); d.Ram.Write32(packet+20,0); d.Ram.Write32(packet+24,0);
        d.Ram.Write32(packet+28,d.Bstr("file")); d.Ram.Write32(packet+32,0x89abcdef);
        Assert.Equal(CpxError.None,d.Dispatcher.BeginPacket(packet));
        int seen=0; var io=InhibitLifecycleTests.NextIo(d);
        while(io.Kind!=IoKind.Flush||++seen<barrier)
        {
            // Once the engine has begun mutation, it owns the packed scalar.
            if(io.Kind==IoKind.Write) d.Ram.Write32(packet+32,0);
            InhibitLifecycleTests.Service(d,io); io=InhibitLifecycleTests.NextIo(d);
        }
        d.Dispatcher.RequestStorageCancellation(); Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
        Assert.Equal(CpxError.Busy,d.Dispatcher.BeginPacket(packet));
        Assert.Equal(CpxError.Io,d.Dispatcher.CompleteIo(io.Token,0,false));
        Assert.Equal((0u,304u),InhibitLifecycleTests.Finish(d)); Assert.False(d.Dispatcher.MediaAvailable);
        var disk=new MemoryDevice(d.Image); FileMutationTests.Mount(disk,new Ram(new byte[100000])); var image=new IndependentImage(disk.Bytes);
        Assert.Contains(image.OwnerInfo[image.Names[(1,"file")]],new[]{0u,0x89abcdefu});
        new Cpx4Volume<MemoryDevice>(disk,new byte[36864]).Check();
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void EveryOwnerSectorBarrierAndReplayCutPreservesAnAtomicPairAndOwnership(int operation)
    {
        var original=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(original,new byte[36864]); v.Put("file"u8,"payload"u8); v.TryGetInfo("file"u8,out var info); ulong id=info.Id;
        if(operation is not (2 or 5)) v.SetComment(id,Encoding.Latin1.GetBytes("é comment"));
        if(operation is not (0 or 5)) v.SetOwner(id,0x1234abcd);
        var baseline=(byte[])original.Bytes.Clone(); var trace=new CrashDevice(baseline); Mutate(trace,id,operation);
        void Verify(byte[] bytes,bool committed)
        {
            var disk=new MemoryDevice(bytes); FileMutationTests.Mount(disk,new Ram(new byte[100000]));
            var image=new IndependentImage(disk.Bytes); new Cpx4Volume<MemoryDevice>(disk,new byte[36864]).Check();
            bool exists=image.Objects.ContainsKey(id); if(!exists) { Assert.Equal(4,operation); return; }
            Assert.Equal("payload"u8.ToArray(),image.Read(id));
            uint old=operation is 0 or 5?0u:0x1234abcdu,next=operation==1?0xfedc9876u:operation==2?0u:0x1234abcdu;
            Assert.Contains(image.OwnerInfo[id],committed?new[]{next}:new[]{old,next});
            if(image.OwnerInfo[id]!=0) Assert.True(image.OwnershipEnabled);
            if(operation==2&&image.OwnerInfo[id]==0) Assert.Equal(0UL,IndependentImage.L(image.Objects[id],48));
        }
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(baseline){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>Mutate(fault,id,operation)); Verify(fault.Durable,false);
        }
        int third=trace.Events.Select((s,n)=>(s,n)).Where(x=>x.s=="after flush").Skip(2).First().n+1;
        var journal=new CrashDevice(baseline){Cut=third}; Assert.Throws<PowerLoss>(()=>Mutate(journal,id,operation));
        var replay=new CrashDevice(journal.Durable); FileMutationTests.Mount(replay,new Ram(new byte[100000]));
        for(int cut=1;cut<=replay.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(journal.Durable){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>FileMutationTests.Mount(fault,new Ram(new byte[100000]))); Verify(fault.Durable,true);
        }
        output.WriteLine($"Owner operation {operation}: {trace.Events.Count} cuts × 5, replay {replay.Events.Count} cuts × 5.");
    }
}
