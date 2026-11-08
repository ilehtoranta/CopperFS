# SFS2 first extent-tree read under Kickstart

The previous CopperSFS2 handler fetched sector 5 twice on the benchmark volume. Its mount request already covered sectors 0–7. The mount cache retained the root, directory, clean marker and node containers from that request, but discarded the `BNDC` extent-tree block. The first DOS read needed the extent length and next extent key, so it fetched sector 5 again. Original SmartFilesystem 1.279 covered sector 5 in its own mount read and issued only payload requests during the loop.

CopperSFS2 now admits a `BNDC` sector from the existing mount read if its on-disk block number matches its physical address. The cache stores that sector as unverified. Its first metadata consumer checks the type, block number and checksum through the usual logical read path, which gives staged writes precedence. The change uses an ordinary evictable cache entry. A tree outside the mount prefix or evicted before use still takes the demand path.

The comparison uses a PAL M68020 under Kickstart 3.1 with synchronous CopperHDF and 512-byte sectors. Each arm reads the same file and verifies its contents. The workloads read 64 KiB in 64-byte packets, 256 KiB in 512-byte packets and 1 MiB in either 4 KiB or 64 KiB packets. Timings are medians of three runs per configuration. “First open” follows remount. “Reopen” follows an untimed open and close of the same file.

## Device and CPU profiles

Both Copper builds issued three fresh-startup requests covering nine sectors and two remount requests covering the same nine sectors. All 96 measured first opens per Copper build issued zero requests. On the 64 KiB workload, every timed run previously read 1,049,088 bytes in 17 requests. Both optimized Copper and original SFS read exactly 1,048,576 bytes in 16 requests, after either first open or reopen and under either Copper payload-cache policy. The separate instruction traces identified every remaining request as a 64 KiB transfer directly into the caller's buffer.

| Cold 64 KiB bypass trace | Previous Copper | Optimized Copper | Original SFS |
|---|---:|---:|---:|
| CSUN handler cycles | 174,026 | 169,456 | 141,508 |
| TRST handler cycles | 174,207 | 169,484 | 141,505 |

## Read-loop and session timing

Each triple gives **previous Copper / optimized Copper / original SFS**. Original SFS uses its native cache policy. At 64 KiB the optimized loop used 1.2–6.6% fewer guest cycles than previous Copper and 7.3–15.4% fewer than the original. At 64 B the change against previous Copper was within 0.06% in either direction. Overall, the optimized loop beat the original in all 32 comparisons.

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 18,739,300 / 18,743,760 / 19,081,830 | 18,741,250 / 18,743,270 / 19,100,300 | 24.23 / 24.22 / 23.79 |
| CSUN | cached | 512 | 8,944,170 / 8,940,060 / 11,041,960 | 8,949,830 / 8,934,600 / 11,043,460 | 203.04 / 203.13 / 164.46 |
| CSUN | cached | 4,096 | 8,172,790 / 8,156,110 / 9,663,720 | 8,175,760 / 8,155,710 / 9,664,970 | 888.81 / 890.63 / 751.68 |
| CSUN | cached | 65,536 | 280,460 / 276,980 / 325,940 | 287,550 / 276,930 / 312,060 | 25,900.45 / 26,225.87 / 22,286.44 |
| CSUN | bypass | 64 | 18,740,930 / 18,743,710 / 19,088,890 | 18,739,380 / 18,742,610 / 19,097,090 | 24.23 / 24.22 / 23.78 |
| CSUN | bypass | 512 | 7,467,140 / 7,476,200 / 11,039,160 | 7,479,070 / 7,475,140 / 11,042,240 | 243.20 / 242.91 / 164.51 |
| CSUN | bypass | 4,096 | 3,763,740 / 3,758,140 / 9,661,700 | 3,764,170 / 3,757,800 / 9,663,990 | 1,930.01 / 1,932.88 / 751.84 |
| CSUN | bypass | 65,536 | 295,200 / 284,030 / 325,920 | 295,320 / 275,720 / 312,340 | 24,607.18 / 25,574.91 / 22,287.80 |
| TRST | cached | 64 | 18,721,830 / 18,722,620 / 19,079,860 | 18,725,500 / 18,724,550 / 19,096,370 | 24.25 / 24.25 / 23.79 |
| TRST | cached | 512 | 8,948,640 / 8,937,460 / 11,040,780 | 8,968,380 / 8,937,830 / 11,038,610 | 202.94 / 203.19 / 164.48 |
| TRST | cached | 4,096 | 8,184,030 / 8,179,640 / 9,662,970 | 8,183,370 / 8,173,820 / 9,663,880 | 887.59 / 888.06 / 751.74 |
| TRST | cached | 65,536 | 296,660 / 290,400 / 327,450 | 281,930 / 276,990 / 312,160 | 24,486.08 / 25,013.92 / 22,183.66 |
| TRST | bypass | 64 | 18,717,550 / 18,728,070 / 19,085,860 | 18,721,910 / 18,728,210 / 19,098,820 | 24.26 / 24.24 / 23.79 |
| TRST | bypass | 512 | 7,487,520 / 7,481,400 / 11,042,100 | 7,491,680 / 7,481,470 / 11,041,810 | 242.54 / 242.74 / 164.46 |
| TRST | bypass | 4,096 | 3,763,970 / 3,756,670 / 9,663,380 | 3,761,350 / 3,756,950 / 9,662,490 | 1,929.89 / 1,933.64 / 751.71 |
| TRST | bypass | 65,536 | 280,720 / 275,820 / 325,990 | 295,480 / 289,400 / 312,090 | 25,876.46 / 26,336.16 / 22,283.02 |

