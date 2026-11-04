# CopperFS

CopperFS is the umbrella repository for CopperFFS (OFS/FFS), CopperSFS
(classic SFS/SFS2), their shared native handler sources and CopperTrim.
The formats retain separate allocation, validation and recovery implementations.

| Component | Location |
| --- | --- |
| OFS/FFS core | `src/CopperFFS.Core` |
| Classic SFS and SFS2 cores | `src/CopperSFS.Core` |
| Standalone Amiga handler project and shared sources | `src/CopperFS.Amiga` |
| CopperTrim and compatibility command | `tools/CopperSFS.Trim` |
| Managed filesystem tests | `tests/CopperFFS.Core.Tests`, `tests/CopperSFS.Core.Tests` |
| Native build tools | `build/Build-Handler.ps1`, `build/Build-Trim.ps1` |

CopperFFS supports DOS0 OFS and DOS1 FFS reads, writes and manual TRIM on
one mount. Its reader also handles DOS2-DOS5; those variants remain read-only.
CopperSFS supports classic SFS and SFS2 with their existing CSUN/TRST policies.
CopperTrim uses the same version-1 packet for all four supported writable formats.
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
./build/Build-Handler.ps1 -Sfs2 -ArtifactRoot artifacts/sfs2-csun
./build/Build-Handler.ps1 -Sfs2 -Sfs2TrstWrites -ArtifactRoot artifacts/sfs2-trst
./build/Build-Trim.ps1
```

Set `-CopperSharpRoot` when CopperSharp68k is not beside this checkout. Use `-Cpu
68020` to select that target; the default is 68000. The existing strict-write,
buffered TRST and legacy CSUN selections remain supported. CopperStart's optional
build wrappers select its pinned CopperFS sources. Both entry points produce
the existing CopperFFS, CopperSFS and CopperTrim names.
See [integration and migration](docs/copperfs-integration.md) for boundaries,
validation and source provenance, and [manual TRIM](docs/manual-trim.md) for usage.

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

Published CopperSFS releases remain in the original repository and are immutable.
This source migration does not publish a package, handler or ROM release. The
[previous CopperSFS overview](docs/coppersfs-overview.md) records existing release
and compatibility guidance. Host HDF storage and sparse deallocation remain in
CopperDisk/CopperScreen; CopperFS owns guest filesystem behavior.
