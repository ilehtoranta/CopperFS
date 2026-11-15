# SFS shared image pool qualification

The shared block-image pool is implemented as an **opt-in** SFS TRST profile.
It retains the current per-mount allocation budget, packet rollback, deferred
close policy and four commit barriers. Against the saved nine-image candidate,
the paired workload uses approximately **3.3% fewer total cycles** and **18.7%
fewer physical write bytes**. Original-SFS parity still fails. Neither shared
pool operation nor automatic TRIM is enabled by default.

This implements the pool option from the
[transaction comparison](sfs-transaction-design-2026-11-14.md). It is not a new
on-disk format or a change to the journal encoding.

## Implementation

[SfsSharedImagePool](../src/CopperSFS.Core/SfsSharedImagePool.cs) owns two small
hash indexes and ordered target lists over one fixed pool. Beginning a packet
copies index/list entries, sharing accepted payloads. The first edit of an
accepted image obtains a private image; further edits reuse it. A full-image
store copies its source directly into the private slot without first copying
the old payload. Unchanged images retain their addresses and verification.

Slots superseded during a packet are retained until its acceptance or rejection.
This preserves the existing borrowed-view lifetime without a separate pin table:
views cannot escape the packet boundary. The implementation does not introduce
a public persistent-handle API. At the boundary, a bounded descriptor walk
reclaims slots absent from the chosen mapping. Hash probing is bounded, and
packet execution makes no new heap allocations.

Rejected packets retain the accepted mapping and roots. Capacity rejection
commits only the accepted group, retries the complete packet, and retains the
existing strict fallback and maximum-budget policy. There is no acceptance of
a trial prefix. Ordered references preserve target insertion order during
journal emission and home writes; commit consumes the references directly and
does not gather images into another arena.

The pool uses a distinct private format tag from indexed arenas and the SFS2
metadata store. Existing portable entry points retain their checked arena path;
`InitializeSharedTrstGroup` explicitly selects the pool. The native
`-SharedSfsPool` switch implies grouped SFS and rejects incompatible profiles.
No platform/application dependency is added to the portable core or standalone
handler.

The original arena overwrite-continuation proof assumes contiguous slots and
deferred arena materialization. The pool currently uses the ordinary checked
write path; it does **not** reuse that proof. This is a deliberate limitation
of this version and is included in its measured performance. Existing metadata
sealing and data-relocation rules remain in force. Byte-identical home-write
suppression was not added.

## Memory and executable size

At 512-byte filesystem blocks the allocation remains **9,756 bytes**, rounded
by Exec to 9,760. The pool fits 17 images:

| Storage | Bytes |
|---|---:|
| Group state and pool control | 256 |
| Two view headers | 64 |
| Two 32-bucket indexes | 256 |
| Two 17-entry ordered lists | 136 |
| Seventeen 16-byte descriptors | 272 |
| Seventeen 512-byte payloads | 8,704 |
| Used / allocation | **9,688 / 9,756** |

The two view headers account for the 64-byte difference from the illustrative
layout in the design document. The 8,716-byte allocation fallback holds 15
images in 8,616 bytes. Other block sizes fit their layout within the exact old
arena allocation extent. Native instruction tests cover 512, 1024, 2048 and
4096-byte geometries, absent enumeration storage and both allocation failures.

The 32-entry enumeration cache, DOS buffer count and existing scratch remain
unchanged. Retained mount data allocations measured in the paired full-ROM runs
are identical to the current candidate: **130,104 bytes after mount and 139,864
after mutation/verification**. No additional task-stack allocation is requested;
stack high-water use was not instrumented.

Seventeen images are shared capacity, not seventeen accepted images plus a
second rollback copy. A packet needs room for accepted images, changed accepted
images and new targets together. A nine-image-wide edit can exhaust this pool
where two nine-slot arenas would fit; safe commit/retry remains necessary.

Executable storage is reported separately from mount data allocations:

| Build | Current HUNK | Pool HUNK | Current code | Pool code |
|---|---:|---:|---:|---:|
| Grouped + AutoTrim | 158,488 | 159,872 | 154,544 | 156,096 |
| Grouped without AutoTrim | 148,732 | 150,356 | 145,790 | 147,452 |

