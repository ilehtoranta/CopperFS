# FFS HDD follow-up — 8 November 2026

The retained C# changes remove the material HDD regressions against a fresh
CopperFFS control while preserving the Fast-RAM/CF gain. The complete final
FFS IDE matrix passed **288 cold workload trials**, with no unavailable attempts.

The largest remaining problem was explicit 4 KiB bypass: it still entered the
general read decoder on every packet. It now uses a separate direct completion
path after validating ownership, the retained run, mapping, aliases and overlays.
It retains the existing transfer routine's DMA mask, MaxTransfer, TD64 and error
prefix handling, and does not update adaptive hints. Diagnostic bypass also
prefetches extension-table metadata into the existing LRU on the first 4 KiB
read. Requested bytes and device-command counts are unchanged.

Other retained changes skip inactive sampling work when no clock exists, avoid
duplicate FFS dispatch checks and SFS write-timer checks, and allow complete
512-byte cache hits through the packet fast path. Policy 3 retains small-read
caching; legacy bypass policies 1 and 2 retain their direct path. Normal 4 KiB
reads keep the existing adaptive decision and read-ahead behavior.

All implementation changes are C#. No new code-generation change was retained.
CILica remains enabled by default in `Build-Handler.ps1`; qualification ran its
restricted pipeline, revision `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`, with
endianness assumptions and cross-assembly IPO disabled and pass verification on.
The frozen CopperSharp68k compiler is the existing `compiler-byte` variant based
on `e16a42fbeff7a15376564fed63791081c7fe8fef`.

## Matched IDE comparison

The fresh control and final build each ran 288 trials: Kickstart 3.0 (39.106)
and A1200 3.1 (40.68), stock 2 MiB Chip and 2 MiB Chip plus 8 MiB Fast RAM,
synthetic HDD and CF, original FFS and CopperFFS cached/bypass, four read sizes,
and three cold trials per workload. Small workloads read 64 KiB and 256 KiB;
4 KiB and 64 KiB workloads each read 1 MiB. Rates include open/close time and
use guest machine time. Buffers use Chip RAM on stock machines and Fast RAM
on expanded machines.

The same frozen emulator, ROMs, probe, media and timing profiles were used.
HDD/CF presets are synthetic assumptions; these are not measured physical
A1200 results or a claim of calibrated AA-Gayle cycle accuracy.

Ratios below are final median session throughput divided by the matching
original FFS median. Ranges span the two ROMs.

| Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---|---:|---:|---:|---:|
| 64 B | cached | 1.205× | 1.149–1.151× | 1.182× | 1.147–1.148× |
| 64 B | bypass | 1.205× | 1.149–1.151× | 1.182× | 1.147–1.148× |
| 512 B | cached | 2.092–2.127× | 1.946–1.947× | 1.994–2.079× | 1.908–1.911× |
| 512 B | bypass | 2.064–2.097× | 1.943–1.944× | 1.993–2.079× | 1.906–1.908× |
| 4 KiB | cached | 1.153–1.166× | 1.055× | 1.048–1.049× | 1.008× |
| 4 KiB | bypass | 1.076–1.088× | 1.020–1.021× | 1.065–1.066× | 1.029–1.030× |
| 64 KiB | cached | 1.005–1.012× | 1.002–1.003× | 1.023–1.024× | 1.001–1.003× |
| 64 KiB | bypass | 1.005–1.012× | 1.002–1.003× | 1.023–1.024× | 1.001–1.003× |

The HDD 4 KiB bypass regression is reversed:

| Configuration | Fresh Copper control KiB/s | Final KiB/s | Original FFS KiB/s |
|---|---:|---:|---:|
| Stock, KS 3.0, HDD | 364.36 | 389.57 | 361.98 |
| Stock, KS 3.1, HDD | 350.43 | 388.41 | 357.09 |
| Fast, KS 3.0, HDD | 357.54 | 378.78 | 355.62 |
| Fast, KS 3.1, HDD | 371.47 | 379.26 | 355.92 |

Cached Fast-RAM/CF 4 KiB reads reach **494.32 / 494.70 KiB/s** on KS 3.0 / 3.1,
versus **493.27 / 493.54** for the fresh control and **490.52 / 490.79** for the
original handler. The gain over the earlier pre-adaptive Copper reader remains
approximately **5.7%**. Explicit Fast-RAM/CF bypass now also exceeds original FFS.

