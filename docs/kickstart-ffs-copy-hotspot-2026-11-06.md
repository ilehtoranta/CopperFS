# FFS 4 KiB copy hotspot — 6 November 2026

This pass specializes the C# copy used for complete, aligned read-ahead cache hits. The compiler, SDK metadata, emulator, media and probe are held fixed against the preceding implementation.

## Implemented change

The cache-hit path already validates a non-overlapping span inside the 512-byte-sector read-ahead window. For even source and destination addresses, it now calls a small private C# copy kernel. Its input is a positive multiple of 512 bytes, bounded by the existing 4 KiB policy. It copies the same eight longwords in the same order per 32-byte iteration, using a chunk countdown rather than repeating the general copy entry checks and byte-count loop condition.

CopperSharp68k’s existing pointer-advance and copy folding emits eight ordinary postincrement memory-to-memory moves, followed by SUBQ/BNE. The final helper is 30 bytes with a four-byte callee-save area and no local stack frame. There is no handwritten handler assembly and no new compiler rewrite in this pass: the existing lowering already emits the desired kernel.

Same-address spans still skip copying. Odd addresses, overlapping private buffers, refills, other sector sizes and unsupported spans retain their existing paths. The generic CopyBlock implementation, its alignment/tail/overlap handling, the read-ahead threshold and capacities, logical-overlay precedence, cursor validity, packet ABI, MaxTransfer/DMA/TD64 handling, journal policy and on-disk formats are unchanged. No buffer, handle field or allocation was added.

## Kickstart A/B results

Median open/read/close KiB/s, three trials per workload. Previous is the exact `ffs9-frozen` binary, freshly rerun with the same frozen harness. Originals are unmodified FastFileSystem 40.1 and SmartFilesystem 1.279 (SFS2). The [complete original-handler A/B](kickstart-ffs-copy-original-ab-2026-11-06.md) retains read-loop rates, ranges, device traffic and binary/probe/media hashes.

| Handler | Read size | Previous Copper | Current Copper | Gain | Original | Copper/original |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 27.247 | 27.265 | 1.001× | 35.827 | 0.761× |
| ffs | 512 B | 183.424 | 183.334 | 1.000× | 241.926 | 0.758× |
| ffs | 4,096 B | 777.417 | 794.283 | 1.022× | 1,823.262 | 0.436× |
| ffs | 65,536 B | 6,665.420 | 6,632.858 | 0.995× | 11,205.962 | 0.592× |
| sfs2-csun | 64 B | 21.649 | 21.651 | 1.000× | 23.683 | 0.914× |
| sfs2-csun | 512 B | 149.094 | 149.154 | 1.000× | 163.274 | 0.914× |
| sfs2-csun | 4,096 B | 713.939 | 727.166 | 1.019× | 744.953 | 0.976× |
| sfs2-csun | 65,536 B | 9,957.152 | 10,053.897 | 1.010× | 18,252.276 | 0.551× |
| sfs2-trst | 64 B | 21.591 | 21.557 | 0.998× | 23.680 | 0.910× |
| sfs2-trst | 512 B | 148.741 | 148.735 | 1.000× | 163.297 | 0.911× |
| sfs2-trst | 4,096 B | 712.167 | 725.581 | 1.019× | 745.505 | 0.973× |
| sfs2-trst | 65,536 B | 10,032.513 | 9,936.177 | 0.990× | 18,308.862 | 0.543× |

The 64-byte, 512-byte and large direct-read paths receive no new copy kernel; their small trial differences do not establish a gain. The original FFS remains substantially faster. These are virtual Kickstart 3.1 M68020/PAL guest-clock rates: CopperHDF services reads synchronously without charging physical storage latency. They do not establish physical Amiga disk throughput.

## Profiled copy cost

Exclusive instruction cycles use separate diagnostic streams and the actual HUNK maps. The before profiles come from the preceding `ffs9-frozen` implementation under the same frozen harness. Normal throughput trials use uninstrumented ExecuteFrame execution. Interrupts, STOP waits and marker boundaries remain residual. CopyBlock and the new CopyReadAhead must be added when comparing total copy costs.

| FFS 4 KiB read phase | Previous cycles | Current cycles |
|---|---:|---:|
| Complete read phase | 9,211,040 | 9,020,150 |
| CopyBlock | 4,483,062 | 346,532 |
| CopyReadAhead | 0 | 3,950,840 |
| Combined copy | 4,483,062 | 4,297,372 |
| Cache-hit wrapper | 142,544 | 148,778 |

Combined copy cycles fall 4.14%; complete read-phase cycles fall 2.07%. Copying still accounts for 47.64% of the current read phase. The CSVs retain all twelve workload comparisons, all 36 phase pairs and every attributed method.

## Alternatives measured

Exploratory FFS Kickstart trials used the same frozen compiler/SDK and harness. The 32-byte countdown kernel was retained. A 128-byte countdown unroll did not improve on it; calling Exec.CopyMemQuick for eligible cached spans regressed throughput. Neither alternative is in the final source. Grouped C# loads/stores and a narrow counter were also slower in instruction-level probes. No speculative source reads or MOVEM-load overreads were introduced.

| Exploratory variant | Before 4 KiB KiB/s | Candidate 4 KiB KiB/s |
|---|---:|---:|
| count | 777.748 | 794.460 |
| count128 | 777.420 | 793.219 |
| exec | 777.893 | 735.344 |

## Preserved read-ahead

All twelve workloads retain exactly the previous Copper device-request counts and bytes. Counts span read-loop entry through close, excluding open/remount; metadata and speculative reads are included.

| Handler | Read size | Requests before → after | Device KiB before → after |
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

Frozen filesystem source, compiler source/binaries, SDK metadata inputs, compile commands, native HUNKs, maps and allocation reports are in `artifacts/copy10-final`. The compiler and all SDK/support/compiler metadata inputs match `ffs9-frozen` byte-for-byte; compiler-source manifests are identical. This deliberately preserves unrelated ongoing CopperSharp68k development in the live checkout without mixing it into the measured change. Only NativeAmigaFileSystem.cs differs between the frozen filesystem snapshots. The unchanged frozen harness is `artifacts/read3-kickstart-build`; both A/B runs use identical emulator/probe/media hashes.

- Host: Release solution build and tests passed: 1,230 unique tests, 3 optional-media skips. The 18 Amiga host tests were rechecked after selecting the final kernel; unchanged portable-core test receipts are retained from the full solution run.
- Native: 76 actual-handler packet replays on 68000/68020 and 133 CopperStart copy/buffer/cache instruction replays passed. The added packet cases check every cache-hit sector length from 1–4 KiB, returned bytes, caller guards, odd destinations and unchanged device reads. All three HUNK reports contain zero managed allocation sites.
- Full-ROM: 72 original-handler A/B trials, 72 previous/current Copper trials, 12 final diagnostic streams and 2 SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every benchmark stream is SHA-256 checked; zero writes and unchanged media are asserted. The exploratory alternatives add 72 separately recorded trials.
- Unavailable/not exercised: physical Amigas/disks, replacement-ROM boot and the full hardware/media matrix. Missing optional media tests remain skips.

Detailed receipts and CSVs are in `artifacts/kickstart-copy10-original-ab`, `artifacts/kickstart-copy10-controlled`, `artifacts/kickstart-copy10-profiles`, `artifacts/copy10-validation` and `artifacts/copy10-candidates`. No ROM, media or build artifact was committed; no package or ROM was published.
