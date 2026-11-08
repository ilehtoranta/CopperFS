#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
using Amiga;

namespace CopperStart.Dos;

public static partial class NativeAmigaFileSystem
{
    // SFS2 enumerates through NativeSfs2Bridge.Next, so its mount's classic
    // enumeration slots are unused. Word zero is the shared cursor generation.
    private static class Sfs2InputPath
    {
        internal const int Node = 4, Generation = 8, Directory = 12, FirstExtent = 16, SizeHigh = 20, SizeLow = 24, Name = 28;
        // The complete BSTR key fits before the independently versioned mount result.
        internal const int Root = Name + 256, RootGeneration = Root + 4, NodeTree = Root + 8,
            FirstChild = Root + 12, CaseSensitive = Root + 16, ExtentTree = Root + 20,
            InputObject = ExtentTree + 4;
    }

    internal const int Sfs2DirectReadLimitOffset = Sfs2InputPath.InputObject + (int)ObjectBytes;

    private static uint DirectReadLimit(APTR state)
    {
        // Mount geometry is immutable. Qualify the whole partition for CMD_READ
        // and any longword-aligned public buffer before publishing a block limit.
        var mask = APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaskOffset);
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) != 512 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SectorBytesOffset) != 512 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BufferMemoryTypeOffset) != (uint)global::Amiga.Exec.MemoryFlags.Public ||
            mask is not (0xFFFFFFFCu or 0xFFFFFFFEu or uint.MaxValue) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteHighOffset) != 0) return 0;
        var start = APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset);
        var blocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
        if (((ulong)blocks << 9) + start > 0x100000000UL) return 0;
        return APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset) >> 9;
    }

    // SFS2 uses only the preceding lookup fields in the enumeration allocation.
    // The mount initially owns a cleared object. END clears it again after unlinking.
    private static APTR Sfs2InputObject(APTR state)
    {
        if (!NativeSfs2Bridge.IsSfs2(state)) return APTR.Null;
        var cache = APTR.ReadUInt32(state, SmartEnumerationCacheOffset);
        return cache == 0 ? APTR.Null : APTR.FromPointer(cache + Sfs2InputPath.InputObject);
    }

    private static bool StableSfs2Lookup(ref NativeFileSystemPlatform p, APTR state)
    {
        // A removable device may change between packets. A transient logical
        // image can also disappear when its transaction completes or rolls back.
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, SmartTransactionBufferOffset));
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, SmartWriteBufferOffset));
        var queue = APTR.FromPointer(APTR.ReadUInt32(state, SmartWriteCombinerOffset));
        return (APTR.ReadUInt32(state, FixedHardDiskOffset) != 0 || APTR.ReadUInt32(state, RamBlocksOffset) != 0) &&
            p.RedoOverlay.IsNull && (batch.IsNull || APTR.ReadUInt32(batch, 0) == 0) &&
            (dirty.IsNull || AmigaSmartFileSystemWriteBuffer.Count(ref p, dirty) == 0) &&
            AmigaFileSystemWriteCombiner.Count(ref p, queue) == 0;
    }

    private static void ClearSfs2Lookup(APTR entry)
    {
        APTR.WriteUInt32(entry, Sfs2InputPath.Node, 0);
        APTR.WriteUInt32(entry, Sfs2InputPath.Root, 0);
    }

    internal static bool MountSfs2ForInput(ref NativeFileSystemPlatform p, APTR state, bool emptyCache = false)
    {
        var entry = APTR.FromPointer(APTR.ReadUInt32(state, SmartEnumerationCacheOffset));
        if (entry.IsNotNull)
        {
            ClearSfs2Lookup(entry);
            APTR.WriteUInt32(entry, Sfs2DirectReadLimitOffset, 0);
        }
        // Single-owner execution: these reads cannot create a transaction or
        // change the medium while the clean root is being checked.
        var stable = StableSfs2Lookup(ref p, state);
        if (stable && APTR.ReadUInt32(state, FixedHardDiskOffset) != 0)
            p.RetainSfs2MountPrefix(APTR.Null, emptyCache);
        if (!AmigaSmartFileSystemReadOnlyCore.MountSfs2ForLookup(ref p, Metadata(state), emptyCache, out var root, out var extentTree)) return false;
        if (entry.IsNotNull && stable)
        {
            APTR.WriteUInt32(entry, Sfs2DirectReadLimitOffset, DirectReadLimit(state));
            APTR.WriteUInt32(entry, Sfs2InputPath.NodeTree, root.NodeTree);
            APTR.WriteUInt32(entry, Sfs2InputPath.ExtentTree, extentTree);
            APTR.WriteUInt32(entry, Sfs2InputPath.FirstChild, root.FirstChild);
            APTR.WriteUInt32(entry, Sfs2InputPath.CaseSensitive, root.CaseSensitive ? 1u : 0u);
            APTR.WriteUInt32(entry, Sfs2InputPath.RootGeneration, APTR.ReadUInt32(entry, 0));
            APTR.WriteUInt32(entry, Sfs2InputPath.Root, root.RootBlock);
        }
        return true;
    }

    private static uint ResolveSfs2Input(ref NativeFileSystemPlatform p, APTR state, uint directory,
        APTR bstr, byte length, out ulong size, out bool isDirectory, out DOS.Error error)
    {
        var name = APTR.FromPointer(bstr.Raw + 1);
        var entry = APTR.FromPointer(APTR.ReadUInt32(state, SmartEnumerationCacheOffset));
        if (entry.IsNull)
            return NativeSfsExtension.ResolveInformation(state, directory, name, length, out size, out isDirectory, out _, out error);
        if (!StableSfs2Lookup(ref p, state))
        {
            ClearSfs2Lookup(entry);
            return NativeSfsExtension.ResolveInformation(state, directory, name, length, out size, out isDirectory, out _, out error);
        }

        var node = APTR.ReadUInt32(entry, Sfs2InputPath.Node);
        var generation = APTR.ReadUInt32(entry, 0);
        if (node != 0 && APTR.ReadUInt32(entry, Sfs2InputPath.Generation) == generation &&
            APTR.ReadUInt32(entry, Sfs2InputPath.Directory) == directory &&
            SameInputPath(entry, bstr, (uint)length + 1))
        {
            size = ((ulong)APTR.ReadUInt32(entry, Sfs2InputPath.SizeHigh) << 32) | APTR.ReadUInt32(entry, Sfs2InputPath.SizeLow);
            isDirectory = false;
            error = DOS.Error.None;
            return node;
        }

        return ResolveSfs2InputMiss(ref p, state, entry, directory, bstr, length, out size, out isDirectory, out error);
    }

    // Only a cache miss needs the lookup root and a new object description.
    private static uint ResolveSfs2InputMiss(ref NativeFileSystemPlatform p, APTR state, APTR entry, uint directory,
        APTR bstr, byte length, out ulong size, out bool isDirectory, out DOS.Error error)
    {
        var name = APTR.FromPointer(bstr.Raw + 1);
        var generation = APTR.ReadUInt32(entry, 0);
        var root = APTR.ReadUInt32(entry, Sfs2InputPath.Root);
        var lookup = default(AmigaSmartFileSystemReadOnlyCore.LookupRoot);
        if (root != 0 && APTR.ReadUInt32(entry, Sfs2InputPath.RootGeneration) == generation)
            lookup = new AmigaSmartFileSystemReadOnlyCore.LookupRoot(root, APTR.ReadUInt32(entry, Sfs2InputPath.NodeTree),
                APTR.ReadUInt32(entry, Sfs2InputPath.FirstChild), APTR.ReadUInt32(entry, Sfs2InputPath.CaseSensitive) != 0);
        var node = AmigaSmartFileSystemReadOnlyCore.ResolveInformation(ref p, directory, name, length, Metadata(state), lookup,
            out size, out isDirectory, out _, out var firstExtent, out error);
        if (node != 0 && !isDirectory)
        {
            p.Copy(bstr, APTR.FromPointer(entry.Raw + Sfs2InputPath.Name), (uint)length + 1);
            APTR.WriteUInt8(entry, Sfs2InputPath.Name, length);
            APTR.WriteUInt32(entry, Sfs2InputPath.Directory, directory);
            APTR.WriteUInt32(entry, Sfs2InputPath.Generation, generation);
            APTR.WriteUInt32(entry, Sfs2InputPath.SizeHigh, (uint)(size >> 32));
            APTR.WriteUInt32(entry, Sfs2InputPath.SizeLow, (uint)size);
            APTR.WriteUInt32(entry, Sfs2InputPath.FirstExtent, firstExtent);
            APTR.WriteUInt32(entry, Sfs2InputPath.Node, node);
        }
        return node;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ReadNewSfs2Cursor(ref NativeFileSystemPlatform p, APTR state, APTR file, uint node,
        ulong position, APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
    {
        var start = GetSfs2ReadStart(ref p, state, node);
        return NativeFileSystemReadCore.ReadSfs2Cursor(ref p, file, node, position, destination, length, metadata, data, out error, start);
    }

    private static AmigaSmartFileSystemReadOnlyCore.ReadStart GetSfs2ReadStart(ref NativeFileSystemPlatform p, APTR state, uint node)
    {
        var entry = APTR.FromPointer(APTR.ReadUInt32(state, SmartEnumerationCacheOffset));
        if (entry.IsNull || node == 0 || APTR.ReadUInt32(entry, Sfs2InputPath.Node) != node ||
            APTR.ReadUInt32(entry, Sfs2InputPath.Root) == 0) return default;
        var generation = APTR.ReadUInt32(entry, 0);
        if (APTR.ReadUInt32(entry, Sfs2InputPath.Generation) != generation ||
            APTR.ReadUInt32(entry, Sfs2InputPath.RootGeneration) != generation || !StableSfs2Lookup(ref p, state)) return default;
        var firstExtent = APTR.ReadUInt32(entry, Sfs2InputPath.FirstExtent);
        if (firstExtent == 0) return default;
        var size = ((ulong)APTR.ReadUInt32(entry, Sfs2InputPath.SizeHigh) << 32) | APTR.ReadUInt32(entry, Sfs2InputPath.SizeLow);
        return new AmigaSmartFileSystemReadOnlyCore.ReadStart(node, size, firstExtent, APTR.ReadUInt32(entry, Sfs2InputPath.ExtentTree));
    }

    private static bool SameInputPath(APTR entry, APTR bstr, uint bytes)
    {
        // Both BSTRs start on longword boundaries. Their length byte is part
        // of the key, and the final comparison stays inside the guest BSTR.
        var key = APTR.FromPointer(entry.Raw + Sfs2InputPath.Name);
        var offset = 0;
        while (bytes >= 8)
        {
            if (APTR.ReadUInt32(key, offset) != APTR.ReadUInt32(bstr, offset) ||
                APTR.ReadUInt32(key, offset + 4) != APTR.ReadUInt32(bstr, offset + 4)) return false;
            offset += 8;
            bytes -= 8;
        }
        if (bytes >= 4)
        {
            if (APTR.ReadUInt32(key, offset) != APTR.ReadUInt32(bstr, offset)) return false;
            offset += 4;
            bytes -= 4;
        }
        if (bytes >= 2)
        {
            if (APTR.ReadUInt16(key, offset) != APTR.ReadUInt16(bstr, offset)) return false;
            offset += 2;
            bytes -= 2;
        }
        return bytes == 0 || APTR.ReadUInt8(key, offset) == APTR.ReadUInt8(bstr, offset);
    }
}
#endif
