namespace CopperNova.Reference;

public static partial class NovaImage
{
    private const uint Packed=0x434E5032;
    public static byte[] FormatPacked(int bytes=16*1024*1024) => FormatPacked(bytes,false);
    public static byte[] FormatPacked(int bytes,bool reusableObjects)
    {
        if(bytes%512!=0||bytes<4096*512||bytes>65536*512) throw new ArgumentOutOfRangeException(nameof(bytes));
        byte[] disk=new byte[bytes]; uint blocks=(uint)(bytes/512);
        void Page(int sector,ushort type,uint owner) { int p=sector*512; Put(disk,p,Packed); Half(disk,p+4,(ushort)(type==30&&reusableObjects?2:1)); Half(disk,p+6,type); Put(disk,p+12,(uint)sector); Put(disk,p+20,owner); Put(disk,p+24,(uint)(Identity>>32)); Put(disk,p+28,unchecked((uint)Identity)); Seal(disk,(uint)sector); }
        Put(disk,64,blocks); Put(disk,68,1394); Put(disk,72,blocks-1394); Put(disk,76,1);
        for(int index=0;index<32;index++) { int sector=2+index*2; Half(disk,96+index*2,(ushort)sector);
            for(int bit=0;bit<2048;bit++) if(index*2048+bit<1394||index*2048+bit>=blocks) disk[sector*512+128+(bit>>3)]|=(byte)(1<<(bit&7));
            Page(sector,34,(uint)index); }
        Half(disk,160,194); int leaf=194*512; Half(disk,leaf+32,64); Half(disk,leaf+40,1); disk[leaf+42]=11; Half(disk,leaf+44,24); Half(disk,leaf+46,1);
        Page(194,32,0); Page(0,30,0); return disk;
    }
    public static CheckResult CheckPacked(byte[] disk)
    {
        using var stream=new MemoryStream(disk,writable:false);
        return CheckPacked(stream,0,disk.Length);
    }

