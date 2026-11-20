# CPX4 write redesign

Historical checkpoint. See the [latest benchmark report](cpx4-benchmark-2026-11-15.md)
for current results and coverage. Optimization is paused; the target remains unmet.

The subsequent [CPU hot-path redesign](cpx4-cpu-redesign-2026-11-15.md) supersedes
the measurements below. Both checkpoints still miss the original-handler target.

## Acceptance result

The redesigned native CPX4 path is substantially faster than the starting
implementation, but **does not yet exceed original Kickstart FFS 40.1 or SFS
1.279**. The performance acceptance criterion remains open. CPX3 has no native
handler in this comparison; these results describe CPX4 only.

Mixed mutation time fell from 320,521,500 to 12,550,165 emulated bus cycles
(25.5 times faster). The complete sequence fell from 352,313,435 to 23,989,250
cycles (14.7 times faster). The new mutation phase still costs 7.45 times FFS
and 5.04 times SFS. This is development source, not a package or ROM release.

## Persistence contract

The user selected buffered replies. Successful writes may precede persistence;
ACTION_FLUSH, successful unmount and shutdown wait for publication and the device
barrier. This implementation transfers data before acknowledging byte progress
and retains unpublished metadata in mount-owned storage. DOS caller buffers stay
pinned through their transfers and are released before reply. An unflushed group
can be lost after power failure. Existing in-place overwrites retain their
original atomicity limitations.

Portable clients remain strict by default. `EnableBufferedWrites` accepts aligned
caller-owned storage. The native handler allocates that storage once at startup,
frees it at shutdown, and keeps the strict path if allocation fails. Operations
and the portable core allocate nothing. Native maintenance retires dirty groups
on its 30-second timer; capacity pressure can retire them earlier.

## Implementation

Contiguous allocations are bounded to 64 KiB and one bitmap window, stopping at
sparse successors, occupied units, reserved ranges and the backup page. Append
searches start after the preceding physical extent and wrap once before reporting
full. Tree-backed allocation and unwritten conversion retain the single-unit
fallback. Full mapped reads and overwrites also transfer directly; partial heads
and tails retain read/modify/write. Transport MaxTransfer, DMA mask, bounce
capacity and address-width constraints still apply.

A bounded write set holds 42 final metadata images. Repeated edits replace the
existing image. Per-operation undo preserves earlier acknowledged changes after
a rejected request. Capacity retries restore the current operation, retire the
earlier group and retry admission. Inline writes skip the large rollback copy
only after rejection checks and an eight-image admission bound. I/O or corruption
errors poison the mount and require recovery.
Rollback preserves the mount's monotonic completion-token counter. A deep-tree
regression forces post-admission cache misses and catches token reuse across a
capacity retry.

Blocks freed from the persistent generation cannot be reused until the freeing
group retires. Sixteen allocation ranges distinguish blocks first allocated in
the current group. Optional discard waits while a dirty group exists. These rules
protect the still-persistent generation from unpublished free-space changes.

Retirement invalidates the old commit slot and drains the device, writes sealed
images and descriptors, persists the commit record, installs home pages, then
persists both checkpoints. Journal layout and ordering are preserved. Home
installation reuses owned, sealed images. Recovery continues to authenticate
on-media journal pages. Mount/recovery temporarily disable buffered sealing so
repaired pages receive real CRCs, then invalidate cached state before re-entry.
DOS Format drains a pending group before beginning destructive reinitialization,
so closed-file buffered changes do not cause a spurious busy error.

An eight-object cache avoids repeated traversal. Sixteen checksum-validated clean
pages are cached separately; staging invalidates matching entries, and retirement
and recovery clear the cache. Owned scratch-page trust avoids repeated checksums.
Control fields occupy reserved gaps outside dispatcher information buffers and
symlink text.

CRC-32C uses four generated 256-entry tables in 4 KiB of caller-owned arena
memory. Polynomial, byte order and zeroed checksum field are unchanged. Anchor
discovery retains an independent calculation before initialization. Internal
copies use aligned longwords with byte tails. Bitmap ranges use byte masks after
window validation. Wide arithmetic uses 32-bit carry/borrow formulas. Normal
directory lookup seeks the existing folded-name CRC key and compares full names
for collisions; encoded entry-ID aliases retain scanning.

