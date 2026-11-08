# Kickstart 64 KiB sequential reads

At 64 KiB, end-to-end throughput with CopperHDF payload bypass improved by 10.6% for FFS and 25.5–25.6% for SFS2. Copper now reaches 73.5% of original FFS throughput and about 69% of original SFS2 throughput. The remaining FFS gap is mainly in the read loop. On SFS2, opening the file still costs slightly more extra time than reading the entire 1 MiB.

## Where the time went

The probe opened a 1 MiB file, made sixteen consecutive 64 KiB DOS reads, and closed it. Each trial remounted the volume before timing. The table gives guest bus cycles from one instrumented run of the preceding Copper handlers with CopperHDF payload bypass and the two unmodified originals. The original SFS2 binary was independently profiled for each Copper journal build.

| Handler | Open | Read loop | Close | Handler self cycles in read loop |
|---|---:|---:|---:|---:|
| FastFileSystem 40.1 | 50,910 | 568,430 | 17,880 | 365,864 |
| Previous CopperFFS | 95,410 | 863,950 | 16,180 | 713,805 |
| SmartFilesystem 1.279 (CSUN comparison) | 71,080 | 310,840 | 14,860 | 141,615 |
| Previous CopperSFS2 CSUN | 244,470 | 454,210 | 22,500 | 327,271 |
| SmartFilesystem 1.279 (TRST comparison) | 71,060 | 310,820 | 14,860 | 141,613 |
| Previous CopperSFS2 TRST | 249,440 | 452,610 | 17,990 | 327,081 |

FFS spent 295,520 extra cycles in the read loop. Both handlers issued the same 43 direct payload reads and 28 pointer-table reads, transferred all 1 MiB directly to the caller, and executed zero recognized MOVE bytes into that buffer. Copper's general FFS reader took 192,892 exclusive cycles. Its leading instruction sites executed 2,019 times in the pointer scan. Block copies took another 70,840 cycles and metadata checksums 66,788. With the normal cache, short runs at table boundaries also copied payload and the loop took 977,120 cycles.

On SFS2-CSUN, open accounted for 173,390 extra cycles, the read loop for 143,370 and close for 7,640. Open issued zero device reads on both handlers. Copper spent 74,983 open cycles checksumming metadata and 46,295 copying blocks already in memory. Path resolution and description reopened the same volume and looked up the same object repeatedly. The read loop also paid for first-read metadata validation, the private cursor adapter and software 64-bit division. Both handlers already transferred the full payload in sixteen direct device reads. The TRST profile showed the same pattern.

## Changes

- FFS reuses the validated matching header during pathname traversal. The reader bounds its pointer scan by the table and disk once, checks consecutive pointers in groups of four and stops at the first discontinuity. On 512-byte volumes it uses shifts and a mask for sector arithmetic.
- SFS2's standalone open describes the final checked object while it is still in caller-owned scratch. On the first read, the reader uses that same-call object lookup for both the file size and first extent. Sector arithmetic on 512-byte volumes keeps the full 64-bit position.
- Existing cache policy, read-ahead, logical write overlays, EOF handling and short-transfer paths remain in use. These C# changes add no native storage or heap allocation. The existing CopperSharp68k compiler generated all three MC68000 HUNKs.

## Phase timings after optimization

Median guest bus cycles for 64 KiB DOS reads with payload bypass. Each cell lists previous Copper → current Copper → original. Each phase has three observations. The end-to-end rates below use the median of complete sessions.

| Handler | Open | Sixteen reads | Close |
|---|---:|---:|---:|
| FFS | 95,510 → 73,340 → 50,910 | 863,760 → 797,410 → 579,290 | 16,180 → 16,130 → 17,880 |
| SFS2 CSUN | 245,660 → 156,300 → 71,050 | 454,240 → 385,400 → 310,860 | 17,920 → 17,980 → 14,860 |
| SFS2 TRST | 249,510 → 157,380 → 71,050 | 454,240 → 384,760 → 310,880 | 17,990 → 18,000 → 14,860 |

With normal caching, the optimized FFS open/read/close medians were 73,340 / 903,770 / 16,130 cycles. SFS2-CSUN used 157,680 / 400,070 / 17,980 and TRST used 160,650 / 386,100 / 18,000. Both SFS2 policies still issued sixteen 64 KiB reads. FFS issued 71 read-loop requests on both policies, transferring 1,278 KiB cached and 1,038 KiB with bypass. Request counts and device bytes matched the preceding Copper build at every size under each policy.

In the optimized scalar FFS profile, the general reader and pointer scanner together used 155,833 exclusive cycles, down from 192,892 in the previous reader. Metadata copies and checksums still used 70,840 and 66,700 cycles with bypass. The original spent 365,864 cycles in handler code across the read loop, compared with 647,282 for current Copper. The original and Copper sent identical payload and metadata requests on this run.

On SFS2-CSUN, open-phase checksum and block-copy costs fell from 74,983 / 46,295 to 39,657 / 26,783 cycles. The 64-bit division helper disappeared from the measured loop. Checksum, metadata copies and the private cursor adapter still cost 35,252, 21,992 and 31,616 cycles in that loop. Original SFS2 used 141,615 handler cycles in the read loop, compared with 273,027 for current Copper. The original binaries have no method maps here. Their instruction offsets, handler totals and observed device transfers support this comparison without assigning guessed names to original routines.

