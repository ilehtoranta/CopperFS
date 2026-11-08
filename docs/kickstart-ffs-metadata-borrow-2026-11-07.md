# FFS metadata cache views, 7 November 2026

The FFS read loop previously copied each validated file header and extension
from the native block cache into caller scratch. A 64 KiB packet profile charged
70,851 guest cycles to `CopyBlock` during a 1 MiB sequential read. The core now
accepts a borrowed metadata view when the native platform can retain one. The
platform fills an LRU cache image directly on a physical miss, eliminating the
second 512-byte copy. On a cache hit it returns the existing image.

The fast path requires a DOS1, DOS3 or DOS5 FFS mount, normal physical
storage, a DMA-compatible cache image, and an output buffer disjoint from that
image. Overlay, RAM, queue and unavailable-cache cases use the existing read
path. The core still validates the checksum and block type/owner, and discards
the borrowed view before data I/O can evict it. A failed physical transfer
invalidates the victim and is not retried. OFS and caller-scratch overlap keep
their existing paths. The change is C# only.

## Controlled before/after

The before and after handlers used frozen source snapshots and the same
CopperSharp68k compiler. Only the FFS core, native platform and platform
interface differ. Kickstart 3.1 A500, one 1 MiB file, three fresh mounts per
packet size, and the same zero-latency CopperHDF policy were used throughout.

| Packet | Before KiB/s | After KiB/s | Change | Read requests before/after |
| ---: | ---: | ---: | ---: | ---: |
| 64 B | 32.526 | 32.523 | -0.01% | 2 / 2 |
| 512 B | 207.484 | 208.035 | +0.27% | 11 / 11 |
| 4 KiB | 848.612 | 854.779 | +0.73% | 44 / 44 |
| 64 KiB | 8,558.819 | 9,294.641 | +8.60% | 71 / 71 |

All 24 workload trials passed. Payload hashes, device read bytes and request
counts match within each before/after pair. The 64 KiB profile removed the
70,851-cycle `CopyBlock` charge; the new borrow method costs 32,118 cycles.
The isolated result measures the FFS edit; other source work in the shared
working tree is excluded from this pair.

## Original-handler A/B from the current tree

The rebuilt current handlers were tested against original FastFileSystem 40.1
and SmartFilesystem with the same Kickstart ROM probe. Numbers are median
complete-session KiB/s. All four packet sizes passed three trials for each
variant with matching file payload hashes.

| Handler | 64 B original/Copper | 512 B | 4 KiB | 64 KiB |
| --- | ---: | ---: | ---: | ---: |
| FFS | 35.8 / 32.7 | 241.9 / 210.2 | 1,823.1 / 858.1 | 11,202.0 / 9,395.4 |
| SFS2 CSUN | 23.7 / 24.0 | 163.3 / 164.5 | 745.5 / 783.1 | 18,306.6 / 15,765.7 |
| SFS2 TRST | 23.7 / 23.9 | 163.3 / 164.9 | 745.6 / 785.6 | 18,303.3 / 16,421.8 |

The original FFS and CopperFFS fetched the same byte totals at all four sizes.
For 64 KiB they issued the same 71 device requests. The SFS2 implementations
have different request patterns, but their output hashes match. These are
synchronous emulation results, not a physical A1200 IDE throughput claim.

## Coverage and compiler status

The Release solution build passed with zero warnings. The Release solution
test run passed 1,306 tests and skipped three optional external-media cases.
Both current SFS2 variants and CopperFFS
have compatible native HUNK reports with zero managed allocation sites. The
current FFS plus either SFS2 variant passed 82 native instruction replays, with
one optional benchmark skipped. CopperStart's two enabled full-ROM native OS
replays passed.

An earlier live CopperSharp68k compiler rejected the separate SFS2
`ResolveSfs2Input` method during machine-IR verification (`Byte value ...
allows an address register`). CopperSharp68k commit `01033d827` corrected the
register constraint for narrow operands of longword guest-memory stores.
The current compiler subsequently rebuilt both SFS2 variants and CopperFFS.
Its narrow-longword-store regression test passed on 68000 and 68020; each
current-handler pairing passed 118 native instruction replays, with one
optional benchmark skipped. The controlled FFS comparison still uses its
frozen compiler for reproducibility.

Source snapshots, maps, compatibility reports, trial JSON and logs are in the
ignored `artifacts/ffs-meta-borrow` directory. No ROM or handler was published.
