# CopperNova packed metadata and root publication — 17 November 2026

CNP2 is an isolated experimental format for 512-byte sectors, 4,096–65,536 sectors per volume. The handler also recognizes CNM1; the benchmark control is the frozen item-1 HUNK, not the new binary in compatibility mode. Production CPX4, FFS and SFS handlers are not changed.

## What a root owns

All numbers are big-endian. Each physical page is one sector. The common 32-byte header holds magic (4), version/type (2 each), CRC-32C (4, logically zero for calculation), physical sector, generation, owner/range ID and 64-bit volume identity. A child pointer is an actual 16-bit physical sector. Every child is checked against its permitted pair, type, key range, volume, generation and CRC on input. The allocator never puts file data in the metadata area.

| Sector(s) | Contents |
|---|---|
| 0, 1 | Alternating root records; parity equals generation parity. |
| 2–65 | Two possible locations for each of 32 bitmap pages, each covering 2,048 sectors. |
| 66–193 | One pair per optional branch for 16 consecutive object IDs. |
| 194–1217 | Eight possible leaf pairs per branch. A leaf can cover several consecutive two-ID ranges. |
| 1218–1345 | Paired extent overflow pages: seven extra physical-start/run-length pairs per object. |
| 1346–1393 | 24 paired attribute pages, each holding up to four 79-byte comments; fetched only when needed. |
| 1394 onward | File data. |

Root byte budget: 32 header + 32 reserved; bytes 64–87 hold volume geometry, free data count, highest allocated object ID and 64-bit entry serial; 88–95 reserved; 96–159 bitmap pointers; 160–287 tree pointers; 288–415 extent overflow pointers; 416–511 attribute directory (24 entries of group-plus-one and physical pointer). Group counters and object allocation participate in the same root commit as names and file lengths. Monotonic IDs 1–1023 are stable under rename and relocation. There is no inode delete/reuse operation in this prototype.

This is a **fixed-height, range-partitioned B+tree variant**, not a general dynamically balanced B+tree. Root children cover 16 IDs and can point straight to a leaf. When a bucket overflows, an intermediate node contains up to eight direct physical leaf pointers with implicit separator keys. Leaves are repacked in bounded buckets, so one changed object can rewrite other leaves in its bucket. There is no secondary logical-page selector lookup. Packing follows stable object-ID order. Interleaved creation in unrelated directories can spread one directory’s children across buckets; the one-leaf result is not a guarantee for every small directory. The fixed pairs deliberately trade space for simple reclamation: the packed format reserves 1,394 sectors, 114 more than CNM1 (57 KiB).

Leaf budget: 32-byte header, 2-byte end offset and 6 reserved bytes leave 472 bytes for sorted records. Each object uses 24 bytes: ID (2), type/count (1 each), encoded length and parent ID (2 each), byte length and change counter (4 each), first physical extent and run length (2 each), and 4 reserved. A directory appends typed name records containing reference generation, child ID, 64-bit entry serial, folded hash, length and full ASCII name, with the existing eight-byte alignment. Names remain case insensitive and collision checked by full comparison. Files store logical runs consecutively; logical offsets are reconstructed on input. File size, extent count, truncation, comments, parent, and stable IDs survive remount.

The tested namespace still has the preceding prototype's limits: 192 bytes of directory records, ASCII names up to 168 bytes, at most eight extents per file, no links, no cross-directory move. Comments use DOS SetComment and ExamineObject; this is not arbitrary xattr support. The attribute group pool has 24 entries; emptying a published comment does not reclaim its group. ExamineObject returns an error for names longer than the packet FIB can hold. Protection/owner changes, timestamps, general directory enumeration, and sparse files are outside the implemented packet surface. The page count below applies only to the supported records and measured fixture.

## Ordering and bounds