Against the fresh control, the smallest HDD ratio is 0.999718: a **0.028%**
decrease for stock KS 3.1 cached 4 KiB. Other HDD medians are higher. Original
handler session times varied by up to **0.523%** between the matched runs;
sub-percent differences should not be overstated.

There are still historical differences: the pre-adaptive `3766` reader leads
by **0.52%** for cached Fast/HDD 4 KiB on KS 3.0 and by **0.69%** for Fast/HDD
64 KiB on KS 3.1. Those older comparisons have not been erased or called fixed.

## Profiling and rejected changes

The cached-policy native service-double replay, which excludes real ROM driver
and media costs, reduced cycles by 2.6–2.7% at 64 B, 41–44% at 512 B,
approximately 0.5% at 4 KiB and 0.4% at 64 KiB. Its device counts are unchanged.
These CPU results are separate from the full-ROM throughput above.

Doubling the C# copy-loop unroll reduced native replay cycles but did not reduce
Fast-RAM full-ROM copy cycles or improve HDD throughput. Unconditional metadata
prefetch changed the HDD adaptive decision to direct reads without a throughput
win. Prefetch after a cached decision also produced no useful Fast-RAM HDD gain.
None of those changes, including their extra per-file hint field, was retained.

## Verification and provenance

- Host: Release solution build passed; full Release solution tests passed
  **1,307**, with **3 skipped external-media tests**. Those skips are unavailable
  coverage, not passes.
- Native: **110 current FFS cases** passed across 68000/68020, including small
  read-ahead, all bypass policies, metadata future errors, aliases, chunk limits,
  error prefixes and absence of adaptive hint writes during explicit bypass.
  The compatible frozen replay suite passed **176 cases with each CSUN/TRST
  SFS2 binary**. Its two old internal no-clock-mode assertions are superseded
  by the current external-behavior test. CopperStart native OS replays passed
  **2 cases**. FFS and consumer compatibility reports have **zero managed
  allocation sites**; reads use existing scratch, request and cache storage.
- Full ROM: **288 final trials passed, zero unavailable**. Payloads, read-only
  media identities, ROMs, timing profiles and buffer placement are retained in
  raw records. All **192 before/after Copper pairs** preserve read command
  counts, transport bytes, PIO bytes and completion counts; this includes
  **48 pairs each** at 64 B and 512 B. Physical media fetches are reported
  separately and can change with command timing/order.
- This is an FFS optimization and rerun. SFS2 implementation changes and new
  SFS2 full-ROM qualification are outside this result. An exploratory combined
  reader run used stale SFS2 binaries and failed four unrelated SFS2 cases;
  those failures were not counted as successful native qualification.

The final handler is **218,960 bytes**, SHA-256
`E9A0CE080DA7D3474CE07E278D5D5A12080A62F87E277BFE610EF4140AA91F76`.
The checked-in C# rebuild and benchmarked artifact are byte-identical. The
fresh control SHA-256 is
`7F9090C426B7F2AE7A780CEA609870AA57EF48C49FAA04842ACC59999A953B68`.

Local immutable records and derived audits:

- `artifacts/ffs-span/hdd-control-ide`: fresh before matrix.
- `artifacts/ffs-span/hdd-final-fast-ide`: final matrix and report, including
  all four sizes, trial ranges, CPU timelines, transport/media/PIO statistics
  and identity hashes.
- `artifacts/ffs-span/hdd-final-fast-analysis`: paired comparison and original
  identity audit; historical means the pre-adaptive reader on the same engine.
- `artifacts/ffs-span/hdd-final-fast-preservation-audit.json` and
  `hdd-final-fast-receipt.json`: traffic, compiler, allocation and coverage
  receipts.
- `artifacts/ffs-span/hdd-final-fast-native`: C# source snapshot, CILica report,
  compiler invocation and rebuilt handler. The matching benchmark input is
  `hdd-explicit-fast-native`; their handler hashes match.

No ROM, original handler, emulator timing, package or published benchmark
record was modified or published. Unfinished superseded experimental matrices
remain incomplete records, not passing results.
