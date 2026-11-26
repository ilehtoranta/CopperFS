# Metadata writes and commit frequency — 2026-11-12

FFS large overwrites use 76.3% fewer cycles than the previous candidate. The
mixed release workload improves by 0.54% for FFS and 0.25% for SFS2 TRST.
Original-handler parity still fails. Automatic TRIM remains disabled by default.

The baseline is the candidate from the earlier performance gap qualification.
FFS and SFS2 TRST are the performance priorities; applicable FFS changes also
cover OFS. No disk format, packet ABI, recovery protocol or durability barrier
for a changed transaction is removed.

## Implemented changes

- **FFS and OFS:** an overwrite that leaves the file size unchanged no longer
  rereads and rewrites its identical file header at completion. The root is
  still invalidated before mutation and restored only on successful completion.
- **FFS and OFS:** after validating the entire root, changing its validity word
  adjusts the checksum by `old validity - new validity`. This avoids a second
  whole-root checksum scan. The resulting bytes and write ordering are unchanged.
- **FFS:** full, contiguous existing data blocks use one bounded bulk transfer
  instead of reading each block into scratch, copying over it and writing it
  individually. Existing pointer/allocation validation still precedes the write.
  Runs stop at pointer-table boundaries, fragmentation and the existing EOF.
  Partial blocks retain the read/modify/write path. Device transfer limits and
  DMA/bounce handling remain the platform's responsibility.
- **OFS applicability:** the first two changes apply directly. The raw bulk
  overwrite path does not: OFS embeds a header, checksum and forward link in
  each data block. Its previous run-construction optimization is retained.
- **SFS2 TRST:** the existing compression plan identifies images identical to
  the original. Those images are omitted from journal records and home writes,
  while their block numbers remain excluded from journal-space allocation.
  An entirely unchanged operation publishes no journal and issues no flush.
  Changed transactions still use journal → TRFA → home blocks → TROK, each
  with its existing durability barrier. Subsequent operations reset the staging
  descriptor, including the temporary high-bit omission flags.

The omission flag is outside the supported SFS2 block-number domain and is
confined to volatile staging. It is never written into a disk journal record.
No additional permanent allocation is introduced.

## Large FFS writes, with durable checkpoints

The probe grows a 96 KiB file across pointer tables, interleaves another file,
then overwrites it in twelve 8 KiB packets. Both write phases now explicitly
flush before their ending timing marker. Fast RAM is explicitly allocated
before loading either handler. Both arms are rerun with this revised probe;
older probe timings without those flushes are not used below.

Mean guest cycles from three alternating trials, emulated 68020 / Kickstart 3.1:

| Phase | Previous CopperFFS | New CopperFFS | Change |
| --- | ---: | ---: | ---: |
| Growth plus flush | 2,422,860 | 2,412,973 | 0.41% fewer |
| Overwrite plus flush | 7,751,270 | 1,836,387 | 76.31% fewer |
| Read/verification | 4,021,633 | 4,022,220 | +0.015%, within 0.1% tolerance |

During overwrite, device reads fall from 194 to **zero**, writes from 228 to
**37**, and the explicit flush count remains one. Growth retains 40 writes and
one flush. The before/after large-write gate passes all its cycle and I/O checks.
Earlier bulk-write prototypes failed the growth tolerance; they are excluded.

Direct comparison against **original FFS 40.1**, using the same flushed probe:

| Phase | Original FFS | New CopperFFS | Original / Copper writes |
| --- | ---: | ---: | ---: |
| Growth plus flush | 291,893 | 2,412,837 | 15 / 40 |
| Overwrite plus flush | 274,160 | 1,836,503 | 16 / 37 |
| Read/verification | 3,990,060 | 4,022,060 | 0 / 0 |

Both write phases have one flush for each handler. Original FFS also performs
one flush during verification. The new implementation is still about 8.27×
the original's growth cycles and 6.70× its overwrite cycles. The original-handler
large-write gate fails; the large improvement versus Copper's baseline is not
an original-handler parity pass. All payload and independent image checks pass.

## Mixed release workload against the originals

Fast RAM, millions of guest cycles. Mutation includes the final explicit flush;
verification includes guest data comparisons and OS work. Originals are FFS
40.1 and SFS 1.279 on the corresponding media format. Lower is better.

