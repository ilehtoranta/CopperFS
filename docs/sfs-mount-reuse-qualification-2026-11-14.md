# SFS mount and extent reuse

The shared-pool SFS handler uses 20 previously unused bytes in the standalone mount record. A successful mount records the selected root block, object container, node and extent tree roots, and volume flags. Group startup still reads the current free count. Recovery, failed group publication, inhibit, and media or geometry changes invalidate the mount information. Readers on an unvalidated volume use the existing checked traversal.

Opening a regular file seeds its existing read cursor from the verified object record. The first read still checks the extent record. Partial and fragmented writes reuse the current physical extent within a packet, with a fresh tree lookup on crossing its boundary. No borrowed metadata pointer survives another block access. The journal encoding, allocation admission, pressure retries and commit barriers follow the whole-group policy.

## Performance

Kickstart 3.1, emulated 68020, SFS 1.279, identical 16 MiB media, 512-byte blocks and 32 DOS buffers. Figures are mean whole-workload cycles across three alternating trials. The standalone build used `-SharedSfsPool -AutoTrim -Cpu 68000`, CILica scalar replacement on and inlining off. The saved candidate and the rebuilt handler both used whole-group overwrites and the default cache budget.

| Release gate | Original SFS | CopperSFS | Above original |
|---|---:|---:|---:|
| Fast mutation | 17,507,700 | 19,568,550 | 11.77% |
| Fast verification | 18,308,293 | 18,934,327 | 3.42% |
| Chip mutation | 17,515,677 | 19,571,990 | 11.74% |
| Chip verification | 18,353,817 | 19,021,430 | 3.64% |

All four measurements exceed the 0.1% release allowance. In the Fast gate, mixed mutation issued 23 reads, 67 writes and 12 flushes. Original SFS issued 7, 17 and 7 respectively. Verification issued 18 reads against the original's 13.

The separate three-handler comparison times mount independently. Each row below uses identical probes across all three arms.

| Fast RAM, cycles | Original SFS | Saved CopperSFS | Mount and extent reuse |
|---|---:|---:|---:|
| Mount | 522,010 | 377,610 | 378,080 |
| Mixed mutation after mount | 16,986,090 | 19,551,847 | 19,199,380 |
| Mixed verification | 18,309,903 | 19,217,383 | 18,934,067 |
| 96 KiB overwrite and explicit flush | 348,553 | 1,176,280 | 1,157,120 |
| Growth, overwrite and verification with commits | 5,782,583 | 6,942,937 | 6,859,443 |

The native platform's metadata-view calls fell from 714 to 603 in mutation and from 250 to 120 in verification. Root selections fell from 30 to 3 and from 32 to zero respectively. Marker readability checks fell from 25 to 1 and from 25 to zero. Mutation made one recovery check, down from four. Verification made zero, down from one. The mutation still executed three commits. The isolated overwrite still took 17 writes and four flushes against the original's 12 writes and one flush. Its three calls to `RelocateOverwrite` consumed 517,876 inclusive cycles. The final commit consumed 86,006 inclusive cycles.

For 1 MiB sequential reads, all three handlers issued 64 reads on 8 and 128 MiB volumes with contiguous and fragmented files. Across cold and warm passes in Fast and Chip RAM the new candidate used 0.41–0.49% fewer cycles than original SFS and 0.01–0.02% fewer than the saved candidate.

Exec free-memory differences measured 130,104 bytes retained after mount and 139,864 after mixed work for both Copper builds. These samples begin after loading the handler. The HUNK grew from 162,532 to 164,320 bytes and the native code grew from 158,744 to 160,164 bytes. The standalone compiler reported zero managed allocation sites.

## Recovery and native replay

The ten grouped recovery cases passed, covering the four publication barriers, pressure failures, recovery with either handler and continuation cuts. In the separate overwrite-contract experiment, original SFS remounted 43 candidate prefixes across aligned and partial overwrites, successful commits, injected partial writes and failed flushes. Every recovered candidate payload was entirely old or entirely new. Free space, the file size and the neighboring file's digest matched expectations at each cut. The original handler's own trace exposed intermediate prefixes of 512, 1,024 and 1,536 bytes for the aligned request and 475 and 987 bytes for the partial request.

The Release solution built and recorded 2,261 passes and five skips, including 1,328 SFS passes. The standalone M68000 binary built successfully. CopperStart passed all 198 selected instruction replays, including hashed lookup, fragmented transfers, buffer placement and invalidation. Its source-integrated adapter explicitly returns a null SFS mount record when the shared-pool profile is disabled.

The 15 additional full-ROM compatibility cases passed, covering close, explicit flush, timer, inhibit/remount, packet rejection, aligned and partial overwrites, cross-handler handoff and automatic discard after reallocation. The shared-pool handler also preserved the saved candidate's startup response and device ordering on foreign FFS media, with zero writes and zero flushes.

## Reproduction

The manifest, per-trial counts, device traces, profiles and logs are under `artifacts/sfs-mount-reuse`. The manifest pins original SFS and the base disk by SHA-256. Use `COPPERFS_SFS_REFERENCE` pointing at `artifacts/sfs1-redesign/check-metadata/CopperSFS`. Set `COPPERFS_SFS_GROUPED=1`, `COPPERFS_CORRECTNESS_MANIFEST` and `COPPERSHARP_KICKSTART31_ROM` for the ROM comparisons. The `original-performance` directories hold the release gates. The `warm` and `continuation` directories hold the three-handler comparisons. The `cpu-profile` and `overwrite-profile` directories hold instruction attribution. Physical hardware coverage remains untested.
