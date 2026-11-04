using Amiga;

namespace CopperStart.Dos;

public interface INativeRecordLockPlatform : IAmigaGuestMemory
{
    APTR AllocateRecord(uint bytes);
    void FreeRecord(APTR record, uint bytes);
    bool StartRecordTimer(APTR state, uint ticks, out APTR timer);
    void StopRecordTimer(APTR timer, bool completed);
    void ReplyRecordPacket(APTR state, APTR record, int result, DOS.Error error);
}

/// <summary>Handler-owned cooperative range locks. No managed allocations or polling.</summary>
public static class NativeRecordLockCore
{
    public const int LocksOffset = 2312, WaitersOffset = 2316;
    public const uint RecordBytes = 56;
    public const int PacketOffset = 32, TimerOffset = 36, ReplyPortOffset = 40, MessageOffset = 44;
    private const int Owner = 4, Node = 8, Mode = 12, Offset = 16, Length = 24, MagicOffset = 48, StateOffset = 52;
    private const uint Magic = 0x524C3634;
    private static APTR Ptr<T>(ref T p, APTR a, int o) where T : struct, INativeRecordLockPlatform => APTR.FromPointer(p.ReadUInt32(a, o));
    private static ulong Quad<T>(ref T p, APTR a, int o) where T : struct, INativeRecordLockPlatform =>
        ((ulong)p.ReadUInt32(a, o) << 32) | p.ReadUInt32(a, o + 4);
    private static void Quad<T>(ref T p, APTR a, int o, ulong v) where T : struct, INativeRecordLockPlatform
    { p.WriteUInt32(a, o, (uint)(v >> 32)); p.WriteUInt32(a, o + 4, (uint)v); }
    private static bool Valid<T>(ref T p, APTR state, APTR r) where T : struct, INativeRecordLockPlatform =>
        p.IsMapped(r, RecordBytes) && p.ReadUInt32(r, MagicOffset) == Magic && p.ReadUInt32(r, StateOffset) == state.Raw;
    private static bool Overlap(ulong a, ulong al, ulong b, ulong bl) =>
        al != 0 && bl != 0 && (a <= b ? b - a < al : a - b < bl);

    private static bool Conflict<T>(ref T p, APTR state, uint owner, uint node, ulong offset, ulong length, uint mode)
        where T : struct, INativeRecordLockPlatform
    {
        var r = Ptr(ref p, state, LocksOffset);
        while (r.IsNotNull)
        {
            if (!Valid(ref p, state, r)) return true;
            if (p.ReadUInt32(r, Owner) != owner && p.ReadUInt32(r, Node) == node &&
                ((p.ReadUInt32(r, Mode) & mode & 2) == 0) && Overlap(offset, length, Quad(ref p, r, Offset), Quad(ref p, r, Length))) return true;
            r = Ptr(ref p, r, 0);
        }
        return false;
    }

    public static int Lock<T>(ref T p, APTR state, APTR file, uint node, ulong offset, ulong length,
        uint mode, uint timeout, APTR packet, out DOS.Error error) where T : struct, INativeRecordLockPlatform
    {
        error = DOS.Error.None;
        if (mode > 3) { error = DOS.Error.BadNumber; return 0; }
        var conflict = Conflict(ref p, state, file.Raw, node, offset, length, mode);
        if (conflict && ((mode & 1) != 0 || timeout == 0))
        { error = (mode & 1) != 0 ? DOS.Error.LockCollision : DOS.Error.LockTimeout; return 0; }
        var r = p.AllocateRecord(RecordBytes);
        if (r.IsNull) { error = DOS.Error.NoFreeStore; return 0; }
        for (var i = 0; i < RecordBytes; i += 4) p.WriteUInt32(r, i, 0);
        p.WriteUInt32(r, MagicOffset, Magic); p.WriteUInt32(r, StateOffset, state.Raw);
        p.WriteUInt32(r, Owner, file.Raw); p.WriteUInt32(r, Node, node); p.WriteUInt32(r, Mode, mode);
        Quad(ref p, r, Offset, offset); Quad(ref p, r, Length, length);
        if (!conflict)
        {
            p.WriteUInt32(r, 0, p.ReadUInt32(state, NativeFileSystemStateCodec.RecordLocksOffset)); p.WriteUInt32(state, NativeFileSystemStateCodec.RecordLocksOffset, r.Raw);
            return -1;
        }
        if (!p.StartRecordTimer(state, timeout, out var timer))
        { p.FreeRecord(r, RecordBytes); error = DOS.Error.NoFreeStore; return 0; }
        p.WriteUInt32(r, PacketOffset, packet.Raw); p.WriteUInt32(r, TimerOffset, timer.Raw);
        p.WriteUInt32(r, ReplyPortOffset, p.ReadUInt32(packet, NativeDosPacketFieldCodec.PortOffset));
        p.WriteUInt32(r, MessageOffset, p.ReadUInt32(packet, NativeDosPacketFieldCodec.LinkOffset));
        var link = APTR.FromPointer(state.Raw + WaitersOffset);
        while (Ptr(ref p, link, 0).IsNotNull) link = Ptr(ref p, link, 0);
        p.WriteUInt32(link, 0, r.Raw);
        return 0; // the handler retains this packet until grant, timeout or close
    }

