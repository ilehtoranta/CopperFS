# FFS cached reads and HDD stream resumption, 2026-11-07

This follows the [small-read/copy investigation](kickstart-ffs-small-read-profile-2026-11-07.md).
The implementation fixes a synthetic-drive stream-resumption bug, removes
repeated native range decoding on complete FFS payload-window hits, and tests
bounded FFS extension-metadata read-ahead. It keeps the existing C# copy kernel
and payload read-ahead. Final measurements distinguish these changes.

## Retained changes

`LightweightStorageDrive` previously redirected prefetch on misses only. After
a metadata read redirected the stream, a request satisfied from retained
payload could leave background reads following metadata instead of payload.
Resumption also stopped if its next predicted sector was already cached.

The drive now recognizes a hit outside the current prefetch stream, cancels
the abandoned prediction and resumes after the requested sector. It skips
retained successors and protects them while replacing abandoned predictions.
The current foreground sector and ATA PIO buffers keep their protection.
This uses the existing canonical clock and next-device deadline. Profile
parameters, spindle calculations, command/interface timing and Gayle wait
states are unchanged. Both original and Copper handlers use the corrected
drive in all new comparisons; historical records remain unchanged.

The standalone C# read dispatcher also has a native FFS cache-hit path for
whole-sector packets larger than 512 B and no larger than 4 KiB. It requires
a matching validated file cursor, a complete run and payload-window hit, no
logical overlays, a mapped output span and disjoint private buffers. Metadata
LRU priority remains intact. Misses, EOF clipping, aliases, bypass and other
formats use the existing reader. No mount/file records grow.
The normal small-read and metadata paths, invalidation and completed-prefix
errors remain in place.

The FFS-only build additionally honors up to 32 metadata pages when requested
through DosEnvec.NumberOfBuffers. The first 16 pages retain their existing
optional allocation; failure to allocate the extra pages leaves those 16.
After a successful read from offset zero with a request larger than 4 KiB,
confirmed fixed 512-byte FFS media can prefetch at most 28 following extension
headers into that metadata LRU, reserving four pages. Bounds also follow the
file size. This changes neither payload-window size nor direct-read thresholds.
There is no allocation in the read path.

