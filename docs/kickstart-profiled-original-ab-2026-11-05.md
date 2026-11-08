# Original-handler A/B: Kickstart sequential reads — 5 November 2026

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
| ffs | 64 B | 64 KiB | 36.044 | 25.476 | 0.71× |
| ffs | 512 B | 256 KiB | 244.279 | 159.927 | 0.65× |
| ffs | 4,096 B | 1,024 KiB | 1,854.893 | 476.787 | 0.26× |
| ffs | 65,536 B | 1,024 KiB | 12,628.942 | 4,605.685 | 0.36× |
| sfs2-csun | 64 B | 64 KiB | 23.784 | 21.483 | 0.90× |
| sfs2-csun | 512 B | 256 KiB | 164.505 | 141.469 | 0.86× |
| sfs2-csun | 4,096 B | 1,024 KiB | 751.586 | 482.536 | 0.64× |
| sfs2-csun | 65,536 B | 1,024 KiB | 23,364.558 | 12,847.160 | 0.55× |
| sfs2-trst | 64 B | 64 KiB | 23.767 | 21.482 | 0.90× |
| sfs2-trst | 512 B | 256 KiB | 164.506 | 141.596 | 0.86× |
| sfs2-trst | 4,096 B | 1,024 KiB | 751.936 | 484.004 | 0.64× |
| sfs2-trst | 65,536 B | 1,024 KiB | 23,032.662 | 12,849.887 | 0.56× |

## Open/read/close throughput

| Handler | Read size | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|
| ffs | 64 B | 35.826 | 25.294 | 0.71× |
| ffs | 512 B | 242.032 | 158.076 | 0.65× |
| ffs | 4,096 B | 1,822.869 | 472.179 | 0.26× |
| ffs | 65,536 B | 11,231.432 | 4,247.580 | 0.38× |
| sfs2-csun | 64 B | 23.682 | 21.025 | 0.89× |
| sfs2-csun | 512 B | 163.291 | 136.582 | 0.84× |
| sfs2-csun | 4,096 B | 744.965 | 468.653 | 0.63× |
| sfs2-csun | 65,536 B | 18,252.276 | 7,146.200 | 0.39× |
| sfs2-trst | 64 B | 23.666 | 21.024 | 0.89× |
| sfs2-trst | 512 B | 163.271 | 136.653 | 0.84× |
| sfs2-trst | 4,096 B | 745.564 | 469.301 | 0.63× |
| sfs2-trst | 65,536 B | 18,306.555 | 7,185.859 | 0.39× |

The original handlers are faster in every measured row. Copper retains the improvements over its own pre-optimization implementation, but does not reach original-handler throughput in this virtual Kickstart configuration. The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend does not charge physical device latency to the guest clock.

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Original (A) KiB/s range | Copper (B) KiB/s range | Requests A → B | Device KiB A → B |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 36.036–36.064 | 25.439–25.491 | 129 → 4 | 64 → 256 |
| ffs | 512 B | 244.249–244.649 | 159.917–159.986 | 519 → 18 | 260 → 1,152 |
| ffs | 4,096 B | 1,854.070–1,866.868 | 476.756–476.831 | 284 → 71 | 1,038 → 4,544 |
| ffs | 65,536 B | 12,568.415–12,803.005 | 4,605.452–4,622.597 | 71 → 71 | 1,038 → 3,056 |
| sfs2-csun | 64 B | 23.741–23.798 | 21.455–21.505 | 8 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 164.473–164.562 | 141.239–141.530 | 32 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 750.546–751.702 | 482.520–483.047 | 128 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 21,966.316–23,366.061 | 12,845.569–13,054.726 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 64 B | 23.741–23.794 | 21.472–21.484 | 8 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 164.473–164.542 | 141.543–141.668 | 32 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 750.635–751.950 | 483.548–484.045 | 128 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 22,280.967–23,370.571 | 12,847.387–13,221.771 | 16 → 16 | 1,024 → 1,024 |

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
| ffs | after | 170,324 | `C771512D2E19ACEC4CBF72FE3227D898934E3EFBAACAD137D70D5A64D2E96260` |
| sfs2-csun | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-csun | after | 215,152 | `D4824732EE30BAD9349B3A94E84D4829ABA2FA91DD86F2AF16F59C88470AD2B5` |
| sfs2-trst | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-trst | after | 208,112 | `9E4F3BE4A8D7C2FD4AC50F55CBD70BD3A614393410B43C61CD4F79DFBE1AEF57` |

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
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot artifacts/sequential-readahead -ArtifactRoot artifacts/kickstart-original-ab-new -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py artifacts/kickstart-original-ab-new docs/kickstart-original-handler-ab-2026-11-05.md
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. Both arms ran in one test invocation with identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,177 tests passed; 3 optional tests skipped.
- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
