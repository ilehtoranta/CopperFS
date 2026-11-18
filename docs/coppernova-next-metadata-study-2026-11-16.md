# CopperNova next-format metadata study, 16 November 2026

Implementation follow-up: the [isolated CNM1 prototype](../prototypes/CopperNova.Prototype/README.md)
now implements the common metadata path and both established workloads. See the
dated prototype benchmark report for its results and qualification. The proposal
below records the earlier design decision; its larger capacity, overflow trees
and full namespace feature plan are not claims about the limited prototype.

Recommendation: prototype directly addressed 512-byte inode pages, inline small
directories and extents, and a bounded physical redo journal. This is a new
incompatible format candidate. No format magic, handler registration or package
version is assigned. Existing CPX4 and CPX5 production/experimental code is
unchanged. This document and its independent Python model are the only additions
to an isolated worktree based on `6a4380d`.

This is a credible design to measure, not a demonstrated speed improvement.
It preserves buffered durability, allocation-free operations and single-owner
mount execution. Breaking the format does not authorize weakening checksums,
crash ordering, namespace semantics, packet ABI or completion validation.

## Evidence that constrains the design

The [qualified A86 report](coppernova-append-range-qualification-2026-11-16.md)
measures these two-trial median emulated bus cycles:

| Mixed phase | A86 | Original FFS 40.1 | Original SFS 1.279 |
| --- | ---: | ---: | ---: |
| Mutation | 2,387,235 | 1,683,555 | 2,489,380 |
| Flush | 498,765 | 20,460 | 161,275 |
| Remount | 530,820 | 108,975 | 218,815 |
| Read/verify | 1,291,260 | 969,780 | 1,061,865 |
| Total | 4,708,080 | 2,782,770 | 3,931,335 |

The FFS gap is 1,925,310 cycles: mutation 703,680, flush/remount 900,150, and
read/verify 321,480. Publication improvements alone cannot close it. The stream
total is 369,030,775 against FFS 367,442,645 and SFS 369,868,185. Its guest
verification loop dominates; small total differences need repeated comparisons.

Direct inodes, 512-byte pages, inline selection catalogues and two-barrier shadow
publication have already been tried in CPX5 features 3 through 5. A86 retains
those experiments. Repeating them under a new name is not a redesign. This
candidate additionally removes persistent copy selection, the selector catalogue,
paired metadata allocation and the metadata reserve list, and avoids separate
directory-tree/name-blob pages for the common small-directory case.

## Selection of the metadata method

| Method | Common-path benefit | Cost or reason for not selecting it first |
| --- | --- | --- |
| Existing paired shadow pages and selectors | Two publication barriers; no home installation | Measured selection, checkpoint and validation costs remain |
| General copy-on-write metadata tree | Explicit root publication; unchanged nodes shared | Parent rewriting and delayed reclamation; not a cheap small-object path by itself |
| Logical operation/delta journal | Small log records | Replay must duplicate namespace/allocator semantics; incomplete operations and retries are harder to audit |
| Direct home pages with full-page redo | Address arithmetic, coalesced updates, simple idempotent replay | Changed metadata is written twice; clean retirement needs four ordered barriers |

Choose the last option as an isolated experiment. It deliberately spends some
metadata I/O to simplify the CPU path. If that tradeoff loses the established
benchmarks, reject it; the number of removed layers is not performance evidence.

## Direct metadata layout

Use one 512-byte home sector per inode, including a CRC-32C, full object identity,
home address, type and publication generation. A provisional budget is a 64-byte
common header, up to 256 bytes of scalar/object fields, and 192 bytes of inline
payload. This preserves room for the existing logical object fields; final byte
offsets require a field-by-field semantics audit before writing a formatter.

