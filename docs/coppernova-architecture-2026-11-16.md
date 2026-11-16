# CopperNova architecture experiment, 16 November 2026

The performance objective is active and unmet. This isolated worktree starts
from the qualified CPX4 checkpoint plus the V26 source patch. The first three candidates
changes buffered retirement, with an explicit opt-in and unchanged default
portable policy. It does not change the on-disk format or native allocation bound.
The native handler opts into the experimental policy; it is not a release.
The subsequent CPX5 experiments use an explicit incompatible format and separate
fixtures; they do not convert CPX4 media.

## Four-barrier retirement

1. Seal and persist data, journal images, header and descriptors.
2. Persist the new commit record.
3. Install and persist home metadata pages.
4. Persist one alternating checkpoint, selected by transaction parity.

The explicit flush operation retains its final device drain. Successful flush,
unmount and shutdown still require persistence. An interrupted ordinary buffered
write can lose its unpublished metadata. Existing in-place overwrite limitations
are unchanged. Native execution remains single-owner and allocation-free after
startup; policy state uses the existing four-byte gap at mount offset 4016.

## Safety argument to test

The old commit record in a reused journal slot is at most the durable checkpoint
generation. Recovery ignores it while new images/descriptors are written. A new
commit record cannot authenticate a transaction before its staged data and images
are durable. Therefore the old-record invalidation write/barrier is unnecessary
for this buffered protocol. No home write precedes the durable new commit.

Only the checkpoint that is older than the current parity-selected checkpoint is
overwritten. The current checkpoint remains intact if that write tears. Before
checkpoint persistence, recovery replays the durable commit; afterward it ignores
that commit as retired. Two valid unequal checkpoints are normal for this policy.
Mount repairs an invalid copy, a legacy transition with its newer checkpoint in
the wrong parity slot, or checkpoints left behind by actual journal replay.
Read-only mounts retain their existing refusal to replay committed transactions.

The first tests cover every sector boundary and both sides of barriers across
three generations, including journal-slot reuse, with absent, reversed, partial
and torn persistence. Independent ownership and payload checks remain required.
Measurements and further crash/compatibility coverage are pending; this argument
does not by itself qualify the implementation or establish a performance win.

## Measured CPX4 candidates

All rows are single unprofiled screening trials using the existing runner,
workloads, machine and original-handler inputs. Guest and independent persisted
ownership/payload checks pass. Lower emulated bus cycles are better.

| Candidate | Mixed total | Streaming total | Observed focused coverage |
|---|---:|---:|---|
| V26 baseline | 6,859,910 | 374,415,580 | Prior report |
| A1, four-barrier retirement | 6,782,720 | 374,378,340 | 6 host cases |
| A2, bounded reserve validation batches | 6,644,560 | 374,170,810 | 14 host; 7 compiled stack/core |
| A3, bounded CPU continuation batches | 6,384,830 | 373,883,330 | 14 host; 7 compiled stack/core |

The original FFS reference medians remain 2,782,595 mixed and 367,442,910
streaming cycles; SFS 1.279 medians are 3,918,700 and 369,862,555. A3 still
misses both targets. Full native, separate full-ROM and physical-media coverage
have not been established for these prototypes. The existing qualified V7
checkpoint has not been replaced in the user's original workspace.

A2 validates up to sixteen reserve entries in one CPU turn when their bitmap is
already authenticated. It preserves duplicate checks and yields at bitmap I/O.
A3 optionally batches four engine CPU phases and four dispatcher CPU visits;
I/O, completion, failure and cancellation boundaries remain visible. Both are
bounded and retain all persistent execution state in caller-owned memory.

## New-format work

The [CPX5 shadow-publication design](cpx5-shadow-publication-design.md) prototypes
paired metadata, a persistent selector tree and three checkpoints. Its protocol
uses two publication barriers and an explicit mapping root. It is now connected
to the filesystem handler in this experimental branch. An independent formatter
and read-only decoder extend the reference project without a production-core
dependency. Ownership checks reserve both copies of every metadata allocation.
The mapping region starts at 512 KiB: the earlier 256 KiB proposal collided with
the final group-zero bitmap at 4 KiB metadata size and was corrected before use.

