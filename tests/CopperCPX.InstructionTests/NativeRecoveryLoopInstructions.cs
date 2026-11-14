using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperFS.Cpx4.Reference;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

public static class NativeRecoveryLoopProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000;
        NativeCpxTransport.Initialize(arena,APTR.FromPointer(0x8200),default,Wide.From(8192),4096,uint.MaxValue,true);
        NativeCpxLifecycle.BeginStartup(arena,APTR.FromPointer(0x8300),APTR.FromPointer(0x8320));
        var dispatcher=new CpxDosDispatcher<NativeCpxMemory>(new NativeCpxMemory(),arena);
        return NativeCpxHandler.Run(arena,APTR.FromPointer(0x8000),APTR.FromPointer(0x8040),ref dispatcher);
    }
}

public class NativeRecoveryLoopInstructions
{
    private static readonly Lazy<M68kCompilationResult> Compiled=new(()=>AmigaM68kCompiler.Compile(new()
    {
        AssemblyPath=typeof(NativeRecoveryLoopProbe).Assembly.Location,EntryPoint=typeof(NativeRecoveryLoopProbe).FullName+"::Entry",
        ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],
        IncludedExportNames=[],Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,
        RuntimeProfile=M68kRuntimeProfile.Freestanding,MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo
    }));

    [Theory][InlineData(false)][InlineData(true)]
    public void DelayedStartupReplayCanBeInterruptedBeforeOrAfterAHomeWrite(bool stopAtBarrier)
    {
        var disk=new CpxBootTests.Image(); Cpx4Volume<CpxBootTests.Image>.Format(disk,512,new byte[36864],12345);
        var volume=new Cpx4Volume<CpxBootTests.Image>(disk,new byte[36864]); volume.Create("Inline"u8,out ulong id);
        CpxBootTests.MakeReplay(disk);
        // One case combines a committed log and a damaged primary. The
        // backup must authorize replay even when shutdown races its barrier.
        if(stopAtBarrier) disk.Bytes[65536+80]^=1;
        var compiled=Compiled.Value; Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.True(compiled.Code.Length<0x30000);
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var reloc in compiled.Relocations)
        { uint p=0x10000+(uint)reloc.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000);
        bus.Bytes[0x800f]=7; bus.Put(0x8014,0x8018); bus.Bytes[0x804f]=8; bus.Put(0x8054,0x8058);
        bus.Put(0x830a,0x8320); bus.Put(0x8324,0x8100); bus.Put(0x832c,123);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus);
        cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int waits=0,submitted=0,reaped=0,aborted=0,replies=0,transfersThisVisit=0;
        bool active=false,ready=false,interrupted=false,homeWritten=false;
        int command=0;
        for(int n=0;n<40_000_000&&cpu.State.ProgramCounter!=0xf1000;n++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            switch(lvo)
            {
                case 306: // SetSignal at the top of each real dispatch visit
                    transfersThisVisit=0; cpu.State.D[0]=0; break;
                case 462:
                    Assert.Equal(1,++transfersThisVisit); Assert.False(active||interrupted);
                    Assert.Equal(0x8200u,cpu.State.A[1]); active=true; ready=false; waits=0; submitted++;
                    command=bus.Bytes[0x821d]; Assert.True(command is 24 or 25 or 4); break;
                case 468:
                    Assert.True(active); cpu.State.D[0]=ready?0x8200u:0; break;
                case 318:
                    Assert.True(active); Assert.Equal(0,replies); waits++; cpu.State.D[0]=128;
                    if(waits==2&&(stopAtBarrier?command==4:command==25))
                    { Assert.False(interrupted); interrupted=true; cpu.State.D[0]=4096; }
                    if(waits>=3) { ready=true; cpu.State.D[0]=256; }
                    break;
                case 480:
                    Assert.True(active&&interrupted); Assert.Equal(1,++aborted); break;
                case 474:
                    Assert.Equal(1,++transfersThisVisit); Assert.True(active&&ready); Assert.Equal(0,replies);
                    int offset=(int)bus.Get(0x822c),length=(int)bus.Get(0x8224),buffer=(int)bus.Get(0x8228);
                    if(command==24) disk.Bytes.AsSpan(offset,length).CopyTo(bus.Bytes.AsSpan(buffer));
                    // A write may already have reached home even when AbortIO races completion.
                    if(command==25) { bus.Bytes.AsSpan(buffer,length).CopyTo(disk.Bytes.AsSpan(offset)); homeWritten=true; }
                    bus.Put(0x8220,(uint)length); cpu.State.D[0]=0; active=false; reaped++; break;
                case 366:
                    Assert.False(active); Assert.True(interrupted); Assert.Equal(submitted,reaped);
                    Assert.Equal(0x8300u,cpu.State.A[1]); Assert.Equal(0u,bus.Get(0x832c)); Assert.Equal(304u,bus.Get(0x8330));
                    replies++; break;
                case 372:
                    Assert.Equal(1,replies); cpu.State.D[0]=0; break;
                default:
                    cpu.ExecuteInstruction(); continue;
            }
            cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(304u,cpu.State.D[0]);
        Assert.True(homeWritten); Assert.Equal(1,aborted); Assert.Equal(1,replies);
        var reopened=new Cpx4Volume<CpxBootTests.Image>(disk,new byte[36864]); reopened.Check();
        if(stopAtBarrier) Assert.Equal(1,disk.Bytes[65536+80]);
        byte[] recovered=new byte[3]; Assert.Equal(3,reopened.Read(id,0,recovered)); Assert.Equal("rpl"u8.ToArray(),recovered);
    }
}
