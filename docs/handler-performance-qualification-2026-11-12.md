# Handler performance investigation, 2026-11-12

For the subsequent optimization pass and current candidate results, see
[performance gap qualification](performance-gap-qualification-2026-11-12.md).
The measurements below describe the earlier candidate.

The mutation bottlenecks have been reduced substantially. Automatic TRIM remains
disabled by default: compatibility/recovery results and the performance gates
are separate, and the candidate still fails original-handler parity. No package
or ROM is published by this work.

## Measured bottlenecks and changes

Diagnostic instruction profiles use the same release workload as the original
comparison. They attribute exclusive instruction cycles, not inclusive stack
time. Scalar profiling is diagnostic only; all acceptance timing uses the
unmodified frame runner. Guest payload generation, byte verification and OS work
are reported outside the handler and must not be attributed to filesystem code.

- **FFS/OFS:** byte-at-a-time payload writes, repeated table writes and cache
  eviction, and scalar write checksums were avoidable costs. Payloads now use
  the platform's bounded copy primitive. Fixed-disk FFS consumes a verified,
  contiguous allocation hint in one data transfer and publishes a complete
  pointer prefix per run. It stays within one bitmap/table and retains the root
  invalidation and final valid-root publication protocol. Immediate retained
  writes preserve current cache images and invalidate them on failure. The
  unrolled write checksum remains local, preserving the original read checksum
  path.
- **SFS:** an append with a partial first sector forced its complete middle
  through per-block allocation, staging, compression and journal emission.
  The full middle can now use the existing contiguous-append path. Boundary
  sectors remain staged. The original allocation map and any acknowledged
  group checkpoint must both prove replacement data unreachable before it can
  bypass the overlay. Failed later writes cannot damage previously accepted data.
- **SFS2 TRST:** the old profile spent 51.5 million cycles in `NativeByte`,
  27.6 million in `TrstByte`, 24.2 million in `NativePass`, 18.2 million in
  `TrstEncode`, and 17.6 million in `NativeRequired`. Those five routines alone
  accounted for roughly 56% of the 249-million-cycle mutation phase. Healthy
  commits now apply their owned final images after publishing the complete
  durable journal, avoiding read/decode of their own journal. Restart recovery
  still validates the complete stream before application. The existing shared
  LONGCOMPRESSION planner classifies words once; bounded copies emit literals
  across page boundaries. Journal allocation caches the original/final bitmap
  intersection in private scratch.
- **SFS2 writes:** full newly allocated runs are reserved and updated as a run.
  TRST bypasses data staging only after checking the durable allocation bitmap;
  blocks retired during this transaction retain full-data staging. CSUN uses
  its existing original-free allocation search. Both policies retain their
  journal formats, marker transitions, durability barriers and zero-actual-on-
  failure contract. Write checksums reuse the existing unrolled sum.

The native cores remain allocation-free, use caller-owned scratch and exclusive
mount ownership, and retain their package/handler identities. No filesystem
dependency on CopperStart, CopperDisk or CopperScreen was added.

## Experiments rejected

A packed opposite-parity copy prototype was correct on 68000 and 68020 but
increased SFS growth timing. A separate 32-bit division shortcut also failed to
improve the actual workload. Both were removed. The original local copy loop
and arithmetic remain. Exact-range copy tests, including opposite parity and
bus-access guards, are retained because more write paths now use that primitive.

Sharing the FFS read checksum through a new common helper introduced a read-path
call. The reader's original implementation was restored; the write optimization
uses its own local unrolled loop.

Disabling call clustering on the SFS handler also failed qualification: its large
overwrite phase regressed 0.501%, and its zero-byte read phase regressed 0.310%.
The maintained build profile was not changed.

## Original-handler results

Positive percentages mean more emulated cycles than the original. The previous
candidate is the tracked-range build in the
[freed-range report](freed-range-trim-qualification-2026-11-12.md).

| Profile | Previous mutation gap | Selected mutation gap | Selected verification gap |
| --- | ---: | ---: | ---: |
| OFS | +284.147% | +178.810% | +10.051% |
| FFS | +159.350% | +79.108% | +3.175% |
| SFS | +102.038% | +28.190% | +13.192% |
| SFS2 CSUN | +195.712% | +177.317% | +17.402% |
| SFS2 TRST | +1497.242% | +116.928% | +17.757% |

All five original-handler gates fail. Diagnostic instruction profiles show
roughly 31% fewer mutation cycles for FFS, 37% for SFS and 86% for SFS2 TRST
against the previous Copper candidate. These reductions do not establish parity.

The subsequent [TRIM-only CPU profile](trim-cpu-profile-2026-11-12.md) separates
range-tracking and timer-service costs from this ordinary filesystem workload.

Mutation-phase device command counts in the selected original comparison are:

| Profile | Writes original / Copper | Flushes original / Copper |
| --- | ---: | ---: |
| OFS | 314 / 914 | 2 / 1 |
| FFS | 47 / 162 | 2 / 1 |
| SFS | 17 / 206 | 7 / 116 |
| SFS2 CSUN | 17 / 337 | 7 / 260 |
| SFS2 TRST | 17 / 156 | 7 / 104 |

These are commands, not byte counts. Original SFS issues substantially fewer
writes and flushes for this workload. Copper retains its strict metadata
publication policy and existing write-group boundaries. Matching the original
cannot be achieved simply by deleting those durability barriers.

