using System.Buffers.Binary;
using System.Text.Json;
using CopperMod.Amiga.Lightweight;
using CopperSFS;

namespace CopperFS.Kickstart.Tests;

// Host-side observation at a guest marker. It performs no emulated operations
// and cannot change timed cycles, allocation, cache placement or guest state.
internal static class OwnedMountMemory
{
    internal static void Capture(LightweightA500Machine machine, string directory)
    {
        var mounts = new List<object>();
        uint Long(uint address)
        {
            if (address < machine.ChipRam.Length - 4) return BinaryPrimitives.ReadUInt32BigEndian(machine.ChipRam.Span.Slice((int)address, 4));
            var offset = address - machine.FastRamBase!.Value;
            return offset < machine.FastRam.Length - 4 ? BinaryPrimitives.ReadUInt32BigEndian(machine.FastRam.Span.Slice((int)offset, 4)) : 0;
        }
        void Scan(ReadOnlyMemory<byte> memory, uint origin, string placement)
        {
            for (var offset = 0; offset <= memory.Length - 2332; offset += 4)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(memory.Span.Slice(offset, 4)) != 0x41465331) continue;
                var state = origin + (uint)offset; var context = Long(state + 2320);
                if (Long(context) != 0x53324D43 || Long(state + 2324) != context) continue;
                var bytes = Long(context + 16); var storage = Long(context + 44); var cache = Long(context + 36);
                if (bytes is < 512 or > 32768 || storage != Long(state + 2328) || Long(cache + 8) != (Sfs2MetadataStore.Format | bytes)) continue;
                mounts.Add(new
                {
                    State = state, Placement = placement, Context = context, BlockBytes = bytes,
                    OwnedAllocationBytes = storage, AdditionalStateBytes = 12,
                    ReplacedUndoAllocationBytes = AmigaSmartFileSystemTransactionBuffer.StorageBytes(bytes, 32),
                    AdditionalPerMountBytes = storage + 12 - AmigaSmartFileSystemTransactionBuffer.StorageBytes(bytes, 32),
                    ContextAndRootBytes = Sfs2MountContext.StorageBytes(bytes),
                    CacheHeaderAndIndexBytes = Long(cache + 20), CacheCapacity = Long(cache + 4),
                    CacheOccupiedSlots = Long(cache), CacheSlotBytes = bytes + Sfs2MetadataStore.SlotBytes,
                    TransactionAllocationBytes = Long(Long(state + 2044) + 8),
                    LegacyUndoBuffer = Long(state + 1756), LegacyDirtyBuffer = Long(state + 2020),
                    AcceptedGeneration = Long(context + 12), AttemptGeneration = Long(context + 48), CommitState = Long(context + 52)
                });
            }
        }
        Scan(machine.ChipRam, 0, "Chip"); Scan(machine.FastRam, machine.FastRamBase!.Value, "Fast");
        File.WriteAllText(Path.Combine(directory, "owned-mount-memory.json"), JsonSerializer.Serialize(mounts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
