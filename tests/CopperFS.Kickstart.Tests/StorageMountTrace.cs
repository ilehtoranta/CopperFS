using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Copper68k;
using CopperMod.Amiga.Lightweight;

namespace CopperFS.Kickstart.Tests;

// Optional, external scalar diagnostics. Never used by acceptance measurements
// or added to the production engine's instruction/device paths.
internal sealed class StorageMountTrace : IDisposable
{
    private readonly LightweightA500Machine machine;
    private readonly IM68kCore core;
    private readonly Action<long> advance;
    private readonly Func<long,long> clamp;
    private readonly Func<bool> dispatch;
    private readonly Func<long> boundary;
    private readonly byte[] rom;
    private readonly StreamWriter log;
    private readonly List<(uint Task,uint Return,uint Request,string Kind)> calls=[];
    private int steps;
    private uint caller;
    internal void Control(uint pointer)=>log.WriteLine($"CONTROL {pointer:X8} {Convert.ToHexString(Memory(pointer,288))}");

    internal StorageMountTrace(LightweightA500Machine machine,byte[] rom,string path)
    {
        this.machine=machine; this.rom=rom; log=new(path) { AutoFlush=true };
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(LightweightA500Machine);
        core=(IM68kCore)type.GetField("_cpu",flags)!.GetValue(machine)!;
        advance=type.GetMethod("AdvanceHardwareTo",flags)!.CreateDelegate<Action<long>>(machine);
        clamp=type.GetMethod("ClampCpuBatchTarget",flags)!.CreateDelegate<Func<long,long>>(machine);
        dispatch=type.GetMethod("DispatchPendingCpuInterrupt",flags)!.CreateDelegate<Func<bool>>(machine);
        boundary=type.GetProperty("NextFrameCycle",flags)!.GetMethod!.CreateDelegate<Func<long>>(machine);
    }

    private ReadOnlySpan<byte> Memory(uint address,int length)
    {
        if(address<machine.ChipRam.Length && length<=machine.ChipRam.Length-address)
            return machine.ChipRam.Span.Slice((int)address,length);
        if(machine.FastRamBase is uint first && address>=first && address-first<=machine.FastRam.Length && length<=machine.FastRam.Length-(address-first))
            return machine.FastRam.Span.Slice((int)(address-first),length);
        if(address>=0xF80000 && address-0xF80000<=rom.Length && length<=rom.Length-(address-0xF80000))
            return rom.AsSpan((int)(address-0xF80000),length);
        return [];
    }
    private uint Long(uint address)=>Memory(address,4) is var bytes && bytes.Length==4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : 0;
    private string Text(uint address)
    {
        var text=new StringBuilder();
        for(var i=0u;i<128;i++)
        {
            var bytes=Memory(address+i,1); if(bytes.Length==0 || bytes[0]==0) break;
            text.Append(bytes[0] is >=32 and <127 ? (char)bytes[0] : '?');
        }
        return text.ToString();
    }
    private string Request(uint pointer)
    {
        var bytes=Memory(pointer,48);
        if(bytes.Length!=48) return "<invalid>";
        var device=Long(pointer+20); var command=BinaryPrimitives.ReadUInt16BigEndian(bytes[28..]);
        var result=$"IO={pointer:X8} cmd={command} device={device:X8}:{Text(Long(device+10))} error={unchecked((sbyte)bytes[31])} actual={Long(pointer+32)} length={Long(pointer+36)} data={Long(pointer+40):X8} offset={Long(pointer+44):X8}";
        if(command==22) result+=" geometry="+Convert.ToHexString(Memory(Long(pointer+40),32));
        return result;
    }
    private void Observe()
    {
        var cpu=machine.Cpu; var pc=cpu.ProgramCounter; var exec=Long(4);
        if(exec<600) return;
        var task=Long(exec+276);
        for(var i=calls.Count-1;i>=0;i--)
        {
            var call=calls[i];
            if(task!=call.Task || pc!=call.Return) continue;
            log.WriteLine($"RETURN task={task:X8} pc={pc:X8} {call.Kind} D0={cpu.D[0]:X8} {Request(call.Request)}");
            if(call.Kind is "OpenDevice" or "DoIO" && Long(call.Request+20) is var dev && Text(Long(dev+10))=="scsi.device" && pc<0xF80000)
            { caller=pc; steps=2000; }
            calls.RemoveAt(i);
        }
        if(pc==exec-444 || pc==exec-456 || pc==exec-462 || pc==exec-450)
        {
            var kind=pc==exec-444 ? "OpenDevice" : pc==exec-450 ? "CloseDevice" : pc==exec-456 ? "DoIO" : "SendIO";
            if(kind=="OpenDevice" && Text(cpu.A[0])=="scsi.device" && pc<0xF80000)
                log.WriteLine($"GLOBALS A4={cpu.A[4]:X8} {Convert.ToHexString(Memory(cpu.A[4]+0x7200,256))}");
            log.WriteLine($"CALL cycle={machine.Cycle} task={task:X8} pc={pc:X8} return={Long(cpu.A[7]):X8} {kind} name={Text(cpu.A[0])} D0={cpu.D[0]:X8} D1={cpu.D[1]:X8} {Request(cpu.A[1])}");
            if(kind!="SendIO") calls.Add((task,Long(cpu.A[7]),cpu.A[1],kind));
        }
        if(steps>0 && pc<0xF80000 && pc>caller-0x10000 && pc<caller+0x10000)
        {
            steps--;
            log.WriteLine($"STEP {pc:X8} {Convert.ToHexString(Memory(pc,8))} SR={cpu.StatusRegister:X4} D={string.Join(',',cpu.D.Select(v=>v.ToString("X8")))} A={string.Join(',',cpu.A.Select(v=>v.ToString("X8")))}");
        }
    }
    internal void ExecuteFrame()
    {
        var target=machine.CompletedFrames+1; var cpu=machine.Cpu;
        while(machine.CompletedFrames<target)
        {
            var end=boundary();
            if(cpu.Halted) { advance(end); cpu.Cycles=Math.Max(cpu.Cycles,end); continue; }
            if(cpu.Stopped)
            {
                var wake=((cpu.StatusRegister>>8)&7)>=6 ? end : clamp(end);
                advance(wake); cpu.Cycles=Math.Max(cpu.Cycles,wake); dispatch(); continue;
            }
            if(dispatch()) continue;
            while(cpu.Cycles<end)
            {
                if(dispatch()) continue;
                if(cpu.Halted || cpu.Stopped) break;
                Observe(); var previous=cpu.Cycles; core.ExecuteInstruction();
                if(cpu.Cycles<=previous) throw new InvalidOperationException("CPU clock did not advance.");
                advance(cpu.Cycles);
            }
        }
    }
    public void Dispose()=>log.Dispose();
}
