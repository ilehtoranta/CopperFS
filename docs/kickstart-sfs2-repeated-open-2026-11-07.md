# SFS2 metadata verification across opens

The unit of reuse is an individual metadata block in the mount's existing cache. The native platform records successful SFS2 checksum verification in the entry's existing verification word. Classic SFS uses the block type as its verification value. SFS2 uses the complement of its ASCII type. Each SFS2 hit still checks the actual type and own-block number. Replacement, overwrite and invalidation clear or replace the verification value along with the image.

The fast path copies into the mount's private metadata scratch. It uses the ordinary logical reader when RAM, redo, dirty, staged or queued data takes precedence. Portable platforms can decline the fast path. Root selection, volume cleanliness, node-tree and object validation still execute on every open. No pointer escapes the operation and the change adds no heap allocation, cache entries or mount fields.

## Measurements

All arms use the same C# guest probe, formatted SFS2 image and synchronous CopperHDF backend under Kickstart 3.1 on the PAL M68020 configuration. The original is SmartFilesystem 1.279. The previous arm uses the preceding optimized Copper HUNKs. The current build isolates the metadata verification change from concurrent controller and writer development.

“Cold” means the first DOS open after the benchmark remount. Mount itself can populate the metadata cache. “Warm” means one untimed open and close of the same pathname immediately before the measured open on that mount. The priming open reads no payload. Every trial measures DOS open, read loop and close individually and checks all payload bytes. The table gives three-trial medians. Physical IDE timing is outside this comparison.

## Where the open cycles go

A separate instruction trace of the CSUN comparison with 64 KiB reads and payload-cache bypass measured the following exclusive handler cycles. DOS opens in all six traces issued zero CopperHDF read requests. The first open can still benefit from data fetched during mount.

| Timed open | Previous Copper | New Copper | Original |
|---|---:|---:|---:|
| First after remount | 102,204 | 87,955 | 39,582 |
| Reopen of same path | 94,738 | 60,010 | 18,387 |

