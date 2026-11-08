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