The selected FFS binary passes the before/after shared-workload, large-write and
small-packet checks. OFS passes the shared and large-write checks but its zero-byte
read phase regresses 0.153%. SFS passes the large-write check but its seek phase
regresses 0.374%; its shared workload also fails the command-count check because
physical flushes increase. SFS2 TRST's shared workload fails the write-command
check (156 versus 149 on the previous candidate). The rebuilt CSUN binary's seek
phase regresses 0.584%, although its shared workload check passes. These are unresolved performance
qualification failures, even when aggregate CPU time improves. No tolerance was
relaxed to hide them.

## Qualification method and remaining costs

Original FFS 40.1 and SmartFilesystem 1.279 are hash-pinned. The comparison
rotates three original/candidate trials, uses identical media and CPU settings,
and allocates the Fast RAM payload before loading either handler. Mutation
timing includes filesystem ACTION_FLUSH. Written images are independently
checked; compatibility also uses original-handler readback. A prior Copper
candidate is a diagnostic control, never a substitute release reference.

The additional large-write, small-packet and shared-workload probes retain their
0.1% cycle allowance. Device-command assertions now allow fewer commands, rather
than requiring identical counts from an optimization intended to batch I/O.
Increased reads, writes or flushes still fail those diagnostic checks.

Some new-data bypasses require an additional physical flush before an inline
TRST can publish the new mapping. Previously, that data was encoded inside the
journal itself. The barrier is necessary: removing it could publish metadata
before its referenced data is durable. Consequently lower emulated CPU cycles
do not establish parity on a slow or physical controller. The original-handler
workload uses a device without UNMAP support; poisoned UNMAP and irreversible
discard cuts are qualified separately.

Remaining profiles identify cache copies and checksum/allocation validation as
substantial costs. OFS retains forward-chain headers and per-data-block checksums.
FFS still performs full allocation validation on growth and maintains its
invalid/valid root protocol. SFS and SFS2 still pay for strict metadata transaction
publication, while SFS2 CSUN copies and journals old data. These checks and
barriers were retained; the existing recovery contract cannot be weakened to
make a timing test pass.

## Evidence and limits

Host coverage: the Release solution build passed. The full solution test run
passed 1,456 cases, with three external-image cases skipped. The final FFS suite
passed 279 cases after restoring the reader's checksum path; 34 focused recovery
cases passed on the final SFS source. The focused runs overlap the full suite
and are not added to its count.

Native coverage: all four affected standalone 68000 handlers rebuilt. CopperStart's
selected buffer, geometry and size instruction replays passed 193 cases with
`-p:CopperStartNativeSfs2=true`. Eleven additional arithmetic/copy/queue checks
passed, including execution on 68000 and 68020 and exact copy bus-access bounds.

Full-ROM coverage: the selected handlers passed 28 automatic-TRIM and clean-handoff
cases, including reallocation, partial discard, inhibit/remount, original-reader
compatibility and irreversible-discard cuts. Four additional foreign-family
startup cases passed without writes. FFS failure tests require the original valid
state or an invalid root after a failed mutation; they do not claim SFS-style
atomic rollback for FFS.

Nine additional published-journal recovery cases passed: seven SFS transactions
replayed by the original SmartFilesystem handler, and both SFS2 journal policies
recovered by both Copper policies. The SFS2 TRST case now additionally verifies
replay by the original handler. The original is never used to recover private
CSUN records. Together these give **41 passing full-ROM compatibility/recovery
cases**. The five original performance gates fail separately.

The selected binary manifest is `artifacts/perf-gap/selected/manifest.json`.
OFS/FFS use `ffs-local-sum`, SFS uses `final/sfs`, SFS2 TRST uses
`final/sfs2-trst`, and CSUN uses `csun-local-sum`. Each has frozen input assemblies,
an optimizer report and map. The selected handler hashes are:

| Handler | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS | 72,272 | `D2BC23F6583592CA81E43DF5AB87296D7D0273764F9C6F42B4621945755E65B6` |
| CopperSFS / SFS | 148,464 | `1A336C7E1920D934C182CADAFBB9351AA73920A06B5E16D43D7A54881440B966` |
| CopperSFS / SFS2 CSUN | 240,088 | `4F764A56D885F95BD18746F043C2826EAF9643C487E897AEB6829909BFDB1326` |
| CopperSFS / SFS2 TRST | 198,208 | `1C6AA48C03BD10E4A306501788CFDECA9EC84BAD57B4BEEC5F96E9BB04C63BCE` |

Ignored `artifacts/perf-gap` contains frozen before/after handlers, maps, input
assemblies, CILica provenance, manifests, profiles, command counts, cycle samples
and qualification logs. CILica uses scalar replacement with inlining disabled.
Experiments and rejected builds are retained separately from selected binaries.
ROMs and reference media are local test inputs and are not redistributed.

The first long append cut fixture exceeded the crash model's 131,072-state cap.
The exhaustive fixture was reduced to a four-sector payload plus five bytes,
which still exercises the new multi-block path. Large appends remain covered by
independent-manifest and full-ROM tests. Resource-limit failures are not counted
as passes.

Physical SSD/controller latency, asynchronous completion, actual media swaps
and real Amiga hardware remain unavailable qualification, not passes. Automatic
TRIM remains opt-in until original-handler parity and the applicable device
qualification are satisfied.
