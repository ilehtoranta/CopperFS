# Classic OFS/FFS DOS0–DOS5 writes

CopperFFS's maintained writer and quick formatter support DOS0–DOS5. The low bit
selects FFS data blocks versus OFS headers/chains; types 2–5 use international
Latin-1 name hashing. Byte 0xFF remains unchanged and cannot alias 0xDF.
DOS6/DOS7 are outside this core's supported formats.

DOS4/DOS5 also maintain T_DIRC directory-cache chains. The canonical hash tables
remain authoritative. Before publishing a valid root, the writer rebuilds only
the affected directory caches using the existing three caller-owned scratch
blocks. Creation initializes an empty cache for a new directory. File growth,
truncation, date/protection/comment changes, rename/move and deletion update the
containing directories; deletion releases the directory's own cache blocks.
Existing cache chains are checked for type, ownership, checksum, bounded records,
allocation and termination before their pages are reclaimed. Allocation metadata
is flushed before the root is marked valid. Any failed mutation retains the
existing invalid-root contract; callers must discard unpublished images or
validate failed direct-device writes before continuing. Trim remains separately
qualified for DOS0/DOS1.

The independently implemented layout follows the
[ADF format description](https://adflib.github.io/FAQ/adf_info.html#p47) and the
[current ADFlib cache-record reference](https://github.com/adflib/ADFlib/blob/master/src/adf_cache.c).
No ADFlib implementation is incorporated. Cache blocks store type 33, their own
block number, parent, record count, successor and checksum. Records contain the
entry block, size, protection, UID/GID, 16-bit date fields, entry type, Latin-1
name and comment, followed by word alignment. Full comments are retained within
the 79-byte canonical limit; record and block bounds determine packing.

Validation distinguishes direct managed core tests, actual compiled 68020 guest
execution and standalone handler/native replays. Independent byte-level tests
compare cache records with canonical headers and allocation bits, so the normal
hash-chain reader cannot conceal stale caches. Tests cover 512/1024-byte blocks,
empty and multi-block caches, international duplicates, OFS extension chains,
metadata changes, moves, truncation, removal, reclaimed space and corrupt cycles.
CopperScreen imports continue to use staged whole-image replacement, with writes
performed exclusively by the packaged guest service. No throughput or JIT claim
is made. See the local validation receipts for exact run counts and unavailable
external-media coverage.
