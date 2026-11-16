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

A25 admits classic absolute resize packets directly when a healthy idle engine
already owns the cached regular-file record. Normal resize validation still
enforces protection and other open handles' positions. DOS resource scans reuse
their pool base. Warm-cache protection tests exposed an existing attribute-write
dependency on stale page scratch; buffered direct inodes now update their owned
record with undo. All 595 CPX host cases, 29 focused compiled-native cases and 141
rebuilt CopperStart filesystem replay cases pass. The Release solution builds;
its complete test snapshot passes 2,551 cases, with zero failures and nine skips
(125 compiled-native cases; six unrun ROM gates). The separate legacy CPX4 allocation gate
passes with matched sources and zero operation-time allocation.
Both unchanged workloads pass with identical persisted media hashes at
**5,390,960 mixed** and **370,201,990 streaming** cycles. Both references remain
faster; these single screening trials do not establish acceptance. CPX5 full-ROM
and physical-media gates remain unrun.

A26 makes the pending arena request a borrowed native transport view. Its six
words retain their existing layout; the last word is the next-token counter,
which equals the current completion token while the engine waits. The native
consumer captures identity before servicing storage and retains no reference
across visits. Host request-driving checks compare every published descriptor
word, and compiled tests exercise split/bounced transfers, delayed reads and
unsupported discard through both APIs. All 63 focused host and 37 native cases
pass; four additional borrowed-path cancellation cases pass. The legacy CPX4
allocation gate passes with matching sources and an intact 4 KiB stack guard.
Unchanged workloads pass at **5,333,610 mixed** and **370,095,910 streaming** cycles
with identical persisted image hashes. The performance objective remains unmet.

A fresh two-trial comparison for A26 passes all six trials in each workload.
Mixed medians are **5,340,870 CPX5**, **2,782,740 FFS** and **3,931,355 SFS 1.279**;
streaming medians are **370,095,530**, **367,442,715** and **369,856,955** cycles.
The mixed guest probe retains SHA-256
`0984E37B49A652D20E62ED24A524713C228E1071B8422A5676A78E177C0AD5B1`.

A27 routes repeated publication visits through a smaller kernel frame and checks
recovery phase before computing unrelated geometry. Page clearing uses bounded
groups of eight ordered stores with exact tails. All 84 focused host and 29
compiled filesystem/engine/event-loop cases pass. Both unchanged workloads pass
with identical media hashes at **5,266,590 mixed** and **370,017,610 streaming**
cycles. The legacy CPX4 ROM allocation gate passes.

The new explicit CPX5 feature-5 ROM allocation probe **fails** at startup with
DOS error 103 on 512 KiB Chip RAM and no Fast RAM. Its stack guard is intact
(936-byte peak); no operation-time allocation occurs because mount never finishes.
The compact buffer and shadow context require 69,616 bytes, exceeding the 68,360
bytes remaining. This is a failed CPX5 platform gate, not a qualification pass.
The fixture is built through the portable engine and independently checked;
its two aliases are long Latin-1 names, and it does not claim UTF-8 import coverage.
The original legacy fixture retains its UTF-8 entry. CPX5 physical-media coverage
remains unrun. The performance objective remains unmet.

A28 adds an explicit P=512 workspace profile. A caller that owns the full
maximum-size mount arena can lend its unused 21 KiB tail to the 42-page undo
bank. Snapshot and cache storage remain in the smaller external allocation;
geometry changes revoke the lease before rebuilding pointers. External storage
including shadow context falls from 71,728 to 50,224 bytes (compact: 69,616 to
48,112). Journal capacity, publication ordering and the four-phase budget stay
the same. Admission rejects overlapping or insufficient regions before changes.

All 67 focused host cases and 30 native cases pass, including byte-for-byte
comparison against the contiguous profile, sector-cut retries, 1 MiB appends and
catalogue promotion with guards around both regions. The full Release snapshot
passes 2,564 cases with zero failures and ten skips; rebuilt CopperStart replay
passes 141. A final startup guard prevents re-admitting an already enabled
workspace at publication, preserving its warmed cache. That final binary passes
both the legacy and **CPX5 feature-5 ROM allocation gates on 512 KiB Chip RAM and
no Fast RAM**, with matching sources, intact 4 KiB stack guards and zero
operation-time Exec allocations. These remain partial packet/allocation gates;
they do not qualify every ROM workload or physical media.

