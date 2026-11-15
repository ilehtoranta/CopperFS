# SFS rejection handling and transaction capacity

The opt-in grouped SFS profile no longer invokes recovery for known harmless
packet rejections when its accepted state is intact. Its existing RAM budget
also holds nine block images per transaction arena instead of eight. On the
paired workload, commits fall from **10 to 9** and device flushes from **36 to
33**. Total CPU time is effectively unchanged. Original-handler parity still
fails, so this does not enable grouping or automatic TRIM by default.

## Rejection policy

The exemption covers ObjectExists, ObjectNotFound, ObjectWrongType,
DeleteProtected, WriteProtected, DirectoryNotEmpty, ObjectInUse, InvalidLock and
SeekError. It applies only to SFS, with no recovery-pending flag, no failed group
publication and an overlay matching the accepted checkpoint (or no overlay when
there is no pending group). Other errors retain recovery processing. In
particular, allocation, disk-space, device and validation failures are not
classified as harmless.

Rejected grouped packets already restore their request, roots and accepted
overlay. The adapter now preserves that state instead of committing it through
the generic recovery path. Explicit flush, timer, inhibit and manual TRIM retain
their commit boundaries; changed-file close retains the existing opt-in deferred
policy. The transaction protocol, four durability barriers, journal encoding,
CSUN recovery and disk structures are unchanged.

## Capacity without additional RAM

The enumeration cache keeps all 32 entries. Each SFS FileInfoBlock has duplicate
entry types and 32 reserved bytes that the core clears. The private cache now
stores 224 bytes: the owner UID/GID occupies the duplicate type word. A hit
restores the owner, reconstructs the second type and clears the reserved bytes.
Full-length filenames and comments are retained. Extended ExamineNext packets
still derive their 64-bit fields after the cache step.

This reduces each cache entry from 276 to 240 bytes and the cache allocation
from 8,836 to 7,684 bytes. At 512-byte filesystem blocks, the saved 1,152 bytes
allow the group allocation to rise from 8,716 to 9,756 bytes. The two-arena
accepted/trial rollback scheme remains unchanged. No new state fields or
normal-operation allocations are introduced by the change.

If the optional enumeration allocation is unavailable, the original group budget
is used. If the larger group allocation fails, allocation retries the original
smaller budget. Both failing allocations preserve the existing failure behavior.
Larger filesystem blocks use only capacity fitting the same reclaimed byte
budget; they do not necessarily gain a slot. The strict fallback and its existing
maximum allocation are unchanged.

Measured retained memory, including allocator rounding:

| Point | Previous candidate | Candidate |
|---|---:|---:|
| After mount | 131,256 | 130,104 |
| After mutation | 139,976 | 139,864 |
| After verification | 139,976 | 139,864 |

These measurements are identical across the Fast/Chip warm trials: 1,152 bytes
less at mount and **112 bytes less** once the group is allocated.

An initial experiment reduced enumeration capacity to 16 entries to obtain 12
transaction slots. Repeated scans of a 24-entry directory became approximately
65% slower. That policy was rejected and is not in the final source.

## Paired performance

Three alternating trials per arm, original SFS 1.279, the previous pressure-retry
candidate and this candidate. All arms use identical media, a 68020, 32 DOS
buffers and explicitly controlled Fast/Chip payload placement. Warm timing
separates mount; mutation includes the final ACTION_FLUSH. Means are millions
of cycles.

| Phase | Original SFS | Previous candidate | Candidate |
|---|---:|---:|---:|
| Fast mount | 0.522 | 0.375 | 0.375 |
| Fast mutation | 16.992 | 20.758 | 20.800 |
| Fast verification | 17.569 | 20.181 | 20.134 |
| Chip mount | 0.524 | 0.375 | 0.375 |
| Chip mutation | 17.006 | 20.761 | 20.802 |
| Chip verification | 17.617 | 20.272 | 20.221 |

Mutation takes approximately 0.2% more cycles; verification takes approximately
0.23–0.25% fewer. Combined mutation and verification improves only 0.01–0.02%:
there is no meaningful end-to-end speedup. The warm mutation gap to original SFS
remains approximately 22%.

| Mutation work | Original | Previous | Candidate |
|---|---:|---:|---:|
| Commits (TROK writes) | 2 | 10 | 9 |
| Device flushes | 7 | 36 | 33 |
| Write requests | 17 | 112 | 108 |
| Physical bytes written | 150,016 | 210,432 | 230,400 |

Fewer commits do not guarantee less work. The changed grouping boundaries produce
fewer write requests but approximately 9.5% more physical bytes on this workload.
This is a measured limitation, not a performance win hidden by deferred timing.

Cold original-handler gates include mount and final commit:

