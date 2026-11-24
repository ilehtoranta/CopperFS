namespace CopperNova.Reference;

public static partial class NovaImage
{
    // The reference implementation deliberately decodes the disk format
    // independently of the guest allocator and cache.
    private const int SectorBytes=512, PackedPrefixSectors=1394;
    private const uint WideTop=3440, WideSelectors=3442;
    private const long LargestPackedPartition=(long)uint.MaxValue*SectorBytes;

    private static uint WideWindows(uint blocks)=>((blocks-1)>>11)+1;
    private static uint WideSelectorCount(uint windows)=>((windows-1)>>11)+1;
    private static uint WideBitmaps(uint windows)=>WideSelectors+WideSelectorCount(windows)*2;


    /// <summary>Format a designated partition with a growing CNP2 object table with packed leaves.</summary>
    public static void FormatPackedWide(Stream target,long start,long length,uint allocationHint=0,string? volumeName=null)
    {
        ValidatePackedRange(target,start,length);
        if(!target.CanWrite) throw new ArgumentException("The designated partition must be writable.",nameof(target));
        uint blocks=(uint)(length/SectorBytes),windows=WideWindows(blocks),floor=(WideBitmaps(windows)+2*windows+2047)&~2047u;
        if(floor>=blocks) throw new ArgumentException("The allocation catalogue leaves no data space.",nameof(length));
        if(allocationHint!=0&&(allocationHint<floor||allocationHint>=blocks))
            throw new ArgumentOutOfRangeException(nameof(allocationHint));

        var prefix=new byte[PackedPrefixSectors*SectorBytes];
        if(volumeName!=null)
        {
            if(volumeName.Length is 0 or >30 || volumeName.Any(c=>c>255 || !ValidLabelByte((byte)c)))
                throw new ArgumentException("Volume names require 1 to 30 AmigaDOS label bytes.",nameof(volumeName));
            prefix[160]=(byte)DiskText.GetBytes(volumeName,prefix.AsSpan(161));
        }
        Put(prefix,0,Packed); Half(prefix,6,30);
        Put(prefix,24,(uint)(Identity>>32)); Put(prefix,28,unchecked((uint)Identity)); Put(prefix,76,1);
        Half(prefix,4,7); Put(prefix,64,blocks); Put(prefix,68,floor); Put(prefix,72,blocks-floor);
        prefix.AsSpan(96,64).Clear();
        Half(prefix,96,(ushort)WideTop); Put(prefix,100,allocationHint);
        Seal(prefix,0);
        target.Position=start;
        target.Write(prefix.AsSpan(0,PackedPrefixSectors*SectorBytes));
        var page=new byte[SectorBytes];
        Half(page,32,11); Put(page,108,1);
        WritePage(194,40,1);
        Put(page,32,194);
        WritePage(1394,39,0);
        WritePage(WideTop,36,0);
        for(uint selector=0;selector<WideSelectorCount(windows);selector++)
            WritePage(WideSelectors+selector*2,37,selector);
        for(uint window=0;window<windows;window++)
        {
            uint first=window<<11,lengthInWindow=Math.Min(2048u,blocks-first);
            if(first<floor) MarkBits(page.AsSpan(128,256),0,Math.Min(2048u,floor-first));
            MarkBits(page.AsSpan(128,256),lengthInWindow,2048);
            if(window==0) Put(page,384,1394);
            WritePage(WideBitmaps(windows)+window*2,34,window);
        }

        void WritePage(uint sector,ushort type,uint owner)
        {
            Put(page,0,Packed); Half(page,4,1); Half(page,6,type); Put(page,12,sector); Put(page,20,owner);
            Put(page,24,(uint)(Identity>>32)); Put(page,28,unchecked((uint)Identity));
            Put(page,8,Checksum(page,true));
            target.Position=checked(start+(long)sector*SectorBytes);
            target.Write(page);
            Array.Clear(page);
        }
    }

    private static void ValidatePackedRange(Stream source,long start,long bytes)
    {
        if(!source.CanRead||!source.CanSeek||start<0||(start&511)!=0||
            bytes%SectorBytes!=0||bytes<4096*SectorBytes||bytes>LargestPackedPartition||start>source.Length-bytes)
            throw new InvalidDataException("Invalid packed partition geometry");
    }

    private static bool Selected(byte[] selectors,uint ordinal)=>
        (selectors[128+(int)(ordinal>>3)]&(1<<(int)(ordinal&7)))!=0;
}
