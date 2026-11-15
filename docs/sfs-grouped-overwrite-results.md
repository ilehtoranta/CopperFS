# SFS grouped overwrite on the 68020

The updated SFS shared-pool profile extends its durable-free destination proof across two distinct allocated source extents. It requires the terminal successor to remain on the validated leaf and in the same source bitmap. A packet can cross the extent boundary. Failed unpublished I/O preserves the earlier accepted count. TRIM receives each physical source interval separately. The existing group, pool and bitmap storage hold this state. Explicit flush retains all four TRST barriers.

Mixed-workload improvements include using verified hashed-directory images during traversal, a 512-byte node-leaf shortcut, RAM copy handling for unaligned 68020 buffers, and avoiding eight-sector cache insertion for file-end scratch reads. Device DMA requirements still apply. M68000 and M68010 retain byte copies when source and destination have opposite parity.

## Three-arm full-ROM comparison

The guarded inlined profile was compared with original SFS 1.279 and merged commit 6919188. All three arms used Kickstart 3.1, an emulated 68020, identical media and 32 DOS buffers. The means below count final explicit flushes. Raw observations are in artifacts/sfs-disjoint-source.

| Placement and phase | Original SFS | 6919188 | Guarded inlined profile |
|---|---:|---:|---:|
| Fast growth | 1,039,887 | 1,128,690 | 1,097,757 |
| Fast overwrite | 324,547 | 629,770 | 558,657 |
| Fast verify | 4,069,940 | 4,000,073 | 3,988,713 |
| Fast total | 5,759,290 | 6,081,893 | 5,963,690 |
| Chip growth | 1,036,360 | 1,129,380 | 1,097,747 |
| Chip overwrite | 347,960 | 632,313 | 561,153 |
| Chip verify | 4,073,507 | 4,004,237 | 3,991,423 |
| Chip total | 5,784,273 | 6,089,260 | 5,968,867 |

In the separate instruction profile, the overwrite consumed 114,139 Kickstart cycles and 438,263 handler cycles. Continuation admission and checks used 146,626 inclusive cycles, the general relocation used 70,449, and commit streaming used 79,897. These figures overlap their callees. The earlier task/interrupt trace in sfs-overwrite-rom-qualification.md attributes 11,778 of the 14,383 additional ROM cycles to scheduling input.device during the measured interval. The main remaining cost is in the filesystem.

## Inlining and recovery

Enabling CILica inlining on the two-extent profile before the overlap guard reduced Fast overwrite from 584,000 to 557,660 and the full workload from 5,998,827 to 5,955,390 cycles. Both arms used scalar replacement. The resulting handler grew from 171,200 to 181,160 bytes. Raw results are in artifacts/sfs-two-source-inline. Inlining is an explicit standalone build switch and remains disabled by default.

Directed tests cover a packet that fails on its second sector while spanning both source intervals, retry, a pre-flush power cut, explicit flush, neighbor-file contents and the two freed intervals. A separate malformed-image test rejects overlapping physical source extents despite a consistent bitmap and metadata checksums. The guarded profile passed Release tests (2,177 passed, five conditional skips) and the original-handler full-ROM recovery and mixed gates. Its first inlined binary eliminated the private cache-invalidation replay entry and failed 11 CopperStart replay cases. Hardware tests were not run.

## Native replay qualification with inlining enabled

The cache replays call private machine-code entries and detect the page-count cache layout by the presence of AddCachePage. Retaining InvalidateBlocks alone restored the read-window entry but left the replay seeding an incompatible cache layout. Preserving AddCachePage, CacheBlock and InvalidateBlocks as callable entries passed all 29 targeted cases, including compiled cache insert, eviction, resizing, invalidation and both CPU models. All 77 link and file-size replays passed on the same binary.

The grouped M68000 handler SHA-256 is 3C868FACB8740E48C6FBF455E399EDD041BEEB7DB050F42D5DDCEDD5012FF00C. Its compatibility report lists zero reachable managed allocations. The previous guarded build retained 126,008 bytes after mount and 138,888 after mutation. The changes preserving the three entries only constrain the compiler.

| Fast RAM, three trials | Original SFS | 6919188 | Replay-qualified entries |
|---|---:|---:|---:|
| Growth and flush | 1,044,133 | 1,128,567 | 1,098,157 |
| Overwrite and flush | 324,590 | 629,757 | 556,467 |
| Verification | 4,070,353 | 4,000,223 | 3,989,947 |
| Total | 5,763,997 | 6,081,873 | 5,963,213 |

