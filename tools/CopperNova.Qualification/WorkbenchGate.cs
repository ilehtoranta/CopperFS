using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperNova.Reference;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using FastFileSystemVolume=Hst.Amiga.FileSystems.FastFileSystem.FastFileSystemVolume;
using AmigaFileMode = Hst.Amiga.FileSystems.FileMode;

internal static partial class WorkbenchGate
{
    private const uint DosType=0x434e5032;
    private const int PartitionOffset=4*32768;
    private const long Td64Boundary=1L<<32;
    private const long FullPartitionBytes=Td64Boundary+64*1024*1024;

    private static async Task<(byte[] Handler,byte[] Original,byte[] Boot,byte[] ProbeDisk,byte[] Probe)> PrepareBoot(
        string handlerPath,string workbenchPath,string output,bool fullDisk=false,bool retirement=false,
        bool synchronizedStart=false,string? frozenProbe=null)
    {
        Directory.CreateDirectory(output);
        byte[] handler=File.ReadAllBytes(handlerPath), original;
        if (Path.GetExtension(workbenchPath).Equals(".zip",StringComparison.OrdinalIgnoreCase)) {
            using var zip=ZipFile.OpenRead(workbenchPath);
            using var source=zip.Entries.Single(e=>e.Name.EndsWith(".adf",StringComparison.OrdinalIgnoreCase)).Open();
            using var copy=new MemoryStream(); await source.CopyToAsync(copy); original=copy.ToArray();
        } else original=File.ReadAllBytes(workbenchPath);
        byte[] probe;
        if(frozenProbe!=null) probe=File.ReadAllBytes(frozenProbe);
        else
        {
            var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
                AssemblyPath=typeof(WorkbenchProbe).Assembly.Location,
                EntryPoint=synchronizedStart?"WorkbenchProbe::SyncedDiskEntry":
                    retirement?"WorkbenchProbe::FullDiskRetireEntry":fullDisk?"WorkbenchProbe::FullDiskEntry":"WorkbenchProbe::Entry",
                Cpu=M68kCpuTarget.M68000,
                OutputFormat=M68kOutputFormat.Hunk,RuntimeProfile=M68kRuntimeProfile.Application,
                ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[] });
            if(!compiled.FrameworkAnalysis.IsCompatible||compiled.FrameworkAnalysis.ManagedAllocationSites.Count!=0)
                throw new InvalidDataException("Probe compilation failed");
            probe=compiled.Image;
        }
        string extras=Path.Combine(output,"probe-files"); Directory.CreateDirectory(extras);
        File.WriteAllBytes(Path.Combine(extras,"PROBE"),probe);
        var probeDisk=await FileSystemAdf.CreateAsync(extras,"NovaProbe");
        byte[] boot=(byte[])original.Clone();
        using(var stream=new MemoryStream(boot)) {
            var volume=await FastFileSystemVolume.MountAdf(stream); await volume.ChangeDirectory("S");
            string startup;
            await using(var file=await volume.OpenFile("startup-sequence",AmigaFileMode.Read)) {
                using var reader=new StreamReader(file,Encoding.Latin1); startup=await reader.ReadToEndAsync();
            }
            var lines=startup.Replace("\r","").Split('\n').ToList();
            // Workbench 2.04 normally prompts for a keymap on first use.
            // The disposable boot copy runs the DOS probe unattended.
            lines.RemoveAll(line=>line.Trim().Equals("PickMap sys: initial",StringComparison.OrdinalIgnoreCase));
            int load=lines.FindIndex(l=>l.Trim().Equals("C:LoadWB",StringComparison.OrdinalIgnoreCase)||l.Trim().Equals("LoadWB",StringComparison.OrdinalIgnoreCase));
            if(load<0) throw new InvalidDataException("Workbench startup has no LoadWB");
            lines.Insert(load+1,"DF1:PROBE");
            await using(var file=await volume.OpenFile("startup-sequence",AmigaFileMode.Write,overwrite:true,ignoreProtectionBits:true))
                await file.WriteAsync(Encoding.Latin1.GetBytes(string.Join('\n',lines)));
            await volume.Flush();
        }
        return (handler, original, boot, probeDisk, probe);
    }

    public static async Task Prepare(string handlerPath,string workbenchPath,string output)
    {
        if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Choose an empty directory for disposable fixture images.");
        var (handler, _, boot, probeDisk, _) = await PrepareBoot(handlerPath,workbenchPath,output);
        File.WriteAllBytes(Path.Combine(output,"boot.adf"),boot);
        File.WriteAllBytes(Path.Combine(output,"probe.adf"),probeDisk);
        File.WriteAllBytes(Path.Combine(output,"blank.rdb"),CreateImage(handler));
    }

    public static async Task Run(string handlerPath,string romPath,string workbenchPath,string output,
        bool wide=false,bool fullDisk=false,bool benchmark=false,bool retirement=false,string? preparedMedia=null,
        string? comparison=null)
    {
        if(comparison!=null)
        {
            if(comparison is not ("v2-low" or "v2-high" or "v5-mid" or "v5-high"))
                throw new ArgumentException("Expected v2-low, v2-high, v5-mid or v5-high.",nameof(comparison));
            fullDisk=comparison is "v5-mid" or "v5-high";
            wide=comparison=="v2-high";
        }
        int triggerFrame=int.Parse(Environment.GetEnvironmentVariable("NOVA_WORKBENCH_TRIGGER_FRAME")??"2200");
        bool streamWorkload=fullDisk||comparison!=null;
        bool synchronizedStart=comparison!=null&&Environment.GetEnvironmentVariable("NOVA_WORKBENCH_SYNC")=="1";
        int releaseOffset=synchronizedStart?int.Parse(Environment.GetEnvironmentVariable("NOVA_WORKBENCH_RELEASE_OFFSET")??"0"):0;
        if(releaseOffset is <0 or >120000) throw new ArgumentOutOfRangeException(nameof(releaseOffset));
        string? frozenProbe=comparison!=null&&!synchronizedStart?Environment.GetEnvironmentVariable("NOVA_WORKBENCH_PROBE"):null;
        var (handler,original,boot,probeDisk,probe)=await PrepareBoot(handlerPath,workbenchPath,output,streamWorkload,retirement,synchronizedStart,frozenProbe);
        byte[] rom=File.ReadAllBytes(romPath);
        string media=Path.Combine(output,"workbench.rdb");
        // Put both roots below 4 GiB and the payload region above it. Keep
        // the volume small so this test isolates transport from allocation.
        long partitionStart=wide ? Td64Boundary-2*32768 : PartitionOffset;
        var image=CreateImage(handler);
        long partitionBytes=fullDisk?FullPartitionBytes:image.Length-PartitionOffset;
        if(preparedMedia!=null)
        {
            using var blank=File.OpenRead(preparedMedia);
            Verify(blank,expectCommitted:false,fullDisk:true);
            File.Copy(preparedMedia,media);
        }
        else if(fullDisk)
        {
            // Put the first file run just below 4 GiB. The second run
            // crosses the next 65,536-sector allocation group.
            long boundary=comparison is "v5-mid"?Td64Boundary/2:Td64Boundary;
            uint hint=checked((uint)((boundary-partitionStart)/512-2));
            SaveFullDisk(media,image,hint);
        }
        else
        {
            if(wide) MovePartition(image,partitionStart,partitionBytes);
            SaveImage(media,image,partitionStart);
        }
        var cpu=benchmark?M68kCpuModel.M68020:M68kCpuModel.M68000;
        var results=new List<object>();
        uint publishedGeneration=0;
        bool crossesBoundary=(wide||fullDisk)&&preparedMedia==null&&comparison is not ("v5-mid");
        byte[] phases=retirement?[210,1,0,213,211]:crossesBoundary&&!benchmark?(fullDisk?[1,0,210,211]:[0,210,211]):[210,211];
        foreach(byte expected in phases) {
            int cutBeforeBarrier=expected switch { 1=>0, 0=>1, _=>-1 };
            bool cuttingPower=cutBeforeBarrier>=0;
            var cache=new VolatileDisk(partitionStart,partitionBytes,cutBeforeBarrier);
            byte marker=0; bool cut=false; long frames; uint pc;
            long mutationStart=0,readStart=0,readEnd=0,flushStart=0,acknowledged=0,releaseCycle=0;
            bool armed=false;
            int mutationLine=0,mutationClock=0;
            bool profiling=comparison!=null&&expected==210&&Environment.GetEnvironmentVariable("NOVA_WORKBENCH_PROFILE")=="1";
            WorkbenchProfile? profile=null;
            using(var machine=new LightweightA500Machine(new() {
                CpuModel=cpu,ChipRamBytes=512*1024,FastRamBytes=2*1024*1024,FloppyDriveCount=2,
                Hardfiles=[new(0,media,readOnly:false,mountMode:AmigaHardfileMountMode.RigidDiskBlock){IoHook=cache}] }))
            {
                machine.ParallelDataWritten+=(value,_,cycle)=>
                {
                    if(value==179) armed=true;
                    if(value==181)
                    {
                        mutationStart=cycle;
                        mutationLine=machine.BeamLine;
                        mutationClock=machine.BeamColorClock;
                    }
                    if(value==182) readStart=cycle;
                    if(value==183) flushStart=cycle;
                    if(value==210) acknowledged=cycle;
                    if(value==211) readEnd=cycle;
                    if(value is 180 or 181 or 210 or 211 or 213 or >=240) marker=value;
                    profile?.Marker(value);
                };
                machine.LoadKickstart(rom); machine.MountAdf(0,boot); machine.MountAdf(1,probeDisk);
                machine.SetDriveWriteProtected(0,true); machine.SetDriveWriteProtected(1,true); machine.Reset();
                try
                {
                    for(int frame=0;frame<18000&&marker is not (210 or 211 or 213 or >=240);frame++)
                    {
                        if(synchronizedStart&&expected==210&&frame==triggerFrame)
                        {
                            if(!armed||mutationStart!=0) throw new InvalidDataException("The workload did not wait for its timing trigger.");
                            void Trigger()
                            {
                                releaseCycle=machine.Cycle;
                                machine.SubmitInput(new LightweightInputState(0,8,0,0,0,0));
                            }
                            if(releaseOffset==0) Trigger();
                            else
                            {
                                var scalar=profile??new WorkbenchProfile(machine,handler,probe,handlerPath+".map");
                                scalar.Frame(machine.Cycle+releaseOffset,Trigger);
                                continue;
                            }
                        }
                        if(!profiling||frame<1900) machine.ExecuteFrame();
                        else
                        {
                            profile??=new WorkbenchProfile(machine,handler,probe,handlerPath+".map");
                            profile.Frame();
                        }
                    }
                }
                catch(PowerCut) when(cuttingPower) { cut=true; }
                frames=machine.CompletedFrames;
                pc=machine.Cpu.ProgramCounter;
            }
            if(profiling&&profile==null) throw new InvalidDataException("The timed phase ended before tracing was enabled.");
            profile?.Save(Path.Combine(output,"instruction-profile.json"));
            Console.WriteLine($"Workbench boot {expected}: marker {marker}, frames {frames}, TD64 reads {cache.Td64Reads}, TD64 writes {cache.Td64Writes}, barriers {cache.Barriers}, cut {cut}");
            bool barriersOk=expected switch
            {
                210 when retirement => cache.Barriers>=4&&(cache.Barriers&1)==0,
                210 or 213 => cache.Barriers==2,
                _ => true
            };
            if(cuttingPower ? !cut||marker!=181||cache.Barriers!=cutBeforeBarrier :
                marker!=expected||!barriersOk)
                throw new InvalidDataException($"Workbench session failed at PC {pc:X8}, with {cache.Reads} backing reads and {cache.Writes} backing writes");
            if(crossesBoundary&&expected==210&&(cache.Td64Writes==0||!cache.WroteBelowBoundary||!cache.WroteAboveBoundary))
                throw new InvalidDataException("The workload did not issue TD64 writes on both sides of 4 GiB");
            if(expected==211&&(cache.Writes!=0||crossesBoundary&&(cache.Td64Reads==0||!cache.ReadAboveBoundary)))
                throw new InvalidDataException("The cold boot did not read back through TD64");
            results.Add(new {marker,CompletedFrames=frames,cache.Reads,cache.Writes,cache.Barriers,
                CutBeforeBarrier=cuttingPower?(int?)(cutBeforeBarrier+1):null,
                CutBeforePublicationBarrier=cut&&cutBeforeBarrier==1,
                ReleaseCycle=releaseCycle,MutationStart=mutationStart,MutationLine=mutationLine,MutationColorClock=mutationClock,
                FlushStart=flushStart,Acknowledged=acknowledged,
                MutationCycles=flushStart>0&&mutationStart>0?flushStart-mutationStart:0,
                FlushCycles=acknowledged>0&&flushStart>0?acknowledged-flushStart:0,
                MutationThroughFlushCycles=acknowledged>0&&mutationStart>0?acknowledged-mutationStart:0,
                ColdReadbackCycles=readEnd>0&&readStart>0?readEnd-readStart:0,
                cache.FirstPayloadGroup,cache.LastPayloadGroup,
                cache.Td64Reads,cache.Td64Writes,cache.WroteBelowBoundary,cache.WroteAboveBoundary});
            if(fullDisk&&crossesBoundary&&expected==210&&cache.FirstPayloadGroup>=cache.LastPayloadGroup)
                throw new InvalidDataException("The workload did not span multiple allocation groups");
            // Dropping the device cache leaves only the completed barriers
            // in the backing file seen by the next machine.
            using var captured=File.OpenRead(media);
            uint generation=Verify(captured,expectCommitted:retirement||!cut,fullDisk:fullDisk,
                retirement:retirement,retired:retirement&&(expected is 213 or 211),streamWorkload:streamWorkload);
            if(retirement)
            {
                if(expected==210) publishedGeneration=generation;
                else if(generation!=publishedGeneration+(expected is 213 or 211?1u:0u))
                    throw new InvalidDataException("Unexpected wide retirement generation");
            }
        }
        File.WriteAllText(Path.Combine(output,"workbench.json"),JsonSerializer.Serialize(new {
            HandlerSha256=Hash(handler),RomSha256=Hash(rom),WorkbenchSha256=Hash(original),ProbeSha256=Hash(probe),
            Cpu=cpu.ToString(),PartitionOffset=partitionStart,PartitionBytes=partitionBytes,PhysicalHardware=false,
            Comparison=comparison,SynchronizedStart=synchronizedStart,ReleaseOffset=releaseOffset,
            TestedAcross4GiB=crossesBoundary,Boots=results
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    public static uint Verify(Stream stream,bool expectCommitted=true,bool fullDisk=false,
        bool retirement=false,bool retired=false,bool streamWorkload=false)
    {
        var layout=AmigaRdbSetup.ReadLayout(stream);
        var partition=layout.Partitions.Single(p=>p.DeviceName=="CNP0"&&p.DosType==DosType);
        ulong cylinder=(ulong)layout.Heads*layout.SectorsPerTrack*512;
        ulong length=checked(cylinder*((ulong)partition.LastCylinder-partition.FirstCylinder+1));
        if(fullDisk ? length!=FullPartitionBytes : length is <(2*1024*1024) or >(32*1024*1024))
            throw new InvalidDataException("Unexpected CNP2 partition length");
        var check=NovaImage.CheckPacked(stream,checked((long)(cylinder*partition.FirstCylinder)),(long)length);
        if(!expectCommitted)
        {
            if(check.Entries.Count!=0||check.Generation!=0)
                throw new InvalidDataException("Unpublished metadata became visible after the power cut");
            return check.Generation;
        }
        var file=check.Entries.SingleOrDefault(e=>e.Path=="/Dst/Keep");
        streamWorkload|=fullDisk;
        int payloadLength=retirement?WorkbenchProbe.RetirementPayload:streamWorkload?WorkbenchProbe.FullDiskPayload:512;
        if(streamWorkload)
        {
            var gap=check.Entries.SingleOrDefault(e=>e.Path=="/Dst/Gap");
            if(gap?.Size!=512||gap.Sha256!=PatternHash(512)||(!retirement&&check.Generation!=1))
                throw new InvalidDataException("Invalid full-disk overflow or generation");
        }
        if(retired)
        {
            if(file!=null||check.Entries.Count!=2||check.DataSectors!=1)
                throw new InvalidDataException("Large retirement left a file or allocated sectors");
            return check.Generation;
        }
        if(check.Entries.Count!=(streamWorkload?3:2)||file==null||file.Slot!=(streamWorkload?3:2)||
            file.Size!=payloadLength||file.Sha256!=PatternHash(payloadLength))
            throw new InvalidDataException($"Cold media differs from the acknowledged namespace: {file}; {check.Entries.Count} entries");
        return check.Generation;
    }

    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static string PatternHash(int bytes)
    {
        byte[] chunk=new byte[Math.Min(65536,bytes)];
        for(int i=0;i<chunk.Length;i++) chunk[i]=(byte)(i^0x6d);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(int offset=0;offset<bytes;offset+=chunk.Length)
            hash.AppendData(chunk,0,Math.Min(chunk.Length,bytes-offset));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] CreateImage(byte[] handler,uint stackBytes=4096)
    {
        byte[] partition=NovaImage.FormatPacked(16*1024*1024),disk=new byte[PartitionOffset+partition.Length];
        using var stream=new MemoryStream(disk);
        AmigaRdbSetup.Initialize(stream);
        // Larger HUNKs need longer LSEG records on the emulated controller,
        // which supports 128 links. DOS still uses 512-byte filesystem sectors.
        int rdbBytes=handler.Length>128*492?1024:512, payload=rdbBytes-20;
        void Put(int block,int at,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(block*rdbBytes+at),value);
        int reserved=PartitionOffset/rdbBytes, segments=(handler.Length+payload-1)/payload;
        if(segments+3>reserved) throw new InvalidDataException("Handler exceeds the fixture RDB reservation");
        Put(0,132,(uint)(reserved-1)); Put(0,136,4); Seal(disk,0);
        AmigaRdbSetup.AddPartition(stream,"CNP0",4,(uint)(disk.Length/32768-1),DosType,false);
        if(rdbBytes!=512)
        {
            int part=(int)BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(28));
            disk.AsSpan(part*512,512).CopyTo(disk.AsSpan(part*rdbBytes));
            disk.AsSpan(part*512,512).Clear();
            Put(0,16,(uint)rdbBytes);
        }
        Put(0,32,2); Put(0,152,(uint)(2+segments)); Seal(disk,0);
        Put(2,0,0x46534844); Put(2,4,64); Put(2,16,uint.MaxValue); Put(2,32,DosType);
        Put(2,36,0x10000); Put(2,40,0x190); Put(2,60,stackBytes); Put(2,72,3); Put(2,76,uint.MaxValue); Seal(disk,2,rdbBytes);
        for(int i=0;i<segments;i++) {
            int block=3+i,bytes=Math.Min(payload,handler.Length-i*payload);
            Put(block,0,0x4c534547); Put(block,4,(uint)(5+(bytes+3)/4)); Put(block,16,i+1==segments?uint.MaxValue:(uint)(block+1));
            handler.AsSpan(i*payload,bytes).CopyTo(disk.AsSpan(block*rdbBytes+20)); Seal(disk,block,rdbBytes);
        }
        partition.CopyTo(disk,PartitionOffset); return disk;
    }

    private static void Seal(byte[] disk,int block,int rdbBytes=512)
    {
        var page=disk.AsSpan(block*rdbBytes,rdbBytes);
        int words=(int)BinaryPrimitives.ReadUInt32BigEndian(page[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(page[8..],0);
        uint sum=0;
        for(int i=0;i<words;i++) sum=unchecked(sum+BinaryPrimitives.ReadUInt32BigEndian(page[(i*4)..]));
        BinaryPrimitives.WriteUInt32BigEndian(page[8..],unchecked(0u-sum));
    }

    private static void MovePartition(byte[] disk,long start,long bytes)
    {
        uint first=checked((uint)(start/32768));
        uint last=checked(first+(uint)(bytes/32768)-1);
        int rdbBytes=(int)BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(16));
        int partition=checked((int)BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(28)));
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(64),last+1);
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(140),last);
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(partition*rdbBytes+164),first);
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(partition*rdbBytes+168),last);
        Seal(disk,0);
        Seal(disk,partition,rdbBytes);
    }

    private static void SaveImage(string media,byte[] image,long start)
    {
        if(start==PartitionOffset) { File.WriteAllBytes(media,image); return; }
        using var stream=new FileStream(media,FileMode.Create,FileAccess.ReadWrite,FileShare.Read);
        if(!AmigaSparseFile.TryEnable(stream)) throw new IOException("Wide qualification requires sparse host storage.");
        stream.SetLength(start+image.Length-PartitionOffset);
        stream.Write(image.AsSpan(0,PartitionOffset));
        stream.Position=start;
        stream.Write(image.AsSpan(PartitionOffset));
    }

    public static void PrepareWideMedia(string handlerPath,string media)
    {
        SaveFullDisk(media,CreateImage(File.ReadAllBytes(handlerPath)),0);
        using var blank=File.OpenRead(media);
        Verify(blank,expectCommitted:false,fullDisk:true);
    }

    private static void SaveFullDisk(string media,byte[] image,uint hint)
    {
        MovePartition(image,PartitionOffset,FullPartitionBytes);
        using var disk=new FileStream(media,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        if(!AmigaSparseFile.TryEnable(disk)) throw new IOException("Full-disk qualification requires sparse host storage.");
        disk.SetLength(PartitionOffset+FullPartitionBytes);
        disk.Write(image.AsSpan(0,PartitionOffset));
        NovaImage.FormatPackedWide(disk,PartitionOffset,FullPartitionBytes,hint);
    }

    private sealed class PowerCut : Exception;

    private sealed class VolatileDisk(long start,long volumeBytes,int cutBeforeBarrier):IAmigaHardfileIoHook
    {
        private const int SectorBytes=512;
        private readonly SortedDictionary<long,byte[]> pending=new();
        public int Reads,Writes,Barriers;
        public int Td64Reads,Td64Writes;
        public bool WroteBelowBoundary,WroteAboveBoundary,ReadAboveBoundary;
        public long PayloadStart=long.MaxValue;
        public bool WrotePayloadBelowBoundary,WrotePayloadAboveBoundary;
        public long FirstPayloadGroup=long.MaxValue,LastPayloadGroup=-1;
        public bool FlushOnDispose=>false;
        public void ObserveOpen(int unit) {}
        public void ObserveCommand(ushort command)
        {
            if(command==24) Td64Reads++;
            if(command==25) Td64Writes++;
        }
        private void CheckRange(long offset,int bytes)
        {
            if((offset&(SectorBytes-1))!=0||(bytes&(SectorBytes-1))!=0)
                throw new IOException("Unaligned I/O in the RDB fixture");
            if(offset>=0&&offset<=PartitionOffset-bytes) return;
            if(offset<start||offset-start>volumeBytes-bytes)
                throw new IOException("I/O outside the RDB fixture");
        }
        public void Read(FileStream backing,long offset,Span<byte> bytes)
        {
            CheckRange(offset,bytes.Length);
            Reads++; ReadAboveBoundary|=offset+bytes.Length>Td64Boundary;
            backing.Position=offset;
            backing.ReadExactly(bytes);
            for(int i=0;i<bytes.Length;i+=SectorBytes)
                if(pending.TryGetValue(offset+i,out var sector))
                    sector.AsSpan().CopyTo(bytes.Slice(i,SectorBytes));
        }
        public void Write(FileStream backing,long offset,ReadOnlySpan<byte> bytes)
        {
            CheckRange(offset,bytes.Length);
            if(offset<start) throw new IOException("Unexpected write outside the CNP2 partition");
            Writes++; WroteBelowBoundary|=offset<Td64Boundary; WroteAboveBoundary|=offset+bytes.Length>Td64Boundary;
            if(offset>=PayloadStart)
            {
                WrotePayloadBelowBoundary|=offset<Td64Boundary;
                WrotePayloadAboveBoundary|=offset+bytes.Length>Td64Boundary;
            }
            if(offset>=Td64Boundary-1024)
            {
                FirstPayloadGroup=Math.Min(FirstPayloadGroup,(offset-start)>>25);
                LastPayloadGroup=Math.Max(LastPayloadGroup,(offset-start+bytes.Length-1)>>25);
            }
            for(int i=0;i<bytes.Length;i+=SectorBytes)
                pending[offset+i]=bytes.Slice(i,SectorBytes).ToArray();
        }
        public void Flush(FileStream backing)
        {
            if(Barriers==cutBeforeBarrier) throw new PowerCut();
            foreach(var (offset,sector) in pending)
            {
                backing.Position=offset;
                backing.Write(sector);
            }
            backing.Flush(true);
            pending.Clear();
            Barriers++;
        }
    }
}
