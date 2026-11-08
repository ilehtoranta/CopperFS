# Sequential-read optimization follow-up — 6 November 2026

This pass reduces FFS metadata traffic, batches copies from retained data windows, and lowers checksum and 64-bit division costs. Both original handlers remain faster. The tables below keep the remaining gap and any small-read regression visible.

## C# changes

- FFS cursors retain the validated next pointer-table key. Advancing does not reload the preceding table; entering the successor still checks its checksum, type, own key, parent and range. Backward seeks reload and validate the header. Shared scratch may be overwritten between packets.
- The native adapter stores that scalar in the FFS-private word at byte 96 of the existing 216-byte file record. SFS2 keeps its separate layout. The high size/position words at 88–95 and common invalidation word at 56 are preserved.
- Native FFS metadata cache misses fetch the requested block without displacing the data read-ahead window. Metadata already in that window is reused before issuing I/O. Logical redo, dirty, queued-write and RAM views retain precedence.
- Aligned cached reads copy each available contiguous span once. The common single-block path has a separate small C# frame. Aliased private buffers retain sector-at-a-time behavior, and a failed speculative refill retries only the requested suffix. The existing 4 KiB read-ahead cutoff, MaxTransfer/DMA/TD64 handling and local C# copy routine remain.
- FFS and SFS read-only checksums process eight words per loop, followed by every remaining word. The entire block and the original checksum predicates are retained; no validation is sampled or omitted.
- CopperSharp68k `Runtime.Managed/Integer64Arithmetic.cs` finds a power-of-two divisor shift with five bounded tests instead of a variable-length shift loop. The general restoring division and divide-by-zero, signed and overflow behavior remain.
- Routine changes are C#. No handwritten instructions, generated-byte patches, new Exec-copy dependency, managed allocation, handle growth, on-disk change, packet ABI change or journal-policy change was introduced.

## Fresh controlled throughput

Median open/read/close KiB/s, three trials per workload. Previous Copper is the exact retained `profiled-read-v2` HUNK, rerun with the same frozen test assembly, emulator assemblies, probe, ROM and media as the final build. Original is a separate fresh A/B using unmodified FastFileSystem 40.1 and SmartFilesystem 1.279. All variants use the same 64 B, 512 B, 4 KiB and 64 KiB consecutive-read workloads and remount before each trial. The [full original-handler A/B](kickstart-followup-original-ab-2026-11-06.md) includes read-loop throughput, ranges, device traffic, configuration and binary hashes.

| Handler | Read size | Previous Copper | Final Copper | Gain | Original | Copper/original |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 25.294 | 24.934 | 0.986× | 35.827 | 0.696× |
| ffs | 512 B | 158.075 | 165.381 | 1.046× | 242.027 | 0.683× |
| ffs | 4,096 B | 472.177 | 653.756 | 1.385× | 1,823.583 | 0.359× |
| ffs | 65,536 B | 4,247.704 | 6,446.554 | 1.518× | 11,206.308 | 0.575× |
| sfs2-csun | 64 B | 21.025 | 21.510 | 1.023× | 23.682 | 0.908× |
| sfs2-csun | 512 B | 136.581 | 145.780 | 1.067× | 163.281 | 0.893× |
| sfs2-csun | 4,096 B | 468.650 | 626.872 | 1.338× | 744.976 | 0.841× |
| sfs2-csun | 65,536 B | 7,147.817 | 9,881.436 | 1.382× | 18,252.276 | 0.541× |
| sfs2-trst | 64 B | 21.025 | 21.513 | 1.023× | 23.668 | 0.909× |
| sfs2-trst | 512 B | 136.638 | 145.712 | 1.066× | 163.226 | 0.893× |
| sfs2-trst | 4,096 B | 469.271 | 627.348 | 1.337× | 744.413 | 0.843× |
| sfs2-trst | 65,536 B | 7,185.574 | 9,874.988 | 1.374× | 18,307.016 | 0.539× |

ffs at 64 B remains 1.4% slower than the preceding Copper build. This tradeoff is retained explicitly; improved metadata traffic does not imply faster execution for every packet size.

CopperHDF services requests synchronously without charging physical seek, rotation or transfer latency to the guest clock. These are virtual Kickstart 3.1 M68020/PAL results, not physical Amiga storage rates. Host elapsed time is not the throughput metric.

## CPU profile evidence

One separate diagnostic trial per size and binary, split into open/read/close. Exclusive instruction cycles come from the actual standalone HUNK maps. Interrupt dispatch, STOP waits and boundary markers remain residual; the normal ExecuteFrame A/B runs above are uninstrumented. The previous binaries were reprofiled using this same frozen diagnostic harness.

