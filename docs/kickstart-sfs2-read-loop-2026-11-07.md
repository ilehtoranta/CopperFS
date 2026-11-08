# SFS2 sequential read loop under Kickstart

The native SFS2 file record already stores the validated extent, its logical start and length, the file size and a node key. Previously each packet reconstructed the portable traversal cursor from that record and wrote its fields back after the read, including packets that fit entirely inside the same extent.

The C# native adapter now checks whether an aligned packet on a 512-byte-block volume fits within the saved extent, the 64-bit file size and the usable disk range. A hit goes through the existing logical block reader. That reader continues to handle pending writes, small-read caching, payload-cache bypass, DMA restrictions, transfer splitting and TD64 offsets. A partial device failure returns the completed byte count and clears the cursor key.

An empty or invalidated cursor, extent boundary, EOF tail, unaligned buffer or overlap with the private file record or scratch uses the existing traversal path. The optimization uses the current handle fields and requires no new mount or handle storage.

## Benchmark

The previous and optimized Copper handlers were rebuilt from matched source snapshots with the same CopperSharp68k executable. The reference is original SmartFilesystem 1.279. All arms use the same C# guest probe, Kickstart 3.1, PAL M68020 and synchronous CopperHDF. The payload-cache policy applies to the Copper arms. The original uses its native policy.

The probe times DOS open, the read loop and close individually and verifies the full returned byte stream. The workloads read 64 KiB in 64-byte calls, 256 KiB in 512-byte calls, and 1 MiB in either 4 KiB or 64 KiB calls. Each table entry is the median of three uninstrumented trials. First open follows remount. Reopen adds an untimed open and close of the same path. Fresh-startup timing begins after loading the handler HUNK and before DOS device registration.

## Where the 64 KiB loop spent its time

On the CSUN bypass trace the previous adapter and portable reader used 31,616 and 49,251 exclusive handler cycles respectively. Both Copper and original SFS sent 16 device requests directly to the caller for the full 1 MiB. Copper also read the 512-byte extent-tree block at sector 5 on its first read. The saved-extent path kept that on-demand read and eliminated 15 trips through the general reader.

| 64 KiB bypass trace | General-reader calls, before → after | Handler cycles, before → after | Original handler cycles | Total guest loop cycles, before → after / original |
|---|---:|---:|---:|---:|
| CSUN | 16 → 1 | 228,575 → 173,990 | 141,508 | 360,930 → 295,820 / 327,460 |
| TRST | 16 → 1 | 229,570 → 174,215 | 141,507 | 337,280 → 296,700 / 325,940 |

Copper now completes the guest read loop faster while still executing more cycles inside the handler. On the CSUN trace about 121,600 instruction cycles were attributed outside optimized Copper and about 185,800 outside original SFS. The original HUNK lacks method symbols. The timings below use ordinary, uninstrumented emulator execution.

## All four read sizes

All timing and throughput triples are **previous Copper / optimized Copper / original SFS**. The read loop improved against both comparison arms in all 32 combinations of SFS2 build, payload policy, packet size and first open or reopen. With 64 KiB packets after first open, guest read time fell by 13.6–20.1% against previous Copper. In the bypass workload, full-session throughput rose to 19,629 KiB/s on CSUN and 19,305 KiB/s on TRST, versus about 17,600 KiB/s on original SFS.

