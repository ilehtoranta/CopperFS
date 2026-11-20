namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint ObjectRecord => Arena + CpxArena.MountBytes;

    public bool ObjectFound
    {
        get
        {
            uint cpxArenaBase = Arena;
            return memory.Read32(cpxArenaBase + (OperationAt)) == (uint)CpxOperation.ReadObject && memory.Read32(cpxArenaBase + (Finished)) != 0 && memory.Read32(cpxArenaBase + (ErrorAt)) == 0 && memory.Read32(cpxArenaBase + (SourcePage)) != 0;
        }
    }

    private StepResult ReadStaged(ulong at, uint buffer, uint nextPhase, bool requireInode = false)
    {
        uint cpxArenaBase = Arena;
        int staged = FindStaged(at);
        if (DirectInodes) memory.Write32(ShadowStorage + 4, 1);
        if (staged >= 0 && (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
        {
            Copy(buffer, (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)staged)) * (memory.Read32(cpxArenaBase + (PageBytes)))), (memory.Read32(cpxArenaBase + (PageBytes))));
            TrustBufferedPage(buffer, at);
            if (DirectInodes) memory.Write32(ShadowStorage + 4, memory.Read16(buffer + 4) == 23 ? 1u : 0u);
            memory.Write32(cpxArenaBase + (Phase), nextPhase);
            return StepResult.Pending;
        }

        if (staged < 0 && (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && ReadBufferedClean(at, buffer))
        {
            if (DirectInodes) memory.Write32(ShadowStorage + 4, memory.Read16(buffer + 4) == 23 ? 1u : 0u);
            memory.Write32(cpxArenaBase + (Phase), nextPhase);
            return StepResult.Pending;
        }

        if (staged < 0 && ShadowRootControl && (at == StateAddress || at == Anchor + 8 * Q)) return ReadShadowControl(at, buffer, nextPhase);
        if (staged < 0 && ShadowVolume) return BeginShadowRead(at, buffer, nextPhase, requireInode);
        return Issue(IoKind.Read, staged < 0 ? at : Image(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1, (uint)staged), buffer, (memory.Read32(cpxArenaBase + (PageBytes))), nextPhase);
    }

    private StepResult ObjectStep()
    {
        if (DirectInodes) return DirectObjectStep();
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4)));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                memory.Write32(cpxArenaBase + (SourcePage), 0);
                memory.Write32(cpxArenaBase + (TreeDepth), 0);
                memory.Write32(cpxArenaBase + (ExpectedLevel), uint.MaxValue);
                if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt))is CpxOperation.ReadObject or CpxOperation.ReadFile or CpxOperation.WriteMapped or CpxOperation.Lookup && ReadBufferedObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))))))
                    return Done();
                return ReadStaged(StateAddress, p, 1);
            case 1:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(TreeAddress);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 2:
                if (memory.Read32(cpxArenaBase + (TreeDepth)) == 8 || !Allocatable(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)) + 4))))))))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (TreeDepth), memory.Read32(cpxArenaBase + (TreeDepth)) + 1);
                return ReadStaged(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)) + 4)))))), p, 3);
            case 3:
                uint level = memory.Read16(p + 50), count = memory.Read16(p + 48), width = level == 0 ? 256u : 16u;
                if (!Control(p, level == 0 ? 11u : 12u, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)) + 4))))))) || !ReadableGeneration(p) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 40)) + 4))))))) == 0) || level >= 8 || (memory.Read32(cpxArenaBase + (ExpectedLevel)) != uint.MaxValue && level != memory.Read32(cpxArenaBase + (ExpectedLevel))) || count > ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / width || (level != 0 && count == 0) || memory.Read16(p + 52) != count * width)
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (TreeCount), count);
                memory.Write32(cpxArenaBase + (TreeLevel), level);
                memory.Write32(cpxArenaBase + (ScanIndex), 0);
                memory.Write32(cpxArenaBase + (TreeSlot), 0);
                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint size = memory.Read32(cpxArenaBase + (TreeLevel)) == 0 ? 256u : 16u, index = memory.Read32(cpxArenaBase + (ScanIndex)), end = index + 64;
                if (end > memory.Read32(cpxArenaBase + (TreeCount)))
                    end = memory.Read32(cpxArenaBase + (TreeCount));
                for (; index < end; index++)
                {
                    ulong key = ((((ulong)(memory.Read32(((uint)(p + 64 + index * size))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + index * size)) + 4)))));
                    if (index > 0 && (((((ulong)(memory.Read32(((uint)(p + 64 + (index - 1) * size))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + (index - 1) * size)) + 4)))))) >= (key))
                        return Fail(CpxError.Corrupt);
                    if (memory.Read32(cpxArenaBase + (TreeLevel)) != 0)
                    {
                        if (!Allocatable(((((ulong)(memory.Read32(((uint)(p + 64 + index * size + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + index * size + 8)) + 4)))))))
                            return Fail(CpxError.Corrupt);
                        if ((key) <= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))))))
                            memory.Write32(cpxArenaBase + (TreeSlot), index);
                    }
                    else if ((((ulong)(key)) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))))))))
                        memory.Write32(cpxArenaBase + (TreeSlot), index + 1);
                }

                memory.Write32(cpxArenaBase + (ScanIndex), index);
                if (index < memory.Read32(cpxArenaBase + (TreeCount)))
                    return StepResult.Pending;
                if (memory.Read32(cpxArenaBase + (TreeLevel)) != 0)
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(TreeAddress);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 64 + memory.Read32(cpxArenaBase + (TreeSlot)) * size + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + memory.Read32(cpxArenaBase + (TreeSlot)) * size + 8)) + 4)))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(cpxArenaBase + (ExpectedLevel), memory.Read32(cpxArenaBase + (TreeLevel)) - 1);
                    memory.Write32(cpxArenaBase + (Phase), 2);
                    return StepResult.Pending;
                }

                CacheBufferedObjects(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)) + 4)))))));
                if (memory.Read32(cpxArenaBase + (TreeSlot)) != 0)
                {
                    uint source = p + 64 + (memory.Read32(cpxArenaBase + (TreeSlot)) - 1) * 256;
                    Copy((cpxArenaBase + CpxArena.MountBytes), source, 256);
                    memory.Write32(cpxArenaBase + (SourcePage), 1);
                }

                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
