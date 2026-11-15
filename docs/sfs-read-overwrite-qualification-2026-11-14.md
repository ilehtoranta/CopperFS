# SFS read, transaction and overwrite experiments

The zero-extra-RAM shared-pool handler reduces mixed verification by 1.68 million emulated cycles. Bulk transfers across misaligned caller addresses account for most of the improvement. Original SFS 1.279 still wins both mixed release phases. Keep whole-group recovery and the existing release memory budget. The in-place overwrite and additional cache are independent build experiments.

## Paired measurements

Each entry below is the mean of three alternating ordinary Kickstart 3.1 runs on the emulated 68020, in cycles. Each comparison used identical 16 MiB images, 512-byte blocks and 32 requested DOS buffers. Mutation precedes verification on the same mount. The 96 KiB overwrite includes close and explicit flush. The last column includes growth, overwrite, verification and their commits. The synthetic guest verifies the bytes it reads, so the phase timings include guest application time. The original row and both saved Copper controls come from the same control run. Each experimental build also has its own paired original measurements in the raw files.

| Fast RAM | Mixed mutation | Mixed verification | Overwrite and flush | Growth through verification |
|---|---:|---:|---:|---:|
| Original SFS 1.279 | 17,004,393 | 17,567,920 | 326,847 | 5,771,487 |
| Saved CopperSFS, auto TRIM off | 19,604,283 | 20,122,973 | 1,169,440 | 6,929,800 |
| Saved CopperSFS, auto TRIM on | 19,610,463 | 20,160,040 | 1,207,283 | 6,981,173 |
| Bulk reads and borrowed edges | 19,629,700 | 18,483,587 | 1,207,593 | 6,980,813 |
| Read and shared-pool edit changes | 19,555,117 | 18,479,693 | 1,191,423 | 6,935,690 |
| Same with extra cache | 19,293,383 | 18,389,653 | 1,196,090 | 6,954,483 |
| In-place experiment, baseline RAM | 19,565,313 | 19,228,570 | 984,760 | 6,746,827 |

| Chip RAM | Mixed mutation | Mixed verification | Overwrite and flush | Growth through verification |
|---|---:|---:|---:|---:|
| Original SFS 1.279 | 17,001,183 | 17,616,497 | 348,703 | 5,786,563 |
| Saved CopperSFS, auto TRIM off | 19,605,543 | 20,215,357 | 1,171,250 | 6,934,467 |
| Saved CopperSFS, auto TRIM on | 19,612,937 | 20,245,800 | 1,209,613 | 6,987,347 |
| Bulk reads and borrowed edges | 19,631,197 | 18,572,460 | 1,209,593 | 6,987,090 |
| Read and shared-pool edit changes | 19,556,970 | 18,568,873 | 1,193,447 | 6,941,820 |
| Same with extra cache | 19,295,207 | 18,479,247 | 1,197,957 | 6,965,023 |
| In-place experiment, baseline RAM | 19,568,933 | 19,311,947 | 985,357 | 6,751,253 |

The release gate uses a separate probe that includes startup in mutation. In its paired runs the zero-extra-RAM whole-group build used 19,921,400 against 17,501,347 cycles for Fast mutation and 18,483,843 against 17,575,233 for verification. Chip used 19,923,657 against 17,522,570 and 18,571,940 against 17,620,373 respectively. All four exceed 1.001 times the original.

## Where the time goes

* The extent reader splits a partial leading sector, a contiguous whole-sector body and a partial tail. Native bulk I/O checks the DMA mask and transfer limit. A bounce uses the existing transfer workspace. Borrowed edge images obey logical overlay and pending-write precedence. The portable alignment-restricted fallback and prefix-on-error behavior have dedicated tests.
* Mixed verification drops from 30 to 20 device reads. Original uses 13. The read-only change reduces exclusive CopyBlock cycles from approximately 1.72M to 797,525 and handler cycles from 3.83M to 2.24M. Three large DMA-workspace copies still cost 491,922 cycles. The aligned 96 KiB verification issues 12 reads on all three handlers.
* Shared-pool edits return an existing private image, copy an accepted image once, or acquire a clean verified image and copy it into a new slot. The measured mutation still has 667 calls to CopyBlock, about 1.21M exclusive cycles, and 714 metadata-view calls costing 1.71M inclusive cycles. Reusing the bucket saves lookups. The unchanged copy count limits the gain.
* The three mutation commits cost 1.66M inclusive cycles. The call stacks show pressure retries on a write and a rename, followed by the requested flush. There are 13 external journal-page submissions and 48 retained block submissions. Of the latter, three publish the marker, 42 apply home images and three write the clean marker. The physical device saw 67 writes and 12 flushes, against 17 writes and seven flushes on original SFS. Method timings overlap. Sum device operations or exclusive cycles when attributing total work.