| CPX5 candidate | Mixed total | Streaming total | Result |
|---|---:|---:|---|
| A4, first integration | excluded | excluded | Guest reopen failed after remount; persisted images independently valid |
| A5, corrected nested continuation | 9,773,520 | 378,005,590 | Guest, ownership and complete payload checks pass |
| A6, retained geometry and authenticated map cache | 8,415,830 | 376,429,610 | Guest, ownership and complete payload checks pass |
| A7, geometry only in mapping phases and admitted owned publication | 8,179,710 | 376,099,050 | Guest, ownership and complete payload checks pass |
| A8, checkpoint volume controls | 8,238,830 | 376,048,480 | Guest, ownership and complete payload checks pass |
| A9, cached controls and discard on idle | 7,524,330 | 375,185,490 | Guest, ownership and complete payload checks pass |

A5 is a functional integration checkpoint, not a performance improvement. It
uses the same guest probe bytes as A3 (SHA-256
`0984E37B49A652D20E62ED24A524713C228E1071B8422A5676A78E177C0AD5B1`
for mixed). A rebuilt-runner A3 control measured 6,384,370 mixed cycles versus
6,384,830 previously. Reference handler comparisons must be repeated before any
acceptance claim. The A4 defect was loss of a caller's nested phase translation
across the inserted map-read continuation. Host and compiled tests now explicitly
look up a name after remount, in addition to reading by object ID.

A5 profiling identifies repeated geometry admission and uncached authenticated
map traversal as substantial new CPU costs. A6 retains validated geometry in
caller storage and adds two authenticated map-cache pages, invalidated after
publication and recovery. An internal engine-owned publication path also avoids
checking a just-sealed pinned image twice; the public publication API retains its
checksum admission. A6 remains slower than A3 and both references. Its 19 focused
compiled component/filesystem cases pass with zero managed allocation sites and
the 4 KiB per-instruction stack guard. Further publication and geometry-view
overhead remains visible in the CPU profile.

A7 restores geometry only for mapping work. The engine validates every owned
image's header/target/payload and seals it; the map writer admits unique physical
pairs and buffer ranges. Internal publication then avoids repeating public
descriptor admission. Public untrusted publication still performs its full checks.
The focused shadow host set passes 36 cases; five additional cancellation cases
cover map reads/writes, ordinary metadata writes and both barriers, followed by
quiescent remount and independent ownership validation. Both compiled filesystem
cases pass again. This still misses the performance target substantially.

A8 introduces an explicit CPX5 feature value 2. Its checkpoint authenticates the
complete volume state and 32-entry metadata reserve list, alongside the mapping
root. These occupy 496 bytes including the common header, so separate control
page writes and selected-copy reads are unnecessary. Feature value 1 remains
readable and writable through the earlier paired-control path. The independent
formatter/decoder and production reader agree on the layout without sharing the
implementation. The existing object tree and 1 KiB metadata size remain.

The A8 shadow host set passes 48 cases and its 19 focused compiled cases pass.
New cases include state-only publication with no ordinary metadata writes, all
sector/barrier cuts for that path, ownership-state extensions, legacy fixtures,
and appended context storage with the compact four-page cache. The first A8
mixed trial regresses because synthesized controls do not populate the cache
used by fast-create planning. A9 corrects that integration and moves optional
post-flush discard to the existing idle-maintenance path. Its 49 focused host
cases pass, including a DOS packet check that flush persists writes and later
idle maintenance still discards free space. A9 still trails A3 and both references.
The full host snapshot passes 539 CPX, 438 FFS, 1,343 SFS, 18 Amiga and 32 storage
cases, with three SFS media skips. The native snapshot has 90 passes, three
startup-fixture failures and six ROM skips. The failures were traced to the fake
Exec allocator returning arena `0x40008` inside the expanded handler code range
`0x10000..0x49044`. The fixture now derives a nonoverlapping arena and emulates
MEMF_CLEAR; all seven DMA allocation/cleanup cases pass on replay. Four focused
compiled filesystem cases now also cover control-only publication. A complete
solution recheck passed 2,465 cases, with zero failures and nine skips, after that
fixture correction. Its native portion passed 95 cases with six ROM skips.
CopperStart's replay
suite was rebuilt against this worktree and passed all 141 selected cases.

