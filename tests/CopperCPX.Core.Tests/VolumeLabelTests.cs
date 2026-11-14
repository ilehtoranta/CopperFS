using System.Text;
using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
namespace CopperCPX.Tests;

public class VolumeLabelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static string Cached(CpxEngine<Ram> e,Ram ram)=>Encoding.Latin1.GetString(ram.Bytes,(int)e.VolumeLabelAddress+1,ram.Read8(e.VolumeLabelAddress));
    internal static void Change(IBlockDevice disk,string label,bool format=false,int unit=512)
    {
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram,unit);
        var bytes=Encoding.Latin1.GetBytes(label); bytes.CopyTo(ram.Bytes,90000);
        Assert.Equal(CpxError.None,format?e.BeginReformat(90000,(uint)bytes.Length):e.BeginLabel(90000,(uint)bytes.Length));
        ram.Bytes.AsSpan(90000,bytes.Length).Fill(0); // Admission owns its input.
        EngineTests.Drive(ref e,ram,disk); Assert.Equal(label,Cached(e,ram));
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void LabelRoundTripsAndSurvivesBothWritersAndFormat(int unit)
    {
        var disk=EngineTests.Fresh(unit); string label=new('é',30); Change(disk,label,false,unit);
        Assert.Equal(label,new IndependentImage(disk.Bytes).Label);
        var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out _); reference.Check();
        Assert.Equal(label,new IndependentImage(disk.Bytes).Label);
        Change(disk,"ÄNewÿ",true,unit); var decoded=new IndependentImage(disk.Bytes); Assert.Equal("ÄNewÿ",decoded.Label); Assert.Empty(decoded.Names);
        var ram=new Ram(new byte[100000]); var mounted=FileMutationTests.Mount(disk,ram,unit); Assert.Equal("ÄNewÿ",Cached(mounted,ram));
    }
    [Fact] public void RelabelRetiresOneImageWithSixBarriersWithoutReserveUse()
    {
        var disk=new MemoryDevice(ExhaustedVolume.Create()); var before=new IndependentImage(disk.Bytes);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram); "New"u8.CopyTo(ram.Bytes.AsSpan(90000));
        Assert.Equal(CpxError.None,e.BeginLabel(90000,3)); int barriers=0; uint maximum=0;
        while(true)
        {
            var step=e.Step(); maximum=Math.Max(maximum,e.TransactionImageCount);
            if(step==StepResult.Complete) break; Assert.NotEqual(StepResult.Failed,step); Assert.Equal("CopperCPX4",Cached(e,ram));
            if(step!=StepResult.NeedIo) continue; var io=e.Request;
            Assert.Equal(StepResult.NeedIo,e.Step());
            if(io.Kind==IoKind.Flush) barriers++;
            if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush(); Assert.Equal(CpxError.None,e.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.Equal(6,barriers); Assert.Equal(1u,maximum); Assert.Equal("New",Cached(e,ram));
        Assert.Equal(before.Metadata,new IndependentImage(disk.Bytes).Metadata);
        new Cpx4Volume<MemoryDevice>(disk,new byte[36864]).Check();
    }
    [Fact] public void DosFormatAndRelabelValidateAdmissionAndExposePersistentRootName()
    {
        using var d=new DispatcherDriver(512); byte[] original=(byte[])d.Image.Clone();
        foreach(string invalid in new[]{"",new string('x',31),"a:b","a/b","\u001f","\u007f"})
        { Assert.Equal((0u,210u),d.Packet(9,d.Bstr(invalid))); Assert.Equal((0u,210u),d.Packet(1020,d.Bstr(invalid),CpxArena.Magic)); }
        Assert.Equal((0u,115u),d.Packet(1020,d.Bstr("Work"),0x444f5301)); Assert.Equal(original,d.Image);
        uint locked=d.Packet(8,0,d.Bstr(""),0xfffffffe).Result;
        Assert.Equal((0u,202u),d.Packet(1020,d.Bstr("Work"),CpxArena.Magic)); Assert.True(d.Dispatcher.MediaAvailable);
        Assert.Equal((uint.MaxValue,0u),d.Packet(9,d.Bstr("ÄWork"))); d.Packet(15,locked);
        d.Execute(new(ActionKind.Remount)); Assert.Equal((uint.MaxValue,0u),d.Packet(23,0,86000>>2));
        Assert.Equal("ÄWork",Encoding.Latin1.GetString(d.Ram.Bytes,86009,d.Ram.Read8(86008)));
        Assert.Equal((uint.MaxValue,0u),d.Packet(31,1)); Assert.Equal((0u,226u),d.Packet(9,d.Bstr("No")));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1020,d.Bstr("Fresh"),CpxArena.Magic)); Assert.True(d.Dispatcher.MediaAvailable);
        Assert.Equal("Fresh",new IndependentImage(d.Image).Label);
    }
    [Fact] public void EveryLabelSectorBarrierAndInterruptedReplayKeepsAnAtomicLabel()
    {
        var image=EngineTests.Fresh(512).Bytes; var trace=new CrashDevice(image); Change(trace,"ÄWork");
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(image){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>Change(fault,"ÄWork"));
            var disk=new MemoryDevice(fault.Durable); FileMutationTests.Mount(disk,new Ram(new byte[100000]));
            Assert.Contains(new IndependentImage(disk.Bytes).Label,new[]{"CopperCPX4","ÄWork"}); new Cpx4Volume<MemoryDevice>(disk,new byte[36864]).Check();
        }
        int third=trace.Events.Select((v,n)=>(v,n)).Where(x=>x.v=="after flush").Skip(2).First().n+1;
        var committed=new CrashDevice(image){Cut=third}; Assert.Throws<PowerLoss>(()=>Change(committed,"ÄWork"));
        var replay=new CrashDevice(committed.Durable); FileMutationTests.Mount(replay,new Ram(new byte[100000]));
        for(int cut=1;cut<=replay.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(committed.Durable){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>FileMutationTests.Mount(fault,new Ram(new byte[100000])));
            var disk=new MemoryDevice(fault.Durable); FileMutationTests.Mount(disk,new Ram(new byte[100000])); Assert.Equal("ÄWork",new IndependentImage(disk.Bytes).Label);
        }
        output.WriteLine($"Label: {trace.Events.Count} cuts × 5; replay: {replay.Events.Count} cuts × 5.");
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void MalformedCommittedLabelsWithValidChecksumsNeverReachHomePages(int damage)
    {
        var image=EngineTests.Fresh(512).Bytes; var trace=new CrashDevice(image); Change(trace,"ÄWork");
        int cut=trace.Events.Select((v,n)=>(v,n)).Where(x=>x.v=="after flush").Skip(2).First().n+1;
        var fault=new CrashDevice(image){Cut=cut}; Assert.Throws<PowerLoss>(()=>Change(fault,"ÄWork"));
        // Re-sign every envelope so the test reaches semantic payload validation.
        const int log=1048576,p=1024; var bytes=fault.Durable;
        var page=bytes.AsSpan(log+5*p,p);
        switch(damage)
        {
            case 0: page[120]=0xc0; break; // overlong UTF-8
            case 1: page[127]=1; break; // nonzero padding
            case 2: Layout.Put16(page,116,61); break;
            case 3: page[119]=2; break;
            case 4: page[112]=0; break;
            case 5: page[122]=(byte)':'; break;
        }
        Layout.Seal(page); var descriptor=bytes.AsSpan(log+p,p); Layout.Put32(descriptor,64+20,Layout.U32(page,8)); Layout.Seal(descriptor);
        uint digest=Layout.Crc(descriptor.Slice(64,32));
        foreach(int at in new[]{log,log+69*p}) { var envelope=bytes.AsSpan(at,p); Layout.Put32(envelope,100,digest); Layout.Seal(envelope); }
        var disk=new MemoryDevice(bytes); var ram=new Ram(new byte[100000]); var e=new CpxEngine<Ram>(ram,32);
        e.Initialize(512,512,Wide.From(8192),1); e.BeginOperation(CpxOperation.Mount); StepResult result=StepResult.Pending;
        for(int n=0;n<1000;n++)
        {
            result=e.Step(); if(result is StepResult.Complete or StepResult.Failed) break;
            if(result!=StepResult.NeedIo) continue; var io=e.Request; Assert.Equal(IoKind.Read,io.Kind);
            disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); e.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Equal(StepResult.Failed,result); Assert.Equal(CpxError.Corrupt,e.Error); Assert.Equal(0,disk.Writes);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FailedFormatBarrierOrCancellationLeavesMediaOfflineUntilReformatted(bool cancel)
    {
        using var d=new DispatcherDriver(512); InhibitLifecycleTests.Begin(d,1020,d.Bstr("Fresh"),CpxArena.Magic);
        var io=InhibitLifecycleTests.NextIo(d);
        while(io.Kind!=IoKind.Flush) { InhibitLifecycleTests.Service(d,io); io=InhibitLifecycleTests.NextIo(d); }
        if(cancel)
        {
            d.Dispatcher.RequestStorageCancellation(); Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step());
            Assert.Equal(CpxError.Io,d.Dispatcher.CompleteIo(io.Token,0,false));
        }
        else d.Dispatcher.CompleteIo(io.Token,0,false);
        Assert.Equal((0u,cancel?304u:213u),InhibitLifecycleTests.Finish(d)); Assert.False(d.Dispatcher.MediaAvailable);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1020,d.Bstr("Recovered"),CpxArena.Magic));
        Assert.Equal("Recovered",new IndependentImage(d.Image).Label); Assert.True(d.Dispatcher.MediaAvailable);
    }
    [Fact] public void FormatRefusesNotificationsAndWriteProtectionBeforeChangingMedia()
    {
        using var d=new DispatcherDriver(512); byte[] before=(byte[])d.Image.Clone();
        uint request=NotificationLifecycleTests.Request(d,""); Assert.Equal((uint.MaxValue,0u),d.Packet(4097,request));
        Assert.Equal((0u,202u),d.Packet(1020,d.Bstr("Busy"),CpxArena.Magic)); d.Packet(4098,request);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1023,1));
        Assert.Equal((0u,214u),d.Packet(1020,d.Bstr("Protected"),CpxArena.Magic)); Assert.Equal((0u,214u),d.Packet(9,d.Bstr("Protected")));
        Assert.Equal(before,d.Image);
    }
}
