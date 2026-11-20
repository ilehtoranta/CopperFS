# Bounded deferred TRIM qualification, 2026-11-12

Automatic TRIM is implemented as optional idle maintenance after normal commit.
It remains **disabled by default**. Original-handler parity is still a release
gate, and physical-device qualification is unavailable. This work publishes no
handler package, ROM or media image.

## What changed

The 128-entry queue receives locally accumulated free ranges instead of a
callback for every freed block. Insertion only extends the last range or
appends. Hints are volatile and best effort; overflow cannot affect deletion,
commit or recovery. Recycling an SFS file does not unallocate its data.

Additional permanent TRIM storage is 2,148 bytes per mount, including a
once-allocated 512-byte DMA command buffer. The queue, prepared batch and small
state records use the rest. Existing filesystem scratch and deferred timer are
reused. Allocation failure disables maintenance without failing the mount.

Collectors do not call recovery. SFS/SFS2 require the completed normal commit,
clean marker and durability boundary; FFS/OFS require valid allocation metadata
and a successful barrier. The batch is bound to the mount and mutation
generation. Any foreground operation invalidates it. Current bitmap words are
checked before each command, including staged remainders. No authorization
survives a yield to mutation, recovery or external access.

FFS uses one forward cursor to locate touched bitmap addresses and one shared
layout validation walk for the batch. Both share the 128-pointer budget. The
walk also records the addresses needed for root/bitmap self-allocation checks,
so those checks cannot hide another unbounded pointer-chain traversal. Partial
validation is abandoned if a foreground operation interleaves.

The event loop checks for foreground packets before every maintenance turn.
Each batch examines at most eight hints and 4,096 filesystem blocks. The
transport sends at most one UNMAP, with up to eight descriptors and an aggregate
block-count limit, then returns to the event loop. All alignment is inward.
Capabilities, including unsupported status, are cached per attachment.
Transport failure invalidates them immediately; submitted ranges remain intact
until cache invalidation has completed after a possibly partial discard.

The implementation and caller-owned context contract are described in
[automatic-trim.md](automatic-trim.md). Disk formats, TRST/CSUN recovery policy,
manual CopperTrim and its packet ABI are unchanged.

## Final candidate inputs

The ignored `artifacts/trim-design/release-qualification/manifest.json` pins the
candidate and original-handler paths. Original binaries remain hash-pinned to
FFS 40.1 (`9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773`)
and SmartFilesystem 1.279 (`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`).

| Opt-in handler | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS, OFS/FFS | 80,704 | `E31674B4B3A33B326D5BCBCC69AE73DA28052C0244B564F790BA769587AA4896` |
| CopperSFS, SFS | 155,812 | `4BACAD326DB5A5D2E0FBA06365F64A9B385585BDCBC6AD43824BEC51AD14153E` |
| CopperSFS, SFS2 CSUN | 250,760 | `8BACD24F5DCF73F28A6417A2AF73D96B7DD8AA10BE5B4759060AD174759DE7DA` |
| CopperSFS, SFS2 TRST | 206,612 | `FE72CF9B402501CD187A386B752A57F57D819D112EDA25AAE3B7B73D9230B9E9` |

Build directories are `artifacts/trim-design/release/ffs-final`, `sfs`,
`sfs2-csun` and `sfs2-trst`. They retain input assemblies, maps and CILica
reports. Each build uses scalar replacement with inlining disabled:
CILica `0.20.2-preview`, revision `ca199fd583f492e156f2617dff43b5f28ff8684d`.
These are experimental AutoTrim binaries, not newly published versions.

## Measurement method

All five configurations use Kickstart 3.1 and emulated 68020 execution. ROM
workloads use a 16 MiB volume and 512-byte filesystem blocks. Fast/Chip payload
memory is explicitly allocated before loading the handler. The original parity
gate uses three alternating original/candidate trials, includes ACTION_FLUSH
in mutation timing, and independently checks every resulting image.

Diagnostic instruction scopes separately price initialization, notification
callbacks, normal commit and automatic service. These scopes exclude the
five-second wait. Callback cycles alone do not include the new local range
accumulation; the complete foreground deletion is measured separately and
combined with service when comparing total work. Diagnostic scalar execution
does not replace the unmodified frame runner used by the parity gate.

The baseline comparison reruns the previous candidate binaries with the same
new probe, saved in `artifacts/trim-design/comparison-old`. It does not compare
different guest workloads or substitute the earlier callback-only profile for
whole foreground timing. `build/Summarize-TrimProfile.ps1 -ArtifactRoot <root>`
generates `trim-summary.json` from completed five-profile diagnostic runs.

The extended matrix deletes a 2.25 MiB file, sixteen interleaved 4 KiB files
while retaining their neighbours, and two successive 32 KiB files on one
attachment. Fragmented cases use Chip RAM; large and warm cases use Fast RAM.
Reports include foreground deletion cycles, submitted discard bytes, commands
and capability discovery. Submitted bytes measure requested reclamation, not
physical SSD erase completion; FFS also frees header/extension blocks.

## Measured work and reclamation

