using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Copper68k;
using CopperCPX.Reference.Tests;
using CopperDisk;
using CopperFS.Cpx4.Reference;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

public partial class CpxBootTests
{
    private enum CutEdge { BeforeFlush, AfterFlush, AfterSector }
    private sealed record Cut(CutEdge Edge,int Number);
    private sealed class PowerOff : Exception;

    // The running device sees every write. A new device receives only Durable.
    // No exception or disposal path flushes this cache into the next boot.
    private sealed class JournalDisk(byte[] initial,Cut? cut=null) : IAmigaHardfileIoHook
    {
        internal readonly byte[] Durable=(byte[])initial.Clone();
        private readonly byte[] cache=(byte[])initial.Clone();
        internal readonly List<List<long>> Batches=[];
        private readonly List<long> pending=[];
        internal bool Armed,Replied,Tripped,WriteReplied;
        internal int Sectors;
        public bool FlushOnDispose=>false;
        public void Read(FileStream backing,long offset,Span<byte> destination)=>
            cache.AsSpan(checked((int)offset),destination.Length).CopyTo(destination);

        private void Boundary(CutEdge edge,int number)
        {
            if(Armed&&cut?.Edge==edge&&cut.Number==number)
            { Tripped=true; throw new PowerOff(); }
        }

        public void Write(FileStream backing,long offset,ReadOnlySpan<byte> source)
        {
            Assert.Equal(0,offset%512); Assert.Equal(0,source.Length%512);
            for(int i=0;i<source.Length;i+=512)
            {
                int at=checked((int)offset+i);
                source.Slice(i,512).CopyTo(cache.AsSpan(at,512));
                pending.Add(at);
                if(!Armed) continue;
                Sectors++;
                if(cut?.Edge==CutEdge.AfterSector)
                {
                    // Device may destage a sector even without a successful flush.
                    cache.AsSpan(at,512).CopyTo(Durable.AsSpan(at,512));
                    Boundary(CutEdge.AfterSector,Sectors);
                }
            }
        }

        public void Flush(FileStream backing)
        {
            int number=Batches.Count+1;
            Boundary(CutEdge.BeforeFlush,number);
            foreach(long at in pending)
                cache.AsSpan((int)at,512).CopyTo(Durable.AsSpan((int)at,512));
            if(Armed) Batches.Add([..pending]);
            pending.Clear();
            Boundary(CutEdge.AfterFlush,number);
        }
    }

    [CpxBootTheory][InlineData(false)][InlineData(true)]
    public Task StandaloneHunkSurvivesJournalPowerCuts(bool interruptReplay)=>RunJournal(interruptReplay,0);

    [CpxBootTheory][InlineData(42)][InlineData(43)][InlineData(1)]
    public Task StandaloneHunkRecoversBoundary(int boundary)=>RunJournal(false,boundary);

    [CpxBootTheory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public Task StandaloneHunkRecoversMetadata(int operation)=>RunJournal(false,0,operation);

    private async Task RunJournal(bool interruptReplay,int boundary,int operation=0)
    {
        string repo=FindRoot();
        string handler=Path.Combine(Environment.GetEnvironmentVariable("COPPERCPX_HANDLER_ROOT")??
            Path.Combine(repo,"artifacts","native-cpx4-handler"),"CopperCPX4");
        var build=ValidateBuild(repo,handler);
        string root=Path.Combine(Environment.GetEnvironmentVariable("COPPERCPX_BOOT_ROOT")??
            Path.Combine(repo,"artifacts","cpx4-boot-512k"),operation!=0?$"crash-metadata-{operation}":boundary!=0?$"crash-boundary-{boundary}":interruptReplay?"crash-replay":"crash-publication");
        string boot=Path.Combine(root,"boot");
        Directory.CreateDirectory(Path.Combine(boot,"S"));
        string receipt=Path.Combine(root,"qualification.json");
        File.Delete(receipt);
        var probe=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(CpxBootProbe).Assembly.Location,EntryPoint=typeof(CpxBootProbe).FullName+"::Entry",
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Hunk,RuntimeProfile=M68kRuntimeProfile.Application,
            ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[] });
        Assert.True(probe.FrameworkAnalysis.IsCompatible); Assert.Empty(probe.FrameworkAnalysis.ManagedAllocationSites);
        File.Copy(handler,Path.Combine(boot,"HANDLER"),true);
        File.WriteAllBytes(Path.Combine(boot,"PROBE"),probe.Image);
        File.WriteAllText(Path.Combine(boot,"S","startup-sequence"),"PROBE\n");
        byte[] adf=await FileSystemAdf.CreateAsync(boot,"CPX4Crash");
        byte[] rom=File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")!);

