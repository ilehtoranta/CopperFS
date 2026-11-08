# SFS2 CILica IDE A/B rerun — 8 November 2026

All **576 cold workload trials passed**, with three trials per workload: CSUN/TRST, Kickstart 3.0/3.1, stock 2 MiB Chip / 2 MiB Chip plus 8 MiB Fast, HDD/CF, original/cached/bypass, and 64 B/512 B/4 KiB/64 KiB reads. The original handler and ROMs are unmodified. IDE uses Gayle and native `scsi.device`; CopperHDF is absent. Synthetic timing is not calibrated physical A1200 performance.

Both freshly rebuilt handlers use restricted CILica with verification enabled, endian assumptions and cross-assembly IPO disabled. Both compiler compatibility reports show **zero managed allocation sites**; both current native reader suites pass **204 tests**. No product source changes were required.

Ratios below are median session throughput divided by the matching original SFS2 handler; ranges span both ROMs. Session time includes mount/open/read/close.

| Build | Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---:|---|---:|---:|---:|---:|
| sfs2-csun | 64 | cached | 1.352–1.362× | 1.360–1.370× | 1.291–1.302× | 1.299–1.300× |
| sfs2-csun | 64 | bypass | 1.352–1.362× | 1.360–1.370× | 1.291–1.302× | 1.299–1.300× |
| sfs2-csun | 512 | cached | 1.292–1.298× | 1.291–1.297× | 1.237–1.252× | 1.241–1.241× |
| sfs2-csun | 512 | bypass | 1.289–1.296× | 1.287–1.294× | 1.235–1.249× | 1.237–1.238× |
| sfs2-csun | 4096 | cached | 1.305–1.307× | 1.277–1.280× | 1.221–1.230× | 1.197–1.198× |
| sfs2-csun | 4096 | bypass | 1.184–1.186× | 1.249–1.250× | 1.246–1.256× | 1.315–1.316× |
| sfs2-csun | 65536 | cached | 1.013–1.015× | 1.013–1.015× | 1.014–1.029× | 1.020–1.020× |
| sfs2-csun | 65536 | bypass | 1.013–1.015× | 1.013–1.015× | 1.014–1.029× | 1.019–1.020× |
| sfs2-trst | 64 | cached | 1.353–1.363× | 1.357–1.368× | 1.295–1.295× | 1.296–1.298× |
| sfs2-trst | 64 | bypass | 1.353–1.363× | 1.357–1.368× | 1.295–1.295× | 1.296–1.298× |
| sfs2-trst | 512 | cached | 1.298–1.305× | 1.293–1.301× | 1.247–1.248× | 1.240–1.241× |
| sfs2-trst | 512 | bypass | 1.295–1.302× | 1.290–1.297× | 1.244–1.245× | 1.237–1.239× |
| sfs2-trst | 4096 | cached | 1.308–1.311× | 1.279–1.282× | 1.226–1.226× | 1.197–1.198× |
| sfs2-trst | 4096 | bypass | 1.187–1.190× | 1.249–1.251× | 1.252–1.253× | 1.314–1.316× |
| sfs2-trst | 65536 | cached | 1.017–1.017× | 1.015–1.017× | 1.022–1.023× | 1.019–1.019× |
| sfs2-trst | 65536 | bypass | 1.017–1.017× | 1.015–1.017× | 1.022–1.023× | 1.019–1.019× |

Host, native and full-ROM coverage are distinct. Host Release build/test logs are `artifacts/ffs-span/sfs2-full-host-{build,test}.log`. Native logs are `sfs2-full-sfs2-{csun,trst}-native-tests.log`. Full-ROM records, trial ranges, payload/ROM/media/probe hashes, memory addresses, timing parameters, CPU timelines, transport commands/bytes, physical fetches and PIO bytes are in `sfs2-full-cilica-ide/`. Source and CILica input/output hashes are in `sfs2-full-cilica-native/`. The emulator and compiler basis match the preceding FFS qualification. No ROMs, media, packages or binaries are published.

- `sfs2-csun` SHA-256: `73FB17A622915F1ECFF93D563211D4D77FA36C2ABF395A96DD33FB5D7CFD0514`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.
- `sfs2-trst` SHA-256: `F44A8B24E348F14F1DEBD345F631453B681A08B64A25C3F2502C85325ACCEB16`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.

Host verification: Release solution build succeeded; full solution tests passed **1,307**, skipped **3**. Skips are unavailable host coverage, not passes. Full-ROM unavailable attempts: **0**.
