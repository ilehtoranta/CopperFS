# Original-handler A/B: Kickstart sequential reads — 6 November 2026

All 72 trials passed through a booted Kickstart 3.1 system: a native guest application calls DOS.Open/Read/Close, DOS sends packets to the loaded standalone handler, and the handler uses guest Exec and copperhdf.device. There are no Exec/DOS packet service doubles in this benchmark. A is the original Amiga FastFileSystem or SmartFilesystem binary, loaded unmodified; B is the optimized Copper handler. This is a fresh A/B run, distinct from comparing Copper versions before and after the optimization.

These are end-to-end **virtual Kickstart** measurements. CopperHDF copies sectors synchronously from host storage without modelling physical seek, rotation or transfer latency in the guest clock. The numbers qualify this emulator/backend configuration, not physical Amiga disk throughput.

## Configuration and timing

- CopperMod.Amiga.Lightweight, M68020, PAL guest bus clock 7,093,790 Hz, 512 KiB Chip RAM, 2 MiB Fast RAM, no Slow RAM. Copper handlers and the probe use MC68000 HUNK code; the 1 MiB caller buffer is allocated in Fast RAM before timing.
- Licensed Kickstart 3.1 ROM, SHA-256 `8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE`. No ROM is copied into the repository or artifacts.
- Original synthetic, fully formatted 16 MiB FFS DOS1 / SFS2 volumes, 512-byte blocks, one 1 MiB file physically contiguous at LBA 8,192–10,239. Before and after use byte-identical, read-only images. FFS retains its pointer tables; SFS2 has one extent.
- Same 64 KiB MaxTransfer and mount environment on both arms: 32 DOS buffers and 128 sectors per track. Copper retains its 64 KiB cache window and native extra-cache defaults of 16 entries for FFS / 32 for SFS2. Copper payload policy: Cached; partial-sector and metadata caching remain enabled. Original handlers retain their own internal cache/read-ahead policies; cache memory budgets are not equalized.
- Same workloads as before: 64-byte reads consume 64 KiB; 512-byte reads consume 256 KiB; 4 KiB and 64 KiB reads consume 1 MiB each. Compare before/after within each row.
- Three trials per workload and binary. Inhibit/uninhibit remounts the volume before each open to reset filesystem caches. Traversal and caches stay live between consecutive reads. Guest boot, handler loading and cache reset are excluded.
- Read-loop time spans consecutive real DOS.Read calls, including guest packet transport, scheduling and device dispatch. Session time additionally includes pathname lookup, DOS.Open and DOS.Close. Timings come from parallel-port markers on the guest bus clock; KiB/s = bytes / 1,024 / guest seconds. Host elapsed time is diagnostic only. Tiny marker/loop overhead is included consistently.
- Every returned byte is SHA-256 checked at the read-end marker without advancing guest time; each case asserts zero write requests and unchanged backing media. Host assertions and hashing do not contribute guest cycles. This benchmark does not measure EOF, fragmented files or writes.

## Read-loop throughput

Medians of three trials, KiB/s. Ranges below retain the observed guest scheduling/timing variation. B/A below 1 means Copper is slower than the original. The same original SFS binary is independently repeated for both Copper policy builds.

| Handler | Read size | Read total | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 64 KiB | 36.045 | 27.341 | 0.76× |
| ffs | 512 B | 256 KiB | 244.247 | 212.201 | 0.87× |
| ffs | 4,096 B | 1,024 KiB | 1,855.652 | 852.406 | 0.46× |
| ffs | 65,536 B | 1,024 KiB | 12,568.415 | 7,433.069 | 0.59× |
| sfs2-csun | 64 B | 64 KiB | 23.782 | 21.939 | 0.92× |
| sfs2-csun | 512 B | 256 KiB | 164.511 | 152.448 | 0.93× |
| sfs2-csun | 4,096 B | 1,024 KiB | 751.840 | 747.369 | 0.99× |
| sfs2-csun | 65,536 B | 1,024 KiB | 23,367.564 | 16,525.710 | 0.71× |
| sfs2-trst | 64 B | 64 KiB | 23.766 | 21.924 | 0.92× |
| sfs2-trst | 512 B | 256 KiB | 164.526 | 152.379 | 0.93× |
| sfs2-trst | 4,096 B | 1,024 KiB | 751.937 | 747.267 | 0.99× |
| sfs2-trst | 65,536 B | 1,024 KiB | 23,020.983 | 16,363.770 | 0.71× |

## Open/read/close throughput

