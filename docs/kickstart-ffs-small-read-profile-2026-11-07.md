# FFS small-read and cached-copy profiling, 2026-11-07

This follows the [IDE cache-policy optimization](kickstart-ide-read-optimization-2026-11-07.md).
The retained change optimizes partial FFS reads in C#. The cached 4 KiB payload
copy remains the original interleaved C# loop: the alternatives measured here
were slower. No read-ahead threshold or cache budget was reduced.

## Retained implementation

* The standalone packet dispatcher routes positive sub-sector reads through
  the existing native retained-run adapter. A request wholly within one
  512-byte sector can consume a valid retained run without reconstructing the
  general cursor. It still checks mapping, private-record/scratch aliases,
  the run bounds and EOF. Misses and aliases use the general reader.
* After resolving RAM, redo, dirty, staged and queued-write precedence, FFS
  partial reads can borrow a current physical payload-window image when the
  metadata LRU has no image. The view is consumed immediately. This avoids
  copying an entire sector into scratch and the metadata LRU for each new
  sector of a small-read stream.

Neither change grows mount state, file records or buffers. Public packet ABI,
completed-prefix errors, invalidation, overlay precedence and native read-ahead
remain in place. No emulator, ROM or original-handler instructions are patched.

## Diagnostic method and findings

The C# storage harness now supports `COPPERFS_STORAGE_CPU_PROFILE=64,4096`.
This uses the existing external scalar instruction profiler and real native
Gayle PIO under A1200 Kickstart. Profiles are explicitly diagnostic. The formal
runner clears the setting and rejects cases with a diagnostic marker; accepted
throughput continues to use normal `ExecuteFrame` execution.

The diagnostic machine is PAL A1200, M68EC020, Kickstart 3.1 40.68, 2 MiB Chip
plus 8 MiB Fast, CF-like drive, Fast RAM output. Each workload starts a fresh
machine. Both original and Copper FFS payloads and read-only media are checked.

The 64-byte baseline spends approximately 1.85 million exclusive cycles in the
general reader, 1.19 million reconstructing its native cursor and 1.22 million
in block copies. The retained path removes most general-reader visits and
avoids the extra whole-sector copies. Its diagnostic read phase falls from
16,700,480 to 14,303,260 guest cycles, 14.35% fewer. These are diagnostic read
phases, distinct from the unprofiled open/read/close throughput below.

| Read size | Original FFS read cycles | Copper before | Copper retained | Retained / before |
|---|---:|---:|---:|---:|
| 64 B | 14,612,920 | 16,700,480 | 14,303,260 | 0.8565 |
| 4 KiB | 14,146,810 | 15,890,520 | 15,860,470 | 0.9981 |

The baseline cached 4 KiB read phase spends about 3.95 million cycles in the
payload copy helper, roughly a quarter of its 15.9-million-cycle read phase.
The original handler transfers the 4 KiB payload directly through ROM
scsi.device. Copper's cached path additionally copies retained payload bytes.
The existing helper already emits ordinary postincrement memory-to-memory
longword moves, without spills or a frame.

Measured alternatives were rejected:

* Eight-word grouped loads/stores increased the 4 KiB read phase to about
  16.7 million cycles; an incrementing variant with a spill reached 17.3 million.
* Spill-free four-word tiles still reached about 16.7 million cycles.
* Calling real Exec `CopyMemQuick` reached about 16.6 million cycles.
* A 68020-targeted compiler prototype combined loads and stores using MOVEM.
  Differential tests checked registers, flags, overlap, access order and exact
  bus footprints on supported models, including retaining scalar loads on
  68000 to avoid its extra read. Full compilation-pipeline tests also passed.
  Its real Kickstart copy remained slower than the original interleaved loop.

The code-generation experiments were implemented in CopperSharp68k, measured
in isolated compiler snapshots, and removed from the maintained source after
they failed the actual workload. No compiler change or CPU-specific handler
requirement is retained. Experimental artifacts remain separate from acceptance.