For files, eight 24-byte inline extents fit: 64-bit logical start, 64-bit physical
start, 32-bit run length and 32-bit flags. One contiguous 8 KiB or 2 MiB file
needs one extent. Overflow uses a direct-pointer extent tree, without a separate
copy selector. Sparse regions, unwritten allocation and wide offsets require
explicit descriptors and overflow checks; they are not replaced by assumptions
about contiguous files.

For small directories, store variable-length entries in the parent inode's
192-byte payload. A provisional entry has 24 bytes for target ID, stable entry ID,
folded-name hash, length and flags, followed by the name, aligned to eight bytes.
`Renamed.bin` and `Temp.bin` consume 72 bytes together. Lookup compares hashes
then the complete folded names using existing charset rules. The target inode
does not own its filename, which supports hard-link aliases without changing the
target identity. Retain existing alias/enumeration semantics and stable entry
IDs; a hash is never sufficient proof of a name match.

Directories that outgrow the inline area promote transactionally to packed
directory leaf pages and a bounded-height index. Long names, comments and
symlinks use explicit continuation pages where needed; preserve current logical
length limits. Promotion, hash collisions, stable enumeration and cross-parent
rename are mandatory prototype cases. No automatic demotion is needed in the
first experiment. A small directory has no separate name page or index root.

With the root inode already admitted, opening `Bench/Renamed.bin` needs the
Bench inode and target file inode. A fully cold path also reads the root inode.
Once admitted into pinned caller-owned cache pages, repeated operations edit
scalar fields and extent entries directly. They do not reserialize whole object
records or repeat full-page checksums on every helper call.

## Allocation and identity

Use deterministic group geometry rather than a persistent metadata reserve.
A concrete starting geometry is 65,536 sectors per group, 1,024 inode sectors,
one inode bitmap, 32 allocation bitmap sectors and one group descriptor. Each
allocation bitmap covers 2,048 sectors, leaving space for its own identity and
CRC; bitmap selection is a shift and mask. Reserve those 1,058 sectors in every
full group. That is approximately 1.61% fixed space, excluding global anchors
and journal. The smaller last group needs independently checked geometry.

A provisional 64-bit object ID packs a 32-bit inode slot and 32-bit incarnation.
The low ten slot bits identify the inode within its group. This supports at most
2^32 slots and 128 TiB at the proposed 512-byte group geometry; those are explicit
candidate limits, not inherited CPX4 guarantees. Retire a slot before incarnation
wrap rather than allowing a stale handle to alias a new file. Root identity is
reserved. If these limits are unsuitable, revise the layout before accepting it.
Do not quietly narrow the existing public identity or storage APIs.

Overflow directory/extent/name pages can use dynamically allocated blocks, with
typed ownership and final allocation-state checks. Their allocation, unlink and
reuse follow the same journal protocol. Independent checking enumerates allocated
inode slots from the inode bitmaps and walks overflow references. It must detect
unreachable allocated metadata, duplicate ownership and raw data that resembles
an inode; plausible headers are not allocation evidence.

Keep allocation hints, pending frees and contiguous-run proofs in fixed RAM
structures. Exclude fixed metadata, journal, anchors and out-of-range sectors
before returning an allocation. Never reuse a block freed only in an unpublished
group. The native scheduler yields between fixed bitmap windows; it must not scan
the volume or build a volume-sized index during mount.

## Redo publication and clean mounting

Use two fixed journal banks, each holding at most 48 final home-page images, a
1,024-byte descriptor and a 512-byte commit record: 26,112 bytes per bank. A
descriptor binds generation, volume identity, unique target addresses and image
CRCs. A commit binds its bank and complete descriptor digest under its own CRC.
Each image is already sealed for its HOME address, so installing it at home
requires no checksum retargeting or resealing. CRCs detect accidental corruption;
this is not cryptographic authentication.

Two small alternating clean records bind the exact committed generation and
commit digest. A normal nonempty retirement is:

1. Finish file-data transfers and write sealed final images plus descriptors to
   the bank that does not hold the latest durable commit. Persist barrier one.