A nonempty flush writes inactive bitmap, leaf, optional branch and overflow pages. It then issues a device barrier covering those pages and pending file data, writes the other root sector with the new generation, and issues a second barrier. Only then can flush, inhibit/unmount or shutdown succeed and quarantined file sectors be reused. A clean flush still issues one barrier. Writes may reply earlier. An error or short transfer poisons the mount; it never converts into a successful durability reply. In-place file overwrites individually retain buffered semantics; there is no transaction-wide atomic data overwrite guarantee. Devices must honor successful barriers; CRCs detect torn pages subject to checksum collision limits.

The first data sector is 1394. The initial allocation hint is 1408, the next 32-sector bitmap boundary. A wrapped search includes sectors 1394–1407. A host observer tracing the same 32 sequential 64 KiB writes measured 4,194 bitmap byte reads and 6 longword reads at hint 1394 versus 32 byte reads and 128 longword reads at 1408, with identical payload digests. These are bitmap payload reads, not disk commands or guest cycles.

A mount reads both roots in one request, checks identity and generation, selects the newest valid record, and opens the root directory. Remaining pages are checked on access. There is no replay, home installation, clean marker, or full tree scan during a clean mount. One previous valid root remains reachable throughout a commit; the alternate root is not promised as a permanent historical snapshot. Until the new root is durable no page reachable from the selected root may be overwritten.

One mount owner uses the existing 98,304-byte arena. The native path has fixed tables, no recursion, and no operation allocation. At most 64 cache slots and 48 dirty decoded metadata records; at most 16 records, eight leaf fences and 16 object probes per bucket pass, with individual page I/O yielding. Native records are decoded into the existing packet cache. This preserves packet semantics but still costs CPU. Bitmap allocation retains the 256 skipped-candidate and 128-sector run bounds; truncation retires at most one 2,048-sector bitmap window per work step. A changed group is rebuilt a leaf at a time, independently of volume size.

## Reproducing the comparison

From the isolated worktree, use the Release solution and the native compiler as described in the execution-core report. Build the new handler with `build/Build-NovaPrototype.ps1 -ArtifactRoot artifacts/packed-aligned/handler -SkipCompilerBuild`. Run `build/Benchmark-NovaPrototype.ps1 -Packed` once with `-Workload mixed` and once with `-Workload stream`. Supply the original evidence directory and ROM, the new handler, `artifacts/execution-core/final-handler/CopperNovaPrototype` as the baseline, and a different output directory for each workload. When using isolated managed build output, pass its benchmark DLL with `-BenchAssembly`. Inputs are verified against the original FFS 40.1/SFS 1.279 binaries, SFS base, ROM and workload probe hashes. All handlers receive the same DOS operations. Crash and barrier qualification here establishes CopperNova’s contract; no equivalent crash guarantee is inferred for the reference handlers.

The targeted ROM gate uses `NOVA_PACKED=1` and the qualification tool with `<HUNK> <ROM> <output>`. Building that host tool a second time with `-p:PackedTreeGate=true` adds a long comment, a split leaf and interleaved eight-extent files to the correctness guest. It does not change the filesystem HUNK or either timed guest. Both gates independently check the post-shutdown image. Use SDK 10 target overrides where the adjacent compiler checkout also declares an unavailable SDK target.

## Results

Median total and phase costs below are **emulated 68020 bus cycles** including the guest and filesystem. Two trials per handler and workload; all 16 passed guest assertions and independent image checks. Machine: Kickstart 3.1, 512 KiB Chip + 2 MiB Fast, 16 MiB volume, 512-byte sectors, 32 DOS buffers. Each workload includes mutation, explicit flush, remount and full byte verification. The order was current/CNP2/FFS/SFS, then reversed.

