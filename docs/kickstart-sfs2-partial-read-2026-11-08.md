# SFS2 TRST 64-byte reads under Kickstart

The small-packet workload opens a file after remount, reads 64 KiB through 1,024 consecutive 64-byte DOS reads, and closes the handle. Each Copper payload-cache policy fetched the stream in one 64 KiB request. The earlier regression was measurable in CPU time under both policies.

The historical comparison uses the TRST build from before the cached CSUN session changes. With payload caching, the historical and regressed traces spent 11,696,288 and 11,733,064 cycles inside the handler. With bypass, the corresponding totals were 11,696,283 and 11,732,869. The earlier and later builds executed 1,208,687 and 1,208,685 handler instructions respectively in both profiles. In the cached trace, the general reader accounted for 10,843 extra cycles with the same instruction count. The rest of the difference was spread across several methods. The profiles establish where cycles accumulated without isolating a hardware timing cause.

Both builds entered the general reader and reconstructed the cursor from eleven words in the native handle 1,024 times. The C# adapter now reuses the saved extent for a nonempty packet shorter than 512 bytes that fits in one sector and within EOF on a 512-byte-block volume. It borrows a logical sector already held by the platform or reads it into the existing scratch block, then copies just the requested bytes. It checks that the destination is mapped and separate from the private handle and scratch. A cache-image alias uses scratch. A read error invalidates the cursor. Sector crossings, EOF tails and extent changes continue through the general reader.

The sub-sector check runs after the aligned read path. SFS2 mount also initializes its undo staging buffer by writing the empty count and capacity. Each append writes its key and full block image before incrementing that count, so mount can omit clearing the unused payload of 32 slots. This uses the existing mount allocation.

The A/B uses Kickstart 3.1, a PAL M68020, synchronous CopperHDF, a 64 KiB MaxTransfer and original SmartFilesystem 1.279. The 64 B, 512 B, 4 KiB and 64 KiB workloads read 64 KiB, 256 KiB, 1 MiB and 1 MiB respectively. Each cell is the median of three uninstrumented trials with matching ROM, probe, media and output digests. The separate CPU traces record exclusive instruction cycles. First open follows remount. Reopen follows an untimed open and close.

## TRST 64-byte profile

| Payload cache | General-reader entries, before → after | Handler cycles, before → after | Whole guest loop cycles, before → after | Device reads, Copper / original |
|---|---:|---:|---:|---:|
| Cached | 1,024 → 1 | 11,733,064 → 8,769,004 | 18,791,850 → 15,669,010 | 1 / 8 |
| Bypass | 1,024 → 1 | 11,732,869 → 8,768,942 | 18,791,310 → 15,668,560 | 1 / 8 |

## Timed Kickstart A/B

