# SFS pressure retry qualification

The opt-in grouped SFS profile now reuses its emptied transaction arenas after
a pressure commit. On the paired workload this reduces commits from **14 to 10**,
device flushes from **49 to 36**, and mutation cycles by **1.97%** with **zero
additional per-mount RAM**. Original SFS remains faster; neither grouping nor
automatic TRIM is enabled by default by this change.

## Change and safety

Previously, an exhausted group committed its accepted checkpoint and retried the
current packet through the strict engine, which committed that packet separately.
The adapter now commits the accepted checkpoint and retries the complete packet
in the same, empty two-arena group. If it fits, subsequent packets can join it.
If it still fails for capacity or disk space, the existing strict fallback uses
the whole allocation. That fallback retains its existing bounded growth policy.

There is one grouped retry, not an unbounded retry loop. A rejected trial restores
the original request and accepted state. A failed checkpoint commit stops before
the retry. No journal encoding, disk structure, commit barrier or allocation
rule changes. Explicit filesystem flush, timer, inhibit and manual TRIM still
commit accepted work. Close remains deferred in this opt-in profile. Large Write
packets and mounts without a timer retain their strict path.

No cache was removed or repurposed. The SFS TRST build already omits the legacy
undo/dirty allocations and uses a one-slot physical write queue. This change
reuses the existing transaction buffers across pressure boundaries instead of
increasing their capacity. The initial group remains 8,716 bytes (8,720 bytes
after allocator rounding), with eight slots in each arena at 512-byte blocks.

## Paired results against original SFS

Three alternating trials for original SFS 1.279, the previous grouped candidate
and this candidate; identical media, 68020, 32 DOS buffers and explicitly
controlled Fast/Chip payload placement. Warm measurements separate mount from
mutation. Mutation includes the final ACTION_FLUSH; no deferred commit cost is
excluded. Cycles are means in millions.

| Phase | Original SFS | Previous grouped | Arena reuse |
|---|---:|---:|---:|
| Fast mount | 0.522 | 0.379 | 0.375 |
| Fast mutation | 16.986 | 21.174 | 20.758 |
| Fast read/verify | 17.570 | 20.167 | 20.181 |
| Chip mount | 0.524 | 0.378 | 0.375 |
| Chip mutation | 16.994 | 21.178 | 20.761 |
| Chip read/verify | 17.615 | 20.259 | 20.272 |

| Mutation work | Original SFS | Previous grouped | Arena reuse |
|---|---:|---:|---:|
| Commits (TROK writes) | 2 | 14 | 10 |
| Write requests | 17 | 133 | 112 |
| Device flushes | 7 | 49 | 36 |
| Read requests | 5 | 16 | 15 |

This is 28.6% fewer commits, 26.5% fewer flushes and 15.8% fewer write requests
than the previous candidate. Read/verify takes one additional read and about
0.06–0.07% more cycles; this is not a read-performance improvement. The warm
mutation gap to original is still about 22.2%. All six instrumented closes add
zero flushes. The final explicit flush adds three, bringing the total to 36.
Combined mutation plus verification improves about 0.97–0.98%; the 1.97% figure
is specifically the mutation phase, not an end-to-end speedup.

Cold original-handler parity gates include mount and final commit:

| Build / placement | Original mutation | Candidate mutation | Original verify | Candidate verify |
|---|---:|---:|---:|---:|
| AutoTrim / Fast | 17.518 | 21.122 | 17.572 | 20.184 |
| AutoTrim / Chip | 17.544 | 21.127 | 17.615 | 20.275 |
| No AutoTrim / Fast | 17.499 | 21.105 | 17.577 | 20.185 |
| No AutoTrim / Chip | 17.521 | 21.106 | 17.618 | 20.290 |

All four parity tests fail: mutation costs approximately 20–21% more cycles,
and verification approximately 15% more. Fewer commits are useful, but this
optimization does not establish original-handler performance parity. Remaining
bounded arena pressure and strict fallbacks still exist; this measurement does
not attribute every remaining commit to pressure.

## RAM and handler size

Both candidates retain 131,256 bytes after mount and 139,976 after mutation and
verification, consistently across all six warm trials. Original SFS uses the
same DOS buffer setting; this is not a matched-total-RAM comparison. No new
allocation, state field or larger buffer was introduced by the change.

