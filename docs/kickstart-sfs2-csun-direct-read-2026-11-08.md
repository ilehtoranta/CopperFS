# Cached CSUN session after reopen under Kickstart

CopperSFS2 read 1 MiB in sixteen 64 KiB packets after a priming open and close. The cached CSUN session took **250,680 guest bus cycles**, down from 272,370 in the matched regressed build. The earlier Copper build took 263,290 cycles. Original SmartFilesystem 1.279 took 386,770 in the paired run. Session time includes the timed reopen, reads and close. Remount and the priming open and close precede that interval.

## CPU profile

The regression reproduced with the same sixteen device reads. Across three diagnostic traces the historical handler spent 118,826–118,830 cycles in the read loop. The regressed handler used 114,107–114,112. Kickstart ROM read-loop time was 96,224 / 100,625 / 115,061 cycles historically and 96,247 / 113,848 / 117,043 in the regressed build. The higher session median came principally from the latter two trials spending longer in ROM.

The regressed read loop spent about 17,000 handler cycles in the general transfer routine, with another 5,300 in direct-buffer eligibility and DMA mask checks. All sixteen requests had the same device geometry. The C# change checks once at mount whether the partition has 512-byte blocks and sectors, fits entirely in 32-bit byte offsets, and accepts longword-aligned public memory. For eligible mounts it stores MaxTransfer in blocks in an unused word of the existing mount allocation.

Large reads still check the live redo overlay, transaction batch, pending writes and RAM backing before taking the physical path. Each direct request checks its count, volume bounds, alignment and caller-address range, invalidates the track window, and requires a complete successful device transfer. Smaller requests keep their existing read-ahead path. A missing mount cache, a restrictive DMA mask, a wider partition or a request above MaxTransfer uses the existing transfer and bounce-buffer logic. A failed remount clears eligibility.

The final read loop took 107,220–107,255 handler cycles, about 6,900 fewer per session. Final ROM read-loop cycles were 96,233 / 97,380 / 96,207. Reopen alone took 30,840 cycles in the standard run, up from 26,240. The final reopen traces used 5,789–5,793 handler cycles and 24,142–24,219 ROM cycles. Reopen and close issued no device requests.

## Kickstart measurements

The matched A/B used Kickstart 3.1, a PAL 68020, synchronous CopperHDF, 512-byte sectors and 64 KiB MaxTransfer. The four workloads read 64 KiB in 64-byte calls, 256 KiB in 512-byte calls and 1 MiB in either 4 KiB or 64 KiB calls. The standard probe runs three trials per size. All throughput figures use uninstrumented guest time. The handler arms within each comparison have matching ROM, probe, media and output hashes. Original SFS uses its own caching in each pair.

| Cached CSUN 64 KiB | Handler | Reopen cycles | Read-loop cycles | Close cycles | Session cycles |
|---|---|---:|---:|---:|---:|
| Standard probe | Previous Copper | 26,240 | 235,320 | 10,810 | 272,370 |
| Standard probe | Final Copper | 30,840 | 209,010 | 10,830 | 250,680 |
| Standard probe | Original SFS | 45,330 | 312,140 | 14,880 | 386,770 |
| Five fresh boots | Previous Copper | 26,250 | 235,170 | 10,810 | 272,230 |
| Five fresh boots | Final Copper | 30,840 | 209,000 | 10,830 | 250,680 |
| Five fresh boots | Original SFS | 45,330 | 312,230 | 14,880 | 375,880 |
| 15 trials in one boot | Previous Copper | 26,250 | 220,520 | 10,810 | 258,840 |
| 15 trials in one boot | Final Copper | 26,270 | 213,550 | 10,830 | 250,670 |
| 15 trials in one boot | Original SFS | 46,610 | 312,070 | 14,880 | 375,710 |

Each column has its own median. The five-boot comparison pools 15 timings from the unchanged three-trial probe. The longer probe uses a separate guest binary that runs 15 trials per size in one boot. In that probe, cached CSUN reopen took 26,270 cycles against 26,250 previously. The CSUN bypass session took 251,990 cycles against 258,870 previously, while its standard three-trial median rose from 257,660 to 265,390.

## All four read sizes

