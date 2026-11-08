# CSUN cached reopen and bypass session under Kickstart

The acceptance test uses the original three-trial guest probe. Each trial remounts, opens and closes the file once, then measures a second open, sequential reads and close. At 64 KiB it reads 1 MiB in sixteen DOS packets. The priming open reads no payload.

## Profile and changes

The cached reopen regression appeared chiefly in Kickstart ROM execution. Across the regressed traces, the open handler used 5,788–5,793 cycles and ROM used 24,142–24,148. Before the regression the handler used 5,764 cycles and two of the three trials used about 19,690 ROM cycles. For the regressed bypass read loop, handler time was almost constant at 107,234–107,238 cycles. ROM time ranged from 96,205 to 116,179 cycles. One bypass close also spent 25,616 cycles in ROM.

The object packet dispatcher included the stack space for a full disk-geometry value because the same routine can resolve an FFS root. SFS2 already has a constant root node. Giving the disk-root calculation its own C# method reduced the dispatcher's generated local frame from 140 to 60 bytes. The existing open path still validates the BSTR, handle and lock, and uses the same pathname cache and generation checks. The aligned BSTR comparison checks pairs of longwords, then the remaining longword, word and byte within the caller's bounds. In an isolated 68020 replay an equal 32-byte key took 100 instructions against 119 previously, and a 128-byte key took 340 against 431. The eight-byte key in the guest probe took 40 against 41.

On a retained SFS2 extent the handler also checked scratch overlap twice and walked Exec's memory headers separately for the caller and data scratch. The native metadata and data scratch sectors are consecutive. The C# reader checks their combined range for aliasing, then tests whether a single memory region covers both the caller and data scratch. It handles either address order and checks the end before adding lengths. If the ranges cross a memory-region boundary, it checks the two mappings separately. Non-adjacent scratch buffers retain their individual overlap checks. All validation happens within the packet and uses scalar locals.

## Method

The three arms use the previous Copper build, final Copper build and original SmartFilesystem 1.279. Each pair uses Kickstart 3.1, a PAL 68020, synchronous CopperHDF, 512-byte sectors and 64 KiB MaxTransfer. Original SFS uses its native caching in both paired runs. The workloads read 64 KiB in 64-byte calls, 256 KiB in 512-byte calls and 1 MiB in either 4 KiB or 64 KiB calls. The standard timings use uninstrumented guest bus cycles. Handler, guest probe, ROM, disk and output digests were checked against the manifests and across matching trials.

## CSUN 64 KiB results

Each entry is a guest-cycle median. Individual phase medians can come from different trials. The five-boot comparison pools five fresh boots of the unchanged three-trial probe. The extended probe runs 15 trials per size in one boot.

| Run | Policy | Handler | Reopen | Read loop | Close | Session |
|---|---|---|---:|---:|---:|---:|
| Three trials | cached | Previous Copper | 30,850 | 208,980 | 10,830 | 250,660 |
| Three trials | cached | Final Copper | 26,130 | 211,560 | 10,680 | 248,370 |
| Three trials | cached | Original SFS | 50,030 | 312,120 | 14,880 | 386,450 |
| Three trials | bypass | Previous Copper | 26,270 | 223,770 | 10,830 | 265,560 |
| Three trials | bypass | Final Copper | 26,130 | 211,560 | 10,690 | 248,380 |
| Three trials | bypass | Original SFS | 45,330 | 312,110 | 14,870 | 375,750 |
| Five boots | cached | Previous Copper | 30,840 | 209,000 | 10,830 | 250,670 |
| Five boots | cached | Final Copper | 26,130 | 211,520 | 10,680 | 248,350 |
| Five boots | cached | Original SFS | 45,330 | 312,120 | 14,880 | 386,450 |
| Five boots | bypass | Previous Copper | 26,270 | 223,120 | 10,830 | 265,480 |
| Five boots | bypass | Final Copper | 26,130 | 211,560 | 10,680 | 248,370 |
| Five boots | bypass | Original SFS | 45,330 | 312,140 | 14,880 | 386,460 |
| 15 trials | cached | Previous Copper | 26,270 | 213,560 | 10,830 | 250,690 |
| 15 trials | cached | Final Copper | 26,130 | 211,580 | 10,690 | 249,630 |
| 15 trials | cached | Original SFS | 45,330 | 312,110 | 14,880 | 376,930 |
| 15 trials | bypass | Previous Copper | 26,270 | 213,540 | 10,830 | 251,910 |
| 15 trials | bypass | Final Copper | 26,130 | 211,560 | 10,680 | 248,390 |
| 15 trials | bypass | Original SFS | 45,330 | 312,340 | 14,880 | 377,110 |

