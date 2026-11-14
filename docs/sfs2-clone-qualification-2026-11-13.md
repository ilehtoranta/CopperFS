# SFS2 metadata-copy and clone qualification

The opt-in owned SFS2 TRST profile now copies only changed images when preparing
its next private transaction arena. Against the frozen deferred-close candidate,
warm mutation uses **2.42% fewer cycles**, transaction cloning uses **51.67% fewer
cycles**, and exclusive block-copy time falls **38.17%**. Additional per-mount
allocation remains **zero**. Recovery and explicit-flush ordering are unchanged.

Original SFS still leads: the new candidate uses **17.83% more mutation cycles**
in the paired Fast-memory workload. The redesign and automatic TRIM remain
opt-in; this does not qualify either for default enablement.

## Changes and safety conditions

The two physical accepted/trial arenas remain separate. When the inactive arena
is the accepted predecessor of the current view, unchanged slots already hold
identical private images. Clone compares block identity and revision, copies
changed/new slots, and retains checksum verification for unchanged revisions.
The hash index is copied when keys have been appended; it can remain in place
when the count is unchanged. Transaction keys do not disappear during a group.

A previously unused word in the existing 32-byte group header records the
predecessor relationship. It is cleared **before** touching a trial and restored
only after packet acceptance. This is essential: a rejected trial can contain
different bytes with revision numbers equal to another attempt, even if it was
sealed before acceptance failed. Such trials always require a complete clone.
The first clone in a new group also copies completely, preventing accidental
reuse of block/revision pairs from a previous committed group. Source/destination
layout, capacity and pin checks remain; each clone receives a fresh handle epoch.

The public standalone `Clone` entry point retains its full-copy behavior. Only
the group owner can request predecessor reuse. No shared writable images,
copy-on-write references, heap allocations or additional cache slots are added.

SFS2 mutation pathname and description helpers now consume borrowed scalar
results rather than requesting a full block in scratch when their callers only
need a node number, size, directory flag or parent. The checked portable entry
points keep their existing image-returning contracts. SFS behavior and CSUN
journal policy are unchanged.

New host cases verify reduced cloning across 512/1024/4096-byte blocks, appended
keys, stale handles, attempt-generation wrap, arena reuse after a commit, partial
rejection and rejection after sealing. The grouped power-cut test now executes
enough accepted packets to exercise incremental cloning before every cut.
Existing owned-state tests cover tree growth, relocation, allocation pressure,
rollback, pinned images and recovery.

## Paired performance

Three alternating runs per arm use identical 16 MiB images and DOS operations
on the emulated M68020. Payload placement is explicitly Fast or Chip RAM;
handler and mount state remain in Fast RAM. Default comparison uses 32 DOS
buffers for each arm. Mount is separate, and mutation includes its final
explicit commit. Values below are mean guest cycles rounded to whole cycles.

| Handler | Fast mount | Fast mutation | Fast read/verify | Chip mutation | Chip read/verify |
|---|---:|---:|---:|---:|---:|
| Original SFS 1.279 | 522,160 | 15,088,043 | 16,684,540 | 15,092,697 | 16,715,707 |
| Previous deferred-close candidate | 492,300 | 18,219,203 | 19,317,873 | 18,220,523 | 19,403,263 |
| New candidate | 468,520 | 17,777,607 | 19,302,487 | 17,779,923 | 19,383,863 |

Read/verify is essentially unchanged: 0.08% fewer Fast cycles and 0.10% fewer
Chip cycles than the previous candidate. Its Fast gap to original is 15.69%.
Mount is 23,780 cycles cheaper than the previous binary, without a source change
to mount processing; generated code/layout can affect this separate interval.

| Warm mutation I/O | Original | Previous | New |
|---|---:|---:|---:|
| Commits | 2 | 1 | 1 |
| Device flushes | 7 | 4 | 4 |
| Read requests | 4 | 5 | 5 |
| Write requests | 17 | 17 | 17 |

The improvement does not come from deferring another commit or removing a
barrier. Close retains its previously qualified deferred behavior; explicit
flush, timer, inhibit, pressure and manual-TRIM boundaries are unchanged.

Matched-memory runs give original SFS 472 DOS buffers (412,264 bytes after
mutation) against 32 candidate buffers (412,328 bytes). They retain three
alternating trials. Fragmented 8/128 MiB diagnostics use one set of runs each;
they are diagnostic samples rather than release acceptance means.

