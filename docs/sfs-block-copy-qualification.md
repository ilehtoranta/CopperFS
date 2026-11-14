# SFS block-copy qualification

The revised shared-pool handler moved 62,808 fewer bytes through its block-copy routine during mixed mutation. The reduction came from three changes within the existing mount allocation.

* During TRST encoding a retained original is still borrowed first. On a miss, the standalone shared-pool profile reads the requested sector into existing journal-encoding scratch through the normal DMA transfer path. The encoder finishes comparing the original before another access can expire it. Logical overlays, pending writes and RAM mounts retain the checked fallback. The previous profile copied 67 sectors into the LRU while reading originals for commit.
* Bitmap availability, free-node leaf searches and node-fullness checks now borrow the verified image and consume the needed fields before another block access. Paths that change a node still acquire writable storage.
* A date or protection update may reuse the existing accepted object image under exclusive ownership. The code checks the date, object, block bounds and exact accepted slot before changing the field and its checksum. The set of journal targets and the worst-case space reservation stay the same. A new target or a borrowed scratch image follows the ordinary trial path. Larger edits still get a private copy so rejected packets can roll back.

## Instruction profile

The baseline was the saved 24-write CopperSFS binary. Both builds ran the same benchmark with the same instruction profiler. Counts and byte totals below are actual entries to the native block-copy routine. Cycles are exclusive instruction cycles for that routine attributed to the return address.

| Mixed mutation | Previous calls | Revised calls | Previous bytes | Revised bytes | Previous cycles | Revised cycles |
|---|---:|---:|---:|---:|---:|---:|
| Cache insertion | 114 | 46 | 58,368 | 23,552 | 282,589 | 113,913 |
| Private metadata edit | 92 | 85 | 47,104 | 43,520 | 228,063 | 210,742 |
| Cached read into caller storage | 79 | 33 | 40,448 | 16,896 | 196,187 | 81,869 |
| **All callers** | **591** | **463** | **217,866** | **155,058** | **1,141,461** | **835,334** |

The two complete commits fell from 1,304,724 to 941,058 inclusive cycles. All handler instructions during mutation fell from 6,708,081 to 6,145,650 cycles. These totals overlap the copy attribution above. Private edits remain the largest of the three copy sites, at 85 sectors copied to preserve either the accepted checkpoint or an immutable lower-layer image.

## I/O and memory

The final media hash was identical to the saved candidate. Both wrote 147,456 bytes in 24 commands with eight flushes. Original SFS 1.279 wrote 150,016 bytes in 17 commands with seven flushes. In the mixed mutation phase with mount measured separately, device read commands increased from 14 to 19 against the original's five. Across the complete instrumented device trace, including mount and verification, bytes read fell from 202,240 to 168,448 against the original's 183,296. Physical command latency is unmeasured.

Exec free-memory samples at 32 DOS buffers gave 126,008 bytes retained at mount and 138,888 after mixed work for both CopperSFS builds. Original SFS retained 156,480 and 172,904 bytes respectively. The revised M68000 HUNK is 167,016 bytes (+468) with 163,016 bytes of code (+344). CILica scalar replacement was enabled and inlining disabled.

## Ordinary 68020 timing

Three alternating runs per arm used Kickstart 3.1, the same 16 MiB media and 32 DOS buffers. The paired workload measures mount separately and includes the last explicit flush in mutation. Timing uses the ordinary emulator frame runner.

| Workload cycles | Original SFS | Previous CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast mixed mutation | 16,985,523 | 18,380,007 | 17,797,613 |
| Chip mixed mutation | 17,006,223 | 18,382,030 | 17,799,360 |
| Fast mixed verification | 18,309,757 | 18,953,777 | 18,907,170 |
| Chip mixed verification | 18,351,570 | 19,038,493 | 18,987,697 |
| Fast growth, overwrite and verification through commits | 5,760,173 | 6,563,923 | 6,515,063 |
| Chip growth, overwrite and verification through commits | 5,808,770 | 6,569,800 | 6,520,650 |
| Fast isolated 96 KiB overwrite and flush | 325,330 | 892,820 | 912,697 |
| Chip isolated 96 KiB overwrite and flush | 371,810 | 894,550 | 914,640 |