Early native IDE scsi.device rejects TD_GETGEOMETRY. A read-only standard SCSI
INQUIRY fallback confirms nonremovable direct-access media; a driver name or
partition geometry alone is insufficient. It requires successful device/SCSI
status and complete command/data results. The command and response use existing
mount scratch. This is the documented [SCSI-direct interface](https://wiki.amigaos.net/wiki/SCSI_Device),
not an emulator gateway or ROM patch.

Speculation follows only bounded, plausible extension links. It does not trust
their checksums: the normal reader still validates each header when needed.
Future I/O and checksum failures remain deferred to the relevant read packet.
RAM/write overlays and private-buffer aliases decline prefetch; metadata fills
do not evict the payload window. Small requests retain their existing paths.
The new feature is selected by Build-Handler.ps1 -Ffs; SFS2 builds exclude it.
Prefetch for 4 KiB packets was measured and rejected: it adds CPU work to the
cached CF workload. The retained threshold matches the existing direct
large-read path. If a metadata slot cannot receive a DMA-safe direct transfer,
speculation stops instead of falling back through the payload window.

CopperSharp68k has two related register-constraint corrections. Displacement
folding retains both register banks for full-width longword stores. Narrow
byte/word values used by a longword store remain restricted to data registers;
allowing a byte value in An prevented the current SFS2 input resolver from
compiling. Tests reproduce both old failures and execute widened stores with
zero-extension/guard checks on 68000 and 68020. Handler bodies remain C#.
No ROM, original handler or generated instruction bytes are patched.

## Rejected experiments

Cross-table payload batching reduced large-read commands from 71 to 44, but
slowed HDD workloads despite improving CF. Its C# core and tests were removed
from maintained source. Its private snapshots and measurements remain as
negative evidence.

A spill-free eight-word grouped copier and a paired-load/store copier also
lost to the existing scalar kernel under Kickstart. Neither is retained.
CPU timing was inspected but not changed: this investigation does not qualify
the approximate A1200 CPU/bus model against physical hardware.

## Measurement protocol

Private records are under `artifacts/ffs-ide-regression/`. `before-native/`
freezes the starting native source; `final-fixed-native/` freezes the initial
source and all three rebuilt handlers. `final-bench/` references those HUNKs.
`before-ide/` is the 288-workload FFS control on the corrected drive;
`after-ide/` is the 864-workload FFS/SFS2 CSUN/TRST comparison with the originals.
Each uses three fresh-machine trials at 64 B, 512 B, 4 KiB and 64 KiB, both
Kickstart ROMs, both RAM profiles, both synthetic drives and both cache policies.
Totals, ROM/media/probe identities and partition geometry are unchanged.

New before/after comparisons use the same frozen corrected-engine harness.
Comparisons with older drive-model results are identified separately and do
not attribute a scheduler change to filesystem CPU work. The diagnostic scalar
profiles are separate from normal ExecuteFrame throughput trials.

`qualified-native/` freezes the retained implementation. `qualified-ide/`
reruns its 288 FFS trials. Both rebuilt SFS2 HUNKs are byte-identical to the
864-trial `after-ide/` run; its 576 SFS2 trials therefore remain applicable.
`qualified-results/` composes those records without editing or relabeling trials,
and records both source measurement hashes. Intermediate candidates and their
negative results remain separate.

## CPU attribution and limits

Fresh diagnostic profiles compare original FFS with the final CopperFFS on
Kickstart 3.1, Fast RAM and CF. The original 4 KiB read phase takes 14,146,350
cycles; Copper's starting snapshot takes 15,800,100 and the retained snapshot
takes 15,286,140: 3.25% fewer than the starting snapshot. These figures exclude
open/close and use scalar profiling; accepted throughput uses unprofiled frames.

The retained payload copier still takes 3,950,860 exclusive cycles, about 25.8%
of the Copper read phase. It emits the existing short postincrement longword
loop without spills. Original FFS transfers the requested payload directly
through ROM scsi.device; the cached Copper path additionally copies from its
retained window. No faster copier was qualified. The optimization reduces the
CPU gap but does not eliminate this cache-copy cost.

## Verification

Host coverage: CopperFS Release build passed, with one existing compiler nullable
warning. The Release solution tests passed 1,306 tests; three optional external
media tests were skipped. The focused CopperSharp68k regression suite passed
40 tests. The old compiler reproduces the register-bank/narrow-value failures.

Native coverage: FFS, SFS2 CSUN/TRST and the classic replay handler rebuilt with
zero managed allocation sites. The final instruction replay suite passed 118
tests against each SFS2 variant (236 executions). The native FFS before/after
benchmark passed all 48 workloads. New cases cover bounded/deferred metadata
prefetch, a seeded open cursor, DMA rejection, aliases, RAM overlays and mapping.
The DMA rejection test fails against the prior prefetch implementation and
passes against the retained handler. Existing small-read, error-prefix, overlay
and both bypass-control cases remain in the suite.

CopperScreen Release build passed. Its separately built Lightweight diagnostic
suite passed 1,323 tests with seven optional skips; focused storage tests also
passed, including zero managed allocations during cached stream redirection.
CopperStart Release build passed with 13 existing warnings. Its source-consumer
suite passed 36 tests with 46 optional skips. Two explicitly configured licensed
ROM replays then passed against the final classic handler, including mutation,
remount and recovery. Remaining optional platform/media coverage is not a pass.

`receipts/qualified-native.json` verifies all 81 native source snapshot files,
zero working-tree divergences, all four allocation reports and HUNK hashes.
Separate PDB receipts match 165 compiler, 54 engine, 12 harness, four runner and
two shared matrix source files. No CPU timing policy, ROM, original-handler
bytes, media contents or benchmark phase boundaries were changed.

## Final IDE comparison

The accepted result contains 864 validated workload trials, 72 cases and
499.5 MiB of checked payload, with no unavailable attempts. Each read size has
216 trials. It includes 192 original-SFS2 trials, 96 on Kickstart 3.1. Actual
ROM scsi.device performs Gayle PIO with CopperHDF absent; every backing image
remains unchanged. Timing parameters, memory addresses/classes, byte counts,
CPU timelines and individual trial ranges remain in the raw records.

Ratios below use median open/read/close guest throughput. Greater than 1 is
faster than the original handler in the same configuration. Ranges combine
Kickstart 3.0/3.1; SFS2 also combines CSUN/TRST.

| FS | Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---:|---|---:|---:|---:|---:|
| FFS | 64 B | cached | 1.103–1.113× | 1.047–1.048× | 1.063–1.071× | 1.032–1.033× |
| FFS | 64 B | bypass | 1.102–1.114× | 1.047–1.048× | 1.063–1.071× | 1.032–1.033× |
| FFS | 512 B | cached | 1.686–1.702× | 1.551–1.552× | 1.620–1.701× | 1.502–1.504× |
| FFS | 512 B | bypass | 1.685–1.701× | 1.546–1.548× | 1.620–1.700× | 1.499–1.500× |
| FFS | 4 KiB | cached | 1.150–1.172× | 1.048–1.049× | 1.020–1.045× | 0.941× |
| FFS | 4 KiB | bypass | 0.994–1.015× | 0.985× | 1.001–1.004× | 0.985× |
| FFS | 64 KiB | cached | 1.011–1.015× | 0.998–0.999× | 1.017–1.025× | 0.995× |
| FFS | 64 KiB | bypass | 1.011–1.015× | 0.998× | 1.017–1.025× | 0.995× |
| SFS2 | 64 B | cached | 1.086–1.097× | 1.087–1.097× | 1.019–1.021× | 1.019–1.031× |
| SFS2 | 64 B | bypass | 1.086–1.097× | 1.087–1.098× | 1.019–1.021× | 1.019–1.031× |
| SFS2 | 512 B | cached | 1.104–1.112× | 1.098–1.104× | 1.044–1.046× | 1.039–1.054× |
| SFS2 | 512 B | bypass | 1.101–1.109× | 1.096–1.102× | 1.042–1.044× | 1.037–1.052× |
| SFS2 | 4 KiB | cached | 1.221–1.226× | 1.196–1.198× | 1.136× | 1.110–1.124× |
| SFS2 | 4 KiB | bypass | 1.113–1.116× | 1.166–1.169× | 1.152–1.155× | 1.206–1.220× |
| SFS2 | 64 KiB | cached | 0.998–1.000× | 0.999–1.002× | 0.993–0.995× | 0.997–1.019× |
| SFS2 | 64 KiB | bypass | 0.998–1.000× | 0.999–1.002× | 0.993–0.995× | 0.997–1.019× |

The large FFS HDD deficit against the originals is removed: 64 KiB is now
1.1–2.5% faster across both ROMs, RAM profiles and policies. This trades roughly
1.3% of CF large-read throughput for the metadata scheduling improvement; CF
64 KiB remains within 0.5% of original FFS. Against the matched starting Copper
snapshot, all cached 4 KiB cases improve 0.3–3.2%. Small-read and bypass results
are mixed, so this is not a claim of a uniform speedup.

Cached Fast RAM/CF 4 KiB improves **2.9% in session throughput**, distinct from
the **3.25% diagnostic read-cycle reduction**. Kickstart 3.0 changes from
448.38 to 461.30 KiB/s versus original 490.38; Kickstart 3.1 changes from
448.67 to 461.66 versus original 490.67. Its deficit narrows from 8.56% to
5.91–5.93%, and remains unresolved. The next useful optimization must reduce
that extra copy cost without silently substituting the bypass policy. Another
unroll or grouped-load copier has no supporting result in this investigation.

All 96 paired FFS original controls retain matching input identities. One
unchanged original workload (Fast RAM, Kickstart 3.1 HDD, 512 B) differs by
1.108% between runs; other controls differ by at most about 0.04%. This is
reported control variation, not a filesystem gain. Small differences of that
scale should not be overinterpreted. All 72 cached/bypass trial pairs at 64 B
and all 72 at 512 B retain matching read command, transport-byte, PIO-byte and
completion counts. Small-read read-ahead is preserved.

The final FFS HUNK is
`45D6AC27C6F1C48227606D3A4A2B6BFE5105DFBB1C100FAB0FDBCF46376CD1A6`;
the starting HUNK is
`3D11F174A9FD0600B213510CE12C9484C8243D70641BA7C82215F1BFDFD0DAC9`.
Final SFS2 CSUN is
`5C33FAC15ADE226C19EAC30B8AA7C80A95FBA973E964971F16BD19DD205A4190`;
TRST is
`68BA0E401A4EC50EB7FE92AC8720D35C9785E9209A0258D0BC2DBEE49113ACE6`.
`qualified-analysis/analysis.md` retains full per-configuration before/after
rates and traffic; `audit.json` retains the pairing checks. Drive profiles
remain synthetic, not measured physical A1200 performance.
