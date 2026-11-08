# Native sequential reads with read-ahead — 5 November 2026

This measures the actual standalone HUNK handlers with CopperStart's instruction replay bus and Copper68k 1.5.4. The numbers are emulated filesystem CPU cycles with synchronous Exec/device service doubles. They exclude real device latency, real Exec/device-task execution, Amiga bus contention and DMA timing. CPU speedup is therefore not an end-to-end disk-throughput measurement.

All 144 timed trials passed packet result, position, bounds and full-stream SHA-256 checks. Each configuration was repeated three times in alternating before/after order; cycles, instructions and I/O counts were identical across repetitions. Each trial starts with a fresh handle and empty filesystem caches; code warm-up uses separate fixtures.

## Configuration

- Same frozen compiler and compiler flags for both variants; MC68000 HUNKs executed on both CPU models.
- One 1 MiB physically contiguous file on a synthetic 4 MiB volume, with 512-byte filesystem/device blocks. FFS uses 29 pointer tables; SFS2 uses one extent.
- 64 KiB device MaxTransfer, a 64 KiB read-ahead window, DMA-safe aligned caller buffer, and the native mount defaults of 16 FFS / 32 SFS2 extra cache entries.
- 64-byte packets read 64 KiB; 512-byte packets read 256 KiB; 4 KiB and 64 KiB packets each read 1 MiB. Compare before/after within a row; packet sizes do not all process the same total byte count.
- Reads start on an already open handle. Mount, pathname lookup, open/close and writes are excluded. Direct handler packet dispatch is included; client packet transport and DOS buffering are not. The cache and traversal state remain live across consecutive packets within a trial.

The fixture installs mounted geometry and an open native file record directly. The synthetic image contains the metadata needed by the read paths; it is not bootable media or a full filesystem-format qualification.

## Guest CPU cycles

### M68000

| Handler | Packet | Read total | Before cycles | After cycles | CPU speedup |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 64 KiB | 63,144,954 | 28,023,062 | 2.25× |
| ffs | 512 B | 256 KiB | 68,500,360 | 20,915,324 | 3.28× |
| ffs | 4,096 B | 1,024 KiB | 176,563,182 | 43,880,582 | 4.02× |
| ffs | 65,536 B | 1,024 KiB | 60,346,236 | 3,227,104 | 18.70× |
| sfs2-csun | 64 B | 64 KiB | 1,989,688,126 | 122,199,666 | 16.28× |
| sfs2-csun | 512 B | 256 KiB | 1,030,777,058 | 68,321,094 | 15.09× |
| sfs2-csun | 4,096 B | 1,024 KiB | 849,013,626 | 65,865,878 | 12.89× |
| sfs2-csun | 65,536 B | 1,024 KiB | 411,717,804 | 3,684,104 | 111.76× |
| sfs2-trst | 64 B | 64 KiB | 1,989,721,426 | 122,249,694 | 16.28× |
| sfs2-trst | 512 B | 256 KiB | 1,030,795,504 | 68,346,540 | 15.08× |
| sfs2-trst | 4,096 B | 1,024 KiB | 849,044,336 | 65,940,964 | 12.88× |
| sfs2-trst | 65,536 B | 1,024 KiB | 411,743,084 | 3,687,190 | 111.67× |

### M68020

