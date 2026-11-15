# SFS2 owned mount state: implementation and qualification

The redesign is implemented as an **opt-in SFS2 TRST profile**. It removes
repeated root selection, recovery and legacy transaction-buffer lookup from
ordinary mounted operations. It improves the measured warm mutation workload
by **28.8%** versus the frozen grouped candidate. It **does not meet original
SFS performance parity**, so neither this profile nor automatic TRIM becomes
a release default.

The evidence currently supports **zero additional RAM** as the experimental
budget. This is a recommendation from the measurements, not a new release RAM
ceiling. Larger caches do not materially improve these workloads and increase
mount initialization cost.

## Execution and ownership

`Sfs2MountContext` is a caller-owned layout: a 64-byte execution header, one
retained selected-root image and a bounded metadata cache. Establishment runs
the existing checked recovery and mount paths. Geometry, selected roots and
options then remain valid until suspension/remount. Mount epochs, accepted
transaction generations, trial-attempt generations and image revisions have
separate lifetimes. Failed trials cannot reuse an earlier handle's identity.
Counter exhaustion refuses further use instead of reviving stale handles.

Mutable root images remain private in the trial arena. Acceptance validates
and publishes them with the packet; rejection restores the previous accepted
overlay. Current mutations retain fixed root addresses, including node-tree
growth beneath those addresses. A failed commit suspends the mount. A separate
pending/publishing/durable state prevents a low-level device flush, or merely
detaching an overlay, from certifying transaction completion.

Recovery also retries the durability barrier after a failed commit. A failed
final flush can leave the clean marker visible in volatile device storage;
seeing that marker on recovery is insufficient to restore service. A failed
retry keeps the mount suspended. Host crash images and compiled 68000/68020
tests exercise both the failed retry and subsequent successful recovery.

`Sfs2MetadataStore` supplies indexed transaction and cache images, explicit
acquire/release, exclusive private edits, revision-bound verification and
packet-boundary sealing. The transaction index contains relative slot
ordinals, so arena cloning requires no pointer rebasing. Clean images can be
evicted only when unpinned. Caller scratch is the bounded fallback when every
cache image is pinned. Raw overlay readers refuse unpublished/unsealed images.

All contextual metadata reads enter the same store. Transaction images win;
physical writes invalidate the clean-store key before entering the write
queue, so a pending physical image cannot coexist with an older clean hit.
Cache misses consult that queue before physical cache/device reads. Providers
which attach a context must honor this invalidation contract. Native owned
SFS2 reads bypass the old undo/dirty-buffer searches, and those buffers are not
allocated for this mount. SFS and recovery/legacy policies retain their paths.

Lookup/node/extent traversal and native information/enumeration borrow images
or capture packet-local scalars. The common write/resize path uses read and
private-edit handles; bitmap and free-count updates edit transaction images.
Complex metadata transformations still use bounded private scratch and stage
their resulting images through the same store. Copies for private edits,
accepted/trial cloning, physical write batching and journal encoding remain;
this is not a claim that all metadata copies have disappeared.

The two accepted/trial arenas, dual-view proof before direct unpublished data
writes, four TRST barriers, strict pressure retry and close/flush/timer/inhibit
boundaries remain. Commit consumes the prepared mount state. Automatic TRIM
requires durable completion; manual execution commits pending work first.
Portable callers without an attached context retain the checked implementation.
There are no disk-format, journal-encoding or packet-ABI changes.

## RAM and handler size

512-byte-block observations from the running handler, rather than requested
capacities alone:

| Additional KiB | Owned allocation bytes | Cache slots | Total mount RAM after mutation | Handler bytes | Cold mount cycles, Fast |
|---:|---:|---:|---:|---:|---:|
| 0 | 16,508 | 29 | 412,328 | 222,668 | 449,800 |
| 16 | 32,892 | 60 | 428,712 | 222,672 | 483,250 |
| 64 | 82,044 | 152 | 477,864 | 222,672 | 602,117 |
| 256 | 278,652 | 518 | 674,472 | 222,672 | 1,016,460 |

The old undo allocation was 16,520 bytes. The replacement includes 12 added
native-state bytes, so `16,508 + 12 = 16,520`: the zero-budget profile really
adds zero permanent per-mount RAM. Context/root storage uses 576 bytes; cache
header/index uses 288/544/1,056/4,128 bytes respectively. Both transaction arenas
still share the existing 196,608-byte allocation. Recorded legacy undo and
dirty-buffer pointers are zero. Optional allocation falls back through
256 → 64 → 16 → 0 KiB, then to the checked grouped path before mutation.

