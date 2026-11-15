# SFS overwrite at the end of an extent

The shared-pool SFS handler can now finish overwriting the last extent of a file using its existing relocation state. The 96 KiB overwrite including explicit flush improved by 8.69% in Fast RAM and 8.53% in Chip RAM against the preceding candidate. Both RAM classes still take 2.46 times the cycles of original SFS 1.279.

## Where the cycles went

The workload overwrites an existing, split file in twelve 8 KiB packets. CopperSFS keeps the previously committed payload reachable until the replacement group is published. A general relocation must find the extent, find space free in both allocation views, and change the extent tree and allocation metadata. The preceding handler had a cheaper continuation for adjacent packets but stopped a block short of consuming the last extent. That forced another general relocation.

| Instrumented overwrite including flush | Original SFS | Preceding CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Whole guest, cycles | 324,580 | 872,920 | 797,120 |
| Handler instructions, cycles | 156,496 | 725,177 | 649,178 |
| General relocation, calls / inclusive cycles | — | 3 / 244,928 | 2 / 169,879 |
| Extent record lookup, calls / inclusive cycles | — | 9 / 75,328 | 6 / 52,704 |
| Bounded free-run scan, calls / inclusive cycles | — | 4 / 55,324 | 3 / 36,145 |
| Unpublished payload dispatch, calls / inclusive cycles | — | 12 / 33,913 | 12 / 33,935 |
| Explicit filesystem flush, inclusive cycles | — | 97,858 | 112,770 |
| Device reads / writes / flushes | 0 / 12 / 1 | 0 / 17 / 4 | 0 / 17 / 4 |

Method timings overlap. Finishing the deferred edit made the flush itself more expensive, and the whole guest still saved 75,800 cycles. The two remaining general relocations cost 169,879 cycles. Checking and applying continuations took 172,023 cycles over eleven calls. Those are the largest remaining identified costs in overwrite processing. Original SFS had no symbol map in this run.

## Implementation and recovery

The specialization requires a validated shared-pool continuation, a terminal extent with no successor, and private images for all affected metadata. The destination must be free in the committed and accepted views. The existing journal-space and bitmap bounds still apply. The final packet extends its predecessor, removes the exhausted extent record, reseals the leaf, updates the allocation words and retires the continuation. Metadata publication still follows successful payload transfer. A deferred edit uses the same finalization when the group is materialized. Ordinary relocation handles unsupported shapes.

The final HUNK is 168,024 bytes (+264), including 163,864 code bytes (+192). Measured retained memory for both Copper builds is 126,008 bytes at mount and 138,888 after mixed mutation. Original SFS used 156,480 and 172,904 bytes respectively. The optimization adds no per-mount storage. Scalar replacement was enabled and inlining disabled.

## Paired performance

Three alternating ordinary trials per handler and caller RAM class used Kickstart 3.1, an emulated 68020, identical 16 MiB SFS media and 32 DOS buffers. Timing includes explicit commits. The earlier 2.51× comparison included an extra three flushes in one original trial per RAM class. Original SFS issued one flush in every isolated overwrite in this rerun, putting the preceding handler at 2.69× on the fresh controls.

| Mean guest cycles | Original SFS | Preceding CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast overwrite and flush | 324,557 | 872,963 | 797,123 |
| Chip overwrite and flush | 324,853 | 873,637 | 799,133 |
| Fast growth, overwrite and verification through commits | 5,759,170 | 6,501,113 | 6,402,980 |
| Chip growth, overwrite and verification through commits | 5,765,163 | 6,499,377 | 6,408,997 |
| Fast warm mixed mutation | 16,973,190 | 17,761,197 | 17,787,757 |
| Chip warm mixed mutation | 16,993,850 | 17,762,570 | 17,789,283 |
| Fast mixed verification | 20,072,133 | 20,684,217 | 20,672,230 |
| Chip mixed verification | 20,140,773 | 20,787,933 | 20,780,370 |

Warm mixed mutation regressed 0.15% against the preceding candidate in each RAM class. The independent release gate includes mount in mutation. Revised CopperSFS exceeded original by 3.781% for Fast mutation, 2.955% for Fast verification, 3.661% for Chip mutation and 3.191% for Chip verification. The release maximum is 0.1% per phase. The shared-pool profile remains opt-in.

## Recovery and compatibility qualification

The Kickstart 3.1 power-cut replay used the actual 96 KiB workload. At nine cuts during overwrite and commit, both original SFS and revised CopperSFS recovered the entire old or new file. Twelve grouped recovery/pressure cases and both partial journal and partial home-write tests passed. A failed partial journal read could be retried successfully. The boundary trace and all 22 handoff, close, flush, timer, rejection and enumeration cases passed, as did seven foreign/dirty startup cases and five automatic-TRIM cases.

The independent overwrite-contract test remounted every saved sector prefix with original SFS, checked neighboring contents and free space, and included injected payload-write and flush failures. All 43 CopperSFS prefixes had wholly old or wholly new payload. Across 26 original-SFS prefixes the aligned overwrite also exposed 512-, 1,024- and 1,536-byte prefixes of a 2,048-byte replacement. The partial-sector case exposed intermediate prefixes of 475 and 987 bytes out of 1,200. Matching the original's in-place writes would weaken the tested whole-group guarantee.

The Release solution built with zero warnings and errors. Its test run recorded 2,305 passes, eight conditional skips and zero failures. Three skips were external SFS/SFS2 fixture tests and five were optional CPX native tests. Six targeted host cases exercised the new terminal edit with deferred and immediate metadata updates, both record orders, or a failed second sector followed by retry. They checked visibility of accepted data, the durable pre-flush image, post-flush remount, the neighboring file and allocation count.

Both standalone M68000 handler configurations built. CopperStart passed 186 cache, transfer, deletion, buffer and geometry replays and 77 link and size replays. The native compiler reported zero managed allocation sites for the revised handler. Physical Amiga and storage-device tests were outside this run.

The five full-ROM sequential-read fixtures passed. Across all 20 Fast/Chip and cold/warm comparisons, the three handlers each needed 64 device reads per 1 MiB. Revised CopperSFS used 0.41–0.50% fewer cycles than original SFS.

## Reproduction

The manifests and scalar profiles for the three binaries are in `artifacts/sfs-overwrite-tail/{original,saved,final}`. The revised directory contains the ordinary trials under `continuation-fast`, `continuation-chip`, `warm-fast` and `warm-chip`. The isolated scalar profile is `overwrite-profile/candidate/overwrite-profile.json` under each arm. `COPPERFS_SFS_REFERENCE` points to the preceding binary in `artifacts/sfs-io/release-candidate/candidate/CopperSFS`.

| Handler | SHA-256 |
|---|---|
| Original SFS 1.279 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| Preceding shared-pool candidate | `A2F5FC94158FFDB6117429ED68FC04BA7371B75B3EF1A8C42F555555BCB1B71B` |
| Revised shared-pool candidate | `42C554CF2440ED2F6C6123B81F8FCEB490A9C574CA169F17B157ADD29C6BBB39` |
