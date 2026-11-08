# Kickstart storage matrix

New runs use `kickstart-storage-v2`, correcting the IDE partition fixture.
Historical `kickstart-storage-v1` records remain immutable and revalidatable.
The machine/backend/read-size selectors and default 1,188-workload count are
unchanged. See the [mount investigation](kickstart-sfs2-ide-mount-fix-2026-11-07.md)
for the demonstrated setup defect and the complete IDE rerun.

The C# benchmark exercises real Kickstart Exec/DOS and loaded filesystem handlers.
Its three storage paths are synchronous CopperHDF, deferred timed CopperHDF and
the native ROM `scsi.device` accessing an emulated read-only Gayle ATA master.
The IDE configurations fit no CopperHDF board. Payload hashes and observed PIO
bytes establish that guest instructions actually transfer IDE data.

This is a synthetic storage comparison, not a physical A1200 measurement. The
existing EC020 motherboard bridge and conservative IDE wait states are modeled
policies. Their AA-Gayle physical cycle accuracy has not been established.

## Machines and workloads

The default matrix contains eleven scenarios: the historical zero-latency control,
plus five devices on each A1200 memory profile. A1200 uses PAL Alice/Lisa,
M68EC020, licensed Kickstart 3.0 39.106 and either 2 MiB Chip or 2 MiB Chip plus
8 MiB Fast. The caller buffer explicitly uses Chip on stock machines and Fast on
expanded machines. The historical control retains its 40.63 ROM, M68020,
512 KiB Chip and 2 MiB Fast configuration.

| Packet size | Payload per workload | DOS read calls |
|---:|---:|---:|
| 64 B | 64 KiB | 1,024 |
| 512 B | 256 KiB | 512 |
| 4 KiB | 1 MiB | 256 |
| 64 KiB | 1 MiB | 16 |

Each of FFS, SFS2 CSUN and SFS2 TRST compares the original handler against Copper
cached and payload-bypass policies. There are three cold trials per workload:
11 scenarios × 3 filesystem profiles × 3 variants × 4 sizes × 3 trials = **1,188**.
Every trial constructs a fresh machine and drive, with an empty drive cache,
initial head position and spindle phase. Ordinary boot, mount and drive read-ahead
occur during that trial. The measured session covers open/read/close; the read
loop has a separate interval. Boot precedes those intervals; the first open
performs the cold filesystem mount. A fresh-machine workload does not issue the
legacy probe's inhibit/remount pair, which can race original SFS startup I/O.
All arms receive 32 mount buffers and a 64 KiB maximum transfer. Each handler
retains its native cache policy and memory budget. Copper cached/bypass arms use
the same HUNK, with the explicit mount control selecting the payload policy.

## Synthetic timing profiles

| Parameter | HDD-like v1 | CF-like v1 |
|---|---:|---:|
| Command overhead | 500 µs | 50 µs |
| Mechanical geometry | 4 heads, 64 sectors/track | none |
| Rotation | 3,600 RPM | none |
| Cylinder seek | 2 ms + 80 µs/cylinder | none |
| Head switch | 200 µs | none |
| Track skew | one sector | none |
| Drive read-ahead | 64 KiB | disabled |
| Abstract CopperHDF interface | 2 MiB/s | 8 MiB/s |

These assumptions are explicit and uncalibrated. One shared C# drive model runs
on the canonical 7,093,790-tick PAL machine clock. Rational spindle deadlines
avoid cumulative rounding drift. CF has no additional media delay; its bandwidth
limit is charged once by the abstract interface. Media availability and interface
transfer are pipelined. Bounded prefetch stops at cache capacity and cannot
overwrite an active command's protected sector buffer. Cache hits retain command
and interface costs. Host file duration never supplies guest latency.
CF retains logical CHS geometry for the native ATA driver; it applies no
mechanical seek, head-switch or rotation costs.
Head-switch cost applies within a cylinder; a cylinder seek covers positioning
when the cylinder changes. Track skew and spindle phase use the same integer
clock even while the drive is idle or prefetch is paused.
On a cache miss that redirects the stream, old speculative entries become
replaceable while remaining readable until replaced. They cannot pin capacity
needed by the new stream. The foreground sector and command/PIO staging buffers
remain protected independently.

Gayle data transfers use guest PIO instructions, split bus accesses and controller
wait states rather than a CopperHDF RAM-copy gateway. The initial model enforces a
five-mother-tick minimum per IDE bus access. This conservative policy does not
claim measured AA-Gayle timing or distinguish all fast/slow-bank electrical phases.

## Running and inspecting results

Build standalone handlers with `src/CopperFS.Amiga/Build-Handler.ps1`, retaining
their maps and allocation-analysis reports. Supply the resulting native benchmark
manifest and privately obtained original handlers and ROMs:

```powershell
./tools/Benchmark-KickstartSequentialReads.ps1 -Matrix `
  -NativeArtifactRoot <native-root> -ArtifactRoot <fresh-output-root> `
  -OriginalFfsHandler <FastFileSystem> -OriginalSfs2Handler <SmartFilesystem> `
  -KickstartRom <historical-40.63-ROM> -A1200Rom <39.106-ROM-or-ZIP> `
  -ReferenceProvenance <reference-provenance.json> -TestAssembly <test-DLL>
```