## Recovery contract

An original-SFS guest wrote and flushed a 4 KiB file and an independently checked neighboring file. It then overwrote 2,048 bytes at offset zero or 1,200 bytes at offset 37. The host recorded write addresses, complete sector images, DOS reply markers and flushes. Every sector prefix of the resulting write-through trace was remounted with original SFS and checked for file size, content, neighboring-file digest and free-block count. A second series injected an I/O failure after persisting one payload sector or at a flush and cut power after the device reported the fault.

| Overwrite | Original SFS | Copper whole-group | Copper in-place |
|---|---|---|---|
| Aligned, bytes recovered from new payload | 0, 512, 1,024, 1,536, 2,048 | 0 or 2,048 | 0, 512, 1,024, 1,536, 2,048 |
| Partial, bytes recovered from new payload | 0, 475, 987, 1,200 | 0 or 1,200 | 0, 475, 987, 1,200 |

In these small examples original SFS wrote the original file's sectors 370–373 during explicit flush, after close had replied. The partial-write trial used sectors 370–372. Successful explicit flush returned the entire acknowledged payload in every arm. The original's timing of physical submission and its possible post-crash contents are separate observations. The in-place prototype may start writing sooner. Sector-atomic, write-through emulation does not characterize a physical device's internal power-loss behavior.

The opt-in path admits a request only on a clean mount, wholly within one existing allocated extent, with a checked file-owner chain, current allocation bitmap, administrative-area exclusions and disjoint buffers. Growth, archived files, pending groups and cross-extent packets follow whole-group processing. The final prototype reduced the isolated overwrite from 17 writes and four flushes to 12 writes and one flush, matching original command counts. It costs roughly three times the original cycles and worsens mixed verification. Keep the stronger policy for release.

## Mount RAM and cache

| Measured experimental binary | HUNK bytes | Code bytes | Retained after mount | Retained after mixed work |
|---|---:|---:|---:|---:|
| Bulk reads | 162,208 | 158,308 | 130,104 | 139,864 |
| Bulk reads and private edits | 162,060 | 158,328 | 130,104 | 139,864 |
| Additional cache | 162,164 | 158,432 | 146,184 | 155,944 |
| In-place overwrite | 165,420 | 161,072 | 130,104 | 139,864 |

The optional cache allocates 30 pages at 536 bytes each, including headers and Exec rounding. MoreCache rolls back the complete optional batch on allocation failure. With 32 DOS buffers it reduces mixed mutation reads from 18 to 10 and verification reads from 20 to 18. Mutation CopyBlock calls fall from 667 to 619, verification from 131 to 115. The physical cache index records 412 hits in 576 mutation lookups at baseline and 417 in 520 with extra RAM. Verification records 267 in 365 and 281 in 362. The Fast cold mount rises from 376,590 to 445,400 cycles and the complete growth workload gets slightly slower.

The 1 MiB streaming matrix uses one cold and one warm pass on each mount, checks every byte, and covers both memory placements and either one or sixteen extents on 8 and 128 MiB volumes. Each aligned 16 KiB request normally maps to one device read. On Fast RAM the larger cache changes the warm pass by less than 0.02% in every geometry. The separate 16 MiB matched-RAM experiment uses DOS AddBuffers to give both Copper configurations 156,368 retained bytes against 156,480 for original SFS. At this budget all three used 64 reads. Fast warm-pass cycles were 136,202,970, 136,202,460 and 136,850,570 for baseline, extra cache and original. Keep the current RAM budget.

## Reproducing the experiments

Use the standalone build script with `-SharedSfsPool -AutoTrim -Cpu 68000`. Add `-SfsInPlaceOverwrite` or `-SfsExtraKiB 16` independently. CILica scalar replacement is enabled with inlining disabled. Set `COPPERSHARP_KICKSTART31_ROM`, `COPPERFS_CORRECTNESS_MANIFEST`, `COPPERFS_SFS_REFERENCE` and `COPPERFS_SFS_GROUPED=1` for the full-ROM comparisons. Each manifest directory owns its output. Run the recovery-contract test with `COPPERFS_TEST_IN_PLACE=1` only for the in-place policy. The read-placement matrix expects the cache build as candidate and the zero-extra-RAM build as reference. Set `COPPERFS_PERFORMANCE_PROFILE=1` only for instruction attribution.

Raw measurements and SHA-256 identities are under `artifacts/sfs1-redesign` in the workspace. The `controls`, `read-results`, `metadata-results`, `cache-results` and `inplace-final-results` directories separate the experiment stages. The warm and continuation directories hold the ordinary timing samples. The CPU profiles include every CopyBlock source, destination, byte count and caller, plus transaction call stacks. The overwrite-contract directories hold complete device payload traces and the checked content at each recovered prefix. The `trim-matrix/sfs/Warm` run reclaimed 65,536 bytes with two UNMAPs and includes discovery, foreground flush and deferred maintenance measurements.