Retaining the extent costs an additional cache insertion at mount. In the 64 KiB cached CSUN case, the remount and first-open median rose from 149,240 to 152,090 cycles. First-session throughput (open, read and close) fell from 20,374 to 19,563 KiB/s, compared with 17,632 KiB/s for original SFS. All optimized remount plus first-open and reopen medians remained below the original. Individual phase medians need not sum to the median of the entire session.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 147,880 / 166,900 / 212,890 | 61,510 / 57,070 / 67,810 | 32,940 / 32,890 / 45,320 | 24.13 / 24.12 / 23.68 | 24.15 / 24.14 / 23.69 |
| CSUN | cached | 512 | 159,500 / 152,100 / 193,530 | 68,540 / 61,620 / 66,520 | 34,200 / 38,850 / 45,320 | 200.92 / 201.28 / 163.26 | 201.73 / 201.74 / 163.55 |
| CSUN | cached | 4,096 | 149,090 / 168,150 / 211,460 | 56,920 / 58,300 / 66,520 | 32,940 / 37,480 / 49,900 | 880.25 / 882.38 / 745.05 | 882.66 / 885.13 / 746.57 |
| CSUN | cached | 65,536 | 149,240 / 152,090 / 193,610 | 56,920 / 57,060 / 67,760 | 37,570 / 34,140 / 45,320 | 20,373.71 / 19,563.28 / 17,632.02 | 21,102.29 / 20,899.51 / 18,784.21 |
| CSUN | bypass | 64 | 147,890 / 153,370 / 212,870 | 61,500 / 57,070 / 71,090 | 32,940 / 32,890 / 45,320 | 24.13 / 24.12 / 23.68 | 24.16 / 24.16 / 23.70 |
| CSUN | bypass | 512 | 147,950 / 153,440 / 193,770 | 61,510 / 57,070 / 66,520 | 32,930 / 34,150 / 45,320 | 240.13 / 240.46 / 163.25 | 240.93 / 241.10 / 163.57 |
| CSUN | bypass | 4,096 | 149,130 / 159,160 / 212,950 | 58,180 / 57,070 / 71,200 | 32,930 / 32,900 / 45,330 | 1,884.37 / 1,887.93 / 745.00 | 1,903.43 / 1,906.34 / 746.91 |
| CSUN | bypass | 65,536 | 147,910 / 152,100 / 193,580 | 56,920 / 57,070 / 71,130 | 37,550 / 32,900 / 51,180 | 19,629.36 / 19,977.01 / 17,634.59 | 20,709.43 / 22,159.30 / 18,538.76 |
| TRST | cached | 64 | 148,070 / 151,850 / 212,890 | 61,580 / 61,530 / 71,100 | 34,130 / 34,240 / 45,320 | 24.15 / 24.14 / 23.67 | 24.18 / 24.18 / 23.69 |
| TRST | cached | 512 | 162,870 / 167,860 / 194,850 | 57,010 / 56,930 / 71,110 | 32,900 / 32,920 / 45,320 | 201.17 / 201.36 / 163.28 | 201.35 / 201.61 / 163.62 |
| TRST | cached | 4,096 | 148,130 / 151,820 / 213,030 | 61,630 / 56,940 / 71,140 | 32,890 / 34,170 / 45,320 | 878.41 / 879.24 / 745.11 | 882.17 / 882.81 / 746.78 |
| TRST | cached | 65,536 | 148,080 / 151,840 / 198,260 | 57,010 / 56,930 / 71,100 | 32,900 / 34,160 / 45,330 | 19,543.28 / 19,882.96 / 17,569.76 | 20,895.90 / 21,195.26 / 19,335.20 |
| TRST | bypass | 64 | 148,100 / 151,850 / 212,890 | 61,610 / 56,930 / 71,100 | 32,900 / 34,150 / 45,320 | 24.14 / 24.15 / 23.67 | 24.18 / 24.18 / 23.69 |
| TRST | bypass | 512 | 149,310 / 153,160 / 194,950 | 57,000 / 61,500 / 66,520 | 32,900 / 32,920 / 45,330 | 239.99 / 240.33 / 163.25 | 240.77 / 241.09 / 163.57 |
| TRST | bypass | 4,096 | 148,080 / 151,870 / 211,460 | 61,590 / 61,520 / 71,110 | 32,900 / 32,920 / 49,870 | 1,889.92 / 1,893.53 / 745.08 | 1,904.61 / 1,905.36 / 746.97 |
| TRST | bypass | 65,536 | 155,250 / 158,910 / 193,580 | 61,580 / 61,500 / 71,090 | 32,900 / 32,920 / 45,320 | 19,305.39 / 19,564.33 / 17,630.74 | 20,696.45 / 20,990.09 / 19,325.43 |

