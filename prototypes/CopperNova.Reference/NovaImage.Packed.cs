namespace CopperNova.Reference;

public static partial class NovaImage
{
    private const uint Packed=0x434E5032;
    public static byte[] FormatPacked(int bytes=16*1024*1024)
    {
        var disk=new byte[bytes];
        using var stream=new MemoryStream(disk);
        FormatPackedWide(stream,0,bytes);
        return disk;
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
        var disk=new byte[1024];
        source.Position=start;
        source.ReadExactly(disk);
        uint windows=WideWindows(blocks);
        bool RootOK(int bank)
        {
            int p=bank*512; ushort version=U16(disk,p+4);
            uint floor=(WideBitmaps(windows)+2*windows+2047)&~2047u;
            return U32(disk,p)==Packed&&version==6&&U16(disk,p+6)==30&&
                U32(disk,p+12)==bank&&(U32(disk,p+16)&1)==bank&&U32(disk,p+20)==0&&U32(disk,p+64)==blocks&&
                U32(disk,p+68)==floor&&floor<blocks&&
                (U16(disk,p+96) is (ushort)WideTop or (ushort)(WideTop+1))&&
                U32(disk,p+72)<=blocks-floor&&U32(disk,p+76)>=1&&U32(disk,p+76)<=Math.Min(blocks/2,0x1fffffffu)&&
                U32(disk,p+8)==Checksum(disk.AsSpan(p,512),true);
        }
        bool a=RootOK(0),b=RootOK(1); void Require(bool condition) { if(!condition) throw new InvalidDataException("Inconsistent packed metadata"); }
        Require(a||b); if(a&&b) Require(U16(disk,4)==U16(disk,516)&&U32(disk,24)==U32(disk,536)&&U32(disk,28)==U32(disk,540));
        int root=b&&(!a||U32(disk,528)>U32(disk,16))?512:0;
        return CheckObjectTable(source,start,disk.AsSpan(root,512).ToArray());
    }
}
