# Combined geometry division, 2026-11-12

CopperFFS now computes paired quotients and remainders through
`AmigaGeometryMath.DivRem`. `Divide` and `Remainder` retain their original
implementations for single-result callers. The combined helper retains the
existing zero-divisor result: both outputs are zero.

The paired callers cover OFS/FFS reads, writes, extension-table traversal and
file-information block counts. Read validation still precedes the zero-length
return. The FFS write copy loop caches its source and destination pointers;
memory accesses remain a forward byte copy. This keeps the additional arithmetic
temporaries from causing spills inside the byte loop. The measured FFS write
improvement below includes that adjustment, not just division pairing.

SFS2 has a separate internal `AmigaSfsGeometryMath.DivRem(ulong, uint, out uint)`.
It uses one 64-bit division and computes the remainder as
`unchecked((uint)value - (uint)quotient * divisor)`. Since the remainder is below
the 32-bit divisor, taking this difference modulo 2^32 retains the exact result,
including when the quotient exceeds 32 bits. This avoids a second runtime
division and a 64-bit multiplication. Zero division retains SFS's existing
exception behavior. Readers, writers, resize padding and file-information
projection reuse the paired results. Two separated SFS2 bitmap pairs were also
moved together so the compiler can combine their 32-bit operations.

Adjacent 32-bit pairs in classic SFS are already eligible for native fusion.
The additional classic read/bitmap experiment gave negligible mixed-workload
gains but slowed seek packets by 0.234%, so it was discarded. The rebuilt classic
handler is byte-identical to its baseline. No changes to the classic native
reader or reservation arithmetic are retained.

There are no new application dependencies, managed allocations, persistent
native state, disk fields, packet layouts or journal-ordering changes.

## Generated native code

Disassembly of the final MC68000 HUNKs confirms:

- FFS `DivRem` has one guarded `DIVU.W` path and one 32-step fallback division
  loop, retaining both outputs. The single-result helpers remain reachable.
- SFS2 `DivRem` calls `Integer64Arithmetic.DivideUnsigned` once, followed by a
  32-bit product/subtraction. It does not call the remainder runtime helper.
- All four compatibility reports contain zero managed allocation sites.

Complete executable sizes, in bytes:

| Handler | Baseline | Final | Change |
| --- | ---: | ---: | ---: |
| CopperFFS, including OFS | 55,296 | 55,140 | -156 |
| Classic buffered CopperSFS | 141,688 | 141,688 | 0, identical binary |
| SFS2 CSUN | 180,160 | 179,984 | -176 |
| SFS2 TRST | 175,444 | 175,100 | -344 |

The baseline and candidate inputs use the same compiler DLL hash. Sources were
frozen from the already modified working tree; the comparison is against that
snapshot, not an otherwise clean Git revision. This excludes concurrent work
from the measured changes. Current-checkout host checks are recorded separately.

## Arithmetic probes

The new ROM-free tests execute 290 unsigned 32-bit vectors and 352 unsigned
64-bit vectors per arm and CPU combination, including quotient-width boundaries,
high-bit divisors, maximum values and seeded random inputs. Host checks also
verify the differing zero-divisor policies. The MC68000 code-generation check
requires one fallback loop for the paired FFS probe versus two for separate
calls. Native results are checked against host quotient and remainder oracles.

Changes in summed synthetic RAM-bus instruction cycles; negative is faster:

| Output target / execution model | FFS 32-bit pair | SFS2 64-bit pair |
| --- | ---: | ---: |
| 68000 / 68000 | -41.72% | -48.35% |
| 68000 / 68020 | -42.73% | -47.48% |
| 68020 / 68020 | +6.25% | -47.86% |

**Explicit MC68020 output is a compiler limitation:** CopperSharp currently
runs its 32-bit division-combining pass only for MC68000. The FFS helper still
emits two `DIVU.L` instructions for `-Cpu 68020`, and this arithmetic-only probe
is slower. Its correctness checks pass, but a one-division claim and the handler
performance qualification below apply to MC68000 output. Compiler source was
not changed in this work.

## Full-ROM workload measurements

These tests use a licensed Kickstart 3.1, an emulated 68020, MC68000 handlers,
512-byte blocks and synchronous CopperHDF storage. They measure guest bus
cycles, not host execution time or physical-device throughput. Three trials
alternate baseline/candidate order. Returned data, independent persisted-image
walkers, and paired read/write/flush command counts all pass.

Mean cycle changes for the mixed growth, overwrite, metadata and read workload:

| Profile | Mutations | Read/enumeration verification |
| --- | ---: | ---: |
| OFS | -0.20% | -0.34% |
| FFS | -13.35% | -0.42% |
| SFS2 CSUN | -0.15% | -0.41% |
| SFS2 TRST | -0.05% | -0.33% |

The warmed small-file probe separately times 512 seeks, 512 seek/one-byte-read
pairs and 512 zero-length reads:

| Profile | Seeks | One-byte reads | Zero-length reads |
| --- | ---: | ---: | ---: |
| OFS | -0.21% | -1.21% | -0.50% |
| FFS | -0.14% | -1.38% | -0.65% |
| SFS2 CSUN | +0.05% | -0.41% | +0.05% |
| SFS2 TRST | +0.07% | -0.36% | +0.07% |

All eight final comparisons pass the existing 0.1% mean-cycle allowance. The
small SFS2 seek/empty-read increases remain visible above; those phases do not
establish a speedup. Earlier candidates and their failing performance receipts
are retained in the artifact directory.

## Coverage and reproduction

- **Host:** Release solution build and complete solution test run passed:
  1,335 passed, three optional external-media tests skipped. After the final
  edits, focused runs passed 257 FFS tests and 838 SFS tests, with one optional
  media skip. The final solution build has zero warnings and errors.
- **Native:** The four final handlers passed 193 CopperStart replay cases;
  two optional SFS2 cache-summary cases were skipped. Additional checks passed
  all 52 size-dispatch cases with SFS2 enabled and six final FFS overwrite/fault
  cases. The new arithmetic suite passed all seven cases, including both CPU
  execution models and explicit MC68020 output.
- **Full ROM:** All eight final paired workload/small-packet comparisons passed.
  Classic SFS has no new performance claim because its final executable is
  identical. Other ROM versions, physical hardware, additional media and
  power-cut workloads were not newly qualified by this change.

The standard commands were run with isolated output directories to avoid other
active builds in the checkout:

```powershell
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/divrem-20261112/host
dotnet test CopperFS.slnx -c Release --no-build --artifacts-path artifacts/divrem-20261112/host
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~GeometryMathInstructionTests
```

The standalone profiles use `build/Build-Handler.ps1` with `-Ffs`, no family
switch for classic SFS, `-Sfs2`, and `-Sfs2 -Sfs2TrstWrites`, respectively.
Performance probes and manifest setup follow the
[test README](../tests/CopperFS.Kickstart.Tests/README.md).

Ignored `artifacts/divrem-20261112/` contains the frozen before/after sources,
`optimization.patch`, all candidate HUNKs/maps/compatibility reports, final
disassemblies, arithmetic samples, full-ROM samples and TRX receipts.
`accepted/manifest.json`, `accepted-results.json`, `native-code-summary.json`,
`rom-ffs-final.log` and `rom-sfs2-accepted.log` identify the final comparisons.
FFS samples are under `qualification-final/`; final SFS2 samples are under
`accepted/`. No packages, handlers, ROMs or media were published.
