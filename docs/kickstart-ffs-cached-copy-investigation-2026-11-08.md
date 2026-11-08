# Cached FFS copy investigation, 2026-11-08

The requested combined acceptance target remains **unmet**. No tested filesystem
candidate both improves the cached 4 KiB Fast RAM/CF result and resolves the HDD
regressions. The unsuccessful filesystem and SDK RAM-copy experiments were
reverted. Full 4 KiB payload caching, small-read read-ahead, metadata caching,
overlay precedence and the existing reader remain in place.

This follows [the CILica investigation](kickstart-cilica-cached-ffs-2026-11-08.md).
The immediately preceding FFS baseline is
`3B9D2DEA0F645B60C6D6CC23EA36ACFF3757040BB8A952166ACB8717BD92E8B0`.
The earlier qualified HDD reference is
`45D6AC27C6F1C48227606D3A4A2B6BFE5105DFBB1C100FAB0FDBCF46376CD1A6`.
These references, their records and the original handlers are unchanged.

## What was retained

Two C# compiler changes remain in CopperSharp68k:

* Pointer-backed `Span<T>` constructors no longer produce a false managed-heap
  requirement in the static allocation analysis. Only the compiler-owned
  borrowed-span intrinsic is exempted; actual managed allocation remains
  rejected in the freestanding ROM profile.
* The existing byte Span/Buffer forward-copy intrinsic uses an aligned,
  counted longword loop for suitable ranges. It preserves the original
  backward, odd-address, short-copy and large-count fallback paths. Tails copy
  exactly the remaining bytes, without additional registers or allocations.

The general byte-copy microbenchmark improves the 4 KiB instruction replay
from 205,496 to 21,962 cycles on 68000, and 45,205 to 4,597 on 68020. This uses
the compiler test bus and is **not a Kickstart throughput gain**. FFS already
has a specialized longword copier; replacing it with the faster general Span
intrinsic still loses throughput under Kickstart. The retained FFS reader does
not use that replacement.

All new implementation and code-generation changes are C#. No ROM, original
handler, generated HUNK bytes, device latency or emulator timing was patched.
No adaptive payload bypass was introduced.

## CILica and provenance

Every filesystem candidate and final handler build uses restricted CILica preprocessing,
pinned to `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`, with
`AssumeLittleEndian=false`, `AllowCrossAssemblyIPO=false` and
`VerifyEachPass=true`. CILica was not disabled to obtain a measurement.

Local inputs, source snapshots, hashes, optimizer reports, maps, native
compatibility reports and measurements are under `artifacts/ffs-span`.
The frozen compiler/SDK base is CopperSharp68k commit
`e16a42fbeff7a15376564fed63791081c7fe8fef`. The retained compiler variant is
`compiler-byte`; the changed compiler sources and test fixture are recorded
separately from the original inventory. Native builds use caller-owned scratch
and the existing single-owner mount execution.

## Why the experiments were rejected

The baseline diagnostic cached Fast RAM/CF 4 KiB read phase is 15,053,630
machine-clock cycles. `CopyReadAhead` accounts for 3,950,488 exclusive cycles,
about 26.2% of that phase. Its source and destination are both in Fast RAM.
This is a substantial memory-transfer cost, not merely a managed allocation
or an unoptimized byte loop.

One profiled cold trial per candidate used the same frozen emulator and
licensed Kickstart 3.1 ROM. The following are diagnostic session rates, not
three-trial acceptance medians:

| Candidate | Cached Fast RAM/CF 4 KiB, KiB/s | Decision |
|---|---:|---|
| Existing specialized reader | 468.13 | Retained |
| Borrowed uint Span with wide register copying | 443.01 | Rejected |
| Metadata prefetch also on 4 KiB reads | 466.34 | Rejected as combined fix |
| Byte Span with improved compiler intrinsic | 462.95 | Rejected for FFS |
| Direct RAM copy with large inline MOVE loop | 466.75 | Rejected |
| Same kernel kept out of line | 466.49 | Rejected |
| Bounded MOVEM kernel | 445.61 | Rejected |
| Compact counted MOVE kernel | 466.74 | Rejected |
| Grouped ordinary C# loads/stores | 444.99 | Rejected |

The direct RAM-copy SDK API was removed along with its filesystem callers.
The 68000 MOVEM boundary test also demonstrated its final dummy source read;
the corrected prototype reserved the last block for ordinary transfers before
profiling. Neither prototype is part of the retained implementation.

