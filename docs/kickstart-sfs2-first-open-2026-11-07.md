# SFS2 first open after mount

During mount, SFS2 selects a root copy, checks the clean marker and validates the root directory object. The standalone handler now retains the validated root block, node tree, root's first child container and case-sensitivity flag. These fit in the mount's existing enumeration buffer after the pathname entry. The values own no pointers into a cache block or scratch buffer.

A first input-file lookup can start with those values. It still checks the pathname, traverses the child containers and node tree, validates the final object's identity and size, and goes through the DOS lock and handle checks. On an exact pathname hit the handler returns the existing cached result before consulting the mount information.

The mount information has its own generation stamp. Writes, directory mutations, inhibit and media changes invalidate it with the existing enumeration generation. Generation wrap clears the buffer. Recovery and transient transaction views discard both the mount information and the pathname entry. After invalidation, a cold pathname lookup takes the full validation path. A successful mount can publish fresh values. Removable media and mounts without optional storage use the ordinary resolver.

## Measurement

The guest probe measures DOS open, read loop and close independently on a PAL M68020 under Kickstart 3.1 with synchronous CopperHDF. First open follows remount. Reopen adds one untimed open and close of the same file. The workload reads 64 KiB in 64-byte calls, 256 KiB in 512-byte calls, and 1 MiB at both 4 KiB and 64 KiB. Each measurement is the median of three trials. The previous build already has the pathname cache. The original is SmartFilesystem 1.279 and uses its native cache policy.

Before this change, a 64 KiB bypass first-open trace spent 79,163 cycles in CSUN and 79,684 in TRST, versus 39,582 in the original. On CSUN, checksums took 17,868 cycles and four block copies took 9,604. Both original and Copper issued zero device reads during this open. The original's busiest copy loop moved 384 longwords in 6,546 cycles. Its binary has no method symbols, so the trace identifies the copy but cannot assign it to a particular filesystem object.

## Where the cycles went

| Bypass trace, 64 KiB | Previous handler cycles | Optimized handler cycles | Original handler cycles |
|---|---:|---:|---:|
| CSUN first open | 79,163 | 58,309 | 39,582 |
| TRST first open | 79,684 | 58,219 | 39,582 |
| CSUN reopen | 11,014 | 11,147 | 18,387 |
| TRST reopen | 11,141 | 11,162 | 18,387 |

The optimized first-open traces issued zero device reads. On CSUN, checksum work fell to 10,746 cycles. Four block copies still cost 9,622 cycles. The first-open handler gap fell by 53% on CSUN and 54% on TRST. The original handler is still faster at first open. All 16 reopen medians remained faster than the original, although the read loop still affects whole-session throughput. For example, the 64 KiB CSUN bypass reopen read loop rose from 340,550 to 363,500 cycles even as its DOS open median fell.

## All four read sizes

The matched Copper builds isolate this change on the preceding benchmark source and CopperSharp68k executable. A second run used handlers rebuilt from the working tree, which also includes storage changes. The rows report **previous / optimized / working tree / original**. Each comparison checked the ROM, guest probe, media and returned data hashes, the handler binary, and the sum of the three timed phases.