The total mount RAM column is the difference in Exec free memory before
DeviceProc and after the complete workload. It includes native mount resources,
but excludes the already-loaded handler and workload buffers. Candidate free
memory remains unchanged from completed mount through mutation and reads.
The grouped reference uses the same 412,328 bytes after its lazy arena
allocation. Original SFS with 32 buffers uses 172,904 bytes in this workload.

The frozen grouped AutoTrim handler is 210,916 bytes; original SFS 1.279 is
97,984 bytes. Keeping the reference paths plus the new ownership machinery
increases candidate code size by 11,752 bytes (5.6%). Without AutoTrim the owned
handler is 211,528 bytes, versus the earlier grouped 198,772 bytes. This is a
CPU-work reduction, not yet a smaller handler.

## Performance

All figures are emulated M68020 cycles, with identical synthetic media and DOS
packets per comparison. Three alternating trials use a 16 MiB volume. Payload
buffers are explicitly Fast or Chip; handler and mount allocations remain in
Fast RAM. A final filesystem flush is included. These are emulator results,
not physical 14 MHz board or controller measurements.

Warm Fast-RAM workload, after separately timed mount:

| Handler / extra RAM | Mutation cycles | Read/verify cycles |
|---|---:|---:|
| Original SFS, 32 buffers | 15,078,087 | 16,672,817 |
| Frozen grouped candidate | 29,919,997 | 19,864,387 |
| Owned, 0 KiB | 21,296,220 | 19,332,480 |
| Owned, 16 KiB | 21,299,603 | 19,331,070 |
| Owned, 64 KiB | 21,294,653 | 19,320,630 |
| Owned, 256 KiB | 21,301,963 | 19,327,493 |

This mixed workload includes directory operations, small and large writes,
growth, overwrites, rename, metadata updates, deletion and readback. At zero
extra RAM, warm mutation is still 41.2% slower than original SFS. Combining
mutation and verification gives 40.63 million cycles, versus 49.78 million for
the grouped reference and 31.75 million for original SFS. The reference rows
are the paired zero-budget run; each optional-budget comparison repeats all
three arms, with the same conclusion.

The unchanged release gate includes cold first access in mutation timing:

| Extra KiB | Fast mutation | Fast verify | Chip mutation | Chip verify |
|---:|---:|---:|---:|---:|
| 0 | 21,736,987 | 19,332,470 | 21,735,470 | 19,419,970 |
| 16 | 21,774,747 | 19,332,547 | 21,774,387 | 19,419,810 |
| 64 | 21,887,960 | 19,320,787 | 21,888,737 | 19,408,503 |
| 256 | 22,306,570 | 19,326,453 | 22,307,003 | 19,414,360 |

Original SFS is approximately 15.59 million mutation and 16.68/16.72 million
Fast/Chip verification cycles. **All eight parity assertions fail.** Zero-extra
RAM is 39.43% slower in Fast mutation and 15.92% slower in verification.
Without AutoTrim, the corresponding candidate figures are 21,619,840 and
19,277,120 cycles; both parity assertions also fail. TRIM is not the main gap.

### Matched RAM

Original SFS's additional buffer costs were calibrated from actual Exec
allocations: 544 bytes per requested buffer. Three-trial comparisons were then
repeated with the following settings, in both payload memory classes:

| Candidate extra KiB | Original buffers | Candidate mount bytes | Original mount bytes | Difference |
|---:|---:|---:|---:|---:|
| 0 | 472 | 412,328 | 412,264 | 64 |
| 16 | 502 | 428,712 | 428,584 | 128 |
| 64 | 593 | 477,864 | 478,088 | -224 |
| 256 | 954 | 674,472 | 674,472 | 0 |

Original warm mutation remains 15.06–15.07 million cycles and verification
16.65 million in Fast RAM. Candidate warm figures remain essentially
unchanged. The grouped binary remains immutable at its measured baseline RAM;
it is exactly matched to the zero-extra candidate, not relabeled as matching
the larger optional configurations.

### Fragmented small and large volumes

The 8 and 128 MiB fixtures contain a 1 MiB file split across 16 extents. The
diagnostic reads and verifies every payload byte, after the mixed workload.
These are single-trial Fast-RAM diagnostics; CPU totals include the identical
probe's byte verification and are not an isolated handler instruction count.

| Extra KiB | 8 MiB fragmented read cycles | 128 MiB fragmented read cycles |
|---:|---:|---:|
| 0 | 136,395,900 | 136,376,410 |
| 16 | 136,394,650 | 136,381,660 |
| 64 | 136,394,510 | 136,392,760 |
| 256 | 136,378,880 | 136,375,880 |

