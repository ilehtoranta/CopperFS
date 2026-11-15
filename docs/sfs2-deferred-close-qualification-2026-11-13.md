# SFS2 deferred-close qualification

The opt-in owned SFS2 TRST profile now retains accepted transactions across
changed-file closes, using **zero additional per-mount RAM**. In the mixed
workload this reduces seven commits to one, device flushes from **25 to 4**,
and warm mutation cycles by **5.04%**. Original SFS uses two commits and seven
flushes in the same workload. The candidate still uses **20.77% more mutation
cycles than original**, so the release parity gate still fails. Owned SFS2
and automatic TRIM remain opt-in.

This report supersedes the close-policy conclusion in the earlier
[commit qualification](sfs2-commit-qualification-2026-11-13.md). That work
preserved the then-required durable-close policy; the current request
explicitly authorizes deferred closes.

## Why close can defer safely

Committing every changed close was our stronger durability policy, not a
requirement of the SFS2 disk format or TRST protocol. The accepted transaction
belongs to the mount, independently of a file handle. Retiring a handle can
release its reservation and issue its notification while the accepted images
remain available to subsequent packets, including reopen/read/overwrite.

`NativeSfs2Bridge.CanDeferClose` permits this only for an owned, ready SFS2
mount with the existing timer present and no pending recovery. An absent or
suspended context, changed geometry, pending recovery, SFS volume, or missing
timer retains the synchronous path. The checked grouped reference and CSUN
policy keep their existing close behavior. No new state fields, buffers,
allocations, disk structures, packet ABI, or journal encoding are introduced.

The five-second timer is armed from the first pending work and is not reset
by later packets. Explicit filesystem flush, timer expiry, inhibit, capacity
pressure and manual TRIM still commit pending work. A timer allocation failure
already causes the mutation path to commit synchronously. Capacity pressure
still commits the accepted group before retrying the rejected packet strictly.

**Close now guarantees acceptance and visibility, not power-loss durability.**
Unflushed closed changes can be lost before a later commit. Explicit
`ACTION_FLUSH` still waits for durable transaction completion and reports a
failed barrier. All four TRST ordering stages remain intact. A failed timer
or explicit commit suspends normal mutation until recovery succeeds;
automatic TRIM remains ineligible until durable completion. A low-level device
flush does not independently certify a filesystem transaction.

## Paired performance

The default comparison uses three alternating runs per handler, identical
16 MiB media and workloads, and an emulated M68020. Payload buffers are placed
explicitly in Fast or Chip RAM; handler and mount storage remain in Fast RAM.
All arms use 32 DOS buffers. Mount is timed separately. Mutation includes the
final explicit commit. Values are mean guest cycles rounded to whole cycles.

"Previous" is the frozen inline-journal candidate from the earlier commit
qualification, which used 25 flushes. The earlier 28-flush candidate predates
that optimization and is not substituted for this baseline.

| Handler | Fast mount | Fast mutation | Fast read/verify | Chip mutation | Chip read/verify |
|---|---:|---:|---:|---:|---:|
| Original SFS 1.279 | 522,143 | 15,085,783 | 16,684,567 | 15,085,603 | 16,718,950 |
| Previous candidate | 468,660 | 19,185,577 | 19,278,820 | 19,185,917 | 19,365,760 |
| Deferred-close candidate | 492,300 | 18,219,117 | 19,317,793 | 18,220,430 | 19,403,330 |

Fast mutation improves 5.04% from the previous candidate but remains 20.77%
above original. Read/verify is 0.20% slower than the previous candidate and
15.78% above original. Mount costs 23,640 more cycles than the previous build;
it is reported separately rather than hidden in the warm improvement.

| Warm mutation I/O | Original | Previous | Deferred close |
|---|---:|---:|---:|
| Commits | 2 | 7 | 1 |
| Device flushes | 7 | 25 | 4 |
| Read requests | 4 | 33 | 5 |
| Write requests | 17 | 47 | 17 |

The six changed closes issue no flushes. The final explicit flush completes
one combined transaction. Original SFS performs an earlier internal commit
and a final explicit commit; matching deferred-close semantics does not imply
identical internal batch boundaries. Across the whole probe, read bytes are
175,104 / 683,008 / 475,136 and write bytes are 145,920 / 160,256 / 141,312 for
original / previous / new respectively.

Matched-memory runs use 472 original DOS buffers and 32 candidate buffers.
Original post-mutation mount RAM is then 412,264 bytes versus the candidate's
412,328 bytes. Fragmented 8 and 128 MiB diagnostics use one alternating set
each; they are diagnostic samples rather than three-trial means.

