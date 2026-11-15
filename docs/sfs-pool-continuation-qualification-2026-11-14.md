# SFS shared-pool overwrite continuation

The overwrite-continuation path now supports the opt-in shared image pool.
It uses the existing allocation, accepted/trial protocol and four TRST commit
barriers. In the paired 96 KiB overwrite workload it reduces overwrite cycles
by about 41% and total growth/overwrite/verification cycles by about 10%
against the previous pool candidate. Original-SFS parity is still not met.
The pool and automatic TRIM remain opt-in.

## Ownership and recovery

The proof records physical pool indices instead of contiguous arena indices.
Three previously reserved group-header words, at offsets 232–240, retain the
corresponding descriptor revisions. Before using a proof, the implementation
checks the mapped pool layout, image owner, revision, block identity and type
certification. The private proof and deferred-run seals include these records.
No image pointer is retained across packet boundaries without this validation.

Consecutive writes still target blocks free in both durable and accepted
allocation views. A whole successful unpublished data transfer advances the
deferred count. A failed transfer leaves the accepted count and metadata intact.
The retained leaf and bitmap images are materialized before reads, edits,
ordinary mutations or commit. Pool accessors reject invalid deferred state;
they cannot silently fall through to old disk images.

At a bitmap boundary, the trial view obtains private copies of the leaf and
each affected bitmap before payload I/O. Success promotes the whole view;
failure restores the accepted view and reclaims speculative images. Ordinary
mutations invalidate continuation proofs before reusing slots. Revision wrap
does not extend a proof lifetime across those boundaries.

Journal encoding, disk structures, close deferral, explicit filesystem flush,
timer/inhibit/pressure boundaries and recovery ordering are unchanged. The
commit implementation and its four durability barriers are unchanged. Automatic
TRIM still requires durable transaction completion.

## Memory and code

There is **no additional permanent mount-data allocation**. The pool still
uses 9,688 bytes within the existing 9,756-byte allocation (9,760 after Exec
rounding), with 17 image slots. The existing 8,716-byte fallback still holds
15 images. The three revision records use already allocated header space.
No new heap allocation site or task-stack allocation is introduced. Stack
high-water use has not been measured. Executable footprint is separate:

| M68000 handler | Previous pool HUNK | New HUNK | Previous code | New code |
|---|---:|---:|---:|---:|
| AutoTrim | 159,872 | 162,060 | 156,096 | 158,100 |
| Without AutoTrim | 150,356 | 153,260 | 147,452 | 149,790 |

The larger loaded executable does consume additional memory; the zero-growth
claim applies to mount data, not total code-plus-data residency. Both maps report
zero managed allocation sites, eight initialized-data bytes and zero BSS bytes.
CILica scalar replacement is enabled and inlining disabled.

Full-ROM `AvailMem` deltas match the previous pool: 130,104 retained mount-data
bytes after mounting and 139,864 after mutation/verification. The starting
available-memory readings differ by 2,008 bytes because of the larger loaded
AutoTrim executable; that cost is not hidden in the mount-data measurement.

## Paired overwrite comparison

Three alternating trials per arm, M68020 emulation, Kickstart 3.1, identical
16 MiB starting image and 32 DOS buffers. Payload buffers are explicitly
allocated in Fast or Chip RAM. All three handlers run the same probe. The
previous arm is the saved shared-pool candidate, not the older two-arena build.

The workload creates 40 filler files, grows a file in two 48 KiB writes with
an intervening allocated gap, explicitly flushes, overwrites it with twelve
8 KiB packets, closes and explicitly flushes again, then reads and verifies
all 96 KiB. The table includes both explicit commits. Total includes the
between-phase probe work; it excludes the common filler setup and final inhibit.
Whole-run physical write bytes, including setup and inhibit, are reported below.

Mean emulated CPU cycles, rounded to the nearest cycle:

