# FFS HDD gap closure — 8 November 2026

The retained C# changes close both remaining historical HDD gaps and preserve
the Fast-RAM/CF gain. The final workspace-source build passed **288 cold IDE
workload trials**, with **zero unavailable attempts**. Every HDD workload median
exceeds both the preceding `E9A0` build and the historical pre-adaptive `3766`
build. The smallest HDD gains are **0.169%** and **0.124%**, respectively.

Rates below include mount/open/read/close and use guest elapsed time, in KiB/s.
These are synthetic HDD/CF results through native ROM `scsi.device` and Gayle
PIO, not measurements of physical A1200 hardware.

| Target workload | Previous E9A0 | Historical 3766 | Final | Matching original FFS |
|---|---:|---:|---:|---:|
| KS 3.0, Fast RAM, HDD, cached 4 KiB | 372.90 | 374.86 | **375.97** | 355.62 |
| KS 3.1, Fast RAM, HDD, cached 64 KiB | 570.22 | 574.21 | **574.92** | 556.99 |
| KS 3.1, Fast RAM, HDD, bypass 64 KiB | 570.22 | 574.20 | **574.92** | 556.99 |
| KS 3.0, Fast RAM, CF, cached 4 KiB | 494.32 | 467.81 | **496.83** | 490.36 |
| KS 3.1, Fast RAM, CF, cached 4 KiB | 494.70 | 468.01 | **497.25** | 490.82 |

The two HDD targets improve approximately **0.82%** over E9A0. Fast/CF cached
4 KiB improves **0.51–0.52%** over E9A0, remains **6.2%** above the pre-adaptive
reader, and exceeds matching original FFS by approximately **1.3%**.

## Retained implementation

- FFS header, extension-table and root lookups use the mounted geometry scalars
  they need. They preserve the existing root formula and bounds/checksum checks
  while avoiding construction and copying of the full device descriptor.
- The cached-decision state remains latched until existing seek, invalidation,
  error or read-size reset paths apply. Completed cached 4 KiB packets no longer
  update a sequential-position hint that the latched decision does not use.
- Direct 4 KiB packet validation lives in a separate helper, reducing the common
  cached-hit frame. Ownership, mapping, run bounds, aliases, overlays and the
  existing transfer/error-prefix paths remain authoritative.
- The optional FFS sampling timer uses a 40-byte request in mount-owned storage
  otherwise reserved for SFS overwrite reservations. Those uses are mutually
  exclusive. The reservation count, mount size and file-object layout do not
  change. Construction clears the request before opening `timer.device`;
  cleanup closes it without calling `DeleteIORequest` on the embedded storage.
  Chip-only and explicit-bypass mounts retain their existing no-sampling paths.
- CopperSharp68k now admits resolved, aggressively inlined value-type accessors
  in constrained calls independently of the guest-wrapper caller-arity limit.
  Small field setters are admitted alongside getters. Ordered memory effects,
  `NoInlining`, recursion checks and machine-IR verification remain in force.
  Tests exercise mutable scalar and guest-memory cursors in a ten-argument
  generic caller on both 68000 and 68020.

All implementation is C#. The compiler fix is in CopperSharp68k. No generated
HUNK, ROM, original handler, controller timing or emulator code was patched.
There is no new portable-core dependency or public cursor API. Small-read and
metadata read-ahead, diagnostic controls `0x43504301`/`0x43504302`, logical
overlays, DMA masks, MaxTransfer, TD64 and allocation-free native reads remain.

CILica remains enabled by default. Qualification used its restricted pipeline,
revision `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`, with little-endian
assumptions and cross-assembly IPO disabled and verification after every pass.
The compiler basis is the retained `compiler-byte` variant of
`e16a42fbeff7a15376564fed63791081c7fe8fef`, plus the accessor fixes above.
Concurrent unrelated compiler changes were excluded from the matched experiment.

## Complete original-handler comparison

The final matrix covers Kickstart 3.0 (39.106) and A1200 3.1 (40.68), stock
2 MiB Chip and 2 MiB Chip plus 8 MiB Fast RAM, HDD/CF, original FFS and CopperFFS
cached/bypass, all four read sizes, and three fresh-machine trials per workload.
Buffers use Chip RAM on stock machines and Fast RAM on expanded machines.
The 64 B/512 B workloads read 64 KiB/256 KiB; both larger workloads read 1 MiB.

Ratios are final median session throughput divided by matching original FFS.
Ranges combine the two ROMs.

| Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---|---:|---:|---:|---:|
| 64 B | cached | 1.212× | 1.151–1.154× | 1.186–1.195× | 1.149–1.150× |
| 64 B | bypass | 1.212× | 1.151–1.154× | 1.186–1.195× | 1.149–1.150× |
| 512 B | cached | 2.169–2.174× | 1.959–1.960× | 2.055–2.087× | 1.925–1.926× |
| 512 B | bypass | 2.137–2.173× | 1.955–1.956× | 2.001–2.086× | 1.922–1.923× |
| 4 KiB | cached | 1.155–1.170× | 1.060× | 1.050–1.057× | 1.013× |
| 4 KiB | bypass | 1.081–1.094× | 1.024–1.025× | 1.073× | 1.035× |
| 64 KiB | cached | 1.011–1.020× | 1.007–1.008× | 1.029–1.032× | 1.009–1.010× |
| 64 KiB | bypass | 1.011–1.020× | 1.007–1.008× | 1.029–1.032× | 1.010× |

All **192 before/after Copper trial pairs** preserve read and session command
counts, transport bytes, PIO bytes and completions. This includes **48 pairs
each at 64 B and 512 B**. ROM, probe, media, payload, timing profile, memory
class, device, mount control and partition identities match. Physical media
fetches remain separately reported: altered scheduling can change drive-cache
and prefetch activity without changing requested transport data.

The final 96 original-handler trials retain their identities. Their maximum
session-time difference from the preceding matrix is **0.033%**. The final
experiment has identical frozen harness and emulator-source hashes across all
three comparison matrices. Individual trial ranges and timing parameters remain
in the raw records. Sub-percent margins should not be extrapolated to real disks.

## CPU profiling and verification

The allocation-free native service-double replay uses the cached policy without
real ROM PIO or media costs. Device requests are unchanged: 3, 12, 45 and 72.

| Read | 68000 cycle reduction | 68020 cycle reduction |
|---|---:|---:|
| 64 B | 1.01% | 0.89% |
| 512 B | 2.80% | 2.62% |
| 4 KiB | 1.52% | 1.42% |
| 64 KiB | 11.05% | 9.95% |

Scalar full-ROM profiles separately identified geometry/cursor overhead and
open/read rotational waits. Reducing handler instructions did not always reduce
HDD elapsed time. Cursor-interface, metadata-batching and altered prefetch
experiments were rejected; their source and results remain isolated artifacts.
An isolated retained candidate also passed 288 trials. The results above use a
fresh rebuild of the actual workspace filesystem source, not that candidate.

- **Host:** `dotnet build CopperFS.slnx -c Release` succeeded;
  `dotnet test CopperFS.slnx -c Release` passed **1,307**, skipped **3**.
  After the final native root-lookup change, the Release solution was rebuilt
  and the 267 FFS / 18 Amiga host cases passed again. Skips are unavailable
  coverage, not passes.
- **Compiler:** the focused inlining suite passed **70** cases. The exact frozen
  compiler variant also passed all **15** borrowed-view/accessor cases.
- **Native:** the current FFS replay passed **114** cases on the final HUNK.
  The compatible cross-format legacy reader set passed **170** cases each with
  newly rebuilt CSUN and TRST handlers on the frozen filesystem source basis.
  Two legacy fixture methods (eight parameter cases) are excluded: their old
  no-clock hint/heap-request expectations are superseded by current FFS cases.
  Both CopperStart native OS replays passed. FFS, classic SFS, CSUN and TRST
  compatibility reports contain **zero managed allocation sites**.
- **Full ROM:** final workspace-source FFS IDE A/B **288 passed**, **0 unavailable**.
  Original FFS is unmodified, payloads validate, and the IDE path uses native
  `scsi.device` PIO with CopperHDF absent. SFS2 full-ROM A/B was not rerun in this
  FFS follow-up; its compiler-preservation native coverage is reported above.

## Local records

- Final workspace HUNK SHA-256:
  `CB3118D0F12B89886D625745A833A66B6AC78F6CC5D295348DB82E828A7F8991` (217,712 bytes).
- Final matrix: `artifacts/ffs-span/hdd-gap2-live-ide/`.
- Before: `artifacts/ffs-span/hdd-final-fast-ide/`; historical:
  `artifacts/ffs-span/adaptive-before-ide/`.
- Final native inputs and source hashes: `artifacts/ffs-span/hdd-gap2-live-native/`.
- Native CPU measurements: `artifacts/ffs-span/hdd-gap2-live-manifest/measurements.json`.
- Full-ROM scalar profiles: `artifacts/ffs-span/hdd-gap2-live-profile/`.
- Per-configuration analysis: `artifacts/ffs-span/hdd-gap2-live-analysis/analysis.md`.
- Preservation audit: `artifacts/ffs-span/hdd-gap2-final-preservation-audit.json`.
- Qualification receipt: `artifacts/ffs-span/hdd-gap2-final-receipt.json`.

Existing records are unchanged. No ROMs, media, packages or build artifacts are
committed or published.
