# New Amiga filesystem design and safety review

Draft for implementation planning, 13 November 2026.

This proposes a new filesystem with small allocation units, variable-length
extents, inline file data, UTF-8 names with an explicit legacy charset interface,
and first-class discard support. Its main design constraint is predictable work
and bounded memory on a 68000. The format is not frozen: the review below identifies
several requirements that must be resolved before a production writer is built.

The new format must have its own identity, core and handler. It does not change
CopperFFS or CopperSFS formats, namespaces, packages, packet ABI or journal policy.
No magic number, DosType, package name or private packet number is assigned here.
Portable execution remains allocation-free, with caller-owned storage and one
owner per mount. CopperStart supplies OS integration; CopperDisk/CopperScreen
own host storage and sparse deallocation. Standalone native handlers must build
through CopperFS.Amiga using CopperSharp without a CopperStart dependency.

## Review conclusion

The 13 November revision fixes directory keys, tree height, transaction credits,
metadata reserves and byte-to-UTF-8 conversion. The resulting largest admitted
foreground operation uses at most 42 distinct metadata images and 20 newly
assigned live metadata pages, within a 64-image journal and a 32-page reserve
working target. Reserve refill itself can acquire 32 uninitialised pool pages
using at most six journal images. These are
algorithmic bounds under the rules below. The coordinated CPX3 host prototype now
enforces operation credits and exercises recovery, tree-depth admission and
reserve exhaustion. Its 42-image recovery witness is a deliberately constructed
journal transaction, not a claim that a natural file workload reaches that bound.
See [the implementation qualification](cpx3-prototype-qualification.md) for the
executed cases and remaining native gates; finite tests are not a general proof.

| Finding | Consequence | Draft resolution or remaining gate |
| --- | --- | --- |
| Checkpoint fallback and journal reuse were unspecified | Recovery could replay obsolete metadata over newer data or need an overwritten log | Explicit retirement rule and two checkpoint copies; see transaction protocol |
| Repeated staging of one dirty page was unbounded | A small transaction could exhaust its slot | Fixed staging-image slots, rewritten only before commit |
| A metadata page can span several device sectors | Torn home writes must not invalidate the only recovery path | Full-page redo, retained until home writes and checkpoints are durable |
| Commit failure has an uncertain outcome | Retrying the operation could apply it twice | Freeze normal mutations, recover, then reconcile; no automatic logical retry |
| Data written before a metadata commit can remain exposed through an old extent | Metadata journalling does not make file overwrites atomic | State separate data and metadata guarantees |
| Freed blocks could be reused or trimmed too early | Recovery could restore a reference to destroyed data | No reuse or discard until the freeing transaction is retired |
| Large names reduced directory-tree fanout | More tree levels and dirty pages | Fixed 16-byte directory keys, 128-byte leaf entries, optional name pages, height at most eight |
| Metadata allocation could recursively expand a transaction | Split credits alone understated dirty pages | Persisted reserve pool; no allocation-summary tree in v1 |
| Legacy conversion and comparison were underspecified | Unreachable names or collisions | DOS bytes converted through an immutable volume charset; Latin-1 default; no native Unicode packet required |
| Six page buffers omitted several resident costs | The earlier roughly 10 KiB figure understated mount memory | Revised itemised budget plus stack, DMA and per-open resources |
| A backup anchor is not a metadata mirror | Corruption after journal retirement may be unrecoverable | Fail closed, independent checker and salvage tool; no self-healing claim |

## Units and limits

All multibyte on-disk fields are big-endian, decoded explicitly rather than
through native struct layout. Records are at least word-aligned. Reserved bytes
are written as zero and covered by checksums. The formatter selects:

| Symbol or property | Draft value |
| --- | --- |
| Allocation unit A | 512, 1024, 2048 or 4096 bytes |
| Metadata page P | max(1024, A) bytes |
| Group size G | 65,536 allocation units |
| Address | Unsigned 64-bit allocation-unit number, partition relative |
| File size | Unsigned 64-bit byte count |
| Object and directory-entry IDs | Unsigned 64-bit identifiers, zero reserved |
| Page checksum | CRC32C, reflected polynomial 0x82F63B78, initial/final XOR 0xFFFFFFFF |
| Maximum distinct metadata targets per transaction | 64; current operation bound at most 42 |
| Maximum tree path | Eight pages including root |
| Minimum volume | 4 MiB, subject to formatter space validation |
| Maximum stored filename | 768 UTF-8 bytes, excluding padding |
| Maximum legacy input component | 255 bytes at format level; packet interfaces may impose a lower limit |

A must be an integral multiple of the advertised logical sector size. Unsupported
sector sizes fail formatting/mounting rather than requiring hidden sub-sector
read-modify-write. P is an integral multiple of A. Metadata addresses must be
aligned to P/A allocation units. Device physical-sector size, failure atomicity
and discard granularity are separate properties.

64-bit fields do not imply that all 64-bit unit addresses can be converted into
64-bit byte addresses. Validate volume length, partition base, exclusive ends and
every shift/addition against representable device byte ranges. Use subtraction
for bounds checks where computing an exclusive end could overflow. Driver and
legacy DOS API limits can be lower than format limits.

Transaction IDs and allocated identifiers must not wrap. Exhaustion refuses new
mutations. Tree-height limits can also constrain usable objects or directory
entries before the address space is exhausted; those limits must be published.

## Volume regions

Offsets below are partition-relative bytes and are multiples of all proposed P
values. The formatter verifies that the regions do not overlap.

| Byte offset | Region |
| --- | --- |
| 0 through 65,535 | Reserved boot and integration area |
| 65,536 | Primary immutable format anchor, one P-byte page |
| 65,536 + P | Checkpoint A, one page |
| 65,536 + 2P | Checkpoint B, one page |
| 65,536 + 3P | Mutable volume-state page |
| 65,536 + 4P | Charset/profile pages, bounded by the group-0 descriptor |
| 65,536 + 8P | Metadata reserve control page, one page |
| 131,072 | Group-0 descriptor, followed by its 16 bitmap pages |
| 1,048,576 | Journal slot A, 512 KiB |
| 1,572,864 | Journal slot B, 512 KiB |
| 2,097,152 onward | Available group-0 space, except other allocated structures |
| Last P-aligned complete page | Backup immutable format anchor |

Groups start at absolute allocation-unit multiples of G, including group 0.
Later groups place their descriptor at the group start and their 16 bitmap
pages immediately after it. Group 0 uses the special location above. Its first
2 MiB are permanently allocated. Other groups reserve their first 17P bytes.

The last group may be partial. If its descriptor, bitmap pages and any overlapping
backup anchor cannot fit, its entire tail is unusable and implicitly reserved;
do not read nonexistent group metadata. A remainder smaller than P at the volume
end is also reserved. The backup anchor is always excluded independently of the
bitmap. A group can therefore have no allocatable units.

