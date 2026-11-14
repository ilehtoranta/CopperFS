using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

public static class DmaAllocationProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000;
        uint memoryType=APTR.ReadUInt32(APTR.FromPointer(0x7ff0),0);
        uint mask=APTR.ReadUInt32(APTR.FromPointer(0x7ff0),4);
        const uint caller=0x90000;
        NativeCpxTransport.Initialize(arena,APTR.FromPointer(0x8200),default,Wide.From(8192),4096,mask,true);
        if(!NativeCpxTransport.ReserveDma(arena,memoryType)) return 103;
        for(uint op=0;op<2;op++)
        {
            var io=new CpxIo {Kind=op==0?IoKind.Write:IoKind.Read,Buffer=caller,Bytes=1024,Token=1+op};
            StepResult step;
            do { step=NativeCpxTransport.Service(arena,io,out _,out _); } while(step==StepResult.Pending);
            if(step!=StepResult.Complete) return 20;
        }
        var query=new CpxIo {Kind=IoKind.QueryDiscard,Buffer=caller,Bytes=4,Token=3};
        StepResult queryStep; bool unsupported;
        do { queryStep=NativeCpxTransport.Service(arena,query,out _,out unsupported); } while(queryStep==StepResult.Pending);
        if(queryStep!=StepResult.Complete||!unsupported) return 20;
        NativeCpxTransport.ReleaseDma(arena); NativeCpxTransport.ReleaseDma(arena); return 0;
    }
}

public class NativeDmaInstructions
{
    private static M68kCompilationResult Compile(Type entry,string method)=>AmigaM68kCompiler.Compile(new()
    {
        AssemblyPath=entry.Assembly.Location,EntryPoint=entry.FullName+"::"+method,
        ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
        Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,
        RuntimeProfile=M68kRuntimeProfile.Freestanding,MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo
    });
    private static readonly Lazy<M68kCompilationResult> Transport=new(()=>Compile(typeof(DmaAllocationProbe),"Entry"));
    private static readonly Lazy<M68kCompilationResult> Handler=new(()=>Compile(typeof(NativeCpxHandler),"Entry"));

    // Exec owns every block in this ledger. Reject duplicate frees, changed
    // lengths, freeing an interior pointer and releasing storage during DMA.
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public void RestrictiveMaskBouncesSplitWritesAndDelayedReads(bool chipOnly,bool classMismatch)
    {
        var bus=new NativeTestBus(); bus.Put(0x7ff0,chipOnly?3u:1u);
        bus.Put(0x7ff4,classMismatch?0xffffeu:0x1fffeu);
        byte[] original=Enumerable.Range(0,1024).Select(n=>(byte)(n*13+7)).ToArray();
        original.CopyTo(bus.Bytes,0x90000);
        var live=new Dictionary<uint,uint>(); int allocations=0,submits=0,polls=0,reaps=0,typeChecks=0; bool active=false;
        uint result=Execute(Transport.Value,bus,(int lvo,IM68kCore cpu)=>
        {
            switch(lvo)
            {
                case 534: Assert.True(classMismatch); Assert.Equal(0x41000u,cpu.State.A[1]); typeChecks++; cpu.State.D[0]=5; break;
                case 198:
                    Assert.False(active); Assert.Equal(0,submits); allocations++;
                    uint size=cpu.State.D[0],flags=cpu.State.D[1];
                    Assert.True((flags&(chipOnly?3u:1u))==(chipOnly?3u:1u));
                    // All page-sized candidates are unavailable. A 1 KiB candidate
                    // crosses the ceiling and must be returned to Exec.
                    uint allocated=size==1024?(classMismatch?0xffe00u:0x1fe00u):size==512?0xa000u:0;
                    if(allocated!=0) Assert.True(live.TryAdd(allocated,size));
                    cpu.State.D[0]=allocated; break;
                case 210:
                    Assert.False(active); Assert.True(live.Remove(cpu.State.A[1],out uint owned));
                    Assert.Equal(owned,cpu.State.D[0]); break;
                case 462:
                    Assert.False(active); Assert.InRange(submits,0,4);
                    if(submits==4)
                    {
                        Assert.Equal(28,bus.Bytes[0x821d]); Assert.Equal(0xa000u,bus.Get(0x8228));
                        Assert.Equal(0xa050u,bus.Get(0xa000)); Assert.Equal(0xa020u,bus.Get(0xa00c)); Assert.Equal(0xa030u,bus.Get(0xa016));
                        submits++; active=true; polls=0; break;
                    }
                    Assert.Equal(submits<2?25:24,bus.Bytes[0x821d]); Assert.Equal(0xa000u,bus.Get(0x8228));
                    Assert.Equal(512u,bus.Get(0x8224)); Assert.Equal((uint)(submits%2*512),bus.Get(0x822c));
                    if(submits<2) Assert.Equal(original[(submits*512)..(submits*512+512)],bus.Bytes[0xa000..0xa200]);
                    else bus.Bytes.AsSpan(0xa000,512).Fill((byte)submits);
                    submits++; active=true; polls=0; break;
                case 468: Assert.True(active); cpu.State.D[0]=++polls==1?0u:0x8200u; break;
                case 474:
                    Assert.True(active); Assert.True(polls>=2); active=false; reaps++; bus.Put(0x8220,512);
                    if(submits==5) bus.Bytes[0x821f]=253;
                    cpu.State.D[0]=0; break;
                default: return false;
            }
            return true;
        });
        Assert.Equal(0u,result); Assert.Equal(chipOnly?4:10,allocations); Assert.Equal(5,reaps); Assert.Empty(live);
        Assert.Equal(classMismatch?1:0,typeChecks);
        Assert.All(bus.Bytes[0x90000..0x90200],b=>Assert.Equal(2,b)); Assert.All(bus.Bytes[0x90200..0x90400],b=>Assert.Equal(3,b));
    }

