# SFS2 TRST 64 KiB sessions under Kickstart

Reading 1 MiB in sixteen 64 KiB DOS packets after remount took 331,970 guest cycles with payload caching and 327,350 with bypass, including open and close. The regressed build took 347,710 cycles under both policies. The pre-regression medians were 336,090 and 339,910 respectively. Original SmartFilesystem 1.279 took 411,980 and 413,050 in the paired runs.

## Where the cycles went

The 64 KiB read-loop traces put the historical handler near 162,800 cycles and the regressed handler near 163,100. The larger change in whole-session timing came from time outside the handler landing in different phases and repetitions. In the regressed cached run, open took 51,850, 51,850 and 71,190 guest cycles while its handler code took 30,115, 30,115 and 30,120. With bypass, close took 10,440, 29,710 and 10,440 guest cycles while the handler used 2,597 in each. The long close included 26,211 cycles in Kickstart ROM. These measurements locate the extra work without identifying a particular interrupt source.

In the original C# path the standalone packet dispatcher forwarded the arguments through the extension and SFS2 bridge. The first read copied metadata into scratch and built a directory entry after the node scan had checked its strings. The final dispatcher calls the SFS2 reader directly after handle, mount and recovery checks. Cursor initialization consumes the validated root and object record and reads the file flags, size and first extent while the view is valid. It retains the object-type and size errors. Large reads resolve overlays and RAM storage before going to the device. A DMA mask that imposes only start alignment can finish its check once alignment and range overflow have been checked.

In the cached first-trial trace, copying took 7,341 handler cycles before and 2,453 after. The previous extension and bridge wrappers used another 11,152 cycles together. The new dispatcher's own time is included in the totals below. Across three trials, read-loop handler work fell from 163,105–163,134 cycles to 144,431–144,445 under the two policies. All 18 historical, regressed and final TRST traces show sixteen 64 KiB transfers directly into the caller's buffer, with zero device requests during open and close.

## Timed A/B

The arms used the same C# guest probe, Kickstart 3.1 ROM, PAL M68020, synchronous CopperHDF, 512-byte sectors and 64 KiB MaxTransfer. Timings are medians of three uninstrumented runs, and the result digests matched. The four workloads read 64 KiB in 64-byte calls, 256 KiB in 512-byte calls and 1 MiB in either 4 KiB or 64 KiB calls. First open follows remount. Reopen follows a priming open and close.