For the same 32 KiB allocation/deletion probe, deferred service is 48–61% cheaper.
The complete foreground deletion also improves. Combining foreground deletion
and service gives a 6.8–23.4% reduction; this includes local range tracking and
does not move unreported work outside the measured interval.

| Profile | Previous deletion cycles | New deletion cycles | Previous service cycles | New service cycles | Combined change |
| --- | ---: | ---: | ---: | ---: | ---: |
| OFS | 2,378,890 | 2,344,400 | 376,863 | 184,674 | −8.23% |
| FFS | 628,680 | 582,800 | 312,155 | 137,944 | −23.39% |
| SFS | 983,450 | 953,790 | 121,385 | 63,086 | −7.96% |
| SFS2 CSUN | 3,400,710 | 3,336,240 | 306,093 | 118,553 | −6.80% |
| SFS2 TRST | 2,039,720 | 1,979,850 | 329,452 | 144,734 | −10.32% |

The new one-time staging initialization costs 2,535–2,576 emulated cycles,
separate from the table. First-use discovery adds about 15,000 cycles, already
included in service above. Warm-mount runs perform zero repeat capability
queries. The command buffer is retained instead of allocated each service.

| Profile | Free-range callbacks, old → new | Preparation/validation cycles | Dispatch + invalidation cycles | Device reads / barriers | UNMAPs | Submitted bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OFS | 69 → 3 | 104,334 | 52,264 | 2 / 1 | 2 | 35,328 |
| FFS | 65 → 3 | 81,162 | 28,710 | 0 / 1 | 2 | 33,280 |
| SFS | 64 → 1 | 23,933 | 11,988 | 0 / 0 | 1 | 32,768 |
| SFS2 CSUN | 64 → 2 | 55,089 | 26,816 | 1 / 0 | 2 | 32,768 |
| SFS2 TRST | 64 → 2 | 62,645 | 46,090 | 2 / 0 | 2 | 32,768 |

These are inclusive method-family costs within service; discovery, queue and
scheduling work account for the remainder. Normal journal commit belongs to
the foreground or existing write timer, not to automatic validation. Zero
device reads/barriers means retained metadata and an already completed barrier
were reused, not that validation or durability was omitted. Every clean-mount
automatic service profile has zero recovery calls. Each turn contains at most
one discard. FFS preparation tests also verify a single barrier across resumed
layout/descriptor turns.

All fifteen extended matrix cases pass. Every case makes exactly three
capability-discovery commands in total, including the two-delete warm workload.

| Profile | Large deletion bytes / UNMAPs | Fragmented deletion bytes / UNMAPs | Two warm deletions bytes / UNMAPs |
| --- | ---: | ---: | ---: |
| OFS | 2,510,336 / 10 | 81,920 / 17 | 70,656 / 4 |
| FFS | 2,392,064 / 10 | 73,728 / 18 | 66,560 / 4 |
| SFS | 2,359,296 / 3 | 65,536 / 16 | 65,536 / 2 |
| SFS2 CSUN | 2,359,296 / 5 | 65,536 / 17 | 65,536 / 4 |
| SFS2 TRST | 2,359,296 / 3 | 65,536 / 16 | 65,536 / 4 |

The corresponding payloads are 2,359,296, 65,536 and 65,536 bytes. Extra OFS/FFS
bytes include filesystem metadata and OFS block overhead. These counters are
submitted ranges, not a claim about physical-device erase latency. Queue
saturation is tested separately: 200 disjoint hints retain 128 and drop 72,
without I/O or changing filesystem results. Fragment overflow is best effort;
the bounded staging record is never enlarged to preserve hints.

The five full foreground diagnostic profiles also pass. Inclusive commit/
finalization scopes below include their I/O and checksum callees. This is the
complete parity workload, not the separate 32 KiB deletion probe above:

| Profile | Full foreground cycles | Commit/finalization family | Calls | Inclusive cycles |
| --- | ---: | --- | ---: | ---: |
| OFS | 42,276,120 | `Finish` | 26 | 642,161 |
| FFS | 21,773,810 | `Finish` | 26 | 584,920 |
| SFS | 22,542,690 | `CommitTrst` | 36 | 3,580,387 |
| SFS2 CSUN | 43,302,370 | `Finish` | 26 | 3,928,404 |
| SFS2 TRST | 33,722,730 | `CommitTrst` | 26 | 6,472,781 |

These families do not include all transaction work: log construction, data I/O
and allocation changes also occur elsewhere in the foreground profile. The
complete method and instruction records are retained under
`release-qualification/cpu-profile/<profile>/candidate`. Automatic service no
longer repeats those commits or clean-mount recovery.

## Original-handler parity

Positive percentages below mean more emulated cycles than the original.
All ten parity cases fail, including the existing 0.1% interrupt allowance.
Automatic TRIM therefore remains disabled by default.