        var image=new Image();
        Cpx4Volume<Image>.Format(image,512,new byte[36864],12345);
        byte[] original=Enumerable.Range(0,512).Select(n=>(byte)(5*n+11)).ToArray();
        byte[] appended=[..original,..Enumerable.Range(0,512).Select(n=>(byte)(7*n+3))];
        byte[] neighbor=Enumerable.Range(0,512).Select(n=>(byte)(13*n+31)).ToArray();
        var volume=new Cpx4Volume<Image>(image,new byte[36864]);
        volume.Create("target"u8,out ulong target); volume.Write(target,0,original,out _);
        volume.Create("neighbor"u8,out ulong other); volume.Write(other,0,neighbor,out _);
        if(operation>=3)
        {
            volume.Write(target,1024,original,out _);
            volume.Write(target,2048,original,out _);
            original=[..original,..new byte[512],..original,..new byte[512],..original];
        }
        string media=Path.Combine(root,"disk.hdf");
        File.WriteAllBytes(media,image.Bytes); // Supplies geometry; all guest I/O is intercepted.

        void Check(byte[] durable,bool published)
        {
            var decoded=new IndependentImage(durable);
            if(operation!=0)
            {
                Assert.Equal(other,decoded.Names[(1,"neighbor")]);
                Assert.Equal(neighbor,decoded.Read(other));
                bool deleted=published&&operation==3, linked=published&&operation==2;
                Assert.Equal(deleted?1:linked?3:2,decoded.Names.Count);
                Assert.Equal(!deleted,decoded.Objects.ContainsKey(target));
                if(!deleted)
                {
                    Assert.Equal(target,decoded.Names[(1,"target")]);
                    Assert.Equal(published&&operation==4?original[..100]:original,decoded.Read(target));
                    Assert.Equal(published&&operation==1?0x12345678u:0u,decoded.OwnerInfo[target]);
                }
                if(linked)
                {
                    byte[] obj=decoded.Objects[decoded.Names[(1,"soft")]];
                    Assert.Equal(3,obj[84]); Assert.Equal(3,obj[85]); Assert.Equal(180UL,IndependentImage.L(obj,8));
                    int offset=checked((int)IndependentImage.L(obj,96)*512);
                    Assert.Equal(new string('a',180),System.Text.Encoding.ASCII.GetString(durable,offset+68,180));
                }
                return;
            }
            Assert.Equal(2,decoded.Names.Count); Assert.Equal(target,decoded.Names[(1,"target")]);
            Assert.Equal(other,decoded.Names[(1,"neighbor")]); Assert.Equal(neighbor,decoded.Read(other));
            Assert.Equal(published?appended:original,decoded.Read(target));
            Assert.Equal(published?2UL:1UL,IndependentImage.L(decoded.Objects[target],16));
        }
        Check(image.Bytes,false);

