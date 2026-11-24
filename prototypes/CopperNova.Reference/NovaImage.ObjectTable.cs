using System.Security.Cryptography;
using System.Text;

namespace CopperNova.Reference;

public static partial class NovaImage
{
    private static CheckResult CheckObjectTable(Stream source,long start,byte[] root)
    {
        uint blocks=U32(root,64),floor=U32(root,68),last=U32(root,76),generation=U32(root,16);
        uint windows=WideWindows(blocks),payloadBlocks=0,extentPages=0;
        var runs=new List<(uint First,uint End)>{(0,floor)};
        var objects=new Dictionary<uint,byte[]>();
        var holes=new Dictionary<uint,uint>();
        byte[] Page(uint sector,int type,uint owner)
        {
            Require(sector<blocks);
            source.Position=start+(long)sector*512;
            var p=new byte[512]; source.ReadExactly(p);
            Require(U32(p,0)==Packed&&U16(p,4)==1&&U16(p,6)==type&&U32(p,12)==sector&&
                U32(p,16)<=generation&&U32(p,20)==owner&&U32(p,24)==U32(root,24)&&
                U32(p,28)==U32(root,28)&&U32(p,8)==Checksum(p,true));
            return p;
        }
        static void Require(bool ok)
        {
            if(!ok) throw new InvalidDataException("Inconsistent growing CNP2 object table or allocation bitmap");
        }
        var top=Page(U16(root,96),36,0); byte[] selector=[];
        byte[] locator=[];
        const uint perPage=60;
        for(uint group=0;group*perPage<last;group++)
        {
            uint sectorNumber;
            uint window=group>>5,selection=window>>11;
            if((group&65535)==0) selector=Page(WideSelectors+selection*2+(Selected(top,selection)?1u:0u),37,selection);
            if((group&31)==0) locator=Page(WideBitmaps(windows)+window*2+(Selected(selector,window&2047)?1u:0u),34,window);
            sectorNumber=U32(locator,384+(int)(group&31)*4);
            if(group==0) Require((sectorNumber&~1u)==1394);
            else { Require((sectorNumber&~1u)>=floor&&(sectorNumber|1u)<blocks); runs.Add((sectorNumber&~1u,(sectorNumber|1u)+1)); }
            var index=Page(sectorNumber,39,group);
            for(int slot=0;slot<perPage;slot++)
            {
                int entry=32+slot*8;
                uint id=1+group*perPage+(uint)slot,sector=U32(index,entry),tag=U32(index,entry+4);
                if(id>last) { Require(sector==0&&tag==0); continue; }
                if(id==1) Require((sector&~1u)==194);
                else
                {
                    Require((sector&~1u)>=floor&&(sector|1u)<blocks);
                    runs.Add((sector&~1u,(sector|1u)+1));
                }
                byte[] p;
                if(tag==0) p=Page(sector,40,id);
                else
                {
                    uint value=tag>>2,kind=9+(tag&3);
                    Require(kind>=10&&value<=last);
                    p=new byte[512];
                    Half(p,32,(ushort)kind); Put(p,108,kind==12?1u:value);
                    if(kind==12) Put(p,116,value);
                }
                if(U16(p,34)==1)
                {
                    string? text=ReadDiskText(p,64,79,false);
                    Require(!string.IsNullOrEmpty(text));
                    Array.Clear(p,64,160);
                    Put(p,68,U32(p,36)); Put(p,96,U32(p,40)); Put(p,100,U32(p,44));
                    Put(p,108,U32(p,48)); Put(p,116,U32(p,52)); Put(p,136,U32(p,56));
                    p[144]=(byte)Encoding.Latin1.GetBytes(text!,p.AsSpan(145));
                }
                else Require(U16(p,34)==0 && p[144]==0);
                Require(p[144]<=79&&U32(p,64)==0&&U32(p,108)>=1&&U32(p,108)<=last);
                if(U16(p,32)==12)
                {
                    Require(id!=1&&U32(p,68)==0&&U32(p,96)==0&&U32(p,108)==1);
                    holes.Add(id,U32(p,116));
                }
                else objects.Add(id,p);
            }
        }
        for(uint id=U32(root,88);id!=0;) { Require(holes.Remove(id,out uint next)); id=next; }
        Require(holes.Count==0);
        var visited=new HashSet<uint>();
        var serials=new HashSet<ulong>();
        var entries=new List<Entry>();
        var pending=new Stack<(uint Id,uint Parent,string Path)>(); pending.Push((1,1,""));
        var buffer=new byte[65536];
        while(pending.TryPop(out var item))
        {
            Require(visited.Add(item.Id)&&objects.ContainsKey(item.Id));
            var p=objects[item.Id]; uint count=U32(p,96),size=U32(p,68);
            Require(count<=8&&U32(p,108)==item.Parent);
            string? comment=p[144]==0?null:Encoding.Latin1.GetString(p,145,p[144]);
            if(U16(p,32)==11)
            {
                uint used=U32(p,100),children=0;
                Require(used<=192&&size==0);
                if(item.Id!=1) entries.Add(new Entry(item.Path,true,item.Id,0,null){Comment=comment});
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pages=new List<(byte[] Image,uint Id,int Chain)>{(p,item.Id,-1)};
                for(int chain=0;chain<16;chain++)
                {
                    uint bucket=U32(p,228+4*chain);
                    while(bucket!=0)
                    {
                        Require(visited.Add(bucket)&&objects.ContainsKey(bucket));
                        var spill=objects[bucket];
                        Require(U16(spill,32)==11&&U32(spill,296)==item.Id&&U32(spill,108)==item.Id);
                        pages.Add((spill,bucket,chain)); bucket=U32(spill,292);
                    }
                }
                uint spillEntries=0;
                foreach(var leaf in pages)
                {
                    p=leaf.Image; used=U32(p,100); count=U32(p,96); children=0;
                    Require(used<=192&&count<=8);
                    for(int at=0;at<used;children++)
                    {
                        int e=320+at; Require(used-at>=24);
                        int len=U16(p,e+20),step=(24+len+7)&~7;
                        Require(len is >0 and <=168&&at+step<=used&&U32(p,e)==1);
                        string name=DiskText.GetString(p,e+24,len); uint hash=0;
                        Require(name.All(c=>c<=255 && c!=0 && c!=47 && c!=58));
                        for(int i=0;i<len;i++)
                        {
                            uint c=p[e+24+i];
                            if(c is >=97 and <=122 || i!=0 && p[e+23+i]==195 && c is >=160 and <=190 && c!=183) c-=32;
                            hash=unchecked((hash<<5|hash>>27)+c);
                        }
                        Require(names.Add(name)&&hash==U32(p,e+16)&&serials.Add(((ulong)U32(p,e+8)<<32)|U32(p,e+12)));
                        uint id=U32(p,e+4);
                        Require(objects.ContainsKey(id)&&U32(objects[id],136)==(leaf.Chain<0?0:leaf.Id));
                        if(leaf.Chain>=0) Require(((hash^(hash>>8)^(hash>>16))&15)==leaf.Chain);
                        pending.Push((id,item.Id,item.Path+"/"+name)); at+=step;
                    }
                    Require(children==count);
                    if(leaf.Chain>=0) spillEntries+=children;
                }
                Require(spillEntries==U32(objects[item.Id],300));
                continue;
            }
            Require(item.Id!=1&&U16(p,32)==10&&U32(p,100)==0);
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            uint logical=0,remaining=size;
            var mappings=new Stack<byte[]>();
            mappings.Push(p);
            uint upper=(uint)(((ulong)size+511)/512);
            for(;;)
            {
                uint lower=U32(p,308),link=U32(p,304);
                Require(lower<=upper);
                uint end=lower;
                for(int i=0;i<U32(p,96);i++) end=checked(end+U32(p,320+i*24+16));
                Require(end==upper);
                if(lower==0) { Require(link==0); break; }
                Require(link>=floor&&link<blocks);
                runs.Add((link,link+1)); extentPages++;
                p=Page(link,41,item.Id);
                Require(U32(p,96)==8&&U32(p,308)<lower);
                upper=lower; mappings.Push(p);
            }
            // Compare shortcuts against the independently followed physical chain.
            // Each tuple names the exact sector and exclusive logical end.
            var indexed=new List<(uint Sector,uint End)>();
            foreach(var image in mappings)
            {
                bool inode=ReferenceEquals(image,objects[item.Id]);
                uint signature=U32(image,316);
                if(signature==0)
                {
                    Require(indexed.Count==0 && U32(image,312)==0);
                    continue;
                }
                Require(signature==0x45584931 && U32(image,312)==indexed.Count);
                int n=indexed.Count,group=n&~7;
                void Check(int at,int index)
                {
                    var expected=index>=0?indexed[index]:default;
                    Require(U32(image,at)==expected.Sector && U32(image,at+4)==expected.End);
                }
                for(int i=0;i<7;i++) Check(228+i*8,i<(n&7)?group+i:-1);
                Check(284,group-1);
                if(inode) continue;
                int groups=(n&7)==7?n/8:0,groupCursor=groups;
                for(int level=0;level<16;level++)
                {
                    if(groups<=16)
                    {
                        Check(100+level*8,level<groups?(level+1)*8-1:-1);
                        continue;
                    }
                    int width=1<<level;
                    bool present=(groups&width)!=0;
                    Check(100+level*8,present?groupCursor*8-1:-1);
                    if(present) groupCursor-=width;
                }
                indexed.Add((U32(image,12),checked(U32(image,320+7*24+4)+U32(image,320+7*24+16))));
            }
            int extents=checked((mappings.Count-1)*8+(int)count);
            while(mappings.TryPop(out var image))
            for(int n=0;n<U32(image,96);n++)
            {
                int e=320+n*24; uint disk=U32(image,e+12),length=U32(image,e+16);
                Require(U32(image,e+4)==logical&&disk>=floor&&disk<blocks&&length>0&&length<=blocks-disk);
                logical=checked(logical+length); runs.Add((disk,disk+length)); payloadBlocks=checked(payloadBlocks+length);
                uint bytes=(uint)Math.Min(remaining,(ulong)length*512);
                remaining-=bytes; source.Position=start+(long)disk*512;
                while(bytes!=0) { int take=(int)Math.Min((uint)buffer.Length,bytes); source.ReadExactly(buffer.AsSpan(0,take)); digest.AppendData(buffer,0,take); bytes-=(uint)take; }
            }
            Require(remaining==0&&logical==((ulong)size+511)/512);
            entries.Add(new Entry(item.Path,false,item.Id,size,Convert.ToHexString(digest.GetHashAndReset())){Comment=comment,Extents=extents});
        }
        Require(visited.Count==objects.Count);
        runs.Sort();
        for(int i=1;i<runs.Count;i++) Require(runs[i-1].End<=runs[i].First);
        uint free=checked(blocks-floor-(last-1)*2-payloadBlocks-extentPages-((last-1)/perPage)*2);
        Require(free==U32(root,72));
        int cursor=0;
        var expected=new byte[256];
        for(uint window=0;window<windows;window++)
        {
            uint group=window>>11;
            if((window&2047)==0) selector=Page(WideSelectors+group*2+(Selected(top,group)?1u:0u),37,group);
            var bitmap=Page(WideBitmaps(windows)+window*2+(Selected(selector,window&2047)?1u:0u),34,window);
            Array.Clear(expected);
            uint first=window<<11,end=first+Math.Min(2048,blocks-first);
            while(cursor<runs.Count&&runs[cursor].End<=first) cursor++;
            for(int i=cursor;i<runs.Count&&runs[i].First<end;i++)
                MarkBits(expected,Math.Max(first,runs[i].First)-first,Math.Min(end,runs[i].End)-first);
            MarkBits(expected,end-first,2048);
            Require(expected.AsSpan().SequenceEqual(bitmap.AsSpan(128,256)));
        }
        return new(generation,objects.Count,payloadBlocks,free,entries);
    }
}
