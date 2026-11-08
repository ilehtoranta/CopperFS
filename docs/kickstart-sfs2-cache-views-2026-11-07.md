# Reading cached SFS2 metadata during open

CopperSFS2 can now decode a validated root, clean marker, node-tree block or directory object directly from the mount's metadata cache. Each borrow ends at the next block access. Root selection saves the primary root's scalar fields before fetching the backup. Path traversal likewise consumes each directory record, child link and node-tree pointer before fetching another block. DOS open returns the node, parent, size and directory flag as values.

The native platform borrows from the same verified entries used by the preceding implementation. A cache miss or an overriding logical image takes the ordinary read-and-validate path into private scratch. Root and object APIs that return a caller-owned image, editable metadata, or name and comment pointers retain their scratch contract. The new path adds no mount storage.

## Kickstart comparison

The comparison uses the preceding Copper build, the build with borrowed metadata views, and original SmartFilesystem 1.279. Both Copper builds use the same CopperSharp68k compiler and the same source snapshot except for the cache-view change. All arms use the same C# DOS probe, synthetic media, Kickstart 3.1, PAL M68020 and synchronous CopperHDF. The payload-cache setting applies to Copper. The original runs with its native policy.

First open starts after remount, which can already populate metadata. Reopen adds one untimed open and close of the same pathname after remount, without reading payload. DOS open, read loop and close have separate timing markers. The 64-byte workload reads 64 KiB, the 512-byte workload reads 256 KiB, and the 4 KiB and 64 KiB workloads each read 1 MiB. Reported cycles are guest bus cycles. Throughput covers the whole session. Each result is the median of three trials.

## Effect on open

In a separate instruction trace of the 64 KiB workload with payload-cache bypass, the warmed CSUN and TRST handlers made zero block copies during DOS open. Previously each made seven. On the first open after remount, both went from nine copies to four. Those four occur on the ordinary metadata read and cache-fill path. All four new open traces issued zero CopperHDF read requests.

| Build | Open | Block copies, previous → new | Exclusive copy cycles, previous → new | Total handler instruction cycles, previous → new |
|---|---|---:|---:|---:|
| CSUN | First | 9 → 4 | 21,869 → 9,598 | 87,955 → 77,811 |
| CSUN | Reopen | 7 → 0 | 17,167 → 0 | 60,010 → 44,359 |
| TRST | First | 9 → 4 | 21,829 → 9,604 | 88,012 → 77,645 |
| TRST | Reopen | 7 → 0 | 17,148 → 0 | 60,213 → 44,142 |