An A8 streaming verification profile confirms the existing one-extent read fast
path is used for all 32 reads. No idle-maintenance operation runs during that
phase; timer polling is a small part of the measured handler CPU time. This
rules out background discard as the principal streaming-read bottleneck.

The initial generation-only selector failed a new crash/retry regression: an
abandoned page could reappear when a different transaction reused its generation.
Explicit map reachability fixes that regression. The prototype now has a bounded
wide-partition map geometry, map reader and postorder map writer. The writer keeps
at most one caller-owned page per map level; it can borrow retired undo pages
without allocating memory during execution. Map writes precede the publication
kernel's first barrier, so no third barrier is introduced.

The new-format foundation passes 21 focused host cases and 17 compiled MC68000
cases using the standalone handler's HUNK/pass profile. These include actual
mapping-tree publication, restart with a different write set, all checkpoint
survivor slots, cancellation, short completions, wide geometry, and reuse of
quiescent mapping scratch for publication. Every compiled case checks a 4 KiB
stack bound and zero managed allocation sites. These are component tests;
these component cases alone are not full-filesystem coverage.

The first integrated backend additionally passes 14 focused host cases, including
all four allocation-unit sizes, every sector/barrier cut across three successive
publications under five persistence patterns, and the two-barrier count plus
explicit final device drain. Two compiled full-core cases create/write/flush,
remount, look up and read the file with a 4 KiB stack guard and no managed
allocation sites. A6's focused host run has 35 passes plus a separate four-generation
map-cache selector-reuse regression. A full CPX host snapshot before the phase
fix and caching changes passed 523 cases in 8 minutes 9 seconds.

The Release solution snapshot before the shared-checksum bridge passed 2,432
tests across six suites, with nine skips and zero failures. That run included
511 CPX host cases and 90 compiled CPX cases; six opted-in ROM theories were
skipped and are not passes. The subsequent shared-checksum changes separately
pass the 21 host and 17 HUNK component cases, including initialized table use.
CopperStart's unchanged geometry/buffer replay binary was rerun: 141 passes.
No full-ROM campaign or physical-media qualification is claimed for this branch.

A10 introduces feature 3 with separate direct inode pages and a two-bit inode
catalogue in the authenticated selector map. Full 64-bit IDs encode a slot hint
and serial; owner and record IDs must match exactly, including after slot reuse.
The independent formatter seeds the root catalogue at generation one. Its
read-only decoder enumerates authenticated inode flags, retaining complete
namespace, allocation ownership and payload verification without trusting raw
data as metadata. Features 1 and 2 retain their original decoding and mutation
paths. This candidate retains metadata pages of at least 1 KiB.

A10 passes 31 focused host and six compiled filesystem cases, including crash
and changed-write-set retry, deletion, stale identities, 4 KiB stack guards and
zero managed allocation sites. It regresses to 9,853,040 mixed and 376,807,640
streaming cycles because it declines the previous warm create path. A11 restores
a bounded create plan for direct inodes and retains identity geometry across
steps. It also constructs CRC tables from fixed polynomial basis values and
prepares a discovery checksum table in unused caller-owned scratch; all on-disk
checksum rules remain the same. Independent polynomial division validates every
table entry. A11 passes 93 focused host, three additional checksum and one
directory split/retirement/deletion case, plus 13 compiled filesystem/DMA cases.

A11 single unprofiled screening results are **7,550,000 mixed** and
**375,708,250 streaming** cycles. Both guest and persisted payload/ownership
checks pass. These results still lose to both references and the earlier A3
CPX4 candidate. The performance target remains unmet. Diagnostic profiles show
cost spread across page copying, continuation/publication bookkeeping, selected
map traversal and mount validation; the first direct-inode integration is not
an accepted replacement. Separate full-ROM and physical-media gates have not
been run for these candidates.