In the standard probe cached CSUN reopen fell by 4,720 cycles (15.3%), below the earlier 26,240-cycle result. The bypass session fell by 17,180 cycles (6.5%), below its earlier 257,660-cycle median. The cached session fell from 250,660 to 248,370 cycles. Pooling five independent boots gave 26,130 for cached reopen and 248,370 for the bypass session.

The final cached reopen traces used 5,651 handler cycles and 19,695–19,697 ROM cycles. The final bypass read loop used 105,260–105,264 handler cycles and 96,210–100,674 ROM cycles. A memory-list check accounted for 4,295 cycles across the first traced bypass loop versus 7,240 previously. The new combined buffer helper also has a cost. Total handler work in the read loop fell by about 1,970 cycles. The larger end-to-end change includes less ROM time.

## Four-size A/B

Each triple gives **previous Copper / final Copper / original SFS**. The first session includes first open, read and close. The reopened session measures the same operations after the priming open and close. The standard matrix contains 288 timed workloads across the three arms.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 128,810 / 128,820 / 212,890 | 51,210 / 55,810 / 71,100 | 26,270 / 26,130 / 45,320 | 30.46 / 30.44 / 23.67 | 30.56 / 30.50 / 23.69 |
| CSUN | cached | 512 | 128,740 / 128,890 / 194,930 | 51,210 / 55,900 / 66,520 | 26,270 / 26,130 / 45,320 | 212.70 / 214.63 / 163.26 | 213.26 / 215.38 / 163.55 |
| CSUN | cached | 4,096 | 128,690 / 128,860 / 212,830 | 51,210 / 51,230 / 71,110 | 26,270 / 26,130 / 45,330 | 909.23 / 915.61 / 745.19 | 913.80 / 916.72 / 747.02 |
| CSUN | cached | 65,536 | 129,860 / 128,910 / 193,630 | 51,220 / 55,920 / 71,100 | 30,850 / 26,130 / 50,030 | 26,353.36 / 26,430.07 / 17,629.46 | 28,979.66 / 29,246.85 / 18,796.85 |
| CSUN | bypass | 64 | 143,390 / 128,810 / 212,890 | 51,220 / 51,230 / 71,100 | 26,270 / 26,130 / 45,330 | 30.47 / 30.44 / 23.66 | 30.54 / 30.52 / 23.69 |
| CSUN | bypass | 512 | 128,650 / 128,810 / 194,790 | 55,800 / 55,810 / 66,520 | 27,610 / 26,130 / 45,320 | 257.79 / 260.75 / 163.25 | 258.23 / 261.61 / 163.57 |
| CSUN | bypass | 4,096 | 128,730 / 128,770 / 212,980 | 51,210 / 51,230 / 71,100 | 26,270 / 26,130 / 49,950 | 2,040.57 / 2,063.23 / 744.70 | 2,054.97 / 2,077.00 / 746.92 |
| CSUN | bypass | 65,536 | 128,700 / 128,850 / 193,640 | 51,210 / 51,230 / 71,110 | 26,270 / 26,130 / 45,330 | 26,359.10 / 26,440.65 / 17,611.50 | 27,353.67 / 29,245.68 / 19,332.11 |
| TRST | cached | 64 | 128,420 / 129,480 / 212,890 | 55,530 / 51,110 / 71,100 | 26,250 / 26,140 / 45,330 | 30.51 / 30.55 / 23.66 | 30.55 / 30.59 / 23.68 |
| TRST | cached | 512 | 128,400 / 129,450 / 194,790 | 50,950 / 51,110 / 66,520 | 26,260 / 26,140 / 45,330 | 213.70 / 214.80 / 163.25 | 214.33 / 215.33 / 163.55 |
| TRST | cached | 4,096 | 128,480 / 128,240 / 212,980 | 50,950 / 51,110 / 71,100 | 26,250 / 26,140 / 49,920 | 913.54 / 913.64 / 744.70 | 914.71 / 918.63 / 746.52 |
| TRST | cached | 65,536 | 128,530 / 128,340 / 193,640 | 50,950 / 55,680 / 71,110 | 26,250 / 26,140 / 50,020 | 26,393.58 / 26,584.84 / 17,611.50 | 29,154.12 / 29,257.46 / 18,796.36 |
| TRST | bypass | 64 | 128,400 / 129,650 / 212,890 | 50,950 / 51,110 / 71,100 | 26,260 / 26,150 / 45,330 | 30.51 / 30.54 / 23.66 | 30.58 / 30.59 / 23.70 |
| TRST | bypass | 512 | 128,390 / 129,520 / 194,850 | 50,950 / 51,110 / 66,520 | 26,250 / 26,150 / 45,320 | 259.82 / 260.72 / 163.27 | 260.67 / 261.70 / 163.56 |
| TRST | bypass | 4,096 | 129,790 / 128,450 / 212,970 | 50,950 / 51,110 / 71,090 | 26,250 / 26,150 / 45,320 | 2,054.07 / 2,060.07 / 745.15 | 2,068.87 / 2,078.83 / 746.83 |
| TRST | bypass | 65,536 | 128,430 / 128,240 / 198,260 | 50,950 / 55,690 / 71,100 | 26,250 / 26,140 / 45,330 | 26,394.54 / 26,581.92 / 17,572.31 | 29,145.93 / 29,256.28 / 19,327.48 |

