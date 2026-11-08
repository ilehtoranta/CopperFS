# Native sequential-read benchmark — 5 November 2026

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
| ffs | 64 B | 64 KiB | 63,144,954 | 28,055,920 | 2.25× |
| ffs | 512 B | 256 KiB | 68,500,360 | 11,384,820 | 6.02× |
| ffs | 4,096 B | 1,024 KiB | 176,563,182 | 7,796,906 | 22.65× |
| ffs | 65,536 B | 1,024 KiB | 60,346,236 | 2,605,956 | 23.16× |
| sfs2-csun | 64 B | 64 KiB | 1,989,688,126 | 122,197,618 | 16.28× |
| sfs2-csun | 512 B | 256 KiB | 1,030,777,058 | 58,976,072 | 17.48× |
| sfs2-csun | 4,096 B | 1,024 KiB | 849,013,626 | 30,459,720 | 27.87× |
| sfs2-csun | 65,536 B | 1,024 KiB | 411,717,804 | 3,673,864 | 112.07× |
| sfs2-trst | 64 B | 64 KiB | 1,989,721,426 | 122,249,710 | 16.28× |
| sfs2-trst | 512 B | 256 KiB | 1,030,795,504 | 58,984,130 | 17.48× |
| sfs2-trst | 4,096 B | 1,024 KiB | 849,044,336 | 30,465,218 | 27.87× |
| sfs2-trst | 65,536 B | 1,024 KiB | 411,743,084 | 3,676,962 | 111.98× |

### M68020

| Handler | Packet | Read total | Before cycles | After cycles | CPU speedup |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 64 KiB | 13,762,656 | 5,754,565 | 2.39× |
| ffs | 512 B | 256 KiB | 15,211,230 | 2,378,191 | 6.40× |
| ffs | 4,096 B | 1,024 KiB | 39,743,417 | 1,674,444 | 23.74× |
| ffs | 65,536 B | 1,024 KiB | 13,231,709 | 594,769 | 22.25× |
| sfs2-csun | 64 B | 64 KiB | 482,241,339 | 25,104,685 | 19.21× |
| sfs2-csun | 512 B | 256 KiB | 249,183,113 | 12,164,684 | 20.48× |
| sfs2-csun | 4,096 B | 1,024 KiB | 195,319,348 | 6,311,756 | 30.95× |
| sfs2-csun | 65,536 B | 1,024 KiB | 88,294,151 | 828,828 | 106.53× |
| sfs2-trst | 64 B | 64 KiB | 482,239,426 | 25,113,457 | 19.20× |
| sfs2-trst | 512 B | 256 KiB | 249,182,608 | 12,164,752 | 20.48× |
| sfs2-trst | 4,096 B | 1,024 KiB | 195,320,891 | 6,311,824 | 30.95× |
| sfs2-trst | 65,536 B | 1,024 KiB | 88,296,279 | 828,896 | 106.52× |

## Device requests and transferred bytes

These counts are identical on both CPU models. Metadata requests include read-ahead; data requests count any device request overlapping the file's payload. Device bytes include every requested byte, including speculative and repeated reads.

| Handler | Packet | All requests before → after | Data requests before → after | Metadata requests before → after | Device KiB before → after |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 4 → 4 | 2 → 2 | 2 → 2 | 256 → 256 |
| ffs | 512 B | 19 → 520 | 11 → 512 | 8 → 8 | 1,216 → 768 |
| ffs | 4,096 B | 388 → 285 | 196 → 256 | 192 → 29 | 24,832 → 2,880 |
| ffs | 65,536 B | 86 → 72 | 43 → 43 | 43 → 29 | 5,504 → 2,880 |
| sfs2-csun | 64 B | 4 → 4 | 1 → 1 | 3 → 3 | 256 → 256 |
| sfs2-csun | 512 B | 7 → 515 | 4 → 512 | 3 → 3 | 448 → 448 |
| sfs2-csun | 4,096 B | 19 → 259 | 16 → 256 | 3 → 3 | 1,216 → 1,216 |
| sfs2-csun | 65,536 B | 64 → 19 | 16 → 16 | 48 → 3 | 4,096 → 1,216 |
| sfs2-trst | 64 B | 4 → 4 | 1 → 1 | 3 → 3 | 256 → 256 |
| sfs2-trst | 512 B | 7 → 515 | 4 → 512 | 3 → 3 | 448 → 448 |
| sfs2-trst | 4,096 B | 19 → 259 | 16 → 256 | 3 → 3 | 1,216 → 1,216 |
| sfs2-trst | 65,536 B | 64 → 19 | 16 → 16 | 48 → 3 | 4,096 → 1,216 |

The direct-read path bypasses the existing block read-ahead window for aligned whole-block packets. This can increase device requests even while CPU work falls: small partial-block reads continue to use the cache, and large packets amortize direct I/O. Actual storage latency and Exec/device overhead must be measured before translating these CPU ratios into disk speedups.

## First packet and host execution diagnostics

The first-packet cycles expose initialization cost. Host time is the median of three instruction-execution-loop timings, excluding fixture setup and correctness checks but including host service doubles. It is affected by host load/JIT/GC and is not native throughput.

