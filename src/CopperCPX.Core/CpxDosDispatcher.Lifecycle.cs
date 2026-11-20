namespace CopperCPX;
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint SoftwareProtected = 184, ProtectionKey = 188, LockAccess = 192, LifeResource = 196, LifeMode = 200;
    public bool IsWriteProtected => engine.IsReadOnly || memory.Read32(arena + 512 + (SoftwareProtected)) != 0;

    private bool ConflictsExcept(Wide id, uint except)
    {
        uint cpxArenaBase = arena;
        // Exactly 64 slots; performed in its own dispatcher phase.
        uint pool = cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes;
        for (uint n = 0; n < 32; n++)
        {
            uint h = pool + n * 64 + 8, l = pool + 2048 + n * 48 + 8;
            if (h != except && memory.Read32(h - 8) == 1 && Wide.Equal(G(h), id))
                return true;
            if (l != except && memory.Read32(l - 8) == 1 && Wide.Equal(G(l + 20), id))
                return true;
        }

        return false;
    }

    private StepResult BeginFromLock()
    {
        uint cpxArenaBase = arena;
        uint fh = Arg(0), lockBptr = Arg(1);
        if (fh > 0x3fffffff || !Buffer(fh << 2, 44))
            return Finish(0, 115);
        if (lockBptr == 0 || !LockId(lockBptr, out var id))
            return Finish(0, 211);
        uint p = lockBptr << 2, context = LockContext(p);
        Context(context, context + 8);
        memory.Write32(cpxArenaBase + 512 + (LifeResource), p);
        Put((cpxArenaBase + 512) + Object, id);
        return Start(engine.BeginOperation(CpxOperation.ReadObject, id), 71);
    }

    private StepResult BeginChangeMode()
    {
        uint cpxArenaBase = arena;
        uint kind = Arg(0), bptr = Arg(1), mode = Arg(2), resource;
        if (kind > 1 || bptr > 0x3fffffff || !Buffer(bptr << 2, kind == 0 ? 40u : 44u))
            return Finish(0, 115);
        if (mode is 1004 or 1005)
            mode = 0xfffffffe;
        else if (mode == 1006)
            mode = uint.MaxValue;
        if (mode is not (0xfffffffe or 0xffffffff))
            return Finish(0, 115);
        resource = kind == 0 ? bptr << 2 : memory.Read32((bptr << 2) + 36);
        if (!ValidResource(resource, kind == 0 ? ResourceKind.Lock : ResourceKind.Handle))
            return Finish(0, 211);
        memory.Write32(cpxArenaBase + 512 + (LifeResource), resource);
        memory.Write32(cpxArenaBase + 512 + (LifeMode), mode);
        Put((cpxArenaBase + 512) + Object, G(resource + (kind == 0 ? 20u : 0u)));
        memory.Write32(cpxArenaBase + 512 + (Phase), 74);
        return StepResult.Pending;
    }

    private StepResult WriteProtection()
    {
        uint cpxArenaBase = arena;
        if (Arg(0) != 0)
        {
            if ((engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0))
                return Finish(0, 214);
            // Publish the control state only after the device confirms the barrier.
            return Start(engine.BeginOperation(CpxOperation.Flush), 75);
        }

        if (memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0 && memory.Read32(cpxArenaBase + 512 + (ProtectionKey)) != 0 && memory.Read32(cpxArenaBase + 512 + (ProtectionKey)) != Arg(1))
            return Finish(0, 210);
        memory.Write32(cpxArenaBase + 512 + (SoftwareProtected), 0);
        memory.Write32(cpxArenaBase + 512 + (ProtectionKey), 0);
        return Finish(uint.MaxValue);
    }

    private StepResult LifecycleStep()
    {
        uint cpxArenaBase = arena;
        uint phase = memory.Read32(cpxArenaBase + 512 + (Phase)), p = memory.Read32(cpxArenaBase + 512 + (LifeResource));
        if (phase == 83)
            return FinalComponent();
        if (phase == 84)
            return Start(engine.BeginRename(G((cpxArenaBase + 512) + OldParent), (cpxArenaBase + 1280), memory.Read32(cpxArenaBase + 512 + (OldLength)), G((cpxArenaBase + 512) + ParentId), (cpxArenaBase + 1024) + memory.Read32(cpxArenaBase + 512 + (PathAt)), memory.Read32(cpxArenaBase + 512 + (PathLength)) - memory.Read32(cpxArenaBase + 512 + (PathAt))), 31);
        if (phase == 85)
        {
            uint at = memory.Read32(cpxArenaBase + 512 + (PathAt)), length = memory.Read32(cpxArenaBase + 512 + (PathLength)) - at;
            Wide parent = G((cpxArenaBase + 512) + ParentId);
            if (memory.Read32(cpxArenaBase + 512 + (Action)) == 16)
                return Start(engine.BeginDelete(parent, (cpxArenaBase + 1024) + at, length), 31);
            Put((cpxArenaBase + 512) + OldParent, parent);
            memory.Write32(cpxArenaBase + 512 + (OldLength), length);
            for (uint n = 0; n < length; n++)
                memory.Write8((cpxArenaBase + 1280) + n, memory.Read8((cpxArenaBase + 1024) + at + n));
            memory.Write32(cpxArenaBase + 512 + (Destination), 1);
            return StartPath(Arg(2), Arg(3));
        }

        if (phase == 80)
        {
            // Validation was a separate phase. Retire at most 16 cursors and
            // scan at most 32 lock slots; no nested pool scans.
            ReleaseEnumerations(Arg(0), 0, false);
            memory.Write32(p + 12, 0);
            memory.Write32(p - 8, 0);
            return Finish(uint.MaxValue);
        }

        if (phase == 81)
        {
            if (Conflicts(G((cpxArenaBase + 512) + Object), memory.Read32(cpxArenaBase + 512 + (Action)) == 1006))
                return Finish(0, 202);
            memory.Write32(cpxArenaBase + 512 + (Phase), 82);
            return StepResult.Pending;
        }

        if (phase == 82)
        {
            if (!Reserve(ResourceKind.Handle))
                return Finish(0, 103);
            if (memory.Read32(cpxArenaBase + 512 + (Action)) == 1006)
                return Start(engine.BeginResize(G((cpxArenaBase + 512) + Object), default), 32);
            return OpenHandle();
        }

        if (phase == 74)
        {
            if (memory.Read32(cpxArenaBase + 512 + (LifeMode)) == uint.MaxValue && ConflictsExcept(G((cpxArenaBase + 512) + Object), p))
                return Finish(0, 202);
            memory.Write32(p + (Arg(0) == 0 ? 8u : 16u), memory.Read32(cpxArenaBase + 512 + (LifeMode)));
            return Finish(uint.MaxValue);
        }

        if (phase == 78)
        {
            if (Conflicts(G((cpxArenaBase + 512) + Object), memory.Read32(cpxArenaBase + 512 + (LockAccess)) == uint.MaxValue))
                return Finish(0, 202);
            memory.Write32(cpxArenaBase + 512 + (Phase), 79);
            return StepResult.Pending;
        }

        if (phase == 79)
        {
            Wide id = G((cpxArenaBase + 512) + Object);
            uint locked = memory.Read32(cpxArenaBase + 512 + (Reserved));
            if (locked == 0 && pools.Take(ResourceKind.Lock, out locked, out _) != CpxError.None)
                return Finish(0, 103);
            memory.Write32(cpxArenaBase + 512 + (Reserved), 0);
            memory.Write32(locked + 4, id.Low);
            memory.Write32(locked + 8, memory.Read32(cpxArenaBase + 512 + (LockAccess)));
            memory.Write32(locked + 12, memory.Read32(cpxArenaBase + 512 + (Port)));
            memory.Write32(locked + 16, memory.Read32(cpxArenaBase + 512 + (Volume)) >> 2);
            Put(locked + 20, id);
            uint context = LockContext(locked);
            Put(context, G((cpxArenaBase + 512) + ParentId));
            Put(context + 8, G((cpxArenaBase + 512) + EntryId));
            return Finish(locked >> 2);
        }

        if (phase == 72)
        {
            if (!Reserve(ResourceKind.Handle))
                return Finish(0, 103);
            memory.Write32(cpxArenaBase + 512 + (Phase), 73);
            return StepResult.Pending;
        }

        if (phase == 73)
        {
            uint handle = memory.Read32(cpxArenaBase + 512 + (Reserved));
            Put(handle, G((cpxArenaBase + 512) + Object));
            memory.Write32(handle + 16, memory.Read32(p + 8));
            memory.Write32(handle + 20, 1);
            Put(handle + 24, G((cpxArenaBase + 512) + ParentId));
            Put(handle + 32, G((cpxArenaBase + 512) + EntryId));
            uint fh = Arg(0) << 2;
            memory.Write32(fh + 8, memory.Read32(cpxArenaBase + 512 + (Port)));
            memory.Write32(fh + 36, handle);
            // No fallible work follows the ownership transfer. Failure before
            // this phase leaves both the caller's lock and FileHandle untouched.
            ReleaseEnumerations(Arg(1), 0, false);
            memory.Write32(p + 12, 0);
            memory.Write32(p - 8, 0);
            memory.Write32(cpxArenaBase + 512 + (Reserved), 0);
            return Finish(uint.MaxValue);
        }

        StepResult step = engine.Step();
        if (step is StepResult.Pending or StepResult.NeedIo)
            return step;
        if (step == StepResult.Failed)
            return Finish(0, ToDosError(engine.Error));
        if (phase == 75)
        {
            memory.Write32(cpxArenaBase + 512 + (SoftwareProtected), 1);
            memory.Write32(cpxArenaBase + 512 + (ProtectionKey), Arg(1));
            return Finish(uint.MaxValue);
        }

        if (!engine.ObjectFound)
            return Finish(0, 205);
        if (phase == 70)
        {
            if (memory.Read8(engine.ObjectRecord + 84) != 2)
                return Finish(0, 213);
            Put((cpxArenaBase + 512) + Object, G(engine.ObjectRecord));
            Put((cpxArenaBase + 512) + ParentId, G(engine.ObjectRecord + 40));
            Put((cpxArenaBase + 512) + EntryId, default);
            return MakeLock(G((cpxArenaBase + 512) + Object), 0xfffffffe);
        }

        if (phase == 71)
        {
            if (memory.Read8(engine.ObjectRecord + 84) != 1)
                return Finish(0, 212);
            if ((memory.Read32(engine.ObjectRecord + 72) & 8) != 0)
                return Finish(0, 224);
            memory.Write32(cpxArenaBase + 512 + (Phase), 72);
            return StepResult.Pending;
        }

        return Finish(0, 209);
    }
}
