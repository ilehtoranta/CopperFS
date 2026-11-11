# CILica inlining performance follow-up

The code-size increase is a workload-dependent speed/size tradeoff. General
inlining helped the previously measured FFS sequential reads, but it increased
guest cycles for most of the targeted OFS overwrite workloads below. The small
x64 reproduction benefited from inlining. None of these measurements alone
establishes the best setting for all workloads.

## Full-ROM OFS overwrites

These measurements use the exact three MC68000 FFS binaries from the
[root-cause investigation](cilica-inline-root-cause-2026-11-11.md): all general
CILica inlining off (187,236 bytes); all on (213,372 bytes); and on with only
`Header`/`Put` temporarily prevented from inlining by CILica (203,844 bytes).
The helper control's temporary attributes were cleared before CopperSharp
compilation. The source and compiler settings were otherwise unchanged.

Each cell uses the existing `OfsHardDiskPlacement` guest probe and times the
guest `DOS.Write` call in bus cycles. It executes in an emulated 68020 running
Kickstart 3.1 with synchronous CopperHDF storage. The payload contains the
specified number of 488-byte OFS data blocks; `run` and `stride` describe how
those blocks were scattered. The packed case places the payload contiguously.
Two independent paired runs per cell passed and gave the listed ranges. The
experiment disables the fixture's arm-specific relocation expectation because
both binaries have the same filesystem policy; the test still validates OFS
block pointers and checksums and reads the data back after a fresh boot. The
two arms issued identical read/write/flush counts and produced byte-identical
written disk images in every measured pair.

Positive percentages mean the fully inlined binary used more cycles; negative
percentages mean it used fewer. Compare the two columns against different
baselines: the second column isolates the two helpers' combined CILica impact.

| OFS overwrite | Fully on vs all off | Fully on vs helpers kept out |
| --- | ---: | ---: |
| 8 blocks, scattered 1 at a time | +2.31% | +4.54% to +4.55% |
| 8 blocks, packed | +2.60% | +4.80% to +4.83% |
| 32 blocks, runs of 4, stride 257 | +4.57% to +4.58% | +5.01% |
| 32 blocks, packed | +4.51% to +4.66% | +5.13% to +5.29% |
| 32 blocks, runs of 4, stride 5 | +4.51% | +5.13% to +5.14% |
| 32 blocks, packed, stride-5 fixture | +4.52% | +5.27% to +5.28% |
| 64 blocks, runs of 8, stride 257 | -6.76% to -6.07% | -2.02% to -1.31% |
| 64 blocks, packed | +3.11% | +4.70% |

The scattered 64-block case exercises the handler's relocation path; its
baseline and on arms still issued matching I/O and produced the same image.
These are simulated bus cycles, not measurements of physical disk latency,
and two runs do not represent a full workload distribution. Raw data and
test results are in ignored `artifacts/cilica-inline-20261111/performance-*`.

In the previous independent FFS **read** experiment, fully on used 5.18–11.11%
fewer open/read/close session cycles than all off across 64-byte, 512-byte,
4 KiB and 64 KiB read requests. Repeating that read comparison between fully on
and the two-helper control gave differences of +0.10%, -0.07%, +0.08%, and
-1.35% respectively; the 64 KiB trials varied more than that last difference.
That is no clear read regression from retaining these two helpers in this
limited test. The read probes and three trials per arm are described in
[the original comparison](cilica-inlining-comparison-2026-11-11.md).

## x64 reduced example

On Windows x64, .NET 10.0.12, and an AMD Ryzen 5 5600X, the standalone
[reproduction](../repros/cilica-inline-size/README.md) timed nine rotated rounds
of three million checksum-writing operations per arm after warmup. Tiered
compilation and ReadyToRun were disabled, and the benchmark validates the result.

| CILica setting | Median ns/op | Median paired time change vs off |
| --- | ---: | ---: |
| Inlining off | 103.28 | baseline |
| Inlining on | 89.48 | -13.06% |
| On, 200-byte IL cap | 100.04 | -3.22% |

The medians and paired percentage are calculated separately. Individual samples
ranged from 102.71–114.56, 89.20–99.78, and 99.09–108.73 ns/op respectively.
This is a synthetic hot loop with an in-memory device, not the real filesystem,
and its benefit cannot be transferred directly to the 68000 results. Raw samples
are in the reproduction's ignored benchmark artifact directory. Run
`./benchmark.ps1` in the reproduction to repeat the experiment.

The tests above passed 16 OFS full-ROM theory cases across the four paired runs
and two FFS read benchmark arms. The current checkout's Release solution build
passed with one existing CopperSharp nullable warning; its host tests passed
1,323 with three external-image tests skipped. Native instruction replay was
not rerun for this timing-only follow-up; the prior control-binary replay is
recorded in the root-cause investigation. No real hardware, physical media,
other ROM versions, or general filesystem workload were measured here.
