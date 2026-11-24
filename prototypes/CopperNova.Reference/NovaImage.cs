using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopperNova.Reference;

// Independently implemented host formatter/decoder. No production/core dependency.
public static partial class NovaImage
{
    private const uint Magic=0x434E4D31;
    private const ulong Identity=0x4E4F564142454E43;
    private static uint U32(byte[] a,int offset)=>BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(offset,4));
    private static ushort U16(byte[] a,int offset)=>BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(offset,2));
    private static void Put(byte[] a,int offset,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(offset,4),value);
    private static void Half(byte[] a,int offset,ushort value)=>BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(offset,2),value);
    private static uint Checksum(ReadOnlySpan<byte> a,bool metadata)
    {
        uint value=uint.MaxValue;
        for(int i=0;i<a.Length;i++)
        {
            value^=metadata&&i>=8&&i<12?(byte)0:a[i];
            for(int bit=0;bit<8;bit++) value=(value>>1)^((value&1)!=0?0x82F63B78u:0);
        }
        return ~value;
    }
    private static void Header(byte[] image,uint sector,ushort type,uint owner=0)
    {
        int p=checked((int)sector*512); Put(image,p,Magic); Half(image,p+4,1); Half(image,p+6,type);
        Put(image,p+20,sector); Put(image,p+24,type is 10 or 11?1u:0); Put(image,p+28,owner);
        Put(image,p+40,(uint)(Identity>>32)); Put(image,p+44,unchecked((uint)Identity));
    }
    private static void Seal(byte[] image,uint sector)
    { int p=checked((int)sector*512); Put(image,p+8,Checksum(image.AsSpan(p,512),true)); }
    public static byte[] Format(int bytes=16*1024*1024)
    {
        if(bytes%512!=0||bytes<4096*512||bytes>65536*512) throw new ArgumentOutOfRangeException(nameof(bytes));
        var image=new byte[bytes]; uint blocks=(uint)(bytes/512);
        InitializeMetadata(image,blocks);
        return image;
    }

    private static void InitializeMetadata(byte[] image,uint blocks)
    {
        for(uint n=0;n<2;n++)
        {
            Header(image,n,1); int p=(int)n*512;
            Put(image,p+64,blocks); Put(image,p+68,1280); Put(image,p+72,256); Put(image,p+76,128);
            Put(image,p+80,130); Put(image,p+84,129); Put(image,p+88,48); Seal(image,n);
        }
        Header(image,128,3); int group=128*512;
        Put(image,group+64,blocks); Put(image,group+68,1280); Put(image,group+72,blocks-1280); Put(image,group+80,1022); Seal(image,128);
        Header(image,129,5); image[129*512+128]=3; Seal(image,129);
        for(uint map=0;map<32;map++)
        {
            uint home=130+map; Header(image,home,4); int p=(int)home*512; Put(image,p+64,map*2048);
            for(uint bit=0;bit<2048;bit++)
            {
                uint block=map*2048+bit;
                if(block<1280||block>=blocks) image[p+128+(int)(bit>>3)]|=(byte)(1<<(int)(bit&7));
            }
            Seal(image,home);
        }
        Header(image,257,11,1); Put(image,257*512+76,1); Put(image,257*512+104,1); Put(image,257*512+108,1); Seal(image,257);
        Header(image,58,20); Put(image,58*512+72,0); Seal(image,58);
        Header(image,116,22); Put(image,116*512+64,U32(image,58*512+8)); Put(image,116*512+68,58); Seal(image,116);
    }
    public sealed record Entry(string Path,bool Directory,uint Slot,long Size,string? Sha256) { public string? Comment {get;init;} public int Extents {get;init;} }
    public sealed record CheckResult(uint Generation,int AllocatedInodes,uint DataSectors,uint FreeSectors,IReadOnlyList<Entry> Entries) { public string? VolumeName {get;init;} }
    public static CheckResult Check(byte[] image)
    {
        if(image.Length%512!=0) throw new InvalidDataException("Invalid image size");
        using var source=new MemoryStream(image,writable:false);
        return Check(image,(uint)(image.Length/512),source,0);
    }

    // Metadata can be a normalized prefix. File payload is always checked
    // against the original stream, at the partition-relative extent address.
    private static CheckResult Check(byte[] image,uint blocks,Stream source,long start,
        uint dataFloor=1280,Action<uint,byte[]>? readWideBitmap=null)
    {
        uint generation=0;
        var payload=new byte[16*1024];
        Valid(0,1); Valid(1,1);
        if(U32(image,64)!=blocks||U32(image,68)!=1280) throw new InvalidDataException("Anchor geometry mismatch");
        int commit=-1;
        foreach(int home in new[]{58,114})
        {
            try { Valid(home,20); }
            catch(InvalidDataException) { continue; }
            uint seq=U32(image,home*512+36);
            if(commit<0||seq>generation) { commit=home; generation=seq; }
        }
        if(commit<0) throw new InvalidDataException("No valid journal commit");
        bool clean=false;
        foreach(int home in new[]{116,117})
        {
            try { Valid(home,22); }
            catch(InvalidDataException) { continue; }
            int p=home*512;
            if(U32(image,p+36)>generation) throw new InvalidDataException("Clean generation exceeds latest commit");
            if(U32(image,p+36)==generation&&U32(image,p+64)==U32(image,commit*512+8)&&U32(image,p+68)==commit&&U32(image,p+72)==U32(image,commit*512+64)) clean=true;
        }
        if(!clean) throw new InvalidDataException("Image requires recovery; not independently clean");
        Valid(128,3); Valid(129,5);
        var used=new List<(uint First,uint End)> { (0,dataFloor) };
        var allocated=new HashSet<uint>();
        for(uint slot=1;slot<1024;slot++) if(Bit(129*512+128,slot)) { Valid((int)(256+slot),0); allocated.Add(slot); }
        if(!Bit(129*512+128,0)||!allocated.Contains(1)) throw new InvalidDataException("Reserved inode bits missing");
        var visited=new HashSet<uint>(); var serials=new HashSet<ulong>(); var entries=new List<Entry>(); uint dataCount=0;
        Walk(1,"",0);
        if(!allocated.SetEquals(visited)) throw new InvalidDataException("Unreachable inode allocation");
        used.Sort((left,right)=>left.First.CompareTo(right.First));
        for(int i=1;i<used.Count;i++)
            if(used[i].First<used[i-1].End) throw new InvalidDataException("Duplicate data ownership");
        var actualBitmap=new byte[256]; var expectedBitmap=new byte[256];
        int nextRange=0;
        uint mapCount=readWideBitmap is null?32:((blocks-1)>>11)+1;
        for(uint map=0;map<mapCount;map++)
        {
            uint first=map<<11;
            if(readWideBitmap is null)
            {
                int home=(int)(130+map); Valid(home,4);
                if(U32(image,home*512+64)!=first) throw new InvalidDataException("Bitmap identity mismatch");
                image.AsSpan(home*512+128,256).CopyTo(actualBitmap);
            }
            else readWideBitmap(map,actualBitmap);
            Array.Clear(expectedBitmap);
            uint length=first>=blocks?0:Math.Min(blocks-first,2048u),end=first+length;
            while(nextRange<used.Count&&used[nextRange].End<=first) nextRange++;
            for(int i=nextRange;i<used.Count&&used[i].First<end;i++)
                MarkBits(expectedBitmap,Math.Max(first,used[i].First)-first,Math.Min(end,used[i].End)-first);
            MarkBits(expectedBitmap,length,2048);
            if(!actualBitmap.AsSpan().SequenceEqual(expectedBitmap))
                throw new InvalidDataException($"Allocation ownership mismatch in window {map}");
        }
        uint free=blocks-dataFloor-dataCount;
        if(free!=U32(image,128*512+72)||U32(image,128*512+80)!=1023-allocated.Count) throw new InvalidDataException("Free counters mismatch");
        return new(generation,allocated.Count,dataCount,free,entries);

        bool Bit(int p,uint bit)=>(image[p+(int)(bit>>3)]&(1<<(int)(bit&7)))!=0;
        void Valid(int home,int wanted)
        {
            int p=checked(home*512);
            if(home<0||home>=blocks||U32(image,p)!=Magic||U16(image,p+4)!=1||wanted!=0&&U16(image,p+6)!=wanted||
                U32(image,p+16)!=0||U32(image,p+20)!=home||U32(image,p+32)!=0||U32(image,p+40)!=(uint)(Identity>>32)||U32(image,p+44)!=unchecked((uint)Identity)||
                U32(image,p+8)!=Checksum(image.AsSpan(p,512),true)) throw new InvalidDataException($"Invalid metadata at sector {home}");
        }
        void Walk(uint slot,string path,int depth)
        {
            if(depth>32||!allocated.Contains(slot)||!visited.Add(slot)) throw new InvalidDataException("Invalid or repeated inode reference");
            int p=(int)(256+slot)*512; bool directory=U16(image,p+6)==11;
            if(!directory&&U16(image,p+6)!=10||U32(image,p+24)!=1||U32(image,p+28)!=slot||U32(image,p+64)!=0||U32(image,p+36)>generation) throw new InvalidDataException("Inode identity mismatch");
            uint count=U32(image,p+96),size=U32(image,p+68); if(count>8) throw new InvalidDataException("Inline capacity exceeded");
            if(directory)
            {
                if(path.Length!=0) entries.Add(new(path,true,slot,0,null));
                uint bytes=U32(image,p+100),at=0,actual=0; if(bytes>192) throw new InvalidDataException("Directory length invalid");
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while(at<bytes)
                {
                    int e=p+320+(int)at; uint length=U16(image,e+20),record=(24+length+7)&~7u;
                    if(length==0||record>bytes-at||U32(image,e)!=1) throw new InvalidDataException("Directory entry invalid");
                    string name=Encoding.ASCII.GetString(image,e+24,(int)length);
                    uint hash=0; foreach(char c in name) hash=unchecked((hash<<5|hash>>27)+(uint)char.ToUpperInvariant(c));
                    ulong serial=((ulong)U32(image,e+8)<<32)|U32(image,e+12);
                    if(hash!=U32(image,e+16)||!names.Add(name)||!serials.Add(serial)) throw new InvalidDataException("Directory name/serial mismatch");
                    Walk(U32(image,e+4),path+"/"+name,depth+1); at+=record; actual++;
                }
                if(actual!=count) throw new InvalidDataException("Directory count mismatch");
            }
            else
            {
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); uint logical=0,remaining=size;
                for(uint i=0;i<count;i++)
                {
                    int e=p+320+(int)i*24; uint disk=U32(image,e+12),run=U32(image,e+16);
                    if(U32(image,e)!=0||U32(image,e+4)!=logical||U32(image,e+8)!=0||U32(image,e+20)!=0||run==0||
                        disk<dataFloor||disk>=blocks||run>blocks-disk||(ulong)logical+run>((ulong)size+511)/512)
                        throw new InvalidDataException("Extent invalid");
                    used.Add((disk,disk+run));
                    dataCount=checked(dataCount+run);
                    uint take=(uint)Math.Min(remaining,(ulong)run*512);
                    source.Position=checked(start+(long)disk*512);
                    remaining-=take;
                    while(take!=0)
                    {
                        int piece=(int)Math.Min(take,(uint)payload.Length);
                        source.ReadExactly(payload.AsSpan(0,piece));
                        hash.AppendData(payload.AsSpan(0,piece));
                        take-=(uint)piece;
                    }
                    logical+=run;
                }
                if(remaining!=0||logical!=((ulong)size+511)/512) throw new InvalidDataException("Extent/file length mismatch");
                entries.Add(new(path,false,slot,size,Convert.ToHexString(hash.GetHashAndReset())));
            }
        }
    }

    private static void MarkBits(Span<byte> bits,uint first,uint end)
    {
        while(first<end&&(first&7)!=0)
        {
            bits[(int)(first>>3)]|=(byte)(1<<(int)(first&7));
            first++;
        }
        uint wholeEnd=end&~7u;
        if(first<wholeEnd)
        {
            bits.Slice((int)(first>>3),(int)((wholeEnd-first)>>3)).Fill(255);
            first=wholeEnd;
        }
        while(first<end)
        {
            bits[(int)(first>>3)]|=(byte)(1<<(int)(first&7));
            first++;
        }
    }
}
