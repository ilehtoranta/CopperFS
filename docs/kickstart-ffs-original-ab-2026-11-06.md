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
| ffs | 64 B | 64 KiB | 36.051 | 27.426 | 0.76× |
| ffs | 512 B | 256 KiB | 244.190 | 185.509 | 0.76× |
| ffs | 4,096 B | 1,024 KiB | 1,856.168 | 786.832 | 0.42× |
| ffs | 65,536 B | 1,024 KiB | 12,630.259 | 7,505.338 | 0.59× |
| sfs2-csun | 64 B | 64 KiB | 23.778 | 21.941 | 0.92× |
| sfs2-csun | 512 B | 256 KiB | 164.505 | 152.509 | 0.93× |
| sfs2-csun | 4,096 B | 1,024 KiB | 751.654 | 732.977 | 0.98× |
| sfs2-csun | 65,536 B | 1,024 KiB | 23,366.061 | 15,832.696 | 0.68× |
| sfs2-trst | 64 B | 64 KiB | 23.782 | 21.869 | 0.92× |
| sfs2-trst | 512 B | 256 KiB | 164.464 | 152.081 | 0.92× |
| sfs2-trst | 4,096 B | 1,024 KiB | 751.588 | 732.702 | 0.97× |
| sfs2-trst | 65,536 B | 1,024 KiB | 23,366.061 | 16,521.575 | 0.71× |

## Open/read/close throughput

| Handler | Read size | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|
| ffs | 64 B | 35.833 | 27.247 | 0.76× |
| ffs | 512 B | 241.941 | 183.440 | 0.76× |
| ffs | 4,096 B | 1,822.485 | 777.671 | 0.43× |
| ffs | 65,536 B | 11,201.124 | 6,664.380 | 0.59× |
| sfs2-csun | 64 B | 23.676 | 21.649 | 0.91× |
| sfs2-csun | 512 B | 163.234 | 149.094 | 0.91× |
| sfs2-csun | 4,096 B | 745.031 | 713.939 | 0.96× |
| sfs2-csun | 65,536 B | 18,252.276 | 9,957.152 | 0.55× |
| sfs2-trst | 64 B | 23.680 | 21.591 | 0.91× |
| sfs2-trst | 512 B | 163.255 | 148.737 | 0.91× |
| sfs2-trst | 4,096 B | 744.966 | 712.020 | 0.96× |
| sfs2-trst | 65,536 B | 18,252.276 | 10,049.863 | 0.55× |

The original handlers are faster in every measured row. Copper retains the improvements over its own pre-optimization implementation, but does not reach original-handler throughput in this virtual Kickstart configuration. The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend does not charge physical device latency to the guest clock.

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Original (A) KiB/s range | Copper (B) KiB/s range | Requests A → B | Device KiB A → B |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 36.026–36.060 | 27.361–27.429 | 129 → 2 | 64 → 64 |
| ffs | 512 B | 244.180–244.582 | 185.261–185.523 | 519 → 11 | 260 → 260 |
| ffs | 4,096 B | 1,854.557–1,866.278 | 786.691–788.641 | 284 → 44 | 1,038 → 1,038 |
| ffs | 65,536 B | 12,540.425–12,808.875 | 7,367.182–7,514.577 | 71 → 71 | 1,038 → 1,278 |
| sfs2-csun | 64 B | 23.744–23.794 | 21.899–21.946 | 8 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 164.485–164.529 | 152.447–152.517 | 32 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 750.764–751.868 | 732.901–733.531 | 128 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 21,988.924–23,366.812 | 15,577.708–16,393.683 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 64 B | 23.744–23.797 | 21.817–21.869 | 8 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 164.457–164.511 | 152.068–152.142 | 32 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 750.707–751.780 | 731.131–732.764 | 128 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 21,967.644–23,369.068 | 16,473.242–16,523.078 | 16 → 16 | 1,024 → 1,024 |

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
| ffs | after | 173,048 | `9819BF2FF48EB2D32ABFC4B5FBD1A2AA0E693BBD6B4C6534A293FB3AE138D9D7` |
| sfs2-csun | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-csun | after | 218,736 | `984A064F9414C821A6D1D3CB411167C48BCB3FC82501C9F4FB1A8667AE5DE8DD` |
| sfs2-trst | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-trst | after | 212,696 | `9AEB2E13C73C8EB339F7160656E7E6811F4CEA92A74C63743155EA0DCA587C4A` |

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
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot 'artifacts\ffs9-frozen' -ArtifactRoot 'artifacts\kickstart-ffs9-original-ab-new' -TestAssembly '<frozen Kickstart test DLL>' -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py 'artifacts\kickstart-ffs9-original-ab-new' 'docs\kickstart-ffs-original-ab-2026-11-06.md' --date '6 November 2026'
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. Both arms use identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,230 tests passed; 3 optional tests skipped.
- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
