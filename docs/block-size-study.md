# Block-size study, 2026-10-24

This page records the original scalar-write baseline. The subsequent
[batched-write study](batched-writes.md) measures the new optional writer with
512-byte blocks and up to 64 KiB transfers.

Keep **512-byte filesystem blocks as the default for classic Amiga volumes
dominated by small files and metadata operations**. For workloads dominated by
large writes, **4 KiB is the strongest candidate to validate on the target
handler/device**. There is no workload-independent optimum: 1 KiB won the
small-file write workload, while 512 bytes won creation, small reads, lookups
and 32-byte overwrites. This study does not change a format or handler default.

These are measurements of the portable core on an **in-memory x64 host
device**, not MC68000 timings or storage throughput. The separate native
handler has its own buffered-write implementation. Its performance and
non-512-byte interoperability need separate measurement.

## Method and reproduction

```powershell
powershell -NoProfile -File scripts/Measure-BlockSizes.ps1
```

The script builds `tools/CopperSFS.Bench` in Release and disables tiered JIT
compilation. It writes a fresh `artifacts/block-size/<timestamp>/report.json`
with individual samples, I/O counts, recovery counts, source and assembly
SHA-256 hashes, runtime identity and a source-change check. `-Mode benchmark`
or `-Mode recovery` selects one part; `-Repetitions` defaults to five.

The recorded run used .NET 10.0.12, Windows x64, and five samples after one
verified warm-up per case. All four block sizes use an 8 MiB synthetic volume,
the same names and deterministic file bytes, and the same request sizes:

- 128 files of 64–3,072 bytes, totaling 201,073 bytes. Creation, first writes,
  reads, lookups and 32-byte overwrites are measured separately.
- A 1 MiB file, transferred in sixteen 64 KiB requests, for first writes,
  overwrites and reads. File creation is excluded from the large-write timer.
- Each sample begins with a fresh volume clone and empty adapter cache.
  Fixture creation, cloning, input preparation and verification are outside
  the timer. The sequence of cases rotates between repetitions within each
  block size; block sizes run in ascending order.
- Every measured result passes the separate image checker, including full
  allocation ownership and expected file SHA-256 checks. Reads are also
  compared byte-for-byte. I/O counts must be identical across repetitions.

Fixtures are original synthetic images, not independently formatted media.
No real block device, disk image supplied by the user, or native handler is
modified. Host costs include the adapter and call wrappers. In-memory flushes
have no simulated storage latency, so elapsed time must not be presented as
device throughput. Very short read timings are particularly noise-sensitive.

## Results

Median elapsed milliseconds with no adapter cache; lower is better:

| Workload | 512 B | 1 KiB | 2 KiB | 4 KiB |
| --- | ---: | ---: | ---: | ---: |
| Create 128 empty files | 2.43 | 3.84 | 7.71 | 11.93 |
| Write 128 small files | 25.48 | 20.30 | 21.68 | 30.16 |
| Read 128 small files | 0.42 | 0.52 | 0.71 | 1.05 |
| Look up 128 names | 0.89 | 1.47 | 2.51 | 3.51 |
| Overwrite 32 bytes in each small file | 8.23 | 8.94 | 12.17 | 18.98 |
| Write 1 MiB | 309.62 | 111.52 | 52.72 | 30.05 |
| Overwrite 1 MiB | 145.18 | 53.40 | 26.37 | 15.15 |
| Read 1 MiB | 1.27 | 1.00 | 0.98 | 1.02 |

For the 1 MiB first-write workload, the deterministic block-operation counts
explain the benefit of larger blocks more reliably than host timings:

| Block size | Block reads | Bytes read, MiB | Flush calls |
| --- | ---: | ---: | ---: |
| 512 B | 2,609,822 | 1,274.33 | 4,288 |
| 1 KiB | 507,190 | 495.30 | 2,224 |
| 2 KiB | 127,878 | 249.76 | 1,200 |
| 4 KiB | 37,166 | 145.18 | 688 |

The core allocates and journals blocks individually. Allocation searches and
repeated journal-chain scans produce substantial read amplification; larger
blocks reduce that work as well as the number of per-block flush barriers.
This result supports optimizing those paths, not attributing the entire
improvement to larger transfers. At 4 KiB the bulk-write host workload took
about one tenth of the 512-byte time, but small-file creation took nearly five
times as long.

Space usage after writing the same 128 small files:

| Block size | File allocation, bytes | Tail waste, bytes | Metadata/reserved, bytes | Core write scratch |
| --- | ---: | ---: | ---: | ---: |
| 512 B | 232,960 | 31,887 | 33,792 | 2 KiB |
| 1 KiB | 263,168 | 62,095 | 34,816 | 4 KiB |
| 2 KiB | 350,208 | 149,135 | 69,632 | 8 KiB |
| 4 KiB | 524,288 | 323,215 | 139,264 | 16 KiB |

