using System.Buffers.Binary;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;

internal static partial class WorkbenchGate
{
    public static async Task VolumeLabel(string handlerPath,string formatterPath,string romPath,string output)
    {
        Directory.CreateDirectory(output);
        var handler=File.ReadAllBytes(handlerPath); var rom=File.ReadAllBytes(romPath);
        var disk=CreateImage(handler,600);
        VerifyInstalledHandler(disk[..PartitionOffset],handler);
        Array.Clear(disk,PartitionOffset,disk.Length-PartitionOffset);
        string media=Path.Combine(output,"label.rdb");
        File.WriteAllBytes(media,disk);
        var results=new List<object>();
        foreach(var (entry,marker,expected) in new[]{("LabelFormatEntry",210,"Work Disk"),("LabelRenameEntry",211,"Kesä"),("CommentWriteEntry",213,"Kesä"),("LabelColdEntry",212,"Kesä")})
        {
            var boot=await FormatBoot(output,entry,entry=="LabelFormatEntry"?File.ReadAllBytes(formatterPath):null);
            for(int stage=entry=="LabelColdEntry"?2:0;stage<=2;stage++)
            {
                var run=RunFreshBoot(boot,rom,media,PartitionOffset,disk.Length-PartitionOffset,stage);
                if(stage<2 ? !run.Cut||run.Cache.Barriers!=stage : run.Cut||run.Marker!=marker ||
                    run.Cache.Barriers!=(entry=="LabelColdEntry"?0:2))
                    throw new InvalidDataException($"Label {entry} stage {stage} returned {run.Marker}, barriers {run.Cache.Barriers}.");
                using var stream=File.OpenRead(media);
                string? name=null;
                if(entry!="LabelFormatEntry" || stage==2)
                {
                    var recovered=CopperNova.Reference.NovaImage.CheckPacked(stream,PartitionOffset,disk.Length-PartitionOffset);
                    name=recovered.VolumeName;
                    if(entry=="CommentWriteEntry" && stage<2 && recovered.Entries.Count!=0) throw new InvalidDataException("Uncommitted file survived.");
                    if((entry=="CommentWriteEntry" && stage==2 || entry=="LabelColdEntry") && recovered.Entries.Single(e=>e.Path=="/Å/ü").Comment!=new string('ÿ',79)) throw new InvalidDataException("Comment did not round-trip.");
                    if(name!=(entry=="LabelRenameEntry" && stage<2?"Work Disk":expected)) throw new InvalidDataException($"Recovered label {name}.");
                }
                else
                {
                    stream.Position=PartitionOffset;
                    var roots=new byte[1024]; stream.ReadExactly(roots);
                    if(roots.Any(b=>b!=0)) throw new InvalidDataException("Premature format publication.");
                }
                results.Add(new {entry,stage,name,run.Marker,run.Cut,run.Cache.Barriers,run.Cache.Writes});
            }
        }
        File.WriteAllText(Path.Combine(output,"qualification.json"),JsonSerializer.Serialize(new {
            HandlerSha256=Hash(handler),FormatterSha256=Hash(File.ReadAllBytes(formatterPath)),RomSha256=Hash(rom),results
        },new JsonSerializerOptions {WriteIndented=true}));
    }

