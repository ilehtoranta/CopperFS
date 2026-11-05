# CopperFS integration and migration

## Ownership

CopperStart's A1200 Full ROM consumer defines
`COPPERSTART_FULL_FILESYSTEM_EXTENSION`. Its CSEE/15 profile keeps the common
packet task in a 512 KiB extension, while standalone handlers retain their own
ABI and code paths. A device read failure during initial DOS probing does not
trigger unrelated filesystem probes; declared SFS mounts retain secondary-root
recovery. CopperStart owns the extension registration and graphics/task entry
metadata. This profile does not add a standalone-handler dependency on CopperStart.

The Full consumer source-links `AmigaDiskFileSystemFormatCore`,
`AmigaSmartFileSystemFormatCore` and `NativeFileSystemFormat` from CopperFS.
The portable formatters allocate no managed state and use explicit reserved-block
writes through `IAmigaFormatFileSystemPlatform`. CopperStart owns packet/DOS-list
integration and enables it only for the Full ROM pair. Classic SFS formatting
includes its recycled-directory options; SFS2 uses a clean version-4 volume
without recycling. The independent checker recognizes the original NORECYCLED
node-2 sentinel and rejects that sentinel when recycling is enabled. It also
reconciles recycled-file/block counters against the walked directory and extents.

CopperFS maintains OFS/FFS and classic SFS/SFS2 cores, shared native filesystem
handler sources, CopperTrim, semantic tests and handler/command build recipes.
CopperStart maintains its Exec/DOS services, OS-specific codecs, extension ROM
registration and native integration replays. CopperDisk and CopperScreen retain
HDF I/O, sparse host deallocation, profiles, backups and desktop controls.

CopperStart imports build/CopperFS.Amiga.props from external/CopperFS. This
includes the maintained sources in the existing CopperStart.Dos native assembly.
It preserves native entry and managed type identities without an extra device
abstraction or runtime dispatch layer. CopperFFS.Core is also independently
buildable for host consumers; do not reference that assembly together with a
CopperStart.Dos assembly which already includes those same types.

CopperSFS.Core remains the owner of existing geometry, platform and TRIM packet
types. Splitting these into another assembly would be a separate ABI migration.
The old CopperSFS.slnx remains a compatibility entry for SFS-only work;
CopperFS.slnx builds and tests both maintained cores.

Native handlers build from `src/CopperFS.Amiga/CopperFS.Amiga.csproj`, using only
CopperFS and CopperSharp inputs. `build/Build-Handler.ps1` has no CopperStartRoot
parameter and imports no CopperStart project or DLL. CopperStart's wrappers remain
optional consumers of the same build recipe. Standalone builds do not qualify
full extension/reset-ROM boot. No published CopperSFS package identity or
historical release URL is changed.

SFS dispatch, record locking, notification layouts and the required Exec/packet
field constants are maintained here. Only the native-client-service export and
OS-specific removable-media/bootstrap integration remain in CopperStart. The
standalone profile excludes those integration paths and uses standard AmigaOS
library calls. Existing type namespaces, entry names and conditional symbol names
are retained for compatibility; they do not require CopperStart assemblies.

## Provenance

The repository starts from CopperSFS commit
`ee11ac96dd9aaeb3140b6f55e25b49b96b1aec8c` with its original Git ancestry and
MIT license retained. CopperFFS and native handler sources and their tests were
copied without behavior changes from CopperStart commit
`cdd3935` (the tested OFS writer baseline). Their original history remains in
CopperStart. New filesystem development belongs here. The subsequent standalone
extraction imports shared dispatch/support from CopperStart `b5158bf`; the
OS-specific extension export remains in that repository.
The [source parity manifest](migration-source-parity.json) records the 27 moved
core, native and test files and their SHA-256 values after newline normalization.

