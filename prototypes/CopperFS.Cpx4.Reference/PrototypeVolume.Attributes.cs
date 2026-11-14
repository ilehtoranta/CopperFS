using static CopperFS.Cpx4.Reference.Layout;
namespace CopperFS.Cpx4.Reference;

public readonly record struct AmigaStamp(int Days,ushort Minutes,ushort Ticks);
public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    public bool OwnershipEnabled { get { Ready(); Load(layout.State,B(3)); return U16(B(3),52)==136; } }
    private void ReadAttribute(ulong page,ulong id)
    {
        Load(page,B(4));
        if(!IsPage(B(4),PageType.Attribute,page,id)||U16(B(4),64)==2&&!OwnershipEnabled) throw new InvalidDataException("Invalid attributes or ownership feature.");
    }
    public uint ReadOwner(ulong id)
    {
        Ready(); using var frame=RentRecords(256); var obj=frame.Bytes;
        if(!GetObject(id,obj)) throw new FileNotFoundException(); ulong page=U64(obj,48);
        if(page==0) return 0; ReadAttribute(page,id); return AttributeOwner(B(4));
    }
    public MutationResult SetOwner(ulong id,uint ownerInfo)
    {
        Ready(); if(readOnly) throw new InvalidOperationException("Read-only mount.");
        using var frame=RentRecords(416); var obj=frame.Bytes[..256]; var text=frame.Bytes[256..];
        if(!GetObject(id,obj)||U32(obj,76)==0) return MutationResult.NotFound;
        ulong page=U64(obj,48); int n=0; uint previous=0;
        if(page!=0) { ReadAttribute(page,id); previous=AttributeOwner(B(4)); n=AttributeLength(B(4)); B(4).Slice(AttributeData(B(4)),n).CopyTo(text); }
        if(previous==ownerInfo) return MutationResult.Success;
        if(page==0&&ownerInfo!=0&&!EnsureReserve(Operation.Attribute)) return MutationResult.ReserveExhausted;
        Begin(Operation.Attribute);
        if(ownerInfo!=0) EnableOwnership();
        WriteAttributes(id,obj,page,ownerInfo,text[..n]); SaveObject(obj); CommitTransaction(); return MutationResult.Success;
    }
    private void EnableOwnership()
    {
        Load(layout.State,B(3)); if(U16(B(3),52)==136) return;
        if(U16(B(3),52)==48)
        {
            B(3).Slice(112,80).Clear(); Put32(B(3),112,0x4c424c31); Put16(B(3),116,10); Put16(B(3),118,1); "CopperCPX4"u8.CopyTo(B(3)[120..]);
        }
        Put16(B(3),52,136); Put32(B(3),192,0x4f574e31); Put32(B(3),196,1); Stage(layout.State,B(3));
    }
    private void WriteAttributes(ulong id,scoped Span<byte> obj,ulong page,uint owner,scoped ReadOnlySpan<byte> comment)
    {
        if(comment.Length==0&&owner==0) { if(page!=0) ReleaseMetadata(page); Put64(obj,48,0); return; }
        if(page==0) page=TakeMetadata(); Header(B(4),PageType.Attribute,page,id,generation+1);
        Put16(B(4),64,owner==0?(ushort)1:(ushort)2); int data=owner==0?72:76;
        if(owner!=0) Put32(B(4),68,owner); Put16(B(4),data-4,1); Put16(B(4),data-2,(ushort)comment.Length);
        comment.CopyTo(B(4)[data..]); Stage(page,B(4)); Put64(obj,48,page);
    }
    public MutationResult SetProtection(ulong id,uint flags)
    {
        Ready(); using var frame=RentRecords(256); var obj=frame.Bytes;
        if(!GetObject(id,obj)||U32(obj,76)==0) return MutationResult.NotFound;
        Begin(Operation.Attribute); Put32(obj,72,flags); SaveObject(obj); CommitTransaction(); return MutationResult.Success;
    }
    public MutationResult SetDate(ulong id,AmigaStamp stamp)
    {
        Ready(); if(stamp.Minutes>=1440||stamp.Ticks>=3000) return MutationResult.InvalidOperation;
        using var frame=RentRecords(256); var obj=frame.Bytes; if(!GetObject(id,obj)||U32(obj,76)==0) return MutationResult.NotFound;
        Begin(Operation.Attribute); Put32(obj,56,unchecked((uint)stamp.Days)); Put16(obj,60,stamp.Minutes); Put16(obj,62,stamp.Ticks);
        SaveObject(obj); CommitTransaction(); return MutationResult.Success;
    }
    public MutationResult SetComment(ulong id,scoped ReadOnlySpan<byte> latin1)
    {
        Ready(); if(latin1.Length>79||latin1.Contains((byte)0)) return MutationResult.InvalidOperation;
        using var frame=RentRecords(256+160); var obj=frame.Bytes[..256]; var encoded=frame.Bytes[256..]; int n=EncodeLatin1(latin1,encoded);
        if(!GetObject(id,obj)||U32(obj,76)==0) return MutationResult.NotFound;
        ulong page=U64(obj,48); uint owner=0; if(page!=0) { ReadAttribute(page,id); owner=AttributeOwner(B(4)); }
        if(n!=0&&page==0&&!EnsureReserve(Operation.Attribute)) return MutationResult.ReserveExhausted;
        Begin(Operation.Attribute);
        WriteAttributes(id,obj,page,owner,encoded[..n]);
        SaveObject(obj); CommitTransaction(); return MutationResult.Success;
    }
    public int ReadCommentUtf8(ulong id,scoped Span<byte> output)
    {
        Ready(); using var frame=RentRecords(256); var obj=frame.Bytes; if(!GetObject(id,obj)) return -1;
        ulong page=U64(obj,48); if(page==0) return 0; ReadAttribute(page,id);
        int n=AttributeLength(B(4)); if(output.Length<n) return -1; B(4).Slice(AttributeData(B(4)),n).CopyTo(output); return n;
    }
    public MutationResult CreateSymlink(ulong parent,scoped ReadOnlySpan<byte> name,scoped ReadOnlySpan<byte> latin1Target,out ulong id)
    {
        Ready(); id=0; if(latin1Target.Length==0||latin1Target.Length>768||latin1Target.Contains((byte)0)) return MutationResult.InvalidOperation;
        using var frame=RentRecords(768); int length=EncodeLatin1(latin1Target,frame.Bytes); if(length<0) return MutationResult.InvalidOperation;
        int n=LegacyName(name,NameScratch); if(n<0) return MutationResult.InvalidName;
        return CreateEncoded(parent,n,name.Length,false,out id,frame.Bytes[..length]);
    }
    public int ReadSymlinkUtf8(ulong id,scoped Span<byte> output)
    {
        Ready(); using var frame=RentRecords(256); var obj=frame.Bytes;
        if(!GetObject(id,obj)||obj[84]!=3||U64(obj,8)>768||output.Length<(int)U64(obj,8)) return -1;
        int length=(int)U64(obj,8);
        if(obj[85]==0) { if(length>160||U16(obj,88)!=length) throw new InvalidDataException("Invalid inline symlink."); obj.Slice(96,length).CopyTo(output); }
        else
        { ulong page=U64(obj,96); Load(page,B(4)); if(obj[85]!=3||!IsPage(B(4),PageType.Symlink,page,id)||U16(B(4),64)!=length) throw new InvalidDataException("Invalid external symlink."); B(4).Slice(68,length).CopyTo(output); }
        return length;
    }
    private static int EncodeLatin1(scoped ReadOnlySpan<byte> source,scoped Span<byte> target)
    {
        int n=0; foreach(byte c in source) { int count=c<128?1:2; if(n>target.Length-count) return -1;
            if(c<128) target[n++]=c; else { target[n++]=(byte)(0xc0|(c>>6)); target[n++]=(byte)(0x80|(c&63)); } } return n;
    }
}
