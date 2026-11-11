# Optional Kickstart comparisons

`tools/Test-OriginalHandlers.ps1` runs a correctness A/B against external original
FFS 40.1 and SmartFilesystem 1.279 binaries. Supply their paths, a licensed ROM,
and an independently formatted clean 16 MiB classic SFS image (two reserved
blocks). The script pins reference binary hashes, builds four candidate profiles,
and records binary/source/input provenance under a fresh ignored output directory.
`-FrozenHandlerRoot` explicitly reuses previously built `ffs`, `sfs`,
`sfs2-csun` and `sfs2-trst` subdirectories instead.

The nine cases are OFS/FFS at 16 MiB, OFS/FFS at DD and HD capacities/geometries,
classic SFS with buffered TRST, and SFS2 with CSUN or TRST. Each case boots both
writers from identical media, independently checks their persisted images, then
fresh-boots both readers against each writer's output (six boots per case).
Two additional SFS2 cases capture the first durable TRST redo log and the last
durable dirty CSUN image for a directory creation. Each case checks the journal
ID emitted by its writer and boots both Copper handlers from the captured image.
The independent image checker verifies that TRST completes the creation and CSUN
undoes it.
The guest checks large growth, fragmented allocation opportunities, unaligned
overwrites, truncation by reopening, rename, comment/protection/date, directory
enumeration, missing objects, nonempty-directory deletion and EOF/backward seeks.
The independent walkers compare every file's path, size and content hash across
the two writers, without requiring identical allocation or timestamps.

DD/HD correctness cases use the synchronous CopperHDF device with 11/22 sectors
per track and 80 cylinders. They qualify filesystem geometry and interoperability,
not floppy DMA, physical persistence, rotational timing or physical performance.
This is clean-close/remount coverage; it is not a crash or torn-write A/B.
Original FFS 40.1's OFS append sequence restarts and omitted old-EOF link are
recorded in its inspection report. Copper's newly authored images must retain
canonical sequences and complete forward links.

Classic SFS additionally checks repeated `MODE_NEWFILE` replacement, preserved
recycled bytes/metadata, collision suffixes, empty-file recycling, and the
original handler's delete/write-protection rules. Recycled entries remain in
the strict file-manifest comparison.

For direct invocation, set `COPPERFS_CORRECTNESS_MANIFEST` to the script's
manifest and select `FullyQualifiedName~OriginalHandlerCorrectnessTests`.
Direct tests skip when the ROM or manifest is absent; invalid configured inputs
fail. Reference binaries and images are never added to the repository.

## Sequential reads

The [storage matrix protocol](../../docs/kickstart-storage-matrix.md) extends this
suite with stock/expanded A1200, timed CopperHDF and native Gayle `scsi.device`,
while retaining the historical defaults. Select it with `-Matrix`; its C# runner
accounts for all 1,188 scheduled cold workloads before producing a complete
report. New `kickstart-storage-v2` IDE runs require successful reads from every
handler. They use a 16 MiB partition after one reserved 128 KiB cylinder, with
matching SFS2 root byte ranges. This avoids the original SFS whole-device
geometry path selected by the previous flat fixture. Historical v1 reports
retain their unavailable records; see the [mount investigation](../../docs/kickstart-sfs2-ide-mount-fix-2026-11-07.md).
Use `-A1200Kickstart 3.1 -A1200Rom31 <licensed-A1200-40.68-ROM>` for the separate
3.1 machine profile. `-FileSystemProfile Sfs2 -Backend A1200Ide` on the direct
matrix launcher selects the 288-workload original/CSUN/TRST IDE comparison with
both RAM and drive profiles. See the [3.1 configuration note](../../docs/kickstart31-sfs2-ide-2026-11-07.md)
for input identity and coverage. The A500 3.1 image remains the historical control.

This project boots a licensed Kickstart 3.1 ROM in CopperScreen's lightweight
emulator and runs a compiled guest application through real DOS calls and the
standalone filesystem HUNK. It intentionally stays outside `CopperFS.slnx`:
ordinary host tests need neither a ROM nor CopperScreen.

Use `tools/Benchmark-KickstartSequentialReads.ps1` with frozen handler artifacts
from `tools/Benchmark-SequentialReads.ps1`. It accepts the ROM path explicitly
or through `COPPERSHARP_KICKSTART31_ROM`. Use a new output directory. ROM contents
are never copied to artifacts. Generated synthetic HDFs, boot ADFs and binaries
stay under the ignored `artifacts/` directory.

