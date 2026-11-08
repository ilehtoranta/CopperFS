# Cached TRST 64 KiB reads after reopen under Kickstart

The benchmark remounts the volume, opens and closes the file once, then times a second open, a 1 MiB read in sixteen 64 KiB packets, and close. The priming open reads no payload. The previously reported session median rose from 296,090 to 303,000 guest cycles. All three regressed trials still issued sixteen direct payload reads.

The per-trial profiles locate the difference in the read interval. The historical handler used 144,416–144,444 cycles in that interval and the regressed handler used 143,845–143,857. Kickstart ROM read-loop time was 115,058, 101,825 and 107,514 cycles historically and 115,039, 108,497 and 115,060 in the regressed build. Reopen used 5,783 handler cycles in all three regressed traces and close used 2,674. The traces locate the extra cycles in ROM execution.

The priming open already resolved the canonical node through the node tree, checked the object record and its strings, and validated the file size. The C# implementation keeps the regular file's first extent key alongside the existing pathname result and the extent-tree key alongside the clean mount result. At cursor initialization it reuses those scalars only for the matching node on a stable SFS2 mount with current generations for both results. The initial extent count is zero, so the first read still checks the extent record's checksum, bounds and back-link before using it. Cache misses, invalidations, soft links, and active transaction or write overlays use the ordinary metadata path. The keys fit in the existing mount-owned storage and no disk-buffer pointer survives an I/O.

The native dispatcher passes the validated scalars by value on the cold cursor path. The portable reader checks the destination, scratch aliases and mapping before accepting them. Other callers use the existing entry point and normal metadata validation.

The A/B uses Kickstart 3.1 on a PAL M68020 with synchronous CopperHDF, 512-byte sectors and 64 KiB MaxTransfer. Cold runs open immediately after remount and warm runs include the priming open and close. The workloads read 64 KiB, 256 KiB, 1 MiB and 1 MiB with 64-byte, 512-byte, 4 KiB and 64 KiB calls respectively. Each standard cell is the median of three uninstrumented runs with matching ROM, probe, media and result digests.

## Cached TRST after reopen

The 64 KiB session fell from 303,000 to 262,710 guest cycles, 13.3% fewer than the regressed build and 11.3% fewer than the earlier 296,090-cycle median. Original SmartFilesystem 1.279 used 387,550 cycles. Each entry below is the median of three uninstrumented measurements. Individual phase medians can come from different trials.

| 1 MiB in sixteen 64 KiB reads | Regressed Copper | Final Copper | Original SFS |
|---|---:|---:|---:|
| Reopen cycles | 27,530 | 32,130 | 45,330 |
| Read-loop cycles | 264,930 | 220,040 | 312,110 |
| Close cycles | 10,530 | 10,540 | 14,880 |
| Complete session cycles | 303,000 | 262,710 | 387,550 |
| Complete session KiB/s | 23,973.73 | 27,650.42 | 18,743.49 |

The final instruction traces used 118,282–118,288 handler cycles in the read loop, about 25,600 fewer per session. Each trace recorded 16 payload reads of 65,536 bytes directly into the caller's buffer and zero requests at reopen and close. The separately timed reopen rose by 4,600 guest cycles. Handler time at reopen changed from 5,783 cycles to 5,794–5,796, with the larger change in Kickstart ROM time.

A separate cached TRST run used 15 repetitions per size with the same guest probe across all three Copper builds. At 64 KiB even the slowest final session finished ahead of the fastest session in either earlier build.

| Cached TRST 64 KiB session cycles | Minimum | Median | Maximum |
|---|---:|---:|---:|
| Copper before the regression | 288,970 | 293,660 | 305,010 |
| Regressed Copper | 288,210 | 302,950 | 310,290 |
| Final Copper | 262,670 | 262,790 | 269,760 |

## All four Kickstart A/Bs

