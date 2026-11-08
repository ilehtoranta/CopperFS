# Copy hotspots and small FFS reads — 6 November 2026

This pass optimizes the existing C# copy loops through CopperSharp68k and removes full geometry construction from retained FFS reads. The 64-byte FFS regression is fixed. All three native handlers remain allocation-free.

## Implemented changes

- FFS reads use the existing stable BlockBytes scalar while consuming a retained run. Full geometry is constructed when pointer-table traversal is necessary. Header/extension validation, scratch aliasing, errors, completed prefixes and OFS behavior retain their existing paths.
- The existing non-overlapping RAM copy path expresses each longword copy and pointer advance directly in C#. The 32-byte loop is retained. Matching-odd alignment still peels a byte, opposite-parity spans still use byte accesses, and the word/byte tails and overlap/wrap fallbacks retain their existing behavior.
- CopperSharp68k `Compiler/Backend/M68kPeepholeOptimizer.CopyGroups.cs` folds longword accesses followed by an address advance into ordinary MOVE postincrement. Matching postincrement load/store pairs then become memory-to-memory MOVE when their temporary is dead. Each rewrite preserves the C# access order, widths, pointer updates and all CCR bits. There is no added source read or ABI clobber. Stack pointers, base aliases, observable temporaries, instruction-effect overrides, fixups and independent interior entries prevent the corresponding rewrite. The compiler also supports grouped stores for other eligible C# load/store groups.
- Unreferenced IL position labels can be folded and are retargeted to instruction boundaries. Full C# compilation tests check that the final pipeline emits grouped stores and postincrement memory-to-memory copies; instruction replay compares the complete memory-access trace, bytes, registers and flags on 68000, 68020 and 68040, including overlap and negative displacements.
- Routine bodies stay in C#. The generated final handler contains eight ordinary postincrement memory-to-memory longword moves per 32-byte iteration. Read-ahead, logical overlays, caller-owned scratch, 216-byte file records, MaxTransfer/DMA/TD64 handling, packet ABI, formats and journal policy are preserved.

## Kickstart A/B results

Median open/read/close KiB/s, three trials per workload. Previous Copper is the exact `read4-final` binary, freshly rerun with the same frozen harness. The originals are unmodified FastFileSystem 40.1 and SmartFilesystem 1.279. The [complete original-handler A/B](kickstart-copy-original-ab-2026-11-06.md) includes read-loop rates, ranges, device traffic and binary/probe/media hashes.

| Handler | Read size | Previous Copper | Optimized Copper | Gain | Original | Copper/original |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 24.932 | 26.827 | 1.076× | 35.827 | 0.749× |
| ffs | 512 B | 165.349 | 177.006 | 1.070× | 242.005 | 0.731× |
| ffs | 4,096 B | 653.776 | 714.919 | 1.094× | 1,823.075 | 0.392× |
| ffs | 65,536 B | 6,448.728 | 6,604.935 | 1.024× | 11,304.493 | 0.584× |
| sfs2-csun | 64 B | 21.507 | 21.668 | 1.007× | 23.667 | 0.916× |
| sfs2-csun | 512 B | 145.766 | 147.906 | 1.015× | 163.346 | 0.905× |
| sfs2-csun | 4,096 B | 626.914 | 667.813 | 1.065× | 744.134 | 0.897× |
| sfs2-csun | 65,536 B | 9,819.324 | 10,016.189 | 1.020× | 18,303.787 | 0.547× |
| sfs2-trst | 64 B | 21.518 | 21.640 | 1.006× | 23.693 | 0.913× |
| sfs2-trst | 512 B | 145.707 | 147.822 | 1.015× | 163.240 | 0.906× |
| sfs2-trst | 4,096 B | 627.303 | 667.621 | 1.064× | 744.970 | 0.896× |
| sfs2-trst | 65,536 B | 9,895.031 | 10,020.611 | 1.013× | 18,252.276 | 0.549× |

FFS 64-byte reads now reach 26.827 KiB/s. The minimum of all three trials (26.806) exceeds the maximum of the earlier pre-regression Copper trials (25.302). The corresponding pre-regression median was 25.294 KiB/s.

