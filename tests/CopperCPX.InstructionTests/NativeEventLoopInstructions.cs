using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using System.Buffers.Binary;

namespace CopperCPX.InstructionTests;

public static class NativeEventLoopProbe
{
    public static uint MediaChangeEntry()=>NativeCpxMedia.TakeChange(Arena)?1u:0u;
    public static uint ControlEntry()=>NativeCpxHandler.TakeControl(APTR.ReadUInt32(APTR.FromPointer(0x7000),0),APTR.FromPointer(0x7100));
    public const uint Arena = 0x70000, HandlerPort = 0x8000, IoPort = 0x8040,
        TimerPort = 0x8080, NormalReplyPort = 0x80c0, WaitReplyPort = 0x8100,
        WaitMessage = 0x8300, WaitPacket = 0x8320, UnlockMessage = 0x8400, UnlockPacket = 0x8420;

    public static uint Entry()
    {
        var memory = new NativeCpxMemory(); var engine = new CpxEngine<NativeCpxMemory>(memory, Arena);
        if (engine.Initialize(512, 512, Wide.From(8192), 1) != CpxError.None) return 1;
        memory.Write32(Arena + 40, 1); // Mounted object/handle fixture; no storage operation is emulated here.
        var dispatcher = new CpxDosDispatcher<NativeCpxMemory>(memory, Arena); dispatcher.Initialize(HandlerPort, 0x8500);
        if (!NativeCpxRecordTimers.Initialize(Arena)) return 2;
        var pools = new FixedPools<NativeCpxMemory>(memory, Arena + CpxArena.Scratch + 6 * 1024 + CpxArena.IndexBytes);
        if (pools.Take(ResourceKind.Handle, out uint a, out _) != CpxError.None ||
            pools.Take(ResourceKind.Handle, out uint b, out _) != CpxError.None) return 3;
        memory.Write32(a + 4, 7); memory.Write32(b + 4, 7);
        memory.Write32(0x8208, 2008); memory.Write32(0x8214, a); memory.Write32(0x821c, 8); memory.Write32(0x8220, 1);
        if (dispatcher.BeginPacket(0x8200) != CpxError.None) return 4;
        for (uint n = 0; n < 10 && dispatcher.Busy; n++) dispatcher.Step();
        if (dispatcher.Busy || dispatcher.DosError != 0) return 5;
        memory.Write32(WaitPacket + 20, b); memory.Write32(UnlockPacket + 20, a);
        memory.Write32(0x7fe8,1); // Startup-owned ports and requests are ready.
        if(memory.Read32(0x7ff8)!=0) Exec.AllocMem(8,Exec.MemoryFlags.Public); // Observer positive control.
        // The test invokes the actual handler loop, not a second event-loop model.
        NativeCpxHandler.Run(Arena, APTR.FromPointer(HandlerPort), APTR.FromPointer(IoPort), ref dispatcher);
        return 6; // A test stops only at an observed, correctly quiesced Exec.Wait.
    }
}

