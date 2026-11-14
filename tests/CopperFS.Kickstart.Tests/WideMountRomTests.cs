using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Amiga;
using Copper68k;
using CopperDisk;
using CopperFS.Cpx4.Reference;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using CopperStart.Dos;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed class WideMountTheoryAttribute : TheoryAttribute
{
    public WideMountTheoryAttribute()
    {
        foreach (var key in new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERFS_WIDE_FFS_HANDLER", "COPPERFS_WIDE_SFS_HANDLER", "COPPERFS_WIDE_SFS2_HANDLER", "COPPERFS_WIDE_CPX_HANDLER" })
            if (!File.Exists(Environment.GetEnvironmentVariable(key))) Skip = $"Supply {key} for wide partition ROM tests.";
    }
}

public sealed class WideMountRomTests
{
    private const uint LowCylinder = 0x7fffff;
    private const long Start = (long)LowCylinder * 512;
    private static readonly Lazy<byte[]> Probe = new(() =>
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath = typeof(WideMountProbe).Assembly.Location, EntryPoint = typeof(WideMountProbe).FullName + "::Entry",
            Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
            RuntimeProfile = M68kRuntimeProfile.Application, ExceptionMode = M68kExceptionMode.Full, IncludedExportNames = [] });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        return compiled.Image;
    });

    [WideMountTheory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    public async System.Threading.Tasks.Task CrossingPartitionMountRequiresTd64(int format, bool reject)
    {
        var handler = Environment.GetEnvironmentVariable(format < 2 ? "COPPERFS_WIDE_FFS_HANDLER" : format == 2 ? "COPPERFS_WIDE_SFS_HANDLER" : format == 3 ? "COPPERFS_WIDE_SFS2_HANDLER" : "COPPERFS_WIDE_CPX_HANDLER")!;
        byte[] volume = format switch {
            0 => DosDiskImage.Create(false, includeLargeFile:false, blockCount:32768, payload:[1,2,3], volumeName:"Wide").Bytes,
            1 => DosDiskImage.Create(true, includeLargeFile:false, blockCount:32768, payload:[1,2,3], volumeName:"Wide").Bytes,
            2 => ReadMedia.Sfs(), 3 => ReadMedia.Sfs2(), _ => CreateCpx() };
        Assert.Equal(16 * 1024 * 1024, volume.Length);
        var root = Directory.CreateTempSubdirectory("CopperFS-WideMount-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"boot","S"));
            File.WriteAllBytes(Path.Combine(root,"boot","PROBE"),Probe.Value);
            File.Copy(handler,Path.Combine(root,"boot","HANDLER"));
            File.WriteAllText(Path.Combine(root,"boot","S","startup-sequence"),"PROBE\n");
            var boot = await FileSystemAdf.CreateAsync(Path.Combine(root,"boot"),"WideMount");
            var media=Path.Combine(root,"disk.hdf");
            using(var file=new FileStream(media,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read))
            {
                Assert.True(AmigaSparseFile.TryEnable(file),"Sparse host storage is required.");
                file.SetLength(Start+volume.Length); file.Position=Start; file.Write(volume);
            }
            for(int pass=0;pass<(reject?1:2);pass++)
            {
            var hook=new DeviceTrace();
            using var machine=new LightweightA500Machine(new() { CpuModel=M68kCpuModel.M68000, ChipRamBytes=512*1024,
                FastRamBytes=2*1024*1024, SlowRamBytes=0, FloppyDriveCount=1,
                Hardfiles=[new(0,media,readOnly:false) { ExposeBootNode=false, IoHook=hook }] });
            machine.LoadKickstart(File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")!));
            machine.MountAdf(0,boot); machine.SetDriveWriteProtected(0,true); machine.Reset();
            uint control=0,pointer=0,pending=0; int octets=0,injected=0; bool done=false,failed=false;
            machine.ParallelDataWritten+=(value,_,_) => {
                if(octets!=0) { pointer=(pointer<<8)|value; if(--octets==0) { control=pointer; Write32(machine,control+60,(uint)format); Write32(machine,control+56,(uint)pass); } return; }
                if(value==200) { octets=4; pointer=0; } else if(value==210) done=true; else if(value==250) failed=true;
            };
            hook.Command = command => { if(command==24 && reject) pending=machine.Cpu.A[1]; };
            var scalar=new ScalarMachine(machine,() => { if(pending!=0) { Write8(machine,pending+31,253); Write32(machine,pending+32,0); pending=0; injected++; } });
            var watch=Stopwatch.StartNew();
            for(int frame=0;frame<40000&&!done&&!failed&&watch.Elapsed<TimeSpan.FromMinutes(5);frame++) scalar.Frame();
            Assert.True(done&&!failed,$"format={format} reject={reject}, stage={(control==0?0:Read32(machine,control+20))}, pc={machine.Cpu.ProgramCounter:X8}");
            Assert.Equal(reject?0u:1u,Read32(machine,control+20));
            Assert.True(hook.Commands.Contains(24),"Mount did not issue TD_READ64.");
            if(reject) { Assert.Equal(1,injected); Assert.Equal(0,hook.Writes); Assert.DoesNotContain((ushort)25,hook.Commands); Assert.DoesNotContain((ushort)2,hook.Commands); Assert.Equal(1,hook.Commands.Count(c=>c==24)); }
            else
            {
                Assert.Contains(hook.ReadOffsets, offset => offset >= 0x100000000L);
                if(pass==0) { Assert.True(hook.Writes>0,"The successful mount did not persist its test file."); Assert.Contains(hook.WriteOffsets, offset => offset >= 0x100000000L); }
                Assert.All(hook.WriteOffsets, offset => Assert.InRange(offset, Start, Start + volume.Length - 1));
            }
            }
        }
        finally { Directory.Delete(root,true); }
    }
    private static byte[] CreateCpx()
    {
        var image=new CpxImage(); Cpx4Volume<CpxImage>.Format(image,512,new byte[36864],12345); return image.Bytes;
    }
    private sealed class CpxImage : IBlockDevice
    {
        public byte[] Bytes=new byte[16*1024*1024]; public int SectorSize=>512; public ulong SectorCount=>32768;
        public void Read(ulong sector,Span<byte> bytes)=>Bytes.AsSpan(checked((int)sector*512),bytes.Length).CopyTo(bytes);
        public void Write(ulong sector,ReadOnlySpan<byte> bytes)=>bytes.CopyTo(Bytes.AsSpan(checked((int)sector*512)));
        public void Flush() { }
    }
    private sealed class DeviceTrace : IAmigaHardfileIoHook
    {
        public Action<ushort>? Command; public readonly List<ushort> Commands=[]; public readonly List<long> ReadOffsets=[],WriteOffsets=[]; public int Writes;
        public bool FlushOnDispose=>false;
        public void ObserveCommand(ushort command) { Commands.Add(command); Command?.Invoke(command); }
        public void Read(FileStream backing,long offset,Span<byte> bytes) { ReadOffsets.Add(offset); backing.Position=offset; backing.ReadExactly(bytes); }
        public void Write(FileStream backing,long offset,ReadOnlySpan<byte> bytes) { Writes++; WriteOffsets.Add(offset); backing.Position=offset; backing.Write(bytes); }
        public void Flush(FileStream backing)=>backing.Flush(true);
    }
    private static Span<byte> Memory(LightweightA500Machine m,uint address,int length)
    {
        if(address<(uint)m.ChipRam.Length) return MemoryMarshal.AsMemory(m.ChipRam).Span.Slice((int)address,length);
        return MemoryMarshal.AsMemory(m.FastRam).Span.Slice(checked((int)(address-m.FastRamBase!.Value)),length);
    }
    private static uint Read32(LightweightA500Machine m,uint p)=>BinaryPrimitives.ReadUInt32BigEndian(Memory(m,p,4));
    private static void Write32(LightweightA500Machine m,uint p,uint v)=>BinaryPrimitives.WriteUInt32BigEndian(Memory(m,p,4),v);
    private static void Write8(LightweightA500Machine m,uint p,byte v)=>Memory(m,p,1)[0]=v;

    // Scalar execution lets the test replace the completed TD64 request with
    // IOERR_NOCMD before the guest receives the synchronous device reply.
    private sealed class ScalarMachine
    {
        private readonly LightweightA500Machine machine; private readonly IM68kCore core; private readonly Action<long> advance;
        private readonly Func<long,long> clamp; private readonly Func<bool> dispatch; private readonly Func<long> boundary; private readonly Action after;
        public ScalarMachine(LightweightA500Machine m,Action afterInstruction)
        {
            machine=m; after=afterInstruction; const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic; var t=m.GetType();
            core=(IM68kCore)t.GetField("_cpu",flags)!.GetValue(m)!;
            advance=t.GetMethod("AdvanceHardwareTo",flags)!.CreateDelegate<Action<long>>(m);
            clamp=t.GetMethod("ClampCpuBatchTarget",flags)!.CreateDelegate<Func<long,long>>(m);
            dispatch=t.GetMethod("DispatchPendingCpuInterrupt",flags)!.CreateDelegate<Func<bool>>(m);
            boundary=t.GetProperty("NextFrameCycle",flags)!.GetMethod!.CreateDelegate<Func<long>>(m);
        }
        public void Frame()
        {
            long target=machine.CompletedFrames+1; var cpu=machine.Cpu;
            while(machine.CompletedFrames<target)
            {
                long end=boundary();
                if(cpu.Halted) { advance(end); cpu.Cycles=Math.Max(cpu.Cycles,end); continue; }
                if(cpu.Stopped) { long wake=((cpu.StatusRegister>>8)&7)>=6?end:clamp(end); advance(wake); cpu.Cycles=Math.Max(cpu.Cycles,wake); dispatch(); continue; }
                if(dispatch()) continue;
                while(cpu.Cycles<end)
                {
                    if(dispatch()) continue; if(cpu.Halted||cpu.Stopped) break;
                    long before=cpu.Cycles; core.ExecuteInstruction(); after();
                    if(cpu.Cycles<=before) throw new InvalidOperationException("CPU stopped advancing.");
                    advance(cpu.Cycles);
                }
            }
        }
    }
}

public static class WideMountProbe
{
    public static int Entry()
    {
        var dos=Exec.OpenLibrary("dos.library",37); if(!dos.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase=dos.Value; var process=Exec.FindTask(CString.FromPointer(0)); APTR.WriteUInt32(process,184,uint.MaxValue);
        var control=APTR.FromPointer(Exec.AllocMem(1024,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear));
        if(control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw>>24)); CorrectnessProbe.Report((int)(control.Raw>>16));
        CorrectnessProbe.Report((int)(control.Raw>>8)); CorrectnessProbe.Report((int)control.Raw);
        uint format=APTR.ReadUInt32(control,60);
        if(CorrectnessProbe.Mount(control,format)!=0) return CorrectnessProbe.Failed(control,3);
        var env=APTR.FromPointer(control.Raw+128); APTR.WriteUInt32(env,12,1); APTR.WriteUInt32(env,20,1);
        APTR.WriteUInt32(env,36,0x7fffff); APTR.WriteUInt32(env,40,0x807ffe);
        if(format==4) APTR.WriteUInt32(env,64,0x43505834);
        var port=DOS.DeviceProc("SEQ0:"); APTR.WriteUInt32(control,20,port.IsNull?0u:1u);
        APTR.WriteUInt32(control,24,(uint)DOS.IoErr());
        if(port.IsNotNull)
        {
            var data=APTR.FromPointer(control.Raw+512);
            bool reopen=APTR.ReadUInt32(control,56)!=0;
            var file=DOS.Open("SEQ0:WideTest",reopen?DOS.FileMode.OldFile:DOS.FileMode.NewFile);
            if(!file.HasValue) return CorrectnessProbe.Failed(control,4);
            if(reopen)
            {
                if(DOS.Read(file.Value,data,4)!=4 || APTR.ReadUInt32(data,0)!=0x12345678) return CorrectnessProbe.Failed(control,5);
            }
            else
            {
                APTR.WriteUInt32(data,0,0x12345678);
                if(DOS.Write(file.Value,data,4)!=4) return CorrectnessProbe.Failed(control,6);
            }
            if(DOS.Close(file.Value)==0) return CorrectnessProbe.Failed(control,7);
            if(!reopen && DOS.DoPkt(port,27,0,0,0,0,0)==0) return CorrectnessProbe.Failed(control,8);
        }
        CorrectnessProbe.Report(210); return 0;
    }
}
