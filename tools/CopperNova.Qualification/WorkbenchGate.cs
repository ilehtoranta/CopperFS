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

internal static class WorkbenchGate
{
    private const uint DosType=0x434e5032;
    private const int PartitionOffset=4*32768;

    private static async Task<(byte[] Handler,byte[] Original,byte[] Boot,byte[] ProbeDisk,byte[] Probe)> PrepareBoot(string handlerPath,string workbenchPath,string output)
    {
        Directory.CreateDirectory(output);
        byte[] handler=File.ReadAllBytes(handlerPath), original;
        if (Path.GetExtension(workbenchPath).Equals(".zip",StringComparison.OrdinalIgnoreCase)) {
            using var zip=ZipFile.OpenRead(workbenchPath);
            using var source=zip.Entries.Single(e=>e.Name.EndsWith(".adf",StringComparison.OrdinalIgnoreCase)).Open();
            using var copy=new MemoryStream(); await source.CopyToAsync(copy); original=copy.ToArray();
        } else original=File.ReadAllBytes(workbenchPath);
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(WorkbenchProbe).Assembly.Location,EntryPoint="WorkbenchProbe::Entry",Cpu=M68kCpuTarget.M68000,
            OutputFormat=M68kOutputFormat.Hunk,RuntimeProfile=M68kRuntimeProfile.Application,
            ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[] });
        if(!compiled.FrameworkAnalysis.IsCompatible||compiled.FrameworkAnalysis.ManagedAllocationSites.Count!=0)
            throw new InvalidDataException("Probe compilation failed");
        string extras=Path.Combine(output,"probe-files"); Directory.CreateDirectory(extras);
        File.WriteAllBytes(Path.Combine(extras,"PROBE"),compiled.Image);
        var probeDisk=await FileSystemAdf.CreateAsync(extras,"NovaProbe");
        byte[] boot=(byte[])original.Clone();
        using(var stream=new MemoryStream(boot)) {
            var volume=await FastFileSystemVolume.MountAdf(stream); await volume.ChangeDirectory("S");
            string startup;
            await using(var file=await volume.OpenFile("startup-sequence",AmigaFileMode.Read)) {
                using var reader=new StreamReader(file,Encoding.Latin1); startup=await reader.ReadToEndAsync();
            }
            var lines=startup.Replace("\r","").Split('\n').ToList();
            int load=lines.FindIndex(l=>l.Trim().Equals("C:LoadWB",StringComparison.OrdinalIgnoreCase)||l.Trim().Equals("LoadWB",StringComparison.OrdinalIgnoreCase));
            if(load<0) throw new InvalidDataException("Workbench startup has no LoadWB");
            lines.Insert(load+1,"DF1:PROBE");
            await using(var file=await volume.OpenFile("startup-sequence",AmigaFileMode.Write,overwrite:true,ignoreProtectionBits:true))
                await file.WriteAsync(Encoding.Latin1.GetBytes(string.Join('\n',lines)));
            await volume.Flush();
        }
        return (handler, original, boot, probeDisk, compiled.Image);
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

    public static async Task Run(string handlerPath,string romPath,string workbenchPath,string output)
    {
        var (handler,original,boot,probeDisk,probe)=await PrepareBoot(handlerPath,workbenchPath,output);
        byte[] rom=File.ReadAllBytes(romPath);
        string media=Path.Combine(output,"workbench.rdb");
        var image=CreateImage(handler);
        var results=new List<object>();
        foreach(byte expected in new byte[]{210,211}) {
            File.WriteAllBytes(media,image);
            var cache=new VolatileDisk(image);
            using var machine=new LightweightA500Machine(new() {
                CpuModel=M68kCpuModel.M68000,ChipRamBytes=512*1024,FastRamBytes=2*1024*1024,FloppyDriveCount=2,
                Hardfiles=[new(0,media,readOnly:false,mountMode:AmigaHardfileMountMode.RigidDiskBlock){IoHook=cache}] });
            byte marker=0;
            machine.ParallelDataWritten+=(value,_,_)=>{ if(value is 180 or 181 or 210 or 211 or >=240) marker=value; };
            machine.LoadKickstart(rom); machine.MountAdf(0,boot); machine.MountAdf(1,probeDisk);
            machine.SetDriveWriteProtected(0,true); machine.SetDriveWriteProtected(1,true); machine.Reset();
            for(int frame=0;frame<18000&&marker is not (210 or 211 or >=240);frame++) machine.ExecuteFrame();
            Console.WriteLine($"Workbench boot {expected}: marker {marker}, frames {machine.CompletedFrames}, reads {cache.Reads}, writes {cache.Writes}, barriers {cache.Barriers}");
            if(marker!=expected || (expected==210&&(cache.Writes==0||cache.Barriers<2))) throw new InvalidDataException("Workbench session failed");
            results.Add(new {marker,machine.CompletedFrames,cache.Reads,cache.Writes,cache.Barriers});
            // The next machine receives only sectors retained at the last completed barrier.
            image=cache.Durable;
            using var captured=new MemoryStream(image,writable:false);
            Verify(captured);
        }
        File.WriteAllBytes(media,image);
        File.WriteAllText(Path.Combine(output,"workbench.json"),JsonSerializer.Serialize(new {
            HandlerSha256=Hash(handler),RomSha256=Hash(rom),WorkbenchSha256=Hash(original),ProbeSha256=Hash(probe),
            Cpu="68000",PartitionOffset,PhysicalHardware=false,Boots=results
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void Verify(Stream stream)
    {
        var layout=AmigaRdbSetup.ReadLayout(stream);
        var partition=layout.Partitions.Single(p=>p.DeviceName=="CNP0"&&p.DosType==DosType);
        ulong cylinder=(ulong)layout.Heads*layout.SectorsPerTrack*512;
        ulong length=checked(cylinder*((ulong)partition.LastCylinder-partition.FirstCylinder+1));
        if(length is <(2*1024*1024) or >(32*1024*1024)) throw new InvalidDataException("Unsupported CNP2 partition length");
        stream.Position=checked((long)(cylinder*partition.FirstCylinder));
        var bytesOnDisk=new byte[(int)length]; stream.ReadExactly(bytesOnDisk);
        var check=NovaImage.CheckPacked(bytesOnDisk);
        var file=check.Entries.SingleOrDefault(e=>e.Path=="/Dst/Keep");
        byte[] bytes=Enumerable.Range(0,512).Select(i=>(byte)(i^0x6d)).ToArray();
        if(check.Entries.Count!=2||file==null||file.Slot!=2||file.Size!=512||file.Sha256!=Hash(bytes))
            throw new InvalidDataException("Cold media differs from the acknowledged namespace");
    }

    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] CreateImage(byte[] handler)
    {
        byte[] partition=NovaImage.FormatPacked(16*1024*1024,reusableObjects:true),disk=new byte[PartitionOffset+partition.Length];
        using var stream=new MemoryStream(disk);
        AmigaRdbSetup.Initialize(stream);
        void Put(int block,int at,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(block*512+at),value);
        void Seal(int block) {
            int at=block*512,words=(int)BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(at+4));
            Put(block,8,0); uint sum=0;
            for(int i=0;i<words;i++) sum=unchecked(sum+BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(at+i*4)));
            Put(block,8,unchecked(0u-sum));
        }
        int reserved=PartitionOffset/512, segments=(handler.Length+491)/492;
        if(segments+3>reserved) throw new InvalidDataException("Handler exceeds the fixture RDB reservation");
        Put(0,132,(uint)(reserved-1)); Put(0,136,4); Seal(0);
        AmigaRdbSetup.AddPartition(stream,"CNP0",4,(uint)(disk.Length/32768-1),DosType,false);
        Put(0,32,2); Put(0,152,(uint)(2+segments)); Seal(0);
        Put(2,0,0x46534844); Put(2,4,64); Put(2,16,uint.MaxValue); Put(2,32,DosType);
        Put(2,36,0x10000); Put(2,40,0x190); Put(2,60,4096); Put(2,72,3); Put(2,76,uint.MaxValue); Seal(2);
        for(int i=0;i<segments;i++) {
            int block=3+i,bytes=Math.Min(492,handler.Length-i*492);
            Put(block,0,0x4c534547); Put(block,4,(uint)(5+(bytes+3)/4)); Put(block,16,i+1==segments?uint.MaxValue:(uint)(block+1));
            handler.AsSpan(i*492,bytes).CopyTo(disk.AsSpan(block*512+20)); Seal(block);
        }
        partition.CopyTo(disk,PartitionOffset); return disk;
    }

    private sealed class VolatileDisk(byte[] initial):IAmigaHardfileIoHook
    {
        private readonly byte[] working=(byte[])initial.Clone();
        public byte[] Durable { get; }=(byte[])initial.Clone();
        public int Reads,Writes,Barriers;
        public bool FlushOnDispose=>false;
        public void ObserveOpen(int unit) {}
        public void ObserveCommand(ushort command) {}
        public void Read(FileStream backing,long offset,Span<byte> bytes) { Reads++; working.AsSpan((int)offset,bytes.Length).CopyTo(bytes); }
        public void Write(FileStream backing,long offset,ReadOnlySpan<byte> bytes) { Writes++; bytes.CopyTo(working.AsSpan((int)offset)); }
        public void Flush(FileStream backing) { working.CopyTo(Durable,0); Barriers++; }
    }
}
