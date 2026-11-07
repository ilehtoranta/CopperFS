# Selective research port (2026-11-04)

Source: CopperSFS research commit `4a902a1ff9189bbdff841c23441c66678a6e2427`,
retained in the history of `codex/preserve-local-research` in CopperFS. Baseline:
`a110fcf8e586287c795e0256c70d2147672becf8`. This port changes host tests and
diagnostic tools; it does not replace filesystem or native handler implementations.

## Research PR reconciliation

PR #1 originally proposed the complete research snapshot. Its conflict resolution
retains the validated selective port from main, including the deferred findings
below. Unported experiments and obsolete implementations remain available at
[the immutable research snapshot](https://github.com/ilehtoranta/CopperFS/tree/4a902a1ff9189bbdff841c23441c66678a6e2427).
The PR branch now incorporates main; use that commit, rather than the moving
branch tip, to inspect or extract the original research. This reconciliation
changes documentation only relative to main and does not import deferred code.

## Included

- Crash-state Cartesian products, byte-verified corpus containment and their tests.
- Reliability simulation with guarded partition boundaries, read/write/flush failures,
  torn writes and restart checks. Selected exhaustive mutations: create, overwrite,
  append, shrink, truncate, delete and rename. Seeded 300-operation reference-model
  tests and torn-write scenarios remain separate tests.
- Block-size round trips; SFS2 read-only, metadata recovery, batched writes and
  wide-file tests; image-checker corruption cases.
- Optional recovery of a transaction captured from the original SFS2 handler.
- `CopperSFS.Check`, a read-only checker for a raw partition image, not a whole
  partitioned disk; `CopperSFS.Bench`, an in-memory diagnostic study runner.
- Dated research documents under `docs/history/research-20261024`. Their results
  and commands describe the old branch and are not current qualification.

The existing mainline StudyDisk, StudyFixture, discard simulator and image checker
were moved into the tools and shared with tests through project references.
Their implementation was retained. New simulator adapters implement the current
metadata/data I/O contract; the wide simulator also implements the TRST overlay.
No CopperStart or CopperScreen build dependency was added.

## Running

```powershell
dotnet build CopperFS.slnx -c Release
dotnet test CopperFS.slnx -c Release
dotnet run --project tools/CopperSFS.Check -c Release -- partition.img 512 1
dotnet run --project tools/CopperSFS.Bench -c Release -- --mode benchmark --repetitions 3 --output artifacts/study.json
```

The two `FullBatchesRelocationAndCutsPreserveEveryFile` cases are tagged
`Category=Extended` and can take many minutes each. The unfiltered solution
command includes them. For a routine run use `--filter "Category!=Extended"`;
run `--filter "Category=Extended"` separately to retain the full coverage.

The study requires a fresh output filename. Modes are `benchmark`, `recovery`,
and `all`. It hashes source and binaries and reports failures, not valid timings,
when setup or integrity checks fail. These timings are host CPU costs on an
in-memory device, not host disk or Amiga throughput.

Exhaustive wide-volume crash enumeration is included in the default suite.
A focused run passed all three scalar, batched and TRST cases; separate quick
round trips also verify wide writes, resize and reopen.

External image tests use `COPPERSFS_SFS2_REFERENCE_IMAGE`; captured redo recovery
uses `COPPERSFS_SFS2_NATIVE_REDO_IMAGE`. Missing images are skipped, not passes.
No licensed media is included. `COPPERSFS_RELIABILITY_ARTIFACTS` optionally names
an ignored output directory for failed synthetic images and context.
`COPPERSFS_MAX_CRASH_STATES` is a safety bound; exceeding it fails, rather than
silently sampling an allegedly exhaustive test.

## Deferred differences found during the port

These are unresolved research-versus-main differences, not dismissed failures or
proof that the old implementation should replace main. Original assertions remain
available at the source commit. No assertions were weakened to hide these cases.

| Research suites / cases | Observation and follow-up |
| --- | --- |
| FileSystemCompatibilityTests | Packed containers and embedded-null comments are resolved by the correction below. The extent-owner and metadata-relocation correction below covers the remaining two cases. The initial benchmark failed its setup checker with `Block 6: incorrect node fullness flag`; the node-fullness correction below resolves that finding. |
| FileSystemRegressionTests | Node-branch growth and recovery regressions are ported. The historical failure was the node-fullness flag corrected in 6979849. |
| TransactionRegressionTests | Archive-bit updates, empty-container unlinking and read visibility are covered by the classic transaction correction below. |
| ReliabilityTests advanced growth scenarios | Admin growth now uses reachable directories and children to fill the two initial admin ranges. The regression detects the mutation that first uses a third range, reopens both images, and replays that mutation through the crash matrix. Extent growth fills the 35-record root leaf with live file extents, verifies the next write produces a two-child branch after reopen, and exercises mutation and recovery failures at the split. The near-full fixture appends one committed block at a time until 20 blocks remain. It reopens that image, attempts an 8 KiB append, and checks that disk-full recovery preserves both the file contents and the free-space count. At each near-full mutation fault, the harness varies pending versions of every original block read by recovery or by the independent checker. It repeats when those choices expose additional blocks. Recovery cuts use the full persistence product. The matrix also covers creating and removing directories, renaming a directory with a child, growing the directory container chain, growing a file by resize, and updating comments, protection and dates. |
| BatchedWriteTests, RelocationTests, ReservedOverwriteTests | Old range-count and scratch-buffer assumptions differ from current scalar/range APIs and small-write policy. Main's existing batching/relocation tests remain intact. |
| TrstWriteTests | Hard-coded marker position, four-flush expectation and journal-space assumptions differ from current TRST placement and reservation. Main's existing TRST tests remain intact. |
| Native reference runners and original-handler handoff suites | Classic CopperSFS-to-original TRST recovery is ported to the current Kickstart harness (see below). Other historical native runners remain in the research snapshot. |

Classic historical zero first-extent backlinks are accepted by main's checker.
The old test rejecting that representation was omitted; the existing wrong-owner
corruption case remains. The original port's benchmark failure is retained in its historical receipt.
The subsequent node-fullness fix makes the current benchmark pass.

## Classic TRST handoff to original SmartFilesystem

The optional Kickstart regression ports the seven transaction scenarios from
research commit 4a902a1. It generates a synthetic 16 MiB classic SFS volume,
interrupts CopperSFS at every write/flush event, and boots each distinct dirty
image with either none or all of the pending writes persisted. The guest mounts
the image using original SmartFilesystem 1.279. For overwrite, append and first
write, it reads and checks the resulting 1,024 bytes through DOS. For all seven
operations the host checks the persisted TROK marker, file and directory
metadata, file hashes, and free-block count against the uninterrupted result.

The native run passed all seven scenarios, recovering 37 distinct dirty images
across 87 write/flush cut points.

Set COPPERSHARP_KICKSTART31_ROM to the local decoded Kickstart 3.1 ROM and
COPPERFS_ORIGINAL_SFS to the extracted original SmartFilesystem executable
(SHA-256 C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205).
With CopperSharp68k and CopperScreen beside CopperFS, run:

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~OriginalHandlerRecoversCopperSfsTrst
```

## Classic batching boundaries

The two boundary cases from research commit 4a902a1 run in
`BatchBoundaryTests`. With four free blocks, a 4 KiB overwrite uses 1 KiB
batches. The regression observes the first batch in a crash image, checks
the disk-full error and zero reported bytes, then verifies the original file
hash, metadata and allocation count after recovery at every write/flush cut.

A second fixture starts with a single 65,535-block extent. The two-block
append from the research suite and an eight-block append exercise the current
small-write and batched paths. Both must retain the full extent, link its
successor in both directions, and preserve the complete file across reopen.

## Node-fullness correction (2026-11-04)

Classic node allocation and removal now recompute fullness along the modified
node's ancestor path inside the existing transaction. Each parent's low pointer
bit is updated only when its child changes between full and non-full. The bounded
traversal uses caller-owned scratch and the current cached block size; parent
updates use the existing journal/overlay path. On-disk layout is unchanged.

The helper and two leaf-boundary regressions were selectively adapted from research
commit 4a902a1. Additional tests cover buffered fill/free/reopen and every write
and flush cut/failure during fullness transitions under both CSUN and TRST.
The original wrong-fullness corruption checks remain intact.

The final Release build passed with zero warnings/errors. The full solution run
passed 1,126 tests with three external-media tests unavailable; the subsequently
added buffered regression passed in the seven-test focused run, for 1,127 distinct
passing host tests across validation runs. Both default TRST and legacy CSUN 68000
handlers compiled and passed CopperStart's native classic TRIM/rewrite/cold-reboot
replay on Windows/NTFS, with no skips. These existing guest replays are integration
checks; the new node-boundary crash/failure regressions are managed tests.
The combined benchmark/recovery command passed all 98 benchmark configurations
and 40 recovery scenarios, with source hashes unchanged during execution.
No real-disk or Amiga throughput improvement is claimed.

Current validation receipts are under `artifacts/node-fullness-*`. The benchmark
is a synthetic in-memory host study; passing it establishes workload integrity,
not real-disk or Amiga performance. Other deferred compatibility findings above
are outside this fix.

## Packed-container and comment correction (2026-11-04)

Classic SFS now accepts complete object records up to the block boundary, including
containers with fewer than 27 unused tail bytes. Insertion and comment growth can
use that final space without reserving an extra sentinel record. Every occupied
record must still have a nonempty, terminated name and a terminated comment;
malformed strings fail validation. The on-disk format is unchanged.

Length-delimited comments containing NUL are rejected with BadNumber before the
metadata operation prepares or edits disk state. Empty comments and the maximum
79-byte comment remain supported. SFS2 already rejects embedded NULs and is unchanged.

The parser correction and compatibility fixtures were selectively adapted from
research commit 4a902a1ff9189bbdff841c23441c66678a6e2427, specifically
FileSystemCompatibilityTests. The original 13 imported cases reproduce 11 failures
on unmodified main and all pass with the correction. Expanded coverage checks
exact-end insertion/comment growth, empty/maximum comments, every write/flush cut
and failure for create/remove/rename/comment under CSUN and TRST, and buffered
mutations followed by rejection, flush and reopen. A rejected buffered comment
must preserve previously acknowledged edits and neighbouring objects.

The Release solution build passed with one existing CS8602 warning in the sibling
CopperSharp compiler. The complete solution suite, including both extended batch
cases, passed 1,153 tests; three external-media tests were unavailable. The focused selection passed 30 tests (26 new cases and four
existing packed-object cases). The combined study passed 98 benchmark configurations
and 40 recovery scenarios with unchanged source hashes. Concurrent host builds mean
these results establish integrity only; no timing or throughput claim is made.

Both default TRST and legacy CSUN 68000 handlers compiled successfully and passed
CopperStart's classic write/delete/manual-TRIM/rewrite/cold-reboot replay on
Windows/NTFS, one pass per variant with no skips. CopperTrim was rebuilt as well.
Native builds used isolated intermediates after a shared-build collision; no build
script changes were needed. These guest replays are integration coverage; the new
packed-boundary and invalid-comment checks are managed regressions. Linux/macOS
and external original-handler images were not exercised.

General metadata relocation when a container genuinely lacks space remains
outside this correction. Validation receipts use the ignored artifacts/packed-*
paths; managed boundary/crash tests and native integration replays are reported
separately.

## Extent owner tags and metadata relocation (2026-11-06)

Classic SFS now records the owning object node with its high bit set in every
new first extent. Later extents point back to the previous extent. The insertion
helper applies this rule to scalar and batched writes, contiguous allocation and
resize. Older files with a zero first backlink remain readable and writable.

When a longer comment no longer fits, SetComment moves the object within its
parent directory in the same transaction and updates its node's container pointer.
The object keeps its node, name, hash-chain position, attributes and file data.
Rename and recycling also keep the source record in the data scratch block so
the allocator can use bitmap scratch when the directory needs more admin space.

The compatibility and crash regressions in ExtentOwnerAndMetadataTests come
from research commit 4a902a1. They cover native first backlinks, comment growth,
rename of files and directories, admin-pool expansion and recovery after each
write or flush interruption. Additional cases check contiguous allocation and
reopen after buffered and TRST relocation. Local validation logs are under
`artifacts/owner-*`.

## Node-branch growth investigation (2026-11-06)

The three classic SFS cases in NodeBranchGrowthTests are adapted from
FileSystemRegressionTests at 4a902a1. All three reproduced an incorrect parent
fullness flag on ec93282, passed on the immediately following fullness fix
6979849, and passed on main at 536b882.

The flat and nested fixtures each create 300 files and resolve every name to its
original node after remount. The failure-injection fixture fills two 49-node
leaves, then verifies that the next create installs a third leaf starting at
node 99 in the existing index. Across 36 write/flush boundaries, the test checked
321 crash images from power cuts and transient or persistent I/O failures.
Recovery produced either the complete old or new directory and was byte-stable
on a second recovery.

## Classic transaction correction (2026-11-06)

TransactionRegressionTests was ported from research commit 4a902a1. Eleven of
its 24 cases failed on main at 8f06e4c. A successful nonempty write or size
change now clears the archive bit inside the content transaction. Zero-length
writes, same-size resizes and truncation of an empty file preserve the bit.

Removing the last object from a container repairs the adjacent links or the
parent's first-container pointer, then frees the admin slot in the same
transaction. The tests exercise head, middle, tail and only-child removal,
same-directory rename with immediate reuse, and recovery after interrupted
writes and flushes.

Classic file and metadata queries check the transaction marker before starting
traversal. A failed strict mutation blocks reads with DiskNotValidated until
recovery. The live mount may still read a complete acknowledged version after
a failed write-back or grouped TRST checkpoint while its matching overlay is
visible. A remount must recover from the on-disk marker. Callers invalidate
read and enumeration cursors on mutation and media change.

The Windows Release build passed. The solution suite passed 1,250 tests and
skipped three external-media cases. CopperStart's native classic SFS replay
passed separately with rebuilt 68000 TRST and CSUN handlers. Each replay
covered writing, deleting, manual TRIM, rewriting and a cold reboot on NTFS.
The managed regression suite exercised transaction fault injection.

## Original selective-port validation



On Windows, the final standard Release solution build succeeded with zero
warnings and errors. An earlier isolated build emitted one existing CS8602
warning in the sibling CopperSharp compiler. The final edits passed 52 focused tests;
one external-image case was unavailable. The checker command was exercised on
valid and corrupt images, with input bytes unchanged, and on missing arguments.
The standalone recovery command passed all 40 scenarios across four block sizes;
its source hashes were unchanged during execution. The benchmark correctly
returned failure for the node-fullness finding described above.

The final frozen solution, excluding the two extended batch cases, passed
1,118 tests with three external-media tests unavailable. Both extended batch
cases also passed (9m54s without relocation, 1m19s with relocation). Across these
runs, 1,120 distinct tests passed and three external-media cases were unavailable.
The final Category filter was checked to select exactly those two extended cases.
No native replay pass is inferred from these managed results. Native
handlers, full ROM boots and Linux/macOS storage integration were not rerun
for this host-only port.

Local receipts (ignored build artifacts): `artifacts/research-production-build.log`,
`artifacts/research-frozen/`, `artifacts/research-verified/`,
`artifacts/research-final-edits/`, `artifacts/research-wide/`,
`artifacts/research-recovery-validated.json`, and
`artifacts/research-benchmark-validated.json`.
