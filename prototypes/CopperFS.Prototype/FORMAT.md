# CPX3 on-disk research format

All integers are unsigned big-endian unless stated otherwise. Byte offsets in
tables are relative to the structure named by the table. Addresses are partition-
relative allocation units, never native pointers. This document describes the
current host implementation, not a registered Amiga DosType or frozen format.

## Geometry and placement

A is 512, 1024, 2048 or 4096 bytes. P=max(1024,A); Q=P/A. The logical device sector
is a power of two from 512 through A. Volume capacity is a whole number of A and
fits u64 bytes; minimum capacity is 4 MiB. A metadata address is Q-aligned and
contains a complete P-byte page. The group size is 65,536 AUs.

| Byte offset | Region |
| ---: | --- |
| 0 through 65,535 | Reserved |
| 65,536 | Immutable primary anchor |
| 65,536 + P, +2P | Checkpoint A, B |
| 65,536 + 3P | Mutable state |
| 65,536 + 4P through +7P | Charset reservation; profile 1 uses the first page |
| 65,536 + 8P | Reserve control |
| 131,072 | Group-zero descriptor, then 16 bitmap pages |
| 1,048,576 | Journal slot A, 512 KiB |
| 1,572,864 | Journal slot B, 512 KiB |
| 2,097,152 | First allocatable region |
| floor(volumeBytes/P)*P - P | Backup anchor |

All AUs below 2 MiB, the backup anchor and any trailing partial page are
unavailable. Other groups begin with their descriptor and 16 P-byte bitmap
pages. A final group too short for those 17 pages plus backup-page allowance is
entirely unavailable. Group zero's descriptor is fixed at 128 KiB, not at AU zero.
Formatting creates root object ID 1 in one object leaf and allocates 32 reserve
pages. Root directory ID 1 is implicit in CPX3.

## Common page header

| Offset | Size | Value |
| ---: | ---: | --- |
| 0 | 4 | 0x43505833 (CPX3) |
| 4 | 2 | Page type |
| 6 | 2 | Structure version 3 |
| 8 | 4 | CRC32C over all P bytes, these four bytes treated as zero |
| 12 | 4 | Flags; bitmap pages store their 0..15 map index here |
| 16 | 8 | Home AU address |
| 24 | 8 | Owner ID; object trees/system pages use zero, group/bitmap pages use group base |
| 32 | 8 | Last modifying transaction |
| 40 | 8 | Zero; no sibling links |
| 48 | 2 | Entry count |
| 50 | 2 | Tree level, leaves zero |
| 52 | 2 | Used payload bytes after the 64-byte header |
| 54 | 10 | Zero |

CRC32C uses reflected polynomial 0x82F63B78, initial/final XOR 0xFFFFFFFF;
123456789 checks to E3069283. Payload length includes fixed-format fields and
their reserved padding: anchor 64, checkpoint 24, state 48, reserve 520, charset
4, group 80, bitmap 512, journal header/commit 40. Arrays use count times record
width. Name payload length is four plus UTF-8 byte length. Unused bytes are zero.

Types: 1 anchor, 2 checkpoint, 3 state, 4 reserve, 5 charset, 6 group, 7 bitmap,
8 journal header, 9 descriptor page, 10 commit, 11 object leaf, 12 object branch,
13 directory leaf, 14 directory branch, 15 extent leaf, 16 extent branch,
17 name. Values 18 attribute and 19 symlink are reserved; no public writer emits
them. A journal image's header identifies its home address, not its journal slot.

## Control payloads

Anchor, checkpoint, journal header and commit carry a 128-bit identity at 64.
The CPX3 prototype fixes its upper u64 to zero and uses the caller's nonzero u64
volume ID at 72. It is an identity binding, not a cryptographic authenticator.

Anchor: A u32 at 80; P u32 at 84; AU count u64 at 88; required feature bits u64=1
at 96; device-sector bytes u32 at 104; charset profile u32=1 at 108; reserve
address u64 at 112; state address u64 at 120. Journal/checkpoint placement is
derived from geometry. Both valid anchors must agree. Old CPX versions and
unknown required features are refused. Destructive format invalidates old
anchors and flushes before rebuilding metadata and publishing the new anchors.

Checkpoint: retired transaction C u64 at 80, equal to header transaction.
There are no mutable object roots in the anchor or checkpoint.

State: next object ID u64 at 64; next entry ID at 72; object-tree root at 80;
work-queue head object ID at 88; global free AU count at 96; live directory-entry
count at 104. IDs are not reused after commit.

Reserve: count u16 at 64, six zero bytes, then 64 AU addresses at 72. Unused
addresses are zero. Each entry owns one Q-aligned P-byte page whose bitmap units
are allocated. Capacity 64, refill target 32, protected growth margin 12.
Assignment does not update the bitmap. A refill may touch at most two bitmap
windows. Return goes to the pool if it has space, otherwise to the ordinary
free bitmap; neither is reusable before retirement.

Charset: profile u32=1 at 64. Latin-1 mapping and fold rules are normative built-in
tables in this prototype. Alternative charset-table pages are not implemented.

Group: valid AU count u32 at 64; exact free count u32 at 68; zero at 72..79;
16 exact per-bitmap free counts u32 at 80. Search hints are not persisted yet.