All arms make 64 device reads. The paired original SFS measurements span
136.87–136.89 million cycles and the grouped reference 136.36–136.39 million.
No material cache-size benefit appears in this streaming workload. The small
volume improves neither the mutation-parity conclusion nor the RAM choice.

### Where cycles went

Identical warm Fast workloads, diagnostic instruction profiles:

| Work | Grouped reference | Owned, 0 KiB |
|---|---:|---:|
| Root selections | 192 calls | 0 |
| Filesystem recovery | 40 calls | 0 |
| Legacy undo-buffer lookups | 874 calls | 0 |
| Prepare, inclusive cycles | 4,302,722 | 61,257 |
| Block-copy, exclusive cycles | 4,152,454 | 1,465,539 |
| Metadata block sum, exclusive cycles | 2,521,002 | 241,461 |

The new store has 615 metadata read requests and 32 physical-provider fills:
583 are served by owned transaction/cache images. This is a combined retained
image hit count, not a claim that all 32 fills reach the device.
The seven `NativeSfs2Bridge.Recover`/`RecoverTrstGroup` wrapper calls in the
profile are commit-boundary dispatch; they do not invoke filesystem recovery
on this clean mount.

Remaining work is visible rather than hidden in deferred timing. The workload
still makes **52 physical write commands and 28 flushes**, versus original
SFS's **17 writes and 7 flushes**. That follows the retained transaction
protocol and boundaries. Indexed slot/address/hash access is now prominent;
the safe two-arena clone accounts for approximately 306,000 inclusive copying
cycles. More RAM changes neither commit traffic nor this indexing cost.

The next useful optimization targets are the generated indexed-access code
and redundant physical metadata writes within the existing barriers. Changing
barrier policy is outside this redesign. Profile evidence does not support
spending another 256 KiB to address the remaining gap.

### Deferred maintenance is included separately

The automatic-TRIM diagnostic deletes and reclaims 32,768 bytes, with two
range notifications, two bounded service turns and two UNMAPs:

| Profile | Tracking cycles | Service cycles | Tracking + service |
|---|---:|---:|---:|
| Grouped reference | 1,460 | 152,025 | 153,485 |
| Owned, 0 KiB | 1,476 | 138,166 | 139,642 |
| Owned, 16 KiB | 1,476 | 142,635 | 144,111 |
| Owned, 64 KiB | 1,494 | 142,740 | 144,234 |
| Owned, 256 KiB | 1,494 | 157,526 | 159,020 |

Each fresh mount performs three discovery commands; subsequent service reuses
capabilities. These service totals include that first discovery. The separate
large-delete test reclaims all 2,359,296 bytes in three UNMAPs; the fragmented
Chip test reclaims all 65,536 bytes in 16 single-descriptor commands. The warm
two-delete case reclaims 65,536 bytes in four commands with only one capability
probe. Waiting for the five-second timer is not counted as handler CPU work.
The mixed release gate does not include this separate reclamation workload;
the table above exposes its additional cost and coverage.

## Correctness and qualification

- Release solution build passes. The full run passes 1,537 filesystem tests
  with three external-image tests skipped. Concurrent unrelated prototype
  tests are not counted as CopperFS qualification.
- The final focused owned/group/trim run passes 76 tests, also included in the
  full solution run. Four block sizes and all four RAM budgets match the
  strict reference for 13 mutation scenarios. Twelve mutation scenarios test
  every recorded commit cut and recover a complete old or accepted view.
- Ownership tests cover stale handles, pinned eviction/scratch fallback,
  replacement, hash collisions, arena swaps, revision/generation exhaustion,
  malformed/overlapping storage, rejected packets and device-flush refusal of
  unfinished transactions. Growing 80 nodes, relocating long records and
  deleting them agrees with strict TRST at 0 and 256 KiB.
- All four AutoTrim budget handlers, the owned no-AutoTrim handler, SFS and
  SFS2 CSUN build standalone. CILica scalar replacement is enabled and
  inlining disabled. No CopperStart application dependency was introduced.
- CopperStart native replay: 193 tests pass. A further 12 compiled cases pass
  on 68000/68020: ten allocation-failure cases exercise every budget fallback;
  two verify suspension and recovery after a failed durability barrier.
- Full ROM: the 18-case owned recovery/mutation/TRIM matrix passes, including
  original-SFS recovery, every tested journal barrier, grouped growth and
  overwrite, strict pressure retry, timer/inhibit and discard failure/cuts.
  The 20-case foreign-startup/manual-TRIM/SFS handoff matrix passes. Three
  no-AutoTrim pressure and timer/inhibit cases pass.