## All four Kickstart read sizes

Median complete open/read/close throughput in KiB/s, three remounted trials per cell. The two original columns are independent reruns of the same unmodified handler with its own cache policy. The 64-byte workload reads 64 KiB in total, the 512-byte workload reads 256 KiB, and the two larger workloads each read 1 MiB.

| Handler | Read bytes | Previous cached | Current cached | Original | Previous bypass | Current bypass | Original |
|---|---:|---:|---:|---:|---:|---:|---:|
| FFS | 64 | 27.16 | 27.93 | 35.83 | 27.15 | 27.94 | 35.83 |
| FFS | 512 | 209.33 | 210.40 | 241.92 | 250.25 | 252.07 | 241.82 |
| FFS | 4,096 | 841.53 | 849.53 | 1,823.09 | 1,719.86 | 1,754.26 | 1,823.72 |
| FFS | 65,536 | 6,670.56 | 7,347.36 | 11,202.85 | 7,445.49 | 8,233.07 | 11,208.56 |
| SFS2 CSUN | 64 | 21.66 | 24.18 | 23.68 | 21.68 | 24.18 | 23.68 |
| SFS2 CSUN | 512 | 149.10 | 165.00 | 163.29 | 168.59 | 189.38 | 163.28 |
| SFS2 CSUN | 4,096 | 727.51 | 778.15 | 744.97 | 1,300.18 | 1,471.21 | 745.34 |
| SFS2 CSUN | 65,536 | 10,073.97 | 12,645.65 | 18,252.28 | 10,073.28 | 12,648.95 | 18,250.90 |
| SFS2 TRST | 64 | 21.64 | 24.14 | 23.68 | 21.65 | 24.11 | 23.68 |
| SFS2 TRST | 512 | 149.03 | 164.74 | 163.23 | 168.58 | 188.89 | 163.26 |
| SFS2 TRST | 4,096 | 727.58 | 776.26 | 745.03 | 1,300.21 | 1,467.76 | 744.95 |
| SFS2 TRST | 65,536 | 10,260.67 | 12,636.85 | 18,252.28 | 10,067.41 | 12,636.63 | 18,252.28 |

## Reproduction and coverage

The configuration uses Kickstart 3.1, PAL M68020 at 7,093,790 Hz, 512 KiB Chip RAM, 2 MiB Fast RAM, 512-byte sectors, and 64 KiB MaxTransfer. Each filesystem's probe and fixture hashes matched across all arms. FFS and original SFS2 used caller address `0x215470`. Both current Copper SFS2 builds used `0x215670`. Previous Copper TRST used `0x215470` and previous CSUN used `0x215670`. All are longword-aligned in Fast RAM. Device traffic and guest bus cycles qualify the synchronous CopperHDF model. Physical A1200 IDE timing was outside this run.

The current source snapshot starts from the preceding measured build and incorporates only the three changed filesystem source files. This isolates the read changes from concurrent writer work in the live tree. The compiler executable is identical. The rebuilt SDK/compiler metadata inputs have matching method tokens, names, signatures and IL across builds. `artifacts/ab13-final` records the inputs, compile commands, maps and compatibility reports for each HUNK.

- **Host.** Release solution build passed. 1,266 tests passed and three external-media cases skipped. Coverage includes discontinuities in each unrolled pointer position, out-of-volume SFS2 sizes, alternate block sizes and positions crossing 4 GiB.
- **Native.** All three HUNKs reported zero managed allocation sites. All 120 packet replays passed across MC68000/020 and the two SFS2 builds. CopperStart's OS-consumer replay passed 157 tests with one classic-SFS-only skip. An initial integration run built the consumer with standalone SFS2 flags. Rebuilding it with its OS extension profile and rerunning the same suite corrected that setup mismatch.
- **Full ROM.** 144 current-versus-original throughput trials and 72 preceding-Copper trials passed at the four read sizes. Every payload hash matched, benchmark media stayed read-only, and the recorded phases summed to the session in every trial. Separately, both original and Copper wrote the test volume and each reader reopened each writer's image under Kickstart. All 18 correctness boots across FFS, SFS2-CSUN and SFS2-TRST passed with matching file manifests.

Run `tools/Benchmark-KickstartSequentialReads.ps1` for each payload policy with the two original handlers and the frozen native manifest. For instruction profiles, run the throughput test directly against a copy of that manifest in a fresh directory with `COPPERFS_KICKSTART_PROFILE=1` and `COPPERFS_KICKSTART_PROFILE_PACKET=65536`. Normal A/B timings use the standard emulator frame driver. [Harness instructions](../tests/CopperFS.Kickstart.Tests/README.md)

Raw results and ranges are under `artifacts/kickstart-ab13-{cached,bypass}`, `artifacts/ab13-previous-{cached,bypass}`, and `artifacts/ab13-analysis/measurements.json`. Paired instruction and device traces are under `artifacts/ab13-{baseline,final}-profile`, with the additional cached FFS traces under `artifacts/ab13-{baseline,final}-cached-profile`. Replay and cross-reader receipts are under `artifacts/ab13-validation`.
