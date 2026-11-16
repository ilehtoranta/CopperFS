namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private uint ShadowCheckpointChecksum(uint incoming,uint bytes,ulong address,uint validated,bool rootControl)
    {
        if(rootControl&&memory.Read32(Arena+ValidOne)!=0&&CanCacheMetadata&&CheckpointBodyMatches(incoming,validated,bytes))
            return CpxShadowChecksums<TMemory>.ExpectedRetarget(ref memory,validated,bytes,address,CrcTable,BufferedStorageAddress,Arena+4080);
        return Crc(incoming,bytes,true);
    }

    // The selected control image already passed a complete checksum. Compare
    // every other byte before applying the address-only CRC transform. Any
    // differing body or header takes the ordinary full-checksum path instead.
    private bool CheckpointBodyMatches(uint incoming,uint validated,uint bytes)
    {
        if(memory.Read32(incoming+32)!=memory.Read32(validated+32)||memory.Read32(incoming+36)!=memory.Read32(validated+36)||
            memory.Read32(incoming)!=memory.Read32(validated)||memory.Read32(incoming+4)!=memory.Read32(validated+4)||
            memory.Read32(incoming+12)!=memory.Read32(validated+12)) return false;
        for(uint at=24;at<bytes;at+=4)
            if(memory.Read32(incoming+at)!=memory.Read32(validated+at)) return false;
        return true;
    }
}
