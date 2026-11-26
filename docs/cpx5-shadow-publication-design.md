# Experimental CPX5 shadow publication

This is a proposed incompatible format, not a CPX4 journal-policy change. The
publication kernel uses magic `CPX5` and page version 5. The experimental branch
now mounts and mutates independently formatted CPX5 fixtures. Its first passing
filesystem benchmark is slower than CPX4 and both references. Existing
CPX4 media, namespaces, handler identities and published packages are unchanged.

## Publication invariant

Each logical metadata page has two physical copies. An authenticated mapping root
explicitly selects the visible copy of every page. A transaction writes the other
copy of every changed page and constructs a new mapping root. Unchanged pages
retain their old selectors. Every selected old page remains intact until publication.

Generation filtering alone is insufficient: an interrupted transaction can leave
unpublished pages tagged G+1. Reusing G+1 with a different write set would expose
those abandoned pages. A regression reproduced this in the first draft. Explicit
root reachability fixes it; restarting after every tested crash and publishing a
different partial write set now preserves unchanged pages.

There are three checkpoint locations. A transaction preserves one authenticated
checkpoint at the current generation and writes the other two locations. Its
ordering is:

1. Complete new data and metadata-copy writes, then persist them with a barrier.
2. Write both new checkpoints, then persist them with a second barrier.
3. Advance the in-memory durable generation and permit a successful reply.

During the first stage, the preserved checkpoint selects the old metadata copies.
During checkpoint publication, a valid new checkpoint selects metadata already
made durable by the first barrier. If both checkpoint writes tear, the untouched
checkpoint still selects the old version. On completion there are two durable
new checkpoints; the next transaction preserves one of those. Slot selection must
follow the authenticated surviving checkpoint rather than generation parity alone.

This removes redo descriptors, journal image writes, home installation and journal
replay from the proposed commit path. It costs two physical metadata copies and
requires a format revision. It does not remove metadata authentication or assume
that a sector write is atomic.

The failure model is interrupted writes, lost/reordered outstanding writes and
torn sectors around device barriers. It does not claim recovery from arbitrary
simultaneous damage to all current checkpoints or both copies of a metadata page.
Existing in-place file-data overwrite limitations remain: metadata snapshot
publication does not make overwritten user data transactional.

## Checkpoint volume controls

The second experimental CPX5 layout advertises anchor feature value 2. Legacy
feature value 1 continues to use the separate paired state/reserve pages.
Feature 2 places these fields in each authenticated checkpoint:

| Offset | Bytes | Meaning |
|---:|---:|---|
| 56 | 2 | State payload length: 48, 128 or 136 |
| 58 | 2 | Reserve capacity, 32 |
| 64 | 32 | Existing volume identity, generation and explicit mapping root |
| 96 | 136 | State payload, unused bytes zero |
| 232 | 264 | Reserve count/padding and up to 32 logical paired-page addresses |

The checkpoint payload length is 432, ending at byte 496. Selected checkpoints
at the same generation must agree on mapping root and controls. The immutable
anchors still identify the explicit layout; no CPX4 sector is reinterpreted.

The engine materializes private typed state/reserve views from the authenticated
checkpoint. Dirty views participate in the existing rollback/admission machinery.
Retirement copies their admitted contents into a pinned root template and omits
them from ordinary metadata descriptors. Template mutation begins only after
retirement has made rollback unavailable; an uncertain completion poisons the
mount and recovery reloads the durable checkpoint. A control-only publication
still performs the data barrier before its two checkpoint writes and final
publication barrier. Explicit flush retains its separate device drain.

The extra template is caller-owned. The engine accounts separately for four or
eight usable clean-cache pages when native startup appends this context to one
Exec allocation. A larger allocation size must not accidentally authorize cache
pages that overlap the appended context.

## Prototype boundary

