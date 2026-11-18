# CopperNova metadata prototype: benchmark and qualification, 16 November 2026

CNM1-P4 implements the common direct-metadata path in an isolated prototype.
**It does not beat both original handlers in both workloads.**
Existing CPX4/CPX5, FFS and SFS source and native build selections remain unchanged.
The [prototype README](../prototypes/CopperNova.Prototype/README.md) records the layout,
supported operations, format and feature limits. This is not a production replacement.

## Fresh complete-workload measurements

Two-trial median emulated bus cycles; lower is better. Same frozen mixed and
streaming guest HUNKs, Kickstart 3.1, emulated 68020, 512 KiB Chip plus 2 MiB
Fast, 16 MiB images, 512-byte sectors, 32 requested DOS buffers and interleaved
prototype/FFS/SFS/SFS/prototype/FFS order. All twelve trials pass guest checks
and independent persisted checks. The prototype decoder verifies full ownership,
bitmaps/counters and both retained file payloads. Reference inputs are original
FFS 40.1 and SmartFilesystem 1.279, unchanged and hash-checked.

| Workload | CNM1-P4 | FFS 40.1 | SFS 1.279 |
| --- | ---: | ---: | ---: |
| Mixed | 5,039,940 | 2,782,155 | 3,931,315 |
| Streaming | 369,689,425 | 367,443,375 | 369,863,275 |

The backend does not charge physical seek/rotation/transfer latency. Host elapsed
time is not compared. The 32-buffer setting does not equalize internal cache
memory. The prototype has a 96 KiB arena and a narrower feature set than the
references; these results do not establish full filesystem or physical-device parity.

## Phase accounting

| Workload / phase | CNM1-P4 | FFS | SFS |
| --- | ---: | ---: | ---: |
| Mixed mutation | 2,158,190 | 1,683,070 | 2,489,360 |
| Mixed flush | 1,139,900 | 20,445 | 161,265 |
| Mixed remount | 583,770 | 108,980 | 218,800 |
| Mixed read/verify | 1,158,080 | 969,660 | 1,061,890 |
| Streaming mutation | 2,793,255 | 2,111,110 | 4,561,650 |
| Streaming flush | 1,249,035 | 20,370 | 171,910 |
| Streaming remount | 583,355 | 115,170 | 217,350 |
| Streaming read/verify | 365,063,780 | 365,196,725 | 364,912,365 |

Flush/remount account for 1,594,245 of the 2,257,785-cycle mixed gap
to FFS (70.6%). Direct inodes and inline directories remove
selection and name-page work, but do not make the current publication path cheap.
Mixed changes seven final metadata images. Retirement seals and journals them,
writes descriptor/commit/clean controls, installs every image at home, and orders
four persistence barriers. Its CRC kernel still visits zero padding; the older
qualified CPX path had additional sparse-zero CRC shortcuts. These are code-level
costs to profile, not exclusive-cycle attribution. Fewer metadata reads alone
are insufficient evidence of a faster native implementation.

A short FFS ACTION_FLUSH phase does not prove omitted persistence: earlier
operations can perform work before the marker. Acceptance compares the full sequence.

## Durability and execution evidence

- Latest prototype host tests: **19 passed, zero failed/skipped**.
- Physical byte-sector power cuts: 74 crash/changed-retry states after three
  committed warmup transactions, 32 interrupted-recovery states, and failures
  or short transfers at each of 13 journal request positions. The model tests
  loss, reordered persistence, torn writes, bank reuse and idempotent replay.
- Final Release solution build passed with zero warnings/errors. The solution
  suite run started on P2: 2,726 unchanged legacy cases plus 11 prototype cases
  passed, zero failed, ten skipped. After the counter/ownership fixes, the
  final P4 build and all 19 prototype cases were rerun. The old projects do not
  depend on the new prototype; their source/tests and shared boundaries are unchanged.
- The legacy solution coverage separates 2,587 host cases from 139 compiled
  native cases. Seven default-skipped ROM methods and three unavailable SFS
  media cases remain skips, not passes.
- Fresh CopperStart geometry/buffer replay: **169 passed**, zero failed/skipped.
  This is separate legacy source-integration coverage, not prototype adapter coverage.
- Final 31,256-byte MC68000 HUNK: **zero reachable managed allocation sites**;
  manifest hashes match every source input and both comparisons and the ROM gate.
- Actual-HUNK ROM gate: MC68000, 512 KiB Chip, no Fast RAM, 4 KiB handler stack;
  **740-byte measured peak**, intact guard, 9 observed startup Exec allocations
  and **zero operation-time Exec allocations**. The test runs mixed operations,
  remount/readback, then appends without client flush and invokes ACTION_DIE.
  It observes four shutdown barriers and independently checks all 7,027 final
  main-file bytes, the temporary file and complete ownership after shutdown.
- Broader packet/media/platform coverage, physical Amigas and physical storage
  were not run. One targeted ROM gate is not a full platform qualification.

Successful ACTION_FLUSH, inhibit/unmount and shutdown wait for full clean
retirement. Earlier replies may precede persistence. New data is ordered before
commit, home writes follow the durable commit, and uncommitted frees are
quarantined. Existing in-place-overwrite atomicity limitations remain. The
native core/transport owns fixed caller storage and performs no operation-time
allocation or recursive traversal. Each continuation has fixed bounds and I/O yields.

## Provenance and reproduction

Handler SHA-256: `434545812FBB9B04CB84606106278EC4619F6EF5B1D1A0B5E3D80AF7E4FA7B38`.
Compiler CLI SHA-256: `7B32D0B0C95F179DA708F7081BA8BD9ECA6253A3D1BFBF09D1FA643B0A148DB1`.

The installed .NET 10.0.401 SDK cannot target the neighboring compiler's new
.NET 11 framework. Builds select its existing .NET 10 target using
`-p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0`; no compiler/SDK source
was edited. The separate native project consumes only portable cores and CopperSharp.

Use `build/Build-NovaPrototype.ps1`, `build/Benchmark-NovaPrototype.ps1` and the
qualification command in the README. Selected raw receipts live under ignored
`artifacts/nova-p4-*` and `artifacts/metadata-study`. P1 has complete historical
comparisons; P2 streaming was interrupted when superseded and is not an acceptance
result. No benchmark guest was edited or recompiled for the measured comparisons.

The [machine-readable report](../benchmarks/coppernova-prototype/2026-11-16-metadata.json)
retains phase ranges, input hashes, qualification and source hashes. Original
ROMs, handler binaries, generated media and build artifacts are not committed.
No production CPX4 replacement, conversion, package or ROM was published.
