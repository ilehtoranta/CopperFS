# SFS commit baseline and identical metadata updates

The fresh baseline confirms that SFS still spends far more device commands on
mutation than original SFS 1.279. This change removes transactions for identical
protection, date and comment updates. It does not solve the transaction-grouping
gap: measured total mutation cycles improve by only 0.08–0.09%.

## Change and safety boundaries

After the existing preparation and record validation, compare the requested
protection word, normalized timestamp or complete comment with the current
record. Return success before Begin/Edit/Put/Finish when they are identical.
Invalid timestamps, embedded NUL comments, invalid records and write protection
still go through their existing checks. Changed updates retain the existing
journal encoding, publication and durability barriers. No close, timer, inhibit,
explicit-flush or recovery dispatch was changed. Pending groups are still
committed before strict metadata operations.

There are no new mount fields, buffers, caches or allocations: additional mount
RAM is zero by construction. Standalone builds keep scalar replacement enabled
and CILica inlining disabled. Automatic TRIM remains explicitly opted in for
these comparison builds; its release default is unchanged.

## Fresh original-handler comparison

Three alternating trials per handler and memory placement, using the same SFS
fixture, 68020, packet workload and original SFS 1.279. The timed mutation phase
includes its final filesystem ACTION_FLUSH. These are emulator cycles, not
physical-device timings. Values below are arithmetic means in millions.

| Payload placement / phase | Original, baseline run | Before | Original, candidate run | After |
|---|---:|---:|---:|---:|
| Fast mutation | 17.538 | 22.679 | 17.503 | 22.661 |
| Fast read/verify | 17.577 | 20.177 | 17.575 | 20.190 |
| Chip mutation | 17.506 | 22.685 | 17.530 | 22.665 |
| Chip read/verify | 17.614 | 20.271 | 17.621 | 20.280 |

Read/verify is slightly worse (0.06% Fast, 0.04% Chip); this is not a read-path
improvement. Original timing varies between runs, so both original measurements
are shown. The candidate remains approximately 29% over original mutation cycles
and 15% over original verification cycles. Both parity gates fail.

| Mutation device requests | Original | Before | After |
|---|---:|---:|---:|
| Reads | 7 | 23 | 23 |
| Writes | 17 | 206 | 203 |
| Flushes | 7 | 116 | 113 |

The reduction is real but small. SFS already groups Writes; non-write mutations
still use strict dispatch and changed-file close still commits. Removing those
boundaries requires qualifying a wider accepted-transaction lifetime, including
visibility, rejected packets and capacity retry. This patch does not claim that
work is complete or that SFS has reached original-handler parity.

The fresh full-ROM boundary trace makes the remaining cost concrete. Across the
six instrumented closes, original SFS issues no flushes. CopperSFS issues
4 + 4 + 4 + 4 + 3 + 0 = **19 flushes**. Before the final explicit flush,
original has issued 3 flushes and CopperSFS 113. The explicit flush adds four
for original and zero for CopperSFS because its preceding strict operations
already made the changes durable. Both reply with clean device state. The
unmarked SFS-specific replacement/protection operations are included in total
workload timing, but not in those six close markers. Merely moving close work
to the next strict mutation would not necessarily reduce total work; grouping
must span those operations to deliver the larger saving.

## Reproduction and artifacts

Ignored receipts are under `artifacts/sfs-commit-tuning/`. The baseline and
candidate were built with `build/Build-Handler.ps1 -AutoTrim -SkipCompilerBuild`
and separate artifact roots. `baseline-manifest.json` and
`qualification/manifest.json` pin the actual handlers and source media.

- Baseline HUNK: 157,000 bytes,
  SHA-256 `1A961D044E73C3033C67265AE8F1100A69344B826956BAF0312AB264ACCF3428`.
- Candidate HUNK: 156,880 bytes,
  SHA-256 `621D4A82B767DA1A3AAFC68960F9C1240B258C42E513C4699AC79D3550015086`.
- Original SFS: SHA-256
  `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
- Normal build without automatic TRIM: 147,836 bytes, SHA-256
  `4C74D54104F2FD67FC3AEE117B9AEDA22EFD3D66588F15247EFB3C7020CA39FC`.

The `SfsCommitPerformance` tests select SFS explicitly and preserve independent
Fast/Chip comparisons. `SfsCommitHandoff` checks both handlers' clean handoffs;
`SfsCommitIdenticalMetadata` checks the compiled no-op and following real commit.
Six host cases cover all three metadata operations through both journal paths.
The portable strict TRST API deliberately retains its pre-staging flush; the
native platform elides that flush when clean. The initial host assertion was
corrected to distinguish that call from transaction-publication barriers.

The initial baseline command also selected eight non-SFS cases without their
manifest entries; those setup failures are not qualification failures or passes.
Only the two completed SFS measurements are used above. An initial overlapping
host rebuild hit Windows DLL locks; the final solution run uses an isolated
artifact tree. Physical-device power-loss and latency qualification is not
claimed.

## Qualification

- Release solution build passes (one existing CopperSharp nullable warning).
  Filesystem host tests: **1,558 pass**, three optional media cases skip
  (SFS 1,212, FFS 296, Amiga 18, storage 32). This includes all six new metadata
  cases and the existing recovery/failure suites.
- Full solution tests: **1,869 pass, one fails, four skip**. The failure is in
  the separately developed CPX implementation:
  `LinkPacketTests.ParentComponentsResolveFromDirectoryOrFileAndStopAtRoot`,
  expected error 0, actual 205. No CPX source was changed for this SFS work.
  Its test count changed during concurrent workspace development, so this is
  the final isolated build's receipt, not a claim about all subsequent edits.
- Both affected standalone profiles build successfully, with no managed
  allocation sites reported by the compiler.
- CopperStart native replay: 51 unique selected cases pass. The first invocation
  omitted the handler-path environment variable and skipped five cases; the
  compiled-handler subset was then rerun with the actual candidate (33 passes,
  overlapping 28 of the first invocation's 46 passes).
- Full-ROM automatic-TRIM candidate: ten correctness cases pass: seven original
  recovery scenarios, clean handoffs, identical-update/changed-update behavior,
  and the commit-boundary trace. Recovery covers 87 injected cut points and
  boots original SFS on 37 distinct published dirty images.
- Full-ROM normal profile without automatic TRIM: two more cases pass (clean
  handoffs and identical-update/changed-update behavior), for twelve total
  correctness cases across the two built profiles.
- Two original-performance gates fail as reported above; passing file/image
  checks in their workloads is not a performance pass.
- The SFS no-op probe initially assumed the SFS2 fixture's Payload file existed;
  it now creates its own file. Its following changed metadata commit expects
  three actual device flushes because there is no dirty unpublished data to
  flush before journal publication. The corrected compiled test passes.
- Physical devices, actual DMA behavior and hardware power cuts are untested.