| Fast mutation configuration | Original cycles | Previous cycles | New cycles |
|---|---:|---:|---:|
| 16 MiB, matched mount RAM, three trials | 15,070,373 | 19,185,497 | 18,219,087 |
| 8 MiB, fragmented, one trial | 15,261,160 | 19,566,340 | 18,451,340 |
| 128 MiB, fragmented, one trial | 15,283,210 | 20,099,290 | 18,950,810 |

All corresponding Chip cases also pass image/data checks; their new mutation
counts are 18,220,477, 18,451,440 and 18,951,520 cycles. The measured gaps remain
with matched RAM and fragmentation.

The separate cold release gates fail in all four configurations:

| Build / payload placement | Mutation overhead over original | Read/verify overhead |
|---|---:|---:|
| AutoTrim / Fast | 19.947% | 15.772% |
| AutoTrim / Chip | 19.918% | 16.039% |
| No AutoTrim / Fast | 19.434% | 15.556% |
| No AutoTrim / Chip | 19.426% | 15.841% |

These are performance assertion failures, not correctness passes. Neither
profile qualifies for default enablement under the original-handler parity
criterion.

## Deferred work is included

The automatic-TRIM diagnostic now measures the complete create/close/delete
foreground interval, then adds the later timer commit and both service turns.
Measuring delete alone would omit the work newly deferred from close.

| Component, cycles | Previous | Deferred close |
|---|---:|---:|
| Create, close and delete foreground | 2,055,370 | 1,779,730 |
| Timer commit | 206,686 | 65,712 |
| Both TRIM service turns | 139,363 | 142,500 |
| Total | **2,401,419** | **1,987,942** |

Total CPU cost falls **17.22%** with identical reclamation: two hints, two
UNMAP commands, and 32,768 bytes reclaimed. Delete alone falls from 306,110 to
254,000 cycles, but is a subset of the foreground row and must not be added
again. The timer commit drops from five writes/three flushes to one write/one
flush: this create/delete sequence cancels its metadata changes, leaving only
the existing unpublished-write drain. It is not a removed journal barrier.

The new service reads no blocks from the device, versus three before; retained
validated images satisfy those reads. Initial capability discovery occurs in
the first service turn; the second issues only UNMAP. The existing warm-service
test covers reuse without repeated discovery. Cold automatic-TRIM initialization
is separately 2,570 cycles (previous 2,574), with no I/O. The five-second waiting
interval is not counted as CPU work. These measurements do not model physical
device command latency.

## Remaining CPU cost and memory

In the instruction profile, `CommitTrst` falls from 1,651,070 cycles over
seven calls to 719,501 over one call: **56.42% less commit work**. Compression
planning falls from 306,455 cycles/42 records to 114,108/16. Commit now accounts
for about 3.95% of total mutation guest cycles, so flush count alone cannot
explain the remaining 3.13M-cycle gap to original.

Larger accepted groups increase rollback-preserving clone work: `Clone`
inclusive time rises from 339,917 to 873,974 cycles. Exclusive `CopyBlock`
time rises from 1,164,155 to 1,452,185 cycles. Metadata `ReadView` falls from
327,608 to 294,533 cycles. Inclusive clone and exclusive copying overlap and
must not be summed. Copying accepted state and metadata access remain concrete
optimization targets; this change preserves the proven two-arena protocol.

The candidate consumes **412,328 bytes of mount RAM**, unchanged at mount,
after mutation and after verification. Owned metadata uses 16,508 bytes plus
12 state bytes, replacing the existing 16,520-byte undo allocation. The
transaction arenas remain 196,608 bytes, with 29 clean-cache slots. Additional
per-mount RAM is zero. Original uses 172,904 bytes after mutation with its
default 32 buffers; the matched-memory comparison above accounts for this.

| Binary | HUNK file bytes | Loaded code bytes |
|---|---:|---:|
| Previous AutoTrim candidate | 225,416 | 219,288 |
| Deferred close, AutoTrim | 225,556 | 219,416 |
| Deferred close, no AutoTrim | 213,372 | 208,188 |

The AutoTrim build adds 128 loaded code bytes, separately from the unchanged
per-mount allocation. CILica scalar replacement stays enabled and inlining
disabled.

## Qualification coverage

- **Host:** Release solution build and tests pass. Filesystem suites have
  1,547 passes and three external-image skips. Concurrent CPX/prototype suites
  add 152 passes and one skip; those are not SFS qualification evidence.
  The build retains one existing CopperSharp nullable warning.
