# CopperNova architecture experiment, 16 November 2026

The performance objective is active and unmet. This isolated worktree starts
from the qualified CPX4 checkpoint plus the V26 source patch. The first candidate
changes buffered retirement, with an explicit opt-in and unchanged default
portable policy. It does not change the on-disk format or native allocation bound.
The native handler opts into the experimental policy; it is not a release.

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
uses two publication barriers and an explicit mapping root. It is not connected
to the filesystem handler yet, so the table above contains no CPX5 results.

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
full-filesystem and full-ROM CPX5 coverage remain pending.

The Release solution snapshot before the shared-checksum bridge passed 2,432
tests across six suites, with nine skips and zero failures. That run included
511 CPX host cases and 90 compiled CPX cases; six opted-in ROM theories were
skipped and are not passes. The subsequent shared-checksum changes separately
pass the 21 host and 17 HUNK component cases, including initialized table use.
CopperStart's unchanged geometry/buffer replay binary was rerun: 141 passes.
No full-ROM campaign or physical-media qualification is claimed for this branch.