Each triple gives **regressed Copper / final Copper / original SFS**. The policy column is the CopperHDF payload policy for Copper. Original SFS used its own caching in both paired runs. Session throughput includes the timed open or reopen, reads and close. All 32 final session throughput medians exceeded the corresponding original.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 128,180 / 129,060 / 212,890 | 56,300 / 56,750 / 71,100 | 27,560 / 27,560 / 45,330 | 29.99 / 29.92 / 23.66 | 30.03 / 29.98 / 23.71 |
| CSUN | cached | 512 | 123,580 / 128,910 / 194,850 | 51,730 / 52,170 / 66,520 | 27,560 / 27,560 / 45,330 | 209.77 / 209.49 / 163.25 | 210.31 / 210.12 / 163.56 |
| CSUN | cached | 4,096 | 135,250 / 128,890 / 212,970 | 51,730 / 52,160 / 71,090 | 27,550 / 27,570 / 45,320 | 903.22 / 902.40 / 745.13 | 904.75 / 906.92 / 747.08 |
| CSUN | cached | 65,536 | 128,180 / 128,930 / 198,220 | 56,320 / 56,750 / 71,090 | 27,560 / 27,560 / 46,670 | 21,881.62 / 25,115.10 / 17,588.05 | 25,074.36 / 27,590.55 / 18,729.96 |
| CSUN | bypass | 64 | 128,320 / 129,060 / 212,880 | 56,300 / 52,160 / 71,090 | 27,560 / 27,570 / 45,330 | 29.99 / 29.94 / 23.67 | 30.04 / 29.98 / 23.69 |
| CSUN | bypass | 512 | 128,180 / 129,060 / 194,820 | 51,730 / 52,170 / 71,120 | 27,560 / 27,570 / 45,320 | 253.98 / 253.64 / 163.25 | 254.72 / 254.32 / 163.62 |
| CSUN | bypass | 4,096 | 128,160 / 130,130 / 212,830 | 51,730 / 56,750 / 71,110 | 32,120 / 27,560 / 45,320 | 1,992.51 / 2,005.36 / 745.20 | 2,014.01 / 2,018.61 / 746.69 |
| CSUN | bypass | 65,536 | 128,160 / 128,910 / 193,610 | 51,730 / 52,170 / 71,140 | 27,550 / 27,560 / 45,330 | 22,661.89 / 24,522.45 / 17,631.17 | 24,468.76 / 26,183.33 / 19,335.71 |
| TRST | cached | 64 | 128,490 / 129,210 / 212,870 | 51,970 / 56,810 / 71,090 | 27,530 / 27,550 / 45,320 | 29.98 / 29.91 / 23.68 | 30.03 / 29.98 / 23.69 |
| TRST | cached | 512 | 129,730 / 129,120 / 193,770 | 56,560 / 56,720 / 66,520 | 27,530 / 27,550 / 45,330 | 210.07 / 210.39 / 163.25 | 210.65 / 211.05 / 163.58 |
| TRST | cached | 4,096 | 128,480 / 129,130 / 212,950 | 51,970 / 52,150 / 71,200 | 27,530 / 27,540 / 49,900 | 901.62 / 905.90 / 745.00 | 906.35 / 909.03 / 746.90 |
| TRST | cached | 65,536 | 128,560 / 129,130 / 193,580 | 56,630 / 56,730 / 71,130 | 27,530 / 32,130 / 45,330 | 22,237.99 / 25,169.93 / 17,634.59 | 23,973.73 / 27,650.42 / 18,743.49 |
| TRST | bypass | 64 | 128,480 / 129,220 / 212,890 | 56,550 / 56,700 / 71,100 | 27,530 / 27,550 / 45,320 | 29.98 / 29.93 / 23.66 | 30.04 / 29.98 / 23.69 |
| TRST | bypass | 512 | 129,700 / 129,140 / 194,850 | 56,540 / 52,150 / 66,520 | 27,530 / 27,540 / 45,320 | 254.13 / 254.16 / 163.27 | 254.79 / 255.12 / 163.55 |
| TRST | bypass | 4,096 | 128,490 / 129,220 / 212,970 | 53,260 / 52,150 / 71,090 | 32,140 / 27,540 / 49,900 | 2,002.98 / 2,010.68 / 745.13 | 2,014.19 / 2,027.24 / 746.57 |
| TRST | bypass | 65,536 | 128,690 / 130,370 / 198,210 | 51,970 / 57,970 / 71,080 | 27,530 / 27,550 / 45,320 | 23,137.57 / 25,175.16 / 17,591.88 | 23,975.32 / 27,651.47 / 18,784.21 |

For cached TRST, the 64-byte reopen workload changed from 30.03 to 29.98 KiB/s, compared with 23.69 for original SFS. Every final 64-byte workload still fetched the entire 64 KiB stream with one request. The 4 KiB remount-plus-first-open median was 129,130 cycles, compared with 128,480 immediately before this change, 161,190 before the previous remount fix, and 212,950 for original SFS. All 96 final timed remounts issued two requests totaling 4,608 bytes and first open issued zero.

