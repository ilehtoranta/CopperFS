using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // A private packet keeps the Workbench Format command's raw format
    // operations outside this utility. The native owner handles the packet.
    public const uint InitializeCnp2 = 0x434e5001;
    private const uint FormatState = 624;

    public bool CanFormat => R(FormatState) == 2 && !IsMounted && !Busy && !HasOutstandingIo;

    private bool BlankRootSectors(uint controls)
    {
        for (uint offset = 0; offset < 1024; offset += 4)
            if (m.Read32(controls + offset) != 0)
                return false;
        return true;
    }

    public CpxError BeginFormat(uint identityHigh, uint identityLow)
    {
        if (!CanFormat || GrowDataFloor >= R(Blocks))
            return CpxError.InvalidArgument;

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
        m.Write16(page + 4, type == 30 ? 6u : 1u);
        m.Write16(page + 6, type);
        m.Write32(page + 12, sector);
        m.Write32(page + 20, owner);
        m.Write32(page + 24, R(IdentityHi));
        m.Write32(page + 28, R(IdentityLo));
        Seal(page);
        return Issue(IoKind.Write, sector, page, 512, next);
    }
}