The final unchanged screening workloads pass at **5,277,530 mixed** and
**370,030,470 streaming** cycles, with the earlier persisted image hashes
restored. The initial cold-cache variant was slower and changed only catalogue
entry order and corresponding checkpoint checksums; its evidence is retained.
The full solution/replay snapshot precedes the final warm-cache guard; the two
ROM probes and workload screens cover that final native binary. Both reference
filesystems remain faster in both workloads.

A29 publishes the engine's already admitted, sealed descriptors through a direct
bounded continuation. The engine owns the single completion token throughout;
the standalone public publication kernel keeps its original admission API.
The metadata barrier precedes both non-surviving checkpoint writes, and their
persistence barrier precedes generation advancement and group retirement.
The native handler shrinks from 270,084 to 263,736 bytes.

All 602 CPX host cases, 30 focused native cases and 141 rebuilt CopperStart replay
cases pass. The Release solution builds and passes 2,564 tests with ten skips.
Both legacy and CPX5 ROM allocation probes pass on 512 KiB Chip RAM, no Fast RAM,
with matched source inputs, intact 4 KiB stack guards and no operation-time
Exec allocations. The unchanged workloads pass with identical image hashes at
**5,194,870 mixed** and **369,967,260 streaming** cycles. Both reference handlers
still win both workloads; the objective remains unmet.

A30 computes the second newly generated checkpoint's checksum from its address
delta, after the first write quiesces. An independent full-checksum oracle covers
512–4096-byte pages and wide, equal and extreme addresses. It changes no read
validation, disk bytes or persistence barriers. All 45 focused host cases, four
updated oracle theories, 30 native cases and both ROM allocation gates pass.
The unchanged screens pass at **5,155,060 mixed** and **369,880,490 streaming**.

A31 replaces separate read/modify/write visits with one contiguous window for
small unaligned overwrites wholly inside an authenticated written extent. The
window is bounded by three metadata pages; aliased input retains the existing
ordering. Progress is reported only after the window write succeeds. Host checks
cover surrounding bytes, alias fallback, cancellation and failed reads; compiled
tests cover both geometries, format profiles, arena undo and stack/workspace
guards. All 19 focused host and 34 native cases pass, as do both ROM allocation
gates with zero operation-time Exec allocations. The Release build and 141
rebuilt CopperStart replay cases pass. The full solution passes 2,579 tests with
zero failures and ten skips (613 CPX host; 135 native with seven ROM skips).

Unchanged A31 screens pass at **5,007,170 mixed** and **369,770,130 streaming**
with identical persisted images. Streaming is slightly faster than the latest
SFS 1.279 median (369,856,955), but FFS still wins both workloads (2,782,740 mixed;
367,442,715 streaming). These single screens do not establish acceptance. The
performance objective remains active and unmet. The ROM probes cover partial
packet/allocation behavior; full ROM workload and physical-media qualification
remain unrun.

Further unchanged screening results are below. Every row passes guest and
independent persisted ownership/payload checks with the earlier image hashes.
Each binary also passes both separate ROM allocation gates with matching source
inputs, intact 4 KiB stack guards and zero operation-time Exec allocations.

| Candidate | Mixed total | Streaming total | Focused host / native cases |
|---|---:|---:|---:|
| A32, early owned append admission and general file-frame separation | 5,036,500 | 369,703,890 | 52 / 34 |
| A33, bounded validated descriptor hints | 5,040,960 | 369,746,220 | 72 / 34 |
| A34, adjacent checkpoint read batch and recovery frame separation | 5,010,360 | 369,679,530 | 54 / 34 |
| A35, scalar checked I/O address and exclusive-end arithmetic | 4,985,520 | 369,582,570 | 58 / 48 |

A32 improves streaming but regresses mixed work. A33's hints show no measured
gain and are omitted from the next retained candidate. A34 borrows the idle
42-page undo bank only during quiescent recovery; every checkpoint still has its
own checksum, identity, generation, catalogue and consistency checks. Six host
theories cover every page size, each corrupt checkpoint copy, the arena lease,
workspace guards and mutation after remount. Unbuffered execution retains
independent checkpoint reads.