For an A/B against original Amiga binaries, supply both `-OriginalFfsHandler`
and `-OriginalSfs2Handler`. They replace arm A; arm B remains the frozen optimized
Copper handlers. The manifest records reference versions and SHA-256 hashes.
Both arms use the same guest probe, read-only synthetic media and mount settings.
Original cache policies remain intact; their memory budgets are not equalized.
Use `-PayloadCachePolicy Cached` or `-PayloadCachePolicy Bypass` to compare
Copper's normal policy with its opt-in whole-block payload bypass on
`copperhdf.device`. The same HUNK and probe are used; the mount's existing
DosEnvec control word selects the policy. Partial-sector and metadata caching
remain enabled. Original-handler arms always receive control zero. The applied
control value is retained in the manifest and every raw trial.
The same original SFS binary is repeated against each Copper SFS2 policy build.
An optional `-ReferenceProvenance` JSON receipt preserves archive and extraction
hashes alongside the results; reference binaries remain external inputs.

The guest opens each file, reads consecutive 64-byte, 512-byte, 4 KiB or 64 KiB
chunks, then closes it. It remounts the volume outside timing before each trial.
For a repeated-open comparison, set the top-level `PrimeOpen` field to `true`
in a copy of the read manifest and run the throughput test directly. After
remounting, the probe opens and closes the same path once before starting the
timed session. Both arms use the same probe and record `PrimeOpen` in each trial.
Each raw trial records open, read-loop, close and whole-session guest bus cycles.
Set the manifest's `MeasureMount` field to `true` to also record mount and
first open as one contiguous interval. The probe writes `startup.json` for
the initial DOS device registration, DeviceProc startup and first file open.
Handler loading is complete before that interval begins. Each workload then
records uninhibit/remount and the following first open in `RemountAndFirstOpen`.
With `PrimeOpen`, the ordinary open timer still measures the subsequent reopen.
Phase records include device command counts, bytes and backing-file offsets.
The backing-file hook can deliver one command in multiple sector-sized reads.
Host hashing checks the entire returned stream without advancing guest time.
The read-only hardfile hook also checks device requests and rejects writes.

The guest probe, original synthetic fixture/checker links and host observer do
not add dependencies to the portable cores or standalone handler build. The
probe's mount sequence follows CopperStart's MIT-licensed `NativeSfsAbProbe`;
no external filesystem implementation or media fixture is imported.

CopperHDF services host sectors synchronously without physical disk latency in
guest time. Results are virtual Kickstart throughput for the documented machine,
not physical Amiga disk performance. See the dated report in `docs/` for the
configuration, trial ranges, binary hashes and coverage boundaries.

Set `COPPERFS_KICKSTART_PROFILE=1` when invoking the throughput test directly
to collect exclusive instruction-cycle profiles for open, read and close.
Set `COPPERFS_KICKSTART_PROFILE_PACKET=65536` to collect only the 64 KiB phases.
Set `COPPERFS_KICKSTART_PROFILE_ALL_TRIALS=1` to save each repetition separately,
for example `65536-trial2-close-profile.json`.
All four workloads still run in the same order. Select both handler arms for
a paired profile. Copper maps supply method names, and original HUNKs supply
relocation-aware code ranges for instruction offsets and handler totals.
Use a fresh manifest/output directory. By default, diagnostics run one trial per size using
the scalar driver adapted from CopperStart's MIT-licensed `SfsCpuProfile`;
acceptance benchmarks use normal `ExecuteFrame`, with profiling explicitly off.
The driver reads ordinary RAM and compiler maps without guest memory accesses
or inserted guest code. Interrupt dispatch, STOP waits and marker boundaries
remain separately identifiable residual cycles. Profile times are diagnostics,
not substituted for the uninstrumented A/B results.

For a direct storage-matrix diagnostic, set
`COPPERFS_STORAGE_CPU_PROFILE=64,4096` and run
`StorageMatrixThroughputTests.ColdReadThroughRealDosAndConfiguredStorage` with
a fresh matrix manifest. It collects one cold trial per selected size and
marks the case `diagnostic-cpu-profile.json`. Select either CPU profiling or
mount tracing. The formal matrix runner clears this environment variable and
rejects diagnostic cases from throughput reports.

## Native SFS2 TRST crash recovery

`NativeSfs2RecoveryMatrix` ports the six single-mutation cases from the
CopperSFS research snapshot `4a902a1`. It boots Kickstart with a synthetic
16 MiB SFS2 partition, performs each mutation through DOS with the standalone
CopperSFS TRST handler, and checks the uninterrupted result. The mutations
overwrite across a sector boundary, truncate, rename, delete, create an empty
file, and set a comment.