The standalone shared-pool build enables the new read path. Other profiles and alignment-restricted adapters keep the existing read policy. The final rebuilds after scoping that switch use the `check-` prefix. Their paired controls are the corresponding experimental binaries above. For a separate instruction profile covering only the 96 KiB overwrite and explicit flush, set `COPPERFS_OVERWRITE_PROFILE=1` and run `ProfileSfsOverwriteAndFlush`.

The supported-device TRIM run also measured the cost outside the foreground packet. Each 32 KiB range cost 681 tracking cycles. The first filesystem flush and reclaiming idle turn cost 141,020 and 67,782 cycles. The repeat cost 142,454 and 51,116 cycles. Tracking plus flush plus reclaiming service therefore cost 209,483 and 194,251 cycles respectively. These figures cover the named operations and exclude the intentional idle wait.

## Final scoped builds

Use the following paired release-gate measurements for the release decision. The standalone and probe binaries were rebuilt from the shared workspace between build rounds. The later harness also timed the saved binaries differently, including original SFS. Each row compares handlers with the same harness and media. Comparing absolute cycles between the two harness builds overstates or understates some changes.

| Release probe | Original SFS | Zero-extra-RAM CopperSFS | Difference |
|---|---:|---:|---:|
| Fast mutation | 17,494,783 | 19,919,990 | +13.86% |
| Fast verification | 18,305,630 | 19,216,060 | +4.97% |
| Chip mutation | 17,512,887 | 19,921,777 | +13.76% |
| Chip verification | 18,349,770 | 19,303,237 | +5.20% |

The final zero-extra-RAM HUNK is 162,532 bytes, including 158,744 bytes of code. The cache build is 162,636 bytes, including 158,848 bytes of code. The in-place build is 165,736 bytes, including 161,432 bytes of code. The separately rebuilt default HUNK is 152,456 bytes. Hashes and probe identities are recorded per arm in the `check-*-results` directories.

On the final Fast overwrite and explicit flush, whole-group used 1,176,307 cycles. In-place used 980,910. Original averaged 325,320 in the whole-group comparison and 348,387 in the in-place comparison. The in-place profile attributes 771,518 inclusive cycles to the prototype across 12 writes. Preparation ran 12 times for 224,581 cycles, including 149,112 in recovery checks. Extent-record lookup took 127,758 cycles and object lookup from roots took 111,667. Bulk payload submission took 53,799. The profile favors reusing a clean mount's validated roots and a revision-bound file mapping as the next experiment. Whole-group overwrite has a different cost distribution, with three calls to relocate extents costing 521,126 inclusive cycles.

The final whole-group mixed verification profile records 796,970 exclusive CopyBlock cycles in 131 calls and 2,238,123 handler cycles. The cache build records 757,254 copy cycles in 115 calls. The additional cache still requires 16,080 bytes and gives only a small improvement in the latency of the mixed workload. Neither optional design passes original-handler parity.

| Final Fast RAM results | Whole-group | Whole-group with extra cache | In-place |
|---|---:|---:|---:|
| Cold mount | 377,610 | 445,213 | 378,710 |
| Mixed mutation after mount | 19,551,877 | 19,289,567 | 19,556,907 |
| Mixed verification | 19,217,437 | 19,112,617 | 19,219,353 |
| Isolated overwrite and flush | 1,176,307 | 1,176,360 | 980,910 |
| Growth through verification | 6,942,853 | 6,947,340 | 6,733,040 |

## Qualification coverage

* **Host.** The Release solution build succeeded. The final Release solution run recorded 2,191 passes and four skips, including 1,327 SFS passes. The focused SFS run also passed 91 cases covering copy-on-edit, unaligned reads, error positions, buffer overlap, growth fallback and in-place preservation of every byte outside the payload.
* **Native.** The standalone shared-pool, extra-cache, in-place and default M68000 profiles built. The handler-bound CopperStart instruction and replay selection passed all 198 cases with the final whole-group binary.
* **Full ROM.** Kickstart 3.1 tests exercised grouped pressure and barrier failures, rejection retention, explicit flush, timer, close, inhibit/remount, automatic discard and reallocation, cross-handler reads and original recovery. The final whole-group build passed the 19-case recovery/compatibility run and its separate 10-case paired comparison and overwrite-contract run. The final in-place build also passed its 10-case comparison and recovery run. Timing parity failed in the separate Fast and Chip release tests.
* **Physical hardware.** No physical Amiga, DMA controller or discard-capable device was exercised. All cycle results and power-cut outcomes in this report are emulated.
