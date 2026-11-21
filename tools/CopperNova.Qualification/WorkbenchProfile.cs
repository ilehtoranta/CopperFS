using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Copper68k;
using CopperMod.Amiga.Lightweight;

// Profile the work between the existing guest markers. Normal benchmark runs
// use ExecuteFrame throughout. This diagnostic switches to single instructions
// early enough to include the very first timed instruction.
internal sealed class WorkbenchProfile
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private readonly LightweightA500Machine machine;
    private readonly IM68kCore core;
    private readonly Action<long> advance, synchronize;
    private readonly Func<long,long> clamp;
    private readonly Func<long> frameEnd;
    private readonly Func<bool> interrupt;
    private readonly byte[] ram;
    private readonly uint ramBase;
    private readonly (uint Start,uint Length,string Name)[] symbols;
    private readonly List<(uint Start,uint Length,string Name)> images=[];
    private readonly Dictionary<(byte Phase,uint Task,uint Pc), (long Cycles,long Instructions)> samples=[];
    private readonly byte[] handler,probe;
    private readonly Dictionary<uint,string> taskNames=[];
    private byte phase;

    public WorkbenchProfile(LightweightA500Machine machine,byte[] handler,byte[] probe,string mapPath)
    {
        this.machine=machine;
        this.handler=handler;
        this.probe=probe;
        Type type=machine.GetType();
        core=(IM68kCore)type.GetField("_cpu",Private)!.GetValue(machine)!;
        advance=type.GetMethod("AdvanceHardwareTo",Private)!.CreateDelegate<Action<long>>(machine);
        synchronize=type.GetMethod("SynchronizeCpuToBoardCycle",Private)!.CreateDelegate<Action<long>>(machine);
        clamp=type.GetMethod("ClampCpuBatchTarget",Private)!.CreateDelegate<Func<long,long>>(machine);
        interrupt=type.GetMethod("DispatchPendingCpuInterrupt",Private)!.CreateDelegate<Func<bool>>(machine);
        frameEnd=type.GetProperty("NextFrameCycle",Private)!.GetMethod!.CreateDelegate<Func<long>>(machine);
        object fast=type.GetField("_fastRam",Private)!.GetValue(machine)!;
        ram=(byte[])fast.GetType().GetProperty("Memory",Private)!.GetValue(fast)!;
        ramBase=(uint)fast.GetType().GetProperty("ConfiguredBase")!.GetValue(fast)!;
        var row=new Regex(@"^([0-9A-F]{8}) +([0-9]+) (\S+::\S+)$");
        symbols=File.ReadLines(mapPath).Select(line=>row.Match(line)).Where(match=>match.Success)
            .Select(match=>(Convert.ToUInt32(match.Groups[1].Value,16),
                uint.Parse(match.Groups[2].Value),match.Groups[3].Value)).ToArray();
        if(symbols.Length==0) throw new InvalidDataException("Native method map is empty.");
    }

    public void Marker(byte value)
    {
        if(value==181)
        {
            FindImage(handler,"Handler");
            FindImage(probe,"Probe");
            phase=1;
        }
        else if(value==183) phase=2;
        else if(value==210) phase=0;
    }

    private void FindImage(byte[] hunk,string name)
    {
        // The compiler emits a single code HUNK with the standard empty
        // resident-name list. Try several signatures to skip relocated words.
        if(Word(hunk,0)!=0x3f3||Word(hunk,4)!=0||Word(hunk,8)!=1||Word(hunk,24)!=0x3e9)
            throw new InvalidDataException("Unexpected profiling HUNK layout.");
        int length=checked((int)Word(hunk,28)*4);
        FindInRam(machine.ChipRam.Span,0);
        FindInRam(ram,ramBase);
        if(!images.Any(image=>image.Name==name))
            throw new InvalidDataException($"Could not locate the loaded {name} HUNK in guest RAM.");

        void FindInRam(ReadOnlySpan<byte> memory,uint basis)
        {
            var bytes=hunk.AsSpan(32,length);
            for(int sample=8;sample<200;sample+=8)
            {
                int next=0;
                while(next<memory.Length)
                {
                    int found=memory[next..].IndexOf(bytes.Slice(sample,24));
                    if(found<0) break;
                    int start=next+found-sample;
                    next+=found+1;
                    if(start<0||start>memory.Length-length) continue;
                    int matches=0;
                    for(int check=16;check<256;check+=16)
                        if(memory.Slice(start+check,12).SequenceEqual(bytes.Slice(check,12))) matches++;
                    if(matches>=5&&!images.Any(image=>image.Start==basis+(uint)start))
                        images.Add((basis+(uint)start,(uint)length,name));
                }
            }
        }
    }

    public void Frame(long releaseAt=0,Action? release=null)
    {
        long target=machine.CompletedFrames+1;
        var state=core.State;
        while(machine.CompletedFrames<target)
        {
            if(release!=null&&machine.Cycle>=releaseAt)
            {
                release();
                release=null;
            }
            long end=frameEnd(),before=machine.Cycle;
            byte section=phase;
            if(state.Halted||state.Stopped)
            {
                long wake=state.Halted||((state.StatusRegister>>8)&7)>=6?end:clamp(end);
                advance(wake);
                synchronize(wake);
                Record(section,0,uint.MaxValue,machine.Cycle-before,0);
                if(state.Stopped) interrupt();
                continue;
            }
            uint task=section==0?0:Task(),pc=state.ProgramCounter;
            if(interrupt())
            {
                Record(section,task,uint.MaxValue-1,machine.Cycle-before,0);
                continue;
            }
            long cpuBefore=state.Cycles;
            core.ExecuteInstruction();
            if(state.Cycles<=cpuBefore) throw new InvalidOperationException("CPU did not advance.");
            advance(state.Cycles);
            Record(section,task,pc,machine.Cycle-before,1);
        }
        if(release!=null) throw new InvalidOperationException("The timing trigger was outside the selected frame.");
    }

    private uint Task()
    {
        uint exec=GuestLong(4),task=GuestLong(exec+276);
        if(task!=0&&!taskNames.ContainsKey(task))
        {
            uint name=GuestLong(task+10);
            Span<byte> text=stackalloc byte[64];
            int length=0;
            while(length<text.Length)
            {
                byte b=GuestByte(name+(uint)length);
                if(b==0) break;
                text[length++]=b;
            }
            taskNames.Add(task,length==0?$"${task:X8}":Encoding.Latin1.GetString(text[..length]));
        }
        return task;
    }

    private void Record(byte section,uint task,uint pc,long cycles,long instructions)
    {
        if(section==0||cycles<=0) return;
        var key=(section,task,pc);
        var previous=samples.GetValueOrDefault(key);
        samples[key]=(previous.Cycles+cycles,previous.Instructions+instructions);
    }

    public void Save(string path)
    {
        var rows=samples.Select(sample=>
        {
            var (section,task,pc)=sample.Key;
            var (region,method)=Resolve(pc);
            return new { Phase=section==1?"Mutation":"Flush",Region=region,
                Task=taskNames.GetValueOrDefault(task,""),Method=method,Pc=$"{pc:X8}",
                sample.Value.Cycles,sample.Value.Instructions };
        }).ToArray();
        File.WriteAllText(path,JsonSerializer.Serialize(new
        {
            HandlerBases=images.Where(image=>image.Name=="Handler").Select(image=>$"{image.Start:X8}"),
            ProbeBases=images.Where(image=>image.Name=="Probe").Select(image=>$"{image.Start:X8}"),
            Regions=rows.GroupBy(row=>new {row.Phase,row.Region,row.Task})
                .Select(group=>new {group.Key, Cycles=group.Sum(row=>row.Cycles),Instructions=group.Sum(row=>row.Instructions)})
                .OrderByDescending(row=>row.Cycles),
            Methods=rows.GroupBy(row=>new {row.Phase,row.Region,row.Method})
                .Select(group=>new {group.Key, Cycles=group.Sum(row=>row.Cycles),Instructions=group.Sum(row=>row.Instructions)})
                .OrderByDescending(row=>row.Cycles),
            Instructions=rows.OrderByDescending(row=>row.Cycles)
        },new JsonSerializerOptions {WriteIndented=true}));
    }

    private (string Region,string Method) Resolve(uint pc)
    {
        if(pc==uint.MaxValue) return ("CPU stopped","");
        if(pc==uint.MaxValue-1) return ("Interrupt entry","");
        foreach(var image in images)
        {
            if(pc<image.Start||pc-image.Start>=image.Length) continue;
            if(image.Name=="Probe") return ("Probe","");
            uint relative=pc-image.Start;
            foreach(var symbol in symbols)
                if(relative>=symbol.Start&&relative-symbol.Start<symbol.Length)
                    return ("Handler",symbol.Name);
            return ("Handler","Unmapped");
        }
        if(pc is >=0xf80000 and <=0xffffff) return ("Kickstart","");
        if(pc<machine.ChipRam.Length||pc>=ramBase&&pc-ramBase<ram.Length) return ("Guest RAM","");
        return ("Expansion","");
    }

    private byte GuestByte(uint at)
    {
        if(at<machine.ChipRam.Length) return machine.ChipRam.Span[(int)at];
        if(at>=ramBase&&at-ramBase<ram.Length) return ram[(int)(at-ramBase)];
        return 0;
    }

    private uint GuestLong(uint at)=>
        (uint)GuestByte(at)<<24|(uint)GuestByte(at+1)<<16|(uint)GuestByte(at+2)<<8|GuestByte(at+3);

    private static uint Word(byte[] data,int at)=>BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at,4));
}
