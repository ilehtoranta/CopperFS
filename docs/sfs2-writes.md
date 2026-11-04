# SFS2 writes and recovery

`AmigaSmartFileSystem2WriteCore` adds explicit, allocation-free mutation and
recovery of the verified `SFS\2` structure-version-4 format. Classic
`AmigaSmartFileSystemWriteCore` retains its existing API and disk format. The
writers reject the other format before mutation; mounting or writing never
converts a volume. Both use `IAmigaWritableFileSystemPlatform` and caller-owned
scratch buffers. The optional batched path uses `IAmigaBlockRangeFileSystemPlatform`.

This document describes the portable API. The subsequent
[native SFS2 integration](native-sfs2.md) provides wide DOS packets, handle
positions and a separately qualified bridge in the sibling CopperStart checkout.
Existing downloadable handlers have not been replaced by these development builds.

## Supported operations and layout

The SFS2 writer provides `Create` (file/directory), `Write`, `WriteBatched`,
`Resize`, `Truncate`, `Rename`, `Remove`, protection/comment/date updates,
`Recover`, and an optional `ExecuteTrst` transaction. `Request.Position` and
`Resize` sizes are `ulong`; the on-disk size is six-byte big-endian, bounded by
`MaxFileSize = 0xFFFFFFFFFFFF`. A transfer remains limited to `Int32.MaxValue`
bytes and the actual mapped buffer. Growth also requires sufficient space on
the volume. A wider size field does not create sparse files.

Writes are bounded to `MaxVolumeBlocks = 2^31` filesystem blocks: 1 TiB with
512-byte blocks, scaling with block size. This keeps physical block pointers
below the extent owner-tag bit. Larger/newer vendor variants require separate
format verification and are refused by this writer. Object-node allocation is
likewise limited to 31-bit identities. These are format/addressing bounds,
not a claim of native or physical-device qualification at those capacities.

The writer uses the [verified version-4 layout](sfs2-read-only.md): checksum seed
2, 27-byte object headers, 32-bit extent lengths in 16-byte leaf entries and
one low fullness bit in object-node branch pointers. Directory pointers remain
32-bit. Content changes clear the archive bit transactionally; comment relocation
and rename preserve all 27 fixed header bytes, including the full file length.
Names may contain up to 107 Latin-1 bytes and comments up to 79.

The SFS2 mutation routines are separately specialized from the classic writer,
sharing the read-only layout decoder through an internal bridge. This keeps
classic public request/work structures and mutations unchanged. Changes to
common transaction algorithms should be reviewed against both implementations.

Formatting, conversion, recycled-file management and link creation/traversal
are outside this change. `Remove` permanently removes the selected ordinary
file or empty directory; it does not add it to `.recycled`. Existing unsupported
recycled accounting remains explicitly rejected by the independent checker.

## Transactions and recovery

Ordinary operations use the private `CSUN` undo protocol with SFS2 checksums.
Each old block and journal record becomes durable before its target changes.
Flush barriers order journal publication, home writes and the final `TROK`
marker. Failure returns zero reported write bytes and requires recovery before
reads; the next mutation attempts recovery automatically. Failure at the final
barrier can leave the complete new state. Check contents after recovery.

The batched path retains at most 64 KiB per transfer and enforces `MaxTransfer`.
Eligible fragmented overwrites can relocate full blocks into contiguous space.
Retired blocks stay unavailable for allocation or journaling until commit, and
rollback restores the original extent mapping and bitmap. Partial blocks and
unavailable contiguous space use in-place undo. Individual sectors in a range
transfer are not assumed atomic together.

After a dirty `CSUN` transaction, recover with CopperSFS2 before handing the
volume to an original SFS2 handler. Original handlers do not understand this
private undo journal. Clean handoffs preserve the SFS2 format.

`ExecuteTrst` instead stages the entire mutation, including file data, in a
caller-owned buffer. Each distinct changed block requires `BlockBytes + 4`
bytes. Insufficient staging fails before publication. Native `TRST` pages use
the original seven-byte operation header, word alignment and LONGCOMPRESSION.
Only after the complete journal is durable is `TRFA` published; recovery then
rolls forward. The staging buffer must be separate from the four work buffers,
names and input data. It is not limited to the batched path's 64 KiB, but it must
be large enough for every changed block in the whole operation.

The SFS2 recovery decoder accepts both private undo and original native redo
records. A complete record stream is validated before native replay. Damaged
journals remain dirty rather than exposing partial state. Recovery needs a
writable device and intact recovery data. Callers serialize all access to a
volume through mutation/recovery and supply distinct mapped buffers.

Atomicity applies to one API operation. Creating a file and then writing it are
two transactions; interruption can leave the successfully created empty file.

## Example

