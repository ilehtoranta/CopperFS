# IDE sequential-read cache policy optimization, 2026-11-07

This follow-up preserves the partitioned-v1 fixture and unmodified original
handlers from [the corrected IDE mount comparison](kickstart-sfs2-ide-mount-fix-2026-11-07.md).
Changes are in the C# filesystem sources. There are no emulator, ROM, original
handler or generated-instruction patches in this slice.

## Implementation

* DMA-safe, non-aliasing FFS/SFS2 metadata misses and initial format probes fetch the requested block
  into existing private scratch and retain it in the existing metadata LRU.
  They can still use an existing payload-window hit, but do not replace that
  window with an unrelated 64 KiB speculative read. Physical reads still occur
  only after logical overlay lookup and preserve metadata validation.
* The diagnostic scsi.device control `0x43504302` retains physical read-ahead
  for 512-byte, single-sector requests, while larger aligned transfers bypass
  the payload cache. Partial-sector reads and metadata retain their paths.
  Its private policy word is now 3 (bypass bit plus sector-read-ahead bit).
  The existing CopperHDF control `0x43504301` keeps policy 1 and its existing
  whole-sector bypass behavior. Ordinary mounts use policy 0.
* An optional `ReadDataBlocks(..., bool readAhead)` platform overload lets FFS
  suppress speculative read-ahead for fragments of an API request larger than
  4 KiB. A large request split across a 72-entry FFS pointer table must not
  turn its short tail into another full-window refill. The hint is captured
  before EOF clipping, including the native cached-cursor shortcut. Platforms
  explicitly implement this overload; platforms without read-ahead policy
  forward to their existing transfer method. A default interface body was
  rejected because it boxes mutable struct receivers and discards their state
  updates. Existing fixtures, study tools and CopperStart probe consumers use
  explicit forwarding, with no payload-path change.

No mount allocations, object sizes, filesystem formats, public packet ABI,
journal ordering or cache budgets change. The reader still consumes borrowed
physical images immediately. Scratch/private-record alias handling, transfer
limits, completed error prefixes and overlay precedence remain in place.
Classic SFS keeps its existing small metadata read-ahead policy after format
detection; its shared initial mount-probe path was also rebuilt and replayed.

## Measurement identity

Two fresh full matrices use the same frozen ROM harness, media, C# probe,
synthetic drive timings and compiler:

* `artifacts/ide-read-opt/current-ide/`: rebased working-tree handlers before
  this slice, from `current-native/`.
* `artifacts/ide-read-opt/optimized-ide/`: optimized handlers from
  `candidate1-native/`.

The final explicit-interface-forwarding rebuild is under `final-native/` and
`final-classic/`. All four HUNKs are byte-for-byte identical to the benchmarked
handlers; `receipts/native-binary-equivalence.json` records the matching hashes
and zero-allocation compatibility receipts. The final C# contract correction
therefore does not change measured guest execution. The frozen harness and its
managed dependencies were kept unchanged throughout both matrices.

The earlier accepted `artifacts/storage-sfs-mount/ide-full-v2/` uses older
frozen handlers. It remains immutable. Improvements already present in the
rebased sources are distinguished from the changes implemented here.

Each new matrix covers Kickstart 3.0 A1200 39.106 and Kickstart 3.1 A1200 40.68;
2 MiB Chip and 2 MiB Chip plus 8 MiB Fast; HDD-like and CF-like drives; original,
cached and diagnostic bypass policies; FFS, SFS2 CSUN and SFS2 TRST; 64 B, 512 B,
4 KiB and 64 KiB reads; and three fresh-machine cold trials per workload.
Stock buffers are in Chip RAM and expanded-machine buffers are in Fast RAM.

Host: AMD Ryzen 5 5600X (6 cores), Windows build 26200, x64 .NET 10.0.12.
The baseline used eight scenario workers and the optimized run uses four;
both overlap during part of execution. Host wall time is not a throughput
metric and does not set device deadlines. Each guest executes its own machine
clock and fresh drive state. Unchanged-original trial ranges are retained
rather than assuming bit-identical session timings between runs.

Throughput uses emulated guest time. The marked session includes cold first
open/mount, reads and close; machine boot is outside it. CPU timeline cycles
include stalls and waits and are not retired instruction cycles. Native PIO
passes through Gayle and ROM scsi.device; CopperHDF is absent from IDE trials.
HDD/CF timings are synthetic assumptions and AA-Gayle physical timing remains
unverified. These results are not measured physical A1200 performance.