These instruction counts compare the new trace with the [preceding build's profile](kickstart-sfs2-repeated-open-2026-11-07.md). The uninstrumented A/B timings below were rerun for all three binaries. The largest remaining CSUN reopen costs were packet dispatch (5,901 cycles), path resolution (4,127 exclusive cycles) and retained-block lookup (3,847). The 64 KiB bypass reopen still took 22,020 more guest cycles than the original on CSUN and 25,170 more on TRST.

## All four read sizes

| Handler | Cache | Open | Read bytes | Open cycles, previous / new / original | Session KiB/s, previous / new / original |
|---|---|---|---:|---:|---:|
| sfs2-csun | cached | cold | 64 | 115,430 / 105,450 / 66,450 | 24.15 / 24.16 / 23.66 |
| sfs2-csun | cached | cold | 512 | 115,430 / 104,130 / 66,450 | 166.10 / 166.40 / 163.24 |
| sfs2-csun | cached | cold | 4,096 | 115,450 / 104,080 / 66,450 | 783.63 / 785.48 / 745.45 |
| sfs2-csun | cached | cold | 65,536 | 114,260 / 100,800 / 71,010 | 15,250.98 / 15,657.62 / 17,571.03 |
| sfs2-csun | cached | warm | 64 | 81,680 / 70,630 / 45,250 | 24.19 / 24.22 / 23.68 |
| sfs2-csun | cached | warm | 512 | 86,300 / 71,950 / 45,250 | 166.64 / 166.99 / 163.46 |
| sfs2-csun | cached | warm | 4,096 | 86,250 / 66,050 / 45,250 | 786.86 / 788.97 / 746.67 |
| sfs2-csun | cached | warm | 65,536 | 86,230 / 66,050 / 45,250 | 15,797.23 / 16,652.62 / 19,329.03 |
| sfs2-csun | bypass | cold | 64 | 114,340 / 105,360 / 66,450 | 24.15 / 24.18 / 23.67 |
| sfs2-csun | bypass | cold | 512 | 114,330 / 105,360 / 70,990 | 190.65 / 191.23 / 163.19 |
| sfs2-csun | bypass | cold | 4,096 | 114,260 / 99,500 / 66,440 | 1,493.65 / 1,500.41 / 745.01 |
| sfs2-csun | bypass | cold | 65,536 | 114,240 / 104,110 / 66,450 | 15,031.02 / 15,853.77 / 17,600.84 |
| sfs2-csun | bypass | warm | 64 | 86,250 / 70,680 / 45,250 | 24.19 / 24.22 / 23.69 |
| sfs2-csun | bypass | warm | 512 | 86,260 / 66,050 / 45,250 | 191.29 / 191.88 / 163.47 |
| sfs2-csun | bypass | warm | 4,096 | 86,260 / 70,610 / 45,250 | 1,504.03 / 1,513.19 / 746.58 |
| sfs2-csun | bypass | warm | 65,536 | 86,250 / 67,280 / 45,260 | 15,599.79 / 16,605.80 / 19,269.04 |
| sfs2-trst | cached | cold | 64 | 114,330 / 103,910 / 66,450 | 24.14 / 24.14 / 23.65 |
| sfs2-trst | cached | cold | 512 | 114,280 / 103,910 / 71,020 | 165.80 / 166.20 / 163.17 |
| sfs2-trst | cached | cold | 4,096 | 114,280 / 103,910 / 66,440 | 783.48 / 785.41 / 745.11 |
| sfs2-trst | cached | cold | 65,536 | 114,380 / 104,000 / 66,440 | 14,670.98 / 15,601.80 / 17,574.86 |
| sfs2-trst | cached | warm | 64 | 86,460 / 65,850 / 45,250 | 24.17 / 24.19 / 23.68 |
| sfs2-trst | cached | warm | 512 | 86,460 / 65,850 / 45,250 | 166.37 / 166.73 / 163.46 |
| sfs2-trst | cached | warm | 4,096 | 86,480 / 67,080 / 45,250 | 785.96 / 788.41 / 746.67 |
| sfs2-trst | cached | warm | 65,536 | 87,680 / 65,840 / 45,250 | 15,605.82 / 16,600.11 / 19,329.03 |
| sfs2-trst | bypass | cold | 64 | 115,640 / 103,910 / 66,450 | 24.13 / 24.14 / 23.67 |
| sfs2-trst | bypass | cold | 512 | 114,320 / 104,010 / 70,990 | 190.47 / 190.86 / 163.24 |
| sfs2-trst | bypass | cold | 4,096 | 114,340 / 103,920 / 66,440 | 1,495.81 / 1,499.05 / 745.20 |
| sfs2-trst | bypass | cold | 65,536 | 114,370 / 103,920 / 66,450 | 15,120.82 / 15,890.88 / 17,605.53 |
| sfs2-trst | bypass | warm | 64 | 86,460 / 70,380 / 49,830 | 24.17 / 24.19 / 23.68 |
| sfs2-trst | bypass | warm | 512 | 86,500 / 67,100 / 45,250 | 190.92 / 191.67 / 163.52 |
| sfs2-trst | bypass | warm | 4,096 | 81,910 / 70,480 / 45,250 | 1,500.86 / 1,509.38 / 746.69 |
| sfs2-trst | bypass | warm | 65,536 | 86,480 / 70,420 / 45,250 | 15,608.17 / 16,594.42 / 18,540.18 |

On the CSUN 64 KiB bypass reopen, median read-loop cycles were 356,840 / 352,250 / 312,240, and close cycles were 17,940 / 17,940 / 14,860 (previous / new / original). A session median uses complete individual trials, so independently calculated phase medians need not sum to it. The raw trials checked all returned bytes, phase totals and matching ROM, medium, guest probe and handler identities.

## Verification

* **Host.** The Release solution built and 1,270 tests passed. Three external-media tests skipped. A separately built regression test overwrote a single borrowed slot on every lookup. It resolved a nested path with differing primary and backup roots without touching metadata scratch, then verified that a publicly returned name and comment survived clearing the borrowed slot.
* **Native.** Both benchmark HUNKs passed 62 packet checks apiece, including rejection of a corrupt staged replacement. Rebuilding the SFS2 handlers from the working tree with concurrent controller changes also passed 62 checks per variant. All four SFS2 builds reported zero managed allocation sites. CopperStart's OS integration profile passed 157 replay checks with one classic-only skip.
* **Full ROM.** All 288 timed workload trials passed. Twelve additional Kickstart boots exercised writer/remount handoffs between Copper and original SmartFilesystem. Timing covers the emulated synchronous CopperHDF configuration.
