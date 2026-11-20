# CopperNova metadata cache policy experiment — 17 November 2026

The isolated CNM1 prototype now uses reuse-aware CLOCK replacement. Under a
working set larger than the metadata cache it reduces native end-to-end cycles
by **4.97%** and metadata reads/CRC-validation bytes by **30.54%**. The original
mixed and streaming workloads fit in cache and show small regressions instead.
This is a cache-pressure improvement, not achievement of the cross-filesystem
performance objective. Existing CPX4/FFS/SFS source and formats are unchanged.

## Controlled measurements

Native figures are emulated **68020 bus cycles** on the existing Kickstart 3.1
configuration: 512 KiB Chip + 2 MiB Fast, 16 MiB disk, 512-byte sectors, 32 DOS
buffers. They include guest, DOS and handler execution. Host wall time is not
used as a performance result. Lower cycle counts are better.

| Workload | Frozen P4 | Final CLOCK | Change |
|---|---:|---:|---:|
| Metadata cache pressure, measured scan | 211,405,165 | 200,891,170 | **4.97% faster** |
| Original mixed, all four phases | 5,040,010 | 5,043,400 | 0.0673% slower |
| Original stream, all four phases | 369,689,200 | 369,694,425 | 0.00141% slower |

Pressure figures are two-trial medians for each native handler, with identical
seed and guest HUNK. Baseline range: 211,402,460–211,407,870; final range:
200,890,940–200,891,400. The original workloads have one freshly rerun P4 trial
each and two final trials each. Final mixed trials both give 5,043,400; final
streaming range is 369,693,440–369,695,410. The tiny original-workload changes
should not be presented as gains or extrapolated to other platforms.

Both original workloads use their unchanged frozen guest HUNKs and produce
byte-identical final media between baseline and final handlers. All guest and
independent ownership/payload checks pass.

Fresh two-trial reference medians, using those same frozen original guests:

| Workload | Final CNM1 | Kickstart FFS 40.1 | SFS 1.279 |
|---|---:|---:|---:|
| Mixed | 5,043,400 | 2,783,360 | 3,978,650 |
| Streaming | 369,694,425 | 367,442,630 | 369,854,875 |

FFS still wins both original workloads. SFS wins mixed; CNM1 remains narrowly
ahead of SFS on streaming. Reference trials were collected alongside CLOCK
revision 1; final CNM1 was measured separately against the same frozen inputs.
The pressure workload compares the two CNM1 implementations, not FFS/SFS.

## Why pressure improves

The fixture has 115 metadata objects (58,880 bytes of 512-byte pages) competing
for the existing 64-page / 32,768-byte metadata cache. It scans 72 files three
times and revisits six hot file paths after each scan entry: 1,512 complete
open/read-one-byte/close sequences, each verifying its payload. Setup is excluded.

The portable-engine experiment loads the exact same seeded image for each
policy and observes:

| Counter | P4 | CLOCK |
|---|---:|---:|
| Metadata sectors read and validated | 465 | 323 |
| Metadata CRC-validation bytes, derived from reads | 238,080 | 165,376 |
| Data sectors read | 1,512 | 1,512 |
| Measured write requests / flushes | 0 / 0 | 0 / 0 |
| Caller-owned arena bytes | 98,304 | 98,304 |

Retaining reused directory/inode pages avoids 142 metadata reads and 71 KiB of
CRC validation. Native whole-run read requests likewise fall by exactly 142,
from 1,991 to 1,849 (those totals include mounting and data). The native probe
verifies every byte read. Independent checks also validate the complete image,
which must remain byte-identical after the read-only experiment.

## Policy and limits

The P4 policy selects the first clean page untouched by the current packet.
CLOCK instead compares each candidate's existing access epoch with the epoch
observed on its previous visit by the clock hand. A changed epoch earns one
second chance. A newly admitted page starts probationary.

There are no additional stores on ordinary cache hits. New state occupies four
bytes of existing mount-state padding and four bytes of existing per-page
padding. The victim scan examines at most 128 slots. Dirty pages and all pages
used by the current packet remain ineligible. The hand may retain its bounded
position across remount; page ownership and access observations are invalidated.

The implementation retains all previous dirty-page admission thresholds and
pressure-triggered commits. Flush cleans pages without evicting them. Metadata
remains separate from data: aligned data transfers use caller buffers, and
partial sectors use scratch. Streaming data cannot displace metadata.

CRC coverage, the on-disk format, commit/recovery ordering and the buffered
durability contract are unchanged. Earlier write replies may precede persistence;
successful flush, unmount and shutdown retire buffered writes. The experiment
does not repair the expensive CRC kernel or reduce the four commit barriers.
Those remain major costs in the original workloads.

## Qualification and provenance

Final native HUNK: 31,340 bytes,
SHA-256 `4572155BEEE109B407E660C18308BB59510CE9BE6E882C3EC68415BCB5FF666F`.
Frozen P4 HUNK:
`434545812FBB9B04CB84606106278EC4619F6EF5B1D1A0B5E3D80AF7E4FA7B38`.
Both use the same compiler binary. Source manifests identify every engine and
adapter input; the baseline host source matches the P4 manifest.

- Final prototype: 21 host tests pass, including cache pressure, retained clean
  pages after flush, sector crash/recovery cases, failed/short I/O and alias guards.
- CopperStart integration replay: 169 pass. These cover existing integration
  paths, separately from the new native prototype adapter.
- Actual final HUNK: booted Kickstart on MC68000, 512 KiB Chip, zero Fast and a
  4 KiB handler stack. Peak stack 740 bytes; guard intact; nine startup Exec
  allocations and **zero operation allocations**. Four shutdown barriers and
  independent persisted-tail/ownership checks pass.
- That 4 KiB stack gate covers mixed operations and shutdown. The separate
  cache-pressure timing probe retains the benchmark's 32 KiB handler stack.
- Full solution regression: **2,747 pass, zero failures, ten optional skips**
  (2,608 host cases including the prototype, plus 139 compiled-native cases).
  The receipt is recorded by the evidence generator.
  It began on revision 1; unchanged legacy suites continue to apply. Revision 3
  was rebuilt and its 21 prototype tests rerun separately. Optional legacy
  ROM/media skips remain skips; no physical-device or broad-platform pass.

The final Release solution and standalone handler builds pass. The .NET 10 SDK
requires the existing net10 target overrides because neighboring compiler
projects also declare net11. No compiler sources or published versions changed.

Reproduction is documented in `tools/CopperNova.CacheBench/README.md`.
`tools/studies/record_cache_policy.mjs` checks the native/source/input hashes,
benchmark receipts and test completion before writing the source-only evidence
file `benchmarks/coppernova-prototype/2026-11-17-cache-policy.json`. ROMs, media and
build outputs remain in ignored artifact directories.