| Build | Payload | Read bytes | Read-loop cycles, first open | Read-loop cycles, reopen | Read-loop KiB/s, first open |
|---|---|---:|---:|---:|---:|
| CSUN | cached | 64 | 18,876,260 / 18,737,300 / 19,087,970 | 18,890,210 / 18,741,830 / 19,101,900 | 24.05 / 24.23 / 23.78 |
| CSUN | cached | 512 | 10,918,430 / 8,943,710 / 11,042,970 | 10,919,160 / 8,949,220 / 11,037,230 | 166.33 / 203.05 / 164.45 |
| CSUN | cached | 4,096 | 9,155,490 / 8,174,170 / 9,662,220 | 9,149,930 / 8,174,380 / 9,663,460 | 793.41 / 888.66 / 751.80 |
| CSUN | cached | 65,536 | 351,000 / 280,430 / 327,320 | 355,620 / 288,330 / 312,140 | 20,695.27 / 25,903.22 / 22,192.48 |
| CSUN | bypass | 64 | 18,878,710 / 18,740,970 / 19,085,180 | 18,875,860 / 18,739,460 / 19,098,970 | 24.05 / 24.23 / 23.79 |
| CSUN | bypass | 512 | 9,444,470 / 7,467,050 / 11,041,610 | 9,440,540 / 7,478,350 / 11,039,760 | 192.28 / 243.20 / 164.47 |
| CSUN | bypass | 4,096 | 4,741,220 / 3,763,810 / 9,662,570 | 4,754,640 / 3,763,990 / 9,661,130 | 1,532.10 / 1,929.97 / 751.77 |
| CSUN | bypass | 65,536 | 351,040 / 295,210 / 327,330 | 354,440 / 295,220 / 312,080 | 20,692.92 / 24,606.35 / 22,191.80 |
| TRST | cached | 64 | 18,921,580 / 18,724,780 / 19,085,290 | 18,941,950 / 18,721,650 / 19,096,010 | 23.99 / 24.25 / 23.79 |
| TRST | cached | 512 | 10,953,330 / 8,947,780 / 11,037,830 | 10,952,030 / 8,965,400 / 11,038,840 | 165.80 / 202.96 / 164.53 |
| TRST | cached | 4,096 | 9,170,780 / 8,187,430 / 9,663,330 | 9,168,210 / 8,178,940 / 9,663,300 | 792.09 / 887.22 / 751.71 |
| TRST | cached | 65,536 | 341,930 / 295,520 / 325,980 | 352,070 / 281,900 / 312,090 | 21,244.23 / 24,580.54 / 22,283.70 |
| TRST | bypass | 64 | 18,927,610 / 18,717,740 / 19,078,850 | 18,921,300 / 18,721,420 / 19,103,620 | 23.99 / 24.26 / 23.80 |
| TRST | bypass | 512 | 9,481,570 / 7,488,140 / 11,041,590 | 9,480,990 / 7,491,870 / 11,040,320 | 191.53 / 242.52 / 164.47 |
| TRST | bypass | 4,096 | 4,770,670 / 3,763,800 / 9,662,750 | 4,766,480 / 3,761,870 / 9,664,350 | 1,522.65 / 1,929.98 / 751.76 |
| TRST | bypass | 65,536 | 337,290 / 280,690 / 325,980 | 341,840 / 295,440 / 326,510 | 21,536.48 / 25,879.23 / 22,283.70 |

## Mount, open and full-session results

