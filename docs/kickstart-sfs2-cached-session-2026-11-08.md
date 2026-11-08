# Cached SFS2 CSUN open, read and close under Kickstart

The reported regression was in a cold DOS session reading a 1 MiB file in sixteen 64 KiB packets. Retaining the extent-tree sector had reduced the number of read requests from 17 to 16. In the earlier A/B the cached CSUN median for open plus read plus close had nevertheless risen from 356,540 to 371,310 guest cycles. A fresh build reproduced it at 371,360 cycles.

Profiles of all three repetitions explain how a faster median read loop produced a slower median session. In the regressed binary's first trial the ROM used 40,016 cycles during open, compared with 20,954 in the second. The handler used 35,311 and 35,313 respectively. In the third trial the ROM used 15,551 more read-loop cycles than in the second while handler cycles differed by nine. In the last two closes the ROM used about 4,450 more cycles than in the first. The program counters establish where the extra cycles ran. These traces do not identify a particular interrupt source. Open and close issued zero device reads in every trace.

The C# handler now dispatches an ordinary SFS2 input open or close directly to the shared object operations. Those operations still validate file ownership, pathname, locks and output handles. An inhibited or recovering open goes through the general packet decoder. Close still handles active reservations, record locks, pending waiters and write notifications, and prepares reservation work only when the mount has reservations. The retained-extent read path uses the file position's two native words to check the exclusive end against EOF, including a carry across 4 GiB. It asks for read-ahead on packets up to 4 KiB and sends larger packets through the existing logical block reader with read-ahead disabled.

The A/B uses Kickstart 3.1, PAL M68020, synchronous CopperHDF, 512-byte disk blocks and a 64 KiB MaxTransfer. Each uninstrumented cell takes the median of three trials. First open follows remount. Reopen follows a priming open and close. Each trial checks the stream digest. The scalar profiles use the same guest probe and media, with each repetition recorded separately.

The standalone SFS2 build initializes a new metadata cache slot's key and bookkeeping without clearing its entire payload. The invalid key keeps the slot out of the hash table. A complete copy or successful block read supplies its contents before the handler publishes the key. Allocation failure still rolls back all slots added by the request. This uses the existing OS allocations at mount and adds no per-packet storage.

## Cached CSUN profile

The table gives exclusive instruction cycles inside Copper for the cold 64 KiB session. Ranges cover all three repetitions; times spent in Kickstart are separate.

| Phase | Previous Copper | Final Copper |
|---|---:|---:|
| Open | 35,311–35,313 | 30,255–30,287 |
| Read 1 MiB | 169,405–169,414 | 162,672–162,801 |
| Close | 10,097–10,119 | 2,564 |

The final close took 10,410 guest cycles in all three profiles. The last read loop still spent 116,210 cycles in ROM, compared with 116,191 in the corresponding previous trace. Each final trace shows 16 direct 64 KiB transfers to the caller and zero device requests during open and close.

## End-to-end results

The cached CSUN median for open, sixteen reads, and close fell from 371,360 to **337,240 guest cycles**, compared with 412,000 for original SmartFilesystem 1.279. The individual final trials took 331,360, 337,240 and 347,310 cycles. All three beat the earlier 356,540-cycle pre-regression median. The remount plus first-open and separately timed reopen medians beat both previous Copper and original SFS in every size and cache configuration.