## Fresh handler startup

Fresh-startup timing begins after loading the handler executable and before DOS device registration. Each triple uses the same arm order as above.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 426,590 / 417,450 / 518,280 | 56,860 / 57,000 / 96,710 | 483,450 / 474,450 / 614,990 |
| CSUN | bypass | 427,230 / 418,020 / 518,280 | 56,860 / 57,000 / 96,710 | 484,090 / 475,020 / 614,990 |
| TRST | cached | 413,860 / 418,260 / 518,280 | 56,940 / 56,880 / 96,710 | 470,800 / 475,140 / 614,990 |
| TRST | bypass | 414,410 / 418,860 / 518,280 | 56,940 / 56,870 / 96,710 | 471,350 / 475,730 / 614,990 |

## Native and full-ROM verification

Each standalone handler passed 128 native packet replays on 68000 and 68020. The extended mount-prefix replay uses sector 6 for its extent tree. It checks that a successful prefix read avoids the second read, a failed speculative read falls back to demand I/O, and an invalidated handle sees a corrupt staged replacement. The old handler failed the new device-request assertion on both CPUs. Both optimized compiler reports recorded zero managed allocation sites.

CopperStart passed 157 native integration checks, with one classic-only skip. The full-ROM A/B ran 288 timed workloads across the three arms and measured fresh startup 36 times. A separate 12 writer/remount boots checked both SFS2 variants against original SFS after filesystem mutations. File contents matched across the handoffs.

The Release solution built, with 1,307 host tests passed and 3 external-media tests skipped.