`CpxShadowCommit<TMemory>` is an allocation-free, caller-owned continuation kernel.
It owns no allocator, namespace, mount implementation or DOS registration. The
caller supplies an authenticated current generation/surviving checkpoint, valid
allocation decisions, a pinned immutable descriptor list, sealed metadata images
and a separate checkpoint workspace. The kernel verifies image identity,
generation and CRC before issuing any write. The integrated engine has an internal
entry for its own freshly sealed, immutable pinned images. The engine admits their
headers/targets/payloads, and the map writer admits unique pairs and buffer ranges;
internal publication avoids repeating that admission and checksum calculation.
The public entry retains full untrusted admission. Admission is bounded to 42 images.
One step authenticates or submits at most one image or checkpoint. I/O completion
tokens remain monotonic across reuse of the same arena; stale, short, cancelled
and failed completions cannot acknowledge a generation.

The kernel commits an opaque mapping-root value under the checkpoint CRC. The
small protocol tests embed a complete two-page selector map in that value. A
general filesystem needs a persistent mapping tree; that integration is pending.
The kernel uses 160 caller-owned state bytes, at most 1,344 descriptor bytes,
one checkpoint workspace of at most 4,096 bytes and the caller's pinned images.
There are no native heap allocations or retained CPU frames. CRC-32C is retained;
the standalone kernel retains a bitwise validation fallback and accepts the
existing initialized CRC/zero tables from its caller. Compiled cases cover both
paths. It is a correctness prototype, not a filesystem performance result.

## Integration work

The filesystem integration must add paired metadata reads, cache the selected copy
with each owned image, quarantine both copies until publication, update allocation
and ownership accounting, and publish the fixed state-page pair through the three
checkpoints. Read-only mounts must select a complete generation without rewriting
media. Caller-buffer replacement, rollback, cancellation, media changes and cache
eviction must preserve or invalidate copy selectors correctly.

The independent formatter/decoder must recognize CPX5 explicitly. CPX4 readers
must continue to reject incompatible media. The existing guest workloads and
original-handler inputs stay fixed; CPX5 requires independent persisted ownership
and payload checks before any benchmark can count. Handler, full-ROM and physical
media coverage must be reported separately from this protocol kernel's tests.

Three host protocol tests currently pass, including every sector/barrier cut across
three publications with five persistence patterns and preservation of acknowledged
generations and crash-then-restart with a different write set. Five compiled MC68000 cases pass: all three checkpoint-survivor slots,
cancellation and short completion. They assert zero managed allocation sites and
per-instruction 4 KiB stack bounds. Full-ROM and full-filesystem CPX5 tests have
not run.

## Persistent selector tree

The mapping geometry uses power-of-two fanout, up to eight levels and paired
nodes in a deterministic reserved region. Leaf bits select ordinary metadata
copy A or B. Branch fields have two bits: implicit/default A, child copy A,
child copy B, or invalid. The authenticated checkpoint contains the selected
physical root node. An implicit subtree needs no disk reads. Generation checks
remain additional validation, rather than the source of visibility decisions.

Map reads authenticate only the selected path. A selected invalid or future node
is rejected; an unselected future copy is ignored. Tests cover leaf boundaries,
owner/CRC/selector corruption and wide partition geometry.

The map writer sorts at most 42 changed-page references and walks their prefix
tree in postorder. It keeps one mutable page per level in caller storage, writes
completed child nodes to their inactive copies, then writes their parents. It
never modifies an active map node. Unchanged selectors come from the active
parent's complete image, so abandoned branches cannot leak into a retry. No
mapping write requires a separate barrier: all map and ordinary metadata writes
are drained by the publication kernel's first barrier. The bounded frame pages
can reuse at most eight retired undo images; the map state itself needs 1,024
caller-owned bytes. Quiescent mapping buffers can be reused for checkpoint work.

The combined tree/publication tests include power loss during map writes, normal
metadata writes and both barriers, followed by a different partial update after
restart. Compiled tests also plant CRC-valid abandoned map/data copies. These
component tests retain CRC-32C and pass under the standalone HUNK/pass profile
with no managed allocation and per-instruction 4 KiB stack checks. There are 21
focused host cases and 17 compiled cases across the current shadow components.

Before full-filesystem integration, the metadata layout must account for paired
allocation, the permanent map region, checkpoint selectors, and variable-length
name/link storage. The geometry supports 512-byte map nodes, but this is not a
claim that the filesystem already supports 512-byte CPX5 metadata pages.
