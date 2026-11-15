# CPX3 filesystem research prototype

This host prototype implements the bounded journal, allocation and tree model in
[the design](../../docs/new-filesystem-design.md). CPX3 is an **incompatible,
unregistered research format**. CPX1/CPX2 images require destructive reformatting;
there is no implicit conversion. CopperFFS/CopperSFS identities, formats and
native handlers are unaffected. All four prototype projects are non-packable.

## Run

From the repository root with .NET 10:

~~~powershell
dotnet run --project prototypes/CopperFS.Prototype.Demo -c Release
dotnet test prototypes/CopperFS.Prototype.Tests -c Release
dotnet test prototypes/CopperFS.Prototype.InstructionTests -c Release
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-qualification
dotnet test CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-qualification
~~~

The demo opens no files or physical devices. It formats disposable 4 MiB memory
devices. Format is destructive; interruption may leave an unformatted device.

## Implemented

- A = 512/1024/2048/4096-byte allocation units; P = max(1024,A) metadata pages.
  The device sector is a supported power of two, at least 512 and no larger than A.
- CRC32C, big-endian 64-byte page headers, fixed control regions, two immutable
  anchors, two checkpoints, two 512 KiB journal slots and allocation groups.
- Packed 256-byte objects, 128-byte directory entries and 24-byte extents in
  B+ trees of at most eight levels. No fixed file-count or extent-count cap.
  Files have 160 inline bytes or six inline extent descriptors before tree promotion.
- Subdirectories, create, hard links to regular files, atomic rename including
  cross-directory replacement, deletion, enumeration, sparse reads, append,
  random write, unwritten preallocation, shrink and sparse extension.
- Latin-1 DOS bytes explicitly converted to UTF-8. A separate CreateUtf8 import
  accepts strictly validated UTF-8. Fixed Latin-1 case folding, full-name collision
  checks and stable 19-byte aliases support the 107-byte legacy enumeration limit.
- Streamed redo images, per-operation credit enforcement, a 64-address metadata
  reserve (32-page target/12-page margin), bounded refill and persistent cleanup.
- Synchronous optional discard, limited to four bitmap windows and one device
  command per call, revalidated against retired free space. Discarded reads may
  be nonzero. Allocated unwritten extents and reserve pages are never discarded.
- Constant-workspace allocation checking and an independent test-image decoder.

Put is a host convenience: delete/create plus chunked writing. **Whole-file
replacement is not atomic.** Use rename for atomic namespace replacement.
Write/Append/Preallocate report committed progress; a large request can complete
partially. External in-place overwrite data can be mixed after a crash. Inline
data and all metadata changes have the transaction guarantee. No TRIM or new
transaction starts before both checkpoints retire the preceding transaction.
Append enforces its EOF constraint and uses the same bounded write path as Write,
including zero-initialization and journaled conversion of a retained unwritten
extent after shrink. A successful append must remain readable after remount.

## Memory and execution contract

PrototypeVolume<TDevice> is a borrowed ref struct. The caller supplies scratch,
owns mount serialization and must not copy/share a live mount or alias its
scratch with input, output or another mount. Successful core calls allocate no
managed objects; device adapters, test fixtures and exception paths are excluded.

| A | P | Caller scratch, 6P + 12 KiB | Inline bytes |
| ---: | ---: | ---: | ---: |
| 512 | 1024 | 18,432 | 160 |
| 1024 | 1024 | 18,432 | 160 |
| 2048 | 2048 | 24,576 | 160 |
| 4096 | 4096 | 36,864 | 160 |

Six P-byte slots hold the current tree page, two split outputs, control pages,
name/data I/O and journal I/O. The 12 KiB area includes a 2 KiB transaction index,
128-byte scalar tree path, two 768-byte name buffers and a 4 KiB LIFO record arena.
Object/entry/extent records and internal name-search state use that arena. It
does not contain 42 or 64 journal payload pages. Native scalar frames, mount
state, caller-owned cursors, DMA and OS objects remain additional costs.

Allocation, name and ancestry scans expose caller-owned continuations that read
at most four metadata pages per step. Initialize NameSearch through its ref
parameter; keep cursors in persistent caller storage, not a native stack local.
Discard all cursors after media change/remount; committed mutations invalidate
their generation. A found free range is a hint, not a reservation.

Blocking APIs call IBlockDevice.Cooperate at most four I/O requests apart,
including journal writes and flushes. The callback retains exclusive mount and
scratch ownership; it may run unrelated work but must not reenter this mount.
With deferCleanup:true, unlink/shrink leave durable work; ReclaimStep retires at
most one cleanup/prune transaction per call. Other mutations wait until cleanup
finishes. PruneDirectoryStep separately removes one empty directory-tree path.
These transaction steps can scan multiple pages and cooperate internally; they
are not four-page-return operations. Device latency has no hard time bound.