```csharp
var work = new AmigaSmartFileSystem2WriteCore.Work
{
    Meta = metadata, Data = data, Bitmap = bitmap, Journal = journal
};
if (!AmigaSmartFileSystem2WriteCore.Recover(ref platform, ref work)) return;

var request = new AmigaSmartFileSystem2WriteCore.Request
{
    Node = node, Position = position64, Buffer = input, Length = inputBytes
};
bool ok = AmigaSmartFileSystem2WriteCore.Write(
    ref platform, ref work, ref request, out uint actual);
```

Use `AmigaSmartFileSystemReadOnlyCore.Root(..., out format)` to identify even
a dirty volume. Select the corresponding writer/recovery API explicitly.
`Mount` requires a clean volume and therefore is not the format probe to use
before recovery.

## Validation and reproduction

The independent `CopperSFS.Check` now accepts classic version 3 and SFS2 version
4. File lengths in its `ImageEntry` report are `ulong`. It uses separate
checksum, traversal and allocation code, and checks full file hashes, node/hash
registration, extent ownership, bitmap/free counts and unreachable metadata.
Native formatter bitmap regions outside admin ranges and reserved node sentinels
3..6 are accounted for explicitly; ownership checks remain enabled.

Host tests cover both journal policies across 512-, 1,024-, 2,048- and 4,096-byte
blocks, ordinary mutations, packed directory containers, node-root growth,
admin expansion, comment relocation, extent splitting/pruning and multiple
write batches. Small scenarios enumerate every whole-block pending-version
combination at each write/flush cut and inject transient/persistent I/O errors.
Selected dirty states are interrupted again during recovery. Torn mutation
writes retain 1-, 256- and 511-byte prefixes; recovery must produce a complete
before-or-after manifest or refuse mount. Large batch tests distinguish
exhaustive small persistence spaces from deterministic samples of larger ones.

Sparse host backing additionally exercises scalar, batched and native-redo
writes across the 4 GiB file boundary on a logical volume above 128 GiB. It checks
48-bit length updates, extension/shrinkage of extents longer than 65,535 blocks,
contents, allocation bits, free counts, every mutation cut and repeat recovery.
These sparse tests inspect the affected structures and bytes; they do not run
the whole-volume allocation checker over a 136 GiB logical device.

Original SmartFilesystem 1.279 checks use the independently formatted 16 MiB
image from the read-only milestone, 512-byte blocks, Kickstart 3.1 and an emulated
68020. The new probe verifies CopperSFS2-created files/directories, renamed
objects, contents, comments, protection, dates, resize and deletion. It also
recovers CopperSFS2-generated native redo images before and during home writes.
Conversely, CopperSFS2 recovers a dirty native transaction captured from the
original handler. Resulting images pass the independent checker. This qualifies
the recorded emulator profile, not physical hardware or large native files.

Build the optional reference tool, then capture an original dirty transaction:

```powershell
dotnet build tools/CopperSFS.Sfs2Reference -c Release
dotnet run --no-build --project tools/CopperSFS.Sfs2Reference -c Release -- `
  C:/path/to/SmartFilesystem C:/path/to/kickstart-3.1.rom `
  artifacts/sfs2-write/new-native-redo capture-redo

$env:COPPERSFS_SFS2_REFERENCE_IMAGE = (Resolve-Path `
  artifacts/sfs2-reference/new-run/reference.hdf).Path
$env:COPPERSFS_SFS2_NATIVE_REDO_IMAGE = (Resolve-Path `
  artifacts/sfs2-write/new-native-redo/reference.hdf).Path
$env:COPPERSFS_SFS2_ORIGINAL_HANDLER = 'C:/path/to/SmartFilesystem'
$env:COPPERSHARP_KICKSTART31_ROM = 'C:/path/to/kickstart-3.1.rom'
powershell -NoProfile -File scripts/Test-Reliability.ps1 `
  -OutputDirectory artifacts/sfs2-write/new-validation `
  -ArtifactsPath artifacts/sfs2-write/new-build
```

The base reference image is generated using the [read-only reproduction
command](sfs2-read-only.md). Native inputs are optional and their tests explicitly
skip when unavailable. ROMs, original binaries and generated images are ignored
local artifacts. The `capture-redo` backend persists all preceding writes and
cuts at the first published nonempty `TRFA` marker; it is one reproducible
original transaction, not an exhaustive native crash sweep.

The reliability report records source/binary hashes, the source-change audit,
TRX outcomes and separate SFS2 exhaustive/sampled counts. Native handoff reports
also identify the original handler, ROM, compiler and emulator. Initial failed
setup runs remain alongside accepted reruns. Successful flushes are assumed
durable; arbitrary device reordering, lying flushes, untested sector tears and
real TD64/NSD hardware remain unqualified. No native CopperSFS handler release
is implied by portable-core validation.
