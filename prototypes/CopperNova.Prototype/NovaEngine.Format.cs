using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // A private packet keeps the Workbench Format command's raw format
    // operations outside this utility. The native owner handles the packet.
    public const uint InitializeCnp2 = 0x434e5001;
    private const uint FormatState = 624;

    // Counted UTF-8 in the root. DOS sees a separately decoded counted byte string.
    private const uint RootLabel=160, LabelScratch=800;
    public uint VolumeLabelAddress
    {
        get
        {
            uint label=Root()+RootLabel, scratch=Arena+State+LabelScratch;
            if(!IsMounted || !GrowingObjects || m.Read8(label)==0) return 0;
            Zero(scratch,32);
            return DecodeText(label,scratch,30,true)?scratch:0;
        }
    }

    private void EncodeText(uint source,uint target)
    {
        uint count=m.Read8(source),bytes=0;
        for(uint i=1;i<=count;i++)
        {
            uint c=m.Read8(source+i);
            if(c>=128)
            {
                m.Write8(target+(++bytes),(byte)(0xc0|(c>>6)));
                c=0x80|(c&63);
            }
            m.Write8(target+(++bytes),(byte)c);
        }
        m.Write8(target,(byte)bytes);
    }

    // Only canonical UTF-8 for U+0000..U+00FF has an inverse on the DOS interface.
    // A zero destination validates the disk image without publishing any bytes.
    private bool DecodeText(uint source,uint target,uint limit,bool label)
    {
        return DecodeTextBytes(source+1,m.Read8(source),target,limit,label);
    }
    private bool DecodeTextBytes(uint payload,uint bytes,uint target,uint limit,bool label)
    {
        uint source=payload-1,count=0;
        if(bytes>limit*2) return false;
        for(uint i=1;i<=bytes;i++)
        {
            uint c=m.Read8(source+i);
            if(c>=128)
            {
                if((c!=0xc2 && c!=0xc3) || i==bytes) return false;
                uint tail=m.Read8(source+(++i));
                if(tail<128 || tail>191) return false;
                c=((c&3)<<6)|(tail&63);
            }
            if(++count>limit || label && (c<32 || c==127 || c==47 || c==58)) return false;
            if(target!=0) m.Write8(target+count,(byte)c);
        }
        if(target!=0) m.Write8(target,(byte)count);
        return true;
    }

    private bool ValidLabel(uint name)
    {
        uint length=m.Read8(name);
        if(length>30) return false;
        for(uint i=1;i<=length;i++)
        {
            byte c=m.Read8(name+i);
            if(c<32 || c==127 || c=='/' || c==':') return false;
        }
        return true;
    }

    private bool StageLabel(uint name)
    {
        if(!ExternalRange(name,1)) return false;
        uint length=m.Read8(name);
        if(length==0 || length>30 || !ExternalRange(name,length+1) || !ValidLabel(name)) return false;
        Zero(Arena+State+LabelScratch,32);
        m.Copy(Arena+State+LabelScratch,name,length+1);
        return true;
    }

    public bool CanFormat => R(FormatState) == 2 && !IsMounted && !Busy && !HasOutstandingIo;

    private bool BlankRootSectors(uint controls)
    {
        for (uint offset = 0; offset < 1024; offset += 4)
            if (m.Read32(controls + offset) != 0)
                return false;
        return true;
    }

    public CpxError BeginFormat(uint identityHigh, uint identityLow,uint label=0)
    {
        if (!CanFormat || GrowDataFloor >= R(Blocks))
            return CpxError.InvalidArgument;

        if(label==0) Zero(Arena+State+LabelScratch,32);
        else if(!StageLabel(label)) return CpxError.InvalidArgument;

        // Once writes start, a failure requires a new mount and fresh root
        // reads. In particular a failed final barrier may leave a valid root.
        W(FormatState, 0);
        W(IdentityHi, identityHigh);
        W(IdentityLo, identityLow);
        W(CancelAt, 0);
        W(BusyAt, 1);
        W(Phase, 1300);
        return CpxError.None;
    }

    private StepResult FormatStep(uint phase)
    {
        uint page = Ptr(Scratch);
        switch (phase)
        {
            case 1300:
                Zero(page, 512);
                m.Write16(page + 32, 11);
                m.Write32(page + 108, 1);
                return WriteFormatPage(page, 194, ObjectImageType, 1, 1306);
            case 1306:
                Zero(page, 512);
                m.Write32(page + 32, 194);
                return WriteFormatPage(page, ObjectIndexStart, ObjectIndexType, 0, 1301);
            case 1301:
                Zero(page, 512);
                W(Index, 0);
                return WriteFormatPage(page, WideTopStart, WideTopType, 0, 1302);
            case 1302:
            {
                uint index = R(Index);
                if (index == SelectorCount)
                {
                    W(Index, 0);
                    return Next(1303);
                }
                Zero(page, 512);
                W(Index, index + 1);
                return WriteFormatPage(page, WideSelectorStart + index * 2, WideSelectorType, index, 1302);
            }
            case 1303:
            {
                uint window = R(Index);
                if (window == WindowCount)
                    return Issue(IoKind.Flush, 0, 0, 0, 1304);

                Zero(page, 512);
                uint first = window << 11, floor = GrowDataFloor, end = R(Blocks) - first;
                if (first < floor)
                {
                    // The metadata floor is window aligned.
                    for (uint n = 128; n < 384; n += 4)
                        m.Write32(page + n, uint.MaxValue);
                }
                else if (end < 2048)
                {
                    if ((end & 7) != 0)
                        m.Write8(page + 128 + (end >> 3), (byte)(255u << (int)(end & 7)));
                    for (uint n = (end + 7) >> 3; n < 256; n++)
                        m.Write8(page + 128 + n, 255);
                }
                if(window==0) m.Write32(page+384,ObjectIndexStart);
                W(Index, window + 1);
                return WriteFormatPage(page, WideBitmapStart + window * 2, 34, window, 1303);
            }
            case 1304:
                Zero(page, 512);
                EncodeText(Arena+State+LabelScratch,page+RootLabel);
                m.Write32(page + 64, R(Blocks));
                m.Write32(page + 68, GrowDataFloor);
                m.Write32(page + 72, R(Blocks) - GrowDataFloor);
                m.Write32(page + 76, 1);
                m.Write16(page + 96, WideTopStart);
                return WriteFormatPage(page, 0, 30, 0, 1305);
            case 1305:
                return Issue(IoKind.Flush, 0, 0, 0, 100);
            default:
                return Fatal();
        }
    }

    private StepResult WriteFormatPage(uint page, uint sector, uint type, uint owner, uint next)
    {
        m.Write32(page, PackedMagic);
        m.Write16(page + 4, type == 30 ? 7u : 1u);
        m.Write16(page + 6, type);
        m.Write32(page + 12, sector);
        m.Write32(page + 20, owner);
        m.Write32(page + 24, R(IdentityHi));
        m.Write32(page + 28, R(IdentityLo));
        Seal(page);
        return Issue(IoKind.Write, sector, page, 512, next);
    }
}
