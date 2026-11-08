# CopperHDF payload-cache policies — 6 November 2026

Payload bypass improves FFS 4 KiB open/read/close throughput by 2.04×, reaching 94.3% of original FFS. SFS2 improves by 1.79× and reaches 1.75× original SFS2 throughput. 64-byte workloads retain their cached request counts and transferred bytes.

This comparison runs the same C# CopperFFS/SFS2 handler HUNKs with normal caching and an explicit whole-block payload-cache bypass. Both policies are compared with unmodified FastFileSystem 40.1 and SmartFilesystem 1.279. All four read sizes run through real Kickstart DOS.

## Policy and compatibility

- Opt in per mount by setting DosEnvec `de_Control` to `0x43504301` (`Control = 1129333505`). Only the exact `copperhdf.device` name accepts it. Missing controls, unknown values, environment tables shorter than index 18 and other drivers keep normal caching.
- Whole-block FFS/SFS2 reads use the existing bounded direct-transfer path. Partial-sector/unaligned reads, metadata caches and retained traversal state keep their existing paths. Redo, transaction and dirty-write views are checked before the policy. DMA masks, bounce buffers, MaxTransfer, TD64, EOF and completed-prefix errors remain in force.
- The policy occupies the previously unused native state word at offset 1644. State/handle sizes and allocated cache buffers do not grow. All three standalone handlers are native compatible with zero managed allocation sites. No portable-core application dependency, packet ABI, on-disk format or journal policy changes.

## End-to-end throughput

Median open/read/close KiB/s. Each Copper policy has three trials per workload; the original column pools its two independent three-trial runs. All three variants use identical media and guest-probe images per filesystem. Cached/bypass Copper arms also use identical caller addresses. Ratios compare complete policy executions.

| Handler | Read size | Normal cache | Payload bypass | Bypass/cache | Original | Bypass/original |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 27.160 | 27.151 | 1.000× | 35.827 | 0.758× |
| ffs | 512 B | 209.290 | 250.124 | 1.195× | 241.962 | 1.034× |
| ffs | 4,096 B | 841.510 | 1,718.628 | 2.042× | 1,823.434 | 0.943× |
| ffs | 65,536 B | 6,670.867 | 7,446.251 | 1.116× | 11,218.697 | 0.664× |
| sfs2-csun | 64 B | 21.659 | 21.683 | 1.001× | 23.679 | 0.916× |
| sfs2-csun | 512 B | 149.116 | 168.589 | 1.131× | 163.261 | 1.033× |
| sfs2-csun | 4,096 B | 727.647 | 1,300.352 | 1.787× | 745.141 | 1.745× |
| sfs2-csun | 65,536 B | 10,074.534 | 10,073.276 | 1.000× | 18,251.130 | 0.552× |
| sfs2-trst | 64 B | 21.644 | 21.650 | 1.000× | 23.673 | 0.915× |
| sfs2-trst | 512 B | 149.033 | 168.586 | 1.131× | 163.302 | 1.032× |
| sfs2-trst | 4,096 B | 727.582 | 1,300.262 | 1.787× | 745.495 | 1.744× |
| sfs2-trst | 65,536 B | 10,261.974 | 10,066.576 | 0.981× | 18,304.710 | 0.550× |

The opt-in trades many more commands at 512 B and 4 KiB for less guest CPU copying. Small 64-byte medians are essentially unchanged. Large SFS2 reads already use the same direct-transfer path with either policy; their medians/ranges retain guest scheduling and phase variation. FFS large packets can contain short runs at pointer-table boundaries, so the policy can also affect a 64 KiB DOS read.

## Read-loop throughput and traffic

Device traffic includes read-loop through close, excluding remount/open. Partial-sector workloads retain their cached request counts and transferred bytes. Whole-block bypass deliberately exchanges additional device commands for fewer CPU copies. Timing remains specific to the synchronous CopperHDF backend.