These are virtual Kickstart 3.1 M68020/PAL guest-clock measurements. CopperHDF services reads synchronously without charging physical device seek, rotation or transfer latency. The rates do not establish physical Amiga storage performance. No host wall-time speedup is claimed.

## CPU hotspot evidence

Exclusive instruction cycles from one separate diagnostic stream per size, using the same frozen Kickstart harness and actual HUNK maps. The previous profiles are the retained `read4-final` profiles from that harness. Normal ExecuteFrame A/B measurements above are uninstrumented. Interrupt dispatch, STOP waits and marker boundaries remain residual.

| Workload / method | Previous self cycles | Optimized self cycles | Previous phase share | Optimized phase share |
|---|---:|---:|---:|---:|
| ffs, 64 B read: get_Geometry | 943,835 | 3,680 | 5.2% | 0.0% |
| ffs, 64 B read: CopyBlock | 1,438,207 | 1,217,276 | 7.9% | 7.2% |
| ffs, 4,096 B read: CopyBlock | 5,169,423 | 4,483,129 | 47.0% | 44.6% |
| sfs2-csun, 64 B read: CopyBlock | 1,456,114 | 1,243,441 | 7.0% | 6.0% |
| sfs2-csun, 4,096 B read: CopyBlock | 5,118,826 | 4,439,284 | 45.3% | 41.9% |
| sfs2-csun, 65,536 B open: CopyBlock | 53,682 | 46,334 | 19.7% | 18.5% |

The CSVs retain all 12 workload comparisons, 36 paired phases and every attributed method. These are complete-binary measurements; they do not isolate a speedup for each source edit.

## Read-ahead and device traffic

Requests and bytes span read-loop entry through close, excluding open/remount. Metadata and speculative reads are included.

| Handler | Read size | Requests previous → optimized | Device KiB previous → optimized |
|---|---:|---:|---:|
| ffs | 64 B | 2 → 2 | 64 → 64 |
| ffs | 512 B | 11 → 11 | 260 → 260 |
| ffs | 4,096 B | 44 → 44 | 1,038 → 1,038 |
| ffs | 65,536 B | 71 → 71 | 1,278 → 1,278 |
| sfs2-csun | 64 B | 1 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 4 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 16 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 16 → 16 | 1,024 → 1,024 |

SFS2 TRST retains the same read-ahead request counts as CSUN.

## Provenance and verification

Frozen source, SDK/compiler inputs, compile commands, HUNKs, maps and allocation reports are in `artifacts/read8-frozen`. All three profiles use matching SDK/support/compiler metadata inputs. The benchmark uses the unchanged frozen Kickstart test assembly from `artifacts/read3-kickstart-build`; both A/B invocations record identical emulator and probe hashes. Concurrent unrelated source changes were preserved. Source snapshots and binary hashes define the measured implementation; no per-edit attribution is inferred. A 128-byte unroll was measured and rejected because it regressed throughput; it is absent from the final source.

- Host: Release solution build passed; 1,229 tests passed, 3 optional external-media tests skipped. The filesystem source matches the frozen handler snapshot; this includes the Amiga host checks.
- Compiler: 96 final frozen-compiler copy, transport, wrapper and bulk-copy lowering tests passed. This includes the compiled C# emission regression and exact-access replay on three CPU models.
- Native: 68 actual-handler packet replays and 133 CopperStart copy/buffer/cache instruction replays passed. All three HUNK compatibility reports contain zero managed allocation sites.
- Full-ROM: 72 original-handler A/B trials, 72 previous/optimized Copper trials, 12 final profile streams and 2 SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every benchmark stream was SHA-256 checked; zero writes and unchanged media were asserted.
- Unavailable/not exercised: physical Amigas/disks, replacement-ROM boot and the full hardware/media matrix. Missing optional media tests remain skips.

Detailed receipts are in `artifacts/kickstart-read8-original-ab`, `artifacts/kickstart-read8-controlled`, `artifacts/kickstart-read8-profiles` and `artifacts/read8-validation`. No ROM, media, reference handler, credential or build artifact is committed; no package or ROM was published.
