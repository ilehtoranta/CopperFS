using Amiga;
using System.Runtime.CompilerServices;

namespace CopperStart.Dos;

#if COPPERFS_NATIVE_FFS_READAHEAD
/// <summary>Bounded per-file FFS stream hints inside the existing object.</summary>
internal static class NativeFfsSequentialRead
{
    // FFS ends its cursor at 99. These file-only words are not shared with
    // directory enumeration or SFS2's wider cursor. No new allocation/ABI.
    private const int Next = 100, Count = 104, Mode = 108, CachedTicks = 112;
    private const int Clock = 116, ProbeTicks = 120, Started = 124, Probes = 128, Stamp = 136;
    private const int Minimum = 144, Maximum = 148;
    private const uint Warmup = 16, DirectPolicy = 4, Disabled = 3;

    internal static bool WantsDirect(APTR file, uint position) =>
        APTR.ReadUInt32(file, Count) == Warmup && APTR.ReadUInt32(file, Next) == position &&
        APTR.ReadUInt32(file, Mode) < 2;

    internal static bool UseDirect(APTR file, uint position) => APTR.ReadUInt32(file, Mode) == 2 &&
        APTR.ReadUInt32(file, Count) == Warmup && APTR.ReadUInt32(file, Next) == position;

    internal static bool NeedsWarmup(APTR state, APTR file) => APTR.ReadUInt32(file, Count) < Warmup &&
        APTR.ReadUInt32(file, Mode) != Disabled && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) != 0;

    internal static void Reset(APTR file)
    {
        APTR.WriteUInt32(file, Count, 0); APTR.WriteUInt32(file, Mode, 0);
        APTR.WriteUInt32(file, CachedTicks, 0); APTR.WriteUInt32(file, ProbeTicks, 0);
        APTR.WriteUInt32(file, Probes, 0);
        APTR.WriteUInt32(file, Minimum, uint.MaxValue); APTR.WriteUInt32(file, Maximum, 0);
    }

    private static uint Now(APTR file)
    {
        TimerDevice.ReadEClock(APTR.FromPointer(APTR.ReadUInt32(file, Clock)), APTR.FromPointer(file.Raw + Stamp));
        return APTR.ReadUInt32(file, Stamp + 4);
    }

    internal static void Record(APTR file, uint position)
    {
        var count = APTR.ReadUInt32(file, Count);
        if (count != 0 && (APTR.ReadUInt32(file, Next) != position || count > Warmup)) { Reset(file); count = 0; }
        if (count < Warmup) count++;
        APTR.WriteUInt32(file, Count, count); APTR.WriteUInt32(file, Next, position + 4096);
    }

    // Single-owner mount execution permits this packet-local payload hint.
    // Metadata and partial-sector paths do not consult it. End restores it
    // before another packet can execute, including partial/error completions.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint Begin(APTR state, APTR file, APTR packet, APTR output, uint length, ulong position)
    {
        var type = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        if (type is not (0x444F5301 or 0x444F5303 or 0x444F5305) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset) != 0 ||
            !Disjoint(output, length, file, 216) || !Disjoint(output, length, state, NativeAmigaFileSystem.StateBytes) ||
            !Disjoint(output, length, packet, 64) || length > uint.MaxValue - output.Raw) return 0;
        if (position > uint.MaxValue || ((uint)position & 511) != 0 || (output.Raw & 3) != 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.FixedHardDiskOffset) == 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) != 512 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset) <= 8)
        { Reset(file); return 0; }
        if (APTR.ReadUInt32(file, Count) > Warmup ||
            APTR.ReadUInt32(file, 56) != APTR.ReadUInt32(file, 4) || APTR.ReadUInt32(file, 84) != type ||
            APTR.ReadUInt32(file, Mode) != Disabled && APTR.ReadUInt32(file, Next) != (uint)position) Reset(file);
        if (APTR.ReadUInt32(file, Count) == 0)
        {
            Reset(file);
            var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset));
            var timer = request.IsNull ? 0u : APTR.ReadUInt32(request, 20);
            if (timer == 0 || APTR.ReadUInt16(APTR.FromPointer(timer), 20) < 36)
            { APTR.WriteUInt32(file, Mode, Disabled); APTR.WriteUInt32(file, Clock, 0); return 1; }
            APTR.WriteUInt32(file, Clock, timer);
        }
        var mode = APTR.ReadUInt32(file, Mode);
        if (APTR.ReadUInt32(file, Count) == Warmup && mode != Disabled)
        {
            if (mode == 0) APTR.WriteUInt32(file, Mode, 1);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset, DirectPolicy);
            if (mode == 2) return 1;
        }
        if (APTR.ReadUInt32(file, Clock) == 0 || mode == Disabled) return 1;
        APTR.WriteUInt32(file, Started, Now(file));
        return 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void End(APTR state, APTR file, uint length, ulong position, int result, DOS.Error error, uint sample)
    {
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset, 0);
        if (result != (int)length || error != DOS.Error.None || (uint)position > uint.MaxValue - length)
        { Reset(file); return; }
        if (sample == 2)
        {
            var ticks = unchecked(Now(file) - APTR.ReadUInt32(file, Started));
            if (APTR.ReadUInt32(file, Mode) == 1)
            {
                var probes = APTR.ReadUInt32(file, Probes) + 1;
                var total = APTR.ReadUInt32(file, ProbeTicks) + ticks;
                APTR.WriteUInt32(file, Probes, probes); APTR.WriteUInt32(file, ProbeTicks, total);
                // Equal 64 KiB epochs include comparable pointer-table work.
                // Reject ambiguous samples and require a 1/128 advantage.
                if (probes == Warmup)
                {
                    var cached = APTR.ReadUInt32(file, CachedTicks);
                    APTR.WriteUInt32(file, Mode, total < cached - cached / 128 ? 2u : Disabled);
                }
            }
            else
            {
                var total = APTR.ReadUInt32(file, CachedTicks);
                if (ticks > uint.MaxValue / Warmup || total > uint.MaxValue - ticks)
                    APTR.WriteUInt32(file, Mode, Disabled);
                else APTR.WriteUInt32(file, CachedTicks, total + ticks);
                var count = APTR.ReadUInt32(file, Count);
                if (count != 0)
                {
                    var minimum = APTR.ReadUInt32(file, Minimum);
                    var maximum = APTR.ReadUInt32(file, Maximum);
                    if (ticks < minimum) minimum = ticks;
                    if (ticks > maximum) maximum = ticks;
                    APTR.WriteUInt32(file, Minimum, minimum); APTR.WriteUInt32(file, Maximum, maximum);
                    // Ignore the initial payload-window fill. Later large
                    // service spikes imply expensive metadata seeks/refills;
                    // keep caching without issuing a direct-read trial.
                    if (count >= 2 && minimum <= uint.MaxValue / 4 && maximum > minimum * 4)
                        APTR.WriteUInt32(file, Mode, Disabled);
                }
            }
        }
        if (APTR.ReadUInt32(file, Mode) != Disabled || APTR.ReadUInt32(file, Count) == 0)
            Record(file, (uint)position);
    }

    private static bool Disjoint(APTR a, uint bytes, APTR b, uint otherBytes) =>
        a.Raw <= b.Raw ? b.Raw - a.Raw >= bytes : a.Raw - b.Raw >= otherBytes;
}
#endif
