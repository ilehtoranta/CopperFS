# CPX4 CPU hot-path redesign, 2026-11-15

Historical checkpoint. See the [latest benchmark report](cpx4-benchmark-2026-11-15.md)
for current results and coverage. Optimization is paused; the target remains unmet.

Continued in [metadata and persistence redesign](cpx4-metadata-redesign-2026-11-15.md).

## Acceptance remains open

CPX4 still loses both workloads to original Kickstart FFS 40.1 and
SmartFilesystem 1.279. This is a development checkpoint, not a performance win
or a published filesystem version. CPX3 is outside this native comparison.

Two trials per filesystem ran in the established order CPX4, FFS, SFS, SFS,
CPX4, FFS. Values below are median **emulated bus cycles** on the unchanged
Kickstart 3.1 / 68020 / 512 KiB Chip + 2 MiB Fast machine, 16 MiB partitions,
512-byte sectors and 32 requested DOS buffers. The CPX4 HUNK targets 68000.
Internal cache memory differs between implementations. The benchmark handler
stack is 32 KiB; the separate ROM gate uses 4 KiB and no Fast RAM.

| Workload and phase | CPX4 | Original FFS 40.1 | Original SFS 1.279 |
|---|---:|---:|---:|
| Mixed mutation | 4,137,120 | 1,683,665 | 2,478,920 |
| Mixed flush | 1,553,295 | 20,455 | 161,220 |
| Mixed remount | 1,816,280 | 108,980 | 218,775 |
| Mixed verification | 1,535,665 | 969,780 | 1,059,595 |
| **Mixed total** | **9,042,360** | **2,782,880** | **3,918,510** |
| Streaming mutation | 6,067,910 | 2,110,830 | 4,561,745 |
| Streaming flush | 1,864,820 | 20,330 | 171,910 |
| Streaming remount | 1,996,320 | 114,990 | 217,350 |
| Streaming verification | 368,051,555 | 365,196,530 | 364,911,340 |
| **Streaming total** | **377,980,605** | **367,442,680** | **369,862,345** |

Mixed performs the existing 8 KiB write, two partial overwrites, truncate to
6003 twice, rename, second file, ACTION_FLUSH, inhibit/remount and complete
read verification. Streaming performs the existing 32 x 64 KiB writes, partial
overwrites, no-op truncates and namespace operations, followed by the same
barrier/remount and a complete 2 MiB verification. Its guest byte-verification
loop dominates the total; mutation and durability phases remain visible.

Every trial passes guest assertions and the existing independent CPX4 checker,
FFS checksum/payload checks or SFS checker/payload checks. Neither the workload,
original handlers nor emulator timing was changed for these measurements.

Relative to the preceding qualified checkpoint, mixed mutation falls from
12,550,165 to 4,137,120 cycles and streaming mutation from 48,980,915 to
6,067,910. Mixed total falls from 23,989,250 to 9,042,360. These gains do not
satisfy the requested comparison: CPX4 mutation remains 2.46 times FFS for
mixed and 2.87 times FFS for streaming.

## Retained changes

- Cache immutable geometry and storage addresses; use direct arena state
  accesses and aligned native 16-bit metadata accesses. Portable memory
  implementations preserve the big-endian word contract.
- Skip occupied bitmap bytes before address validation during reserve refill.
  Recovery reuses its authenticated bitmap while consecutive reserve entries
  stay in the same window, retaining every address, duplicate and allocation
  check.
- Admit bounded cached directory creation, same-directory file rename and
  directory lookup. General trees, aliases, external names and uncertain shapes
  retain the general path. Shape, owner, ordering and canonical-key checks
  precede fast-path mutation. Plans that would exhaust the metadata reserve
  return to ordinary refill/admission. Malformed empty-inline records cannot
  bypass the ordinary write validator.
- Transfer aligned initial writes and contiguous appends directly, update owned
  metadata in place, and cross a bitmap window after validating and owning the
  second bitmap. Group prefixes, reserved units and the backup remain excluded.
- Resolve single-extent mapped reads and in-place overwrites from cached
  records, including partial read/modify/write. No-op resizes avoid reclamation.
  Sparse, unwritten, fragmented, large-address and capacity cases retain their
  existing paths.
- Preserve validated metadata from startup into normal execution. Every
  remount clears old cache state; a repeated physical read refreshes its cached
  image. A failed mount followed by repair cannot reuse the failed attempt's
  pages.
