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