All 32 final session medians exceeded the corresponding original. Cached TRST 64 KiB reopen fell from 26,250 to 26,140 cycles. Cached TRST remount plus first open at 4 KiB fell from 128,480 to 128,240, and fresh startup plus first open fell from 400,790 to 399,180. In cached CSUN, first open at 64 KiB rose from 51,220 to 55,920 cycles and the warm 64-byte session changed from 30.56 to 30.50 KiB/s.

## Read loop

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 14,841,130 / 14,846,470 / 19,088,420 | 14,817,720 / 14,850,920 / 19,107,950 | 30.59 / 30.58 / 23.78 |
| CSUN | cached | 512 | 8,475,910 / 8,393,530 / 11,040,730 | 8,477,260 / 8,394,980 / 11,043,560 | 214.26 / 216.36 / 164.48 |
| CSUN | cached | 4,096 | 7,927,200 / 7,867,050 / 9,661,780 | 7,912,130 / 7,882,580 / 9,663,280 | 916.34 / 923.35 / 751.83 |
| CSUN | cached | 65,536 | 213,590 / 208,240 / 325,990 | 208,980 / 211,560 / 312,120 | 34,009.27 / 34,883.02 / 22,283.02 |
| CSUN | bypass | 64 | 14,836,590 / 14,854,450 / 19,089,380 | 14,827,960 / 14,838,680 / 19,099,110 | 30.60 / 30.56 / 23.78 |
| CSUN | bypass | 512 | 6,977,790 / 6,898,180 / 11,038,770 | 6,989,410 / 6,900,820 / 11,042,390 | 260.26 / 263.26 / 164.51 |
| CSUN | bypass | 4,096 | 3,497,770 / 3,456,320 / 9,662,250 | 3,497,760 / 3,457,370 / 9,661,730 | 2,076.76 / 2,101.67 / 751.80 |
| CSUN | bypass | 65,536 | 213,540 / 212,510 / 326,470 | 223,770 / 211,560 / 312,110 | 34,017.24 / 34,182.11 / 22,250.26 |
| TRST | cached | 64 | 14,813,990 / 14,800,400 / 19,089,380 | 14,824,650 / 14,806,150 / 19,105,300 | 30.65 / 30.68 / 23.78 |
| TRST | cached | 512 | 8,431,720 / 8,392,640 / 11,038,770 | 8,431,370 / 8,392,590 / 11,043,290 | 215.38 / 216.38 / 164.51 |
| TRST | cached | 4,096 | 7,889,730 / 7,877,830 / 9,662,250 | 7,904,210 / 7,870,660 / 9,663,690 | 920.70 / 922.09 / 751.80 |
| TRST | cached | 65,536 | 208,880 / 211,430 / 326,470 | 208,820 / 211,420 / 312,190 | 34,776.14 / 34,356.72 / 22,250.26 |
| TRST | bypass | 64 | 14,820,820 / 14,805,070 / 19,089,040 | 14,809,570 / 14,802,740 / 19,096,770 | 30.63 / 30.67 / 23.78 |
| TRST | bypass | 512 | 6,926,570 / 6,903,540 / 11,041,440 | 6,929,470 / 6,899,720 / 11,042,560 | 262.18 / 263.05 / 164.47 |
| TRST | bypass | 4,096 | 3,474,640 / 3,456,390 / 9,662,450 | 3,474,060 / 3,457,440 / 9,663,760 | 2,090.59 / 2,101.63 / 751.78 |
| TRST | bypass | 65,536 | 213,400 / 206,860 / 327,400 | 212,170 / 208,080 / 312,140 | 34,039.55 / 35,115.74 / 22,187.05 |

