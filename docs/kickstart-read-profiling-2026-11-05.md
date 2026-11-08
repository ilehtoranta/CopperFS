# Kickstart read CPU profiling and C# optimizations — 5 November 2026

The native Kickstart profiles identified full geometry construction, general 64-bit division, cache copying and read-packet setup as substantial costs. The resulting C# changes improve all measured workloads. The original handlers remain faster in this configuration.

## Profile evidence

Exclusive instruction cycles from actual standalone Copper HUNKs under Kickstart 3.1, M68020 PAL, 512 KiB Chip / 2 MiB Fast RAM. One diagnostic trial per read size and binary, split at the existing guest markers into open, read and close. The opt-in C# scalar driver follows CopperStart's MIT-licensed SfsCpuProfile and its interrupt/hardware ordering. Maps locate the relocated code; profile reads inspect RAM without guest bus accesses. Interrupt dispatch, STOP waits and boundary markers are residual rather than charged to a guessed function. Normal ExecuteFrame acceptance measurements are reported separately.

| Workload / hotspot | Before self cycles | After self cycles | Before phase share | After phase share |
|---|---:|---:|---:|---:|
| sfs2-csun, 65,536 B open: get_Geometry | 1,692,471 | 1,840 | 76.0% | 0.4% |
| sfs2-csun, 64 B read: DivRemUnsigned | 36,047,977 | 1,563,377 | 56.1% | 7.4% |
| sfs2-csun, 65,536 B read: get_Geometry | 1,025,233 | 920 | 46.7% | 0.2% |
| sfs2-csun, 65,536 B read: DivRemUnsigned | 554,194 | 24,462 | 25.2% | 4.3% |
| ffs, 4,096 B read: CopyBlock | 11,746,089 | 6,047,974 | 38.5% | 39.7% |
| ffs, 64 B read: CopyBlock | 3,889,655 | 1,452,285 | 14.9% | 8.1% |

CopyBlock's percentage can remain high even when its absolute cost falls: other work was also removed. The CSVs retain every phase and method, including outside-handler time. The initial FFS 64-byte read phase spent about 20% inside the general DispatchPacket decoder; normal FFS reads now use a separate small C# decoder.

## Implemented changes

- CopperSFS read-only traversal uses the platform's existing stable BlockCount/BlockBytes/ReservedBlocks scalars. Checksum length is read once; checksum, own-block, format, backlink, range and clean-volume checks remain in place.
- CopperSharp68k Runtime.Managed/Integer64Arithmetic.cs recognizes power-of-two divisors and computes exact full-width quotients/remainders with shifts and masks. The general restoring division remains for other divisors; zero-divisor, sign and overflow behavior is preserved. This is a reusable C# runtime improvement, not a filesystem-only code patch.
- Ordinary standalone FFS Read packets avoid the general decoder's large frame, duplicate lock lookup and root-key setup. Inhibited/recovery states retain the old route; packed integration keeps its existing media checks.
- Small aligned FFS/SFS2 reads retain physical read-ahead without copying each data sector into the metadata LRU. Track-cache hits no longer construct a full device descriptor. Overlay/dirty/queued/RAM precedence is still checked before entering the physical data path, and failed speculative reads retain the requested-suffix fallback.
- Partial reads can consume a retained image immediately rather than first copying a full sector to scratch. Aliased destinations fall back to scratch; no borrowed pointer survives another block access.
- No allocation, handle/state growth, on-disk format, packet ABI, journal policy or standalone dependency was added. All routine changes are C#. There are no handwritten instructions, emitted-byte patches or filesystem-specific assembler kernels. The original local C# CopyBlock implementation remains; no new Exec-copy dependency is required.

## Uninstrumented throughput

Median open/read/close KiB/s, three trials. Same read sizes/totals, bit-identical media, caller buffers, mount geometry and guest probe HUNKs as the previous A/B. The original columns below are newly rerun reference handlers; the previous Copper column is the retained pre-profile optimization result. Full read-loop results, ranges and binary hashes are in the [new original-handler A/B report](kickstart-profiled-original-ab-2026-11-05.md).