    public static async Task FragmentedFile(string handlerPath,string romPath,string output)
    {
        Directory.CreateDirectory(output);
        var handler=File.ReadAllBytes(handlerPath);
        var rom=File.ReadAllBytes(romPath);
        var disk=CreateImage(handler,600);
        VerifyInstalledHandler(disk[..PartitionOffset],handler);
        long length=disk.Length-PartitionOffset;
        string media=Path.Combine(output,"fragmented.rdb");
        var create=await FormatBoot(output,"ExtentCreateEntry",null);
        var read=await FormatBoot(output,"ExtentReadEntry",null);
        var delete=await FormatBoot(output,"ExtentDeleteEntry",null);
        var neighbor=await FormatBoot(output,"ExtentNeighborEntry",null);
        var results=new List<object>();
        void Boot(byte[] boot,string phase,int stage,int expected)
        {
            var (marker,cut,io)=RunFreshBoot(boot,rom,media,PartitionOffset,length,stage);
            if(stage<2 ? !cut || io.Barriers!=stage : cut || marker!=expected)
                throw new InvalidDataException($"Guest failed {phase}, cut={cut}, marker={marker}.");
            // Deleting a spilled file commits its retirement before DOS replies.
            // The subsequent explicit Flush also updates the clean device.
            int expectedBarriers=phase=="delete"?3:phase=="create"?2:0;
            if(stage==2 && io.Barriers!=expectedBarriers)
                throw new InvalidDataException($"Unexpected barrier count during {phase}: {io.Barriers}.");
            if((phase=="read"||phase=="neighbor") && io.Writes!=0)
                throw new InvalidDataException("Read-only boot issued a write.");
            using var f=File.OpenRead(media);
            var report=CopperNova.Reference.NovaImage.CheckPacked(f,PartitionOffset,length);
            int files=phase=="create"&&stage<2?0:phase=="neighbor"||phase=="delete"&&stage==2?1:2;
            if(report.Entries.Count!=files || report.Entries.Any(e=>e.Extents!=WorkbenchProbe.FragmentedRuns||e.Size!=WorkbenchProbe.FragmentedRuns*512))
                throw new InvalidDataException("Incorrect committed fragmented files.");
            results.Add(new {phase,stage,cut,marker,io.Writes,io.Barriers,files,Extents=report.Entries.Select(e=>e.Extents).ToArray()});
        }
        File.WriteAllBytes(media,disk);
        for(int stage=0;stage<=2;stage++) Boot(create,"create",stage,210);
        Boot(read,"read",2,211);
        for(int stage=0;stage<=2;stage++)
        {
            Boot(delete,"delete",stage,212);
            if(stage<2) Boot(read,"read",2,211);
        }
        Boot(neighbor,"neighbor",2,213);
        File.WriteAllText(Path.Combine(output,"qualification.json"),JsonSerializer.Serialize(new {
            HandlerSha256=Hash(handler),RomSha256=Hash(rom),Cpu="MC68000",RdbStackBytes=600,PartitionBytes=length,results
        },new JsonSerializerOptions {WriteIndented=true}));
    }

    public static async Task RejectRetired(string handlerPath,string formatterPath,string romPath,string fixture,string output)
    {
        Directory.CreateDirectory(output);
        var handler=File.ReadAllBytes(handlerPath);
        var formatter=File.ReadAllBytes(formatterPath);
        var rom=File.ReadAllBytes(romPath);
        var media=Path.Combine(output,"retired.rdb");
        File.Copy(fixture,media);
        ushort version;
        using(var disk=File.Open(media,FileMode.Open,FileAccess.ReadWrite))
        {
            var layout=AmigaRdbSetup.ReadLayout(disk);
            var partition=layout.Partitions.Single(p=>p.DeviceName=="CNP0"&&p.DosType==DosType);
            long cylinder=(long)layout.Heads*layout.SectorsPerTrack*512;
            if(partition.FirstCylinder*cylinder!=PartitionOffset||
                ((long)partition.LastCylinder-partition.FirstCylinder+1)*cylinder!=FullPartitionBytes)
                throw new InvalidDataException("Expected the full-disk comparison geometry.");
            disk.Position=PartitionOffset;
            var roots=new byte[1024];
            disk.ReadExactly(roots);
            version=BinaryPrimitives.ReadUInt16BigEndian(roots.AsSpan(4));
            if(version is not (3 or 4 or 5)||BinaryPrimitives.ReadUInt16BigEndian(roots.AsSpan(516))!=version)
                throw new InvalidDataException("Expected two published v3, v4 or v5 roots.");
            var rdb=CreateImage(handler);
            MovePartition(rdb,PartitionOffset,FullPartitionBytes);
            VerifyInstalledHandler(rdb[..PartitionOffset],handler);
            disk.Position=0;
            disk.Write(rdb.AsSpan(0,PartitionOffset));
        }
        var boot=await FormatBoot(output,"RetiredFormatEntry",formatter);
        var (marker,cut,cache)=RunFreshBoot(boot,rom,media,PartitionOffset,FullPartitionBytes,2);
        if(marker!=215||cut||cache.Reads==0||cache.Writes!=0||cache.Barriers!=0)
            throw new InvalidDataException("Retired volume was mounted, modified, or accepted for formatting.");
        File.WriteAllText(Path.Combine(output,"qualification.json"),JsonSerializer.Serialize(new {
            Version=version,HandlerSha256=Hash(handler),FormatterSha256=Hash(formatter),RomSha256=Hash(rom),
            marker,cache.Reads,cache.Writes,cache.Barriers
        }));
    }