2. Write that bank's commit sector. Persist barrier two. The metadata transaction
   is now durable and recoverable, even if installation later fails.
3. Copy the SAME pinned images to their home addresses. Persist barrier three.
4. Write a matching clean record, preserving the preceding valid clean record.
   Persist barrier four. The baseline candidate returns successful flush here.

No home write precedes a durable commit. No next mutation, raw-data reuse or
discard starts before clean retirement. This restriction is intentional: it
avoids accumulating transactions, revocation logs and a second pending write
set. A later optimization must prove any relaxation separately.

On mount, validate anchors and the small commit/clean records. A clean record
matching the newest valid commit permits skipping journal payload reads. Load
and CRC-check metadata pages on first use. Mount does not validate every inode,
bitmap or free-space reserve. Conflicting valid records, impossible sequences,
unknown mandatory features and geometry mismatches are errors, not fallback
opportunities. Sequence wrap is rejected.

If the newest commit lacks a matching clean record, validate the bounded complete
descriptor and all images, redo its homes, persist, then publish/persist the clean
record. Replay is idempotent, including repeated crashes. Because each prior
transaction is fully home-installed before the next mutation, recovery needs
only the latest committed delta. It does not replay older banks over newer data.

The untouched latest commit survives an interrupted bank reuse. A stale commit
from an older generation cannot authorize the replacement bank's new descriptors
or images. Torn/mixed records must fail CRC/identity/digest checks. Before a retry,
quiesce outstanding I/O, recover, and select the next generation from media.
Retain monotonic completion tokens; late completions cannot complete a retry.

Do not advertise a two-barrier flush as an improvement by deferring home writes
to the measured remount. Such an alternative is possible, but all deferred work
must be charged to the same complete workloads. The initial candidate uses the
four-barrier clean path precisely to keep that comparison straightforward.

## Durability, bounded execution and admission

Ordinary successful write replies may precede persistence. Successful ACTION_FLUSH,
unmount/inhibit and shutdown must finish all data, publication, home and clean
barriers. Empty explicit flush still issues a device drain as needed by the
transport contract. Failure after commit can leave a durable transaction even
when the caller receives an error; poison the mount and recover. Do not report
success on a short, cancelled, stale or failed transfer/barrier.

File-data overwrites remain in place, with the existing pre-flush power-failure
atomicity limitations. This design does not promise rollback of overwritten
user data. New allocation must not publish unwritten contents, and partial-sector
read/modify/write and truncation tails retain their current visibility semantics.
Transport masks, MaxTransfer, bounce buffers and wide addressing remain enforced.

Use a 48-image fixed write set with replacement by home address, one admission
index and fixed undo storage for the current DOS request. Undo must preserve
earlier acknowledged buffered requests after an ordinary rejected operation.
Perform capacity admission before irreversible data I/O. Namespace operations
must fit an entire atomic update; establish worst-case index/split/rename bounds
before claiming general filesystem support. Deep reclaim is a resumable durable
operation, with bounded batches, rather than an unbounded delete transaction.

The provisional core arena accounts for 74,752 bytes: dirty images 24,576, undo
24,576, clean cache 8,192, descriptors 1,024, bounded index 2,048, control 4,096,
tree scratch 4,096, name scratch 2,048 and CRC tables 4,096. A 96 KiB target leaves
23,552 bytes for unaccounted core state. This is a layout budget, not a verified
native memory requirement. Adapter/startup resources and the 4 KiB stack are
separate. No native operation allocates, recurses or retains caller request
buffers after reply. One continuation processes at most one sector or one fixed
index/bitmap slice and yields at I/O.

Disk pages pass CRC, identity, geometry and structural admission on cache entry.
Owned dirty pages retain that proof until changed and are sealed once at commit.
Public/untrusted inputs do not inherit an internal ownership proof. Media changes,
failed I/O, eviction, reuse and rollback invalidate the appropriate proofs.