The frozen compiler is `artifacts/ab11-final/compiler/CopperSharp.Compiler.Cli.dll`
(SHA-256 `D72449E1D07195FBED6193BB8F4603A78BEB705F728D6C6CA5DF232A48215EF5`).
All three optimized standalone handlers, plus the shared classic SFS handler,
are compatible and have zero managed allocation sites. No code-generation
fix was required. Compile arguments, native source snapshots, maps, handler
hashes and allocation receipts remain under the private artifact roots.

## Verification

Host:

* `dotnet build CopperFS.slnx -c Release`: passed with isolated artifact output
  and the frozen SDK. One existing nullable warning in the frozen compiler.
* The final interface-forwarding build adds a mutable-state/allocation
  regression. It fails against the previous default-interface version (the
  original platform's read counter remains zero), then passes with explicit
  forwarding and zero allocation on the repeated 8 KiB read. The final Release
  solution test run passed 1,305 tests (267 FFS, 989 SFS, 18 native adapter
  semantics and 31 benchmark-tool tests), with three optional media tests
  skipped as unavailable coverage.
* CopperStart's full Release solution build passed, as did the two historical
  probe projects updated for the explicit platform overload. The solution
  build has 13 existing warnings and zero errors. Focused consumer and ROM
  replay results below also passed after the interface correction.

Native:

* All eight new discriminating native regression cases failed before the
  fix. They cover IDE single-sector batching, unrelated failing metadata
  neighbors and large FFS pointer-table tails, on 68000 and 68020.
* The complete focused native suite passed 70 cases for CSUN and 70 for TRST.
  Existing tests cover CopperHDF policy selection, partial reads, aliases,
  transfer limits, failures, wide files and logical overlays.
* The native before/after replay benchmark validated 144 workload trials.
  Its Exec/device service doubles isolate instruction work; this is separate
  from ROM timing and device qualification. Short-read instruction costs are
  not uniformly lower: FFS uses up to 2.6% more cycles, and SFS2 up to 0.9% more.
  At 64 KiB, FFS cycles fall 14.5–15.2% and SFS2 cycles fall 11.3–13.0% across
  the two CPU models. Fewer fetched bytes can still mean more small metadata
  commands, so native service-double request counts alone do not predict IDE
  session throughput.
* CopperStart's maintained-source consumer suite: 36 passed, 46 optional
  tests skipped in its default invocation. Two of those optional full-ROM
  native replays were then explicitly enabled and passed with the rebuilt
  classic SFS handler and the available licensed A500 3.1 ROM. They exercise
  read/write, remount/recovery and concurrent DOS workers. The other 44
  optional cases remain unavailable. The consumer uses the current SDK
  because its own current sources require newer attribute APIs; benchmark
  handler compilation continues to use the same frozen compiler on both sides.

Full ROM:

* Initial optimized qualification: 36/36 4 KiB cold cases passed on A1200
  Kickstart 3.1, both RAM profiles, both drives, all filesystem builds and
  all three policy arms, with no unavailable attempts.
* Both formal matrices passed 864/864 cold workload trials, 72 cases per run,
  216 trials per read size and zero unavailable attempts. Each validated
  499.5 MiB of payload, including 192 original-SFS2 trials (96 on Kickstart 3.1).
  Each trial verifies its payload, actual PIO path and unchanged read-only media.

Artifacts retain TRX files, payload hashes, ROM/handler/probe/media/timing
hashes, memory placement, trial ranges, commands/bytes, PIO bytes and modeled
physical-media fetches. Source/PDB receipts verify 54 engine files, 12 harness
files, four runner files and two shared fixture files per matrix, with no mismatches. No ROM, key,
original-handler or package publication is part of this work.

## Formal results

Ratios below use median whole-session throughput relative to the unmodified
original handler in the same machine/ROM/drive configuration. Above 1 is faster.
Ranges combine both ROMs; SFS2 also combines CSUN and TRST. The last column
isolates this slice against the newly measured rebased handlers. Individual
trials and their ranges are retained in each matrix's `report.md` and
`measurements.json`; `final-analysis/analysis.md` also lists all matching
before/after session rates and traffic.

| FS | Read | Policy | Stock HDD / original | Stock CF / original | Fast HDD / original | Fast CF / original | New / rebased before |
|---|---:|---|---:|---:|---:|---:|---:|
| FFS | 64 B | cached | 0.981 | 0.929 | 0.924–0.926 | 0.894–0.895 | 1.035–1.063 |
| FFS | 64 B | bypass | 0.981 | 0.929 | 0.924–0.926 | 0.894–0.895 | 1.035–1.063 |
| FFS | 512 B | cached | 1.734–1.758 | 1.553–1.554 | 1.624–1.676 | 1.499–1.501 | 1.051–1.090 |
| FFS | 512 B | bypass | 1.733–1.757 | 1.547–1.548 | 1.623–1.675 | 1.495–1.496 | 1.555–1.898 |
| FFS | 4 KiB | cached | 1.240–1.241 | 1.015–1.016 | 1.075–1.076 | 0.909 | 1.037–1.062 |
| FFS | 4 KiB | bypass | 1.015 | 0.989 | 1.006–1.012 | 0.988–0.989 | 1.029–1.059 |
| FFS | 64 KiB | cached | 1.032–1.041 | 1.000 | 0.978–0.991 | 0.999 | 1.200–1.294 |
| FFS | 64 KiB | bypass | 1.032–1.041 | 1.000 | 0.978–0.991 | 0.998–0.999 | 1.035–1.098 |
| SFS2 | 64 B | cached | 1.077–1.095 | 1.085–1.098 | 1.012–1.021 | 1.018–1.030 | 1.141–1.239 |
| SFS2 | 64 B | bypass | 1.077–1.095 | 1.085–1.097 | 1.012–1.020 | 1.018–1.030 | 1.141–1.239 |
| SFS2 | 512 B | cached | 1.081–1.098 | 1.084–1.094 | 1.024–1.035 | 1.026–1.041 | 1.201–1.333 |
| SFS2 | 512 B | bypass | 1.078–1.096 | 1.081–1.091 | 1.022–1.033 | 1.023–1.039 | 1.600–1.973 |
| SFS2 | 4 KiB | cached | 1.208–1.220 | 1.190–1.195 | 1.123–1.132 | 1.106–1.118 | 1.167–1.268 |
| SFS2 | 4 KiB | bypass | 1.102–1.109 | 1.160–1.163 | 1.139–1.149 | 1.199–1.213 | 1.181–1.244 |
| SFS2 | 64 KiB | cached | 0.982–0.993 | 0.996–1.000 | 0.978–0.991 | 0.991–1.013 | 1.353–1.451 |
| SFS2 | 64 KiB | bypass | 0.984–0.996 | 0.996–1.000 | 0.977–0.991 | 0.991–1.013 | 1.353–1.451 |

The largest corresponding unchanged-original trial differs by 2.17% between
the two runs. This is an observed individual-trial difference, not a confidence
interval. Results within a few percent are best treated as near parity.
The 20–97% gains at the targeted workloads are much larger than that variation.

The original binaries, ROMs, probe, payloads and timing identities match across
all 288 corresponding original trials. Both runs use identical harness and
engine hashes. Seventy-two matched 64-byte cached/bypass pairs have identical
read transport commands, bytes, PIO bytes and completions; the same is true of
all 72 corresponding 512-byte pairs. Drive-prefetch media fetches remain
separate counters and can differ with the guest timeline.

## Traffic and remaining work

For the 1 MiB workload read in 64 KiB packets, these PIO byte counts are the same
across both ROMs, RAM profiles and drive models:

| Handler/policy | Read PIO KiB, before → after | Outside-read PIO KiB, before → after |
|---|---:|---:|
| FFS cached | 1,278 → 1,038 | 129 → 2.5 |
| FFS bypass | 1,038 → 1,038 | 129 → 2.5 |
| SFS2 cached/bypass, CSUN/TRST | 1,024 → 1,024.5 | 448 → 6 |

Outside-read traffic includes device identification and cold open/close work.
SFS2 now fetches one additional 512-byte metadata sector during the read loop;
it no longer fetches hundreds of unrelated sectors during cold mount.
Its 64 KiB session throughput improves 35–45% against the rebased handlers.
FFS's short pointer-table fragments no longer trigger 240 KiB of excess PIO,
and cached 64 KiB throughput improves 20–29%. The explicit IDE sector policy
raises 512-byte bypass throughput 56–90% for FFS and 60–97% for SFS2.

The next targets are FFS's 64-byte packet/cursor path (still 2–11% behind the
original) and its 4 KiB cached copy path on Fast RAM/CF (9.1% behind, versus
about 1.2% behind with bypass). Profile those CPU paths before changing the
cache policy again. Caching remains valuable on HDD: at 4 KiB it is 24% faster
than original FFS on the stock machine, and 7.5% faster with Fast RAM. SFS2 is
ahead at 64 B, 512 B and 4 KiB across this matrix; its 64 KiB results are around
parity, so there is less reason to prioritize its large-read path next.
