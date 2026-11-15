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