A35 replaces wide arithmetic aggregate temporaries with explicit checked high
and low words. An independent big-integer oracle covers 10,500 random/boundary
cases across seven geometry combinations, including carries, overflow, capacity
ends and malformed spans. Native coverage includes DMA and storage cancellation.
These intermediate candidates did not receive separate full solution runs.
FFS remains faster in both workloads; the performance goal remains unmet.

A36 retains early append admission, checkpoint batching and checked scalar I/O,
while dropping the unhelpful descriptor hints and general file-frame split.
Packet transfers, mapping-tree completion callbacks and slow split transfers now
have separate continuations. Compiler reports confirm common packet and mapping
callback frames fall from 208/184 bytes to zero, transport from 224 to 80 bytes,
and issue from 44 to 16 bytes. Bounds and the four-phase budget are unchanged.

All 207 focused host cases, 630 CPX host cases in the full solution snapshot,
48 native cases and 141 rebuilt CopperStart replay cases pass. The Release
solution builds and passes 2,596 tests with zero failures and ten skips. Both source-matched ROM
allocation gates pass with intact 4 KiB stack guards and zero operation-time
Exec allocations. The unchanged screens pass at **4,954,590 mixed** and
**369,493,380 streaming** cycles, with identical persisted images. FFS still wins
both workloads, and mixed work also trails SFS 1.279. This is further measured
progress, not completion of the performance objective.

A fresh two-trial comparison passes every arm in both workloads. A36 medians
are **4,954,560 mixed / 369,495,375 streaming**, FFS 40.1 medians are
**2,783,360 / 367,444,370**, and SFS 1.279 medians are
**3,931,305 / 369,923,045**. SFS streaming varies from 369,862,470 to
369,983,620; CPX beats both SFS trials there, while FFS wins both workloads.

| Further candidate | Mixed total | Streaming total |
|---|---:|---:|
| A37, cached classic seek/resize bounds and grouped CRC zero scan | 4,954,240 | 369,497,530 |
| A38, owned sealing frame and matching-checkpoint checksum proof | 4,922,850 | 369,469,260 |
| A39, scalar classic seek bounds | 4,921,430 | 369,462,890 |

A37's total performance is essentially unchanged. Its new warm/cold cache tests
cover stale operation scratch, every seek basis, negative offsets, bounds,
protection and relative resize, followed by full persisted payload verification.
A38 keeps all owned-page validation while reducing the sealing frame from 348
to 28 bytes. For an incoming checkpoint, it compares every byte except the
checksum and self-address against a previously fully validated copy. Only exact
body equality permits the linear address CRC transform; the stored incoming
checksum must still match. Every differing body/header takes the full checksum
path, and subsequent format, catalogue and generation checks are unchanged.
An independent full-checksum oracle covers 7,684 cases, including a bit change at
every byte position for 512–4096-byte pages, with source/input guards preserved.

A39 replaces only classic cached seek arithmetic with checked scalar words;
wide seeks and resize retain the general path. Its 12,000-case big-integer
oracle covers signed extremes, all bases, wide sizes, overflow and unchanged
positions on errors. All 166 focused host, 645 full CPX host, 50 focused native
and 141 rebuilt CopperStart replay cases pass. The Release solution builds and
passes 2,611 tests with zero failures and ten skips. A37 and A38 each pass 50 native cases too.
All three binaries pass the unchanged guest and independent image checks, with
identical media hashes, and both source-matched ROM allocation gates retain
intact 4 KiB stack guards and zero operation-time Exec allocations. These remain
partial ROM gates; physical-media qualification is unrun. FFS still wins both
workloads. The performance objective remains unmet.

| Further candidate | Mixed total | Streaming total |
|---|---:|---:|
| A40, cached object admission | 4,903,320 | 369,509,800 |
| A41, exact small-address CRC contributions | 4,889,710 | 369,496,540 |
| A42, checkpoint recovery/publication frame separation | 4,881,080 | 369,490,550 |
| A43, completed flush reuse | 4,872,590 | 369,483,260 |
| A44, adjacent replacement checkpoint writes | 4,866,490 | 369,476,420 |

