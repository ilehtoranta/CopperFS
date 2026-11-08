# Cached TRST remount and first open under Kickstart

The regression appeared in the remount plus first-open interval preceding the 4 KiB workload. Both Copper builds issued two remount reads and zero first-open reads. In the pre-regression run, the long remount and long open occurred in the same repetition. In the regressed run, different repetitions paid those costs, raising the median of the combined interval from 146,170 to 161,110 guest cycles.

The cached TRST instruction traces show where the variation occurred. In the regressed build, remount took 89,620, 94,210 and 108,990 guest cycles, including 74,470–74,486 cycles in Copper and 14,488–33,322 in Kickstart ROM. First open took 71,460, 52,040 and 52,040 cycles. Copper used 30,302 cycles in each open. The largest changes between repetitions were in ROM execution.

The C# change removes work specific to a freshly invalidated cache. An ordinary standalone SFS2 inhibit or uninhibit packet now uses the same operation as the general dispatcher without its unrelated lookups and work area. A pending recovery still passes through the general decoder. On uninhibit the handler invalidates directory and file cursors, the track window and every ordinary cache key and verification tag. It clears the hash buckets and preserves the LRU links and tail. It can then populate empty slots directly from the existing mount read. The same type and on-disk address checks admit sectors as before, and each entry is unverified until its normal logical reader checks the checksum. Failed speculative reads fall back to individual reads.

The benchmark uses a PAL M68020, Kickstart 3.1, synchronous CopperHDF, 512-byte sectors and a 64 KiB MaxTransfer. The 64-byte, 512-byte, 4 KiB and 64 KiB packet sizes read 64 KiB, 256 KiB, 1 MiB and 1 MiB respectively. Each uninstrumented cell is a median of three trials with matching media, ROM, probe and returned-data hashes. Each combined timing is the median of complete remount plus first-open measurements. Reopen follows a priming open and close. Original SmartFilesystem 1.279 uses its own cache policy.

For open and reopen, the decoder reuses the mapped memory region found when checking the BSTR length byte. A name or file handle outside that region still receives its own bounds check. The pathname cache keeps all bytes of the aligned BSTR, including its length. A hit compares aligned longwords and the remaining bytes within the validated bounds. The lookup still checks the directory, generation, transaction state and ownership of the file.

## Cached TRST 4 KiB results

The matched run reproduced the regressed median at 161,190 cycles. Final Copper took 128,490 cycles, 20.3% fewer, and beat the earlier 146,170-cycle result. Each row is an uninstrumented trial. Each triple gives remount / first open / combined, in guest cycles.

| Trial | Regressed Copper | Final Copper | Original SFS |
|---|---:|---:|---:|
| 1 | 89,620 / 71,570 / 161,190 | 76,620 / 52,080 / 128,700 | 127,080 / 71,220 / 198,300 |
| 2 | 94,260 / 52,150 / 146,410 | 76,370 / 52,080 / 128,450 | 126,550 / 66,630 / 193,180 |
| 3 | 109,230 / 52,150 / 161,380 | 76,410 / 52,080 / 128,490 | 141,800 / 78,360 / 220,160 |

Across the three final instruction traces, remount used 56,665–56,874 handler cycles and first open used 30,223. The reduction inside the remount handler alone was about 17,600–17,800 cycles per trial. The cache invalidation and population take place in the measured remount interval. All 96 final timed remounts issued two requests totaling 4,608 bytes, followed by zero first-open requests.

## All four read sizes

