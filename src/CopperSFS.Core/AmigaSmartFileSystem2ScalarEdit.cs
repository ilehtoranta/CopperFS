using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    private static bool EditScalarMetadata<T>(ref T p, ref Work w, uint block, uint id, APTR scratch,
        out Sfs2MetadataStore.Handle edit) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!w.RedoStaging || p.Sfs2Context.IsNull || !p.UsesOriginalSfs2DataWrites)
            return EditMetadata(ref p, ref w, block, id, scratch, out edit);
        return Sfs2MetadataStore.EditScalars(ref p, block, id, scratch, out edit) ||
            Fail(ref w, p.ReadUInt32(p.RedoOverlay, 12) != 0 ? DOS.Error.NoFreeStore : DOS.Error.DiskNotValidated);
    }

    private static void Scalar32<T>(ref T p, ref Sfs2MetadataStore.Handle edit, int offset, uint value)
        where T : struct, IAmigaGuestMemory
    {
        if (!edit.TracksChecksum) { p.WriteUInt32(edit.Image, offset, value); return; }
        TrackScalar(ref p, ref edit, offset, 4);
        var at = offset & ~3; var shift = (offset & 3) * 8;
        var first = p.ReadUInt32(edit.Image, at);
        if (shift == 0)
            edit.Checksum = unchecked(edit.Checksum + first - value);
        else
        {
            var second = p.ReadUInt32(edit.Image, at + 4);
            var newFirst = (first & (uint.MaxValue << (32 - shift))) | (value >> shift);
            var newSecond = (second & (uint.MaxValue >> shift)) | (value << (32 - shift));
            edit.Checksum = unchecked(edit.Checksum + first + second - newFirst - newSecond);
        }
        p.WriteUInt32(edit.Image, offset, value);
    }

    private static void Scalar16<T>(ref T p, ref Sfs2MetadataStore.Handle edit, int offset, ushort value)
        where T : struct, IAmigaGuestMemory
    {
        if (!edit.TracksChecksum) { p.WriteUInt16(edit.Image, offset, value); return; }
        TrackScalar(ref p, ref edit, offset, 2);
        var at = offset & ~3; var shift = (2 - (offset & 3)) * 8;
        var old = p.ReadUInt32(edit.Image, at);
        var replacement = (old & ~(0xFFFFu << shift)) | ((uint)value << shift);
        edit.Checksum = unchecked(edit.Checksum + old - replacement);
        p.WriteUInt16(edit.Image, offset, value);
    }

    private static void TrackScalar<T>(ref T p, ref Sfs2MetadataStore.Handle edit, int offset, int bytes)
        where T : struct, IAmigaGuestMemory
    {
        if ((p.ReadUInt32(edit.Arena, 8) & ~Sfs2MetadataStore.Format) != 512) return;
        var changed = p.ReadUInt32(edit.Slot, 16);
        changed |= 1u | (1u << (offset >> 4)) | (1u << ((offset + bytes - 1) >> 4));
        p.WriteUInt32(edit.Slot, 16, changed); // Includes the checksum region.
    }
}