These are single unchanged workload screens, not acceptance comparisons. Every
screen passes guest verification and independent persisted ownership/full-payload
checks; both resulting image hashes remain identical to the earlier candidates.
Streaming is still slower than FFS. Mixed work still trails both references.
A40 improves mixed work but regresses streaming relative to A39; later changes
recover part of that regression.

A40 admits an authenticated cached object through a dedicated pending completion
phase. It preserves busy, cancellation, stale identity and cold traversal behavior.
A41 uses exact polynomial CRC contributions for small self-address differences;
random wide differences and other lengths retain the general transform. A42
separates recovery and publication frames from map geometry restoration. None of
these changes remove checksum, ownership or schema validation.

A43 reuses a successfully completed device flush only until the next device
request. The single mount owner observes quiescence; failed completion poisons
the mount and cannot authorize reuse. Reads conservatively break reuse too.
An in-place data write always requires a new flush, even with no dirty metadata.
Both metadata-before-checkpoint and checkpoint-before-retirement barriers remain.
The checkpoint barrier can also satisfy the enclosing ACTION_FLUSH and a following
inhibit or shutdown without intervening I/O. These workloads now issue two device
flush commands rather than four. The standalone publication kernel is unchanged.

A44 writes two adjacent replacement checkpoints in one request when the survivor
is an outer copy. An interior survivor uses the original two requests; recovery
prefers an outer copy only after verifying equal-generation identity. The old
survivor stays untouched until both new copies are persistent. The pair buffer
borrows the caller-owned undo bank only after metadata writes have quiesced.
Five additional cases cover 512/4096-byte units, arena undo, interior survivor
fallback, repeated publications/remounts, self-address/CRC and old-root guards.

Host coverage: A40/A41/A42/A43/A44 pass 219/63/68/207/56 focused cases;
A44 additionally passes the five checkpoint-pair cases. Native coverage: every
candidate passes 50 compiled filesystem/startup/event-loop/transport/resize cases.
A43 and A44 Release builds and 141 rebuilt CopperStart replay cases pass. Their
full solution runs pass 2,633 and 2,638 tests respectively, with zero failures
and ten skips each. Full-ROM coverage is limited to the two
source-matched allocation/packet gates for each binary: all pass with intact
4 KiB stack guards and zero operation-time Exec allocations on 512 KiB Chip RAM.
Those gates do not establish complete ROM workload or physical-media coverage.

| Subsequent experiment | Mixed total | Streaming total | Decision |
|---|---:|---:|---|
| A45, pointer-stride/full-key metadata scans | 4,852,920 | 369,461,220 | Retain |
| A46, zero-word CRC branch | 4,922,790 | 369,552,740 | Reject |
| A48, branchless word CRC | 4,943,060 | 369,516,550 | Reject |

A45 compares the low key word first and advances fixed descriptor/object strides;
the complete high word remains authoritative. A 10,000-query independent oracle
covers duplicate and colliding keys, inactive/shrunken tables and immutable RAM.
It passes 66 focused host cases. A46 and A48 each pass 53 focused host cases.
All three pass 66 compiled native cases and both exact-source ROM allocation
gates, with identical independently checked workload images. The two CRC
experiments remain rejected despite correctness: the isolated 68000/68020 CPU
diagnostic did not predict complete Amiga workload performance. No checksum
algorithm or format change is retained from those experiments.

Each native result belongs to its archived source snapshot. The retained branch
also keeps three earlier arena-undo addressing calls in the legacy journal path;
they had been absent in the scratch producer used for A44/A45. A50 is the exact
rebuild of the retained combination. Its Release solution build and all 2,639
tests pass, with zero failures and ten skips (including seven licensed-ROM test
groups). All 141 rebuilt CopperStart replay cases and both explicit source-matched
ROM allocation gates pass. Physical-media tests remain unrun.

A fresh two-trial A50 comparison passes all six trials in each workload:

