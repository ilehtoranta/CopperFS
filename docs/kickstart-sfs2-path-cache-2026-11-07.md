# SFS2 pathname caching across opens

The standalone SFS2 handler remembers the last successful input-file lookup for each mount. A hit requires the same starting directory node, length and exact pathname bytes. The entry owns the name, node and 64-bit size. It uses the existing mount-owned enumeration buffer. SFS2 enumerates through its own reader and can use the classic enumeration slots for this entry. The mount state and allocation sizes stay the same. A different spelling goes through the volume's normal comparison rules. The DOS packet path validates the lock, name and handle addresses and checks for an exclusive opener on every hit.

The entry is eligible on known fixed disks and handler-owned RAM volumes. Removable or unclassified media use the resolver on every open. A redo overlay, active staged transaction, populated write buffer or nonempty device write queue clears the entry and uses the current logical view. Mutation and media invalidation advance the existing enumeration generation, including the fast write path, inhibit and uninhibit. Generation wrap clears the buffer. Pending recovery invalidates the entry explicitly, and remount releases its storage. A mount without the optional buffer uses the ordinary resolver. Writer lookups, failed lookups and directory results never populate it.

## Kickstart measurements

The comparison uses original SmartFilesystem 1.279, the preceding Copper build with validated metadata views, and the same build with pathname caching. The Copper builds use the same CopperSharp68k executable. The PAL M68020, Kickstart 3.1, guest DOS probe, starting SFS2 image and synchronous CopperHDF settings match across the three arms. The payload-cache policy applies to the Copper builds. All arms use 512-byte sectors and 64 KiB MaxTransfer.

First open starts after remount. Reopen starts after an additional untimed open and close of the same path. The probe times DOS open, the read loop and close independently. The table reports three-trial medians for open and for whole-session throughput. The 64-byte workload reads 64 KiB, the 512-byte workload reads 256 KiB, and each larger workload reads 1 MiB.

## Work inside the handler

In the 64 KiB bypass trace, reopen skipped root selection, metadata checksums and pathname traversal. DOS handle creation and lock checks still ran. All four profiled opens issued zero device reads.

| SFS2 handler | First open, previous → new handler cycles | Reopen, previous → new handler cycles |
|---|---:|---:|
| CSUN | 77,811 → 79,227 | 44,359 → 11,014 |
| TRST | 77,645 → 79,728 | 44,142 → 11,141 |

The first lookup still resolves the path and now also populates the entry. The two first-open traces added 1,416 and 2,083 handler cycles. Complete DOS timings below also include time outside the handler. With 64 KiB reads and bypass, reopen session throughput rose from 16,596 to 17,947 KiB/s for CSUN and from 16,594 to 17,918 KiB/s for TRST. Original SFS reached 18,523 and 19,329 KiB/s respectively.

## All four read sizes

