# SFS mixed mutation writes

The workload creates directories, grows and partially overwrites files, deletes a donor file, renames a file, truncates and replaces files, edits metadata, and explicitly flushes. The comparison uses original SFS 1.279 and the previous CopperSFS whole-group handler from `artifacts/sfs-overwrite-relocation/handler`. Measurements use emulated 68020 execution under Kickstart 3.1, identical 16 MiB images, 512-byte sectors and 32 DOS buffers.

## Why the previous candidate issued 67 commands

| Commit interval | Cause | Payload commands | External journal pages | Publication marker | Home blocks | Clean marker |
|---|---|---:|---:|---:|---:|---:|
| First | Growth packet exhausted the shared image pool | 2 | 5 | 1 | 12 | 1 |
| Second | Rename exhausted the pool | 3 | 6 | 1 | 17 | 1 |
| Third | Explicit filesystem flush | 1 | 2 | 1 | 13 | 1 |
| Total | | 6 | 13 | 3 | 42 | 3 |

The first pressure retry occurred after a 63-sector unpublished payload transfer. The failed packet left the accepted file intact. The commit reused the beginning of that free extent for its journal, then the retried packet rewrote the 63 sectors. Reading each destination before writing showed 58 byte-identical sector writes across this workload. None of the 42 home writes was byte-identical to the preceding device image.

Original SFS used two journal publications. The first used an inline TRST, a ten-sector home write and a clean marker. The second included an external journal, a TRFA publication, two home writes and a clean marker. The original physical trace contains 293 sectors in 17 commands and seven flushes. Its home runs sometimes include intervening unchanged blocks. CopperSFS only groups adjacent targets already present in its published journal.

## Changes

The grouped append path can retain a pointer to the packet's final contiguous full-sector body until the trailing sector and metadata edits fit the transaction. It checks both allocation views before transferring the body and requires transfer success before accepting the packet. The pointer's lifetime ends inside the synchronous write. Separate unpublished and staged-data paths still handle blocks already owned by a checkpoint.

After durable journal publication, the grouped handler sorts at most 64 home-image pointers in the existing bitmap scratch and gathers adjacent images in the mount's existing DMA buffer. A failed transfer invalidates cached images across the attempted run. The accepted images and journal remain available to recovery. The journal format, explicit flush and all four durability barriers follow the previous protocol.

With just these changes, the same 42 home sectors took 16 commands. Eliminating the failed payload transfer saved another command and 32,256 bytes. The result was 40 commands, three commits, 12 flushes and 155,136 bytes. Gathering home images alone adds copying, so the qualification also measures execution cycles.

## Transaction capacity within the same mount budget

The final shared-pool profile keeps 32 directory enumeration buckets. Each bucket caches a complete result when its name is at most 31 bytes and its comment at most 23 bytes. Longer results use the normal checked enumeration cursor. The freed 4,096 bytes fund optional transaction images. The allocation uses the previous arena's actual retained size, including allocator rounding at each filesystem block size, as its upper bound. On the 512-byte fixture the pool fits 22 images, up from 17. Allocation failure retains the smaller pool.

The final trace combined file growth and rename in the first group. The subsequent 4,097-byte write to the new file triggered the remaining pressure commit. The second commit was the explicit flush. Both groups used external TRST pages and all four durability barriers. The first committed 18 home blocks in seven commands, the second 13 in five commands. Across the two groups there were five payload commands, 11 external journal pages, two TRFA publications, 12 home commands and two TROK writes. All 288 sectors differed from the preceding device image.

| Mixed mutation I/O | Original SFS | Saved candidate | Batched home writes and delayed append | Final candidate |
|---|---:|---:|---:|---:|
| Commits | 2 | 3 | 3 | 2 |
| Device write commands | 17 | 67 | 40 | 32 |
| Device flushes | 7 | 12 | 12 | 8 |
| Bytes written | 150,016 | 187,392 | 155,136 | 147,456 |

The final candidate issued 52.2% fewer write commands and transferred 21.3% fewer bytes than the previous candidate. It transferred 2,560 fewer bytes than original SFS but needed 15 more write commands. Original SFS's first inline journal and its larger, occasionally overlapping contiguous writes account for different command and barrier counts. In the instruction profile the time inside commit fell from 1,668,408 to 1,327,629 cycles. These figures include nested I/O and encoding work.

## Release gate and memory

The existing gate measures whole-probe cycles on the ordinary frame runner, including mount in the mutation phase. Means are from three alternating trials. Each phase allows at most 0.1% above original SFS.