The timing triples are **previous Copper / final Copper / original SFS**. Session throughput covers open, read and close. Combined mount timing includes the remount and the first open, and excludes the read loop. For each metric the median is taken over complete trials, so the sum of phase medians can differ from the session median. Original SFS always uses its own cache policy.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 166,900 / 146,360 / 212,890 | 57,070 / 52,000 / 71,100 | 32,900 / 28,930 / 45,320 | 24.12 / 24.21 / 23.67 | 24.14 / 24.24 / 23.70 |
| CSUN | cached | 512 | 152,080 / 146,360 / 194,800 | 61,620 / 52,000 / 66,520 | 38,850 / 27,690 / 45,330 | 201.32 / 203.32 / 163.26 | 201.71 / 203.50 / 163.57 |
| CSUN | cached | 4,096 | 168,160 / 146,320 / 211,440 | 58,300 / 52,000 / 71,090 | 37,480 / 27,690 / 45,330 | 882.42 / 885.85 / 745.08 | 885.12 / 888.57 / 747.07 |
| CSUN | cached | 65,536 | 152,100 / 146,350 / 193,580 | 57,070 / 52,000 / 71,110 | 34,170 / 27,690 / 49,920 | 19,560.64 / 21,539.68 / 17,631.17 | 20,899.51 / 23,204.09 / 18,737.69 |
| CSUN | bypass | 64 | 153,340 / 146,360 / 212,890 | 57,070 / 56,580 / 71,100 | 32,900 / 27,690 / 45,320 | 24.12 / 24.21 / 23.67 | 24.16 / 24.24 / 23.69 |
| CSUN | bypass | 512 | 153,360 / 146,340 / 194,780 | 57,070 / 56,590 / 66,520 | 34,130 / 32,260 / 45,330 | 240.40 / 243.06 / 163.25 | 241.10 / 243.85 / 163.63 |
| CSUN | bypass | 4,096 | 159,160 / 147,600 / 212,980 | 57,070 / 52,010 / 71,100 | 32,900 / 27,690 / 45,320 | 1,887.66 / 1,918.48 / 745.27 | 1,906.36 / 1,923.50 / 746.69 |
| CSUN | bypass | 65,536 | 152,110 / 146,390 / 193,640 | 57,070 / 52,010 / 71,100 | 32,900 / 27,690 / 45,330 | 19,977.56 / 21,834.92 / 17,590.18 | 22,157.95 / 23,560.07 / 19,331.60 |
| TRST | cached | 64 | 151,810 / 146,400 / 212,870 | 61,510 / 51,950 / 71,090 | 34,170 / 27,680 / 45,320 | 24.14 / 24.11 / 23.67 | 24.17 / 24.14 / 23.70 |
| TRST | cached | 512 | 167,970 / 146,310 / 194,970 | 56,940 / 51,940 / 66,520 | 32,920 / 27,690 / 45,320 | 201.36 / 203.47 / 163.27 | 201.60 / 204.08 / 163.57 |
| TRST | cached | 4,096 | 151,820 / 146,320 / 212,950 | 56,930 / 51,950 / 71,090 | 34,290 / 27,680 / 45,330 | 879.32 / 887.01 / 745.08 | 882.98 / 888.04 / 746.91 |
| TRST | cached | 65,536 | 151,840 / 146,310 / 193,650 | 56,930 / 56,510 / 71,090 | 37,500 / 27,680 / 51,180 | 19,882.96 / 21,614.67 / 17,594.01 | 21,196.50 / 22,471.20 / 18,538.76 |
| TRST | bypass | 64 | 151,850 / 146,320 / 208,330 | 56,930 / 51,950 / 66,520 | 34,150 / 27,680 / 45,320 | 24.15 / 24.11 / 23.67 | 24.18 / 24.14 / 23.69 |
| TRST | bypass | 512 | 157,720 / 146,310 / 198,200 | 61,520 / 51,950 / 71,110 | 32,920 / 27,680 / 45,330 | 240.31 / 242.95 / 163.23 | 241.05 / 243.89 / 163.62 |
| TRST | bypass | 4,096 | 151,820 / 146,320 / 198,310 | 61,510 / 51,950 / 71,120 | 32,920 / 27,680 / 45,330 | 1,892.98 / 1,912.92 / 745.07 | 1,906.44 / 1,932.09 / 746.79 |
| TRST | bypass | 65,536 | 158,920 / 146,300 / 198,150 | 61,500 / 56,520 / 71,100 | 32,920 / 27,680 / 45,330 | 19,564.33 / 21,311.55 / 17,568.48 | 21,001.62 / 23,639.81 / 19,335.20 |

All 32 final session medians beat original SFS. Relative to previous Copper, the 64 B TRST sessions were up to 0.2% slower. Every timed 64 KiB trial across the three arms completed 1 MiB in sixteen requests. All 96 final Copper remounts issued two reads totaling 4,608 bytes, followed by zero first-open reads.

