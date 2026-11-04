# Compiled writer performance investigation — 2026-10-25

Follow-up: [implementation, qualification and benchmark rerun](native-write-fixes.md).
The findings and proposals below describe the earlier binaries; the follow-up
records which fixes were implemented and the remaining measured bottleneck.

The benchmark gap contains large, avoidable implementation costs. TRST spends
72.8% of measured cycles encoding and replaying its own journal through byte
helpers. CSUN's 8 KiB overwrites spend 44.3% scanning free space and 22.0% copying
blocks. Buffered CSUN's 64 KiB appends have three sector-at-a-time fallbacks that
consume 85.5% of the entire run. These are measured in the previously qualified
compiled handlers, not inferred from managed-host timings.

This investigation adds diagnostics and documentation only. It does not change
the native handler, state layout, journal format, SFS2 profile, or default policy.
Optimizations below are proposals; no post-fix speedup is claimed.

## Evidence and measurement boundary

The [60-run acceptance benchmark](native-write-benchmark.md) remains the source
for comparisons against original SFS 1.279. The diagnostic runs use the same
frozen CopperSFS HUNKs and their matching symbol maps:

| Handler | HUNK SHA-256 |
| --- | --- |
| TRST | `6F9331C6BBA0D479A06282FED2AD3994B432FB57273D0077BA39F8C303204820` |
| CSUN buffered | `CEE80B0F3D6716138EA8C1C1D2C29EF4130D96112DCE93DE98DA8F219E935A08` |

`artifacts/native-write-investigation/analysis.json` records profile paths,
hashes, exclusive method cycles, packet entry counts and diagnostic trial data.
Regenerate it with:

```powershell
python scripts/Summarize-NativeWriteInvestigation.py artifacts/native-write-investigation
```

The profiler attributes executed MC68000 handler instructions on the benchmark's
emulated 68020 machine. Percentages below divide exclusive instruction cycles by
the entire measured interval; nested calls are not double-counted. Interrupt
dispatch, STOP waits and boundary-marker timing are residual. This is a CPU
diagnostic with no added storage latency, not a real-device throughput forecast.
The original handler was benchmarked but was not instruction-profiled.

The two initial 8 KiB append profiles use the older sequential-megabyte probe,
which includes a small additional setup write. The 64 KiB append and overwrite
profiles use the acceptance workloads directly. Their totals differ from the
normal-driver results by less than 0.03%; use the acceptance results, not these
profiles, for absolute throughput comparisons. The overwrite range lies in one
contiguous extent of the fixture file despite its raw `fragmented-overwrite-8k`
label.

Native product sources are being edited separately. These findings apply to the
pinned HUNKs above, not a claim that every later source change has been measured.
Function names identify the inspected algorithms; live source line numbers may
move.

## TRST: a healthy commit takes the full crash-recovery path

The 8 KiB append profile measures 1,771,465,699 cycles:

| Exclusive function | Cycles | Share |
| --- | ---: | ---: |
| `NativeByte` | 470,236,543 | 26.5% |
| `NativePass` | 226,678,921 | 12.8% |
| `NativeRequired` | 152,193,554 | 8.6% |
| `TrstByte` | 264,842,085 | 15.0% |
| `TrstEncode` | 174,834,791 | 9.9% |
| `CopyBlock` | 133,451,979 | 7.5% |

`CommitTrst` in `AmigaSmartFileSystemTrst.cs` already holds the final block images
in its bounded arena. It measures and emits the compressed data in two passes,
sending every literal byte through `TrstByte`. After publishing and flushing the
journal and TRFA marker, it calls `ReplayNative`.

`ReplayNative` in `AmigaSmartFileSystemNativeRecovery.cs` parses the entire journal
once for validation and again for application. Both passes consume literal
payload one byte at a time through `NativeRequired` and `NativeByte`. The apply
pass reads target blocks and reconstructs final data that the arena already
contains. Those three replay functions alone consume 47.9% of the run; encoding
consumes another 24.8%. Full replay is valuable during mount recovery, but using
it for every successful commit adds substantial redundant work.

The acceptance benchmark supports a CPU bottleneck: moving from 8 to 64 KiB
packets reduces flush commands from 516 to 68 per MiB, yet throughput remains
about 4 KiB/s. TRST also performs more reads than CSUN. On overwrites it writes
half as much journal data as CSUN and issues fewer flushes, but still runs slower.

The first optimization should retain the existing journal publication barriers,
then apply the arena's final images directly, flush the home writes, and finally
write and flush TROK. Actual recovery should retain full validation and replay.
Encoding should copy bounded spans of literal data across journal pages instead
of calling a page-aware helper for every byte. Both changes can preserve the
192 KiB bound and whole-write atomicity. They still require native failure and
crash qualification, including partial home application and marker failures.

