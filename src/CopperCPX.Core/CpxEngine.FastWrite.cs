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
        uint hint = memory.Read32(Arena + (FastPointer)), record = ObjectRecord;
        if (hint == 0 || !HasBufferedChanges || memory.Read32(Arena + (BufferFreed)) != 0 || memory.Read8(record + 85) != 1 || memory.Read16(record + 90) != 1 || ((uint)(((ulong)(GetWide(record + 96))) >> 32)) != 0 || ((uint)((ulong)(GetWide(record + 96)))) != 0 || memory.Read32(record + 116) != 0)
            return;
        uint shift = memory.Read32(Arena + (UnitShift)), size = memory.Read32(record + 12), length = memory.Read32(record + 112);
        if (memory.Read32(record + 8) != 0 || (size & ((1u << (int)shift) - 1)) != 0 || (size >> (int)shift) != length || length == 0 || !(((ulong)(GetWide(record + 16))) == ((ulong)(((ulong)(length))))))
            return;
        ulong physical = GetWide(record + 104), next = physical + length;
        int bitmap = FindStaged(Bitmap(next)), state = FindStaged(Anchor + 3 * Q), leaf = FindStaged(RW(SavedLeaf));
        ulong group = (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & 0xffff0000))), descriptor = (((ulong)(group)) == 0) ? ((ulong)(131072u >> (int)shift)) : group;
        int counts = FindStaged(descriptor);
        if (bitmap < 0 || state < 0 || leaf < 0 || counts < 0)
            return;
        uint image = BufferedImage((uint)leaf), slot = memory.Read32(Arena + (SavedSlot));
        if (slot == 0 || slot > memory.Read16(image + 48))
            return;
        uint objectAt = image + 64 + (slot - 1) * 256;
        if (!(((ulong)(GetWide(objectAt))) == ((ulong)(RW(ObjectKey)))))
            return;
        uint cached = 0, objects = BufferedObjects;
        for (uint n = 0; n < 8; n++)
            if (memory.Read32(objects + n * 272 + 268) != 0 && (((ulong)(GetWide(objects + n * 272 + 12))) == ((ulong)(RW(ObjectKey)))))
            {
                cached = objects + n * 272 + 12;
                break;
            }

        if (cached == 0)
            return;
        PutWide(hint + 4, RW(ObjectKey));
        memory.Write32(hint + 12, cached);
        memory.Write32(hint + 16, objectAt);
        memory.Write32(hint + 20, BufferedImage((uint)bitmap));
        PutWide(hint + 24, (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & ~4095u))));
        memory.Write32(hint + 32, BufferedImage((uint)counts));
        memory.Write32(hint + 36, BufferedImage((uint)state));
        memory.Write32(hint + 40, 1);
        memory.Write32(hint, 1);
    }

    private bool BeginFastAppend(ulong id, ulong position, uint bytes)
    {
        uint h = memory.Read32(Arena + (FastPointer));
        if (h == 0 || memory.Read32(h) != 1 || !HasBufferedChanges || memory.Read32(Arena + (BufferFreed)) != 0 || !(((ulong)(GetWide(h + 4))) == ((ulong)(id))))
            return false;
        uint record = memory.Read32(h + 12), shift = memory.Read32(Arena + (UnitShift)), unit = 1u << (int)shift;
        if (bytes == 0 || bytes > 65536 || (bytes & (unit - 1)) != 0 || !(((ulong)(GetWide(record))) == ((ulong)(id))) || !(((ulong)(GetWide(record + 8))) == ((ulong)(position))) || ((uint)(((ulong)(position)) >> 32)) != 0 || ((uint)((ulong)(position))) > uint.MaxValue - bytes || (memory.Read32(record + 72) & 4) != 0)
            return false;
        uint count = bytes >> (int)shift, length = memory.Read32(record + 112);
        ulong next = GetWide(record + 104) + length;
        ulong windowAt = (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & ~4095u))), cachedWindow = GetWide(h + 24);
        if (length > 65536 || count > 65536 - length || (((uint)((ulong)(next))) & 65535) + count > 65536 || !DataUnit(next + (count - 1)))
            return false;
        uint first = 4096 - (((uint)((ulong)(next))) & 4095);
        if (first > count)
            first = count;
        if (!(((ulong)(windowAt)) == ((ulong)(cachedWindow))))
        {
            if ((((uint)((ulong)(next))) & 4095) != 0 || !(((ulong)(windowAt)) == ((ulong)(cachedWindow + 4096))))
                return false;
            first = 0;
        }

        uint bitmap = memory.Read32(h + 20), bit = ((uint)((ulong)(next))) & 4095, left = first;
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

        uint group = memory.Read32(h + 32), state = memory.Read32(h + 36), window = 80 + ((((uint)((ulong)(next))) & 65535) >> 12) * 4;
        uint secondWindow = window + (first == 0 ? 0u : 4u);
        if (memory.Read32(group + 68) < count || memory.Read32(group + window) < first || first < count && (memory.Read32(Arena + (StagedCount)) == 42 || memory.Read32(group + secondWindow) < count - first) || memory.Read32(state + 96) == 0 && memory.Read32(state + 100) < count)
            return false;
        memory.Write32(h + 44, 0);
        memory.Write32(h + 48, first);
        memory.Write32(h + 52, secondWindow);
        WW(NewPhysical, next);
        memory.Write32(Arena + (WriteRunUnits), count);
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (Phase), first < count ? 502u : 500u);
        return true;
    }

    private StepResult PrepareCrossWindowAppend()
    {
        uint h = memory.Read32(Arena + (FastPointer)), first = memory.Read32(h + 48), left = memory.Read32(Arena + (WriteRunUnits)) - first, p = Page(2);
        ulong next = RW(NewPhysical) + first;
        if (memory.Read32(Arena + (Phase)) == 502)
            return ReadStaged(Bitmap(next), p, 503);
        if (!ControlBitmap(p, next, (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & 0xffff0000)))) || !ReadableGeneration(p))
            return Fail(CpxError.Corrupt);
        for (uint bit = 0; bit < left; bit++)
            if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0)
            {
                InvalidateFastWrite();
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            }

        memory.Write32(h + 44, OwnMetadata(p, Bitmap(next), 7, (((ulong)(((uint)(((ulong)(next)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(next))) & 0xffff0000)))));
        memory.Write32(Arena + (Phase), 500);
        return StepResult.Pending;
    }

    private StepResult FinishFastAppend()
    {
        uint h = memory.Read32(Arena + (FastPointer)), bitmap = memory.Read32(h + 20), count = memory.Read32(Arena + (WriteRunUnits)), bit = memory.Read32(Arena + (NewPhysical + 4)) & 4095, left = count;
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

        uint group = memory.Read32(h + 32), state = memory.Read32(h + 36), window = 80 + ((memory.Read32(Arena + (NewPhysical + 4)) & 65535) >> 12) * 4;
        uint firstCount = memory.Read32(h + 44) == 0 ? count : memory.Read32(h + 48);
        memory.Write32(group + 68, memory.Read32(group + 68) - count);
        memory.Write32(group + window, memory.Read32(group + window) - firstCount);
        if (firstCount < count)
        {
            uint second = memory.Read32(h + 52);
            memory.Write32(group + second, memory.Read32(group + second) - (count - firstCount));
            memory.Write32(h + 20, memory.Read32(h + 44));
            PutWide(h + 24, (((ulong)(memory.Read32(Arena + (NewPhysical)))) << 32 | ((ulong)(memory.Read32(Arena + (NewPhysical + 4)) & ~4095u))) + (firstCount == 0 ? 0u : 4096u));
            memory.Write32(h + 44, 0);
        }

        Scalar64.Subtract(GetWide(state + 96), ((ulong)(count)), out var free);
        PutWide(state + 96, free);
        uint record = memory.Read32(h + 12), leaf = memory.Read32(h + 16), size = memory.Read32(record + 12) + memory.Read32(Arena + (FileBytes)), length = memory.Read32(record + 112) + count;
        if (memory.Read32(h + 40) == 0)
        {
            length = count;
            for (uint n = 0; n < 2; n++)
            {
                uint target = n == 0 ? record : leaf;
                Clear(target + 96, 160);
                Extent(target + 96, default, RW(NewPhysical), count, 0);
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
        NoteBufferedAllocation(RW(NewPhysical), count, true);
        memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileBytes)));
        return Done();
    }

    private bool BeginFastInitialWrite(ulong id, ulong position, uint bytes)
    {
        if (!BufferedWrites || !HasBufferedChanges || memory.Read32(Arena + (BufferFreed)) != 0 || !(((ulong)(position)) == 0) || bytes == 0 || bytes > 65536 || (bytes & ((1u << (int)memory.Read32(Arena + (UnitShift))) - 1)) != 0 || memory.Read32(Arena + (StagedCount)) > 40)
            return false;
        uint record = CachedObject(id);
        if (record == 0 || memory.Read8(record + 84) != 1 || memory.Read8(record + 85) != 0 || !(((ulong)(GetWide(record + 8))) == 0) || !(((ulong)(GetWide(record + 16))) == 0) || memory.Read16(record + 88) != 0 || !(((ulong)(GetWide(record + 24))) == 0) || (memory.Read32(record + 72) & 4) != 0)
            return false;
        uint entry = record - 12, slot = memory.Read32(entry + 8);
        int leaf = FindStaged(GetWide(entry)), state = FindStaged(Anchor + 3 * Q);
        if (leaf < 0 || state < 0 || slot == 0)
            return false;
        uint h = memory.Read32(Arena + (FastPointer)), objectAt = BufferedImage((uint)leaf) + 64 + (slot - 1) * 256;
        if (!(((ulong)(GetWide(objectAt))) == ((ulong)(id))))
            return false;
        memory.Write32(h, 0);
        PutWide(h + 4, id);
        memory.Write32(h + 12, record);
        memory.Write32(h + 16, objectAt);
        memory.Write32(h + 36, BufferedImage((uint)state));
        memory.Write32(h + 40, 0);
        memory.Write32(h + 44, 0);
        memory.Write32(h + 48, bytes >> (int)memory.Read32(Arena + (UnitShift)));
        memory.Write32(Arena + (Phase), 510);
        return true;
    }

    private StepResult PrepareFastInitialWrite()
    {
        uint first = memory.Read32(Arena + (GeometryFirst)), p = Page(2), descriptor = Page(3), h = memory.Read32(Arena + (FastPointer)), shift = memory.Read32(Arena + (UnitShift)), count = memory.Read32(Arena + (FileBytes)) >> (int)shift;
        if (memory.Read32(Arena + (Phase)) == 510)
            return ReadStaged(Bitmap(((ulong)(first))), p, 511);
        if (memory.Read32(Arena + (Phase)) == 511)
        {
            if (!ControlBitmap(p, ((ulong)(first)), default) || !ReadableGeneration(p))
                return Fail(CpxError.Corrupt);
            return ReadStaged(((ulong)(131072u >> (int)shift)), descriptor, 512);
        }

        if (!Control(descriptor, 6, ((ulong)(131072u >> (int)shift))) || !ReadableGeneration(descriptor) || memory.Read16(descriptor + 52) != 80)
            return Fail(CpxError.Corrupt);
        uint bit = first & 4095, start = first & ~4095u;
        while (bit + count <= 4096)
        {
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

        uint state = memory.Read32(h + 36), window = 80 + ((first & 65535) >> 12) * 4;
        if (bit + count > 4096 || !DataUnit(((ulong)(start + bit + count - 1))) || memory.Read32(descriptor + 68) < count || memory.Read32(descriptor + window) < count || memory.Read32(state + 96) == 0 && memory.Read32(state + 100) < count)
        {
            InvalidateFastWrite();
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        ulong physical = ((ulong)(start + bit));
        memory.Write32(h + 20, OwnMetadata(p, Bitmap(physical), 7, default));
        PutWide(h + 24, ((ulong)(start)));
        memory.Write32(h + 32, OwnMetadata(descriptor, ((ulong)(131072u >> (int)shift)), 6, default));
        WW(NewPhysical, physical);
        memory.Write32(Arena + (WriteRunUnits), count);
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (Phase), 500);
        return StepResult.Pending;
    }

    private bool BeginFastMapped(ulong id, ulong position, uint bytes, bool write)
    {
        if (!BufferedWrites || bytes == 0 || bytes > 65536 || ((uint)(((ulong)(position)) >> 32)) != 0)
            return false;
        uint record = CachedObject(id), shift = memory.Read32(Arena + (UnitShift)), unit = 1u << (int)shift;
        if (record == 0 || memory.Read8(record + 84) != 1 || memory.Read8(record + 85) != 1 || memory.Read16(record + 90) != 1 || (memory.Read32(record + 72) & (write ? 4u : 8u)) != 0 || memory.Read32(record + 8) != 0 || ((uint)((ulong)(position))) > memory.Read32(record + 12) || bytes > memory.Read32(record + 12) - ((uint)((ulong)(position))) || !(((ulong)(GetWide(record + 96))) == 0) || memory.Read32(record + 116) != 0)
            return false;
        uint prefix = bytes & ~(unit - 1), logical = ((uint)((ulong)(position))) >> (int)shift, length = memory.Read32(record + 112), take = ((((uint)((ulong)(position))) & (unit - 1)) + bytes + unit - 1) >> (int)shift;
        ulong physical = GetWide(record + 104);
        ulong last = physical + (length - 1);
        if (length == 0 || length > 65536 || logical > length || take > length - logical || ((uint)(((ulong)(physical)) >> 32)) != ((uint)(((ulong)(last)) >> 32)) || (((uint)((ulong)(physical))) >> 16) != (((uint)((ulong)(last))) >> 16) || !DataUnit(physical) || !DataUnit(last))
            return false;
        WW(NewPhysical, physical + logical);
        memory.Write32(Arena + (DataProgress), 0);
        if ((((uint)((ulong)(position))) & (unit - 1)) != 0 || write && (bytes & (unit - 1)) != 0 || prefix == 0)
            memory.Write32(Arena + (Phase), 540);
        else
        {
            memory.Write32(Arena + (DataTake), prefix);
            memory.Write32(Arena + (Phase), write ? 520u : 530u);
        }

        return true;
    }

    private StepResult FastMappedStep(uint phase)
    {
        if (phase == 520 || phase == 530)
            return Issue(phase == 520 ? IoKind.Write : IoKind.Read, RW(NewPhysical), memory.Read32(Arena + (FileBuffer)), memory.Read32(Arena + (DataTake)), phase + 1);
        if (phase == 531 && memory.Read32(Arena + (DataTake)) != memory.Read32(Arena + (FileBytes)))
        {
            memory.Write32(Arena + (FileValue), memory.Read32(Arena + (DataTake)));
            return Issue(IoKind.Read, RW(NewPhysical) + (memory.Read32(Arena + (DataTake)) >> (int)memory.Read32(Arena + (UnitShift))), Page(4), 1u << (int)memory.Read32(Arena + (UnitShift)), 532);
        }

        if (phase == 532)
            Copy(memory.Read32(Arena + (FileBuffer)) + memory.Read32(Arena + (FileValue)), Page(4), memory.Read32(Arena + (FileBytes)) - memory.Read32(Arena + (FileValue)));
        memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileBytes)));
        return Done();
    }

    private StepResult FastPartialMappedStep(uint phase)
    {
        uint progress = memory.Read32(Arena + (DataProgress)), unit = 1u << (int)memory.Read32(Arena + (UnitShift)), intra = (memory.Read32(Arena + (FilePosition + 4)) + progress) & (unit - 1), buffer = memory.Read32(Arena + (FileBuffer)) + progress;
        bool write = (CpxOperation)memory.Read32(Arena + (OperationAt)) == CpxOperation.WriteMapped;
        ulong physical = RW(NewPhysical) + (((memory.Read32(Arena + (FilePosition + 4)) & (unit - 1)) + progress) >> (int)memory.Read32(Arena + (UnitShift)));
        if (phase == 540)
        {
            uint left = memory.Read32(Arena + (FileBytes)) - progress, take = unit - intra;
            if (take > left)
                take = left;
            if (intra == 0 && left >= unit)
            {
                take = left & ~(unit - 1);
                memory.Write32(Arena + (DataTake), take);
                return Issue(write ? IoKind.Write : IoKind.Read, physical, buffer, take, 541);
            }

            memory.Write32(Arena + (DataTake), take);
            return Issue(IoKind.Read, physical, Page(4), unit, 542);
        }

        if (phase == 542)
        {
            if (write)
            {
                Copy(Page(4) + intra, buffer, memory.Read32(Arena + (DataTake)));
                return Issue(IoKind.Write, physical, Page(4), unit, 543);
            }

            Copy(buffer, Page(4) + intra, memory.Read32(Arena + (DataTake)));
        }

        progress += memory.Read32(Arena + (DataTake));
        memory.Write32(Arena + (DataProgress), progress);
        memory.Write32(Arena + (FileValue), progress);
        if (progress == memory.Read32(Arena + (FileBytes)))
            return Done();
        memory.Write32(Arena + (Phase), 540);
        return StepResult.Pending;
    }
}
