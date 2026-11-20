using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

public static class StartupPoolProbe
{
    public static uint Entry()
    {
        const uint arena=0x70000;
        var memory=new NativeCpxMemory(); var engine=new CpxEngine<NativeCpxMemory>(memory,arena);
        uint page=memory.Read32(0x8300);
        if(engine.Initialize(512,512,Wide.From(8192),1,metadataPageBytes:page)!=CpxError.None) return 1;
        uint pool=arena+CpxArena.Scratch+6*page+CpxArena.IndexBytes;
        for(uint n=0;n<CpxArena.PoolBytes;n+=4) memory.Write32(pool+n,0xa5a5a5a5);
        memory.Write32(pool-4,0x12345678); memory.Write32(pool+CpxArena.PoolBytes,0x5a5a5a5a);
        memory.Write32(arena+3952+28,0);
        for(uint turn=0;turn<16;turn++)
        {
            var result=NativeCpxLifecycle.ClearStartupPools(arena);
            if(result!=(turn==15?StepResult.Complete:StepResult.Pending)) return 2;
        }
        for(uint n=0;n<CpxArena.PoolBytes;n+=4) if(memory.Read32(pool+n)!=0) return 3;
        return memory.Read32(pool-4)==0x12345678&&memory.Read32(pool+CpxArena.PoolBytes)==0x5a5a5a5a?0u:4u;
    }
}

public class NativeStartupPoolInstructions
{
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void StartupRetiresScratchFromAllFinalPoolGeometriesInBoundedVisits(uint page)
    {
        var compiled=Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new EngineInstructions.Bus(); compiled.Code.CopyTo(bus.Memory,0x10000);
        foreach(var relocation in compiled.Relocations)
        {uint at=0x10000u+(uint)relocation.Offset;bus.Put(at,bus.Get(at)+0x10000);}
        bus.Put(0x8300,page); bus.Memory.AsSpan(0xee000,8192).Fill(0xa5); bus.Put(0xf0000,0xf1000);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        for(int n=0;cpu.State.ProgramCounter!=0xf1000&&n<10_000_000;n++)
        {cpu.ExecuteInstruction();Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u);Assert.False(cpu.State.Halted||cpu.State.Stopped);}
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter);Assert.Equal(0u,cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
    }
    private static readonly Lazy<M68kCompilationResult> Compilation=new(()=>ShadowInstructionProfile.Compile(typeof(StartupPoolProbe)));
}
