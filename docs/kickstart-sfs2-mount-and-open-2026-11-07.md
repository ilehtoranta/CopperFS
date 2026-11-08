# SFS2 mount and first open under Kickstart

On this image the primary root is block 0, the root directory object is block 2, the clean marker is block 4, the node tree is block 6 and the directory container holding `Payload` is block 7. The backup root is block 32,767. All sectors are 512 bytes.

The previous Copper handler fetched individual metadata blocks. On remount it read 0, 32,767, 4 and 2. Looking up the name then read 7 and 6. Original SmartFilesystem 1.279 issued two mount requests, each for 16 sectors, covering 0–15 and 32,752–32,767. The sectors needed for the first open had already arrived. A fresh handler startup showed the same original read ranges and zero first-open requests.

## C# change

On an eligible fixed disk Copper now reads a bounded prefix into the mount's existing I/O buffer and retains nearby SFS2 metadata in its ordinary LRU. An initial SFS2 signature in the existing boot-probe scratch allows the normal mount cache to be initialized early. The reader keeps that primary block and can fetch its neighbors in one request. On remount it can fetch the primary and neighbors together. The 4 KiB cap covers up to eight 512-byte sectors or four 1 KiB sectors. Metadata elsewhere on the volume still uses demand reads.

An entry enters the LRU as unverified. The subsequent lookup still validates its checksum, type and own-block number. The read-ahead path checks the available buffers, device mask and maximum transfer size. It declines on active logical overlays or an occupied track window. On a failed speculative transfer, required blocks can still be read individually. All retained entries use the existing write, recovery and media invalidation paths.

Root selection compares both copies before materializing the selected image for callers that need it. Remount can borrow the verified root object when it needs only the lookup fields. During initial format detection a secondary root rejected by the classic type check can be retained unverified for the SFS2 validator.

## Timing boundaries

The benchmark runs real DOS calls on a PAL M68020 with Kickstart 3.1 and synchronous CopperHDF. The before and after handlers use matched working-tree sources and the same CopperSharp68k executable. All arms run the same C# probe and starting media. Copper's payload policy is a mount option. The original uses its native policy.

* **Fresh startup** starts immediately before DOS device registration, after loading the handler HUNK. The mount interval ends when `DeviceProc` returns. The next interval ends when the first `DOS.Open` **returns**. Each median uses three independent machine boots.
* **Remount** starts immediately before uninhibit, after the prior inhibit finishes. The combined timer ends when the following first open returns. A primed workload then closes the file before the separately timed reopen. The raw results record guest cycles, device command counts and byte ranges. The profiler also records each command's starting offset and total length.
* **Whole-session throughput** covers timed open, read loop and close. The four workloads read 64 KiB, 256 KiB, 1 MiB and 1 MiB in calls of 64 bytes, 512 bytes, 4 KiB and 64 KiB, respectively. All phase and session medians use three trials per arm. Combined medians use complete intervals from individual trials.

## Device traffic and CPU work

| Interval | Handler | Mount requests | First-open requests | Combined bytes |
|---|---|---:|---:|---:|
| Fresh startup | Previous Copper | 10 | 2 | 6,144 |
| Fresh startup | Optimized Copper | 3 | 0 | 4,608 |
| Fresh startup | Original SFS | 2 | 0 | 16,384 |
| Remount | Previous Copper | 4 | 2 | 3,072 |
| Remount | Optimized Copper | 2 | 0 | 4,608 |
| Remount | Original SFS, usual case | 2 | 0 | 16,384 |

Optimized fresh startup read sector 0, sectors 1–7, and sector 32,767. Remount read sectors 0–7 and sector 32,767. In four of the original's 96 full-workload remount trials, it also fetched sectors 16–31, bringing the total to three requests and 24,576 bytes. Its first open issued zero reads in every trial.

The 64 KiB bypass instruction traces below count cycles executing inside the handler, excluding Kickstart and the rest of the DOS operation. Columns show **previous Copper / optimized Copper / original SFS**. Original SFS still executed fewer handler cycles. The end-to-end comparison also includes OS execution and waiting.

| Build | Startup mount + first open, handler cycles | Remount + first open, handler cycles |
|---|---:|---:|
| CSUN | 215,464 / 189,937 / 124,591 | 110,907 / 106,827 / 88,424 |
| TRST | 213,850 / 188,164 / 124,643 | 111,094 / 106,432 / 88,424 |

## Fresh startup

Median DOS guest bus cycles, **previous / optimized / original**. These independent intervals start before device registration and include handler startup.

| Build | Payload | Mount cycles | First open cycles | Combined cycles |
|---|---|---:|---:|---:|
| CSUN | cached | 434,450 / 414,990 / 518,280 | 64,300 / 61,670 / 96,710 | 498,750 / 476,660 / 614,990 |
| CSUN | bypass | 434,980 / 415,630 / 518,280 | 64,310 / 61,690 / 96,710 | 499,290 / 477,320 / 614,990 |
| TRST | cached | 432,340 / 426,190 / 518,280 | 69,220 / 61,610 / 96,710 | 501,560 / 487,800 / 614,990 |
| TRST | bypass | 432,960 / 426,810 / 518,280 | 69,240 / 61,600 / 96,700 | 502,200 / 488,410 / 614,990 |

## Remount, first open and reopen

Each timing or throughput cell shows **previous / optimized / original**. Cycle counts are guest bus cycles and throughput is KiB/s. Combined remount-and-first-open time improved over previous Copper in all 16 workload and cache-policy combinations. All 16 optimized first-open and reopen medians were below the original. Read-loop and close costs still affect session throughput. For example, at 64 KiB with bypass, CSUN's first-open session went from 16,986 to 16,862 KiB/s while TRST went from 16,451 to 17,421 KiB/s.

