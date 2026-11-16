# Experimental checkpoint selection catalogue

Feature 5 reduces small-volume metadata mapping work. It keeps the feature 4
paired 512-byte metadata pages, direct inode identities, 32 reserve entries and
full-length string continuations. Existing format features 1–4 retain their
layout. The fixture formatter explicitly selects this feature; no conversion or
native formatting is implied.

Checkpoints occupy `max(P, 1024)` bytes. Their spacing is independent of metadata
page spacing. Three checkpoints begin one checkpoint span after the anchor.
The immutable charset moves after all three checkpoints. State and reserve
addresses remain virtual views of checkpoint controls. The checkpoint payload
length is 960, including the existing controls at bytes 96–495.

Bytes 496–1023 contain a fixed catalogue: count, `INL1` tag, 64 unsigned 64-bit
entries and eight padding bytes. Each entry packs `(physicalPairIndex << 2) |
selectedCopy | inodeFlag`. The inode flag is 2. Absent entries select ordinary
copy A. Entries with both flags zero are removed. Recovery validates the count,
tag, range, uniqueness and unused bytes under the full checkpoint CRC. Equal
generation checkpoints must agree on all controls and catalogue bytes.

The otherwise impossible mapping-root value `ulong.MaxValue` selects the inline
catalogue. Reads then need no separate map-page I/O. Each commit changes one
dirty pair per continuation turn, taking prior selections from an immutable
caller-owned copy. Metadata still goes to the unselected paired page.

When 64 entries are insufficient, the continuation restores the old catalogue
and seeds the existing radix mapper with the union of old selections and dirty
pairs. At most 64 old entries and 42 dirty images require 106 descriptors in a
private 128-entry workspace. Map-only descriptors preserve their flags and
perform no metadata I/O. Dirty descriptors preserve their old copy and set the
new inode flag from the admitted image. General public map admission remains
limited to 42 ordinary image descriptors. Promotion is one-way; full-map
checkpoints carry an empty catalogue with its tag intact.

The mapper's fixed eight frames and 128 ordering slots fit its unchanged
1024-byte state allocation. Descriptor storage, frame buffers and the saved
catalogue borrow the retirement undo area only after operation rollback is no
longer possible. Root input and template storage are separate caller-owned
regions when the checkpoint exceeds P. No continuation allocates.

Publication retains both barriers: write the new map and metadata, drain, write
the two non-surviving checkpoints, drain, then advance the acknowledged durable
generation. Explicit flush retains the final device drain. A failed mount is
poisoned until quiescence and recovery; recovery follows only an authenticated
checkpoint root, so abandoned successors cannot become reachable on a changed
retry.

Qualification includes both page sizes, capacity promotion, independent full
ownership and payload checks, every sector/barrier power cut with changed retry,
compiled instruction execution with a 4 KiB stack guard and zero allocation
sites, unchanged guest workloads, and the full solution/native replay gates.
Performance acceptance requires both original reference handlers to lose both
workloads. The architecture remains experimental until those conditions pass.