## Fresh startup

Startup begins after loading the handler HUNK and before DOS device registration.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 351,360 / 349,850 / 518,280 | 51,150 / 51,160 / 96,710 | 402,510 / 401,010 / 614,990 |
| CSUN | bypass | 351,940 / 350,530 / 518,280 | 51,150 / 51,160 / 96,710 | 403,090 / 401,700 / 614,990 |
| TRST | cached | 345,310 / 343,570 / 518,280 | 55,480 / 55,610 / 96,710 | 400,790 / 399,180 / 614,990 |
| TRST | bypass | 345,940 / 344,130 / 518,280 | 55,590 / 55,620 / 96,710 | 401,530 / 399,750 / 614,990 |

## Run identity

The previous and original controls are in `artifacts/ab33/runs`. Final manifests, phase traces and supplemental boots are in `artifacts/ab33`. The following SHA-256 digests identify the CSUN handlers.

| Handler | SHA-256 |
|---|---|
| Previous Copper | `197020A6D23AB2857365D791E674A05D5AE0C8E0FF68FFDC8D6DD72C49FC1F70` |
| Final Copper | `98766BEF80C867C131F8C1564309D55395F9DE425D54D07CAB8B109DE53EC507` |
| Original SFS | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

## Verification

Both standalone SFS2 handlers passed the CopperSharp compatibility check with zero managed allocation sites. The existing cursor generation and mutation invalidation paths still apply. Native replays covered exact-end and split Exec memory regions in both address orders, scratch and caller mappings one byte short, a destination overlapping the private file record, BSTR lengths across word and longword boundaries, and a mismatch inside the second word of a pair.

Every 64-byte stream used a single 64 KiB read-ahead request. All 96 final timed remounts issued two requests totaling 4,608 bytes and first open issued zero. For both variants and policies, all 64 KiB read traces transferred 1 MiB in sixteen direct-to-caller requests. Timed reopen and close issued zero requests. The full-ROM mutation check also passed twelve writer/remount boots with handoffs between Copper and original SFS.

The Release build passed, with 1,307 host tests passing and three external-media skips. CopperStart passed 157 native integration replays. Its cache-page-summary theory skipped on SFS2 and passed both cases on classic TRST.

The mixed native suite passed 200 of 204 cases per SFS2 variant with the workspace FFS handler. Four FFS timer-clock cases expected an inline request while the workspace handler allocated one. All four also failed against the pre-change FFS binary. With the matching inline-clock FFS test binary, all 204 cases passed for each of the same SFS2 binaries.