For comparison, the related AROS SFS source commits from its current cached block
images in `writeoperations`, and packs journal payload using `CopyMem`.
Its decompressor handles literal longwords directly. This is supporting design
evidence, not a claim that AROS master exactly matches the measured original
1.279 binary. Sources: [transactions.c](https://raw.githubusercontent.com/aros-development-team/AROS/master/rom/filesys/SFS/FS/transactions.c)
and [support.c](https://raw.githubusercontent.com/aros-development-team/AROS/master/rom/filesys/SFS/FS/support.c).

## CSUN overwrite: allocation scans and copy traffic dominate

The buffered-CSUN overwrite profile measures 365,818,704 cycles:

| Exclusive function | Cycles | Share |
| --- | ---: | ---: |
| `ScanFreeRun` | 162,147,411 | 44.3% |
| `CopyBlock` | 80,660,111 | 22.0% |
| `ReadCached` | 15,527,983 | 4.2% |
| `BlockSum` | 14,804,640 | 4.0% |
| `TransferData` | 7,330,124 | 2.0% |

`JournalRun` requests temporary space for undo images and their descriptors.
`FindFreeRun` tries the preferred prefix and, when it cannot use it, scans from
the reserved-block boundary. `ScanFreeRun` tests each individual block, repeatedly
doing bitmap addressing and division/remainder work instead of skipping occupied
bitmap words. Repeated journal allocation makes this extremely expensive.

Use word-oriented scans, persistent search hints and bounded journal reservations,
with the same original/final free-space and reservation checks. Already-published
journal space must remain unavailable until the transaction is safely cleared.
These checks are correctness requirements; the per-bit scanning strategy is not.

Copying is independently a large measured cost. Cache reads, journal handling and
staging pass block images through several buffers. The current `CopyBlock` already
uses longword copies; this finding is not evidence of a byte-copy implementation.
Caller-specific copy attribution is still needed before removing individual
copies, since some protect cache ownership or the undo checkpoint.

The buffered policy does not buffer these large overwrites: `WriteBatched` routes
them through strict packet transactions. This explains why buffered and strict
CSUN overwrite throughput is nearly identical. The original's small metadata
journal and different transaction contract also account for part of its advantage;
the comparison does not establish equivalent whole-packet overwrite atomicity.

## CSUN append: three bulk-path misses dominate the large-packet result

Per-packet instruction entry counts confirm the fallback, rather than merely
correlating long latency with allocation work. Packet numbers are zero-based.

| 64 KiB packet | Write latency | `FileBlock` entries | `AllocateData` entries | `WriteDataBlocks` entries |
| --- | ---: | ---: | ---: | ---: |
| 0 | 693.14 ms | 129 | 128 | 0 |
| 1 | 664.35 ms | 129 | 128 | 0 |
| 7 | 685.58 ms | 129 | 128 | 0 |
| Each of the other 13 | 13.88–17.53 ms | 1 | 0 | 1 |

All 16 packets enter `AppendContiguousRun` once. On each slow packet, the fallback
allocates 128 separate 512-byte sectors and repeatedly updates the extent and
bitmap. The three slow packets consume 14,493,060 of the run's 16,944,089 cycles:
85.5%, including the final checkpoint in the denominator.

The bulk append algorithm tests the immediate continuation (or initial allocation
cursor), requiring a usable contiguous run in one bitmap. If that fails,
`WriteOperation` proceeds sector by sector. It should first find another eligible
run, or process the request as a small number of runs, with batched bitmap and
extent updates. The exact rejection condition for each of these three packets
was not instrumented; the sector-at-a-time fallback itself is proven by counts.

The fast packets are already near the original handler's 14.85 ms median. Also,
buffered CSUN's 8 KiB append throughput is only about 13% below original SFS.
This is not a uniform slowdown of every compiled operation: a few expensive
algorithmic paths create much of the observed gap.

## Rejected hypothesis and next work

The mount's `BufferMemoryType=0` initially looked like it might prevent direct
I/O, because `CanTransferDirect` checks for `Public`. Geometry construction already
ORs in `Public`, so both mount settings reach the same effective flag. Three
normal-driver trials explicitly setting it to 1 produced no material improvement.
For 64 KiB append: 16,944,179 cycles versus the acceptance baseline's 16,947,519.
This is not a demonstrated cause of the copy overhead.

Recommended order:

1. Apply retained TRST images after journal publication and use bulk literal
   encoding. Keep recovery validation and all durability barriers.
2. Preserve bulk append handling across allocation discontinuities.
3. Replace repeated per-bit free-run searches with word scans and reusable hints.
4. Attribute copies by caller, then remove redundant copies while preserving
   buffer ownership and checkpoint data.

After each production change, repeat the same compiled workloads and crash cuts,
including original-handler recovery compatibility. Do not infer a new default's
performance from managed tests or an isolated microbenchmark.

Diagnostics: five instruction-profile runs and three memory-flag control runs
passed. The five include the original two append profiles, direct workload
profiles for append/overwrite, and the packet-entry confirmation. The acceptance
workload diagnostics perform deep image checks and exact payload/tail validation.
The profiler changes are host-side; normal acceptance runs still use the normal
frame driver with profiling disabled.
The full CopperStart Release solution build also passed with six existing
warnings, using isolated build outputs (`solution-build.log`).

Reproduction uses `CompiledWriteDiagnosticProfile` in the native test project,
the standard A/B fixture/ROM/Python environment, `COPPERSFS_AB_PERF=1`, the pinned
`COPPERSFS_HANDLER_PATH` and `COPPERSFS_AB_PROFILE_MAP`, and
`COPPERSFS_BENCH_OUTPUT`. Set `COPPERSFS_PROFILE_MODE` to 0, 1 or 2 for 8 KiB append,
64 KiB append or overwrite. `COPPERSFS_PROFILE_NAME=CSUN-buffered` enables the
matching journal assertion. Optional `COPPERSFS_PROFILE_PUBLIC_MEMORY=1` selects
the control mount; `COPPERSFS_PROFILE_NO_INSTRUCTIONS=1` selects the normal driver.
