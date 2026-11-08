# Cached CSUN first open under Kickstart

The timed first open follows an inhibit/uninhibit remount and precedes any payload read. The 64 KiB label describes the size of the subsequent read packets. The acceptance measurement uses the unchanged three-trial Kickstart probe. A separate warm run times reopen after an untimed open and close.

## Profile

The earlier three-trial cached CSUN first-open median was 51,220 guest bus cycles. The regressed build measured 55,840 in the matched rerun. Historical instruction traces used 30,736–30,738 cycles inside the handler. The regressed traces used 30,747–30,755. Historical ROM costs were 24,117, 19,694 and 19,690 cycles. The regressed trials used 24,176, 38,519 and 24,169 in ROM. All three regressed first opens issued zero device reads.

The largest measured Copper costs included pathname resolution, two metadata checksums, reading the canonical object and validating its size. The C# lookup now compares identical bytes directly and folds only differing bytes on a case-insensitive volume. A leaf node uses its slot offset directly. Only an internal node needs division by its stride or decoding of a packed child pointer. The 512-byte size check uses a wide shift for the volume's byte capacity. Other block sizes retain the wide multiplication.

The object-container scan computes the name offset and string boundary once per block. It reuses them while validating the terminators, name and comment limits, record alignment and the root trailer. A directory match still goes through the node index to find the canonical record. All temporary state belongs to the current lookup.

## Kickstart configuration

The A/B compares previous Copper, final Copper and original SmartFilesystem 1.279 under Kickstart 3.1 on a PAL 68020. The device is synchronous CopperHDF with 512-byte sectors and 64 KiB MaxTransfer. Workloads read 64 KiB in 64-byte calls, 256 KiB in 512-byte calls and 1 MiB in either 4 KiB or 64 KiB calls. Original SFS uses its own cache policy in each pair. The timing tables use uninstrumented guest bus cycles. The ROM, guest probe, disk, result and handler identities are checked across the corresponding runs.

## Cached CSUN 64 KiB phase timings

Columns show independent guest-cycle medians. The combined column measures remount plus first open in the same trial. The session includes first open, sixteen reads and close. The five-boot comparison pools 15 timings from five boots of the unchanged standard probe. The extended probe runs 15 trials per size in one boot.

| Run | Handler | Remount | First open | Remount + first open | Read loop | Close | Session |
|---|---|---:|---:|---:|---:|---:|---:|
| Three trials | Previous Copper | 72,880 | 55,840 | 128,830 | 208,250 | 10,690 | 274,800 |
| Three trials | Final Copper | 77,050 | 47,690 | 124,850 | 212,690 | 10,700 | 271,080 |
| Three trials | Original SFS | 126,890 | 71,100 | 198,100 | 312,190 | 14,880 | 413,390 |
| Five boots | Previous Copper | 72,880 | 55,810 | 128,830 | 208,230 | 10,690 | 274,740 |
| Five boots | Final Copper | 77,050 | 47,690 | 124,850 | 212,720 | 10,700 | 271,110 |
| Five boots | Original SFS | 126,900 | 71,080 | 193,690 | 325,970 | 14,880 | 412,020 |
| 15 trials | Previous Copper | 74,200 | 55,800 | 130,040 | 211,540 | 10,690 | 274,730 |
| 15 trials | Final Copper | 77,060 | 47,690 | 124,900 | 212,650 | 10,700 | 271,060 |
| 15 trials | Original SFS | 126,930 | 71,090 | 193,710 | 316,680 | 14,880 | 405,230 |

All three final standard trials took 47,690 cycles to open. This is 14.6% below the matched regressed median and 3,530 cycles below the earlier 51,220-cycle median. Remount alone rose from 72,880 to 77,050 cycles while the combined interval fell from 128,830 to 124,850. The cold open/read/close session also improved, from 274,800 to 271,080 cycles.

