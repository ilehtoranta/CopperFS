# Deferred automatic TRIM (experimental)

Automatic TRIM remains disabled by default. Build with
`build/Build-Handler.ps1 -AutoTrim`, adding `-Ffs` for OFS/FFS, no format
switch for SFS, `-Sfs2 -Sfs2CsunWrites` for SFS2 CSUN, or `-Sfs2` for SFS2
TRST. Use a separate artifact directory per profile. Manual CopperTrim retains
its existing single-range transport, maintenance scan and version-1 packet ABI.

The [OFS/FFS mount index qualification](ffs-index-performance.md) covers the
indexed CopperFFS handler. The [metadata write comparison](metadata-write-qualification-2026-11-12.md)
records the preceding cross-family measurements.
The candidate still fails original-handler parity, so the default remains off.

SFS2 TRST also has an opt-in `-GroupedSfs2Trst` profile. Its
[cross-packet grouping qualification](sfs2-group-qualification-2026-11-13.md)
documents commit boundaries and the original-SFS comparison. Pending groups
block automatic discard; manual TRIM commits them before scanning allocation.

## Tracking and memory

Unallocation accumulates consecutive blocks in the freeing operation's local
state before notifying the mount. The callback extends the
last adjacent/overlapping hint or appends to a fixed 128-entry queue. It does
not search the queue, force a commit or send a disk command. The first pending
FFS range schedules the five-second timer.
Overflow drops incoming hints. SFS recycled files remain allocated; their data
is eligible only when actually unallocated. There is no allocation-side
cancellation and no persistent discard log.

An opt-in mount uses up to 2,148 bytes of dedicated TRIM storage.

| Storage | Bytes |
| --- | ---: |
| 128 range pairs and queue count | 1,028 |
| Prepared batch, layout cursor, input/output/remainder ranges | 512 |
| Scheduling, capabilities and counters | 64 |
| Generation-bound SFS root scalars | 32 |
| DMA-compatible SCSI command buffer | 512 |
| **Total** | **2,148** |

The specialized CopperFFS handler places its 64-byte control record and a
four-byte staging pointer in the unused SFS reservation area. Its 64, 256 and
1024 KiB workspace builds each reserve the last 1,572 bytes for the queue,
batch and root scalars. The first free initializes that storage. A mount that
could not allocate the optional index requests those 1,572 bytes separately
on its first free. The portable core receives only the index portion.

The DMA command buffer adds 512 bytes on the first eligible CopperFFS service
turn. Other handlers request it during automatic-TRIM initialization. Failure
to allocate staging or the command buffer disables maintenance for the
attachment. Timer resources are additional.

## Commit boundary and ownership

The sequence is **record hints → normal commit → validate allocation → one
discard command → check waiting packets**. Foreground replies never execute
automatic TRIM. A five-second timer establishes eligibility; ordinary SFS
commit processing remains responsible for completing buffered changes.

Automatic collectors never recover a journal or commit a logical overlay.
SFS/SFS2 require no pending overlay/checkpoint, a validated clean marker and a
successful durability barrier. TRST publication alone is insufficient. FFS/OFS
require valid allocation metadata and an idle-time durability barrier. A raw
device `Flush()` cannot certify an unfinished filesystem transaction.

The explicit 512-byte batch context belongs to a mount and generation. In the
specialized CopperFFS handler the first intervening packet, including a read,
invalidates the prepared input. Subsequent packets see an empty batch. The
other handlers check the pending context on each foreground operation.
Recovery, inhibit, media/geometry reset and failed maintenance
also invalidate it. Prepared root values are scalars, not borrowed cache
pointers. Callers of the portable collectors must enforce this lifetime and
only reuse Stage/RootsValid after their own successful validation.

Only bitmap words intersecting the selected hints are scanned. Checks include
bitmap identity, checksum and permanent-metadata allocation/exclusions.
Free-then-reallocate is safe because the current allocation state decides what
can be discarded. Single-owner mount execution excludes mutation from final
validation through device completion. Remainders are hints: allocation is
checked again before the next command even if the generation has not changed.

With a verified CopperFFS workspace, the collector looks up bitmap identities
directly and checks the sorted permanent-block keys against the hints. It still
reads and checks the affected bitmap before each command. Manual CopperTrim
rebuilds the entire layout proof on a fresh invocation.

Without a verified workspace FFS shares one pointer-layout walk across the batch, retains exact alias checks
for touched bitmap pages and protects all layout keys. It yields after at most
128 pointer entries per maintenance turn. The preliminary lookup of selected
bitmap addresses uses one forward cursor and the same budget; root and bitmap
self-allocation checks use addresses captured by the walk instead of starting
new pointer-chain lookups. A foreground operation discards its
partial context. Work therefore scales with pointer-layout size, but no
unrelated bitmap contents or full-volume free-space totals are scanned.

## Bounded idle processing

The event loop services maintenance only when the packet port is empty. Each
batch selects at most eight hints totalling 4,096 filesystem blocks, sorts and
merges them, and issues at most one UNMAP before checking the port again.
Subsequent idle turns continue immediately without waiting for a larger batch.
The command limit is aggregate across all descriptors, not a limit per range.

An optional batch-discard platform interface packs up to eight ranges into one
UNMAP. Devices advertising one descriptor receive one range per command. Up
to eight excess free fragments remain staged for revalidation; further
fragmentation is dropped. Alignment rounds inward and never extends a discard
into unchecked blocks. Splitting queued ranges preserves enough overlap to
avoid losing aligned interior sectors at a batch boundary.

Capabilities, including unsupported status, are cached for a media attachment.
Inhibit/remount and geometry/media resets clear the cache. Query, discard or
ordinary device-transfer failure suspends maintenance until remount. A partial discard invalidates all
submitted cache ranges, since any subset may have completed. Pending recovery
or failed commit also suspends automatic maintenance; normal recovery owns the
repair. The filesystem operation that produced the hint remains unaffected.

A crash loses optional reclamation. Existing free space without hints remains
available to manual CopperTrim. Continuous foreground traffic can postpone
maintenance indefinitely. CPU work and command count are bounded; a packet
arriving during synchronous UNMAP still waits for that command's device latency.

## Qualification and enablement

The release gate is parity with original FFS 40.1 and SmartFilesystem 1.279,
with CILica scalar replacement enabled and inlining disabled. Comparisons use
identical media/CPU/packets, three rotated trials and controlled Fast or Chip
RAM payload placement before either handler is loaded. Mutation timing includes
an explicit filesystem ACTION_FLUSH. Separate measurements report initialization,
tracking, deferred service, device commands and reclamation coverage.

See the [current qualification report](deferred-trim-design-qualification-2026-11-12.md).
Emulator tests do not qualify physical SSD/controller latency,
real DMA behavior or hot replacement. No package or ROM is published here.
