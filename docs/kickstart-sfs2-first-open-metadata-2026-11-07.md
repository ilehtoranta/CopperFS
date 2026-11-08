# SFS2 metadata reads on first open

The first pathname lookup after remount still needs the file's directory container and node-tree block. Previously, each miss could read a block into mount scratch, copy it into the metadata cache and validate the scratch image. Revisiting the cached object could therefore require another checksum.

On a native SFS2 metadata-view miss, the reader can now transfer directly into a cache slot, validate the block type, own-block number and SFS2 checksum, and return the verified image. It uses the existing verification word and C# block-sum routine. A track-window hit retains the existing read-ahead data. The reader unlinks the victim before a potentially partial device transfer. A failed read returns an error, and a corrupt image receives no verification mark.

The optimization requires private mount scratch and a cache slot that satisfies the device's DMA constraints. Redo, staged and queued images keep their logical read precedence. Providers and devices that decline the optimization can use their retained view or ordinary scratch read. The existing pathname and mount generations govern reopen and mutation invalidation.

## Kickstart comparison

All three arms use the same C# DOS probe, synthetic SFS2 medium, PAL M68020, Kickstart 3.1 and synchronous CopperHDF. The two Copper builds come from matched working-tree source snapshots and the same CopperSharp68k compiler. This baseline includes single-block metadata I/O. The original is SmartFilesystem 1.279 and runs with its native cache policy.

First open follows remount. For reopen, the probe first opens and closes the same file without reading its contents. It measures open, read and close separately and verifies the returned data. The 64-byte workload reads 64 KiB, the 512-byte workload reads 256 KiB, and the 4 KiB and 64 KiB workloads each read 1 MiB. Timings are three-trial medians.

## Instruction profile

The 64 KiB bypass first open fetched two 512-byte metadata blocks in both Copper versions. The new handler validated each block once and made zero full-block copies. Previously it copied both blocks and checksummed the directory object a second time. The original issued zero device reads during this open. Its HUNK lacks symbols that would identify its individual checksum routines.

| First-open trace | Checksum passes, previous → new | Checksum cycles | Block copies | Copy cycles | Total handler cycles, previous → new / original |
|---|---:|---:|---:|---:|---:|
| CSUN | 3 → 2 | 10,636 → 3,614 | 2 → 0 | 4,880 → 0 | 56,436 → 42,621 / 39,582 |
| TRST | 3 → 2 | 10,626 → 3,632 | 2 → 0 | 4,880 → 0 | 56,647 → 42,229 / 39,582 |

On reopen, neither Copper build read a device block or performed a metadata checksum or copy. CSUN spent 11,147 cycles inside the new handler versus 11,167 previously. TRST spent 11,141 versus 11,117. Original SFS used 18,387. The following table uses complete DOS timings from independent uninstrumented runs.

## All four read sizes

First-open medians improved over the preceding Copper build in all 16 comparisons, and were below the original in 11. Reopen medians were below the original in all 16. Whole-session throughput includes the read loop and close.

