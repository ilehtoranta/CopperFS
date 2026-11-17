# Handler performance gaps and qualification — 2026-11-12

The subsequent [metadata-write optimization pass](metadata-write-qualification-2026-11-12.md)
contains the current candidate and includes direct original-handler comparisons.
The measurements below describe its baseline.

The candidate is faster, but **does not meet the original-handler parity gate**.
Automatic TRIM remains opt-in. This work does not change disk formats, packet
ABI, journal policies, commit barriers, recovery ordering or manual CopperTrim.
SFS2 TRST remains the release policy, with CSUN recovery support retained.

## Changes

- OFS growth constructs the forward links of a verified contiguous allocation
  run before writing its blocks. Each new data block is sealed once, the previous
  run is linked once, and the completed pointer prefix is published once per
  run. Failure after mutation begins leaves the root invalid. Fragmented and
  partial-block paths remain supported; no new staging allocation is required.
- FFS/OFS free-space counting skips eight entirely free bitmap words together
  and uses pointer iteration for mixed groups. It retains complete bitmap
  checksums, pointer validation and exact handling of the final partial word.
- SFS2 bitmap and CSUN descriptor queries consume read-only metadata views
  before another I/O/cache operation can expire them. Writers still receive
  private copies. Verified physical-cache images can serve all private scratch
  buffers; queued images use a separate SFS2 checksum certificate, invalidated
  on replacement. Logical overlays retain precedence over physical caches.
- SFS2 TRST journal allocation skips occupied bitmap words. Both the durable
  original map and the proposed final map must still mark a block free; staged
  targets, permanent metadata and the final reserved block remain excluded.

An OFS retained-data borrowing experiment was removed: it increased mutation
cycles from 34.59 million to 35.33 million because lookup overhead outweighed
the copies saved. The retained implementation uses the faster measured path.

## Measurement method

Baseline: the final hashes in
[deferred TRIM qualification](deferred-trim-design-qualification-2026-11-12.md).
Candidate manifest: `artifacts/perf-gaps/qualification/manifest.json`.
Originals are FFS 40.1 and SFS 1.279 with the same pinned hashes and initial
media as that baseline. No original binaries or ROMs are checked in.

The full-ROM runs use Kickstart 3.1, an emulated 68020, 16 MiB volumes and
512-byte filesystem blocks. Each original/candidate pair runs three alternating
trials with identical packets and media. The payload buffer is explicitly
allocated in Fast or Chip RAM before loading either handler. Mutation timing
includes the explicit final flush. Every resulting image is checked independently.
These are workload cycle measurements, not physical device throughput claims.

The tables below use the unmodified frame runner. Separate instruction profiles
identify costs; scalar profiling is not substituted for acceptance timing.
The verification phase includes guest comparisons and OS work, and the
`<outside-handler>` profile bucket is not attributed to the filesystem.

## Results against the previous candidate

Fast RAM, mean guest cycles in millions. A reduction is positive.

| Profile | Previous mutation | New mutation | Reduction | Previous verification | New verification |
| --- | ---: | ---: | ---: | ---: | ---: |
| OFS | 42.276 | 34.592 | 18.18% | 20.494 | 20.469 |
| FFS | 21.774 | 20.897 | 4.02% | 17.993 | 17.954 |
| SFS | 22.543 | 22.543 | unchanged | 20.916 | 20.916 |
| SFS2 CSUN | 43.302 | 35.674 | 17.62% | 20.351 | 20.314 |
| SFS2 TRST | 33.723 | 32.122 | 4.75% | 20.430 | 20.405 |

Chip RAM mutation reductions are 18.18%, 4.02%, unchanged, 17.64% and 4.76%,
respectively. Verification cycles are unchanged or slightly lower in both RAM
classes. SFS was profiled and rerun, but these changes do not improve its
mutation path.

## Remaining gap against the originals

Optimization priority is FFS and SFS, including the SFS2 TRST release profile.
OFS performance is secondary; retain its compatibility and recovery coverage.
The historical OFS timing result below remains evidence, not the main target
for further hard-disk performance work.

