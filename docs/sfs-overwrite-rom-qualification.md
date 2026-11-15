# SFS overwrite scheduling and relocation

The reference is the grouped SFS handler from `3d8a18f`, with the earlier `259f645` binary used to identify the Kickstart regression. All timings are guest cycles on the emulated 68020. Ordinary comparisons use three alternating trials per handler and caller RAM placement. The profiles use a separate instruction-stepped run.

## Kickstart scheduling

In the saved reference pair, overwrite spent an additional 14,383 cycles inside Kickstart. Classifying instruction addresses by the resident ranges in the actual Kickstart 3.1 ROM gives 5,419 extra cycles in Intuition, 4,747 in Exec, 3,219 in timer.device, 418 in cia.resource and 584 outside those resident ranges. Graphics contributed −4. DOS instruction and cycle counts matched.

A fresh run sampled ExecBase.ThisTask and the interrupt mask before each ROM instruction. The input.device task ran for 11,778 ROM cycles during the current reference's overwrite and zero during the earlier binary's overwrite. Both intervals took two level-2 and five level-3 interrupts. The first level-3 interrupt arrived 45,234 cycles into the current interval and 95,046 into the earlier interval. The task and interrupt evidence places the difference in scheduled system work within a differently phased measurement interval. It does not establish the cause of every ROM cycle. Original SFS ran 324,540 total cycles in this separate trace. All ordinary comparisons keep interrupts, the explicit flush, and inter-phase work enabled.

## Exact EOF and extent transitions

The native writer required an overwrite to end strictly before EOF to try an established continuation. The core already admits a write ending exactly at its validated file limit without growing the file. Extending the native check to equality removed one general packet setup and saved 13,054 Fast overwrite cycles in isolation.

A subsequent experiment removed a second general relocation. A verified shared-pool leaf can extend the contiguous replacement, remove its exhausted source extent and repair the successor's back-link in one private image. If the successor is terminal, its offset survives record deletion after adjusting for the shift. Its next write requires a fresh allocation proof. When a parent separator might divide the successor's key range, the builder checks the routing of its last block before admitting the handoff. On a routing mismatch it shortens the proof and uses ordinary relocation. Payload transfer still requires the destination to be free in both accepted and durable views. A test stops with a successor key advanced past a stale separator and checks the remounted payload, neighbour and tree routing.

The bare handoff experiment changed the 96 KiB overwrite from two general relocation calls to one. Fast overwrite fell from 753,240 to 634,117 cycles versus 347,727 for original SFS. Chip overwrite fell from 759,720 to 636,487 versus 347,967. Growth took 13,680 more Fast cycles and 13,420 more Chip cycles. The complete workload saved 91,643 Fast cycles and 90,430 Chip cycles. In the revised scalar trace input.device had no ROM execution in the overwrite interval. The direct I/O and journal barriers are still included in these totals.

The raw ROM/task attribution for original SFS and both reference builds is under `artifacts/sfs-overwrite-rom-eof/rom-{original,before,current}`. The exact-EOF experiment is in that directory and the extent-handoff experiment is in `artifacts/sfs-overwrite-rom-successor`.

The extent-handoff build passed the Release suite (2,174 passed, five conditional skips), 263 CopperStart native replays and the full-ROM recovery, handoff, foreign-startup, sequential-read and TRIM checks. The original-handler performance gate exceeded its +0.1% threshold. The profile remains opt-in. Physical hardware was not exercised.

## Growth commit and total workload

Eleven metadata images were planned and emitted in one growth commit. The indexed-pool trial spent 461,986 cycles in the commit stream, including 188,332 in journal emission. Short page-local literal runs repeatedly called the generic block copier. The revised emitter copies their exact bytes directly to the existing page, using longword transfers only at even addresses. Cross-page literals use the existing transition path. The pool hash folds the high and low halves of the block key, and the proof checksum uses the existing block-sum routine. Slot identity checks, journal encoding, mount allocations and all four commit barriers are preserved.

In the revised growth trace the commit stream took 388,548 cycles, including 129,623 planning the original-SFS journal encoding, 115,604 emitting it, and 84,293 writing home blocks. These inclusive measurements come from instruction-stepped runs. The acceptance timings below use ordinary execution. The candidate binary is 169,380 bytes, up 724 bytes from the pushed control. No per-mount storage was added.

| Caller RAM and phase | Original SFS 1.279 | Pushed 3d8a18f | Revised |
|---|---:|---:|---:|
| Fast growth and flush | 1,044,830 | 1,197,863 | 1,128,667 |
| Fast overwrite and flush | 324,547 | 753,250 | 629,757 |
| Fast verification | 4,070,347 | 4,004,993 | 4,000,173 |
| Fast total | 5,764,643 | 6,266,017 | 6,081,920 |
| Chip growth and flush | 1,047,647 | 1,198,090 | 1,129,347 |
| Chip overwrite and flush | 347,903 | 759,793 | 632,370 |
| Chip verification | 4,073,363 | 4,004,100 | 4,004,250 |
| Chip total | 5,795,397 | 6,271,867 | 6,089,297 |

