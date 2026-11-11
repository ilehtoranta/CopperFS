# Deferred automatic TRIM (experimental)

Build with `build/Build-Handler.ps1 -AutoTrim`, adding `-Ffs` for OFS/FFS,
`-Sfs2` for SFS2 CSUN, or `-Sfs2 -Sfs2TrstWrites` for SFS2 TRST. The option
remains off by default. Use a separate artifact directory per profile.

A successful DeleteObject packet schedules maintenance on the handler's
five-second timer. Another deletion restarts the cursor. Recycled SFS files
remain allocated and are preserved. Truncation alone does not schedule a pass.

Each turn discards within a 4,096-filesystem-block window. Windows overlap for
physical-sector alignment. The scanner visits only bitmap pages intersecting
that window. Mount ownership remains exclusive until synchronous UNMAP completes;
allocations cannot interleave with a scanned range. Device requests can still
stall foreground packets. This is deferred maintenance, not an idle-priority or
asynchronous device worker.

## Validation and durability

The first window recovers outstanding transactions, checkpoints acknowledged
changes, completes the durability barrier and validates the full allocation
layout. A caller-owned four-byte certificate retains that global validation
while the mount remains unchanged. Subsequent windows still prepare fresh work
and checksum the current bitmap pages; they retain no lists of freed addresses.
They reuse the completed barrier when no writes or recovery have intervened.

The native adapter invalidates the certificate before normal, queued, overlay,
retained, unpublished and raw device writes, including failed attempts. Pending
buffered timer commits also invalidate it before being attempted. Geometry
changes, inhibit/remount and teardown reset maintenance and validation state.
A restart begins with a cleared certificate. Reallocation between windows is
therefore checked against the current allocation map, and pending changes must
be made durable before newly free space can be discarded.

Portable callers opting into the certificate MUST implement that same lifetime
contract. Null storage retains uncached full validation and barriers. This is
explicit caller-owned scratch, independent of all filesystem work buffers.
The manual command and its version-1 packet ABI are unchanged.

The first full preflight can still be expensive on large volumes; in particular,
FFS checks bitmap-pointer aliases. The window bounds discard volume, not a hard
latency limit. Cached validation does not replace a complete filesystem checker.

## Devices and lifecycle

The build adds 44 bytes to mount state and uses the existing temporary manual
TRIM control buffer during service. There is no allocated free-range queue.
Discovery still requires SCSI UNMAP through HD_SCSICMD. There is no ATA
pass-through or inference from an SSD label. Unsupported discovery in the
automatic FFS path no longer reads the boot block and evicts cached file data.
Manual discovery keeps its existing format-check ordering.

Unsupported devices and failed passes suspend automatic TRIM without changing
the delete result. Inhibit stops maintenance. Remount clears the suspension and
certificate; a subsequent successful delete can schedule a new pass. Manual
CopperTrim remains available independently.

## Release gate

The release performance gate is parity with original FFS 40.1 and
SmartFilesystem 1.279, not parity with an earlier Copper build. The new original
comparison rotates three trials, uses identical media and CPU settings, and
allocates the Fast RAM payload before loading either handler. It includes an
explicit filesystem ACTION_FLUSH in the mutation timing and verifies results.
Internal before/after comparisons remain useful diagnostics.

Automatic TRIM remains disabled. See [qualification and measurements](automatic-trim-qualification-2026-11-12.md)
for the original-handler results, cached preflight costs and coverage limits.
Physical devices, asynchronous controllers and actual hot-swapped media remain
unqualified; emulator inhibit/remount checks are separate evidence. Neither
packages nor ROMs are published by this work.