The device model accepts atomic 512-byte writes into volatile storage and
makes them durable on flush. The test traces every sector write and flush,
then repeats the guest workload and interrupts it once per distinct durable
image. It compares the interrupted disk byte for byte with the traced image.
CopperSFS and original SmartFilesystem 1.279 independently mount copies of
that disk. A published TRST journal must recover the complete committed
manifest, including free space and file hashes. Each handler must leave a
clean marker and preserve the disk byte for byte on its next boot.

Build the handler and set the local input paths before running:

```powershell
./build/Build-Handler.ps1 -Sfs2 -Sfs2TrstWrites -ArtifactRoot "$PWD/artifacts/sfs2-trst"
$env:COPPERFS_SFS2_TRST_HANDLER = "$PWD/artifacts/sfs2-trst/CopperSFS"
$env:COPPERFS_ORIGINAL_SFS = '<path to original SmartFilesystem 1.279>'
$env:COPPERSHARP_KICKSTART31_ROM = '<path to decoded Kickstart 3.1 ROM>'
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~NativeSfs2RecoveryMatrix
```

## Native SFS2 CSUN crash recovery

`NativeSfs2CsunRecoveryMatrix` runs the same overwrite, truncate, rename,
delete, create and comment operations with the standalone CSUN undo handler.
For every distinct durable disk image, the harness cuts a fresh guest run at
the corresponding write or flush. A dirty marker must roll back to the entire
pre-mutation manifest, including file data, metadata and free-block count.
The matrix covers the initially empty undo chain and requires a dirty marker
pointing to a CSUN descriptor. Clean images must preserve their manifest,
including the fully committed result. After recovery the marker must be clean,
and a second native mount must leave the disk byte-identical.

Use a decoded Kickstart 3.1 ROM as above. CSUN recovery uses CopperSFS for
both mounts and does not require the original SmartFilesystem binary.

```powershell
./build/Build-Handler.ps1 -Sfs2 -ArtifactRoot "$PWD/artifacts/sfs2-csun"
$env:COPPERFS_SFS2_CSUN_HANDLER = "$PWD/artifacts/sfs2-csun/CopperSFS"
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~NativeSfs2CsunRecoveryMatrix
```

## Native SFS2 file positions

`NativeSfs2FilePosition` runs the two DOS packet regressions from CopperSFS
research commit `4a902a1`. Use the Kickstart ROM and freshly built SFS2 TRST
handler variables from the recovery matrix above, then run:

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~NativeSfs2FilePosition
```

The wide case requires a sparse-capable temporary directory. Its 136 GiB
logical HDF places a file extent at 128 GiB. The guest writes 1,027 bytes
starting three bytes below 4 GiB, checks both words of the resulting position
and file size, and checks that shrinking respects a second open handle's
position. After that handle closes, the guest shrinks the file. A fresh
Kickstart boot verifies the surviving 512 bytes and EOF without writing the
disk. Host inspection checks the affected extent, object, bitmap, free count
and clean journal marker without scanning the whole logical device.

The exhaustion case writes a 17-byte prefix, then submits a 256 KiB write
which must report NoFreeStore and preserve position 17. A 1 KiB retry on the
same handle must succeed, with the original prefix and exact EOF intact.
The host checks the persisted file hash and observes TRST journal writes.

## Recover an original SFS2 redo transaction

`NativeCopperSfsRecoversOriginalSfs2Redo` uses the 16 MiB raw partition
captured by the research reference tool at commit `4a902a1`. Original
SmartFilesystem 1.279 had published a nonempty TRFA marker pointing to a
TRST journal. The archived capture has SHA-256
`01212067D559A385FF8B1E29AF81D71E7438B46751C6A0DFD5C0C9E621B8D4ED`.

Use the Kickstart and CopperSFS TRST handler settings from the native TRST
matrix above. Point the test at the dirty capture, which it copies for replay:

```powershell
$env:COPPERSFS_SFS2_NATIVE_REDO_IMAGE = '<path to capture-redo/reference.hdf>'
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~NativeCopperSfsRecoversOriginalSfs2Redo
```

The guest mounts with the capture's two reserved blocks, reads the patterned
payload, examines its size and date, and enumerates all 81 entries in Docs.
The host checks the clean TROK marker, replay outside the marker sector, and
the recovered files with the independent image checker. A fresh Kickstart
machine mounts with the same handler and repeats the guest checks; the
complete resulting partition must match the first recovered image byte for byte.

## Original SFS2 image on 68000 and 68020

The `OriginalSfs2Image` tests port the full original-image workload at
research commit `4a902a1`. Supply the clean 16 MiB, two-reserved-block
partition created by SmartFilesystem 1.279. The archived original-06
fixture has SHA-256 `B11C46915C402D5A8E617FBC2F0BC2CD67549E4FD03F9363256148AD168D65B0`.

Set the Kickstart ROM, original SmartFilesystem and both CopperSFS handler
variables from the CSUN and TRST sections above. Build both CopperSFS handlers
for 68000 so the same executable runs on both emulated CPUs. Then run:

```powershell
$env:COPPERSFS_SFS2_REFERENCE_IMAGE = '<path to clean original-06/reference.hdf>'
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~OriginalSfs2Image
```

Each case reads a copy of the original image and enumerates its directory.
A second boot creates files and a directory, renames a file, sets its comment,
date and protection, overwrites across a sector boundary, grows the original
payload and deletes a file. A fresh CopperSFS boot on the selected CPU checks
the persisted bytes and metadata. Original SmartFilesystem independently
reads the same changed image on a 68020. Each read-only verification boot must
leave the complete partition byte-identical.

## Size and performance comparisons

`HandlerSizeChangesPreserveWorkloadCycles` compares frozen baseline and candidate
handlers on the same synthetic media through real Kickstart DOS/Exec. Set
`COPPERSHARP_KICKSTART31_ROM` and `COPPERFS_SIZE_MANIFEST`, then run:

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~HandlerSizeChangesPreserveWorkloadCycles
```

