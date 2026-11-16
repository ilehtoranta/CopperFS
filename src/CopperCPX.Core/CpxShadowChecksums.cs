namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    internal static uint ChecksumForShadow(ref TMemory memory, in CpxCrcInput input)
        => CrcKernel(ref memory, in input);
}

internal static class CpxShadowChecksums<TMemory> where TMemory : struct, ICpxMemory
{
    // Only a newly generated, engine-owned checkpoint may use this transform.
    // After its write is quiesced, the next checkpoint differs solely in its
    // logical address. CRC linearity applies the eight changed bytes and the
    // remaining zero-input transform, preserving the complete disk checksum.
    internal static void RetargetOwned(ref TMemory memory,uint buffer,uint bytes,ulong address,uint table,uint zeros,uint word)
    {
        uint checksum=memory.Read32(buffer+8),high=(uint)(address>>32),low=(uint)address;
        memory.Write32(buffer+16,memory.Read32(buffer+16)^high);
        memory.Write32(buffer+20,memory.Read32(buffer+20)^low);
        uint delta=0;
        for(uint at=16;at<24;at++) delta=(delta>>8)^memory.Read32(table+((delta^memory.Read8(buffer+at))&255)*4);
        uint remaining=bytes-24;
        while(remaining>=64)
        {
            memory.Write32(word,delta);
            delta=memory.Read32(zeros+(uint)memory.Read8(word+3)*4)^memory.Read32(zeros+1024+(uint)memory.Read8(word+2)*4)^
                memory.Read32(zeros+2048+(uint)memory.Read8(word+1)*4)^memory.Read32(zeros+3072+(uint)memory.Read8(word)*4);
            remaining-=64;
        }
        for(uint at=0;at<remaining;at++) delta=(delta>>8)^memory.Read32(table+(delta&255)*4);
        memory.Write32(buffer+16,high); memory.Write32(buffer+20,low);
        memory.Write32(buffer+8,checksum^delta);
    }

    internal static uint Compute(ref TMemory memory, uint buffer, uint bytes, uint table, uint zeros, uint word)
    {
        if (table != 0)
        {
            var input = new CpxCrcInput { Buffer = buffer, Bytes = bytes, Table = table, Zeros = zeros, Word = word, Page = true };
            return CpxEngine<TMemory>.ChecksumForShadow(ref memory, in input);
        }
        uint crc = uint.MaxValue;
        for (uint at = 0; at < bytes; at++)
        {
            crc ^= at is >= 8 and < 12 ? 0u : memory.Read8(buffer + at);
            for (uint bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
        }
        return ~crc;
    }
}
