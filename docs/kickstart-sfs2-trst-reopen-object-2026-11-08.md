# SFS2 file reopen under Kickstart

The cached TRST 64 KiB workload remounts, opens and closes the file once, then times a second DOS open, a 1 MiB read in sixteen packets, and close. The timed reopen takes place before the first read packet. The priming open reads no payload.

## Where the reopen cycles went

The previously reported median rose from 27,530 to 32,130 guest cycles. An independent 15-repetition run reproduced that shift from 27,540 to 32,120. In all three regressed instruction traces the reopen itself made zero device requests. The handler took 5,794–5,796 cycles, up from 5,783. Kickstart ROM execution accounted for 25,397–25,423 cycles in the regressed traces. Inside Copper the largest costs were the object packet dispatcher (about 1,570 cycles), pathname resolution on a cache hit (700), object construction (590) and pathname comparison (418).

Each successful input open also called Exec to allocate a cleared 216-byte private file object. Closing the handle unlinked the object and returned it to Exec. These calls occurred in both earlier builds. Eliminating the calls removes recurring ROM work that a pathname cache hit alone could not avoid.

## Native change

Standalone CopperSFS2 now uses one cleared file record from the mount's existing enumeration storage for a shared input handle. The pathname, root and file record occupy separate fields. Concurrent opens use independently owned records through the existing allocator. If the optional mount storage is absent the same fallback applies. The ordinary ownership list, conflict checks and record-lock cleanup still apply to every handle.

Closing the mount-owned record unlinks it before clearing all 216 bytes for reuse. On generation wrap the handler invalidates the pathname and root keys while preserving any active record. The existing cursor invalidation still visits every open handle. The open dispatcher reads the mount format once and fetches FFS metadata scratch only on the FFS branch. A pathname hit returns before entering the directory-resolution routine.

The most recently opened file is the head of the owned-object list. Read checks its token and type directly. Older handles still go through the bounded list search. After a successful read the handler updates both position words from the position captured before I/O, including the carry across 4 GiB. A caller whose destination overlaps the position fields gets the same writeback.

## Benchmark configuration

The paired runs used Kickstart 3.1, a PAL M68020, synchronous CopperHDF, 512-byte sectors and 64 KiB MaxTransfer. The four workloads read 64 KiB in 64-byte calls, 256 KiB in 512-byte calls and 1 MiB in either 4 KiB or 64 KiB calls. All standard timings are medians of three uninstrumented runs. The cache policy column applies to Copper. Original SmartFilesystem 1.279 used its native caching in each paired run.

## Cached TRST 64 KiB phase timings

All values below are guest bus cycles. Each phase has its own median. The first group uses the standard three-trial probe. The second group pools that same probe across five fresh boots. The third uses a separate guest probe that runs 15 trials per size in one boot. Within each comparison the handler arms have matching guest, ROM, media and output digests.

| Run | Handler | Reopen | Read loop | Close | Whole session |
|---|---|---:|---:|---:|---:|
| Standard | Previous Copper | 32,130 | 220,040 | 10,540 | 262,710 |
| Standard | Final Copper | 26,260 | 215,700 | 10,820 | 257,440 |
| Standard | Original SFS | 45,330 | 317,280 | 14,880 | 377,480 |
| Five boots | Previous Copper | 32,130 | 220,030 | 10,540 | 262,730 |
| Five boots | Final Copper | 26,270 | 215,700 | 10,820 | 257,370 |
| Five boots | Original SFS | 49,920 | 312,170 | 14,880 | 377,480 |
| Long probe | Previous Copper | 27,550 | 224,620 | 10,540 | 262,700 |
| Long probe | Final Copper | 26,270 | 220,290 | 10,820 | 258,620 |
| Long probe | Original SFS | 45,330 | 312,210 | 14,880 | 376,900 |

The standard cached TRST reopen used 18.3% fewer guest cycles than the regressed build and finished below the earlier 27,530-cycle median. The complete session used 2.0% fewer cycles than the immediately preceding build. Across five fresh boots the final session medians ranged from 257,340 to 257,440 cycles. The previous build ranged from 262,700 to 262,790.