public class NativeEventLoopInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false,true,false)] [InlineData(true,false,false)]
    [InlineData(true,true,false)] [InlineData(true,false,true)]
    public void MediaLatchPeekDoesNotLoseAnInterruptAndOnlyMasksForAcknowledgement(bool registered,bool pending,bool late)
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeEventLoopProbe).Assembly.Location,EntryPoint=typeof(NativeEventLoopProbe).FullName+"::MediaChangeEntry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new Bus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var relocation in compiled.Relocations) { uint at=0x10000+(uint)relocation.Offset; bus.Put(at,bus.Get(at)+0x10000); }
        uint data=NativeEventLoopProbe.Arena+3808;
        bus.Put(4,0x9000); bus.Put(NativeEventLoopProbe.Arena+3824,registered?0xa000u:0u); bus.Put(data,pending?1u:0u);
        bus.Bytes.AsSpan(0xee000,4096).Fill(0xa5);
        bool disabled=false,inject=false,injected=false;
        bus.ReadObserver=(address,value)=> { if(late&&!injected&&!disabled&&(address==data||address==data+2)&&value==0) inject=true; };
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus);
        (uint Result,int Masks) Run()
        {
            bus.Put(0xf0000,0xf1000); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
            int masks=0,enables=0,instructions=0;
            while(cpu.State.ProgramCounter!=0xf1000&&instructions++<10000)
            {
                uint pc=cpu.State.ProgramCounter;
                if(pc==0x9000-120||pc==0x9000-126)
                {
                    if(pc==0x9000-120) { Assert.False(disabled); disabled=true; masks++; }
                    else { Assert.True(disabled); disabled=false; enables++; }
                    uint stack=cpu.State.A[7]; cpu.State.ProgramCounter=bus.Get(stack); cpu.State.A[7]=stack+4;
                }
                else cpu.ExecuteInstruction();
                if(inject) { Assert.False(disabled); bus.Put(data,1); injected=true; inject=false; }
            }
            Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.False(disabled); Assert.Equal(masks,enables);
            Assert.All(bus.Bytes.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
            return (cpu.State.D[0],masks);
        }
        Assert.Equal((registered&&pending?1u:0u,registered&&pending?1:0),Run());
        Assert.Equal(late||!registered&&pending?1u:0u,bus.Get(data));
        if(late) { Assert.True(injected); Assert.Equal((1u,1),Run()); Assert.Equal(0u,bus.Get(data)); }
    }

    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public void ControlPollingPreservesWaitSignalsAndOnlyClearsAnActuallyPendingBreak(bool pending,bool fromWait)
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(NativeEventLoopProbe).Assembly.Location,EntryPoint=typeof(NativeEventLoopProbe).FullName+"::ControlEntry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new Bus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var relocation in compiled.Relocations) { uint at=0x10000+(uint)relocation.Offset; bus.Put(at,bus.Get(at)+0x10000); }
        uint received=128u|(pending?4096u:0u),woken=fromWait?4096u:0u;
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000); bus.Put(0x7000,woken); bus.Put(0x7100+ExecLayout.Task.SignalReceived,received);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        int clears=0,instructions=0;
        while(cpu.State.ProgramCounter!=0xf1000&&instructions++<10000)
        {
            if(cpu.State.ProgramCounter==0x9000-306)
            {
                Assert.Equal(0u,cpu.State.D[0]); Assert.Equal(4096u,cpu.State.D[1]); clears++;
                cpu.State.D[0]=bus.Get(0x7100+ExecLayout.Task.SignalReceived); bus.Put(0x7100+ExecLayout.Task.SignalReceived,received&~4096u);
                uint stack=cpu.State.A[7]; cpu.State.ProgramCounter=bus.Get(stack); cpu.State.A[7]=stack+4;
            }
            else cpu.ExecuteInstruction();
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(pending?1:0,clears);
        Assert.Equal(pending?woken|received:woken,cpu.State.D[0]); Assert.Equal(128u,bus.Get(0x7100+ExecLayout.Task.SignalReceived));
    }
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)]
    public void ActualCompiledLoopDrainsResidualWorkAndConsumesSignalsReturnedByWait(bool cancelFromWait,bool injectAllocation)
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(NativeEventLoopProbe).Assembly.Location,
            EntryPoint = typeof(NativeEventLoopProbe).FullName + "::Entry",
            ManagedAssemblyPaths = [typeof(CpxArena).Assembly.Location, typeof(APTR).Assembly.Location],
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
            RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
            ExceptionMode = M68kExceptionMode.Yolo
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.True(compiled.Code.Length < NativeEventLoopProbe.Arena-0x10000, "Probe code must not overlap its guest arena.");
        var bus = new Bus(); compiled.Code.CopyTo(bus.Bytes, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint address = 0x10000 + (uint)relocation.Offset; bus.Put(address, bus.Get(address) + 0x10000); }
        bus.Put(4, 0x9000); bus.Put(0xf0000, 0xf1000); bus.Put(0x7ff8,injectAllocation?1u:0u); bus.Bytes.AsSpan(0xee000, 4096).Fill(0xa5);
        Port(bus, NativeEventLoopProbe.HandlerPort, 7); Port(bus, NativeEventLoopProbe.IoPort, 8);
        Port(bus, NativeEventLoopProbe.TimerPort, 9);
        Packet(bus, NativeEventLoopProbe.WaitMessage, NativeEventLoopProbe.WaitPacket, NativeEventLoopProbe.WaitReplyPort, 2008, 5000);
        Packet(bus, NativeEventLoopProbe.UnlockMessage, NativeEventLoopProbe.UnlockPacket, NativeEventLoopProbe.NormalReplyPort, 2009, 0);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus);
        cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        uint minimum = 0xf0000, request = 0; int created = 0, sends = 0, aborts = 0, reaps = 0, waits = 0, polls = 0;
        bool parkedPacketSent = false, unlockSent = false, normalReplied = false, waitReplied = false,
            active = false, complete = false, breakDelivered = false, done = false, allocationObserved = false;
        int instructions = 0;
        while (!done && instructions++ < 3_000_000)
        {
            int lvo = unchecked((int)(0x9000 - cpu.State.ProgramCounter));
            if (bus.Get(0x7fe8) != 0 && lvo is 198 or 204 or 222 or 654 or 666 or 684 or 696 or 708 or 870 or 894 or 936 or 942 or 972 or 984)
            {
                if(injectAllocation) { allocationObserved=true; done=true; break; }
                Assert.Fail($"Mounted handler requested Exec allocation LVO -{lvo} during packet processing.");
            }
            if (lvo is 654 or 666 or 444 or 306 or 318 or 366 or 372 or 462 or 468 or 474 or 480)
            {
                switch (lvo)
                {
                    case 666: cpu.State.D[0] = NativeEventLoopProbe.TimerPort; break;
                    case 654:
                        Assert.Equal(40u, cpu.State.D[0]); Assert.Equal(NativeEventLoopProbe.TimerPort, cpu.State.A[0]);
                        cpu.State.D[0] = 0x9400 + (uint)created++ * 64; break;
                    case 444: cpu.State.D[0] = 0; break;
                    case 306:
                        Assert.Equal(4096u, cpu.State.D[1]);
                        // Exec.Wait consumed the break bit: polling SetSignal
                        // afterwards cannot recover it. Only Wait's return can.
                        cpu.State.D[0] = 0; break;
                    case 372:
                        Assert.Equal(NativeEventLoopProbe.HandlerPort, cpu.State.A[0]);
                        if (!parkedPacketSent) { parkedPacketSent = true; cpu.State.D[0] = NativeEventLoopProbe.WaitMessage; }
                        else if (!cancelFromWait && sends == 1 && !unlockSent)
                        { unlockSent = true; cpu.State.D[0] = NativeEventLoopProbe.UnlockMessage; }
                        else cpu.State.D[0] = 0;
                        break;
                    case 462:
                        Assert.False(active); Assert.Equal(0x9400u, cpu.State.A[1]); request = cpu.State.A[1];
                        Assert.Equal(100u, bus.Get(request + 32)); Assert.Equal(0u, bus.Get(request + 36));
                        active = true; complete = false; sends++; break;
                    case 480:
                        Assert.True(active); Assert.Equal(request, cpu.State.A[1]); aborts++; polls = 0; break;
                    case 468:
                        Assert.True(active); Assert.Equal(request, cpu.State.A[1]);
                        if (aborts != 0 && ++polls >= 2) complete = true;
                        if (complete) bus.Put(NativeEventLoopProbe.TimerPort + 20, request);
                        cpu.State.D[0] = complete ? request : 0; break;
                    case 474:
                        Assert.True(active && complete); Assert.Equal(request, cpu.State.A[1]);
                        active = false; reaps++; bus.Put(NativeEventLoopProbe.TimerPort + 20, NativeEventLoopProbe.TimerPort + 24);
                        cpu.State.D[0] = 0xfffffffe; break;
                    case 366:
                        uint target = cpu.State.A[0], message = cpu.State.A[1];
                        if (target == NativeEventLoopProbe.WaitReplyPort)
                        {
                            Assert.False(waitReplied); Assert.False(active); Assert.Equal(1, reaps);
                            Assert.Equal(NativeEventLoopProbe.WaitMessage, message);
                            Assert.Equal(cancelFromWait ? 0u : uint.MaxValue, bus.Get(NativeEventLoopProbe.WaitPacket + 12));
                            Assert.Equal(cancelFromWait ? 304u : 0u, bus.Get(NativeEventLoopProbe.WaitPacket + 16));
                            waitReplied = true;
                        }
                        else
                        {
                            Assert.False(cancelFromWait); Assert.False(normalReplied);
                            Assert.Equal(NativeEventLoopProbe.NormalReplyPort, target); Assert.Equal(NativeEventLoopProbe.UnlockMessage, message);
                            Assert.Equal(uint.MaxValue, bus.Get(NativeEventLoopProbe.UnlockPacket + 12));
                            Assert.Equal(0u, bus.Get(NativeEventLoopProbe.UnlockPacket + 16)); normalReplied = true;
                        }
                        break;
                    case 318:
                        waits++; Assert.Equal((1u << 7) | (1u << 8) | (1u << 9) | 4096u, cpu.State.D[0]);
                        if (waitReplied && (cancelFromWait || normalReplied))
                        {
                            var dispatcher = new CpxDosDispatcher<HostMemory>(new(bus), NativeEventLoopProbe.Arena);
                            Assert.False(dispatcher.Busy); Assert.False(dispatcher.HasAsyncResources); Assert.False(dispatcher.HasRecordWork);
                            Assert.False(active); done = true; break;
                        }
                        Assert.True(active, "The handler must not sleep with an unsubmitted timer descriptor.");
                        if (cancelFromWait && !breakDelivered)
                        { breakDelivered = true; cpu.State.D[0] = 4096; }
                        else
                        {
                            Assert.Equal(1, aborts); complete = true;
                            bus.Put(NativeEventLoopProbe.TimerPort + 20, request); cpu.State.D[0] = 1u << 9;
                        }
                        break;
                }
                if (done) break;
                cpu.State.ProgramCounter = bus.Get(cpu.State.A[7]); cpu.State.A[7] += 4;
            }
            else cpu.ExecuteInstruction();
            minimum = Math.Min(minimum, cpu.State.A[7]); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u);
            Assert.False(cpu.State.Stopped || cpu.State.Halted); Assert.NotEqual(0xf1000u, cpu.State.ProgramCounter);
        }
        if(injectAllocation) { Assert.True(allocationObserved,"The observer missed the injected Exec.AllocMem call."); return; }
        Assert.True(done, "Actual handler loop failed to reach quiescent Exec.Wait within its instruction bound.");
        Assert.Equal(16, created); Assert.Equal(1, sends); Assert.Equal(1, aborts); Assert.Equal(1, reaps);
        Assert.True(waitReplied); Assert.Equal(cancelFromWait, breakDelivered); Assert.True(waits >= 1);
        Assert.All(bus.Bytes.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
        output.WriteLine($"Actual Run: break returned by Wait={cancelFromWait}; code={compiled.Code.Length}; stack={0xf0000-minimum}; instructions={instructions}; waits={waits}; simulated Exec boundary.");
    }

    private static void Port(Bus bus, uint port, byte signal)
    { bus.Bytes[port + 15] = signal; bus.Put(port + 20, port + 24); }
    private static void Packet(Bus bus, uint message, uint packet, uint reply, uint action, uint timeout)
    {
        bus.Put(message + 10, packet); bus.Put(packet, message); bus.Put(packet + 4, reply); bus.Put(packet + 8, action);
        bus.Put(packet + 12, 0x12345678); bus.Put(packet + 28, 8); bus.Put(packet + 36, timeout);
    }
    private struct HostMemory(Bus bus) : ICpxMemory
    {
        public void Copy(uint destination,uint source,uint bytes) => CpxMemoryCopy.Forward(ref this,destination,source,bytes);
        public uint Read16(uint p)=>(uint)Read8(p)<<8|Read8(p+1);
        public void Write16(uint p,uint v) { Write8(p,(byte)(v>>8)); Write8(p+1,(byte)v); }
        public byte Read8(uint p) => bus.Bytes[p];
        public uint Read32(uint p) => bus.Get(p);
        public void Write8(uint p, byte b) => bus.Bytes[p] = b;
        public void Write32(uint p, uint v) => bus.Put(p, v);
    }
    private sealed class Bus : IM68kBus
    {
        internal Action<uint,uint>? ReadObserver;
        internal readonly byte[] Bytes = new byte[0x100000];
        internal uint Get(uint p) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p));
        internal void Put(uint p, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)p), v);
        public void ResetExternalDevices(long c) { }
        public byte ReadByte(uint a, ref long c, M68kBusAccessKind k) => Bytes[a];
        public ushort ReadWord(uint a, ref long c, M68kBusAccessKind k) { ushort value=BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)a)); ReadObserver?.Invoke(a,value); return value; }
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k) { uint value=Get(a); ReadObserver?.Invoke(a,value); return value; }
        public void WriteByte(uint a, byte v, ref long c, M68kBusAccessKind k) => Bytes[a] = v;
        public void WriteWord(uint a, ushort v, ref long c, M68kBusAccessKind k) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)a), v);
        public void WriteLong(uint a, uint v, ref long c, M68kBusAccessKind k) => Put(a, v);
    }
}