| Handler | Packet | Read total | Before cycles | After cycles | CPU speedup |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 64 KiB | 13,762,656 | 5,745,837 | 2.40× |
| ffs | 512 B | 256 KiB | 15,211,230 | 4,519,088 | 3.37× |
| ffs | 4,096 B | 1,024 KiB | 39,743,417 | 9,735,774 | 4.08× |
| ffs | 65,536 B | 1,024 KiB | 13,231,709 | 735,215 | 18.00× |
| sfs2-csun | 64 B | 64 KiB | 482,241,339 | 25,104,685 | 19.21× |
| sfs2-csun | 512 B | 256 KiB | 249,183,113 | 14,240,461 | 17.50× |
| sfs2-csun | 4,096 B | 1,024 KiB | 195,319,348 | 14,129,485 | 13.82× |
| sfs2-csun | 65,536 B | 1,024 KiB | 88,294,151 | 831,068 | 106.24× |
| sfs2-trst | 64 B | 64 KiB | 482,239,426 | 25,113,457 | 19.20× |
| sfs2-trst | 512 B | 256 KiB | 249,182,608 | 14,244,625 | 17.49× |
| sfs2-trst | 4,096 B | 1,024 KiB | 195,320,891 | 14,145,937 | 13.81× |
| sfs2-trst | 65,536 B | 1,024 KiB | 88,296,279 | 831,136 | 106.24× |

## Device requests and transferred bytes

These counts are identical on both CPU models. Metadata requests include read-ahead; data requests count any device request overlapping the file's payload. Device bytes include every requested byte, including speculative and repeated reads.

| Handler | Packet | All requests before → after | Data requests before → after | Metadata requests before → after | Device KiB before → after |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 4 → 4 | 2 → 2 | 2 → 2 | 256 → 256 |
| ffs | 512 B | 19 → 19 | 11 → 11 | 8 → 8 | 1,216 → 1,216 |
| ffs | 4,096 B | 388 → 72 | 196 → 43 | 192 → 29 | 24,832 → 4,608 |
| ffs | 65,536 B | 86 → 72 | 43 → 43 | 43 → 29 | 5,504 → 3,120 |
| sfs2-csun | 64 B | 4 → 4 | 1 → 1 | 3 → 3 | 256 → 256 |
| sfs2-csun | 512 B | 7 → 7 | 4 → 4 | 3 → 3 | 448 → 448 |
| sfs2-csun | 4,096 B | 19 → 19 | 16 → 16 | 3 → 3 | 1,216 → 1,216 |
| sfs2-csun | 65,536 B | 64 → 19 | 16 → 16 | 48 → 3 | 4,096 → 1,216 |
| sfs2-trst | 64 B | 4 → 4 | 1 → 1 | 3 → 3 | 256 → 256 |
| sfs2-trst | 512 B | 7 → 7 | 4 → 4 | 3 → 3 | 448 → 448 |
| sfs2-trst | 4,096 B | 19 → 19 | 16 → 16 | 3 → 3 | 1,216 → 1,216 |
| sfs2-trst | 65,536 B | 64 → 19 | 16 → 16 | 48 → 3 | 4,096 → 1,216 |

FFS and SFS2 transfers up to 4 KiB now use the existing physical read-ahead cache when its window is larger than the request. Larger transfers remain direct. Pending writes and redo overlays retain their existing precedence. A failed speculative refill retries only the requested suffix through the existing direct-transfer path. No cache allocation or native record growth was added. Device requests do not exceed the original baseline in any measured row.

## Comparison with the first sequential-read optimization

The original baseline binaries and their measured cycles/I/O match the preceding run exactly. The middle column below is the preceding direct-read implementation; the final column includes this read-ahead fix. I/O counts apply to both CPU models; cycle comparisons below use M68000.