| Profile | Fast mutation + flush | Fast verification | Chip mutation + flush | Chip verification |
| --- | ---: | ---: | ---: | ---: |
| OFS | +179.256% | +10.730% | +179.208% | +10.983% |
| FFS | +78.958% | +3.600% | +78.986% | +3.865% |
| SFS | +28.969% | +14.046% | +28.894% | +14.282% |
| SFS2 CSUN | +177.468% | +18.633% | +177.709% | +18.890% |
| SFS2 TRST | +116.248% | +19.112% | +116.341% | +19.363% |

The parity backend does not support UNMAP. These rows measure the filesystem
workload and opt-in bookkeeping/unsupported discovery. The supported-device
maintenance costs and requested reclamation are reported separately above.
The large filesystem gaps predate this redesign; cheaper TRIM does not make
the candidate pass the original-handler gate.

## Qualification coverage

- Host: Release solution build passes with no warnings. The final full solution
  run passes 1,476 tests and skips three external-image tests. Subsequent FFS
  budget/self-allocation changes pass its entire 286-case suite (three additional
  cases). The SFS suite includes 1,143 passes and three external-image skips.
- Native: all four standalone handlers build for 68000, using CILica scalar
  replacement without inlining. Eight instruction cases pass on 68000/68020,
  covering permanent allocation failure, lifecycle/generation invalidation,
  overflow, transport failure and eight-descriptor UNMAP with high LBAs.
  CopperStart's selected buffer, geometry and size replays pass 193 cases with
  `-p:CopperStartNativeSfs2=true`.
- Full ROM: 23 automatic-maintenance cases and 22 compatibility/recovery cases
  pass for the final hashes above. They cover poisoned free space, reallocation,
  recycling, partial UNMAP failure, cuts during discard, inhibit/remount,
  original-handler readback, nine mutation/clean-handoff profiles (including
  OFS/FFS DD and HD), and both SFS2 journal policies' published-log recovery.
  The additional 23 startup/manual cases pass after correcting a stale test
  assertion: both FFS binaries correctly reject dirty SFS without writes; only
  the SFS2 handler is expected to recover it. The affected two cases were rerun.
- Commit-barrier cuts: all four final-ROM deletion/truncation cases pass for
  SFS2 TRST and CSUN. They cut at each distinct durable prefix, recover/reopen
  it, and verify the appropriate before/after file manifest. TRST is also
  recovered by the original handler. The wider 19-case journal run on the
  preceding build passed too; that evidence predates the final transport-fault
  and FFS traversal fixes and is not substituted for the final four cases.
- Physical devices: not run; not counted as passes.

Host coverage also includes failed barriers, pending overlays, dirty markers,
recycled-file retention, reallocated data, bitmap boundaries, aliases,
permanent metadata, alignment, aggregate limits and unsupported devices.
The final instruction run caught and fixed a 68000 CDB alignment issue: the
UNMAP parameter length at odd CDB byte 7 is now written as two bytes.

## Reproduction and limits

Build each profile with `build/Build-Handler.ps1 -AutoTrim`, a distinct
`-ArtifactRoot`, and the appropriate format flags from `automatic-trim.md`.
Supply a local ROM through `COPPERSHARP_KICKSTART31_ROM`, and point
`COPPERFS_AUTOTRIM_MANIFEST` and `COPPERFS_CORRECTNESS_MANIFEST` at the local
manifest. Set `COPPERFS_TRIM_PROFILE=1` only for diagnostic runs; clear it for
the parity gate. Compile instruction tests with `-p:CopperFsAutoTrim=true`.
Use `-AssertBounded` with `Summarize-TrimProfile.ps1` to reject a diagnostic
service scope containing recovery or more than one discard command. UNMAP
descriptor and aggregate limits follow the
[Seagate SCSI interface manual](https://www.seagate.com/files/staticfiles/support/docs/manual/Interface%20manuals/100293068L.pdf).

The release matrix uses `AutomaticTrimAfterDelete`,
`AutomaticTrimBoundedIdleTurns`, `AutomaticTrimStops`, `PowerCutDuringAutomatic`,
`SharedMutationsAndEveryCleanHandoff`, `BothSfs2HandlersRecoverThePublishedJournal`,
the foreign-startup/manual-packet tests, and both `ReleaseCandidate*StaysOnPar`
methods. Commit-cut tests additionally use `COPPERFS_SFS2_TRST_HANDLER`,
`COPPERFS_SFS2_CSUN_HANDLER` and `COPPERFS_ORIGINAL_SFS`.

Host fixtures cover larger allocation layouts, including a sparse 50 GB FFS
layout and 8/128 MiB SFS2 volumes with 1,024-byte blocks. They are synthetic
safety/work-budget evidence, not original-handler interoperability on those
volume sizes. The ROM transport advertises one UNMAP descriptor; eight-descriptor
encoding, aggregate limits and high LBAs are separately executed as native
68000/68020 instruction tests and host transport tests.

No physical SSD/controller, DMA bus or real hot replacement is available.
Geometry reset and inhibit/remount tests verify software invalidation; they do
not qualify physical media replacement. Synchronous UNMAP completion latency
remains device-dependent. A packet arriving during an issued command waits for
that command, and continuous foreground traffic may postpone reclamation.
Hints dropped on overflow or invalidation are not replayed after a crash.
