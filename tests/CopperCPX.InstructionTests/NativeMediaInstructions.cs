using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using System.Buffers.Binary;
namespace CopperCPX.InstructionTests;

public static class NativeMediaProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000; uint mode=APTR.ReadUInt32(APTR.FromPointer(0x7ff0),0);
        APTR primary=APTR.FromPointer(0x8000); APTR.WriteUInt32(primary,20,0x1234); APTR.WriteUInt32(primary,24,0x5678);
        bool initialized=NativeCpxMedia.Initialize(arena,primary,APTR.FromPointer(0x8100),APTR.FromPointer(0x8200));
        if(mode is 3 or 4) return !initialized&&NativeCpxMedia.Destroy(arena)?0u:1u;
        if(!initialized) return 2;
        if(mode==1)
        { if(!NativeCpxMedia.Poll(arena)||NativeCpxMedia.Active(arena)||NativeCpxMedia.TakeChange(arena)) return 3; return NativeCpxMedia.Destroy(arena)?0u:4u; }
        if(mode!=5)
        {
            if(NativeCpxMedia.Poll(arena)||NativeCpxMedia.TakeChange(arena)) return 5;
            Exec.Wait(NativeCpxMedia.SignalMask(arena));
            if(!NativeCpxMedia.TakeChange(arena)||NativeCpxMedia.TakeChange(arena)) return 6;
        }
        if(NativeCpxMedia.Detach(arena)!=StepResult.Pending||NativeCpxMedia.Destroy(arena)) return 7;
        if(NativeCpxMedia.Detach(arena)!=StepResult.Pending) return 8;
        StepResult result=NativeCpxMedia.Detach(arena);
        if(mode==2) return result==StepResult.Failed&&!NativeCpxMedia.Destroy(arena)&&NativeCpxMedia.Detach(arena)==StepResult.Failed?0u:9u;
        return result==StepResult.Complete&&NativeCpxMedia.Destroy(arena)?0u:10u;
    }
}