    [Theory][InlineData("arena")][InlineData("no dma")][InlineData("unreachable dma")][InlineData("open device")]
    public void FailedStartupRepliesNoFreeStoreAndReleasesEachOwnedBlock(string failure)
    {
        var bus=new NativeTestBus(); const uint arena=0x40008;
        bus.Put(0x830a,0x8320);bus.Put(0x8324,0x8100);bus.Put(0x8338,0x8380>>2);
        bus.Put(0x8384,0x83c0>>2);bus.Put(0x8388,0x8400>>2);
        bus.Put(0x8400,16);bus.Put(0x8404,128);bus.Put(0x840c,1);bus.Put(0x8410,1);bus.Put(0x8414,8192);
        bus.Put(0x8430,3);bus.Put(0x8434,4096);bus.Put(0x8438,0x1f000);
        var live=new Dictionary<uint,uint>(); int tries=0,opens=0,replies=0,deletePorts=0,deleteRequests=0;
        uint result=Execute(Handler.Value,bus,(int lvo,IM68kCore cpu)=>
        {
            switch(lvo)
            {
                case 552: cpu.State.D[0]=0x9800; break;
                case 294: cpu.State.D[0]=0x7000; break;
                case 384: case 372: cpu.State.D[0]=0x8300; break;
                case 198:
                    uint size=cpu.State.D[0],address;
                    if(size==CpxArena.Bytes(4096)) address=failure=="arena"?0:arena;
                    else
                    {
                        Assert.True(tries++<4); Assert.Equal(3u,cpu.State.D[1]);
                        address=failure switch {"unreachable dma"=>0x30008u,"open device"=>0xa008u,_=>0};
                    }
                    if(address!=0) Assert.True(live.TryAdd(address,size));
                    cpu.State.D[0]=address; break;
                case 210:
                    Assert.True(live.Remove(cpu.State.A[1],out uint length)); Assert.Equal(length,cpu.State.D[0]); break;
                case 666: cpu.State.D[0]=0x8040; break;
                case 654: cpu.State.D[0]=0x8200; break;
                case 444: opens++; cpu.State.D[0]=1; break;
                case 366: Assert.Equal(0x8300u,cpu.State.A[1]); Assert.Equal(0u,bus.Get(0x832c)); Assert.Equal(103u,bus.Get(0x8330)); replies++; break;
                case 660: deleteRequests++; break;
                case 672: deletePorts++; break;
                case 414: break;
                case 462: Assert.Fail("Startup submitted I/O after allocation/device-open failure."); break;
                default: return false;
            }
            return true;
        });
        Assert.Equal(20u,result); Assert.Equal(1,replies); Assert.Empty(live);
        Assert.Equal(failure=="arena"?0:failure=="open device"?1:4,tries); Assert.Equal(failure=="open device"?1:0,opens);
        Assert.Equal(failure=="arena"?0:1,deletePorts); Assert.Equal(deletePorts,deleteRequests);
    }

    private static uint Execute(M68kCompilationResult compiled,NativeTestBus bus,Func<int,IM68kCore,bool> hook)
    {
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var reloc in compiled.Relocations)
        { uint p=0x10000+(uint)reloc.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus);cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        for(int n=0;n<3_000_000&&cpu.State.ProgramCounter!=0xf1000;n++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            if(lvo>0&&lvo<=1000&&hook(lvo,cpu)) { cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]);cpu.State.A[7]+=4; }
            else cpu.ExecuteInstruction();
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); return cpu.State.D[0];
    }
}