Each timing triple is **regressed Copper / final Copper / original SFS**. The session throughput columns include open, read and close. Reopen cycles measure the open operation separately. All 16 remount-plus-first-open medians improved over both comparison arms. All 32 open/read/close medians exceeded original SFS.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 146,100 / 128,190 / 212,890 | 51,840 / 56,310 / 71,100 | 27,800 / 27,550 / 45,330 | 29.87 / 29.99 / 23.66 | 29.91 / 30.03 / 23.71 |
| CSUN | cached | 512 | 147,360 / 123,580 / 194,850 | 56,450 / 51,730 / 66,520 | 27,800 / 27,560 / 45,330 | 210.01 / 209.78 / 163.25 | 210.63 / 210.29 / 163.56 |
| CSUN | cached | 4,096 | 146,080 / 135,360 / 212,970 | 56,450 / 51,730 / 71,090 | 27,790 / 27,560 / 45,320 | 902.16 / 903.29 / 745.13 | 906.51 / 905.02 / 747.08 |
| CSUN | cached | 65,536 | 147,420 / 128,150 / 198,220 | 51,840 / 56,300 / 71,090 | 27,800 / 27,560 / 46,670 | 23,162.66 / 21,939.11 / 17,588.05 | 25,082.15 / 25,076.09 / 18,729.96 |
| CSUN | bypass | 64 | 146,100 / 128,310 / 208,470 | 51,840 / 56,300 / 71,100 | 27,790 / 27,560 / 45,320 | 29.87 / 29.98 / 23.68 | 29.91 / 30.05 / 23.68 |
| CSUN | bypass | 512 | 147,320 / 128,160 / 198,110 | 51,840 / 51,730 / 71,100 | 27,800 / 27,560 / 45,330 | 254.28 / 253.99 / 163.21 | 255.09 / 254.67 / 163.56 |
| CSUN | bypass | 4,096 | 146,140 / 128,160 / 198,300 | 51,840 / 51,730 / 71,110 | 27,800 / 32,100 / 45,320 | 2,005.24 / 1,992.50 / 744.96 | 2,017.28 / 2,014.94 / 746.99 |
| CSUN | bypass | 65,536 | 147,340 / 128,160 / 198,100 | 53,070 / 51,730 / 71,100 | 27,790 / 27,560 / 50,030 | 22,680.99 / 22,662.59 / 17,571.88 | 25,083.01 / 24,474.53 / 18,794.41 |
| TRST | cached | 64 | 146,350 / 128,500 / 198,150 | 52,040 / 51,970 / 71,120 | 27,750 / 27,530 / 45,320 | 29.94 / 29.98 / 23.67 | 29.98 / 30.04 / 23.69 |
| TRST | cached | 512 | 146,460 / 129,730 / 193,580 | 52,040 / 56,560 / 71,120 | 29,110 / 27,530 / 45,330 | 209.85 / 210.10 / 163.15 | 210.34 / 210.67 / 163.62 |
| TRST | cached | 4,096 | 161,190 / 128,490 / 198,300 | 52,040 / 51,970 / 71,110 | 27,740 / 27,530 / 45,330 | 902.82 / 903.16 / 745.15 | 904.88 / 906.44 / 746.79 |
| TRST | cached | 65,536 | 146,380 / 128,500 / 198,210 | 56,620 / 56,570 / 71,170 | 27,750 / 27,540 / 45,330 | 21,838.20 / 22,239.36 / 17,573.58 | 24,533.22 / 23,973.73 / 19,335.20 |
| TRST | bypass | 64 | 146,360 / 128,480 / 212,890 | 56,580 / 56,550 / 71,100 | 27,740 / 27,530 / 45,320 | 29.93 / 29.98 / 23.66 | 29.96 / 30.04 / 23.69 |
| TRST | bypass | 512 | 147,600 / 129,750 / 194,850 | 52,040 / 56,570 / 66,520 | 27,750 / 27,540 / 45,330 | 253.04 / 254.24 / 163.25 | 254.31 / 254.77 / 163.64 |
| TRST | bypass | 4,096 | 146,360 / 128,490 / 212,970 | 52,040 / 53,330 / 71,090 | 27,750 / 32,110 / 45,330 | 1,996.91 / 2,003.04 / 745.13 | 2,009.62 / 2,013.64 / 746.70 |
| TRST | bypass | 65,536 | 150,960 / 128,680 / 198,220 | 52,040 / 51,980 / 71,090 | 27,740 / 27,540 / 45,320 | 22,143.09 / 23,137.57 / 17,588.05 | 23,899.59 / 23,976.90 / 19,339.83 |

The final TRST 64-byte session after first open reached 29.98 KiB/s under either policy. The final 64 KiB session reached 22,239 KiB/s cached and 23,138 with bypass. The cached 64 KiB session after reopen fell from 24,533 to 23,974 KiB/s. On CSUN, the largest session decrease was the cached 64 KiB first-open session, from 23,163 to 21,939 KiB/s. The corresponding original SFS sessions measured 19,335 and 17,588 KiB/s respectively.

## Reopen timing

In the standard cached TRST 4 KiB workload, the median reopen took 27,530 cycles, down from 27,740. The scalar trace measured 5,783 handler cycles in each final reopen versus 6,011 previously. In the bypass run, the three-trial median was 32,110 cycles versus 27,750 previously. The three final bypass reopen traces used 5,783–5,785 handler cycles each, down from 6,011. ROM cycles ranged from 20,950 to 33,325. A separate check repeated all four workloads 15 times after a priming open on each build and policy. It used identical extended guest probes and checked the full output stream. Below are cycles for the 15 TRST 4 KiB reopens per arm.

| Payload cache | Previous median | Final median | Previous mean | Final mean | Final range |
|---|---:|---:|---:|---:|---:|
| Cached | 27,750 | 27,530 | 28,675 | 28,151 | 27,530–32,210 |
| Bypass | 27,750 | 27,540 | 29,967 | 30,911 | 27,530–46,930 |

