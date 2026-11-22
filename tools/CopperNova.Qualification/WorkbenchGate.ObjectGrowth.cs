using System.Security.Cryptography;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperNova.Reference;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;

internal static partial class WorkbenchGate
{
    public static async Task ObjectGrowth(string handlerPath,string romPath,string output,string? formatterPath=null,int version=6,int fileCount=0,int perDirectory=0)
    {
        if(version!=6)
            throw new ArgumentOutOfRangeException(nameof(version));
        if(fileCount<0||fileCount>0&&(perDirectory<1||perDirectory>fileCount||fileCount>0xffffff))
            throw new ArgumentOutOfRangeException(nameof(fileCount));
        int expectedFiles=fileCount==0?ObjectGrowthProbe.Files:fileCount;
        bool small=expectedFiles<1023;
        byte[] handler=File.ReadAllBytes(handlerPath),rom=File.ReadAllBytes(romPath);
        var probe=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(ObjectGrowthProbe).Assembly.Location,EntryPoint="ObjectGrowthProbe::Entry",
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Hunk,RuntimeProfile=M68kRuntimeProfile.Application,
            ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[] });
        if(!probe.FrameworkAnalysis.IsCompatible||probe.FrameworkAnalysis.ManagedAllocationSites.Count!=0)
            throw new InvalidDataException("Object-growth DOS probe failed to compile");
        string files=Path.Combine(output,"boot"),media=Path.Combine(output,"growth.rdb");
        Directory.CreateDirectory(Path.Combine(files,"S"));
        if(fileCount!=0)
        {
            var config=new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(config,fileCount);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(config.AsSpan(4),perDirectory);
            File.WriteAllBytes(Path.Combine(files,"scale"),config);
        }
        File.WriteAllBytes(Path.Combine(files,"PROBE"),probe.Image);
        File.WriteAllText(Path.Combine(files,"S","startup-sequence"),"PROBE\n");
        string cutMode=Path.Combine(files,"cut-mode");
        if(File.Exists(cutMode)) File.Delete(cutMode);
        var boot=await FileSystemAdf.CreateAsync(files,"ObjectGrowth");
        File.WriteAllText(cutMode,"");
        var cutBoot=await FileSystemAdf.CreateAsync(files,"ObjectCuts");
        uint stackBytes=formatterPath==null?4096u:600u;
        var disk=CreateImage(handler,stackBytes);
        // The first object pair ends at the 4 GiB boundary. Subsequent pairs
        // exercise wide addresses in the index as well as the TD64 transport.
        uint firstPair=checked((uint)((Td64Boundary-PartitionOffset)/512-2));
        long partitionStart=PartitionOffset;
        var formatting=new List<object>();
        byte[]? formatter=formatterPath==null?null:File.ReadAllBytes(formatterPath);
        if(formatter==null) SaveFullDisk(media,disk,small?firstPair/2:firstPair);
        else
        {
            // Position the fresh partition so ordinary allocations cross the TD64 boundary.
            long windows=(FullPartitionBytes/512+2047)/2048;
            long floor=(3442+2*((windows+2047)/2048)+2*windows+2047)&~2047L;
            partitionStart=Td64Boundary-floor*512-32768;
            MovePartition(disk,partitionStart,FullPartitionBytes);
            VerifyInstalledHandler(disk[..PartitionOffset],handler);
            using(var blank=new FileStream(media,FileMode.CreateNew,FileAccess.Write))
            {
                if(!AmigaSparseFile.TryEnable(blank)) throw new IOException("Sparse fixture unavailable");
                blank.SetLength(partitionStart+FullPartitionBytes);
                blank.Write(disk.AsSpan(0,PartitionOffset));
            }
            var initialize=await FormatBoot(output,"FormatEntry",formatter);
            var reject=await FormatBoot(output,"FormatRejectEntry",formatter);
            for(int stage=0;stage<4;stage++)
            {
                var (marker,cut,cache)=RunFreshBoot(stage==3?reject:initialize,rom,media,partitionStart,FullPartitionBytes,stage);
                if(stage<2 ? !cut||marker!=180||cache.Barriers!=stage :
                    cut||marker!=(stage==2?214:215)||cache.Barriers!=(stage==2?2:0)||stage==3&&cache.Writes!=0)
                    throw new InvalidDataException("Unexpected format publication or refusal");
                using var device=File.OpenRead(media);
                device.Position=partitionStart;
                var roots=new byte[1024]; device.ReadExactly(roots);
                if(stage<2 ? roots.AsSpan().ContainsAnyExcept((byte)0) :
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(roots.AsSpan(4))!=version||
                    NovaImage.CheckPacked(device,partitionStart,FullPartitionBytes).Entries.Count!=0)
                    throw new InvalidDataException("Unexpected durable namespace after format");
                formatting.Add(new {stage,marker,cut,cache.Barriers,cache.Writes});
            }
        }
        var boots=new List<object>();
        foreach(int stage in small&&fileCount==0?new[]{210,211,212}:new[]{210,0,1,211,212})
        {
            bool cutting=stage<2;
            int expected=cutting?180:stage;
            var cache=new VolatileDisk(partitionStart,FullPartitionBytes,cutting?stage:-1);
            using var machine=new LightweightA500Machine(new(){
                CpuModel=M68kCpuModel.M68000,ChipRamBytes=512*1024,FastRamBytes=2*1024*1024,FloppyDriveCount=1,
                Hardfiles=[new(0,media,readOnly:false,mountMode:AmigaHardfileMountMode.RigidDiskBlock){IoHook=cache}]
            });
            byte marker=0; int progress=0;
            long begin=0,end=0;
            machine.ParallelDataWritten+=(value,_,cycle)=>{
                if(value==170) Console.WriteLine($"Phase {expected}: {++progress*2048} filenames reached");
                if(value==178) begin=cycle;
                if(value is 180 or >=210 and <=212 or >=240) { marker=value; end=cycle; }
            };
            machine.LoadKickstart(rom); machine.MountAdf(0,cutting?cutBoot:boot); machine.SetDriveWriteProtected(0,true); machine.Reset();
            bool cut=false;
            try {
                for(int frame=0;frame<Math.Max(120000L,100L*expectedFiles)&&(marker==0||marker==180);frame++) machine.ExecuteFrame();
            } catch(PowerCut) when(cutting) { cut=true; }
            Console.WriteLine($"Object growth boot {expected}: marker {marker}, {cache.Writes} writes, {cache.Barriers} barriers");
            if(marker!=expected||cut!=cutting||(cutting?cache.Barriers!=stage:expected==212?cache.Writes!=0:cache.Barriers<2||cache.Barriers%2!=0))
                throw new InvalidDataException($"Object growth failed at PC {machine.Cpu.ProgramCounter:X8}");
            using var captured=new FileStream(media,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var prefix=new byte[PartitionOffset]; captured.ReadExactly(prefix);
            if(!prefix.AsSpan().SequenceEqual(disk.AsSpan(0,PartitionOffset))) throw new InvalidDataException("RDB changed");
            var check=NovaImage.CheckPacked(captured,partitionStart,FullPartitionBytes);
            if(!small&&(stage==210&&!cache.WroteAboveBoundary||stage==211&&!cache.ReadAboveBoundary))
                throw new InvalidDataException("Object table workload did not cross 4 GiB");
            int filesOnDisk=check.Entries.Count(e=>!e.Directory);
            if(filesOnDisk!=(stage==210||cutting?expectedFiles:0)||check.Entries.Any(e=>e.Path==(fileCount==0?"/C/trial":"/trial")))
                throw new InvalidDataException("Durable object table differs from the expected DOS namespace");
            boots.Add(new {marker,Cycles=begin>0?end-begin:0,CutBeforeBarrier=cutting?(int?)(stage+1):null,filesOnDisk,cache.ReadAboveBoundary,cache.WroteAboveBoundary,cache.Reads,cache.Writes,cache.Barriers,machine.CompletedFrames});
        }
        File.WriteAllText(Path.Combine(output,"qualification.json"),JsonSerializer.Serialize(new {
            HandlerSha256=Convert.ToHexString(SHA256.HashData(handler)),RomSha256=Convert.ToHexString(SHA256.HashData(rom)),
            ProbeSha256=Convert.ToHexString(SHA256.HashData(probe.Image)),Version=version,Populated=ObjectGrowthProbe.PopulateAll,Cpu="MC68000",RdbStackBytes=stackBytes,FormatterSha256=formatter==null?null:Hash(formatter),Formatting=formatting,PartitionOffset=partitionStart,PartitionBytes=FullPartitionBytes,Files=expectedFiles,FilesPerDirectory=perDirectory,Boots=boots
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}