| Handler | Read size | Cache KiB/s | Bypass KiB/s | Original KiB/s | Cache/bypass requests | Cache/bypass device KiB |
|---|---:|---:|---:|---:|---:|---:|
| ffs | 64 B | 27.341 | 27.332 | 36.044 | 2 / 2 | 64.5 / 64.5 |
| ffs | 512 B | 212.201 | 254.510 | 244.210 | 11 / 519 | 259.5 / 259.5 |
| ffs | 4,096 B | 852.406 | 1,764.689 | 1,855.474 | 44 / 284 | 1038 / 1038 |
| ffs | 65,536 B | 7,433.069 | 8,409.694 | 12,552.472 | 71 / 71 | 1278 / 1038 |
| sfs2-csun | 64 B | 21.939 | 21.963 | 23.783 | 1 / 1 | 64 / 64 |
| sfs2-csun | 512 B | 152.448 | 172.896 | 164.497 | 4 / 512 | 256 / 256 |
| sfs2-csun | 4,096 B | 747.369 | 1,369.419 | 751.711 | 16 / 256 | 1024 / 1024 |
| sfs2-csun | 65,536 B | 16,525.710 | 15,991.636 | 23,362.679 | 16 / 16 | 1024 / 1024 |
| sfs2-trst | 64 B | 21.924 | 21.947 | 23.775 | 1 / 1 | 64 / 64 |
| sfs2-trst | 512 B | 152.379 | 172.958 | 164.534 | 4 / 512 | 256 / 256 |
| sfs2-trst | 4,096 B | 747.267 | 1,366.927 | 751.816 | 16 / 256 | 1024 / 1024 |
| sfs2-trst | 65,536 B | 16,363.770 | 15,991.636 | 23,027.553 | 16 / 16 | 1024 / 1024 |

## Measurement and receipts

- Same PAL 7,093,790 Hz M68020 machine, 512 KiB Chip plus 2 MiB Fast RAM, licensed Kickstart 3.1 and immutable emulator binaries used by the preceding paired-profile run. Handlers and probe contain MC68000 code. The probe is rebuilt with mount-control support; each filesystem's arms use the same rebuilt image.
- Original formatted 16 MiB synthetic DOS1/SFS2-v4 media; 512-byte sectors, contiguous 1 MiB payload, 64 KiB MaxTransfer/cache window, native extra-cache defaults. 64-byte reads consume 64 KiB; 512-byte reads consume 256 KiB; 4 KiB and 64 KiB reads consume 1 MiB. Each trial remounts outside timing.
- Guest bus time determines throughput. Host execution time is diagnostic only. CopperHDF charges no physical seek/rotation/transfer latency. These results do not establish real A1200/Gayle or physical disk throughput.
- Both policies use byte-identical HUNKs and identical compiler/SDK metadata. The existing frozen CopperSharp68k compiler from `artifacts/ab11-final` is reused; this feature needs no code-generation changes. Filesystem inputs, metadata hashes and compile commands are frozen in `artifacts/ab12-final`. Unrelated concurrent working-tree changes are preserved.
- Every caller buffer is longword-aligned in the same Fast RAM bank. Copper SFS2-CSUN uses `0x215670`; its original-handler arm uses `0x215470`. All other arms use `0x215470`. Addresses are stable across cached/bypass runs and all three trials, and retained in the raw and combined CSV results.
- Host: Release solution build passed; 1,255 tests passed and 3 optional-media cases skipped.
- Native: 120 actual-HUNK packet replays passed on 68000/68020 across both SFS2 policies. They cover device/control scoping, small-read cache reuse, direct payload requests, metadata LRU preservation, fragmented/EOF reads, error prefixes, private-object aliases, wide positions and pending transaction images.
- CopperStart integration: 157 native copy/buffer/cache replays passed; 1 classic-SFS-only cache-summary case skipped for these SFS2 HUNKs. The existing source-consumer adapter receives the identical payload policy/initializer with its older packet API retained. Stable compiled Exec/SDK dependencies avoid unrelated concurrent Exec development; the receipts retain the overlay source and build inputs.
- Full-ROM: 12 Kickstart cases passed, covering 144 SHA-256-checked trials (72 Copper plus 72 original-handler trials). Every arm rejects writes and checks unchanged backing media. Both policies run against both original handlers at all four read sizes.
- Unavailable/not exercised: physical Amiga disks/controllers, device-latency modelling, replacement-ROM boot, and the full hardware/media matrix. No ROM, media or build artifact was committed; no package/ROM was published.

[Cached-policy A/B and trial ranges](kickstart-payload-cache-cached-ab-2026-11-06.md) · [Bypass-policy A/B and trial ranges](kickstart-payload-cache-bypass-ab-2026-11-06.md)

Raw receipts: `artifacts/kickstart-ab12-cached`, `artifacts/kickstart-ab12-bypass`, `artifacts/kickstart-ab12-policies` and `artifacts/ab12-validation`. The combined CSV retains medians and min/max values. The original binary/archive provenance is unchanged from the paired-profile report.