Each timing triple gives **previous Copper / final Copper / original SFS**. Original SFS uses its native cache policy. Session throughput includes DOS open, all reads and close. On TRST, a 64-byte session after first open improved from 24.11 KiB/s to 28.90 cached and 28.91 with bypass. All 32 final session medians beat original SFS across CSUN, TRST, the two cache policies, four sizes and first open or reopen.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 146,360 / 145,660 / 212,890 | 52,000 / 51,500 / 71,100 | 28,930 / 27,620 / 45,320 | 24.21 / 28.94 / 23.67 | 24.25 / 29.01 / 23.69 |
| CSUN | cached | 512 | 146,330 / 145,710 / 194,850 | 52,000 / 51,500 / 71,110 | 27,690 / 27,620 / 45,330 | 203.31 / 203.61 / 163.28 | 203.51 / 204.16 / 163.63 |
| CSUN | cached | 4,096 | 146,350 / 146,970 / 213,030 | 52,000 / 56,080 / 71,140 | 27,690 / 27,620 / 45,320 | 886.08 / 886.18 / 745.11 | 888.29 / 888.73 / 746.69 |
| CSUN | cached | 65,536 | 146,370 / 145,710 / 198,260 | 52,000 / 51,500 / 71,100 | 27,690 / 27,620 / 45,330 | 21,541.59 / 21,409.53 / 17,569.76 | 23,217.44 / 23,648.28 / 19,331.60 |
| CSUN | bypass | 64 | 146,360 / 146,920 / 212,890 | 56,580 / 51,500 / 71,100 | 27,690 / 27,620 / 45,330 | 24.21 / 28.92 / 23.66 | 24.24 / 29.01 / 23.70 |
| CSUN | bypass | 512 | 146,430 / 145,760 / 194,400 | 56,650 / 51,500 / 66,520 | 32,270 / 27,620 / 45,330 | 242.95 / 243.50 / 163.23 | 243.91 / 244.78 / 163.56 |
| CSUN | bypass | 4,096 | 147,580 / 150,230 / 212,980 | 52,000 / 56,050 / 71,120 | 27,690 / 27,620 / 45,330 | 1,918.20 / 1,919.52 / 744.20 | 1,923.11 / 1,935.13 / 746.84 |
| CSUN | bypass | 65,536 | 146,390 / 145,760 / 193,580 | 52,000 / 51,500 / 71,080 | 27,690 / 27,620 / 45,330 | 21,919.25 / 21,646.87 / 17,572.31 | 23,563.89 / 22,564.04 / 19,330.05 |
| TRST | cached | 64 | 146,390 / 146,110 / 212,890 | 51,950 / 56,390 / 66,520 | 27,680 / 27,680 / 45,320 | 24.11 / 28.90 / 23.68 | 24.14 / 28.97 / 23.70 |
| TRST | cached | 512 | 146,340 / 147,390 / 198,200 | 51,950 / 51,850 / 71,100 | 27,680 / 27,680 / 45,320 | 203.46 / 203.18 / 163.31 | 204.11 / 203.99 / 163.57 |
| TRST | cached | 4,096 | 146,300 / 146,170 / 208,450 | 51,940 / 51,850 / 66,520 | 27,680 / 27,690 / 45,330 | 887.02 / 886.07 / 745.19 | 888.08 / 888.66 / 746.91 |
| TRST | cached | 65,536 | 146,380 / 147,500 / 193,630 | 56,520 / 51,850 / 66,520 | 27,680 / 27,680 / 51,180 | 21,613.38 / 20,889.89 / 17,629.46 | 22,471.20 / 22,451.06 / 18,538.76 |
| TRST | bypass | 64 | 146,320 / 146,170 / 212,890 | 51,950 / 53,150 / 66,520 | 27,680 / 27,680 / 45,330 | 24.11 / 28.91 / 23.68 | 24.14 / 28.94 / 23.70 |
| TRST | bypass | 512 | 146,280 / 147,390 / 198,200 | 51,950 / 56,430 / 71,100 | 27,680 / 28,940 / 45,330 | 242.99 / 243.00 / 163.31 | 243.88 / 243.78 / 163.57 |
| TRST | bypass | 4,096 | 146,320 / 146,170 / 208,450 | 51,950 / 51,850 / 66,520 | 27,680 / 27,680 / 45,330 | 1,912.18 / 1,910.81 / 745.19 | 1,931.99 / 1,932.17 / 747.01 |
| TRST | bypass | 65,536 | 146,300 / 146,130 / 193,630 | 56,510 / 51,850 / 66,520 | 27,680 / 27,680 / 50,030 | 21,370.48 / 20,883.28 / 17,629.46 | 23,638.27 / 23,521.93 / 18,731.89 |

