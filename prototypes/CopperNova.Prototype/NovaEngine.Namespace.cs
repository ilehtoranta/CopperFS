using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // These packet-local pointers reference cache slots pinned for this packet's epoch.
    private const uint DeleteComment = 564, AncestorSteps = 568, UnitNumber = 572;

    // Directory spill pages use normal shadowed object images. Only the owner
    // is visible to DOS. Each hash chain is threaded through private pages.
    private const uint BucketHeads=228, BucketNext=292, BucketOwner=296, SpillCount=300, NameContainer=136;
    private const uint DirectoryRoot=688, RoomPage=692, CreateBucket=696, ChainSteps=700, RetireBucket=704;
    private const uint ChainCheckpoint=736,ChainPower=740,ChainDistance=744;
    private void ResetChain()
    {
        W(ChainSteps,0); W(ChainPower,1); W(ChainDistance,0); W(ChainCheckpoint,0);
    }
    // Brent cycle detection checks malformed bucket links without extra disk reads.
    private bool AdvanceChain(uint id)
    {
        if(id>LastObjectSlot || id==R(ChainCheckpoint) || R(ChainSteps)>=LastObjectSlot) return false;
        uint distance=R(ChainDistance)+1,power=R(ChainPower);
        if(distance==power) { W(ChainCheckpoint,id); W(ChainPower,power*2); distance=0; }
        W(ChainDistance,distance); W(ChainSteps,R(ChainSteps)+1); return true;
    }
    private uint DirectoryId(uint page) => m.Read32(page+BucketOwner)!=0?m.Read32(page+BucketOwner):m.Read32(page+28);
    private uint ChainHead(uint page,uint hash) => page+BucketHeads+((hash^(hash>>8)^(hash>>16))&15)*4;
    private void CountSpill(uint page,bool add)
    {
        if(!DynamicIndex||m.Read32(page+BucketOwner)==0) return;
        uint owner=Image((uint)Find(ObjectHome(m.Read32(page+BucketOwner))));
        m.Write32(owner+SpillCount,add?m.Read32(owner+SpillCount)+1:m.Read32(owner+SpillCount)-1);
        Dirty(owner);
    }
    private void ReleaseBucket(uint page)
    {
        if(page==R(RoomPage)||m.Read32(page+20)==R(SourceParent)) return;
        m.Write32(Meta((page-Ptr(CacheImages))>>9)+12,R(Epoch)-1);
    }

    private bool ReusableIds() => PackedVolume && m.Read16(Root() + 4) >= 2;
    private bool ValidNextHole(uint id, uint next, uint last) =>
        next == 0 || (next >= 2 && next <= last && next != id);
    private bool ValidHole(uint page, uint id, uint last) =>
        id >= 2 && id <= last && ValidInodeIdentity(page, id) && m.Read16(page + 6) == 12 &&
        m.Read32(page + 108) == 1 && m.Read32(page + 96) == 0 && m.Read32(page + 68) == 0 &&
        ValidNextHole(id, m.Read32(page + 116), last);

    private bool HasBuckets(uint page)
    {
        if(m.Read16(page+6)!=11) return false;
        for(uint i=0;i<16;i++) if(m.Read32(page+BucketHeads+i*4)!=0) return true;
        return false;
    }
    private const uint EnumRoot=708, EnumChain=712, EnumBest=716;
    private StepResult ExamineBucket(uint page,uint owner)
    {
        uint directory=R(EnumRoot),fib=Arg(1)<<2;
        if(m.Read16(page+6)!=11 || page!=directory&&m.Read32(page+BucketOwner)!=owner) return Fatal();
        uint used=m.Read32(page+100),after=m.Read32(fib),best=R(EnumBest);
        if(used>192) return Fatal();
        for(uint offset=0;offset<used;)
        {
            uint entry=page+320+offset;
            if(used-offset<24) return Fatal();
            uint length=m.Read16(entry+20),size=(24+length+7)&~7u;
            if(length==0||size>used-offset) return Fatal();
            uint id=m.Read32(entry+4); if(id>after&&id<best) best=id;
            offset+=size;
        }
        W(EnumBest,best);
        uint next=0;
        if(page!=directory)
        {
            next=m.Read32(page+BucketNext);
            m.Write32(Meta((page-Ptr(CacheImages))>>9)+12,R(Epoch)-1);
        }
        uint chain=R(EnumChain);
        while(next==0&&chain<16) { next=m.Read32(directory+BucketHeads+chain*4); chain++; ResetChain(); }
        W(EnumChain,chain);
        if(next!=0)
        {
            if(!AdvanceChain(next)) return Fatal();
            return Load(ObjectHome(next),71);
        }
        if(best==uint.MaxValue) return Error(232);
        W(Target,best); return Load(ObjectHome(best),54);
    }
    private StepResult NextDirectoryEntry()
    {
        uint directory = R(Loaded), fib = Arg(1) << 2;
        if (!ValidInode(directory, R(Target)) || m.Read16(directory + 6) != 11) return Error(212);
        if(DynamicIndex)
        {
            W(EnumRoot,directory); W(EnumBest,uint.MaxValue); W(EnumChain,0); ResetChain();
            return ExamineBucket(directory,R(Target));
        }
        uint used = m.Read32(directory + 100), after = m.Read32(fib), best = uint.MaxValue;
        if (used > 192) return Fatal();
        // Use stable IDs as an ordered enumeration key. A compacted or renamed
        // record does not change the position of other surviving objects.
        for (uint offset = 0; offset < used;)
        {
            uint entry = directory + 320 + offset;
            if (used - offset < 24) return Fatal();
            uint length = m.Read16(entry + 20), bytes = (24 + length + 7) & ~7u;
            if (length == 0 || bytes > used - offset) return Fatal();
            uint id = m.Read32(entry + 4);
            if (id > after && id < best) best = id;
            offset += bytes;
        }
        if (best == uint.MaxValue) return Error(232); // ERROR_NO_MORE_ENTRIES
        W(Target, best);
        return Load(ObjectHome(best), 54);
    }

    private void RemoveName(uint directory, uint offset)
    {
        uint entry = directory + offset, size = (24 + m.Read16(entry + 20) + 7) & ~7u;
        uint used = m.Read32(directory + 100);
        m.Copy(entry, entry + size, used - (offset - 320) - size);
        ZeroTail(directory + 320 + used - size, size);
        m.Write32(directory + 100, used - size);
        m.Write32(directory + 96, m.Read32(directory + 96) - 1);
        CountSpill(directory,false); Dirty(directory);
    }

    private StepResult MoveEntry()
    {
        uint dest = R(Parent), inode = R(Inode), id = m.Read32(inode + 28);
        int slot = Find(R(SourceParent));
        if (slot < 0) return Fatal();
        uint source = Image((uint)slot), old = source + R(SourceEntry);
        if (m.Read32(old + 4) != id || m.Read32(inode + 108) != (DynamicIndex?DirectoryId(source):m.Read32(source+28))) return Fatal();
        uint used = m.Read32(dest + 100), length = R(ComponentBytes);
        WriteEntry(dest + 320 + used, id, m.Read32(old + 8), m.Read32(old + 12),
            Arena + Path + R(ComponentAt), length, R(ComponentHash));
        m.Write32(dest + 100, used + ((24 + length + 7) & ~7u));
        m.Write32(dest + 96, m.Read32(dest + 96) + 1);
        m.Write32(inode + 108, DynamicIndex?DirectoryId(dest):m.Read32(dest+28));
        if(DynamicIndex) m.Write32(inode+NameContainer,m.Read32(dest+BucketOwner)!=0?m.Read32(dest+28):0);
        CountSpill(dest,true);
        Dirty(dest); Dirty(inode);
        RemoveName(source, R(SourceEntry));
        return Finish(uint.MaxValue);
    }

    private StepResult NamespaceStep(uint phase)
    {
        uint page = R(Loaded), id = R(Target);
        switch (phase)
        {
            case 62:
                id = m.Read32(Arena + RenameBuffer + 4);
                if (!ValidInode(page, id)) return Fatal();
                W(Inode, page);
                if (m.Read16(page + 6) == 10) return MoveEntry();
                W(AncestorSteps, 0);
                W(Loaded, DynamicIndex?R(DirectoryRoot):R(Parent));
                goto case 64;
            case 64:
                page = R(Loaded);
                uint ancestor = m.Read32(page + 28), parent = m.Read32(page + 108);
                if (!ValidInode(page, ancestor) || m.Read16(page + 6) != 11) return Fatal();
                if (ancestor == m.Read32(Arena + RenameBuffer + 4)) return Error(202);
                if (ancestor == 1) return MoveEntry();
                if (R(AncestorSteps) >= LastObjectSlot || parent == 0 || parent > LastObjectSlot) return Fatal();
                W(AncestorSteps, R(AncestorSteps) + 1);
                // Only the source directory, destination and moved object are needed at commit.
                if (!DynamicIndex && page != R(Parent) && m.Read32(page + 20) != R(SourceParent))
                    m.Write32(Meta((page - Arena - CacheImages) >> 9) + 12, R(Epoch) - 1);
                return Load(ObjectHome(parent), 64);
            case 63:
                if (!ValidInode(page, id) || m.Read32(page + 108) != (DynamicIndex?DirectoryId(R(Parent)):m.Read32(R(Parent)+28))) return Fatal();
                if (Conflicts(id, true, includeLocks: true)) return Error(202);
                if (m.Read16(page + 6) == 11 && (m.Read32(page + 96) != 0 || DynamicIndex&&m.Read32(page+SpillCount)!=0)) return Error(216);
                if(DynamicIndex&&HasBuckets(page))
                {
                    for(uint i=0;i<16;i++)
                    {
                        uint link=page+BucketHeads+i*4,bucket=m.Read32(link);
                        if(bucket==0) continue;
                        W(RetireBucket,link); W(DirectoryRoot,page); W(Target,bucket); return Load(ObjectHome(bucket),72);
                    }
                }
                // Only a populated file needs the larger bitmap-retirement reserve.
                if (GrowingObjects && m.Read32(page + 68) != 0 && R(DirtyCount) > 12) return StartFlush(0);
                W(Inode, page); W(DeleteComment, 0);
                if (!GrowingObjects && CommentIndex(id >> 2, false) != 0) return Load(2000 + (id >> 2), 65);
                return Load(128, 66);
            case 72:
                if(m.Read32(page+BucketOwner)!=m.Read32(R(DirectoryRoot)+28)||m.Read32(page+96)!=0) return Fatal();
                W(Inode,page); return Load(128,66);
            case 65:
                W(DeleteComment, page);
                return Load(128, 66);
            case 66:
                if (m.Read16(page + 6) != 3) return Fatal();
                W(Group, page);
                if (m.Read32(R(Inode) + 68) != 0) {
                    W(ResizeLength, 0); W(Resume, 67);
                    return Next(400);
                }
                goto case 67;
            case 67:
                uint inode = R(Inode), group = R(Group), attribute = R(DeleteComment);
                if (attribute != 0) {
                    uint comment = attribute + 64 + (id & 3) * 80;
                    if (m.Read8(comment) != 0) { Zero(comment, 80); Dirty(attribute); }
                }
                if(R(RetireBucket)!=0) m.Write32(R(RetireBucket),m.Read32(inode+BucketNext));
                else RemoveName(R(Parent), R(SourceEntry));
                if(DynamicIndex) { m.Write32(inode+NameContainer,0); Zero(inode+BucketHeads,76); }
                m.Write16(inode + 6, 12); m.Write32(inode + 108, 1);
                m.Write32(inode + 116, m.Read32(group + 84));
                m.Write32(group + 84, id);
                Dirty(inode); Dirty(group);
                if(R(RetireBucket)!=0) { Dirty(R(DirectoryRoot));
                    m.Write32(Meta((inode-Ptr(CacheImages))>>9)+12,R(Epoch)-1); return Next(0); }
                return R(WideStreaming)!=0?FinishWideRetirement(10):Finish(uint.MaxValue);
            default: return Fatal();
        }
    }
}
