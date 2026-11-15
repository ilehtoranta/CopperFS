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
