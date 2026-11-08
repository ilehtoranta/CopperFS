# SFS2 first open and sequential reads under Kickstart

**Frozen baseline, 2026-11-08.** Use the two `ab35final` handlers identified by SHA-256 in the verification section for future CopperSFS2 sequential-read comparisons. The three-trial reference medians are 44,150 guest bus cycles for cached CSUN 4 KiB first open and 240,670 for the cached TRST 64 KiB reopen session.

The cached CSUN regression reproduced at 52,270 cycles for the first open preceding a 4 KiB read loop. On the earlier build the median was 51,230. The instruction trace put 27,209–27,210 cycles inside the regressed handler, down from 30,747. Two trials spent about 24,150 cycles in Kickstart during the open, up from about 19,690. All three opens completed without a device read. The packet-size label identifies the subsequent workload. DOS has yet to receive a read packet at this point.

On cached TRST, the previous reader used 105,395–105,404 handler cycles in the 1 MiB read loop after reopen, up from 105,054–105,065. All three trials used sixteen device requests. The most expensive handler methods included DOS dispatch, the aligned extent reader and device-transfer setup. The first packet also copied the extent leaf into scratch. Open, read and close each picked up different amounts of Kickstart time across the three trials.

## C# changes

- Path resolution checks the component bytes and their terminator together. A match still requires a valid comment and aligned record boundary. A nonmatch still goes through full record validation before the scanner advances.
- On a stable mounted lookup, the reader can capture the size, type, parent and first extent of a matching first record in an object container. It follows the node index before accepting those fields. A different indexed container supplies its own canonical record, including its own size validation. Only scalars survive the index read. Later records retain the canonical scan, which also preserves the result when multiple records contain the same node.
- Read-only extent traversal consumes the verified tree image before looking up another block. It checks the ordering of later entries even after finding the key. Mutation callers retain their scratch image. Aligned runs larger than eight 512-byte sectors can use the direct reader from the first packet. Short runs retain their read-ahead policy.
- The native transfer path handles an empty write queue before entering the range scan. A direct request on a qualified 32-bit partition uses the validated offset and checks both device status and completed byte count. The packet loop defers notification-list traversal and timer setup until a reply or a pending write needs them.

The changes use existing caller-owned storage, pathname generations, overlay checks and cursor invalidation.

## Measurement

The A/B boots Kickstart 3.1 on an emulated PAL 68020 with synchronous CopperHDF, 512-byte sectors and a 64 KiB MaxTransfer. The original handler is SmartFilesystem 1.279. Each handler reads identical data with the same ROM, disk and guest probe. The cache-policy labels apply to Copper. Original SFS uses its own policy in each control run.

The guest reads 64 KiB in 64-byte calls, 256 KiB in 512-byte calls and 1 MiB in 4 KiB or 64 KiB calls. A cold trial times the first open after remount, then the reads and close. A warm trial adds an untimed open and close before timing the reopened session. Session medians use the sum from each trial. Independent phase medians need not add to the session median.

## Three-trial guest bus cycles

Previous Copper is the handler that reproduced the reported regressions. The workspace control was rebuilt with the other source changes present in the final checkout and this optimization removed. The final handlers beat both controls on the two target measurements.

| Volume and policy | Phase | Previous Copper | Workspace control | Final Copper | Original SFS |
|---|---|---:|---:|---:|---:|
| CSUN cached | 4 KiB first open | 52,270 | 47,650 | 44,150 | 71,080 |
| TRST cached | 64 KiB reopen session | 248,640 | 249,330 | 240,670 | 375,600 |
| CSUN cached | 64 KiB first open | 47,690 | 47,650 | 44,140 | 70,820 |
| CSUN cached | 64 KiB first session | 271,080 | 270,640 | 258,100 | 413,070 |
| CSUN cached | 64 KiB reopen | 30,710 | 26,160 | 25,950 | 49,920 |
| CSUN cached | 64 KiB reopen session | 248,270 | 247,940 | 239,890 | 387,670 |
| CSUN bypass | 64 KiB reopen session | 243,720 | 243,380 | 239,920 | 387,530 |
| TRST bypass | 64 KiB reopen session | 248,680 | 249,310 | 239,490 | 375,700 |

## Repeatability

Five fresh boots ran the same three-trial guest binary. The longer probe ran fifteen trials in one boot. Each cell is the median of the individual phase or session measurements.

