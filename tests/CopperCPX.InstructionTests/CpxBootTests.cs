using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using CopperFS.Cpx4.Reference;
namespace CopperCPX.InstructionTests;

public sealed class CpxBootTheoryAttribute : TheoryAttribute
{
    public CpxBootTheoryAttribute()
    {
        if(Environment.GetEnvironmentVariable("COPPERCPX_BOOT")!="1") Skip="Set COPPERCPX_BOOT=1 for the licensed-ROM 512 KiB boot gate.";
    }
}
public class CpxBootTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [CpxBootTheory][InlineData(false,false,false,false,false,false)][InlineData(true,false,false,false,false,false)][InlineData(false,true,false,false,false,false)][InlineData(false,false,true,false,false,false)][InlineData(true,false,false,true,false,false)][InlineData(false,false,true,true,false,false)][InlineData(false,false,false,false,true,false)][InlineData(false,false,false,false,true,true)] public async System.Threading.Tasks.Task ActualHunkBootsWith512KiBAndFourKiBTaskStack(bool deep,bool replay,bool format,bool owners,bool damaged,bool dma)
    {
        bool diagnostic=Environment.GetEnvironmentVariable("COPPERCPX_ASYNC_DIAGNOSTIC")=="1";
        string repo=FindRoot(),root=Path.Combine(Environment.GetEnvironmentVariable("COPPERCPX_BOOT_ROOT")??Path.Combine(repo,"artifacts",diagnostic?"cpx4-async-diagnostic":"cpx4-boot-512k"),dma?"dma-fallback":damaged?"backup-anchor":owners&&format?"unsupported-trim":owners?"owners-depth8":format?"format":replay?"recovery":deep?"depth8":"growth"),handler=Path.Combine(Environment.GetEnvironmentVariable("COPPERCPX_HANDLER_ROOT")??Path.Combine(repo,"artifacts","native-cpx4-handler"),"CopperCPX4");
        string? romPath=Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM"); Assert.True(File.Exists(romPath),"Licensed Kickstart ROM is unavailable."); Assert.True(File.Exists(handler),"Rebuild CopperCPX4 first.");
        var build=ValidateBuild(repo,handler);
        Directory.CreateDirectory(Path.Combine(root,"boot","S"));
        File.Delete(Path.Combine(root,"qualification.json"));
        var probe=AmigaM68kCompiler.Compile(new M68kCompilationRequest { AssemblyPath=typeof(CpxBootProbe).Assembly.Location,EntryPoint=typeof(CpxBootProbe).FullName+"::Entry",
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Hunk,RuntimeProfile=M68kRuntimeProfile.Application,ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[] });
        Assert.True(probe.FrameworkAnalysis.IsCompatible); Assert.Empty(probe.FrameworkAnalysis.ManagedAllocationSites);
        File.WriteAllBytes(Path.Combine(root,"boot","PROBE"),probe.Image); File.Copy(handler,Path.Combine(root,"boot","HANDLER"),true);
        File.WriteAllText(Path.Combine(root,"boot","S","startup-sequence"),"PROBE\n");
        var boot=await FileSystemAdf.CreateAsync(Path.Combine(root,"boot"),"CPX4Boot");
        var disk=new Image(); if(damaged) PrepareDamagedAnchor(disk); else if(!format) Format(disk); if(deep) MakeDeep(disk); if(replay) MakeReplay(disk); string media=Path.Combine(root,"disk.hdf"); File.WriteAllBytes(media,disk.Bytes);
        var hook=new Storage(!(format&&owners)); using var machine=new LightweightA500Machine(new() { CpuModel=M68kCpuModel.M68000,ChipRamBytes=512*1024,FastRamBytes=0,SlowRamBytes=0,
            FloppyDriveCount=1,Hardfiles=[new(0,media,readOnly:false){ExposeBootNode=false,IoHook=hook}] });
        machine.LoadKickstart(File.ReadAllBytes(romPath!)); machine.MountAdf(0,boot); machine.SetDriveWriteProtected(0,true); machine.Reset();
        var sampler=new StackSampler(machine); uint control=0,pointer=0; int octets=0; bool done=false,failed=false;
        machine.ParallelDataWritten+=(value,_,_)=>
        {
            if(octets!=0) { pointer=(pointer<<8)|value; if(--octets==0) { control=pointer; sampler.Control=control; BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(machine.ChipRam).Span.Slice((int)control+60),(deep?1u:0)|(replay?2u:0)|(diagnostic?4u:0)|(format?8u:0)|(owners?16u:0)|(damaged?32u:0)|(dma?64u:0)); } return; }
            if(value==200) { octets=4; pointer=0; }
            if(value==224) { sampler.Code=Read(machine,control+32); sampler.Startup=true; }
            if(value==225) sampler.Startup=false;
            if(value==226) sampler.Shutdown=true;
            if(value==210) done=true; if(value==250) failed=true;
        };
        // Eight-level lookups amplify every added DOS call. Give that workload
        // a separate finite execution allowance; RAM and task-stack gates are
        // identical for all fixtures and are never enlarged on timeout.
        // The expanded ExAll/Inhibit workload still obeys the same emulated
        // frame bound. Host watchdogs stopped active metadata work at 153247
        // frames (12 minutes) and 163708 frames (15 minutes, first remount).
        // Allow slower hosts to execute the remaining bounded frames. A stuck
        // handler still fails the unchanged emulated-frame/stack/memory gates.
        int frameLimit=deep?180000:120000,minutes=deep?30:8;
        if(diagnostic) { frameLimit=10000; minutes=2; }
        var watch=Stopwatch.StartNew();
        for(int frame=0;frame<frameLimit&&!done&&!failed&&watch.Elapsed<TimeSpan.FromMinutes(minutes);frame++) sampler.Frame();
        uint stage=control==0?0:Read(machine,control+20),error=control==0?0:Read(machine,control+24);
        if(!done||failed) File.WriteAllBytes(Path.Combine(root,"failed-chipram.bin"),machine.ChipRam.ToArray());
        uint extraDmaBytes=dma?Read(machine,control+1880):0,arenaBytes=CpxArena.Bytes(4096);
        uint diagnosticArena=control==0?0:Read(machine,control);
        var receipt=new { Passed=done&&!failed,AsyncDiagnosticOnly=diagnostic,PartialPacketCoverage=true,MaximumDepthFixture=deep,CommittedJournalReplay=replay,ChipRam=512*1024,FastRam=0,SlowRam=0,Cpu="MC68000",Stage=stage,DosError=error,
            Substage=control==0?0:Read(machine,control+28),DiagnosticArena=diagnosticArena,
            TimerWords=diagnosticArena==0||done&&!failed?[]:Enumerable.Range(0,52).Select(n=>Read(machine,diagnosticArena+2816+(uint)n*4)).ToArray(),
            WaitWords=diagnosticArena==0||done&&!failed?[]:Enumerable.Range(0,16).Select(n=>Read(machine,diagnosticArena+CpxArena.Scratch+6*Read(machine,diagnosticArena+12)+CpxArena.IndexBytes+14336+(uint)n*4)).ToArray(),
            PC=machine.Cpu.ProgramCounter.ToString("X8"),Frames=machine.CompletedFrames,Seconds=watch.Elapsed.TotalSeconds,FrameLimit=frameLimit,WallTimeLimitMinutes=minutes,
            HandlerBytes=new System.IO.FileInfo(handler).Length,HandlerSha256=build.HunkSha256,SourceInputsMatched=true,SourceHashes=build.Sources,
            ProbeAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CpxBootProbe).Assembly.Location))),ResidentArena=arenaBytes,ExternalDmaAllocation=extraDmaBytes,ExternalDmaBuffer=dma?Read(machine,control+1884):0,DmaFallback=dma,
            StackLower=sampler.Lower,StackUpper=sampler.Upper,StackPeak=sampler.Peak,StackGuardIntact=sampler.GuardIntact,
            StartupAndRecoveryStackPeak=sampler.StartupPeak,ShutdownStackPeak=sampler.ShutdownPeak,
            WaitCharStackPeak=sampler.UnsupportedPeak[0],TimerPacketStackPeak=sampler.UnsupportedPeak[1],DiskChangeStackPeak=sampler.UnsupportedPeak[2],
            RecordLockStackPeak=sampler.RecordPeak,NotificationStackPeak=sampler.NotifyPeak,InhibitStackPeak=sampler.InhibitPeak,MediaEventStackPeak=sampler.MediaPeak,ExAllStackPeak=sampler.ExAllPeak,ObservedStartupExecAllocations=sampler.StartupExecAllocations,ObservedOperationExecAllocations=sampler.OperationExecAllocations,FormatAndLabelStackPeak=sampler.VolumePeak,OwnerStackPeak=sampler.OwnerPeak,OwnershipWorkload=owners&&!format,BlankMediaFormat=format,DamagedPrimaryAnchor=damaged,
            ManualTrimStackPeak=sampler.ManualTrimPeak,DiscardSupported=hook.SupportsDiscard,ManualTrimRanges=control!=0&&owners?Read(machine,control+1800):0,NewReadLinkStackPeak=sampler.NewReadLinkPeak,MediaChangeEventInjected=!format&&!owners,DeviceChangeSubscriptionSupported=false,Progress=control==0?0:Read(machine,control+36),
            MinimumFreeBytes=sampler.MinimumFree,LoadedSegmentAllocation=control==0?0:Read(machine,control+40)-Read(machine,control+56),
            OsAndDeviceNetMountAllocation=control==0?0:Read(machine,control+56)-Read(machine,control+44)-arenaBytes-extraDmaBytes-4096,
            FreeBeforeLoad=control==0?0:Read(machine,control+40),FreeMounted=control==0?0:Read(machine,control+44),FreeAfterWork=control==0?0:Read(machine,control+48),FreeAfterShutdown=control==0?0:Read(machine,control+52),
            hook.Reads,hook.Writes,hook.Flushes,hook.Discards };
        string json=JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true}); output.WriteLine(json);
        Assert.True(done&&!failed,$"Boot failed at stage {stage}, DOS error {error}; see {root}.");
        Assert.True(sampler.StartupExecAllocations>0,"The Exec-allocation observer did not see startup-owned allocations.");
        Assert.Equal(0u,sampler.OperationExecAllocations);
        if(dma) { Assert.Equal(8192u,extraDmaBytes); Assert.Equal(0u,Read(machine,control+1884)&4095u); }
        Assert.Equal(4096u,sampler.Upper-sampler.Lower); Assert.InRange(sampler.Peak,1u,4096u); Assert.True(sampler.GuardIntact);
        Assert.InRange(sampler.StartupPeak,1u,sampler.Peak); Assert.InRange(sampler.ShutdownPeak,1u,sampler.Peak);
        if(!diagnostic) Assert.All(sampler.UnsupportedPeak,peak=>Assert.InRange(peak,1u,4096u));
        if(owners) Assert.InRange(sampler.ManualTrimPeak,1u,4096u);
        if(owners&&!format) { Assert.InRange(sampler.OwnerPeak,1u,4096u); Assert.InRange(sampler.NewReadLinkPeak,1u,4096u); }
        else if(damaged) Assert.InRange(sampler.MinimumFree,1u,524288u);
        else if(format) Assert.InRange(sampler.VolumePeak,1u,4096u);
        else {
        Assert.InRange(sampler.RecordPeak,1u,4096u); Assert.InRange(sampler.NotifyPeak,1u,4096u);
        Assert.InRange(sampler.InhibitPeak,1u,4096u); Assert.InRange(sampler.MediaPeak,1u,4096u);
        if(!diagnostic) Assert.InRange(sampler.ExAllPeak,1u,4096u); }
        Assert.True(hook.Reads>0&&hook.Writes>0&&hook.Flushes>=(damaged?1:7));
        uint manual=owners?Read(machine,control+1800):0;
        if(owners&&!format) Assert.True(manual>0);
        if(!damaged) Assert.Equal(hook.SupportsDiscard?1+(int)manual:0,hook.Discards);
        machine.Dispose(); // Release the host hardfile handle before independent reopening.
        if(damaged) VerifyDamagedAnchor(File.ReadAllBytes(media)); else if(owners&&!format) VerifyOwners(File.ReadAllBytes(media)); else if(format) VerifyFormat(File.ReadAllBytes(media)); else if(!diagnostic) Verify(File.ReadAllBytes(media),deep);
        Assert.Equal(build.HunkSha256,ValidateBuild(repo,handler).HunkSha256);
        File.WriteAllText(Path.Combine(root,"qualification.json"),json);
    }
    private sealed record BuildSource(string Path,string Sha256);
    [CpxBootTheory] [InlineData(false)]
    public System.Threading.Tasks.Task NativeHandlerExAllAndAsyncOperationsAvoidExecAllocation(bool unused)
    {
        Assert.False(unused);
        return ActualHunkBootsWith512KiBAndFourKiBTaskStack(false,false,false,false,false,false);
    }
    private static void PrepareDamagedAnchor(Image image)
    {
        Cpx4Volume<Image>.Format(image,4096,new byte[49152],12345);
        var v=new Cpx4Volume<Image>(image,new byte[49152]);
        v.Create("Rescued"u8,out ulong id); v.Write(id,0,"old!"u8,out _);
        BinaryPrimitives.WriteUInt32BigEndian(image.Bytes.AsSpan(65536+80),512); // plausible but unauthenticated
    }
    private static void VerifyDamagedAnchor(byte[] bytes)
    {
        Assert.Equal(512u,BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(65536+80)));
        var v=new Cpx4Volume<Image>(new Image{Bytes=bytes},new byte[49152]); v.Check();
        Assert.True(v.TryGetInfo("Rescued"u8,out var file)); byte[] data=new byte[4];
        Assert.Equal(4,v.Read(file.Id,0,data)); Assert.Equal("new!"u8.ToArray(),data);
    }
    private static void VerifyOwners(byte[] bytes)
    {
        var volume=new Cpx4Volume<Image>(new Image{Bytes=bytes},new byte[36864]); volume.Check(); Assert.True(volume.OwnershipEnabled);
        Assert.Equal(0x12340005u,volume.ReadOwner(1)); Assert.True(volume.TryGetInfo("Inline"u8,out var info)); Assert.Equal(0xfedc9876u,volume.ReadOwner(info.Id));
        byte[] comment=new byte[160]; int length=volume.ReadCommentUtf8(info.Id,comment); Assert.Equal("owned"u8.ToArray(),comment[..length]);
        const int state=65536+3072; Assert.Equal(136,BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(state+52)));
        Assert.Equal("Owned",System.Text.Encoding.UTF8.GetString(bytes,state+120,BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(state+116))));
    }
    private static void VerifyFormat(byte[] bytes)
    {
        var image=new Image { Bytes=bytes }; var volume=new Cpx4Volume<Image>(image,new byte[36864]); volume.Check();
        Assert.True(volume.TryGetInfo("Kept"u8,out var info)); byte[] data=new byte[4]; Assert.Equal(4,volume.Read(info.Id,0,data)); Assert.Equal(new byte[]{0x12,0x34,0x56,0x78},data);
        const int state=65536+3*1024; Assert.Equal(128,BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(state+52)));
        int length=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(state+116)); Assert.Equal("ÄWorkÿ",new System.Text.UTF8Encoding(false,true).GetString(bytes,state+120,length));
    }
    private sealed record BuildStamp(string HunkSha256,BuildSource[] Sources);
    private static BuildStamp ValidateBuild(string repo,string handler)
    {
        string manifest=Path.Combine(Path.GetDirectoryName(handler)!,"source-manifest.json");
        Assert.True(File.Exists(manifest),"The native source manifest is missing; rebuild CopperCPX4.");
        var stamp=JsonSerializer.Deserialize<BuildStamp>(File.ReadAllText(manifest))!;
        Assert.NotNull(stamp); Assert.NotEmpty(stamp.Sources);
        var inputs=Directory.GetFiles(Path.Combine(repo,"src","CopperCPX.Core"),"*.cs")
            .Concat(Directory.GetFiles(Path.Combine(repo,"src","CopperFS.Amiga","Cpx4"),"*.cs"))
            .Append(Path.Combine(repo,"prototypes","CopperFS.Prototype","WideMath32.cs"))
            .Select(p=>Path.GetRelativePath(repo,p)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(inputs,stamp.Sources.Select(s=>s.Path).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(stamp.HunkSha256,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(handler))));
        foreach(var source in stamp.Sources)
        {
            string path=Path.Combine(repo,source.Path); Assert.True(File.Exists(path),$"Native source missing: {source.Path}");
            Assert.True(source.Sha256==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),$"Stale native build: {source.Path}. Rebuild CopperCPX4.");
        }
        return stamp;
    }
    private static string FindRoot()
    { string? p=AppContext.BaseDirectory; while(p!=null&&!File.Exists(Path.Combine(p,"CopperFS.slnx"))) p=Path.GetDirectoryName(p); return p??throw new InvalidOperationException("Repository not found."); }
    private static uint Read(LightweightA500Machine machine,uint p)=>BinaryPrimitives.ReadUInt32BigEndian(machine.ChipRam.Span.Slice((int)p,4));
    private static void Format(Image disk)
    {
        Cpx4Volume<Image>.Format(disk,512,new byte[36864],12345); var v=new Cpx4Volume<Image>(disk,new byte[36864]);
        v.Create("Inline"u8,out _); v.Create("Payload"u8,out ulong id); byte[] data=Enumerable.Range(0,4096).Select(n=>(byte)(n*7+11)).ToArray(); v.Write(id,0,data,out _);
        v.Create("Wide"u8,out id); v.Write(id,0x100000009UL,new byte[]{0x5a},out _);
        v.Create("Extents"u8,out id);
        for(uint n=0;n<6;n++) v.Write(id,n*1024,new byte[512],out _);
        v.Truncate(id,8192);
        v.Create("Unwritten"u8,out id);
        for(uint n=0;n<40;n++) v.Preallocate(id,n*4096,3,out _);
        v.Create("EmptyTree"u8,out id);
        for(uint n=0;n<7;n++) v.Write(id,n*1024,new byte[512],out _);
        v.Truncate(id,0);
        v.Create(new byte[]{0xe9,0xc4},out id); v.SetComment(id,new byte[]{0xc0,0xff});
        v.CreateUtf8(1,"日本語"u8,false,out _);
        v.Create(System.Text.Encoding.ASCII.GetBytes(new string('q',108)),out _);
    }
    private static void Verify(byte[] bytes,bool deep)
    {
        var image=new Image { Bytes=bytes }; var volume=new Cpx4Volume<Image>(image,new byte[36864]); volume.Check();
        Assert.True(volume.TryGetInfo("Inline"u8,out var info)); Assert.Equal(deep?40UL:1064UL,info.Length);
        byte[] notifiedData=new byte[1]; Assert.Equal(1,volume.Read(info.Id,0,notifiedData)); Assert.Equal((byte)7,notifiedData[0]);
        Assert.False(volume.TryGetInfo("Created"u8,out _)); Assert.False(volume.TryGetInfo("New00"u8,out _)); Assert.False(volume.TryGetInfo("New07"u8,out _));
        Assert.True(volume.TryGetInfo("Extents"u8,out info)); Assert.Equal(8209UL,info.Length);
        byte[] data=new byte[17]; Assert.Equal(17,volume.Read(info.Id,6157,data)); Assert.All(data,b=>Assert.Equal(0x7c,b));
        Assert.True(volume.TryGetInfo("Unwritten"u8,out info)); Assert.Equal(17,volume.Read(info.Id,39*4096+525,data)); Assert.All(data,b=>Assert.Equal(0x7c,b));
        Assert.True(volume.TryGetInfo("EmptyTree"u8,out info)); Assert.Equal(17UL,info.Length); Assert.Equal(17,volume.Read(info.Id,0,data)); Assert.All(data,b=>Assert.Equal(0x7c,b));
        Assert.False(volume.TryGetInfo("EnumEmpty"u8,out _));
        Assert.False(volume.TryGetInfo("NotifyTemp"u8,out _));
        Assert.True(volume.TryGetInfo("Inline"u8,out info)); byte[] comment=new byte[79]; int length=volume.ReadCommentUtf8(info.Id,comment);
        Assert.Equal("native comment"u8.ToArray(),comment[..length]);
    }
    internal static void MakeReplay(Image image)
    {
        var disk=new CutAtCommit(image); var volume=new Cpx4Volume<CutAtCommit>(disk,new byte[36864]);
        Assert.True(volume.TryGetInfo("Inline"u8,out var info)); disk.Armed=true;
        try { volume.Write(info.Id,0,"rpl"u8,out _); Assert.Fail("Commit barrier was not cut."); } catch(SimulatedPowerLoss) { }
    }
    private sealed class SimulatedPowerLoss : IOException;
    private sealed class CutAtCommit(Image image) : IBlockDevice
    {
        public bool Armed; private int barriers; public int SectorSize=>512; public ulong SectorCount=>8192;
        public void Read(ulong s,Span<byte> b)=>image.Read(s,b); public void Write(ulong s,ReadOnlySpan<byte> b)=>image.Write(s,b);
        public void Flush() { if(Armed&&++barriers==3) throw new SimulatedPowerLoss(); }
    }
    private static void MakeDeep(Image disk)
    {
        // Offline synthetic fixture: transfer already allocated reserve pages
        // into one-child branches until both trees have eight levels.
        // No production writer is patched or used during the measured workload.
        const int p=1024,reserve=65536+8192,state=65536+3072,rootLeaf=2097152;
        var image=disk.Bytes;
        ulong L(int at)=>BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(at));
        void Put(int at,ulong value)=>BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(at),value);
        void Word(int at,ushort value)=>BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(at),value);
        void Seal(int at)
        {
            uint crc=uint.MaxValue;
            for(int n=0;n<p;n++) { crc^=n is >=8 and <12?0u:image[at+n]; for(int b=0;b<8;b++) crc=(crc>>1)^(0x82f63b78u&(0u-(crc&1))); }
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at+8),~crc);
        }
        int count=BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(reserve+64)); ulong generation=L(state+32);
        ulong Wrap(ulong child,ushort type,ulong owner,int width)
        {
            ushort original=BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(checked((int)child*512)+50));
            for(ushort level=(ushort)(original+1);level<=7;level++)
            {
                ulong next=L(reserve+72+(--count)*8); Put(reserve+72+count*8,0); int at=checked((int)next*512); image.AsSpan(at,p).Clear();
                BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at),0x43505834); Word(at+4,type); Word(at+6,4);
                Put(at+16,next); Put(at+24,owner); Put(at+32,generation); Word(at+48,1); Word(at+50,level); Word(at+52,(ushort)width);
                Put(at+64+width-8,child); Seal(at); child=next;
            }
            return child;
        }
        ulong directory=Wrap(L(rootLeaf+64+32),14,1,24); Put(rootLeaf+64+32,directory); Seal(rootLeaf);
        Put(state+80,Wrap(L(state+80),12,0,16)); Seal(state); Word(reserve+64,(ushort)count); Seal(reserve);
        var check=new Cpx4Volume<Image>(disk,new byte[36864]); check.Check();
    }
    internal sealed class Image : IBlockDevice
    {
        public byte[] Bytes=new byte[4*1024*1024]; public int SectorSize=>512; public ulong SectorCount=>8192;
        public void Read(ulong s,Span<byte> b)=>Bytes.AsSpan((int)s*512,b.Length).CopyTo(b);
        public void Write(ulong s,ReadOnlySpan<byte> b)=>b.CopyTo(Bytes.AsSpan((int)s*512)); public void Flush() { }
    }
    private sealed class Storage(bool supportsDiscard) : IAmigaHardfileDiscardIoHook
    {
        public int Reads,Writes,Flushes,Discards; public bool FlushOnDispose=>false; public bool SupportsDiscard=>supportsDiscard; public uint MaximumDiscardSectors=>4096;
        public void ObserveOpen(int unit) { } public void ObserveCommand(ushort command) { }
        public void Read(FileStream backing,long offset,Span<byte> destination) { Reads++; backing.Position=offset; backing.ReadExactly(destination); }
        public void Write(FileStream backing,long offset,ReadOnlySpan<byte> source) { Writes++; backing.Position=offset; backing.Write(source); }
        public void Flush(FileStream backing) { Flushes++; backing.Flush(true); }
        public void Discard(FileStream backing,long offset,int bytes) { Discards++; backing.Position=offset; backing.Write(new byte[bytes]); }
    }
    // Diagnostic scalar execution follows ReadCpuProfile (MIT, this repository).
    // It executes the actual HUNK and ROM, including Exec/DOS calls on the task stack.
    private sealed class StackSampler
    {
        private readonly LightweightA500Machine machine; private readonly IM68kCore core; private readonly Action<long> advance;
        private readonly Func<long,long> clamp; private readonly Func<bool> dispatch; private readonly Func<long> boundary;
        public uint Code,Control,Lower,Upper,Peak,RecordPeak,NotifyPeak,InhibitPeak,MediaPeak,ExAllPeak,VolumePeak,OwnerPeak,NewReadLinkPeak,ManualTrimPeak,StartupExecAllocations,OperationExecAllocations,MinimumFree=uint.MaxValue; private uint task; private byte[]? finalGuard;
        public readonly uint[] UnsupportedPeak=new uint[3];
        public bool Startup,Shutdown;
        public uint StartupPeak,ShutdownPeak;
        public bool GuardIntact=>Lower!=0&&(finalGuard??machine.ChipRam.Span.Slice((int)Lower,32).ToArray()).All(b=>b==0xa5);
        internal StackSampler(LightweightA500Machine machine)
        {
            this.machine=machine; const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic; var type=machine.GetType();
            core=(IM68kCore)type.GetField("_cpu",flags)!.GetValue(machine)!;
            advance=type.GetMethod("AdvanceHardwareTo",flags)!.CreateDelegate<Action<long>>(machine); clamp=type.GetMethod("ClampCpuBatchTarget",flags)!.CreateDelegate<Func<long,long>>(machine);
            dispatch=type.GetMethod("DispatchPendingCpuInterrupt",flags)!.CreateDelegate<Func<bool>>(machine); boundary=type.GetProperty("NextFrameCycle",flags)!.GetMethod!.CreateDelegate<Func<long>>(machine);
        }
        internal void Frame()
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
                    if(Code!=0)
                    {
                        uint exec=Read(machine,4),current=exec<512*1024-280?Read(machine,exec+276):0;
                        if(exec<512*1024-336)
                        {
                            uint header=Read(machine,exec+322),free=0; int guard=0;
                            while(header!=exec+326&&header!=0&&header<512*1024-32&&guard++<32) { free+=Read(machine,header+28); header=Read(machine,header); }
                            if(header==exec+326&&free<=512*1024) MinimumFree=Math.Min(MinimumFree,free);
                        }
                        if(task==0&&cpu.ProgramCounter==Code)
                        {
                            task=current; Lower=Read(machine,task+58); Upper=Read(machine,task+62);
                            Assert.Equal(4096u,Upper-Lower);
                            System.Runtime.InteropServices.MemoryMarshal.AsMemory(machine.ChipRam).Span.Slice((int)Lower,32).Fill(0xa5);
                        }
                        if(task!=0&&current==task)
                        {
                            uint phase=Read(machine,Control+36),lvo=unchecked(exec-cpu.ProgramCounter);
                            if(lvo is 198 or 204 or 222 or 654 or 666 or 684 or 696 or 708 or 870 or 894 or 936 or 942 or 972 or 984)
                            {
                                if(Startup) StartupExecAllocations++;
                                else if(phase is 170 or 300 or 500) OperationExecAllocations++;
                            }
                            uint sp=core.State.A[7];
                            if(sp<=Upper&&sp>=Lower-4096)
                            {
                                Peak=Math.Max(Peak,Upper-sp); uint progress=Read(machine,Control+36);
                                if(Startup) StartupPeak=Math.Max(StartupPeak,Upper-sp);
                                if(Shutdown) ShutdownPeak=Math.Max(ShutdownPeak,Upper-sp);
                                if(progress==170) RecordPeak=Math.Max(RecordPeak,Upper-sp);
                                if(progress==300) NotifyPeak=Math.Max(NotifyPeak,Upper-sp);
                                if(progress==400) InhibitPeak=Math.Max(InhibitPeak,Upper-sp);
                                if(progress==500) ExAllPeak=Math.Max(ExAllPeak,Upper-sp);
                                if(progress==600) MediaPeak=Math.Max(MediaPeak,Upper-sp);
                                if(progress==700) VolumePeak=Math.Max(VolumePeak,Upper-sp);
                                if(progress==800) OwnerPeak=Math.Max(OwnerPeak,Upper-sp);
                                if(progress==950) ManualTrimPeak=Math.Max(ManualTrimPeak,Upper-sp);
                                if(progress is >=960 and <=962) UnsupportedPeak[progress-960]=Math.Max(UnsupportedPeak[progress-960],Upper-sp);
                                if(progress==900) NewReadLinkPeak=Math.Max(NewReadLinkPeak,Upper-sp);
                            }
                            finalGuard??=new byte[32]; machine.ChipRam.Span.Slice((int)Lower,32).CopyTo(finalGuard);
                        }
                    }
                    long before=cpu.Cycles; core.ExecuteInstruction(); if(cpu.Cycles<=before) throw new InvalidOperationException("CPU stopped advancing."); advance(cpu.Cycles);
                }
            }
        }
    }
}