| Filesystem | Mixed median cycles | Streaming median cycles |
|---|---:|---:|
| CopperNova / CPX4 handler, A50 retained profile | 4,853,915 | 369,459,855 |
| Original Kickstart FFS 40.1 | 2,782,880 | 367,442,630 |
| Original SFS 1.279 | 3,918,540 | 369,850,835 |

The goal is unmet: FFS wins both workloads, and SFS also wins mixed work. CPX
streaming beats both SFS trials. The main fixed costs remain namespace mutation,
metadata sealing/publication and remount validation. For mixed work, CPX spends
about 503,000 cycles flushing and 639,000 remounting, versus FFS's 20,000 and
109,000. Reducing device commands alone has not removed these CPU costs.

An independent data write-back experiment (A49) adds a fixed caller-owned 8 KiB
bank with bounded sector masks and no packet-time allocation. Its source-matched
screens pass persisted image checks at 5,042,180 / 369,762,880 cycles, but it is
slower than A50. Mixed device read/write commands fall from 27/13 to 25/11.
The added cache validation and state-access calls outweigh that I/O reduction.
It passes 80 focused host cases, 72 compiled native cases, 270 independent
sector/barrier crash images and three randomized model cases. Both ROM allocation
gates pass with the bank allocated on 512 KiB; that fixture did not yet dirty it.
A later sector-limit coherency test exposes an unsigned-window boundary defect.
A49 is not retained. Subsequent experiments fix that boundary, reduce CPU
overhead, constrain the cache to the buffered shadow profile and strengthen the
ROM fixture to require observation of dirty cached data. Their qualification is
still in progress.

The write-back follow-ups remain unretained after measurement:

| Candidate | Mixed cycles | Streaming cycles |
|---|---:|---:|
| A51, checked/scalar data cache | 5,006,650 | 369,715,200 |
| A52, general small-write cache | 5,142,480 | 369,917,590 |
| A53, existing longword copy for even buffers | 4,862,950 | 369,472,660 |

A51/A52 pass 85/86 focused host and 74/75 compiled native cases respectively,
including the sector-limit regression and legacy/unbuffered bypass cases.
Their independent crash checks cover 270/540 recovered images. Both strengthened
512 KiB ROM gates pass with an allocated 8 KiB bank, observed dirty cached data,
1,068-byte peak handler stack, intact guards and zero operation-time Exec
allocations. The stronger fixture writes and reads an existing payload file;
it does not change the comparison benchmarks. Separate full-solution runs were
not performed for these unretained intermediates.

In the mixed mutation profile, A51 spends about 428,000 ROM cycles versus
A50's 336,000, while filesystem code cycles remain close. Four additional
CopyMemQuick calls outweigh the eliminated DMA requests. A53 removes those ROM
calls for even buffers using the existing ordered longword routine; its initial
screens still slightly regress, so it is not retained. Its nine dedicated native
copy cases, 75 focused native cases and both ROM allocation gates pass.

A54 retains the complete schema proof of an already authenticated checkpoint
when another checkpoint matches every byte except its checksum and self address.
The incoming checkpoint still passes its own address-adjusted checksum. Any
changed byte takes the complete validation path. This also avoids copying the
same 1,024-byte control image again. Five independent valid-CRC/invalid-schema
cases reject malformed root payloads, reserve entries and catalogue/conflict
state. The complete checksum byte-fault oracle remains in place.

The A54 single-trial screens pass at **4,827,230 mixed / 369,433,380 streaming
cycles**. The goal remains unmet against the A50 reference medians: FFS
2,782,880 / 367,442,630, and SFS 1.279 3,918,540 / 369,850,835. These are
screening results, not a fresh all-arm acceptance comparison.

A54 Release build and full solution validation pass: **2,644 passed, zero failed,
10 skipped**, including 678 CPX host and 135 compiled native cases. The seven
native licensed-ROM skips are separate from the two source-matched ROM allocation
gates, which pass on 512 KiB Chip RAM, zero Fast RAM and a 68000. The gates cover
part of the packet surface, not complete ROM behavior. All 141 rebuilt CopperStart
filesystem replay cases pass. Physical media remain untested.

A55 replaces large even native copies with pointer-based unrolled stores. Its
4,855,890 / 369,464,620 cycle screens regress relative to A50 and it is not
retained. Twelve dedicated native copy cases, 78 focused native cases and both
ROM allocation gates pass. It has no separate full-solution qualification.