Earlier uncommitted CopperSFS research was preserved and pushed as
`codex/preserve-local-research`, commit
`4a902a1ff9189bbdff841c23441c66678a6e2427`, in the original CopperSFS repository.
That branch predates the mainline TRST/TRIM implementation and is retained as
research, not substituted for the validated mainline. The original checkout was
fast-forwarded to ee11ac9 and is clean. The unrelated Samsung TV test directory
was moved intact to a sibling directory; it is not filesystem source.

Historical ProbeDosBase/ProbeDosPut variants in CopperStart contain intentional
reader/native differences. Byte-identical core files can be linked to CopperFS;
experimental variants stay explicit in those probes rather than silently changing
their benchmark inputs. Production uses only the maintained CopperFS sources.

## Validation

The pre-migration OFS writer passed 164 CopperStart filesystem tests and six
native OFS/FFS cases on Windows/NTFS. The portable suite moves 163 of these tests
here; the physical MFM clock test remains in CopperStart. Native cases include
same-mount write/delete/TRIM/rewrite, destructive partial discard, cold boot and
independent ROM OFS reading. These are correctness results, not throughput data.

Migration validation on 2026-11-04 passed:

- CopperFS Release solution build; 163 OFS/FFS and 837 SFS/SFS2 tests. One optional
  external-media SFS test was unavailable.
- CopperStart Release solution and both retained benchmark probe builds; all 164
  integrated filesystem tests. Existing CopperStart warnings remain.
- CopperScreen Release solution, with no warnings.
- Rebuilt CopperFFS, classic CopperSFS, SFS2 CSUN and SFS2 TRST standalone handlers
  and both command names; all twelve native sparse-storage cases passed without
  skips on Windows/NTFS.

The rebuilt CopperFFS is byte-identical to the pre-migration OFS writer binary
(SHA-256 `DDB60FABBF0B034FFEF1B092EB466DCAAB65668DBEA80431AE3F1897C3A9A977`).
CopperTrim and CopperSFSTrim retain SHA-256
`612EED6C29614166CF61F0BB1F03D40586E162ECEB1A3F89B0D11DF112E08381`.
The migration changes source ownership and build paths, not these binaries.

Native TRX, per-case allocation measurements and the standalone migration receipt
are local artifacts under CopperStart's `artifacts/copperfs-migration/`.
This is not the complete v5 extension-bundle receipt: extensions were not rebuilt
in this migration run. Linux/macOS native replay and full extension/reset-ROM
boot remain separate, unavailable coverage here. No packages or media were published.

## Independent native builds, 2026-11-04

The follow-up standalone project removes the CopperStart build dependency.
CopperFFS was built directly from CopperFS; classic CopperSFS, SFS2 CSUN, SFS2
TRST and CopperTrim also built from a source snapshot under the host's temporary
directory, with no CopperStart checkout beside it. The only explicit external
build root was CopperSharp68k. All four handler input graphs were inspected and
contained no CopperStart DLL or library dependency. Their input assembly is now
`CopperFS.Amiga.dll`; native entry names and on-disk/packet formats are unchanged.

The CopperFS Release solution passed with 1,018 managed tests and one unavailable
external-media test. That includes 17 retained record-lock cases and a new
transitive assembly dependency check. Invalid buffered/strict configurations are
rejected by the standalone project. CopperStart's Release solution, 164 integrated
filesystem tests and 50 focused record-lock/notification/attribute/ABI tests passed.
All twelve native sparse-storage cases passed without skips against the independent
68000 binaries on Windows/NTFS. Results are in CopperStart's
`artifacts/independent-fs-native/`; binaries and dependency evidence are in
CopperFS's `artifacts/standalone-handlers/` and `standalone-dependencies.json`.

The CopperStart 512 KiB SFS2 CSUN extension also built and linked, preserving the
separate OS integration path. This is not extension boot qualification. The 68020
target, Linux/macOS host replay and full extension/reset-ROM boot were not rerun
in this follow-up. No packages or media were published.
