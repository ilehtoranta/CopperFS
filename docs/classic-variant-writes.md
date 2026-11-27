# Classic OFS/FFS DOS0–DOS5 writes

CopperFFS's maintained writer and quick formatter support DOS0–DOS5. The low bit
selects FFS data blocks versus OFS headers/chains; types 2–5 use international
Latin-1 name hashing. Byte 0xFF remains unchanged and cannot alias 0xDF.
DOS6/DOS7 long-name volumes are read-only: every write fails with 214 (see
`compatibility.md`).

DOS4/DOS5 also maintain T\_DIRC directory-cache chains. The canonical hash tables
remain authoritative. Before publishing a valid root, the writer edits only the
records a mutation changed, using the existing three caller-owned scratch
blocks: new records go to the head cache block (a full head gets a new head
block next to the directory), changed records stay in their block when they
fit, and a block emptied by a removal leaves the chain. Creation initializes an
empty cache for a new directory, and deletion releases the directory's own cache
blocks. A directory without a cache chain is rebuilt in full. Cache blocks are
checked for type, ownership, checksum, bounded records and allocation before
they are edited or reclaimed. Allocation metadata is flushed before the root is
marked valid. Any failed mutation retains the existing invalid-root contract;
callers must discard unpublished images or validate failed direct-device writes
before continuing. TRIM covers DOS0–DOS5 and leaves cache blocks allocated.

The independently implemented layout follows the
[ADF format description](https://adflib.github.io/FAQ/adf_info.html#p47) and the
[current ADFlib cache-record reference](https://github.com/adflib/ADFlib/blob/master/src/adf_cache.c).
No ADFlib implementation is incorporated. Cache blocks store type 33, their own
block number, parent, record count, successor and checksum. Records contain the
entry block, size, protection, UID/GID, 16-bit date fields, entry type, Latin-1
name and comment, followed by word alignment.
