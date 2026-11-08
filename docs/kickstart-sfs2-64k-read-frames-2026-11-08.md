> Provisional candidate results: stock/HDD TRST throughput is under further investigation.

# SFS2 64 KiB optimization — 8 November 2026

All **576 cold workload trials passed**, with three trials per workload: CSUN/TRST, Kickstart 3.0/3.1, stock 2 MiB Chip / 2 MiB Chip plus 8 MiB Fast, HDD/CF, original/cached/bypass, and 64 B/512 B/4 KiB/64 KiB reads. The original handler and ROMs are unmodified. IDE uses Gayle and native `scsi.device`; CopperHDF is absent. Synthetic timing is not calibrated physical A1200 performance.

Both freshly rebuilt handlers use restricted CILica with verification enabled, endian assumptions and cross-assembly IPO disabled. Both compiler compatibility reports show **zero managed allocation sites**; both current native reader suites pass **204 tests**. The retained C# changes isolate directory enumeration, first-cursor initialization, no-file reads and cursor restoration; retained partial-sector reads can inline. CILica remains enabled; no new code-generation fix was needed.

Ratios below are median session throughput divided by the matching original SFS2 handler; ranges span both ROMs. Session time includes mount/open/read/close.

| Build | Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---:|---|---:|---:|---:|---:|
| sfs2-csun | 64 | cached | 1.405–1.416× | 1.419–1.429× | 1.365–1.366× | 1.368–1.369× |
| sfs2-csun | 64 | bypass | 1.405–1.416× | 1.418–1.429× | 1.365–1.366× | 1.368–1.369× |
| sfs2-csun | 512 | cached | 1.320–1.327× | 1.325–1.331× | 1.282–1.283× | 1.279–1.280× |
| sfs2-csun | 512 | bypass | 1.317–1.323× | 1.320–1.327× | 1.279–1.280× | 1.276–1.277× |
| sfs2-csun | 4096 | cached | 1.314–1.317× | 1.289–1.292× | 1.239–1.239× | 1.210–1.211× |
| sfs2-csun | 4096 | bypass | 1.192–1.193× | 1.260–1.262× | 1.264–1.265× | 1.331–1.332× |
| sfs2-csun | 65536 | cached | 1.013–1.013× | 1.015–1.017× | 1.026–1.027× | 1.022–1.022× |
| sfs2-csun | 65536 | bypass | 1.013–1.013× | 1.015–1.017× | 1.026–1.027× | 1.022–1.022× |
| sfs2-trst | 64 | cached | 1.402–1.413× | 1.421–1.433× | 1.367–1.367× | 1.369–1.372× |
| sfs2-trst | 64 | bypass | 1.402–1.413× | 1.421–1.433× | 1.367–1.367× | 1.369–1.371× |
| sfs2-trst | 512 | cached | 1.313–1.319× | 1.323–1.330× | 1.284–1.284× | 1.278–1.278× |
| sfs2-trst | 512 | bypass | 1.309–1.315× | 1.319–1.326× | 1.281–1.281× | 1.275–1.275× |
| sfs2-trst | 4096 | cached | 1.308–1.310× | 1.288–1.292× | 1.238–1.239× | 1.210–1.210× |
| sfs2-trst | 4096 | bypass | 1.188–1.190× | 1.260–1.261× | 1.265–1.266× | 1.330–1.330× |
| sfs2-trst | 65536 | cached | 1.007–1.008× | 1.016–1.019× | 1.025–1.026× | 1.022–1.022× |
| sfs2-trst | 65536 | bypass | 1.007–1.007× | 1.015–1.019× | 1.025–1.026× | 1.021–1.022× |

Host, native and full-ROM coverage are distinct. Host Release build/test logs are `artifacts/ffs-span/sfs2-full-host-{build,test}.log`. Native logs are `sfs2-full-sfs2-{csun,trst}-native-tests.log`. Full-ROM records, trial ranges, payload/ROM/media/probe hashes, memory addresses, timing parameters, CPU timelines, transport commands/bytes, physical fetches and PIO bytes are in `sfs2-64k-qualified-ide/`. Source and CILica input/output hashes are in `sfs2-64k-partial-native/`. The emulator and compiler basis match the preceding FFS qualification. No ROMs, media, packages or binaries are published.

- `sfs2-csun` SHA-256: `97462F0029CC3B82A71A0EC027D9B4C364ED4BB2FB6603807F9A304E2B660970`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.
- `sfs2-trst` SHA-256: `685FEC8A30745F1B920CBEC5537479D9A8FC455652E12CE4B4D981AED87B8762`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.