public class NativeMediaInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public void CompiledChangeSubscriptionAndInterruptPreserveOwnership(int mode)
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeMediaProbe).Assembly.Location,EntryPoint=typeof(NativeMediaProbe).FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var relocation in compiled.Relocations) { uint p=0x10000+(uint)relocation.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0x7ff0,(uint)mode); bus.Put(0xf0000,0xf1000); bus.Bytes.AsSpan(0xee000,4096).Fill(0xa5);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int allocations=0,deletes=0,frees=0,sends=0,reaps=0,polls=0,disable=0,signals=0; uint minimum=0xf0000;
        for(int count=0;cpu.State.ProgramCounter!=0xf1000&&count<1_000_000;count++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            if(lvo is 330 or 336 or 654 or 660 or 636 or 462 or 468 or 474 or 120 or 126 or 318)
            {
                switch(lvo)
                {
                    case 330: cpu.State.D[0]=mode==3?uint.MaxValue:7; break;
                    case 336: Assert.Equal(7u,cpu.State.D[0]); frees++; break;
                    case 654: Assert.Equal(56u,cpu.State.D[0]); Assert.Equal(0x8100u,cpu.State.A[0]); allocations++; cpu.State.D[0]=mode==4?0u:0x9400; break;
                    case 660: Assert.Equal(0x9400u,cpu.State.A[0]); deletes++; break;
                    case 636: break;
                    case 120: disable++; break;
                    case 126: Assert.True(disable>0); disable--; break;
                    case 462:
                        sends++; polls=0; Assert.Equal(0x9400u,cpu.State.A[1]);
                        Assert.Equal(sends==1?20:21,BinaryPrimitives.ReadUInt16BigEndian(bus.Bytes.AsSpan(0x941c)));
                        Assert.Equal(0x1234u,bus.Get(0x9414)); Assert.Equal(0x5678u,bus.Get(0x9418));
                        Assert.Equal(22u,bus.Get(0x9424)); Assert.Equal(0x40000u+3784,bus.Get(0x9428));
                        Assert.Equal(0u,bus.Get(0x9420)); Assert.Equal(0,bus.Bytes[0x941e]); Assert.Equal(0,bus.Bytes[0x941f]); break;
                    case 468: polls++; cpu.State.D[0]=mode==1||mode==5&&sends==1||sends==2&&polls>=2?0x9400u:0; break;
                    case 474:
                        reaps++; cpu.State.D[0]=mode==1?0xfffffffdu:mode==2?0xfffffffbu:0;
                        bus.Bytes[0x941f]=mode==1?(byte)253:mode==2?(byte)251:(byte)0; break;
                    case 318:
                        Assert.Equal(128u,cpu.State.D[0]); Callback(bus); signals++; cpu.State.D[0]=128; break;
                }
                cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
            }
            else cpu.ExecuteInstruction();
            minimum=Math.Min(minimum,cpu.State.A[7]); Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u); Assert.False(cpu.State.Stopped||cpu.State.Halted);
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]); Assert.Equal(0,disable);
        Assert.Equal(mode==3?0:1,allocations); Assert.Equal(mode is 2 or 3 or 4?0:1,deletes);
        Assert.Equal(mode is 2 or 3?0:1,frees); Assert.Equal(mode is 3 or 4?0:mode==1?1:2,sends);
        Assert.Equal(mode is 3 or 4?0:mode==5?2:1,reaps); Assert.Equal(mode is 0 or 2?1:0,signals);
        Assert.All(bus.Bytes.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
        output.WriteLine($"Native media subscription mode={mode}: stack={0xf0000-minimum}, code={compiled.Code.Length}; simulated Exec, executed MC68000 callback.");
    }
    private static void Callback(NativeTestBus bus)
    {
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus);
        bus.Put(0xed000,0xed800); cpu.Reset(0x40000+3744,0xed000);
        for(int n=0;n<8;n++) cpu.State.D[n]=(uint)(0x11110000+n);
        for(int n=0;n<7;n++) cpu.State.A[n]=(uint)(0x22220000+n);
        cpu.State.A[1]=0x40000+3808; uint minimum=0xed000; int signals=0;
        for(int n=0;cpu.State.ProgramCounter!=0xed800&&n<100;n++)
        {
            if(cpu.State.ProgramCounter==0x9000-324)
            { Assert.Equal(128u,cpu.State.D[0]); Assert.Equal(0x8200u,cpu.State.A[1]); signals++; cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4; }
            else cpu.ExecuteInstruction();
            minimum=Math.Min(minimum,cpu.State.A[7]);
        }
        Assert.Equal(0xed800u,cpu.State.ProgramCounter); Assert.Equal(1,signals); Assert.Equal(1u,bus.Get(0x40000+3808));
        Assert.Equal(0u,cpu.State.D[0]); for(int n=1;n<8;n++) Assert.Equal((uint)(0x11110000+n),cpu.State.D[n]);
        for(int n=0;n<7;n++) Assert.Equal(n==1?0x40000u+3808:(uint)(0x22220000+n),cpu.State.A[n]);
        Assert.InRange(0xed000-minimum,1u,64u);
    }
}

internal sealed class NativeTestBus:IM68kBus
{
    internal readonly byte[] Bytes=new byte[0x100000];
    internal uint Get(uint p)=>BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p));
    internal void Put(uint p,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)p),value);
    public void ResetExternalDevices(long c) { }
    public byte ReadByte(uint a,ref long c,M68kBusAccessKind k)=>Bytes[a];
    public ushort ReadWord(uint a,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)a));
    public uint ReadLong(uint a,ref long c,M68kBusAccessKind k)=>Get(a);
    public void WriteByte(uint a,byte v,ref long c,M68kBusAccessKind k)=>Bytes[a]=v;
    public void WriteWord(uint a,ushort v,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)a),v);
    public void WriteLong(uint a,uint v,ref long c,M68kBusAccessKind k)=>Put(a,v);
}
