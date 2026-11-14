# CPX4 native handler: implementation and qualification

Date: 2026-11-14. **The complete native-handler milestone remains unfinished.**
There is now a runnable, independently built `CopperCPX4` HUNK. It boots with
Kickstart 3.1 on an emulated MC68000 with exactly 512 KiB RAM and a 4096-byte task
stack. These results qualify the workloads below, not full packet parity or every
mutation/failure path. CPX3, CopperFFS and CopperSFS formats remain unchanged.

## Implemented native path

`CopperCPX.Core` has no OS/application dependency. Addresses, file sizes,
positions, generations and partition calculations use explicit uint pairs. State
lives at fixed offsets in caller-owned memory. Begin/Step/CompleteIo return
Pending/NeedIo/Complete/Failed without retaining page views or record leases.
Typed admission methods validate and copy small mutation/name inputs; DOS data
buffers remain caller-owned until the packet completes or I/O is quiesced.

The engine implements mount, journal recovery, metadata reads, object lookup,
generic object/directory/extent cursors and tree mutations, file reads/writes,
create/delete/rename, external resizing, protection/date updates, transactions
and bounded TRIM. The
cursor keeps eight scalar path frames and handles exact, predecessor and
successor searches, including empty leaves. Name lookup compares Latin-1 DOS
bytes against decoded UTF-8 with fixed Latin-1 folding and supports `__u` aliases.
It currently scans directory entries; hash-based lookup optimization is pending.

Writes promote inline data into allocated units, coalesce contiguous written
extents, promote a seventh inline descriptor into an extent-tree leaf, and insert
into/split existing extent trees. Partial writes to unwritten runs produce up to
three descriptors: unwritten left, written unit, unwritten right. The written
unit is zero-filled before copying the caller's bytes; remaining unwritten units
retain their flags and allocation. A fully pruned empty tree from the reference
implementation is readable as a hole and is recreated on its first write.
Existing written mappings can be overwritten. Each allocation transaction
updates bitmap, group counters, volume counters and object metadata together;
external data reaches the staging barrier before metadata publication. A packet
reports only its retired prefix. External in-place overwrites are not atomic.

All extent insertions, replacement edits and the object update are admitted and
staged before the external data write. An unpublished tree-height or transaction
admission failure cannot expose a partly converted unwritten run. The edit list
holds at most three records, and inline-list promotion holds at most eight;
neither grows with file size or tree depth.

Raw core writes and SetFileSize can extend a file sparsely, including beyond
4 GiB. It clears the old partial-unit tail before publishing a larger size.
Inline resizing clears newly exposed holes and truncated tails. DOS resizing
respects other open handles' positions and checks narrow-result representability.

Create supports files/directories and inline or external UTF-8 names encoded
from Latin-1 DOS bytes. Rename supports parent changes, case-only changes and
core-level replacement, with bounded ancestry checks. The DOS RenameObject packet
rejects an existing destination; FindOutput truncates an existing file or creates
a new one. Delete and replacement honor link counts, queue detached objects, and
reclaim their data, trees, name, attribute and external-link pages. Comment and
SetOwner setters share one bounded attribute page; ownership uses the sticky
`OWN1` state feature and version 2 attributes described in the design document.
Portable link operations, DOS hard/soft-link creation and
resolution, and a resumable formatter are present. DOS Format and RenameDisk now
use the defined `LBL1` state-page extension for persistent volume labels.
The dispatcher checks open resources
and reserves a handle/lock before a creation or replacement-open mutation.

Tree insertion splits upward using scalar path frames, two output pages and one
256-byte pending record. At height eight it checks a full path before assigning
split pages. Deletes retain empty reachable nodes until bounded pruning. Empty
object nodes are pruned after orphan retirement and on mount/flush, so a crash
between unlink and pruning does not permanently consume their space. Directory
nodes are reclaimed when their directory is removed; pruning empty nodes in a
surviving directory remains a separate optimization.

Reserve admission is 32 pages for create, 10 for rename and 30 for data
allocation. Refill retires separately, targets 32 pages, and changes at most two
bitmap windows/six images per transaction. Metadata allocation enforces the
per-operation new-page limit and forbids allocation after metadata release in
the same transaction. Delete and shrink cleanup require no new metadata pages.

The journal retains 64 descriptor slots, a foreground bound of 42 images, and
six publication barriers: invalidation, staged images/data, commit, homes,
checkpoint A and checkpoint B. A 43rd target makes the transaction abort-only.
Recovery validates every committed image before replaying any home image. I/O
failure or uncertain completion poisons the mount. External shrink publishes its
new size and durable cleanup queue entry together, then frees at most one bitmap
window per reclaim transaction. Namespace retirement precedes orphan cleanup.
Writable mount drains outstanding cleanup after validating and replaying the
entire committed log. Fully reclaimed empty files return to writable inline
storage. Unpublished admission failure discards the high-level transaction;
the low-level 43-image rejection remains abort-only until explicit abort.

## Scheduling, transport and memory

There is one active filesystem operation and one outstanding storage request.
Record waits have separate preallocated timer requests. The handler
returns to its event loop after at most four phases; conflicting packets stay in
the Exec port queue. Steps visit at most one metadata page, scan at most 64 tree
records, process one bitmap window, or checksum one page. Data growth currently
uses one allocation unit per transaction, below the prototype's 256 KiB cap.
An eight-entry continuation stack stores only operation numbers and return
phases in the operation arena. Helpers never recurse through retained CPU frames.
Page validation and journal-image checksumming execute in separate steps; an
instrumented memory test checks at most one page checksum per step across the
new mutation families, including arbitrarily delayed I/O completion.
This is bounded but deliberately not a throughput claim.

The standalone profile links only CPX4 and CopperSharp. Startup, volume
registration, queue processing, SendIO/CheckIO/reaping, flush and shutdown execute
in the actual HUNK. TD64 is probed directly, without NSD. Only explicit
IOERR_NOCMD on the read-only capability probe permits a checked narrow probe,
and only when the entire partition ends at or below 4 GiB. NSD is unsupported.
Normal wide I/O is never retried with a truncated address. Transfer admission
checks partition bounds/base addition, maximum transfer, DMA masks and bounce
requirements. Every submission resets address halves, buffer, length and
completion fields. Short completion and failed barriers are errors.

TRIM revalidates retired bitmap state and reserve ownership while serializing
with all allocation. Allocated/unwritten/reserve units are excluded. Flush
performs one maintenance window and at most one allocation-unit discard. The
native transport discovers SCSI B2/B0/READ CAPACITY(16) asynchronously, checks
completion/status/length, and submits one bounded UNMAP descriptor. An explicitly
unsupported command disables discard cleanly; other errors are not capability
success. Idle timer-driven maintenance is not implemented yet.