| CPU | Handler | Packet | First-packet cycles before → after | Host median ms before → after |
|---|---|---:|---:|---:|
| M68000 | ffs | 64 B | 89,154 → 109,918 | 1,384.68 → 507.24 |
| M68000 | ffs | 512 B | 93,346 → 85,202 | 1,157.54 → 235.19 |
| M68000 | ffs | 4,096 B | 265,446 → 85,200 | 3,183.27 → 110.39 |
| M68000 | ffs | 65,536 B | 3,321,192 → 152,306 | 1,167.23 → 63.86 |
| M68000 | sfs2-csun | 64 B | 2,048,656 → 2,044,278 | 30,673.45 → 1,742.27 |
| M68000 | sfs2-csun | 512 B | 2,110,454 → 2,019,418 | 17,890.23 → 1,030.74 |
| M68000 | sfs2-csun | 4,096 B | 3,392,714 → 2,019,416 | 13,922.24 → 461.72 |
| M68000 | sfs2-csun | 65,536 B | 25,548,072 → 2,019,416 | 6,941.11 → 48.44 |
| M68000 | sfs2-trst | 64 B | 2,048,712 → 2,047,280 | 26,617.12 → 1,505.06 |
| M68000 | sfs2-trst | 512 B | 2,110,510 → 2,022,366 | 15,261.09 → 779.01 |
| M68000 | sfs2-trst | 4,096 B | 3,392,854 → 2,022,364 | 10,695.98 → 319.57 |
| M68000 | sfs2-trst | 65,536 B | 25,549,652 → 2,022,364 | 5,467.30 → 39.14 |
| M68020 | ffs | 64 B | 20,730 → 25,185 | 791.78 → 309.86 |
| M68020 | ffs | 512 B | 21,556 → 19,248 | 738.07 → 100.33 |
| M68020 | ffs | 4,096 B | 58,691 → 19,248 | 1,688.00 → 69.08 |
| M68020 | ffs | 65,536 B | 722,971 → 34,798 | 540.08 → 26.99 |
| M68020 | sfs2-csun | 64 B | 496,914 → 495,835 | 16,463.37 → 1,104.75 |
| M68020 | sfs2-csun | 512 B | 510,798 → 489,877 | 8,050.08 → 476.57 |
| M68020 | sfs2-csun | 4,096 B | 783,150 → 489,877 | 6,973.52 → 250.01 |
| M68020 | sfs2-csun | 65,536 B | 5,486,308 → 489,877 | 3,482.25 → 31.87 |
| M68020 | sfs2-trst | 64 B | 496,920 → 495,915 | 12,654.48 → 832.93 |
| M68020 | sfs2-trst | 512 B | 510,804 → 489,945 | 6,682.99 → 406.16 |
| M68020 | sfs2-trst | 4,096 B | 783,163 → 489,945 | 5,713.52 → 206.57 |
| M68020 | sfs2-trst | 65,536 B | 5,486,441 → 489,945 | 2,969.55 → 24.64 |

## Provenance and reproduction

Baseline reader revision: `6979849a5554ea357ddc23e4df71d8406736db25`. Only the six modified reader/adapter source files were restored from this revision, and the new native cursor adapter was omitted. All other working-tree production sources, including the unrelated write-allocation changes, are identical in the before/after snapshots. The source-manifest comparison checks this boundary.

All six handlers compiled as native-compatible with zero managed allocation sites. Handler SHA-256:

| Handler | Variant | Bytes | SHA-256 |
|---|---|---:|---|
| ffs | before | 166,116 | `28678F1DDC46D27A2DC5147A80933CCECCB4915177AB01BB4E5DA07FF56487B2` |
| ffs | after | 169,024 | `CB855236D5CC7F779251A8D7F2F4555E77FB4D139CC4A337DFCF6A4A88F7C3D0` |
| sfs2-csun | before | 209,480 | `89B568B0AF53405AD6345B2069305BB5EA16424F773232C49C09848A42C2ECE4` |
| sfs2-csun | after | 214,596 | `54E276CB6E1C11DD9F2BBD4237720C9ED68A8010BCC6D62F4D71115311B04CCD` |
| sfs2-trst | before | 202,596 | `44FE8CDCD4DC862391F9806ECA7DEBCEB9C137E20B85F557717E7CDBAA3ACF32` |
| sfs2-trst | after | 207,308 | `6930DF9CF0FF085A3CD818B4BBE9C4ED9A07C5F00F4211C9957AEE6BBA652323` |

The ignored artifact directory contains raw trials (`measurements.json`), summary JSON/CSV, source and compiler manifests, compile commands, compatibility reports, maps and HUNKs. No ROM or media is needed for this instruction benchmark.

```powershell
./tools/Benchmark-SequentialReads.ps1 -BaselineRef 6979849a5554ea357ddc23e4df71d8406736db25
$env:COPPERFS_READ_BENCH_MANIFEST = (Resolve-Path artifacts/sequential-benchmark/benchmark.json).Path
dotnet test tests/CopperFS.Native.Tests/CopperFS.Native.Tests.csproj -c Release --filter 'FullyQualifiedName~SequentialReadBenchmarks'
& (uv python find --offline) tools/Summarize-SequentialReads.py artifacts/sequential-benchmark docs/sequential-read-benchmark-2026-11-05.md
```

Use a fresh `-ArtifactRoot` when rebuilding; existing snapshots are not overwritten. The build script freezes the compiler and rejects SDK/compiler metadata drift between input builds. The native test project uses CopperStart's MIT-licensed replay bus by source reference; standalone handlers retain their CopperFS/CopperSharp-only build boundary.

Host semantic tests, native instruction replays and full-ROM/media tests are separate coverage. This benchmark adds no full-ROM, real storage or physical Amiga performance qualification.