| Handler | Previous HUNK | New HUNK | Previous code | New code |
|---|---:|---:|---:|---:|
| Grouped + AutoTrim | 157,640 | 157,556 | 154,094 | 154,016 |
| Grouped, no AutoTrim | 148,616 | 148,540 | 145,710 | 145,632 |

Both standalone builds report zero managed allocation sites. CILica scalar
replacement remains enabled and inlining disabled.

## Qualification

Host qualification, native replays and full-ROM tests are separate coverage.
The Release solution build passes. The complete solution test run also includes
the concurrently edited CPX project: its 12 `RecordLockPacketTests` cases fail,
returning `(0, 209)` (`ActionNotKnown`) instead of the expected record-lock packet
results. CPX production/test files were not changed by this SFS task. Therefore
the solution-wide test command is **not a pass**, independently of the SFS results.
The completed run has **1,897 passes, 12 failures and four skips**. SFS itself
passes **1,220 tests** with three external-media skips; FFS passes 296, the Amiga
host project 18 and storage benchmarks 32. The fourth skip is the opt-in CPX boot
test. Skipped external-media/platform coverage is not counted as a pass.
The focused host run passes 40 tests, including five new pressure cases covering
retained accepted data, rejected requests, retry in the same budget, power loss
before final flush, and failures at each of the four commit barriers.

CopperStart's affected native replay selection passes all 63 tests. Both affected
standalone handlers build successfully.

Full-ROM qualification passes **38 correctness cases**: 20 with AutoTrim and 18
without it. Two paired warm-comparison diagnostics also pass. The four cold
original-handler parity gates fail for performance only, as reported above;
there are no skipped tests in these full-ROM selections.

Full-ROM pressure tests verify that close adds no commit after the retry, power
loss retains only a prefix of complete packet edits, and explicit flush retains
all edits. Both CopperSFS and original SFS remount the results. Failure tests add
unpublished growth before the partial overwrites so all four durability barriers
are exercised, including the data barrier that a clean metadata-only transaction
can elide. Mutations remain refused while the fault is active; recovery and retry
complete after removing it. Existing grouped tests cover published journal
prefixes, original-handler recovery, flush/close/timer/inhibit/manual TRIM, clean
handoffs and automatic-TRIM reallocation/discard failure.

Physical-device completion, power-loss and performance tests were not run. The
results above are host/emulator coverage, not physical-device qualification.

## Receipts and reproduction

Ignored receipts and generated media are under `artifacts/sfs-buffer-reuse/`.
The previous reference is the frozen `artifacts/sfs-grouped/final-candidate/`
binary. No generated media or handler binaries belong in a source commit.

- Previous grouped AutoTrim SHA-256:
  `399797D0622338BEC241159FB2AD6EAEA3EAEFFC5361E8FA959ECC9740E3C6F1`.
- New grouped AutoTrim:
  `D8B2935E5F083FA7685357B822A42693E32013AA861F53B68643D857D6816D83`.
- New grouped without AutoTrim:
  `590D318710431534DB7B5C77F1E784329EB740FDEC8C29F8862DD5E6DAB6A8C8`.
- Original SFS:
  `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

```powershell
build/Build-Handler.ps1 -GroupedSfsTrst -AutoTrim -ArtifactRoot artifacts/sfs-buffer-reuse/candidate
build/Build-Handler.ps1 -GroupedSfsTrst -ArtifactRoot artifacts/sfs-buffer-reuse/no-auto
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/sfs-buffer-reuse/release
dotnet test CopperFS.slnx -c Release --no-build --artifacts-path artifacts/sfs-buffer-reuse/release
```

Full-ROM runs require the licensed ROM, `COPPERFS_SFS_GROUPED=1`, and
`COPPERFS_CORRECTNESS_MANIFEST` pointing at the corresponding qualification
manifest. `SfsGroupedWarmComparison` additionally uses `COPPERFS_SFS_REFERENCE`
for the previous candidate. Raw cycle summaries, commit traces, RAM observations,
build logs, host logs and full-ROM logs are retained beside the manifests.