| Fast mutation configuration | Original cycles | Previous cycles | New cycles |
|---|---:|---:|---:|
| 16 MiB, matched mount RAM | 15,072,787 | 18,219,363 | 17,777,640 |
| 8 MiB, fragmented | 15,289,990 | 18,451,650 | 18,008,660 |
| 128 MiB, fragmented | 15,293,640 | 18,950,630 | 18,492,130 |

Corresponding Chip cases pass their data/image checks with 17,779,857,
18,010,640 and 18,493,210 new mutation cycles. The improvement remains with
matched memory and fragmentation, but original-handler parity is not reached.

The separate cold release gates fail in all four configurations:

| Build / payload placement | Mutation overhead over original | Read/verify overhead |
|---|---:|---:|
| AutoTrim / Fast | 16.811% | 15.790% |
| AutoTrim / Chip | 16.917% | 15.944% |
| No AutoTrim / Fast | 16.217% | 15.598% |
| No AutoTrim / Chip | 15.934% | 15.926% |

## CPU attribution and remaining costs

| Profiled component | Previous cycles | New cycles |
|---|---:|---:|
| `Clone`, inclusive, 25 calls | 873,974 | 422,408 |
| `CopyBlock`, exclusive | 1,452,185 | 897,848 |
| Copies attributed to `Clone` | 761,427 | 271,436 |
| Copies attributed to metadata `Header` | 270,569 | 206,116 |
| `CommitTrst`, inclusive, one call | 719,501 | 718,963 |

Metadata `Header` copy calls fall from 109 to 83. Incremental cloning uses
smaller copy calls instead of copying the entire occupied arena; its aggregate
copy cost falls 64.35%. Inclusive and exclusive rows overlap and must not be
summed. Scalar-profile total mutation is 17,777,450 cycles, consistent with the
unmodified runner's paired result above.

The remaining copies include changed transaction images, scratch-based mutation
helpers and physical-write staging. Exclusive metadata access still costs
290,080 cycles in `ReadView`, 209,960 in hash `Bucket`, 151,987 in `View`, and
135,584 in `VerifyView`. Record-string scanning costs 156,153 cycles and checksum
summing 156,123. The remaining gap is spread across these paths and packet work;
commit frequency is already the same as the previous qualified candidate.

## Total maintenance cost and memory

The create/close/delete diagnostic includes the later timer commit and both
automatic-TRIM service turns, not just foreground packet timing.

| Component, cycles | Previous | New |
|---|---:|---:|
| Create, close and delete foreground | 1,779,840 | 1,764,660 |
| Timer commit | 65,712 | 86,161 |
| Both TRIM service turns | 142,494 | 128,325 |
| Total | **1,988,046** | **1,979,146** |

The total improves **0.45%**. The delete-only subset rises from 254,010 to
264,180 cycles and the later commit also costs more; the full accounting retains
those regressions. Reclamation coverage is unchanged: two hints, two UNMAPs
and 32,768 bytes reclaimed. The idle wait is excluded from CPU work, while
command dispatch and commit work are included. Physical-device latency is not
represented by these emulator measurements.

Mount RAM stays **412,328 bytes** at mount, after mutation and after verification.
The 16,508-byte owned allocation plus 12 state bytes still replaces the existing
16,520-byte undo allocation. Transaction arenas stay 196,608 bytes and the clean
cache holds 29 slots. The predecessor flag uses existing header space.

The AutoTrim HUNK file shrinks from 225,556 to **225,424 bytes**. Loaded code
grows from 219,416 to **219,704 bytes** (+288); this code growth is separate from
the unchanged per-mount allocation. CILica scalar replacement remains enabled
and inlining disabled.

| Rebuilt profile | HUNK file bytes | Loaded code bytes |
|---|---:|---:|
| Owned SFS2 TRST, AutoTrim | 225,424 | 219,704 |
| Owned SFS2 TRST, no AutoTrim | 213,840 | 208,484 |
| Checked SFS2 TRST, AutoTrim | 220,168 | 214,020 |
| SFS2 CSUN policy, AutoTrim | 264,480 | 258,196 |

## Qualification coverage and limitations

