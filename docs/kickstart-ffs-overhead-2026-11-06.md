# Remaining FFS overhead — 6 November 2026

This pass targets retained FFS traversal and the small contiguous cache-hit path in C#. The exact preceding Copper binaries are rerun beside the new binaries, with a separate A/B against unmodified Amiga handlers.

## Implemented changes

- A new FFS read overload reports whether traversal state changed or was invalidated. The existing overloads remain available. The native adapter skips rewriting the nine cursor words after an unchanged retained-run read. Reads whose destination overlaps the private file object retain the old complete writeback.
- A complete 512-byte-sector cache hit can copy its requested contiguous span directly. Eligible hits avoid refill descriptors and the larger loop frame. Bounds, the first-block metadata-cache priority and non-overlap checks gate this path. Other block sizes, aliases, refills and failed speculative reads retain the preceding implementation.
- Null pending-write buffers and queues are rejected before calling their intersection helpers. Non-null logical overlays retain their existing precedence.
- The 4 KiB read-ahead threshold, cache capacities, MaxTransfer/DMA/TD64 handling, completed-prefix behavior, OFS validation, on-disk formats and packet ABI are preserved. No cursor field, native allocation or application dependency was added. Routine bodies remain C#; this pass adds no compiler rewrite.

## Kickstart A/B results

Median open/read/close KiB/s, three trials per workload. Previous Copper is the exact `read8-frozen` implementation, freshly rerun with the same frozen harness. The originals are FastFileSystem 40.1 and SmartFilesystem 1.279 (SFS2). The [complete original-handler A/B](kickstart-ffs-original-ab-2026-11-06.md) records ranges, read-loop rates, traffic and binary/media/probe hashes.

| Handler | Read size | Previous Copper | Current Copper | Gain | Original | Copper/original |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 26.826 | 27.247 | 1.016× | 35.833 | 0.760× |
| ffs | 512 B | 177.007 | 183.440 | 1.036× | 241.941 | 0.758× |
| ffs | 4,096 B | 714.934 | 777.671 | 1.088× | 1,822.485 | 0.427× |
| ffs | 65,536 B | 6,604.514 | 6,664.380 | 1.009× | 11,201.124 | 0.595× |
| sfs2-csun | 64 B | 21.670 | 21.649 | 0.999× | 23.676 | 0.914× |
| sfs2-csun | 512 B | 147.920 | 149.094 | 1.008× | 163.234 | 0.913× |
| sfs2-csun | 4,096 B | 667.647 | 713.939 | 1.069× | 745.031 | 0.958× |
| sfs2-csun | 65,536 B | 10,017.985 | 9,957.152 | 0.994× | 18,252.276 | 0.546× |
| sfs2-trst | 64 B | 21.642 | 21.591 | 0.998× | 23.680 | 0.912× |
| sfs2-trst | 512 B | 147.824 | 148.737 | 1.006× | 163.255 | 0.911× |
| sfs2-trst | 4,096 B | 667.689 | 712.020 | 1.066× | 744.966 | 0.956× |
| sfs2-trst | 65,536 B | 10,025.451 | 10,049.863 | 1.002× | 18,252.276 | 0.551× |

SFS2 benefits from the shared cache-hit path at 4 KiB; its small-read and 64 KiB medians do not establish a gain for every workload.

An additional FFS comparison rebuilds the preceding filesystem source with the exact current compiler and SDK metadata bytes. Both arms use those identical compiler/SDK inputs, isolating the filesystem source changes from concurrent compiler development. The same frozen Kickstart harness checks all 24 trial streams.

| Read size | Previous C#, current compiler KiB/s | Current C#, current compiler KiB/s | Gain |
|---|---:|---:|---:|
| 64 B | 26.874 | 27.249 | 1.014× |
| 512 B | 177.122 | 183.457 | 1.036× |
| 4,096 B | 715.197 | 777.872 | 1.088× |
| 65,536 B | 6,602.233 | 6,662.730 | 1.009× |

These are virtual Kickstart 3.1 M68020/PAL guest-clock measurements. CopperHDF services reads synchronously without charging physical storage latency. The rates do not establish performance on physical Amiga disks.

## CPU hotspot evidence

Exclusive instruction cycles from separate diagnostic streams and the exact HUNK maps. Before profiles are the retained `read8-frozen` profiles from the same frozen harness. The throughput trials above use normal ExecuteFrame execution without profiling. Interrupt dispatch, STOP waits and marker boundaries remain residual.

