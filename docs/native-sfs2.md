# Native SFS2 handler integration

The sibling CopperStart checkout now has an explicit SFS2 build profile for
the native CopperSFS handler. One HUNK detects and mounts classic `SFS\0`
version 3 and SFS2 `SFS\2` version 4. Neither mount nor mutation converts a
volume. Classic media continues through the existing embedded classic engine,
including its buffering, recycling and selected journal policy.

This produces development artifacts. Existing downloadable handlers have not
been replaced, and this work does not publish a release.

## Native adapter and ABI

The adapter selects the verified SFS2 reader, writer and recovery decoder using
the actual root identifier/version/checksum. Root probing happens before
recovery; normal traversal starts only after recovery and clean mount succeed.
Volume names use the SFS2 object-header offset. Allocation, data and journals
use the existing native device adapter, including its physical write queue,
read-your-writes, DMA mask, bounce buffering, MaxTransfer and TD64 handling.

Private file objects retain both words of file size and current position. Shared
handles observe size changes and invalidated mappings; resize considers every
other open handle's full-width position. Public FileLock fields and DOS file
handle layout are unchanged. Including classic overwrite reservations and the
handler record-lock heads, native mount state is 2,320 bytes. The SFS2 extension
ABI is **CSEC, version 13** and uses a **512 KiB** image at E00000..E7FFFF;
the classic-only profile uses
CSEB/version 12 and 256 KiB. The DOS provider and extension ROM must be rebuilt
with matching profiles. The distinct signatures reject mismatched images.

Ordinary DOS Read/Write packets transfer a signed-LONG-sized buffer at the
full-width stored position. Private native Seek64/SetFileSize64 packets use the
existing QUAD input/output ABI (actions 26400/26401). Extended examine packets
publish full big-endian QUAD sizes and block counts. SFS2's legacy `fib_Size`
is zero above `0x7FFFFFFF`; use the wide fields for those files. Legacy seek and
resize refuse positions their results cannot represent instead of wrapping.
Classic packet results retain their existing behavior.

The native SFS2 engine retains the [verified format limits](sfs2-writes.md):
48-bit lengths and at most `2^31` filesystem blocks (1 TiB at 512-byte blocks).
Growth requires actual free space. This does not add filesystem sparse files.
Manual TRIM remains unavailable for SFS2; the existing classic TRIM path is not
applied to version-4 structures. SFS2 deletion permanently removes an ordinary
file or empty directory; recycled-file management and link traversal are not
added by this integration.

## Journal policy and memory

SFS2 uses strict per-packet private CSUN undo by default, including on a buffered
classic build. It does not use classic mount write-back buffers. Large writes
can use a lazy, mount-owned 64 KiB buffer and retain transaction boundaries.
An interrupted private transaction needs CopperSFS recovery before an original
handler can use the volume.

The separate `-Sfs2TrstWrites` switch selects original-compatible native TRST
redo for SFS2. It uses the native platform's existing bounded overlay rather
than passing a multiword generic platform to the 68000 compiler. The lazy arena
is 192 KiB per mount: a 16-byte header and `4 + BlockBytes` bytes per changed
block, including data and metadata. An oversized operation fails before
publication with zero reported bytes, preserves the file position and can be
retried with a smaller request. There is no CSUN fallback in this policy.
Both policies recover original SFS2 TRST and CopperSFS SFS2 CSUN on mount.

The embedded copy retains the portable version-4 algorithms, with native
platform/arena adapters. Scalar metadata/FIB projections stay in that module;
its record is named `ReadEntry` to avoid the compiler's collision with static
layout classes named `Entry`. All new native roots remain allocation-free apart
from the existing explicit Exec allocations for mount-owned scratch storage.

## Reproduction

From CopperStart, build a dual-format MC68000 candidate:

```powershell
./build/Build-SfsHandler.ps1 -Sfs2 -ArtifactRoot artifacts/native-sfs2/csun
# Optional SFS2 native redo; the classic policy is selected independently.
./build/Build-SfsHandler.ps1 -Sfs2 -Sfs2TrstWrites `
  -ArtifactRoot artifacts/native-sfs2/trst
./build/Build-SfsExtension.ps1 -Sfs2 -ArtifactRoot artifacts/native-sfs2/extension
```

From CopperSFS, generate the original-handler fixtures using the
[reference procedure](sfs2-writes.md), then qualify each candidate:

```powershell
$env:COPPERSHARP_KICKSTART31_ROM = 'C:/path/to/licensed-kickstart-3.1.rom'
./scripts/Test-NativeSfs2.ps1 `
  -Handler ../CopperStart/artifacts/native-sfs2/csun/CopperSFS `
  -ReferenceImage artifacts/sfs2-reference/new-run/reference.hdf `
  -NativeRedoImage artifacts/sfs2-write/new-native-redo/reference.hdf `
  -OriginalHandler C:/path/to/SmartFilesystem `
  -OutputDirectory artifacts/native-sfs2/new-csun-qualification
# Repeat with the TRST candidate and -Trst.
```