Thus zero additional mount-data RAM does not mean a smaller executable: the
AutoTrim HUNK grows by 1,384 bytes. Both qualified builds use the existing
68000 code target, execute on the 68020 harness, and retain CILica scalar
replacement with inlining disabled. Compiler maps report zero managed
allocation sites and no new BSS.

## Paired cycles and writes

Three alternating trials per arm use original SFS 1.279, the saved nine-slot
candidate and the shared pool. All use identical 16 MiB media, 32 DOS buffers
and explicitly controlled Fast/Chip payload placement. Mutation includes final
ACTION_FLUSH. Mount is timed separately; the total below includes mount,
mutation and verification. Units are millions of reported emulator cycles.

| Phase / placement | Original SFS | Current candidate | Shared pool |
|---|---:|---:|---:|
| Fast mount | 0.522 | 0.375 | 0.371 |
| Fast mutation, including commit | 16.995 | 20.800 | 19.401 |
| Fast verification | 17.569 | 20.134 | 20.157 |
| **Fast total** | **35.085** | **41.309** | **39.929** |
| Chip mount | 0.524 | 0.375 | 0.370 |
| Chip mutation, including commit | 16.987 | 20.801 | 19.402 |
| Chip verification | 17.616 | 20.221 | 20.241 |
| **Chip total** | **35.127** | **41.398** | **40.014** |

Mutation improves approximately 6.7% versus the current candidate. Verification
changes by approximately +0.1%; total work improves approximately 3.3%. The warm
mutation gap to original SFS remains approximately 14.2%, and total including
mount remains approximately 13.8–13.9% higher.

| Mutation work | Original SFS | Current candidate | Shared pool |
|---|---:|---:|---:|
| Commits, TROK writes | 2 | 9 | 3 |
| Device flushes | 7 | 33 | 12 |
| Device write requests | 17 | 108 | 67 |
| Physical bytes written | 150,016 | 230,400 | 187,392 |

The pool saves 43,008 physical bytes versus the current candidate but still
writes 24.9% more bytes than original SFS. Request counts and per-sector trace
counts are different measurement layers; the pool trace records 366 sectors.
The reduction is not solely fewer flush commands:

| Bytes by trace signature | Current candidate | Shared pool |
|---|---:|---:|
| TRST / TRFA / TROK | 15,872 | 9,728 |
| SFS metadata signatures | 25,600 | 12,288 |
| Remaining writes | 188,928 | 165,376 |

Signature categories are diagnostic, not a complete proof of data-write
provenance. No speculative attribution of the remaining gap to a particular
function is made here.

Separate cold original-handler gates include mount and final commit:

| Build / placement | Original mutation | Pool mutation | Original verification | Pool verification |
|---|---:|---:|---:|---:|
| AutoTrim / Fast | 17.508 | 19.762 | 17.572 | 20.158 |
| AutoTrim / Chip | 17.533 | 19.763 | 17.621 | 20.244 |
| No AutoTrim / Fast | 17.496 | 19.723 | 17.577 | 20.130 |
| No AutoTrim / Chip | 17.523 | 19.725 | 17.620 | 20.220 |

All four parity gates fail: approximately 12.6–12.9% more mutation cycles and
14.5–14.9% more verification cycles than original SFS.

Four warm directory scans remain cache-capacity checks. With 24 entries the
original/current/pool counts are 1,960,150 / 1,286,210 / 1,265,360 cycles; with
64 entries they are 5,170,510 / 5,295,930 / 5,288,260. All issue zero device I/O.
These are single-run diagnostics, not additional paired performance trials.

These totals include transaction completion rather than stopping at deferred
packet acknowledgement. Supported-discard service is exercised separately in
the TRIM tests; these numbers are not a supported-device UNMAP latency benchmark.
Payload RAM placement is controlled; this run does not independently force all
metadata/cache allocations into Chip RAM. Cycle counts are not converted to
14 MHz milliseconds or presented as physical-device timings.

## Recovery and qualification

The durability sequence is unchanged: unpublished data/journal durability,
publication durability, home-image durability, then clean-marker durability.
Failed commits retain the accepted logical view and require recovery before
mutations resume. Unpublished destinations must still be free in both committed
and accepted allocation views. Explicit flush, timer, inhibit and manual TRIM
retain their commit boundaries; automatic TRIM requires durable completion.

