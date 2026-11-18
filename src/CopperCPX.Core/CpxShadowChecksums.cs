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
        uint checksum=ExpectedRetarget(ref memory,buffer,bytes,address,table,zeros,word);
        memory.Write32(buffer+16,(uint)(address>>32)); memory.Write32(buffer+20,(uint)address);
        memory.Write32(buffer+8,checksum);
    }

    // A validated source page can also authenticate an identical incoming
    // checkpoint body. This transform does not mutate either page or replace
    // an incoming checksum; the reader must compare the returned value.
    internal static uint ExpectedRetarget(ref TMemory memory,uint buffer,uint bytes,ulong address,uint table,uint zeros,uint word)
    {
        uint high=memory.Read32(buffer+16)^(uint)(address>>32),low=memory.Read32(buffer+20)^(uint)address;
        if(high==0&&low==0) return memory.Read32(buffer+8);
        if(high==0&&low<32&&TryShortAddressDelta(bytes,low,out uint small)) return memory.Read32(buffer+8)^small;
        uint delta=0;
        memory.Write32(word,memory.Read32(buffer+16)^(uint)(address>>32));
        for(uint at=0;at<4;at++) delta=(delta>>8)^memory.Read32(table+((delta^memory.Read8(word+at))&255)*4);
        memory.Write32(word,memory.Read32(buffer+20)^(uint)address);
        for(uint at=0;at<4;at++) delta=(delta>>8)^memory.Read32(table+((delta^memory.Read8(word+at))&255)*4);
        uint remaining=bytes-24;
        while(remaining>=64)
        {
            memory.Write32(word,delta);
            delta=memory.Read32(zeros+(uint)memory.Read8(word+3)*4)^memory.Read32(zeros+1024+(uint)memory.Read8(word+2)*4)^
                memory.Read32(zeros+2048+(uint)memory.Read8(word+1)*4)^memory.Read32(zeros+3072+(uint)memory.Read8(word)*4);
            remaining-=64;
        }
        for(uint at=0;at<remaining;at++) delta=(delta>>8)^memory.Read32(table+(delta&255)*4);
        return memory.Read32(buffer+8)^delta;
    }

    // Linear CRC contributions of address byte 23, followed by bytes-24 zeros.
    // Checkpoint self-address differences use these five low bits. Other
    // lengths and address differences retain the general exact transform.
    private static bool TryShortAddressDelta(uint bytes,uint difference,out uint delta)
    {
        uint a,b,c,d,e;
        switch(bytes)
        {
            case 512: a=0x061a2649; b=0x0c344c92; c=0x18689924; d=0x30d13248; e=0x61a26490; break;
            case 1024: a=0x3e30ef23; b=0x7c61de46; c=0xf8c3bc8c; d=0xf46b0fe9; e=0xed3a6923; break;
            case 2048: a=0x13fc09c5; b=0x27f8138a; c=0x4ff02714; d=0x9fe04e28; e=0x3a2ceaa1; break;
            case 4096: a=0x54f8810e; b=0xa9f1021c; c=0x560e72c9; d=0xac1ce592; e=0x5dd5bdd5; break;
            default: delta=0; return false;
        }
        delta=0;
        if((difference&1)!=0) delta^=a;
        if((difference&2)!=0) delta^=b;
        if((difference&4)!=0) delta^=c;
        if((difference&8)!=0) delta^=d;
        if((difference&16)!=0) delta^=e;
        return true;
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
