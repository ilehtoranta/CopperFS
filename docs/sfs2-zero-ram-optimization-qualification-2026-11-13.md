# SFS2 zero-extra-RAM optimization qualification

The opt-in owned SFS2 TRST handler now uses **8.5% fewer warm mutation cycles**
than the previously qualified owned candidate. The measured mount allocation
remains **412,328 bytes**, including the existing transaction arenas. No extra
RAM, larger cache, new commit boundary or removed durability barrier is involved.

Original SFS remains faster. The original-handler performance gates fail in
both Fast and Chip payload RAM, with and without automatic TRIM. The owned
profile and automatic TRIM therefore remain opt-in.

## Changes

- Hash buckets hold relative byte offsets into owned storage, eliminating
  repeated slot-address multiplication during lookup. They remain valid when
  an accepted arena is cloned. Arena scans advance a pointer by the fixed
  stride. Header, index and slot allocation sizes are unchanged.
- Read acquisition returns the image's owner with its address. Pinning and
  editing no longer repeat the lookup merely to rediscover that owner.
- Owned allocation/free operations update bitmap words in ranges, acquiring
  each intersecting page once. Freeing an extent no longer acquires an edit
  handle for each individual block. The checked legacy path retains its
  existing per-block journal ordering. Range hints still describe actual
  unallocation and do not authorize discard by themselves.
- Allocation checks borrow bitmap/root images while consuming their scalar
  fields. Owned truncate uses file-information scalars and a private edit
  handle instead of two full object-block copies.
- Commit uses retained clean metadata as its before-image. The platform
  contract already invalidates those keys before a physical write enters the
  queue; a clean hit cannot hide a newer pending write. Journal planning
  consumes the borrowed image before emission can invalidate it.
- A newly allocated single block can avoid redo-data staging only after the
  existing committed-and-accepted allocation checks both prove it free.
  These short writes use the physical queue, joining the first journal
  barrier. Bulk unpublished transfers retain their direct path. Overwrites
  of accepted data and reuse of committed blocks retain private redo staging.

Both arenas, packet rollback, strict pressure retry, close/flush/timer/inhibit
boundaries and all four TRST barriers remain. The failed-final-flush recovery
barrier fix remains active. Disk structures, journal encoding, packet ABI,
SFS support and CSUN recovery are preserved.

## Paired performance results

The comparison uses original SFS 1.279, the frozen previously qualified
zero-extra-RAM owned handler, and this candidate. Three alternating trials
run identical DOS workloads and media on the emulated M68020. The 16 MiB mixed
workload includes metadata changes, growth, overwrites, deletion and readback.
Mutation timing includes its final commit. Mount is timed separately.

Payload buffers are explicitly Fast or Chip RAM. Handler and mount storage
remain in Fast RAM; this is not a Chip-only machine comparison.

| Handler | Fast mutation cycles | Fast read/verify | Chip mutation | Chip read/verify |
|---|---:|---:|---:|---:|
| Original SFS | 15,075,100 | 16,679,677 | 15,095,077 | 16,719,627 |
| Previous owned candidate | 21,296,180 | 19,332,580 | 21,295,863 | 19,419,513 |
| New owned candidate | 19,486,390 | 19,303,373 | 19,487,820 | 19,386,347 |

Warm Fast mutation improves 8.5%, but is still **29.3% slower than original
SFS**. Mutation plus verification improves from 40.63M to 38.79M cycles;
original SFS uses 31.75M. Read/verify improvement is small.

The unchanged release gate includes cold first access in mutation timing:

| Profile / placement | Candidate mutation | Original mutation | Candidate verify | Original verify |
|---|---:|---:|---:|---:|
| AutoTrim, Fast | 19,945,543 | 15,589,757 | 19,303,600 | 16,677,773 |
| AutoTrim, Chip | 19,945,473 | 15,632,073 | 19,386,093 | 16,716,603 |
| No AutoTrim, Fast | 19,844,007 | 15,597,000 | 19,278,877 | 16,678,733 |
| No AutoTrim, Chip | 19,844,323 | 15,591,727 | 19,361,923 | 16,724,653 |

All four parity assertions fail. AutoTrim Fast mutation is 27.9% slower than
original SFS and verification 15.7% slower. Disabling AutoTrim does not close
the gap.

## Memory, size and initialization