        JournalDisk Run(byte[] start,bool recover,bool published,Cut? cut=null)
        {
            var disk=new JournalDisk(start,cut){Armed=recover};
            using var machine=new LightweightA500Machine(new() { CpuModel=M68kCpuModel.M68000,
                ChipRamBytes=524288,FastRamBytes=0,SlowRamBytes=0,FloppyDriveCount=1,
                Hardfiles=[new(0,media,readOnly:false){ExposeBootNode=false,IoHook=disk}] });
            machine.LoadKickstart(rom); machine.MountAdf(0,adf); machine.SetDriveWriteProtected(0,true); machine.Reset();
            uint control=0; int remaining=0; bool done=false,mounted=false;
            machine.ParallelDataWritten+=(value,_,_)=>
            {
                if(remaining!=0)
                {
                    control=(control<<8)|value;
                    if(--remaining==0)
                        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(machine.ChipRam)
                            .Span.Slice((int)control+60),((uint)operation<<12)|256u|(boundary==1?2048u:0u)|(recover?512u:0u)|(published?1024u:0u));
                    return;
                }
                if(value==200) remaining=4;
                if(value==225) { mounted=true; if(boundary>1) done=true; }
                if(value==219&&boundary==1) { disk.Tripped=true; throw new PowerOff(); }
                if(value==217) { Assert.False(recover); disk.Armed=true; }
                if(value==218) { Assert.False(recover); if(operation==0) Assert.Empty(disk.Batches); disk.WriteReplied=true; }
                if(value==216) { Assert.False(recover); if(operation==0) Assert.Equal(7,disk.Batches.Count); disk.Replied=true; }
                if(value==210) done=true;
                if(value==250) Assert.Fail($"Crash guest failed at {Read(machine,control+20)}, DOS error {Read(machine,control+24)}, cut {cut}.");
            };
            try
            {
                for(int frame=0;frame<14000&&!done;frame++) machine.ExecuteFrame();
            }
            catch(PowerOff) when(disk.Tripped)
            {
                if(boundary!=1) Assert.NotNull(cut); Assert.False(disk.Replied);
                Assert.Equal(!recover,mounted);
                return disk;
            }
            Assert.Null(cut); Assert.True(done&&mounted,"Guest did not complete its DOS verification.");
            Assert.Equal(!recover,disk.Replied); Assert.Equal(!recover,disk.WriteReplied); return disk;
        }

        if(boundary!=0)
        {
            var (fixture,targets)=NativeBoundaryCrashTests.Fixture(boundary);
            var before=new IndependentImage(fixture.Disk);
            var crashImages=new List<byte[]>();
            if(boundary==1)
            {
                // Refill invalidates its journal slot before discovering the full disk.
                crashImages.Add(Run(fixture.Disk,false,false,new(CutEdge.BeforeFlush,1)).Durable);
                crashImages.Add(Run(fixture.Disk,false,false,new(CutEdge.AfterFlush,1)).Durable);
                crashImages.Add(Run(fixture.Disk,false,false).Durable); // DOS replied ERROR_DISK_FULL.
            }
            else
            {
                byte[] disk=NativeBoundaryCrashTests.Run(fixture.Disk,(uint)boundary,targets,boundary==42?3:0);
                crashImages.Add(disk);
                if(boundary==42) crashImages.Add(Run(disk,true,false,new(CutEdge.AfterSector,83)).Durable);
            }
            ulong generation=IndependentImage.L(fixture.Disk,66560+80)+(boundary==42?1UL:0UL);
            foreach(byte[] interrupted in crashImages)
            {
                var recovered=Run(interrupted,true,false);
                var after=new IndependentImage(recovered.Durable);
                if(boundary!=1) NativeBoundaryCrashTests.VerifyLeaves(fixture.Disk,recovered.Durable,targets,boundary==42);
                Assert.Equal(before.Names.Count,after.Names.Count);
                foreach(var (key,id) in before.Names){Assert.Equal(id,after.Names[key]);Assert.Equal(before.Read(id),after.Read(id));}
                Assert.Equal(generation,IndependentImage.L(recovered.Durable,66560+80));
                Assert.Equal(generation,IndependentImage.L(recovered.Durable,67584+80));
            }
            File.WriteAllText(receipt,JsonSerializer.Serialize(new{build.HunkSha256,boundary,generation,files=before.Names.Count,cuts=crashImages.Count}));
            return;
        }

        if(operation!=0)
        {
            foreach(int barrier in new[]{2,3,6})
            {
                bool published=barrier>=3;
                byte[] stopped=Run(image.Bytes,false,false,new(CutEdge.AfterFlush,barrier)).Durable;
                Check(Run(stopped,true,published).Durable,published);
            }
            File.WriteAllText(receipt,JsonSerializer.Serialize(new{build.HunkSha256,operation,barriers=new[]{2,3,6}}));
            return;
        }

        var clean=Run(image.Bytes,false,false);
        Check(clean.Durable,true);
        Assert.Equal(7,clean.Batches.Count);
        // Bind the numeric edges to actual on-media destinations and page types.
        long commit=Assert.Single(clean.Batches[0],at=>at%1024==0&&at<2097152);
        Assert.Contains(commit,clean.Batches[0]); Assert.Contains(commit+512,clean.Batches[0]);
        Assert.Equal(new[]{commit,commit+512},clean.Batches[2]);
        Assert.Equal(10,IndependentImage.S(clean.Durable,checked((int)commit+4)));
        Assert.Contains(clean.Batches[0],at=>at>=2097152&&clean.Durable.AsSpan((int)at,512).SequenceEqual(appended.AsSpan(512)));
        Assert.Equal(new long[]{66560,67072},clean.Batches[4]);
        Assert.Equal(new long[]{67584,68096},clean.Batches[5]);
        Assert.Empty(clean.Batches[6]); // Explicit ACTION_FLUSH also drains the device.
        var cuts=new List<Cut>();
        byte[] initial=image.Bytes;
        var trace=clean;
        if(interruptReplay)
        {
            // A real cut after commit persistence, before the first home write.
            initial=Run(initial,false,false,new(CutEdge.AfterFlush,3)).Durable;
            trace=Run(initial,true,true);
            Check(trace.Durable,true); Assert.Equal(3,trace.Batches.Count);
            for(int n=0;n<3;n++) Assert.Equal(clean.Batches[n+3],trace.Batches[n]);
            for(int sector=1;sector<=trace.Sectors;sector++) cuts.Add(new(CutEdge.AfterSector,sector));
        }
        else
        {
            // Commit record is a two-sector page. A controller can make the
            // complete page durable before the writer even submits flush #3.
            cuts.Add(new(CutEdge.AfterSector,clean.Batches.Take(3).Sum(batch=>batch.Count)));
        }
        for(int barrier=1;barrier<=trace.Batches.Count;barrier++)
        {
            cuts.Add(new(CutEdge.BeforeFlush,barrier));
            cuts.Add(new(CutEdge.AfterFlush,barrier));
        }
        var cases=new List<object>();
        foreach(var cut in cuts)
        {
            bool published=interruptReplay||cut.Edge==CutEdge.AfterSector||
                cut.Number>3||cut.Number==3&&cut.Edge==CutEdge.AfterFlush;
            var stopped=Run(initial,interruptReplay,interruptReplay,cut);
            var restarted=Run(stopped.Durable,true,published);
            Check(restarted.Durable,published);
            // Compare both complete checkpoint pages, including their CRCs.
            // A decoder that tolerates one torn copy could otherwise hide it.
            byte[] expected=published?clean.Durable:image.Bytes;
            Assert.True(restarted.Durable.AsSpan(66560,2048).SequenceEqual(expected.AsSpan(66560,2048)));
            cases.Add(new {edge=cut.Edge.ToString(),cut.Number,published,sha256=Convert.ToHexString(SHA256.HashData(restarted.Durable))});
        }
        Assert.Equal(build.HunkSha256,ValidateBuild(repo,handler).HunkSha256);
        var json=JsonSerializer.Serialize(new {build.HunkSha256,guestSha256=Convert.ToHexString(SHA256.HashData(probe.Image)),
            cpu="MC68000",chipRam=524288,stackBytes=4096,sectorBytes=512,interruptReplay,
            sectorsPerBarrier=trace.Batches.Select(b=>b.Count),cases},new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(receipt,json); output.WriteLine(json);
    }
}