The remaining 4 KiB copy is close to the modeled longword-transfer cost. This
is a limitation of the measured cached path, not a claim about physical A1200
cycle accuracy. Changing cache/transport policy or the emulator's timing to
produce a better number is outside this optimization.

## Measurement records

Private records are under `artifacts/ffs-small-copy/`. `before-native/` freezes
the initial working tree; `before-ide/` contains its 288-trial FFS-only IDE
control. Source updates from overlapping work are retained, including the FFS
checksum/pointer cursor and SFS2 path-cache changes. Final source/handler
identities and fresh original-handler comparisons are recorded with acceptance.

The final FFS HUNK is
`4CDB9DD7126997D597B04875B73621CBDA4A825C242AEA03E8808215E564C8D0`;
the before HUNK is
`D65B8AE3784929836EB38A94D9C4FD0E8BD21576859BAB86878D12C32EA750B0`.
Both use the same frozen CopperSharp68k compiler, targeting 68000 instructions
on the emulated M68EC020. The retained checksum cursor change was supplied by
overlapping work, so the aggregate before/after ratio includes that change;
it does not isolate every cycle to the partial-packet optimization.

`receipts/final-native-frozen.json` verifies all 81 files in the final native
source snapshot and records zero allocation sites in FFS, SFS2 CSUN/TRST and
the classic SFS replay build. The benchmark uses those frozen handlers.
Subsequent FFS metadata-view and SFS2 mount-lookup edits in the shared working
tree are outside this measured snapshot and have not been overwritten.

PDB checks verify 54 engine files, four runner files and two shared matrix
files. The harness has 12 matching files in `verified-harness-source/`.
Its snapshot restores the pre-existing `CorrectnessProbe.cs` from the earlier
immutable IDE record to match the actual binary; that separate correctness
probe changed after the harness build. Raw run source records are unchanged.

The comparison retains Kickstart 3.0 and 3.1, both RAM profiles, HDD-like and
CF-like drives, original/cached/bypass arms, all four read sizes and three cold
trials. Drive assumptions remain synthetic and AA-Gayle physical timing is
unverified. Host wall time does not determine guest device deadlines.

Read totals remain 64 KiB at 64 B, 256 KiB at 512 B, and 1 MiB at 4 KiB and
64 KiB. Stock uses 2 MiB Chip and Chip output; expanded uses 2 MiB Chip plus
8 MiB Fast and Fast output. Native ROM `scsi.device` performs Gayle PIO with
CopperHDF absent. Every trial starts a fresh machine and empty synthetic drive
cache. Timing profiles, ROM/media/handler/probe hashes, exact buffer addresses,
per-trial CPU/native timeline counters, elapsed guest time, transport commands
and bytes, physical backing fetches and PIO bytes are retained in each case.
Timeline counters include waits; exclusive CPU attribution comes from the
separate diagnostic profiles. Final summary ratios use median unprofiled
open/read/close guest throughput, compared only within matching configurations.

## Verification and final A/B

Host coverage: the Release solution build passed; the Release solution tests
passed 1,305 tests with three optional image/media tests skipped. The frozen
compiler emits one pre-existing nullable warning.

Native coverage: all three affected standalone handlers rebuilt with zero
managed allocation sites. Instruction replays passed 76 tests against each
SFS2 variant (152 executions), including both CPU models, partial-sector and
odd-address reads, EOF and seek errors, read-ahead, aliases, overlays and both
bypass controls. The native before/after service-double benchmark also passed
all 48 workloads. Its 64 B replay uses 25.15% fewer cycles on 68020 and 27.74%
fewer on 68000, with three device requests both before and after. Those replay
ratios are not full-ROM throughput.

CopperStart's Release solution build passed with 13 existing warnings. Its
source-consumer native suite passed 36 tests with 46 optional tests skipped;
explicitly configured licensed-ROM replays then passed both selected OS tests,
including read/write/remount/recovery. The remaining optional media/A/B tests
were not run and are not passes.

Full-ROM coverage: `before-ide/` and `after-ide/` each completed 288 validated
FFS workloads, 24 cases and 166.5 MiB of checked payload, with no unavailable
attempts. Each size has 72 trials. This is a fresh FFS-only comparison with
original FastFileSystem; the prior SFS2 IDE record is not relabeled as a rerun.
The shared copy loop is unchanged, and SFS2 CSUN/TRST received the native
regression coverage above.

