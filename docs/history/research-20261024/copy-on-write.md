> Historical research snapshot from CopperSFS commit 4a902a1ff9189bbdff841c23441c66678a6e2427.
> Preserved on 2026-11-04; commands, paths, results and recommendations below are historical, not current validation.
> See [current port status](../../research-port.md) before using this material.

# Contiguous replacement of fragmented overwrites

This records the initial fragmented-only policy and its measurements. The
[large contiguous overwrite follow-up](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/contiguous-relocation.md) adds relocation
for 64 KiB contiguous ranges in both the portable and native SFS1 writers.

`WriteBatched` now relocates eligible fragmented overwrites into contiguous
free space. It keeps 512-byte blocks, the existing disk format and the existing
`CSUN` recovery reader. The previous in-place batching path remains available.

## Selection and integration

The existing six-argument `WriteBatched` entry point enables relocation by
default. To explicitly retain in-place overwrites, use the overload:

```csharp
AmigaSmartFileSystemWriteCore.WriteBatched(
    ref platform, ref work, ref request, batchBuffer, batchBytes,
    out actual, relocateFragmented: false);
```

A replacement candidate must contain at least 16 KiB of fully overwritten
blocks and cross a physical gap between existing extents. It is bounded by
the caller's scratch buffer, `MaxTransfer`, 64 KiB and the existing file
allocation. Adjacent extent records describing physically continuous data do
not trigger relocation. The 16 KiB threshold is an initial conservative policy,
not a measured optimum.

The allocator looks for a run large enough for the complete candidate within
one bitmap block. If none exists, that iteration uses in-place undo journaling.
Small writes, small transfer buffers, contiguous ranges and partial leading or
trailing blocks also retain the in-place path. Ordinary `Write` is unchanged.
The new path uses the same caller-owned memory and adds no heap allocation.

Only overwritten full blocks move. Untouched bytes in the file remain in their
original blocks. Extents are split at replacement boundaries, old entries are
removed, and the new extent is linked between the retained neighbors. The
allocator prefers continuing the preceding extent; adjacent replacements merge
when their combined length fits the format's 65,535-block extent limit. This
works within one request and across successive committed requests. It does
not guarantee that the entire file becomes contiguous.

## Transaction ordering

The operation remains part of the enclosing write transaction:

1. Reserve the replacement run using the journaled allocation bitmap.
2. Write the replacement bytes and flush them.
3. Journal and update extent records, neighbor links and allocation metadata.
4. Update the file object, flush all changes, then commit the clean marker.

Old blocks are marked free in the transaction's working bitmap, but every
allocator also checks the original transaction bitmap. This prevents their
reuse for data, journal records or admin space until commit. Their original
contents remain available for rollback even after earlier batches have moved.
Recovery restores the old metadata on interruption, making the new blocks
unreachable and allocated old blocks reachable again.

No undo copy of a relocated data block is necessary. Its original block remains
intact until commit; only mapping/allocation changes need undo. This reduces
data-journal traffic compared with saving both an old-data image and a journal
record for every overwritten block. Extent changes still cost metadata I/O and
can grow or prune the extent tree. A later failure, including journal exhaustion
after the first successful relocation, returns zero written bytes and requires
recovery of the whole request.

## Benchmark method

The final comparison runs scalar writes, the previous in-place batched path
and relocation on identical starting images, with five measured samples after
warm-up. `direct-batched-64k` explicitly disables relocation;
`direct-batched-relocate-64k` enables it. Cache variants are also recorded.
Each 1 MiB workload consists of sixteen 64 KiB requests. Every result is checked
against expected contents with the independent image checker.

The fragmented fixture is produced by the original scalar allocator. A
separate contiguous fixture ensures the selection policy is measured on an
already contiguous file as well. Timings measure host CPU costs on an in-memory
device, not physical storage or Amiga throughput. Device request, block-transfer
and flush counts are recorded separately.

| Workload, 512 B blocks | Scalar median | In-place batching | Relocation enabled |
| --- | ---: | ---: | ---: |
| Fragmented overwrite, 1 MiB | 137.32 ms | 90.86 ms | 2.07 ms |
| Contiguous overwrite, 1 MiB | 78.99 ms | 1.91 ms | 1.86 ms |
| First write, 1 MiB | 295.58 ms | 2.47 ms | 2.27 ms |
| First writes to 128 small files | 24.70 ms | 6.12 ms | 6.23 ms |