| Workload / method | Previous self cycles | Final self cycles | Previous phase share | Final phase share |
|---|---:|---:|---:|---:|
| ffs, 65,536 B read: Checksum | 201,332 | 66,700 | 12.8% | 6.7% |
| ffs, 65,536 B read: ReadPhysicalCached | 73,444 | 27,956 | 4.7% | 2.8% |
| ffs, 4,096 B read: ReadAheadDataBlocks | 831,463 | 490,223 | 5.5% | 4.5% |
| ffs, 4,096 B read: CopyBlock | 6,048,106 | 5,169,423 | 39.7% | 47.0% |
| sfs2-csun, 65,536 B open: Checksum | 240,376 | 75,031 | 56.3% | 27.6% |
| sfs2-csun, 65,536 B read: Checksum | 141,372 | 44,181 | 25.0% | 9.9% |
| sfs2-csun, 512 B read: DivRemUnsigned | 781,611 | 693,658 | 6.1% | 5.7% |

The CSV receipts retain all 36 phase pairs and every attributed method. Changes in exclusive cycles do not establish isolated per-change speedups: the accepted measurements identify complete handler binaries.

Remaining CPU costs in the final build:

| Workload | Method | Self cycles | Phase share |
|---|---|---:|---:|
| ffs, 64 B read | ReadFfs | 1,467,386 | 8.1% |
| ffs, 64 B read | get_Geometry | 943,835 | 5.2% |
| ffs, 4,096 B read | CopyBlock | 5,169,423 | 47.0% |
| sfs2-csun, 65,536 B open | Checksum | 75,031 | 27.6% |
| sfs2-csun, 65,536 B open | CopyBlock | 53,682 | 19.7% |

## Device traffic

Read-loop entry through close, excluding open/remount. FFS metadata and speculative bytes are included.

| Handler | Read size | Requests previous → final | Device KiB previous → final |
|---|---:|---:|---:|
| ffs | 64 B | 4 → 2 | 256 → 64 |
| ffs | 512 B | 18 → 11 | 1,152 → 260 |
| ffs | 4,096 B | 71 → 44 | 4,544 → 1,038 |
| ffs | 65,536 B | 71 → 71 | 3,056 → 1,278 |
| sfs2-csun | 64 B | 1 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 4 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 16 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 16 → 16 | 1,024 → 1,024 |

SFS2 TRST retains the same request counts as CSUN. Small aligned reads retain read-ahead. The FFS metadata optimization reduces traffic and preserves already-prefetched data rather than replacing its window.

## Provenance and coverage

Final source snapshots, managed inputs, HUNKs, maps, compatibility reports, compiler hashes, metadata hashes and canonical compile arguments are retained in `artifacts/read4-final`. The compiler is the frozen C# source/build from `artifacts/read3-frozen`; its source manifest was checked unchanged. All three final profiles use matching SDK/support/compiler metadata inputs. Concurrent unrelated writer/backend working-tree changes were preserved. Later writer edits are outside these frozen native binaries. No published package or ROM was changed, and no reference handler, ROM, media or build output is committed.

- Host: Release solution build passed; 1,203 tests passed, 3 optional external-media tests skipped.
- CopperSharp68k: 7 focused arithmetic checks passed, including host boundary/random/exception cases and actual compiled full-width division on four CPU models.
- Native: 68 final-HUNK packet replays passed across FFS/SFS2, CSUN/TRST and M68000/M68020; 133 CopperStart buffer/cache/copy replays passed using the final read-method overlay. All three final handlers are native-compatible with zero managed allocation sites.
- Full-ROM: 72 final original-handler A/B trials and 72 controlled previous/final Copper trials passed; 12 final and 12 baseline diagnostic streams passed; 2 SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every sequential stream was SHA-256 checked, with zero writes and unchanged read-only media asserted.
- New regressions cover every checksum word, cached-successor validation after scratch reuse, backward-seek traversal counts and metadata reuse from the data read-ahead window, including the high private file words.
- Physical Amigas/disks, replacement-ROM boot and the full media/hardware matrix were not exercised. Optional missing-media tests remain skips.

Receipts: `artifacts/kickstart-read4-original-ab`, `artifacts/kickstart-read4-controlled`, `artifacts/kickstart-read4-profiles`, `artifacts/kickstart-read3-previous-profiles` and `artifacts/read4-validation`. The benchmark driver accepts `-TestAssembly` to reuse a frozen harness and filters only the sequential-throughput suite. The summarizer counts actual TRX outcomes by suite.

```powershell
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py artifacts/kickstart-read4-original-ab docs/kickstart-followup-original-ab-2026-11-06.md --date "6 November 2026"
```
