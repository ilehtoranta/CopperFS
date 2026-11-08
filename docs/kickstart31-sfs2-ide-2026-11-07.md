# A1200 Kickstart 3.1 SFS2 IDE comparison

**Mount diagnosis superseded:** the later [C# benchmark setup investigation](kickstart-sfs2-ide-mount-fix-2026-11-07.md)
shows that the flat fixture's zero partition start selected original SFS2's
whole-device geometry path. Original SFS2 mounts and reads successfully through
unmodified native `scsi.device` with a correctly described partition and matching
root byte ranges. The v1 measurements and unavailable receipts below are retained
as historical records; they do not establish a general ROM/handler incompatibility.

The C# matrix now supports an explicit A1200 Kickstart 3.1 (40.68) configuration
and an SFS2-only selector. Existing 3.0 defaults, earlier measurements, native
handlers and device timing behavior remain unchanged. The guest probe remains
C# compiled by CopperSharp68k, with no managed allocation sites.

The targeted matrix contains 24 cases and 288 scheduled cold workloads:

| Dimension | Selection |
|---|---|
| RAM | 2 MiB Chip; 2 MiB Chip + 8 MiB Fast |
| Buffer | Chip on stock; Fast on expanded |
| Device | Native ROM `scsi.device` through Gayle, CopperHDF absent |
| Drive | Synthetic HDD and CF profiles |
| Filesystem | Original SFS 1.279; Copper SFS2 CSUN/TRST, cached and bypass |
| Read size | 64 B, 512 B, 4 KiB, 64 KiB |
| Trials | Three fresh machines per workload |

The [protocol and command](kickstart-storage-matrix.md#a1200-kickstart-31-and-targeted-sfs2-comparisons)
describe the new selectors. Distinct `stock-ks31-*` and `fast-ks31-*` IDs prevent
mixing results across ROM versions. The unmodified A1200 identity is checked
independently of filenames and version headers, which also rejects A4000 40.68.

## Coverage and native input

The Release build passes with no warnings. The full Release suite reports
**1,300 passed and 3 external-media cases skipped** (18 Amiga integration,
266 FFS, 989 SFS and 27 storage-runner tests). After the final ROM-specific
geometry classification change, the rebuilt storage-runner suite reports
**28 passed**. It covers default-matrix compatibility, the complete 288-workload
selection, both-ROM selection, rejected wrong-model ROMs, licensed in-memory
decoding and preserved output records.

A separate guest-probe compilation check passes with zero managed allocation
sites for both probes. Case discovery confirms all 24 targeted cases, and the
earlier 1,188-attempt report revalidates unchanged with the updated runner. These
are host/compilation checks; full-ROM results are reported separately below.
Receipts are recorded under `artifacts/storage-ks31/`, including
`results/final-host/`, `results/latest-options/`, `probe-compilation.log` and
`report-backcompat-final.log`.

The user supplied the licensed Amiga Forever A1200 ROM from
`D:/Amiga Files/System/rom/amiga-os-310-a1200.rom`. The C# loader uses the local
adjacent key and decodes only in memory. Neither key nor decoded image is copied
into the repository. The decoded image matches the unmodified A1200 identity:

| Input | Hash |
|---|---|
| Decoded SHA-1 | `E21545723FE8374E91342617604F1B3D703094F1` |
| Decoded SHA-256 | `6D43840D4099A74170EA0F0425B6257C3891EBCAA39C4D1840075A9AB22B5707` |
| Encoded source SHA-256 | `FEDE5134F761298F9993591BE851A036718F1EA05F916F4A8BE832C4A68AB8C5` |

The first native qualification exposes the same missing geometry operation as
3.0, with a different error: unmodified 40.68 returns **IOERR_NOCMD (-3)** for
`TD_GETGEOMETRY` (22), with zero geometry sectors. Original SFS 1.279 then refuses
the mount with **DOS error 225**, before filesystem payload reads. The native
probe issues the command independently; the ROM hash and result are retained in
`artifacts/storage-ks31/geometry-diagnostic/`. Copper SFS2 reads through actual
Gayle PIO successfully on the same image and ROM. The original handler and ROM
remain unmodified.

## Full-ROM results

The complete **288-attempt matrix finished** with **192 validated Copper read
trials and 96 unavailable original-handler attempts**. Each of the four sizes
has 48 validated reads and 24 unavailable attempts, covering both RAM layouts,
both drive profiles, CSUN/TRST and both Copper policies. The 24 case receipts
comprise 16 successful read cases and eight confirmed capability failures;
they do not imply 24 successful read benchmarks. Every unavailable attempt
records geometry error -3 and DOS error 225. Original throughput and A/B ratios
therefore remain unavailable on 40.68 as well as 39.106.

Accepted outputs under `artifacts/storage-ks31/kickstart31-ide-final/` are:

- [Full four-size report](../artifacts/storage-ks31/kickstart31-ide-final/report.md)
- [Medians and trial ranges](../artifacts/storage-ks31/kickstart31-ide-final/summary.csv)
- [192 raw read trials](../artifacts/storage-ks31/kickstart31-ide-final/measurements.json)
- [96 native mount refusals](../artifacts/storage-ks31/kickstart31-ide-final/unavailable.json)
- [Coverage receipts](../artifacts/storage-ks31/kickstart31-ide-final/validation-receipts.json)

Read trials validate **111 MiB** of returned payload, buffer placement, actual
Gayle PIO and unchanged read-only media. CopperHDF is absent from IDE machines.
All 24 matching 64-byte cached/bypass trial pairs retain one 64 KiB payload
request. The read-phase command counts are:

| Read size | Total payload | Cached commands | Bypass commands |
|---:|---:|---:|---:|
| 64 B | 64 KiB | 1 | 1 |
| 512 B | 256 KiB | 4 | 512 |
| 4 KiB | 1 MiB | 16 | 256 |
| 64 KiB | 1 MiB | 16 | 16 |

The diagnostic policy bypasses whole aligned sectors for 512-byte and 4 KiB
reads, increasing commands relative to cached read-ahead. Partial-sector
64-byte reads retain caching. These existing policy differences are visible in
the results; the ROM configuration change does not alter filesystem policy.

Selected end-to-end medians in KiB/s for **4 KiB requests**, including cold
mount/open, the 1 MiB read and close, are:

| RAM | Drive | SFS2 build | Cached | Bypass |
|---|---|---|---:|---:|
| 2 MiB Chip | HDD | CSUN | 385.31 | 357.68 |
| 2 MiB Chip | HDD | TRST | 396.68 | 367.33 |
| 2 MiB Chip | CF | CSUN | 420.49 | 411.70 |
| 2 MiB Chip | CF | TRST | 419.92 | 411.61 |
| 2 MiB Chip + 8 MiB Fast | HDD | CSUN | 345.95 | 349.97 |
| 2 MiB Chip + 8 MiB Fast | HDD | TRST | 344.66 | 349.26 |
| 2 MiB Chip + 8 MiB Fast | CF | CSUN | 366.72 | 393.28 |
| 2 MiB Chip + 8 MiB Fast | CF | TRST | 366.55 | 392.94 |

These are synthetic emulated-drive measurements. AA-Gayle physical timing is
unverified; they are not measurements of a physical A1200. The full report
retains all four sizes, minima/maxima, timelines and transport/media/PIO counts.

The run uses the same frozen filesystem handlers and reference provenance as
the preceding matrix. Both Copper SFS2 compiler compatibility records contain
zero managed allocation sites. Filesystem handlers and the guest probe source
were unchanged by this configuration work, so handler rebuilding and
CopperStart native replay tests were not repeated. The new probe compilation
check is reported separately from those earlier native tests.

Portable PDB checks match 54 engine files, 11 harness files, three runner files
and the shared configuration source exactly. All 54 engine hashes match the
preceding accepted matrix. Report revalidation passes without rewriting either
the new report or the historical 1,188-attempt report. The initial qualification
run remains development evidence, excluded from these results. No ROM, key,
handler package or media is published.
