namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // One validated append context uses the spare 64 bytes between the saved
    // allocation ranges and object cache. All pointers refer to owned dirty
    // images. No DOS buffer or CPU frame is retained after completion.
    private void InvalidateFastWrite()
    {
        uint p = memory.Read32(Arena + (FastPointer));
        if (p != 0)
            memory.Write32(p, 0);
    }

    private void ArmFastAppend()
    {
        uint cpxArenaBase = Arena;
        uint hint = memory.Read32(cpxArenaBase + (FastPointer)), record = (cpxArenaBase + CpxArena.MountBytes);
        if (hint == 0 || !((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) || memory.Read32(cpxArenaBase + (BufferFreed)) != 0 || memory.Read8(record + 85) != 1 || memory.Read16(record + 90) != 1 || ((uint)(((ulong)(((((ulong)(memory.Read32(((uint)(record + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 96)) + 4))))))) >> 32)) != 0 || ((uint)((ulong)(((((ulong)(memory.Read32(((uint)(record + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 96)) + 4)))))))) != 0 || memory.Read32(record + 116) != 0)
            return;
        uint shift = memory.Read32(cpxArenaBase + (UnitShift)), size = memory.Read32(record + 12), length = memory.Read32(record + 112);
        if (memory.Read32(record + 8) != 0 || (size & ((1u << (int)shift) - 1)) != 0 || (size >> (int)shift) != length || length == 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 16)) + 4))))))) == ((ulong)(((ulong)(length))))))
            return;
        ulong physical = ((((ulong)(memory.Read32(((uint)(record + 104))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 104)) + 4))))), next = physical + length;
        int bitmap = FindStaged(Bitmap(next)), state = FindStaged(StateAddress), leaf = FindStaged(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SavedLeaf)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SavedLeaf)) + 4)))))));
        ulong group = (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & 0xffff0000))), descriptor = (((ulong)(group)) == 0) ? ((ulong)(131072u >> (int)shift)) : group;
        int counts = FindStaged(descriptor);
        if (bitmap < 0 || state < 0 || leaf < 0 || counts < 0)
            return;
        uint image = (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)leaf)) * (memory.Read32(cpxArenaBase + (PageBytes)))), slot = memory.Read32(cpxArenaBase + (SavedSlot));
        if (slot == 0 || slot > memory.Read16(image + 48))
            return;
        uint objectAt = image + 64 + (slot - 1) * 256;
        if (!(((ulong)(((((ulong)(memory.Read32(((uint)(objectAt))))) << 32 | ((ulong)(memory.Read32(((uint)(objectAt)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))))))))
            return;
        uint cached = 0, objects = (memory.Read32(cpxArenaBase + 4088));
        for (uint n = 0; n < 8; n++)
            if (memory.Read32(objects + n * 272 + 268) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(objects + n * 272 + 12))))) << 32 | ((ulong)(memory.Read32(((uint)(objects + n * 272 + 12)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))))))))
            {
                cached = objects + n * 272 + 12;
                break;
            }

        if (cached == 0)
            return;
        {
            uint cpxWideAddress = (uint)(hint + 4);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(hint + 12, cached);
        memory.Write32(hint + 16, objectAt);
        memory.Write32(hint + 20, (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)bitmap)) * (memory.Read32(cpxArenaBase + (PageBytes)))));
        {
            uint cpxWideAddress = (uint)(hint + 24);
            ulong cpxWideValue = (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & ~BitmapWindowMask)));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(hint + 32, (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)counts)) * (memory.Read32(cpxArenaBase + (PageBytes)))));
        memory.Write32(hint + 36, (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)state)) * (memory.Read32(cpxArenaBase + (PageBytes)))));
        memory.Write32(hint + 40, 1);
        memory.Write32(hint, 1);
    }

    private bool BeginFastAppend(ulong id, ulong position, uint bytes)
    {
        uint cpxArenaBase = Arena;
        uint h = memory.Read32(cpxArenaBase + (FastPointer));
        if (h == 0 || memory.Read32(h) != 1 || !((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) || memory.Read32(cpxArenaBase + (BufferFreed)) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(h + 4))))) << 32 | ((ulong)(memory.Read32(((uint)(h + 4)) + 4))))))) == ((ulong)(id))))
            return false;
        uint record = memory.Read32(h + 12), shift = memory.Read32(cpxArenaBase + (UnitShift)), unit = 1u << (int)shift;
        if (bytes == 0 || bytes > 65536 || (bytes & (unit - 1)) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4))))))) == ((ulong)(id))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4))))))) == ((ulong)(position))) || ((uint)(((ulong)(position)) >> 32)) != 0 || ((uint)((ulong)(position))) > uint.MaxValue - bytes || (memory.Read32(record + 72) & 4) != 0)
            return false;
        uint count = bytes >> (int)shift, length = memory.Read32(record + 112);
        ulong next = ((((ulong)(memory.Read32(((uint)(record + 104))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 104)) + 4))))) + length;
        ulong windowAt = (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & ~BitmapWindowMask))), cachedWindow = ((((ulong)(memory.Read32(((uint)(h + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(h + 24)) + 4)))));
        if (length > 65536 || count > 65536 - length || (((uint)((ulong)(next))) & 65535) + count > 65536 || !DataUnit(next + (count - 1)))
            return false;
        uint first = BitmapWindowUnits - (((uint)((ulong)(next))) & BitmapWindowMask);
        if (first > count)
            first = count;
        if (!(((ulong)(windowAt)) == ((ulong)(cachedWindow))))
        {
            if ((((uint)((ulong)(next))) & BitmapWindowMask) != 0 || !(((ulong)(windowAt)) == ((ulong)(cachedWindow + BitmapWindowUnits))))
                return false;
            first = 0;
        }

        uint bitmap = memory.Read32(h + 20), bit = ((uint)((ulong)(next))) & BitmapWindowMask, left = first;
        while (left != 0)
        {
            uint offset = bit & 7, take = 8 - offset;
            if (take > left)
                take = left;
            uint mask = ((1u << (int)take) - 1) << (int)(8 - offset - take);
            if ((memory.Read8(bitmap + 64 + (bit >> 3)) & mask) != 0)
                return false;
            bit += take;
            left -= take;
        }

        uint group = memory.Read32(h + 32), state = memory.Read32(h + 36), window = 80 + ((((uint)((ulong)(next))) & 65535) >> (int)BitmapWindowShift) * 4;
        uint secondWindow = window + (first == 0 ? 0u : 4u);
        if (memory.Read32(group + 68) < count || memory.Read32(group + window) < first || first < count && (memory.Read32(cpxArenaBase + (StagedCount)) == 42 || memory.Read32(group + secondWindow) < count - first) || memory.Read32(state + 96) == 0 && memory.Read32(state + 100) < count)
            return false;
        memory.Write32(h + 44, 0);
        memory.Write32(h + 48, first);
        memory.Write32(h + 52, secondWindow);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
            ulong cpxWideValue = next;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (WriteRunUnits), count);
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (Phase), first < count ? 502u : 500u);
        return true;
    }

    private StepResult PrepareCrossWindowAppend()
    {
        uint cpxArenaBase = Arena;
        uint h = memory.Read32(cpxArenaBase + (FastPointer)), first = memory.Read32(h + 48), left = memory.Read32(cpxArenaBase + (WriteRunUnits)) - first, p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(2)) * 4)));
        ulong next = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))) + first;
        if (memory.Read32(cpxArenaBase + (Phase)) == 502)
            return ReadStaged(Bitmap(next), p, 503);
        if (!ControlBitmap(p, next, (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & 0xffff0000)))) || !ReadableGeneration(p))
            return Fail(CpxError.Corrupt);
        for (uint bit = 0; bit < left; bit++)
            if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0)
            {
                InvalidateFastWrite();
                memory.Write32(cpxArenaBase + (Phase), 0);
                return StepResult.Pending;
            }

        memory.Write32(h + 44, OwnMetadata(p, Bitmap(next), 7, (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & 0xffff0000)))));
        memory.Write32(cpxArenaBase + (Phase), 500);
        return StepResult.Pending;
    }

    private StepResult FinishFastAppend()
    {
        uint cpxArenaBase = Arena;
        uint h = memory.Read32(cpxArenaBase + (FastPointer)), bitmap = memory.Read32(h + 20), count = memory.Read32(cpxArenaBase + (WriteRunUnits)), bit = memory.Read32(cpxArenaBase + (NewPhysical + 4)) & BitmapWindowMask, left = count;
        while (left != 0)
        {
            if (left == count - memory.Read32(h + 48) && memory.Read32(h + 44) != 0)
            {
                bitmap = memory.Read32(h + 44);
                bit = 0;
            }

            uint offset = bit & 7, take = 8 - offset;
            if (take > left)
                take = left;
            uint at = bitmap + 64 + (bit >> 3), mask = ((1u << (int)take) - 1) << (int)(8 - offset - take);
            memory.Write8(at, (byte)(memory.Read8(at) | mask));
            bit += take;
            left -= take;
        }

        uint group = memory.Read32(h + 32), state = memory.Read32(h + 36), window = 80 + ((memory.Read32(cpxArenaBase + (NewPhysical + 4)) & 65535) >> (int)BitmapWindowShift) * 4;
        uint firstCount = memory.Read32(h + 44) == 0 ? count : memory.Read32(h + 48);
        memory.Write32(group + 68, memory.Read32(group + 68) - count);
        memory.Write32(group + window, memory.Read32(group + window) - firstCount);
        if (firstCount < count)
        {
            uint second = memory.Read32(h + 52);
            memory.Write32(group + second, memory.Read32(group + second) - (count - firstCount));
            memory.Write32(h + 20, memory.Read32(h + 44));
            {
                uint cpxWideAddress = (uint)(h + 24);
                ulong cpxWideValue = (((ulong)(memory.Read32(cpxArenaBase + (NewPhysical)))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (NewPhysical + 4)) & ~BitmapWindowMask))) + (firstCount == 0 ? 0u : BitmapWindowUnits);
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            memory.Write32(h + 44, 0);
        }

        Scalar64.Subtract(((((ulong)(memory.Read32(((uint)(state + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(state + 96)) + 4))))), ((ulong)(count)), out var free);
        {
            uint cpxWideAddress = (uint)(state + 96);
            ulong cpxWideValue = free;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        uint record = memory.Read32(h + 12), leaf = memory.Read32(h + 16), size = memory.Read32(record + 12) + memory.Read32(cpxArenaBase + (FileBytes)), length = memory.Read32(record + 112) + count;
        if (memory.Read32(h + 40) == 0)
        {
            length = count;
            for (uint n = 0; n < 2; n++)
            {
                uint target = n == 0 ? record : leaf;
                Clear(target + 96, 160);
                Extent(target + 96, default, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))), count, 0);
                memory.Write8(target + 85, 1);
                memory.Write16(target + 88, 0);
                memory.Write16(target + 90, 1);
            }

            memory.Write32(h + 40, 1);
            memory.Write32(h, 1);
        }

        memory.Write32(record + 12, size);
        memory.Write32(record + 20, length);
        memory.Write32(record + 112, length);
        memory.Write32(leaf + 12, size);
        memory.Write32(leaf + 20, length);
        memory.Write32(leaf + 112, length);
        NoteBufferedAllocation(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))), count, true);
        RememberAllocationEnd(((((ulong)(memory.Read32((Arena) + ((NewPhysical))))) << 32 | ((ulong)(memory.Read32((Arena) + ((NewPhysical) + 4)))))) + count);
        memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileBytes)));
        return Done();
    }

    private bool BeginFastInitialWrite(ulong id, ulong position, uint bytes)
    {
        uint cpxArenaBase = Arena;
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || !((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) || memory.Read32(cpxArenaBase + (BufferFreed)) != 0 || !(((ulong)(position)) == 0) || bytes == 0 || bytes > 65536 || (bytes & ((1u << (int)memory.Read32(cpxArenaBase + (UnitShift))) - 1)) != 0 || memory.Read32(cpxArenaBase + (StagedCount)) > 40)
            return false;
        uint record = CachedObject(id);
        if (record == 0 || memory.Read8(record + 84) != 1 || memory.Read8(record + 85) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4))))))) == 0) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 16)) + 4))))))) == 0) || memory.Read16(record + 88) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))))) == 0) || (memory.Read32(record + 72) & 4) != 0)
            return false;
        uint entry = record - 12, slot = memory.Read32(entry + 8);
        ulong search = AllocationSearchStart();
        if (search >= 65536) return false; // The compact initial-write plan owns group zero only.
        int leaf = FindStaged(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4)))))), state = FindStaged(StateAddress);
        if (leaf < 0 || state < 0 || slot == 0)
            return false;
        uint h = memory.Read32(cpxArenaBase + (FastPointer)), objectAt = (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)leaf)) * (memory.Read32(cpxArenaBase + (PageBytes)))) + 64 + (slot - 1) * 256;
        if (!(((ulong)(((((ulong)(memory.Read32(((uint)(objectAt))))) << 32 | ((ulong)(memory.Read32(((uint)(objectAt)) + 4))))))) == ((ulong)(id))))
            return false;
        memory.Write32(h, 0);
        {
            uint cpxWideAddress = (uint)(h + 4);
            ulong cpxWideValue = id;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(h + 12, record);
        memory.Write32(h + 16, objectAt);
        memory.Write32(h + 36, (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)state)) * (memory.Read32(cpxArenaBase + (PageBytes)))));
        memory.Write32(h + 40, 0);
        memory.Write32(h + 44, 0);
        memory.Write32(h + 48, bytes >> (int)memory.Read32(cpxArenaBase + (UnitShift)));
        memory.Write32(h + 56, (uint)search);
        memory.Write32(cpxArenaBase + (Phase), 510);
        return true;
    }

    private StepResult PrepareFastInitialWrite()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(2)) * 4))), descriptor = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), h = memory.Read32(cpxArenaBase + (FastPointer)), shift = memory.Read32(cpxArenaBase + (UnitShift)), count = memory.Read32(cpxArenaBase + (FileBytes)) >> (int)shift;
        uint first = memory.Read32(h + 56);
        if (memory.Read32(cpxArenaBase + (Phase)) == 510)
            return ReadStaged(Bitmap(((ulong)(first))), p, 511);
        if (memory.Read32(cpxArenaBase + (Phase)) == 511)
        {
            if (!ControlBitmap(p, ((ulong)(first)), default) || !ReadableGeneration(p))
                return Fail(CpxError.Corrupt);
            return ReadStaged(((ulong)(131072u >> (int)shift)), descriptor, 512);
        }

        if (!Control(descriptor, 6, ((ulong)(131072u >> (int)shift))) || !ReadableGeneration(descriptor) || memory.Read16(descriptor + 52) != GroupPayloadBytes)
            return Fail(CpxError.Corrupt);
        uint windowMask = BitmapWindowMask, windowUnits = BitmapWindowUnits;
        uint bit = first & windowMask, start = first & ~windowMask;
        while (bit + count <= windowUnits)
        {
            if ((bit & 31) == 0 && memory.Read32(p + 64 + (bit >> 3)) == uint.MaxValue)
            { bit += 32; continue; }
            if (memory.Read8(p + 64 + (bit >> 3)) == 255)
            {
                bit = (bit | 7) + 1;
                continue;
            }

            uint scan = 0;
            for (; scan < count; scan++)
                if ((memory.Read8(p + 64 + ((bit + scan) >> 3)) & (0x80 >> (int)((bit + scan) & 7))) != 0)
                    break;
            if (scan == count)
                break;
            bit += scan + 1;
        }

        uint state = memory.Read32(h + 36), window = 80 + ((first & 65535) >> (int)BitmapWindowShift) * 4;
        if (bit + count > windowUnits || !DataUnit(((ulong)(start + bit + count - 1))) || memory.Read32(descriptor + 68) < count || memory.Read32(descriptor + window) < count || memory.Read32(state + 96) == 0 && memory.Read32(state + 100) < count)
        {
            InvalidateFastWrite();
            memory.Write32(cpxArenaBase + (Phase), 0);
            return StepResult.Pending;
        }

        ulong physical = ((ulong)(start + bit));
        memory.Write32(h + 20, OwnMetadata(p, Bitmap(physical), 7, default));
        {
            uint cpxWideAddress = (uint)(h + 24);
            ulong cpxWideValue = ((ulong)(start));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(h + 32, OwnMetadata(descriptor, ((ulong)(131072u >> (int)shift)), 6, default));
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
            ulong cpxWideValue = physical;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (WriteRunUnits), count);
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (Phase), 500);
        return StepResult.Pending;
    }

    private bool BeginFastMapped(ulong id, ulong position, uint bytes, bool write)
    {
        uint cpxArenaBase = Arena;
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || bytes == 0 || bytes > 65536 || ((uint)(((ulong)(position)) >> 32)) != 0)
            return false;
        uint record = CachedObject(id), shift = memory.Read32(cpxArenaBase + (UnitShift)), unit = 1u << (int)shift;
        if (record == 0 || memory.Read8(record + 84) != 1 || memory.Read8(record + 85) != 1 || memory.Read16(record + 90) != 1 || (memory.Read32(record + 72) & (write ? 4u : 8u)) != 0 || memory.Read32(record + 8) != 0 || ((uint)((ulong)(position))) > memory.Read32(record + 12) || bytes > memory.Read32(record + 12) - ((uint)((ulong)(position))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 96)) + 4))))))) == 0) || memory.Read32(record + 116) != 0)
            return false;
        uint prefix = bytes & ~(unit - 1), logical = ((uint)((ulong)(position))) >> (int)shift, length = memory.Read32(record + 112), take = ((((uint)((ulong)(position))) & (unit - 1)) + bytes + unit - 1) >> (int)shift;
        ulong physical = ((((ulong)(memory.Read32(((uint)(record + 104))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 104)) + 4)))));
        ulong last = physical + (length - 1);
        if (length == 0 || length > 65536 || logical > length || take > length - logical || ((uint)(((ulong)(physical)) >> 32)) != ((uint)(((ulong)(last)) >> 32)) || (((uint)((ulong)(physical))) >> 16) != (((uint)((ulong)(last))) >> 16) || !DataUnit(physical) || !DataUnit(last))
            return false;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
            ulong cpxWideValue = physical + logical;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (DataProgress), 0);
        if ((((uint)((ulong)(position))) & (unit - 1)) != 0 || write && (bytes & (unit - 1)) != 0 || prefix == 0)
            memory.Write32(cpxArenaBase + (Phase), 540);
        else
        {
            memory.Write32(cpxArenaBase + (DataTake), prefix);
            memory.Write32(cpxArenaBase + (Phase), write ? 520u : 530u);
        }

        if (!write) ArmReadCapability(id, physical, memory.Read32(record + 12), length, shift);
        return true;
    }

    private StepResult FastMappedStep(uint phase)
    {
        uint cpxArenaBase = Arena;
        if (phase == 520 || phase == 530)
            return Issue(phase == 520 ? IoKind.Write : IoKind.Read, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))), memory.Read32(cpxArenaBase + (FileBuffer)), memory.Read32(cpxArenaBase + (DataTake)), phase + 1);
        if (phase == 531 && memory.Read32(cpxArenaBase + (DataTake)) != memory.Read32(cpxArenaBase + (FileBytes)))
        {
            memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (DataTake)));
            return Issue(IoKind.Read, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))) + (memory.Read32(cpxArenaBase + (DataTake)) >> (int)memory.Read32(cpxArenaBase + (UnitShift))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)), 532);
        }

        if (phase == 532)
            Copy(memory.Read32(cpxArenaBase + (FileBuffer)) + memory.Read32(cpxArenaBase + (FileValue)), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), memory.Read32(cpxArenaBase + (FileBytes)) - memory.Read32(cpxArenaBase + (FileValue)));
        memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileBytes)));
        return Done();
    }

    private StepResult FastPartialMappedStep(uint phase)
    {
        uint cpxArenaBase = Arena;
        uint progress = memory.Read32(cpxArenaBase + (DataProgress)), unit = 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)), intra = (memory.Read32(cpxArenaBase + (FilePosition + 4)) + progress) & (unit - 1), buffer = memory.Read32(cpxArenaBase + (FileBuffer)) + progress;
        bool write = (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt)) == CpxOperation.WriteMapped;
        ulong physical = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))) + (((memory.Read32(cpxArenaBase + (FilePosition + 4)) & (unit - 1)) + progress) >> (int)memory.Read32(cpxArenaBase + (UnitShift)));
        if (phase == 540)
        {
            uint left = memory.Read32(cpxArenaBase + (FileBytes)) - progress, take = unit - intra;
            if (take > left)
                take = left;
            if (intra == 0 && left >= unit)
            {
                take = left & ~(unit - 1);
                memory.Write32(cpxArenaBase + (DataTake), take);
                return Issue(write ? IoKind.Write : IoKind.Read, physical, buffer, take, 541);
            }

            memory.Write32(cpxArenaBase + (DataTake), take);
            return Issue(IoKind.Read, physical, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 542);
        }

        if (phase == 542)
        {
            if (write)
            {
                Copy((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))) + intra, buffer, memory.Read32(cpxArenaBase + (DataTake)));
                return Issue(IoKind.Write, physical, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 543);
            }

            Copy(buffer, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))) + intra, memory.Read32(cpxArenaBase + (DataTake)));
        }

        progress += memory.Read32(cpxArenaBase + (DataTake));
        memory.Write32(cpxArenaBase + (DataProgress), progress);
        memory.Write32(cpxArenaBase + (FileValue), progress);
        if (progress == memory.Read32(cpxArenaBase + (FileBytes)))
            return Done();
        memory.Write32(cpxArenaBase + (Phase), 540);
        return StepResult.Pending;
    }
}
