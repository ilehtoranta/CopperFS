using Amiga;
using CopperCPX.Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using System.Buffers.Binary;
namespace CopperCPX.InstructionTests;

public static class NativeRecordTimerProbe
{
    private const uint Arena=0x40000,Packet=0x8000;
    private static uint Call(ref CpxDosDispatcher<NativeCpxMemory> dispatcher,uint action,uint owner,uint timeout=0)
    {
        APTR p=APTR.FromPointer(Packet); APTR.WriteUInt32(p,8,action); APTR.WriteUInt32(p,20,owner);
        APTR.WriteUInt32(p,24,0); APTR.WriteUInt32(p,28,8); APTR.WriteUInt32(p,32,0); APTR.WriteUInt32(p,36,timeout);
        if(dispatcher.BeginPacket(Packet)!=CpxError.None) return 900;
        for(uint n=0;n<100;n++) { if(dispatcher.Step()==StepResult.Complete) return dispatcher.DosError; }
        return 901;
    }
    public static uint Entry()
    {
        var memory=new NativeCpxMemory(); var engine=new CpxEngine<NativeCpxMemory>(memory,Arena);
        if(engine.Initialize(512,512,Wide.From(8192),1)!=CpxError.None) return 1;
        memory.Write32(Arena+40,1); // This component fixture supplies an already-mounted object/handle model.
        var dispatcher=new CpxDosDispatcher<NativeCpxMemory>(memory,Arena); dispatcher.Initialize(0x9010,0x9020);
        bool initialized=NativeCpxRecordTimers.Initialize(Arena);
        if(memory.Read32(0x7ff0)!=0) return initialized?2u:0u;
        if(!initialized) return 3;
        var pool=new FixedPools<NativeCpxMemory>(memory,Arena+CpxArena.Scratch+6*1024+CpxArena.IndexBytes);
        if(pool.Take(ResourceKind.Handle,out uint a,out _)!=CpxError.None||pool.Take(ResourceKind.Handle,out uint b,out _)!=CpxError.None) return 4;
        memory.Write32(a+4,7); memory.Write32(b+4,7);
        if(Call(ref dispatcher,2008,a)!=0||dispatcher.Result!=uint.MaxValue) return 5;
        if(Call(ref dispatcher,2008,b,uint.MaxValue)!=0||!dispatcher.PacketDeferred) return 6;
        uint slot=dispatcher.NextRecordWaitSlot;
        if(!NativeCpxRecordTimers.Service(Arena,ref dispatcher)||!NativeCpxRecordTimers.Active(Arena)) return 7;
        if(NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)) return 8;
        if(!dispatcher.CancelRecordWait(Packet)||!NativeCpxRecordTimers.Service(Arena,ref dispatcher)) return 9;
        if(NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)||dispatcher.TryTakeRecordReply(out _)) return 10;
        if(!NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)||!dispatcher.TryTakeRecordReply(out var reply)||reply.Error!=304) return 11;
        if(Call(ref dispatcher,2008,b,2)!=0||!NativeCpxRecordTimers.Service(Arena,ref dispatcher)) return 12;
        if(NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)) return 13;
        if(!NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)||!dispatcher.TryTakeRecordReply(out reply)||reply.Error!=242) return 14;
        if(Call(ref dispatcher,2008,b,1000)!=0||!NativeCpxRecordTimers.Service(Arena,ref dispatcher)) return 15;
        // Keep the pending packet separate from the unlock request's output.
        APTR.WriteUInt32(APTR.FromPointer(slot),24,0x8100);
        if(Call(ref dispatcher,2009,a)!=0) return 16;
        for(uint n=0;n<16;n++) dispatcher.StepRecordWaits();
        if(!NativeCpxRecordTimers.Service(Arena,ref dispatcher)) return 17;
        if(NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)||dispatcher.TryTakeRecordReply(out _)) return 18;
        if(!NativeCpxRecordTimers.ReapForSlot(Arena,slot,ref dispatcher)||!dispatcher.TryTakeRecordReply(out reply)||reply.Result!=uint.MaxValue) return 19;
        if(NativeCpxRecordTimers.Active(Arena)||dispatcher.HasAsyncResources) return 20;
        NativeCpxRecordTimers.Destroy(Arena); return 0;
    }
}