| Profile | Original mutation | Copper mutation | Extra mutation cycles | Extra verification cycles |
| --- | ---: | ---: | ---: | ---: |
| FFS | 12.165 | 20.785 | +70.86% | +3.37% |
| SFS | 17.482 | 22.543 | +28.95% | +14.05% |
| SFS2 TRST | 15.621 | 32.041 | +105.12% | +18.94% |
| SFS2 CSUN | 15.599 | 35.674 | +128.69% | +18.43% |
| OFS, secondary priority | 15.140 | 34.453 | +127.56% | +10.58% |

Versus the previous candidate, mutation cycles fall 0.54% for FFS, 0.25% for
TRST and 0.40% for OFS; SFS and CSUN are unchanged. Chip RAM reductions are
0.53%, 0.23% and 0.40%, respectively. The affected profiles' verification
cycles remain slightly lower. All ten original-handler Fast/Chip parity gates
still fail their speed assertions; functional probes and image checks pass.

## Commit-frequency result and remaining work

The full-ROM no-op test verifies **zero writes and zero flushes** for an identical
metadata packet, immediately followed by a changed packet with **four flushes**.
This checks both native omission and correct reuse of the staging buffer.

In the mixed workload, TRST reads fall from 111 to 109 and writes from 156 to
154. Flushes remain **104**, versus **seven** for original SFS. There are still
26 changed transactions: eliminating unchanged images does not combine them.
Cross-packet transaction grouping is not implemented in this pass. It remains
the major commit-frequency opportunity and requires a retained acknowledged
view, rollback on failed packet staging, bounded buffer-pressure commits, and
correct flush/inhibit/media/recovery boundaries. Simply removing an existing
barrier does not provide those guarantees.

FFS's mixed workload is dominated by growth and small partial overwrites, so
its improvement there is much smaller than in the full-block overwrite probe.
Root validation/publication and allocation/pointer checks remain significant
costs. They are retained rather than bypassed to obtain a faster timing result.
No work is moved to deferred execution by this pass. Automatic TRIM's bounds,
allocation tracking and commit eligibility remain unchanged; the qualification
matrix includes larger, fragmented and warm reclamation, live reallocation,
partial discard failure and inhibit/remount.

## Qualification and artifacts

The selected manifest is `artifacts/metadata-pass/qualified/manifest.json`.
Its affected binaries are:

| Handler | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS (OFS/FFS) | 81,828 | `8EA8D5F6FD3B0AF2B708AF3EE5428C37D849512585CEA52260DF910E2AF7462B` |
| CopperSFS SFS2 TRST | 206,972 | `AC31E5CF299DE9E9C5AC31771EBD98C039D39FE8577A75B7AD1D2F9DC7056EBB` |

Both are standalone 68000 builds with automatic TRIM explicitly opted in for
qualification. CILica scalar replacement remains enabled and inlining disabled.
SFS and CSUN binaries are unchanged from the previous qualified manifest.

- Host: Release solution build passes with no warnings or errors; solution tests
  pass 1,495 cases with three external-image skips. After the final shared root
  checksum adjustment, all 296 FFS/OFS core tests pass again.
- Native: both affected standalone handlers build; all 193 CopperStart native
  replay tests pass on the final source.
- Full-ROM correctness: 68 compatibility cases, six TRST commit-cut/recovery
  cases and one no-op commit case pass. The 28 affected FFS/OFS compatibility
  cases were rerun against the final CopperFFS binary and pass; these replace
  earlier runs within the 68-case matrix, rather than increasing its count.
- Performance: the final large-write comparison against the previous CopperFFS
  passes. All ten Fast/Chip mixed-workload original-handler parity gates fail,
  as does the large-write gate against original FFS. Payload and image checks
  pass; the remaining failures are performance comparisons reported above.

Physical devices, a real DMA bus, AmigaOS 2.0 hardware and physical media
replacement are unavailable and are not counted as passes.

Raw timing samples and hashes are under `qualified/original-performance`,
`qualified/original-performance-chip`, `qualified/large-writes` and
`qualified-original-large/large-writes`. The two large-write directories use
the same revised probe, with the previous CopperFFS and original FFS as their
respective baselines. `qualified/parity-summary.json` contains all mixed-workload
means and comparisons. Earlier `bulk`, `final`, `release` and `selected`
directories are intermediate experiments, not the selected candidate.

Use the environment variables and build/test commands in the preceding
performance gap qualification (`docs/performance-gap-qualification-2026-11-12.md`
in git history), pointing
to this manifest. The large-write test additionally uses `COPPERFS_SIZE_MANIFEST`;
the full-ROM no-op test uses `COPPERFS_SFS2_TRST_HANDLER` and `COPPERFS_ORIGINAL_SFS`.
No packages, ROMs or releases are published by this work.