The isolated overwrite took 2.23% more Fast RAM cycles and 2.25% more Chip RAM cycles than the previous candidate. It issued the same 17 writes and four flushes on both CopperSFS builds. A separate instruction replay reproduced a 19,940-cycle increase. Kickstart accounted for 17,547 additional cycles and handler code for 1,894. The complete growth/overwrite/verify workload improved by 0.74–0.75%.

| Independent release gate | Original cycles | Revised cycles | Above original |
|---|---:|---:|---:|
| Fast mixed mutation including mount | 17,506,823 | 18,144,720 | 3.644% |
| Chip mixed mutation including mount | 17,503,020 | 18,154,947 | 3.725% |
| Fast verification | 18,307,997 | 18,900,417 | 3.236% |
| Chip verification | 18,352,563 | 18,987,903 | 3.462% |

Both RAM classes miss the 0.1% gate. The shared-pool profile stays opt-in.

## Recovery and compatibility

On Kickstart 3.1, CopperSFS and original SFS recovered the preceding group after a cut inside each of the three external journal writes. After a cut in each published group's home writes they recovered the new group. The grouped suite passed all 12 barrier failure, retry and publication cases. The aligned and partial overwrite suite replayed 42 candidate prefixes with original SFS, including device-write and explicit-flush failures. Contents were wholly old or wholly new, with the expected neighbors and allocation counts.

The 22 lifecycle, capacity and instruction cases passed, covering original-SFS handoff, deferred close, explicit flush, timer, inhibit/remount, harmless rejection, directory enumeration and allocation fallback. Both new host regression cases passed with borrowed or scratch-backed object views. They exercise a successful pending protection/date change, rejection of a bad date, error ordering for a missing object, and visibility after explicit flush. Seven foreign/dirty-startup cases and five automatic-TRIM cases passed.

Both default and shared-pool standalone handlers built. CopperStart passed 186 cache, transfer, buffer and geometry instruction replays, including binary cache-invalidation comparisons, plus 77 link and file-size cases on the source-integrated backend. No physical device was exercised.

## Reproduction

The Release solution build completed with one nullable warning in the CopperSharp compiler dependency. The solution tests recorded 2,279 passes, six conditional skips and zero failures. Three skips require external SFS/SFS2 fixtures and three are optional CPX native configurations. All five Kickstart sequential-read cases passed. On contiguous and fragmented 8 and 128 MiB volumes at equal DOS buffer counts, both handlers issued 64 reads per 1 MiB pass. CopperSFS used 0.41–0.50% fewer cycles than original SFS in cold and warm runs. The final-source harness also passed all ten 68000/68020 pool-capacity and allocation-fallback instruction tests.

The candidate SHA-256 is A14A9472F958400AB610731AE06B6D6666575A7B9652F618492AEC06DACEC230 and the saved 24-write control is 616977ADF427EA3DD2A14D97EE084680E0A2E1CE63104C26B208ADCA73773F0B. Build with Build-Handler.ps1 using -SharedSfsPool -AutoTrim -Cpu 68000 and the pinned compiler inputs. The release profile uses scalar replacement with inlining disabled.

Raw baseline call stacks are under artifacts/sfs-copy-reduction/baseline. The candidate manifest, per-trial measurements, instruction profiles, traces and test logs are under artifacts/sfs-copy-reduction/final. Use the previous handler under artifacts/sfs-mixed-journal/candidate as COPPERFS_SFS_REFERENCE. SfsGroupedWarmComparison measures three handlers, SfsCommitPerformance tests original parity, and SfsCommitBoundaryTrace records device traffic. ProfileReleaseWorkload records the copy routines and their native call stacks when COPPERFS_PERFORMANCE_PROFILE is set to 1.