The final three reopen traces used 5,786–5,788 cycles inside the handler and 19,694–24,226 in Kickstart ROM. Reopen and close made zero device requests. The read loop took 113,956–114,008 handler cycles, down from 118,282–118,288, with 16 direct-to-caller reads in every trace. Clearing the mount record added 280 guest cycles to median close.

## Four-size A/B under Kickstart

Each triple gives **previous Copper / final Copper / original SFS**. The first session includes first open, the reads and close. The reopened session starts after a priming open and close. Across the three arms, the main matrix covers 288 timed workloads.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 129,070 / 128,480 / 208,600 | 56,740 / 51,220 / 66,520 | 27,570 / 26,250 / 45,330 | 29.93 / 30.56 / 23.68 | 29.97 / 30.62 / 23.71 |
| CSUN | cached | 512 | 128,880 / 128,380 / 198,090 | 52,170 / 51,220 / 66,520 | 27,560 / 26,250 / 45,320 | 209.49 / 213.50 / 163.22 | 210.15 / 214.14 / 163.57 |
| CSUN | cached | 4,096 | 128,870 / 128,400 / 212,960 | 52,170 / 51,220 / 71,090 | 27,570 / 26,250 / 45,330 | 902.40 / 912.02 / 745.06 | 906.85 / 915.71 / 747.05 |
| CSUN | cached | 65,536 | 128,930 / 129,610 / 193,610 | 56,750 / 51,220 / 71,170 | 27,560 / 26,250 / 49,910 | 25,114.23 / 25,703.41 / 17,570.61 | 27,589.51 / 26,746.35 / 18,731.41 |
| CSUN | bypass | 64 | 129,060 / 128,490 / 212,870 | 52,160 / 51,230 / 71,100 | 27,560 / 26,250 / 45,330 | 29.93 / 30.57 / 23.67 | 29.98 / 30.57 / 23.69 |
| CSUN | bypass | 512 | 129,060 / 128,520 / 199,600 | 52,170 / 51,220 / 71,100 | 27,560 / 26,250 / 45,330 | 253.59 / 259.26 / 163.25 | 254.30 / 260.27 / 163.57 |
| CSUN | bypass | 4,096 | 130,110 / 128,490 / 211,450 | 56,750 / 51,220 / 71,100 | 27,560 / 26,250 / 45,320 | 2,005.95 / 2,051.80 / 745.28 | 2,018.25 / 2,066.28 / 746.92 |
| CSUN | bypass | 65,536 | 128,900 / 128,440 / 193,580 | 52,170 / 52,470 / 71,140 | 27,560 / 26,250 / 45,320 | 24,553.95 / 25,592.93 / 17,631.17 | 26,251.46 / 28,196.73 / 19,334.17 |
| TRST | cached | 64 | 129,250 / 128,760 / 212,890 | 52,150 / 51,460 / 71,100 | 27,550 / 26,260 / 45,330 | 29.92 / 30.55 / 23.66 | 29.98 / 30.59 / 23.69 |
| TRST | cached | 512 | 129,220 / 130,030 / 194,990 | 52,150 / 51,460 / 66,520 | 27,540 / 26,270 / 45,330 | 210.36 / 213.91 / 163.25 | 211.05 / 214.56 / 163.63 |
| TRST | cached | 4,096 | 129,150 / 128,660 / 212,960 | 52,150 / 56,020 / 71,080 | 27,550 / 26,260 / 45,320 | 904.29 / 913.16 / 745.14 | 908.89 / 915.22 / 746.69 |
| TRST | cached | 65,536 | 129,130 / 128,670 / 198,220 | 52,150 / 51,460 / 71,080 | 32,130 / 26,260 / 45,330 | 25,176.91 / 25,586.62 / 17,575.28 | 27,650.42 / 28,216.44 / 19,243.51 |
| TRST | bypass | 64 | 129,230 / 128,710 / 208,600 | 52,150 / 51,460 / 66,520 | 27,550 / 26,270 / 45,330 | 29.92 / 30.54 / 23.68 | 29.98 / 30.59 / 23.70 |
| TRST | bypass | 512 | 129,130 / 130,030 / 198,090 | 52,150 / 51,460 / 66,520 | 27,540 / 26,270 / 45,320 | 254.20 / 259.50 / 163.22 | 255.12 / 260.58 / 163.60 |
| TRST | bypass | 4,096 | 129,170 / 129,980 / 212,960 | 56,740 / 52,790 / 71,090 | 27,550 / 26,270 / 45,330 | 2,010.75 / 2,051.70 / 745.06 | 2,027.25 / 2,068.52 / 746.70 |
| TRST | bypass | 65,536 | 129,260 / 128,700 / 193,610 | 52,150 / 51,460 / 71,170 | 27,550 / 26,270 / 45,320 | 24,046.75 / 25,591.13 / 17,570.61 | 27,651.47 / 28,227.41 / 19,328.51 |

