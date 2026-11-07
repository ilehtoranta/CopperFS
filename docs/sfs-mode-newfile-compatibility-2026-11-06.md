# Classic SFS MODE_NEWFILE compatibility, 2026-11-06

The remaining classic-SFS comparison failure is fixed. The full strict matrix
now passes **9/9**, and an additional classic-CSUN comparison passes. Comparisons
include every saved file in `.recycled`; no filtering or expected-failure
exception was added.

## Behavior and implementation

With classic SFS recycling enabled, opening an existing file with `MODE_NEWFILE`
now moves the previous object into `.recycled` and creates a fresh empty object
at the original path. The returned handle uses the new object. The previous
data extents remain owned by the recycled object, with their bytes, comment,
protection and date preserved. Existing recycling rules provide collision
suffixes such as `$AAA`.

Expanded controls against original SmartFilesystem 1.279 confirmed two details:
empty files are recycled too; delete protection blocks replacement, while write
protection permits replacement with a fresh writable file. The native comparison
checks both behaviors, rather than inferring them from truncation semantics.

`ReplaceFile` is a separate core operation. Ordinary `Truncate`, resize,
recycling-disabled truncation and the SFS2 path retain their existing semantics.
The classic replacement uses one CSUN transaction/savepoint or one TRST staging
arena: the recycle move delays its finish, and creation retains the active
journal chain and pending recycle counters. The final finish publishes both
namespace changes together. Creation failure must not publish just the move.

The native provider dispatches replacement locally for `ACTION_FINDOUTPUT`.
Wire opcodes, request layouts, mount-state fields, scratch buffers, on-disk
formats and journal policies are unchanged. No portable-core dependency on an
application project, managed allocation or new native buffer was added.

## Validation

| Coverage | Result |
| --- | --- |
| Release solution build | Pass; one existing compiler nullable warning |
| Release solution host tests | **1228 passed, 3 external-fixture skips** |
| Focused replacement regressions | **25 passed**, included in the host total |
| Compiled native replay, MC68000/MC68020 | **34 passed**, no skips |
| Strict original-handler matrix | **9 passed**, 54 completed guest boots |
| Additional classic CSUN A/B | **1 passed**, 6 completed guest boots |
| CopperStart's existing classic-SFS A/B cases | **7 passed**, 18 guest boots |

The 25 focused cases cover strict CSUN, batched CSUN, buffered CSUN and TRST.
They exercise preserved bytes, collision names, empty reopens, protection,
recycling-disabled behavior, every write/flush power-cut boundary, every
write/flush I/O failure, and TRST arena exhaustion before publication. Recovery
checks accept only the complete before or after state, including allocation,
metadata and data sectors. Existing recycling and mutation tests pass as well.

The strict matrix repeats OFS/FFS on 16 MiB, DD and HD geometry, classic SFS with
buffered TRST, and SFS2 with CSUN/TRST. Both writers start from identical images;
each saved result boots with both readers. The separately implemented DOS and
SFS walkers validate checksums, ownership and allocation, then compare complete
file path/size/content-hash manifests.

The classic workload additionally reopens a 4097-byte file, reopens its new
7-byte replacement again to force a collision, and reopens an empty file. Both
readers verify the recycled bytes and the original fixed date, comment and
protection, plus the active file's reset metadata. It also checks the original
handler's delete/write-protection behavior. Both classic journal profiles pass
the complete file-manifest comparison, including these recycled objects.

## Frozen build provenance

All five handlers use the same frozen source snapshot and frozen compiler
directory. Managed input preparation uses the maintained `Build-Handler.ps1`
pipeline; the exact generated CLI arguments are saved, and independent native
compilations run in parallel. The five compatibility reports are compatible
and have zero managed allocation sites. Source, compiler and input hashes are
retained alongside the binaries.

| Candidate | Bytes | SHA-256 |
| --- | ---: | --- |
| Classic buffered TRST | 172764 | `7592114EEDF04549F75A5A484013C729BBDC1DBE94A737F098EFB1E19E2796F9` |
| Classic CSUN | 161668 | `EB76A133B01EBFE0636D28BBD5AFA7B5491780CBDFE45D2A177D2333E36993BD` |
| SFS2 CSUN | 218316 | `524FA6C1A5B779324C6CD3778F08CF93ED8121F2C305AA6201994BD380A3016F` |
| SFS2 TRST | 212168 | `2BFD3E43E5C5A7169E8CBA9253260552C536182A6CFA9558EFD33936C22EE960` |
| CopperFFS | 172764 | `C2ED0F540F092B5DB21444FF2ACA8AE54AC100777154A8852D7D8FE2DF539D8A` |

The reference binaries and ROM match the hashes and extraction provenance in
the [initial A/B report](original-handler-correctness-ab-2026-11-06.md).
The classic image is independently formatted by the pinned amidisk revision;
the other fixtures remain original repository fixtures, accepted by the
reference handlers. The expanded original-only control passes before candidate
qualification.

## Evidence and limits

Qualified local evidence is under ignored
`artifacts/sfs-newfile-fix/qualified/`: `summary.json`, `provenance.json`,
`source-hashes.json`, the frozen `source` and `compiler` directories, managed
inputs, compiler arguments/maps/reports, independent inspections, per-boot
`result.json` and `qualification.json`, and TRX results. The additional CSUN run
is in `classic-csun`; the standard matrix is in `matrix`. The 18 CopperStart
guest reports are copied into `copperstart-reports`. Earlier controls and
superseded builds under the parent directory are diagnostic history.

These guest runs boot real licensed Kickstart 3.1 on an emulated 68020 and load
the compiled MC68000 handlers. Target media use synchronous `copperhdf.device`.
DD/HD rows qualify filesystem geometry and interoperability, not target floppy
DMA or physical-floppy persistence/performance. Host crash/failure coverage is
separate from the native clean-close/remount tests. Custom CopperStart ROMs and
physical media were not requalified for this change.

No ROM, reference binary, generated media or build artifact is tracked. No
package, handler release or ROM was published.