    public static CheckResult CheckPacked(Stream source,long start,long bytes)
    {
        ValidatePackedRange(source,start,bytes);
        uint blocks=(uint)(bytes/512);
        // CNP2 v1/v2 keeps all metadata below sector 1394. Decode that
        // prefix and validate file data directly from the disk stream.
        var disk=new byte[1394*512];
        source.Position=start;
        source.ReadExactly(disk);
        uint windows=WideWindows(blocks);
        bool RootOK(int bank)
        {
            int p=bank*512; ushort version=U16(disk,p+4);
            uint floor=version==3?WideFloor(windows):1394;
            return U32(disk,p)==Packed&&version is >=1 and <=3&&U16(disk,p+6)==30&&
                U32(disk,p+12)==bank&&(U32(disk,p+16)&1)==bank&&U32(disk,p+20)==0&&U32(disk,p+64)==blocks&&
                U32(disk,p+68)==floor&&floor<blocks&&
                (version==3?U16(disk,p+96) is (ushort)WideTop or (ushort)(WideTop+1):blocks<=65536)&&
                U32(disk,p+72)<=blocks-floor&&U32(disk,p+76) is >=1 and <=1023&&
                U32(disk,p+8)==Checksum(disk.AsSpan(p,512),true);
        }
        bool a=RootOK(0),b=RootOK(1); void Require(bool condition) { if(!condition) throw new InvalidDataException("Inconsistent packed metadata"); }
        Require(a||b); if(a&&b) Require(U16(disk,4)==U16(disk,516)&&U32(disk,24)==U32(disk,536)&&U32(disk,28)==U32(disk,540));
        int root=b&&(!a||U32(disk,528)>U32(disk,16))?512:0;
        bool wide=U16(disk,root+4)==3;
        uint dataFloor=U32(disk,root+68);
        uint gen=U32(disk,root+16),last=U32(disk,root+76),free=U32(disk,root+72); var reached=new HashSet<int>(); var ids=new HashSet<int>(); var holes=new Dictionary<int,uint>();
        int Page(int sector,int basis,int type,int owner) { Require((sector==basis||sector==basis+1)&&reached.Add(sector)); int p=sector*512;
            Require(U32(disk,p)==Packed&&U16(disk,p+4)==1&&U16(disk,p+6)==type&&U32(disk,p+12)==sector&&U32(disk,p+20)==owner&&U32(disk,p+16)<=gen&&U32(disk,p+24)==U32(disk,root+24)&&U32(disk,p+28)==U32(disk,root+28)&&U32(disk,p+8)==Checksum(disk.AsSpan(p,512),true)); return p; }
        var view=new byte[1280*512];
        InitializeMetadata(view,blocks);
        for(int index=0;!wide&&index<32;index++) { int p=Page(U16(disk,root+96+index*2),2+index*2,34,index),home=130+index;
            disk.AsSpan(p+128,256).CopyTo(view.AsSpan(home*512+128));
            if(index==0) for(int bit=1280;bit<1394;bit++) { Require((disk[p+128+(bit>>3)]&(1<<(bit&7)))!=0); view[home*512+128+(bit>>3)]&=(byte)~(1<<(bit&7)); }
            Seal(view,(uint)home); }
        var wideExtents=new byte[512];
        void ReadWidePage(byte[] page,uint sector,uint type,uint owner)
        {
            Require(sector<blocks);
            source.Position=checked(start+(long)sector*512);
            source.ReadExactly(page);
            Require(U32(page,0)==Packed&&U16(page,4)==1&&U16(page,6)==type&&U32(page,12)==sector&&
                U32(page,16)<=gen&&U32(page,20)==owner&&U32(page,24)==U32(disk,root+24)&&
                U32(page,28)==U32(disk,root+28)&&U32(page,8)==Checksum(page,true));
        }
        for(int group=0;group<64;group++) { int tree=U16(disk,root+160+group*2),overflow=U16(disk,root+288+group*2),extra=0; bool usedExtra=false;
            if(wide) Require(overflow==0);
            if(overflow!=0) extra=Page(overflow,1218+group*2,33,group);
            int[] children=new int[8];
            if(tree!=0) { int branch=66+group*2; if(tree==branch||tree==branch+1) { int p=Page(tree,branch,31,group); for(int i=0;i<8;i++) children[i]=U16(disk,p+32+i*2); Require(children[0]!=0&&children.Count(v=>v!=0)>1); } else children[0]=tree; }
            for(int fence=0;fence<8;fence++) { if(children[fence]==0) continue;
                int p=Page(children[fence],194+group*16+fence*2,32,group*8+fence),end=U16(disk,p+32),upper=fence+1; while(upper<8&&children[upper]==0) upper++;
                Require(end>40&&end<=512); int prev=0;
                for(int off=40;off<end;) { Require(end-off>=24); int rec=p+off,id=U16(disk,rec),type=disk[rec+2],count=disk[rec+3],len=U16(disk,rec+4),parent=U16(disk,rec+6);
                    Require(id>prev&&id<=last&&id/16==group&&(id%16)/2>=fence&&(id%16)/2<upper&&ids.Add(id)&&parent>=1&&parent<=last&&count<=8&&len>=24&&len<=216&&len<=end-off); prev=id;
                    if(type==12) {
                        Require(U16(disk,root+4)>=2&&id!=1&&count==0&&parent==1&&len==24&&U32(disk,rec+8)==0&&U32(disk,rec+16)==0&&U32(disk,rec+20)==0);
                        holes.Add(id,U32(disk,rec+12)); off+=len; continue;
                    }
                    uint home=(uint)(256+id); int dest=(int)home*512; Header(view,home,(ushort)type,(uint)id); Put(view,dest+108,(uint)parent); Put(view,dest+104,1); Put(view,dest+76,1); Put(view,dest+96,(uint)count); Put(view,dest+116,U32(disk,rec+12)); Put(view,dest+68,U32(disk,rec+8));
                    if(type==11) { Require(U32(disk,rec+8)==0); Put(view,dest+100,(uint)(len-24)); disk.AsSpan(rec+24,len-24).CopyTo(view.AsSpan(dest+320)); }
                    else
                    {
                        Require(type==10&&len==24);
                        uint logical=0,runAndSide=U32(disk,rec+20);
                        if(wide)
                        {
                            Require(count>1||(runAndSide>>31)==0);
                            if(count>1) ReadWidePage(wideExtents,1394+(uint)(id-1)*2+(runAndSide>>31),38,(uint)id);
                        }
                        for(int i=0;i<count;i++)
                        {
                            uint phys,run;
                            if(wide)
                            {
                                int at=32+(i-1)*8;
                                phys=i==0?U32(disk,rec+16):U32(wideExtents,at);
                                run=i==0?runAndSide&0x7fffffffu:U32(wideExtents,at+4);
                            }
                            else
                            {
                                if(i>0) { Require(extra!=0); usedExtra=true; }
                                int src=i==0?rec+16:extra+64+(id%16)*28+(i-1)*4;
                                phys=U16(disk,src); run=U16(disk,src+2);
                            }
                            Require(phys>=dataFloor&&phys<blocks&&run>0&&run<=blocks-phys&&run<=uint.MaxValue-logical);
                            int extent=dest+320+i*24; Put(view,extent+4,logical); Put(view,extent+12,phys); Put(view,extent+16,run); logical+=run;
                        }
                    }
                    view[129*512+128+(id>>3)]|=(byte)(1<<(id&7)); Seal(view,home); off+=len;
                }
            }
            Require(usedExtra==(extra!=0));
        }
        Require(ids.Count==last);
        var chain=new HashSet<int>(); uint cursor=U32(disk,root+88);
        while(cursor!=0) { Require(cursor<=last&&chain.Add((int)cursor)&&holes.ContainsKey((int)cursor)); cursor=holes[(int)cursor]; }
        Require(chain.Count==holes.Count); Put(view,128*512+72,wide?free:free+114); Put(view,128*512+80,1023-last+(uint)holes.Count); Seal(view,128); Seal(view,129);
        var comments=new Dictionary<uint,string>(); var groups=new HashSet<int>();
        for(int i=0;i<24;i++) { int tag=U16(disk,root+416+i*4),sector=U16(disk,root+418+i*4); if(tag==0) { Require(sector==0); continue; }
            int group=tag-1; Require(group<=255&&groups.Add(group)); int attr=Page(sector,1346+i*2,35,group);
            for(int j=0;j<4;j++) { int str=attr+64+j*80,len=disk[str],id=4*group+j; Require(len<=79); if(len!=0) { Require(ids.Contains(id)&&!holes.ContainsKey(id)&&id!=1); comments[(uint)id]=System.Text.Encoding.Latin1.GetString(disk,str+1,len); } } }
        CheckResult checkedView;
        if(wide)
        {
            var top=new byte[512]; var selector=new byte[512]; var bitmap=new byte[512];
            ReadWidePage(top,U16(disk,root+96),36,0);
            uint current=uint.MaxValue;
            checkedView=Check(view,blocks,source,start,dataFloor,(window,destination)=>
            {
                uint index=window>>11;
                if(index!=current)
                {
                    ReadWidePage(selector,WideSelectors+index*2+(Selected(top,index)?1u:0u),37,index);
                    current=index;
                }
                ReadWidePage(bitmap,WideBitmaps(windows)+window*2+(Selected(selector,window&2047)?1u:0u),34,window);
                bitmap.AsSpan(128,256).CopyTo(destination);
            });
        }
        else checkedView=Check(view,blocks,source,start);
        return checkedView with { Generation=gen,FreeSectors=free, Entries=checkedView.Entries.Select(e=>e with { Comment=comments.GetValueOrDefault(e.Slot) }).ToArray() };
    }
}
