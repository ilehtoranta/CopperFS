# Filesystem source checkpoint validation, 2026-11-15

Validation uses the frozen source snapshot in `8bef243`. Later checkpoint
documentation changes do not alter executable source. The shared original
worktree is preserved; this checkpoint is published on
`codex/sfs2-performance-checkpoint`. It is not a release or a merge into main.

## Host and solution checks

`dotnet build CopperFS.slnx -c Release -m:1 -p:UseSharedCompilation=false`
passed with zero warnings and zero errors.

The complete solution test passed on 2026-11-16 with
`dotnet test CopperFS.slnx -c Release --no-build -m:8 -p:UseSharedCompilation=false`:
**2,508 passed, nine skipped, zero failed**.
An initial sequential invocation was stopped to run independent projects
concurrently; its partial results are not counted as a complete pass.

| Suite | Passed | Skipped |
|---|---:|---:|
| Prototype instruction | 1 | 0 |
| Prototype core | 77 | 0 |
| CPX core | 484 | 0 |
| CPX instruction | 74 | 6 |
| FFS core | 438 | 0 |
| Amiga adapter | 18 | 0 |
| Storage benchmark tests | 32 | 0 |
| SFS core, including SFS2 | 1,384 | 3 |

The nine skips require external media or opt-in full-ROM fixtures.

## Native builds and replay

The handler rebuild matrix uses `build/Build-Handler.ps1 -SkipCompilerBuild`,
with separate artifact/input directories for each profile:

| Profile | Additional flags | Result |
|---|---|---|
| FFS | `-Ffs -AutoTrim` | Passed |
| Classic SFS | `-AutoTrim` | Passed |
| Classic SFS shared pool | `-SharedSfsPool -SfsInPlaceOverwrite -AutoTrim` | Passed |
| Default SFS2 recovery | `-Sfs2 -AutoTrim` | Passed |
| Opt-in SFS2 original payload writes | `-Sfs2 -OwnedSfs2 -Sfs2OriginalDataWrites -AutoTrim` | Passed |
| CPX4 | `-Cpx4` | Passed |

All six native compatibility reports have no reachable managed allocation
sites. Standalone input builds consume CopperFS and CopperSharp, with no
CopperStart source or project dependency.

The normal-pipeline opt-in SFS2 rebuild is 248,988 bytes, SHA256
`26192DC97730DF7F4D8C5E8B4439B77DA580D52892B6BB7315FC724E07907E2C`.
It is distinct from the earlier measured codec-experiment HUNK.

CopperStart's `CopperStart.Floppy.Native.Tests` was rebuilt in Release with
`-p:CopperStartNativeSfs2=true` and `-p:CopperFSRoot=<checkpoint-root>` into
an isolated artifacts directory. The following instruction replay classes
passed **234 cases**, with **five skipped** actual-handler prefetch cases
whose external fixture was not configured:

- `NativeFileSystemTransferInstructionTests`
- `NativeFileSystemBufferInstructionTests`
- `FileSystemSizeInstructionTests`
- `FileSystemLinkInstructionTests`
- `FileSystemGeometryInstructionTests`
- `NativeSfsTransferInstructionTests`
- `NativeSfsDeletionInstructionTests`
- `NativeSfsMetadataPrefetchInstructionTests`
- `NativeSfsReadWindowInstructionTests`

An initial replay attempt omitted the SFS2 build flag and failed four SFS2
size/attribute cases against the compiled-out backend; it is not a qualified
profile. The earlier frozen SFS2 assembly separately passed all 12 cases in
those three test methods. A broad name filter also selected full-boot tests
without their required boot fixture. Those broad attempts were stopped;
the final replay selection above explicitly covers instruction replay.

## Full-ROM and performance coverage

The [SFS2 performance report](sfs2-performance-2026-11-15.md) identifies the
earlier measured HUNK, paired cycle/rate results, and its successful clean
six-boot cross-handler handoff. The source checkpoint has later first-write
and overlay changes without a completed paired native performance run or
full-ROM recovery qualification. Chip RAM, physical controller latency,
physical Amiga operation, and hardware power-loss tests are unqualified.
Skipped or unavailable cases are not passes.

Local logs and generated handlers are under `artifacts/checkpoint-validation/`;
they are deliberately excluded from the commit. Licensed ROMs and reference
media remain external. No package, handler binary, ROM, or website is published
by this source checkpoint.
