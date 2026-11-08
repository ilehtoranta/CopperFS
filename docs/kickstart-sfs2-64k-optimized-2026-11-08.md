# SFS2 64 KiB optimization — 8–9 November 2026

All **576 cold workload trials passed**, with three trials per workload: CSUN/TRST, Kickstart 3.0/3.1, stock 2 MiB Chip / 2 MiB Chip plus 8 MiB Fast, HDD/CF, original/cached/bypass, and 64 B/512 B/4 KiB/64 KiB reads. The original handler and ROMs are unmodified. IDE uses Gayle and native `scsi.device`; CopperHDF is absent. Synthetic timing is not calibrated physical A1200 performance.

Both freshly rebuilt handlers use restricted CILica with verification enabled, endian assumptions and cross-assembly IPO disabled. Both compiler compatibility reports show **zero managed allocation sites**; both current native reader suites pass **204 tests**. The retained C# changes isolate directory enumeration, first-cursor initialization and no-file reads, and separate aligned reads, partial reads and cursor restoration into small frames. CILica remains enabled; no new code-generation fix was needed.

Ratios below are median session throughput divided by the matching original SFS2 handler; ranges span both ROMs. Session time includes mount/open/read/close.

| Build | Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---:|---|---:|---:|---:|---:|
| sfs2-csun | 64 | cached | 1.370–1.389× | 1.380–1.390× | 1.321–1.331× | 1.322–1.322× |
| sfs2-csun | 64 | bypass | 1.370–1.389× | 1.380–1.390× | 1.321–1.331× | 1.322–1.322× |
| sfs2-csun | 512 | cached | 1.329–1.348× | 1.330–1.337× | 1.293–1.306× | 1.286–1.287× |
| sfs2-csun | 512 | bypass | 1.325–1.344× | 1.326–1.334× | 1.290–1.303× | 1.283–1.283× |
| sfs2-csun | 4096 | cached | 1.319–1.329× | 1.291–1.295× | 1.243–1.253× | 1.213–1.213× |
| sfs2-csun | 4096 | bypass | 1.196–1.205× | 1.263–1.263× | 1.268–1.279× | 1.334–1.335× |
| sfs2-csun | 65536 | cached | 1.015–1.029× | 1.015–1.017× | 1.030–1.045× | 1.022–1.022× |
| sfs2-csun | 65536 | bypass | 1.015–1.029× | 1.015–1.017× | 1.030–1.045× | 1.022–1.022× |
| sfs2-trst | 64 | cached | 1.369–1.380× | 1.376–1.386× | 1.318–1.318× | 1.319–1.320× |
| sfs2-trst | 64 | bypass | 1.369–1.380× | 1.376–1.386× | 1.318–1.318× | 1.319–1.321× |
| sfs2-trst | 512 | cached | 1.333–1.340× | 1.331–1.339× | 1.289–1.291× | 1.285–1.287× |
| sfs2-trst | 512 | bypass | 1.330–1.335× | 1.328–1.335× | 1.286–1.287× | 1.282–1.284× |
| sfs2-trst | 4096 | cached | 1.319–1.323× | 1.292–1.294× | 1.242–1.242× | 1.212–1.213× |
| sfs2-trst | 4096 | bypass | 1.197–1.201× | 1.263–1.264× | 1.267–1.267× | 1.332–1.334× |
| sfs2-trst | 65536 | cached | 1.018–1.019× | 1.017–1.019× | 1.024–1.025× | 1.021–1.022× |
| sfs2-trst | 65536 | bypass | 1.018–1.019× | 1.017–1.019× | 1.024–1.025× | 1.021–1.022× |

Host, native and full-ROM coverage are distinct. Host Release build/test logs are `artifacts/ffs-span/sfs2-64k-balanced-host-{build,test}.log`. Native logs are `sfs2-64k-balanced-sfs2-{csun,trst}-native.log`. Full-ROM records, trial ranges, payload/ROM/media/probe hashes, memory addresses, timing parameters, CPU timelines, transport commands/bytes, physical fetches and PIO bytes are in `sfs2-64k-balanced-ide/`. Source and CILica input/output hashes are in `sfs2-64k-balanced-native/`. The emulator and compiler basis match the preceding FFS qualification. No ROMs, media, packages or binaries are published.

- `sfs2-csun` SHA-256: `810F6EB655BDA381AEB09529D880F1C79A623A0B3C7DB2BC3DDA2AC6997E46B2`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.
- `sfs2-trst` SHA-256: `77491677925046AA72EE728AB5DE5980184795942EAB5AA9443BE5C886E8FE50`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.

## Profile findings and retained changes

The 64 KiB profile was dominated by native ROM/PIO execution. The largest
handler costs were dispatch, retained-extent validation and transfer setup.
There was no large payload-copy hotspot to replace. CILica inlining had pulled
cursor construction and enumeration work into frames paid by ordinary reads.