- Rebuilt SFS and CSUN pass their clean mutation/handoff tests. These two
  profiles were built before the final owned/grouped recovery-barrier fix,
  which is outside their enabled paths. Both rebuilt
  Copper policies and original SFS recover published TRST; both Copper
  policies recover CSUN. The journal test now compares redo recovery with
  the actual first clean checkpoint, since a grouped transaction can contain
  several accepted packets. Original SFS is never asked to replay CSUN.
- Default and matched-memory comparisons use three alternating trials in
  Fast/Chip payload RAM. Additional 8/128 MiB fragmented-volume comparisons
  are single-trial diagnostics, not substitutes for the parity gate.

The manual-TRIM comparison originally required an identical read trace. The
owned profile deliberately removes redundant reads, so that one comparison
now requires identical results, image bytes and exact write/flush/discard
ordering; other startup/recovery ordering assertions remain intact.

Physical Amiga, physical-controller/SSD latency, removable-media replacement
and real power-loss behavior have **not** been qualified here. Emulator cuts,
injected failures and native instruction execution do not replace those tests.
The three unavailable external-media host tests remain skips.

## Reproduction and retained evidence

Build an experimental candidate with:

```powershell
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -Sfs2ExtraKiB 0
# Add -AutoTrim only for the automatic-discard qualification profile.
# Repeat with 16, 64 and 256; no larger default is selected.
dotnet build CopperFS.slnx -c Release
dotnet test CopperFS.slnx -c Release
./build/Summarize-OwnedSfs2.ps1 -ArtifactRoot artifacts/owned-sfs2
```

Full-ROM measurements require the licensed ROM in
`COPPERSHARP_KICKSTART31_ROM` and the retained manifest containing the original
handler, candidate and SFS fixture paths/hashes. For example, rerun the paired
zero-budget comparisons with:

```powershell
$env:COPPERFS_CORRECTNESS_MANIFEST = (Resolve-Path artifacts/owned-sfs2/durable-qualification-0/manifest.json).Path
$env:COPPERFS_GROUPED_REFERENCE = (Resolve-Path artifacts/trst-group/optimized-native/CopperSFS).Path
$env:COPPERFS_SFS2_OWNED = '1'
dotnet test tests/CopperFS.Kickstart.Tests/CopperFS.Kickstart.Tests.csproj -c Release --filter 'FullyQualifiedName~OwnedSfs2SeparatesMountAndSteadyState'
dotnet test tests/CopperFS.Kickstart.Tests/CopperFS.Kickstart.Tests.csproj -c Release --filter '(FullyQualifiedName~ReleaseCandidateStaysOnParWithOriginalHandler|FullyQualifiedName~ReleaseCandidateChipMemoryStaysOnParWithOriginalHandler)&DisplayName~sfs2-trst'
# The second command is expected to fail its performance assertions.
```

Repeat against each budget's manifest. For matched RAM set
`COPPERFS_MATCH_ORIGINAL_BUFFERS` to the value in the table. For fragmented
diagnostics set `COPPERFS_OWNED_FRAGMENTED=1`, `COPPERFS_OWNED_VOLUME_MIB` to
8 or 128 and `COPPERFS_OWNED_TRIALS=1`. Clear these overrides before repeating
the default three-trial comparison. Use `COPPERFS_TRIM_PROFILE=1` and a
separate `COPPERFS_AUTOTRIM_MANIFEST` output directory to profile
`AutomaticTrimAfterDeletePreservesReallocatedFiles` without replacing the
correctness matrix's evidence.

Local ignored evidence is under `artifacts/owned-sfs2`: final `durable-*` binaries and
CILica reports, `durable-qualification-{budget}/manifest.json`, per-arm media
and hashes, `owned-performance`, `original-performance[-chip]`, CPU profiles,
`durable-maintenance-{budget}` scopes, and separated build/test logs. `durable-summary.json`
rejects stale candidate hashes and reports measured RAM, cycles and I/O.
ROMs, external handler binaries, media and generated artifacts are not source
changes or publication outputs.

Reference hashes:

- Grouped AutoTrim candidate: `0733EAA7EFE6B90EFC93800B3209677CAF4830456FC2CB59D07B4BB937ABDE34`.
- Original SFS 1.279: `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
- Owned 0 KiB: `AEB79692DA47B741568052436C3B4CF42796A7CEC7797EC3E0BF3D08D305F00D`.
- Owned 16 KiB: `F66587572E8AB5AD964863CC79F87557495DAA0FBC83FCFADBEA162C8745452F`.
- Owned 64 KiB: `6CBCE5580903BD68C4C7617205A38278A5AB89D92487B78FF86685F02E3FF195`.
- Owned 256 KiB: `284F7F7121D450E22E03C9035FAD863117314CBCB197C33BB607C5230E01A888`.
