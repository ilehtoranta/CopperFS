# Adaptive FFS sequential reads, 2026-11-08

The user authorized adaptive direct 4 KiB reads after the
[cached-copy investigation](kickstart-ffs-cached-copy-investigation-2026-11-08.md).
The normal mount policy can now avoid sustained payload copies. This is a
change of cache policy, not a claim that fully payload-cached reads became
faster. Explicit CopperHDF/scsi.device bypass controls remain unchanged.

## Native implementation

The implementation is C# in the native Amiga adapter. Portable filesystem
cores acquire no application/emulator dependencies. FFS and SFS2 remain
separate; this change does not enable adaptive reads in CopperSFS.

For a normal fixed-disk mount whose payload window is in Fast RAM:

1. Measure up to sixteen consecutive complete 4 KiB reads using the native
   `timer.device` E-clock. This consumes the existing 64 KiB read-ahead window.
2. Exclude the initial fill from the latency-spike test. A later service time
   over four times the minimum observed cached service time keeps the stream
   cached, avoiding a direct-read probe on expensive seek/refill patterns.
3. Otherwise measure sixteen actual requested direct reads, another 64 KiB.
   Keep direct reading only if this epoch is cheaper by at least 1/128.
4. Stop sampling after the decision. Seek, discontinuous reading, a change of
   read size, cursor invalidation or failure resets/restarts the stream hint.

The thresholds are conservative heuristics, not hardware calibration. An
already warm or irregular workload can remain cached on a fast device.
Chip-RAM payload windows stay cached: their direct-read probe was slower in
the stock-machine comparison. The memory distinction uses Exec `TypeOfMem`,
not an emulator flag, controller name, ROM identity or benchmark path.

The existing 216-byte file object supplies hint words and E-clock scratch.
File size/position, traversal cursor, on-disk data and packet ABI do not move.
One optional timer request is constructed at mount startup for normal fixed
Fast-RAM mounts and released by existing mount teardown. It is never armed.
Unavailable allocation or pre-V36 timer support retains caching. There are
no read-path allocations, new queues or portable-core dependencies.

During warm-up/probing, a packet-local internal payload hint uses the existing
single-owner mount execution and restores the normal policy before completion,
including errors and short reads. Metadata and partial-sector readers retain
their existing paths. Learned direct reads use a validated owned run and the
existing transfer helper, preserving DMA masks, MaxTransfer, TD64, aliases,
logical overlays and error prefixes. Tiny reads keep their original decoder.

## Toolchain and records

CILica stays enabled throughout handler builds: restricted pipeline,
revision `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`,
`AssumeLittleEndian=false`, `AllowCrossAssemblyIPO=false`,
`VerifyEachPass=true`. The frozen CopperSharp base is
`e16a42fbeff7a15376564fed63791081c7fe8fef` plus the previously retained
borrowed-span/byte-copy fixes. No additional code-generation fix was needed.

All development records remain local under `artifacts/ffs-span`. Source
snapshots, managed/CILica inputs, maps, compatibility reports, native and
full-ROM measurements remain separate. Existing results are not overwritten.
No ROM, original handler, generated native bytes or emulator timing was
patched. No package, ROM, media or build artifact is published.

The rebased before handler is
`3766B58A4C96781CCE9247426D4DA428DFEE5D5BE50534F52486C38FF8F180E2`.
It includes the unrelated source changes already present when this work
started; the preceding 45D6 and 3B9D controls remain separate historical runs.

The final FFS handler is 218,712 bytes, SHA-256
`0E12747D1AE13B6EB88603975AA93A62AA812F3F467B7C2D0462090644235240`.
Its source/build record is `adaptive-chip-safe-native`, final matrix is
`adaptive-chip-safe-ide`, and matched audit is `adaptive-final-analysis`.
`adaptive-final-receipt.json` and `adaptive-final-allocation-receipt.json`
record the toolchain, inputs, handler identities and allocation checks.

## Measurement protocol

Each FFS IDE matrix has 288 cold workload trials: original, normal cached
policy, explicit bypass; 64 B, 512 B, 4 KiB, 64 KiB; Kickstart 3.0/3.1;
2 MiB Chip or 2 MiB Chip plus 8 MiB Fast RAM; synthetic HDD/CF; three trials.
The after "cached" variant is the normal adaptive policy described above.

The frozen Lightweight harness, ROMs, media, probe and synthetic timing
profiles are matched. Native ROM `scsi.device` performs actual Gayle PIO;
CopperHDF is absent. Every payload, buffer placement and read-only media hash
is checked. CPU timelines, machine time, commands, bytes, media fetches, PIO
and trial ranges remain in raw results. These are synthetic emulator results,
not measured physical A1200 performance or a claim of AA-Gayle cycle accuracy.