| Handler | Cache | Open | Read bytes | Open cycles, previous / new / original | Session KiB/s, previous / new / original |
|---|---|---|---:|---:|---:|
| sfs2-csun | cached | cold | 64 | 105,450 / 105,540 / 66,450 | 24.16 / 24.18 / 23.67 |
| sfs2-csun | cached | cold | 512 | 104,110 / 105,410 / 71,030 | 166.44 / 166.28 / 163.19 |
| sfs2-csun | cached | cold | 4,096 | 104,070 / 105,540 / 66,440 | 785.48 / 785.64 / 744.47 |
| sfs2-csun | cached | cold | 65,536 | 100,740 / 105,450 / 66,450 | 15,657.62 / 15,323.04 / 17,577.41 |
| sfs2-csun | cached | warm | 64 | 70,630 / 32,700 / 49,830 | 24.22 / 24.27 / 23.68 |
| sfs2-csun | cached | warm | 512 | 71,940 / 32,700 / 45,250 | 166.95 / 167.38 / 163.46 |
| sfs2-csun | cached | warm | 4,096 | 66,050 / 32,700 / 45,250 | 788.92 / 791.72 / 746.77 |
| sfs2-csun | cached | warm | 65,536 | 66,050 / 32,700 / 46,580 | 16,663.32 / 18,241.28 / 19,260.35 |
| sfs2-csun | bypass | cold | 64 | 105,430 / 105,450 / 71,060 | 24.17 / 24.18 / 23.66 |
| sfs2-csun | bypass | cold | 512 | 105,360 / 105,560 / 66,450 | 191.18 / 190.90 / 163.22 |
| sfs2-csun | bypass | cold | 4,096 | 99,500 / 105,430 / 66,450 | 1,500.46 / 1,498.73 / 745.26 |
| sfs2-csun | bypass | cold | 65,536 | 104,110 / 100,850 / 66,440 | 15,857.93 / 15,361.60 / 17,570.18 |
| sfs2-csun | bypass | warm | 64 | 70,630 / 32,700 / 49,830 | 24.22 / 24.27 / 23.68 |
| sfs2-csun | bypass | warm | 512 | 66,050 / 32,700 / 45,250 | 191.83 / 192.47 / 163.53 |
| sfs2-csun | bypass | warm | 4,096 | 70,590 / 32,700 / 45,250 | 1,513.19 / 1,522.46 / 746.87 |
| sfs2-csun | bypass | warm | 65,536 | 67,300 / 37,320 / 45,250 | 16,595.94 / 17,946.98 / 18,523.16 |
| sfs2-trst | cached | cold | 64 | 103,910 / 105,910 / 66,450 | 24.14 / 24.14 / 23.67 |
| sfs2-trst | cached | cold | 512 | 103,930 / 107,240 / 70,990 | 166.19 / 166.12 / 163.17 |
| sfs2-trst | cached | cold | 4,096 | 103,930 / 106,120 / 66,440 | 785.61 / 785.28 / 745.06 |
| sfs2-trst | cached | cold | 65,536 | 104,000 / 106,020 / 66,450 | 15,606.49 / 15,383.07 / 17,571.88 |
| sfs2-trst | cached | warm | 64 | 65,850 / 32,840 / 45,250 | 24.19 / 24.22 / 23.68 |
| sfs2-trst | cached | warm | 512 | 65,840 / 32,840 / 45,250 | 166.71 / 167.42 / 163.48 |
| sfs2-trst | cached | warm | 4,096 | 67,110 / 32,840 / 45,250 | 788.26 / 791.05 / 746.71 |
| sfs2-trst | cached | warm | 65,536 | 65,850 / 32,840 / 49,790 | 16,598.59 / 18,167.37 / 18,638.17 |
| sfs2-trst | bypass | cold | 64 | 103,990 / 105,960 / 66,450 | 24.15 / 24.14 / 23.67 |
| sfs2-trst | bypass | cold | 512 | 103,930 / 106,000 / 71,010 | 190.86 / 190.84 / 163.19 |
| sfs2-trst | bypass | cold | 4,096 | 103,990 / 106,010 / 66,450 | 1,498.19 / 1,497.85 / 745.03 |
| sfs2-trst | bypass | cold | 65,536 | 103,970 / 105,970 / 66,440 | 15,889.49 / 15,822.69 / 17,600.84 |
| sfs2-trst | bypass | warm | 64 | 70,410 / 32,840 / 45,250 | 24.19 / 24.23 / 23.68 |
| sfs2-trst | bypass | warm | 512 | 67,090 / 32,840 / 45,250 | 191.66 / 192.29 / 163.46 |
| sfs2-trst | bypass | warm | 4,096 | 70,450 / 37,460 / 45,250 | 1,509.58 / 1,518.44 / 746.67 |
| sfs2-trst | bypass | warm | 65,536 | 70,420 / 32,840 / 45,250 | 16,594.42 / 17,917.76 / 19,329.03 |

On the 64 KiB TRST bypass reopen, the read loop measured 341,540 / 354,520 / 315,700 cycles (previous / new / original) and close measured 17,980 / 18,050 / 14,860. Open, read, close and session medians were each calculated from three complete trials. The comparison checked ROM, probe, handler, medium and returned stream hashes, and checked the phase totals for every trial.

## Verification

* The measured SFS2 builds passed 64 native packet replays apiece. The cache-specific replay exercised independent opens, a reused name address with different bytes, relative directories, exclusive locks, 64-bit sizes, staged metadata, generation wrap and an unavailable cache on both 68000 and 68020. Both working-tree builds also passed all 72 current packet replays. Freestanding compiler reports showed zero managed allocation sites.
* CopperStart passed 157 native integration checks, including its mount-state boundary guard. One classic-only check skipped.
* The timed Kickstart runs checked all bytes in 288 workloads. Twelve writer/remount boots checked interoperability with the original. On the same mount the guest primed the path before delete, rename and truncate, checked the old names returned ObjectNotFound, and observed the replacement size. Both versions produced matching file manifests.
* The Release solution built, 1,305 host tests passed and 3 external-media tests skipped.