Metadata/reserved includes both roots, metadata, and unused reserved admin
slots. Scratch is the core's four caller-owned blocks, not total handler RAM.
The optional adapter cache below adds another 64 KiB of data storage plus
bookkeeping. Empty-file allocation and existing volume geometry should not be
confused with these measured nonempty-file totals.

## Caching and larger transfers with 512-byte allocation

Two experimental read-through LRU adapters share the same 64 KiB data budget:
one caches individual filesystem blocks; the other fetches aligned 32 KiB
ranges. The latter has only two cache lines and applies read-ahead to every
miss. This is a simple comparison policy, not an adaptive production cache.
Writes remain synchronous write-through calls and update cached bytes.

| Workload, 512 B blocks | No cache: reads / MiB | Block cache: reads / MiB | 32 KiB read-ahead: reads / MiB |
| --- | ---: | ---: | ---: |
| Read 128 small files | 2,517 / 1.23 | 495 / 0.24 | 990 / 30.94 |
| Look up 128 names | 2,314 / 1.13 | 17 / 0.0083 | 2 / 0.0625 |
| Read 1 MiB | 6,272 / 3.06 | 2,232 / 1.09 | 82 / 2.56 |

Read-ahead reduced sequential device requests about 76 times while preserving
512-byte allocation. Applied indiscriminately, it transferred about 25 times
as many bytes for small-file reads as the uncached adapter. A useful production
design would keep metadata cached and enable larger transfers for detected
sequential data access. The measurements do not establish 32 KiB as an optimal
transfer size.

For the 1 MiB first write, the 512-byte block cache reduced device reads from
2,609,822 to 99,728, and transferred reads from 1,274.33 to 48.70 MiB. It left
all 4,288 flush calls intact. The host time increased from 309.62 to 370.20 ms:
cache bookkeeping adds CPU work, while this memory device has negligible I/O
latency and the core still traverses and validates its metadata. Caching does
not remove the algorithmic overhead. Write coalescing was not implemented
in this initial study. The subsequent [batched-write implementation and study](batched-writes.md)
retains 512-byte blocks and combines data and journal transfers while preserving
the required undo-before-data ordering.

## Recovery and correctness

The separate recovery matrix uses 1 MiB images and ten operations per block
size: create, mkdir, overwrite, append, shrink, grow, truncate, move/rename,
delete and rmdir. It checks every write/flush cut point in each mutation and
every recovery cut point from one dirty image per operation. At each cut it
enumerates the Cartesian product of the durable value and all pending whole
versions of each block. The 65,536-combination guard was not reached.

| Block size | Mutation image checks | Interrupted-recovery image checks | Sector-prefix tears recovered | Refused |
| --- | ---: | ---: | ---: | ---: |
| 512 B | 45,008 | 9,796 | 0 | 0 |
| 1 KiB | 8,840 | 964 | 292 | 0 |
| 2 KiB | 2,300 | 492 | 654 | 0 |
| 4 KiB | 1,016 | 356 | 1,246 | 0 |

All **68,772 whole-block crash/recovery image checks** and **2,192 sampled
sector-prefix tears** passed. Counts are executions, not unique disk states.
Recovered images must match the complete before-or-after manifest and free
space count, pass the independent structural checker, mount, and remain
byte-identical after a second recovery. Successful clean mutations are also
checked against a separate expected-content model.

For larger blocks, every mutation write is interrupted after each possible
512-byte sector prefix; other pending writes are discarded. No such prefix
exists inside a 512-byte block. These cases all recovered successfully; the
harness would separately count a detected, unmountable damaged image as a
refusal, not a successful recovery. The existing reliability suite separately
tests sub-sector tears with 512-byte filesystem blocks.

This matrix does not cover arbitrary sector subsets/reordering, sector tears
during recovery, injected I/O errors at these larger block sizes, full-disk
conditions, every metadata-tree growth boundary, or real flush implementations.
It expands host coverage without qualifying larger blocks on physical Amigas.
`BlockSizeTests` retains the recovery cases and checks cache coherence across
writes, read-ahead boundaries and flushes for all four sizes.

The full repository reliability script subsequently passed **163 tests, zero
failures and zero skips**, with no source changes during that run. Its TRX and
source/binary identity report are retained at
`artifacts/block-size/validation/`. The study adds twelve test cases; production
filesystem code and native handler binaries are not changed by this work.

The recorded report is retained locally at
`artifacts/block-size/20261024-final/report.json`. It finished with status
`passed` and no source changes during the run. Assembly SHA-256 identities:

- `CopperSFS.Core.dll`: `58502F328F38044C25937A69A97833C756BB0C03BE28531A466752580FBD6D63`
- `CopperSFS.Bench.dll`: `C088D79D9FEE67F04B6CBFEB289E1CF3E87C60C802E1180924261CB9CDA7B4C5`