| Volume and phase | Trials per boot | Boots | Previous Copper | Final Copper | Original SFS |
|---|---:|---:|---:|---:|---:|
| CSUN cached 4 KiB first open | 3 | 1 | 52,270 | 44,150 | 71,080 |
| CSUN cached 4 KiB first open | 3 | 5 | 52,270 | 44,150 | 71,080 |
| CSUN cached 4 KiB first open | 15 | 1 | 47,690 | 44,150 | 66,520 |
| TRST cached 64 KiB reopen session | 3 | 1 | 248,640 | 240,670 | 375,600 |
| TRST cached 64 KiB reopen session | 3 | 5 | 248,640 | 240,690 | 375,800 |
| TRST cached 64 KiB reopen session | 15 | 1 | 248,720 | 239,480 | 375,710 |

Across five boots, the cached CSUN 64 KiB first-open median fell from 47,690 to 44,150 cycles. Its reopened session fell from 248,290 to 239,890 cycles cached and from 243,720 to 239,900 with bypass.

## Instruction profiles

The final CSUN first open spent 23,666 cycles in the handler in each trace. In trial zero, verified metadata access fell from 1,926 to 1,356 exclusive cycles. The two checksums still cost 3,614 cycles. The final TRST read loop spent 96,747–96,759 cycles in the handler. Its first trial lost the 2,453 cycles previously attributed to the block copy. Queue intersection checks fell from 3,184 to 1,840 cycles. Notification reply checks fell from 1,584 to 640, and write-timer checks from 1,536 to 1,024. The device completed the same sixteen transfers.

Each slash-separated group below lists the three profiled trials. Historical denotes the earlier implementation preceding the reported regressions. Whole-phase cycles include time outside the handler and ROM regions. The throughput tables use separate uninstrumented runs.

| Profile | Phase | Build | Handler cycles by trial | ROM cycles by trial | Total cycles by trial |
|---|---|---|---|---|---|
| CSUN | open | historical | 30,747 / 30,747 / 30,747 | 19,693 / 19,691 / 19,695 | 51,230 / 51,220 / 51,230 |
| TRST | open | historical | 5,662 / 5,662 / 5,662 | 19,692 / 19,692 / 19,694 | 26,140 / 26,140 / 26,150 |
| TRST | read | historical | 105,065 / 105,054 / 105,057 | 101,757 / 100,664 / 96,207 | 212,640 / 211,410 / 206,840 |
| TRST | close | historical | 3,544 / 3,544 / 3,416 | 6,797 / 6,795 / 11,244 | 10,700 / 10,700 / 15,300 |
| CSUN | open | previous | 27,210 / 27,209 / 27,210 | 24,151 / 24,148 / 19,692 | 52,270 / 52,270 / 47,690 |
| TRST | open | previous | 5,695 / 5,695 / 5,695 | 24,302 / 19,693 / 19,693 | 30,910 / 26,170 / 26,170 |
| TRST | read | previous | 105,395 / 105,404 / 105,399 | 97,368 / 100,688 / 100,654 | 208,350 / 211,740 / 211,690 |
| TRST | close | previous | 3,562 / 3,562 / 3,562 | 6,795 / 6,799 / 6,797 | 10,720 / 10,720 / 10,720 |
| CSUN | open | current | 23,666 / 23,666 / 23,666 | 19,692 / 19,694 / 19,690 | 44,140 / 44,150 / 44,140 |
| TRST | open | current | 5,448 / 5,448 / 5,448 | 19,694 / 19,692 / 19,692 | 25,930 / 25,930 / 25,930 |
| TRST | read | current | 96,753 / 96,747 / 96,759 | 101,799 / 97,367 / 101,842 | 204,260 / 199,690 / 204,320 |
| TRST | close | current | 3,324 / 3,324 / 3,324 | 6,795 / 6,797 / 6,795 | 10,480 / 10,480 / 10,480 |

## Four-size Kickstart A/B

Each cell gives previous Copper / final Copper / original SFS, in that order. Throughput is KiB/s. The session includes the timed open, read loop and close.

