namespace CopperCPX;
internal struct CpxCrcInput {public uint Buffer,Bytes,Table,Zeros,Word;public bool Page;}
public partial struct CpxEngine<TMemory> where TMemory:struct,ICpxMemory
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static uint CrcKernel(ref TMemory memory, in CpxCrcInput input)
    {
        uint buffer=input.Buffer, bytes=input.Bytes, table=input.Table, word=input.Word, zeros=input.Zeros;
        bool page=input.Page;
        uint crc = uint.MaxValue;
        // On 68000, extracting the high bytes with long shifts costs more than
        // spilling the accumulator once and addressing its bytes. The private
        // word at 4080 is mount-owned and never overlaps a DMA or page buffer.
        if ((buffer & 1) == 0 && (bytes & 3) == 0)
        {
            uint n=0;
            if (page)
            {
                for (; n < 8; n += 4)
                {
                    memory.Write32(word, crc);
                    crc = memory.Read32(table + 3072 + ((uint)memory.Read8(word + 3) ^ memory.Read8(buffer + n)) * 4) ^ memory.Read32(table + 2048 + ((uint)memory.Read8(word + 2) ^ memory.Read8(buffer + n + 1)) * 4) ^ memory.Read32(table + 1024 + ((uint)memory.Read8(word + 1) ^ memory.Read8(buffer + n + 2)) * 4) ^ memory.Read32(table + ((uint)memory.Read8(word) ^ memory.Read8(buffer + n + 3)) * 4);
                }

                memory.Write32(word, crc);
                crc = memory.Read32(table + 3072 + (uint)memory.Read8(word + 3) * 4) ^ memory.Read32(table + 2048 + (uint)memory.Read8(word + 2) * 4) ^ memory.Read32(table + 1024 + (uint)memory.Read8(word + 1) * 4) ^ memory.Read32(table + (uint)memory.Read8(word) * 4);
                n = 12;
            }

            if (zeros != 0)
            {
                uint first = bytes < 64 ? bytes : 64;
                for (; n < first; n += 4)
                { memory.Write32(word, crc);
                crc = memory.Read32(table + 3072 + ((uint)memory.Read8(word + 3) ^ memory.Read8(buffer + n)) * 4) ^ memory.Read32(table + 2048 + ((uint)memory.Read8(word + 2) ^ memory.Read8(buffer + n + 1)) * 4) ^ memory.Read32(table + 1024 + ((uint)memory.Read8(word + 1) ^ memory.Read8(buffer + n + 2)) * 4) ^ memory.Read32(table + ((uint)memory.Read8(word) ^ memory.Read8(buffer + n + 3)) * 4); }
                while (bytes - n >= 64)
                {
                    uint end = n + 64, scan = n;
                    for (; scan < end; scan += 16)
                    {
                        uint at = buffer + scan;
                        if ((memory.Read32(at) | memory.Read32(at + 4) | memory.Read32(at + 8) | memory.Read32(at + 12)) != 0) break;
                    }
                    if (scan == end)
                    {
                        memory.Write32(word, crc);
                        crc = memory.Read32(zeros + (uint)memory.Read8(word + 3) * 4) ^ memory.Read32(zeros + 1024 + (uint)memory.Read8(word + 2) * 4) ^ memory.Read32(zeros + 2048 + (uint)memory.Read8(word + 1) * 4) ^ memory.Read32(zeros + 3072 + (uint)memory.Read8(word) * 4);
                        n = end; continue;
                    }
                    for (; n < end; n += 4) { memory.Write32(word, crc);
                crc = memory.Read32(table + 3072 + ((uint)memory.Read8(word + 3) ^ memory.Read8(buffer + n)) * 4) ^ memory.Read32(table + 2048 + ((uint)memory.Read8(word + 2) ^ memory.Read8(buffer + n + 1)) * 4) ^ memory.Read32(table + 1024 + ((uint)memory.Read8(word + 1) ^ memory.Read8(buffer + n + 2)) * 4) ^ memory.Read32(table + ((uint)memory.Read8(word) ^ memory.Read8(buffer + n + 3)) * 4); }
                }
            }
            for (; n < bytes; n += 4) { memory.Write32(word, crc);
                crc = memory.Read32(table + 3072 + ((uint)memory.Read8(word + 3) ^ memory.Read8(buffer + n)) * 4) ^ memory.Read32(table + 2048 + ((uint)memory.Read8(word + 2) ^ memory.Read8(buffer + n + 1)) * 4) ^ memory.Read32(table + 1024 + ((uint)memory.Read8(word + 1) ^ memory.Read8(buffer + n + 2)) * 4) ^ memory.Read32(table + ((uint)memory.Read8(word) ^ memory.Read8(buffer + n + 3)) * 4); }
            return ~crc;
        }
        for (uint n = 0; n < bytes; n++)
            crc = (crc >> 8) ^ memory.Read32(table + ((crc ^ (page && n >= 8 && n < 12 ? 0u : memory.Read8(buffer + n))) & 255) * 4);
        return ~crc;
    }


}