- **Native:** both affected standalone handlers build. CopperStart native
  replay has 193 passes; compiled 68000/68020 allocation-fallback, recovery
  barrier and close-policy checks have 14 passes. The new policy probe also
  verifies no managed allocation sites.
- **Full-ROM correctness:** 73 selected cases pass: 55 with AutoTrim and 18
  with no AutoTrim. They cover cross-close visibility, flush/timer/inhibit
  boundaries, pre-boundary cuts, original-SFS replay, cuts around journal and
  home writes, every commit barrier, grouped growth and overwrite, reallocation,
  rejected packets, capacity-pressure retry, SFS handoff, CSUN recovery,
  foreign startup/recovery ordering, notification behavior, manual TRIM,
  automatic-TRIM remount, partial discard failure and power-cut recovery.
- **Full-ROM performance:** paired diagnostics and image checks pass; all
  four original-handler parity gates fail as quantified above.
- **Physical devices:** not tested. Emulator storage/failure injection does
  not establish real-drive flush, power-loss or UNMAP behavior.

The new cross-close workload overwrites an existing file, closes/reopens it,
grows and shrinks it across closes, deletes a newly created file, allocates a
replacement, renames the original and updates its comment. Tests verify the
accepted data before commit, and the old or complete new state after cuts and
replay. Persistent failures at barriers 1-4 are tested for both explicit and
timer commits, including blocked mutation followed by successful recovery.
Manual TRIM DryRun commits the pending closed group before validation without
issuing UNMAP.

An initial fault-test run was stopped because AmigaDOS opened an unattended
error requester. The probe now sets its process WindowPtr to -1; the complete
eight-case fault suite then passes. The aborted run is not counted as a pass.

## Reproduction and evidence

Ignored local evidence is under `artifacts/deferred-close/`. Build commands:

```powershell
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -AutoTrim -Sfs2ExtraKiB 0 -SkipCompilerBuild -ArtifactRoot artifacts/deferred-close/candidate
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -Sfs2ExtraKiB 0 -SkipCompilerBuild -ArtifactRoot artifacts/deferred-close/no-auto
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/deferred-close/release
dotnet test CopperFS.slnx -c Release --no-build --artifacts-path artifacts/deferred-close/release
```

ROM runs use the licensed local `COPPERSHARP_KICKSTART31_ROM`,
`COPPERFS_SFS2_OWNED=1`, `COPPERFS_SFS2_GROUPED=1` and
`COPPERFS_SFS2_DEFERRED_CLOSE=1`. Set `COPPERFS_SFS2_TRST_HANDLER` to the
candidate, `COPPERFS_ORIGINAL_SFS` to the original and
`COPPERFS_CORRECTNESS_MANIFEST` to `qualification/manifest.json` (or the
no-auto manifest). The paired baseline is pinned with
`COPPERFS_GROUPED_REFERENCE` and `COPPERFS_GROUPED_REFERENCE_SHA256`.
`OwnedSfs2SeparatesMountAndSteadyState` writes the phase/I/O samples;
`TraceSfs2CommitBoundaries` asserts zero close flushes and final durability.
Matched-memory runs set `COPPERFS_MATCH_ORIGINAL_BUFFERS=472`. Fragmented
diagnostics set `COPPERFS_OWNED_FRAGMENTED=1`, `COPPERFS_OWNED_TRIALS=1` and
`COPPERFS_OWNED_VOLUME_MIB=8` or `128`.

The logs `boundary-tests.log`, `fault-tests.log`, `manual-trim.log`,
`rom-main.log`, `rom-foreign.log` and `no-auto-qualification.log` contain the
73 correctness cases. `performance.log` and `no-auto-qualification.log`
retain the failing release gates. `qualification/owned-performance` contains
per-phase cycles, I/O and mount-memory snapshots. `qualification/owned-cpu-profile`
contains instruction attribution. `maintenance-before` and `maintenance-after`
contain the complete foreground and deferred-maintenance accounting.

Pinned SHA-256 identities:

| Artifact | SHA-256 |
|---|---|
| Original SFS 1.279 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| Previous AutoTrim candidate | `2F0A591DEDBC205DCEFFFDDFEAD88A671D0811443FB86879E6C780D958872ECA` |
| Deferred close, AutoTrim | `7E621B0E503CEB621C35B0F05C33EF5A2316ED70DC863D22250A2DFE44744E29` |
| Deferred close, no AutoTrim | `79B0FB11561575B083D10B0EFD053A3FD8E8D1F9BCFC48327FCC86ABD1D6B82F` |

Unchanged checked TRST and CSUN binaries from `artifacts/commit-tuning` are
used as recovery/handoff references. No ROMs, media or generated binaries are
source changes or publication inputs.