    public static bool IsPending<T>(ref T p, APTR state, APTR packet) where T : struct, INativeRecordLockPlatform
    {
        var r = Ptr(ref p, state, WaitersOffset);
        while (r.IsNotNull)
        {
            if (!Valid(ref p, state, r)) return false;
            if (p.ReadUInt32(r, PacketOffset) == packet.Raw) return true;
            r = Ptr(ref p, r, 0);
        }
        return false;
    }

    public static int Unlock<T>(ref T p, APTR state, APTR file, ulong offset, ulong length, out DOS.Error error)
        where T : struct, INativeRecordLockPlatform
    {
        error = DOS.Error.RecordNotLocked;
        var link = APTR.FromPointer(state.Raw + LocksOffset);
        var r = Ptr(ref p, link, 0);
        while (r.IsNotNull)
        {
            if (!Valid(ref p, state, r)) return 0;
            if (p.ReadUInt32(r, Owner) == file.Raw && Quad(ref p, r, Offset) == offset && Quad(ref p, r, Length) == length)
            {
                p.WriteUInt32(link, 0, p.ReadUInt32(r, 0)); p.FreeRecord(r, RecordBytes);
                Service(ref p, state); error = DOS.Error.None; return -1;
            }
            link = r; r = Ptr(ref p, r, 0);
        }
        return 0;
    }

    private static void Service<T>(ref T p, APTR state) where T : struct, INativeRecordLockPlatform
    {
        var link = APTR.FromPointer(state.Raw + WaitersOffset);
        var r = Ptr(ref p, link, 0);
        while (r.IsNotNull)
        {
            if (!Valid(ref p, state, r)) return;
            var next = Ptr(ref p, r, 0);
            if (!Conflict(ref p, state, p.ReadUInt32(r, Owner), p.ReadUInt32(r, Node), Quad(ref p, r, Offset), Quad(ref p, r, Length), p.ReadUInt32(r, Mode)))
            {
                p.WriteUInt32(link, 0, next.Raw);
                p.StopRecordTimer(Ptr(ref p, r, TimerOffset), false);
                p.WriteUInt32(r, TimerOffset, 0);
                p.WriteUInt32(r, 0, p.ReadUInt32(state, NativeFileSystemStateCodec.RecordLocksOffset)); p.WriteUInt32(state, NativeFileSystemStateCodec.RecordLocksOffset, r.Raw);
                p.ReplyRecordPacket(state, r, -1, DOS.Error.None);
                p.WriteUInt32(r, PacketOffset, 0); p.WriteUInt32(r, ReplyPortOffset, 0); p.WriteUInt32(r, MessageOffset, 0);
            }
            else link = r;
            r = next;
        }
    }

    public static bool TimerReply<T>(ref T p, APTR state, APTR timer) where T : struct, INativeRecordLockPlatform
    {
        var link = APTR.FromPointer(state.Raw + WaitersOffset);
        var r = Ptr(ref p, link, 0);
        while (r.IsNotNull)
        {
            if (!Valid(ref p, state, r)) return false;
            if (p.ReadUInt32(r, TimerOffset) == timer.Raw)
            {
                p.WriteUInt32(link, 0, p.ReadUInt32(r, 0)); p.StopRecordTimer(timer, true);
                p.ReplyRecordPacket(state, r, 0, DOS.Error.LockTimeout); p.FreeRecord(r, RecordBytes);
                return true;
            }
            link = r; r = Ptr(ref p, r, 0);
        }
        return false;
    }

    public static void Close<T>(ref T p, APTR state, APTR file) where T : struct, INativeRecordLockPlatform
    {
        Release(ref p, state, LocksOffset, file, false);
        Release(ref p, state, WaitersOffset, file, false);
        Service(ref p, state);
    }

