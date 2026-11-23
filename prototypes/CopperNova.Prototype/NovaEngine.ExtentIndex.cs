using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // File-only fields in the inode; each immutable page saves the preceding
    // directory so truncation can restore it without rebuilding an index.
    private const uint GroupDirectory=228, IndexedPages=312, IndexSignature=316;
    private const uint ExtentIndexMagic=0x45584931, ExpectedOrdinal=772;
    // Every eighth page can name 16 earlier groups directly. Above that
    // capacity the same space holds one root per set bit of the group count.
    private const uint SubtreeDirectory=100;

    private bool ValidIndexHeader(uint image)
    {
        uint count=m.Read32(image+IndexedPages),magic=m.Read32(image+IndexSignature);
        if(count==0) return magic==0 || magic==ExtentIndexMagic;
        if(magic!=ExtentIndexMagic || count>m.Read32(image+PrefixEnd)/8) return false;
        uint last=image+GroupDirectory+((count-1)&7)*8;
        return m.Read32(last)==m.Read32(image+PrefixLink) && m.Read32(last+4)==m.Read32(image+PrefixEnd);
    }

    private StepResult ReadIndexEntry(uint image,uint offset,uint ordinal,uint phase)
    {
        uint sector=m.Read32(image+offset),end=m.Read32(image+offset+4);
        if(!ExtentOK(sector,1)||end==0||end>m.Read32(image+PrefixEnd)) return Fatal();
        W(PrefixSector,sector); W(PrefixLimit,end); W(ExpectedOrdinal,ordinal);
        return Issue(IoKind.Read,sector,Ptr(ExtentBuffer),512,phase);
    }

    private StepResult LookupPrefix(uint image,uint logical)
    {
        uint count=m.Read32(image+IndexedPages);
        if(count==0) return ReadPrefix(image,314); // Original v6 chain.

        uint baseOrdinal=count&~7u,anchor=GroupDirectory+56;
        if(baseOrdinal!=0 && logical<m.Read32(image+anchor+4))
        {
            if(image==Ptr(ExtentBuffer) && (count&7)==7)
            {
                uint groups=count>>3,cursor=groups,chosen=0,ordinal=0;
                if(groups<=16)
                {
                    for(uint i=0;i<groups;i++)
                    {
                        uint offset=SubtreeDirectory+i*8;
                        if(logical<m.Read32(image+offset+4)) return ReadIndexEntry(image,offset,(i+1)*8-1,314);
                    }
                    return Fatal();
                }
                for(uint level=0;groups!=0;level++,groups>>=1)
                {
                    if((groups&1)==0) continue;
                    uint offset=SubtreeDirectory+level*8;
                    if(logical>=m.Read32(image+offset+4)) break;
                    chosen=offset; ordinal=cursor*8-1;
                    cursor-=1u<<(int)level;
                }
                if(chosen==0) return Fatal();
                return ReadIndexEntry(image,chosen,ordinal,314);
            }
            return ReadIndexEntry(image,anchor,baseOrdinal-1,314);
        }
        for(uint i=0;i<(count&7);i++)
        {
            uint offset=GroupDirectory+i*8;
            if(logical<m.Read32(image+offset+4)) return ReadIndexEntry(image,offset,baseOrdinal+i,314);
        }
        return Fatal();
    }

    private void AppendPrefixIndex(uint inode,uint sector,uint end)
    {
        uint n=m.Read32(inode+IndexedPages),slot=n&7;
        if(slot==7) Zero(inode+GroupDirectory,56);
        uint entry=inode+GroupDirectory+8*slot;
        m.Write32(entry,sector); m.Write32(entry+4,end);
        m.Write32(inode+IndexedPages,n+1); m.Write32(inode+IndexSignature,ExtentIndexMagic);
    }

    private StepResult WriteIndexedPrefix(uint inode,bool inherited)
    {
        uint page=Ptr(ExtentBuffer),n=m.Read32(inode+IndexedPages);
        if(inherited)
        {
            // The previous anchor already contains the older forest. Merge the
            // least-significant groups by replacing them with that anchor.
            uint groups=n>>3,slot=0;
            if(groups<=16)
            {
                slot=groups-1;
                Zero(page,SubtreeDirectory);
            }
            else if(groups==17)
            {
                // Convert the full direct table to roots for 16 + 1 groups.
                m.Copy(page+SubtreeDirectory+4*8,page+SubtreeDirectory+15*8,8);
                Zero(page,SubtreeDirectory+4*8);
                Zero(page+SubtreeDirectory+5*8,11*8);
            }
            else
            {
                while((groups&1)==0) { groups>>=1; slot++; }
                Zero(page,SubtreeDirectory+slot*8);
            }
            m.Copy(page+SubtreeDirectory+slot*8,inode+GroupDirectory+56,8);
            Zero(page+GroupDirectory,512-GroupDirectory);
        }
        else Zero(page,512);
        m.Copy(page+GroupDirectory,inode+GroupDirectory,64);
        m.Write32(page+IndexedPages,n); m.Write32(page+IndexSignature,ExtentIndexMagic);
        m.Write32(page+PrefixLink,m.Read32(inode+PrefixLink));
        m.Write32(page+PrefixEnd,m.Read32(inode+PrefixEnd));
        m.Write32(page+96,8); m.Copy(page+320,inode+320,192);
        uint sector=R(DataPhysical);
        Stamp(page,ExtentPageType,sector,m.Read32(inode+28),R(Generation));
        return Issue(IoKind.Write,sector,page,512,315);
    }
}
