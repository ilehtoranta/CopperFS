# SFS mixed-mutation journal and CPU qualification

On the 16 MiB mixed workload the shared-pool handler issued 24 write commands, down from 32. Through the final explicit flush it saved 308,534 Fast RAM cycles (1.65%) against the saved candidate. Original SFS 1.279 used 17 write commands and remains faster. The release gate allows at most 0.1% over original on each phase. The shared-pool profile stays opt-in.

## What reached the device

Both CopperSFS traces have the same 288 sector writes in the same order. Every write changed its sector. The complete final media images also have the same SHA-256. One pressure commit occurs while accepting the 4,097-byte write. Explicit flush commits the second group. The intervening close replies require no device flush.

| Mixed-workload commands | Saved CopperSFS | Revised CopperSFS |
|---|---:|---:|
| Unpublished file payload | 5 | 5 |
| External TRST journal pages | 11 | 3 |
| TRFA publication | 2 | 2 |
| Home writes after publication | 12 | 12 |
| TROK clean markers | 2 | 2 |
| **Write commands** | **32** | **24** |
| Device flushes | 8 | 8 |
| Bytes written | 147,456 | 147,456 |

The first journal occupies sectors 284–292. They now go in runs of seven and two. The second journal uses 284–285 in one command. Each page still has its own free-space proof, address, next pointer and checksum. The mount lends the directory enumeration cache to commit and invalidates its lookup keys before returning it. The single-page fallback handles unavailable or undersized workspace. Normal transfer limits, DMA restrictions and device errors still apply. An error before journal publication leaves the previous committed group authoritative. Publication, home writes and the final clean marker retain their durability barriers.

Original SFS wrote 150,016 bytes. Its first group used an inline journal, one ten-sector home command and a clean marker. The second used a two-sector external journal, TRFA, two home commands and a clean marker, plus nine file-data commands before publication. Its home commands spanned intervening sectors, including the marker location. The two original groups needed seven flushes. CopperSFS uses two external journals and writes the 31 accepted home images in twelve contiguous runs, including relocated file data. Original's 17 commands include twelve sector writes identical to the preceding contents. Coalescing adjacent journal pages explains eight of the previous fifteen extra commands.

## Cycles and memory

Kickstart 3.1, emulated 68020, 32 DOS buffers, same starting media for each handler. Means use three alternating trials in each payload RAM class. The three-handler benchmark starts the mutation clock after mount and includes explicit flush.

| Payload and phase | Original SFS 1.279 | Saved CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast mixed mutation | 17,003,217 | 18,688,407 | 18,379,873 |
| Chip mixed mutation | 17,006,080 | 18,691,340 | 18,382,043 |
| Fast mixed verification | 18,311,517 | 18,952,760 | 18,953,840 |
| Chip mixed verification | 18,352,650 | 19,039,103 | 19,038,143 |
| Fast growth, overwrite and verification through commits | 5,760,710 | 6,631,533 | 6,563,837 |
| Chip growth, overwrite and verification through commits | 5,764,200 | 6,637,293 | 6,569,573 |
| Fast 96 KiB overwrite and flush | 325,337 | 910,233 | 892,847 |
| Chip 96 KiB overwrite and flush | 325,657 | 912,190 | 894,443 |

The independent release gate includes mount in the mutation phase.

| Gate | Original cycles | Revised cycles | Above original |
|---|---:|---:|---:|
| Fast mixed mutation | 17,531,467 | 18,755,763 | 6.983% |
| Chip mixed mutation | 17,534,560 | 18,757,817 | 6.976% |
| Fast mixed verification | 18,306,840 | 18,953,847 | 3.534% |
| Chip mixed verification | 18,349,583 | 19,038,543 | 3.755% |

The before/after Exec samples give 126,008 bytes retained at mount and 138,888 after the mixed workload for both CopperSFS builds. Original SFS used 156,480 and 172,904 respectively. The HUNK grew by 348 bytes to 166,548 and its code grew by 584 bytes to 162,672. The journal cursor uses transient commit stack space. The release compiler settings used scalar replacement with inlining disabled.

## Where the cycles went

The diagnostic instruction profile measures 6,707,942 handler cycles during mutation, down from 7,002,133. A fresh run with original SFS attributed 4,616,965 cycles to its handler and 2,764,640 to Kickstart, compared with 1,975,257 in Kickstart for revised CopperSFS. This attribution is separate from whole-probe acceptance timing. Clean shared-pool lookups now dispatch directly from the format and owner fields, while either deferred-state indicator still forces validation. The occupied order vector and hash index are cloned with one copy, and pool finalization walks the owned order vector directly.