Each triple gives **regressed Copper / final Copper / original SFS**. Original SFS uses its own cache policy. Session throughput covers the timed open, read and close.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 145,660 / 146,100 / 208,470 | 51,500 / 51,840 / 71,100 | 27,620 / 27,800 / 45,330 | 28.94 / 29.86 / 23.68 | 29.01 / 29.91 / 23.69 |
| CSUN | cached | 512 | 145,730 / 147,420 / 198,110 | 51,500 / 56,540 / 71,100 | 27,620 / 27,800 / 45,330 | 203.60 / 210.02 / 163.21 | 204.17 / 210.59 / 163.59 |
| CSUN | cached | 4,096 | 147,050 / 150,630 / 198,300 | 56,090 / 56,420 / 71,110 | 27,630 / 27,800 / 45,330 | 886.16 / 902.22 / 744.96 | 888.66 / 906.50 / 747.11 |
| CSUN | cached | 65,536 | 145,770 / 147,350 / 198,100 | 51,500 / 51,840 / 71,100 | 27,620 / 27,800 / 49,920 | 21,408.90 / 23,164.87 / 17,571.88 | 23,649.05 / 25,083.88 / 19,333.14 |
| CSUN | bypass | 64 | 146,890 / 146,100 / 213,040 | 51,500 / 51,840 / 71,130 | 27,620 / 27,790 / 45,330 | 28.92 / 29.87 / 23.67 | 29.01 / 29.91 / 23.70 |
| CSUN | bypass | 512 | 145,750 / 147,410 / 194,770 | 51,500 / 51,840 / 66,520 | 27,620 / 27,800 / 45,320 | 243.45 / 254.26 / 163.25 | 244.70 / 255.09 / 163.56 |
| CSUN | bypass | 4,096 | 145,800 / 146,090 / 212,950 | 51,500 / 51,840 / 71,200 | 27,620 / 27,790 / 45,320 | 1,920.38 / 2,002.89 / 745.20 | 1,935.20 / 2,017.44 / 746.83 |
| CSUN | bypass | 65,536 | 145,760 / 147,320 / 193,590 | 51,500 / 53,070 / 71,080 | 27,620 / 27,800 / 45,330 | 21,644.29 / 22,577.36 / 17,634.59 | 22,478.16 / 25,083.88 / 19,327.48 |
| TRST | cached | 64 | 146,100 / 146,360 / 212,890 | 56,390 / 52,040 / 66,520 | 27,680 / 27,740 / 45,330 | 28.90 / 29.94 / 23.67 | 28.98 / 29.98 / 23.69 |
| TRST | cached | 512 | 147,390 / 146,400 / 198,120 | 51,850 / 52,040 / 71,100 | 27,680 / 29,070 / 45,320 | 203.14 / 209.69 / 163.26 | 203.98 / 210.34 / 163.57 |
| TRST | cached | 4,096 | 146,170 / 161,110 / 211,450 | 51,850 / 52,040 / 71,100 | 27,680 / 27,750 / 49,950 | 886.12 / 902.88 / 745.28 | 888.64 / 904.87 / 746.92 |
| TRST | cached | 65,536 | 147,570 / 146,370 / 193,610 | 51,850 / 56,620 / 71,160 | 27,680 / 27,750 / 45,330 | 20,891.09 / 21,881.62 / 17,632.02 | 22,451.06 / 24,531.56 / 19,332.11 |
| TRST | bypass | 64 | 146,180 / 146,370 / 212,890 | 53,090 / 56,630 / 71,100 | 27,680 / 27,750 / 45,330 | 28.91 / 29.93 / 23.67 | 28.94 / 29.96 / 23.69 |
| TRST | bypass | 512 | 147,350 / 147,610 / 194,910 | 56,510 / 52,040 / 66,520 | 29,000 / 27,750 / 45,330 | 243.07 / 252.99 / 163.27 | 243.77 / 254.35 / 163.53 |
| TRST | bypass | 4,096 | 146,130 / 146,420 / 211,460 | 51,850 / 52,040 / 71,110 | 27,680 / 27,750 / 49,930 | 1,911.02 / 1,996.94 / 745.03 | 1,932.40 / 2,009.70 / 746.80 |
| TRST | bypass | 65,536 | 146,130 / 147,710 / 193,580 | 51,850 / 52,040 / 71,120 | 27,680 / 27,740 / 45,330 | 20,891.09 / 22,190.44 / 17,586.35 | 23,520.40 / 23,905.09 / 18,783.72 |

All 32 session medians exceeded both comparison arms. Under cached TRST at 4 KiB, remount plus first open rose from 146,170 to 161,110 cycles, versus 211,450 for original SFS. That interval is timed separately from session throughput. Across all 96 final remount trials, mount used two requests totaling 4,608 bytes and first open used none.

