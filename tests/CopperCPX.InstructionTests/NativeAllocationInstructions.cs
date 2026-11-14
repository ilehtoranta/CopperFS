using Amiga;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

public sealed class NativeAllocationInstructions
{
    private static M68kCompilationRequest Request(string entry, M68kCpuTarget cpu) => new()
    {
        AssemblyPath=typeof(NativeCpxHandler).Assembly.Location, EntryPoint=entry,
        ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],
        IncludedExportNames=[], Cpu=cpu, OutputFormat=M68kOutputFormat.Hunk,
        RuntimeProfile=M68kRuntimeProfile.Freestanding, MemoryManagement=M68kMemoryManagement.None,
        ExceptionMode=M68kExceptionMode.Yolo, PeepholeOptimization=M68kPeepholeOptimizationMode.FixedPoint
    };

    [Theory] [InlineData(M68kCpuTarget.M68000)] [InlineData(M68kCpuTarget.M68020)]
    public void CompleteHandlerEntryHasNoReachableManagedAllocation(M68kCpuTarget cpu)
    {
        // NativeCpxHandler and the adapter are linked from exactly the same
        // source files as the standalone build; unlike a component probe the
        // root is the actual handler entry and all compiler-reachable code.
        var compiled=AmigaM68kCompiler.Compile(Request(typeof(NativeCpxHandler).FullName+"::Entry",cpu));
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.NotEmpty(compiled.Image);
    }

    [Fact]
    public void NativeCompilerRejectsAReachableManagedAllocation()
    {
        var error=Assert.Throws<M68kCompilationException>(() => AmigaM68kCompiler.Compile(
            Request(typeof(ManagedAllocationControl).FullName+"::Entry",M68kCpuTarget.M68000)));
        Assert.Contains("managed allocation requires a managed heap",error.Message);
    }
}

public static class ManagedAllocationControl
{
    public static uint Entry()
    {
        var bytes=new byte[8];
        bytes[0]=APTR.ReadUInt8(APTR.FromPointer(0x8000),0);
        return bytes[0];
    }
}
