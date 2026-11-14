using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using System.Text;

namespace CopperCPX.Tests;

public class NamespaceCrashTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static (byte[] Image,uint Id,uint Directory) Fixture(int operation)
    {
        if(operation==4) return(ExhaustedVolume.Create(),2,0);
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        v.Create("source"u8,out ulong id); v.Create(1,"destination"u8,true,out ulong dir);
        if(operation==5)
        {
            v.Create(Encoding.Latin1.GetBytes(new string('x',100)),out _);
            Assert.True(v.AvailableReservePages<32);
        }
        if(operation!=0&&operation!=5)
        {
            if(operation==3) for(uint n=0;n<7;n++) v.Write(id,n*1024,new byte[512].Select(_=>(byte)0x5a).ToArray(),out _);
            else v.Write(id,0,"persistent original content"u8,out _);
            v.SetComment(id,"external attribute"u8);
            if(operation==1) { v.Create(dir,"victim"u8,false,out ulong victim); v.Write(victim,0,new byte[1700],out _); v.SetComment(victim,"victim attribute"u8); }
        }
        return(disk.Bytes,(uint)id,(uint)dir);
    }
    private static void Operate(IBlockDevice disk,int operation,uint id,uint directory)
    {
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        byte[] name=operation is 0 or 5?Encoding.Latin1.GetBytes(new string('é',100)):operation==4?"full"u8.ToArray():"source"u8.ToArray(); name.CopyTo(ram.Bytes,90000); "victim"u8.CopyTo(ram.Bytes.AsSpan(91000));
        CpxError error=operation switch
        {
            0 or 5=>e.BeginCreate(Wide.From(1),90000,(uint)name.Length),
            1=>e.BeginRename(Wide.From(1),90000,6,Wide.From(directory),91000,6,true),
            2=>e.BeginDelete(Wide.From(1),90000,6),
            4=>e.BeginDelete(Wide.From(1),90000,4),
            _=>e.BeginResize(Wide.From(id),Wide.From(3300))
        };
        Assert.Equal(CpxError.None,error); EngineTests.Drive(ref e,ram,disk);
    }
    private static void Verify(MemoryDevice disk,int operation,uint id,uint directory)
    {
        new IndependentImage(disk.Bytes); var v=new Cpx4Volume<MemoryDevice>(disk,new byte[36864]); v.Check(); Assert.False(v.HasPendingCleanup);
        bool source=v.TryGetInfo("source"u8,out var info);
        if(operation is 0 or 5) { Assert.True(source); Assert.Equal((ulong)id,info.Id); }
        if(operation==1)
        {
            Assert.True(v.TryGetInfo(directory,"victim"u8,out var victim));
            Assert.Equal(!source,victim.Id==id);
        }
        if(operation==3)
        {
            Assert.True(source); Assert.Contains(info.Length,new ulong[]{3300,6656});
            byte[] data=new byte[7000]; int read=v.Read(id,0,data); Assert.Equal((int)info.Length,read);
            for(int n=0;n<read;n++) Assert.Equal(n%1024<512?(byte)0x5a:(byte)0,data[n]);
        }
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void EveryNamespaceSectorAndBarrierRecoversWithIndependentOwnership(int operation)
    {
        var (image,id,dir)=Fixture(operation); var trace=new CrashDevice(image); Operate(trace,operation,id,dir);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(image){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>Operate(fault,operation,id,dir));
            var disk=new MemoryDevice(fault.Durable); FileMutationTests.Mount(disk,new Ram(new byte[100000])); Verify(disk,operation,id,dir);
        }
        output.WriteLine($"Operation {operation}: {trace.Events.Count} cuts x 5 = {trace.Events.Count*5} recovered images.");
    }
    [Fact] public void RepeatedCrashDuringReplayAndDurableCleanupKeepsOwnership()
    {
        var (image,id,dir)=Fixture(2); var trace=new CrashDevice(image); Operate(trace,2,id,dir);
        int barriers=0,cut=0;
        for(int n=0;n<trace.Events.Count;n++) if(trace.Events[n]=="after flush"&&++barriers==3) { cut=n+1; break; }
        var first=new CrashDevice(image){Cut=cut}; Assert.Throws<PowerLoss>(()=>Operate(first,2,id,dir));
        var recoveryTrace=new CrashDevice(first.Durable); FileMutationTests.Mount(recoveryTrace,new Ram(new byte[100000]));
        for(int second=1;second<=recoveryTrace.Events.Count;second++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(first.Durable){Cut=second,Persistence=mode};
            Assert.Throws<PowerLoss>(()=>FileMutationTests.Mount(fault,new Ram(new byte[100000])));
            var final=new MemoryDevice(fault.Durable); FileMutationTests.Mount(final,new Ram(new byte[100000])); Verify(final,2,id,dir);
            Assert.False(new Cpx4Volume<MemoryDevice>(final,new byte[36864]).TryGetInfo("source"u8,out _));
        }
        output.WriteLine($"Interrupted recovery/cleanup: {recoveryTrace.Events.Count} cuts x 5.");
    }
}