Each triple gives **previous Copper / final Copper / original SFS**. The policy column applies to Copper. The first and reopened session throughput figures both include the timed open, reads and close.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 128,480 / 128,810 / 212,870 | 51,220 / 51,210 / 71,090 | 26,250 / 26,270 / 49,960 | 30.56 / 30.47 / 23.67 | 30.62 / 30.56 / 23.69 |
| CSUN | cached | 512 | 128,380 / 128,650 / 194,780 | 51,220 / 51,210 / 66,520 | 26,250 / 26,270 / 45,330 | 213.47 / 212.69 / 163.29 | 214.05 / 213.28 / 163.57 |
| CSUN | cached | 4,096 | 128,400 / 128,670 / 211,450 | 51,220 / 51,220 / 71,100 | 26,250 / 26,270 / 49,900 | 912.04 / 909.25 / 745.06 | 915.78 / 913.78 / 746.48 |
| CSUN | cached | 65,536 | 129,670 / 129,860 / 193,630 | 51,220 / 51,220 / 71,100 | 26,240 / 30,840 / 45,330 | 25,707.96 / 26,361.97 / 17,630.31 | 26,669.75 / 28,977.35 / 18,781.29 |
| CSUN | bypass | 64 | 128,380 / 143,400 / 212,870 | 51,220 / 51,210 / 71,090 | 26,250 / 27,560 / 45,330 | 30.56 / 30.46 / 23.67 | 30.57 / 30.54 / 23.71 |
| CSUN | bypass | 512 | 128,410 / 128,640 / 193,190 | 51,220 / 51,220 / 66,520 | 26,250 / 27,520 / 45,320 | 259.29 / 257.83 / 163.26 | 260.34 / 258.19 / 163.55 |
| CSUN | bypass | 4,096 | 128,400 / 128,720 / 205,670 | 51,220 / 51,210 / 71,100 | 26,250 / 26,270 / 45,320 | 2,053.94 / 2,041.07 / 745.00 | 2,066.16 / 2,056.16 / 746.93 |
| CSUN | bypass | 65,536 | 129,660 / 128,740 / 195,050 | 52,460 / 51,220 / 72,480 | 26,250 / 26,270 / 49,910 | 25,594.73 / 26,358.14 / 17,572.73 | 28,192.35 / 27,371.19 / 18,733.83 |
| TRST | cached | 64 | 128,780 / 128,390 / 212,890 | 51,460 / 55,520 / 71,100 | 26,270 / 26,260 / 49,960 | 30.55 / 30.51 / 23.66 | 30.59 / 30.55 / 23.69 |
| TRST | cached | 512 | 130,030 / 128,370 / 194,470 | 51,460 / 50,950 / 66,520 | 30,850 / 26,260 / 45,330 | 213.90 / 213.71 / 163.23 | 214.52 / 214.40 / 163.57 |
| TRST | cached | 4,096 | 128,660 / 128,480 / 212,990 | 56,020 / 50,950 / 71,120 | 26,260 / 26,250 / 49,900 | 913.24 / 913.63 / 744.21 | 915.18 / 914.90 / 746.48 |
| TRST | cached | 65,536 | 128,660 / 128,540 / 193,580 | 51,460 / 50,950 / 71,130 | 26,270 / 26,250 / 45,330 | 25,597.44 / 26,399.33 / 17,571.46 | 28,227.41 / 29,149.44 / 18,781.29 |
| TRST | bypass | 64 | 128,710 / 128,370 / 208,330 | 51,460 / 50,950 / 66,520 | 26,270 / 26,250 / 45,320 | 30.54 / 30.51 / 23.67 | 30.60 / 30.58 / 23.69 |
| TRST | bypass | 512 | 130,010 / 128,430 / 198,200 | 51,460 / 50,950 / 71,110 | 26,270 / 26,250 / 45,330 | 259.50 / 259.80 / 163.23 | 260.47 / 260.63 / 163.57 |
| TRST | bypass | 4,096 | 129,980 / 129,830 / 198,310 | 52,730 / 50,950 / 71,120 | 26,260 / 26,250 / 49,870 | 2,051.61 / 2,054.11 / 745.07 | 2,068.54 / 2,069.24 / 746.97 |
| TRST | bypass | 65,536 | 128,720 / 128,400 / 198,150 | 51,460 / 50,960 / 71,100 | 26,260 / 26,250 / 45,320 | 25,594.73 / 26,399.33 / 17,568.48 | 28,229.60 / 29,149.44 / 19,325.43 |

Cached TRST 64 KiB reopen took 26,250 cycles against 26,270 previously. Cached TRST remount plus first open for the 4 KiB workload took 128,480 cycles against 128,660. Fresh TRST startup plus first open moved from 399,630 to 400,790 cycles, compared with 614,990 for original SFS. The largest remount variation was in the CSUN bypass 64-byte workload, where the three-trial median rose from 128,380 to 143,400 cycles.

