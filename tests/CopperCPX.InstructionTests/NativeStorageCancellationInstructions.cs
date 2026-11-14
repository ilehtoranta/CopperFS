using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using System.Buffers.Binary;
namespace CopperCPX.InstructionTests;

public static class NativeStorageCancellationProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000,buffer=0x80000; uint mode=APTR.ReadUInt32(APTR.FromPointer(0x7ff0),0);
        NativeCpxTransport.Initialize(arena,APTR.FromPointer(0x8200),Wide.From(512),Wide.From(8192),512,0x7fffe,true);
        var io=new CpxIo {Kind=mode==0?IoKind.Read:mode==1?IoKind.Write:mode==2?IoKind.Flush:IoKind.Discard,Sector=Wide.From(1024),Bytes=mode==2?0u:1024,Buffer=buffer,Token=1};
        if(NativeCpxTransport.Service(arena,io,out _,out _)!=StepResult.Pending) return 1;
        if(mode==1)
        {
            // First split reached the device, but the entire engine request has
            // not completed. Cancellation must never submit its second split.
            if(NativeCpxTransport.Service(arena,io,out uint prefix,out _)!=StepResult.Pending||prefix!=512) return 2;
        }
        NativeCpxTransport.RequestStop(arena); NativeCpxTransport.RequestStop(arena);
        if(mode!=1)
        {
            if(NativeCpxTransport.ResetMedia(arena)) return 3;
            if(NativeCpxTransport.Service(arena,io,out _,out _)!=StepResult.Pending) return 4;
        }
        if(NativeCpxTransport.Service(arena,io,out uint actual,out bool unsupported)!=StepResult.Failed||actual!=0||unsupported||NativeCpxTransport.Waiting(arena)) return 5;
        if(mode==0) for(uint n=0;n<1024;n++) if(APTR.ReadUInt8(APTR.FromPointer(buffer+n),0)!=0xa5) return 6;
        if(!NativeCpxTransport.ResetMedia(arena)) return 7;
        if(APTR.ReadUInt32(APTR.FromPointer(arena+2304),0)!=0) return 8;
        return 0;
    }
}

public class NativeStorageCancellationInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void CompiledCancellationQuiescesReadWriteFlushAndUnmapWithoutPublishingProgress(int mode)
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeStorageCancellationProbe).Assembly.Location,EntryPoint=typeof(NativeStorageCancellationProbe).FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var r in compiled.Relocations) { uint p=0x10000+(uint)r.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0x7ff0,(uint)mode); bus.Put(0xf0000,0xf1000); bus.Bytes.AsSpan(0xee000,4096).Fill(0xa5);
        bus.Bytes.AsSpan(0x80000,1024).Fill(0xa5);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int sends=0,aborts=0,polls=0,reaps=0; bool active=false; uint minimum=0xf0000;
        for(int n=0;cpu.State.ProgramCounter!=0xf1000&&n<3_000_000;n++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            if(lvo is 462 or 468 or 474 or 480)
            {
                Assert.Equal(0x8200u,cpu.State.A[1]);
                if(lvo==462)
                {
                    Assert.False(active); active=true; sends++;
                    Assert.Equal(mode==0?24:mode==1?25:mode==2?4:28,BinaryPrimitives.ReadUInt16BigEndian(bus.Bytes.AsSpan(0x821c)));
                    Assert.Equal(0,bus.Bytes[0x821e]); Assert.Equal(0,bus.Bytes[0x821f]);
                    if(mode==0) { Assert.Equal(0x41000u,bus.Get(0x8228)); bus.Bytes.AsSpan(0x41000,512).Fill(0x5a); }
                }
                else if(lvo==480) { Assert.True(active); aborts++; }
                else if(lvo==468) { Assert.True(active); polls++; cpu.State.D[0]=mode==1||polls>=2?0x8200u:0; }
                else
                {
                    Assert.True(active); active=false; reaps++;
                    // The completion races cancellation and claims success.
                    // Abort ownership, not the device's optimistic status, wins.
                    bus.Put(0x8220,mode<2?512u:0); bus.Bytes[0x821f]=0; cpu.State.D[0]=0;
                }
                cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
            }
            else cpu.ExecuteInstruction();
            minimum=Math.Min(minimum,cpu.State.A[7]); Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u); Assert.False(cpu.State.Stopped||cpu.State.Halted);
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]);
        Assert.Equal(1,sends); Assert.Equal(1,reaps); Assert.Equal(mode==1?0:1,aborts); Assert.False(active);
        Assert.All(bus.Bytes.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
        output.WriteLine($"Storage cancellation mode={mode}: stack={0xf0000-minimum}, code={compiled.Code.Length}; simulated device/Exec, compiled transport.");
    }
}