    public static void Destroy<T>(ref T p, APTR state) where T : struct, INativeRecordLockPlatform
    {
        Release(ref p, state, WaitersOffset, APTR.Null, true);
        Release(ref p, state, LocksOffset, APTR.Null, true);
    }

    private static void Release<T>(ref T p, APTR state, int head, APTR owner, bool all) where T : struct, INativeRecordLockPlatform
    {
        var link = APTR.FromPointer(state.Raw + (uint)head);
        var r = Ptr(ref p, link, 0);
        while (r.IsNotNull)
        {
            if (!Valid(ref p, state, r)) return;
            var next = Ptr(ref p, r, 0);
            if (all || p.ReadUInt32(r, Owner) == owner.Raw)
            {
                p.WriteUInt32(link, 0, next.Raw);
                if (head == WaitersOffset)
                {
                    p.StopRecordTimer(Ptr(ref p, r, TimerOffset), false);
                    p.ReplyRecordPacket(state, r, 0, all ? DOS.Error.NoDisk : DOS.Error.InvalidLock);
                }
                p.FreeRecord(r, RecordBytes);
            }
            else link = r;
            r = next;
        }
    }
}

internal struct NativeRecordLockPlatform : INativeRecordLockPlatform
{
    public bool IsMapped(APTR a, uint bytes) => new NativeFileSystemPlatform(APTR.Null).IsMapped(a, bytes);
    public byte ReadUInt8(APTR a, int o = 0) => APTR.ReadUInt8(a, o);
    public ushort ReadUInt16(APTR a, int o = 0) => APTR.ReadUInt16(a, o);
    public uint ReadUInt32(APTR a, int o = 0) => APTR.ReadUInt32(a, o);
    public void WriteUInt8(APTR a, int o, byte v) => APTR.WriteUInt8(a, o, v);
    public void WriteUInt16(APTR a, int o, ushort v) => APTR.WriteUInt16(a, o, v);
    public void WriteUInt32(APTR a, int o, uint v) => APTR.WriteUInt32(a, o, v);
    public void Clear(APTR a, uint bytes)
    { for (uint i = 0; i < bytes; i++) APTR.WriteUInt8(a, (int)i, 0); }
    public void Copy(APTR source, APTR destination, uint bytes)
    { global::Amiga.Exec.CopyMem(source, destination, bytes); }
    public APTR AllocateRecord(uint bytes) => global::Amiga.Exec.AllocMem(bytes, global::Amiga.Exec.MemoryFlags.Public);
    public void FreeRecord(APTR r, uint bytes) => global::Amiga.Exec.FreeMem(r, bytes);
    public bool StartRecordTimer(APTR state, uint ticks, out APTR timer)
    {
        timer = global::Amiga.Exec.CreateIORequest(APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset)), TimerRequest.Size);
        if (timer.IsNull) return false;
        if (global::Amiga.Exec.OpenDevice(CString.FromPointer(CString.ToUInt32("timer.device")), (uint)TimerUnit.MicroHz, timer, 0) != 0)
        { global::Amiga.Exec.DeleteIORequest(timer); timer = APTR.Null; return false; }
        APTR.WriteUInt16(timer, NativeExecFieldCodec.IoCommandOffset, (ushort)TimerCommand.AddRequest);
        APTR.WriteUInt8(timer, NativeExecFieldCodec.IoFlagsOffset, 0);
        APTR.WriteUInt32(timer, NativeTimerRequestFieldCodec.SecondsOffset, ticks / 50);
        APTR.WriteUInt32(timer, NativeTimerRequestFieldCodec.MicrosecondsOffset, ticks % 50 * 20000);
        global::Amiga.Exec.SendIO(timer);
        return true;
    }
    public void StopRecordTimer(APTR timer, bool completed)
    {
        if (!completed) { global::Amiga.Exec.AbortIO(timer); global::Amiga.Exec.WaitIO(timer); }
        global::Amiga.Exec.CloseDevice(timer); global::Amiga.Exec.DeleteIORequest(timer);
    }
    public void ReplyRecordPacket(APTR state, APTR record, int result, DOS.Error error)
    {
        var packet = APTR.FromPointer(APTR.ReadUInt32(record, NativeRecordLockFieldCodec.PacketOffset));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.PortOffset, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset));
        global::Amiga.Exec.PutMsg(APTR.FromPointer(APTR.ReadUInt32(record, NativeRecordLockFieldCodec.ReplyPortOffset)),
            APTR.FromPointer(APTR.ReadUInt32(record, NativeRecordLockFieldCodec.MessageOffset)));
    }
}