Native BeginIO permits IOF_QUICK completion. A retained flag is reaped immediately;
a device that clears it keeps the existing asynchronous CheckIO/WaitIO ownership.
Short transfers, errors and cancellation remain explicit. This passes native
instruction tests but adds little speed on the benchmark device. The protocol
follows the official [Exec device I/O documentation](https://wiki.amigaos.net/wiki/Exec_Device_I/O).

Optional buffered storage costs 122,248 bytes (119.4 KiB) with 1 KiB metadata pages,
or 447,880 bytes (437.4 KiB) with 4 KiB pages, in addition to the native handler's
63,488-byte maximum-geometry arena. Low-memory mounts can use the strict fallback.
The policy changes no filesystem identity, on-disk format or DOS packet ABI.

## Original-handler comparison

Both workloads used Kickstart 3.1, an emulated 68020, 512 KiB Chip plus 2 MiB Fast,
16 MiB hardfiles, 512-byte sectors, 32 requested DOS buffers and a 68000 CPX4 HUNK. Each
handler ran twice, ordered CPX4, FFS, SFS, SFS, CPX4, FFS. Values are median
emulated bus-cycle deltas. Host wall time is informational. The benchmark uses a
32 KiB handler stack; the separate ROM gate uses 4 KiB. Format and initial mount
precede timing.
Cache memory differs: CPX4 uses the fixed arenas described above; the DOS buffer
count does not limit those internal allocations.

Mixed workload: create directory; write 8 KiB; overwrite across boundaries twice;
truncate to 6003 bytes twice; close/rename; create a second 1024-byte file; flush;
inhibit/remount; read and verify every retained byte.

| Handler | Mutations | Flush | Remount | Read/verify | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| CPX4 redesigned | 12,550,165 | 2,992,355 | 6,166,620 | 2,280,110 | 23,989,250 |
| Kickstart FFS 40.1 | 1,683,650 | 20,455 | 108,980 | 969,780 | 2,782,865 |
| SFS 1.279 | 2,487,765 | 168,065 | 215,230 | 1,058,350 | 3,929,410 |

CPX4 used 117 read commands, 35 writes and eight flushes, including activity
outside the timed mutation phase. The starting run used 569 reads, 483 writes and
170 flushes. Commands differ from individual backing-sector accesses. All six
final trials passed guest verification and independent persisted-image checks.

Streaming workload: 32 writes of 64 KiB (2 MiB), the same partial overwrites and
namespace work, explicit flush/remount, then complete verification. Truncates
are no-ops at 2 MiB. Buffer initialization precedes timing.

| Handler | Mutations | Flush | Remount | Read/verify | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| CPX4 redesigned | 48,980,915 | 3,272,055 | 6,162,840 | 371,095,225 | 429,511,035 |
| Kickstart FFS 40.1 | 2,110,820 | 20,345 | 115,070 | 365,197,980 | 367,444,215 |
| SFS 1.279 | 4,561,805 | 171,910 | 217,350 | 364,911,215 | 369,862,280 |

All six streaming trials passed both checks. Guest byte verification dominates
the total; CPX4 mutation cost remains 23.2 times FFS and 10.7 times SFS.

The original profile was dominated by bit-at-a-time CRC processing. After
grouping and checksum changes, samples spread across continuation-state access,
copies, traversal and dispatch. These are statistical PC samples, not exact
inclusive cycle accounting. The remaining speed target requires reducing whole
metadata and continuation operations, including per-run restaging and repeated
namespace traversal.

## Qualification and evidence

Ignored local evidence lives under `artifacts/cpx4-write-redesign-20261115`:

- `verified-handler/source-manifest.json`: stable source, compiler and HUNK
  hashes, plus an allocation-free compatibility report.
- `verified-mixed/benchmark.json` and `verified-stream/benchmark.json`:
  per-trial timing ranges, input hashes and persisted-image checks.
- `runner` and `stream-runner`: original experiment harnesses. The maintained
  [comparison tool](../tools/CopperCPX.Bench/README.md) reproduces both workloads
  with supplied input paths. Its mixed guest binary is byte-identical to the
  experiment, and it additionally verifies CPX4 payloads through the reference
  reader. ROMs and original handler binaries remain external inputs.
- `current-tests.log` (unfiltered solution), `verified-build.log`,
  `verified-tests.log` (CPX rerun after the token fix), `verified-replay.log`, and
  explicit ROM receipts: separate coverage, recorded below when complete.

Build with `build/Build-Cpx4Handler.ps1 -ArtifactRoot <output>` and run
`dotnet build CopperFS.slnx -c Release` and
`dotnet test CopperFS.slnx -c Release`. Source integration also requires
CopperStart's native replay tests. Explicit ROM gates require `COPPERCPX_BOOT=1`,
the licensed `COPPERSHARP_KICKSTART31_ROM` path, `COPPERCPX_HANDLER_ROOT` and a
fresh `COPPERCPX_BOOT_ROOT`. Ordinary solution runs skip those gated methods;
a skip is not a pass.

Host regressions exercise run boundaries, sparse successors, buffered capacity
retry, rejection, free-space quarantine, cache invalidation, remount/replay CRCs,
and every-sector/barrier power cuts. Native tests exercise quick and deferred
I/O, abort ownership, event-loop signals and transport constraints. The ROM
crash probe distinguishes early write reply from successful explicit flush and
independently checks recovered images after publication and replay cuts.

Qualification completed on 2026-11-15:

- Host: 2,288 managed tests passed across the six managed suites. Three external
  SFS fixture tests remained skipped; those fixtures are unavailable to this run.
- Native: 75 instruction tests passed (74 CPX4 and one prototype arithmetic
  test), plus 141 CopperStart native replay tests. The standalone CPX4 HUNK built
  with zero reachable managed allocation sites, using CopperSharp alone.
- Full ROM: all 19 explicit cases passed on the emulated MC68000 with 512 KiB
  Chip, no Fast/Slow RAM and a 4 KiB handler task stack. Ten boot/memory scenarios
  observed a maximum stack use of 1,600 bytes, minimum free memory of 22,456
  bytes, and zero Exec allocations during operations. The remaining nine cases
  cover interrupted publication/replay, metadata changes and capacity boundaries.
- Comparison: all twelve final original-handler trials passed guest and
  independent persisted-payload checks. The performance acceptance target failed.

The unfiltered solution run passed 2,362 tests with nine skips. After the final
token repair, the CPX suites were rerun: 463 host plus 74 native tests passed,
including the new regression. Other suites were unchanged. The six gated ROM
method skips in ordinary runs are covered by the separate 19-case ROM run;
the three unavailable SFS fixture skips remain skips. The latest solution build
succeeded with one existing nullable warning in the sibling CopperSharp compiler.

`qualification-summary.json` records the selected latest results without counting
overlapping runs twice. The qualified HUNK SHA-256 is
`5026827CD07739A34A4A6E999EAD93BC634BFAFDEADE37A967C2B15081DDDC66`.
Its source manifest still matches all current native inputs. Coverage is emulated;
physical Amiga hardware and real-storage throughput remain unmeasured.