The retained changes are in three C# files: `NativeAmigaFileSystem.cs`,
`NativeFileSystemReadCore.cs` and `NativeSfs2InputPath.cs`. Explicit method
boundaries isolate enumeration, first-cursor initialization, no-file reads and
cursor restoration. Aligned and partial-sector readers each keep their own
small frame. Native behavior, packet ABI, persistent traversal state, aliases,
EOF/error prefixes, overlays, transfer limits, metadata and payload read-ahead
are preserved. No public cursor API or application dependency was added.

| Generated frame | Matched control | Final |
|---|---:|---:|
| Dispatch | 164 B | 28 B |
| Retained aligned reader | 152 B | 12 B |
| Separate partial/miss reader | included above | 48 B |

CILica remains enabled throughout. These are C# method-structure changes; no new
CopperSharp68k code-generation change was required. Qualification uses the same
frozen compiler basis as the preceding FFS experiment, including its existing
accessor fixes. No ROM, original handler, generated HUNK or emulator timing was
patched.

## Matched CPU attribution

Other source work changed between the preceding published local SFS2 run and
this qualification. The matched control therefore holds the selected source
snapshot fixed and removes only this optimization's changes. Its source diff
contains exactly the three native files above. Native service-double replay
uses three trials on each CPU, without real ROM PIO or media delays.

| Read | 68000 cycle reduction | 68020 cycle reduction |
|---|---:|---:|
| 64 B | 9.28–9.32% | 5.34–5.35% |
| 512 B | 12.34–12.35% | 8.99% |
| 4 KiB | 5.06% | 3.91% |
| 64 KiB | **10.78–10.79%** | **7.78%** |

Native device-request counts are unchanged at every size. These CPU figures
isolate the retained optimization. Full-ROM comparisons against the preceding
run also include the other source changes, and are not isolated CPU attribution.

## End-to-end result and preservation

All final workload medians exceed the preceding full SFS2 run:

| Read | Session throughput change |
|---|---:|
| 64 B | +1.25–1.80% |
| 512 B | +2.55–3.71% |
| 4 KiB | +0.84–1.45% |
| 64 KiB | +0.06–0.30% |

All 64 KiB configurations remain ahead of the matching original handler:
CSUN by approximately 1.5–4.5%, TRST by 1.7–2.6%. ROM/PIO and media waits dominate,
so the real-ROM throughput gain is much smaller than the handler CPU reduction.
Original-handler session times moved by up to 1.42% between runs; sub-percent
cross-run differences should not be overstated or extrapolated to physical disks.

All **384 matched Copper trial pairs** have identical read and session command
counts, transport bytes, PIO bytes and completions, and matching ROM, probe,
media, payload, timing-profile, memory-class, device, mount-control and partition
identities. This includes **96 pairs each at 64 B and 512 B**. Media fetches remain
separately recorded because drive prefetch activity can vary with command timing.
The frozen harness and emulator-source receipts are unchanged.

## Verification and experiment limits

- Host: final Release solution build succeeded; full solution tests passed
  **1,318**, skipped **3**. Skips remain unavailable host coverage, not passes.
- Native: final CSUN and TRST configurations each passed **204** reader cases.
  Both generated handlers have **zero managed allocation sites**. The rebuilt
  shared integration passed both CopperStart native OS replays. The paired CPU
  replay contains 96 measurements; all request counts match.
- Full ROM: **576 passed**, **0 unavailable**, across all requested ROM/RAM/drive/
  policy/read-size combinations. Every payload validates through native Gayle
  PIO, with CopperHDF absent. No zero-latency or timed-CopperHDF rerun is claimed.

An experimental geometry projection was rejected after HDD regressions.
Two complete provisional matrices exposed a small-read call-boundary regression
and a stock/HDD/TRST large-read regression. Their immutable raw records remain
under `sfs2-64k-final-ide/` and `sfs2-64k-qualified-ide/`. The final retained
layout is `sfs2-64k-balanced-native/`, qualified by `sfs2-64k-balanced-ide/`.
Earlier provisional reports are explicitly marked as such.

The exact selected source and input/output CILica hashes are retained in the
native build directory. The matched control is `sfs2-64k-matched-control-native/`;
its paired native measurements are `sfs2-64k-balanced-manifest/measurements.json`.
Before/control/final scalar profiles are retained under their corresponding
`*-profile/` directories. Source snapshots retain concurrent work; unrelated
source changes were not reverted or attributed to these three files.

Per-configuration three-trial ranges and detailed original comparisons:
`artifacts/ffs-span/sfs2-64k-balanced-analysis/analysis.md`.
Preservation audit: `artifacts/ffs-span/sfs2-64k-balanced-preservation-audit.json`.
Receipt: `artifacts/ffs-span/sfs2-64k-balanced-receipt.json`.
Qualification covers the retained native source snapshot. A subsequent unrelated write-timer factoring change in the live NativeAmigaFileSystem.cs is outside that snapshot; it was preserved. The read-path implementations in NativeFileSystemReadCore.cs and NativeSfs2InputPath.cs match the qualified source exactly, and the retained dispatch extraction is unchanged.