| Build | Payload | Read bytes | Remount + first open cycles | First open cycles | Reopen cycles | First session KiB/s | Reopen session KiB/s |
|---|---|---:|---:|---:|---:|---:|---:|
| CSUN | cached | 64 | 153,370 / 148,440 / 212,890 | 69,120 / 61,750 / 71,100 | 32,910 / 32,910 / 45,320 | 23.92 / 23.95 / 23.68 | 23.96 / 23.97 / 23.68 |
| CSUN | cached | 512 | 154,570 / 148,430 / 193,560 | 65,680 / 57,170 / 66,520 | 32,910 / 32,910 / 45,330 | 164.74 / 165.21 / 163.27 | 165.22 / 165.55 / 163.52 |
| CSUN | cached | 4,096 | 154,510 / 148,380 / 205,290 | 65,660 / 61,780 / 71,100 | 34,140 / 32,910 / 45,330 | 784.97 / 786.55 / 745.02 | 787.23 / 789.53 / 747.04 |
| CSUN | cached | 65,536 | 153,230 / 148,390 / 199,430 | 64,370 / 61,770 / 72,370 | 32,910 / 32,910 / 50,030 | 16,419.25 / 16,862.53 / 17,571.46 | 17,675.79 / 17,923.07 / 18,801.71 |
| CSUN | bypass | 64 | 153,200 / 148,420 / 212,890 | 68,950 / 57,170 / 71,100 | 32,910 / 32,910 / 45,330 | 23.89 / 23.96 / 23.67 | 23.96 / 23.99 / 23.70 |
| CSUN | bypass | 512 | 154,570 / 148,430 / 194,920 | 68,950 / 57,170 / 66,520 | 32,910 / 32,910 / 45,330 | 190.02 / 190.71 / 163.26 | 190.75 / 191.18 / 163.57 |
| CSUN | bypass | 4,096 | 153,190 / 148,430 / 211,450 | 64,370 / 57,170 / 71,100 | 32,910 / 32,910 / 45,330 | 1,498.11 / 1,506.30 / 745.05 | 1,513.24 / 1,510.94 / 746.91 |
| CSUN | bypass | 65,536 | 153,230 / 148,430 / 193,610 | 68,970 / 57,170 / 71,120 | 37,520 / 32,910 / 45,330 | 16,985.55 / 16,862.14 / 17,632.02 | 17,673.21 / 17,922.19 / 19,330.05 |
| TRST | cached | 64 | 154,570 / 148,100 / 207,300 | 65,950 / 57,030 / 66,520 | 32,870 / 32,870 / 45,320 | 23.94 / 23.89 / 23.68 | 23.99 / 23.90 / 23.69 |
| TRST | cached | 512 | 153,370 / 149,380 / 198,150 | 64,710 / 57,030 / 66,520 | 32,860 / 32,870 / 45,330 | 164.90 / 164.62 / 163.26 | 165.35 / 164.94 / 163.62 |
| TRST | cached | 4,096 | 153,430 / 148,070 / 211,450 | 69,390 / 57,030 / 71,100 | 32,870 / 32,880 / 45,330 | 785.31 / 785.60 / 745.05 | 788.89 / 787.53 / 746.79 |
| TRST | cached | 65,536 | 160,400 / 155,090 / 193,650 | 76,360 / 62,990 / 66,520 | 32,870 / 32,880 / 45,330 | 16,399.98 / 16,821.53 / 17,635.88 | 17,672.35 / 17,829.81 / 19,335.20 |
| TRST | bypass | 64 | 154,580 / 148,120 / 212,890 | 69,340 / 58,270 / 71,100 | 38,680 / 37,450 / 45,320 | 23.92 / 23.88 / 23.66 | 23.98 / 23.92 / 23.69 |
| TRST | bypass | 512 | 153,340 / 149,360 / 194,850 | 64,720 / 57,030 / 66,520 | 32,870 / 32,870 / 45,330 | 190.13 / 190.02 / 163.27 | 190.78 / 190.51 / 163.57 |
| TRST | bypass | 4,096 | 154,560 / 149,330 / 212,970 | 64,710 / 61,620 / 71,090 | 32,860 / 32,870 / 49,870 | 1,497.43 / 1,497.68 / 745.13 | 1,507.76 / 1,507.75 / 746.97 |
| TRST | bypass | 65,536 | 158,030 / 148,140 / 198,210 | 69,290 / 61,640 / 71,080 | 32,870 / 32,880 / 45,320 | 16,451.23 / 17,421.43 / 17,591.88 | 18,337.05 / 18,495.33 / 19,325.43 |

## Verification

Both SFS2 native builds passed all 120 packet replays across the 68000 and 68020 suite. The mount-specific replay tested a valid prefetched node, a directory container outside the prefix, a failing speculative sector and a staged corrupt replacement. Both compiler reports list zero managed allocation sites. CopperStart passed 157 native integration checks with one classic-only skip.

The full-ROM A/B included 288 timed workloads, plus 12 additional independent startup repetitions. In all, 36 fresh startups were measured. Another 12 writer/remount boots checked the Copper/original file handoffs and primed-path deletion, rename and truncation. File manifests matched.
The Release solution built. 1,307 host tests passed, including selected-root copy and mixed-format checks for borrowed and scratch-backed providers. 3 external-media tests skipped.
