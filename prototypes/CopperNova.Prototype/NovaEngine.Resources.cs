using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The caller supplies aligned, public storage for one packet. Publication transfers
    // ownership to the mount. After completion, ReleasedPacketResource can be freed.
    public const uint PacketResourceBytes = 48;
    public static bool NeedsPacketResource(uint action) => action is 8 or 19 or 22 or 29 || OpensHandle(action);
    public uint ReleasedPacketResource => R(Released);

    private static bool OpensHandle(uint action) => action is 1004 or 1005 or 1006;

    // Reuse part of the old slot arena for bucket heads. Only live records are linked.
    private const uint HandleBuckets = 9216, LockBuckets = 9472, ObjectBuckets = 9728;
    private const uint ResourceLow = 9984, ResourceHigh = 9988, HandleSerial = 9992;
    private const uint IdentityNext = 36, ObjectNext = 40, IsHandle = 44;

    private uint ResourceBucket(uint table, uint key) => Ptr(table) + (((key ^ (key >> 6) ^ (key >> 12)) & 63) << 2);

    private uint FindResourceLink(bool handle, uint key)
    {
        uint link = ResourceBucket(handle ? HandleBuckets : LockBuckets, key);
        for (uint p = m.Read32(link); p != 0; p = m.Read32(link))
        {
            if ((handle ? m.Read32(p) : p >> 2) == key) break;
            link = p + IdentityNext;
        }
        return link;
    }

    private bool HandlePointer(uint token, out uint pointer)
    {
        pointer = token == 0 ? 0 : m.Read32(FindResourceLink(true, token));
        return pointer != 0;
    }

    private bool LockSlot(uint bptr, out uint slot)
    {
        slot = 1;
        if (bptr == 0) return true;
        uint p = m.Read32(FindResourceLink(false, bptr));
        if (p == 0) return false;
        slot = m.Read32(p + 4);
        return true;
    }

    private bool Conflicts(uint slot, bool exclusive, bool includeLocks = false)
    {
        for (uint p = m.Read32(ResourceBucket(ObjectBuckets, slot)); p != 0; p = m.Read32(p + ObjectNext))
        {
            if (m.Read32(p + 4) != slot) continue;
            if (m.Read32(p + IsHandle) != 0 ? exclusive || m.Read32(p + 12) == 2 : includeLocks) return true;
        }
        return false;
    }

    private void PublishResource(uint p, uint slot, uint key, bool handle)
    {
        uint identity = ResourceBucket(handle ? HandleBuckets : LockBuckets, key);
        uint objects = ResourceBucket(ObjectBuckets, slot), count = handle ? ActiveHandles : ActiveLocks;
        m.Write32(p + 4, slot);
        m.Write32(p + IsHandle, handle ? 1u : 0u);
        m.Write32(p + IdentityNext, m.Read32(identity));
        m.Write32(p + ObjectNext, m.Read32(objects));
        m.Write32(identity, p);
        m.Write32(objects, p);
        if (!HasResources || p < m.Read32(Ptr(ResourceLow))) m.Write32(Ptr(ResourceLow), p);
        if (!HasResources || p + PacketResourceBytes > m.Read32(Ptr(ResourceHigh))) m.Write32(Ptr(ResourceHigh), p + PacketResourceBytes);
        W(count, R(count) + 1);
        W(ReservedResource, 0);
    }

    private StepResult ReleasePacketResource(bool handle, uint key)
    {
        if (key == 0) return handle ? Error(211) : Finish(uint.MaxValue);
        uint identity = FindResourceLink(handle, key), p = m.Read32(identity);
        if (p == 0) return Error(211);
        uint objects = ResourceBucket(ObjectBuckets, m.Read32(p + 4));
        while (m.Read32(objects) != p) objects = m.Read32(objects) + ObjectNext;
        m.Write32(objects, m.Read32(p + ObjectNext));
        m.Write32(identity, m.Read32(p + IdentityNext));
        uint count = handle ? ActiveHandles : ActiveLocks;
        W(count, R(count) - 1);
        W(Released, p);
        return Finish(uint.MaxValue);
    }

    private StepResult MakeLock(uint slot)
    {
        uint p = R(ReservedResource);
        if (p == 0) return Error(103);
        Zero(p, PacketResourceBytes);
        m.Write32(p + 8, 0xfffffffe);
        m.Write32(p + 12, R(Port));
        m.Write32(p + 16, R(Volume) >> 2);
        m.Write32(p + 20, 1);
        m.Write32(p + 24, slot);
        PublishResource(p, slot, p >> 2, false);
        return Finish(p >> 2);
    }

    private StepResult OpenHandle(uint slot)
    {
        uint p = R(ReservedResource);
        if (p == 0) return Error(103);
        // Exec can immediately reuse the same address. A separate nonrepeating
        // token keeps closed handles invalid even after an inhibit/remount.
        uint token = m.Read32(Ptr(HandleSerial)) + 1, fh = Arg(0) << 2;
        m.Write32(Ptr(HandleSerial), token);
        m.Write32(p, token);
        m.Write32(p + 8, 0);
        m.Write32(p + 12, R(Action) == 1005 ? 0u : R(Action) == 1006 ? 2u : 1u);
        PublishResource(p, slot, token, true);
        m.Write32(fh + 8, R(Port));
        m.Write32(fh + 36, token);
        return Finish(uint.MaxValue);
    }

    private static bool ResourceOverlaps(uint p, uint bytes, uint other, uint otherBytes) =>
        p < other + otherBytes && other < p + bytes;

    private bool ExternalToResources(uint p, uint bytes)
    {
        uint reserved = R(ReservedResource);
        if (reserved != 0 && ResourceOverlaps(p, bytes, reserved, PacketResourceBytes)) return false;
        if (!HasResources || p >= m.Read32(Ptr(ResourceHigh)) || p + bytes <= m.Read32(Ptr(ResourceLow))) return true;
        for (uint bucket = Ptr(ObjectBuckets); bucket < Ptr(ObjectBuckets + 256); bucket += 4)
            for (uint record = m.Read32(bucket); record != 0; record = m.Read32(record + ObjectNext))
                if (ResourceOverlaps(p, bytes, record, PacketResourceBytes)) return false;
        return true;
    }
}
