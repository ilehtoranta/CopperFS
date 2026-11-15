# SFS overwrite relocation

The 96 KiB overwrite workload starts with a file split into two 48 KiB extents. It overwrites the file with twelve 8 KiB DOS writes, closes it and requests an explicit flush. In the saved shared-pool candidate, three writes went through general relocation. Finishing each old extent required another tree walk to remove its record, repair the predecessor and successor, and publish the resulting leaf.

The grouped writer now installs a relocated prefix in a non-root leaf when both keys route there, its linked neighbours are local and the leaf has room. If the destination needs a different leaf, a new ancestor lower bound or a split, the general insertion path handles it. When an entire source extent joins a contiguous predecessor on the same leaf, one private edit extends the predecessor, repairs the successor and removes the old record. The transaction publishes the resulting metadata image once. The allocation checks, unpublished payload transfer, group admission and commit barriers use the existing paths.

## Instruction profile

Kickstart 3.1, emulated 68020, 16 MiB SFS media, Fast RAM. These are inclusive instruction cycles during overwrite and explicit flush. Rows overlap.

| Operation | Previous calls | New calls | Previous cycles | New cycles |
|---|---:|---:|---:|---:|
| General overwrite relocation | 3 | 3 | 517,876 | 272,793 |
| Extent-record lookup | 23 | 9 | 163,258 | 67,272 |
| Native metadata view | 71 | 43 | 159,419 | 96,764 |
| Block copy | 53 | 37 | 105,572 | 65,802 |
| Store sealed metadata | 19 | 10 | 52,829 | 19,002 |
| Continuation entry | 11 | 11 | 167,923 | 165,224 |
| Final TRST commit | 1 | 1 | 86,006 | 85,958 |

The standalone M68000 build used `-SharedSfsPool -AutoTrim -Cpu 68000`, with CILica scalar replacement enabled and inlining disabled. The saved candidate SHA-256 begins with `D873BBA3`, the new binary with `55C7C5E9` and original SFS 1.279 with `C7DD1741`. Full identifiers and raw measurements are under `artifacts/sfs-overwrite-relocation`. The paired comparisons use `COPPERFS_SFS_REFERENCE` pointing to `artifacts/sfs-mount-reuse/handler/CopperSFS`. Handler size grew from 164,320 to 165,296 bytes, including an increase of 920 bytes in native code. The compiler reported zero managed allocation sites.

## Ordinary emulator timings

Mean guest cycles across three alternating trials for each RAM placement. The same probe, DOS buffer count, media and payload placement were used across the three arms. The total includes both explicit commits and the time between phase markers.

| Workload | Payload RAM | Original SFS 1.279 | Previous candidate | New candidate |
|---|---|---:|---:|---:|
| Grow and flush | Fast | 1,035,647 | 1,367,487 | 1,381,887 |
| Overwrite and flush | Fast | 325,320 | 1,157,100 | 914,480 |
| Verify | Fast | 4,071,037 | 4,006,850 | 4,006,890 |
| Total | Fast | 5,760,093 | 6,859,363 | 6,612,997 |
| Grow and flush | Chip | 1,037,640 | 1,368,077 | 1,381,970 |
| Overwrite and flush | Chip | 348,717 | 1,158,960 | 916,577 |
| Verify | Chip | 4,073,020 | 4,010,640 | 4,010,593 |
| Total | Chip | 5,785,897 | 6,865,590 | 6,618,950 |

Overwrite and flush used 21.0% fewer cycles in Fast RAM and 20.9% fewer in Chip RAM than the previous candidate. Across the complete workload the saving was 3.6% in each placement. The growth phase cost about 1% more. Both CopperSFS binaries issued 17 writes and four flushes during the overwrite. Original SFS issued 12 writes and one flush. The remaining CopperSFS costs include 165,224 inclusive cycles entering or executing continuation writes, 59,664 in bounded free-space searches and 85,958 in the explicit commit.

## Release gate

These separate mixed-workload runs include mount in mutation timing. The allowance is at most 0.1% above original SFS for each phase.

| Payload RAM | Phase | Original SFS | New candidate | Above original |
|---|---|---:|---:|---:|
| Fast | Mutate | 17,505,120 | 19,588,517 | 11.90% |
| Fast | Verify | 18,304,587 | 18,932,077 | 3.43% |
| Chip | Mutate | 17,519,930 | 19,590,787 | 11.82% |
| Chip | Verify | 18,351,577 | 19,020,947 | 3.65% |

Mixed mutation issued 23 reads, 67 writes and 12 flushes against 7 reads, 17 writes and 7 flushes for original SFS. The mutation profile recorded three commits costing 1,668,408 inclusive cycles and zero calls to general overwrite relocation. Mixed verification issued 18 reads against the original's 13. The shared-pool profile remains opt-in.

In the three-handler comparison that timed mount separately, Fast RAM mixed mutation used 16,989,827 cycles on original SFS, 19,199,350 on the previous candidate and 19,218,920 on the new candidate. Mixed verification used 18,310,467, 18,934,097 and 18,932,210 cycles respectively. The relocation improvement was specific to overwrites of existing extents.

All five sequential-read ROM cases passed. With identical DOS buffer counts, all three handlers issued 64 device reads per 1 MiB pass on contiguous and fragmented 8 and 128 MiB volumes. CopperSFS used 0.41–0.49% fewer cycles than original SFS across cold and warm passes in Fast and Chip RAM. The separate 16 MiB case used unequal additional DOS buffer counts and is excluded from this timing range.

## Recovery, compatibility and retained memory

The 96 KiB overwrite was interrupted at nine points during payload transfer and around the commit barriers. Both CopperSFS and original SFS remounted each image and recovered the entire old file or the entire new file. All ten grouped recovery cases passed, including failed barriers and pressure commits.

For the separate aligned 2,048-byte and partial-sector 1,200-byte overwrites, original SFS remounted all 42 candidate sector-event prefixes across success, partial write failure and flush failure. Each cut preserved the neighbor's digest, file size and free-block count. The only candidate payload outcomes were 0 or 2,048 changed bytes for the aligned overwrite and 0 or 1,200 for the partial overwrite. Each successful explicit flush recovered the complete new payload. Replaying original SFS's own traces also exposed partially changed payloads of 512, 1,024 and 1,536 bytes aligned and 475 and 987 bytes unaligned.

The 13 other full-ROM compatibility cases passed, including deferred close, explicit flush, inhibit/remount, timer, rejection, handler handoff and TRIM. All five foreign-startup comparisons passed. CopperStart passed all 198 selected native instruction replays. The standalone M68000 builds completed for both the default handler and the shared-pool candidate.

The Release solution built with one nullable-reference warning in the CopperSharp compiler. Its tests recorded 2,263 passes and five skips, including 1,330 SFS passes. The added SFS tests exercise whole-extent replacement in a non-root leaf with both borrowed and scratch-backed metadata. They check pending visibility, the committed view before flush, extent links, file contents and a neighboring file.

The free-memory probe sampled Exec memory after loading each handler. Both CopperSFS builds retained 130,104 bytes after mount and 139,864 after mixed work. That is zero additional per-mount RAM. The new code accounts for 920 more bytes of loaded handler memory. Physical Amiga and device qualification was outside these emulator measurements.