The A11 full Release snapshot subsequently passed **2,484 tests**, with zero
failures and nine media/ROM skips. This includes 553 CPX host and 100 native
cases. CopperStart's rebuilt geometry/buffer replay passed 141 cases separately.

A12 adds explicit feature 4 for 512-byte metadata. It uses 2,048-bit bitmap
windows and 32 bitmap pages per allocation group. Existing features keep their
original geometry. Full-length external names and link targets use authenticated
type-22 continuation pages, bound to the head address, owner and generation.
Reads copy at most 64 string bytes per continuation turn; deletion releases both
pages. Namespace admission accounts for the direct-inode layout and fragments.
The independent decoder verifies both fragments and counts both allocations.

Focused checks pass: 117 geometry/buffering/trim cases, 172 broader namespace and
blob cases, 10 direct-filesystem cases, and eight compiled filesystem cases with
4 KiB stack guards and zero managed allocation sites. The direct set includes
255-character Latin-1 names, 444/445/768-byte link targets, crash/retry with a
different write set, and 2 MiB streaming across bitmap boundaries followed by
truncation. The full Release build passes; its full test snapshot is running.

A12 single unprofiled results are **7,349,570 mixed** and **376,507,650 streaming**
cycles, both with full guest and persisted-image checks passing. Smaller pages
reduce bytes transferred but do not meet the target. A streaming profile exposes
about 5,000 bitmap-geometry helper calls while the allocator rescans occupied
windows for the next file. Allocation search policy and publication overhead
still need work. Separate full-ROM and physical-media gates remain unrun.

An A11 68000 compiler-size-profile alternative passed workload checks but was
slower. A 68020-targeted alternative failed both workloads with DOS error 115;
those failed runs are excluded from performance comparisons.

The A12 full snapshot passed 559 CPX host cases, but 13 native startup cases
failed against obsolete fixture assumptions: discovery now starts at the device
sector size, and startup owns an unmounted engine. Corrected fixtures also retain
an unsupported-command injection in a genuinely split request. All 28 startup
geometry and lifecycle cases pass on the separate recheck; the original failed
snapshot remains recorded and is not reported as a full pass.

A13 retains a mount-owned advisory next-fit position, validates bitmap ownership
at every use, wraps before declaring exhaustion, and skips occupied bitmap words
without per-bit geometry helper calls. The hint can be lost or moved by rollback
without implying that a unit is free. Its fixed eight bytes are supplied by the
caller with the shadow context. Focused host checks pass 73 cases plus two
allocated-suffix/out-of-range hint cases; eight compiled filesystem cases and
141 rebuilt CopperStart replay cases also pass. Its full Release snapshot passes
2,494 cases, with nine skipped gates. Native coverage is 102 passed and six ROM
gates skipped; those skips are not full-ROM passes.

A13 single unprofiled results are **7,345,120 mixed** and **373,975,370 streaming**
cycles. Both checks pass. The streaming allocator no longer scans thousands of
occupied bits for the next file. The target remains unmet; A3 CPX4 is still
slightly faster overall in streaming, and both references still win.

A14 introduces feature 5: a 64-entry selection catalogue in 1 KiB checkpoints,
with metadata remaining at 512 bytes. Small volumes avoid separate radix-map
reads and rewrites; capacity overflow promotes to the existing map without
changing the two publication barriers. Both workload checks pass at **6,571,260
mixed** and **373,108,370 streaming** cycles. Neither target is met. Eleven
compiled filesystem cases pass with zero allocation sites and 4 KiB stack
checking. A 70-file host case proves promotion and independent ownership; the
initial fixed-count power-cut fixture remained inline and does not qualify
promotion recovery. A replacement locates the actual capacity boundary.

