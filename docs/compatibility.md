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
DOS\7 (long file names) mount read-only; see below. Its reference is the FastFileSystem in
Kickstart 3.1's ROM: the `CopperFS.Kickstart.Tests` reference tests run the same
DOS workloads on both and compare results (links, ExNext and ExAll, locks,
dates, inhibit, error codes, FIB fields, directory caches, notifications,
SetFileSize, Format layout and the Format packet). No original FFS 40.1 handler binary was available for these
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
- `ACTION_FORMAT` (the standalone handler, and the format core used by
  CopperStart and host tools) writes the same blocks as Kickstart's Format,
  apart from the root's dates and checksum and the last bitmap page's padding
  past the end of the volume. Like the ROM it needs the volume inhibited
  (202 otherwise), proceeds even while locks are held, and publishes the new
  volume name.
- `ACTION_SET_FILE_SIZE` returns the new size or -1 with the ROM's error
  codes, and clamps the caller's position. The FFS format has no holes, so
  growth allocates every block. On FFS it writes only pointer tables, the
  header and the bitmap; the grown region, like a truncated block's tail,
  reads back whatever the blocks held before, as with the ROM. OFS data
  blocks carry headers, so OFS growth writes them with a zeroed payload
  (the ROM leaves their old payload).
- `ACTION_INFO` counts neither total nor used blocks in the reserved area,
  and Examine reports the root as `ST_USERDIR` (2).

### DOS\6/7 long-name volumes (read-only)

DOS\6 (OFS) and DOS\7 (FFS) volumes mount read-only. The layout comes from
Olaf Barthel's [DCFS and LNFS Low Level Data Structures](https://wiki.amigaos.net/wiki/DCFS_and_LNFS_Low_Level_Data_Structures)
on the AmigaOS wiki, not from any original handler's code:

- Entry headers keep the name and comment as two BCPL strings in one
  112-byte field at the classic comment position, followed by a comment-block
  key and the date. A comment that does not fit beside the name is read from
  its `TYPE_COMMENT` (64) block. The root keeps the classic name and date
  fields, and must repeat the boot block's DOS type in its `FileSystemType`
  field; a volume without it is refused.
- Names use the international hash and may be up to 107 characters, the
  `fib_FileName` limit. Lookups, reads, Examine, ExNext, ExAll and ReadLink
  handle them.
- Every write, including Format, fails with 214 (disk write-protected), and
  Info reports the volume as write-protected (80). The root's `NumBlocksUsed`
  counter is not used; Info counts the bitmap.

There is no OS 3.1.4/3.2 handler or image to compare against, so this is
checked only against images built from that description: host tests in
`CopperFFS.Core.Tests` (`LongNameReadTests.cs`, 512- and 1024-byte blocks)
and a full-ROM test in which Kickstart 3.1's dos.library drives the native
handler (`LongNameReferenceTests.cs`, 512-byte blocks). The header blocks in
[hst-amiga](https://github.com/henrikstengaard/hst-amiga)'s test data were
checked by hand against the same offsets and agree; they are not imported.

### Known differences

- `ACTION_DIE` and `ACTION_SERIALIZE_DISK` return error 209 (action not
  known). `ACTION_GET_DISK_FSSM` returns 209 like the ROM.
- A new file's header is written when it is opened, so a device that fails
  every write fails the Open; Kickstart opens the file and fails the later
  Write or Close. Both report the device's error.
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
the root is marked invalid.

Device errors are reported as the device's own `io_Error` (for example 20),
as Kickstart does. A failed write is retried once, so a transient error goes
unnoticed. After a device error, and whenever a volume is mounted with an
invalid root (for example after a crash), CopperFFS runs its own validator
before the next packet or after an idle period, like Kickstart's: it walks the
directory tree, rebuilds the bitmap and marks the root valid. It uses the
mount index workspace and checks larger volumes in several passes. A damaged
structure (bad checksum, wrong parent, a block used twice) is left alone and
the volume stays read-only for a repair tool. It does not rebuild DOS\4/5
directory caches.

TRIM is available on DOS\0 to DOS\5 through the CopperTrim packet, and as
automatic TRIM in `-AutoTrim` builds. It discards only blocks free in the
on-disk bitmap; directory-cache blocks stay allocated. Manual TRIM packs up
to eight free runs (and at most 4,096 blocks) into one UNMAP when the device
reports multi-descriptor UNMAP in VPD page B0; otherwise it sends one UNMAP
per free run. Automatic TRIM retries after a failed collection and
suspends itself after four consecutive failures or any failed UNMAP.
