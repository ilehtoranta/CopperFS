# Kickstart sequential read throughput — 5 November 2026

All 72 trials passed through a booted Kickstart 3.1 system: a native guest application calls DOS.Open/Read/Close, DOS sends packets to the loaded standalone handler, and the handler uses guest Exec and copperhdf.device. There are no Exec/DOS packet service doubles in this benchmark. Before is the original reader; after includes persistent traversal, contiguous batching and the small-read read-ahead fix.

These are end-to-end **virtual Kickstart** measurements. CopperHDF copies sectors synchronously from host storage without modelling physical seek, rotation or transfer latency in the guest clock. The numbers qualify this emulator/backend configuration, not physical Amiga disk throughput.

## Configuration and timing

- CopperMod.Amiga.Lightweight, M68020, PAL guest bus clock 7,093,790 Hz, 512 KiB Chip RAM, 2 MiB Fast RAM, no Slow RAM. MC68000 HUNK code; the 1 MiB caller buffer is allocated in Fast RAM before timing.
- Licensed Kickstart 3.1 ROM, SHA-256 `8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE`. No ROM is copied into the repository or artifacts.
- Original synthetic, fully formatted 16 MiB FFS DOS1 / SFS2 volumes, 512-byte blocks, one 1 MiB file physically contiguous at LBA 8,192–10,239. Before and after use byte-identical, read-only images. FFS retains its pointer tables; SFS2 has one extent.
- Same 64 KiB MaxTransfer and read-ahead window as the instruction benchmark; native extra-cache defaults are 16 entries for FFS and 32 for SFS2. The mount environment uses 32 DOS buffers and 128 sectors per track.
- Same workloads as before: 64-byte reads consume 64 KiB; 512-byte reads consume 256 KiB; 4 KiB and 64 KiB reads consume 1 MiB each. Compare before/after within each row.
- Three trials per workload and binary. Inhibit/uninhibit remounts the volume before each open to reset filesystem caches. Traversal and caches stay live between consecutive reads. Guest boot, handler loading and cache reset are excluded.
- Read-loop time spans consecutive real DOS.Read calls, including guest packet transport, scheduling and device dispatch. Session time additionally includes pathname lookup, DOS.Open and DOS.Close. Timings come from parallel-port markers on the guest bus clock; KiB/s = bytes / 1,024 / guest seconds. Host elapsed time is diagnostic only. Tiny marker/loop overhead is included consistently.
- Every returned byte is SHA-256 checked at the read-end marker without advancing guest time; each case asserts zero write requests and unchanged backing media. Host assertions and hashing do not contribute guest cycles. This benchmark does not measure EOF, fragmented files or writes.

## Read-loop throughput

Medians of three trials, KiB/s. Ranges below retain the observed guest scheduling/timing variation.

| Handler | Read size | Read total | Before KiB/s | After KiB/s | Speedup |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 64 KiB | 10.662 | 17.391 | 1.63× |
| ffs | 512 B | 256 KiB | 46.257 | 104.330 | 2.26× |
| ffs | 4,096 B | 1,024 KiB | 65.707 | 237.892 | 3.62× |
| ffs | 65,536 B | 1,024 KiB | 188.203 | 3,896.202 | 20.70× |
| sfs2-csun | 64 B | 64 KiB | 0.332 | 7.063 | 21.30× |
| sfs2-csun | 512 B | 256 KiB | 2.603 | 49.406 | 18.98× |
| sfs2-csun | 4,096 B | 1,024 KiB | 15.033 | 182.690 | 12.15× |
| sfs2-csun | 65,536 B | 1,024 KiB | 41.607 | 3,320.310 | 79.80× |
| sfs2-trst | 64 B | 64 KiB | 0.334 | 7.073 | 21.15× |
| sfs2-trst | 512 B | 256 KiB | 2.624 | 49.469 | 18.85× |
| sfs2-trst | 4,096 B | 1,024 KiB | 15.125 | 182.893 | 12.09× |
| sfs2-trst | 65,536 B | 1,024 KiB | 41.685 | 3,442.804 | 82.59× |

## Open/read/close throughput