| Build | Payload policy | Open | Read bytes | DOS open cycles | Whole-session KiB/s |
|---|---|---|---:|---:|---:|
| CSUN | cached | first | 64 | 105,540 / 85,690 / 83,370 / 66,450 | 24.18 / 24.14 / 23.96 / 23.67 |
| CSUN | cached | first | 512 | 105,430 / 84,570 / 83,370 / 66,450 | 166.28 / 166.30 / 164.09 / 163.21 |
| CSUN | cached | first | 4,096 | 105,440 / 81,230 / 78,790 / 66,450 | 785.68 / 786.10 / 783.36 / 744.80 |
| CSUN | cached | first | 65,536 | 105,450 / 81,140 / 92,650 / 71,110 | 15,170.40 / 16,008.55 / 15,604.81 / 17,569.76 |
| CSUN | cached | reopen | 64 | 32,700 / 32,850 / 32,930 / 45,250 | 24.28 / 24.22 / 24.02 / 23.68 |
| CSUN | cached | reopen | 512 | 32,700 / 32,850 / 32,930 / 45,250 | 167.34 / 167.09 / 164.72 / 163.45 |
| CSUN | cached | reopen | 4,096 | 32,700 / 32,850 / 37,500 / 45,250 | 791.68 / 790.09 / 786.62 / 746.65 |
| CSUN | cached | reopen | 65,536 | 32,700 / 32,850 / 32,930 / 45,250 | 18,242.19 / 17,582.52 / 17,448.63 / 19,330.57 |
| CSUN | bypass | first | 64 | 105,460 / 84,560 / 83,370 / 66,440 | 24.18 / 24.15 / 23.94 / 23.67 |
| CSUN | bypass | first | 512 | 105,540 / 85,730 / 83,420 / 71,020 | 190.90 / 190.95 / 189.34 / 163.18 |
| CSUN | bypass | first | 4,096 | 105,510 / 79,900 / 78,790 / 66,450 | 1,498.71 / 1,501.15 / 1,491.94 / 744.85 |
| CSUN | bypass | first | 65,536 | 100,850 / 84,590 / 83,370 / 66,450 | 15,362.25 / 16,257.56 / 16,069.82 / 17,599.98 |
| CSUN | bypass | reopen | 64 | 32,700 / 32,850 / 32,930 / 45,250 | 24.27 / 24.23 / 24.00 / 23.68 |
| CSUN | bypass | reopen | 512 | 32,700 / 32,850 / 34,200 / 45,250 | 192.47 / 192.01 / 190.36 / 163.44 |
| CSUN | bypass | reopen | 4,096 | 32,700 / 32,850 / 32,930 / 49,790 | 1,522.15 / 1,515.64 / 1,503.22 / 746.68 |
| CSUN | bypass | reopen | 65,536 | 37,320 / 32,860 / 32,930 / 45,260 | 17,946.98 / 17,530.75 / 17,780.92 / 19,328.51 |
| TRST | cached | first | 64 | 105,910 / 79,900 / 78,290 / 66,450 | 24.14 / 24.15 / 23.91 / 23.65 |
| TRST | cached | first | 512 | 107,220 / 81,240 / 78,290 / 71,020 | 166.11 / 166.27 / 163.79 / 163.17 |
| TRST | cached | first | 4,096 | 106,000 / 79,910 / 78,290 / 66,440 | 785.29 / 785.68 / 782.79 / 745.11 |
| TRST | cached | first | 65,536 | 106,020 / 84,490 / 84,140 / 66,440 | 15,383.07 / 16,255.38 / 15,385.03 / 17,574.86 |
| TRST | cached | reopen | 64 | 32,840 / 32,850 / 37,400 / 45,250 | 24.22 / 24.22 / 23.98 / 23.68 |
| TRST | cached | reopen | 512 | 32,840 / 32,850 / 32,850 / 45,260 | 167.40 / 166.96 / 164.50 / 163.50 |
| TRST | cached | reopen | 4,096 | 32,840 / 32,850 / 32,850 / 45,250 | 790.97 / 789.69 / 786.05 / 746.49 |
| TRST | cached | reopen | 65,536 | 32,840 / 32,850 / 32,850 / 45,250 | 18,167.37 / 18,168.28 / 17,623.90 / 19,331.60 |
| TRST | bypass | first | 64 | 105,960 / 81,170 / 79,620 / 66,450 | 24.14 / 24.15 / 23.92 / 23.67 |
| TRST | bypass | first | 512 | 106,000 / 81,150 / 78,290 / 71,030 | 190.84 / 190.85 / 189.28 / 163.19 |
| TRST | bypass | first | 4,096 | 105,970 / 84,500 / 82,990 / 66,440 | 1,497.85 / 1,499.20 / 1,486.29 / 744.47 |
| TRST | bypass | first | 65,536 | 105,980 / 84,580 / 82,870 / 66,450 | 15,823.04 / 15,613.53 / 15,759.87 / 17,577.41 |
| TRST | bypass | reopen | 64 | 32,840 / 32,850 / 38,750 / 45,250 | 24.22 / 24.21 / 23.96 / 23.68 |
| TRST | bypass | reopen | 512 | 32,840 / 32,850 / 32,850 / 45,250 | 192.35 / 191.82 / 190.11 / 163.51 |
| TRST | bypass | reopen | 4,096 | 37,420 / 32,850 / 32,850 / 45,250 | 1,519.21 / 1,515.89 / 1,505.91 / 746.65 |
| TRST | bypass | reopen | 65,536 | 32,840 / 32,850 / 32,850 / 45,270 | 17,917.76 / 17,854.35 / 17,751.38 / 19,330.05 |

The working-tree storage code performed two 512-byte reads in the profiled first open on each build. Its reopen issued zero device reads. This is why the request counts above are scoped to the matched comparison.

## Verification

* The Release solution built and 1,305 host tests passed, with three external-media tests skipped. A separate path-equivalence test selected a newer case-sensitive backup root and checked empty, nested, parent, relative, volume-qualified and invalid paths against full resolution.
* Both measured HUNKs passed 66 native packet checks apiece. The 68000 and 68020 replays verified that first lookup could skip the two root copies, clean marker and root object. A staged corrupt clean marker overrode the cached result, and a root-directory change after invalidation made the old child disappear. Both working-tree HUNKs passed all 80 current packet checks. CopperStart passed 157 integration checks with one classic-only skip.
* The freestanding compatibility reports list zero managed allocation sites for both SFS2 variants in both builds. Mount uses the existing optional buffer, with space for the full pathname key.
* Full-ROM coverage included 384 timed workload trials and 24 writer/remount boots across the matched and working-tree comparisons. The mutation probe primed the pathname cache before delete, rename and truncation. Copper and original SFS produced matching file manifests.
