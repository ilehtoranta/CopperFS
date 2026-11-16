# CPX5 integration plan, 16 November 2026

This is an implementation plan for an incompatible experimental format. The
working prototype now mounts both explicit CPX4 and CPX5 fixtures. The integrated
CPX5 backend retains 1 KiB metadata pages and the existing object tree; compact
inodes and smaller metadata remain planned. The original-handler performance
target is still unmet. Component tests and focused filesystem tests do not replace
the complete handler/full-ROM qualification gates.

## Storage and identity

Retain the existing namespaces, portable/native separation, DOS packet ABI and
monotonic object/entry IDs. Use CPX5 magic/version explicitly. CPX4 remains readable
through its existing path; no CPX4 sector is reinterpreted as the new layout.

The selected checkpoint contains an explicit mapping root. Mapping leaves select
metadata copy A/B; mapping branches select their child copies. Map placement is
derived from capacity using `CpxShadowMapGeometry`, with depth bounded to eight.
Allocation must reserve both physical copies of every metadata page. Map nodes
occupy a permanent region beginning at 512 KiB; allocator and independent ownership accounting must
exclude that region. Large permanent prefixes must not collide with the fixed
allocation-group descriptor/bitmap positions: omit non-data prefix groups and
round the first data group beyond the permanent mapping area.

Keep object IDs independent of physical inode addresses. Reusing a physical pair
must not reuse an object identity or retarget a live notification. A compact object
index maps monotonic 64-bit IDs to separate inode pages: 16-byte index records,
with the existing 256-byte object record in a new inode page type. Updating an
existing inode then needs no object-index rewrite. Preserve root object ID one.

Initial full-handler integration can retain 1 KiB metadata pages. A subsequent
512-byte metadata mode needs a smaller reserve record and bitmap window, while
long names/links need authenticated continuation pages. Scratch roles must remain
large enough to materialize the existing 768-byte logical text inputs. Ordinary
file allocation units, inline capacity and UTF-8/legacy name semantics remain
unchanged. A small-page mode must not silently truncate these features.

## Buffered execution

Keep the existing bounded dirty-image and per-operation rollback machinery.
Foreground mutations use logical metadata addresses. Cold metadata reads follow
the durable mapping root; dirty images are read directly from the current write
set. No unsealed map node needs to be exposed to ordinary reads.

Retirement can stream mapping changes after sorting at most 42 dirty pair
references. The map writer derives old copy selectors from the authenticated map,
records the corresponding inactive targets, and writes map nodes in postorder.
Its state and at most eight frame pages can borrow retired undo storage. Preserve
the separate capacity-retry snapshot. No rollback is attempted once retirement
I/O begins; uncertain completion poisons the mount.

All ordinary metadata and mapping writes precede the first device barrier. Both
new checkpoint writes precede the second barrier. Only then advance the durable
generation and release deferred reuse/discard. Explicit flush, unmount and
shutdown retain their device-drain and error propagation requirements.

The core remains the owner of the monotonic I/O-token namespace. Mapping cursors
receive a completion only after the core has matched its token and byte count.
The publication kernel's private completion token must be translated through this
owner, including error, short completion, cancellation and media removal. Reset
borrowed component contexts only after the actual device request is quiescent.

Use the existing fast CRC-32C table through a caller-owned checksum context. The
new cursors accept initialized table addresses and use their own private word
scratch; bitwise validation remains available for standalone model tests. Cache
authenticated map nodes by physical selected address and mount generation. Never
validate or cache an unselected future copy as current metadata.

## Required evidence

Add an independent CPX5 formatter/decoder, including pair ownership, map-region
accounting, the compact inode index and all referenced payloads. Preserve the
existing guest workloads and original FFS/SFS inputs. Pass both guest checks and
the independent persisted-data checks before counting a trial. Use screening
trials during redesign and repeat the full reference comparison before acceptance.

Run the Release solution build/tests, rebuild the affected standalone handler,
run CopperStart's native replays and a separate full-ROM gate. Report host,
compiled native, full-ROM and physical-media coverage independently. Keep the
qualified V7 source available until a replacement has passed its own gates.
