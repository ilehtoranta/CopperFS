> Historical research snapshot from CopperSFS commit 4a902a1ff9189bbdff841c23441c66678a6e2427.
> Preserved on 2026-11-04; commands, paths, results and recommendations below are historical, not current validation.
> See [current port status](../../research-port.md) before using this material.

# Reliability evidence and release gates

CopperSFS remains an experimental preview. Tests establish behavior under
specific assumptions; they do not prove that arbitrary hardware failures or
all program defects cannot lose data. This document describes host evidence,
separate from native handler qualification.

## Conditions checked

A successful core mutation must expose its complete expected result from
durable storage after restart. An interrupted mutation must recover to the
complete before-or-after volume manifest. Paths, object identity, type, size,
protection, ownership fields, flags, comments, dates and SHA-256 file hashes
must match the reference model, including untouched files. This condition
applies per core mutation, not to an application save involving several DOS
operations.

The checker validates checksums, geometry, node mappings, directory and extent
links, hash chains, metadata ownership, bitmap accounting and free counts.
Duplicate ownership, orphan objects/extents, unreachable allocated metadata
and allocation leaks fail the run. Guard blocks surround the simulated
partition; out-of-bounds I/O fails. Recovery is rerun to check idempotence.

The checker has **no reference to CopperSFS.Core or the Amiga SDK**. This
reduces shared-code blind spots. It is still written in this repository from
the same published format and synthetic fixtures; it is not an independent
SFS implementation or external audit.

## Failure model

The 22 mutation scenarios cover file/directory creation, overwrite, append,
shrink/growth, truncate, file/directory removal, cross-directory rename,
directory rename, comments, protection, date, directory-container expansion,
node-tree and extent-tree growth, admin-space expansion, journal exhaustion on
a nearly full disk, and three packed-allocation regressions.

Each scenario is interrupted after every write and flush. Between successful
flushes, a block may retain its durable value or any whole version written
since the barrier. The harness enumerates the Cartesian product across all
pending blocks, including repeated writes to the same block. It also injects
errors at every read and write/flush point on the baseline execution path,
both once and persistently from that point.

The per-fault-point state limit defaults to 16,777,216; set
`COPPERSFS_MAX_CRASH_STATES` to a positive integer to override it. Hitting the
limit fails the test rather than silently sampling or claiming completion.
For the supplementary frozen-handler stress run, byte-exact Cartesian-product
containment avoids repeating covered spaces while still fully enumerating
their union. The completed [native-core supplementary run](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/native-reliability-20261024.md)
covers all 600 fault points and 304,017,665 logical combinations through
3,899,421 image validations. Its result applies to that recorded core input.

One dirty image per scenario is selected for interruptions and errors during
recovery, followed by restart and full validation. This is not exhaustive
enumeration of repeated crashes across every possible dirty image.

Four additional regression scenarios cover admin-container expansion,
allocation into an unused object-node branch, extent pruning and deletion
through an indexed extent tree. They enumerate every write/flush interruption
and one-shot or persistent write/flush failure, checking the complete volume
manifest after recovery and repeating recovery to verify idempotence. These
four scenarios do not inject read errors or interruptions during recovery.

Partial-write tests cover every write of overwrite, append, rename and delete,
persisting prefixes of 1, 4, 8, 12, 16, 256 and 511 bytes while dropping other
pending writes. Recovery must produce a valid before-or-after volume or fail
with the volume unmountable. Refusal is detected damage, **not successful data
recovery**. Arbitrary tear patterns, arbitrary corruption, and partial writes
during recovery are not covered.

Three deterministic random workloads use seeds `104729`, `130363`, and
`155921`. Each generates 300 operations against a separate in-memory model,
with a sampled power cut every third operation that performs I/O. Every step
receives a full integrity and manifest check.

Successful flushes are assumed to preserve all prior writes durably. The new
matrix tests 512-byte blocks on small synthetic images; existing sparse-volume
tests separately cover core addressing beyond 4 GiB. Actual device caches,
flush implementations, DMA constraints, partition translation, TD64/NSD
addressing and compiled MC68000 code are not qualified here.