On the first open Copper performed seven checksums in each build. Five used the faster block-sum routine in the new build. Together the seven cost 17,899 cycles, down from 30,878. On reopen, checksum work fell from seven calls costing 30,993 cycles to one costing 1,816 cycles. Seven private-scratch copies still cost 17,167 cycles (29% of the new handler's reopen time). Packet dispatch used 5,901 cycles, retained-block lookup 3,785, and resolving the object record 3,652. The remaining gap inside the handler was 41,623 cycles.

The original's busiest cold loop at HUNK offsets +0x17dd6 through +0x17dda copies a longword, decrements a counter and branches. It ran 384 iterations (1,536 bytes), costing 6,546 cycles. That loop did not execute on the measured reopen. The original HUNK has no method symbols, so the trace does not identify the metadata objects it retained.

## Kickstart A/B results

The probe reads 64 KiB in the 64-byte workload, 256 KiB in the 512-byte workload, and 1 MiB at both larger sizes. Session throughput includes open, read and close. Each row has three trials for each of the three arms. The ROM, probe, starting media and resulting stream hashes match across each comparison.

| Handler | Cache | Open | Read bytes | Open cycles, previous / new / original | Session KiB/s, previous / new / original |
|---|---|---|---:|---:|---:|
| sfs2-csun | cached | cold | 64 | 128,480 / 115,430 / 66,450 | 24.13 / 24.15 / 23.67 |
| sfs2-csun | cached | cold | 512 | 135,420 / 115,600 / 71,040 | 165.16 / 166.14 / 163.16 |
| sfs2-csun | cached | cold | 4,096 | 128,360 / 115,490 / 66,450 | 779.14 / 783.67 / 745.03 |
| sfs2-csun | cached | cold | 65,536 | 128,450 / 114,220 / 66,450 | 13,515.50 / 15,248.42 / 17,577.41 |
| sfs2-csun | cached | warm | 64 | 122,190 / 81,680 / 45,250 | 24.15 / 24.19 / 23.68 |
| sfs2-csun | cached | warm | 512 | 135,630 / 86,290 / 45,250 | 165.30 / 166.69 / 163.51 |
| sfs2-csun | cached | warm | 4,096 | 120,990 / 86,260 / 45,250 | 780.91 / 786.87 / 746.76 |
| sfs2-csun | cached | warm | 65,536 | 120,920 / 86,280 / 45,250 | 13,673.23 / 15,796.20 / 18,554.38 |
| sfs2-csun | bypass | cold | 64 | 128,420 / 114,230 / 66,450 | 24.15 / 24.15 / 23.67 |
| sfs2-csun | bypass | cold | 512 | 128,390 / 114,310 / 71,030 | 189.46 / 190.67 / 163.19 |
| sfs2-csun | bypass | cold | 4,096 | 128,450 / 114,250 / 66,440 | 1,478.78 / 1,493.34 / 744.47 |
| sfs2-csun | bypass | cold | 65,536 | 128,360 / 114,220 / 66,450 | 13,490.90 / 15,031.33 / 17,577.41 |
| sfs2-csun | bypass | warm | 64 | 122,200 / 86,250 / 45,250 | 24.15 / 24.19 / 23.69 |
| sfs2-csun | bypass | warm | 512 | 120,930 / 86,280 / 45,260 | 189.56 / 191.30 / 163.48 |
| sfs2-csun | bypass | warm | 4,096 | 120,890 / 86,260 / 45,250 | 1,480.45 / 1,503.99 / 746.82 |
| sfs2-csun | bypass | warm | 65,536 | 120,880 / 86,270 / 45,250 | 13,832.84 / 15,599.79 / 18,564.34 |
| sfs2-trst | cached | cold | 64 | 128,040 / 114,330 / 66,450 | 24.16 / 24.13 / 23.67 |
| sfs2-trst | cached | cold | 512 | 128,130 / 114,290 / 71,010 | 165.22 / 165.80 / 163.19 |
| sfs2-trst | cached | cold | 4,096 | 128,140 / 114,380 / 66,450 | 780.24 / 783.49 / 745.03 |
| sfs2-trst | cached | cold | 65,536 | 129,500 / 114,380 / 66,440 | 13,703.67 / 14,671.27 / 17,600.84 |
| sfs2-trst | cached | warm | 64 | 135,470 / 86,460 / 45,250 | 24.15 / 24.16 / 23.68 |
| sfs2-trst | cached | warm | 512 | 120,560 / 86,460 / 45,250 | 165.39 / 166.37 / 163.46 |
| sfs2-trst | cached | warm | 4,096 | 120,580 / 86,490 / 45,250 | 779.92 / 785.97 / 746.87 |
| sfs2-trst | cached | warm | 65,536 | 120,630 / 87,820 / 49,970 | 14,092.35 / 15,605.82 / 18,575.73 |
| sfs2-trst | bypass | cold | 64 | 128,040 / 115,640 / 71,010 | 24.15 / 24.13 / 23.67 |
| sfs2-trst | bypass | cold | 512 | 128,020 / 114,320 / 71,000 | 189.90 / 190.47 / 163.18 |
| sfs2-trst | bypass | cold | 4,096 | 128,040 / 114,330 / 71,060 | 1,479.20 / 1,495.73 / 744.84 |
| sfs2-trst | bypass | cold | 65,536 | 128,140 / 114,380 / 66,440 | 13,509.47 / 15,123.65 / 17,570.18 |
| sfs2-trst | bypass | warm | 64 | 120,550 / 86,470 / 45,250 | 24.17 / 24.17 / 23.68 |
| sfs2-trst | bypass | warm | 512 | 127,600 / 86,500 / 45,250 | 189.80 / 190.93 / 163.51 |
| sfs2-trst | bypass | warm | 4,096 | 121,900 / 86,510 / 45,250 | 1,483.27 / 1,500.90 / 746.76 |
| sfs2-trst | bypass | warm | 65,536 | 120,670 / 86,540 / 45,250 | 13,696.69 / 15,588.41 / 18,554.38 |

At 64 KiB on CSUN with bypass and a primed open, the read loop took 377,360 / 356,840 / 326,590 cycles and close took 17,960 / 17,940 / 19,450 cycles (previous / new / original, independent medians). The open accounts for the largest remaining phase gap in this case. At 64-byte packet sizes the much longer read loop dominates session throughput.

## Coverage

The Release solution built and 1,270 host tests passed. Three external-media tests skipped. Both measured SFS2 native variants passed the standalone packet replays, including the staged corrupt replacement after a verified cache hit on 68000 and 68020 (62 checks per variant). The CopperStart native SFS replay passed 157 checks with one classic-only skip. The compiler compatibility reports for the measured native handlers list zero managed allocation sites.

The 124 standalone replays also passed on handlers rebuilt with the working tree's concurrent controller changes. Those builds also reported zero managed allocation sites.

Full-ROM coverage comprised 288 timed SFS2 trials and 12 cross-reader writer/remount boots under Kickstart. The measurements cover synchronous CopperHDF on the PAL M68020. The benchmark isolates the SFS2 change and excludes concurrent controller work. The guest probe's optional top-level manifest setting `PrimeOpen` reproduces the warm case.