Existing invocations retain their historical defaults. Matrix selectors are
`-StorageBackend All|ZeroLatency|TimedCopperHdf|A1200Ide`,
`-MachineProfile All|Legacy|A1200Stock|A1200Fast`,
`-DriveProfile Both|Hdd|Cf` and `-PayloadCachePolicy Both|Cached|Bypass`.
The direct `Benchmark-KickstartStorageMatrix.ps1` launcher also accepts `-Workers`
(default four) and `-Smoke` (one cold 4 KiB trial per case, never full acceptance).
Concurrent workers own separate machines and files. Wall time is only a watchdog.
PowerShell only forwards arguments. Scenario selection, input validation,
manifest construction, worker execution and reporting are implemented in C#.
The runner can also be invoked directly:

```powershell
dotnet run --project tools/CopperFS.StorageBench -c Release -- matrix `
  --RepositoryRoot . --NativeArtifactRoot <native-root> --ArtifactRoot <fresh-output-root> `
  --OriginalFfsHandler <FastFileSystem> --OriginalSfs2Handler <SmartFilesystem> `
  --A1200Rom <39.106-ROM-or-ZIP> --LegacyRom <40.63-ROM> --TestAssembly <test-DLL>
```

Direct options use the same names as the matrix launcher, with `--` prefixes.
The BCL-only option tests run in `CopperFS.slnx`; they need no ROM or emulator.

### A1200 Kickstart 3.1 and targeted SFS2 comparisons

`-A1200Kickstart 3.0|3.1|Both` selects A1200 ROM configurations; the default
remains 3.0. `-A1200Rom31` (or `COPPERSCREEN_UPGRADE_A1200_ROM31`) supplies a
licensed raw or ZIP A1200 3.1 image. The 3.1 profile pins version 40.68 and the
unmodified A1200 model identity, using SHA-1
`E21545723FE8374E91342617604F1B3D703094F1` from the
[WinUAE ROM identification database](https://github.com/tonioni/WinUAE/blob/master/rommgr.cpp).
SHA-256 remains the recorded benchmark input hash. A500 40.63 and A4000 40.68
images cannot substitute for the A1200 image.
Amiga Forever `AMIROMTYPE1` files use the user's `rom.key` beside the selected
ROM or ZIP. Decoding occurs in memory; the key and decoded ROM are not copied to
the benchmark artifacts. Image identity is verified after decoding.

New scenarios have separate IDs such as `stock-ks31-ide-hdd` and
`fast-ks31-ide-cf`. Results stay separate from the 39.106 matrix and historical
40.63 control. Selecting `Both` adds ten A1200 scenarios, with the historical
control retained once.

`-FileSystemProfile All|Ffs|Sfs2|Sfs2CSUN|Sfs2TRST` selects the filesystem builds.
Its default remains `All`. The targeted missing-IDE comparison is:

```powershell
./tools/Benchmark-KickstartStorageMatrix.ps1 `
  -NativeArtifactRoot <frozen-native-root> -ArtifactRoot <fresh-output-root> `
  -OriginalSfs2Handler <SmartFilesystem-1.279> `
  -Backend A1200Ide -MachineProfile All -DriveProfile Both `
  -A1200Kickstart 3.1 -A1200Rom31 <licensed-A1200-40.68-ROM-or-ZIP> `
  -FileSystemProfile Sfs2 -PayloadCachePolicy Both -TestAssembly <test-DLL>
```

This schedules **288 cold workload trials**: two RAM profiles × two drive
profiles × two SFS2 builds × original/cached/bypass × four sizes × three trials.
The unchanged workloads are 64 B, 512 B, 4 KiB and 64 KiB. This subset needs
neither the original FFS binary nor either older ROM. `Profiles` in the manifest
controls case discovery and report receipt validation; older manifests retain
all three filesystem profiles. Native geometry compatibility is checked during
execution rather than assumed from the ROM version. Any confirmed failure
records its actual ROM revision and remains unavailable coverage.
The native 40.68 whole-device geometry diagnostic returns `IOERR_NOCMD` (-3),
whereas 39.106 returns -1. That result does not prevent original SFS2 from using
an explicit partition. New v2 IDE runs require successful original-handler
reads and treat any mount failure as a failed workload.

`-TimingProfiles` accepts JSON containing optional `Hdd` and `Cf` objects with
the `LightweightDriveTimingProfile` property names, including `Version: 1`.
Unspecified properties retain the selected HDD/CF preset. Unknown fields are
rejected. Per-case `configuration.json` preserves the
effective timing parameters, RAM, CPU, ROM hash and IDE wait policy.