- Reuse the generated CRC tables when geometry is unchanged. The buffered path
  uses an exact linear CRC-32C transform for verified zero runs. Independent
  bitwise-oracle cases cover mixed, dense and zero data at 64-byte boundaries
  for all four allocation-unit sizes. Checksum bytes are still excluded.

The optional buffered region now uses 118,024 bytes with 1 KiB metadata pages
and 419,080 bytes with 4 KiB pages. A 4 KiB zero-transform table replaces eight
clean pages; eight clean pages and eight object records remain. Dirty and undo
capacities stay at 42 pages each. Geometry/control words occupy the existing
gap before the DMA page; the native maximum arena remains 63,488 bytes.

The core remains allocation-free, caller-owned and single-owner. No portable
core dependency on CopperStart, CopperDisk or CopperScreen was added.

## Durability and limits

The agreed buffered contract is unchanged: ACTION_FLUSH, successful inhibit /
unmount and shutdown wait for publication and the device barrier. Earlier
replies may precede persistence. Data buffers remain pinned until their I/O
finishes. In-place data overwrite atomicity is unchanged.

Journal layout, CRC polynomial, descriptors, image validation, commit ordering,
checkpoint barriers and the quarantine of persistently freed blocks are
unchanged. The bounded discard after ACTION_FLUSH is retained. Buffered flush
and inhibit use the persistence barrier; optional pruning remains in ordinary
maintenance flush and recovery. Strict portable execution remains the default;
native mounts retain strict fallback when optional storage cannot be allocated.

The remaining CPU cost is distributed across packet processing, metadata
continuations, page copies and publication/recovery. A separate instruction
profile of mixed mutation attributes approximately 2.53M cycles to CPX methods,
1.15M to other RAM code and 0.38M to ROM. Page copying alone accounts for about
0.43M CPX cycles. This diagnostic uses scalar stepping and is separate from the
unmodified acceptance runner.

A further checksum microbenchmark unrolled the scan for 64 zero bytes. It
reduced sparse-page cost from 11,734 to 10,407 cycles but increased dense-page
cost from 35,160 to 35,575. It was not promoted into the retained handler.

## Reproduction and provenance

The retained HUNK is `artifacts/cpx4-cpu-redesign-20261115/retained-handler/CopperCPX4`,
219,720 bytes, SHA-256
`2ABA2CD1834D84CB97E15F7045FF17D26DFF3ADD8AD618C3D30CFAA8735B1E86`.
Its compatibility report contains zero managed allocation sites. The adjacent
source manifest records every core/native input and compiler/input-assembly
hash. The original-handler and ROM hashes remain those recorded in
[the preceding write redesign](cpx4-write-redesign.md).

The maintained `tools/CopperCPX.Bench` runner produced
`artifacts/cpx4-cpu-redesign-20261115/retained-mixed/benchmark.json` and
`artifacts/cpx4-cpu-redesign-20261115/retained-stream/benchmark.json`.
The scalar profile of the preceding candidate is in `verified-detail-profile`. Superseded experiments and
failed validation attempts are retained as diagnostic artifacts, not passes.

## Validation

`dotnet build CopperFS.slnx -c Release` succeeds. The retained-source
`dotnet test CopperFS.slnx -c Release` run passes 2,403 tests with zero
failures and nine skips. Coverage separates as follows:

- Host/managed suites: 2,328 passes; three external SFS fixture cases unavailable.
- Native instruction execution: 74 CPX4 cases and one prototype arithmetic case
  pass. CopperStart's separate native geometry/buffer replay passes 141 cases.
- The six opt-in ROM test methods are skipped in the standard solution run.
  The explicit 19-case run on the preceding `B7A716...` HUNK passes 18 cases and
  exposes an exhausted-reserve admission difference. The retained `2ABA2C...`
  HUNK corrects it and passes all three full-ROM transaction-boundary cases.
  The other ROM cases were not rerun on that retained HUNK; this is not a
  claim that a fresh 19-case run passes on the final binary.

The final results and provenance are consolidated in
`artifacts/cpx4-cpu-redesign-20261115/qualification-summary.json`.

The complete solution run includes the corrected test arena placement and the
explicit no-memory response for optional startup-buffer allocation in simulated
Exec. Earlier failed or interrupted runs are diagnostic artifacts, not passes.
The compiler emits its existing nullable warning in `M68kSpillAndCopy.cs`.

ROMs, media and generated handlers remain ignored artifacts and are not releases.
