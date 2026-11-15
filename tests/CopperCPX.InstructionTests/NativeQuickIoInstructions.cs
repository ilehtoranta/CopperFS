using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using System.Buffers.Binary;

namespace CopperCPX.InstructionTests;

public static class NativeQuickIoProbe
{
    public static uint Entry()
    {
        APTR request=APTR.FromPointer(0x8200),state=APTR.FromPointer(0x8300);
        APTR.WriteUInt32(request,ExecLayout.IORequest.Device,0xa000);
        var command=new DeviceRequest {Command=2,Buffer=0x80000,Length=512};
        if(NativeCpxIo.Submit(request,state,command,123,true)!=CpxError.None) return 1;
        if(NativeCpxIo.Submit(request,state,command,124,true)!=CpxError.Busy) return 2;
        uint mode=APTR.ReadUInt32(APTR.FromPointer(0x7ff0),0);
        if(mode==1&&NativeCpxIo.Reap(request,state,out _,out _,out _)) return 3;
        if(!NativeCpxIo.Reap(request,state,out uint token,out uint actual,out CpxError error)) return 4;
        if(token!=123||actual!=(mode==2?256u:512u)||error!=(mode>=2?CpxError.Io:CpxError.None)) return 5;
        if(APTR.ReadUInt32(state,0)!=0) return 6;
        NativeCpxIo.RequestAbort(request,state); return 0;
    }
}

public class NativeQuickIoInstructions
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void ActualDeviceVectorSupportsImmediateDeferredShortAndFailedCompletions(int mode)
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeQuickIoProbe).Assembly.Location,EntryPoint=typeof(NativeQuickIoProbe).FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new Bus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var relocation in compiled.Relocations) { uint at=0x10000+(uint)relocation.Offset; bus.Put(at,bus.Get(at)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000); bus.Put(0x7ff0,(uint)mode);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int begins=0,polls=0,reaps=0,steps=0;
        while(cpu.State.ProgramCounter!=0xf1000&&steps++<100000)
        {
            uint pc=cpu.State.ProgramCounter;
            if(pc==0xa000-30)
            {
                begins++; Assert.Equal(0xa000u,cpu.State.A[6]); Assert.Equal(0x8200u,cpu.State.A[1]);
                Assert.Equal(1,bus.Bytes[0x821e]); Assert.Equal(5,bus.Bytes[0x8208]);
                if(mode==1) bus.Bytes[0x821e]=0;
                bus.Put(0x8220,mode==2?256u:512u); bus.Bytes[0x821f]=mode==3?(byte)20:(byte)0;
            }
            else if(pc==0x9000-468) { polls++; cpu.State.D[0]=polls==1?0u:0x8200u; }
            else if(pc==0x9000-474) { reaps++; cpu.State.D[0]=0; }
            else { cpu.ExecuteInstruction(); continue; }
            uint stack=cpu.State.A[7]; cpu.State.ProgramCounter=bus.Get(stack); cpu.State.A[7]=stack+4;
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]); Assert.Equal(1,begins);
        Assert.Equal(mode==1?2:0,polls); Assert.Equal(mode==1?1:0,reaps);
    }
    private sealed class Bus:IM68kBus
    {
        internal readonly byte[] Bytes=new byte[0x100000];
        internal uint Get(uint p)=>BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p));
        internal void Put(uint p,uint v)=>BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)p),v);
        public void ResetExternalDevices(long c) { }
        public byte ReadByte(uint a,ref long c,M68kBusAccessKind k)=>Bytes[a];
        public ushort ReadWord(uint a,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)a));
        public uint ReadLong(uint a,ref long c,M68kBusAccessKind k)=>Get(a);
        public void WriteByte(uint a,byte v,ref long c,M68kBusAccessKind k)=>Bytes[a]=v;
        public void WriteWord(uint a,ushort v,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)a),v);
        public void WriteLong(uint a,uint v,ref long c,M68kBusAccessKind k)=>Put(a,v);
    }
}
