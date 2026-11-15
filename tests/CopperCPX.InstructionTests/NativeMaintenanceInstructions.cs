using Copper68k;
using CopperFS.Cpx4.Reference;
namespace CopperCPX.InstructionTests;

public partial class NativeStorageLoopInstructions
{
    [Theory][InlineData(false)][InlineData(true)]
    public void TimerMakesProgressInTheActualLoopWithIdleOrBackloggedPort(bool traffic)
    {
        const uint timer=0x86c0, storage=0x8200, arena=0x70000;
        var disk=new CpxBootTests.Image();
        Cpx4Volume<CpxBootTests.Image>.Format(disk,512,new byte[36864],12345);
        var compiled=Compilation.Value;
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var reloc in compiled.Relocations)
        { uint p=0x10000+(uint)reloc.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000); bus.Put(0x7ff0,10);
        foreach(uint port in new uint[]{0x8000,0x8040,0x8080}) bus.Put(port+20,port+24);
        bus.Bytes[0x800f]=7; bus.Bytes[0x804f]=8; bus.Bytes[0x808f]=9;
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus);
        cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        bool armed=false,due=false,active=false,ready=false,packet=false;
        int arms=0,ticks=0,reads=0,delivered=0,replies=0,idleWaits=0,storageWaits=0;
        var discarded=new List<uint>();
        for(int n=0;n<10_000_000;n++)
        {
            if(arms==3&&(!traffic||replies==17)) break;
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            switch(lvo)
            {
                case 654: Assert.Equal(0x8080u,cpu.State.A[0]); cpu.State.D[0]=timer; break;
                case 444: cpu.State.D[0]=0; break;
                case 120: case 126: break;
                case 306: cpu.State.D[0]=0; break;
                case 372:
                    Assert.Equal(0x8000u,cpu.State.A[0]); Assert.False(active||packet);
                    if(traffic)
                    {
                        Assert.Equal(replies/8,discarded.Count);
                        packet=true; delivered++; Packet(bus,0x8300,0x8320,1027,0);
                    }
                    cpu.State.D[0]=traffic?0x8300u:0; break;
                case 366:
                    Assert.True(packet); Assert.False(active); Assert.Equal(0x8100u,cpu.State.A[0]);
                    Assert.Equal(uint.MaxValue,bus.Get(0x832c)); Assert.Equal(0u,bus.Get(0x8330));
                    packet=false; replies++; if(replies%8==0) due=true;
                    break;
                case 462 when cpu.State.A[1]==timer:
                    Assert.False(armed||active); Assert.Equal(arms,discarded.Count);
                    Assert.Equal(30u,bus.Get(timer+32)); Assert.Equal(0u,bus.Get(timer+36));
                    armed=true; arms++; break;
                case 468 when cpu.State.A[1]==timer:
                    Assert.True(armed); cpu.State.D[0]=due?timer:0; break;
                case 474 when cpu.State.A[1]==timer:
                    Assert.True(armed&&due); due=armed=false; ticks++; cpu.State.D[0]=0; break;
                case 462:
                    Assert.Equal(storage,cpu.State.A[1]); Assert.False(active||packet);
                    Assert.Equal(1u,bus.Get(arena+520)); // Dispatcher owns the engine through device completion.
                    Assert.Equal(discarded.Count+1,ticks); active=true; ready=false; break;
                case 468:
                    Assert.True(active); cpu.State.D[0]=ready?storage:0; break;
                case 318:
                    if(active) { storageWaits++; ready=true; cpu.State.D[0]=256; }
                    else
                    {
                        Assert.False(traffic); Assert.True(armed); Assert.Equal(512u,cpu.State.D[0]&512u);
                        idleWaits++; due=true; cpu.State.D[0]=512;
                    }
                    break;
                case 474:
                    Assert.Equal(storage,cpu.State.A[1]); Assert.True(active&&ready);
                    uint buffer=bus.Get(storage+40),bytes=bus.Get(storage+36);
                    if(bus.Bytes[storage+29]==24)
                    {
                        disk.Bytes.AsSpan((int)bus.Get(storage+44),(int)bytes).CopyTo(bus.Bytes.AsSpan((int)buffer));
                        reads++;
                    }
                    else
                    {
                        Assert.Equal(28,bus.Bytes[storage+29]); Assert.Equal(0x42,bus.Bytes[buffer+32]);
                        Assert.Equal(0u,bus.Get(buffer+88)); Assert.Equal(1u,bus.Get(buffer+96));
                        discarded.Add(bus.Get(buffer+92)); bus.Bytes[buffer+19]=10; bus.Put(buffer+8,24);
                    }
                    bus.Put(storage+32,bytes); cpu.State.D[0]=0; active=ready=false; break;
                default:
                    cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u);
                    Assert.False(cpu.State.Stopped||cpu.State.Halted);
                    continue;
            }
            cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
        }
        Assert.Equal(3,arms); Assert.Equal(2,ticks); Assert.Equal(2,discarded.Count);
        Assert.True(discarded[1]>discarded[0]); Assert.Equal(8,reads); // Two 1 KiB pages per tick, split at 512 bytes.
        Assert.True(storageWaits>=reads+2); Assert.Equal(traffic?0:2,idleWaits);
        Assert.Equal(traffic?17:0,replies); Assert.Equal(delivered,replies);
    }
}
