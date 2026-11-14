using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
namespace CopperCPX.InstructionTests;
public static class NativeVolumeProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000; APTR volume=APTR.FromPointer(0x8000),label=APTR.FromPointer(0x8100);
        DOS.DOSLibraryBase=APTR.FromPointer(0x9000); APTR.WriteUInt32(volume,40,0x2345);
        APTR.WriteUInt8(label,0,1); APTR.WriteUInt8(label,1,0xe9);
        NativeCpxVolume.Initialize(arena,volume,label.Raw);
        if(APTR.ReadUInt32(volume,40)!=(arena+3904)>>2) return 1;
        NativeCpxVolume.Publish(arena,label.Raw); // Unchanged names acquire no lock.
        APTR.WriteUInt8(label,0,2); APTR.WriteUInt8(label,2,0xff);
        NativeCpxVolume.Publish(arena,label.Raw);
        if(APTR.ReadUInt8(APTR.FromPointer(arena+3904),0)!=2||APTR.ReadUInt8(APTR.FromPointer(arena+3904),2)!=0xff) return 2;
        NativeCpxVolume.Restore(arena,volume); if(APTR.ReadUInt32(volume,40)!=0x2345) return 3;
        APTR.WriteUInt8(label,0,0); NativeCpxVolume.Publish(arena,label.Raw); return 0;
    }
}
public class NativeVolumeInstructions
{
    [Fact] public void CompiledLabelPublicationLocksTheListAndRestoresAllocationOwnership()
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeVolumeProbe).Assembly.Location,EntryPoint=typeof(NativeVolumeProbe).FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var relocation in compiled.Relocations) { uint p=0x10000+(uint)relocation.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(0xf0000,0xf1000); using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int locks=0,unlocks=0;
        for(int n=0;cpu.State.ProgramCounter!=0xf1000&&n<100000;n++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            if(lvo is 654 or 660)
            {
                Assert.Equal(10u,cpu.State.D[1]); if(lvo==654) { locks++; Assert.Equal(1,bus.Bytes[0x40000+3904]); }
                else { unlocks++; Assert.Equal(2,bus.Bytes[0x40000+3904]); }
                cpu.State.D[0]=0x8500; cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
            }
            else cpu.ExecuteInstruction();
            Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u); Assert.False(cpu.State.Halted||cpu.State.Stopped);
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]); Assert.Equal(1,locks); Assert.Equal(1,unlocks);
    }
}
