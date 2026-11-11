# CILica inlining comparison, 2026-11-11

General CILica inlining still increases all four standalone handler sizes by
13–14% with `Cilica.Core` 0.20.5-preview. It improves most measured sequential
read workloads, so this is a size/performance tradeoff. It also removes private
entry points used by some instruction-replay tests; those tests do not qualify
the corresponding behavior in the inlining-on binaries.

## Inputs and method

The filesystem source was the clean detached `origin/main` revision
`c26e6e1c73b93c2bce3152b326f4dbedf559a946`, not the older main checkout.
CopperSharp68k was `452d70c364ad4b1452e66cc30b7152615de8facd` with local
modifications, including compiler backend changes. The CopperScreen test
emulator and CopperStart test sources were also locally modified. Captured
revision, status, patches, binary hashes, build script, optimizer reports and
test results are in ignored `artifacts/cilica-inline-20261111/`. These results
are not reproducible from the dependency commit IDs alone.

The latest NuGet `Cilica.Core` available at the time was `0.20.5-preview`,
whose package identifies upstream commit
`f2f7a8c36e1b5114a0af0991d889fdc0f3f4b3ac`. An isolated copy of the local
CopperSharp restricted optimizer was built with that package. Its obsolete
`EnableScalarReplacement` property was removed to match the new API, while the
explicit scalar-replacement pass remained enabled in both arms. General
inlining alone was toggled. The other restricted passes and CopperSharp code
generation options were the same. Each pair used the same managed input and
dependencies; only `CopperFS.Amiga.dll` was CILica-rewritten. Each handler
targets MC68000 and uses the repository's freestanding HUNK code-size pass list.
These are development measurements, not published binaries or a package update.
All eight compiler compatibility reports recorded zero managed allocation sites.

## Binary size

| Handler | Inlining off | Inlining on | Increase |
| --- | ---: | ---: | ---: |
| CopperFFS | 187,236 | 213,372 | 26,136 (13.96%) |
| CopperSFS classic TRST | 182,636 | 208,032 | 25,396 (13.91%) |
| CopperSFS SFS2 TRST | 230,312 | 261,596 | 31,284 (13.58%) |
| CopperSFS SFS2 CSUN | 237,668 | 268,844 | 31,176 (13.12%) |

## Kickstart sequential-read performance

The table shows percentage change in mean open/read/close session bus cycles
with inlining on versus off; negative is faster. The existing probe runs three
trials per packet size using a licensed Kickstart 3.1, an emulated 68020,
synchronous CopperHDF, 512-byte blocks, and the same synthetic media and probe
in each pair. The 64-byte workload reads 64 KiB, the 512-byte workload reads
256 KiB, and the 4 KiB and 64 KiB workloads read 1 MiB. This is sequential-read
coverage, not a general performance benchmark.

| Handler | 64 B | 512 B | 4 KiB | 64 KiB |
| --- | ---: | ---: | ---: | ---: |
| CopperFFS | -6.54% | -5.18% | -9.51% | -11.11% |
| SFS2 TRST | -5.14% | -2.87% | -1.69% | +0.63% |
| SFS2 CSUN | -4.35% | -2.74% | -1.35% | -3.77% |

The TRST 64 KiB mean increase is small compared with its trial variation:
off trials were 260,290–261,510 cycles and on trials were 246,400–274,050.
It does not establish a repeatable speed regression. Classic SFS performance
was not measured. All six benchmark arms passed their data checks.

## Validation

- **Host:** On the tested revision, the Release solution build passed and the
  solution tests passed 1,418 cases with three external-image tests skipped.
  The original older checkout also built and passed 1,323 cases with three
  external-image tests skipped. A compiler nullable warning appeared in fresh
  native builds.
- **Native replay:** The 204-case direct-handler suite passed for each SFS2
  policy with inlining off. With inlining on, 200 passed and four failed before
  execution because `NativeAmigaFileSystem::ConfigurePayloadCache` was absent
  from both FFS and SFS2 maps. Those four cases cover both CPU models and must
  be reworked to exercise the compiled caller before their behavior is covered.
  Classic SFS off passed 159 CopperStart replay cases. With inlining on, the
  older replay passed 147, failed ten missing `InvalidateBlocks` entry points,
  and skipped one case. The updated replay for its inlined operations passed
  12 cases on the on binary; it executes those operations separately in source
  order rather than validating the complete inlined caller.
- **Kickstart 3.1 full ROM:** Each FFS binary passed three original-handler
  mutation and clean-handoff geometries. Classic SFS and both SFS2 policies
  passed their original-handler mutation and clean-handoff case with each
  inlining setting. The inlining-on SFS2 TRST handler also passed eight
  power-cut recovery and file-position cases, and SFS2 CSUN passed six power-cut
  recovery cases. The six sequential-read benchmark arms passed. These runs
  use emulated 68020 execution and synchronous storage, not physical hardware.

The optional external images and any tests not described here are not passes.
No conclusion about all workloads, physical media, or other ROM versions follows
from this comparison.