| RAM | Phase | Original SFS | Final candidate | Above original |
|---|---|---:|---:|---:|
| Fast | Mixed mutation | 17,509,697 | 19,064,003 | 8.88% |
| Fast | Mixed verification | 18,306,957 | 18,951,583 | 3.52% |
| Chip | Mixed mutation | 17,510,450 | 19,066,620 | 8.89% |
| Chip | Mixed verification | 18,353,750 | 19,038,893 | 3.73% |

At 32 DOS buffers, both CopperSFS builds load before the Exec free-memory samples. The saved candidate retained 130,104 bytes at mount and 139,864 bytes after mixed work. The final candidate retained 126,008 and 138,888 bytes, savings of 4,096 and 976 bytes respectively. The HUNK grew from 165,296 to 166,200 bytes and the native code grew from 161,084 to 162,088 bytes. Loaded code is separate from per-mount memory. The native compiler reported zero managed allocation sites. The build uses scalar replacement with inlining disabled. The shared-pool profile remains opt-in.

The separate growth/overwrite/verify workload, including commits, rose from 6,612,940 to 6,631,510 Fast RAM cycles (+0.28%) and from 6,618,930 to 6,637,350 Chip RAM cycles (+0.28%). The isolated overwrite and flush fell from 914,410 to 910,163 Fast RAM cycles. Repeated enumeration of 24 and 64 entries cost 1.65% and 0.75% more than the saved candidate. A full-ROM test verified repeated enumeration of a 60-byte name and 70-byte comment with both handlers. Ten 68000/68020 allocation and cache-layout cases passed, including fallback on allocation failure.

The three-handler benchmark measures mount separately, then runs mixed mutation through the final explicit filesystem flush.

| Payload RAM and phase | Original SFS | Saved candidate | Final candidate |
|---|---:|---:|---:|
| Fast mutation | 16,986,167 | 19,219,163 | 18,688,100 |
| Chip mutation | 16,991,370 | 19,220,367 | 18,691,233 |
| Fast verification | 18,306,510 | 18,932,163 | 18,952,750 |
| Chip verification | 18,351,260 | 19,020,697 | 19,039,143 |

Fast mutation saved 531,063 guest cycles (2.76%) over the saved candidate. Chip mutation saved 529,134 (2.75%). Mixed verification used about 0.1% more cycles than the saved candidate.

## Recovery and compatibility

For each of the two published groups, a new full-ROM replay retained the first sector of a multi-sector home write and cut the command before its second sector. Both CopperSFS and original SFS replayed the resulting image to the same files, sizes, contents and free-block count as recovery from the journal publication. The existing overwrite-contract test also remounted 43 aligned and partial candidate prefixes with original SFS, covering successful flush, failed flush and partial payload transfer. The file recovered wholly old or wholly new in every case. Neighboring contents, size and allocation checks passed.

All 12 grouped failure, retry and recovery cases passed, including failures at each durability barrier and reconciliation with both handlers. The 11 compatibility cases covered deferred close, explicit flush, timer, inhibit/remount, harmless rejection, directory enumeration and handoff. Five automatic TRIM cases and five foreign-startup cases passed. These are Kickstart 3.1 emulator runs. Physical hardware and device latency remain unverified.

Both the default and shared-pool standalone M68000 builds passed. CopperStart passed 186 selected native replay cases with the final handler selected, including physical cache invalidation against the saved binary. The supplementary link and file-size suite passed 77 cases with the source-integrated SFS2 backend enabled for its wide-file fixtures. In the first run four SFS2-only fixture cases failed with that backend disabled.

The final Release solution build completed with zero warnings and errors. The complete solution rerun recorded 2,276 passes, five skips and zero failures, including 1,330 SFS passes. All five full-ROM sequential-read cases passed. At equal DOS buffer counts, 1 MiB cold and warm reads on contiguous and fragmented 8 and 128 MiB volumes issued 64 reads per pass and used 0.41–0.49% fewer cycles than original SFS.

Raw per-command and sector evidence is under `artifacts/sfs-mixed-writes/baseline/commit-trace` and `artifacts/sfs-mixed-writes/final/commit-trace`. The intermediate experiment is under `artifacts/sfs-mixed-writes/commit-trace`. The final manifest selects `final/handler/CopperSFS` (SHA-256 `5E81486EB3107F8068201F77BA898BF9BFC0DFA32082D48147C64BD83E7796CD`). Use the saved `sfs-overwrite-relocation/handler/CopperSFS` for `COPPERFS_SFS_REFERENCE`, and set `COPPERFS_SFS_GROUPED=1`. Per-trial summaries are in the final artifact directory under `warm-*`, `original-performance*`, `continuation-*` and `enumeration`.