## Reproduce and retain evidence

With .NET 10 and the sibling CopperSharp68k checkout described in the README:

```powershell
powershell -NoProfile -File scripts/Test-Reliability.ps1
```

The script creates a timestamped directory under `artifacts/reliability/` with
`tests.trx` and `report.json`. It records the Git revision, hashes of tracked
and untracked non-ignored repository files, test-directory DLL hashes, .NET SDK
version, outcomes and per-scenario image counts. A changed source tree or test
failure makes the script fail. Use a fresh output directory for every run.
Attach the evidence to the exact sources evaluated; a commit alone does not
identify a dirty working tree.

The build workflow runs this same script on pushes and pull requests and
uploads its results as `reliability-evidence`, including results from failed
runs when available. The workflow itself has not been executed on GitHub as
part of the local validation reported here.

Failures reached through the shared crash/model validation helper save the
unrecovered image and expected manifests in `failures/` under the report
directory. Context includes the scenario, interruption point or random
seed/step. Setup failures and other assertions remain in the TRX. Generated
images and reports are ignored by Git and must not be committed as fixtures.

The initial reliability run on 2026-10-24 passed **84 tests**, including:

| Evidence | Result |
| --- | --- |
| Mutation fault/crash images checked | 42,467 |
| Recovery fault/crash images checked | 11,000 |
| Partial-write cases | 728: 668 recovered, 60 refused mount |
| Random modeled operations | 900 |

Counts are executions, not distinct disk images or statistical probabilities
of safety. They include multiple survival states at the same interruption
point and may include identical contents.

After the six filesystem review fixes, the Release suite passed **109 tests**.
The 25 additional cases in `FileSystemRegressionTests` include the four
write/flush fault scenarios above, successful reads after pruning at several
tree depths, growth beyond 245 objects, deletion after an extent-tree split,
admin header encoding, and parent-relative create and rename paths. Fresh
reports include their crash-image counts in the totals.

## Standalone read-only checker

```sh
dotnet run --project tools/CopperSFS.Check -c Release -- partition.img
```

Optional positional arguments specify block bytes and reserved blocks (default
512 and 1). Input must be a raw partition image starting at filesystem block
zero, not a whole disk/RDB image. Input is opened read-only. Success produces a
JSON manifest (exit 0); errors go to stderr (exit 1; usage errors exit 2).
The tool does not replay journals or repair images.

The supported subset is classic `SFS\0` version 3, two valid root copies,
clean `TROK` transaction state, ordinary files/directories, directory hashes,
and 32-block admin maps. Admin headers accept the reference writer's `32` and
the earlier CopperSFS value `2`. First extents must identify their owning
object with `0x80000000 | node`; incorrect/missing owner tags and inconsistent
node-index fullness flags are rejected. Links, ring lists, nonzero recycled/deleted-file
accounting, other admin-map sizes and directory depths of 256 or more are
rejected. A degraded root is rejected even though the core may mount it.
Every live metadata block must belong to an allocated admin slot. Features
outside this subset cannot be certified by this tool. It uses random-access
streams rather than loading whole images. The separate
[block-size study](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/block-size-study.md) checks synthetic 1–8 MiB images at
512, 1,024, 2,048 and 4,096 bytes per block; its narrower failure model does
not extend every scenario in this reliability matrix to those sizes.

SHA-256 detects changes against a trusted baseline manifest. A single run
cannot establish that unchecksummed file data was never corrupted.

## Defects exposed and remaining release gates

Seeded workloads exposed rollback corruption after delete/shrink. When
logging a bitmap's first undo record, journal allocation considered its
proposed contents, which already marked file blocks free. Journal writes
could overwrite those blocks before rollback restored the file's metadata.
Allocation now checks the on-device bitmap as well as the proposed bitmap and
the original transaction allocation state. Packed delete, truncate and shrink
regressions were confirmed to fail with the old allocator and pass with the
fix. Journal exhaustion now also preserves `DiskFull` instead of replacing it
with `DiskNotValidated` when the second undo block cannot be allocated.