| Buffer / phase | Original SFS 1.279 | Previous pool | Pool + continuation |
|---|---:|---:|---:|
| Fast: growth + flush | 1,049,137 | 1,360,710 | 1,422,620 |
| Fast: overwrite + flush | 325,327 | 2,033,753 | 1,207,337 |
| Fast: read/verify | 4,070,950 | 4,036,350 | 4,037,083 |
| **Fast: total** | **5,770,363** | **7,767,027** | **6,981,150** |
| Chip: growth + flush | 1,043,553 | 1,360,863 | 1,422,830 |
| Chip: overwrite + flush | 325,583 | 2,034,397 | 1,209,503 |
| Chip: read/verify | 4,073,857 | 4,039,990 | 4,040,753 |
| **Chip: total** | **5,767,947** | **7,771,490** | **6,987,297** |

Growth costs about 4.5% more than the previous pool; verification is essentially
unchanged. Despite the overwrite improvement, the new total remains about 21%
above original SFS and overwrite itself costs about 3.7 times as many cycles.

| I/O metric, identical in Fast and Chip trials | Original | Previous pool | New pool |
|---|---:|---:|---:|
| Measured growth/overwrite write commands | 20 | 38 | 38 |
| Measured growth/overwrite device flushes | 5 | 8 | 8 |
| Overwrite write commands | 12 | 17 | 17 |
| Overwrite device flushes | 1 | 4 | 4 |
| Whole-run written bytes | 374,784 | 376,320 | 376,320 |
| Whole-run 512-byte sector writes | 732 | 735 | 735 |

This change removes CPU work; it does not reduce commits or physical writes.
The original overwrite issues twelve data writes and one flush, whereas the
candidate retains relocation metadata and a four-barrier TRST commit. Comparing
only foreground packet replies would conceal that remaining cost. This run
does not attribute all remaining CPU cycles to commit; preparation, lookup and
proof validation still contribute.

## Mixed workload and remaining regressions

The existing mixed workload covers creation, growth, unaligned overwrites,
metadata changes, directory operations and remount verification. It does not
exercise consecutive aligned overwrite continuation. Same three-arm, three-trial
Fast/Chip controls as above; these phases include final flushes and verification.

| Buffer / phase | Original | Previous pool | New pool |
|---|---:|---:|---:|
| Fast: cold mount | 522,090 | 371,390 | 377,000 |
| Fast: mutate | 16,994,523 | 19,400,963 | 19,610,410 |
| Fast: verify | 17,568,643 | 20,156,883 | 20,159,973 |
| Chip: cold mount | 523,890 | 370,280 | 375,800 |
| Chip: mutate | 16,989,183 | 19,402,273 | 19,613,067 |
| Chip: verify | 17,616,933 | 20,241,757 | 20,245,957 |

The mixed mutation phase is about 1.1% slower than the previous pool. The
complete mount/mutate/verify sequence is about 0.55% slower. Thus this is a
targeted overwrite improvement, not a general performance win. The candidate
still writes 187,392 bytes in 67 device write commands and uses 12 flushes,
versus original SFS's 150,016 bytes, 17 commands and seven flushes. The previous
pool has the same candidate I/O counts. Neither benchmark trades away required
commit barriers to improve timing.

The 24- and 64-entry enumeration comparisons also complete without I/O during
enumeration. For 64 entries the original/previous/new counts are
5,168,820 / 5,288,260 / 5,310,960 cycles. Pool accessor materialization checks and
continuation preparation add work outside the successful fast path; this run
has not separately apportioned those costs.

## Qualification

- Final Release solution build: passed, zero warnings/errors.
- Release solution test coverage, reconciled with the full affected-project
  reruns: **2,123 passed, four skipped, no unresolved correctness failures**.
  The final full SFS suite has **1,323 passed, three external-media skips**.
  Other projects: FFS 296, Amiga 18, storage benchmark 32, CPX core 353,
  CPX instructions 23 plus one optional ROM skip, prototype 77 and prototype
  instructions one. Skipped cases are not counted as passes.