The metadata-prefetch candidate completed a separate 288-trial IDE matrix.
It improves some HDD 4 KiB sessions, but worsens the CF deficit to approximately
5.0%. It therefore does not satisfy the combined requirement and was reverted.
Its native tests and matrix remain candidate evidence, not acceptance results.

## IDE acceptance comparison

The final rebuilt-reader matrix covers 288 FFS cold trials: Kickstart 3.0 and 3.1,
2 MiB Chip and 2 MiB Chip plus 8 MiB Fast RAM, synthetic HDD and CF profiles,
original/cached/bypass, four read sizes and three fresh-machine trials each.
The original handler is unmodified. The ROM's `scsi.device` performs actual
Gayle PIO, CopperHDF is absent, every payload is validated and media remains
read-only. These are synthetic-device emulator measurements, not calibrated
physical A1200 performance.

The final results and matched traffic audit are in `verified-ide` and
`verified-analysis`. `retained-ide` is a separate preceding control run.
The historical argument is the 45D6 qualified run, using the same frozen
harness. Filesystem comparisons use matching machine, ROM and device profiles.

| Read | Policy | Stock HDD / original | Stock CF / original | Fast HDD / original | Fast CF / original |
|---|---|---:|---:|---:|---:|
| 64 B | cached | 1.190x | 1.131–1.133x | 1.163–1.168x | 1.131–1.133x |
| 64 B | bypass | 1.190x | 1.131–1.133x | 1.163–1.168x | 1.131–1.133x |
| 512 B | cached | 1.776–1.779x | 1.566x | 1.629–1.702x | 1.519–1.523x |
| 512 B | bypass | 1.775–1.778x | 1.561x | 1.629–1.701x | 1.515–1.519x |
| 4 KiB | cached | 1.149–1.163x | 1.059x | 1.043–1.052x | 0.954x |
| 4 KiB | bypass | 1.006–1.043x | 0.987–0.988x | 1.013–1.019x | 0.992–0.993x |
| 64 KiB | cached | 1.008–1.022x | 1.001x | 1.026x | 1.001–1.002x |
| 64 KiB | bypass | 1.008–1.022x | 1.001x | 1.026x | 1.001–1.002x |

Ranges span the two ROMs. Cached Fast RAM/CF 4 KiB remains approximately
467.73/468.13 KiB/s versus original FFS 490.37/490.72 KiB/s: about a 4.6%
deficit. Both cache policies retain matching 64 B and 512 B command, byte, PIO
and completion counts in all 24 paired trials per read size.

HDD results must also be compared with the earlier Copper reference, not just
the original handler. Stock Kickstart 3.1 cached 4 KiB remains 415.23 versus
418.56 KiB/s in that reference; cached 64 KiB is 560.89 versus 562.40 KiB/s.
These regressions remain unresolved. Faster service-double instruction replay
alone did not predict the full-ROM results, including time outside the read
markers and rotational scheduling.

## Verification coverage

Host coverage: Release solution build passed. Release solution tests passed
1,307 tests with three skips; skipped coverage is not counted as a pass.

Compiler coverage: 20 targeted tests pass, including overlap direction,
alignment, exact access bounds, register preservation, short destinations,
allocation rejection, and counts around the 65,536-group byte-copy limit.
A broad test run for the rejected wide-copy prototype was stopped and is not
reported as passing coverage.

Native coverage: the retained FFS reader with matching SFS2 builds passes
132 replay tests with CSUN and 132 with TRST. CopperStart's two
`NativeSfsAmigaOsTests` pass separately. Native compatibility reports have
zero managed allocation sites. The native before/after service-double
benchmark validates all 48 measurements across two CPUs and four read sizes.

`verified-native` contains fresh CILica-enabled rebuilds of FFS, SFS2 CSUN and
SFS2 TRST. All three are byte-identical to their respective controls. The final
FFS HUNK is `7C53A7DFD98A186EC6A6ADB2FA686927AE008D263777759182635175C94AC33C`,
215,924 bytes. `verification-receipt.json` records compiler source hashes,
native hashes, CILica settings and allocation checks.

Full-ROM coverage: the final rebuilt-reader FFS matrix has 288 validated trials,
zero unavailable attempts, plus separate rejected-candidate matrices and
diagnostic profiles. SFS2 full-ROM throughput is not newly claimed by this
FFS investigation. No physical-Amiga coverage was performed. ROMs, handlers,
media and build artifacts remain local and are not published.
