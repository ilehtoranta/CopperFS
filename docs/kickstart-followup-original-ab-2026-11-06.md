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
| ffs | 64 B | 64 KiB | 36.044 | 25.087 | 0.70× |
| ffs | 512 B | 256 KiB | 244.275 | 167.074 | 0.68× |
| ffs | 4,096 B | 1,024 KiB | 1,855.628 | 660.445 | 0.36× |
| ffs | 65,536 B | 1,024 KiB | 12,629.161 | 7,272.259 | 0.58× |
| sfs2-csun | 64 B | 64 KiB | 23.784 | 21.799 | 0.92× |
| sfs2-csun | 512 B | 256 KiB | 164.483 | 149.064 | 0.91× |
| sfs2-csun | 4,096 B | 1,024 KiB | 751.597 | 642.903 | 0.86× |
| sfs2-csun | 65,536 B | 1,024 KiB | 23,363.806 | 16,024.445 | 0.69× |
| sfs2-trst | 64 B | 64 KiB | 23.768 | 21.802 | 0.92× |
| sfs2-trst | 512 B | 256 KiB | 164.465 | 149.015 | 0.91× |
| sfs2-trst | 4,096 B | 1,024 KiB | 750.765 | 643.176 | 0.86× |
| sfs2-trst | 65,536 B | 1,024 KiB | 23,368.316 | 15,659.311 | 0.67× |

## Open/read/close throughput

| Handler | Read size | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|
| ffs | 64 B | 35.827 | 24.934 | 0.70× |
| ffs | 512 B | 242.027 | 165.381 | 0.68× |
| ffs | 4,096 B | 1,823.583 | 653.756 | 0.36× |
| ffs | 65,536 B | 11,206.308 | 6,446.554 | 0.58× |
| sfs2-csun | 64 B | 23.682 | 21.510 | 0.91× |
| sfs2-csun | 512 B | 163.281 | 145.780 | 0.89× |
| sfs2-csun | 4,096 B | 744.976 | 626.872 | 0.84× |
| sfs2-csun | 65,536 B | 18,252.276 | 9,881.436 | 0.54× |
| sfs2-trst | 64 B | 23.668 | 21.513 | 0.91× |
| sfs2-trst | 512 B | 163.226 | 145.712 | 0.89× |
| sfs2-trst | 4,096 B | 744.413 | 627.348 | 0.84× |
| sfs2-trst | 65,536 B | 18,307.016 | 9,874.988 | 0.54× |

The original handlers are faster in every measured row. Copper retains the improvements over its own pre-optimization implementation, but does not reach original-handler throughput in this virtual Kickstart configuration. The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend does not charge physical device latency to the guest clock.

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Original (A) KiB/s range | Copper (B) KiB/s range | Requests A → B | Device KiB A → B |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 36.020–36.061 | 25.057–25.097 | 129 → 2 | 64 → 64 |
| ffs | 512 B | 244.251–244.891 | 167.014–167.103 | 519 → 11 | 260 → 260 |
| ffs | 4,096 B | 1,853.696–1,863.793 | 660.282–660.492 | 284 → 44 | 1,038 → 1,038 |
| ffs | 65,536 B | 12,536.745–12,795.788 | 7,086.454–7,282.685 | 71 → 71 | 1,038 → 1,278 |
| sfs2-csun | 64 B | 23.741–23.799 | 21.769–21.810 | 8 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 164.452–164.562 | 148.988–149.076 | 32 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 750.636–751.702 | 642.861–642.979 | 128 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 21,966.316–23,366.061 | 15,758.506–16,324.788 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 64 B | 23.740–23.797 | 21.800–21.804 | 8 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 164.428–164.496 | 148.979–149.184 | 32 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 750.382–751.829 | 642.259–643.472 | 128 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 23,365.309–23,371.323 | 15,617.227–16,294.394 | 16 → 16 | 1,024 → 1,024 |

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
| ffs | after | 172,640 | `A17981FCAA2CDDE8BCA2E8FDE63830C6B9BA23A934BE24F926E4BF2632D4AC74` |
| sfs2-csun | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-csun | after | 216,800 | `A89A731533A3367C47149283AE368DBE2F0BF181AD51C21DD10D1B0E3FF1070A` |
| sfs2-trst | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-trst | after | 211,184 | `C4494E2680753E63DD2E7DBCD95B3C31862C16F38775F6DEC6E4F15FB099B0F7` |

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
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot 'artifacts\read4-final' -ArtifactRoot 'artifacts\kickstart-read4-original-ab-new' -TestAssembly '<frozen Kickstart test DLL>' -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py 'artifacts\kickstart-read4-original-ab-new' 'docs\kickstart-followup-original-ab-2026-11-06.md' --date '6 November 2026'
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. Both arms use identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,203 tests passed; 3 optional tests skipped.
- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
