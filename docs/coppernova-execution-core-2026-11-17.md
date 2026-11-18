# CopperNova execution core — 17 November 2026

Item 1 is implemented in the isolated CNM1 prototype: a fused CRC kernel,
bounded epoch-based quarantine retirement, and fewer repeated state/address
helper calls in the common execution paths. Production CPX4, CopperFFS and
CopperSFS remain unchanged. The prototype's existing feature limits remain.

The new handler is **19.89% faster on mixed** and **0.42% faster on streaming**
than the current CLOCK prototype. It still loses both workloads to Kickstart FFS
40.1, and loses mixed to SFS 1.279. It beats SFS on streaming. No general
cross-filesystem performance win is claimed.

## Four-way benchmark

Two trials per handler per workload, balanced order:
current prototype / new prototype / FFS / SFS / SFS / FFS / new / current.
Each workload uses its exact frozen guest HUNK. The machine configuration is
unchanged: emulated 68020, Kickstart 3.1, 512 KiB Chip + 2 MiB Fast, 16 MiB disk,
512-byte sectors, 32 DOS buffers and 32 KiB benchmark handler stacks. Results are
end-to-end **emulated bus cycles**, not host wall time.

| Workload | Current prototype (CLOCK) | New execution core | Kickstart FFS 40.1 | SFS 1.279 |
|---|---:|---:|---:|---:|
| Mixed total | 5,043,400 | **4,040,500** | 2,783,300 | 3,918,540 |
| Streaming total | 369,695,865 | **368,147,680** | 367,443,825 | 369,862,385 |

Phase medians:

| Workload / phase | Current | New | FFS | SFS |
|---|---:|---:|---:|---:|
| Mixed mutation | 2,160,520 | 1,958,005 | 1,684,020 | 2,478,940 |
| Mixed flush | 1,140,380 | 637,170 | 20,480 | 161,230 |
| Mixed remount | 584,120 | 349,545 | 108,970 | 218,790 |
| Mixed read / verify | 1,158,380 | 1,095,780 | 969,830 | 1,059,580 |
| Streaming mutation | 2,798,170 | 2,244,350 | 2,110,880 | 4,561,740 |
| Streaming flush | 1,248,765 | 714,050 | 20,360 | 171,910 |
| Streaming remount | 583,950 | 363,825 | 115,070 | 217,350 |
| Streaming read / verify | 365,064,980 | 364,825,455 | 365,197,515 | 364,911,385 |

Streaming mutation improves about 19.8%, but its large guest readback/byte
verification phase dominates total time. That phase includes DOS and handler
work as well as guest verification; it is not a measurement of guest work alone.

Every trial passes its guest assertions and independent persisted-image check.
The current and new prototypes have identical final whole-disk hashes in each
workload. Their I/O counts also match: mixed 35 read requests / 42 write requests;
streaming 4,121 / 4,126. Both have five whole-run flushes: four for the nonempty
commit and one for the later clean inhibit. No barrier or checksum was removed.

## Execution changes

The four-slice CRC-32C polynomial, initial/final inversion, metadata checksum-field
zeroing and checksum coverage are unchanged. Addresses for the table and private
CRC byte-extraction scratch stay local throughout one bounded loop. There are no
per-four-byte helper/address calls. Inputs are never modified to calculate CRCs.
Private calls checksum 512-byte metadata pages or the 1,024-byte descriptor set,
so a CRC invocation processes at most 256 four-byte rounds.

Packet admission/completion, dispatch, I/O issue/completion, cache lookup/hits,
extent mapping and allocation scanning now retain commonly used pointers and
fields locally. Continuation state is saved at I/O or bounded-work returns. The
cache miss path also avoids a duplicate hash-table walk. Dirty-cache scanning
saves its cursor when returning rather than after every clean-slot visit.
The CLOCK policy, admission thresholds and dirty/current-operation protections
are retained.

Quarantine still uses the existing 8 KiB bitmap, with 32 fixed window-generation
records in previously unused caller-owned state padding. Each record covers
2,048 sectors / 256 bitmap bytes. A window is cleared only when first used for
frees in the current retirement epoch. Allocation resolves that window's active
quarantine pointer once per bounded scan and ignores stale generations.

Only after the fourth successful durability barrier does retirement advance the
epoch. Normal retirement is O(1); epoch wrap clears the 128-byte record table.
Mount invalidates the records before allocation is possible. There is no longer
an 8 KiB retirement clear. Window initialization is charged to mutation and is
included in the benchmark totals.

The arena remains 98,304 bytes and the metadata cache remains 32,768 bytes. No
operation allocation, recursion, new portable-core application dependency or
on-disk field is introduced. Existing bounded scans remain: 128 cache-victim
visits; 256 skipped allocation candidates followed by at most 128 sectors of run
admission; one 2,048-sector retirement window per resize mutation continuation.
The existing native adapter continues to poll cancellation between core steps.

## Emitted HUNK inspection

`tools/CopperNova.HunkInspect` reads actual single-code-HUNK bytes and their map,
reconstructs branch targets from encoded displacements and decodes complete CRC
and selected common-helper instruction ranges. It rejects unsupported layouts
or undecodable instructions. Compiler pre-optimization ASM exports are not used.
Generated listings remain under ignored artifact directories.

