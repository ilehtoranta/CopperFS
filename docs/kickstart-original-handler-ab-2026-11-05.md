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
| ffs | 64 B | 64 KiB | 36.053 | 17.389 | 0.48× |
| ffs | 512 B | 256 KiB | 244.152 | 104.330 | 0.43× |
| ffs | 4,096 B | 1,024 KiB | 1,854.515 | 237.899 | 0.13× |
| ffs | 65,536 B | 1,024 KiB | 12,568.415 | 3,895.868 | 0.31× |
| sfs2-csun | 64 B | 64 KiB | 23.783 | 7.063 | 0.30× |
| sfs2-csun | 512 B | 256 KiB | 164.510 | 49.409 | 0.30× |
| sfs2-csun | 4,096 B | 1,024 KiB | 751.840 | 182.688 | 0.24× |
| sfs2-csun | 65,536 B | 1,024 KiB | 23,369.068 | 3,318.111 | 0.14× |
| sfs2-trst | 64 B | 64 KiB | 23.782 | 7.073 | 0.30× |
| sfs2-trst | 512 B | 256 KiB | 164.511 | 49.467 | 0.30× |
| sfs2-trst | 4,096 B | 1,024 KiB | 751.840 | 182.865 | 0.24× |
| sfs2-trst | 65,536 B | 1,024 KiB | 23,367.564 | 3,442.902 | 0.15× |

## Open/read/close throughput

| Handler | Read size | Original (A) KiB/s | Copper (B) KiB/s | B/A ratio |
|---|---:|---:|---:|---:|
| ffs | 64 B | 35.835 | 17.285 | 0.48× |
| ffs | 512 B | 241.903 | 103.527 | 0.43× |
| ffs | 4,096 B | 1,822.503 | 236.855 | 0.13× |
| ffs | 65,536 B | 11,231.606 | 3,633.347 | 0.32× |
| sfs2-csun | 64 B | 23.677 | 6.825 | 0.29× |
| sfs2-csun | 512 B | 163.307 | 46.558 | 0.29× |
| sfs2-csun | 4,096 B | 745.311 | 172.938 | 0.23× |
| sfs2-csun | 65,536 B | 18,252.276 | 1,636.019 | 0.09× |
| sfs2-trst | 64 B | 23.679 | 6.850 | 0.29× |
| sfs2-trst | 512 B | 163.308 | 46.809 | 0.29× |
| sfs2-trst | 4,096 B | 745.321 | 173.805 | 0.23× |
| sfs2-trst | 65,536 B | 18,250.901 | 1,730.588 | 0.09× |

The original handlers are faster in every measured row. Copper retains the improvements over its own pre-optimization implementation, but does not reach original-handler throughput in this virtual Kickstart configuration. The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend does not charge physical device latency to the guest clock.

## Read-loop trial ranges and device traffic

Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.

| Handler | Read size | Original (A) KiB/s range | Copper (B) KiB/s range | Requests A → B | Device KiB A → B |
|---|---:|---:|---:|---:|---:|
| ffs | 64 B | 36.035–36.064 | 17.362–17.393 | 129 → 4 | 64 → 256 |
| ffs | 512 B | 244.010–244.669 | 104.300–104.359 | 519 → 22 | 260 → 1,408 |
| ffs | 4,096 B | 1,854.136–1,866.643 | 237.833–237.908 | 284 → 87 | 1,038 → 5,568 |
| ffs | 65,536 B | 12,532.203–12,810.908 | 3,851.764–3,896.662 | 71 → 71 | 1,038 → 3,056 |
| sfs2-csun | 64 B | 23.744–23.786 | 7.063–7.064 | 8 → 1 | 64 → 64 |
| sfs2-csun | 512 B | 164.485–164.516 | 49.397–49.413 | 32 → 4 | 256 → 256 |
| sfs2-csun | 4,096 B | 750.707–751.943 | 182.684–182.736 | 128 → 16 | 1,024 → 1,024 |
| sfs2-csun | 65,536 B | 22,000.245–23,369.068 | 3,309.765–3,322.178 | 16 → 16 | 1,024 → 1,024 |
| sfs2-trst | 64 B | 23.744–23.787 | 7.072–7.073 | 8 → 1 | 64 → 64 |
| sfs2-trst | 512 B | 164.469–164.528 | 49.464–49.474 | 32 → 4 | 256 → 256 |
| sfs2-trst | 4,096 B | 750.707–751.954 | 182.861–182.943 | 128 → 16 | 1,024 → 1,024 |
| sfs2-trst | 65,536 B | 21,988.258–23,369.068 | 3,435.331–3,457.454 | 16 → 16 | 1,024 → 1,024 |

