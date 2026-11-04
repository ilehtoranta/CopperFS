# Validation boundary

The separate [SFS2 writer and recovery API](sfs2-writes.md) adds host mutation,
fault/crash, wide-file and original-handler handoff checks. The required
reliability workflow retains separate SFS2 counters and exact binary identities.
These results do not rebuild or qualify a native CopperSFS handler.

The additive [SFS2 read-only API](sfs2-read-only.md) has 30 passing focused host
cases, including traversal of an image formatted and populated by original
SmartFilesystem 1.279 under Kickstart 3.1. Wide files/volumes use synthetic host
fixtures; the compiled native CopperSFS handler is unchanged. The recorded
full-solution run passes all 269 non-TRST tests but includes 15 failures from
concurrent `TrstWriteTests` work; see the linked reproduction details and receipts.

The previously incomplete supplementary nearly-full host stress case completed
on 2026-10-24 against the frozen native-handler core: 600 fault points,
304,017,665 logical combinations and 3,899,421 exhaustive image validations,
with no failures. The limit was raised to 16,777,216; byte-exact containment
preserves full coverage without sampling. See [the completion details](native-reliability-20261024.md).
This does not qualify the later compatibility-review corrections for native use.

The corrected development handlers have also been rebuilt against the newer
embedded core, including the subsequent review fixes. See [native validation
on 2026-10-24](native-reliability-20261024.md) for the exact buffered/strict HUNK
hashes, A500 replay, full write/flush crash sweep, TRIM checks and limits.
Existing downloadable handlers were not replaced.

The second filesystem review follow-up passes 151 Release host tests. It adds
36 cases covering the eight reported issues, including malformed comments,
native extent owner tags, undeletable objects, packed directory containers,
node fullness flags, rename/admin expansion, comment relocation and checker
compatibility. Six scenarios enumerate write/flush cuts and I/O failures,
including relocation during admin expansion and nested node-index updates.
The final run also includes six separately added crash-state enumeration tests.
These newest changes have host validation only; the native binary evidence
above predates them.

After the filesystem review fixes on 2026-10-24, the Release suite passed 109
tests. The 25 additional cases covered the six reported findings, deeper extent
and object-node trees, create/rename path handling, and write/flush interruption
and failure recovery for admin expansion, node-branch growth, extent pruning
and indexed deletion. See [reliability testing](reliability.md) for the scope.
The earlier admin-header finding was subsequently corrected: the reference
writer uses `32`, and the original test incorrectly required `2`. The writer
and test now require `32`; the checker accepts both representations of the
same 32-block layout for compatibility with earlier CopperSFS images.

The initial reliability run on 2026-10-24 passed 84 tests. A separate read-only
checker validates allocation ownership and complete file manifests without
calling the filesystem core. The new host matrix checked 42,467 mutation
fault/crash images, 11,000 recovery fault/crash images, 728 partial-write cases
(668 recovered, 60 refused mount), and 900 modeled random operations. Counts
are executions, not distinct states. See [reliability testing](reliability.md)
for the exact assumptions, reproduction command, defects fixed and remaining
release gates. This does not qualify or update the downloadable handler.

On 2026-10-19 the standalone core's 31 host semantic tests passed in Release
configuration. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB.

The preview handler is compiled for MC68000. A separate check loaded and
unloaded its HUNK with native AmigaDOS `LoadSeg` and `UnLoadSeg`. In an emulated
A500 with a licensed Kickstart 3.1 ROM, the handler then received
`ACTION_STARTUP` from native AmigaDOS on `trackdisk.device`, read an existing
1,300-byte file, created and read back a 1,024-byte file, and read it again
after a fresh boot. Another native run launched two DOS processes that each
wrote a separate 512-byte file while the parent read the SFS volume. The
completed images were mounted and their file bytes checked by a host-side
CopperSFS reader.

One native power-cut replay captured a complete ADF while the SFS transaction
marker indicated an unfinished write. A fresh Kickstart 3.1 boot recovered
that image and produced a mountable volume whose file bytes passed the same
host-side reader. This is one interruption point, not exhaustive crash coverage.

The native runs used a synthetic SFS floppy image. Independent SFS tools,
independently formatted media, physical Amiga hardware, other ROM versions,
different block devices, and TD64/NSD partitions above 4 GiB have not been
checked. The host-side reader shares the CopperSFS core, so it is not an
independent implementation check.