A separate C# diagnostic harness captures private cost statistics through
host-side RAM inspection after the read phase. It changes no guest time or
device behavior and is not used for acceptance throughput. The samples explain
the fourfold guard: the profiled Fast-RAM CF service spike was about 3.36 times
its minimum, while HDD was about 4.72 times. A simple unconditional transition
and shorter unmatched probes were rejected because they regressed HDD.

## Final IDE results

The Fast-RAM/CF 4 KiB deficit is closed. Matched median session throughput:

| ROM | Before cached, KiB/s | After adaptive, KiB/s | Original FFS, KiB/s |
|---|---:|---:|---:|
| Kickstart 3.0 | 467.81 | 493.48 | 490.35 |
| Kickstart 3.1 | 468.01 | 493.50 | 490.67 |

That is about 5.5% faster than the rebased Copper reader and 0.6% faster than
the originals. CF read commands increase from 44 to 269; the original issues
284. Read PIO stays 1,038 KiB. This is the explicitly authorized tradeoff of
avoiding copies by issuing more native device commands.

Ratios below are after/original, with ranges spanning the two ROMs:

| Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---|---:|---:|---:|---:|
| 64 B | normal/adaptive | 1.185x | 1.130x | 1.162–1.163x | 1.128–1.129x |
| 64 B | bypass | 1.185x | 1.130–1.131x | 1.162–1.163x | 1.128–1.130x |
| 512 B | normal/adaptive | 1.750–1.751x | 1.575–1.576x | 1.666–1.717x | 1.528–1.530x |
| 512 B | bypass | 1.727–1.731x | 1.570–1.573x | 1.666–1.716x | 1.524–1.525x |
| 4 KiB | normal/adaptive | 1.152–1.166x | 1.049x | 1.048x | 1.006x |
| 4 KiB | bypass | 0.981–1.007x | 0.981x | 1.008–1.041x | 0.982x |
| 64 KiB | normal/adaptive | 1.003–1.017x | 1.003x | 1.022–1.023x | 1.001–1.003x |
| 64 KiB | bypass | 1.003–1.017x | 1.003x | 1.022–1.023x | 1.001–1.003x |

The HDD cases retain caching and 44 read commands at 4 KiB. Stock CF also
retains caching and 44 commands, avoiding the earlier prototype's 59-command
probe penalty. Both policies preserve all matched 64 B/512 B command, byte,
PIO and completion counts: 24 paired trials per read size, zero mismatches.

**Universal HDD nonregression is not achieved.** Against the rebased Copper
reader, normal-policy 4 KiB HDD results are 417.10 vs 417.16 KiB/s (stock 3.0),
416.30 vs 413.81 (stock 3.1), 372.85 vs 374.86 (Fast 3.0), and 372.90 vs
372.08 (Fast 3.1). Fast 3.1 HDD 64 KiB remains 570.03 vs 574.21 KiB/s,
approximately 0.7% slower. Some smaller/bypass workloads also regress; the
full table and trial ranges remain in the audit. Stock CF 4 KiB remains about
1.0–1.1% slower than the rebased reader despite removing the probe penalty.
These costs are not concealed by the new CF win.

Original controls have matching identities but up to 0.59% session-time
variation between these runs. That does not establish that the remaining
regressions are noise. Reports retain time outside the read markers and
physical media-fetch counts separately, because synthetic rotational
scheduling and startup costs affect session throughput too.

## Verification

Native replay tests cover warm-up/direct transitions, slow-device veto,
clock wraparound/unavailability, seeded cursors, interleaved handles,
discontinuities, errors and completed prefixes, invalidation, RAM overlays,
read/seek output aliases, other formats' cursor preservation, and bounded
timer construction. Source integration uses CopperStart's instruction bus;
CopperStart's AmigaOS native replays are reported separately from host tests.

The older service-double throughput benchmark supplies no timer. It therefore
checks the allocation-free cached fallback rather than predicting the adaptive
full-ROM speedup. Small-read command/byte/PIO/completion counts are compared
between matched full-ROM runs, not inferred from instruction counts.

Missing optional image tests and physical-platform tests are unavailable
coverage, not passes. SFS2 full-ROM throughput is not newly claimed by this
FFS-only change.

Final coverage:

* Host: Release solution build passed; 1,307 solution tests passed, three
  optional image tests skipped.
* Native: 178 reader replay cases passed with matching CSUN artifacts and
  178 with TRST; 48 service-double before/after measurements validated.
  Initial stale-handler SFS2 failures were replaced by matching rebuilt
  artifacts, not reclassified as passes.
* CopperStart: both native AmigaOS replay tests passed with the final classic
  handler build and the licensed A500 3.1 ROM.
* Allocation: final FFS, classic, SFS2 CSUN and SFS2 TRST compatibility reports
  are compatible and contain zero managed allocation sites.
* Full ROM: 288 final FFS trials passed, zero unavailable attempts; the rebased
  before matrix also has 288 validated trials. Every payload and read-only
  media check passed. Hardware coverage remains unavailable.