Direct comparison in millions of guest cycles, Fast RAM, mean of three trials.
FFS uses original FFS 40.1; SFS and both SFS2 profiles use original SFS 1.279
on the corresponding disk format. Lower is better. The mutation workload
includes its final flush; verification includes reads and guest data checks.

| Profile | Original mutation | Copper mutation | Original verification | Copper verification |
| --- | ---: | ---: | ---: | ---: |
| FFS | 12.167 | 20.897 | 17.368 | 17.954 |
| SFS | 17.478 | 22.543 | 18.339 | 20.916 |
| SFS2 TRST | 15.599 | 32.122 | 17.153 | 20.405 |
| SFS2 CSUN | 15.598 | 35.674 | 17.153 | 20.314 |

TRST and CSUN are Copper write policies; the original SFS comparison uses its
own native transaction policy in both rows. These are complete workload costs,
not isolated filesystem-call timings or physical-device throughput.

Positive values mean more cycles than the original handler. The gate permits
only 0.1%; **all ten profile/RAM comparisons fail**. Their functional probes
and image checks complete successfully; the failures are the speed assertions.

| Profile | Fast mutation gap | Fast verification gap | Chip mutation gap | Chip verification gap |
| --- | ---: | ---: | ---: | ---: |
| OFS | +128.50% | +10.60% | +128.43% | +10.84% |
| FFS | +71.76% | +3.38% | +71.80% | +3.66% |
| SFS | +28.98% | +14.05% | +29.04% | +14.31% |
| SFS2 CSUN | +128.71% | +18.43% | +128.74% | +18.75% |
| SFS2 TRST | +105.92% | +18.96% | +105.54% | +19.18% |

Raw samples, means and hashes are in `qualification/original-performance`,
`qualification/original-performance-chip` and `qualification/parity-summary.json`
under `artifacts/perf-gaps`.

## Where cycles went

Exclusive instruction-profile cycles in millions, for the mutation workload:

| Cost | Before | After |
| --- | ---: | ---: |
| OFS block copies | 10.141 | 7.657 |
| OFS sealing/writing (`Put`, excluding callees) | 2.042 | 0.925 |
| FFS free-space counting | 1.332 | 0.515 |
| SFS2 CSUN block copies | 7.727 | 4.746 |
| SFS2 CSUN block sums | 3.840 | 1.685 |
| SFS2 TRST bitmap-position arithmetic | 1.321 | 0.229 |
| SFS2 TRST journal-space loop, excluding callees | 0.625 | 0.209 |

Copies remain the largest handler-local cost: 7.66 million cycles for OFS,
2.32 million for FFS, 1.68 million for SFS, 4.75 million for CSUN and
4.19 million for TRST. They include private scratch, retained cache images,
transaction staging and physical write queues. These buffers have different
ownership and durability lifetimes; removing their copies requires a valid
replacement ownership model. OFS chain checks and SFS2 transaction staging
remain substantial checksum consumers.

SFS's next largest individual costs are redo compression planning (0.774 million),
metadata-view lookup (0.568 million), cache recency updates (0.451 million)
and block sums (0.394 million). It already uses retained verified metadata and
owned redo images, so the SFS2 view optimization does not provide the same saving.

The I/O trace also exposes a larger remaining difference: the candidate commits
and publishes metadata much more frequently than the originals. Device command
counts during the same Fast-RAM mutation interval are:

| Profile | Reads, original / candidate | Writes, original / candidate | Flushes, original / candidate |
| --- | ---: | ---: | ---: |
| OFS | 28 / 53 | 314 / 414 | 2 / 1 |
| FFS | 20 / 7 | 47 / 162 | 2 / 1 |
| SFS | 7 / 23 | 17 / 206 | 7 / 116 |
| SFS2 CSUN | 6 / 185 | 17 / 337 | 7 / 260 |
| SFS2 TRST | 6 / 111 | 17 / 156 | 7 / 104 |

