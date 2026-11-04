# Compatibility and recovery

The separate `AmigaSmartFileSystemReadOnlyCore` accepts clean classic version-3
and SFS2 version-4 volumes. It exposes wide sizes/positions and refuses dirty
volumes without writing or replaying recovery. Existing classic read, write
and recovery entry points remain classic-only. See [verified SFS2 layout,
API and validation limits](sfs2-read-only.md).

`AmigaSmartFileSystem2WriteCore` is the separate SFS2 version-4 mutation and
recovery API. It preserves wide lengths and version-4 metadata, rejects classic
volumes, and never converts a volume. Ordinary operations use private SFS2
`CSUN` undo; `ExecuteTrst` opts into native redo. Original-handler recovery of
generated SFS2 redo and clean handoffs have separate emulator evidence. See
[SFS2 writes and recovery](sfs2-writes.md) for buffers, guarantees and limits.

CopperSFS targets classic `SFS\0` version-3 volumes. The core reads object
containers and nodes, directory chains, bitmaps and extent trees, and supports
file and directory mutation and metadata updates. It validates original SFS
`TRST` redo records on mount and replays complete records before exposing
the volume.

Its ordinary portable mutation APIs use `CSUN` undo records, explicit flush barriers and a
commit marker. This journal is private to CopperSFS. An interrupted CopperSFS
write needs CopperSFS recovery; the original SFS handler should not be
expected to interpret `CSUN`. A damaged journal remains dirty rather than
being silently accepted. The journal reserves free blocks temporarily, so a
mutation can report a full disk before every free data block is consumed.

The opt-in [ExecuteTrst writer](trst-journaling.md) emits original-format TRST
redo records for whole operations, including file data. Its published journal
can be recovered by the original handler; an interrupted published operation
rolls forward. Existing CSUN recovery remains available. This path requires
caller-owned memory for every changed block and does not change the default.

Classic native development handlers now default to [bounded TRST transactions](native-trst.md),
after compiled crash/recovery and original-handler qualification. Their fixed
192 KiB arena rejects oversized operations atomically with `NoFreeStore`.
Existing CSUN recovery remains supported. This does not update previously
published handler binaries or enable the separate SFS2 native profile.

After a failed mutation, public file reads and metadata queries reject a dirty
transaction with `DiskNotValidated` (or `false` for boolean query APIs). Call
`AmigaSmartFileSystemWriteCore.Recover` before retrying those queries. The next
mutation also attempts recovery automatically. A failed `Write` reports zero
bytes in `actual`; failure at the final commit barrier can still leave the
complete new state, so callers must check the result after recovery. No partial
transaction is exposed as a successful read.

Content writes, resizing and truncation clear the archive protection bit in
the same transaction as the content change. Zero-length writes and unchanged
sizes preserve it. Empty directory containers are unlinked and returned to the
admin pool, including clearing the parent's first-container pointer when its
last child is removed or moved.

The core assumes the block device preserves completed block writes across
flush barriers. The external `L:` handler has passed a limited Kickstart 3.1
emulator replay, including one interrupted-write recovery point; see
[validation](validation.md) for the exact boundary. There is no formatter or
RDB autoboot installer here.