public class NativeRecordTimerInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(false)][InlineData(true)]
    public void CompiledTimersOwnRequestsUntilReapAndCleanStartupFailure(bool failOpen)
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeRecordTimerProbe).Assembly.Location,EntryPoint=typeof(NativeRecordTimerProbe).FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new MemoryBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var r in compiled.Relocations) { uint p=0x10000+(uint)r.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Bytes.AsSpan(0xee000,8192).Fill(0xa5); bus.Put(4,0x9000); bus.Put(0x7ff0,failOpen?1u:0u); bus.Put(0xf0000,0xf1000);
        bool[] active=new bool[16],opened=new bool[16],allocated=new bool[16],aborted=new bool[16]; int[] polls=new int[16];
        int created=0,opens=0,closes=0,deletes=0,sends=0,reaps=0,aborts=0,ports=0; uint minimum=0xf0000;
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int instructions=0;
        while(cpu.State.ProgramCounter!=0xf1000&&instructions++<30_000_000)
        {
            uint pc=cpu.State.ProgramCounter; int lvo=unchecked((int)(0x9000-pc));
            if(lvo is 654 or 660 or 666 or 672 or 444 or 450 or 462 or 468 or 474 or 480)
            {
                if(lvo==666) { ports++; cpu.State.D[0]=0x9200; }
                else if(lvo==672) { Assert.Equal(0x9200u,cpu.State.A[0]); ports--; }
                else if(lvo==654)
                {
                    Assert.Equal(0x9200u,cpu.State.A[0]); Assert.Equal(40u,cpu.State.D[0]);
                    allocated[created]=true; cpu.State.D[0]=0x9400+(uint)created++*64;
                }
                else
                {
                    uint request=cpu.State.A[lvo==660?0:1]; int index=checked((int)(request-0x9400)/64); Assert.InRange(index,0,15);
                    if(lvo==444) { opens++; bool fail=failOpen&&opens==9; opened[index]=!fail; cpu.State.D[0]=fail?0xffffffff:0; }
                    else if(lvo==450) { Assert.False(active[index]); Assert.True(opened[index]); opened[index]=false; closes++; }
                    else if(lvo==660) { Assert.False(active[index]||opened[index]); Assert.True(allocated[index]); allocated[index]=false; deletes++; }
                    else if(lvo==462)
                    {
                        Assert.False(active[index]); active[index]=true; aborted[index]=false; polls[index]=0; sends++;
                        Assert.Equal(9,BinaryPrimitives.ReadUInt16BigEndian(bus.Bytes.AsSpan((int)request+28)));
                        Assert.Equal(0,bus.Bytes[request+30]); Assert.Equal(0,bus.Bytes[request+31]);
                        Assert.Equal(sends==1?85899345u:sends==2?0u:20u,bus.Get(request+32));
                        Assert.Equal(sends==1?900000u:sends==2?40000u:0u,bus.Get(request+36));
                    }
                    else if(lvo==480) { Assert.True(active[index]); aborted[index]=true; aborts++; }
                    else if(lvo==468) { Assert.True(active[index]); polls[index]++; cpu.State.D[0]=polls[index]>=(sends==1?3:2)?request:0; }
                    else if(lvo==474) { Assert.True(active[index]); active[index]=false; reaps++; cpu.State.D[0]=aborted[index]?0xfffffffeu:0; }
                }
                cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
            }
            else cpu.ExecuteInstruction();
            minimum=Math.Min(minimum,cpu.State.A[7]); Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u); Assert.False(cpu.State.Stopped||cpu.State.Halted);
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]); Assert.Equal(0,ports);
        Assert.Equal(failOpen?9:16,created); Assert.Equal(created,deletes); Assert.Equal(failOpen?8:16,closes);
        Assert.Equal(failOpen?0:3,sends); Assert.Equal(sends,reaps); Assert.Equal(failOpen?0:2,aborts);
        Assert.All(active,b=>Assert.False(b)); Assert.All(bus.Bytes.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
        output.WriteLine($"Native timers: startup failure={failOpen}, code={compiled.Code.Length}, stack={0xf0000-minimum}, instructions={instructions}; simulated Exec boundary.");
    }
    private sealed class MemoryBus:IM68kBus
    {
        internal byte[] Bytes=new byte[0x100000];
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