The fragmented-overwrite host time improved about **43.9x** over the previous
batched path in this run. The other rows retain the same I/O counts with
relocation enabled; their timing differences do not establish a policy benefit.

| Fragmented overwrite, 1 MiB | In-place batching | Relocation enabled |
| --- | ---: | ---: |
| Block reads | 735,519 | 14,859 |
| Block writes, including journal/metadata | 8,153 | 6,432 |
| Device write requests | 7,748 | 590 |
| Flush calls | 3,906 | 131 |
| Largest range | 4 blocks / 2 KiB | 128 blocks / 64 KiB |

The workload mixes relocated ranges with ranges that already fit a contiguous
transfer and therefore still use in-place undo. Newly freed runs can also
improve journal placement for later committed requests. The result does not
mean every block in the file was relocated or that the whole file is now one
extent.

The report is retained at `artifacts/cow-writes/final/report.json`. It completed
on 2026-10-24 with status `passed` and no source changes during the run. Its
scalar cross-block-size recovery matrix also passed all 68,772 whole-block
image checks and 2,192 sector-prefix tears. Assembly SHA-256 identities:

- `CopperSFS.Core.dll`: `3DD8DEBB851E4EA9BBFADDF760564AB5D9549D06EBB3A7E71C8D9060D0475135`
- `CopperSFS.Bench.dll`: `33E150F0A286E653581C47E8CF33684B41FDFE9CC066FFA5FB4016524A565F0F`

## Recovery coverage

The new `RelocationTests` cases cover full, prefix, suffix and unaligned middle
replacements; overwrite plus extension; preserved old data and unchanged free
counts; repeated writes; scalar fallback; transfer limits and block sizes;
bitmap boundaries; multiple 64 KiB batches; disk exhaustion after relocating
the first batch; indexed-tree pruning; and splitting a full leaf into an index.

Two small fixtures inject every baseline read, write and flush failure, both
transiently and persistently, as well as every mutation write/flush cut. They
also interrupt recovery and inject every recovery read/write/flush failure
from a selected dirty state. Pending-version combinations are fully enumerated
when there are at most 4,096; larger spaces use ten deterministic persistence
samples. The report counts exhaustive and sampled executions separately.

The full-batch, nearly full disk and indexed-tree cases sample all write/flush
cut points, including every individual 512-byte block inside range transfers.
Indexed-tree cases additionally inject transient and persistent write/flush
failures. Full-batch tests also interrupt recovery. Torn-write tests persist
1-, 256- and 511-byte prefixes of every mutation write; recovery must either
produce a valid before-or-after image or refuse mount.

Recovery must restore complete contents, metadata and free-space counts,
including the untouched second file. Each image must mount, pass the independent
structural checker and remain byte-identical after another recovery. Counts
are validation executions, not unique states. Successful flushes are assumed
durable; arbitrary tears and repeated crashes across all dirty states are not
exhaustively covered.

The full Release solution passed **239 tests, zero failures and zero skips**,
including 25 new relocation cases. The report and TRX are retained at
`artifacts/cow-writes/validation/`. No sources changed during the run, and its
core and benchmark DLL hashes match the benchmark identities above. Only
reporting documentation was updated afterward.

| Relocation evidence | Checks |
| --- | ---: |
| Exhaustively enumerated mutation/recovery/failure images | 64,375 |
| Sampled mutation/recovery/failure images | 36,870 |
| Torn-write cases | 246 recovered, 0 refused mount |

The existing batching and filesystem regression suites also passed. Their
counts are retained separately in the same report; the relocation totals above
do not include those suites or the benchmark's scalar recovery matrix.

Reproduce and retain source/binary identities with:

```powershell
powershell -NoProfile -File scripts/Measure-BlockSizes.ps1 -Repetitions 5 -Mode all
powershell -NoProfile -File scripts/Test-Reliability.ps1
```

The measurements above apply to the portable core. The subsequent
[native handler integration](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/native-batched-writes.md) has its own builds,
benchmarks and recovery evidence.
