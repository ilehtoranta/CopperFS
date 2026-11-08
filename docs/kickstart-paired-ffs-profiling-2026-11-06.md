# Original FFS versus CopperFFS: paired profiles — 6 November 2026

Paired Kickstart profiles identify the main 4 KiB gap: the original handler transfers payload directly into the caller buffer, while Copper preserves its read-ahead window and copies cached payload to the caller. This pass reduces the remaining traversal/call overhead in C# and fixes small-mask branch lowering in CopperSharp68k.

## What the original does differently

The original is unmodified FastFileSystem 40.1. Its code has no symbols, so the profiler attributes its code hunk as a module and retains all hot PC offsets/opcodes. It handles the original two-hunk layout (code plus empty data), masks relocation bytes when locating loaded code, and rejects unsupported nonempty secondary hunks. Five synthetic tests validate code-hunk location and relocation bounds. No symbols or function names are invented for the original.

Device traces capture the IORequest passed in A1 at CopperHDF’s BeginIO gateway. The traces record command, byte offset, length and data address without changing guest execution. The same caller buffer, probe, media and emulator are used on both sides. Recognized MOVE writes are a diagnostic count, not a complete emulated bus trace.

| 4 KiB read phase, 1 MiB file | Original FFS | Previous Copper | Current Copper |
|---|---:|---:|---:|
| Guest cycles | 3,915,560 | 9,020,170 | 8,508,670 |
| Handler self cycles | 1,788,284 | 7,033,763 | 6,543,731 |
| Kickstart self cycles | 1,993,583 | 1,896,585 | 1,875,676 |
| Other self cycles | 131,903 | 85,668 | 85,337 |
| Payload device requests | 256 | 16 | 16 |
| Payload bytes read into caller | 1,048,576 | 0 | 0 |
| Payload bytes read into other buffers | 0 | 1,048,576 | 1,048,576 |
| Recognized MOVE bytes written to caller | 0 | 1,048,576 | 1,048,576 |

Previous Copper’s named copy routines cost 4,297,582 cycles, equivalent to 84.2% of the 5,104,610-cycle total gap. The original’s 256 payload transfers cover exactly 1 MiB in the caller buffer; Copper’s 16 payload transfers cover exactly 1 MiB in its cache. Thus the original avoids the cache-to-caller payload copy used by Copper. Absolute Kickstart costs are similar; the difference is not primarily a slower DOS/Exec implementation on Copper’s side.

CopperHDF completes sector transfers synchronously without charging physical seek, rotation or transfer latency to the guest clock. Extra device requests are inexpensive in this model, while CPU copies still consume emulated cycles. This explains why batching fewer requests does not imply higher throughput here. These findings do not establish physical Amiga disk performance.

## Implemented changes

- The native FFS adapter consumes a validated retained 512-byte-sector run directly for aligned packets. It validates the native record identity/type, file size, requested range, mapping and disjoint scratch/private-record ranges. Successful reads avoid constructing a temporary cursor and entering the general table/division frame. A short or failed device read invalidates the cursor and returns the same completed prefix/error.
- The packet dispatcher selects this route only for aligned-size 512-byte–4 KiB requests. Small/large packets retain the existing reader directly. OFS, unaligned data, scratch/private-record aliases, EOF tails, table crossings and unsupported sector sizes retain the general paths. Native records, high size/position words, ABI, formats and journal policy are unchanged.
- CopperSharp68k narrows an AND used only for flags when its mask is at most 0x7FFF and its data result is dead. It also folds an adjacent materialized constant when both scalar results are dead. N and Z are identical at both widths, V/C are cleared and X is preserved. Live values, sign-sensitive masks, independent entries, fixups and custom instruction effects prevent the corresponding rewrite. The rule is under NarrowOperations; it adds no memory access or register clobber.
- Read-ahead, cache sizes, logical overlays, MaxTransfer/DMA/TD64 rules and the C# copy kernel are preserved. No read-path allocation, buffer or record field was added.

## Kickstart A/B throughput

Median open/read/close KiB/s, three trials per workload. Previous Copper is the exact `copy10-final` binary, freshly rerun. Originals are unmodified FastFileSystem 40.1 and SmartFilesystem 1.279. The [complete original-handler A/B](kickstart-paired-ffs-original-ab-2026-11-06.md) records read-loop rates, ranges, traffic and hashes.