All 96 paired original trials retain the same input identities. Their guest
session times differ by at most 0.028%. The before/after harness and source
manifest hashes match. All 24 cached/bypass pairs at 64 B and all 24 at 512 B
have matching command, transport-byte, PIO-byte and completion counts. Thus
the 64 B improvement does not come from reducing read-ahead or device traffic.
The diagnostic report-rejection check also passed.

### Final comparison against original FastFileSystem

Ratios use median open/read/close guest throughput. Greater than 1 is faster;
ranges cover Kickstart 3.0 and 3.1. Both policies use the same Copper HUNK.

| Read size | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---|---:|---:|---:|---:|
| 64 B | cached | 1.106× | 1.058× | 1.068–1.070× | 1.038–1.039× |
| 64 B | bypass | 1.106× | 1.057× | 1.068–1.070× | 1.038–1.039× |
| 512 B | cached | 1.724–1.728× | 1.547–1.548× | 1.626–1.694× | 1.498–1.500× |
| 512 B | bypass | 1.704–1.727× | 1.542–1.544× | 1.622–1.694× | 1.494–1.496× |
| 4 KiB | cached | 1.228× | 1.017× | 1.076–1.083× | 0.911× |
| 4 KiB | bypass | 1.012× | 0.990–0.992× | 1.012× | 0.991× |
| 64 KiB | cached | 1.018–1.028× | 1.004–1.005× | 0.968–0.972× | 1.000× |
| 64 KiB | bypass | 1.018–1.028× | 1.004–1.005× | 0.968–0.972× | 1.000× |

### Change from the frozen Copper baseline

| Read size | Cached throughput ratio | Bypass throughput ratio |
|---|---:|---:|
| 64 B | 1.128–1.161× | 1.128–1.161× |
| 512 B | 0.983–1.012× | 0.983–1.011× |
| 4 KiB | 0.989–1.006× | 0.997–1.006× |
| 64 KiB | 0.960–1.003× | 0.960–1.003× |

The retained optimization improves 64 B sessions by 12.8–16.1% across every
machine/drive/policy combination. The requested cached 4 KiB Fast RAM/CF
hotspot has no material improvement: 446.35–446.67 KiB/s before becomes
446.72–446.88 KiB/s, against the original's 490.37–490.73 KiB/s. Its remaining
gap is about 8.9%; bypass reaches 486.00–486.09 KiB/s, about 0.9% behind.

Larger workloads are not uniformly faster. The largest measured regression
is a 4.0% 64 KiB HDD session slowdown. For example, stock 3.0 cached HDD moves
from 558.72 to 536.45 KiB/s. Its read phase rises from 1,738.45 to 1,809.96 ms,
with 71 commands and 1,038 KiB PIO unchanged; physical fetches rise from
4,234 to 4,253 sectors. Fast 3.1 HDD likewise adds 56.78 ms during the read
phase with unchanged commands/PIO and 85 more prefetched sectors. Fast 3.1
CF's corresponding read phase becomes slightly shorter while open/close adds
about 2.5 ms. These observations are consistent with changed CPU/request
timing interacting with the synthetic HDD's rotation and background prefetch;
that mechanism is an inference, not a separately traced per-command proof.
The slowdowns remain in the report and are not described as improvements.

`after-ide/report.md` preserves per-trial ranges and session traffic;
`after-ide/measurements.json` preserves separate read/session counters and
identities. `final-analysis/analysis.md` pairs all 64 Copper configurations
with their baseline and original, and `final-analysis/audit.json` records the
matrix and traffic checks. Existing benchmark records are unchanged.

Further cached 4 KiB work needs to remove redundant payload movement or find
a demonstrably faster generated kernel. The scalar kernel retained here
outperforms every tested replacement. No new copy optimization or compiler
fix is claimed for that hotspot, and no cache-policy or emulator timing
adjustment was used to conceal the remaining gap.