Extent reads use predecessor lookup and cleanup uses rightmost traversal.
High/low u32 kernels implement carry/borrow, comparisons, checked shifts, rounded
unit counts and device-range checks. Address conversion and arbitrary discard
alignment use those kernels; the host codec/API still represents values as ulong.
The checker and extent-count diagnostics trade repeated reads for bounded RAM.
See [68000 readiness](../../docs/cpx3-68000-readiness.md) for measured stack,
memory, scheduling tests and the remaining native integration gates.

## Journal and reserve contract

A foreground transaction admits at most 42 distinct home targets; operation
classes enforce their smaller limits (create 39, link 22, rename 30, unlink 7,
attribute/initial shrink 6, reclaim 9, prune 27, refill 6). The format has 64 image
slots. A target gets one fixed slot, rewritten in place while preparing; an index
provides read-your-writes without retaining all images in RAM.

1. Invalidate the next slot's old commit page and flush.
2. Stage final metadata images, new/initialized data, header and descriptors; flush.
3. Write the checksummed commit page; flush.
4. Apply home images; flush.
5. Write checkpoint A; flush. Write checkpoint B; flush.

Thus a committed transaction completes six barriers. Reserve refills, large
writes, pruning and cleanup are separate transactions, not hidden additions to
a foreground credit allowance. Expected admission failure may leave journal
staging writes or a completed refill, but cannot publish a partial namespace
operation. A 43rd target is rejected before publication.

Recovery chooses the highest valid retired checkpoint C, ignores stale commits
at or below C and accepts only a committed C+1. It validates the entire log before
the first home write, then replays with bounded buffers and retires both copies.
Interrupted replay is repeatable. A read-only mount refuses required replay.
Every uncertain I/O failure poisons the live mount; quiesce the device and recover
before reconciling the operation. Flush must truthfully persist/order writes.
CRC detection is neither authentication nor a guarantee against undetected
corruption. Post-retirement media decay and writes outside the requested range
are outside the crash model.

The reserve owns bitmap-allocated pages. Splits transfer ownership from it without
bitmap recursion. Refill touches at most two bitmap windows and six targets.
Unlink, initial shrink and reclamation need no new metadata pages. Returned pages
cannot be assigned within the same transaction; full-pool returns free their
bitmap units, and reuse/discard waits for retirement.

## Qualification and remaining gates

Tests generate their own images; no ROM, media, foreign filesystem source or
binary fixture is imported. The independent decoder uses its own byte readers,
CRC, tree traversal and ownership reconstruction.

Crash tests cut after every sector write and before/after every flush, trying
five deterministic persistence schedules: none, all in reverse order, alternating
pending sectors, the final pending sector and torn 128-byte prefixes. Exhausted
reserve cleanup uses the first three schedules. These are finite adversarial
schedules, **not an exhaustive enumeration of every persistence subset**.

Coverage includes slot reuse, torn multi-sector metadata, interrupted replay,
corrupt committed logs rejected before home writes, 42-image commit/recovery,
43rd-image refusal, actual tree split cascades, height-nine refusal, double
unwritten-extent insertion, atomic replacement, persistent shrink, full-disk
empty-reserve cleanup, CRC-name collisions, aliases, sparse zeroing, discarded
nonzero bytes and wide synthetic addresses. The 42-image journal witness uses
valid existing metadata targets; it is not a claim that a naturally generated
file write attained the theoretical maximum. A detached structural tree fixture
tests worst-depth splitting separately.

The original crash qualification is recorded in
[CPX3 qualification](../../docs/cpx3-prototype-qualification.md), followed by the
[Append fix](../../docs/cpx3-append-fix-qualification.md) and
[68000 readiness](../../docs/cpx3-68000-readiness.md). The latter includes actual
MC68000 arithmetic execution and separately scoped CopperStart replay. No CPX3
native handler exists. Full-ROM/physical SSD coverage remains untested.

Not implemented: alternative immutable 8-bit charset tables, attributes/comments/
timestamps/protection mutation APIs, symlinks, DOS packet/error/open-lock rules,
boot installation, DOS packet dispatch, asynchronous discard and salvage/repair. These
remain explicit native/format-freeze gates. See [FORMAT.md](FORMAT.md) for the
CPX3 bytes and [the design](../../docs/new-filesystem-design.md) for the broader
intended filesystem.

