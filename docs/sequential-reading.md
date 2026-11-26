# Sequential reads

The frozen CopperSFS2 sequential-read baseline covers CSUN and TRST at all four Kickstart read sizes under both cache policies. [Baseline measurements and handler hashes](kickstart-sfs2-csun-4k-trst-64k-2026-11-08.md)

The [cached CSUN first-open profile](kickstart-sfs2-csun-first-open-2026-11-08.md) times the open after remount and compares cold and reopened sessions at all four read sizes.

The [three-trial CSUN reopen and bypass comparison](kickstart-sfs2-csun-reopen-bypass-2026-11-08.md) times both regressions and includes the four-size A/B against original SFS.

The [cached CSUN session profile](kickstart-sfs2-csun-direct-read-2026-11-08.md) measures device-transfer setup during a 1 MiB read after reopen and compares all four Kickstart read sizes.

The [SFS2 file reopen profile](kickstart-sfs2-trst-reopen-object-2026-11-08.md) times the reopened handle separately from the read loop and measures the cost of the native file record.

The [cached TRST 64 KiB reopen profile](kickstart-sfs2-trst-reopen-read-2026-11-08.md) measures the first read from a reopened file and compares all four packet sizes with original SFS.

The [FFS HDD gap closure](kickstart-ffs-hdd-gap-closure-2026-11-08.md) records the final FFS IDE comparison against original FastFileSystem.

FFS and the SFS/SFS2 read-only traversal API provide `Read` overloads taking a
caller-owned `ReadCursor` by reference. Existing overloads remain available and
start with a fresh cursor. Neither cursor retains a pointer into shared scratch.

FFS retains the current pointer-table key, validated successor and ordinal, file size, and a validated
contiguous data run. A following packet within that run needs no header or table
read. Advancing to another table resumes from the retained table instead of
walking from the file header or reloading the preceding table. Each successor
still passes the normal extension checks. Runs stop at fragmentation, invalid pointers and
table boundaries. Invalid following metadata does not discard an already read
prefix. OFS retains its existing sector validation and copy path.

The SFS2 cursor retains the 64-bit file size, extent tree, first/current extent,
logical extent start, length, successor and predecessor. Forward reads advance
from the current extent; backward seeks before it restart from the first extent.
Extent identity, checksum, range and backlink checks still apply when entering
an extent. The main classic SFS reader keeps its existing implementation.

Both readers send aligned whole-block runs to `ReadDataBlocks`, bounded by the
current validated run or extent. Partial blocks and unaligned destinations use
the scratch/copy path. Destination ranges overlapping scratch use the previous
read implementation to retain its aliasing behavior. The platform still owns
DMA suitability, bounce buffering, MaxTransfer splitting, TD64 offsets and
visibility of pending writes. This does not introduce asynchronous read-ahead.

The native FFS/SFS2 platform preserves synchronous read-ahead for transfers up
to 4 KiB when the existing cache window is larger than the request. Larger runs
use direct batching. This avoids one device request per small aligned packet.
No cache allocation or handle growth is required. Logical write/redo overlays
keep their precedence; a failed speculative refill retries only the requested
suffix through the normal bounded direct-transfer path.

FFS requests larger than 4 KiB pass a read-ahead suppression hint through short
fragments at pointer-table boundaries, including EOF clipping. Platforms without
read-ahead policy explicitly forward the hint overload to their original
transfer method, preserving mutable struct state without boxing. Metadata misses and initial format probes retain only their
requested blocks in the existing metadata LRU, preserving the payload window.