The standalone admits 512, 1024, 2048 and 4096-byte device sectors from
DosEnvec. Startup authenticates primary and backup anchor candidates before
choosing allocation geometry. Startup also reserves DMA storage for the
device's memory class and mask. See [startup geometry discovery](#startup-geometry-discovery)
and [DMA-constrained allocation](#dma-constrained-allocation).

The fixed budget is 8 KiB mount/transport (including the 4 KiB DMA page), 8 KiB
operation storage, six scratch pages, a 2 KiB journal descriptor index and
16 KiB pools. The handler reserves **59,392 bytes** even on 1 KiB metadata
pages. An external DMA allocation can use up to 8,192 more bytes including
alignment padding. With the 4,096-byte stack the maximum is **70 KiB**,
within the 72 KiB ceiling. Loaded code and OS allocations are measured separately.
Pools reserve 32 handles, 32 locks, 16 enumerators, eight notifications, 32 record
locks and 16 waiters. Their lifetimes are wired, including notification-message
ownership and record waits that outlive the packet which admitted them.
All operation state remains inside the existing 8,192..16,383 region. Information
packets use 644 bytes at mount offsets 3,072..3,715 for a directory record, object
record and staged FIB. UNMAP shares the selected DMA buffer during its request. Lock name
contexts occupy 512 previously unused bytes at pool offsets 15,360..15,871.
Timer bookkeeping occupies mount offsets 2,816..3,023. No page buffer or pool was
added. Startup allocates one timer port and opens 16 forty-byte timer requests;
these OS-owned allocations are measured separately. There is no per-operation
allocation.

## Notifications and record waits

AddNotify/RemoveNotify (4097/4098) support message or signal delivery, INITIAL
and WAIT_REPLY. Each of eight watches stores a copied, bounded DOS pathname,
converted through the existing Latin-1/UTF-8 lookup. Missing targets can be
watched. A read-only continuation refreshes dirty paths before admitting the
next packet, preserving an absent-path create/delete transition. Namespace and
attribute events follow transaction retirement. File changes become eligible
after the modifying handle closes. Failed unpublished mutations do not send
events; a retired unlink still sends its event if subsequent orphan cleanup
fails. These rules follow the classic
[StartNotify contract](https://developer.amigaos3.net/autodocs/dos.library/StartNotify.html).

Every watch has eight embedded message cells; WAIT_REPLY permits one outstanding
message and coalesces further events. Without WAIT_REPLY, pool saturation
coalesces events until a reply frees a cell. A saturated watch cannot starve
other watches. EndNotify detaches the application's request before replying and
removes still-queued messages in eight bounded visits. Held messages keep the
watch slot alive until ReplyMsg; returning one cannot access the detached
request. The native queue-removal path uses reciprocal Exec links under Forbid,
without scanning the application's queue. Applications must treat NotifyMessage
as read-only except for ReplyMsg, as required by the
[notification protocol](https://wiki.amigaos.net/wiki/Notification).

LockRecord/FreeRecord (2008/2009), and SFS2's paired-pointer wide variants
(26402/26403), support exclusive/shared and immediate/timed modes. There are
32 granted ranges and 16 parked requests. Ranges use checked high/low arithmetic;
overlap is tested without wide division. Different handles to one object
conflict, except for shared/shared ranges; the same owner can hold repeated or
overlapping ranges. Unlock matches owner, offset and length. Locks remain
cooperative, so reads/writes do not acquire them implicitly. Zero-length ranges
consume a grant but do not overlap another range.

Immediate conflict returns LockCollision (241), timeout returns LockTimeout
(242), absent unlock returns RecordNotLocked (240), and pool exhaustion returns
NoFreeStore (103). Close releases its grants and cancels parked requests with
InvalidLock (211). The portable cancellation API and the native handler's
Ctrl-C control cancel parked requests with Break (304); this does not introduce
a new DOS abort packet. Storage failures also cancel parked requests. Granted
locks survive a Ctrl-C cancellation of other pending requests.

Each parked packet retains its message, reply port and generation in a fixed
slot. timer.device expiry, cancellation and provisional grant race through one
owner. AbortIO does not release a timer: CheckIO/WaitIO must reap it before the
packet can be replied or its slot reused. A completed timeout is observed before
granting that waiter. A close/cancel before delivery releases any provisional
grant. Startup failure unwinds all opened timer requests; DIE refuses outstanding
waits, timer ownership and notification messages.

The event loop rotates timer reaping, one waiter scan, timer submission/abort,
one deferred packet reply and one notification delivery. With asynchronous
resources it performs one such phase and at most three filesystem phases per
visit; otherwise it permits four filesystem phases. One waiter scan examines
at most 32 grants; owner close examines 16 waiters and 32 grants. Timers and
notifications progress while storage is pending. Timer durations are converted
from 50 Hz ticks with 32-bit division only.
Pending rescan work remains eligible after the last waiter is replied, so the
handler drains it and sleeps instead of spinning at handler priority. Control
signals returned by Exec.Wait are retained for the next bounded visit; polling
SetSignal alone would lose a Ctrl-C that woke the handler.

## Format and persistent volume labels

Format (1020) accepts a Latin-1 BSTR and the exact experimental CPX4 DOS type.
Labels are 1–30 characters, stored as canonical UTF-8 in the defined `LBL1`
state-page extension (128-byte payload); legacy 48-byte state pages retain the
`CopperCPX4` display default. Older CPX4 builds reject labeled pages. CPX3 and
production filesystems are unchanged. The full encoding and crash-publication
contract is in `new-filesystem-design.md`.

The packet refuses open handles/locks, notification resources, retained record
waits and write protection before mutation. Enumeration cursors are retired on
admission. Inhibited media can be formatted, and a successful format clears the
inhibit/offline state. Readable blank media now keeps a native handler available
for Format. When all candidate locations are readable and none contains a
valid anchor, the initial allocation unit is one device sector.
Native Format preserves the initialized geometry; the portable formatter also
supports explicit geometry changes. Volume identity/epoch overflow fails before
the destructive phase. Formatting is not atomic replacement or secure erase.

RenameDisk (9) journals one state-page image through six barriers without taking
reserve pages. It copies the input before yielding, permits existing open files,
and publishes the root notification and decoded label after retirement. Root
examination and QueryAttribute's 30-character limit agree with the DOS node.
The node uses a fixed 32-byte arena BSTR, with its original DOS-allocated name
pointer restored before cleanup. No operation adds a heap allocation or expands
the 69,632-byte arena ceiling. DOS list publication waits on its OS write
semaphore; that OS wait is distinct from bounded engine storage continuations.

Tests cover all four allocation-unit sizes, Latin-1/UTF-8 round trips, maximum
labels, invalid names, legacy images, preservation by the reference writer,
protection/resource admission and failed/cancelled formatting. Relabel works on
an exhausted volume. The 56 sector/barrier cut points under five persistence
schedules and 18 interrupted-replay cut points under five schedules retain an
old or new complete label. Six malformed committed labels are re-signed through
their journal envelopes and still rejected before any home write. Labeled
reformat tests cover 768 cut points under five schedules across same-geometry
and 512↔4096-unit reformats: old intact, new complete, or unmountable. Independent
ownership and content checks accompany recovery.

## Enumeration and information contract

ExamineObject/ExamineFH and their wide variants return the entry's name, comment,
type, protection, date, UID/GID, size and allocated data-unit count. Handles and locks
retain parent and entry identity, so examining distinct hard links returns the
selected name. File allocation counts include unwritten data and exclude holes
and metadata. Directories report zero byte size. Wide values use the existing
SFS2 FIB offsets 228 and 236; unrepresentable classic signed file sizes return
zero, matching SFS2. Root examination returns the persistent volume label, with
`CopperCPX4` as the compatibility default for legacy unlabeled images.

UTF-8 names decode to Latin-1 DOS bytes when representable in the 107-byte FIB
name field. Longer or non-Latin-1 names return a stable `__u` plus 16 hexadecimal
entry-ID alias, usable for lookup. Decoding processes at most 64 scalars per step
and validates the complete name before publishing the FIB. Comments decode from
the existing bounded UTF-8 attribute into at most 79 Latin-1 bytes. A malformed
name/comment or failed I/O leaves the caller's FIB and enumeration position
unchanged.

ExNext and ExNext64 use the existing 16-slot pool. The opaque disk key identifies
a cursor bound to its original FIB address and lock; copying it to a different
FIB is rejected with InvalidLock (211). Re-examination, end-of-directory and
FreeLock retire the corresponding token. Abandoned root cursors require
re-examination of their FIB; pool exhaustion returns NoFreeStore (103). Serial
values are not reused when the same dispatcher arena is reinitialized, and wrap
exhausts admission rather than accepting a stale token. End-of-directory returns
NoMoreEntries (232).

A cursor retains `(name hash, entry ID)`, not tree page addresses. Each packet
reloads the directory's current root and seeks strictly after its previous key.
This tolerates intervening splits and deletion. It is not a snapshot: new names
behind the cursor can be missed, and a renamed entry can appear again if its key
moves forward. No mutation can run during an active enumeration packet.

Info/DiskInfo report allocation-unit size, total units, used units, write state,
filesystem identity and volume node. Used volume units include reserved and
metadata allocations. Classic signed block counts saturate at INT32_MAX;
QueryAttribute returns exact paired counts. Supported queries are maximum DOS
component length (255), volume-label length (30), maximum unsigned file size,
case sensitivity, device type, total units and used units. Unknown attributes
return BadNumber (115); short output storage returns LineTooLong
(120). Output admission checks length, alignment and 32-bit address overflow;
the DOS caller remains responsible for supplying mapped, owned storage. ExAll
and ExAllEnd now implement levels 1–7 with the same sixteen enumeration slots.
Initial pattern/hook filters return ActionNotKnown so dos.library can perform
its own fallback. ED_OWNER (level 7) now returns persistent UID/GID in the
40-byte ExAll header; classic and wide FIB queries return the same identifiers.

## SetOwner and persistent ownership

Packet 1036 stores UID in the high 16 bits and GID in the low 16 bits. It accepts
files, directories and root, uses the same path handling as SetComment, and
preserves comments. Hard links share the object and its owner; new objects start
at zero without inheritance. These are stored identifiers, not an authentication
system; protection-bit semantics remain unchanged.

Nonzero ownership uses version 2 of the existing bounded attribute page. Owner
zero plus a comment uses the original version 1 encoding; removing both fields
returns the page. First use atomically journals the sticky `OWN1` state extension
with the owner update. Older CPX4 readers reject its 136-byte state payload.
Relabel preserves it; reformat resets it. Recovery validates every log image and
the required feature marker before any home write, including a separately read
home state when the marker was committed by an earlier transaction.

The operation retains the existing six-image attribute credit, six barriers,
13-page first-allocation reserve admission and fixed memory budgets. Updating
an existing attribute needs no new reserve page. Eight-level object lookup
does not increase the image count for an in-place object-record replacement.
Ownership notifications publish after retirement; cancellation quiesces storage
and leaves uncertain writes offline until recovery.

Tests cover four allocation sizes, both writers, independent decoding, Latin-1
comments, ownership through every FIB family and ExAll level 7, root/directories,
hard links, write protection, offline errors and exhausted reserves. Sector and
barrier cuts cover first allocation/feature activation, comment-page upgrade,
owner replacement/clear, comment clear and owned-object deletion; replay is
interrupted under the same five persistence schedules. Re-signed invalid
attribute schemas, invalid feature tags/bits and absent feature markers must
fail without a home write. Whole-handler qualification includes actual
dos.library SetOwner on eight-level trees; measured results appear below.

The six ownership mutation scenarios cover **492 sector/barrier cut points ×
five persistence schedules** and **220 replay cut points × five schedules**:
3,560 recovered images in total, checked for complete UID/GID pairs, file
contents and independent allocation ownership. Seven re-signed journal payload
cases and a missing previously committed feature fail before home writes in
both the resumable engine and reference reader. Six cancellation cases stop at
each publication barrier; metadata notifications are tested through all six
barriers and uncertain-write failure. These are host fault-injection results,
separate from the actual-HUNK ROM workload.

## Inhibit, cancellation and media ownership

Inhibit (31) refuses live handles, locks and retained record-wait packets with
ObjectInUse (202). Admission scans at most 64 resource headers; cursor/watch
retirement occurs in a later phase. Inhibit publishes its state only after a
successful persistence barrier. Repeating the same state is idempotent. Offline
Info/DiskInfo report ID_NO_DISK, media operations return NoDisk (226), and resource
cleanup and IsFilesystem remain available. Failed barriers never report success.

Uninhibit performs a fresh mount, including complete journal validation, replay
and durable cleanup. It advances the mount epoch and retires enumeration tokens;
old cached object identities never apply to replacement media. The partition and
allocation geometry must still match the original mount. Notifications retain
their copied paths and outstanding message ownership, suspend old object keys,
and refresh after remount. Open stale resources must be closed before remount.

Ctrl-C cancels the active storage operation as well as parked record waits.
Media removal does the same with NoDisk instead of Break (304). AbortIO is issued
once; CheckIO/WaitIO must finish before CompleteIo, packet reply, bounce-buffer
copy, transport reset or arena reuse. A late nominal success cannot acknowledge
a cancelled request. Split transfers stop before the next submission. Previously
retired write chunks retain their count and file position; the uncertain current
chunk is not acknowledged, even if recovery subsequently finds its commit.
The poisoned mount requires remount/recovery. Reads, writes, flushes and UNMAP all
use this stop path, including startup and shutdown I/O.

The startup-owned TD_ADDCHANGEINT request uses the primary request's opened
device/unit. Its 34-byte MC68000 callback only latches an event and signals the
task. Stub, Interrupt and control data occupy mount offsets 3744–3859, below the
DMA page, with no enlargement of the fixed arena. The held subscription is a
device-control request separate from the single outstanding storage transfer.
TD_REMCHANGEINT reuses the original request and is reaped before callback memory
or the device is released. Failed removal returns an error and retains ownership;
startup failure with an undetachable callback retains the mount instead of freeing
live memory. This exceptional path requires device/system intervention.

Only IOERR_NOCMD cleanly disables change subscriptions. Unexpected registration
completion invalidates media and requires explicit detach. Devices lacking change
interrupt support rely on disk-changed I/O errors or explicit Inhibit; there is
currently no polling fallback. The ROM hardfile used here lacks TD_ADDCHANGEINT:
its boot workload injects the callback's latch+Signal result. Separate compiled
tests execute the actual callback and delayed storage completion protocol. Neither
qualifies physical hot removal, a real controller's AbortIO semantics, or writes
already performed by a device before cancellation.
Protocol references: [dos.library Inhibit](https://developer.amigaos3.net/autodocs/dos.library/Inhibit.html)
and the [trackdisk change-interrupt protocol](https://wiki.amigaos.net/wiki/Trackdisk_Device).

## Handle, lock and parent contract

FileHandleFromLock (1026) takes a FileHandle BPTR and lock BPTR, reserves a
handle, then atomically transfers ownership. It preserves the lock's shared or
exclusive access mode and selected hard-link entry/parent, starts position at
zero and does not truncate. A directory, read-protected file, failed object read
or exhausted handle pool leaves both caller objects unchanged. Successful
conversion retires any enumeration tokens owned by the consumed lock.

CopyDirFH (1030) and ParentFH (1031) take the handler's private handle argument;
DOS's DupLockFromFH and ParentOfFH perform that translation. Parent (29) uses
the selected directory entry's parent for files, including distinct hard links,
and the stored parent for directories. The root has no parent and returns zero
without an error. A failed parent-lock allocation preserves the source handle,
its position and the source lock.

ChangeMode (1028) takes target kind zero for a lock or one for a FileHandle,
then a BPTR and shared/exclusive access mode. It validates the resource and
checks all other handles/locks before granting exclusive access. Conflicts
return ObjectInUse (202) without changing mode, position or contents. Parent and
duplicate locks use the same conflict rules. SameLock's packet result is a DOS
Boolean; the library converts it to its published comparison enumeration.

Resource scans have explicit continuation boundaries: conflict checks visit at
most 64 slots, then handle/lock reservation happens in a later phase. FreeLock
validates in one phase and retires at most 16 cursors plus its lock in the next.
Cursor retirement clears the already validated slot directly, preserves its
generation and avoids nested pool scans. Namespace conflict checks also yield
before destination-path validation or mutation admission. Tests reconstruct the
dispatcher across yields and meter pool control-word reads; the loop structure
separately bounds scalar slot searches. No persistent buffer or pool grew.

Software WriteProtect honors its key and publishes protection only after a
successful persistence barrier. Protected Flush and DIE still require a real
barrier, without admitting pruning or discard. MoreCache reports zero because
the handler's fixed arenas do not grow.

## Packet compatibility matrix

The target comes from `NativeAmigaFileSystem` and the existing SFS2 wide ABI.
No production handler source or identity was substituted for CPX4.

| Packet family | CPX4 status |
| --- | --- |
| Startup, CurrentVolume, DiskType, IsFileSystem, DIE | Implemented; DIE refuses live resources/async ownership and requires a successful barrier |
| FindInput / FindUpdate / FindOutput, End | Read existing, open-or-create, and create-or-truncate; bounded handles, protection/open-lock checks |
| FileHandleFromLock (1026), CopyDirFH (1030), ParentFH (1031) | Implemented; ownership transfer, entry context, pool exhaustion and DOS-library ABI tested |
| Read / Write | Sparse/unwritten reads, extent-list/tree growth, leaf splits, unwritten conversion and bounded retired progress |
| Seek / Seek64 (26400) | Implemented; checked signed offset, paired position, SFS2 pointer ABI |
| SetFileSize / SetFileSize64 (26401) | Inline/external shrink, sparse extension, shared-position minimum and post-commit output |
| LocateObject, FreeLock, CopyDirectory, Parent, SameLock, ChangeMode | Fixed pools, shared/exclusive conflict rules, parent paths, Boolean SameLock packet ABI and bounded retirement |
| ExamineObject / ExamineFileHandle, wide variants 26408/26410 | Names, aliases, comments, attributes, allocation and exact wide sizes |
| ExamineNext / 26409 | Bounded tokens, current-root reseek, retirement and stale-token checks |
| Info/DiskInfo, QueryAttribute 26407 | Allocation-unit geometry/counters and supported scalar/paired attributes |
| ExAll / ExAllEnd (1033/1035) | Levels 1–7 including ownership, bounded shared cursor pool, DOS filter fallback, explicit end/retirement |
| CreateDirectory, DeleteObject, RenameObject | Implemented; parent paths, ancestry and resource/protection checks; existing rename destination returns ObjectExists |
| SetProtection / SetDate / SetComment | Implemented |
| SetOwner (1036) | Persistent UID/GID, shared comment page, atomic feature activation and post-retirement notifications |
| MakeLink (1021), ReadLink (1024), link/path resolution | Hard/soft links, bounded resolution, cycle rejection and cleanup; actual DOS calls exercised |
| NewReadLink (26406) | One-hop, volume-qualified soft and hard link expansion, size queries and overlapping input/output buffers |
| AddNotify/RemoveNotify (4097/4098) | Eight fixed watches; message/signal, INITIAL/WAIT_REPLY, bounded message retirement |
| LockRecord/FreeRecord (2008/2009), wide 26402/26403 | 32 grants, 16 timed waiters, bounded asynchronous grant/timeout/cancellation |
| Flush | Writable: reclamation, persistence barrier, then bounded TRIM; protected: barrier only |
| WriteProtect (1023), MoreCache (18) | Keyed software protection with barrier-before-publication; fixed cache reports zero |
| Inhibit (31) | Barrier before inhibition, offline queries/cleanup, fresh recovery on uninhibit |
| Format (1020), RenameDisk (9) | Persistent 1–30-character Latin-1 labels stored as UTF-8; quiescent destructive format and journaled relabel |
| Private TRIM (CSTR, 0x43535452) | CopperTrim v1 capability query, whole-volume dry run and verified discard with completed range counts |
| WaitChar (20), Timer (30), DiskChange (33) | Mounted CPX4 returns DOS false and ActionNotKnown (209) immediately. Offline CPX4 returns DOS false and NoDisk (226). |
| Unsupported packets | Explicit ActionNotKnown; no invented DOS extensions |

## Validation and measurement scope

### DMA-constrained allocation

Before opening the device, startup checks the in-arena bounce page against
DosEnvec's memory type and DMA mask. If it cannot use that page, it asks Exec
for a separate buffer. Each candidate retains the requested memory class and
MEMF_PUBLIC. Startup tries the requested class first and then compatible
24-bit DMA and chip classes. Candidate sizes halve from 4096 bytes down to
one physical sector. At most twelve allocations are attempted, one at a time.
Unusable blocks are freed immediately. If no candidate fits, startup returns
DOS error 103 before device I/O. The retained allocation records its original
base and length for teardown, including failed startup.

Consecutive low zero bits in the mask constrain the buffer's starting
alignment. Higher zero bits constrain the whole byte range, including interior
addresses. Allocation padding is capped at one page. Transfers that cannot
use the caller's buffer split at the device limit and the actual bounce
capacity. Specific memory-class requirements force bouncing so arbitrary DOS
buffers need no runtime memory-class lookup. A bounced read publishes bytes
only after successful completion of its split request.

SCSI control, CDB, sense and payload addresses are also checked against the
mask. If the embedded SCSI fields cannot meet its alignment, discard reports
unsupported on that mount. Ordinary sector transfers continue through the
selected buffer. The mask and memory-class inputs come from the DOS environment
vector. [Amiga ROM Kernel Reference Manual, DOS](https://developer.amigaos3.net/sites/default/files/downloads/2024-10/Amiga_ROM_Kernel_Reference_Manual_DOS.pdf)

### Startup geometry discovery

DosEnvec supplies the physical sector size and partition bounds. Supported
sector sizes are 512, 1024, 2048 and 4096 bytes. The handler checks cylinder
arithmetic, the DOS block grouping, the 4 MiB minimum and the device transfer
limit before probing. It tests metadata pages of 1024, 2048 and 4096 bytes,
skipping pages smaller than a sector. Each size has two candidate addresses:
byte 65,536 and the last complete page of that size aligned to the partition.
All offsets are partition-relative until the transport adds the base.

Discovery reuses a scratch page. A lifecycle visit performs at most one
submission or reap and one candidate checksum. A bad checksum or local
trackdisk sector error (21–27) permits trying the other copies. A checksum-valid
CPX4 candidate with inconsistent fields, a disagreement about geometry or
volume identity, or a read fault with no usable copy refuses startup. A short
transfer fails and a media change returns DOS error 226. Recovery uses the
selected volume identity directly, so a permanently unreadable primary
sector cannot interrupt recovery with a second primary read. Mounting never
writes a replacement anchor. DOS Format remains available on readable media
without a recognized anchor.

A reaped IOERR_NOCMD on the first TD64 read permits one narrow retry when no
part of that read succeeded and the whole partition ends at or below byte
offset 2³² (exclusive end). Any later unsupported command fails. Splitting
and SCSI discard use the discovered sector shift. An inconsistent SCSI block
length disables discard for that medium.

The host suite covers all ten sector/allocation-unit combinations and
checksum-valid corruption in 14 anchor fields. Compiled MC68000 tests run
DosEnvec parsing and the startup loop with delayed device completions. They
cover a permanently unreadable primary, conflicting authenticated copies,
a trailing partial page, split 1024-byte-sector transfers and TD64-only media
above 4 GiB. Negative traces check every submitted address and stop at the
DOS error reply. The cancellation test also combines a damaged primary with
a committed journal and an interruption at the first home-write barrier.

In the damaged-primary ROM workload, the primary advertises a plausible
512-byte allocation unit under an invalid checksum. The backup specifies
4096 bytes. The DOS probe checks the mounted geometry, reads an existing
file, changes its contents, flushes and shuts down. A separate reference
mount checks the new file bytes and ownership and confirms that the damaged
primary is still present. The ROM hardfile reports 512-byte device sectors.

All seven Kickstart 3.1 workloads completed on an MC68000 with 512 KiB chip
RAM and zero fast or slow RAM. Each task had a 4096-byte stack with an intact
guard. Stack and free-memory measurements below are in bytes.

| Workload | Startup and recovery | Shutdown | Whole-handler peak | Minimum free RAM |
| --- | ---: | ---: | ---: | ---: |
| Damaged primary, 4096-byte allocation unit | 1,576 | 968 | 1,576 | 144,104 |
| Format and label | 1,064 | 968 | 1,632 | 144,104 |
| Format with unsupported TRIM | 1,064 | 968 | 1,632 | 144,104 |
| Depth-eight ownership | 1,576 | 968 | 1,632 | 144,104 |
| General growth | 1,576 | 968 | 1,632 | 143,608 |
| Committed-journal replay | 1,688 | 968 | 1,688 | 143,608 |
| General depth-eight tree | 1,576 | 968 | 1,632 | 143,608 |

The 194,676-byte HUNK used 176,264 bytes of loaded segments, a 69,632-byte
arena and 1,272 bytes of net OS/device mount allocations in addition to its
stack. The Release solution passed 2,242 tests with four skips and zero
failures. The separate compiled MC68000 suite passed 50 tests and the affected
CopperStart geometry and buffer replay suite passed 141. Growth and journal
replay initially exhausted the eight-minute host watchdog at 59,231 and
64,090 frames respectively. Both passed on sequential retry with the same
HUNK, guest probe, memory, stack, frame and time limits.

The 47 native inputs produced HUNK SHA-256
`8A473A273883BE50A5A6E69DAB2B1D9D1AA4D66E4875DD33872B5450A9212A2C`.
The DOS probe HUNK SHA-256 was
`77A390451E58763C361806416E7068F9E0C55AC62900A70DAC5921C22B272DD7`.
The source manifest, per-workload measurements and timeout summary are under
`artifacts/cpx4-discovery/checkpoint`. Device fault injection and larger
physical sectors were exercised at the compiled device boundary. Physical
SSD and controller testing remains open.

### Bounded startup, recovery and shutdown

Once the startup packet has supplied a device and partition, the native event
loop owns the anchor probe, optional narrow retry, mount and journal replay.
Each lifecycle visit advances at most one engine phase and submits or reaps at
most one primary storage request. It checks the media latch and Ctrl-C before
the next visit. A split transfer can resume without sleeping when its next
piece is ready to submit. DOS packets stay queued while startup owns the mount.
Recovery validates the complete committed log before issuing a home write.

The continuation uses 52 bytes at mount offsets 3952–4003 for the phase, DOS
message and packet, error, publication and discovery state. Startup opens one record timer
per visit. On a fresh mount the dispatcher uses the pools already cleared by
Exec. Startup replies before taking the DOS list lock for volume registration.
OS allocation, device-open and DOS list calls still run under their native OS
contracts. The visit bound counts handler work and device requests.

DIE checks for live resources, drives the persistence barrier or writable
flush, then detaches the change request. A cancelled transfer stays owned until
CheckIO/WaitIO completes. Break returns 304 and a media change returns 226.
A failed barrier refuses DIE and leaves the handler available for recovery.
After successful detach the handler unbinds the device node, replies to DIE,
closes one timer per visit, unregisters the volume, and answers queued packets
before exiting. Failed startup removes its subscription and closes the prefix
of timers it actually opened. An unconfirmed callback detach retains the
callback storage and device.

Compiled event-loop tests delay the primary I/O request, queue a second DOS
packet, and deliver Ctrl-C, a media callback or device error 29 while the
first packet is retained. They check the order of abort, reap and reply,
including a late successful device completion. A separate replay test stops
at the first home write or its following flush. In both cases an independent
reference mount replays the remaining obligation and verifies the file bytes
and allocation ownership. A delayed successful DIE barrier also exercises the
queued-packet reply during shutdown.

All six workloads reached shutdown on a Kickstart 3.1 MC68000 with 512 KiB
chip RAM and zero expansion RAM. Startup includes journal recovery when
present. The shutdown measurement runs from the workload's final DIE through
handler exit. Each 4,096-byte stack retained its guard and each backing image
passed its independent checks.

| Workload | Startup and recovery | Shutdown | Whole-handler peak | Minimum free bytes |
| --- | ---: | ---: | ---: | ---: |
| Format and label | 988 B | 964 B | 1,628 B | 146,264 |
| Format with unsupported TRIM | 988 B | 964 B | 1,628 B | 146,264 |
| Depth-eight ownership | 1,572 B | 964 B | 1,628 B | 146,264 |
| General growth | 1,572 B | 964 B | 1,628 B | 145,768 |
| Committed-journal recovery | 1,684 B | 964 B | 1,684 B | 145,768 |
| General depth-eight tree | 1,572 B | 964 B | 1,628 B | 145,768 |

The standalone HUNK occupied 192,112 bytes on disk. Loaded segments used
174,152 bytes, the arena 69,632 bytes, and net OS/device mount allocations
1,272 bytes. The first growth run reached the eight-minute host watchdog at
57,748 frames. Its repeat completed 69,033 frames in 280.51 seconds with the
same HUNK, frame limit, watchdog and emulated machine. The receipts, including
the first attempt, are under `artifacts/cpx4-bounded-lifecycle/checkpoint`.
The 47 native inputs produced HUNK SHA-256
`2197202A9260CDD93E05AEFD40FCD5E886481407F385CECE2D73E2387425197D`.
The Release solution passed 2,200 tests with four skips and zero failures.
The separate compiled-native run passed 32 cases, and CopperStart geometry
and buffer replay passed 141. Physical storage hardware remains unqualified.

### WaitChar, Timer and DiskChange

Direct DOS packets against the CopperFFS, CopperSFS and CopperSFS2 TRST
standalone handlers gave the following `dp_Res1 / dp_Res2` pairs for each of
the three actions. The reference probe sent each action before inhibition,
while inhibited, and after resuming the same volume.

| Volume state | CopperFFS | CopperSFS | CopperSFS2 TRST | CPX4 |
| --- | --- | --- | --- | --- |
| Mounted | 0 / 209 | 0 / 209 | 0 / 209 | 0 / 209 |
| Inhibited | 0 / 209 | 0 / 226 | 0 / 226 | 0 / 226 |
| Resumed | 0 / 209 | 0 / 209 | 0 / 209 | 0 / 209 |

The shared reference dispatcher defaults to ActionNotKnown. CopperSFS checks
the inhibited flag first. CPX4 follows that ordering whenever its medium is
unavailable, including after a device removal. Each reply takes one dispatcher
step with no storage request. An existing timed record-lock waiter keeps its
timer and reply port across these packets. Only a device completion can satisfy
that timer request. The ROM probe sends WaitChar with a live file handle and
a nonzero timeout, then continues to use the handle after all three replies.

The executable contracts are in `PacketContractTests`, `RecordWaitTests` and
`ClassicPacketCompatibilityTests.cs`. Reference-handler boots use the existing
correctness harness with expansion RAM. The CPX4 measurements below use the
separate 512 KiB harness.

All six CPX4 workloads passed on Kickstart 3.1 with an MC68000, 512 KiB
chip RAM and zero expansion RAM. Each reached shutdown and passed its
independent backing-image check. Every workload measured **616 bytes** on
each of the three packet paths and **1,724 bytes** over the whole handler.
All six 4,096-byte stacks retained their guards.

| CPX4 workload | Emulated frames | Minimum free bytes |
| --- | ---: | ---: |
| Format and label | 9,360 | 145,344 |
| Format with unsupported TRIM | 9,339 | 145,344 |
| Depth-eight ownership and supported TRIM | 23,716 | 145,344 |
| File growth | 68,976 | 144,848 |
| Committed-journal recovery | 69,062 | 144,848 |
| General depth-eight tree workload | 167,336 | 144,848 |

Loaded segments occupied 175,584 bytes, the resident arena 69,632 bytes,
and net OS/device mount allocations 1,272 bytes. The standalone HUNK was
197,136 bytes with SHA-256
`34537AA4DA1AC5AF87D7C96B50783CCCDB675CD9E163FAE13585DA275638A453`.
The three reference-handler boots left their backing images byte-identical.
The Release solution passed 2,191 tests with four skips and zero failures.
The 82 focused compatibility and record-wait cases passed, as did the full
412-case CPX4 core suite, 23 compiled MC68000 cases and 141 CopperStart
native replay cases. The focused runs overlap the solution totals.
The HUNK, source hashes and boot receipts are under
`artifacts/cpx4-classic-packets/checkpoint`. Physical hardware coverage
remains open.

### Private TRIM packet

CPX4 accepts the 32-byte big-endian version 1 record used by CopperTrim.
CHECK probes the current medium through SCSI VPD B2/B0 and READ CAPACITY(16).
A positive result reports a maximum in filesystem allocation units, a
granularity of one unit and partition-relative alignment zero. The native
transport checks that the SCSI logical block length matches the device sector
size from DosEnvec. Partition bases must be sector-aligned. Device limits are rounded down to whole allocation units
and capped at 4,096 units per command. A medium that cannot discard a whole
unit returns DOS error 209. Query works on a write-protected volume. DRYRUN
and execute require write access.

For either scan the handler completes a persistence barrier and validates the
retired state, reserve, group descriptors and all 16 bitmap pages per group.
It checks page identity, generation and CRC, reconciles per-window, group and
volume free counts, and independently checks permanent regions, the current
object root and the reserve. It then traverses the windows again under the
same mount ownership. DRYRUN counts proposed ranges. Execute submits one
contiguous UNMAP at a time, split at window and device limits. The handler
yields during both traversals and while waiting for device completion. Each
processing step scans at most one 4,096-bit window. Both passes use the
existing operation arena and page scratch space.

The v1 counters are unsigned 32-bit fields. A scan with more than
4,294,967,295 free units returns DOS error 115 before the first UNMAP.
An unsupported device returns 209 before the barrier and stays disabled until
a fresh mount. A later refusal of UNMAP also disables discard. I/O failures
and short completions report error 213 and require recovery. On failure, the
result record counts only confirmed complete ranges. The scanner trusts the
retired allocation bitmap for file extents, as the allocator does. An
independent ownership check is still needed to diagnose a consistently
misrecorded file extent.

### Private TRIM measurements, 2026-11-14

The standalone HUNK is 196,812 bytes, SHA-256
`9BA4847B4267CC2ED96C1F6893A729D53E08E60B057B019A74A074ABFD009B3A`.
Each ROM workload verified all 46 native source inputs and the HUNK before and
after execution. The direct Release probe assembly SHA-256 is
`9A20E58F2990F43B5549CC02B602CF6161D9A720D7E28DA52B0D28666367A850`.

| Kickstart 3.1 workload, MC68000 and 512 KiB chip RAM only | Frames | Whole-handler stack peak | Minimum free memory |
| --- | ---: | ---: | ---: |
| Depth-eight ownership, links and supported TRIM | 23,740 | 1,724 B | 145,448 B |
| Blank-media format with unsupported discard | 9,363 | 1,724 B | 145,448 B |
| Blank-media format with supported discard | 9,384 | 1,724 B | 145,448 B |
| General growth | 68,999 | 1,724 B | 144,952 B |
| Committed journal recovery | 69,085 | 1,724 B | 144,952 B |
| General depth-eight workload | 167,360 | 1,724 B | 144,952 B |

The probe sent all three CSTR modes through dos.library. The supported
workload measured 992 bytes of stack within the packet. Execute confirmed
the two ranges predicted by DRYRUN, and the later ordinary Flush issued one
additional automatic discard. The unsupported workload measured 880 bytes,
returned 209 for all three modes and issued zero discards, including its
final Flush. The backing images passed independent reopening and checks.

All six 4,096-byte stacks retained their guards. Loaded segments occupied
175,800 bytes, the resident arena 69,632 bytes, and net OS/device mount
allocations 1,272 bytes. The first growth run reached its 480-second host
watchdog at frame 45,561 during concurrent tests. Its separate retry
completed in 479.849 seconds with the same HUNK and limits.

The 20 focused host cases include four allocation sizes, checks against
independently reconstructed ownership, written and unwritten extents,
re-sealed reserve/root bitmap damage, final allocation groups, late corruption,
failed barriers, stale and short completions, media removal and unsupported
devices. The Release solution passed 2,178 tests with four skips and zero
failures, including all 406 CPX core cases. The separate ROM runs exercised
the opt-in boot gate. The direct native components passed 23 tests and
CopperStart replay passed 141. A standalone rebuild after concurrent SFS
profile edits produced a byte-identical HUNK. The frozen HUNK, source hashes, individual boot receipts and test
results are under `artifacts/cpx4-private-trim/checkpoint`.
Physical controller and SSD coverage remains open.

### NewReadLink packet contract

Packet 26406 takes a starting lock BPTR (zero for the root), a Latin-1 path
CString, a destination pointer and a signed byte capacity. With a null
destination it returns the required size including the terminator. A successful
copy returns the length excluding the terminator. A short or negative capacity
returns −2 with DOS error 120 before writing any output. An ordinary entry
returns −1/212, a missing component −1/205, a stale lock −1/211 and an offline
volume −1/226. The input limit is 1,023 bytes and the expanded result limit is
4,095 bytes, each plus a NUL. These bounds match the CopperStart DOS caller.

Resolution stops at the first link. A relative soft target uses the actual
containing directory, a leading colon uses the mounted volume label, and a
device-qualified target keeps its device. The remaining input suffix is
appended once, preserving Amiga parent components. CPX4 hard links to regular
files share an object. The lowest surviving entry ID identifies its canonical
name. Renaming that entry changes the returned path. Deleting it promotes the
oldest survivor. Reading the canonical entry itself reports ordinary-file
semantics. Directory components use the same Latin-1 names and stable aliases
as examination.

The operation snapshots the input and builds the answer backwards in the
existing operation arena. It reuses a directory-entry record and name decoder
while walking parents, with no recursion or enumeration token. For a
multiply-linked file it can search every live directory until it finds the
recorded number of links. This can hold the single mount operation for many
dispatch turns on a large volume. Each cursor yields between storage requests
and retains only fixed-size state. The lookup performs no disk writes.

The host tests exercise the 1,023-byte input limit, a 4,095-byte result from a
32-directory parent walk, refusal of a 4,096-byte result, exact capacity,
in-place expansion, Latin-1 external names, canonical-name promotion, delayed
completions, failed reads and media removal. The Kickstart 3.1 probe sends the
extension with `DoPkt`. In the depth-eight fixture it checks both link kinds
against `Owned:Inline` after a relabel and remount, verifies the size-only
answer and short-buffer error, and checks the trailing NUL and guard byte.

### NewReadLink measurements, 2026-11-14

The standalone MC68000 HUNK is 190,064 bytes, SHA-256
`08E8186A9D3BBB60ACFF8F69F2FB1D8A520FA3FF40331D8391C97B5B332BBAF5`.
All five full-ROM runs matched the 44 native source hashes before and after
execution. The direct Release probe assembly SHA-256 is
`DB400D8F9A3183A132A96245839D03CF2CA150DDF8A742E7E315175568CC84CC`.

| Kickstart 3.1 workload, 512 KiB chip RAM only | Frames | Whole-handler peak | Minimum free |
| --- | ---: | ---: | ---: |
| Depth-eight ownership and NewReadLink | 22,909 | 1,724 B | 151,768 B |
| Blank-media format and labels | 9,368 | 1,724 B | 151,768 B |
| Normal growth | 68,968 | 1,724 B | 151,272 B |
| Committed journal recovery | 69,055 | 1,724 B | 151,272 B |
| Depth-eight general workload | 167,310 | 1,724 B | 151,272 B |

NewReadLink reached **1,348 bytes** during packet dispatch and I/O completion.
The lowest observed handler A7 across startup through shutdown was 295,408
(stack bounds 293,036–297,132). All five 4,096-byte stacks retained their guard.
Loaded segment allocation was 169,968 bytes, the resident arena was 69,632
bytes, and net OS/device mount allocation was 1,272 bytes. The arena and full
stack together occupy 72 KiB. The loaded segment grew by 7,072 bytes from the
SetOwner checkpoint. Physical SSD/controller qualification remains open.

The final Release solution passed 2,159 tests with four skips (three external
media cases and the separately enabled ROM theory). All 389 CPX core cases
passed, including 15 focused link tests. The Release build reported one
nullable-reference warning in the sibling CopperSharp compiler.
The direct compiled-native component run passed 23 tests. CopperStart
geometry and native-buffer replay passed 141. Source snapshots, the HUNK,
build and harness hashes, and the individual ROM receipts are in
`artifacts/cpx4-newreadlink-native/checkpoint`.

### SetOwner checkpoint, 2026-11-14

The final standalone HUNK is **182,568 bytes**, SHA-256
`38F3DBC9C997FF1AAD266D90E220F9AB1A49706F2AC54A0F0619E19B0B01A55A`.
Its 43 native inputs and 133 source/build/test inputs are frozen under
`artifacts/cpx4-owner-native/checkpoint`. The direct Release harness assembly
SHA-256 is `0629BA8A061AB73431D421B38F9C05F6486D4B55BF47E3F1561D17C796D32934`;
the accompanying manifest records all harness assemblies. Native source hashes
and the handler hash must match both before and after every ROM workload.

The focused host suite passes **30 ownership/notification cases**, including
the 3,560 crash recoveries described above. The rebuilt direct Release component
suite passes **23 MC68000 cases**. CopperStart geometry/native-buffer replay
passes **141 cases**. A clean final Release solution build passes without warnings.
The full Release solution passes **2,152 tests, four skips, no failures**, including
**382 CPX core tests**, 296 FFS tests and 1,323 SFS tests. The SFS count reflects
unrelated concurrent work in this checkout; CPX added 29 tests relative to the
Format/label checkpoint. Three external-media skips and the opt-in ROM skip are
not counted as passes. The separately enabled ROM results follow.

The new ownership ROM workload passes with **512 KiB chip RAM, no fast/slow RAM,
MC68000, Kickstart 3.1 and a 4096-byte task stack**. Actual dos.library SetOwner
stores both halves, preserves a maximum-length Latin-1 comment, clears/recreates
attributes, sets root ownership, refuses missing/protected targets, relabels,
remounts, reads wide FIB ownership and enumerates ED_OWNER before flushing and
shutting down. The object and directory trees both start at eight levels.
The ROM images use 512-byte allocation units and 1024-byte metadata pages;
the handler still allocates the fixed 69,632-byte arena sized for the maximum
page geometry. All four allocation sizes are covered by the host tests.

| Full-ROM workload | Frames | Seconds | Whole-handler stack peak | Minimum free memory |
| --- | ---: | ---: | ---: | ---: |
| Depth-eight ownership | 10,354 | 121.588 | 1,508 B | 159,072 B |
| Blank-media Format and labels | 9,351 | 58.985 | 1,700 B | 159,072 B |
| Normal growth | 68,944 | 281.168 | 1,700 B | 158,576 B |
| Committed journal recovery | 69,030 | 255.847 | 1,700 B | 158,576 B |
| Depth-eight general workload | 167,275 | 599.522 | 1,700 B | 158,576 B |

All five runs pass with intact stack guards. Loaded handler segment allocation is
**162,896 bytes**, the fixed resident arena remains **69,632 bytes**, and net
OS/device mount allocation remains **1,272 bytes**, excluding the 4096-byte task
stack. Free memory is **159,184 bytes** after mount and after each workload,
and **234,120 bytes** after handler shutdown. The minimum across all five runs is
**158,576 bytes**. General-workload subfamily stack peaks are record locks
1,324 B, notifications 1,700 B, Inhibit/media handling 1,520 B and ExAll 1,080 B;
the dedicated ownership path peaks at 1,508 B. The 120,000-frame/eight-minute
ordinary limits and 180,000-frame/30-minute depth limits are unchanged, as are
RAM and stack limits. Receipts, source snapshots, source/harness manifests and
the qualification summary are in `artifacts/cpx4-owner-native/checkpoint`.

The measured peak covers executed paths, not every possible native failure path.
Ownership-specific crash injection is host-level; the actual-HUNK ownership
fixture exercises normal updates, admission errors, information queries and
remount. The separate general recovery ROM fixture exercises committed-journal
replay. Physical SSD/controller behavior remains untested.

### Format/label checkpoint, 2026-11-14

The Release solution build passed (one existing CopperSharp nullable warning).
Solution tests passed **2,030 tests, four skips and no failures**, including
**353 CPX core tests**. The solution also captures five additional unrelated SFS
tests relative to the preceding checkpoint. The direct Release component suite
passed **23 executed MC68000 cases**, including DOS label publication and original
name-allocation ownership. CopperStart geometry/native-buffer replay passed
**141 tests**. The three external-media skips and optional ROM theory skip do not
count as passes; separate ROM results follow.

The standalone HUNK is **179,840 bytes**, SHA-256
`FA435684C6D1CB1CDA8002786023058F634569797C35B356E7F74B68D02534FE`,
with **43 native source inputs**. Frozen qualification inputs, the 72-assembly
Release harness manifest and receipts are under
`artifacts/cpx4-format-native/checkpoint`. Boot receipts require the exact native
source and HUNK hashes both before and after execution.

Those four ROM runs use Kickstart 3.1, an MC68000, exactly **512 KiB chip RAM,
zero fast/slow RAM**, and a **4096-byte task stack**. The new blank-media workload
uses actual DOS Format and Relabel, a maximum-length Latin-1 label, refusal with
an open root lock, formatting while inhibited, lookup by the new Latin-1 volume
name, remount, file-content verification, flush and shutdown.

| Full-ROM workload | Frames | Seconds | Whole-handler stack peak | Minimum free memory |
| --- | ---: | ---: | ---: | ---: |
| Blank-media Format and labels | 9,312 | 74.534 | 1,700 B | 163,504 B |
| Committed journal recovery | 68,988 | 365.133 | 1,700 B | 163,008 B |
| Normal growth | 68,901 | 453.220 | 1,700 B | 163,008 B |
| Depth-eight trees | 167,232 | 983.894 | 1,700 B | 163,008 B |

All four runs preserve the stack guard. Loaded segment allocation is **160,600
bytes**, the fixed arena is **69,632 bytes**, and net OS/device mount allocation
is **1,272 bytes**, separate from the 4 KiB task stack. Free memory is **163,616
bytes** both after mount and after the workload in all four runs. Recovery
subfamily peaks are record locks 1,324 B, notifications 1,700 B, ExAll 1,088 B,
Inhibit 1,520 B and media handling 1,520 B; Format/labels peak at 1,700 B.
These are measured exercised paths, not exhaustive bounds for every failure path.
Normal/recovery retain 120,000-frame/eight-minute limits; depth retains its
180,000-frame/30-minute limit. No RAM, stack or execution limit was increased
for this checkpoint. The final direct Release harness assembly SHA-256 is
`00744B3722DAE61005733168AE53E9BA7A70D9629DDF8A0FC72759319F78C6AC`;
all four ROM receipts and the repeated 23-case component run use that assembly.
The archive contains 131 frozen source/build/test inputs and separate host,
component, CopperStart replay and full-ROM receipts.

The first new Format test completed the entire emulated workload and shutdown,
but failed a host assertion that incorrectly expected zero discard requests.
Final Flush correctly attempts one discard, which this device rejects as
unsupported. Only the assertion changed; the handler did not. That failed test
is retained under `failed-format-trim-expectation` and is not counted as a pass.
The corrected Format rerun passes content, ownership, memory and stack checks.
Physical SSD/controller tests remain unperformed.

### Previous Inhibit/media/ExAll checkpoint

The Inhibit/media/ExAll Release solution build passed with the existing
CopperSharp `M68kSpillAndCopy.cs` nullable warning and no errors. Solution tests
passed **2,004 tests with four skips**, including **336 CPX core tests**. That
solution's compiled snapshot contains 19 native cases; the separately rebuilt
direct Release suite below contains all 22 final native cases. The solution also
includes five concurrent, unrelated SFS tests. CopperStart geometry/native-buffer
replay passed **141 tests**. External-media and disabled optional-ROM skips are
not counted as hardware or boot passes.

The Inhibit/media/ExAll checkpoint adds 35 host cases: nine ExAll cases, ten
Inhibit lifecycle cases, two resource-scan scheduling cases and fourteen storage
stop/recovery cases. The latter stop at all six publication barriers for both
cancellation and removal, complete the outstanding request, then independently
check remounted contents and ownership. Separate multi-chunk cases preserve the
512-byte retired prefix. Focused qualification passed **282 tests**, with 125
unchanged input hashes. Its receipt is a host receipt; the HUNK hash captured
there predates the final native rebuild and is not the final native identity.

The direct Release native suite passed **22 tests**. New executed MC68000 cases
cover successful/unsupported/failed change subscription lifetimes, unexpected
registration completion during detach, signal/request allocation failures, and
the emitted interrupt's register preservation. Storage tests delay cancellation
of bounced reads, split writes, barriers and UNMAP, including late nominal
success. Three cases execute the actual `Run` loop through Ctrl-C returned by
Wait, a media event returned by Wait, and a disk-changed device error; none admits
the following packet or replies before WaitIO. Those paths peak at **576, 576 and
552 bytes** with intact 4096-byte guards under simulated Exec. The transport
stop paths peak at **616 bytes**, media subscription helpers at **52 bytes**, and
the compiled portable engine workload at **1324 bytes**. These component values
are separate from whole-HUNK/ROM stack measurements.

The rebuilt final HUNK is **175,568 bytes**, SHA-256
`2A2EE840855B5F70CCB15A2576C76DA01A572E63FC382CB74CD0973BCD20B9BF`,
with 40 native source inputs. Direct native results and the frozen input/harness
manifests are under `artifacts/cpx4-media-native`; the boot gate also validates
the source and HUNK hashes before and after executing each workload.

The first final-HUNK normal boot reached its unchanged 480-second wall limit at
65,826 frames while ExAll's hook-filter fallback was checksumming metadata
(substage 520). It reported no DOS error, poison or stack-guard failure, but did
not reach Inhibit/shutdown and is **not a pass**. Heavy concurrent host campaigns
were running. Its log, image and chip-RAM diagnostic are retained under
`artifacts/cpx4-media-native/failed-growth-wall-limit`; subsequent qualification
uses the same source, HUNK, frame limits, RAM and task stack.
The first depth-eight attempt likewise reached its 720-second host wall limit
at 139,409 of 180,000 allowed frames, during notification substage 315, with no
DOS error or guard failure. Its diagnostic is retained in
`artifacts/cpx4-media-native/failed-depth-wall-limit`. It overlapped other ROM
processes for most of the run. The successful normal retry took 390.993 seconds
and 68,845 frames with no source, HUNK or limit changes; the remaining fixture
results are recorded separately below.
The subsequent quiet depth-only retry also reached the 720-second host watchdog,
this time at 153,247 frames in ExAll pool substage 513, with intact guards and
no DOS error. That evidence justified changing **only the depth fixture's host
watchdog from 12 to 15 minutes**. Its 180,000-frame bound, 512 KiB RAM, 4096-byte
stack, handler and compiled DOS workload are unchanged. Normal/recovery retain
their eight-minute watchdog. The earlier harness DLL, source/manifests and failure
diagnostics are retained in `failed-depth-serial-wall-limit`. This harness-only
change is not represented by the earlier fast-tier source receipt; native inputs
remain identical and the final measurement records its own harness hash.
The 15-minute attempt reached 163,708 frames and uninhibit/remount substage 407,
again actively checksumming metadata with no DOS error and intact stack guards.
It is retained in `failed-depth-fifteen-minute-wall-limit`. Observed host
throughput varied from roughly 213 to 182 frames/second between quiet attempts.
The final depth harness therefore allows **30 host minutes**, retaining the
**180,000 emulated-frame bound** as the finite execution guard. This permits a
slow host to finish the expanded workload; it does not relax native per-step
scheduling, RAM, stack, completion or content checks. Actual measured completion
time, rather than the watchdog allowance, is reported for a successful run.

The following notification/record measurements describe the preceding checkpoint.
The final notification/record-lock checkpoint's Release solution build passed
with one existing nullable-reference warning in CopperSharp's compiler and no
errors. Solution testing passed **1,954 tests, four skips and zero failures**,
including **301 CPX core tests** and **nine compiled component probes**.
CopperStart's affected native replay passed
**141 tests**. Three external-media fixtures and the optional ROM theory are
skips, not passes. Final solution receipts are under
`artifacts/cpx4-record-lifecycle/final-solution-results`. The separate direct
Release run also passed **all nine compiled MC68000 component probes**, under
`artifacts/cpx4-notify-final/compiled-results`.
Its focused host run passed 115
tests; this overlapping subset is not added to the full-suite count. New tests
exercise all pool limits, independent generated range models, shared/exclusive
conflicts, high-half ranges, owner close, stale timer completions, cancellation
before submission and after provisional grant, completion before abort, delayed
reaping, and unrelated storage I/O while a wait is parked. Instrumented full-pool
tests check bounded scans, zero managed allocation and arena guards.
The final corrected fast qualification passed 247 tests and verified 113 captured
source hashes; its receipt is
`artifacts/cpx4-record-lifecycle/final-corrected-fast/receipt.json`.

Notification tests cover INITIAL/message/signal delivery, WAIT_REPLY, saturation
without starving another watch, removal with held messages, ignored caller-private
message fields, absent-path create/delete/recreate, hard-link identity, rename
parents and post-close file events. Delayed journal barriers and uncertain writes
verify that unretired mutations do not publish notifications. The compiled native
timer probe additionally checks real adapter request setup, asynchronous abort
ownership and cleanup after the ninth timer OpenDevice fails. Its observed stack
peaks are 572 bytes for normal operation and 280 bytes for startup-failure cleanup;
the Exec boundary in these component probes is simulated.
Two additional probes now execute the actual compiled handler Run loop. They
require it to drain rescan work after the final waiter reply and reach Exec.Wait,
and inject Ctrl-C exclusively through Wait's return value before delayed abort
completion. Both measured 704 bytes of stack. The final direct-Release component
run passed all nine probes; these event-loop tests retain a simulated Exec
boundary and are separate from the full-ROM workload.

The lifecycle checkpoint's Release solution build passed, and its full solution
test run passed **1,889 tests with four skips and zero failures**, including
**248 CPX core tests**. The focused lifecycle/information/packet/scheduling run
passed 103 tests. CopperStart's affected native geometry/buffer replay run
passed **141 tests**. The skips are three external-media fixtures and the
separately gated ROM theory; they are not counted as passes. These receipts are
in `artifacts/cpx4-lifecycle-final/results`,
`artifacts/cpx4-lifecycle-review/final-focused-results` and
`artifacts/cpx4-lifecycle-replay/results`.

Host tests cover four allocation-unit sizes, resumable tree/data/name operations,
pool exhaustion, delayed/stale completion, cancellation, paired TD64 ranges,
partition overflow, bounce selection and short completion. The strengthened
42-image journal sweep tested 384 sector/barrier cuts with five persistence
schedules: **1,920 recoveries**. Further sweeps cover native
inline writes, allocation/promotion,
host metadata extensions, interrupted replay and independent ownership checking.
Further native sweeps cover create with long names/refill/splits, replacement
rename with attributes, unlink, extent-tree truncation, exhausted-reserve cleanup,
and a second crash during replay/cleanup. Every recovered image is independently
decoded and checked for ownership and expected namespace/content alternatives.
Reserve exhaustion rejects creation without publishing a namespace change;
deletion can recover space and permit a subsequent refill/create. The rejected
operation may invalidate an unused journal slot, but does not advance generation.
Host fixtures verify a full seven-level split cascade uses 19 images and a full
eight-level path rejects ninth-level growth without publishing a prefix.

The strengthened namespace/cleanup sweeps cover **11,100 recovered images**:

| Faulted operation | Sector/barrier cuts | Persistence schedules | Recovered images |
| --- | ---: | ---: | ---: |
| Create, long name and object-tree split | 120 | 5 | 600 |
| Replacement rename and victim cleanup | 248 | 5 | 1,240 |
| Delete with attribute ownership | 148 | 5 | 740 |
| Extent-tree shrink and cleanup | 398 | 5 | 1,990 |
| Full-volume, empty-reserve deletion | 996 | 5 | 4,980 |
| Reserve refill followed by create | 212 | 5 | 1,060 |
| Second crash during replay/cleanup | 98 | 5 | 490 |

The extent-write extension passed **1,960 recovered images** with the strengthened
crash fixture: cuts before and after every sector write and on both sides of
barriers, retaining all outstanding versions of a sector. Five persistence
schedules run per cut. This supersedes the earlier 1,100-image extent sweep;
these are overlapping validations, not additive coverage.

| Faulted extent write | Cuts | Recovered images |
| --- | ---: | ---: |
| Seventh-descriptor promotion | 98 | 490 |
| Extent-tree leaf split | 118 | 590 |
| Unwritten tree split with both insertions | 102 | 510 |
| Unwritten inline-list promotion | 74 | 370 |

Functional tests cover 512/4096-byte units, growth past a full leaf, logical
positions above 4 GiB, partial unwritten writes at either end and in the middle,
stale physical data beneath unwritten mappings, empty-tree reuse, and unchanged
file allocation counts during unwritten conversion. The checksum-per-step meter
also executes extent promotion and unwritten conversion with delayed completions.

Information-packet tests cover 512/4096-byte allocation units, split directories,
Latin-1 conversion, external UTF-8 names, long-name aliases and alias lookup,
distinct hard-link names, comments, wide sparse sizes, classic and wide packet
contracts, token exhaustion/reuse, stale/foreign cursors, empty directories,
read-only mounts, checked output buffers and reseeking after splits/deletes.
Malformed stored UTF-8 must preserve the caller's FIB and return its uncommitted
cursor slot. A separate instrumented test checks one checksum per step and
stable outstanding requests across delayed completions for these packet paths.

Lifecycle tests cover shared/exclusive conversion, failed conversion without
consuming a lock, read protection, both ChangeMode targets, conflict rejection,
parent identity for hard links, root parents, handle/lock exhaustion, keyed
software protection and failed barriers. New scheduling regressions fill all
16 enumeration slots before FreeLock and force reservation of the final handle
slot for each open mode. Parent exhaustion and ChangeMode round-trips check
that position, contents and selected entry survive unchanged.

Compiled component probes execute mount/commit, TD64 splitting/error handling,
Exec delayed I/O/abort, asynchronous UNMAP discovery/submission, and the
coordinated portable formatter/sparse-write/comment/link/cleanup path. They enforce
4096-byte stack guards and absence of managed allocation sites. Their simulated
Exec boundary is reported separately from the real-ROM runs.

The full-ROM tests load the generated HUNK through DOS under licensed Kickstart
3.1, with **512 KiB chip RAM, zero fast RAM and zero slow RAM**. They exercise
startup, DOS file open/read/write/seek/close, inline resize, protection failure,
lock/unlock, wide seek/read above 4 GiB logical file position, create directory,
create/open files, tree splits, cross-directory rename, open-file deletion refusal,
external shrink/zero extension, replacement-open followed by writing, deletion,
seventh-descriptor promotion, extent-tree insertion and append, unwritten
conversion that splits a full leaf, reference-created empty-tree reuse,
classic/wide root enumeration, Latin-1 and alias names, SetComment followed by
enumeration, wide ExamineFH, Info/DiskInfo/QueryAttribute, empty-directory and
invalid-output failure paths, handle/lock conversion, duplicate/parent locks,
ChangeMode success and conflict refusal, software protection, hard/soft links,
cycle refusal, flush/UNMAP and shutdown. The current workload also exercises
classic and wide record packets, all 32 grants and 16 wait slots, actual timer
expiry, grant after cancellation/reaping, owner-close and Ctrl-C cancellation,
and unrelated packets while waiting. Actual StartNotify/EndNotify calls cover
WAIT_REPLY coalescing, post-close mutation, held/queued message removal, request
memory reuse and signal delivery for an initially absent directory's creation
and deletion. Both raw lifecycle packets and actual
DOS OpenFromLock/DupLockFromFH/ParentOfFH/ParentDir/ChangeMode calls execute in
the boot workload, including failed directory conversion preserving its lock.
The expanded workload adds ExAll names/comments and aliases, copied-lock cursor
transfer, all sixteen cursors and exhaustion, explicit ExAllEnd, and real
dos.library pattern/hook fallback. Inhibit exercises live-resource refusal,
idempotence, offline queries/access refusal and fresh remount. An injected media
event exercises stale-handle refusal, offline cleanup and a second remount.
Separate synthetic fixtures exercise eight-level object and directory
trees and startup replay of a committed, unretired transaction. Final media is
reopened and checked after the emulator releases it.

The stack sampler records A7 instruction-by-instruction while the handler task
runs, including its OS/device calls, and checks a guard at the bottom of the
actual 4096-byte task stack. It uses the repository's scalar diagnostic emulator
path; it does not replace DOS packets or the device driver with host dispatch.
It measures executed paths, **not an exhaustive whole-handler maximum**. Full
seven-level split cascades and core replacement rename have host coverage;
their exhaustive compiled-path qualification, complete replay-failure/media-removal matrices and
unimplemented packet families cannot be certified by these results.

Final source hashes, HUNK compatibility report and build inputs are in
`artifacts/native-cpx4-handler`. Boot receipts are in
`artifacts/cpx4-boot-512k/{growth,depth8,recovery}/qualification.json`.
Receipts separate HUNK file size, DOS loaded-segment allocation, resident arena,
task stack, net OS/device mount allocation and the observed free-memory minimum.
Net OS/device allocation is a memory-accounting delta, not an allocation trace.
The generated images, licensed ROM and binaries remain outside tracked source.
Physical SSD/controller qualification has **not** been performed.

Build the instruction-test project directly in Release for ROM qualification;
do not reuse its output from a solution build with `--no-build`. The solution's
external-project configuration mapping can resolve the emulator dependency to
Debug even when the solution configuration is Release. The lifecycle checkpoint's
first three boot attempts stopped in a Debug emulator sprite-order assertion
before the handler workload; those failures remain in
`artifacts/cpx4-lifecycle-final/qualified-results` and are not filesystem passes.
The separate direct-Release qualification and its dependency hashes are retained
under `artifacts/cpx4-lifecycle-qualified`. No emulator assertion or source was
changed for this checkpoint. This does not qualify the failing Debug harness.

At the historical lifecycle checkpoint, the expanded eight-level DOS workload
reached the former eight-minute allowance
at 119,357 frames without completing (DOS error zero, 1,636-byte stack peak,
guard intact). This is retained as a timeout, not a pass. Its bounded execution
allowance was raised to 180,000 frames/12 minutes; normal and recovery retained
120,000 frames/eight minutes. RAM remains exactly 512 KiB and the enforced stack
ceiling remains 4096 bytes. The depth rerun uses the identical handler and DOS
probe workload; only the host harness's execution allowance changed. The later
Inhibit/media/ExAll host-watchdog change is recorded above.

### Current Inhibit/media/ExAll full-ROM results

All three complete workloads passed with the same **175,568-byte handler** and
identical compiled DOS probe, on MC68000 with **512 KiB chip RAM, no fast/slow
RAM and a 4096-byte task stack**. Each run completed shutdown and independent
filesystem ownership/content checks.

| Workload | Frames | Host seconds | Whole-handler stack peak | Minimum free RAM |
| --- | ---: | ---: | ---: | ---: |
| Normal image | 68,845 | 390.993 | 1,700 | 168,360 |
| Eight-level object/directory trees | 167,162 | 657.707 | 1,700 | 168,360 |
| Committed-journal startup recovery | 68,931 | 318.160 | 1,700 | 168,360 |

All stack guards remained intact. Measured family peaks were **1,324 bytes** for
record waits, **1,700** for notifications, **1,088** for ExAll, and **1,520** for
both Inhibit and media-event/remount processing. These are executed-path peaks,
not a proof of the maximum across unexecuted paths.

DOS loaded-segment allocation was **156,864 bytes**; the fixed arena remains
**69,632 bytes**, with **1,272 bytes** net OS/device mount allocation. The latter
increased by 56 bytes for the retained change request. Free RAM after the complete
workload returned to its mounted value of **168,968 bytes** in every fixture;
after handler shutdown it was **243,904 bytes**. The probe continues to own its
loaded segment and bootstrap device entry after shutdown.

Four preceding host-timeout attempts are retained and are not passes. The final
depth run used the documented 30-minute host watchdog but actually completed in
657.707 seconds, within both the original 12-minute allowance and the unchanged
180,000-frame bound. No native source, handler, compiled DOS workload, RAM or
stack limit changed between those attempts. Host throughput variability must
not be confused with target CPU or scheduling qualification.

The normal/recovery receipts use host harness SHA-256
`914CFA04CACA6FB16C2CEC2EE8C678C776B24EFC3DFD7CECB6FD96DCF6E9C7EA`;
the final depth receipt uses
`B8E0207CA64527A2388C4F831217A5D48D2F2B577035CB8AAE052DD48A959BFC`.
Both emitted the identical DOS probe HUNK, SHA-256
`83566AC470A43C2833C1D6E305A9D8FDD48D417C22593335827820CC16E2F223`.
Per-workload receipts, old/new harness and source manifests, the frozen native
sources, handler and qualification summary are archived under
`artifacts/cpx4-media-native/checkpoint`. Physical media removal and controller
behavior remain unqualified; the ROM media event is explicitly injected.

### Historical notification/record-lock full-ROM results

The first notification/record-lock growth attempt reached its 120,000-frame cap
while servicing record locks. It is a failure, retained in
`artifacts/cpx4-notify-qualified/boot.log`, with a 1,668-byte stack peak and intact
guard. Short ROM diagnostics isolated a rescan that stopped being serviced after
the final waiter reply, starving the caller at lower priority. Review also found
that ignoring Exec.Wait's return value lost a Ctrl-C wake. Both native event-loop
paths were corrected without increasing RAM, stack or full-workload time limits.
Diagnostic-only runs are marked `AsyncDiagnosticOnly` and kept under
`artifacts/cpx4-async-diagnostic`; they do not qualify the full workload. Their
10,000-frame diagnostic cap also stopped the depth fixture during notification
namespace work; that result is not a pass.

The corrected notification/record-lock handler is a **164,700-byte HUNK**,
SHA-256 `06A21D8BE8581D80DCD49A5E923658BBA4FB5C0CDCF564E6303F0EBAB705A37D`.
Its complete normal-image workload passed in 238.49 host seconds / 59,408 frames.
Whole-handler stack use peaked at **1,696 bytes**, including the notification
path; record-lock processing peaked at **1,320 bytes**. The 4096-byte stack
guard remained intact. DOS loaded-segment allocation was **147,560 bytes**,
the fixed arena **69,632 bytes**, and additional net OS/device mount allocation
**1,216 bytes**. Minimum free RAM was **180,432 bytes**, including the test
process and its buffers. The 680-byte increase in OS/device allocation over
the lifecycle checkpoint corresponds to the startup timer port and 16 requests.
The matching 37 native source inputs, HUNK and harness hashes are archived in
`artifacts/cpx4-notify-final/checkpoint`. The complete eight-level fixture also
passed: 538.81 host seconds / 142,755 frames, with the same stack peaks, intact
guard and memory measurements. Startup recovery followed by the complete workload
passed in 219.74 seconds / 59,494 frames, again with identical stack peaks and
memory measurements. **All three enabled full-ROM tests passed**, with no skips
or failures in the corrected run; final media passed independent ownership and
content checks. Their guards remained 512 KiB chip / zero fast / zero slow RAM,
MC68000 and a 4096-byte task stack throughout.

| Corrected full-ROM workload | Frames | Host seconds | Whole stack | Record stack | Notification stack | Minimum free RAM |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Normal image, all implemented workload families | 59,408 | 238.49 | 1,696 | 1,320 | 1,696 | 180,432 |
| Eight-level object and directory trees | 142,755 | 538.81 | 1,696 | 1,320 | 1,696 | 180,432 |
| Committed-journal startup replay, then full workload | 59,494 | 219.74 | 1,696 | 1,320 | 1,696 | 180,432 |

Stack and RAM values are bytes. FreeAfterWork equals FreeMounted (181,040 bytes)
in each run. These measurements qualify the executed paths, not an exhaustive
whole-handler worst case or physical SSD/controller behavior. The corrected
receipts supersede the initial failed run without erasing it.

### Historical lifecycle checkpoint

The lifecycle checkpoint uses the 148,872-byte HUNK, SHA-256
`40E97ECFC49CAE78790453EB75ECD9C480556018FEA2944CC10DEA46A0B7EE43`.
All three completed boots measured a DOS loaded-segment allocation
of 133,616 bytes (HUNK file headers/relocations
are not resident code). The fixed arena was 69,632 bytes; the task stack was
4096 bytes; additional net OS/device mount allocation was 536 bytes. Minimum
free RAM during those workloads was 198,368 bytes. This includes the
test process and its buffers; it is not a prediction for a user's Workbench.

| Actual HUNK workload | Peak task stack | Guard | Result |
| --- | ---: | --- | --- |
| Namespace/extent mutations, enumeration/info, lifecycle and links, raw packets and DOS-library calls, failure paths, UNMAP, shutdown | 1,636 bytes | Intact | Passed |
| Eight-level object and directory trees, followed by the same workload | 1,636 bytes | Intact | Passed with the expanded harness allowance |
| Committed-journal startup replay followed by the same workload | 1,636 bytes | Intact | Passed |

The HUNK, 34 native source inputs, build/compiler hashes, probe sources and
completed boot receipts are archived in
`artifacts/cpx4-lifecycle-qualified/checkpoint`. Its harness manifests record
every dependency DLL hash. The earlier information-only checkpoint remains
archived separately in `artifacts/cpx4-info-native/checkpoint`.

Normal, depth and recovery workloads completed in 442.49, 478.15 and 290.43 host
seconds respectively (54,491 / 129,797 / 54,577 emulated frames). The depth
workload exceeded the former 120,000-frame ceiling. The rerun's compiled DOS
probe matches the timeout probe byte-for-byte, SHA-256
`7E501E6032B895DAFF5433785B6455FED598089A7DB57872D6DE61E90889F3FA`.
All three media images passed independent ownership/content checking after
shutdown. Five separate compiled component probes also passed. These completed
results supersede the depth timeout without erasing it or the Debug-harness
failures from the qualification record.

### Historical information-packet checkpoint

Before lifecycle and DOS-link support, Release solution testing passed 1,843 tests with
three external-media skips and one disabled optional-ROM theory. Final
CPX4-specific validation passed **209 host tests**, including the strengthened
crash fixture, packet contracts, deterministic scenarios and coordinated
comment/sparse-write, formatter and link-core changes. The focused information,
scheduling and packet-contract run passed 72 tests before native qualification.
The checkpoint also passed
**five compiled MC68000 probes** and **three explicitly enabled full-ROM boots**,
with no failures or skips in the final native run. They are recorded separately from the integration
checkpoint; the optional skip is not counted as boot coverage. CopperStart's existing
geometry/buffer native replay passed 141 tests, and its integrated filesystem
suite passed 297 tests. CPX3 provenance hashes remain
unchanged (19 matching provenance hashes). The final handler manifest contains
32 matching source hashes. That ROM workload retained eight created files,
tree splits and the then-implemented mutation/information families, with a 120,000-frame/eight-minute
run guard per boot. RAM and task-stack limits were not raised. All three boots
completed within the original guards, despite concurrent host test load.
This is the information-packet checkpoint; concurrent later qualification
and feature additions are not implicitly covered by these receipts.

Final targeted receipts: `artifacts/cpx4-info/final-packet-results` and
`artifacts/cpx4-info-native/qualified-results`. The focused host run overlaps
the full suite; its results are not added to the full host suite count.
The qualified HUNK, source manifest and matching boot receipts are also archived
at `artifacts/cpx4-info-native/checkpoint` before subsequent dispatcher work.
The Release solution build was run
successfully after the final core changes. The full-solution checkpoint and
CopperStart replay receipts are in `artifacts/cpx4-info-release` and
`artifacts/cpx4-info-replay/results`, respectively. CopperStart integration
receipts are in `artifacts/cpx4-info-copperstart/results`. The solution build
reported one existing nullable-reference warning in the CopperSharp compiler.

### Reproducing the current checkpoint

```powershell
Remove-Item Env:COPPERCPX_BOOT -ErrorAction SilentlyContinue
Remove-Item Env:COPPERCPX_ASYNC_DIAGNOSTIC -ErrorAction SilentlyContinue
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx4-media-release
dotnet test CopperFS.slnx -c Release --no-build --artifacts-path artifacts/cpx4-media-release
./build/Build-Handler.ps1 -Cpx4
dotnet test tests/CopperCPX.InstructionTests -c Release --artifacts-path artifacts/cpx4-media-native --filter 'FullyQualifiedName!~CpxBootTests'
$env:COPPERCPX_BOOT = '1'
dotnet test tests/CopperCPX.InstructionTests -c Release --no-build --artifacts-path artifacts/cpx4-media-native --filter 'FullyQualifiedName~CpxBootTests'
```

The normal solution suite skips the optional ROM gate unless explicitly enabled;
a skip is not a pass. The licensed `COPPERSHARP_KICKSTART31_ROM` must be present
for the enabled boot test. Existing CopperStart geometry/transfer replay is run
separately; CopperStart is not a standalone-handler dependency.
Run the ROM gate after other CPU-heavy campaigns finish: its finite host wall
timeout is independent of the emulated frame, RAM and stack bounds. Preserve
timeout diagnostics and report any subsequent retry explicitly.