## Binary and fixture provenance

A loads the supplied original handler binaries unmodified. Versions are extracted from their binary identifiers. The original SmartFilesystem runs on the SFS2 version-4 image in this test, with the complete returned payload verified. No reference binary, ROM or media is committed.

| Original identifier | Bytes | SHA-256 |
|---|---:|---|
| $VER: fs 40.1 (15.2.93) | 24,588 | `9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773` |
| SmartFilesystem 1.279 (23.12.2007) AmigaOS m68k | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

Local reference source receipts:

- FFS: `Workbench v3.1 rev 40.42 (1994)(Commodore)(M10)(Disk 1 of 6)(Install)[!].zip`, member `Workbench v3.1 rev 40.42 (1994)(Commodore)(M10)(Disk 1 of 6)(Install)[!].adf`, file `L/FastFileSystem`. Archive SHA-256 `E320DBBCB2B8E34DA7D3E37A2953C623C26755191A74C2512169B2E6F81F2E78`; ADF SHA-256 `8F54E735925D733719A321A0DDABD8FD6D1C1C3D3C0A516ED6B77DEE89AC9E1E`. Extraction follows the original file's pointer tables and checks each traversed metadata checksum.
- SFS2: `SFS.lha`, member `Smartfilesystem/AmigaOS3.x/L/SmartFilesystem`. Archive SHA-256 `E6C4C3AEE7BAC96C7E6EDDED162FDA8B5F9D9ABF9680112B5028C5CF96B47D92` matches the pre-existing original-handler reference archive pin.
- The ignored `reference-provenance.json` retains these receipts.

B reuses the exact optimized HUNKs from the [read-ahead instruction benchmark](sequential-read-readahead-2026-11-05.md). The three Copper binaries have native-compatible reports with zero managed allocation sites. No allocation behavior is claimed for the original handlers. No production filesystem code was changed for this A/B run. The same guest probe is used on both arms; its explicit Exec buffer/startup allocation occurs before timing.

Raw JSON retains the historical variant labels: `before` = original A; `after` = Copper B.

| Handler | Variant | Bytes | SHA-256 |
|---|---|---:|---|
| ffs | before | 24,588 | `9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773` |
| ffs | after | 169,472 | `40D11C9B0D6AF5E0340CA943BB977C64C7FD918A110A5427AD81A6B85BBC5F3B` |
| sfs2-csun | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
| sfs2-csun | after | 215,316 | `93649E69662225C85F243F50ECA81B02EA07F1E811508E27BA865E0139911071` |
| sfs2-trst | before | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
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
./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot artifacts/sequential-readahead -ArtifactRoot artifacts/kickstart-original-ab-new -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'
& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py artifacts/kickstart-original-ab-new docs/kickstart-original-handler-ab-2026-11-05.md
```

Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. `measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. `environment.json` records the host SDK and Copper assembly hashes. Both arms ran in one test invocation with identical emulator and guest-probe builds. Only guest-clock throughput is compared; host execution overlapped and is not a performance result.

## Coverage

- Host: Release solution build passed; 1,173 tests passed; 3 optional tests skipped.
- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.
- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.
- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.