A15 removes full-page copying when editing an already-owned dirty inode while
retaining per-operation undo. Repeated scalar state accesses are expanded in
selected new cursor paths, and invariant checkpoint dimensions are hoisted out
of loops. Seventy-one focused host cases pass. The full Release snapshot has completed. The snapshot includes the fixed-count assertion
failure; it will not be reported as a full pass. A corrected promotion-cut
fixture and 70-file compiled case are qualified separately. CopperStart's
rebuilt native replay passes 141 cases. Full-ROM and physical-media gates have
not been run for these features.
A15 single unprofiled results are **6,060,570 mixed** and **372,682,410 streaming**
cycles. Both unchanged guest workloads and persisted full-data/ownership checks
pass, producing the same final image hashes as A14. The goal remains unmet.
The corrected fault tests cover the actual catalogue-to-map transition, changed
retry writes, and an acknowledged flush followed by a failed publication; both
cases pass. A separate compiled 70-file case explicitly proves map promotion,
independent ownership, zero allocation sites and the 4 KiB stack guard. The full
snapshot's original fixed-count assertion failure remains recorded separately.
The A15 Release build passes (one known nullable warning in the external compiler).
The complete test snapshot records **2,509 passed, one failed, nine skipped**.
CPX host coverage is 572 passed and the one obsolete fixed-count fixture failure;
native coverage is 106 passed and six skipped ROM gates. The corrected dynamic
promotion/acknowledged-flush cases pass separately, as does the compiled 70-file
promotion guard. This is not reported as a clean full-suite pass. No separate
CPX5 full-ROM or physical-media release gate has run.
A16 combines admission for a bounded continuation visit, observing cancellation
before each phase and ending at I/O, failure or final completion. Repeated aligned
reads can reuse authenticated mapping scalars in the existing fast context; no
DOS buffer is retained, and mutation invalidates the capability. Its workloads
pass at **6,000,620 mixed** and **372,442,000 streaming** cycles. The complete
Release snapshot is clean: **2,512 passed, zero failed, nine skipped**, including
575 CPX host and 106 native cases. The six native ROM skips remain unqualified.
Rebuilt CopperStart replay passes 141 cases.

A17 moves the coarse 30-second idle-maintenance deadline from `MicroHz` to
`VBlank`; record-lock timers and explicit durability barriers are unchanged.
Task-attributed profiling placed most extra streaming ROM time in the caller,
and little of that was inside DOS.Read. Hot ROM code reads CIA timer registers.
The timer change reduces streaming by 1,837,080 cycles to **370,604,920**. Mixed
passes at **5,969,360** cycles. Both guest and independent persisted-image checks
pass and final image hashes match A16. FFS and SFS still win both workloads.
The A17 full Release snapshot passes 2,512 tests, with nine skips and zero
failures; rebuilt CopperStart replay passes 141 cases.

A18 supplies an explicit portable memory-copy primitive. Its native adapter uses
Exec bulk copies for disjoint ranges of at least 64 bytes and preserves the
original ordered scalar behavior for overlap and small ranges. Nine native cases
check alignment, overlap, self-copy, empty ranges, allocation compatibility and
the 4 KiB stack guard. The experimental managed memory interface gains a required
method; packet ABI, arena layout and disk format are unchanged.
The unchanged workloads pass at **5,952,250 mixed** and **370,587,210 streaming**
cycles, with identical persisted image hashes to A17. The gain is small; both
references still win both workloads. Full Release coverage is **2,521 passed,
zero failed, nine skipped**, including 575 CPX host and 115 native cases.
The build succeeds with the existing nullable warning in the external compiler.
Rebuilt CopperStart replay passes 141 cases. No separate CPX5 full-ROM or
physical-media release gate is claimed.

A19 retires the suffix of a cached single written extent in one bounded operation.
Admission checks capacity, allocation bits, counters and inode identity before
editing four owned images. Existing publication makes those images atomic; the
buffered allocation tracker prevents reuse of durable freed data until retirement.
Complex layouts retain the ordinary orphan continuation. Four host cases cover
growth through the partial EOF and every sector/barrier cut under five persistence
schedules, with changed retries and independent ownership/payload checks. Fourteen
compiled filesystem cases pass the 4 KiB stack guard; filesystem replay passes 141.
Mixed operations improve by 291,150 cycles to **5,661,100**; streaming passes at
**370,586,020**. Both persisted image hashes match A18. Both reference filesystems
still win both workloads. The complete Release snapshot passes **2,527 tests**,
with zero failures and nine skips, including 579 CPX host and 117 native cases.
An accidentally broader CopperStart run was interrupted after an unrelated desktop
compiler error (`C68K0004`, external `Install` reference); it is not a pass.