Raw samples and native/ROM logs for this binary are in artifacts/sfs-cache-replay. Earlier samples above document the effect of changing the compiler profile and cannot replace qualification of this binary.

The replay-qualified binary spent 112,999 cycles in Kickstart and 437,327 in handler code during the instrumented overwrite. The original-handler trace spent 158,076 in Kickstart and 156,494 in handler code (artifacts/sfs-overwrite-rom-eof/rom-original). CopperSFS used 45,077 fewer ROM cycles while spending 280,833 more in its own handler. These instruction-stepped profiles are separate from the ordinary acceptance measurements. With 32 DOS buffers the guest memory probe measured 126,008 bytes retained after mount and 138,888 after mutation, identical to 6919188.

With identical input assembly SHA-256 22A8847231083C426FBD23C9C95C659523D576B0CC44FD9BF4F1A24150989F73 and scalar replacement enabled in both arms, CILica inlining reduced Fast overwrite from 584,160 to 556,313, growth from 1,108,043 to 1,098,243, and total from 5,995,720 to 5,963,123 cycles. Chip overwrite fell from 586,520 to 558,837 cycles. The original in this Fast series took 324,597 cycles for overwrite. See artifacts/sfs-cache-replay-noinline; the previous arm is the inlined binary. The full CopperStart candidate replay ran 186 cache, geometry and transfer cases and 77 link and file-size cases with zero failures or skips.

On Kickstart 3.1 the inlined M68000 binary passed 12 grouped recovery and pressure cases, two partial journal/home-write cases, two overwrite contract cases, 22 flush/close, handoff and compatibility cases, seven foreign-startup cases, five TRIM cases, a journal read fault, the commit-boundary trace and five sequential-read tests. The mixed Fast mutation and verification means were 17,330,787 and 19,846,083 against 17,501,030 and 20,073,530 for SFS 1.279. Chip means were 17,332,233 and 19,912,803 against 17,557,333 and 20,140,017. Each mixed phase passed the +0.1% gate. Growth and overwrite still exceed original cycles. The grouped and inlining profiles remain opt-in. Physical-device qualification is outstanding.

The exact replay-qualified source passed the Release solution build and tests (2177 passed, 5 conditional skips), the default and grouped M68000 builds and both CPU copy-instruction tests. The build reported one nullable-reference warning from the CopperSharp compiler dependency.

## Continuation admission workspace

The general error-recovery routine had been inlined into the write dispatcher, placing a 168-byte Work local in its native stack frame. Keeping recovery out of line reduced the dispatcher frame from 184 bytes to 12. The deferred continuation also allocated its admission workspace on every established run. Moving first admission into a separate checked function retains the same validations and rollback and leaves that workspace off the common path. The dispatcher change alone slowed the overwrite. Both changes together improved growth, overwrite and total cycles in both placements.

| Placement and phase | Original SFS | 9d6c8c4 | Separate admission |
|---|---:|---:|---:|
| Fast growth and flush | 1,039,297 | 1,098,163 | 1,089,363 |
| Fast overwrite and flush | 347,703 | 556,503 | 551,937 |
| Fast verification | 4,069,770 | 3,989,913 | 3,987,897 |
| Fast total | 5,781,693 | 5,963,213 | 5,950,873 |
| Chip growth and flush | 1,048,047 | 1,098,583 | 1,089,553 |
| Chip overwrite and flush | 347,967 | 558,867 | 554,383 |
| Chip verification | 4,072,957 | 3,993,487 | 3,992,107 |
| Chip total | 5,793,933 | 5,969,577 | 5,957,760 |

The three original Fast overwrite samples were 324,540, 393,970 and 324,600 cycles. All three contribute to the reported mean. Relative to the median original run, CopperSFS overwrite took 1.70 times as many cycles.

The exact grouped handler SHA-256 is 02C85144E1304DE8B6AC2C2A6BB8AC33F5C26AE966B055747E99E270C23BFD58. Guest memory remained 126,008 bytes after mount and 138,888 after mutation with 32 DOS buffers. Full-ROM samples and individual qualification results are in artifacts/sfs-cold-admission. Physical devices remain untested.

The grouped binary occupies 180,104 bytes, down 452 bytes from 9d6c8c4. On growth, SFS 1.279 issued eight writes and four flushes. Both CopperSFS builds issued one read, nine writes and four flushes. Each CopperSFS growth run committed its group once. On overwrite, SFS 1.279 issued twelve writes and one flush. CopperSFS issued seventeen writes and four flushes.