## Read-loop measurements

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 15,072,430 / 15,104,470 / 19,085,500 | 15,080,410 / 15,103,780 / 19,089,710 | 30.12 / 30.06 / 23.79 |
| CSUN | cached | 512 | 8,594,850 / 8,606,130 / 11,041,370 | 8,595,000 / 8,604,530 / 11,042,240 | 211.29 / 211.01 / 164.47 |
| CSUN | cached | 4,096 | 7,975,090 / 7,986,920 / 9,662,680 | 7,990,640 / 7,971,470 / 9,663,000 | 910.84 / 909.49 / 751.76 |
| CSUN | cached | 65,536 | 251,580 / 226,420 / 327,030 | 250,450 / 225,150 / 315,450 | 28,873.68 / 32,082.15 / 22,212.15 |
| CSUN | bypass | 64 | 15,073,910 / 15,103,190 / 19,078,850 | 15,073,510 / 15,106,680 / 19,097,180 | 30.12 / 30.06 / 23.80 |
| CSUN | bypass | 512 | 7,087,900 / 7,097,030 / 11,041,590 | 7,085,210 / 7,102,470 / 11,038,600 | 256.21 / 255.88 / 164.47 |
| CSUN | bypass | 4,096 | 3,565,210 / 3,554,990 / 9,662,750 | 3,564,060 / 3,555,790 / 9,663,670 | 2,037.48 / 2,043.34 / 751.76 |
| CSUN | bypass | 65,536 | 258,260 / 233,480 / 325,980 | 258,770 / 239,300 / 312,110 | 28,126.85 / 31,112.05 / 22,283.70 |
| TRST | cached | 64 | 15,082,050 / 15,115,890 / 19,088,890 | 15,073,610 / 15,104,200 / 19,098,810 | 30.10 / 30.03 / 23.78 |
| TRST | cached | 512 | 8,577,330 / 8,564,290 / 11,039,160 | 8,582,780 / 8,566,410 / 11,041,600 | 211.72 / 212.04 / 164.51 |
| TRST | cached | 4,096 | 7,976,260 / 7,955,900 / 9,661,700 | 7,976,550 / 7,952,880 / 9,662,470 | 910.71 / 913.04 / 751.84 |
| TRST | cached | 65,536 | 251,400 / 220,030 / 325,920 | 264,930 / 220,040 / 312,110 | 28,894.36 / 33,013.87 / 22,287.80 |
| TRST | bypass | 64 | 15,076,250 / 15,103,980 / 19,088,710 | 15,075,600 / 15,106,140 / 19,100,300 | 30.11 / 30.06 / 23.78 |
| TRST | bypass | 512 | 7,083,420 / 7,078,220 / 11,041,270 | 7,089,340 / 7,080,070 / 11,043,460 | 256.37 / 256.56 / 164.47 |
| TRST | bypass | 4,096 | 3,561,110 / 3,545,590 / 9,662,760 | 3,560,890 / 3,545,130 / 9,664,970 | 2,039.82 / 2,048.75 / 751.76 |
| TRST | bypass | 65,536 | 251,380 / 221,280 / 326,940 | 264,910 / 224,610 / 312,060 | 28,896.65 / 32,827.37 / 22,218.27 |

## Fresh startup

Startup begins after loading the handler HUNK and before DOS device registration. The columns use the same arm order as the session table.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 364,190 / 357,330 / 518,280 | 51,660 / 56,710 / 96,690 | 415,850 / 414,040 / 614,990 |
| CSUN | bypass | 364,760 / 362,490 / 518,280 | 51,660 / 52,120 / 96,700 | 416,420 / 414,610 / 614,990 |
| TRST | cached | 345,500 / 349,320 / 518,280 | 51,910 / 52,080 / 96,710 | 397,410 / 401,400 / 614,990 |
| TRST | bypass | 346,120 / 349,900 / 518,280 | 51,910 / 52,080 / 96,710 | 398,030 / 401,980 / 614,990 |

The local run manifests and per-trial JSON are under `artifacts/ab30b/runs` for final Copper and original SFS, and `artifacts/ab30/runs` for regressed Copper. The cached 64 KiB TRST comparison used the following handler SHA-256 digests.

| Handler | SHA-256 |
|---|---|
| Copper before the regression | `4A7C39F161C989CAB455EA0A3ED49107759CBA0251A8A5F5AB4837046A788E0D` |
| Regressed Copper | `2CE6CD6083D8EF2B043882AD7DE412C3371368AFC6FD443425B85D978F6E8A57` |
| Final Copper | `ECB35B1F3FB9CA9B71D7AAA7260E56F48E7FB1DE998C317679ADA1A4C00A29A6` |
| Original SmartFilesystem 1.279 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

## Verification

Both standalone SFS2 handlers passed CopperSharp's compatibility check with zero managed allocation sites. Each handler passed 178 native packet replays across the 68000 and 68020. The new replays exercise an actual reopened handle, a bad extent checksum, staged corrupt metadata, generation invalidation followed by a resized and relocated file, and a cached soft link. CopperStart passed 157 native integration checks. Its cache-page-summary theory skipped for the SFS2 layout and passed both sector sizes when run separately with the classic TRST handler.

The main full-ROM comparison covered 288 timed workloads across the regressed, final and original handlers. The supplemental 15-repetition runs added 180 workloads across the historical, regressed and final TRST handlers. Twelve additional writer/remount boots checked mutation and clean handoff between Copper and original SFS.

The Release solution build passed. The host suite passed 1,307 tests and skipped three external-media cases.