| Handler | Read size | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|
| ffs | 64 B | 35.828 | 27.160 | 0.76× |
| ffs | 512 B | 241.999 | 209.290 | 0.86× |
| ffs | 4,096 B | 1,823.606 | 841.510 | 0.46× |
| ffs | 65,536 B | 11,231.432 | 6,670.867 | 0.59× |
| sfs2-csun | 64 B | 23.679 | 21.659 | 0.91× |
| sfs2-csun | 512 B | 163.308 | 149.116 | 0.91× |
| sfs2-csun | 4,096 B | 745.321 | 727.647 | 0.98× |
| sfs2-csun | 65,536 B | 18,250.901 | 10,074.534 | 0.55× |
| sfs2-trst | 64 B | 23.666 | 21.644 | 0.91× |
| sfs2-trst | 512 B | 163.313 | 149.033 | 0.91× |
| sfs2-trst | 4,096 B | 745.567 | 727.582 | 0.98× |
| sfs2-trst | 65,536 B | 18,303.787 | 10,261.974 | 0.56× |

The original handlers are faster in every measured row. Results qualify the recorded complete binaries and payload policy in this virtual Kickstart configuration. The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend does not charge physical device latency to the guest clock.

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Original (A) KiB/s range | Copper (B) KiB/s range | Requests A → B | Device KiB A → B |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 36.032–36.059 | 27.314–27.352 | 129 → 2 | 64 → 64 |
| ffs | 512 B | 244.018–244.665 | 211.986–212.265 | 519 → 11 | 260 → 260 |
| ffs | 4,096 B | 1,854.112–1,866.653 | 851.725–852.444 | 284 → 44 | 1,038 → 1,038 |
| ffs | 65,536 B | 12,530.042–12,800.749 | 7,376.458–7,485.847 | 71 → 71 | 1,038 → 1,278 |
| sfs2-csun | 64 B | 23.744–23.787 | 21.938–21.962 | 8 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 164.469–164.528 | 152.443–152.474 | 32 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 750.707–751.954 | 746.751–747.383 | 128 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 21,988.258–23,369.068 | 15,988.117–16,529.846 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 64 B | 23.744–23.783 | 21.917–21.946 | 8 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 164.516–164.542 | 152.377–152.388 | 32 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 750.681–751.960 | 746.594–747.712 | 128 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 22,275.501–23,366.061 | 15,995.862–16,537.749 | 16 → 16 | 1,024 → 1,024 |

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
| ffs | after | 176,308 | `FD6D7CCCF26AB982EA98393BFE4630DD4FE7A85031ABBDA7B8DC8DB85A1155A0` |
| sfs2-csun | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-csun | after | 221,800 | `E4963F694D19B808ABCBF81264A248ABE3EF81A0229AF83F9264A030B4A5F42F` |
| sfs2-trst | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-trst | after | 214,900 | `A53481D004B1D848427F92AB819E0DB57B2914BFDC565195582553F51D7B0924` |

Before/after probe and media hashes are checked equal for each profile:

| Handler | Media SHA-256 | Probe SHA-256 |
|---|---|---|
| ffs | `CBDE4ECC79E4D23BD5CA1660029D66C595CBBAB8841E8BA0D806752D8F58D84C` | `1013CB2958F87980C8899177102069358D315A4FF00B18FB0A349621E5DF5884` |
| sfs2-csun | `8970134CC955B926EA29EADF3F2D896A958D60DB151D116EE1A0D73FE74FB9F8` | `40EFB51F24EEE58C2C24FF5236574B4F9D41936759885003321026A3C19D987A` |
| sfs2-trst | `8970134CC955B926EA29EADF3F2D896A958D60DB151D116EE1A0D73FE74FB9F8` | `40EFB51F24EEE58C2C24FF5236574B4F9D41936759885003321026A3C19D987A` |

## Reproduction

The optional test project stays outside the normal solution because it needs a licensed ROM and the sibling CopperScreen emulator. Standalone handler dependencies are unchanged. Fixtures source-link this repository's original synthetic formatter/checker; the probe's mount sequence follows CopperStart's MIT-licensed NativeSfsAbProbe.

```powershell
# First build/freeze the handlers identified by NativeManifest above.
$env:COPPERSHARP_KICKSTART31_ROM = '<licensed Kickstart 3.1 ROM path>'
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot 'artifacts\ab12-final' -ArtifactRoot 'artifacts\kickstart-ab12-cached-new' -TestAssembly '<frozen Kickstart test DLL>' -PayloadCachePolicy Cached -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py 'artifacts\kickstart-ab12-cached-new' 'docs\kickstart-payload-cache-cached-ab-2026-11-06.md' --date '6 November 2026'
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. Both arms use identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,255 tests passed; 3 optional tests skipped.
- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