| SFS2 variant | Copper payload policy | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 125,010 / 121,420 / 212,890 | 47,690 / 44,140 / 71,100 | 31,930 / 25,950 / 45,320 | 30.47 / 30.87 / 23.66 | 30.51 / 30.87 / 23.70 |
| CSUN | cached | 512 | 125,010 / 121,490 / 194,840 | 47,690 / 44,150 / 66,520 | 26,160 / 30,550 / 45,330 | 214.76 / 214.77 / 163.26 | 215.27 / 215.13 / 163.57 |
| CSUN | cached | 4,096 | 124,850 / 116,810 / 212,000 | 52,270 / 44,150 / 71,080 | 26,160 / 25,950 / 45,330 | 914.67 / 915.94 / 745.26 | 916.43 / 917.77 / 747.07 |
| CSUN | cached | 65,536 | 124,860 / 121,430 / 197,890 | 47,690 / 44,140 / 70,820 | 30,710 / 25,950 / 49,920 | 26,796.67 / 28,144.29 / 17,585.50 | 29,258.63 / 30,280.72 / 18,737.69 |
| CSUN | bypass | 64 | 124,900 / 121,420 / 212,890 | 47,690 / 44,140 / 71,100 | 26,160 / 30,510 / 45,330 | 30.47 / 30.85 / 23.67 | 30.52 / 30.87 / 23.70 |
| CSUN | bypass | 512 | 124,860 / 121,370 / 194,850 | 47,690 / 44,140 / 71,110 | 26,160 / 25,950 / 45,320 | 260.79 / 258.07 / 163.28 | 261.55 / 258.74 / 163.56 |
| CSUN | bypass | 4,096 | 124,840 / 121,480 / 213,030 | 47,690 / 45,400 / 71,140 | 26,160 / 25,950 / 45,330 | 2,063.13 / 2,045.09 / 745.11 | 2,078.33 / 2,056.60 / 746.87 |
| CSUN | bypass | 65,536 | 124,890 / 121,420 / 198,260 | 47,690 / 44,140 / 71,100 | 26,160 / 25,950 / 46,560 | 26,800.62 / 28,016.20 / 17,569.76 | 29,804.86 / 30,276.93 / 18,744.46 |
| TRST | cached | 64 | 124,590 / 121,570 / 212,890 | 47,590 / 45,350 / 66,520 | 30,750 / 25,930 / 45,320 | 30.52 / 30.78 / 23.67 | 30.56 / 30.86 / 23.69 |
| TRST | cached | 512 | 124,740 / 121,530 / 198,120 | 47,590 / 44,110 / 71,100 | 26,180 / 25,930 / 45,330 | 214.56 / 214.68 / 163.26 | 215.06 / 215.29 / 163.64 |
| TRST | cached | 4,096 | 124,570 / 121,570 / 211,450 | 47,590 / 44,110 / 71,100 | 26,170 / 25,930 / 45,330 | 914.51 / 916.71 / 745.28 | 916.44 / 918.90 / 746.70 |
| TRST | cached | 65,536 | 124,580 / 121,550 / 193,610 | 47,590 / 44,110 / 71,160 | 26,170 / 25,930 / 45,320 | 26,779.87 / 26,804.58 / 17,632.02 | 29,215.09 / 30,182.58 / 19,339.83 |
| TRST | bypass | 64 | 124,560 / 121,540 / 212,890 | 47,590 / 44,110 / 71,100 | 26,170 / 25,930 / 45,330 | 30.52 / 30.82 / 23.66 | 30.56 / 30.82 / 23.69 |
| TRST | bypass | 512 | 124,730 / 121,560 / 194,810 | 47,590 / 44,110 / 66,520 | 26,170 / 25,930 / 45,330 | 260.47 / 258.25 / 163.27 | 261.33 / 258.99 / 163.62 |
| TRST | bypass | 4,096 | 125,760 / 122,870 / 212,960 | 48,830 / 44,110 / 71,090 | 26,180 / 25,930 / 45,320 | 2,061.04 / 2,046.39 / 745.04 | 2,073.82 / 2,055.92 / 746.70 |
| TRST | bypass | 65,536 | 124,590 / 121,560 / 193,590 | 47,590 / 44,110 / 71,110 | 26,170 / 25,930 / 45,330 | 26,901.86 / 28,564.85 / 17,568.48 | 29,210.39 / 30,331.29 / 19,334.68 |

At 64 KiB the complete reopened session improved on both Copper variants under both policies. With bypass, 512-byte and 4 KiB reopened-session throughput fell by about 1% compared with previous Copper. Both remained above original SFS.