Every final 64-byte workload fetched its 64 KiB stream in one device request. Each of the 96 final timed remounts used two requests totaling 4,608 bytes, followed by a first open with zero requests. The cached TRST 4 KiB remount plus first open fell from 129,150 to 128,660 cycles, although the isolated first-open median rose from 52,150 to 56,020. Cached TRST fresh startup plus first open fell from 401,310 to 399,630 cycles.

All 32 final session medians exceeded original SFS. Cached CSUN's 64 KiB reopened session declined from 27,589.51 to 26,746.35 KiB/s and remained above the original's 18,731.41 KiB/s.

## Read loop

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 15,101,280 / 14,789,100 / 19,091,020 | 15,108,010 / 14,791,910 / 19,087,480 | 30.06 / 30.70 / 23.78 |
| CSUN | cached | 512 | 8,606,050 / 8,443,920 / 11,041,370 | 8,603,240 / 8,443,240 / 11,042,190 | 211.02 / 215.07 / 164.47 |
| CSUN | cached | 4,096 | 7,987,000 / 7,902,780 / 9,663,610 | 7,972,090 / 7,895,230 / 9,662,210 | 909.48 / 919.18 / 751.69 |
| CSUN | cached | 65,536 | 226,450 / 220,580 / 327,360 | 225,160 / 234,540 / 312,130 | 32,077.90 / 32,931.55 / 22,189.76 |
| CSUN | bypass | 64 | 15,104,920 / 14,785,780 / 19,085,290 | 15,106,020 / 14,785,960 / 19,102,130 | 30.06 / 30.71 / 23.79 |
| CSUN | bypass | 512 | 7,098,350 / 6,932,400 / 11,037,830 | 7,103,210 / 6,937,030 / 11,042,300 | 255.84 / 261.96 / 164.53 |
| CSUN | bypass | 4,096 | 3,555,260 / 3,473,700 / 9,663,330 | 3,556,450 / 3,473,890 / 9,663,700 | 2,043.18 / 2,091.15 / 751.71 |
| CSUN | bypass | 65,536 | 233,100 / 220,460 / 325,980 | 238,580 / 220,480 / 312,130 | 31,162.77 / 32,949.47 / 22,283.70 |
| TRST | cached | 64 | 15,113,380 / 14,797,430 / 19,085,180 | 15,104,290 / 14,800,750 / 19,101,310 | 30.04 / 30.68 / 23.79 |
| TRST | cached | 512 | 8,569,980 / 8,423,110 / 11,041,610 | 8,566,450 / 8,426,860 / 11,038,400 | 211.90 / 215.60 / 164.47 |
| TRST | cached | 4,096 | 7,955,620 / 7,881,860 / 9,662,570 | 7,953,070 / 7,899,810 / 9,663,580 | 913.07 / 921.62 / 751.77 |
| TRST | cached | 65,536 | 225,830 / 221,620 / 327,330 | 220,040 / 215,700 / 317,280 | 32,165.97 / 32,777.01 / 22,191.80 |
| TRST | bypass | 64 | 15,104,270 / 14,799,400 / 19,091,020 | 15,106,370 / 14,786,280 / 19,098,950 | 30.06 / 30.68 / 23.78 |
| TRST | bypass | 512 | 7,079,020 / 6,935,040 / 11,041,370 | 7,080,110 / 6,931,410 / 11,036,920 | 256.53 / 261.86 / 164.47 |
| TRST | bypass | 4,096 | 3,545,320 / 3,473,520 / 9,663,610 | 3,545,110 / 3,474,620 / 9,663,710 | 2,048.91 / 2,091.26 / 751.69 |
| TRST | bypass | 65,536 | 225,830 / 221,550 / 327,360 | 224,620 / 216,900 / 312,140 | 32,165.97 / 32,787.37 / 22,189.76 |

