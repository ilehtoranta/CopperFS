# Original-handler A/B: Kickstart sequential reads — 6 November 2026

All 72 trials passed through a booted Kickstart 3.1 system: a native guest application calls DOS.Open/Read/Close, DOS sends packets to the loaded standalone handler, and the handler uses guest Exec and copperhdf.device. There are no Exec/DOS packet service doubles in this benchmark. A is the original Amiga FastFileSystem or SmartFilesystem binary, loaded unmodified; B is the optimized Copper handler. This is a fresh A/B run, distinct from comparing Copper versions before and after the optimization.

These are end-to-end **virtual Kickstart** measurements. CopperHDF copies sectors synchronously from host storage without modelling physical seek, rotation or transfer latency in the guest clock. The numbers qualify this emulator/backend configuration, not physical Amiga disk throughput.

## Configuration and timing

- CopperMod.Amiga.Lightweight, M68020, PAL guest bus clock 7,093,790 Hz, 512 KiB Chip RAM, 2 MiB Fast RAM, no Slow RAM. Copper handlers and the probe use MC68000 HUNK code; the 1 MiB caller buffer is allocated in Fast RAM before timing.
- Licensed Kickstart 3.1 ROM, SHA-256 `8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE`. No ROM is copied into the repository or artifacts.
- Original synthetic, fully formatted 16 MiB FFS DOS1 / SFS2 volumes, 512-byte blocks, one 1 MiB file physically contiguous at LBA 8,192–10,239. Before and after use byte-identical, read-only images. FFS retains its pointer tables; SFS2 has one extent.
- Same 64 KiB MaxTransfer and mount environment on both arms: 32 DOS buffers and 128 sectors per track. Copper uses its 64 KiB read-ahead window and native extra-cache defaults of 16 entries for FFS / 32 for SFS2. Original handlers retain their own internal cache/read-ahead policies; cache memory budgets are not equalized.
- Same workloads as before: 64-byte reads consume 64 KiB; 512-byte reads consume 256 KiB; 4 KiB and 64 KiB reads consume 1 MiB each. Compare before/after within each row.
- Three trials per workload and binary. Inhibit/uninhibit remounts the volume before each open to reset filesystem caches. Traversal and caches stay live between consecutive reads. Guest boot, handler loading and cache reset are excluded.
- Read-loop time spans consecutive real DOS.Read calls, including guest packet transport, scheduling and device dispatch. Session time additionally includes pathname lookup, DOS.Open and DOS.Close. Timings come from parallel-port markers on the guest bus clock; KiB/s = bytes / 1,024 / guest seconds. Host elapsed time is diagnostic only. Tiny marker/loop overhead is included consistently.
- Every returned byte is SHA-256 checked at the read-end marker without advancing guest time; each case asserts zero write requests and unchanged backing media. Host assertions and hashing do not contribute guest cycles. This benchmark does not measure EOF, fragmented files or writes.

## Read-loop throughput

Medians of three trials, KiB/s. Ranges below retain the observed guest scheduling/timing variation. B/A below 1 means Copper is slower than the original. The same original SFS binary is independently repeated for both Copper policy builds.

| Handler | Read size | Read total | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 64 KiB | 36.044 | 27.005 | 0.75× |
| ffs | 512 B | 256 KiB | 244.253 | 178.972 | 0.73× |
| ffs | 4,096 B | 1,024 KiB | 1,857.260 | 723.099 | 0.39× |
| ffs | 65,536 B | 1,024 KiB | 12,659.755 | 7,343.201 | 0.58× |
| sfs2-csun | 64 B | 64 KiB | 23.773 | 21.956 | 0.92× |
| sfs2-csun | 512 B | 256 KiB | 164.549 | 151.216 | 0.92× |
| sfs2-csun | 4,096 B | 1,024 KiB | 750.775 | 685.333 | 0.91× |
| sfs2-csun | 65,536 B | 1,024 KiB | 23,366.061 | 15,869.360 | 0.68× |
| sfs2-trst | 64 B | 64 KiB | 23.796 | 21.919 | 0.92× |
| sfs2-trst | 512 B | 256 KiB | 164.453 | 151.124 | 0.92× |
| sfs2-trst | 4,096 B | 1,024 KiB | 751.591 | 685.056 | 0.91× |
| sfs2-trst | 65,536 B | 1,024 KiB | 23,362.303 | 16,405.531 | 0.70× |

## Open/read/close throughput

| Handler | Read size | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|
| ffs | 64 B | 35.827 | 26.827 | 0.75× |
| ffs | 512 B | 242.005 | 177.006 | 0.73× |
| ffs | 4,096 B | 1,823.075 | 714.919 | 0.39× |
| ffs | 65,536 B | 11,304.493 | 6,604.935 | 0.58× |
| sfs2-csun | 64 B | 23.667 | 21.668 | 0.92× |
| sfs2-csun | 512 B | 163.346 | 147.906 | 0.91× |
| sfs2-csun | 4,096 B | 744.134 | 667.813 | 0.90× |
| sfs2-csun | 65,536 B | 18,303.787 | 10,016.189 | 0.55× |
| sfs2-trst | 64 B | 23.693 | 21.640 | 0.91× |
| sfs2-trst | 512 B | 163.240 | 147.822 | 0.91× |
| sfs2-trst | 4,096 B | 744.970 | 667.621 | 0.90× |
| sfs2-trst | 65,536 B | 18,252.276 | 10,020.611 | 0.55× |