| Handler | Read size | Previous Copper | Current Copper | Gain | Original | Copper/original |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 27.264 | 27.145 | 0.996× | 35.839 | 0.757× |
| ffs | 512 B | 183.330 | 209.877 | 1.145× | 241.851 | 0.868× |
| ffs | 4,096 B | 794.300 | 842.991 | 1.061× | 1,822.846 | 0.462× |
| ffs | 65,536 B | 6,625.296 | 6,667.377 | 1.006× | 11,231.953 | 0.594× |
| sfs2-csun | 64 B | 21.650 | 21.657 | 1.000× | 23.683 | 0.914× |
| sfs2-csun | 512 B | 149.155 | 149.263 | 1.001× | 163.269 | 0.914× |
| sfs2-csun | 4,096 B | 727.233 | 727.538 | 1.000× | 744.970 | 0.977× |
| sfs2-csun | 65,536 B | 10,052.923 | 10,082.224 | 1.003× | 18,252.276 | 0.552× |
| sfs2-trst | 64 B | 21.558 | 21.718 | 1.007× | 23.677 | 0.917× |
| sfs2-trst | 512 B | 148.696 | 149.633 | 1.006× | 163.260 | 0.917× |
| sfs2-trst | 4,096 B | 725.541 | 727.750 | 1.003× | 744.948 | 0.977× |
| sfs2-trst | 65,536 B | 9,951.014 | 10,080.965 | 1.013× | 18,252.276 | 0.552× |

A separate FFS comparison rebuilds the previous source with the exact final compiler and SDK metadata bytes. This controls compiler/SDK changes while retaining the same frozen guest harness.

| Read size | Previous source, final compiler | Current source, final compiler | Gain |
|---|---:|---:|---:|
| 64 B | 27.177 | 27.143 | 0.999× |
| 512 B | 183.119 | 209.931 | 1.146× |
| 4,096 B | 794.323 | 842.775 | 1.061× |
| 65,536 B | 6,645.115 | 6,670.194 | 1.004× |

The paired 4 KiB diagnostic phase changes from 9,020,170 to 8,508,670 cycles (5.67% fewer). Named copy costs change from 4,297,582 to 4,297,100; the retained read-ahead payload copy remains the largest cost. The original remains faster. The 64-byte FFS median is 0.4% lower than the previous binary (0.1% lower in the fixed-compiler comparison); large-read ranges overlap. No per-edit speedup is inferred from whole-binary timings.

All twelve workloads preserve the previous Copper device-request counts and bytes. FFS request counts remain 2 / 11 / 44 / 71 for 64 B / 512 B / 4 KiB / 64 KiB. SFS2 retains 1 / 4 / 16 / 16. The complete A/B retains trial ranges for small/large reads; small median differences do not establish a gain.

## Verification and provenance

Filesystem source, compiler source/binaries, compile commands, metadata inputs, HUNKs, maps and allocation reports are frozen in `artifacts/ab11-final`. All three profiles use identical SDK/support/compiler metadata inputs. The enhanced profiling harness links the immutable emulator/compiler/SDK binaries from `artifacts/read3-kickstart-build`. Its rebuilt probe differs in image hash from earlier reports, so both acceptance arms and all new paired profiles use this same new probe. Media and caller addresses are unchanged.

Concurrent FFS writer and CopperSharp68k development is preserved in the source snapshots. Results qualify the frozen builds, not later unrelated writer edits in the working tree. The same-compiler comparison controls toolchain changes, but these are complete-source/build measurements.

- Host: Release solution builds passed; 1,250 unique tests passed and 3 optional-media tests skipped. The final 18 Amiga host checks were repeated after routing was finalized.
- Compiler: 36 checks passed against the frozen compiler, including exact register/flag replay on 68000/68020/68040 and compiled C# mask execution. Five profiler parser checks passed.
- Native: 80 actual-handler packet replays and 133 CopperStart copy/buffer/cache instruction replays passed. Aliased private-record destinations include 64-byte and aligned 512-byte reads. All three HUNK reports contain zero managed allocation sites.
- Full-ROM: 72 original-handler A/B trials, 72 previous/current Copper trials, 24 fixed-compiler FFS trials, 16 paired diagnostic streams (before/after passes) and 2 SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every benchmark payload is SHA-256 checked, writes are prohibited and media is rechecked unchanged.
- Unavailable/not exercised: physical Amigas/disks, replacement-ROM boot and the full hardware/media matrix. Missing optional media tests remain skips.

Receipts are in `artifacts/kickstart-ab11-original-ab`, `artifacts/kickstart-ab11-controlled`, `artifacts/ab11-before-profiles`, `artifacts/kickstart-ab11-profiles`, `artifacts/ab11-isolation` and `artifacts/ab11-validation`. No ROM/media/build artifact was committed; no package or ROM was published.
