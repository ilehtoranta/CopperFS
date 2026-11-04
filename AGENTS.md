# CopperFS

Maintain CopperFFS and CopperSFS as separate filesystem implementations in this
umbrella repository. Keep current namespace, package and handler identities;
repository reorganization alone must not change on-disk formats, packet ABI,
journal policy, ordering or native behavior.

Native filesystem code remains allocation-free and uses caller-owned scratch
and single-owner mount execution. CopperStart supplies OS registration and
integration; CopperDisk/CopperScreen own host storage and sparse deallocation.
Do not add a dependency from the portable cores to those application projects.
Standalone native handlers build from CopperFS.Amiga using only CopperSharp.
Do not reintroduce CopperStart project, assembly or source-path dependencies into
that project or Build-Handler.ps1. OS integration is a separate source consumer.

Run dotnet build CopperFS.slnx -c Release and dotnet test CopperFS.slnx -c Release.
For native changes or source integration, rebuild affected handlers and run
CopperStart's native replay tests. Report host, native and full-ROM coverage
separately; unavailable media/platform tests are not passes.

Never commit ROMs, media, credentials or build artifacts. Existing published
versions are immutable. Development source changes do not authorize packages
or ROM publication. Preserve provenance when importing code or test fixtures.