| SFS2 variant | Copper payload policy | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 14,841,170 / 14,653,090 / 19,088,930 | 14,837,860 / 14,664,500 / 19,094,370 | 30.59 / 30.98 / 23.78 |
| CSUN | cached | 512 | 8,397,700 / 8,400,450 / 11,042,350 | 8,399,130 / 8,398,570 / 11,040,630 | 216.25 / 216.18 / 164.46 |
| CSUN | cached | 4,096 | 7,883,280 / 7,876,100 / 9,661,080 | 7,889,580 / 7,878,450 / 9,661,850 | 921.45 / 922.29 / 751.89 |
| CSUN | cached | 65,536 | 212,700 / 203,410 / 327,370 | 206,860 / 203,460 / 312,120 | 34,151.58 / 35,711.33 / 22,189.09 |
| CSUN | bypass | 64 | 14,836,530 / 14,662,290 / 19,079,860 | 14,840,880 / 14,666,670 / 19,097,040 | 30.60 / 30.96 / 23.79 |
| CSUN | bypass | 512 | 6,900,650 / 6,978,440 / 11,040,780 | 6,902,450 / 6,977,710 / 11,042,750 | 263.17 / 260.23 / 164.48 |
| CSUN | bypass | 4,096 | 3,462,500 / 3,497,260 / 9,662,970 | 3,458,270 / 3,495,600 / 9,663,870 | 2,097.92 / 2,077.07 / 751.74 |
| CSUN | bypass | 65,536 | 212,660 / 204,660 / 327,450 | 206,870 / 203,460 / 326,310 | 34,158.00 / 35,493.21 / 22,183.66 |
| TRST | cached | 64 | 14,811,900 / 14,693,860 / 19,088,310 | 14,819,920 / 14,673,370 / 19,097,270 | 30.65 / 30.90 / 23.78 |
| TRST | cached | 512 | 8,400,610 / 8,403,460 / 11,036,270 | 8,402,590 / 8,398,870 / 11,036,540 | 216.18 / 216.10 / 164.55 |
| TRST | cached | 4,096 | 7,884,820 / 7,869,410 / 9,663,760 | 7,889,470 / 7,864,130 / 9,662,190 | 921.27 / 923.07 / 751.68 |
| TRST | cached | 65,536 | 212,940 / 214,490 / 325,940 | 211,710 / 204,260 / 312,240 | 34,113.09 / 33,866.57 / 22,286.44 |
| TRST | bypass | 64 | 14,814,190 / 14,672,360 / 19,087,970 | 14,816,830 / 14,675,140 / 19,101,900 | 30.65 / 30.94 / 23.78 |
| TRST | bypass | 512 | 6,911,950 / 6,976,400 / 11,040,680 | 6,912,340 / 6,974,240 / 11,037,230 | 262.73 / 260.31 / 164.48 |
| TRST | bypass | 4,096 | 3,464,570 / 3,495,100 / 9,663,870 | 3,461,580 / 3,494,780 / 9,663,460 | 2,096.66 / 2,078.35 / 751.67 |
| TRST | bypass | 65,536 | 211,710 / 199,710 / 327,460 | 208,380 / 203,080 / 312,140 | 34,311.28 / 36,372.95 / 22,182.99 |

## Fresh startup

Fresh startup includes the initial device and handler mount. On CSUN the cached mount rose from 345,770 to 365,920 cycles, and startup plus first open rose from 398,080 to 410,000. The cached 4 KiB inhibit/uninhibit remount plus first open improved from 124,850 to 116,810. TRST startup plus first open improved under both policies.

| SFS2 variant | Copper payload policy | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 345,770 / 365,920 / 518,280 | 52,310 / 44,080 / 96,710 | 398,080 / 410,000 / 614,990 |
| CSUN | bypass | 346,370 / 366,450 / 518,280 | 52,210 / 44,090 / 96,710 | 398,580 / 410,540 / 614,990 |
| TRST | cached | 347,270 / 346,100 / 518,280 | 48,760 / 48,620 / 96,710 | 396,030 / 394,720 / 614,990 |
| TRST | bypass | 347,860 / 346,660 / 518,280 | 48,760 / 48,610 / 96,710 | 396,620 / 395,270 / 614,990 |

## Verification

Across 96 final timed remounts, each mount issued two reads totaling 4,608 bytes and the next first open issued zero. Every 64-byte stream read 64 KiB with one device request. All sixteen profiled 1 MiB loops, spanning both variants, both policies and both open modes, issued sixteen 65,536-byte payload reads directly into caller memory. The profiled reopens and closes issued zero requests.

Both SFS2 compiler reports found zero managed allocation sites, covering 731 reachable methods for CSUN and 722 for TRST. The native packet replay suite passed 204 cases with each handler across 68000 and 68020 execution. These cover mutation and cache invalidation, generation wrap, simultaneous handles, read-ahead and buffer aliases. CopperStart passed 157 native integration cases. The cache-page test skipped for the SFS2 layout passed at both sector sizes against classic TRST. The full-ROM write, remount and cross-reader tests passed twelve boots with Copper and original SFS.

The verified CSUN handler SHA-256 is `A767B9029249D139DC0D6A101B3F92DB3C566D6AF9C2F5A800D076FD15525363`. TRST is `22D6EB2A9386C248BE4B33B93BAEA32EF675356E080531D424EF084FD7A259EB`. The original is `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`. The final manifests, raw trials, profiles and verification output are under `artifacts/ab35final`. Previous and original controls are under `artifacts/ab35`, and the matched workspace control is under `artifacts/ab35paired`.

The Release solution build passed. The host suite passed 1,318 tests and skipped three external-media cases. Seven focused cases exercised index redirection, invalid wide sizes, a reused metadata slot, embedded NULs, an overlong comment and an unordered leaf following the matched extent key.