Subsequent CPU experiments keep the benchmark and durability contract unchanged:

| Candidate | Mixed cycles | Streaming cycles | Result |
|---|---:|---:|---|
| A56, direct file continuation dispatch | 4,823,430 | 369,400,120 | Retain |
| A57, wide-helper inline annotations | 4,827,100 | 369,433,410 | Exact same handler bytes as A54; omit |
| A58, reserve validation per bounded turn | 4,785,270 | 369,325,400 | Retain, full qualification passed |
| A59, existing 68020 build option on A58 | 5,111,600 | 369,809,300 | Reject |

A56 routes known file continuations before the general file/tree frame. The
common helper has zero local frame bytes and 16 saved bytes; the general handler
has 116 local frame bytes and 40 saved bytes. Each continuation still consumes
the same phase budget. All 185 focused host cases, 66 compiled native cases and
both source-matched ROM allocation gates pass.

A58 computes invariant geometry and complete 64-bit allocation bounds once per
reserve recovery turn. It retains overflow, exclusive-end, lower-bound, alignment,
duplicate and group-prefix rejection. The turn still processes at most sixteen
entries and yields for bitmap I/O. Aligned bitmap masks check every owned bit;
two independent range theories clear each bit and toggle neighbouring bits across
all admitted span widths and both bitmap window sizes. The final geometry,
reserve and bit-range run passes all 40 cases; the earlier broader focused run
passes 125. Both ROM allocation gates pass with zero operation-time Exec
allocations, intact 4 KiB stack guards, and observed peak stacks of 876/1,068 bytes.
Release solution build and all 141 rebuilt CopperStart replay cases pass. The
66-case focused compiled native run also passes. The full Release solution run
passes **2,646 cases with zero failures and ten skips**, including 680 CPX host
and 135 compiled native cases. The seven native ROM skips remain separate from
the two independently executed ROM allocation gates. Physical media are untested.

The fresh two-trial mixed comparison passes every arm: **CopperNova 4,785,400,
FFS 2,783,360, SFS 1.279 3,921,810 cycles**. The streaming medians are
**CopperNova 369,324,590, FFS 367,442,405, SFS 1.279 369,862,180 cycles**,
again with every trial passing. CopperNova still fails the required performance objective.
A59 targets the benchmark's existing 68020 through the existing build option;
its larger 287,944-byte handler regresses. It has no separate 68020 ROM
qualification and must not inherit a 68000 execution pass.

A60 expands 100 pure wide reads and 96 ordered wide stores in the A56 source,
preserving address/value evaluation order. Its 4,804,960 / 369,368,250 cycle
screens improve on A56 but remain slower than A58. All 185 focused host cases,
66 compiled native cases and both ROM allocation gates pass. It is not retained
as a replacement for A58. A narrower read-only expansion is being measured on
the A58 source separately.

A61 checks whether a supplied metadata pointer already identifies an owned
bank slot before doing the ordinary descriptor scan. Full high/low address,
alignment and active-slot checks preserve correctness, including six collision
and boundary cases. Its 4,796,310 / 369,366,900 cycle screens regress in both
workloads. All 190 other focused host cases, 66 native cases and both ROM gates
pass. The experiment is archived and removed from the retained source.

A62 expands only the 100 pure wide reads on the A58 source. Its screens pass
at 4,780,120 / 369,351,850 cycles: a small mixed gain accompanied by a streaming
regression. It is not retained. All 250 focused host cases and both partial ROM
allocation gates pass; no separate broader native suite or full solution run was
performed for this rejected intermediate.

A63 changes only the existing build option from `ClrPolicy=always` to `auto`
on the exact A58 sources. The compiler and emulator are unchanged. Its
4,782,730 / 369,357,700 cycle screens also trade a small mixed gain for a
streaming regression, so it is not retained. Both actual-HUNK ROM allocation
gates pass. Source qualification comes from A58; no separate instruction suite
was run for this rejected code-generation variant.

A64 requests the existing RAM-folding pass through the CLI. The CLI accepts the
property name but does not forward it; the HUNK is byte-identical to A58.
Its report explicitly inherits A58 results by binary identity. This is not a new
measurement or evidence about the effectiveness of that optimization.