Both CopperSFS builds used one read, nine writes and four flushes in growth. Original SFS used no reads, eight writes and four flushes. Both CopperSFS builds used 17 writes and four flushes for overwrite. The Fast original used 12 writes and one flush. Growth commit CPU cost fell while retaining the same device ordering. The remaining Fast overwrite gap alone accounts for 305,210 cycles. All times include explicit flush. Raw ordinary comparisons and instruction profiles for this candidate are under `artifacts/sfs-overwrite-rom-literals`.

The Fast RAM probe with 32 DOS buffers measured 126,008 bytes retained after mount and 138,888 bytes after mutation for each CopperSFS build. Original SFS retained 156,480 and 172,904 bytes. The grouped M68000 binary rebuilt byte-for-byte from the isolated source checkout (SHA-256 42DB203DEEAA525742056726C3372D71EC580D23C7C55CA8C8EDA1B58B9317D6). CILica used scalar replacement with inlining disabled.

The overwrite profile attributed 152,669 inclusive cycles to 12 continuation attempts, 75,472 to the remaining general relocation, and 78,152 to the commit stream. In growth the revised handler executed 969,420 cycles inside its own code versus 789,409 for original SFS. Kickstart accounted for 152,973 and 234,717 respectively in these separate instruction-stepped runs. One additional 2 KiB read supplied original images for journal planning.

In the warm mixed comparison the revised handler took 58,370 fewer Fast cycles for mutation and 680 fewer for verification than the pushed reference. Chip mutation saved 60,246 cycles and verification cost 2,524 more. Fast mount cost 22,560 more cycles than the reference. Mixed mutation used 14 reads, 24 writes and eight flushes, against the original's five reads, 17 writes and seven flushes. Mixed verification used 17 reads against 13. The independent performance gate measured +3.155% Fast mutation, +3.009% Fast verification, +2.808% Chip mutation and +3.252% Chip verification against original SFS. All four exceeded the +0.1% release limit. The grouped SFS profile stays opt-in.

## CILica inlining

The same handler source was also built with restricted CILica inlining enabled. Scalar replacement remained enabled. The standalone build applies this CILica pass to the platform assembly. The inlined binary measured 178,928 bytes, up 9,548 bytes. Three alternating trials at each buffer placement gave the following means.

| Caller RAM and phase | Original SFS 1.279 | Inlining off | Inlining on |
|---|---:|---:|---:|
| Fast growth and flush | 1,052,967 | 1,128,653 | 1,119,180 |
| Fast overwrite and flush | 347,680 | 629,757 | 630,717 |
| Fast verification | 4,069,750 | 4,000,187 | 3,977,463 |
| Fast total | 5,795,337 | 6,081,913 | 6,048,447 |
| Chip growth and flush | 1,039,637 | 1,129,440 | 1,119,337 |
| Chip overwrite and flush | 324,803 | 632,310 | 633,317 |
| Chip verification | 4,073,587 | 4,004,243 | 3,981,060 |
| Chip total | 5,762,943 | 6,089,323 | 6,054,827 |

The inlined build improved the Fast total by 0.55% and increased Fast overwrite time by 0.15%. It also exceeded the +0.1% original-handler gate on all four mixed phases. The branch retains the smaller inlining-disabled configuration. Raw paired results are in `artifacts/sfs-literals-cilica-inline`. Applying restricted CILica separately to the portable core produced IL that the pinned native compiler rejected in JournalSpace (C68K0005, incompatible stack types at IL_00FC), with either inlining setting. No timing is attributed to these two core builds.

## Qualification of the journal emitter

The Release solution build completed with one nullable-reference warning in the CopperSharp compiler dependency. The Release solution tests passed 2,174 with five conditional skips. Both default and grouped standalone M68000 handlers built. CopperStart passed 186 cache, geometry, buffer and transfer replays and 77 link and size replays. The first consumer rebuild picked up unrelated FFS changes in the sibling main checkout and failed to compile. Rebuilding with CopperFSRoot pointing to the isolated candidate checkout passed all 186 tests, as did replaying the candidate with the existing native harness.

Kickstart 3.1 full-ROM tests passed grouped recovery and pressure (12), partial journal and home writes (2), overwrite recovery contracts (2), close/flush, handoff and capacity checks (22), foreign startup (7), automatic TRIM recovery/reallocation (5), journal read fault retry (1), boundary tracing (1), and sequential reads (5). The original-handler performance gate failed in both RAM placements as reported above. Physical devices were not tested.