The C# `tools/CopperFS.StorageBench` runner starts bounded scenario workers and
validates every expected size/trial and successful test receipt before writing
`report.md`, `summary.csv` and `measurements.json`. A failed or partial run is not
acceptance. Native capability failures are retained separately in
`unavailable.json` in historical v1 records; they have no throughput or invented
A/B ratio. New v2 IDE runs cannot accept these in place of reads. Revalidate an
existing complete run with:

```powershell
dotnet run --project tools/CopperFS.StorageBench -c Release -- report <matrix.json>
```

Revalidation verifies identical existing reports; differing reports are preserved
and rejected. Execution refuses a directory that already contains run records.

Raw results retain bus-clock and CPU/native-clock timelines, guest seconds,
transport read commands/bytes, physical backing fetches/bytes, PIO bytes,
completion counts, buffer placement and mount control. CPU timelines include
stalls and waits; they are not retired instruction cycles. Source snapshots,
harness assembly hashes and handler/probe/ROM/media/payload/timing hashes accompany
the trials. Compare cached/bypass/original only within matching machine, ROM and
device configurations. Trial minima and maxima remain visible alongside medians.
Media fetches describe the modeled drive's backing reads; they do not measure
physical host-disk requests or Windows cache misses.

### IDE partition fixture

V2 IDE media contains one reserved 128 KiB cylinder followed by the unchanged
16 MiB filesystem extent. The manually registered `DosEnvec` describes sectors
256 through 33023: `LowCylinder=1`, `HighCylinder=128`, two surfaces and 128
sectors per track. It is an explicit mount; no RDB auto-mount or filesystem
driver is injected into the ROM. ATA retains its configured four-head, 64-sector
logical geometry; both descriptions have 256 sectors per cylinder. CF still
has no mechanical seek or rotational timing.
SFS2 primary and backup root records have absolute `firstbyte=131072` and
`lastbyte=16908288`, with recalculated SFS2 checksums. Relative filesystem block
numbers, extents and every payload byte remain unchanged. FFS contents are copied
without filesystem edits. CopperHDF fixtures retain their flat layout.

The original SFS handler tests its computed partition start. A zero start selects
whole-device capacity probing with `TD_GETGEOMETRY`; a nonzero start uses the
provided partition bounds. The old flat fixture therefore failed with DOS 225
on both ROMs despite having a high cylinder in its environment table. Updating
only the mount start is insufficient: stale absolute SFS root byte ranges also
prevent mounting. The corrected fixture passes through the original handler
and native ROM without emulation changes, ROM patches or handler patches.

Every v2 IDE measurement and configuration records `MediaLayout`,
`PartitionFirstSector` and `PartitionSectors`. The report validates them across
all arms before comparison. A native mount failure now fails the test rather
than being classified using a separate geometry probe. That diagnostic is
disabled in acceptance runs. Historical v1 flat-fixture reports still contain
their 1,092 read trials and 96 unavailable original attempts; these do not
establish general ROM/handler incompatibility and must not be mixed with v2
partitioned-media results.

## Filesystem policy and device boundaries

`0x43504301` opts `copperhdf.device` mounts into payload bypass. `0x43504302`
does the same for diagnostic `scsi.device` mounts. Both require a complete
environment table and an exact device name. Other controls/devices remain cached.
Metadata, partial-sector reads and write overlays preserve their existing paths;
whole aligned payload sectors may bypass the filesystem cache. Normal mounts
remain cached. Portable cores have no emulator or application dependency.

Timed CopperHDF uses a fixed 64-outstanding-request budget per configured unit,
including unconsumed completions, and a preallocated 1 MiB command staging buffer.
Larger or unaligned timed reads return `IOERR_BADLENGTH`; benchmark transfers are
bounded to 64 KiB. Deferred requests clear `IO_QUICK`, become `NT_MESSAGE`, publish
data/error/actual together at completion, then enter a bounded completion mailbox.
A C# interrupt server compiled with CopperSharp68k invokes real Exec `ReplyMsg`.
Aborts complete once; machine reset discards the previous guest's queues and
caches. Units without timing retain the existing synchronous path.

The ATA master supports IDENTIFY, CHS/LBA28 READ/READ MULTIPLE, multiple setup,
initialize parameters, diagnostics/reset, seek/verify/recalibrate and the bounded
read-ahead/PIO feature controls used by the ROM. Main status acknowledges ATA IRQ;
alternate status and peeks do not. Gayle has a separate masked interrupt latch.
An absent slave floats high. Unsupported commands, writes, DMA, ATAPI and LBA48
are rejected; no writable ATA path is provided.

Register decode and ordering reference the [Commodore Gayle specification,
sections 7 and 16](https://www.amigawiki.org/lib/exe/fetch.php?media=de:parts:gayle_specification.pdf),
the [T13 ATA/ATAPI draft](https://courses.cs.washington.edu/courses/cse451/16au/readings/ata.pdf)
and bounded native 39.106 traces. The available Gayle document describes an earlier
68000-era part; AA fast-bank byte lanes, interrupt details and precise electrical
timing still need hardware qualification. Native traces establish compatibility
for this ROM and workload, not physical cycle accuracy.

No packages, ROMs or media are published. Historical benchmark records are immutable.