| Inspected method | Old call instruction sites | New sites |
|---|---:|---:|
| CRC | 6 | **0** |
| Four-byte CRC helper | 2 | removed |
| Packet admission | 15 | 1 |
| I/O completion | 7 | 0 |
| Packet finish | 6 | 0 |
| I/O issue | 9 | 0 |
| Cache load | 11 | 3 |
| Cache find | 4 | 1 |

These are static call instruction sites, not runtime invocation counts. Error
tail transfers can remain in common helpers. The new CRC has neither call sites
nor branches outside its own method. Its prologue saves nine registers once per
checksum; there is no per-word register-save/call/return sequence. The CRC routine
is 370 bytes versus 442 bytes for the old CRC plus four-byte helper.

A separate host-only scalar observer executes the unchanged correctness guest
and frozen HUNK on MC68000, 512 KiB Chip, zero Fast and a 4 KiB handler stack.
It inserts no guest instructions. Its flush results are **scalar instruction
cycles attributed to the handler task**, separate from the 68020 acceptance
timing above:

| Runtime flush counter | Current | New |
|---|---:|---:|
| Handler-task instruction cycles | 2,136,854 | 1,360,446 |
| Handler-task instructions | 246,164 | 179,429 |
| Whole CRC calculations | **12** | **12** |
| Four-byte helper calls | 1,653 | **0** |
| Address-helper calls | 3,547 | 50 |
| State-read helper calls | 629 | 125 |
| State-write helper calls | 202 | 42 |
| Zero-fill calls | 19 | **3** |

Both profiling executions also pass the independent shutdown payload/ownership,
allocation and stack checks. The 16 removed zero-fill calls are the old 16 ×
512-byte quarantine retirement clear. The three remaining flush zero-fills
prepare the descriptors, commit record and clean marker.

## Durability and qualification

Earlier write replies may precede persistence. Successful flush, unmount and
shutdown establish durability. The four-barrier redo/home/clean protocol, failure
handling, packet ABI, metadata integrity coverage and existing in-place data
overwrite limitations remain unchanged. Packed metadata trees and two-barrier
root publication are not implemented by this change.

The final standalone HUNK is 31,420 bytes, SHA-256
`61EFDFBF1A8EE50BF3625F0487B3514DA56C2C1F3F2299D5E4A053D5BE1DC339`.
The current CLOCK baseline is 31,340 bytes, SHA-256
`4572155BEEE109B407E660C18308BB59510CE9BE6E882C3EC68415BCB5FF666F`.
Both use the same compiler binary. Source manifests and frozen source copies
identify all native inputs; no compiler or emulator source changed.
Rebuilding the frozen baseline engine against the new handler's dependency
inputs reproduces its HUNK byte-for-byte, controlling for managed-build path
and dependency differences as well as compiler identity.

- Host: 28 prototype tests pass, including 32 independent bitwise CRC comparisons
  with unchanged input buffers, cross-window retirement/reuse and epoch wrap,
  84 free/allocate physical crash states, 74 existing transaction/retry states,
  32 interrupted-recovery states, 13 failed and 13 short I/O positions.
- Native: allocation-free compatibility report; freshly rebuilt standalone
  handler; 169 CopperStart replay tests pass. Those replays cover existing
  integration paths separately from the prototype adapter.
- ROM: actual final HUNK on MC68000 / 512 KiB Chip / zero Fast / 4 KiB stack
  passes mixed operations and shutdown. Peak stack 740 bytes, guard intact,
  nine startup Exec allocations and zero operation allocations. Four shutdown
  barriers and independent final payload/ownership checks pass.
- Full solution: **2,754 pass, zero failures, ten optional skips**, comprising
  2,615 host cases (including the 28 prototype cases) and 139 compiled-native
  cases. The final engine sources were used throughout this invocation.
  Optional legacy ROM/media cases remain skips. No physical-device or broad
  platform qualification is claimed.

Final sources were frozen before the full Release solution build/test. The net10
target overrides are needed with the installed .NET 10 SDK because neighboring
compiler projects also declare net11; no project target or published version was
changed. Reports distinguish managed host, compiled-native and actual-ROM checks.

Reproduction: build with `build/Build-NovaPrototype.ps1`; run each original
workload with `build/Benchmark-NovaPrototype.ps1 -BaselineHandler <CLOCK HUNK>`;
inspect each HUNK/map with `CopperNova.HunkInspect`; generate the scalar observer
with `node tools/studies/prepare_execution_profile.mjs`. The observer requires
the matching frozen source directory through `NOVA_PROFILE_SOURCE_ROOT`.
`tools/studies/record_execution_core.mjs` checks hashes, all trial/checker receipts,
byte-identical prototype outputs, unchanged I/O/checksum counts and test completion
before writing `benchmarks/coppernova-prototype/2026-11-17-execution-core.json`.

## Remaining gap

The new mixed flush and remount still total 986,715 cycles; FFS uses 129,450.
That boundary gap accounts for about 68% of the remaining mixed total difference.
The unchanged full-page journal/home protocol and metadata layout remain costly.
Mutation plus readback alone still exceed FFS's entire mixed total by 270,485
cycles, so removing persistence costs alone would not achieve the goal.

The result supports the execution-first approach and quantifies its limits.
It does not establish that the format must change, or that a proposed packed
tree/root publication design will automatically outperform FFS.
