using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

internal static class ShadowInstructionProfile
{
    // Match the standalone handler's container and generated-code pass profile.
    internal static M68kCompilationResult Compile(Type probe) => AmigaM68kCompiler.Compile(new M68kCompilationRequest
    {
        AssemblyPath = probe.Assembly.Location, EntryPoint = probe.FullName + "::Entry",
        ManagedAssemblyPaths = [typeof(CpxShadowCommit<GuestMemory>).Assembly.Location, typeof(global::Amiga.APTR).Assembly.Location],
        IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
        RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
        ExceptionMode = M68kExceptionMode.Yolo, ClrPolicy = M68kClrPolicy.Always,
        CodeSizeOptimizations = new()
        {
            ElideUnusedRegisterArguments = true, ReuseIncomingArgumentHomes = true,
            ClusterInternalCalls = false, InlineSingleUseMethods = true, ForwardReadOnlyAggregateLocals = true,
            RemoveRedundantTransport = true, CompactPrivateStackCopies = true, CompactGuestMemory = true,
            NarrowOperations = true, EliminateRedundantInitialization = true, InlineMemoryHelpers = true,
            SimplifyLoopPhis = true, ShareReturnSequences = false, ShareIdenticalMethods = false
        }
    });
}