| Workload and handler | Mutation | Flush | Remount | Read/verify | Total | Trial range (total) |
|---|---:|---:|---:|---:|---:|---:|
| Mixed · Packed CNP2 | 1,923,790 | 230,095 | 199,360 | 1,020,065 | 3,373,310 | 3,372,840–3,373,780 |
| Mixed · Frozen CNM1 | 1,958,005 | 637,170 | 349,590 | 1,095,760 | 4,040,525 | 4,040,250–4,040,800 |
| Mixed · Kickstart FFS 40.1 | 1,683,555 | 20,460 | 108,975 | 969,780 | 2,782,770 | 2,782,180–2,783,360 |
| Mixed · SFS 1.279 | 2,478,900 | 161,210 | 218,760 | 1,059,610 | 3,918,480 | 3,918,480–3,918,480 |
| Streaming · Packed CNP2 | 2,186,435 | 324,005 | 214,110 | 364,700,960 | 367,425,510 | 367,425,370–367,425,650 |
| Streaming · Frozen CNM1 | 2,244,235 | 713,900 | 364,155 | 364,825,180 | 368,147,470 | 368,147,470–368,147,470 |
| Streaming · Kickstart FFS 40.1 | 2,110,605 | 20,340 | 114,980 | 365,196,810 | 367,442,735 | 367,442,660–367,442,810 |
| Streaming · SFS 1.279 | 4,572,765 | 174,265 | 215,025 | 364,912,070 | 369,874,125 | 369,874,120–369,874,130 |

Mixed uses 16.51% fewer cycles than frozen CNM1. Of the 667,215 cycles saved, 407,075 came from flush and 150,230 from remount. Four objects (volume root, Bench, and two files) occupy one 512-byte leaf: 40 bytes of header plus records of 56, 96, 24 and 24 bytes. Only this leaf, one bitmap and one root were published. Whole-workload backing reads/writes/flushes fell from 35/42/5 to 23/27/3.

FFS still leads mixed by 590,540 cycles. The measured gaps are 240,235 in mutation, 209,635 in flush, 90,385 in remount and 50,285 in verification. That distribution rules out closing the entire gap just by making publication free. It does not identify a particular CPU instruction as the remaining cause.

Streaming publishes one leaf, three bitmap pages and one root. Packed CNP2 uses 0.20% fewer total cycles than CNM1. The difference versus FFS is only 17,225 cycles. Both CNP2 trials were lower than both FFS trials, but readback/verification is over 99% of elapsed cycles; this is not a general sequential throughput claim. In a superseded unaligned-layout trial, mutation took 2,756,980 cycles versus 2,186,410 in the first aligned trial. Both used the same guest and data floor; only the initial search hint changed in the handler.

## Qualification

The final M68000 HUNK is 41,136 bytes (frozen current: 31,420 bytes), SHA-256 ED341072FA04E4CD9FC90565CD49383D68B3BB353BCEE683D4A0862935BFEF7D. It contains both readers. The native compiler reported zero managed allocation sites. Inspection of the actual code bytes still finds a 370-byte CRC loop with zero call instructions. Both targeted ROM gates used a 68000, 512 KiB Chip, zero Fast and a 4 KiB handler stack; both recorded a 740-byte stack peak, intact guard, zero operation allocations and two shutdown barriers. Both independently checked the committed post-shutdown data.

38 focused host cases passed. They include 27 reordered/torn sector states through root and attribute publication, 22 through a leaf split and 12 through collapse (all with changed retry), and all six flush positions under failed and short completion. The expanded ROM guest verified a 79-byte comment, an intermediate branch and an eight-extent file after remount. The 169 CopperStart native integration replays also passed. These replays cover the legacy integration boundary; the two ROM probes exercise the new handler. No physical device or general DOS compatibility qualification was performed.

The final Release solution build succeeded with one CS8602 warning in the adjacent CopperSharp compiler and zero errors. All 38 focused cases passed again from that build. The separate broader solution run started before attribute support and alignment were finalized (32 prototype cases at that point); consult the **coverage.suiteStatus** and completed assembly counts in the [source-only evidence](../benchmarks/coppernova-prototype/2026-11-17-packed-root.json) for that run. Never add the earlier prototype passes to the final 38. The evidence includes the HUNK identity, individual trial ranges, independent checks, ROM receipts and the exact original control hashes.
