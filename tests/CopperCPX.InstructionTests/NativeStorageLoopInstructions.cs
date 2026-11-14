using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
namespace CopperCPX.InstructionTests;

public static class NativeStorageLoopProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000;
        var memory=new NativeCpxMemory(); var engine=new CpxEngine<NativeCpxMemory>(memory,arena);
        uint mode=memory.Read32(0x7ff0);
        if(engine.Initialize(512,512,Wide.From(8192),1,true)!=CpxError.None) return 1;
        memory.Write32(arena+40,1); // Already mounted read-only fixture: Inhibit requires only a barrier.
        var dispatcher=new CpxDosDispatcher<NativeCpxMemory>(memory,arena); dispatcher.Initialize(0x8000,0x8500);
        NativeCpxTransport.Initialize(arena,APTR.FromPointer(0x8200),default,Wide.From(8192),512,uint.MaxValue,true);
        memory.Write32(arena+3824,0x9400); memory.Write32(arena+3828,512); // Registered callback's retained storage, idle in this fixture.
        if(mode is >=6 and <=8) NativeCpxLifecycle.BeginStartup(arena,APTR.FromPointer(0x8300),APTR.FromPointer(0x8320));
        NativeCpxHandler.Run(arena,APTR.FromPointer(0x8000),APTR.FromPointer(0x8040),ref dispatcher);
        return 2;
    }
}

public class NativeStorageLoopInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Lazy<M68kCompilationResult> Compilation=new(()=>AmigaM68kCompiler.Compile(new M68kCompilationRequest {
        AssemblyPath=typeof(NativeStorageLoopProbe).Assembly.Location,EntryPoint=typeof(NativeStorageLoopProbe).FullName+"::Entry",
        ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
        Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
        MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo }));

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    [InlineData(3)][InlineData(4)][InlineData(5)]
    [InlineData(6)][InlineData(7)][InlineData(8)][InlineData(9)]
    public void ActualLoopQuiescesInhibitShutdownAndStartupBeforeReply(int mode)
    {
        var compiled=Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.True(compiled.Code.Length<0x30000);
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var r in compiled.Relocations) { uint p=0x10000+(uint)r.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000); bus.Bytes.AsSpan(0xee000,4096).Fill(0xa5);
        bus.Bytes[0x800f]=7; bus.Put(0x8014,0x8018); bus.Bytes[0x804f]=8; bus.Put(0x8054,0x8058);
        bool startup=mode is >=6 and <=8,die=mode>=3&&!startup,graceful=mode==9;
        int cause=mode%3; // Break, change callback, disk-changed completion
        bus.Put(0x7ff0,(uint)mode);
        Packet(bus,0x8300,0x8320,startup?0u:die?5u:31u,uint.MaxValue); Packet(bus,0x8400,0x8420,25,0x8800>>2);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        uint minimum=0xf0000; int sends=0,aborts=0,reaps=0,waits=0,packets=startup?1:0,replies=0,polls=0;
        bool active=false,complete=false,injected=false,done=false;
        for(int n=0;!done&&n<3_000_000;n++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            if(!startup && lvo is 198 or 204 or 222 or 654 or 666 or 684 or 696 or 708 or 870 or 894 or 936 or 942 or 972 or 984)
                Assert.Fail($"Mounted handler requested Exec allocation LVO -{lvo} while processing storage lifecycle packets.");
            if(lvo is 120 or 126 or 306 or 318 or 366 or 372 or 462 or 468 or 474 or 480)
            {
                switch(lvo)
                {
                    case 120: case 126: break;
                    case 306: cpu.State.D[0]=0; break;
                    case 372:
                        Assert.Equal(0x8000u,cpu.State.A[0]);
                        if(packets==0) { cpu.State.D[0]=0x8300; packets++; }
                        else if(packets==1) { Assert.False(active); Assert.Equal(1,reaps); cpu.State.D[0]=0x8400; packets++; }
                        else cpu.State.D[0]=0;
                        break;
                    case 462:
                        Assert.False(active); Assert.Equal(0x8200u,cpu.State.A[1]); sends++; active=true;
                        Assert.Equal(startup?24:4,bus.Bytes[0x821d]); break;
                    case 480: Assert.True(active); Assert.Equal(0x8200u,cpu.State.A[1]); aborts++; break;
                    case 468:
                        Assert.True(active); polls++; cpu.State.D[0]=complete?0x8200u:0; break;
                    case 474:
                        Assert.True(active&&complete); Assert.True(injected); active=false; reaps++;
                        bus.Bytes[0x821f]=cause==2?(byte)29:(byte)0; bus.Put(0x8220,bus.Get(0x8224));
                        cpu.State.D[0]=cause==2?29u:0; break;
                    case 366:
                        Assert.False(active); Assert.Equal(1,reaps); Assert.Equal(0x8100u,cpu.State.A[0]);
                        if(replies++==0)
                        {
                            Assert.Equal(0x8300u,cpu.State.A[1]); Assert.Equal(graceful?uint.MaxValue:0u,bus.Get(0x832c));
                            Assert.Equal(graceful?0u:cause==0?304u:226u,bus.Get(0x8330)); Assert.Equal(0u,bus.Get(0x40000+512+324));
                        }
                        else
                        {
                            Assert.Equal(0x8400u,cpu.State.A[1]);
                            if(startup||graceful) { Assert.Equal(0u,bus.Get(0x842c)); Assert.Equal(226u,bus.Get(0x8430)); }
                            else { Assert.Equal(uint.MaxValue,bus.Get(0x842c)); Assert.Equal(0u,bus.Get(0x8430)); Assert.Equal(uint.MaxValue,bus.Get(0x8818)); }
                        }
                        break;
                    case 318:
                        waits++;
                        if(replies==2) { Assert.False(active); done=true; break; }
                        Assert.True(active); Assert.Equal(0,replies);
                        if(graceful) { injected=true; complete=waits>=3; cpu.State.D[0]=complete?256u:128u; break; }
                        if(!injected)
                        {
                            injected=true;
                            if(cause==1) bus.Put(0x40000+3808,1);
                            if(cause==2) complete=true;
                            cpu.State.D[0]=cause==0?4096u:cause==1?512u:256u;
                        }
                        else { Assert.Equal(1,aborts); complete=true; cpu.State.D[0]=256; }
                        break;
                }
                if(done) break;
                cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
            }
            else cpu.ExecuteInstruction();
            minimum=Math.Min(minimum,cpu.State.A[7]); Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u);
            Assert.False(cpu.State.Stopped||cpu.State.Halted);
            if(cpu.State.ProgramCounter==0xf1000) { Assert.True(startup||graceful); done=true; }
        }
        Assert.True(done,"Actual Run failed to quiesce."); Assert.Equal(1,sends); Assert.Equal(graceful||cause==2?0:1,aborts); Assert.Equal(1,reaps); Assert.Equal(2,replies);
        Assert.True(waits>=(startup&&cause==2?1:2)); Assert.True(polls>0); Assert.All(bus.Bytes.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
        output.WriteLine($"Actual Run storage stop mode={mode}: stack={0xf0000-minimum}; code={compiled.Code.Length}; waits={waits}; simulated delayed Exec/device.");
    }
    private static void Packet(NativeTestBus bus,uint message,uint packet,uint action,uint argument)
    { bus.Put(message+10,packet); bus.Put(packet,message); bus.Put(packet+4,0x8100); bus.Put(packet+8,action); bus.Put(packet+20,argument); }
}