| Measurement | Previous owned | New owned |
|---|---:|---:|
| Total mount RAM after mutation | 412,328 bytes | 412,328 bytes |
| Owned metadata allocation | 16,508 bytes | 16,508 bytes |
| Additional permanent RAM above the established baseline | 0 | 0 |
| Clean metadata cache capacity | 29 images | 29 images |
| Transaction arena allocation | 196,608 bytes | 196,608 bytes |
| AutoTrim handler size | 222,668 bytes | 223,724 bytes |
| No-AutoTrim handler size | 211,528 bytes | 212,492 bytes |
| Fast cold mount cycles | 449,800 | 468,400 |

Mount RAM stays constant from completed mount through mutation and readback;
legacy undo/dirty buffer pointers remain zero. The native allocation/recovery
tests also pass. The AutoTrim binary grows 1,056 bytes (0.47%), and cold mount
cost grows 18,600 cycles (4.1%). These costs are reported separately from the
warm improvement. CILica scalar replacement is enabled; inlining is disabled.

Matched-memory runs give original SFS 472 buffers: 412,264 measured bytes,
versus 412,328 for both owned handlers. Fast warm mutation remains 15.07M for
original, 21.30M for previous and 19.49M for new. Both Fast/Chip matched-memory
comparisons pass their correctness checks; matching memory does not establish
performance parity.

## Copies, index access and physical traffic

Diagnostic warm Fast instruction profiles:

| Work | Previous | New |
|---|---:|---:|
| Block-copy calls / exclusive cycles | 590 / 1,465,539 | 481 / 1,188,957 |
| Slot-address calculations / exclusive cycles | 3,130 / 689,300 | 329 / 73,810 |
| Hash-bucket searches / exclusive cycles | 1,459 / 436,955 | 1,163 / 300,925 |
| Private metadata edit acquisitions | 113 | 43 |
| Individual allocation-bit updates | 82 | 9 |
| Batched allocation/free range operations | 0 | 7 |
| Core metadata checksum scans | 135 | 131 |
| Mutation device read commands | 45 | 34 |
| Mutation device write commands | 52 | 51 |
| Mutation device flushes | 28 | 28 |

Ordinary clean-mount packets still perform no root reselection, filesystem
recovery or legacy transaction-buffer lookup. Repeated unchanged-image
verification has not been reintroduced. Inclusive profiler costs overlap;
the table uses exclusive cycles where costs could otherwise be double counted.

Whole-workload physical byte counters, including mount and verification:

| Handler | Bytes read | Bytes written |
|---|---:|---:|
| Original SFS | 175,104 | 145,920 |
| Previous owned | 1,404,416 | 162,304 |
| New owned | 683,520 | 162,816 |

Retaining before-images removes eleven read commands and much of the
associated read-ahead traffic: bytes read fall 51.3%. There is a write
tradeoff: **one fewer command, but 512 more bytes written**. A small block
written while unreachable may be overwritten by a later accepted packet.
This is not a claim that total write volume fell. An initial direct-write
experiment increased commands to 57; that variant was rejected in favor of
the queued short-write path.

The remaining copy costs include accepted/trial cloning (about 291K cycles),
scratch-based metadata transformations (285K), private image copies (163K),
physical queue copies and journal encoding. The original handler still makes
only 17 write commands and seven flushes in this workload. The retained
transaction protocol accounts for much of the remaining traffic; this change
does not relax its barriers to make the numbers smaller.

## Deferred work and fragmentation

For the 32 KiB automatic-TRIM workload:

| CPU work | Previous | New |
|---|---:|---:|
| Foreground delete | 928,620 cycles | 296,170 cycles |
| Tracking within that foreground interval | 1,476 cycles | 1,482 cycles |
| Deferred service | 138,166 cycles | 139,810 cycles |
| Foreground delete + deferred service | 1,066,786 cycles | 435,980 cycles |

Tracking is already included in foreground delete; it is not added twice.
The combined measured work improves 59.1%, while deferred service alone
regresses 1.2%. Both versions reclaim all 32,768 bytes with two notifications,
two service turns and two UNMAPs. Three discovery commands occur once per
attachment. The timer wait is not counted as CPU work. The separate large,
fragmented and warm automatic-reclamation cases also pass.

Single-trial 8/128 MiB fragmented-volume diagnostics read and verify a 1 MiB
file in 16 extents. New candidate totals are 136,390,820 / 136,391,230 cycles;
previous totals are 136,395,660 / 136,376,340, and original totals are
136,871,790 / 136,890,760. All arms issue 64 reads. These streaming results
are essentially unchanged and include the common probe's byte verification.

