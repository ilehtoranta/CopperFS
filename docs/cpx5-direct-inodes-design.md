# Proposed direct-inode CPX5 experiment

Feature 3 now has an experimental formatter, independent read-only decoder,
production core integration and focused host tests. Native qualification and
benchmark screening are pending. Feature 1 is the paired-control prototype;
feature 2 embeds volume controls in checkpoints. Neither beats the references.
Both existing paths remain available without reinterpreting their sectors.

The first integrated candidate retains metadata pages of at least 1 KiB. The
512-byte metadata and string continuation design below is still proposed.

## Addressed inodes with unique identities

Use a separate type-23 inode page containing the existing 256-byte object record.
Remove the global object B-tree from this feature. Preserve root object ID one;
its physical pair comes from the checkpoint state. Other object IDs encode a
physical pair slot in their low bits and a monotonically increasing serial in
their high bits. The slot width is the ceiling power-of-two width of the volume's
pair count. Choose the smallest representable ID at or above NextObject whose
low bits match the admitted inode slot, then advance NextObject past it. Reject
overflow before mutation.

The **complete 64-bit ID**, not its physical slot, is the identity. An inode's
owner and record ID must both match the requested full ID. A stale handle or
notification must never resolve to a later occupant of that slot. Test reuse
across epochs explicitly. Maintain reserve order so fresh inode slots normally
increase; avoid advancing the high serial on every allocation merely because a
reserve stack happens to be descending. Document representable-ID exhaustion,
including very wide volumes; do not claim all physical capacity is usable if the
ID scheme can exhaust first under a supported allocation pattern.

This is different from assigning a reusable physical address as the object ID.
DOS handle/notification IDs remain opaque, full-width and unique. Entry IDs keep
their separate existing monotonic counter and alias semantics.

## Authenticated inode catalogue

Extend selector leaves to two bits per pair for this feature: bit zero selects
copy A/B; bit one declares an inode. Branch selectors keep their existing two-bit
meaning. Geometry therefore has half as many leaf entries at a given page size.
Include the leaf-width marker in authenticated map-node headers and derive the
placement independently in the reference decoder.

The catalogue solves two problems. An unknown/stale ID can be rejected before
reading user data as a possible inode. The independent checker can enumerate all
inodes without retaining a global in-memory index or accepting user-data pages
that happen to contain plausible metadata headers. Once a selected pair is
marked as an inode, bad CRC/identity is corruption; a different complete owner ID
is a stale identity, never an alias.

Deleting an inode must clear its catalogue bit in the same publication as the
namespace/reclaim/allocation changes. One option is a private type-24 tombstone
image, admitted only for the released pair; its selector switches normally and
its inode bit is zero. Reusing the pair for ordinary file data must wait for the
durable release as it does today. Buffered metadata reuse can overwrite only the
inactive copy. Dirty-image and clean-cache lookups precede durable-map lookup.

For a fresh feature-3 format, persist the bootstrap root inode's catalogue bit
and its selected map path. Use generation one for these actual map nodes and
checkpoints; the map reader currently rejects generation-zero nodes. Old implicit
normal-A subtrees remain useful for unallocated/non-inode space.

## Small metadata

Allow 512-byte metadata only through the explicit new feature. Ordinary inodes,
directory records and extent records fit. Keep logical namespace strings up to
768 bytes: a name/symlink head stores at most P-68 bytes and an authenticated
type-22 continuation stores the rest. Its physical logical address can occupy
the head's reserved header field at 56. Validate owner, address, total/tail length,
generation and CRC for both pages. Do not read or copy a 768-byte blob past a
512-byte scratch page.

The checkpoint's 496-byte control record already fits. A materialized reserve
view needs only 264 payload bytes at capacity 32. For P=512 use 2048 allocation
bits per bitmap, 32 bitmaps per group and a 144-byte group descriptor; the group
prefix has 33 paired pages. Larger metadata retains its existing bitmap window.
Centralize bitmap shifts/masks/counts. Do not mechanically replace unrelated
4096-byte CRC-table, transfer, allocation-unit or scratch constants.

Native discovery must inspect 512-byte candidates explicitly. Initialization and
remount must retain the selected metadata page size, validate scratch/storage
capacities and initialize checksum geometry accordingly. Legacy CPX4 metadata
remains at least 1 KiB. The operation/name buffers remain large enough for their
logical records even when physical pages shrink.

## Filesystem integration and evidence

ReadObject derives the pair and validates catalogue selection plus full identity.
SaveObject changes only that inode page. Create allocates the inode before
assigning its ID, updates the directory tree and counters, and stages the inode.
Deletion/reclaim releases the inode and clears its catalogue bit. Root ID one
cannot be released. Global object-tree pruning is unnecessary for this feature;
directory/extent-tree maintenance remains. Existing cached object records can
retain a physical inode address with slot one, supporting the bulk read/write
paths without an object-index traversal.

The reference implementation can replace Object TreeFind with direct inode
lookup, Object MarkTree with catalogue enumeration, and Object TreeNext with a
slow constant-space minimum-ID search over authenticated inode flags. Reuse its
existing namespace, extent, payload and ownership checks. All live inode pages
must be accounted for; deleted/tombstoned or raw-data pairs must not masquerade as
live inodes. Check link counts/reclaim membership as required.

Required evidence includes stale-ID/notification reuse, ID overflow, catalogue
clearing and raw-data reuse, both metadata sizes, long names and links, deep
directory/extent trees, namespace rollback/capacity retry, short/failed/stale I/O,
cancellation and every sector/barrier cut across repeated publications with
changed write sets. Use unchanged guest workloads, original handlers and full
persisted checks. Pass host, compiled native, separate full-ROM and integration
gates before promoting a replacement. A proposed layout is not a speed result.