- Focused host tests: 95 passed, including both allocation budgets, revision
  wrap, corrupt owner/revision/geometry rejection, unchanged deferred images,
  partial payload failures, bitmap boundaries, crash cuts and reordered writes.
- Standalone handlers: both AutoTrim and no-AutoTrim M68000 builds passed.
- Native pool allocation/capacity instruction tests: 10 passed.
- CopperStart native buffer, transfer, cache, arithmetic and copy replays:
  198 passed, zero skips, against the new AutoTrim executable.
- Full-ROM grouped/recovery correctness: 21 passed per profile; automatic TRIM:
  five passed. The dedicated continuation matrix adds one passed test per
  profile: nine cuts before/after the overwrite commit barriers and during
  payload writing, each recovered by both original SFS and the candidate.
  All recovered files contain the complete old or new payload. Total:
  **49 correctness passes**, including 36 continuation recovery replays.
- Full-ROM diagnostics: six passed (two mixed-memory, two enumeration and two
  continuation-memory comparisons). These are measurements, not parity passes.
- Full-ROM original-SFS performance gates: **four failed**, Fast/Chip for each
  AutoTrim setting. They remain release blockers.
- Physical Amiga/device testing: **not performed**. Emulated UNMAP failure and
  durability tests are not physical-device qualification.

The first full host and native runs each exposed two tests whose closed-form
seal oracle still assumed the old 57-word arena layout. The oracle now covers
both the old layout and the pool's additional five words, with basis vectors,
random values and overflow patterns. Native and focused host reruns passed;
the full SFS host-suite rerun also passed. The CopperStart change is limited to
`NativeTrstArithmeticInstructionTests.cs`; production filesystem code remains
in CopperFS.

During the solution test run, concurrent CPX work briefly left `OwnerWork`
undefined in `CpxBootProbe`. Its implementation subsequently appeared and the
final whole-solution Release build passed with zero warnings/errors. No CPX
source was changed for this task; its separate instruction-project rerun passed
23 tests with one optional ROM skip. The initial `dotnet test CopperFS.slnx
-c Release` invocation therefore exited unsuccessfully; the totals above
combine its unaffected project results with the final complete SFS and CPX
instruction reruns, rather than claiming a single green solution invocation.

## Reproduction and provenance

Artifacts are local and ignored under `artifacts/sfs-continuation/`; no ROM,
media image or build artifact is added to source control.

| Input | SHA-256 |
|---|---|
| Previous pool, AutoTrim | `684FBD47E2E1A38F271C7304932408324305645A1E806E0687C2A3BF2F69D034` |
| New pool, AutoTrim | `E09766B505B261AE5489557A0E46E37906F7C1B1185F9DD581BDFC47B18E395E` |
| New pool, no AutoTrim | `A0B44CEFCFB8E6624A955F84A15E52F0B65186820BAC0FA300FAD34E8F725821` |
| Original SFS 1.279 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| Starting SFS image | `18170E0D35A5307AA29F17751B50BC848EB7B47211EB76FC1A88E95091409137` |
| Licensed Kickstart 3.1 | `8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE` |

Build with `build/Build-Handler.ps1 -SharedSfsPool -Cpu 68000
-SkipCompilerBuild`, adding `-AutoTrim` for that profile and separate
`-ArtifactRoot` directories. The qualification manifests select the handlers
and the unchanged `artifacts/sfs-capacity/classic-base.hdf` image.

Set `COPPERSHARP_KICKSTART31_ROM`, `COPPERFS_SFS_GROUPED=1`,
`COPPERFS_CORRECTNESS_MANIFEST` and `COPPERFS_SFS_REFERENCE` (the previous pool
handler). Run `SfsContinuationComparisonIncludesExplicitCommits` in
`CopperFS.Kickstart.Tests`. Paired raw samples and image hashes are in
`qualified/continuation-{fast,chip}/cycles.json` and each run's `result.json`.

The preceding [pool qualification](sfs-shared-pool-qualification-2026-11-14.md)
describes the baseline. Its statement that continuation was disabled applies
to that saved version; this report supersedes that limitation.
