# Freed-range automatic TRIM qualification, 2026-11-12

Subsequent bottleneck optimizations and newer measurements are recorded in the
[handler performance investigation](handler-performance-qualification-2026-11-12.md).
The build hashes and measurements below retain the tracked-range baseline.

Automatic TRIM now tracks unallocation and batches durable, revalidated ranges.
It no longer scans the volume's allocation contents to discover free space.
It remains opt-in: the original-handler performance gate fails for every profile.
No packages or ROMs have been published.

## Implementation and cost

OFS/FFS, SFS and SFS2 report actual frees from their bitmap mutation paths,
including bulk relocation and deferred TRST continuations. The memory-only
callback coalesces hints in caller-owned storage. A bounded 64-entry queue drops
new hints on overflow; it never forces a commit or changes an operation's result.
SFS recycled data remains allocated until actually purged.

Each timer turn takes at most eight ranges and 4,096 blocks. One recovery,
filesystem checkpoint and successful device barrier precede the batch. Current
bitmap checks skip blocks reallocated since the hint was recorded. Exclusive
mount ownership prevents allocation between validation and synchronous discard.
Rollback and crash do not require persisting hints. Manual CopperTrim and the
on-disk formats, journal policies and packet ABI are unchanged.

The opt-in state cost is 624 bytes per mount, plus the existing temporary
256-byte DMA-compatible control buffer during service. The native cores remain
allocation-free. SFS/SFS2 validate only intersecting bitmap pages. FFS also walks
bitmap-pointer metadata to reject aliases and protect permanent metadata; this
pointer walk still scales with layout size, without scanning unrelated bitmap
contents.

A host SFS2 test discarding a 16-block hint needs **14 block reads and one device
flush** on both 8 MiB and 32 MiB fixtures. A two-range batch also requires only
one barrier. These are I/O counts, not latency measurements. The previous scan
implementation's whole-pass/certificate results in
[the earlier report](automatic-trim-qualification-2026-11-12.md) describe a
superseded implementation and are not the current automatic path.

Qualification found an FFS scratch identity defect: reading a bitmap page during
discard without updating its cached identity made a subsequent disjoint range
use that page under an old identity. This suspended maintenance before later
ranges. The scanner now updates the identity; a two-page host regression passes,
and the full-ROM cut at discard sector 37 now reaches its intended boundary.

## Performance against the originals

Hash-pinned references are original FFS 40.1 and SmartFilesystem 1.279. Each
profile rotates three original/candidate trials with the same media and 68020
settings. The Fast RAM payload is allocated before loading either handler.
Mutation timing includes explicit filesystem ACTION_FLUSH. Each result is
independently checked. Positive percentages mean more emulated cycles:

| Profile | Mutation + filesystem flush | Verification |
| --- | ---: | ---: |
| OFS | +284.147% | +10.266% |
| FFS | +159.350% | +3.534% |
| SFS | +102.038% | +13.159% |
| SFS2 CSUN | +195.712% | +17.369% |
| SFS2 TRST | +1497.242% | +17.233% |

All five profiles fail parity, including the 0.1% interrupt allowance. These
comparisons use a device without UNMAP support and therefore measure filesystem
execution and opt-in tracking/discovery cost, not SSD discard latency. Actual
UNMAP, reallocation and failures are exercised separately in the automatic
maintenance tests. The large filesystem performance gaps predate this change;
eliminating the allocation scan does not establish original-handler parity.
Default enablement is therefore withheld.

## Reproducible evidence

Ignored `artifacts/freed-trim` holds the local reference manifest, handler input
assemblies, CILica reports, build logs, test logs, cycle samples and media results.
The source uses CILica with inlining disabled and scalar replacement enabled.
Each handler has an isolated input/build directory. The FFS artifact is in
`ffs-fixed`; SFS and both SFS2 policies are in `final/<profile>`. SFS2 CSUN must
be built explicitly with `-Sfs2 -Sfs2CsunWrites`; `-Sfs2` selects TRST.

| Handler | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS | 71,620 | `DA6F3DF59E81036EC043E3EFAF9468937CFC2D77F0AB31D08D995D1C65807711` |
| CopperSFS / SFS | 147,604 | `B2110175BA765DFC9701F8D1737B2F819F23D74092BC4FA9FEACC234352BAD34` |
| CopperSFS / SFS2 CSUN | 237,708 | `3D44190B1F663B3657B59C5FFB9D824520BF5CAEA82AD60A8A91B78C9D7EF97B` |
| CopperSFS / SFS2 TRST | 193,228 | `9B2019745017D4B64A094317A21EEC31E3B5523ED95080847A167975F452950E` |

Set `COPPERFS_AUTOTRIM_MANIFEST` and `COPPERFS_CORRECTNESS_MANIFEST` to the local
manifest and `COPPERSHARP_KICKSTART31_ROM` to a locally supplied ROM. No reference
ROM/media is redistributed. Relevant test filters are `AutomaticTrimAfterDelete`,
`AutomaticTrimStops`, `PowerCutDuringAutomatic`,
`SharedMutationsAndEveryCleanHandoff` and `ReleaseCandidateStaysOnPar`.

## Coverage limits

- Host: Release solution build passed. The full solution test run passed 1,449
  cases, with three external-image cases skipped. After the final FFS scratch
  fix, the entire FFS suite passed 276 cases (one more than in the solution run).
- Native: all four standalone 68000 profiles rebuilt successfully with CILica
  scalar replacement enabled and inlining disabled. The selected CopperStart
  buffer, geometry and size instruction replays passed 193 cases with
  `-p:CopperStartNativeSfs2=true`. Running this broader selection without that
  flag passed 189 and failed four SFS2-only expectations; enabling the required
  feature resolved those configuration failures. The earlier narrower replay
  selection passed 147 cases, and queue/lifecycle replays passed on both 68000
  and 68020 without managed allocation.
- Full ROM: all 19 automatic-maintenance cases passed on Kickstart 3.1/68020.
  They cover OFS/FFS, SFS, SFS2 CSUN and SFS2 TRST; poisoned free space,
  reallocated-file survival, original-handler readback, partial-discard errors,
  inhibit/remount and cuts during irreversible discard. Cut tests are armed
  after a completed workload and fresh allocation/free, so they test the
  intended durable boundary. Failure-suspension assertions count within a
  mount, because an explicit remount correctly permits maintenance to retry.
  All nine additional original/Copper mutation and clean-handoff profiles also
  passed, including OFS/FFS DD and HD floppy fixtures. This gives 28 passing
  compatibility/recovery cases; the five original-performance cases separately
  failed their release gate as reported above.

Host checkpoint cuts model volatile sectors, strict and buffered SFS, and
recovery before discard. Queue tests cover overflow, coalescing and randomized
splits; filesystem tests cover actual frees, stale hints, reuse and partial
failure. A failed FFS durability barrier sends no discard. Existing manual trim
tests retain their full-map corruption checks.

Physical SSD/controller behavior, asynchronous completion, actual media
replacement and foreground latency on slow discard devices remain unavailable
coverage, not passes. Emulator inhibit/remount is separate evidence. Automatic
TRIM must remain disabled pending the release performance gate and applicable
device qualification.