The JSON manifest has this shape; include all five named profiles (`ofs`, `ffs`,
`sfs`, `sfs2-csun`, `sfs2-trst`) when running the complete theory:

```json
{
  "Profiles": [
    { "Name": "ffs", "Baseline": "C:/handlers/before/CopperFFS", "Candidate": "C:/handlers/after/CopperFFS" }
  ],
  "ClassicBase": "C:/fixtures/clean-classic-sfs.hdf",
  "ClassicBaseSha256": "SHA256_OF_THE_CLASSIC_IMAGE"
}
```

For a family-specialization comparison, build the baseline with
`Build-Handler.ps1 -SharedDispatch` and the candidate without that switch.
For active-code comparisons, keep the family selection fixed and freeze the
two source revisions and compiler. Keep CPU and write policy fixed in both
comparisons. Both OFS and FFS profiles use the same CopperFFS binary. The classic SFS
image must be the independently formatted 16 MiB fixture used by the original
handler comparisons. Select individual theory cases with a `DisplayName` filter
when the manifest contains fewer profiles.

Each case rotates baseline/candidate order over three trials and separately
times mutations and verified reads/enumeration after mount. It checks identical
read/write/flush command counts and independently inspects persisted metadata.
Raw cycles and handler hashes are written beside the manifest. A mean cycle
increase above 0.1% fails; the allowance covers timer/interrupt placement and
does not establish performance for other workloads, CPUs or physical devices.

`ActiveCodeChangesPreserveSmallPacketCycles` uses the same manifest and ROM.
It creates and warms a small file before separately timing 512 seeks, 512
seek/one-byte-read pairs and 512 zero-length reads. Three trials rotate the arm
order; data, paired I/O counts and the same 0.1% cycle margin are checked.
Samples are saved under `small-packets/<profile>/cycles.json` beside the manifest.

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~ActiveCodeChangesPreserveSmallPacketCycles
```

`LargeWritesPreserveCyclesAndDiskOperations` uses the same manifest for `ofs`,
`ffs` and classic `sfs`. It first creates forty small files, then separately
times growing a 96 KiB file around another allocation, overwriting it in twelve
8 KiB packets, and reading and checking every byte. This covers multiple
FFS/OFS pointer tables and a classic SFS extent tree containing many records.
Three trials rotate arm order. The test requires identical paired device
command counts, validates persisted allocation and metadata with independent
walkers, and enforces the same 0.1% mean-cycle limit for each phase. Samples are
saved under `large-writes/<profile>/cycles.json` beside the manifest.

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~LargeWritesPreserveCyclesAndDiskOperations
```

## Geometry arithmetic checks

`GeometryMathInstructionTests` needs no ROM or external media. It source-links
the internal arithmetic helpers, checks unsigned boundaries and zero-divisor
policies, then compares native results with host oracles. MC68000 output runs
on both 68000 and 68020 CPU models; explicit MC68020 output is also checked.

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~GeometryMathInstructionTests
```

Set `COPPERFS_DIVREM_ARTIFACT_ROOT` to save generated assembly and per-vector
paired/separate cycle samples. These are synthetic arithmetic measurements;
the full-ROM workload probes above qualify filesystem performance separately.
See the [division optimization report](../../docs/divrem-optimization-2026-11-12.md)
for the measured results and the explicit MC68020 fusion limitation.
