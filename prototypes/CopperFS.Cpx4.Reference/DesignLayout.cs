using System.Buffers.Binary;

namespace CopperFS.Cpx4.Reference;

// CPX4 is an unregistered, incompatible research format. Addresses are allocation units.
internal readonly record struct Layout(int UnitBytes, int SectorBytes, ulong Units, ulong VolumeId, ushort DiskVersion = 4, bool RootControl = false, bool DirectInodes = false, bool SmallMetadata = false)
{
    internal const uint Magic = 0x43505834;
    internal const ushort Version = 4;
    internal bool Shadow => DiskVersion == 5;
    internal uint DiskMagic => Shadow ? 0x43505835u : Magic;
    internal ulong Features => SmallMetadata ? 4UL : DirectInodes ? 3UL : RootControl ? 2UL : 1UL;
    internal const int HeaderBytes = 64, JournalImages = 64, OperationImages = 42;
    internal const int MaxHeight = 8, ReserveCapacity = 64, ReserveTarget = 32, ReserveMargin = 12;
    internal const int NameBytes = 768, InlineBytes = 160, GroupUnits = 65536, BitmapUnits = 4096;
    internal int PageBytes => Math.Max(SmallMetadata ? 512 : 1024, UnitBytes);
    internal int BitmapWindowUnits => PageBytes == 512 ? 2048 : BitmapUnits;
    internal int BitmapWindowShift => AddressMath.PowerShift((uint)BitmapWindowUnits);
    internal int BitmapPayloadBytes => BitmapWindowUnits / 8;
    internal int GroupMapCount => GroupUnits / BitmapWindowUnits;
    internal int GroupMetadataPages => GroupMapCount + 1;
    internal int GroupPayloadBytes => 16 + GroupMapCount * 4;
    internal int UnitShift => AddressMath.PowerShift((uint)UnitBytes);
    internal int DeviceShift => UnitShift - AddressMath.PowerShift((uint)SectorBytes);
    internal ulong UnitOffset(ulong bytes) => AddressMath.Right(bytes, UnitShift);
    internal uint UnitRemainder(ulong bytes) => (uint)bytes & ((uint)UnitBytes - 1);
    internal ulong ByteOffset(ulong units) => AddressMath.Left(units, UnitShift);
    internal ulong DeviceSector(ulong units) => AddressMath.Left(units, DeviceShift);
    internal ulong Q => (ulong)(PageBytes / UnitBytes);
    internal ulong MetadataSpan => Shadow ? 2 * Q : Q;
    internal ulong DeviceUnits => (ulong)(UnitBytes / SectorBytes);
    internal ulong Anchor => (65536UL >> UnitShift);
    internal ulong CheckpointA => Anchor + Q;
    internal ulong CheckpointB => Anchor + 2 * Q;
    internal ulong CheckpointC => Anchor + 3 * Q;
    internal ulong State => Anchor + (Shadow ? 4UL : 3UL) * Q;
    internal ulong Charset => Anchor + (Shadow ? 6UL : 4UL) * Q;
    internal ulong Reserve => Anchor + 8 * Q;
    internal ulong Backup => (Units & ~(Q - 1)) - Q;
    internal ulong DataStart => !Shadow || ShadowMapEnd <= (2097152UL >> UnitShift)
        ? (2097152UL >> UnitShift) : DivideUp(ShadowMapEnd, GroupUnits) * GroupUnits;
    internal ulong FirstMetadata => DataStart + (GroupBase(DataStart) == 0 ? 0 : (ulong)GroupMetadataPages * MetadataSpan);
    internal ulong Journal(ulong transaction) => AddressMath.Right((transaction & 1) == 1 ? 1048576UL : 1572864UL, UnitShift);
    internal ulong Descriptor(ulong transaction, int index) => Journal(transaction) + (ulong)(1 + index) * Q;
    internal ulong Image(ulong transaction, int index) => Journal(transaction) + (ulong)(5 + index) * Q;
    internal ulong Commit(ulong transaction) => Journal(transaction) + 69 * Q;
    internal int DescriptorsPerPage => (PageBytes - HeaderBytes) / 32;
    internal ulong Groups => DivideUp(Units, GroupUnits);
    internal ulong GroupBase(ulong address) => address & ~65535UL;
    internal ulong GroupDescriptor(ulong group) => group == 0 ? (131072UL >> UnitShift) : group;
    internal ulong Bitmap(ulong group, int index) => GroupDescriptor(group) + (ulong)(index + 1) * MetadataSpan;
    internal uint ValidUnits(ulong group) => (uint)Math.Min((ulong)GroupUnits, Units - group);
    internal bool HasGroup(ulong group) => group < Units &&
        (group == 0 || (!Shadow || group >= DataStart) && (ulong)ValidUnits(group) >= (ulong)GroupMetadataPages * MetadataSpan + (Units - group < GroupUnits ? Q : 0));
    internal bool Permanent(ulong unit) => unit < DataStart || unit >= Backup ||
        !HasGroup(GroupBase(unit)) || (GroupBase(unit) != 0 && (unit & 65535) < (ulong)GroupMetadataPages * MetadataSpan);
    internal bool PageAddress(ulong unit) => unit < Units && (unit & (Q - 1)) == 0 && Q <= Units - unit;
    internal bool AllocatablePage(ulong unit) => PageAddress(unit) && (!Shadow || (unit & (MetadataSpan - 1)) == 0) &&
        MetadataSpan <= Units - unit && !Permanent(unit) && !Permanent(unit + MetadataSpan - 1);
    internal static ulong DivideUp(ulong n, ulong d) => AddressMath.Units(n, AddressMath.PowerShift(checked((uint)d)));
    internal void Validate()
    {
        if (SectorBytes is < 512 or > 4096 || (SectorBytes & (SectorBytes - 1)) != 0 ||
            UnitBytes < SectorBytes || UnitBytes > 4096 || (UnitBytes & (UnitBytes - 1)) != 0 ||
            DiskVersion is not (4 or 5) || RootControl && !Shadow || DirectInodes && !RootControl || SmallMetadata && !DirectInodes || VolumeId == 0 || Units < (4194304UL >> UnitShift) || Units > AddressMath.Right(ulong.MaxValue, UnitShift) ||
            Shadow && (ShadowHeight > MaxHeight || FirstMetadata + 33 * MetadataSpan >= Backup))
            throw new ArgumentException("Unsupported CPX4 geometry.");
    }
    internal static ushort U16(ReadOnlySpan<byte> p, int n) => BinaryPrimitives.ReadUInt16BigEndian(p[n..]);
    internal static uint U32(ReadOnlySpan<byte> p, int n) => BinaryPrimitives.ReadUInt32BigEndian(p[n..]);
    internal static ulong U64(ReadOnlySpan<byte> p, int n) => BinaryPrimitives.ReadUInt64BigEndian(p[n..]);
    internal static void Put16(Span<byte> p, int n, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(p[n..], v);
    internal static void Put32(Span<byte> p, int n, uint v) => BinaryPrimitives.WriteUInt32BigEndian(p[n..], v);
    internal static void Put64(Span<byte> p, int n, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(p[n..], v);
    internal static uint Crc(ReadOnlySpan<byte> bytes, bool page = false)
    {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= page && i is >= 8 and < 12 ? 0u : bytes[i];
            for (int b = 0; b < 8; b++) crc = (crc >> 1) ^ (0x82F63B78u & (0u - (crc & 1)));
        }
        return ~crc;
    }
    internal static int PayloadBytes(ReadOnlySpan<byte> p) => (PageType)U16(p, 4) switch
    {
        PageType.Anchor => 64, PageType.Checkpoint => U16(p,6) == 5 ? U16(p,52) == 432 ? 432 : 32 : 24, PageType.State => U16(p,52) is 128 or 136?U16(p,52):48,
        PageType.Reserve => p.Length == 512 ? 264 : 520, PageType.Charset => 4, PageType.Group => p.Length == 512 ? 144 : 80, PageType.Bitmap => p.Length == 512 ? 256 : 512,
        PageType.JournalHeader or PageType.Commit => 40, PageType.Descriptor => U16(p, 48) * 32,
        PageType.Inode => 256, PageType.Tombstone => 0,
        PageType.ObjectLeaf => U16(p, 48) * 256, PageType.DirectoryLeaf => U16(p, 48) * 128,
        PageType.ExtentLeaf => U16(p, 48) * 24, PageType.DirectoryBranch => U16(p, 48) * 24,
        PageType.ObjectBranch or PageType.ExtentBranch => U16(p, 48) * 16,
        PageType.Name or PageType.Symlink => 4 + Math.Min(p.Length - 68, U16(p, 64)),
        PageType.BlobTail => 4 + U16(p, 64), PageType.Attribute => AttributeData(p)-64+AttributeLength(p), _ => 0
    };
    internal static void Seal(Span<byte> p) { Put16(p, 52, checked((ushort)PayloadBytes(p))); Put32(p, 8, Crc(p, true)); }
    internal static bool ValidCrc(ReadOnlySpan<byte> p, ushort version = Version) => U32(p, 0) == (version == 5 ? 0x43505835u : Magic) && U16(p, 6) == version && U32(p, 8) == Crc(p, true);
    internal static void Header(Span<byte> p, PageType type, ulong address, ulong owner, ulong transaction, int count = 0, int level = 0, ushort version = Version)
    {
        p.Clear(); Put32(p, 0, version == 5 ? 0x43505835u : Magic); Put16(p, 4, (ushort)type); Put16(p, 6, version);
        Put64(p, 16, address); Put64(p, 24, owner); Put64(p, 32, transaction);
        Put16(p, 48, (ushort)count); Put16(p, 50, (ushort)level);
    }
    internal static bool IsPage(ReadOnlySpan<byte> p, PageType type, ulong address, ulong owner, ushort version = Version) =>
        ValidCrc(p, version) && U16(p, 4) == (ushort)type && U64(p, 16) == address && U64(p, 24) == owner &&
        U16(p, 52) == PayloadBytes(p) && PayloadBytes(p) <= p.Length - HeaderBytes && (type!=PageType.State||ValidLabel(p)) && (type!=PageType.Attribute||ValidAttribute(p));
    internal static uint AttributeOwner(ReadOnlySpan<byte> p)=>U16(p,64)==2?U32(p,68):0;
    internal static int AttributeData(ReadOnlySpan<byte> p)=>U16(p,64)==2?76:72;
    internal static int AttributeLength(ReadOnlySpan<byte> p)=>U16(p,AttributeData(p)-2);
    internal static bool ValidAttribute(ReadOnlySpan<byte> p)
    {
        int version=U16(p,64),length=AttributeLength(p);
        if(U16(p,66)!=0||length>158) return false;
        return version==1?length>0&&U16(p,68)==1:version==2&&U32(p,68)!=0&&U16(p,72)==1;
    }
    internal static bool ValidLabel(ReadOnlySpan<byte> p)
    {
        if(U16(p,52)==48) return true;
        int length=U16(p,116);
        if(U16(p,52) is not (128 or 136)||U32(p,112)!=0x4c424c31||U16(p,118)!=1||length is <1 or >60) return false;
        if(U16(p,52)==136&&(U32(p,192)!=0x4f574e31||U32(p,196)!=1)) return false;
        string label;
        try { label=new System.Text.UTF8Encoding(false,true).GetString(p.Slice(120,length)); }
        catch(System.Text.DecoderFallbackException) { return false; }
        if(label.Length is <1 or >30||label.Any(c=>c<32||c>255||c is (char)127 or '/' or ':')) return false;
        return p.Slice(120+length,72-length).IndexOfAnyExcept((byte)0)<0;
    }
    internal void Identity(Span<byte> p) { Put64(p, 64, 0); Put64(p, 72, VolumeId); }
    internal void MediaHeader(Span<byte> p, PageType type, ulong address, ulong owner, ulong transaction, int count = 0, int level = 0)
        => Header(p, type, address, owner, transaction, count, level, DiskVersion);
    internal bool IdentityMatches(ReadOnlySpan<byte> p) => U64(p, 64) == 0 && U64(p, 72) == VolumeId;

    // Independent arithmetic; this reference project intentionally has no dependency
    // on the production CPX5 map geometry or cursors.
    internal int ShadowPairShift => AddressMath.PowerShift((uint)(2 * PageBytes / UnitBytes));
    internal int ShadowLeafShift => AddressMath.PowerShift((uint)PageBytes) + (DirectInodes ? 1 : 2);
    internal int ShadowBranchShift => AddressMath.PowerShift((uint)PageBytes) + 1;
    internal ulong ShadowPairs => Units >> ShadowPairShift;
    internal ulong ShadowMapBase => 524288UL >> UnitShift;
    internal ulong ShadowNodes(int level)
    {
        ulong n = ((ShadowPairs - 1) >> ShadowLeafShift) + 1;
        for (int i = 0; i < level; i++) n = ((n - 1) >> ShadowBranchShift) + 1;
        return n;
    }
    internal int ShadowHeight
    {
        get { int height = 1; while (ShadowNodes(height - 1) > 1 && height <= MaxHeight) height++; return height; }
    }
    internal ulong ShadowNodePair(int level, ulong index)
    {
        ulong offset = index;
        for (int i = 0; i < level; i++) offset += ShadowNodes(i);
        return ShadowMapBase + (offset << ShadowPairShift);
    }
    internal ulong ShadowMapEnd => ShadowNodePair(ShadowHeight, 0);
}

internal enum PageType : ushort
{
    Anchor = 1, Checkpoint, State, Reserve, Charset, Group, Bitmap, JournalHeader, Descriptor, Commit,
    ObjectLeaf, ObjectBranch, DirectoryLeaf, DirectoryBranch, ExtentLeaf, ExtentBranch, Name, Attribute, Symlink, SelectorLeaf, SelectorBranch, BlobTail, Inode, Tombstone
}
internal enum TreeKind { Object, Directory, Extent }
internal enum Operation { Create, Link, Rename, Unlink, Attribute, Data, Reclaim, Prune, Refill, Probe }
internal static class Credits
{
    internal static int Images(Operation op) => op switch
    { Operation.Create => 39, Operation.Link => 22, Operation.Rename => 30, Operation.Unlink => 7,
      Operation.Attribute => 6, Operation.Data => 42, Operation.Reclaim => 9, Operation.Prune => 27,
      Operation.Refill => 6, Operation.Probe => 42, _ => throw new ArgumentOutOfRangeException(nameof(op)) };
    internal static int Pages(Operation op) => op switch
    { Operation.Create => 20, Operation.Link or Operation.Rename => 10, Operation.Attribute => 1,
      Operation.Data => 18, _ => 0 };
}