The native test project is intentionally outside the default portable solution.
It requires sibling CopperSharp68k and CopperScreen checkouts, original handler
fixtures and a licensed ROM. Missing inputs explicitly skip optional tests;
the qualification script requires every selected test to pass without skips.
ROMs, handler binaries, HUNKs, sparse images and reports stay in ignored local
artifacts. The wide fixture requires sparse host-file support.

## Validation boundary

The guest tests exercise original-handler media on 68000 and 68020, native
creation/writes, resize, rename/delete, comments/protection/dates, directory
enumeration, remount and clean handoff back to original SmartFilesystem 1.279.
They also mount an actual dirty transaction captured from that original handler.

Separate mutation traces cover overwrite, truncate, rename, delete, create and
comment updates. Every individual 512-byte sector write and flush is assigned
to its durable prefix. The model retains writes until a reliable flush and
loses them on interruption; points with byte-identical durable images share one
actual cut/reboot. Each representative is compared byte-for-byte with its
expected prefix, recovered, checked against a complete independent before/after
manifest, and rebooted again to test recovery idempotence. TRST representatives
also reboot with the original handler and require TRST pages with no CSUN
descriptors in completed mutation traces. Reports distinguish covered cut
points from actual reboot executions.

A sparse host file exercises a normal allocated file above 4 GiB on a logical
136 GiB SFS2 device. Guest checks cover seek, standard Read/Write at that
position, shared handles, extended FIBs, clamped resize and size queries. Host
checks inspect the affected lengths, wide extent count, allocation bits, free
count, marker/checksums and content; they do not run a whole-volume allocation
scan over that device.

These are compiled native-ROM/emulated-device checks, separate from portable
host tests and instruction-level adapter tests. Physical hardware, lying
flushes, torn sectors and arbitrary controller reordering remain outside this
native qualification. Exact test results and binary hashes are retained in the
qualification reports; intermediate failed setup/build runs remain separate.

## Recorded qualification, 2026-10-25

Both development HUNKs target MC68000. The CSUN profile passed all 10 selected
native tests; the SFS2 TRST profile passed all 11, with no skips. Both include
68000/68020 read/mutation/remount and original-handler handoff, wide-file packet
checks on the 136 GiB logical device, and actual original-TRST mount recovery.
TRST additionally passes arena exhaustion/reuse without a CSUN fallback.

| Native policy | Covered sector/flush cut points | Actual cut/reboot groups |
| --- | ---: | ---: |
| SFS2 CSUN | 157 | 65 |
| SFS2 TRST | 63 | 30 |

Each group includes CopperSFS recovery and repeated recovery; TRST groups also
include original-handler recovery. Full independent manifests pass throughout.
The grouped coverage is exhaustive for the recorded atomic-sector/durable-flush
traces, not an arbitrary pending-write or sector-tear model.

Separate classic-core host checks pass 262 tests (one optional external-image
test skipped). Native A500 classic-media startup/write/remount/recovery and
concurrency checks pass on both final policies: four checks without skips.
The current architecture suite passes 69 tests, including named record-lock
codec fields. An earlier integration snapshot passed 120 compiled adapter,
transfer and size checks; those receipts retain their earlier binary boundary.
The portable Release suite
passes 362 tests with five optional original-handler tests skipped and a clean
source audit (`artifacts/native-reserved-overwrites/final-reliability/`).
Original-handler native qualification is recorded separately above. This host
run includes the final classic reservation-pressure fixes.

Final native receipts are under `artifacts/native-sfs2/shared-final-*-qualification/`.
`final-audit.json` collects their exact binaries, original inputs, source audit,
compiler/test assembly identities, ABI checks and supporting results. The
CSUN/TRST distinction selects the SFS2 policy; both use classic TRST by default.

CopperStart's `artifacts/native-sfs2/shared-codec-source/` is the immutable
273-file handler snapshot, with hashes in `shared-codec-source-inputs.json`.
It contains the completed handler record-lock engine and final classic
reservation-pressure fixes. Its two HUNKs and SFS2 ROM are byte-identical to
the `shared-final-*` binaries used by the media tests; the named-codec cleanup
adds no machine-code change. The equivalence proof is retained as
`shared-codec-equivalence.json` in CopperSFS. The classic-only 256 KiB CSEB ROM
also builds and matches its compiled profile; the SFS2 CSEC ROM is 512 KiB.

Subsequent concurrent classic performance optimizations and the dos.library
record-lock API bridge are outside this frozen qualification. Generic build
commands above build the checkout's current sources; use the recorded source
snapshot and compiler inputs to reproduce these exact candidates. Earlier
`csun-qualification/`, `trst-qualification/`, `dual-*-qualification/` and
`combined-*-qualification/` receipts remain separate from these final results.