## Read loop

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 15,136,850 / 15,073,550 / 19,085,500 | 15,137,940 / 15,080,700 / 19,089,710 | 29.99 / 30.12 / 23.79 |
| CSUN | cached | 512 | 8,579,870 / 8,594,610 / 11,041,370 | 8,578,760 / 8,594,910 / 11,042,240 | 211.66 / 211.30 / 164.47 |
| CSUN | cached | 4,096 | 7,970,980 / 7,975,650 / 9,662,680 | 7,974,890 / 7,988,330 / 9,663,000 | 911.31 / 910.78 / 751.76 |
| CSUN | cached | 65,536 | 250,060 / 251,580 / 327,030 | 246,730 / 250,480 / 315,450 | 29,049.19 / 28,873.68 / 22,212.15 |
| CSUN | bypass | 64 | 15,138,310 / 15,074,240 / 19,092,060 | 15,140,600 / 15,070,500 / 19,110,280 | 29.99 / 30.12 / 23.78 |
| CSUN | bypass | 512 | 7,079,470 / 7,087,560 / 11,040,550 | 7,080,910 / 7,084,710 / 11,042,700 | 256.52 / 256.23 / 164.49 |
| CSUN | bypass | 4,096 | 3,558,620 / 3,564,960 / 9,664,920 | 3,562,610 / 3,563,000 / 9,664,190 | 2,041.25 / 2,037.62 / 751.59 |
| CSUN | bypass | 65,536 | 257,920 / 258,250 / 312,190 | 251,300 / 258,700 / 312,150 | 28,163.93 / 28,127.94 / 23,268.01 |
| TRST | cached | 64 | 15,100,600 / 15,078,570 / 19,094,200 | 15,105,810 / 15,069,630 / 19,096,010 | 30.07 / 30.11 / 23.78 |
| TRST | cached | 512 | 8,591,290 / 8,576,390 / 11,044,630 | 8,593,180 / 8,581,910 / 11,038,840 | 211.38 / 211.75 / 164.42 |
| TRST | cached | 4,096 | 7,980,980 / 7,976,680 / 9,662,490 | 7,989,370 / 7,975,750 / 9,663,300 | 910.17 / 910.66 / 751.78 |
| TRST | cached | 65,536 | 246,190 / 251,400 / 312,170 | 257,830 / 264,920 / 312,090 | 29,505.83 / 28,894.36 / 23,269.50 |
| TRST | bypass | 64 | 15,103,080 / 15,075,860 / 19,085,500 | 15,113,370 / 15,075,090 / 19,097,270 | 30.06 / 30.11 / 23.79 |
| TRST | bypass | 512 | 7,109,190 / 7,080,520 / 11,041,370 | 7,097,970 / 7,090,020 / 11,036,540 | 255.45 / 256.48 / 164.47 |
| TRST | bypass | 4,096 | 3,574,860 / 3,561,290 / 9,662,680 | 3,571,790 / 3,563,350 / 9,662,190 | 2,031.98 / 2,039.72 / 751.76 |
| TRST | bypass | 65,536 | 265,500 / 251,360 / 327,030 | 265,690 / 264,900 / 312,240 | 27,359.85 / 28,898.95 / 22,212.15 |

## Fresh startup

Startup begins after loading the handler HUNK and before DOS device registration.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 359,220 / 364,190 / 518,280 | 56,350 / 51,660 / 96,710 | 415,570 / 415,850 / 614,990 |
| CSUN | bypass | 359,780 / 364,760 / 518,280 | 56,360 / 51,660 / 96,710 | 416,140 / 416,420 / 614,990 |
| TRST | cached | 362,710 / 345,520 / 518,280 | 51,970 / 51,910 / 96,710 | 414,680 / 397,430 / 614,990 |
| TRST | bypass | 363,270 / 346,160 / 518,280 | 51,970 / 51,910 / 96,710 | 415,240 / 398,070 / 614,990 |

## Verification

Each final SFS2 HUNK passed 160 native packet replays across the 68000 and 68020. A repeated-remount replay primed the caches, changed the file size and first extent, checked the replacement bytes, then damaged the clean marker. The handler rejected the corrupt remount, denied opens while inhibited and successfully remounted after repair. Other replays covered staged metadata, speculative-read failure, exact pathname length and tail bytes, and validation of a name and handle in distinct memory regions. Both compiler reports recorded zero managed allocation sites.

CopperStart passed 157 native integration checks with one classic-only skip. The main Kickstart comparison covered 288 timed workloads across the three arms. Every final 64-byte workload read the 64 KiB stream with one request. The 64 KiB traces confirmed sixteen direct-to-caller requests for 1 MiB under both policies and both Copper variants. Twelve separate writer/remount boots checked file contents after mutation and handoff between Copper and original SFS. The supplemental reopen check added 240 timed workloads across previous and final TRST.

The Release solution built. 1,307 host tests passed and 3 external-media tests skipped.