## Qualification

- Release solution build passes. Its one nullable warning is in the sibling
  CopperSharp compiler. Release tests pass **1,540 filesystem tests**, with
  three unavailable external-image tests skipped. The 53 unrelated prototype
  tests are excluded from that qualification count.
- The focused owned/group/trim suite passes 79 tests. New coverage checks
  word/page boundaries across 4,033 allocated blocks, neighbouring-file
  preservation, freed-range coverage, single-block unpublished growth,
  rejected overwrite rollback, and subsequent accepted overwrite.
- Every I/O/barrier cut in the new grouped growth test is exercised. Small
  pending-state sets are exhaustive; larger unpublished payload sets use ten
  deterministic crash-image samples per cut. Existing exhaustive small
  transaction-cut tests remain unchanged.
- Standalone owned AutoTrim, owned no-AutoTrim, checked TRST and CSUN builds
  pass. No portable-core dependency on CopperStart was added.
- CopperStart native replays: **193 passed**. Compiled 68000/68020 allocation
  fallback and failed-durability-barrier recovery tests: **12 passed**.
- Full ROM: **18** owned recovery/mutation/TRIM cases, **18** startup/manual
  TRIM/SFS handoff cases, **2** owned cross-policy journal-recovery cases,
  **3** no-AutoTrim pressure/timer/inhibit cases, and **4** rebuilt checked
  TRST/CSUN clean-handoff/recovery cases pass. Original SFS replays TRST;
  it is never asked to replay CSUN.
- Three-trial default/matched-memory Fast/Chip comparisons, CPU profiling,
  fragmented-volume diagnostics and deferred-maintenance profiling complete.
  The four original-handler performance gates fail as reported above.

Physical Amiga hardware, controller/SSD latency and real power-loss behavior
remain unqualified. Full-ROM and native instruction tests are not physical
device passes. No release default or publication is changed.

## Reproduction and evidence

Ignored local evidence is under `artifacts/owned-tuning`: final handler
`refined/CopperSFS`, `qualification/manifest.json`, paired `owned-performance`
results, `original-performance[-chip]`, `owned-cpu-profile`, `maintenance`,
`compatibility`, and separate host/native/full-ROM logs. The `pilot` binary is
the rejected direct-short-write experiment, not the qualified candidate.

```powershell
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -AutoTrim -Sfs2ExtraKiB 0 -ArtifactRoot artifacts/owned-tuning/refined
dotnet build CopperFS.slnx -c Release
dotnet test CopperFS.slnx -c Release

# Requires the licensed ROM in COPPERSHARP_KICKSTART31_ROM and retained manifest inputs.
$env:COPPERFS_CORRECTNESS_MANIFEST = (Resolve-Path artifacts/owned-tuning/qualification/manifest.json).Path
$env:COPPERFS_SFS2_OWNED = '1'
$env:COPPERFS_GROUPED_REFERENCE = (Resolve-Path artifacts/owned-sfs2/durable-0/CopperSFS).Path
$env:COPPERFS_GROUPED_REFERENCE_SHA256 = 'AEB79692DA47B741568052436C3B4CF42796A7CEC7797EC3E0BF3D08D305F00D'
dotnet test tests/CopperFS.Kickstart.Tests/CopperFS.Kickstart.Tests.csproj -c Release --filter 'FullyQualifiedName~OwnedSfs2SeparatesMountAndSteadyState'
```

The test output arm named `grouped` is the frozen previous **owned** candidate
in this comparison, selected and verified by the explicit hash override.
Without that override the fixture retains its original grouped-reference hash.

SHA-256 identities:

- Original SFS: `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
- Previous owned: `AEB79692DA47B741568052436C3B4CF42796A7CEC7797EC3E0BF3D08D305F00D`.
- New AutoTrim: `CB7FE660E5A55B6BBB361517E6D85D550CC1CBB41597E9A2DFC4F34EEB633CC3`.
- New no-AutoTrim: `3473FD9DAFCB4C1FE32BE823B3E16BB287F785D280A33B31EC82A56030913987`.
- Checked TRST: `48EC0663405D683BE9DD28BCCA11E2C928514AE1BA254B0694D1851D7999989D`.
- CSUN: `87CFCAEA7D5197434AB4BA6BCB0B42CC381617D8A4D5F5A7D529411F8FA8AC67`.
