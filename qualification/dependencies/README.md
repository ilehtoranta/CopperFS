# Qualification dependencies

`lock.json` pins the exact compiler, integration test sources, emulator and
.NET SDK used for release qualification. `copperscreen-storage.patch` captures
the storage timing and observation changes needed by the Kickstart test
project, extracted on 2026-11-09 from the CopperScreen working tree based on
revision `7304daac403c1fecf7aead0eac0193392a58e014`. The patch retains that
project's MIT license; see `CopperScreen.LICENSE`. It is a test dependency,
not a dependency of the portable cores or standalone handlers.

Run `./build/Initialize-QualificationDependencies.ps1` to create or verify
the checkouts under `artifacts/qualification-dependencies`. The script refuses
unexpected revisions, untracked or unstaged changes, and a different patched
tree. It never resets or cleans an existing checkout. Pass the resulting paths
as `CopperSharp68kRoot`, `CopperStartRoot`, `CopperScreenRoot` and
`CopperOSRoot`; the handler build accepts `-CopperSharpRoot`.

The compiler and CopperOS use unmodified commits. `copperstart-fixtures.patch`
contains ten adapter lines from the CopperStart working tree based on `ed02a70` and the updated cache replay;
the CopperStart source is copyright 2026 Ilkka Lehtoranta, all rights reserved. CopperScreen
uses the specified patch and Git tree ID; the patch is not a claim that it has
been merged upstream. The cache test is adapted from CopperStart's
`NativeCacheInvalidationInstructionTests.cs` at `ed02a70`, copyright 2026
Ilkka Lehtoranta, all rights reserved.

`nuget-packages.json` records the resolved package identities and NuGet SHA-512
content hashes for the qualification builds. After restoring each build and
before accepting its results, run `./build/Test-QualificationPackages.ps1`
with `-ArtifactsPath` naming the build output root or an array of roots.
It rejects unrecorded packages or changed content hashes. It checks the assets
that exist in the supplied roots; it does not assert that every suite ran.

For example, with the default dependency location, the standalone artifacts
used in this record can be rebuilt with:

```powershell
$deps = "$PWD/artifacts/qualification-dependencies"
$out = "$PWD/artifacts/qualification-handlers"
./build/Initialize-QualificationDependencies.ps1
./build/Build-Handler.ps1 -CopperSharpRoot "$deps/CopperSharp68k" -Ffs -ArtifactRoot "$out/ffs"
./build/Build-Handler.ps1 -CopperSharpRoot "$deps/CopperSharp68k" -ArtifactRoot "$out/sfs"
./build/Build-Handler.ps1 -CopperSharpRoot "$deps/CopperSharp68k" -Sfs2 -ArtifactRoot "$out/sfs2-trst"
./build/Build-Handler.ps1 -CopperSharpRoot "$deps/CopperSharp68k" -Sfs2 -Sfs2CsunWrites -ArtifactRoot "$out/sfs2-csun"
./build/Build-Trim.ps1 -CopperSharpRoot "$deps/CopperSharp68k" -ArtifactRoot "$out/trim"
```

Run the host and native tests with `CopperSharp68kRoot` and `CopperStartRoot`
pointing at these checkouts. The Kickstart project also requires
`CopperScreenRoot`, the external ROM and reference media described in its
[README](../../tests/CopperFS.Kickstart.Tests/README.md). CopperStart's own
integration test project additionally requires `CopperFSRoot` and
`CopperOSRoot`.