## Fresh startup

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 357,480 / 362,210 / 518,280 | 56,700 / 51,160 / 96,710 | 414,180 / 413,370 / 614,990 |
| CSUN | bypass | 362,490 / 362,770 / 518,280 | 52,120 / 51,160 / 96,710 | 414,610 / 413,930 / 614,990 |
| TRST | cached | 349,230 / 343,610 / 518,280 | 52,080 / 56,020 / 96,710 | 401,310 / 399,630 / 614,990 |
| TRST | bypass | 349,900 / 344,160 / 518,280 | 52,080 / 56,030 / 96,710 | 401,980 / 400,190 / 614,990 |

## Fifteen trials per read size

The longer probe measured all four sizes with 15 repetitions in each of six TRST boots. Throughput includes reopen, read and close. Every 64 KiB loop transferred 1 MiB in sixteen requests.

| Copper policy | Read size | Reopen cycles | Session KiB/s |
|---|---:|---:|---:|
| cached | 64 | 32,130 / 26,260 / 45,330 | 29.98 / 30.62 / 23.69 |
| cached | 512 | 27,550 / 26,260 / 45,330 | 211.05 / 214.53 / 163.56 |
| cached | 4,096 | 27,550 / 26,270 / 45,330 | 908.24 / 916.77 / 747.02 |
| cached | 65,536 | 27,550 / 26,270 / 45,330 | 27,651.47 / 28,087.70 / 19,273.13 |
| bypass | 64 | 27,550 / 26,260 / 45,330 | 29.98 / 30.62 / 23.69 |
| bypass | 512 | 27,550 / 26,260 / 45,330 | 255.11 / 260.48 / 163.55 |
| bypass | 4,096 | 27,550 / 26,270 / 45,330 | 2,026.30 / 2,067.78 / 746.80 |
| bypass | 65,536 | 27,550 / 26,260 / 45,330 | 27,642.00 / 28,090.96 / 19,268.01 |

## Run identity and verification

The raw manifests, phase profiles and per-trial results are in the local benchmark artifacts. The previous-build controls are under `artifacts/ab31/runs`, original SFS controls under `artifacts/ab31b/runs` and final Copper runs under `artifacts/ab31f`.

| TRST handler | SHA-256 |
|---|---|
| Previous Copper | `ECB35B1F3FB9CA9B71D7AAA7260E56F48E7FB1DE998C317679ADA1A4C00A29A6` |
| Final Copper | `1896E94988D2B1D83AA962C7DEC1A8B1984F740A38729DA313B345B7293B8769` |
| Original SFS | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

Both standalone SFS2 handlers passed CopperSharp's native compatibility check with zero managed allocation sites. Each passed 188 native packet replays across the 68000 and 68020. The new replay reads from two open handles, wraps the cache generation with the mount-owned handle active, and checks that the next reopen receives cleared state. It counts zero Exec allocations and frees for the mount-owned handle and exactly one matched allocation and free for the overlapping handle. Existing replays cover missing optional mount storage, record reservations, staged writes, media invalidation, buffer aliases, and position and EOF above 4 GiB.

CopperStart passed 157 native integration checks. Its cache-page-summary theory skipped for the SFS2 layout and passed both sector sizes with the classic TRST handler.

The full-ROM mutation tests passed 12 writer/remount boots across Copper CSUN, Copper TRST and original SFS. They verified file contents after writes and clean handoffs between the handlers.

The Release solution built with zero warnings and zero errors. The host suite passed 1,307 tests and skipped three external-media cases.