`copperhdf.device` mounts may explicitly bypass the payload read-ahead window
for whole-block FFS/SFS2 transfers. Set the existing DosEnvec `de_Control` word
to `NativeAmigaFileSystem.CopperHdfPayloadBypassControl` (`0x43504301`, decimal
`1129333505`; `Control = 1129333505` in a mount entry). Omitted control, unknown
values, older environment vectors and all other device names retain normal
caching. This is a per-mount device policy; it does not detect emulation.
`de_Control` is the existing handler-defined control word in the
[Amiga DOS environment ABI](https://amigadev.elowar.com/read/ADCD_2.1/Includes_and_Autodocs_2._guide/node006A.html).

Partial-sector and unaligned reads retain their existing cached paths. Metadata
LRU/cache lookups, cursor state, pending write/redo/transaction overlays and
write ordering are unchanged. Direct payload transfers still enforce DMA masks,
bounce buffering, MaxTransfer, TD64 and completed-prefix errors. The policy uses
the previously spare word at native state offset 1644, without growing state,
handles or buffers. It is selected once when opening the mount and survives
inhibit/remount operations.

The storage matrix can explicitly select `ScsiPayloadBypassControl`
(`0x43504302`) for diagnostic `scsi.device` mounts. This policy keeps read-ahead
for 512-byte single-sector packets, while larger whole-block requests bypass
the payload window. Ordinary scsi.device mounts remain cached. The CopperHDF
control retains its existing whole-block bypass behavior. Both controls keep
metadata, partial-sector reads and logical overlays on their existing paths.

Pass `-PayloadCachePolicy Cached` (the default) or `-PayloadCachePolicy Bypass`
to `tools/Benchmark-KickstartSequentialReads.ps1`. Both use identical handler and
guest-probe images, with different mount control values. Original-handler arms
always receive control zero and retain their own cache policy. Raw trials record
the applied control word as well as device requests and bytes transferred.

The [Kickstart CPU profiling report](kickstart-read-profiling-2026-11-05.md)
records the subsequent C# optimizations and the
[rerun original-handler A/B](kickstart-profiled-original-ab-2026-11-05.md).
These retain the read-ahead window while keeping aligned data out of the
metadata LRU. Partial reads can consume a retained image immediately, with the
scratch path retained for aliasing. Normal standalone FFS reads use a smaller
packet decoder; inhibited/recovery and packed-integration behavior keeps its
existing route. Power-of-two 64-bit division is optimized in CopperSharp68k's
C# runtime for all generated callers.

In the next optimization pass, FFS metadata misses read the requested block
without replacing the data read-ahead window, while metadata already in that
window is reused. Aligned cached reads copy each contiguous span once; the
single-block path keeps its own small frame. Aliased private buffers retain
sector-at-a-time copying. Checksums still include every word, using eight-word
tiles and the trailing-word loop. CopperSharp68k computes power-of-two divisor
shifts with bounded tests.

Retained FFS runs use the stable block-size scalar; full geometry is
constructed for table traversal. The C# RAM copy loop advances its pointers
after each longword. CopperSharp68k's `CompactGuestMemory` pass folds those
advances and removes dead copy temporaries, producing ordinary postincrement
memory-to-memory moves while preserving the C# access order, widths and flags.
The 32-byte chunks, alignment, tail and overlap handling remain in C#.

A complete 512-byte-sector cache
hit copies its contiguous span without entering the refill loop. Metadata-cache
priority, logical overlays, aliased buffers and the read-ahead threshold retain
their existing behavior. Other sector sizes use the existing path.

Eligible even,
non-overlapping cache spans use a small C# countdown copy loop. It keeps the
eight longword copies per 32-byte chunk and their access order. Odd addresses,
refills and aliased buffers keep their existing paths; read-ahead is unchanged.

Original FFS reads
4 KiB payloads directly into the caller; Copper retains its read-ahead cache and
copies its payload. The native adapter now consumes validated retained run
scalars directly for aligned 512-byte–4 KiB packets. Other packets and aliases
retain the general reader. A short device read clears cursor validity and
retains the completed prefix. CopperSharp68k narrows small dead mask results
used only for condition flags, with live values and all CCR semantics preserved.

The native FFS adapter also consumes retained runs for sub-sector packets that
fit within one cached 512-byte sector. It borrows the current logical image
after resolving write overlays and copies only the requested bytes. Cache
misses, cross-sector packets, aliases and invalid cursors use the general
reader. Small packets retain the same physical read-ahead window.

## Lifetime and native records

The owner must clear `ReadCursor.Header` (FFS) or `ReadCursor.Node` (SFS2) after
any volume mutation, recovery or media change, before another read. Errors clear
the validity field. Cursors belong to individual handles; scratch can be reused
by other operations between reads. Mount execution remains single-owner.

The native adapters persist these scalars in the existing 216-byte file object.
All formats share the invalidation word at byte 56. FFS occupies bytes 56..87
and its otherwise unused word at 96 for the next pointer-table key.
SFS2 uses bytes 56..87 and 96..107, preserving the existing high file size and
position words at 88..95. Existing mount invalidation clears all file cursors.
No object allocation, on-disk layout, packet layout or journal policy changes.

The FFS overload with `out bool cursorChanged` lets adapters avoid writing an
unchanged retained cursor back to its owner. Traversal and invalidation report
a change. The native adapter still writes all cursor words when the read
destination overlaps its private file object, preserving alias behavior.

## Verification

Run the ordinary Release solution build and tests for host coverage. The host
regressions check transfer counts, scratch reuse, interleaved cursors, fragmented
files, backward seeks, checksum/backlink failures, completed prefixes, EOF,
invalidation, filesystem block sizes and SFS2 positions crossing 4 GiB.

`tests/CopperFS.Native.Tests` is a separate instruction replay suite. It links
CopperStart's existing MIT-licensed `NativeParityBus` in place, selected by the
`CopperStartRoot` MSBuild property (default: sibling checkout). This is a test
harness dependency; standalone handlers still build using only CopperFS and
CopperSharp. The suite executes actual handler HUNKs on 68000 and 68020 with
Exec/device service doubles, and therefore does not establish full-ROM coverage.

For example, from the repository root in PowerShell:

```powershell
./build/Build-Handler.ps1 -Ffs -ArtifactRoot artifacts/sequential-read/ffs
./build/Build-Handler.ps1 -Sfs2 -Sfs2CsunWrites -ArtifactRoot artifacts/sequential-read/sfs2-csun
./build/Build-Handler.ps1 -Sfs2 -ArtifactRoot artifacts/sequential-read/sfs2-trst
$env:COPPERFS_FFS_HANDLER = (Resolve-Path artifacts/sequential-read/ffs/CopperFFS).Path
$env:COPPERFS_SFS2_HANDLER = (Resolve-Path artifacts/sequential-read/sfs2-csun/CopperSFS).Path
dotnet test tests/CopperFS.Native.Tests/CopperFS.Native.Tests.csproj -c Release
$env:COPPERFS_SFS2_HANDLER = (Resolve-Path artifacts/sequential-read/sfs2-trst/CopperSFS).Path
dotnet test tests/CopperFS.Native.Tests/CopperFS.Native.Tests.csproj -c Release
```

Retain the handler maps beside the HUNKs. The tests also check native MaxTransfer
splitting, handle cursor invalidation, high-word preservation and TD64 reads.
Use CopperStart's licensed-ROM/media replays separately for OS integration.