Each final open trace used 27,210 cycles inside Copper, about 3,540 fewer than before, and 19,690–19,692 cycles in ROM. In the first trace, file-size validation fell from 1,726 to 662 handler cycles, pathname resolution from 4,505 to 3,846 and the object scan from 675 to 545. The block checksums still used 3,614 cycles. Original SFS used 39,578 handler cycles in each of its three diagnostic first opens. Both handlers issued zero first-open device reads.

## Four-size A/B

Each triple gives **previous Copper / final Copper / original SFS**. Cold sessions measure first open after remount. Warm sessions include the timed reopen after a priming open and close. Each session rate includes open, the full read loop and close. The standard matrix covers 288 timed workloads.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 128,820 / 124,990 / 208,470 | 55,800 / 47,690 / 71,100 | 26,130 / 26,160 / 45,320 | 30.44 / 30.46 / 23.68 | 30.50 / 30.51 / 23.69 |
| CSUN | cached | 512 | 128,800 / 124,980 / 198,110 | 55,810 / 47,690 / 71,100 | 26,130 / 27,460 / 45,320 | 214.65 / 214.79 / 163.21 | 215.40 / 215.18 / 163.55 |
| CSUN | cached | 4,096 | 128,810 / 124,860 / 198,300 | 51,230 / 52,270 / 71,110 | 26,130 / 26,160 / 49,900 | 915.23 / 914.60 / 744.96 | 917.39 / 916.38 / 746.57 |
| CSUN | cached | 65,536 | 128,830 / 124,850 / 198,100 | 55,840 / 47,690 / 71,100 | 26,130 / 26,160 / 45,320 | 26,433.92 / 26,796.67 / 17,571.88 | 29,244.50 / 29,258.63 / 18,784.21 |
| CSUN | bypass | 64 | 128,830 / 124,900 / 212,880 | 51,230 / 47,690 / 71,090 | 26,130 / 26,150 / 45,330 | 30.44 / 30.47 / 23.67 | 30.52 / 30.51 / 23.71 |
| CSUN | bypass | 512 | 128,790 / 124,850 / 194,820 | 55,790 / 47,690 / 71,120 | 26,130 / 26,160 / 45,320 | 260.77 / 260.74 / 163.25 | 261.60 / 261.54 / 163.57 |
| CSUN | bypass | 4,096 | 128,800 / 124,930 / 212,830 | 51,230 / 47,690 / 71,110 | 26,130 / 26,150 / 45,330 | 2,062.55 / 2,063.16 / 745.20 | 2,076.95 / 2,078.11 / 747.05 |
| CSUN | bypass | 65,536 | 128,850 / 124,860 / 193,610 | 51,230 / 47,690 / 71,140 | 26,130 / 26,160 / 51,200 | 26,439.69 / 26,796.67 / 17,631.17 | 29,248.03 / 29,803.64 / 18,523.63 |
| TRST | cached | 64 | 129,490 / 124,560 / 212,890 | 51,110 / 47,590 / 71,100 | 26,140 / 30,750 / 45,330 | 30.55 / 30.52 / 23.67 | 30.59 / 30.56 / 23.71 |
| TRST | cached | 512 | 129,430 / 124,740 / 194,930 | 51,110 / 47,590 / 66,520 | 26,140 / 26,170 / 45,320 | 214.81 / 214.51 / 163.26 | 215.28 / 215.15 / 163.55 |
| TRST | cached | 4,096 | 128,270 / 124,570 / 212,830 | 51,110 / 47,590 / 71,110 | 26,140 / 26,170 / 45,320 | 913.63 / 914.39 / 745.19 | 918.66 / 916.26 / 746.93 |
| TRST | cached | 65,536 | 128,360 / 124,580 / 193,630 | 55,690 / 47,590 / 71,100 | 26,140 / 26,170 / 49,910 | 26,583.86 / 26,779.87 / 17,629.46 | 29,259.81 / 29,216.27 / 18,733.83 |
| TRST | bypass | 64 | 129,590 / 124,560 / 212,890 | 51,110 / 47,590 / 71,100 | 26,150 / 26,180 / 45,330 | 30.54 / 30.52 / 23.67 | 30.59 / 30.56 / 23.69 |
| TRST | bypass | 512 | 129,550 / 124,730 / 194,920 | 51,110 / 47,590 / 66,520 | 26,150 / 26,180 / 45,330 | 260.73 / 260.49 / 163.26 | 261.70 / 261.36 / 163.62 |
| TRST | bypass | 4,096 | 128,450 / 125,850 / 211,450 | 51,110 / 48,830 / 71,100 | 26,150 / 26,170 / 45,330 | 2,059.90 / 2,061.24 / 745.05 | 2,078.83 / 2,073.59 / 746.73 |
| TRST | bypass | 65,536 | 128,240 / 124,600 / 193,610 | 55,690 / 47,590 / 71,120 | 26,140 / 26,170 / 45,330 | 26,583.86 / 26,901.86 / 17,632.02 | 29,256.28 / 29,208.05 / 19,334.68 |