## Read loop

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 15,626,480 / 15,139,800 / 19,092,060 | 15,611,380 / 15,137,760 / 19,099,560 | 29.05 / 29.99 / 23.78 |
| CSUN | cached | 512 | 8,854,080 / 8,578,260 / 11,040,550 | 8,856,760 / 8,580,300 / 11,040,730 | 205.10 / 211.70 / 164.49 |
| CSUN | cached | 4,096 | 8,123,150 / 7,970,900 / 9,664,920 | 8,136,080 / 7,974,980 / 9,661,380 | 894.24 / 911.32 / 751.59 |
| CSUN | cached | 65,536 | 277,380 / 250,110 / 312,190 | 269,120 / 251,260 / 312,220 | 26,188.05 / 29,043.38 / 23,268.01 |
| CSUN | bypass | 64 | 15,618,160 / 15,135,850 / 19,087,040 | 15,612,090 / 15,141,030 / 19,096,770 | 29.07 / 30.00 / 23.79 |
| CSUN | bypass | 512 | 7,397,600 / 7,076,200 / 11,043,030 | 7,383,200 / 7,080,870 / 11,042,560 | 245.49 / 256.64 / 164.45 |
| CSUN | bypass | 4,096 | 3,719,680 / 3,559,600 / 9,661,700 | 3,714,750 / 3,562,330 / 9,663,760 | 1,952.87 / 2,040.69 / 751.84 |
| CSUN | bypass | 65,536 | 270,430 / 259,390 / 325,920 | 283,170 / 251,280 / 312,140 | 26,861.08 / 28,004.32 / 22,287.80 |
| TRST | cached | 64 | 15,645,880 / 15,103,200 / 19,088,310 | 15,629,530 / 15,103,100 / 19,099,110 | 29.02 / 30.06 / 23.78 |
| TRST | cached | 512 | 8,877,330 / 8,593,410 / 11,036,270 | 8,862,760 / 8,592,870 / 11,042,390 | 204.57 / 211.33 / 164.55 |
| TRST | cached | 4,096 | 8,118,200 / 7,981,000 / 9,663,760 | 8,131,630 / 7,989,470 / 9,661,730 | 894.78 / 910.17 / 751.68 |
| TRST | cached | 65,536 | 270,700 / 246,180 / 325,940 | 285,430 / 257,860 / 312,110 | 26,834.29 / 29,507.03 / 22,286.44 |
| TRST | bypass | 64 | 15,642,540 / 15,105,440 / 19,088,300 | 15,652,030 / 15,113,660 / 19,103,620 | 29.02 / 30.06 / 23.78 |
| TRST | bypass | 512 | 7,404,320 / 7,107,070 / 11,041,450 | 7,407,020 / 7,096,950 / 11,040,320 | 245.26 / 255.52 / 164.47 |
| TRST | bypass | 4,096 | 3,726,740 / 3,573,230 / 9,662,740 | 3,720,960 / 3,571,670 / 9,664,350 | 1,949.17 / 2,032.91 / 751.76 |
| TRST | bypass | 65,536 | 270,660 / 264,800 / 327,050 | 270,720 / 265,610 / 326,510 | 26,838.25 / 27,432.18 / 22,210.80 |

## Fresh startup

Startup begins after the handler executable has loaded and before DOS device registration. The same arm order applies.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 359,620 / 359,220 / 518,280 | 51,440 / 56,350 / 96,710 | 411,060 / 415,570 / 614,990 |
| CSUN | bypass | 360,250 / 359,780 / 518,280 | 51,430 / 56,360 / 96,710 | 411,680 / 416,140 / 614,990 |
| TRST | cached | 363,220 / 362,710 / 518,280 | 51,790 / 51,970 / 96,710 | 415,010 / 414,680 / 614,990 |
| TRST | bypass | 363,810 / 363,270 / 518,280 | 51,790 / 51,970 / 96,710 | 415,600 / 415,240 / 614,990 |

## Verification

Each final SFS2 binary passed 134 native packet replays across the 68000 and 68020. The first-read replay covered a soft link with a valid size and one with an impossible size, checking the native error, invalid cursor and absence of payload reads. Other replays cover corrupt staged metadata, extent invalidation, scratch and file-record aliases, reads above 4 GiB, EOF, short transfers and MaxTransfer. Both SFS2 compiler reports recorded zero managed allocation sites.

CopperStart passed 157 native integration checks with one classic-only skip. The full-ROM A/B included 288 timed workloads across the three arms. Every final 64-byte workload used one device request for its full 64 KiB stream. All final 64 KiB traces for CSUN and TRST transferred 1 MiB directly in sixteen requests. Twelve additional writer/remount boots verified the contents of files after rename, deletion, truncation and handoff between Copper and original SFS.

The Release solution built. 1,307 host tests passed and 3 external-media tests skipped.