The original handlers are faster in every measured row. Copper retains the improvements over its own pre-optimization implementation, but does not reach original-handler throughput in this virtual Kickstart configuration. The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend does not charge physical device latency to the guest clock.

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Original (A) KiB/s range | Copper (B) KiB/s range | Requests A → B | Device KiB A → B |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 36.029–36.072 | 26.975–27.009 | 129 → 2 | 64 → 64 |
| ffs | 512 B | 244.178–244.601 | 178.852–178.972 | 519 → 11 | 260 → 260 |
| ffs | 4,096 B | 1,854.624–1,866.422 | 722.789–723.370 | 284 → 44 | 1,038 → 1,038 |
| ffs | 65,536 B | 12,630.479–12,810.004 | 7,180.176–7,344.834 | 71 → 71 | 1,038 → 1,278 |
| sfs2-csun | 64 B | 23.741–23.785 | 21.922–21.970 | 8 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 164.519–164.569 | 151.185–151.270 | 32 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 750.384–751.938 | 684.350–685.583 | 128 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 23,362.303–23,369.819 | 15,709.771–16,449.741 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 64 B | 23.733–23.801 | 21.916–21.951 | 8 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 164.442–164.521 | 151.090–151.190 | 32 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 750.865–751.863 | 684.511–685.666 | 128 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 21,964.323–23,366.061 | 15,918.046–16,447.506 | 16 → 16 | 1,024 → 1,024 |

## Binary and fixture provenance

A loads the supplied original handler binaries unmodified. Versions are extracted from their binary identifiers. The original SmartFilesystem runs on the SFS2 version-4 image in this test, with the complete returned payload verified. No reference binary, ROM or media is committed.

| Original identifier | Bytes | SHA-256 |
|---|---:|---|
| $VER: fs 40.1 (15.2.93) | 24,588 | `9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773` |
| SmartFilesystem 1.279 (23.12.2007) | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

Local reference source receipts:

- FFS: `Workbench v3.1 rev 40.42 (1994)(Commodore)(M10)(Disk 1 of 6)(Install)[!].zip`, member `Workbench v3.1 rev 40.42 (1994)(Commodore)(M10)(Disk 1 of 6)(Install)[!].adf`, file `L/FastFileSystem`. Archive SHA-256 `E320DBBCB2B8E34DA7D3E37A2953C623C26755191A74C2512169B2E6F81F2E78`; ADF SHA-256 `8F54E735925D733719A321A0DDABD8FD6D1C1C3D3C0A516ED6B77DEE89AC9E1E`. Extraction follows the original file's pointer tables and checks each traversed metadata checksum.
- SFS2: `SFS.lha`, member `Smartfilesystem/AmigaOS3.x/L/SmartFilesystem`. Archive SHA-256 `E6C4C3AEE7BAC96C7E6EDDED162FDA8B5F9D9ABF9680112B5028C5CF96B47D92` matches the pre-existing original-handler reference archive pin.
- The ignored `reference-provenance.json` retains these receipts.

B uses the frozen Copper HUNKs named by `NativeManifest` and the hash table below. The [read-ahead instruction benchmark](sequential-read-readahead-2026-11-05.md) documents the preceding reader work; the hashes here identify this run's tested binaries. The three Copper binaries have native-compatible reports with zero managed allocation sites. No allocation behavior is claimed for the original handlers. The benchmark does not modify the filesystem implementation. The same guest probe is used on both arms; its explicit Exec buffer/startup allocation occurs before timing.

Raw JSON retains the historical variant labels: `before` = original A; `after` = Copper B.

| Handler | Variant | Bytes | SHA-256 |
|---|---|---:|---|
| ffs | before | 24,588 | `9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773` |
| ffs | after | 172,688 | `346FE56FCB2BC3D14E5F547277DB7141A899BDFFAC26FC1B8CF54CA245D7AAE2` |
| sfs2-csun | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-csun | after | 218,340 | `86D14EB2316A41EE2663CD1954D50F46D607AE4AC6CD465D1228EAF99AFFE17B` |
| sfs2-trst | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-trst | after | 212,128 | `2E82CA466AFA022096D71426697B19DE2902BCF9F9805F9C5BD91E2CFD41C0A5` |

Before/after probe and media hashes are checked equal for each profile:

| Handler | Media SHA-256 | Probe SHA-256 |
|---|---|---|
| ffs | `CBDE4ECC79E4D23BD5CA1660029D66C595CBBAB8841E8BA0D806752D8F58D84C` | `1FFEE61773B91AF69333FD0FA32DD4502D4F2A75D64D3AF1BBF7BB674D8FC8A5` |
| sfs2-csun | `8970134CC955B926EA29EADF3F2D896A958D60DB151D116EE1A0D73FE74FB9F8` | `BE6977F4C32C1A5B23E9C5C54D9D1CEB8A5AFEC081D8C92BFACC891A54BC8933` |
| sfs2-trst | `8970134CC955B926EA29EADF3F2D896A958D60DB151D116EE1A0D73FE74FB9F8` | `BE6977F4C32C1A5B23E9C5C54D9D1CEB8A5AFEC081D8C92BFACC891A54BC8933` |

## Reproduction

The optional test project stays outside the normal solution because it needs a licensed ROM and the sibling CopperScreen emulator. Standalone handler dependencies are unchanged. Fixtures source-link this repository's original synthetic formatter/checker; the probe's mount sequence follows CopperStart's MIT-licensed NativeSfsAbProbe.

```powershell
# First build/freeze the handlers identified by NativeManifest above.
$env:COPPERSHARP_KICKSTART31_ROM = '<licensed Kickstart 3.1 ROM path>'
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot 'artifacts\read8-frozen' -ArtifactRoot 'artifacts\kickstart-read8-original-ab-new' -TestAssembly '<frozen Kickstart test DLL>' -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py 'artifacts\kickstart-read8-original-ab-new' 'docs\kickstart-copy-original-ab-2026-11-06.md' --date '6 November 2026'
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. Both arms use identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,229 tests passed; 3 optional tests skipped.
- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