## Read-loop results

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 18,743,310 / 18,686,390 / 19,088,100 | 18,743,980 / 18,690,250 / 19,094,370 | 24.22 / 24.30 / 23.78 |
| CSUN | cached | 512 | 8,940,090 / 8,869,300 / 11,040,140 | 8,936,500 / 8,884,500 / 11,040,630 | 203.13 / 204.75 / 164.49 |
| CSUN | cached | 4,096 | 8,155,680 / 8,134,640 / 9,663,020 | 8,153,360 / 8,132,700 / 9,661,850 | 890.67 / 892.98 / 751.74 |
| CSUN | cached | 65,536 | 277,010 / 270,240 / 325,980 | 276,930 / 270,160 / 312,120 | 26,223.03 / 26,879.96 / 22,283.70 |
| CSUN | bypass | 64 | 18,743,820 / 18,683,030 / 19,087,850 | 18,742,260 / 18,694,080 / 19,096,780 | 24.22 / 24.30 / 23.78 |
| CSUN | bypass | 512 | 7,479,160 / 7,404,380 / 11,042,460 | 7,475,140 / 7,404,650 / 11,037,980 | 242.81 / 245.26 / 164.46 |
| CSUN | bypass | 4,096 | 3,758,140 / 3,723,690 / 9,660,880 | 3,757,440 / 3,738,380 / 9,663,190 | 1,932.88 / 1,950.76 / 751.90 |
| CSUN | bypass | 65,536 | 284,010 / 269,010 / 326,980 | 275,710 / 270,220 / 312,200 | 25,576.71 / 27,002.87 / 22,215.55 |
| TRST | cached | 64 | 18,722,750 / 18,765,920 / 19,088,180 | 18,726,770 / 18,766,450 / 19,097,090 | 24.25 / 24.19 / 23.78 |
| TRST | cached | 512 | 8,937,210 / 8,861,630 / 11,040,900 | 8,938,490 / 8,859,310 / 11,042,240 | 203.20 / 204.93 / 164.48 |
| TRST | cached | 4,096 | 8,179,600 / 8,126,980 / 9,662,930 | 8,171,190 / 8,141,720 / 9,663,990 | 888.07 / 893.82 / 751.74 |
| TRST | cached | 65,536 | 290,400 / 269,140 / 326,910 | 275,800 / 285,140 / 312,340 | 25,013.92 / 26,989.82 / 22,220.31 |
| TRST | bypass | 64 | 18,727,670 / 18,761,600 / 19,093,490 | 18,728,200 / 18,766,000 / 19,096,010 | 24.24 / 24.20 / 23.78 |
| TRST | bypass | 512 | 7,482,130 / 7,406,560 / 11,041,970 | 7,482,770 / 7,408,010 / 11,038,840 | 242.71 / 245.19 / 164.46 |
| TRST | bypass | 4,096 | 3,757,820 / 3,724,420 / 9,662,660 | 3,755,680 / 3,721,560 / 9,663,300 | 1,933.05 / 1,950.38 / 751.76 |
| TRST | bypass | 65,536 | 275,790 / 269,130 / 312,240 | 289,190 / 269,150 / 312,090 | 26,339.03 / 26,990.83 / 23,264.29 |

## Fresh startup

Timing starts after loading the handler HUNK and before DOS device registration. Both Copper variants made three startup requests totaling 4,608 bytes and no first-open requests.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 417,450 / 397,970 / 518,280 | 57,000 / 51,940 / 96,710 | 474,450 / 449,910 / 614,990 |
| CSUN | bypass | 418,020 / 398,530 / 518,280 | 57,000 / 51,940 / 96,710 | 475,020 / 450,470 / 614,990 |
| TRST | cached | 418,260 / 378,740 / 518,280 | 56,880 / 56,570 / 96,710 | 475,140 / 435,310 / 614,990 |
| TRST | bypass | 418,830 / 379,280 / 518,280 | 56,870 / 56,480 / 96,710 | 475,700 / 435,760 / 614,990 |

## Verification

Each final SFS2 binary passed 130 native packet replays across the 68000 and 68020. The mount-prefix replay requested real cache slots through DOS, supplied nonzero memory for each empty slot, and exercised a successful fill, failed speculative read, subsequent demand read and staged extent replacement. Another replay checked that an inhibited open fails while close releases the owned file's reservation, preserves an unrelated reservation and rejects a second close. Both compiler reports recorded zero managed allocation sites.

CopperStart passed 157 native integration checks, with one classic-only skip. The matched full-ROM comparison covered 288 timed workloads across previous Copper, final Copper and original SFS. Twelve separate writer/remount boots verified file contents after native mutations and handoff to the other handler.

The final Release solution built. 1,307 host tests passed and 3 external-media tests skipped.