| FFS 4 KiB read phase / method | Previous self cycles | Current self cycles | Previous phase share | Current phase share |
|---|---:|---:|---:|---:|
| CopyBlock | 4,483,129 | 4,483,062 | 44.60% | 48.67% |
| Read | 613,426 | 623,974 | 6.10% | 6.77% |
| ReadFfs | 367,109 | 308,175 | 3.65% | 3.35% |
| ReadAheadDataBlocks | 488,503 | 142,544 | 4.86% | 1.55% |
| ReadAheadDataBlocksSlow | 0 | 30,371 | 0.00% | 0.33% |
| ReadPhysicalCached | 277,887 | 44,495 | 2.76% | 0.48% |
| ReadDataBlocks | 252,672 | 229,892 | 2.51% | 2.50% |
| Divide | 165,834 | 116,389 | 1.65% | 1.26% |
| Remainder | 65,209 | 63,602 | 0.65% | 0.69% |
| Intersects | 92,416 | 0 | 0.92% | 0.00% |

The complete 4 KiB read phase changes from 10,052,270 to 9,211,040 guest cycles (8.37% fewer). Copy and non-copy costs remain visible above. The CSVs retain all 12 workloads, 36 phase pairs and every attributed method. These are complete-binary measurements, not isolated per-edit speedups.

Non-copy cycles fall from 5,569,141 to 4,727,978 (15.10% fewer). Copying is essentially unchanged and now accounts for 48.67% of the read phase. The original FFS still leads: it issues 284 device reads in this workload, versus Copper’s 44, with the same 1,038 KiB transferred. The virtual backend charges no physical latency for those extra device requests, while retained read-ahead continues to incur its memory-copy cost.

## Read-ahead and device traffic

Requests and bytes span read-loop entry through close, excluding open/remount. Metadata and speculative reads are included. All twelve workloads retain exactly the previous Copper request counts and bytes.

| Handler | Read size | Requests previous → current | Device KiB previous → current |
|---|---:|---:|---:|
| ffs | 64 B | 2 → 2 | 64.5 → 64.5 |
| ffs | 512 B | 11 → 11 | 259.5 → 259.5 |
| ffs | 4,096 B | 44 → 44 | 1,038.0 → 1,038.0 |
| ffs | 65,536 B | 71 → 71 | 1,278.0 → 1,278.0 |
| sfs2-csun | 64 B | 1 → 1 | 64.0 → 64.0 |
| sfs2-csun | 512 B | 4 → 4 | 256.0 → 256.0 |
| sfs2-csun | 4,096 B | 16 → 16 | 1,024.0 → 1,024.0 |
| sfs2-csun | 65,536 B | 16 → 16 | 1,024.0 → 1,024.0 |

SFS2 TRST has the same traffic as CSUN.

## Provenance and verification

Frozen filesystem and CopperSharp68k source, compiler/SDK metadata inputs, compile commands, HUNKs, maps and allocation reports are retained in `artifacts/ffs9-frozen`. All three profiles use matching SDK/support/compiler metadata inputs. The unchanged frozen harness from `artifacts/read3-kickstart-build` fixes the emulator, probe and media for both A/B invocations. The frozen compiler includes concurrent pre-existing CopperSharp68k development changes since the preceding binaries; the manifests preserve them. Results describe the entire measured implementations and do not attribute those unrelated compiler changes to this pass.

- Host: Release solution build passed; 1,230 tests passed and 3 optional external-media tests skipped. Tests cover the changed-state signal through retained reads, table traversal and invalidation.
- Native: 72 actual-handler packet replays on 68000/68020 and 133 CopperStart copy/buffer/cache instruction replays passed. The packet cases include the private-record alias writeback. All three compatibility reports contain zero managed allocation sites.
- Full-ROM: 72 original-handler A/B trials, 72 previous/current Copper trials, 24 FFS trials with fixed compiler/SDK inputs, 12 current diagnostic streams and 2 SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every read stream was SHA-256 checked; zero writes and unchanged media were asserted.
- Unavailable/not exercised: physical Amigas/disks, replacement-ROM boot and the full hardware/media matrix. Optional missing-media tests remain skips.

Detailed receipts and comparison/profile CSVs are in `artifacts/kickstart-ffs9-original-ab`, `artifacts/kickstart-ffs9-controlled`, `artifacts/kickstart-ffs9-profiles`, `artifacts/ffs9-isolation` and `artifacts/ffs9-validation`. No package or ROM was published.
