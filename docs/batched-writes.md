# Batched writes with 512-byte blocks

The follow-up [contiguous replacement path](copy-on-write.md) relocates eligible
fragmented overwrites, and [large contiguous overwrites](contiguous-relocation.md)
can now relocate too. Both are enabled by default. This page retains the initial
in-place batching measurements; use `relocateFragmented: false` to select
that behavior explicitly.

The portable core now supports contiguous data and undo-journal transfers of
up to 64 KiB while retaining 512-byte filesystem blocks. Allocation granularity,
the on-disk format and the existing `CSUN` recovery implementation are unchanged.
Each write request remains one transaction, including requests spanning several
batches. This is an optional core API; native handler/device integration is
separate and the downloadable handler has not been rebuilt by this work.

## Integration

Implement `IAmigaBlockRangeFileSystemPlatform`, which extends the existing
writable platform with `ReadBlocks(first, count, destination)` and
`WriteBlocks(first, count, source)`. Then call:

```csharp
AmigaSmartFileSystemWriteCore.WriteBatched(
    ref platform, ref work, ref request, batchBuffer, batchBytes, out actual);
```

The caller supplies a mapped buffer separate from the request data and the
four existing scratch blocks. At 512-byte blocks, a 64 KiB batch buffer brings
core scratch storage to 66 KiB. Smaller buffers work, down to one block. The
core limits transfers to the lesser of 64 KiB, the supplied buffer size and
`Geometry.MaxTransfer`, rounded down to whole blocks. It performs no heap
allocation. Existing callers of `Write` retain scalar behavior; a null batch
buffer with zero bytes also selects that path.

Range adapters must provide the same read-after-write visibility and flush
ordering as scalar I/O. Reads must finish before returning, and write sources
must be reusable immediately on return. A range is **not atomic**: failure may
leave a prefix or subset written. The adapter handles DMA/address restrictions
and may split a range or implement it as scalar calls. The test adapter covers
that scalar fallback and 512-, 1,536- and 65,536-byte transfer limits.

Partial leading and trailing blocks retain read/modify/write handling.
Allocation reserves a contiguous run and updates its bitmap and extent once
per run. Runs split at bitmap boundaries, extent boundaries, the 65,535-block
extent limit and the transfer limit. Fragmented free space falls back to
shorter runs; journal images and entries can still occupy separate holes.
Nonempty successful writes clear the archive flag and preserve the other
protection flags. Empty writes leave metadata untouched. On failure, `actual`
is zero; recover the transaction before accessing the volume again.

## Ordering and recovery

For an in-place overwrite, the writer saves a batch of old data blocks and their ordinary
`CSUN` entries, flushes them, publishes the new journal head and flushes again,
then writes the target data range. Metadata retains its existing undo ordering.
All batches share one transaction and its final commit, so a failure after an
earlier batch has been applied still rolls back the entire request.

New data blocks must have been free before the transaction and must not be
reserved for metadata or existing journal records. Their old contents need no
undo image: restoring the original bitmap, extents and file object makes them
unreachable. New data and changed metadata are flushed before committing the
clean transaction marker. The allocator checks both current and original
allocation state, preventing reuse of blocks that rollback still needs.

## Benchmarks

The five-sample Release study uses an in-memory device on the host. Timing
measures CPU and algorithm costs; it does not predict physical disk or Amiga
throughput. Every sample starts from an identical image and verifies contents
with the independent checker. A 1 MiB workload uses sixteen 64 KiB requests.
The optional read cache is disabled in the comparisons below.

| Workload, 512 B blocks | Scalar median | Batched median | Ratio |
| --- | ---: | ---: | ---: |
| First write, 1 MiB | 295.67 ms | 2.28 ms | 129.9x |
| Overwrite, 1 MiB, existing fragmented layout | 140.12 ms | 90.77 ms | 1.54x |
| Overwrite, 1 MiB, contiguous layout | 79.07 ms | 1.85 ms | 42.7x |
| First writes to 128 small files | 24.80 ms | 6.22 ms | 3.99x |
| 32-byte patches to 128 files | 7.94 ms | 7.97 ms | 1.00x |

| 1 MiB workload | Block reads, scalar / batched | Device write requests, scalar / batched | Flushes, scalar / batched |
| --- | ---: | ---: | ---: |
| First write | 2,609,822 / 19,121 | 12,715 / 305 | 4,288 / 176 |
| Fragmented overwrite | 1,179,535 / 735,519 | 8,288 / 7,748 | 4,176 / 3,906 |
| Contiguous overwrite | 704,384 / 12,736 | 8,288 / 160 | 4,176 / 112 |

