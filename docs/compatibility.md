# Compatibility and recovery

CopperSFS targets classic `SFS\0` version-3 volumes. The core reads object
containers and nodes, directory chains, bitmaps and extent trees, and supports
file and directory mutation and metadata updates. It validates original SFS
`TRST` redo records on mount and replays complete records before exposing
the volume.

Current classic standalone builds default to buffered original-format TRST.
Metadata redo records publish payload written into previously unpublished
replacement space, preserving whole-Write atomicity. The native staging budget
is bounded at 192 KiB. Successful Writes are visible before they are durable;
Flush, Close, Inhibit and timer commit establish durability. Crash recovery and
original SFS 1.279 compatibility are qualified for the exact compiled binaries
in the [latency report](trst-latency.md), within its stated device assumptions.
Core integrators use `WriteTrstGroup` and `FlushTrstGroup` for grouped TRST writes;
this is separate from the legacy `Work.Buffered` API below.

Legacy CSUN mutations use `CSUN` undo records, explicit flush barriers and a
commit marker. This journal is private to CopperSFS. An interrupted CopperSFS
write needs CopperSFS recovery; the original SFS handler should not be
expected to interpret `CSUN`. A damaged journal remains dirty rather than
being silently accepted. The journal reserves free blocks temporarily, so a
mutation can report a full disk before every free data block is consumed.

The legacy optional bounded write buffer groups successful mutations into a checkpoint.
Readers see pending changes immediately; a crash can lose changes since the
last successful filesystem flush. A failed operation rolls back its own RAM
changes without discarding earlier successful operations. Leaving
`Work.Buffered` null selects strict per-operation durability, optionally using
a separate staging buffer within each mutation. Both policies retain the same
on-disk undo journal and recovery barriers. See
[transaction batching](transaction-batching.md) for the durability ordering and
the platform's read-through contract.

The core assumes the block device preserves completed block writes across
flush barriers. The external `L:` handler has passed a Kickstart 3.1 comparison
against original SmartFilesystem 1.279, including clean handoff and an
unsampled write/flush interruption sweep; see
[validation](validation.md) for the exact boundary. There is no formatter or
RDB autoboot installer here.

## CopperFFS

CopperFFS reads and writes DOS\0 to DOS\5 (OFS, FFS, their international and
directory-cache variants) with block sizes of 512 to 4096 bytes. DOS\6 and
DOS\7 (long file names) are refused. Its reference is the FastFileSystem in
Kickstart 3.1's ROM: the `CopperFS.Kickstart.Tests` reference tests run the same
DOS workloads on both and compare results (links, ExNext and ExAll, locks,
dates, inhibit, error codes, FIB fields, directory caches, notifications,
Format layout). No original FFS 40.1 handler binary was available for these
comparisons.

### Behaviour that matches Kickstart 3.1

- Hard and soft links: lookups follow hard links, report soft links, and
  deletes promote the first link as Kickstart does. MakeLink, ReadLink,
  ExAll, RenameDisk, SetOwner, ChangeMode and WriteProtect are implemented.
- ExNext resumes from `fib_DiskKey`, so delete-while-listing works.
- Inhibit, lock compatibility, `SetDate` (CPTR argument), `SameLock` results,
  object and directory dates, and `fib_NumBlocks` follow the ROM.
- DOS\4/5 directory caches are updated record by record. Their record order
  differs from Kickstart's, but Kickstart lists and edits them correctly.
- The FFS format core (used by CopperStart and host tools) writes the same
  blocks as Kickstart's Format, apart from the root's dates and checksum and
  the last bitmap page's padding past the end of the volume.

### Known differences

- `ACTION_FORMAT`, `ACTION_SET_FILE_SIZE`, `ACTION_DIE` and `ACTION_SERIALIZE_DISK`
  return error 209 (action not known) in the standalone handler; Kickstart
  implements the first two. `ACTION_GET_DISK_FSSM` returns 209 like the ROM.
- Device I/O failures report 213 (not validated) rather than a separate
  device error.
- Notifications are a superset: CopperFFS also signals on `SetProtection` and
  `SetComment`, signals the directory of a deleted file, and signals a watched
  directory being created or deleted. Every notification Kickstart sends is
  also sent.

### Durability and maintenance

Each mutation marks the root invalid, writes its changes, flushes the device
and only then writes the valid root, so an interrupted mutation leaves a
volume Kickstart's validator repairs. On fixed disks, metadata and small
appends are grouped and published after three idle seconds or before the next
unrelated operation. A mutation that would run out of space is refused before
the root is marked invalid; after a device error during a mutation the root
stays invalid until a validator repairs the volume.

TRIM is available on DOS\0 to DOS\5 through the CopperTrim packet, and as
automatic TRIM in `-AutoTrim` builds. It discards only blocks free in the
on-disk bitmap; directory-cache blocks stay allocated. Manual TRIM sends one
UNMAP per free run. Automatic TRIM retries after a failed collection and
suspends itself after four consecutive failures or any failed UNMAP.