| Handler | Packet | Requests: original → direct → fixed | Cycles: direct → fixed | CPU gain vs original, fixed |
|---|---:|---:|---:|---:|
| ffs | 64 B | 4 → 4 → 4 | 28,055,920 → 28,023,062 | 2.25× |
| ffs | 512 B | 19 → 520 → 19 | 11,384,820 → 20,915,324 | 3.28× |
| ffs | 4,096 B | 388 → 285 → 72 | 7,796,906 → 43,880,582 | 4.02× |
| ffs | 65,536 B | 86 → 72 → 72 | 2,605,956 → 3,227,104 | 18.70× |
| sfs2-csun | 64 B | 4 → 4 → 4 | 122,197,618 → 122,199,666 | 16.28× |
| sfs2-csun | 512 B | 7 → 515 → 7 | 58,976,072 → 68,321,094 | 15.09× |
| sfs2-csun | 4,096 B | 19 → 259 → 19 | 30,459,720 → 65,865,878 | 12.89× |
| sfs2-csun | 65,536 B | 64 → 19 → 19 | 3,673,864 → 3,684,104 | 111.76× |
| sfs2-trst | 64 B | 4 → 4 → 4 | 122,249,710 → 122,249,694 | 16.28× |
| sfs2-trst | 512 B | 7 → 515 → 7 | 58,984,130 → 68,346,540 | 15.08× |
| sfs2-trst | 4,096 B | 19 → 259 → 19 | 30,465,218 → 65,940,964 | 12.88× |
| sfs2-trst | 65,536 B | 64 → 19 → 19 | 3,676,962 → 3,687,190 | 111.67× |

Caching small reads adds CPU copies compared with the direct-only version, while eliminating its small-packet I/O regression. The fixed reader retains the traversal improvements and the large-read batching gains. Device latency and real Exec/device overhead remain outside this CPU benchmark.

## First packet and host execution diagnostics

The first-packet cycles expose initialization cost. Host time is the median of three instruction-execution-loop timings, excluding fixture setup and correctness checks but including host service doubles. It is affected by host load/JIT/GC and is not native throughput.

| CPU | Handler | Packet | First-packet cycles before → after | Host median ms before → after |
|---|---|---:|---:|---:|
| M68000 | ffs | 64 B | 89,154 → 109,842 | 723.40 → 276.96 |
| M68000 | ffs | 512 B | 93,346 → 113,546 | 783.63 → 206.17 |
| M68000 | ffs | 4,096 B | 265,446 → 229,994 | 3,570.11 → 654.18 |
| M68000 | ffs | 65,536 B | 3,321,192 → 153,594 | 1,192.59 → 43.19 |
| M68000 | sfs2-csun | 64 B | 2,048,656 → 2,044,280 | 22,826.49 → 1,277.53 |
| M68000 | sfs2-csun | 512 B | 2,110,454 → 2,047,994 | 11,717.90 → 714.47 |
| M68000 | sfs2-csun | 4,096 B | 3,392,714 → 2,166,356 | 10,361.22 → 699.76 |
| M68000 | sfs2-csun | 65,536 B | 25,548,072 → 2,020,056 | 5,452.28 → 39.86 |
| M68000 | sfs2-trst | 64 B | 2,048,712 → 2,047,264 | 22,038.76 → 1,251.56 |
| M68000 | sfs2-trst | 512 B | 2,110,510 → 2,050,962 | 11,413.89 → 693.32 |
| M68000 | sfs2-trst | 4,096 B | 3,392,854 → 2,169,562 | 9,990.65 → 681.89 |
| M68000 | sfs2-trst | 65,536 B | 25,549,652 → 2,022,992 | 5,261.94 → 38.42 |
| M68020 | ffs | 64 B | 20,730 → 25,165 | 1,127.42 → 346.69 |
| M68020 | ffs | 512 B | 21,556 → 26,125 | 592.27 → 147.54 |
| M68020 | ffs | 4,096 B | 58,691 → 51,871 | 1,439.41 → 523.40 |
| M68020 | ffs | 65,536 B | 722,971 → 35,078 | 367.47 → 20.47 |
| M68020 | sfs2-csun | 64 B | 496,914 → 495,835 | 12,971.18 → 838.06 |
| M68020 | sfs2-csun | 512 B | 510,798 → 496,785 | 6,759.33 → 476.87 |
| M68020 | sfs2-csun | 4,096 B | 783,150 → 522,777 | 5,874.62 → 428.28 |
| M68020 | sfs2-csun | 65,536 B | 5,486,308 → 490,017 | 3,049.96 → 24.45 |
| M68020 | sfs2-trst | 64 B | 496,920 → 495,915 | 12,508.88 → 816.57 |
| M68020 | sfs2-trst | 512 B | 510,804 → 496,861 | 6,585.13 → 453.29 |
| M68020 | sfs2-trst | 4,096 B | 783,163 → 522,909 | 5,672.68 → 412.42 |
| M68020 | sfs2-trst | 65,536 B | 5,486,441 → 490,085 | 2,941.11 → 24.57 |