In the instruction profile the new growth commit took 381,590 cycles out of 1,089,380. Planning eleven journal records took 129,507 cycles, emitting them took 111,261, and writing the home runs took 81,971. The complete growth phase spent 135,762 cycles in 63 block copies. These inclusive measurements overlap. Original SFS spent 789,409 cycles in its handler during growth against 931,679 for CopperSFS. During overwrite those figures were 156,494 and 431,511, while Kickstart took 158,076 and 114,184 respectively. The original instruction profile is in artifacts/sfs-overwrite-rom-eof/rom-original. Ordinary acceptance timings above use the original measured alongside both CopperSFS builds.

A separate tree-descent prototype borrowed the current image until the first edit. It reduced the Fast total to 5,933,823 cycles and growth to 1,072,610, but overwrite rose to 566,830 cycles. Chip overwrite also rose, to 569,400. It remains experimental. Hoisting shared-pool geometry checks also increased Fast overwrite to 555,463 and total to 5,957,223. A compiler-only experiment applying scalar replacement and inlining to 23 portable methods on top of 9d6c8c4 measured 553,183 Fast overwrite cycles and 5,960,147 total. The ordinary comparisons are in artifacts/sfs-borrow-tree, artifacts/sfs-proof-geometry and artifacts/sfs-core-continuation-on respectively.

The Release solution build succeeded with one compiler dependency warning. The solution tests passed 2,177 cases with five conditional skips. Both standalone builds succeeded and the two CopperStart native replay suites passed 186 and 77 cases. Kickstart 3.1 runs passed grouped power cuts and failures, journal and home partial writes, explicit flush and close, original-handler recovery and handoff, foreign startup, TRIM and read placement. Mixed mutation and verification passed the original-handler gate in Fast and Chip RAM. Growth and overwrite remain above the release threshold, so this profile remains opt-in.

### Where the overwrite gap occurs

The host profiler timed each guest DOS call from its library vector to the matching return address and stack pointer. Each of the twelve writes returned 8,192 bytes on all three handlers. These figures are from a single instruction trace per handler; the ordinary timing series above supplies the acceptance measurements.

| Guest call | SFS 1.279 | 9d6c8c4 | 0b3cd3d |
|---|---:|---:|---:|
| First write | 23,864 | 156,634 | 155,912 |
| Second write | 21,347 | 30,481 | 34,941 |
| Remaining ten writes combined | 227,980 | 220,630 | 211,216 |
| Close | 26,356 | 14,566 | 14,540 |
| Explicit flush packet | 14,086 | 123,062 | 124,274 |
| Complete phase including other guest activity | 324,630 | 556,370 | 551,880 |

The first write accounted for 132,048 excess cycles and explicit flush for 110,188. The last ten writes and close recovered some of that difference. Those two expensive calls are where further reductions can most affect the full workload. The first write opens the replacement extent and the flush makes the accepted group durable. The three profile files are under artifacts/sfs-dos-calls.

### Portable-core compiler comparison

The opt-in `-SfsCoreCilica Writer` build option applies CopperSharp's restricted CILica pipeline to the shared-pool SFS writer, redo arena, shared image pool and TRST codec. The existing `-CilicaInlining` switch controls inlining, with scalar replacement enabled independently. The driver retains the original IL of the CSUN journal-space allocator. A separate experiment optimizing the entire portable core except journal-space methods timed out during the full-ROM workload after overwrite and before verification finished. Its final disk image matched the passing run byte for byte and passed the offline image checker.

The production compiler and frozen managed inputs reproduced the saved 180,104-byte control with SHA-256 `02C85144E1304DE8B6AC2C2A6BB8AC33F5C26AE966B055747E99E270C23BFD58`. The comparison below changed only the core IL. Platform IL and native compiler flags were identical, and no runtime storage was added to the filesystem. The off/on comparison selected the same 243 methods and changed only the inlining setting. Each entry is the mean of three ordinary Kickstart 3.1 runs on the emulated 68020.