Both new handlers retained the three-request fresh mount, two-request remount and zero-device-read first open on this fixture. Remount plus first open and separately timed reopen remained faster than original SFS in all 16 workload/policy comparisons. Some individual medians increased versus the previous Copper binary. At 64 KiB with bypass, CSUN fresh startup plus first open rose from 477,400 to 484,090 cycles and its reopen rose from 32,910 to 37,500. Original SFS took 614,990 and 45,330 respectively. First open alone was faster than original SFS in 15 of 16 comparisons. The CSUN cached 512-byte case took 68,610 cycles versus 66,520.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 414,990 / 426,590 / 518,280 | 61,670 / 56,860 / 96,710 | 476,660 / 483,450 / 614,990 |
| CSUN | bypass | 415,710 / 427,230 / 518,280 | 61,690 / 56,860 / 96,710 | 477,400 / 484,090 / 614,990 |
| TRST | cached | 426,190 / 413,860 / 518,280 | 61,610 / 56,940 / 96,710 | 487,800 / 470,800 / 614,990 |
| TRST | bypass | 426,810 / 414,410 / 518,280 | 61,600 / 56,940 / 96,710 | 488,410 / 471,350 / 614,990 |

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 148,390 / 147,870 / 212,890 | 61,750 / 61,500 / 71,100 | 32,910 / 32,940 / 45,330 | 23.95 / 24.13 / 23.66 | 23.97 / 24.15 / 23.69 |
| CSUN | cached | 512 | 148,410 / 159,580 / 194,770 | 57,170 / 68,610 / 66,520 | 32,910 / 32,940 / 45,330 | 165.19 / 200.90 / 163.22 | 165.54 / 201.78 / 163.62 |
| CSUN | cached | 4,096 | 148,360 / 149,070 / 212,940 | 61,750 / 56,920 / 71,190 | 32,910 / 32,940 / 45,320 | 786.56 / 880.09 / 745.16 | 789.33 / 882.96 / 746.70 |
| CSUN | cached | 65,536 | 148,400 / 149,280 / 193,540 | 61,790 / 56,920 / 71,090 | 32,910 / 37,580 / 45,330 | 16,862.53 / 20,372.56 / 17,576.56 | 17,868.84 / 21,121.92 / 19,334.68 |
| CSUN | bypass | 64 | 148,390 / 147,920 / 212,890 | 57,160 / 61,550 / 71,100 | 32,910 / 32,930 / 45,330 | 23.95 / 24.12 / 23.66 | 23.99 / 24.16 / 23.69 |
| CSUN | bypass | 512 | 148,430 / 147,950 / 194,990 | 57,160 / 61,510 / 66,520 | 32,910 / 32,930 / 45,320 | 190.71 / 240.13 / 163.25 | 191.24 / 240.93 / 163.57 |
| CSUN | bypass | 4,096 | 148,400 / 149,120 / 212,960 | 61,740 / 58,170 / 71,080 | 37,480 / 32,940 / 49,900 | 1,503.88 / 1,884.35 / 745.14 | 1,511.60 / 1,903.51 / 746.92 |
| CSUN | bypass | 65,536 | 148,440 / 147,880 / 198,220 | 57,170 / 56,920 / 71,080 | 32,910 / 37,500 / 45,330 | 16,863.31 / 19,628.83 / 17,575.28 | 17,920.86 / 20,714.75 / 19,330.57 |
| TRST | cached | 64 | 148,100 / 148,110 / 212,870 | 57,030 / 61,620 / 71,100 | 32,870 / 34,130 / 45,320 | 23.90 / 24.14 / 23.67 | 23.90 / 24.18 / 23.69 |
| TRST | cached | 512 | 149,330 / 162,870 / 199,600 | 57,040 / 57,010 / 71,100 | 32,880 / 32,890 / 45,330 | 164.62 / 201.17 / 163.25 | 164.99 / 201.41 / 163.62 |
| TRST | cached | 4,096 | 148,100 / 148,130 / 211,450 | 57,040 / 61,630 / 71,100 | 37,450 / 32,890 / 45,330 | 785.61 / 878.54 / 745.28 | 787.54 / 882.15 / 746.79 |
| TRST | cached | 65,536 | 155,090 / 148,140 / 193,580 | 63,000 / 57,010 / 71,140 | 32,870 / 32,890 / 45,330 | 16,820.75 / 19,603.93 / 17,631.17 | 17,824.12 / 20,901.91 / 19,335.20 |
| TRST | bypass | 64 | 148,130 / 148,110 / 212,880 | 58,290 / 61,580 / 71,090 | 37,530 / 32,890 / 45,330 | 23.88 / 24.13 / 23.67 | 23.92 / 24.18 / 23.69 |
| TRST | bypass | 512 | 149,340 / 149,420 / 194,820 | 57,030 / 57,010 / 71,120 | 32,870 / 32,900 / 45,330 | 190.02 / 240.11 / 163.25 | 190.52 / 240.76 / 163.53 |
| TRST | bypass | 4,096 | 153,890 / 148,090 / 212,830 | 61,610 / 61,590 / 71,110 | 32,880 / 32,890 / 49,930 | 1,497.65 / 1,890.00 / 745.20 | 1,507.24 / 1,904.60 / 746.80 |
| TRST | bypass | 65,536 | 148,120 / 155,220 / 193,610 | 61,620 / 61,580 / 71,140 | 32,880 / 32,890 / 45,330 | 17,420.60 / 19,305.39 / 17,631.17 | 18,496.27 / 20,699.99 / 18,783.72 |

## Verification

Both optimized SFS2 HUNKs passed 128 native packet replays across the 68000 and 68020 suite. The added cases checked an aligned 64 KiB packet spanning 4 GiB, the physical starting block, EOF, an invalid seek, and preservation of all cursor words when the destination aliases the file record. Existing replays cover fragmented extents, invalidation, staged data, MaxTransfer, short transfers and small-read read-ahead. Both compiler compatibility reports show zero managed allocation sites.

The Kickstart A/B included 288 timed workloads and 12 additional startup repetitions, for 36 measured fresh startups. Another 12 writer/remount boots verified interoperability with original SmartFilesystem, including deletion, rename and truncation after a priming open. Both variants produced matching file manifests.

CopperStart passed 157 native integration checks, with one classic-only skip.
The Release solution built. 1,307 host tests passed and 3 external-media tests skipped.