## Provenance and reproduction

Baseline reader revision: `6979849a5554ea357ddc23e4df71d8406736db25`. Only the six modified reader/adapter source files were restored from this revision, and the new native cursor adapter was omitted. All other working-tree production sources, including the unrelated write-allocation changes, are identical in the before/after snapshots. The source-manifest comparison checks this boundary.

All six handlers compiled as native-compatible with zero managed allocation sites. Handler SHA-256:

| Handler | Variant | Bytes | SHA-256 |
|---|---|---:|---|
| ffs | before | 166,116 | `28678F1DDC46D27A2DC5147A80933CCECCB4915177AB01BB4E5DA07FF56487B2` |
| ffs | after | 169,472 | `40D11C9B0D6AF5E0340CA943BB977C64C7FD918A110A5427AD81A6B85BBC5F3B` |
| sfs2-csun | before | 209,480 | `89B568B0AF53405AD6345B2069305BB5EA16424F773232C49C09848A42C2ECE4` |
| sfs2-csun | after | 215,316 | `93649E69662225C85F243F50ECA81B02EA07F1E811508E27BA865E0139911071` |
| sfs2-trst | before | 202,596 | `44FE8CDCD4DC862391F9806ECA7DEBCEB9C137E20B85F557717E7CDBAA3ACF32` |
| sfs2-trst | after | 207,860 | `9305E5C4F022AFC56FDC482542141CEFA976A3477BBFD50ECEE0EFCC5DBA7F23` |

The ignored artifact directory contains raw trials (`measurements.json`), summary JSON/CSV, source and compiler manifests, compile commands, compatibility reports, maps and HUNKs. No ROM or media is needed for this instruction benchmark.

```powershell
./tools/Benchmark-SequentialReads.ps1 -BaselineRef 6979849a5554ea357ddc23e4df71d8406736db25 -ArtifactRoot artifacts/sequential-readahead
$env:COPPERFS_READ_BENCH_MANIFEST = (Resolve-Path artifacts/sequential-readahead/benchmark.json).Path
dotnet test tests/CopperFS.Native.Tests/CopperFS.Native.Tests.csproj -c Release --filter 'FullyQualifiedName~SequentialReadBenchmarks'
& (uv python find --offline) tools/Summarize-SequentialReads.py artifacts/sequential-readahead docs/sequential-read-readahead-2026-11-05.md --read-ahead --previous artifacts/sequential-benchmark
```

Use a fresh `-ArtifactRoot` when rebuilding; existing snapshots are not overwritten. The build script freezes the compiler and rejects SDK/compiler metadata drift between input builds. The native test project uses CopperStart's MIT-licensed replay bus by source reference; standalone handlers retain their CopperFS/CopperSharp-only build boundary.

Host semantic tests, native instruction replays and full-ROM/media tests are separate coverage. This benchmark adds no full-ROM, real storage or physical Amiga performance qualification.

## Correctness coverage for this source

- Host: Release build passed; 1,173 tests passed; 3 optional media tests skipped.
- Native: 52 actual-handler packet replays, 131 CopperStart buffer/cache probes, and 144 data-checked timed trials passed.
- Full-ROM/OS correctness: 2 SFS2 Kickstart CSUN/TRST sparse-storage and cold-reboot replays passed. These do not measure storage throughput.
- Not exercised: FFS full-ROM boot, CopperStart replacement-ROM boot, physical hardware performance.