The 64 KiB TRST read loop after first open took about 0.55% more cycles than previous Copper under either policy. Including open and close, the cached median fell from 21,613 to 20,890 KiB/s and bypass from 21,370 to 20,883 KiB/s. Both remained above the original's 17,629 KiB/s. The 512 B and 4 KiB TRST first-open sessions were within 0.2% of previous Copper.

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 18,692,790 / 15,624,560 / 19,079,860 | 18,685,260 / 15,611,600 / 19,096,780 | 24.29 / 29.06 / 23.79 |
| CSUN | cached | 512 | 8,869,760 / 8,852,400 / 11,040,780 | 8,884,260 / 8,857,050 / 11,037,980 | 204.74 / 205.14 / 164.48 |
| CSUN | cached | 4,096 | 8,134,980 / 8,122,670 / 9,662,970 | 8,134,960 / 8,135,490 / 9,663,190 | 892.94 / 894.29 / 751.74 |
| CSUN | cached | 65,536 | 270,190 / 277,370 / 327,450 | 270,170 / 269,130 / 312,200 | 26,884.94 / 26,188.99 / 22,183.66 |
| CSUN | bypass | 64 | 18,688,600 / 15,615,320 / 19,089,240 | 18,692,940 / 15,609,210 / 19,097,090 | 24.29 / 29.07 / 23.78 |
| CSUN | bypass | 512 | 7,407,660 / 7,395,970 / 11,042,620 | 7,402,620 / 7,380,880 / 11,042,700 | 245.15 / 245.54 / 164.45 |
| CSUN | bypass | 4,096 | 3,724,500 / 3,722,390 / 9,663,290 | 3,739,130 / 3,715,570 / 9,663,870 | 1,950.34 / 1,951.45 / 751.72 |
| CSUN | bypass | 65,536 | 268,990 / 270,350 / 327,400 | 270,170 / 283,890 / 312,150 | 27,004.87 / 26,869.03 / 22,187.05 |
| TRST | cached | 64 | 18,764,160 / 15,646,110 / 19,084,120 | 18,765,950 / 15,630,710 / 19,097,090 | 24.20 / 29.02 / 23.79 |
| TRST | cached | 512 | 8,863,280 / 8,875,770 / 11,033,710 | 8,858,890 / 8,863,370 / 11,042,240 | 204.89 / 204.60 / 164.59 |
| TRST | cached | 4,096 | 8,126,830 / 8,118,230 / 9,662,090 | 8,141,380 / 8,131,410 / 9,663,990 | 893.83 / 894.78 / 751.81 |
| TRST | cached | 65,536 | 269,180 / 270,670 / 325,990 | 285,140 / 285,430 / 312,340 | 26,985.81 / 26,837.26 / 22,283.02 |
| TRST | bypass | 64 | 18,763,430 / 15,642,640 / 19,084,120 | 18,766,650 / 15,652,030 / 19,097,040 | 24.20 / 29.02 / 23.79 |
| TRST | bypass | 512 | 7,406,740 / 7,405,310 / 11,033,710 | 7,408,270 / 7,406,500 / 11,042,260 | 245.18 / 245.23 / 164.59 |
| TRST | bypass | 4,096 | 3,726,020 / 3,726,480 / 9,662,090 | 3,721,750 / 3,721,410 / 9,663,290 | 1,949.54 / 1,949.30 / 751.81 |
| TRST | bypass | 65,536 | 269,180 / 270,610 / 325,990 | 269,170 / 270,690 / 312,110 | 26,985.81 / 26,843.21 / 22,283.02 |

## Mount and reopen

All 96 final remounts issued two requests totaling 4,608 bytes. First open issued zero reads. Reopen beat original SFS in all 16 configurations. The following fresh-startup measurements include DOS device registration and exclude loading the HUNK.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 398,010 / 359,620 / 518,280 | 51,940 / 51,440 / 96,710 | 449,950 / 411,060 / 614,990 |
| CSUN | bypass | 398,530 / 360,240 / 518,280 | 51,940 / 51,430 / 96,710 | 450,470 / 411,670 / 614,990 |
| TRST | cached | 378,700 / 363,220 / 518,280 | 56,590 / 51,790 / 96,710 | 435,290 / 415,010 / 614,990 |
| TRST | bypass | 379,280 / 363,930 / 518,280 | 56,480 / 51,790 / 96,710 | 435,760 / 415,720 / 614,990 |

## Verification

The four-size A/B checked 288 timed workloads across previous Copper, final Copper and original SmartFilesystem 1.279. Every final 64-byte session issued one 64 KiB read. Every timed 64 KiB loop returned 1 MiB in sixteen requests. Separate traces for both variants and both policies confirmed that all sixteen requests went directly to the caller's buffer.

Each final SFS2 HUNK passed 132 native packet replays across the 68000 and 68020. Tests exercised a 64-byte read into an odd address against staged data, a destination overlapping the private file record, and a failed read above 4 GiB followed by retry, exact EOF, a short EOF tail and an invalid seek. Both native compatibility reports recorded zero managed allocation sites.

CopperStart passed 157 native integration checks with one classic-only skip. Twelve writer/remount boots checked file contents after mutation and handoff between Copper and original SFS, including rename, deletion and truncation.

The Release solution built. 1,307 host tests passed and 3 external-media tests skipped.