Bitmap: 512 bytes at 64 describe 4096 AUs. The high bit of the first byte is the
first AU; one means allocated/unavailable. Map i covers groupBase+i*4096.
Units outside the volume are one. All higher unused payload bytes are zero.

## Objects and trees

Objects have the 256-byte layout in the design: ID 0, byte size/directory count 8,
allocated payload AU count 16, extent root 24, directory root 32, parent/queue-next
40, attribute-page address 48, two eight-byte Amiga timestamps 56 and 64,
protection u32 at 72, links u32 at 76, object generation u32 at 80, type byte 84,
storage byte 85, flags u16 at 86, inline length u16 at 88, inline extent count u16
at 90, reserved u32 at 92, 160-byte payload at 96.

Implemented types: regular=1, directory=2. Storage: inline=0, inline extents=1,
extent tree=2. Inline extent count is zero in tree mode, not a truncated total.
Object generation starts at one; timestamps/protection remain zero without an
attribute mutation API. Work flags: zero normal, 1 detached unlink, 2 pending
shrink. A queued object's retained mappings continue to own all unreclaimed units.
Only the queue head is reclaimed. Regular-file size defines shrink's retained
prefix; cleanup removes highest extents in bitmap-window chunks.

Extent records are 24 bytes: logical AU u64 at 0, physical AU u64 at 8, count u32
at 16, flags u32 at 20. Count is 1..65,536; physical runs never cross groups.
Flags zero means initialized, one means unwritten. Holes and unwritten ranges
read as zero. New writes initialize full newly exposed units, including partial
first/last units. No coalescing is hidden inside a foreground operation.

Trees have up to eight levels. Leaf arrays start at 64, with object/directory/
extent widths 256/128/24. Branch records contain a lower-fence key and u64 child.
Object/extent keys are 8 bytes (branch width 16); directory keys are 16 bytes
(branch width 24). Keys are strictly ordered. Fences describe intervals, not
the current first live record. Insertion splits by count using two output pages.
Deletion does not merge or redistribute; separate pruning removes empty paths.
The scalar path is eight 16-byte records. A ninth-level insertion is refused.

## Names and directory records

Directory key: canonical UTF-8 CRC32C u32 at 0, zero u32 at 4, stable entry ID u64
at 8. Directory leaf record: key 0..15, target object u64 at 16, external name
page u64 at 24, UTF-8 length u16 at 32, flags u16=0 at 34, legacy byte length
u16 at 36 (FFFF if unrepresentable), ten zero bytes, 80 inline name bytes at 48.

Names longer than 80 bytes occupy one name page owned by entry ID: length u16 at
64, zero u16 at 66, UTF-8 at 68. Limit 768 bytes. Legacy input is 1..255 Latin-1
bytes, converted without UTF-8 guessing; its worst expansion is 510 bytes.
Explicit UTF-8 import rejects invalid encodings, NUL, slash, colon, dot/dot-dot
components and the reserved alias shape. Case folding changes ASCII A-Z and
Latin-1 C0-D6/D8-DE by +32; no normalization or multi-character fold. All matching
hash entries are compared by complete folded scalar streams.

Legacy enumeration returns representable names of at most 107 bytes, otherwise
__u plus sixteen hexadecimal entry-ID digits. Exactly that 19-character shape,
case-insensitively, is reserved at creation. Alias resolution searches only the
addressed directory. Each hard link has its own entry ID; rename retains it.

## Journal bytes and ordering

Odd transactions use slot A; even transactions slot B. Each 512 KiB slot reserves:

| Page index in slot | Content |
| ---: | --- |
| 0 | Header |
| 1..4 | Descriptor pages |
| 5..68 | 64 fixed P-byte image slots |
| 69 | Commit |

At maximum P this uses 286,720 bytes. Remaining slot bytes are reserved.
Header/commit identity is at 64; transaction u64 at 80; predecessor u64 at 88;
image count u32 at 96; descriptor digest u32 at 100. The digest is CRC32C over
the ordered count*32 descriptor bytes, without page headers/padding.

A descriptor is 32 bytes: target AU u64, owner u64, image-slot index u32, image
CRC32C u32, page type u16, flags u16=0, reserved u32=0. Descriptor pages hold
floor((P-64)/32) entries from offset 64. All four pages are written, including
empty pages. Image slot i corresponds to descriptor i. Repeated staging overwrites
that same slot; the descriptor always names the final checksum.

Production operation credits currently cap admission at 42 targets, even though
recovery supports 1..64 images. No image is staged to an anchor, checkpoint,
journal, immutable charset or permanently reserved data region. Duplicates and
misaligned/out-of-volume targets are rejected during replay.

Invalidate commit/flush, stage data+images+header+descriptors/flush, commit/flush,
homes/flush, checkpoint A/flush, checkpoint B/flush. Retired C permits only C+1;
stale commits <=C are ignored. Before any replay home write, validate both control
records, identity, generation, descriptor digest, uniqueness, target policy and
every complete image. A second streaming pass applies them. The device must be
quiescent after a failed request; late writes, lying flushes and undetected CRC
collisions are outside this model.

See README.md for API guarantees and qualification limits. This file does not
specify a native packet ABI, handler installation or a migration from CPX1/CPX2.