OFS previously issued 914 writes; the new run construction reduces that to 414.
Other profiles retain their previous command counts and barriers. These are
commands, not sector counts. The next major opportunity is reducing redundant
metadata publication and grouping transactions while preserving acknowledged
state and recovery semantics. Dropping barriers would not be an equivalent
optimization. The current changes alone cannot close the parity gap.

## Deferred work and reclamation

The parity backend does not support UNMAP. A separate supported-device probe
measures deletion of the same 32 KiB allocation, reallocation safety and every
deferred service turn. This avoids hiding work outside foreground timing.

| Profile | Previous deletion cycles | New deletion cycles | Previous service cycles | New service cycles | Combined reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| OFS | 2,344,400 | 2,306,970 | 184,674 | 205,280 | 0.67% |
| FFS | 582,800 | 523,290 | 137,944 | 139,376 | 8.06% |
| SFS | 953,790 | 953,780 | 63,086 | 63,086 | unchanged |
| SFS2 CSUN | 3,336,240 | 2,464,560 | 118,553 | 112,221 | 25.41% |
| SFS2 TRST | 1,979,850 | 1,773,260 | 144,734 | 129,807 | 10.43% |

OFS service is 20,606 cycles more expensive. The profile attributes this to
cache unhashing/invalidation after the changed write access pattern:
`DiscardRanges` rises from 52,264 to 72,619 inclusive cycles. Preparation falls
from 104,334 to 99,848. Both runs have two device reads, two UNMAPs and the same
28 cache-entry removals. This is a measured maintenance regression, despite the
net saving. FFS service rises by 1,432 cycles; the combined interval still falls.

Cold staging initialization remains 2,535–2,576 cycles. Additional permanent
TRIM memory remains 2,148 bytes per mount. The five profiles issue respectively
3, 3, 1, 2 and 2 range notifications, and 2, 2, 1, 2 and 2 UNMAPs, reclaiming
35,328, 33,280, 32,768, 32,768 and 32,768 bytes. FFS/OFS totals include metadata.
Every clean service scope has zero recovery calls and at most one UNMAP.
Requested reclamation is measured; physical SSD erase completion is not.

All fifteen larger/fragmented/warm cases pass. Each attachment performs exactly
three discovery commands, including the two-delete warm workload; no capability
queries repeat. Every case meets its reclaimed-byte assertion and original-handler
readback. The matrix and its counts are recorded under
`qualification/trim-matrix`. Diagnostic deletion and service scopes are in
`artifacts/perf-gaps/trim-profile`; `build/Summarize-TrimProfile.ps1 -AssertBounded`
checks the service bounds and generates `trim-summary.json`.

## Qualification

- **Host:** `dotnet build CopperFS.slnx -c Release` passes with zero warnings and
  errors. `dotnet test CopperFS.slnx -c Release` passes 1,490 tests and skips three
  external-image tests: 1,148 SFS, 292 FFS, 18 Amiga integration and 32 storage
  benchmark tests pass. Logs: `solution-build-final.log`, `solution-tests-final.log`.
- **Native:** four standalone 68000 handler builds pass. CopperStart's buffer,
  geometry and size replays pass 193 cases; ten additional instruction cases
  pass on 68000/68020, covering copies, lifecycle, DMA allocation failure,
  transport failure and eight-descriptor UNMAP. Logs: `replay-final.log`,
  `instructions.log`, and the per-handler build logs.
- **Full ROM compatibility:** 68 cases pass for the selected candidate hashes.
  Coverage includes original-handler mutation/readback handoffs, OFS/FFS DD and
  HD media, SFS operation through the SFS2 handler, foreign-format startup and
  recovery ordering, both published SFS2 journal types, reallocation, recycled
  retention, partial discard failure, cuts during discard, inhibit/remount,
  larger/fragmented/warm reclamation and manual TRIM. Log: `compatibility.log`.