| Handler | Read size | Previous Copper | Profiled Copper | Copper gain | Original |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 17.285 | 25.294 | 1.46× | 35.826 |
| ffs | 512 B | 103.527 | 158.076 | 1.53× | 242.032 |
| ffs | 4,096 B | 236.855 | 472.179 | 1.99× | 1,822.869 |
| ffs | 65,536 B | 3,633.347 | 4,247.580 | 1.17× | 11,231.432 |
| sfs2-csun | 64 B | 6.825 | 21.025 | 3.08× | 23.682 |
| sfs2-csun | 512 B | 46.558 | 136.582 | 2.93× | 163.291 |
| sfs2-csun | 4,096 B | 172.938 | 468.653 | 2.71× | 744.965 |
| sfs2-csun | 65,536 B | 1,636.019 | 7,146.200 | 4.37× | 18,252.276 |
| sfs2-trst | 64 B | 6.850 | 21.024 | 3.07× | 23.666 |
| sfs2-trst | 512 B | 46.809 | 136.653 | 2.92× | 163.271 |
| sfs2-trst | 4,096 B | 173.805 | 469.301 | 2.70× | 745.564 |
| sfs2-trst | 65,536 B | 1,730.588 | 7,185.859 | 4.15× | 18,306.555 |

The CopperHDF backend services sectors synchronously without charging physical seek, rotation or device transfer latency to guest time. This is virtual Kickstart throughput, not physical Amiga storage performance. No host wall-time speedup is claimed; overlapping host jobs do not define the reported guest-clock KiB/s.

## Provenance and reproduction

Baseline profiles use the exact Copper HUNKs from the preceding A/B. Final HUNKs, maps, managed inputs, source hashes, compiler hashes and compile arguments are frozen in artifacts/profiled-read-v2. The compiler/runtime assembly is the same for all three final builds. Existing unrelated writer and compiler-backend working-tree changes were retained, not reverted. Whole-handler gains are measurements for these identified binaries; no isolated per-change speedup is claimed.

Build FFS, SFS2 CSUN and SFS2 TRST through build/Build-Handler.ps1 with the same compiler and canonical flags. Run the original-handler benchmark script against a manifest pointing at those fresh HUNKs. For diagnostics, set COPPERFS_KICKSTART_PROFILE=1, select DisplayName~after and use a separate fresh artifact directory. The benchmark script clears this flag and the smoke flag for acceptance runs.

```powershell
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py artifacts/kickstart-profiled-v2-ab docs/kickstart-profiled-original-ab-2026-11-05.md
& (uv python find --offline) tools/Summarize-ReadHotspots.py artifacts/kickstart-hotspots-before artifacts/kickstart-hotspots-v2 artifacts/kickstart-original-ab artifacts/kickstart-profiled-v2-ab artifacts/profiled-read-validation docs/kickstart-read-profiling-2026-11-05.md
```

## Coverage

- Host: final Release solution build passed; 1,177 host tests passed; 3 optional external-media tests skipped.
- Compiler/runtime: 7 focused checks passed, covering host boundary/random/exception cases and compiled full-width arithmetic on four CPU models.
- Native: 64 actual-handler packet replays passed across CSUN/TRST and M68000/M68020; 133 CopperStart buffer/cache/copy instruction replays passed using the updated read/cache C# overlay, without editing CopperStart's tracked adapter. All three final HUNKs are native-compatible with zero managed allocation sites.
- Full-ROM: all 72 uninstrumented original-handler A/B trials, 12 final diagnostic workloads and 2 SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every sequential stream was SHA-256 checked; benchmark media remained unchanged.
- Not exercised: physical disks/Amigas, replacement-ROM boot and the full hardware/media matrix. Missing optional media tests are skips, not passes.
