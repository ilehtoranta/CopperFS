# CopperSFS

CopperSFS is an experimental, allocation-free implementation of the classic
Amiga Smart File System on-disk format (`SFS\0`). This repository contains the
portable filesystem core and its host tests.

An additive [read-only API](docs/sfs2-read-only.md) also reads clean SFS2
(`SFS\2`, structure version 4) volumes with 64-bit file positions and 48-bit
on-disk lengths. It was checked against an original-handler image. Existing
classic APIs and native handler binaries retain their current behavior.
The explicit [SFS2 writer](docs/sfs2-writes.md) adds wide writes, resizing,
metadata mutation and recovery, with private undo and optional native redo.
The development [native SFS2 handler profile](docs/native-sfs2.md) integrates
both formats with full-width positions, mutation and mount recovery.

**Status: experimental preview.** A compiled AmigaOS `L:` handler is available
in [prereleases](https://github.com/ilehtoranta/CopperSFS/releases); its source
is not part of this repository. An emulated A500 running a licensed Kickstart
3.1 ROM has exercised native AmigaDOS startup, reads, writes, remount,
concurrent tasks and one interrupted-write recovery point. Other ROM versions,
physical hardware, independently formatted media and broader power-cut points
remain unqualified. See the [installation guide](docs/handler-install.md) and
use only a disposable volume with an independent backup.

Rebuilt development handlers incorporating the reliability corrections have
separate [native validation evidence](docs/native-reliability-20261024.md).
Those results identify exact binaries and do not update existing downloads.

## What is here

- `src/CopperSFS.Core`: read, write and recovery logic for classic `SFS\0`
  volumes. The caller supplies block I/O, big-endian guest memory and scratch
  buffers through `IAmigaFileSystemPlatform`.
- `tests/CopperSFS.Core.Tests`: tests for traversal, mutation, crash recovery,
  I/O failures, independent integrity checking, modeled random workloads and
  sparse volumes larger than 4 GiB, and clean SFS2 read-only traversal.
- `tools/CopperSFS.Check`: a read-only image checker with no dependency on the
  filesystem core, checking allocation ownership and producing file manifests.
- `tools/CopperSFS.Bench`: reproducible block-size, cache and recovery comparisons
  on synthetic host images. See the [block-size study](docs/block-size-study.md)
  and [batched-write results](docs/batched-writes.md).

Public types use the `CopperSFS` namespace. Integrators supply their own block
device and guest-memory adapter through the platform interfaces.

`WriteBatched` supports transfers up to 64 KiB through the optional
`IAmigaBlockRangeFileSystemPlatform` interface and a caller-owned buffer while
retaining 512-byte blocks. See [integration and recovery ordering](docs/batched-writes.md).
Large fragmented overwrites can [move into contiguous free space](docs/copy-on-write.md)
within the same recoverable transaction, with in-place writes as a fallback.
The SFS1 writers also [relocate 64 KiB contiguous overwrite ranges](docs/contiguous-relocation.md)
to avoid copying old data into the undo journal.

The engines do not format volumes. The classic writable API uses 32-bit
AmigaDOS file sizes and seek positions; the explicit SFS2 writer and read-only
API support wider sizes and positions. A partition above 4 GiB additionally needs a block device
with TD64 or NSD 64-bit I/O; block-device integration is outside this core.

The portable core defaults to a private `CSUN` undo journal. An optional
[native TRST writer](docs/trst-journaling.md) stages complete operations,
including file data, and preserves recovery with the original SFS handler.
Classic standalone development builds now [default to buffered TRST](docs/native-trst-group.md)
after compiled writer and original-handler qualification. Existing downloads
are unchanged. With the new default,
whole Writes are atomic and visible immediately, then become durable on a filesystem
`ACTION_FLUSH`, writer Close, Inhibit or timer commit. `-StrictWrites` retains per-Write
durability. The report records the measured throughput, bounded memory, original-handler
recovery and rejected checksum-cache experiments separately from the portable core.
The latest [allocation-transition optimization](docs/native-trst-transitions.md), informed by
the [original SFS implementation study](docs/native-trst-original-study.md), improves overwrite
throughput by another 7.65%, with append throughput effectively unchanged. TRST now reaches
127.1% of original throughput for 8 KiB append, 104.1% for 64 KiB append, and 64.1% for overwrite,
with the same 192 KiB staging cap. Crash recovery and original-handler compatibility were rerun.
Worst overwrite latency improves from 73.60 to 57.77 ms; the report records the other latency tradeoffs.
Overall throughput parity remains unmet.
The preceding [deferred metadata optimization](docs/native-trst-deferred.md) improved overwrite
throughput by 13.8%, while reducing append throughput by 1.8–2.4% at that checkpoint.
The earlier [metadata-view and record-hint optimization](docs/native-trst-searchviews.md) added
8.7–11.8% throughput with unchanged I/O and the same 192 KiB staging cap. At that checkpoint
TRST was 19.3% faster than original SFS for 8 KiB appends and 2.8% slower for 64 KiB appends. Overwrite throughput
remained substantially behind at that checkpoint.
The preceding [occupied-word scan optimization](docs/native-trst-wordscan.md) added 1.6–14.4%
throughput with unchanged I/O and staging memory.
The preceding [staged metadata and arena-copy optimization](docs/native-trst-slots.md) adds
10.6–18.9% throughput and puts 8 KiB appends about 4% ahead of original SFS in the compiled benchmark.
The earlier [MC68000 arithmetic optimization](docs/native-trst-arithmetic.md) adds 7.3–11.3% throughput.
The preceding [bitmap and extent optimizations](docs/native-trst-runs.md) add 3.8–7.5% throughput
over the preceding extent-update checkpoint, with renewed crash and original-handler qualification.
If power is lost during a
CSUN mutation, remount with CopperSFS to recover it before exposing the
volume through another SFS implementation. Recovery requires a writable device
and an intact journal. See [compatibility and recovery](docs/compatibility.md).

## Build and test

Install .NET 10 and clone the public
[CopperSharp68k](https://github.com/ilehtoranta/CopperSharp68k) repository
beside this one, or provide compatible CopperSharp SDK packages. Then run:

```sh
dotnet test CopperSFS.slnx -c Release
```

The default local project reference expects `../CopperSharp68k`. Set
`CopperSharp68kRoot` when it is elsewhere. These tests exercise host semantics;
they do not execute a compiled MC68000 handler. See
[validation](docs/validation.md) for the coverage boundary.

For a reproducible reliability report with source and host-binary checksums, run
`powershell -NoProfile -File scripts/Test-Reliability.ps1`. See
[reliability testing](docs/reliability.md) for the crash model, independent
checker, current results and remaining release gates. These host checks do not
qualify the downloadable Amiga handler.

To compare 512-, 1,024-, 2,048- and 4,096-byte blocks, run
`powershell -NoProfile -File scripts/Measure-BlockSizes.ps1`. The report includes
host timings, block I/O and flush counts, space usage and recovery evidence;
it does not measure native Amiga or physical-device throughput.

The [native batching and relocation integration](docs/native-batched-writes.md)
keeps 512-byte blocks, uses transfers up to 64 KiB, and has separate native
benchmark and cut/reboot evidence for the development handler.

## Name and provenance

CopperSFS is a new implementation and is not the original
[Smart File System C project](https://sourceforge.net/projects/smartfilesystem/).
That project's source is published under LGPLv2. This repository is
MIT-licensed; the original C source is not included. The classic format
references and independent validation sources are listed in
[provenance](docs/provenance.md).

Contributions and independent disk-image compatibility reports are welcome.
Please use disposable images when testing writes and include the exact handler,
device and system version in bug reports.