| Handler | Cache | Open | Read bytes | Open cycles, previous / new / original | Session KiB/s, previous / new / original |
|---|---|---|---:|---:|---:|
| sfs2-csun | cached | first | 64 | 78,490 / 64,660 / 66,450 | 23.98 / 23.91 / 23.67 |
| sfs2-csun | cached | first | 512 | 83,060 / 70,620 / 71,020 | 164.33 / 164.72 / 163.18 |
| sfs2-csun | cached | first | 4,096 | 83,050 / 69,250 / 66,450 | 783.21 / 784.68 / 745.02 |
| sfs2-csun | cached | first | 65,536 | 83,190 / 64,670 / 66,440 | 15,773.22 / 16,448.62 / 17,571.03 |
| sfs2-csun | cached | reopen | 64 | 32,870 / 32,850 / 45,260 | 24.05 / 23.95 / 23.67 |
| sfs2-csun | cached | reopen | 512 | 32,870 / 37,430 / 45,250 | 165.09 / 165.23 / 163.46 |
| sfs2-csun | cached | reopen | 4,096 | 32,870 / 37,390 / 45,250 | 786.79 / 787.56 / 746.64 |
| sfs2-csun | cached | reopen | 65,536 | 32,870 / 32,850 / 45,250 | 18,095.41 / 17,873.68 / 18,515.13 |
| sfs2-csun | bypass | first | 64 | 83,090 / 64,670 / 66,450 | 23.97 / 23.89 / 23.65 |
| sfs2-csun | bypass | first | 512 | 78,480 / 64,660 / 71,020 | 189.48 / 190.02 / 163.17 |
| sfs2-csun | bypass | first | 4,096 | 79,780 / 69,240 / 66,440 | 1,491.78 / 1,497.07 / 745.11 |
| sfs2-csun | bypass | first | 65,536 | 83,090 / 69,240 / 66,440 | 16,248.83 / 16,966.90 / 17,574.86 |
| sfs2-csun | bypass | reopen | 64 | 32,870 / 32,850 / 49,830 | 24.03 / 23.96 / 23.68 |
| sfs2-csun | bypass | reopen | 512 | 34,130 / 32,850 / 45,250 | 190.37 / 190.73 / 163.46 |
| sfs2-csun | bypass | reopen | 4,096 | 32,870 / 32,850 / 45,250 | 1,503.92 / 1,511.54 / 746.77 |
| sfs2-csun | bypass | reopen | 65,536 | 32,870 / 32,850 / 46,580 | 17,742.71 / 17,714.15 / 19,260.35 |
| sfs2-trst | cached | first | 64 | 83,320 / 64,260 / 66,450 | 24.04 / 23.92 / 23.66 |
| sfs2-trst | cached | first | 512 | 79,880 / 65,480 / 71,000 | 164.62 / 164.74 / 163.23 |
| sfs2-trst | cached | first | 4,096 | 91,270 / 64,260 / 66,450 | 783.05 / 784.70 / 745.00 |
| sfs2-trst | cached | first | 65,536 | 79,880 / 68,850 / 66,450 | 15,745.18 / 16,413.32 / 17,583.79 |
| sfs2-trst | cached | reopen | 64 | 32,820 / 32,830 / 45,260 | 24.09 / 23.94 / 23.69 |
| sfs2-trst | cached | reopen | 512 | 32,830 / 32,830 / 45,250 | 165.33 / 165.29 / 163.49 |
| sfs2-trst | cached | reopen | 4,096 | 32,820 / 32,830 / 45,250 | 787.94 / 787.93 / 746.79 |
| sfs2-trst | cached | reopen | 65,536 | 32,820 / 32,840 / 45,250 | 17,535.83 / 17,965.62 / 19,262.90 |
| sfs2-trst | bypass | first | 64 | 83,330 / 64,260 / 66,450 | 24.04 / 23.91 / 23.66 |
| sfs2-trst | bypass | first | 512 | 78,630 / 64,270 / 66,450 | 189.86 / 190.07 / 163.24 |
| sfs2-trst | bypass | first | 4,096 | 83,200 / 68,830 / 66,450 | 1,493.29 / 1,497.22 / 745.45 |
| sfs2-trst | bypass | first | 65,536 | 79,970 / 68,910 / 71,010 | 15,742.80 / 16,418.14 / 17,571.03 |
| sfs2-trst | bypass | reopen | 64 | 32,820 / 32,830 / 45,260 | 24.09 / 23.96 / 23.68 |
| sfs2-trst | bypass | reopen | 512 | 32,820 / 32,830 / 45,250 | 190.93 / 190.81 / 163.51 |
| sfs2-trst | bypass | reopen | 4,096 | 32,820 / 32,830 / 45,250 | 1,507.41 / 1,507.33 / 746.68 |
| sfs2-trst | bypass | reopen | 65,536 | 37,440 / 32,830 / 45,250 | 18,180.10 / 18,388.12 / 19,323.88 |

With 64 KiB reads and bypass, first-open whole-session throughput improved from 16,249 to 16,967 KiB/s for CSUN and from 15,743 to 16,418 KiB/s for TRST. Original SFS reached about 17,575 KiB/s on the two comparisons. The trace attributes CPU cycles inside each handler separately from Kickstart and the rest of the session.

## Verification

* Both SFS2 HUNKs passed all 86 native packet replays on the combined 68000 and 68020 suite. The tests inspected device destination addresses, exercised a failed read followed by retry, rejected an unverified corrupt block, and forced the DMA-mask fallback. Existing cases also exercised staged replacements, mutation invalidation, mounted-root generation changes and reopen.
* The freestanding compatibility reports confirmed zero managed allocation sites. The cache uses the existing metadata slots and per-slot verification words. CopperStart passed 157 native integration checks with one classic-only skip.
* Full-ROM coverage comprised 288 timed trials and 12 writer/remount boots. Both SFS2 variants passed the cross-reader checks against original SmartFilesystem, including the primed-path deletion, rename and truncation cases. All three timed arms used matching probe, ROM, media and stream hashes, with open, read and close accounting for the complete timed session.
* The Release solution built. 1,306 host tests passed and 3 external-media tests skipped.