| Handler | Read size | Before KiB/s | After KiB/s | Speedup |
|---|---:|---:|---:|---:|
| ffs | 64 B | 10.628 | 17.288 | 1.63× |
| ffs | 512 B | 46.069 | 103.528 | 2.25× |
| ffs | 4,096 B | 65.630 | 236.848 | 3.61× |
| ffs | 65,536 B | 187.538 | 3,633.674 | 19.38× |
| sfs2-csun | 64 B | 0.331 | 6.825 | 20.61× |
| sfs2-csun | 512 B | 2.595 | 46.555 | 17.94× |
| sfs2-csun | 4,096 B | 14.963 | 172.968 | 11.56× |
| sfs2-csun | 65,536 B | 41.083 | 1,636.447 | 39.83× |
| sfs2-trst | 64 B | 0.334 | 6.849 | 20.52× |
| sfs2-trst | 512 B | 2.616 | 46.796 | 17.89× |
| sfs2-trst | 4,096 B | 15.055 | 173.815 | 11.55× |
| sfs2-trst | 65,536 B | 41.161 | 1,730.267 | 42.04× |

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Before KiB/s range | After KiB/s range | Requests before → after | Device KiB before → after |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 10.659–10.664 | 17.362–17.392 | 3 → 4 | 192 → 256 |
| ffs | 512 B | 46.223–46.259 | 104.301–104.394 | 18 → 22 | 1,152 → 1,408 |
| ffs | 4,096 B | 65.705–65.710 | 237.844–237.929 | 2,327 → 87 | 148,928 → 5,568 |
| ffs | 65,536 B | 188.191–188.203 | 3,865.455–3,896.913 | 207 → 71 | 13,248 → 3,056 |
| sfs2-csun | 64 B | 0.332–0.332 | 7.063–7.065 | 1 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 2.603–2.603 | 49.394–49.412 | 4 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 15.032–15.033 | 182.671–182.731 | 16 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 41.605–41.610 | 3,309.464–3,321.145 | 61 → 16 | 3,904 → 1,024 |
| sfs2-trst | 64 B | 0.334–0.334 | 7.072–7.073 | 1 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 2.624–2.624 | 49.463–49.469 | 4 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 15.125–15.125 | 182.860–182.950 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 41.684–41.687 | 3,437.997–3,455.727 | 61 → 16 | 3,904 → 1,024 |

This full-ROM fixture exposes a remaining FFS small-read traffic increase: 64-byte reads use 3 → 4 requests, and 512-byte reads use 18 → 22. SFS2 small-read request counts are unchanged. The earlier instruction-only fixture had different mount/open/cache history and did not expose this FFS increase. Read-ahead remains active and the large one-request-per-packet regression is absent, but the previous no-request-regression result does not generalize to this Kickstart workload. These measurements reuse the frozen binaries without changing their cache policy.

## Binary and fixture provenance

Uses the exact frozen before/after HUNKs from the [read-ahead instruction benchmark](sequential-read-readahead-2026-11-05.md). Baseline reader revision: `6979849a5554ea357ddc23e4df71d8406736db25`. Unrelated working-tree writer allocation changes are present identically in both snapshots. No production filesystem code was changed for this Kickstart benchmark. All six reused handlers have native-compatible compiler reports with zero managed allocation sites; the guest probe also passes the allocation-site check. Its explicit Exec buffer/startup allocation occurs before timing. No filesystem read scratch or cache allocation was added.

| Handler | Variant | Bytes | SHA-256 |
|---|---|---:|---|
| ffs | before | 166,116 | `28678F1DDC46D27A2DC5147A80933CCECCB4915177AB01BB4E5DA07FF56487B2` |
| ffs | after | 169,472 | `40D11C9B0D6AF5E0340CA943BB977C64C7FD918A110A5427AD81A6B85BBC5F3B` |
| sfs2-csun | before | 209,480 | `89B568B0AF53405AD6345B2069305BB5EA16424F773232C49C09848A42C2ECE4` |
| sfs2-csun | after | 215,316 | `93649E69662225C85F243F50ECA81B02EA07F1E811508E27BA865E0139911071` |
| sfs2-trst | before | 202,596 | `44FE8CDCD4DC862391F9806ECA7DEBCEB9C137E20B85F557717E7CDBAA3ACF32` |
| sfs2-trst | after | 207,860 | `9305E5C4F022AFC56FDC482542141CEFA976A3477BBFD50ECEE0EFCC5DBA7F23` |

Before/after probe and media hashes are checked equal for each profile:

| Handler | Media SHA-256 | Probe SHA-256 |
|---|---|---|
| ffs | `CBDE4ECC79E4D23BD5CA1660029D66C595CBBAB8841E8BA0D806752D8F58D84C` | `F079EA765B63D8CF67D0335799BB6E7656942A9FEF1A8FE8AC9FBD196E7ED46F` |
| sfs2-csun | `8970134CC955B926EA29EADF3F2D896A958D60DB151D116EE1A0D73FE74FB9F8` | `FB64015E3726310E8B4B1DA46B68EF9518B5A26DBB67152399D3E73CB41889EC` |
| sfs2-trst | `8970134CC955B926EA29EADF3F2D896A958D60DB151D116EE1A0D73FE74FB9F8` | `FB64015E3726310E8B4B1DA46B68EF9518B5A26DBB67152399D3E73CB41889EC` |

## Reproduction

The optional test project stays outside the normal solution because it needs a licensed ROM and the sibling CopperScreen emulator. Standalone handler dependencies are unchanged. Fixtures source-link this repository's original synthetic formatter/checker; the probe's mount sequence follows CopperStart's MIT-licensed NativeSfsAbProbe.

```powershell
# First generate frozen handlers as described in the read-ahead benchmark report.
$env:COPPERSHARP_KICKSTART31_ROM = '<licensed Kickstart 3.1 ROM path>'
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot artifacts/sequential-readahead -ArtifactRoot artifacts/kickstart-sequential-new
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py artifacts/kickstart-sequential-new docs/kickstart-sequential-read-benchmark-2026-11-05.md
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. The reported run used separate before and after test invocations; both variants use identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,173 tests passed; 3 optional tests skipped.
- Native: six previously built, allocation-checked standalone handlers reused byte-for-byte. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