| Workload | Original SFS 1.279 | Saved CopperSFS | Core inlining off | Core inlining on |
|---|---:|---:|---:|---:|
| Fast growth and flush | 1,049,350 | 1,089,387 | 1,110,520 | 1,068,567 |
| Fast overwrite and flush | 324,560 | 551,933 | 570,327 | 534,653 |
| Fast verification | 4,070,190 | 3,987,887 | 3,988,517 | 3,989,513 |
| Fast total | 5,769,020 | 5,950,880 | 5,972,083 | 5,912,540 |
| Chip growth and flush | 1,039,463 | 1,089,577 | 1,110,863 | 1,068,773 |
| Chip overwrite and flush | 347,913 | 554,427 | 572,803 | 537,150 |
| Chip verification | 4,072,910 | 3,992,077 | 3,992,133 | 3,993,233 |
| Chip total | 5,785,233 | 5,957,727 | 5,978,517 | 5,918,967 |

Original and saved columns are from the core-inlining-on series. The inlined handler is 198,968 bytes, compared with 174,116 for the core-inlining-off experiment. The measured guest allocation was 126,008 bytes after mount and 138,888 after mutation at 32 buffers. These figures exclude the loaded handler image.

The instruction trace measured 113,029 Kickstart cycles for the inlined handler against 158,111 for original SFS. Its first 8 KiB write took 145,920 cycles against 23,864 for the original, and its explicit flush took 122,270 against 14,086. The two handlers issued seventeen writes and four flushes versus twelve writes and one flush in this overwrite. Mixed mutation and verification met the +0.1% gate on both memory placements. Growth and overwrite still missed it. Raw timing, compatibility and replay results are in artifacts/sfs-core-wide-inline and artifacts/sfs-core-wide-noinline. Physical hardware was not exercised.

The `HotMethods` profile uses inlining on 24 methods selected from those traces. The remaining 219 receive scalar replacement without inlining. Both stages run in separate optimizer processes. This produced a 177,136-byte HUNK, 2,968 bytes smaller than the saved handler, with the same 126,008 and 138,888 bytes of measured mount and post-mutation allocation. The handler SHA-256 was `BFF490FC4BAC90D87696BD48BD759AC37171C3F11682E5BD541657189357ACC0`.

| Phase | Original SFS 1.279 | Saved CopperSFS | HotMethods |
|---|---:|---:|---:|
| Fast growth and flush | 1,043,333 | 1,089,397 | 1,070,840 |
| Fast overwrite and flush | 324,590 | 551,863 | 539,847 |
| Fast verification | 4,070,323 | 3,988,323 | 3,993,603 |
| Fast total | 5,763,170 | 5,951,273 | 5,934,283 |
| Chip growth and flush | 1,039,550 | 1,089,543 | 1,070,840 |
| Chip overwrite and flush | 347,960 | 554,423 | 552,503 |
| Chip verification | 4,072,937 | 3,992,077 | 3,987,053 |
| Chip total | 5,785,403 | 5,957,710 | 5,940,310 |

These paired means are in artifacts/sfs-core-small-inline. To reproduce the opt-in configuration, run the following from the checkout root.

```powershell
./build/Build-Handler.ps1 -Cpu 68000 -SharedSfsPool -AutoTrim -CilicaInlining -SfsCoreCilica HotMethods
```

During growth on Fast RAM, exclusive handler cycles fell from 931,628 to 921,665. Original SFS used 789,633. The largest measured routines in the new handler were journal delta planning (138,038 cycles), block copying (135,364), and journal emission (52,125). These are exclusive instruction counts. They include the work of preserving the grouped recovery contract.

The DOS-call overwrite trace on Fast RAM measured the first write at 154,838 cycles and explicit flush at 118,876. Original SFS used 23,854 and 14,090 respectively. On Chip RAM the corresponding pairs were 154,850 and 124,894 for CopperSFS, and 23,852 and 14,084 for original SFS. Across the full overwrite, Kickstart itself consumed 109,716 cycles on Fast and 115,481 on Chip with CopperSFS, compared with 158,090 and 158,153 with original SFS. The first relocation and durable commit account for the remaining gap. Raw call and growth profiles are in artifacts/sfs-chip-call-trace.

Growth had four device flushes on all three handlers. Original SFS needed eight writes and no read, compared with nine writes and one read on both CopperSFS builds. Of the candidate's growth block-copy cycles, 37,515 went to 15 private shared-pool edits and 38,812 to 16 copies in retained home-write runs. Dropping a durability barrier would not explain or fix that growth gap. In the partial-sector overwrite power-cut test, the first pre-flush transfer exposed 475 changed bytes after remount with original SFS and zero with CopperSFS. The aligned case exposed 512 bytes with original SFS.