    public static async Task RunFormat(string handlerPath, string formatterPath, string romPath,
        string installedRdb, string output)
    {
        Directory.CreateDirectory(output);
        byte[] handler=File.ReadAllBytes(handlerPath), formatter=File.ReadAllBytes(formatterPath), rom=File.ReadAllBytes(romPath);
        string media = Path.Combine(output, "formatted.rdb");
        long start,length;
        byte[] rdb;
        using (var source=File.OpenRead(installedRdb))
        {
            var layout=AmigaRdbSetup.ReadLayout(source);
            var partition=layout.Partitions.Single(p=>p.DeviceName=="CNP0"&&p.DosType==DosType);
            long cylinder=(long)layout.Heads*layout.SectorsPerTrack*512;
            start=cylinder*partition.FirstCylinder;
            length=cylinder*((long)partition.LastCylinder-partition.FirstCylinder+1);
            if(start<PartitionOffset||(!((length is >=(2*1024*1024) and <=(32*1024*1024))||length==FullPartitionBytes)))
                throw new InvalidDataException("Expected a disposable CNP0 qualification image.");
            byte[] roots=new byte[1024];
            source.Position=start;
            source.ReadExactly(roots);
            if(roots.AsSpan().ContainsAnyExcept((byte)0))
                throw new InvalidDataException("The installed partition must be unformatted.");
            // Qualification copies the RDB and starts with a sparse, empty
            // partition. The source image is opened read-only throughout.
            rdb=new byte[PartitionOffset];
            source.Position=0;
            source.ReadExactly(rdb);
            VerifyInstalledHandler(rdb,handler);
            using var disk = new FileStream(media,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            if(!AmigaSparseFile.TryEnable(disk)) throw new IOException("Formatting qualification requires sparse host storage.");
            disk.SetLength(source.Length);
            disk.Write(rdb);
        }

        var format=await FormatBoot(output,"FormatEntry",formatter);
        var reject=await FormatBoot(output,"FormatRejectEntry",formatter);
        var boot=await FormatBoot(output,"FullDiskEntry",null);
        var results=new List<object>();
        foreach (int cutBefore in new[] {0,1,2})
        {
            var (marker,cut,cache)=RunFreshBoot(format,rom,media,start,length,cutBefore);
            Console.WriteLine($"Format cut={cutBefore}: marker {marker}, {cache.Td64Writes} TD64 writes, {cache.Barriers} barriers");
            if(cutBefore<2?(!cut||marker!=180||cache.Barriers!=cutBefore):(cut||marker!=214||cache.Barriers!=2))
                throw new InvalidDataException("Formatter did not stop at the expected publication boundary.");
            using var disk=File.OpenRead(media);
            if(cutBefore<2)
            {
                byte[] roots=new byte[1024]; disk.Position=start; disk.ReadExactly(roots);
                if(roots.AsSpan().ContainsAnyExcept((byte)0)) throw new InvalidDataException("A root escaped an interrupted format.");
            }
            else Verify(disk,expectCommitted:false,fullDisk:length==FullPartitionBytes);
            results.Add(new {marker,cut,cache.Barriers,cache.Td64Reads,cache.Td64Writes});
        }
        {
            var (marker,cut,cache)=RunFreshBoot(reject,rom,media,start,length,2);
            if(cut||marker!=215||cache.Writes!=0||cache.Barriers!=0)
                throw new InvalidDataException("The formatter did not reject an existing CNP2 volume without writes.");
            results.Add(new {marker,cache.Barriers,cache.Td64Reads,cache.Td64Writes});
        }

        long blocks=length/512,windows=(blocks+2047)/2048;
        long floor=(3442+((windows+2047)/2048)*2+windows*2+2046+2047)&~2047L;
        long firstPayload=start+floor*512;
        bool crosses=firstPayload<Td64Boundary&&firstPayload+WorkbenchProbe.FullDiskPayload>Td64Boundary;
        foreach (int cutBefore in new[] {0,1,2,3})
        {
            var (marker,cut,cache)=RunFreshBoot(boot,rom,media,start,length,cutBefore,firstPayload);
            Console.WriteLine($"File workload cut={cutBefore}: marker {marker}, {cache.Td64Writes} TD64 writes, {cache.Barriers} barriers");
            if(cutBefore<2?(!cut||marker!=181||cache.Barriers!=cutBefore):
                cut||marker!=(cutBefore==2?210:211)||cache.Barriers!=(cutBefore==2?2:0))
                throw new InvalidDataException("File workload stopped outside the expected commit boundary.");
            if(cutBefore==2&&crosses&&(cache.Td64Writes==0||!cache.WrotePayloadBelowBoundary||!cache.WrotePayloadAboveBoundary)||
               cutBefore==3&&(cache.Writes!=0||(crosses&&(cache.Td64Reads==0||!cache.ReadAboveBoundary))))
                throw new InvalidDataException("Expected TD64 writes across 4 GiB and a read-only cold mount.");
            using var disk=File.OpenRead(media);
            Verify(disk,expectCommitted:!cut,fullDisk:length==FullPartitionBytes,streamWorkload:true);
            byte[] prefix=new byte[rdb.Length]; disk.Position=0; disk.ReadExactly(prefix);
            if(!prefix.AsSpan().SequenceEqual(rdb)) throw new InvalidDataException("The RDB changed during format or file I/O.");
            results.Add(new {marker,cut,cache.Barriers,cache.Td64Reads,cache.Td64Writes,
                cache.WrotePayloadBelowBoundary,cache.WrotePayloadAboveBoundary,cache.ReadAboveBoundary});
        }
        File.WriteAllText(Path.Combine(output,"format.json"),JsonSerializer.Serialize(new {
            HandlerSha256=Hash(handler),FormatterSha256=Hash(formatter),RomSha256=Hash(rom),
            RdbPrefixSha256=Hash(rdb),PartitionOffset=start,PartitionBytes=length,TestedAcross4GiB=crosses,
            Cpu="68000",PhysicalHardware=false,Boots=results},new JsonSerializerOptions {WriteIndented=true}));
    }

    private static void VerifyInstalledHandler(byte[] rdb,byte[] handler)
    {
        int headerOffset=0;
        while(headerOffset<16*512 && headerOffset<=rdb.Length-512 &&
              BinaryPrimitives.ReadUInt32BigEndian(rdb.AsSpan(headerOffset))!=0x5244534b) headerOffset+=512;
        if(headerOffset>=16*512||headerOffset>rdb.Length-512) throw new InvalidDataException("No RDSK block.");
        int rdbBytes=checked((int)BinaryPrimitives.ReadUInt32BigEndian(rdb.AsSpan(headerOffset+16)));
        if(rdbBytes is not (512 or 1024)) throw new InvalidDataException("Unsupported fixture RDB block size.");
        uint Read(uint block,int at)
        {
            if(block>=rdb.Length/rdbBytes) throw new InvalidDataException("Unexpected RDB block in qualification image.");
            return BinaryPrimitives.ReadUInt32BigEndian(rdb.AsSpan((int)block*rdbBytes+at));
        }

        if(headerOffset%rdbBytes!=0) throw new InvalidDataException("Misaligned RDSK block.");
        uint header=(uint)(headerOffset/rdbBytes);
        uint fs=Read(header,32);
        while(fs!=uint.MaxValue&&Read(fs,32)!=DosType) fs=Read(fs,16);
        if(fs==uint.MaxValue||Read(fs,0)!=0x46534844) throw new InvalidDataException("Missing CNP2 FSHD.");
        int position=0;
        for(uint seg=Read(fs,72);seg!=uint.MaxValue;seg=Read(seg,16))
        {
            int bytes=checked(((int)Read(seg,4)-5)*4);
            int used=Math.Min(bytes,handler.Length-position);
            if(Read(seg,0)!=0x4c534547||bytes<=0||bytes>rdbBytes-20||
                used<=0||!rdb.AsSpan((int)seg*rdbBytes+20,used).SequenceEqual(handler.AsSpan(position,used))||
                (used<bytes&&(Read(seg,16)!=uint.MaxValue||
                    rdb.AsSpan((int)seg*rdbBytes+20+used,bytes-used).ContainsAnyExcept((byte)0))))
                throw new InvalidDataException("The installed HUNK differs from the candidate.");
            position+=used;
        }
        if(position!=handler.Length) throw new InvalidDataException("Truncated RDB HUNK.");
    }

    private static async Task<byte[]> FormatBoot(string output,string entry,byte[]? formatter)
    {
        string tree=Path.Combine(output,entry);
        Directory.CreateDirectory(Path.Combine(tree,"S"));
        var probe=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(WorkbenchProbe).Assembly.Location,EntryPoint=$"WorkbenchProbe::{entry}",
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Hunk,
            RuntimeProfile=M68kRuntimeProfile.Application,ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[]});
        if(!probe.FrameworkAnalysis.IsCompatible||probe.FrameworkAnalysis.ManagedAllocationSites.Count!=0)
            throw new InvalidDataException("Probe compilation failed.");
        File.WriteAllBytes(Path.Combine(tree,"PROBE"),probe.Image);
        if(formatter!=null) File.WriteAllBytes(Path.Combine(tree,"CopperNovaFormat"),formatter);
        File.WriteAllText(Path.Combine(tree,"S","startup-sequence"),"PROBE\n");
        return await FileSystemAdf.CreateAsync(tree,"NovaInstallGate");
    }

    private static (byte Marker,bool Cut,VolatileDisk Cache) RunFreshBoot(byte[] boot,byte[] rom,string media,
        long start,long length,int cutBefore,long firstPayload=long.MaxValue)
    {
        var cache=new VolatileDisk(start,length,cutBefore<2?cutBefore:-1) {PayloadStart=firstPayload};
        using var machine=new LightweightA500Machine(new() {
            CpuModel=M68kCpuModel.M68000,ChipRamBytes=512*1024,FastRamBytes=2*1024*1024,SlowRamBytes=0,
            FloppyDriveCount=1,
            Hardfiles=[new(0,media,readOnly:false,mountMode:AmigaHardfileMountMode.RigidDiskBlock){IoHook=cache}]});
        byte marker=0;
        machine.ParallelDataWritten+=(value,_,_)=> {
            if(value is 180 or 181 or >=210) marker=value;
        };
        machine.LoadKickstart(rom); machine.MountAdf(0,boot); machine.SetDriveWriteProtected(0,true); machine.Reset();
        try
        {
            for(int frame=0;frame<18000&&marker<210;frame++) machine.ExecuteFrame();
        }
        catch(PowerCut) when(cutBefore<2) { return (marker,true,cache); }
        Console.WriteLine($"Boot ended at {machine.Cpu.ProgramCounter:X8}, marker {marker}");
        return (marker,false,cache);
    }
}