## Workload-specific cost hypothesis

For a colocated mixed fixture, the distinct final dirty home pages can be seven:
root inode, Bench inode, main-file inode, temporary-file inode, inode bitmap,
one allocation bitmap and the group descriptor. Volume counters must fit an
already-counted root/group control page for this budget. Names and extents fit
inline. Repeated overwrites, truncates and rename coalesce into those final pages.
This is a model assumption awaiting a complete formatter and packet trace.

Seven final pages imply seven seals, seven journal-image writes and seven home
writes, plus descriptors/commit/clean controls. Home installation does not seal
again. There is no selector update, reserve scan or checkpoint-body comparison.
These counts are not CPU-cycle predictions, and physical-device performance may
lose to shadow publication because redo writes metadata twice.

## Study validation and next decision

Run the dependency-free abstract model:

```powershell
uv run --offline --no-project python tools/studies/coppernova_metadata_study.py
```

The model checks layout arithmetic and enumerates two-home-page transaction
crashes, bank reuse, recovery, and a different write set after restart. Records
abstract CRC-valid sectors; the real two-sector descriptor is one abstract record.
It assumes completed barriers persist prior writes, device quiescence on restart,
and detected tears without checksum collisions. It includes negative controls
for omitted data/log ordering and premature clean publication. It does not model
real CRC bytes, full-sector layouts, allocation, namespace operations, I/O tokens
or native instruction execution. Passing it is not filesystem qualification.

The local run passed 1,554 transaction/crash/retry outcomes and 144 interrupted
recovery outcomes, 1,698 total, after three completed warmup transactions. Both
negative ordering controls behaved as expected. Layout checks confirm eight
inline extents, 72 bytes for the mixed directory's two entries and the 74,752-byte
provisional scratch subtotal. None of these counts is a native speed result.

Before touching the existing CPX implementations, the next useful work is a
separate prototype, independent formatter/decoder and compiled 68000 kernel.
Measure direct lookup/edit/commit/mount costs first, then integrate enough DOS
operations to run the unchanged mixed and streaming guests against the exact
FFS 40.1 and SFS 1.279 inputs. Require independent persisted ownership/payload
checks, repeated trial ranges and phase accounting. Moving work between phases
does not meet acceptance. Neither does winning a microbenchmark.

Full qualification must include stale IDs and wrap, all alias/name/link forms,
large/colliding directories, extent overflow, cross-directory rename, full disks,
admission rollback, capacity commits, short/stale/failed/cancelled I/O, every
physical sector/barrier cut, journal reuse, changed retries, crash during replay,
allocation/free/reuse and post-shutdown persisted checks. Report host, compiled
native, CopperStart replay and actual-HUNK full-ROM coverage separately.

The initial solution Release build and test attempts in this study worktree are
blocked at restore: installed .NET SDK 10.0.401 cannot target the neighboring
CopperSharp compiler project's new .NET 11 target (NETSDK1045). No SDK/compiler
sources were changed. This study has no native integration, HUNK, ROM result or
new filesystem benchmark. Existing A86 qualifications remain historical evidence.

## Conceptual references and provenance

The proposal and model are original study material; no third-party code or test
fixtures were imported. These primary references motivate the mechanisms, not
their performance on the Amiga:

- [Linux ext4 journal documentation](https://docs.kernel.org/filesystems/ext4/journal.html):
  physical redo records, commit-before-home ordering and replay/reuse hazards.
- [Linux ext4 inline data](https://docs.kernel.org/filesystems/ext4/inlinedata.html):
  small directories held in their inode rather than separate directory blocks.
- [Linux ext4 inode block mapping](https://docs.kernel.org/filesystems/ext4/ifork.html):
  inline extent roots and explicit overflow trees.
- The existing CPX5 direct-inode, shadow-publication and catalogue design documents
  in this checkout record the mechanisms already tried and their limitations.