The focused host selection passes **12 tests**, including mixed-operation cuts
and injected write/flush failures, pool exhaustion, accepted-state retention,
repeated reclamation, colliding keys, copy-on-first-edit, certificate isolation
and allocation canaries. Both pool and legacy mixed-operation paths are tested.
The final extra borrowed-view test was run after the main solution build.

Native qualification passes **10 MC68000/MC68020 pool allocation tests** and
**194 CopperStart native replay tests**. The latter include the qualified
handler's copy, cache invalidation, prefetched-image, discard and arithmetic
paths as well as platform buffer/transfer cases. An earlier smaller selection
had five missing-handler skips; the final handler-bound selection has none.

The final full-ROM results contain **47 correctness passes**: 26 with AutoTrim
and 21 without. Coverage includes published journal prefixes replayed by both
handlers, failure at every commit barrier, refusal of mutations while recovery
fails, pressure retry, harmless rejections, deferred close, explicit flush,
timer, inhibit, manual TRIM and clean handoffs. AutoTrim adds reallocation,
partial failure, inhibit/remount and cuts during discard. Four diagnostics
(paired warm comparisons and enumeration checks) pass separately. The four
original-handler performance gates remain failures as reported above.

Two test assumptions required correction, without changing production code:

- Six pressure tests now use 32 byte edits over a 16 KiB file and a 4 KiB
  unpublished append. The old 16-edit case could fit the pool or elide a clean
  device flush, so it did not reliably exercise pressure and the fourth barrier.
  All six strengthened cases pass.
- The identical-metadata test now distinguishes grouped acknowledgement from
  durability and explicitly flushes before checking the three nonempty device
  barriers. Both pool builds pass. The saved arena candidate also passes this
  test and all six strengthened pressure tests (seven control passes).

The earlier logs retain the obsolete assertion failures; they are superseded by
the focused reruns, not counted as additional successful cases. No production
recovery checks or barrier assertions were weakened to obtain a pass.

Release solution build and tests passed: **2,040 passes and four skips**,
including 1,240 SFS passes and three external-media skips. The remaining skip
is the opt-in CPX boot test. The complete solution receipt is recorded in
`artifacts/sfs-pool-release-tests.log`. Physical-device power-loss, firmware behavior and
completion latency were not tested. Host, native and full-ROM results are not
physical-device qualification or proof of parity across all volume sizes and
fragmentation patterns.

## Reproduction and receipts

Build with `build/Build-Handler.ps1 -SharedSfsPool`, optionally `-AutoTrim`.
The qualified builds use `-Cpu 68000 -SkipCompilerBuild`; scalar replacement
and disabled CILica inlining remain the script's established policy.

- AutoTrim SHA-256:
  `684FBD47E2E1A38F271C7304932408324305645A1E806E0687C2A3BF2F69D034`.
- Without AutoTrim:
  `F9D12904D64E5D8F9BF4235E1BB538B28DC5CAE6C6F82426796189EF5DE90418`.
- Saved current candidate:
  `F98067A9040F8ED4D6EDD8467BD967615741472A68146801A21F943999279C21`.
- Original SFS:
  `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

Ignored receipts are under `artifacts/sfs-pool/`: `qualified-candidate`,
`qualified`, `no-auto`, `no-auto-qualified`, `arena-control`, `host`, `release`,
`instructions` and `native`. The preliminary 68020-target `candidate` and its
`qualification` directory are not the final matched-target comparison.

Release commands use `dotnet build CopperFS.slnx -c Release` and
`dotnet test CopperFS.slnx -c Release` with an isolated artifacts path.
Pool instruction tests use `-p:CopperFsSfsGrouped=true -p:CopperFsSfsPool=true`.
Full-ROM runs set `COPPERFS_SFS_GROUPED=1`, the appropriate qualification
manifest, licensed ROM path and saved candidate reference. The relevant TRX
receipts are `pool-qualified-rom`, `pool-pressure`, `pool-no-op`, `pool-trim`,
`pool-no-auto-rom`, `pool-no-auto-no-op`, `pool-arena-control`,
`pool-instructions`, `pool-qualified-native` and `pool-focused`.

No ROMs, media, generated handlers or qualification artifacts are source-commit
inputs. No release/default switch, commit, push or publication was performed.
