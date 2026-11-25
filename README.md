# CopperFS

[Filesystem reference for CopperFFS, CopperSFS and CopperNova](https://ilehtoranta.github.io/CopperFS/)

CopperFS is the umbrella project for CopperFFS (OFS/FFS), CopperSFS
(classic SFS/SFS2), and CopperNova (CNFS). It also hosts the shared native
handler sources and CopperTrim.

CopperNova is the new Amiga filesystem under development here. Its official
abbreviation is **CNFS**. **CPX4** identifies its experimental version 4
on-disk format. The standalone handler for that format is `CopperCPX4`.
See the [CopperNova design](docs/new-filesystem-design.md).

Use AmigaDOS `Version <handler-file> FILE FULL` to read the embedded version of
a standalone CopperFFS, CopperSFS or CNP2 handler. The CNP2 build produces
`CopperNovaPrototype` and identifies itself as `CopperNova 1.0`.

| Component | Location |
| --- | --- |
| OFS/FFS core | `src/CopperFFS.Core` |
| Classic SFS and SFS2 cores | `src/CopperSFS.Core` |
| CopperNova experimental CPX4 core | `src/CopperCPX.Core` |
| Standalone Amiga handler project and shared sources | `src/CopperFS.Amiga` |
| CopperTrim and compatibility command | `tools/CopperSFS.Trim` |
| Managed filesystem tests | `tests/CopperFFS.Core.Tests`, `tests/CopperSFS.Core.Tests` |
| Native build tools | `build/Build-Handler.ps1`, `build/Build-Trim.ps1` |

CopperFFS reads, writes and quick-formats DOS0–DOS5 on one mount, including
international names and directory caches; see [classic variant writes](docs/classic-variant-writes.md).
Manual TRIM remains separately qualified for DOS0/DOS1.
New OFS/FFS data prefers adjacent blocks, then free runs within the remainder
of the current track (a pointer-table-sized window when track geometry is
unknown), without searching additional bitmap pages for contiguity. Fragmented
space retains the first-free fallback. Disk formats and sector I/O are unchanged.
CopperSFS supports classic SFS and SFS2 with their existing CSUN/TRST policies.
CopperTrim uses the same version-1 packet for DOS0, DOS1, classic SFS and SFS2.
The CopperSFSTrim command remains a byte-identical compatibility alias.

## Build and test

Install .NET 10 and place CopperSharp68k beside this checkout, or set
`CopperSharp68kRoot` to its location. The existing SDK package fallback is
retained. Run:

```powershell
dotnet build CopperFS.slnx -c Release
dotnet test CopperFS.slnx -c Release
```

These are host semantic tests. Optional external media tests can be unavailable;
that is not native replay coverage. Native handlers build directly from CopperFS
using CopperSharp's SDK and compiler. No CopperStart checkout or DLL is required:

```powershell
./build/Build-Handler.ps1 -Ffs -ArtifactRoot artifacts/ffs
./build/Build-Handler.ps1 -ArtifactRoot artifacts/sfs
./build/Build-Handler.ps1 -Sfs2 -ArtifactRoot artifacts/sfs2-trst
./build/Build-Handler.ps1 -Sfs2 -Sfs2OriginalDataWrites:$false -ArtifactRoot artifacts/sfs2-journaled
./build/Build-Handler.ps1 -Sfs2 -Sfs2CsunWrites -ArtifactRoot artifacts/sfs2-csun
./build/Build-Trim.ps1
```

To run CopperStart's native replay against freshly built handlers, use a
CopperStart checkout beside this repository and a licensed Kickstart 3.1 ROM in
`COPPERSHARP_KICKSTART31_ROM`:

```powershell
./tools/Test-NativeReplay.ps1 -Run
```

It builds the `classic`, `ffs`, `sfs2-csun`, `sfs2-trst` and `command` layout
that `COPPERSFS_SPARSE_ARTIFACTS` expects, records SHA-256 hashes and source
revisions in `replay-manifest.json`, and runs the twelve
`NativeSparseStorageTests` cases with a TRX log. Use `-SkipBuild` to rerun
against an existing `-ArtifactRoot`. The original-handler A/B lanes in
`tests/CopperFS.Kickstart.Tests` additionally need external FFS 40.1 and
SmartFilesystem 1.279 binaries, which are never added to this repository.

SFS2 defaults to owned TRST with a 192 KiB transaction arena and no extra cache.
It writes existing payload in place when the extent is validated and no
pending image covers the destination. Metadata commits retain all four TRST
durability barriers. Explicit Flush makes acknowledged payload durable.
A device failure or power loss before Flush succeeds can leave a partially
overwritten file. Select `-Sfs2OriginalDataWrites:$false` for full-data TRST
journaling or `-OwnedSfs2:$false` for the previous non-owned TRST profile.

For low-memory Amigas, `-Sfs2CsunWrites` selects CSUN undo writes with an optional
64 KiB batching buffer and a scalar fallback. Both write policies recover outstanding
TRST and CSUN journals on writable media. A clean SFS2 disk can switch handlers
without conversion. Existing `-Sfs2TrstWrites` commands still select TRST.
Direct MSBuild clients enabling `CopperStartNativeSfs2=true` can select
full-data TRST with `-p:CopperFsSfs2OriginalDataWrites=false`, the previous
TRST profile with `-p:CopperFsSfs2Owned=false`, or CSUN with
`-p:CopperStartSfs2TrstWrites=false`. `-LegacyCsUn` selects the classic SFS writer.

Set `-CopperSharpRoot` when CopperSharp68k is not beside this checkout. Use `-Cpu
68020` to select that target; the default is 68000. The existing strict-write,
buffered TRST and legacy CSUN selections remain supported. CopperStart's optional
build wrappers select its pinned CopperFS sources. Both entry points produce
the existing CopperFFS, CopperSFS and CopperTrim names.
Specialized OFS/FFS builds (`-Ffs`) default to CILica inlining and a matched
native layout for both AutoTrim policies. Add `-AutoTrim` to enable discard.
Use `-FfsTrimLayoutControl:$false` for the compact layout or
`-CilicaInlining:$false` for scalar replacement alone.

Handler builds run CopperSharp's restricted CILica IL optimizer with scalar
replacement enabled. The rewritten assembly and hash report are under the
artifact root's `cilica` directory and the original managed inputs are under
`inputs`. `-SkipCilica` skips optimization for diagnostics. Optimizer failures
stop the build.
The qualified standalone FFS, classic-SFS and SFS2 TRST builds use specialized
packet dispatch by default. SFS2 retains classic-SFS handling and CSUN
recovery. `-NoSpecialization` selects the combined dispatch for comparison.
Other write-policy combinations keep the combined dispatch unless an explicit
specialization switch is supplied.
See [integration and migration](docs/copperfs-integration.md) for boundaries,
validation and source provenance, and [manual TRIM](docs/manual-trim.md) for usage.

## Research tools and additional tests

The isolated [new-filesystem prototype](prototypes/CopperFS.Prototype/README.md)
explores variable-length extents, inline files, bounded scratch and crash recovery
in a separate experimental format. Its in-memory demo and host tests are included
in the solution; it has no native handler or dependency from the existing cores.

The [CopperNova design and safety review](docs/new-filesystem-design.md)
proposes a separate format with small allocation units, extents, inline data,
UTF-8/legacy names and TRIM. The CPX3 prototype implements its journal, tree and
reserve model; [qualification and remaining native gates](docs/cpx3-prototype-qualification.md)
separate host crash tests from 68000 and media coverage. Existing formats are unchanged.
The [68000 readiness report](docs/cpx3-68000-readiness.md) records high/low
arithmetic, resumable scans, scratch budgets and measured arithmetic stack usage.
The separate [CPX4 native port](docs/cpx4-native-progress.md) now builds a standalone
handler with resumable file I/O, namespace mutations, external truncation,
extent-tree growth and unwritten conversion, handle/lock conversion, parent and
access-mode operations, directory enumeration and information
queries, ExAll, notifications, timed record locks, Inhibit/remount and bounded
storage cancellation/media events, Format, persistent volume labels and SetOwner
with persistent UID/GID metadata and ExAll ownership queries, a partial
DOS dispatcher and UNMAP.
Its 512 KiB MC68000 boot/stack workloads are separate from the unfinished full
packet-parity and exhaustive failure-path qualification.

See [the selective research port](docs/research-port.md) for the image checker,
reliability tests, wide-volume tests, and unresolved research findings.

The [optional Kickstart comparison suite](tests/CopperFS.Kickstart.Tests/README.md)
runs matched OFS, FFS and SFS correctness workloads against original handlers,
with independent image checks and clean handoffs in both directions. Use
`tools/Test-OriginalHandlers.ps1` with locally supplied reference binaries and ROM.
The [initial correctness report](docs/original-handler-correctness-ab-2026-11-06.md)
records the findings. The [classic-SFS replacement fix](docs/sfs-mode-newfile-compatibility-2026-11-06.md)
closes the recycled-file difference; the expanded matrix passes 9/9.

The [sequential storage matrix](docs/kickstart-storage-matrix.md) compares original
FFS/SFS2 with Copper's cached and payload-bypass policies on zero-latency
CopperHDF, timed HDD/CF and native A1200 Gayle IDE. The full default run contains
1,188 fresh-machine workload trials. Synthetic drive timing is separate from
physical A1200 performance.

## Compatibility and releases

The repository move does not change filesystem formats, packet ABI, native entry
names, journal policies or existing CopperSFS package identities. Geometry and
platform interfaces remain in CopperSFS.Core to preserve existing type identity.
CopperFFS currently retains its CopperStart.Dos namespace for the same reason.
CopperStart links the maintained filesystem sources into its native DOS assembly;
standalone host consumers can reference CopperFFS.Core directly. The standalone
handler project compiles those same sources into CopperFS.Amiga, without importing
CopperStart's OS implementation. Retained namespaces and compile-symbol names are
compatibility names, not repository or assembly dependencies.

Standalone FFS, default classic buffered TRST and default SFS2 TRST select their
family before native compilation so CopperFFS excludes the SFS writer and
CopperSFS excludes the OFS/FFS writer. SFS2 CSUN and strict/legacy classic write
policies retain shared dispatch unless explicitly specialized. `-Sfs2` selects
TRST; use `-Sfs2 -Sfs2CsunWrites` for CSUN.
The shared source consumer and ordinary managed build retain runtime dispatch.
Use `Build-Handler.ps1 -SharedDispatch` only as a size/performance comparison
control. See the [active-code measurements](docs/active-code-size-2026-11-11.md)
and [initial size pass](docs/handler-size-optimization-2026-11-11.md) for the
remaining gap to the original handlers and the validation scope.
The [largest-function follow-up](docs/largest-functions-size-2026-11-12.md)
records discarded reductions and the additional large-write performance gate.

Published CopperSFS releases remain in the original repository and are immutable.
This source migration does not publish a package, handler or ROM release. The
[previous CopperSFS overview](docs/coppersfs-overview.md) records existing release
and compatibility guidance. Host HDF storage and sparse deallocation remain in
CopperDisk/CopperScreen; CopperFS owns guest filesystem behavior.