At 64 KiB, cached CSUN reopen changed from 26,130 to 26,160 cycles and the complete reopened session fell from 248,390 to 248,270. The CSUN bypass session fell from 248,360 to 243,730. Cached TRST remount plus first open at 4 KiB fell from 128,270 to 124,570. Cached TRST fresh startup plus first open fell from 399,180 to 396,030 cycles.

The three-trial TRST reopened session at 64 KiB rose from 248,260 to 248,630 cycles cached and from 248,290 to 248,700 with bypass. A separate 15-trial warm run measured 248,740 against 249,490 previously cached, and 248,660 against 249,520 previously with bypass. In cached CSUN, isolated first open for the 4 KiB workload rose from 51,230 to 52,270 cycles, and isolated reopen at 512 bytes rose from 26,130 to 27,460. Both combined remount-plus-first-open medians improved. On fresh cached CSUN startup, the isolated first open rose from 51,160 to 52,310 cycles and the combined startup plus first open fell from 401,010 to 398,080.

## Read-loop costs

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 14,848,240 / 14,844,520 / 19,092,060 | 14,850,710 / 14,842,950 / 19,100,300 | 30.58 / 30.58 / 23.78 |
| CSUN | cached | 512 | 8,393,450 / 8,396,320 / 11,040,550 | 8,394,250 / 8,397,800 / 11,043,460 | 216.36 / 216.29 / 164.49 |
| CSUN | cached | 4,096 | 7,874,920 / 7,883,950 / 9,664,920 | 7,881,330 / 7,890,020 / 9,664,970 | 922.43 / 921.37 / 751.59 |
| CSUN | cached | 65,536 | 208,250 / 212,690 / 312,190 | 207,010 / 211,390 / 312,060 | 34,881.35 / 34,153.19 / 23,268.01 |
| CSUN | bypass | 64 | 14,852,960 / 14,837,180 / 19,078,850 | 14,838,830 / 14,843,640 / 19,088,030 | 30.57 / 30.60 / 23.80 |
| CSUN | bypass | 512 | 6,899,430 / 6,906,330 / 11,041,570 | 6,900,530 / 6,903,680 / 11,041,100 | 263.21 / 262.95 / 164.47 |
| CSUN | bypass | 4,096 | 3,455,950 / 3,461,560 / 9,662,750 | 3,457,240 / 3,458,670 / 9,663,030 | 2,101.89 / 2,098.49 / 751.76 |
| CSUN | bypass | 65,536 | 212,790 / 212,670 / 325,980 | 211,550 / 206,880 / 312,240 | 34,137.14 / 34,156.40 / 22,283.70 |
| TRST | cached | 64 | 14,800,240 / 14,818,190 / 19,088,420 | 14,806,440 / 14,820,480 / 19,086,620 | 30.68 / 30.64 / 23.78 |
| TRST | cached | 512 | 8,392,400 / 8,400,610 / 11,040,730 | 8,398,610 / 8,401,570 / 11,043,320 | 216.39 / 216.18 / 164.48 |
| TRST | cached | 4,096 | 7,878,690 / 7,885,840 / 9,661,780 | 7,870,370 / 7,891,010 / 9,663,300 | 921.99 / 921.15 / 751.83 |
| TRST | cached | 65,536 | 211,440 / 212,940 / 325,990 | 211,410 / 211,700 / 312,180 | 34,355.09 / 34,113.09 / 22,283.02 |
| TRST | bypass | 64 | 14,805,170 / 14,813,920 / 19,085,460 | 14,802,740 / 14,819,600 / 19,100,430 | 30.67 / 30.65 / 23.79 |
| TRST | bypass | 512 | 6,902,630 / 6,913,260 / 11,042,060 | 6,899,720 / 6,911,320 / 11,036,840 | 263.09 / 262.69 / 164.46 |
| TRST | bypass | 4,096 | 3,458,250 / 3,464,570 / 9,663,760 | 3,457,440 / 3,461,660 / 9,663,460 | 2,100.50 / 2,096.66 / 751.68 |
| TRST | bypass | 65,536 | 206,860 / 211,710 / 325,940 | 208,080 / 208,380 / 312,130 | 35,115.74 / 34,311.28 / 22,286.44 |