Counts include metadata and journal I/O. Each range is one device request but
contains multiple block transfers; the JSON records both counts. First writes
and contiguous overwrites reached 128-block (64 KiB) ranges. The fragmented
overwrite reached only four blocks per range.

The original scalar allocator leaves many short extents, so existing fragmented
files cannot generally be transferred in 64 KiB ranges. The study retains that
overwrite workload and adds a second, contiguous fixture created with the new
allocator. Both writers receive identical starting layouts within each
comparison. The initial in-place batching path does not defragment existing files;
the [relocation follow-up](copy-on-write.md) adds that behavior for eligible ranges.

The gains combine fewer bitmap/extent scans and updates, fewer journal records
for newly allocated data, and fewer device requests and flushes. They should
not be attributed solely to joining adjacent data writes. Small partial-block
patches retain the scalar journal path and do not gain from range transfers.

## Validation and retained evidence

`BatchedWriteTests` adds 27 cases. Small two-block batches enumerate every
pending whole-block version at every write/flush cut, inject transient and
persistent read/write/flush errors, and interrupt recovery from a dirty image.
These include aligned overwrite, unaligned overwrite and append, with archive
metadata included in the before/after manifest. Near-full rollback after the
first applied batch and fragmented journal allocation have separate exhaustive
write/flush cut tests.

Full-size tests write 128 KiB in two 64 KiB batches, both into an empty file and
over existing data. Every individual 512-byte write and flush remains a cut
point inside range calls. Ten deterministic persistence choices at every cut
exercise none/all, alternating blocks, halves and seeded subsets of pending
versions. Recovery interruptions use the same sampling. These larger cases
are sampled, not an exhaustive enumeration of the exponentially larger state
space. Successful flushes are assumed durable.

Every recovered image must mount, match the complete before-or-after manifest
and free-space count, pass the independent structural checker and remain
byte-identical after another recovery. Torn-write cases interrupt every write
in a small overwrite after 1, 256 or 511 bytes; they must recover correctly or
refuse mount. Refusal is detected damage, not successful recovery. Additional
cases cover buffer guards, invalid requests, scalar adapters, allocation/extent
boundaries and unaligned writes at all four supported study block sizes.

The benchmark's `Recovery` array reruns the original scalar cross-block-size
matrix: all 68,772 whole-block crash/recovery checks and 2,192 sector-prefix
tears passed. Batched results are in the full reliability report's
`batchedWrites` object and per-test output. Counts represent validation
executions, not unique disk states or probabilities. Neither suite qualifies
arbitrary torn sectors, every possible repeated-crash sequence, physical flush
behavior or compiled MC68000 code.

Reproduce with .NET 10 and the sibling CopperSharp68k checkout:

```powershell
powershell -NoProfile -File scripts/Measure-BlockSizes.ps1 -Repetitions 5 -Mode all
powershell -NoProfile -File scripts/Test-Reliability.ps1
```

Both scripts retain source and binary hashes and fail if their measured sources
change during execution. The reliability script runs the full Release solution
test suite. Reports and TRX files are local ignored artifacts; retain them with
the exact sources when sharing results.

The benchmark report is retained at
`artifacts/batched-writes/final-archive-fix/report.json`. It completed on
2026-10-24 with status `passed` and no source changes during the run.
Assembly SHA-256 identities:

- `CopperSFS.Core.dll`: `1F6D422D605A474505A3FD75AF8FD92567FF7B1BF233AAEEC9E4151AC4A2D235`
- `CopperSFS.Bench.dll`: `3F6D869598EA3E84B376C8DCCFAC715141AA8336B2FEE94B03D9F8A35CF90672`

The full Release suite passed **214 tests, zero failures and zero skips**.
Its report and TRX are retained at `artifacts/batched-writes/validation/`.
No source files changed during that run, and its core and benchmark DLL hashes
match those above. Only result documentation was updated afterward.

| Batched-write evidence | Checks |
| --- | ---: |
| Exhaustive mutation and I/O-failure images | 12,032 |
| Exhaustive interrupted-recovery images | 166 |
| Sampled full-batch mutation images | 10,800 |
| Sampled full-batch interrupted-recovery images | 2,670 |
| Torn-sector cases | 51 recovered, 0 refused mount |

The existing reliability scenarios also passed, including their expected
mount refusals for damaged journals. Their separate report totals are 55,320
mutation/failure image checks, 11,000 recovery image checks, and 728 partial-write
cases (668 recovered, 60 refused mount).