The subsequent filesystem review reported six findings, covered by regressions.
The admin-header diagnosis in that review was incorrect and has been corrected
below. Extent pruning preserves index lower bounds, and inserting below
an existing minimum updates those bounds. Admin reservation excludes the whole
pending region from journal allocation. Nonempty files can be deleted through indexed
extent trees. Object-node allocation fills unused branches before growing the
root. Create and rename preserve parent-navigation slashes when resolving the
destination directory. These changes retain the earlier bitmap undo fix.

The second review follow-up adds 36 regression cases; with six separately added
crash-state enumeration tests, the full suite passes 151 host tests. Comments containing an
embedded NUL are rejected before any write, and SFS's undeletable flag prevents
deletion. Native first-extent owner tags are written and handled during removal.
Packed object containers can use the entire block. Node fullness flags propagate
through their parent indexes after allocation and deletion. Rename can expand
admin space, and growing comments can relocate their object while preserving
its identity, hash chain, metadata and file contents. Six added failure matrices
cover these changed transaction paths, including nested indexes and relocation
that also grows admin space.

The earlier assertion that the admin header must contain `2` relied on the
header comment and missed the reference implementation's assignment of `32`.
New containers and the corrected regression test now use `32`, as written by
[reference SFS](https://github.com/aros-development-team/AROS/blob/master/rom/filesys/SFS/FS/adminspaces.c).
The checker accepts `2` as a legacy CopperSFS representation of the same layout.
Synthetic fixtures now use native extent owner tags and admin headers, and
negative checker tests reject incorrect owner tags and node fullness flags.
These newest corrections have not been qualified in a compiled native handler.

The downloadable handler has not been replaced by this work. Separate
development builds using the newer embedded core, allocator correction and
subsequent review fixes have undergone [native validation](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/native-reliability-20261024.md).
These results apply to the exact binaries and profiles recorded there.
Before calling a release stable:

1. Select the exact release handler and retain its corrected core, sources,
   compiler, binary hash and native crash-test evidence. A development build
   does not automatically replace the existing downloadable release.
2. Exchange independently formatted images with an established SFS handler,
   comparing complete manifests after clean handoffs. Recover CopperSFS `CSUN`
   transactions with CopperSFS before handing images to another implementation.
3. Expand block sizes, volumes, corruption patterns, failure-path coverage and
   interrupted-recovery workloads, and obtain independent review of the journal
   ordering and checker.
4. Qualify supported ROM/device/hardware configurations, including real flush
   behavior and partitions above 4 GiB where claimed.

These gates remain open. The results support a preview with stronger host
evidence, not a primary-storage guarantee.

The transaction follow-up adds 24 cases in `TransactionRegressionTests`,
including the seven investigation reproductions. They check dirty-volume read
and metadata-query refusal after journal exhaustion and every overwrite
write/flush error, zero reported write bytes on failure, archive-bit clearing
and no-op preservation, and empty-container unlinking at each directory-chain
position. Nine added scenarios enumerate write/flush cuts and one-shot or
persistent failures, validating complete before-or-after manifests and recovery
idempotence. They include reuse of a container freed during the same rename.
These additions are host tests; they do not qualify a rebuilt native handler.

The optional [batched writer](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/batched-writes.md) has separate crash/failure
coverage for small exhaustive batches and sampled full 64 KiB transfers,
including interruptions between the sectors of one range call. Its evidence is
reported separately under `batchedWrites` in fresh reliability reports so the
sampled executions are not mixed into the original exhaustive-image totals.

The [contiguous overwrite replacement](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/copy-on-write.md) follow-up validates
extent splitting, relinking, tree growth/pruning and rollback after allocation
exhaustion. Fresh reports keep these results under `relocation`, separating
exhaustive pending-version checks, sampled checks and torn-write outcomes.
