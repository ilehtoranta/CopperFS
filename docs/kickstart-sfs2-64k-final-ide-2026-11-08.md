> Provisional candidate results: superseded by the short-read revision currently being qualified.

# SFS2 64 KiB optimization — 8 November 2026

All **576 cold workload trials passed**, with three trials per workload: CSUN/TRST, Kickstart 3.0/3.1, stock 2 MiB Chip / 2 MiB Chip plus 8 MiB Fast, HDD/CF, original/cached/bypass, and 64 B/512 B/4 KiB/64 KiB reads. The original handler and ROMs are unmodified. IDE uses Gayle and native `scsi.device`; CopperHDF is absent. Synthetic timing is not calibrated physical A1200 performance.

Both freshly rebuilt handlers use restricted CILica with verification enabled, endian assumptions and cross-assembly IPO disabled. Both compiler compatibility reports show **zero managed allocation sites**; both current native reader suites pass **204 tests**. The retained C# changes isolate directory enumeration, first-cursor initialization and no-file/unaligned fallback frames. CILica remains enabled; no new code-generation fix was needed.

Ratios below are median session throughput divided by the matching original SFS2 handler; ranges span both ROMs. Session time includes mount/open/read/close.

| Build | Read | Policy | Stock HDD | Stock CF | Fast HDD | Fast CF |
|---|---:|---|---:|---:|---:|---:|
| sfs2-csun | 64 | cached | 1.345–1.355× | 1.352–1.362× | 1.283–1.295× | 1.292–1.294× |
| sfs2-csun | 64 | bypass | 1.345–1.354× | 1.352–1.362× | 1.283–1.295× | 1.292–1.294× |
| sfs2-csun | 512 | cached | 1.328–1.335× | 1.329–1.335× | 1.279–1.294× | 1.286–1.286× |
| sfs2-csun | 512 | bypass | 1.325–1.331× | 1.325–1.332× | 1.276–1.291× | 1.283–1.283× |
| sfs2-csun | 4096 | cached | 1.317–1.320× | 1.291–1.293× | 1.235–1.245× | 1.213–1.213× |
| sfs2-csun | 4096 | bypass | 1.196–1.198× | 1.262–1.263× | 1.260–1.270× | 1.333–1.334× |
| sfs2-csun | 65536 | cached | 1.015–1.016× | 1.016–1.017× | 1.016–1.031× | 1.022–1.022× |
| sfs2-csun | 65536 | bypass | 1.015–1.016× | 1.015–1.017× | 1.016–1.031× | 1.022–1.022× |
| sfs2-trst | 64 | cached | 1.346–1.355× | 1.350–1.360× | 1.288–1.289× | 1.289–1.292× |
| sfs2-trst | 64 | bypass | 1.346–1.355× | 1.350–1.360× | 1.288–1.289× | 1.289–1.292× |
| sfs2-trst | 512 | cached | 1.333–1.340× | 1.331–1.339× | 1.290–1.291× | 1.287–1.288× |
| sfs2-trst | 512 | bypass | 1.329–1.336× | 1.328–1.335× | 1.288–1.288× | 1.283–1.286× |
| sfs2-trst | 4096 | cached | 1.320–1.323× | 1.292–1.295× | 1.242–1.243× | 1.213–1.214× |
| sfs2-trst | 4096 | bypass | 1.197–1.200× | 1.263–1.264× | 1.267–1.267× | 1.332–1.335× |
| sfs2-trst | 65536 | cached | 1.018–1.019× | 1.017–1.019× | 1.024–1.024× | 1.022–1.022× |
| sfs2-trst | 65536 | bypass | 1.018–1.019× | 1.017–1.019× | 1.024–1.024× | 1.021–1.022× |

Host, native and full-ROM coverage are distinct. Host Release build/test logs are `artifacts/ffs-span/sfs2-full-host-{build,test}.log`. Native logs are `sfs2-full-sfs2-{csun,trst}-native-tests.log`. Full-ROM records, trial ranges, payload/ROM/media/probe hashes, memory addresses, timing parameters, CPU timelines, transport commands/bytes, physical fetches and PIO bytes are in `sfs2-64k-final-ide/`. Source and CILica input/output hashes are in `sfs2-64k-steady-native/`. The emulator and compiler basis match the preceding FFS qualification. No ROMs, media, packages or binaries are published.

- `sfs2-csun` SHA-256: `6E97BB93B06A68B215C73A0A50CFE0971AE4C25049C35CD82C87FDE4A9F91031`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.
- `sfs2-trst` SHA-256: `C8BFDDD9837CE9552AB2B922F5C41DF9D2E1A3063A7DCCF06C151EA7D27613D2`; CILica `a879c85f1608c764c7ab4f52b66b2b5db1ee60b6`.