| Inclusive profile measurement | Saved | Revised |
|---|---:|---:|
| Redo materialization calls | 963 | 160 |
| Redo materialization cycles | 213,081 | 19,660 |
| Shared-pool finalization cycles (46 calls) | 105,743 | 63,266 |
| Commit cycles (two calls) | 1,327,629 | 1,304,669 |
| Block copy cycles | 1,202,388 | 1,192,151 |
| Path resolution cycles (62 calls) | 1,477,344 | 1,359,996 |

These rows overlap. Most of the whole-workload saving came outside commit itself. Block copying is still the largest exclusive handler routine at 1,141,538 cycles, around 17% of handler execution. Cache insertion accounts for 282,589 copy cycles, private pool edits for 228,063, cached reads for 196,206 across two call sites and pool stores for 111,324. The data favor examining who needs a private copy and how long verified images can be borrowed before changing the recovery protocol.

## Recovery and compatibility

On Kickstart 3.1, cuts after the first sector of each of the three journal commands left the previously committed group intact. Cuts after the first sector of a multi-sector home command in each published group recovered the published group. CopperSFS and original SFS remounted every cut. File lists, sizes, hashes and free-block counts agreed with the expected state.

The grouped suite passed all 12 failure, pressure-retry and publication cases, including failures around the four durability barriers. The aligned and partial overwrite suite replayed 43 candidate sector prefixes with original SFS. All recovered wholly old or wholly new payloads with unchanged neighbors. The host fault-injection test returns an error on sector two of a journal command, checks that the caller regains its scratch and borrowed workspace, then retries the accepted group and verifies its complete payload after remount.

The lifecycle and capacity run passed 22 cases, including 10 native instruction cases, with explicit flush, deferred close, timer, inhibit/remount, harmless rejection, long directory entries, memory fallback and original-SFS handoff. All five automatic TRIM cases, five foreign-startup cases and two dirty-SFS startup cases passed. Both the default and opt-in standalone M68000 handlers built. CopperStart passed 186 native cache, transfer, buffer and geometry replays and 77 additional link and size instruction cases. Physical Amiga and device testing was outside this run.

All five full-ROM sequential-read configurations passed. At 32 buffers, cold and warm 1 MiB reads on contiguous and fragmented 8 and 128 MiB volumes used 64 reads per pass for both handlers. Original and revised CopperSFS preserved the file hash. Repeated enumeration of 24 and 64 entries used 1,295,320 and 5,251,330 cycles, respectively, compared with 1,293,100 and 5,274,320 on the 32-write candidate.

The Release solution suite recorded 2,276 passes, six conditional skips and zero failures. Three skips require external SFS/SFS2 fixtures and three are optional CPX boot or native configurations. The final Release solution build and the additional journal error-and-retry regression passed. The compiler dependency reported one nullable warning in `M68kSpillAndCopy.cs`. Both original-performance gates failed on timing, with the measured margins shown above.

## Reproducing the comparison

Build with `build/Build-Handler.ps1 -SharedSfsPool -AutoTrim -Cpu 68000 -SkipCompilerBuild -ArtifactRoot artifacts/sfs-mixed-journal/candidate`. The candidate SHA-256 is `616977ADF427EA3DD2A14D97EE084680E0A2E1CE63104C26B208ADCA73773F0B`. The saved 32-write binary has SHA-256 `5E81486EB3107F8068201F77BA898BF9BFC0DFA32082D48147C64BD83E7796CD`. The manifest records the original and starting media hashes. The native optimizer report records CILica revision `ca199fd583f492e156f2617dff43b5f28ff8684d`.

Set `COPPERFS_CORRECTNESS_MANIFEST` and `COPPERFS_AUTOTRIM_MANIFEST` to `artifacts/sfs-mixed-journal/manifest.json`, `COPPERFS_SFS_REFERENCE` to `artifacts/sfs-mixed-writes/final/handler/CopperSFS`, `COPPERFS_SFS_GROUPED=1`, and `COPPERSHARP_KICKSTART31_ROM` to the licensed ROM path. Build the Kickstart harness with `-p:CopperFsSfsGrouped=true -p:CopperFsSfsPool=true`. The `SfsCommitBoundaryTrace`, `SfsGroupedWarmComparison` and `SfsCommitPerformance` tests produce the device trace, three-handler measurements and independent gate values respectively. For CPU attribution run `ProfileReleaseWorkload` for SFS with `COPPERFS_PERFORMANCE_PROFILE=1`. Run ordinary timing with profiling disabled. Raw results and logs are under `artifacts/sfs-mixed-journal`.