## Fresh startup

Startup begins after loading the handler HUNK and before registering the DOS device.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 349,850 / 345,770 / 518,280 | 51,160 / 52,310 / 96,710 | 401,010 / 398,080 / 614,990 |
| CSUN | bypass | 350,430 / 346,370 / 518,280 | 51,160 / 52,210 / 96,710 | 401,590 / 398,580 / 614,990 |
| TRST | cached | 343,570 / 347,270 / 518,280 | 55,610 / 48,760 / 96,710 | 399,180 / 396,030 / 614,990 |
| TRST | bypass | 344,130 / 347,860 / 518,410 | 55,620 / 48,760 / 96,690 | 399,750 / 396,620 / 615,100 |

## Run identity

The raw manifests, traces and trial measurements are in the local benchmark artifacts. The controls are in `artifacts/ab34` and final Copper measurements are in `artifacts/ab34e`.

| CSUN handler | SHA-256 |
|---|---|
| Previous Copper | `98766BEF80C867C131F8C1564309D55395F9DE425D54D07CAB8B109DE53EC507` |
| Final Copper | `D52BFAA87F6E2362DDAD2DC9F5554E164ADD3605290693E8FAC330BB036992B3` |
| Original SFS | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

## Verification

The SFS2 CSUN and TRST compiler reports each recorded zero managed allocation sites. The changed lookup keeps all state within one operation and uses the existing mount, pathname and cursor invalidation rules. The focused regression tests use 512-byte and 1,024-byte blocks in both classic SFS and SFS2, traverse a flagged node-tree branch, verify case-sensitive and Latin-1 case-insensitive names, and reject a matching directory entry whose indexed leaf points to the wrong container.

Both final handlers passed 204 native packet replays across the 68000 and 68020. CopperStart passed 157 native integration checks, with one cache-page-summary theory skipped for the SFS2 layout. Both sector sizes for that theory passed against the classic TRST handler. The full-ROM writer/remount tests passed 12 boots with clean handoffs between Copper and original SFS.

All 96 final timed remounts issued two requests totaling 4,608 bytes and the following first opens issued zero. The 64-byte stream retained its single 64 KiB read-ahead request. All profiled 1 MiB loops using 64 KiB packets made sixteen 65,536-byte transfers directly to the caller, under both policies and both Copper variants.

The Release solution build passed. The host suite passed 1,311 tests and skipped three external-media cases.