A65 keeps checkpoint selection as a pinned view into the owned recovery batch,
materializing the final winner once. It passes 95 focused host cases, 66 compiled
native cases and both partial ROM allocation gates. Its single-trial screens,
4,790,870 / 369,362,180 cycles, regress against A58 and are not retained.

A66 adds a retained proof for an authenticated control image. It passes 113
focused host cases (including 18 proof cases), 66 compiled native cases and both
partial ROM allocation gates. Its screens pass at 4,797,480 / 369,319,120 cycles;
it is not retained. Subsequent native tracing exposes a workspace collision:
the proof and advisory allocation hint share the same eight bytes. Remount's
hint reset erases the proof before root validation. Final-state host assertions
missed this transient collision. A68 gives them separate storage and tests the
proof at the first checkpoint I/O boundary; its qualification is still pending.
No failed or changed-media validation was bypassed by A66.

A67 applies existing application code-generation options to exact A58 sources.
Its screens pass at 4,775,010 / 369,375,710 cycles, but **both actual-HUNK partial
ROM allocation gates fail at stage 502 with DOS error 213**. It is rejected.
No successful qualification file was produced; the recorded failure evidence
comes from the test output. Neither these options nor the experimental sources
replace A58. Compiler, SDK, emulator and application sources are unchanged.

A68 separates the proof and allocator hint into independent eight-byte slots,
adding eight bytes to the startup shadow lease. No operation allocates memory.
Four new regression cases inspect the proof at the first checkpoint read, after
the allocator hint is reset. All 41 focused host cases and 66 compiled native
cases pass. Both source-exact 512 KiB Chip / zero Fast / 68000 partial ROM gates
pass with zero operation-time Exec allocations, intact guards and unchanged
876/1,068-byte peak stacks. Release build and all 141 rebuilt CopperStart replay
cases pass. The full Release solution run passes 2,680 tests with zero failures and ten skips,
including 714 CPX host cases and 135 compiled native cases. The native suite
takes 21 minutes 20 seconds; its seven ROM skips remain distinct from the two
separately executed partial ROM gates. Physical media are
untested.

Fresh two-trial comparisons pass every arm:

| Workload | CopperNova A68 | Kickstart FFS 40.1 | SFS 1.279 |
| --- | ---: | ---: | ---: |
| Mixed | 4,768,820 | 2,783,360 | 3,918,700 |
| Streaming | 369,286,340 | 367,442,220 | 369,862,195 |

These are measured cycle medians from unchanged workloads and runner. The
performance objective remains unmet. A68 improves both totals against A58,
but only beats SFS in streaming.

A69 seals replacement roots directly in the owned control backing, with the
inode shape captured before rebuilding an overlapping header. Its 66 focused
host cases and both partial ROM allocation gates pass. Screens regress to
4,793,630 / 369,341,530 cycles, so it is rejected. Its 66-case compiled native run also passes (14 minutes 42 seconds). The retained
candidate uses A68 publication, without this experiment.

A70 compares authenticated checkpoint bodies in groups of eight longwords,
retaining exact tail bounds and the complete incoming CRC on mismatch. The
53 focused host cases, including the byte-mutation checksum oracle, pass.
Both actual-HUNK partial ROM allocation gates pass. Its screens are
4,757,000 / 369,284,950 cycles. No separate instruction suite or full solution
run was performed for this intermediate; it is not substituted for the A68
source undergoing full qualification. The handler is 269,872 bytes and the
archived manifest contains all 83 exact production inputs.

A71 prototypes bounded lookup/object-read fusion to remove a second operation
admission during pathname resolution. It is stopped before a HUNK or benchmark
is produced. Two recoverable-rejection regression cases demonstrate that an
unfinished lookup mode can otherwise affect a later namespace operation.
A72 resets the mode at successful operation admission. Its initial 107 focused
host cases pass, including cold/warm differential reads, long names, aliases,
busy rejection, cancellation with the existing remount requirement, and both
recoverable-rejection regressions. Native qualification and performance remain
pending; these changes are not part of the retained A68 source.