| Build / placement | Original mutation | Candidate mutation | Original verification | Candidate verification |
|---|---:|---:|---:|---:|
| AutoTrim / Fast | 17.501 | 21.163 | 17.575 | 20.136 |
| AutoTrim / Chip | 17.513 | 21.168 | 17.617 | 20.225 |
| No AutoTrim / Fast | 17.495 | 21.155 | 17.576 | 20.116 |
| No AutoTrim / Chip | 17.527 | 21.155 | 17.621 | 20.201 |

All four parity gates fail: approximately 21% more mutation cycles and 14–15%
more verification cycles than original SFS.

Separate enumeration diagnostics measure four warm scans after an untimed scan;
all three arms issue zero device reads/writes/flushes in the measured scans:

| Directory | Original cycles | Previous cycles | Candidate cycles |
|---|---:|---:|---:|
| 24 entries | 1,964,050 | 1,255,160 | 1,286,010 |
| 64 entries | 5,169,170 | 5,288,410 | 5,295,890 |

The compact representation costs about 2.5% versus the previous 24-entry cache
hit workload, and about 0.1% for 64 entries. The 24-entry case remains about 35%
faster than original SFS; the 64-entry case remains about 2.5% slower. These are
single-run diagnostics, separate from the three-trial release comparison.

## Qualification and code size

Release solution build/tests passed: **2,003 passes, four skips**, including
1,225 SFS passes and three external-media skips. The fourth skip is the opt-in
CPX boot test. The focused host run subsequently passes all 50 group/recovery
cases, including the five nine-slot pressure/failure cases added after that
solution build. Existing eight-slot and twelve-slot cases remain covered.

Ten compiled MC68000/68020 instruction tests pass. They check allocation failure
fallback, absence of optional enumeration storage, budget bounds at 512/1024/
2048/4096-byte blocks, stable allocation reuse, failed-publication/recovery flags,
overlay mismatch and byte-exact cache reconstruction including owner fields,
reserved bytes and surrounding canaries.

CopperStart's affected native replay selection passes **63 tests** against the
compact candidate. Both grouped standalone builds succeed with CILica scalar
replacement enabled, inlining disabled and zero managed allocation sites.

Full-ROM tests explicitly verify that missing/duplicate objects, protected
deletion/writes and nonempty-directory rejection issue no writes or flushes while
accepted metadata is pending. Power cuts lose only that uncommitted metadata;
explicit flush preserves it. Both CopperSFS and original SFS remount the results.
Other coverage includes pressure, failures at every commit barrier, published
journal prefixes, recovery refusal while faults persist, flush/close/timer/
inhibit/manual TRIM, original-handler handoffs and automatic-TRIM reallocation
and discard failure.

The grouped profiles pass **42 full-ROM correctness cases** (22 with AutoTrim,
20 without), plus two warm paired-comparison diagnostics and two enumeration
diagnostics. Four original-handler performance gates fail as quantified above;
there are no skipped tests in these full-ROM selections.
The default ungrouped handler was also rebuilt and passes three full-ROM smoke
checks for clean handoffs, unchanged metadata and commit/close boundaries.
That brings the full-ROM correctness total to **45**. A final AutoTrim rebuild
from the final source is byte-identical to the measured compact candidate.

| Build | Previous HUNK | Candidate HUNK | Previous code | Candidate code |
|---|---:|---:|---:|---:|
| Grouped + AutoTrim | 157,556 | 158,488 | 154,016 | 154,544 |
| Grouped without AutoTrim | 148,540 | 148,732 | 145,632 | 145,790 |

Physical-device power-loss, completion and performance testing was not performed.
Host, native replay and full-ROM coverage are not physical-device qualification.

## Receipts

Ignored artifacts are under `artifacts/sfs-capacity/`. Final measurements use
`compact-qualification` and `compact-no-auto-qualification`; the earlier
`qualification` directory records the rejected 16-entry-cache experiment.

- AutoTrim candidate SHA-256:
  `F98067A9040F8ED4D6EDD8467BD967615741472A68146801A21F943999279C21`.
- No-AutoTrim candidate:
  `4C3B816D258EF06BFB67B7ABC84998AE9F83939532DF47F7C31B290CE0C653BF`.
- Previous pressure-retry AutoTrim candidate:
  `D8B2935E5F083FA7685357B822A42693E32013AA861F53B68643D857D6816D83`.
- Original SFS:
  `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

The original fixture path was unavailable. A retained fixture copy was verified
against the existing SHA-256 before copying into this run's artifact directory:
`18170E0D35A5307AA29F17751B50BC848EB7B47211EB76FC1A88E95091409137`.
No media, ROMs or generated handler binaries are source-commit inputs.

Build with `build/Build-Handler.ps1 -GroupedSfsTrst`, optionally `-AutoTrim`.
Full-ROM tests require the licensed ROM, `COPPERFS_SFS_GROUPED=1`, the corresponding
`COPPERFS_CORRECTNESS_MANIFEST`, and `COPPERFS_SFS_REFERENCE` for paired comparisons.
Instruction tests require `-p:CopperFsSfsGrouped=true` and filter
`FullyQualifiedName~SfsCapacityInstructionTests`.