A20 combines metadata alignment, reserved-prefix and exclusive-end admission in
one range check. Reserve validation reuses bitmap window identity without
reconstructing two bitmap addresses per entry. Six arithmetic boundary theories
cover legacy and shadow geometries at small and large capacities, including
overflow; all 36 focused host and 20 focused native cases pass.
The unchanged workloads pass at **5,530,480 mixed** and **370,413,290 streaming**
cycles, preserving both persisted image hashes. These remain screening results;
both original handlers still win both workloads. No complete Release rerun is
claimed for this intermediate candidate.

A21 expands pure scalar address/value helpers in metadata, inode, checkpoint and
DOS paths. Computed arguments with calls, indexing or mutations retain their
original evaluation. The default 68000 build passes at **5,466,380 mixed** and
**370,327,190 streaming** cycles with unchanged persisted image hashes. Focused
coverage passes 36 host and 27 compiled filesystem/engine/event-loop cases.
The full CPX host snapshot passes 585 cases; no complete solution rerun is claimed.
A separate 68020 build passes both workloads at 5,840,740 and 370,941,180 cycles,
but is slower and lacks separate native stack qualification. It is not retained
as the default. The original performance objective remains unmet.

A22 replaces per-byte bitmap range work with aligned words and masks on the
edges. Allocation changes validate the complete range before editing it. Four
randomized host theories compare against a bit-by-bit oracle and verify every
outside byte; all 22 focused host and 16 compiled filesystem cases pass. Two
compiled cases append 1 MiB through a bitmap-window boundary, flush, remount and
verify every byte with the 4 KiB stack guard. Workloads pass at **5,465,230 mixed**
and **370,292,030 streaming** cycles, retaining the same media hashes. The full
CPX host run passes 589 cases. The performance objective remains unmet.

A23 rebuilds the complete zeroed checkpoint header and copies the admitted owned
payload in bulk, clearing the remaining tail. The resulting media hashes are
unchanged. Mixed passes at **5,447,890** cycles; streaming passes at **370,301,010**,
8,980 cycles worse than A22's screening result. Focused host coverage passes 39
cases, full CPX host coverage passes 589, and rebuilt filesystem replay passes 141.
The complete Release snapshot passes **2,539 tests**, with zero failures and nine
skips, including 119 native cases and six unrun ROM gates. Automatic clearing is a
separate diagnostic variant: 5,437,990 mixed and 370,313,400 streaming cycles.
It is not retained as the default; the build script records the selected policy.

A separate **legacy CPX4** Kickstart gate fails at record-lock stage 172 with DOS
error 103. The stack guard is intact (980-byte peak), operation-time Exec allocation
count is zero, and 68,304 bytes remain free. The failed RAM image shows discovery's
prepared CRC table in the final record-lock pool. Startup's assumption that this
pool still contains only cleared allocation bytes is invalid for small metadata
geometries. This is a failed gate, with a bounded pool-initialization fix in
progress. It does not qualify CPX5 full-ROM or physical-media coverage.

A24 compares packed catalogue words directly, preserving all 62-bit keys and the
existing checkpoint layout. Two dictionary-model theories cover 1,200 updates,
lookups and removals each, including wide keys and the 64-entry capacity; all
22 focused host cases pass. Native startup now clears the final DOS resource pool
in sixteen bounded 1 KiB visits after discovery and before publishing resources.
Cancellation remains available during those visits. All 36 focused native cases
pass, including four pool-geometry/guard cases. The exact legacy CPX4 Kickstart
gate that failed at stage 172 now passes with matched source inputs, zero
operation-time Exec allocations and an intact 4 KiB stack guard.
The unchanged CPX5 workloads pass at **5,423,230 mixed** and **370,316,940 streaming**
cycles, with unchanged media hashes. Both original handlers still win both
workloads. No full CPX5 ROM or physical-media qualification is claimed.