The format anchor contains the UUID, format version, A/P/G shifts, total units,
feature flags, all fixed-region addresses, slot sizes, charset-page count,
reserve-control address and comparison-profile ID. Charset/profile data uses
at most four pages from +4P through +7P. Anchors are immutable after formatting
in v1. Conflicting
valid anchors require diagnosis, not automatic selection of whichever is newer.
The backup is located from the supplied partition extent, not a pointer in a
damaged primary anchor. The CPX4 handler obtains sector size and partition
extent from DosEnvec. For each supported page size at least as large as one
sector, it checks the page checksum and immutable fields at both locations.
All authenticated candidates must agree on the allocation geometry and volume
identity before journal recovery begins. See [native startup geometry
discovery](cpx4-native-progress.md#startup-geometry-discovery). Resizing is outside v1.

The fixed 2 MiB reservation and 4 MiB minimum deliberately exclude ordinary
Amiga floppy volumes. This is an SSD/hard-disk format proposal. Supporting floppy
media would require a separate small-volume profile and another memory/layout
review, not merely reducing the journal constants.

The mutable state page holds the object-index root, root-directory ID, work-queue
head ID, next object/entry IDs and the global free-unit count. There is no
allocation-summary tree or orphan tree in v1. It is an ordinary journalled
metadata page. Format anchors do not contain mutable tree roots. Checkpoints
record journal retirement, not alternative snapshots of the filesystem.

### CPX4 persistent volume label extension

CPX4 accepts the original 48-byte state-page payload, which
displays `CopperCPX4`, and 128 bytes with the following fixed extension. CPX3 and
the production filesystems are unchanged. This is an experimental CPX4 layout
extension: older CPX4 readers reject labeled state pages rather than interpreting
their extra bytes. New readers still accept original unlabeled CPX4 images.
The ownership extension below adds a third, 136-byte payload.

| Page offset | Bytes | Meaning |
| --- | --- | --- |
| 112 | 4 | `LBL1` extension tag |
| 116 | 2 | UTF-8 byte count, 1–60 |
| 118 | 2 | Encoding version, 1 |
| 120 | 60 | Canonical UTF-8 label; unused bytes zero |
| 180 | 12 | Zero padding |

Labels contain 1–30 Latin-1 characters. DOS supplies Latin-1 bytes, which the
handler encodes into UTF-8; NUL, ASCII controls, DEL, slash and colon are refused.
The stored extension must decode strictly to this same character repertoire.
The journal validator checks the tag, encoding, length, characters and padding
before replaying any home image. Future wider label repertoires need a separately
defined encoding/version contract; this does not restrict UTF-8 file names.

RenameDisk changes one state-page image using the existing six transaction
barriers. It allocates no metadata or reserve pages. Root examination and the
DOS volume entry publish the new label only after journal retirement. The
independent decoder and reference writer understand and preserve the extension.
The low-level formatter retains an explicit unlabeled option for reproducible
legacy images; DOS Format requires a valid label and the exact CPX4 DOS type.

### CPX4 persistent DOS ownership

`ACTION_SET_OWNER` (1036) stores a packed unsigned word: UID in bits 31–16,
GID in bits 15–0. Files, directories and the root default to zero; creation does
not inherit ownership. Hard links share their object's metadata. As with
SetComment and SetProtection, a final soft-link name addresses that link object.
The handler stores and returns these identifiers without adding a login or
credential model; existing protection-bit behavior is unchanged. This follows
the [AmigaOS SetOwner contract](https://developer.amigaos3.net/autodocs/dos.library/SetOwner.html).

Ownership shares the object's single external attribute page with its comment.
The common header's owner at offset 24 remains the **object ID**, not the UID.
Version 1 remains the existing comment-only encoding: u16 version 1 at 64,
u16 zero at 66, u16 comment tag 1 at 68, u16 UTF-8 length 1–158 at 70,
then the comment at 72; payload length is 8 plus the comment length.

| Page offset | Bytes | Version 2 attribute meaning |
| --- | --- | --- |
| 64 | 2 | Attribute version 2 |
| 66 | 2 | Zero |
| 68 | 4 | Packed UID/GID, nonzero |
| 72 | 2 | Comment tag, 1 |
| 74 | 2 | UTF-8 comment length, 0–158 |
| 76 | variable | Comment encoded from at most 79 Latin-1 DOS bytes |

Version 2 payload length is exactly 12 plus the comment length. A zero-length
comment is legal for owner-only metadata. Clearing ownership rewrites a surviving
comment in version 1; clearing both removes and releases the attribute page.
Changing either field preserves the other. Cleanup and independent ownership
checking account for this one page once, regardless of the number of hard links.

The first nonzero ownership update also journals a sticky state-page feature
marker in the same transaction: payload length 136, the existing `LBL1` data at
112–191, `OWN1` (u32 0x4f574e31) at 192, and u32 feature bits exactly 1 at 196.
An unlabeled volume receives the default persistent `CopperCPX4` label. A labeled
volume retains its label. Unknown feature bits/tags are corruption; no home
image is replayed until every committed image has passed validation. Version 2
attributes require the ownership feature; reference and independent whole-image
checking enforce that cross-page relationship. Runtime attribute reads enforce
it after mount. Journal replay validates every schema, then requires the feature
in the committed state image or, if no state image exists, reads and validates
the previously committed home state before issuing any home write.

Older CPX4 builds reject the 136-byte state payload. This prevents an older
writer from silently losing UID/GID when changing a comment. The marker remains
after the last owner is cleared and through RenameDisk; Format resets it. This
does not alter CPX3, CopperFFS or CopperSFS formats or provide migration.

SetOwner uses the existing six-image attribute transaction credit and six
publication barriers. First-page allocation needs 13 reserve pages (one plus
the 12-page margin); an existing attribute page can be edited with no free
reserve. Refill may retire separately. No owner is published on a failed
admission, and ownership notifications wait for transaction retirement.
Comments encode at most 64 input bytes per phase; ownership updates copy at
most 158 existing UTF-8 bytes, a fixed bounded copy. Two spare operation scalars
at arena+15068/+15072 and mount scalars at +496/+500 hold the values, cached
feature and recovery-validation flags. The 160-byte comment input and 256-byte object workspace are
reused; resident arena, pools and the 4096-byte task stack are unchanged.

Classic and wide Examine/ExamineNext/ExamineFH return UID/GID at FIB offsets
224/226. ExAll level 7 (`ED_OWNER`) uses a 40-byte header and places UID/GID at
36/38, after the comment pointer at 32. Lower levels retain their existing ABI.

DOS Format requires all handles, locks, notifications and retained record waits
to be released; it retires enumeration cursors and preserves the established
allocation geometry. It also works while inhibited and on readable blank media.
The formatter increments the current volume identity and mount epoch, checks
overflow, invalidates all possible old anchors, and persists that invalidation
before overwriting filesystem metadata or journal slots. A crash leaves the old
volume intact, a complete new volume, or no mountable anchor. It never promises
atomic replacement of an existing filesystem or secure erasure. Failed barriers
and cancelled formatting leave access disabled until successful recovery or
explicit reformatting. The incremented identity is local incarnation tracking,
not a globally unique UUID or a clone-identification mechanism.

The native label adds no arena or per-operation allocation: its decoded BSTR is
at arena+3864, DOS publishes a separate BSTR at +3904, and +3936 stores the DOS
node and original name pointer. Formatting/relabeling owns 30 input bytes at
+16000 and a length at +16032, sharing the mutually exclusive link workspace.
The original DOS-allocated name pointer is restored before `FreeDosEntry`.
Publication acquires the DOS list write semaphore after storage retirement;
semaphore contention is an OS wait, not a bounded storage continuation. This
specific DOS wait is outside the per-step storage-request bound and remains part
of the broader startup/shutdown/OS-wait scheduling review.

Formatting must not publish an apparently mountable anchor before initial roots,
bitmaps, state, invalid journal commits and checkpoint C=0 are durable. For a
destructive reformat, first invalidate the old anchors and persist that change;
then initialise the new UUID and metadata, persist them, and publish/persist the
backup and primary anchors. A crash can leave an unformatted volume. Formatting
does not promise to preserve the previous filesystem. Any boot-registration
publication follows successful filesystem initialisation.

## Metadata pages

Ordinary metadata pages begin with this 64-byte header. Anchor, journal and
checkpoint types additionally bind their payload to the volume UUID.

| Offset | Bytes | Field |
| --- | --- | --- |
| 0 | 4 | Magic |
| 4 | 2 | Page type |
| 6 | 2 | Structure version |
| 8 | 4 | CRC32C, zero while calculating the checksum |
| 12 | 4 | Flags |
| 16 | 8 | This page's allocation-unit address |
| 24 | 8 | Owner object ID or reserved system owner |
| 32 | 8 | Last modifying transaction ID |
| 40 | 8 | Right sibling where applicable, otherwise zero |
| 48 | 2 | Entry count |
| 50 | 2 | Tree level |
| 52 | 2 | Used payload bytes |
| 54 | 10 | Reserved |

The checksum covers all P bytes, including unused space. In addition to CRC,
validate type, self address, owner, level, counts, record boundaries, sibling and
child ranges before using a page. No on-disk field is a native pointer.
CRC bytes are stored big-endian. The check value for ASCII `123456789` is
0xE3069283; the empty byte string gives zero. This fixes CRC parameters, not
the final type/magic assignments. Directory hashing uses the same byte CRC.

A staged redo image retains its target home address in this header; its journal
descriptor identifies its staging location. Do not reject the image merely
because it was read from the journal, and do not accept a mismatched target.

CRC detects accidental corruption with finite collision probability; it is not
authentication. The design does not promise survival of arbitrary torn control
records that happen to validate, lying flushes, writes outside requested ranges
or controller failure that corrupts unrelated sectors.

## Allocation and summaries

One bitmap page describes 4096 units using 512 bytes starting at offset 64.
One means allocated or unavailable; zero means free. Within a big-endian u32,
bit 31 describes the first unit. Remaining payload bytes are zero. Invalid units
in a partial group are always marked allocated.

For a unit address represented by high and low 32-bit halves:

```text
group base   = high : (low & 0xFFFF0000)
group offset = low & 0xFFFF
bitmap index = group offset >> 12
bit index    = group offset & 0x0FFF
```

The descriptor records valid units, exact free units, a search hint, exact
per-bitmap free counts and advisory free-run hints. Descriptor and bitmap changes
are journalled together. V1 has no summary tree: maintaining one while allocating
its own split pages would create a recursive transaction-credit problem. Search
from a locality hint, visit group descriptors, and scan one bitmap page at a time.
The global free-unit count in the state page changes in the same transaction as
allocation. Wide group bases are never narrowed to 32-bit group numbers.

Summaries never authorise allocation or TRIM. Verify the bitmap and fixed-region
exclusions. Free-run hints may become conservative, but an ENOSPC result must not
depend solely on a possibly stale hint. Search continues with a fixed cursor,
yielding after at most four bitmap pages per scheduling turn; an unsuccessful
complete scan establishes lack of suitable space. This bounds RAM/work per turn,
not whole-volume allocation latency. A bitmap/reference inconsistency cannot be
detected by checksums alone; the independent checker verifies reachability and
ownership. Detected disagreement suspends writable operation and discard.

With A=512 and P=1024, 16 bitmap pages use 16 KiB per 32 MiB group; the descriptor
makes 17 KiB, about 0.052 percent before other metadata. Even a 1 TiB volume has
about 512 MiB of bitmap pages on disk. It must not have that bitmap in RAM.
Group-local scans trade some on-disk padding for shifts and masks instead of
division by irregular bitmap payload lengths.

Pages freed by the current transaction remain unavailable until retirement.
Allocation consults both committed metadata and the transaction's staged
allocation changes. It must distinguish newly reserved space from space freed
inside the transaction; a staged zero bit alone is not permission to reuse it.
Avoid a volume-sized pending-free set by checking the committed bitmap as well.

### Metadata reserve pool

The fixed reserve control page contains a u16 count at payload offset 64, six
zero bytes, and 64 u64 page addresses starting at offset 72; unused entries are
zero. Its 584 bytes fit the smallest P. Each listed page is P-aligned and already
marked allocated in the ordinary bitmap. The pool, rather than a file, owns it.
Reject duplicate, reserved-region, live-tree or out-of-volume entries in checking;
mount validates the bounded list and bitmap allocation, while full disjointness
against all live trees is the independent checker's responsibility.

Before an operation needing new metadata, refill to 32 pages if possible. One
refill transaction can add up to 32 aligned P-byte pages selected from at most
two bitmap windows. It changes at most six targets: two bitmaps, two group
descriptors, reserve control and volume state. Store at most 32 selected addresses
in admission scratch. Fragmentation may require several bounded refills, but a
normal refill does not force a separate commit/flush sequence for every page.
It does not insert into any allocation tree. Pool pages need no data initialisation until
assignment because no file can read them. A newly assigned page is initialised
completely and journalled along with removal from the pool. There is no bitmap
update when ownership transfers from the pool to a live metadata structure.

Create, link and file-growth admission must leave at least 12 pool pages after
their worst-case reservation. Up to 20 pages suffice for the largest create
class, so 32 is the normal refill target; capacity is 64 to accept returned pages.
Rename may use the protected 12-page margin, reserving at most ten pages. It may
still fail for lack of reserve; this is not a guarantee of infinitely many
renames on a full disk. Unlink, initial shrink and reclamation require no new
metadata pages and can proceed without refilling the pool. Reserved pages are
allocated space for capacity reporting and TRIM, not hidden bitmap-free space.

Returned metadata pages enter the pool when it has room, or are freed through
their bitmap/descriptor. Neither becomes reusable until transaction retirement.
Refill is independent maintenance, completed before the logical operation;
aborting that operation can leave extra pool capacity but no namespace change.
The pool replaces the earlier unspecified metadata-space reserve. It costs
32P normally (32-128 KiB on disk), up to 64P, and one resident page only when
accessed. It does not require 32 resident page images.

## Object records and extents

Objects live in a B+ tree keyed by stable object ID. Leaf records are 256 bytes.
An object leaf has three records at P=1024 and fifteen at P=4096 before any
additional leaf framing; v1 must not silently add framing that changes capacity.

| Offset | Bytes | Field |
| --- | --- | --- |
| 0 | 8 | Object ID |
| 8 | 8 | Regular-file byte size, directory entry count, or symlink target byte length |
| 16 | 8 | Allocated-unit count |
| 24 | 8 | Extent-tree root or zero |
| 32 | 8 | Directory-tree root or zero |
| 40 | 8 | Parent ID for live directories; next work-item ID for queued regular/orphan objects |
| 48 | 8 | Single attribute-page address or zero |
| 56 | 8 | Modification timestamp |
| 64 | 8 | Creation timestamp |
| 72 | 4 | Amiga protection flags |
| 76 | 4 | Link count |
| 80 | 4 | Generation |
| 84 | 1 | Object type |
| 85 | 1 | Storage mode |
| 86 | 2 | Flags |
| 88 | 2 | Inline byte length |
| 90 | 2 | Inline extent count |
| 92 | 4 | Reserved |
| 96 | 160 | Storage-mode payload |

The payload holds up to 160 file bytes, six extents with 16 zero padding bytes,
a short symlink target, or an empty-directory representation. For the external
symlink storage mode, the first eight payload bytes hold its page address and
the rest are zero; file size records the target byte length. Amiga comments
and bounded optional attributes use one P-byte page with a 64-byte common header
and length-delimited entries; an update cannot overflow into an attribute tree
in v1. Total attribute payload is at most P-64. A longer symlink uses one metadata
payload page (at most 768 UTF-8 bytes), allocated from the same reserve; longer
targets fail explicitly. Initial create does not also install an attribute page.
Allocated-unit count charges external regular-file payload including unwritten
extents, excludes holes and metadata, and is zero for inline contents. Directory
and symlink metadata are not charged as regular-file payload. Volume allocation
statistics include all metadata and reserve pages independently.
Directory counts change with their leaf entries in the same transaction and
are exported according to the DOS directory ABI, not as regular-file byte sizes.
The checker recomputes them. Directory emptiness must not be inferred from an
empty-looking root page while underfull child pages remain.

Each extent is 24 bytes: logical starting unit u64, physical starting unit u64,
unit count u32 and flags u32. Count is in 1..65,536. Physical extents do not cross
group boundaries. Logical extents are ordered and nonoverlapping; every sum and
conversion is checked. Missing ranges are sparse holes, read as zero. Reserved
unwritten extents also read as zero and are not TRIM candidates. More than six
extents uses a tree; the inline and tree modes are mutually exclusive in v1.

Inline-to-extent growth writes initialised external data before publishing the
new object. Extent-to-inline shrink journals the retained bytes and frees the old
extents in the same transaction, or keeps them owned by the queued object until
reclamation completes. While shrink work is pending, retain the extent storage
mode even for a now-small file; convert to inline only after cleanup.
Neither path exposes uninitialised bytes. Extending after truncation must zero
the newly exposed tail even when it shares an allocated unit with old data.
Do not zero bytes still visible through the old file merely to prepare a shrink.

Inline contents receive metadata-journal protection. Ordinary external file-data
overwrites do not. A reset may leave an external overwrite partially applied,
even if the associated timestamp/size change was not committed. A failed write
may similarly have changed data. This storage-mode difference must be documented;
no uniform atomic-file-write API is promised.

Each eight-byte timestamp stores signed s32 days since 1978-01-01, u16 minutes
within the day (0..1439), and u16 ticks within the minute (0..2999 at 50 Hz), all
big-endian. This preserves DateStamp precision without a wide multiply/divide.
Values are Amiga local wall-clock stamps, not claimed UTC instants; host tools
must preserve them or use an explicit user-selected timezone conversion. Never
silently use the host timezone. The all-zero value retains Amiga's unset/epoch
ambiguity. Invalid minute/tick fields are rejected. See the
[DateStamp autodoc](https://developer.amigaos3.net/autodocs/dos.library/DateStamp.html).
Object generation is one in v1; IDs are never reused after committed allocation.
Page transaction IDs provide mutation versions. Any future ID-reuse feature
must define generation changes and exhaustion as an incompatible revision.

## Directory trees and names

### DOS bytes and UTF-8 storage

Ordinary DOS packet names remain byte strings. The handler decodes each component
using the volume's explicitly selected 8-bit charset and stores UTF-8. Enumeration
performs the inverse conversion. No Unicode support in DOS, UTF-8 autodetection,
per-process locale or new native Unicode packet is required for v1.

The default profile is ISO-8859-1: byte b maps to scalar U+00bb. Bytes below 0x80
retain their one-byte UTF-8 representation; other bytes encode as two bytes.
For example `caf` followed by byte E9 is stored as `63 61 66 C3 A9` and returned
to DOS as `63 61 66 E9`. Bytes C3 A9 supplied by a legacy caller are two Latin-1
characters and must not be guessed to mean one UTF-8 character. Windows-1252 is
not interchangeable with Latin-1; select a different table explicitly if needed.
The [Unicode Latin-1 mapping](https://unicode.org/Public/MAPPINGS/ISO8859/8859-1.TXT)
documents the code-point correspondence; no downloaded mapping fixture is added.

Format-time alternative 8-bit tables are permitted, immutable thereafter. They
map bytes injectively into BMP scalars, excluding surrogates; undefined positions
use distinct private-use values without colliding with other entries. ASCII
0..127 mappings are identity mappings. Each table includes an idempotent 256-byte
case-fold table with valid representatives. These constraints make a 255-byte
component expand to at most 765 UTF-8 bytes. Stored names allow 768 bytes, so
UTF-8 expansion no longer silently halves the legacy format-level name limit.
The actual DOS packet interface may accept fewer input bytes.

For the default Latin-1 comparison table, fold ASCII A..Z to a..z, C0..D6 to
E0..F6, and D8..DE to F8..FE; leave all other bytes unchanged. In particular,
multiplication sign D7, sharp-s DF and FF are unchanged. This is a fixed profile,
not a promise to reproduce every historical filesystem's comparison quirks.
Validate comparison against the chosen Amiga compatibility target in packet
tests; never call a mutable OS locale table during lookup.
Alternative profiles retain that ASCII folding and supply their non-ASCII
classes explicitly. The Latin-1 fast path uses direct scalar/byte conversion
and does not need the general decode/inverse lookup tables; the memory budget
below deliberately allows the more expensive configured-table case.

Comparison decodes UTF-8, maps representable scalars to bytes, applies the fold
table, maps back to scalars and re-encodes UTF-8. Unrepresentable scalars remain
unchanged. Feed that canonical byte stream incrementally into CRC32C and compare
the same streams for equality. Do not materialise another complete folded name.
There is no normalisation or full-Unicode case folding: composed/decomposed
spellings may differ. Host image tools can store valid UTF-8 outside the selected
legacy repertoire; aliases make such names accessible to Amiga callers. A future
native UTF-8 API is optional and does not alter existing packet interpretation.

Reject malformed UTF-8 at host import, surrogate scalars, NUL, colon and slash
inside stored components. Legacy input is split into components before conversion.
Other bytes round-trip, subject to the caller/API's filename syntax. Count UTF-8
bytes before any mutation; reject overlength rather than truncating or substituting
question marks. Preserve spelling; a folded-name collision rejects the new entry.

For unrepresentable or API-overlength names, enumeration returns `__u` plus
16 hexadecimal digits of the entry ID, 19 ASCII bytes. Reserve exactly this
19-character shape, after case folding, from real names at every creation
interface; ordinary names such as `__user` remain valid. Legacy
alias lookup scans for that entry ID in the addressed directory; it never treats
an arbitrary ID as permission to access another directory. No alias index is
included in v1. Entry IDs are volume-unique, never reused after committed
allocation, and retained on rename; separate hard links get separate IDs.
Normal representable names are returned as their original legacy bytes.

The initial classic packet profile advertises at most 107 returned filename bytes
for FileInfoBlock, not 255, and obeys BSTR versus C-string conventions at the
correct boundary. The structure has 108 filename bytes; one is needed for its
length/terminator representation. Longer imported names use aliases. Name input
can use the packet's supported component bound up to 255; no caller buffer is
assumed larger than its ABI. See the [Examine autodoc](https://developer.amigaos3.net/autodocs/dos.library/Examine.html).

### Fixed-size index keys and leaf entries

Use a 16-byte directory key: u32 canonical-name CRC32C, u32 zero, u64 entry ID.
Compare hash numerically, then entry ID. A hash is only a search accelerator.
Lookup scans every entry with the matching hash and compares its full canonical
name; insertion checks that whole range for duplicates before assigning an ID.
Even a directory in which every name has the same CRC remains correct. CRC32C
is not collision-resistant: hostile names can make lookup/duplicate checks O(n).
Process at most 64 candidates per continuation with fixed scratch, and retain
ownership or revalidate after a generation change. This bounds memory and work
per turn, not adversarial total latency. Do not silently accept hash equality as
name equality or claim a hard logarithmic name-lookup bound.

Every directory leaf entry is exactly 128 bytes, with no slot-offset array:

| Offset | Bytes | Field |
| --- | --- | --- |
| 0 | 4 | Canonical-name CRC32C |
| 4 | 4 | Zero key padding |
| 8 | 8 | Entry ID |
| 16 | 8 | Target object ID |
| 24 | 8 | Name-page address, zero when inline |
| 32 | 2 | UTF-8 byte length |
| 34 | 2 | Flags |
| 36 | 2 | Legacy byte length, or 0xFFFF when unrepresentable |
| 38 | 10 | Reserved |
| 48 | 80 | Inline name bytes, unused bytes zero |

Names up to 80 UTF-8 bytes are inline. Longer names use exactly one P-byte name
page owned by entry ID: common header, u16 length at 64, u16 zero at 66, name
bytes at 68. The 768-byte limit fits P=1024 with room to spare. The leaf's inline
area is zero when external. A name is never split across pages. Updating a long
name allocates a replacement page before releasing its old page, retaining the
entry ID. Name pages and leaf-pointer updates participate in the same transaction.

This trades space for a strict fanout: a tiny entry consumes 128 bytes before
tree occupancy overhead, and a long name adds one metadata page. With P=1024,
three 256-byte objects per leaf and seven directory entries per leaf give about
488 bytes of combined fully occupied object/index-page cost per tiny file,
before fragmentation or other metadata. The earlier space-efficiency claim must
be judged against that cost. Long names no longer enlarge internal separators.

### Tree shape and depth

All tree pages have the 64-byte common header and a packed fixed-size entry
array. Directory internal entries are 24 bytes: lower-fence key (16) and child
page address (8). Object and extent internal entries are 16 bytes: lower-fence
key (8) and child address (8). Object leaves use 256-byte records; extent leaves
use 24-byte records. Internal entries describe child key intervals, not the
minimum currently live key. The first interval starts at the tree's inherited
lower bound. Deleting its smallest live key therefore need not rewrite ancestors.
No sibling links are maintained in v1; the common header's sibling field is zero.
Enumeration uses a scalar path or a fresh upper-bound search.

| Page kind | Capacity at P=1024 | Capacity at P=4096 |
| --- | ---: | ---: |
| Directory internal children | 40 | 168 |
| Directory leaf entries | 7 | 31 |
| Object/extent internal children | 60 | 252 |
| Object leaf records | 3 | 15 |
| Extent leaf records | 40 | 168 |

Insert bottom-up. A full page plus one entry splits by count into floor/ceil
halves; the algorithm streams the virtual extra entry into two output pages
instead of requiring an oversized contiguous input page. Split propagation may
touch one old and one new page at each level, plus a new root. Store at most
eight 16-byte path records (u64 address, u16 child index, u16 level, u32 reserved).
One mount-generation value validates the whole operation/path set; a page version
is rechecked when reread rather than stored in every path entry. An insertion requiring
a ninth level fails admission before namespace change or external data writes.

There are no synchronous sibling redistributions or merges on deletion. Remove
the record from its leaf and retain fences/underfull pages. Empty-branch pruning
and root collapse are separate nonallocating maintenance transactions. They do
not run as hidden work inside rename or create. They can revisit paths using
bounded memory; no stack of all leaves is retained.

For insert-only directory trees at P=1024 after balanced splits, nonroot internal
fanout is at least 20 and leaves hold at least four entries. For height h>=2,
an occupied shape has at least 2*20^(h-2)*4 entries; a completely full shape has
at most 7*40^(h-1). At h=4 these are 3,200 and 448,000; at h=6 they are 1,280,000
and 716,800,000; at h=8 they are 512,000,000 and 1,146,880,000,000. These are
shape arithmetic, not promised usable directory sizes. Deletions break the lower
occupancy bound, and available media/IDs impose additional limits. The absolute
height-eight limit still holds in a sparse tree. A full root at maximum height
can refuse an insertion despite free disk space; report the format-capacity
limit explicitly. Offline rebuilding may improve such a sparse tree.

Directory enumeration is hash/key ordered, not alphabetical. Store a last-returned
16-byte key and generation in a bounded per-enumeration context; never encode a
wide key by truncating it into fib_DiskKey. Use an opaque nonreused 32-bit context
token within the classic API and caller-supplied bounded context storage. On
context exhaustion return an ordinary resource error, never repurpose reserved
FileInfoBlock bytes. After mutation resume at the next greater key; concurrent
rename/create may be missed or observed again, but an unchanged entry is not
repeated by page splits. Enumeration is not a snapshot. Retiring contexts and
their tokens must follow lock/mount lifetime, with explicit exhaustion handling.

## Transaction credit proof

These bounds count distinct P-byte home metadata images; journal headers,
descriptors, commit records and checkpoint copies have separately reserved
storage. Let H=8. One tree insertion has a conservative target bound I=2H+1=17
and needs at most N=H+1=9 new pages. A nonstructural record update/deletion costs
one target and no new pages. Two insertions are bounded by 2I even if they share
pages. Height admission is checked against the staged tree before any data I/O.

The bound relies on the reserve pool, no allocation-summary tree, fixed-width
keys, no back-links, no hidden merges, no attribute-tree insertion and no
transactional recursion. Replenishment occurs separately. Newly assigned
metadata consumes pool entries in one reserve-control target, not bitmap pages
in arbitrarily many allocation groups. Releasing old metadata can still touch
its bitmap and group descriptor; those targets are counted explicitly below.

| Operation class | Conservative target calculation | Maximum targets | New live metadata pages |
| --- | --- | ---: | ---: |
| Create file/directory or bounded symlink | Directory insertion 17 + object insertion 17 + parent object 1 + name page 1 + optional symlink page 1 + state 1 + reserve 1 | 39 | 2N+2 = 20 |
| Hard link | Directory insertion 17 + parent/target objects 2 + name 1 + state 1 + reserve 1 | 22 | N+1 = 10 |
| Cross-directory rename with replacement | Destination insertion 17 + source/victim leaf deletes 2 + four object records 4 + replacement name 1 + state 1 + reserve 1 + two old-name bitmap/descriptor pairs 4 | 30 | N+1 = 10 |
| Unlink/rmdir of an empty directory | Directory leaf 1 + parent/target objects 2 + state 1 + reserve 1 + old-name bitmap/descriptor 2 | 7 | 0 |
| One attribute-page change | Object 1 + replacement attribute page 1 + state 1 + reserve 1 + old-page bitmap/descriptor 2 | 6 | 1 |
| One data/extent mutation chunk | Up to two extent insertions 34 + one existing extent leaf update 1 + object 1 + state 1 + reserve 1 + at most two bitmap/descriptor pairs 4 | 42 | 2N = 18 |
| Payload/work-queue reclamation chunk | Extent leaf 1 + up to two object leaves 2 + state 1 + reserve 1 + bitmap/descriptor pairs 4 | 9 | 0 |
| One empty branch prune/root collapse | At most H old tree pages 8 + object 1 + state 1 + reserve 1 + at most H bitmap/descriptor pairs 16 | 27 | 0 |
| Refill reserve in a bounded batch | At most two bitmaps 2 + two descriptors 2 + reserve 1 + state 1 | 6 | 0 live-tree pages |

The largest count is 42, leaving 22 journal slots as unused margin. It is not
permission to combine operation classes or add features without a new bound.
Object/root/parent timestamps and archive-bit changes must use the counted
object pages; an extra index or notification persisted on disk requires recounting.
In-memory notification delivery itself is outside these on-disk credits.

A data chunk changes at most one existing extent mapping: update its first
piece and insert at most two additional pieces (for example splitting an
unwritten range around newly initialised data). Inserting a new extent into a
hole needs one insertion. Do not coalesce neighbouring extents in that transaction.
Restrict allocation/freeing to at most two bitmap windows and one contiguous
physical extent; cap payload I/O at 256 KiB per chunk as a separate scheduling
limit. Inline-to-tree conversion of at most seven extents builds one leaf
directly, not seven arbitrary-height insertions. Larger writes commit chunks and
return the correct partial byte count on failure; they are not whole-write atomic.

Shrink first changes size and enqueues the existing object for work without
detaching an unowned tree. Remaining extents stay allocated and owned by that
object, including the hidden tail. Reclaim from the highest logical extent,
adjusting its end/count or deleting it, at most two bitmap windows per chunk.
Reads obey the reduced size. Growth on that object waits for pending shrink work
to finish; never expose the hidden old tail as newly grown content. Unlink likewise
keeps the object's roots until reclamation completes. The work queue is a singly
linked list of existing object IDs, headed by the state page; flags identify
unlink versus shrink and offset 40 is the next pointer. A live directory cannot
enter shrink work, and an unlinked directory is empty, so this union does not
destroy a needed parent link. Queue each object once; work only on the head.
Unlink during pending shrink changes its work flags without enqueuing it twice.
Delete the object record only after all roots/payload have been released.

Before entering the journal transaction, classify the operation, check height,
reserve the worst-case pool pages, identify data bitmap windows and allocate all
RAM resources. Dry-run tree edits may use journal staging without publishing,
but must finish admission before in-place data writes. A 65th target or excess
new-page request is an internal accounting error: abort uncommitted staging,
never partially commit an atomic operation. Capacity failure and insufficient
reserve return explicit errors. Ordinary resource limits cannot strand an
already committed transaction because replay uses the fixed mount arena.

## Failure model and transaction protocol

The crash model allows writes between successful persistence barriers to be lost,
reordered or torn. A completed barrier must make all preceding writes durable.
Completed writes must not damage unrelated ranges. A journal control record must
validate as a complete old/new record or fail validation after a torn write;
undetected corruption is outside the guarantee. A complete new record may be
durable even if the caller did not observe completion. Media read errors and invalid
required recovery records cause an error, not a guess. Physical-sector
read-modify-write effects and real controller flush/discard semantics need
separate hardware qualification.

Only one transaction may be prepared, committed or checkpointed at a time. V1
acknowledges successful metadata mutation after retirement. No cross-operation
write-back batching is assumed. A read of a dirty target uses its staged image;
other reads use committed storage. Mutation-private allocation and index state
must not leak on abort. Caller-visible reads cannot run concurrently with mutation.
Home writes and replay invalidate or update matching read-cache entries. Aborting
a prepared transaction drops its overlay before any committed-state read. Cached
checksums and borrowed page views cannot outlive the corresponding page version.

Each 512 KiB journal slot reserves this geometry:

| Slot-relative region | Size |
| --- | --- |
| Header | P |
| Descriptor pages | 4P |
| Fixed image slots | 64P |
| Commit record | P |
| Remaining bytes | Reserved |

The total is 70P: 70 KiB at P=1024 or 280 KiB at P=4096. A descriptor can be 32
bytes: target u64, owner u64, image slot u32, image CRC u32, type u16, flags u16,
reserved u32. Four descriptor pages accommodate 64 descriptors even at P=1024.
The header binds UUID, transaction ID, predecessor ID and descriptor count.
The commit binds those identities and a checksum over the final descriptors and
their image checksums. Exact control-record byte codecs remain a freeze gate.

### Preparation and commit

1. Recover first. Both checkpoint copies must describe the same retired
   transaction C before new mutations start. Repair a missing/older checkpoint
   copy and persist it; unresolved write failure blocks further mutation.
2. Select the alternating slot for C+1. Invalidate its old commit record and
   persist that invalidation before overwriting any staging content. No slot
   needed by an unretired transaction may be selected.
3. Admit the operation against transaction/page/space credits. Maintain at most
   64 target-to-image mappings. A target has one fixed image slot. Repeated edits
   overwrite that uncommitted image slot instead of appending unlimited images.
   Eviction writes the latest staged image before releasing its RAM page.
4. Write newly allocated external data and final staged metadata. No uncommitted
   home metadata writes are allowed. Persist data, header, descriptors and all
   staged images before publishing the commit record.
5. Write the complete commit record and persist it. After this point the logical
   metadata operation is committed, even if its caller has not received success.
6. Apply each image to its home address, then persist all home writes. Validate
   the target list first: no duplicate/overlapping page targets, no boot/anchor/
   journal/checkpoint/immutable-charset targets, and no out-of-volume or misaligned
   targets. Validate this list before commit as well as during replay.
7. Write checkpoint A for C+1 and persist it; write checkpoint B for C+1 and
   persist it. Each contains UUID, retired transaction ID, version and checksum.
   Checkpointing cannot begin before home persistence succeeds.
8. Retire the transaction. Only now may freed space be reused or discarded, the
   next operation start, or this slot eventually become reusable. Return success.

This intentionally uses more barriers than an optimised pipeline. Removing a
barrier, allowing two active transactions, or acknowledging earlier changes the
proof and is a separate design revision. Empty transactions need not consume a
slot. Rewriting a fixed staging image is safe only before a valid new commit;
committed images are immutable until retirement.

### Mount and restart

Read anchors and checkpoints before interpreting mutable trees. Choose the
highest valid retired ID C among the checkpoint copies. A higher checkpoint is
evidence that its home writes completed; a lower copy is not an older filesystem
snapshot. If both copies are invalid, ordinary mount fails and salvage is separate.

Validate journal identities and control records. Records at or below C are stale
and must not be replayed. There can be at most one committed successor C+1 with
predecessor C. Gaps, conflicting successors or a corrupt committed recovery image
fail mounting. An invalid/uncommitted successor is ignored under the stated crash
model because it could not authorise home writes. A later loss of a once-valid
commit record is corruption outside that inference, not an excuse to claim repair.

Validate the entire successor's descriptor set and all image checksums before
the first home write. This takes two streaming passes, not 64 resident pages.
Replay all images, persist them, then write/persist both checkpoints. Interruption
repeats the replay. Refresh both checkpoint copies to C before reuse even when no
successor exists. Stale checkpoint fallback across later transactions is therefore
not part of normal recovery. If a slot was reused contrary to these rules,
recovery must report corruption rather than reconstructing an imagined history.

The recovery argument is inductive. At retired C, both checkpoint copies name C
and all C home writes have persisted. Before publishing C+1, no C+1 home metadata
can change. After publication, the complete C+1 redo images remain available while
home writes may be mixed. Before either checkpoint can name C+1, all its home
writes have persisted. A torn first checkpoint leaves the second at C and redo
available; a torn second leaves the first at C+1, which already certifies durable
home writes. Both copies advance before the next transaction can reuse a slot.
These implications make choosing the highest valid checkpoint safe under the
stated crash model. They do not authorise fallback after later undetected media
corruption or missing committed images.

A mount requiring replay cannot silently serve ordinary read-only access from
inconsistent home pages. Either refuse pending recovery or implement a separately
specified read-only journal overlay. Forensic tools may inspect the image without
claiming it is a mounted consistent filesystem.

### Errors and long operations

Before commit, failure can abandon staged metadata. Newly written data in still
free space is unreachable; private caches/reservations are reset. External
in-place data writes may already have changed visible file contents.

An error while publishing the commit has an uncertain outcome. Stop normal
mutation and TRIM, recover from physical storage and reconcile the caller's
operation. Do not blindly repeat create, rename or ID allocation. After commit,
home/checkpoint failure retains the journal and enters recovery-required state.
Discard failure never rolls back a committed filesystem operation.

A large unlink detaches the directory entry and queues the existing object as
specified in the credit proof; no new orphan-tree record is allocated. The object
owns its extents, name-independent attributes and remaining tree pages until
head-only chunk cleanup finishes. The legacy handler rejects deletion when its
AmigaDOS lock/open rules require that result; v1 does not introduce POSIX unlink
semantics. Shrink work is resumable through the object size/flags/retained roots,
not a RAM list of extents. Recovery completes journal replay before resuming work.

Directory hard links are unsupported. Moving a directory must reject ancestry
cycles using a bounded traversal with continuation, without building an in-memory
list of every ancestor. Packet-level lock and replacement semantics remain part
of the compatibility contract.

Atomic rename is never split to fit the journal. Large file-data operations and
reclamation use only the bounded classes in the credit table. Failure to honour
the class restrictions is a writer bug, not permission to commit a prefix of
an atomic namespace operation. Existing full-width file metadata remains valid
even when a legacy API cannot report its values: refuse such a query/read/seek
with an explicit overflow or unsupported-range result rather than truncating.
The new handler's exact DOS error-number mapping belongs to its packet contract.

Normal read-only mounting requires no pending replay; otherwise report recovery
required without writing. A future forensic journal overlay is separate. On
media removal or generation change invalidate caches, paths, aliases, contexts,
discard capability and staged reservations. Do not apply old-medium recovery or
retry writes against a replacement medium merely because its geometry matches.

The mounted handler answers the public WaitChar, Timer and DiskChange packets
with DOS false and error 209, matching CopperFFS and CopperSFS. When media is
unavailable it returns error 226, following the CopperSFS inhibited-media rule.
Record-lock deadlines and media-change processing use their own device events.
See the [packet compatibility measurements](cpx4-native-progress.md#waitchar-timer-and-diskchange).

## TRIM and deallocation

The core exports wide partition-relative ranges. The platform adapter translates
them to verified device commands and enforces device limits; no speculative ATA
or SCSI capability is inferred from an SSD label. Unsupported discard does not
prevent a normal mount. CPX4 uses the existing CopperTrim v1 private DOS packet
for capability queries, dry runs and explicit discard. Its packet contract and
implementation limits are recorded in [the native progress report](cpx4-native-progress.md#private-trim-packet).

A batch begins only with a retired transaction and valid allocation state.
Validate descriptors/bitmap pages and fixed-region exclusions, coalesce free
units, align inward in device coordinates and split at command limits. Avoid
partial physical-sector discard under the selected device contract. Optimal
alignment hints must not be mistaken for permission to include allocated units.

Keep single-owner exclusion from final allocation validation through device
completion. Returning to the event loop while an asynchronous discard is still
outstanding does not release this requirement. Late completion after a timeout
must not race with reallocation; fail/stop the device or keep the range unavailable
until quiescence is established. A timeout is not proof that the request stopped.

Release mount ownership between bounded batches. Saved cursors and ranges become
hints and must be revalidated after foreground work or media-generation changes.
An overflowing fixed hint queue may drop hints; manual scanning remains complete.
No persistent trimmed-bit map is required. Losing progress only repeats work.
Budget bitmap scanning as well as command size: finding a free run must not turn
one maintenance turn into an unbounded volume scan. One synchronous command can
still have unbounded device latency; CPU budgeting cannot promise a response-time
bound for arbitrary hardware.

Never assume discarded reads are zero. Newly exposed data is initialised or
represented by a hole/unwritten extent. On partial/error completion, invalidate
any corresponding cache entries and suspend automatic maintenance until device
state is known. Even on success, a queued stale write must never later resurrect
data into space that has been freed, trimmed and reallocated.

The allocator and trim scanner trust the same ownership invariants. Checksums
cannot prove that no live file references a bitmap-free unit. Suspected corruption
disables discard, and the checker must compare all references before repair.
Snapshots/reflinks are outside v1: adding them changes the meaning of free space.

The existing [manual TRIM](manual-trim.md) and
[experimental automatic TRIM](automatic-trim.md) document useful ownership and
transport boundaries. They are references, not evidence that this new format or
its 64-bit deallocation API has been qualified.

## Wide geometry on the 68000

Represent wide values as high/low u32 scalars in hot native paths. Comparison
examines high then low; local increments use carry; power-of-two conversions use
shifts and masks. Generic division and modulo are not needed for group or bitmap
addressing. A tree comparison still costs two-word comparisons; it is not free.

Most loops use a u32 group offset, remaining extent count and bounded transfer
length. Cache the current extent/device address for sequential I/O. Convert the
wide unit base to bytes and add the partition offset at the device boundary with
overflow checks. Do not repeatedly translate every byte or unit in an inner loop.
The format must not force a full-width divide when computing statistics or seeking.

Legacy DOS sizes/positions must reject or explicitly report unsupported ranges;
never truncate the high half. Wide file APIs and wide device access are separate
capabilities.

**TD64 is a first-class device interface for this filesystem.** The native
adapter must support and qualify TD64 reads and writes independently of NSD;
TD64-only devices must not depend on a successful NSD query. Initialise the complete offset and completion
fields for every request, including split transfers and retries; preserve the
high half across partition-base addition and the 4 GiB boundary. The existing
[native transfer adapter](../src/CopperFS.Amiga/NativeAmigaFileSystem.cs) is a
repository reference for TD64 request handling, not qualification of CPX3.

**NSD is not supported.** Reject partitions extending past 4 GiB when TD64
cannot be confirmed by a read-only probe before mounting. Negotiate device capabilities
in the platform adapter. A request outside the supported address range must fail
without truncation or a narrow-command retry; ordinary 32-bit commands remain
usable for ranges they can represent. Neither interface alone establishes the
persistence/ordering contract required by the journal; qualify that separately.

Generated code needs inspection: a C# ulong expression or variable shift may
still call a compiler helper. Carry/borrow, signedness, comparisons around 2^32,
odd memory addresses, byte swapping, register spills and structure copies need
native probes. The existing [geometry helper](../src/CopperSFS.Core/AmigaSfsGeometryMath.cs)
illustrates why duplicate wide division is a real concern, not a format feature
to copy. No assumption is made that a modern host benchmark predicts 68000 cost.

The prototype now uses source-linked high/low u32 kernels for checked shifts,
round-up, carry/borrow, comparison and partition/device ranges. Power-of-two
geometry uses shifts and masks. Arbitrary TRIM alignment uses a fixed 64-round
remainder routine with a power-of-two fast path. The host retains ulong codec
and API values; this is not a claim that every expression in the host core has
been lowered to native pairs. The exact kernels execute in the MC68000 probe;
see [68000 readiness](cpx3-68000-readiness.md) for scope and measurements.

## Memory budget and hidden costs

The following is a working budget, not measured native consumption. Memory is
supplied by the caller; an operation cannot request a larger heap on demand.
Lifecycle analysis must show which buffers can safely alias. Borrowed cache
views may not survive I/O or eviction.

| Implemented prototype scratch | Bytes |
| --- | --- |
| Six page buffers: read, edit, split, second metadata, data partial-write, journal/control I/O | 6P |
| 64 transaction index entries, 32 bytes each | 2048 |
| Eight-entry scalar insertion path | 128 |
| Reserved gap before name buffers | 1920 |
| Two 768-byte UTF-8 component buffers | 1536 |
| Reserved gap before record arena | 512 |
| LIFO records and internal continuation state | 4096 |
| Reserved tail | 2048 |
| Non-page subtotal | 12288 |

This gives **18 KiB at P=1024 or 36 KiB at P=4096 for the provisional core
arena**. This supersedes the earlier illustrative non-page budget: mount scalars,
public continuation storage and future charset tables are additional, not silently
overlaid with live records. The current Latin-1 profile uses arithmetic conversion
and folding, with no per-mount decode/inverse table. Two optional DMA
bounce pages raise those to 20/44 KiB. A provisional 4-8 KiB task stack would
raise them to 24-28/48-52 KiB before open handles, OS objects, code and caches.
Neither six pages nor the stack allowance is proven sufficient. Recovery reuses
the transaction/page arena; it must not allocate a second 64-page replay buffer.

Additional costs that must be measured or capped:

- Open handles, locks, notifications and enumeration state scale with callers.
  Specify per-entry bytes and a caller-supplied limit; resource exhaustion must
  return an ordinary error. Do not hold one tree path per open file by default.
- The task stack includes compiler-generated frames, generic helpers, exception
  support if any and OS calls. Avoid recursive tree walks and page-sized locals.
  Measure native stack high-water marks, not only source-level local variables.
- DMA address masks, memory type and alignment may require Chip RAM buffers plus
  allocation padding. Their memory cannot also hold live staging data during I/O.
  CPX4 reserves one bounce buffer at startup and reuses it across serialized
  requests. Its fixed arena occupies 59,392 bytes and any separate DMA allocation
  occupies at most 8,192 bytes including padding. See [CPX4 DMA-constrained
  allocation](cpx4-native-progress.md#dma-constrained-allocation) for the native
  policy and 512 KiB measurements.
- Charset reverse lookup can avoid the 2 KiB inverse by bounded table searches,
  at a CPU cost. A full Unicode fold/normalisation library is not in this budget.
  Translating full paths at once would exceed the two component buffers.
- Directory separators are fixed keys, so splits do not retain full names per
  level. Iterative traversal stores scalars, then rereads pages. Pinning every
  level would still turn scalar path state into many page images.
- Full before-images/savepoints, a second dirty cache, whole-image checksum
  copies or a sort buffer for all extents would defeat the staging budget.
  V1 has no nested transaction or cross-operation savepoint requirement.
- CRC tables may be shared read-only with code, but code size, relocations and
  hunk loader memory still count on a real machine. Nibble/bitwise CRC variants
  trade memory for CPU; benchmark without changing the checksum polynomial.
- Timers, message ports, IORequests, DMA command/sense storage and device handles
  are OS integration costs beyond the portable arena. Avoid counting the same
  command buffer twice or omitting the asynchronous request object.
- Optional read caches/readahead are separate, explicitly bounded enhancements.
  No recovery or mount-success requirement may depend on their availability.

Small metadata pages lower scratch requirements but increase tree depth and
metadata I/O. Streaming journal staging saves RAM but adds reads and repeated
writes for evicted dirty pages. Benchmark both costs; bounded memory alone does
not establish competitive throughput, latency or SSD write amplification.

### Page-buffer schedule

Reserve six P-byte slots explicitly: B0 tree/current read, B1 editable output,
B2 split output, B3 other metadata/reserve read, B4 data partial-write or external
name read, B5 journal/control I/O. Split a virtual full-node-plus-one-entry stream
from B0 into B1/B2; that extra fixed record uses at most 256 bytes in the fixed
record arena. Stage both outputs before visiting the
parent. Repeated dirty reads come from their journal image, not a retained ancestor.
Name lookup uses B0 for the directory leaf and B4 for the optional name page.
Reserve/refill edits use B3 and are staged before the slot is reused. Bitmap and
descriptor edits are sequential. Recovery uses B0/B5 and reuses the same index.

Subroutines may borrow these named slots only for their declared phase. No
recursive call can require another page buffer. DMA bounce storage is additional
when a slot is unsuitable for the driver. This is a concrete scheduling contract;
native guard-byte and stack-watermark tests still must verify it. Per-open and
enumeration arenas remain caller-sized and outside the core estimate.

## Decisions and remaining qualification

The following design choices replace the former open-ended blockers:

| Former blocker | Decision |
| --- | --- |
| Transaction size | 64 images; current operation classes need at most 42; no implicit operation composition |
| Directory depth with long names | Fixed hash/ID keys, 128-byte leaves, separate long-name page, at most eight levels |
| Metadata reserve | Persisted 64-entry pool, 32-page target, 12-page protected margin, nonallocating unlink/shrink/reclaim |
| Allocation recursion | No allocation-summary tree; direct group/bitmap scanning with continuation |
| Orphan storage | Work flags and next pointer in the existing object, head-only queue; no orphan tree |
| UTF-8 and Amiga compatibility | Explicit DOS byte-to-UTF-8 conversion, Latin-1 default, immutable tables, no new Unicode packet in v1 |
| Timestamps | Packed DateStamp fields preserving 50 Hz ticks and local-time semantics |
| Allocation statistics | File payload units separate from volume metadata/reserve usage |
| Read-only recovery | Refuse ordinary read-only mount when replay is needed |
| Wide device I/O | TD64; NSD unsupported |
| Buffer lifetime | Six named page slots plus 4 KiB record arena with LIFO leases; no per-level pinned pages |
| Long scans | Caller-owned allocation/name/ancestry cursors, at most four metadata reads per step |
| Cleanup scheduling | Optional deferred queue; at most one retired transaction per ReclaimStep |
| Blocking scheduling | Cooperate callback at most four device requests apart, retaining mount ownership |
| Wide arithmetic | Tested u32 high/low kernels; shifts/masks for geometry, bounded remainder for discard alignment |

The [CPX3 byte specification](../prototypes/CopperFS.Prototype/FORMAT.md) now fixes
experimental control fields and type/flag values and implements the journal,
reserve and bounded tree algorithms. CPX3 is deliberately incompatible with
CPX1/CPX2; it is not a production format assignment. Its default charset is the
normative built-in Latin-1 profile, and its 128-bit identity field currently has
a zero high half and caller-supplied nonzero low half. It implements regular
files/directories, links, rename/replacement, sparse/unwritten storage and queued
shrink/unlink; attributes, timestamps/protection mutation and symlinks remain
format/API work. Reserve refill runs when the operation's worst-case admission
threshold is not met, toward the 32-page target, and may complete before a later
logical-operation refusal.

Host qualification includes actual split cascades and height-nine refusal,
cross-directory replacement, reserve-exhaustion reclamation, forced CRC-name
collisions and journal interruption/slot reuse. Production readiness still needs
DOS packet/open-lock/error rules, alternative charset tables and whole-handler
emitted-code memory/stack measurements. The prototype now supplies resumable
allocation/name/ancestry scans, deferred cleanup, indexed predecessor/tail access
and tested arithmetic kernels. Its blocking compatibility APIs retain ownership
across cooperative callbacks; an Amiga packet dispatcher and asynchronous device
completion remain native integration work. Only the arithmetic probe has a native
stack measurement. Do not label managed allocation checks or the 42-image journal
witness as native crash-safety coverage. See [the execution contracts and test
results](cpx3-68000-readiness.md).

Deferred features are snapshots, shared extents, compression, deduplication,
tail packing, online resize and atomic external-file overwrites. Each would
change ownership, recovery or scratch requirements and needs a new review.

## Verification plan and coverage

### Design calculations checked on 13 November

An independent arithmetic check of this revision verified the following:

- Page capacities for P=1024/2048/4096, 70P journal fit, descriptor capacity,
  reserve-control and 768-byte name-page fit, and the fixed control-area bounds.
- All 510 full/nonfull path patterns across heights one through eight obey
  the conservative 2h+1 target and h+1 new-page insertion bounds. Paths needing
  height nine are rejected by admission; counting them only overestimates cost.
  This is a split-propagation counting model, not a filesystem implementation test.
- The operation table has a 42-image maximum, a 20-new-live-page maximum and a
  12-page remainder from a 32-page reserve; rename's ten-page bound fits that
  margin. The itemised non-page memory subtotal is 11,264 bytes.
- All 256 Latin-1 byte values round-trip through UTF-8 and the selected fold
  table is idempotent. This checks conversion independently of forbidden-name
  syntax. Worst-case legacy expansion is 510 bytes for Latin-1 or 765 for the
  permitted BMP tables. Both fit the 768-byte stored limit.
- CRC32C gives E3069283 for `123456789` and zero for empty input. Local document
  links resolve and there is no trailing whitespace.
- A small abstract recovery check covered 56 two-home-page states across four
  transaction generations, including partial home application and old/new/invalid
  checkpoint records. Each recovered the expected committed generation. It is a
  control-order sanity check, not exhaustive sector-persistence or slot-I/O testing.

These calculations establish consistency of the stated design bounds. They do
not establish crash safety, compatibility of every DOS packet, or native memory
consumption. The required implementation tests below must enforce the same
operation restrictions rather than merely asserting a constant of 42.

### Host model and semantic tests

Use synthetic generated images and an independent reachability checker. Cut
execution before/after every write and barrier and enumerate allowed persistence
subsets, including torn pages. The crash matrix must cover:

- Fresh format/anchor selection; conflicting anchors; one or both checkpoint
  copies invalid; crash after either checkpoint barrier; two full slot-reuse
  cycles and stale valid commits from earlier transactions.
- Repeated edits/evictions of one target, all 64 targets, attempted 65th target,
  descriptor/image corruption, duplicate/overlapping targets and bad owner/type.
- Commit success followed by a reported error, commit failure with uncertain
  persistence, failed home write, replay interrupted repeatedly, failed repair
  of a checkpoint copy and read-only mounting with pending replay.
- Rename/replacement and every tree split boundary; full disks; worst-case
  names; forced identical name hashes; height-eight refusal; small directories
  growing; pool exhaustion and nonallocating cleanup; queued shrink changing to
  unlink; legacy open-file deletion rejection; inline conversion and
  sparse/truncate/extend zeroing.
- Wide offsets around 2^32 units/bytes, partition-base carry, final partial
  groups, backup-anchor exclusion, maximum representable byte ranges and
  rejection instead of overflow/wrap.
- Append into a retained unwritten allocation unit after shrink, including
  sparse gaps, inline extent overflow and existing extent trees. Verify zero
  initialization before publishing the written mapping and durable byte counts.
- Free/commit/trim/reallocate ordering; trim interrupted after any descriptor;
  nonzero discarded reads; timed-out I/O completing late; queued cache writes;
  hints invalidated by foreground work and media changes.
- Charset byte round trips, invalid UTF-8, expanded-length overflow, case-fold
  equivalence, aliases, hard links, and enumeration resumed after mutation.

Validate invariants, not just successful remount: no reachable unit is free or
discarded; no unit has incompatible owners; references and file lengths agree;
reserved units remain allocated; acknowledged metadata persists under the stated
model; any retained orphan owns all resources awaiting reclamation. External
overwrite contents follow the weaker documented data guarantee.

### Native and full ROM tests

Build the affected standalone handler once native source exists. Run compiled
68000 arithmetic, buffer-guard, stack and replay tests, then CopperStart native
replay tests for linked integration. Measure Fast RAM/Chip RAM placement and
compare 68000 and 68020 separately. Full-ROM boot and real-controller media tests
are separate qualifications, not inferred from managed/native model success.
Include TD64-only devices, rejection of unsupported wide partitions, partition
offsets and transfers crossing 4 GiB, short/error completion, and high-offset
reinitialisation on split/retried requests. NSD is not supported.
The host prototype's IBlockDevice tests do not qualify either Amiga device ABI.

The current executable evidence is recorded in
[CPX3 prototype qualification](cpx3-prototype-qualification.md). It distinguishes
solution host tests, final prototype tests, compiled-native/CopperStart replay
and full-ROM/physical-media coverage. A prior documentation-only run was aborted
after 15 minutes and was not a full-suite pass; its partial counts must not be
used as current implementation evidence. Tests generate synthetic media and use
an independent ownership decoder. No ROM or external media is imported.

## References and provenance

The proposed format and review originate in this design discussion. No existing
filesystem code, disk image, ROM or test fixture is imported. Existing repository
behaviour remains governed by its own documentation and tests.

- [Repository overview](../README.md) and [integration boundaries](copperfs-integration.md).
- [Existing transaction batching](transaction-batching.md), for contrast with
  existing undo-journal guarantees; it does not specify this new redo protocol.
- [Manual TRIM](manual-trim.md) and [automatic TRIM](automatic-trim.md), for
  current mount ownership, free-space validation and transport constraints.
- [Linux journalling API](https://www.kernel.org/doc/html/latest/filesystems/journalling.html),
  for the general dependency between transaction commitment and safe block reuse;
  this draft does not adopt JBD2's on-disk format or claim its proof applies here.
- [Unicode case mapping](https://unicode.org/faq/casemap_charprop.html) and
  [normalisation](https://unicode.org/faq/normalization.html), for the distinction
  between charset conversion, comparison and canonical equivalence.
- [Amiga NSD device specification](https://wiki.amigaos.net/wiki/NSD_Device_Specifics),
  for the deprecated legacy compatibility path only; TD64 policy and native
  qualification requirements are specified above.
- [Latin-1 mapping](https://unicode.org/Public/MAPPINGS/ISO8859/8859-1.TXT),
  [Examine](https://developer.amigaos3.net/autodocs/dos.library/Examine.html) and
  [DateStamp](https://developer.amigaos3.net/autodocs/dos.library/DateStamp.html),
  for the default charset correspondence, FileInfoBlock widths and timestamp units.
