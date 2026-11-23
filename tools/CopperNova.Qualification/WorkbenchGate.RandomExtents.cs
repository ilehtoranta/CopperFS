using System.Buffers.Binary;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperNova.Reference;

internal static partial class WorkbenchGate
{
    private sealed record SectorRead(int Pass,int Sector,int OverflowReads,int PayloadReads,int OtherReads,
        int SeekReads,long SeekCycles,long ReadCycles);

    public static async Task RandomExtentReads(string handlerPath,string romPath,string output,bool indexed)
    {
        Directory.CreateDirectory(output);
        var handler=File.ReadAllBytes(handlerPath);
        var rom=File.ReadAllBytes(romPath);
        var readBoot=await FormatBoot(output,"RandomExtentReadEntry",null);
        string? expectedHash=null;
        foreach(var (extents,entry) in new[]{(8,"CreateEightExtents"),(64,"CreateSixtyFourExtents"),(1024,"CreateThousandExtents")})
        {
            var disk=CreateImage(handler,600);
            VerifyInstalledHandler(disk[..PartitionOffset],handler);
            var length=disk.Length-PartitionOffset;
            var media=Path.Combine(output,$"extent-{extents}.rdb");
            File.WriteAllBytes(media,disk);
            var create=await FormatBoot(output,entry,null);
            var prepared=RunFreshBoot(create,rom,media,PartitionOffset,length,2);
            if(prepared.Marker!=210) throw new InvalidDataException($"Fixture creation marker {prepared.Marker}.");
            disk=File.ReadAllBytes(media);
            var volume=NovaImage.CheckPacked(disk.AsSpan(PartitionOffset).ToArray());
            var target=volume.Entries.Single(e=>e.Path=="/Target");
            expectedHash??=target.Sha256;
            if(target.Size!=524288||target.Extents!=extents||target.Sha256!=expectedHash)
                throw new InvalidDataException($"Unexpected fixture: {target}.");

            var rows=new List<SectorRead>();
            var io=new VolatileDisk(PartitionOffset,length,-1);
            int phase=0,overflow=0,payload=0,other=0,seekReads=0,bufferSector=0;
            io.ReadObserved=(offset,bytes)=>
            {
                if(phase==1) { seekReads++; return; }
                if(phase!=2) return;
                var data=disk.AsSpan(checked((int)offset),bytes);
                if(bytes==512 && BinaryPrimitives.ReadUInt32BigEndian(data)==DosType &&
                    BinaryPrimitives.ReadUInt16BigEndian(data[6..])==41 &&
                    BinaryPrimitives.ReadUInt32BigEndian(data[12..])==(offset-PartitionOffset)/512 &&
                    BinaryPrimitives.ReadUInt32BigEndian(data[20..])==target.Slot) overflow++;
                else if(bytes==512 && BinaryPrimitives.ReadUInt32BigEndian(data)==0x54000000u+bufferSector) payload++;
                else other++;
            };
            byte done=0;
            long seekStart=0,readStart=0;
            using var machine=new LightweightA500Machine(new() {
                CpuModel=M68kCpuModel.M68020,ChipRamBytes=512*1024,FastRamBytes=2*1024*1024,SlowRamBytes=0,
                FloppyDriveCount=1,Hardfiles=[new(0,media,readOnly:false,mountMode:AmigaHardfileMountMode.RigidDiskBlock){IoHook=io}]
            });
            machine.ParallelDataWritten+=(marker,_,cycle)=>
            {
                if(marker==181)
                {
                    bufferSector=WorkbenchProbe.RandomReadSector(rows.Count%32);
                    seekStart=cycle; seekReads=overflow=payload=other=0; phase=1;
                }
                else if(marker==182 && phase==1) { readStart=cycle; phase=2; }
                else if(marker==183 && phase==2)
                {
                    phase=0;
                    int depth=(extents-1-bufferSector/(1024/extents))/8;
                    bool validCount=indexed && depth!=0 ? overflow>=1 && overflow<=Math.Min(depth,extents==64?1:3) : overflow==depth;
                    if(!validCount||payload!=1||other!=0||seekReads!=0)
                        throw new InvalidDataException($"Sector {bufferSector}: linear depth {depth}, observed {overflow}/{payload}/{other}, seek {seekReads}.");
                    rows.Add(new(rows.Count/32+1,bufferSector,overflow,payload,other,seekReads,readStart-seekStart,cycle-readStart));
                }
                else if(marker>=210) done=marker;
            };
            machine.LoadKickstart(rom); machine.MountAdf(0,readBoot); machine.SetDriveWriteProtected(0,true); machine.Reset();
            for(int frame=0;frame<18000&&done==0;frame++) machine.ExecuteFrame();
            if(done!=211||rows.Count!=96||io.Writes!=0) throw new InvalidDataException($"Read probe {done}, samples {rows.Count}, writes {io.Writes}.");
            File.WriteAllText(Path.Combine(output,$"{extents}-reads.json"),JsonSerializer.Serialize(new {
                Indexed=indexed,Cpu="MC68020",HandlerSha256=Hash(handler),RomSha256=Hash(rom),ProbeSha256=Hash(readBoot),
                FileSha256=target.Sha256,FileBytes=target.Size,Extents=extents,IoBytes=512,rows
            },new JsonSerializerOptions {WriteIndented=true}));
            Console.WriteLine($"{extents} extents: {rows.Average(r=>r.ReadCycles):F0} cycles, {rows.Average(r=>r.OverflowReads):F1} overflow reads.");
        }
    }
}