- **Full ROM commit/recovery cuts:** six final CSUN cases and six final TRST
  cases pass, covering overwrite, truncate, rename, delete, create and comment.
  TRST recovery is also verified with the original handler. Seven further
  original-handler recovery cases pass for SFS journals generated by the portable
  core. Thus 87 distinct compatibility/recovery cases pass in total. The broad
  `commit-cuts.log` includes six earlier TRST cases; they are replaced, not
  supplemented, by the final-hash six-case `commit-cuts-final-trst.log`.
- **Performance:** five complete CPU profiles pass their functional checks;
  the ten Fast/Chip original-handler parity gates fail only their speed assertions.
  Eight supported-discard diagnostic reruns also pass; they repeat cases already
  included in the 68-case matrix. Logs: `profile-final.log`, `parity.log`,
  `trim-profile.log`.

Logs are under `artifacts/perf-gaps`. Physical controller/SSD, real DMA bus,
AmigaOS 2.0 hardware and physical media replacement qualification are unavailable
here and are not counted as passes.

Added host cases cover OFS run publication with failure at every write, checksum
and pointer-table verification across bitmap/table boundaries, independent
bit-by-bit bitmap counts, SFS/SFS2 verification-tag separation, queue replacement
and sorting, expiring read-only views, and occupied journal searches across
bitmap pages. Existing failure/recovery and corruption suites remain required.

## Candidate identity and reproduction

All four handlers build for 68000 using CILica 0.20.2-preview with scalar
replacement enabled and inlining disabled. No new permanent RAM is allocated
by these optimizations. Native sizes include opt-in automatic TRIM:

| Handler | Before bytes | After bytes | SHA-256 |
| --- | ---: | ---: | --- |
| CopperFFS | 80,704 | 81,700 | `77535A1415F040580432665A068D407AB046FF565954E0052EC81E908DFF8915` |
| CopperSFS SFS | 155,812 | 155,812 | `1D95C19D4AD0D41DEEE4DF67253741A538A01C640E60362EB5206E79CD2FED26` |
| CopperSFS SFS2 CSUN | 250,760 | 253,528 | `31C41834DBB714C57E0D39D9D4F2A64A7CE18BA0A4C1F7F0C96D61EADC97CF82` |
| CopperSFS SFS2 TRST | 206,612 | 206,952 | `B25C122A18E791AB4A010BC0DFCC7BA96BB6A6FFF614E2BA386747F3AD947953` |

The manifest selects `prototype/ffs`, `prototype/sfs`, `prototype/sfs2-csun`
and `word-scan/sfs2-trst` under `artifacts/perf-gaps`. The rejected `borrow`
build and the earlier `prototype/sfs2-trst` are not the final candidate.
The final TRST-only word-search change is unreachable in the CSUN/SFS handlers.

Build with `build/Build-Handler.ps1 -AutoTrim`, distinct artifact roots and the
format flags documented in [automatic-trim.md](automatic-trim.md). Run Release
solution build/tests and CopperStart's buffer/geometry/size native replays with
`-p:CopperStartNativeSfs2=true`. Instruction tests use `-p:CopperFsAutoTrim=true`.

Supply the local ROM through `COPPERSHARP_KICKSTART31_ROM`. Set
`COPPERFS_CORRECTNESS_MANIFEST` to the candidate manifest; use the two
`ReleaseCandidate*StaysOnParWithOriginalHandler` methods for acceptance and
`ProfileReleaseWorkload` with `COPPERFS_PERFORMANCE_PROFILE=1` for diagnostics.
Supported-device probes use `COPPERFS_AUTOTRIM_MANIFEST` and optionally
`COPPERFS_TRIM_PROFILE=1`; keep profiling disabled for acceptance timing.
Commit-cut tests additionally require `COPPERFS_SFS2_TRST_HANDLER`,
`COPPERFS_SFS2_CSUN_HANDLER` and `COPPERFS_ORIGINAL_SFS`.

No release package, ROM, commit or publication is produced by this work.