- **Host:** Release solution build passes with one existing CopperSharp nullable
  warning. Filesystem suites pass **1,552 tests**, with three external-image
  skips: SFS 1,206, FFS 296, Amiga adapters 18 and storage benchmark 32.
  The **full solution test command fails** in three CPX packet-contract cases:
  `PendingAndFormatBlockedPacketsRefuseWithoutMutatingOrLeaking` for pending
  `DiskInfo`, `ExamineNext` and `Info` (expected error 209, actual 115). CPX has
  176 passes and three failures; other prototype/instruction suites add 82
  passes and one skip. Those separate CPX/prototype sources were not changed
  for this SFS2 task. The solution is not reported as fully green.
- **Native:** all four standalone builds above pass. CopperStart's filesystem
  buffer/geometry/size replay passes **193 cases** with SFS2 enabled. The
  68000/68020 owned allocation, close-policy and recovery-barrier checks pass
  **14 cases**. Allocation-site checks remain clean.
- **Full-ROM correctness:** **75 selected cases pass**: 55 for the AutoTrim
  candidate, 18 for no AutoTrim and two for the checked reference. Coverage
  includes original-SFS replay across every traced mutation/commit cut,
  cross-close visibility, grouped growth/overwrite/reallocation, rejected
  packets, capacity pressure, all four explicit/timer flush failures,
  inhibit/remount, manual TRIM, deferred discard and discard failure, SFS
  handoffs, foreign startup ordering and CSUN/TRST journal recovery.
- **Full-ROM performance:** paired default/matched/fragmented diagnostics and
  image checks pass. All four original-handler parity assertions fail, as
  reported above; these failures are not counted as correctness passes.
- **Physical devices:** not tested. Emulated durability faults and UNMAP tests
  do not qualify real drive flush, power-loss or completion-latency behavior.

Initial invocations are retained as diagnostic evidence, not passes. A broad
CopperStart run was stopped after unrelated desktop `Install` compilation
errors. The first selected replay omitted `CopperStartNativeSfs2=true`, giving
189 passes and four unsupported-SFS2 dispatch failures; the correctly configured
run passes all 193. One foreign-startup case initially ran before the new CSUN
binary existed; rerunning it after that build passes. The final 75-case count
includes its successful rerun once, not the missing-artifact attempt.

## Evidence

Local ignored evidence is under `artifacts/clone-tuning/`. The comparison
baseline is `artifacts/deferred-close/candidate/CopperSFS`, SHA-256
`7E621B0E503CEB621C35B0F05C33EF5A2316ED70DC863D22250A2DFE44744E29`.
Original SFS 1.279 is pinned to
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
The new AutoTrim candidate is
`7CAA7C30664C53D12CB04F7263E6289D8E1882CFA98483C6F118A9F6EE385F38`;
the no-AutoTrim candidate is
`8C96F12F5DD695CC3CBEF19FC8A01A7D8DE37F161D08EA789A611F18B6B9DEE5`.
Checked TRST is
`3CBBB93F3E9C1B458B917A6D8A8EF67DEF11A5336297D2D25C8F25304E8708AA`;
the CSUN-policy build is
`2B3EF35A73C80B29F81FF7EEAF0AB67E815362DA744545AD45E08DBC26699102`.

`qualification/owned-performance` contains phase cycles, I/O, image hashes and
RAM snapshots. `qualification/owned-cpu-profile` contains instruction attribution.
`maintenance-before` and `maintenance-after` contain full foreground/deferred
accounting. `Set-Qualification.ps1` records the local paths and test switches.
`closed-recovery.log`, `rom-main.log`, `rom-foreign.log`, `rom-journals.log`,
`no-auto-qualification.log` and `checked-qualification.log` record the ROM
correctness coverage. `release-gates.log` and `no-auto-qualification.log` retain
the four parity failures. `release-test.log` retains the separate CPX failures.

```powershell
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -AutoTrim -Sfs2ExtraKiB 0 -SkipCompilerBuild -ArtifactRoot artifacts/clone-tuning/candidate
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -Sfs2ExtraKiB 0 -SkipCompilerBuild -ArtifactRoot artifacts/clone-tuning/no-auto
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/clone-tuning/release
dotnet test CopperFS.slnx -c Release --no-build --artifacts-path artifacts/clone-tuning/release
```

The native replay invocation must include `-p:CopperStartNativeSfs2=true` for
the SFS2 packet cases. Licensed ROMs, media and generated binaries remain local;
none are publication inputs.