## Read-loop timings

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 14,789,270 / 14,838,710 / 19,087,350 | 14,786,790 / 14,820,180 / 19,094,340 | 30.70 / 30.60 / 23.79 |
| CSUN | cached | 512 | 8,442,180 / 8,476,210 / 11,039,700 | 8,442,400 / 8,476,440 / 11,042,460 | 215.11 / 214.25 / 164.50 |
| CSUN | cached | 4,096 | 7,902,560 / 7,927,020 / 9,663,600 | 7,895,020 / 7,912,330 / 9,666,440 | 919.20 / 916.36 / 751.69 |
| CSUN | cached | 65,536 | 220,530 / 213,510 / 325,970 | 235,320 / 209,010 / 312,140 | 32,939.01 / 34,022.02 / 22,284.38 |
| CSUN | bypass | 64 | 14,787,470 / 14,836,140 / 19,095,410 | 14,787,510 / 14,821,730 / 19,086,620 | 30.70 / 30.60 / 23.78 |
| CSUN | bypass | 512 | 6,936,120 / 6,978,030 / 11,040,530 | 6,938,340 / 6,990,150 / 11,043,320 | 261.82 / 260.25 / 164.49 |
| CSUN | bypass | 4,096 | 3,474,610 / 3,496,890 / 9,664,370 | 3,474,090 / 3,495,720 / 9,663,300 | 2,090.61 / 2,077.29 / 751.63 |
| CSUN | bypass | 65,536 | 220,540 / 213,550 / 312,160 | 220,450 / 222,520 / 312,180 | 32,937.52 / 34,015.64 / 23,270.25 |
| TRST | cached | 64 | 14,798,030 / 14,815,660 / 19,088,810 | 14,800,700 / 14,824,440 / 19,094,340 | 30.68 / 30.64 / 23.78 |
| TRST | cached | 512 | 8,423,250 / 8,431,230 / 11,042,380 | 8,428,510 / 8,431,070 / 11,042,460 | 215.59 / 215.39 / 164.46 |
| TRST | cached | 4,096 | 7,881,560 / 7,888,980 / 9,663,240 | 7,900,180 / 7,902,070 / 9,666,440 | 921.65 / 920.78 / 751.72 |
| TRST | cached | 65,536 | 221,500 / 208,820 / 327,380 | 215,700 / 208,840 / 312,140 | 32,794.77 / 34,786.14 / 22,188.41 |
| TRST | bypass | 64 | 14,799,710 / 14,820,620 / 19,093,490 | 14,786,010 / 14,808,350 / 19,098,820 | 30.68 / 30.63 / 23.78 |
| TRST | bypass | 512 | 6,935,230 / 6,927,270 / 11,041,970 | 6,931,240 / 6,930,230 / 11,041,810 | 261.85 / 262.15 / 164.46 |
| TRST | bypass | 4,096 | 3,473,230 / 3,474,340 / 9,662,660 | 3,474,590 / 3,473,420 / 9,662,490 | 2,091.44 / 2,090.77 / 751.76 |
| TRST | bypass | 65,536 | 221,480 / 213,400 / 312,240 | 216,910 / 212,140 / 312,090 | 32,797.73 / 34,039.55 / 23,264.29 |

## Fresh startup

Startup begins after the handler HUNK is loaded and before DOS device registration.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 362,210 / 351,360 / 518,280 | 51,160 / 51,150 / 96,710 | 413,370 / 402,510 / 614,990 |
| CSUN | bypass | 362,770 / 351,940 / 518,280 | 51,160 / 51,150 / 96,690 | 413,930 / 403,090 / 614,990 |
| TRST | cached | 343,610 / 345,310 / 518,280 | 56,020 / 55,480 / 96,710 | 399,630 / 400,790 / 614,990 |
| TRST | bypass | 344,160 / 345,940 / 518,280 | 56,030 / 55,590 / 96,710 | 400,190 / 401,530 / 614,990 |

## Verification

The main matrix covers 288 timed workloads across the three handler arms. All 96 final timed remounts issued two requests totaling 4,608 bytes and first open issued zero. Each 64-byte workload fetched the entire 64 KiB stream in one request. The cold traces and all three warm traces for each Copper variant and policy confirmed sixteen 65,536-byte reads directly into the caller buffer. Twelve writer/remount boots verified file contents across Copper and original SFS handoffs.

Both rebuilt SFS2 handlers passed the native compatibility check with zero managed allocation sites. Each passed 200 packet replays across the 68000 and 68020. New replays covered direct device failure and retry, splitting at MaxTransfer, a replacement DMA mask that requires bouncing, and an 8 KiB transfer above the 4 GiB device boundary. CopperStart passed 157 integration replays with one cache-page-summary case skipped for the SFS2 layout.

The local manifests, traces, trial JSON and compatibility reports are under `artifacts/ab32`. The following SHA-256 digests identify the primary comparison.

| CSUN handler | SHA-256 |
|---|---|
| previous | `5DA38E713A1265245EBF2F84E46D1B4EF2961F28F555F757F2B9754EEC72D7C7` |
| current | `197020A6D23AB2857365D791E674A05D5AE0C8E0FF68FFDC8D6DD72C49FC1F70` |
| original | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

The Release solution build passed. The host suite passed 1,307 tests and skipped three external-media cases.
