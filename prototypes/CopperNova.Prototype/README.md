# CopperNova direct-metadata prototype

The [Workbench namespace qualification](../../docs/coppernova-workbench.md) covers the RDB discovery path and the CNP2 reusable-object format. The [write-side CRC report](../../docs/coppernova-write-crc-2026-11-19.md) records the preceding performance checkpoint.

The [packed leaf CRC and encoder results](../../docs/coppernova-packed-leaf-2026-11-18.md) give the current benchmark and qualification. The [packet-handling profile](../../docs/coppernova-packet-handling-2026-11-18.md) records the preceding build.

This isolated experiment implements the next-format metadata study. It is not a
CPX4 upgrade and cannot mount CPX4 or CPX5 media. It recognizes the experimental `CNM1` (`0x434E4D31`) and
`CNP2` (`0x434E5032`) formats, with version 1 metadata pages. Fresh CNP2 volumes can select root version 2 to enable deletion and object ID reuse. Existing filesystem sources, handler
entry points, packet ABI and build selections are unchanged.

The portable core uses the existing `ICpxMemory`, `CpxIo`, `Wide` and `StepResult`
value ABI from CopperCPX.Core. It does not call the CPX filesystem engine. The
separate native project under CopperFS.Amiga links the existing DMA/TD64/Exec I/O
boundary and depends on CopperSharp, without a CopperStart source or assembly
dependency. Formatter/checker and benchmark tools are separate host consumers.

The new [CNP2 packed metadata and two-barrier root experiment](../../docs/coppernova-packed-root-2026-11-17.md) uses its own media format. The CNM1 layout below is retained for comparison.

The preceding execution-core change and four-way comparison are documented in
[the 17 November report](../../docs/coppernova-execution-core-2026-11-17.md).
The earlier direct-metadata and cache-policy reports describe their frozen
predecessors; they are not measurements of the current source.

## Implemented layout

All metadata pages are 512 bytes and big-endian. Header fields include magic,
version, type, CRC-32C, home address, object identity, generation and volume
identity. Images are sealed for their home address, so redo and home installation
use the same bytes. Disk-loaded pages require checksum and identity validation;
caller-owned dirty pages are sealed once during retirement.

| Partition-relative sectors | Purpose |
| --- | --- |
| 0–1 | Immutable anchors |
| 8–58 and 64–114 | Two journal banks: two descriptor sectors, up to 48 images, commit |
| 116–117 | Alternating clean records |
| 128 | Group descriptor and counters |
| 129 | Inode bitmap |
| 130–161 | Allocation bitmaps, each covering 2,048 sectors |
| 256–1279 | Directly addressed inode slots; slot 0 reserved, slot 1 root |
| 1280 onward | File data |

An inode reserves a 64-byte common header and 256-byte scalar area, leaving
192 bytes for eight 24-byte file extents or short directory entries. Directory
entries contain the full ASCII name, folded-name hash, object reference and
stable entry serial. Full names are compared after hash matches. No selector
catalogue, paired metadata pages, separate name blobs or metadata reserve list
is used. The mixed fixture changes seven distinct metadata pages.

## Execution and durability

The caller must supply a cleared, longword-aligned, exclusively owned 113,408-byte arena for a fresh
mount. It holds operation state, caches, CRC tables, journal images, allocation
quarantine, and hash buckets for live handles and locks. There are 64 cache slots
and at most 48 dirty images.

The core takes a caller-owned 48-byte record for each open or lock. The Amiga
adapter allocates these from Exec and frees them on close, unlock or packet
failure. The number of live records is limited by available RAM. Reads, writes,
and Flush use existing storage. Files are transferred
in chunks no larger than 64 KiB. Path traversal processes at most four resident directories per visit. Same-parent rename resolves its destination after saving the source entry.

CRC addresses and common execution fields stay local within bounded work
batches. The fused CRC kernel has no emitted helper calls. Quarantine retains
its bitmap and uses 32 fixed epoch records in existing state padding: a first
free initializes one 256-byte window, and durable retirement advances the epoch
without clearing 8 KiB. Epoch wrap invalidates the 128-byte record table. The additional 16,896 bytes hold CRC-32C position tables. Packed root and bitmap pages can update the full-sector CRC from a verified image and the regions edited since that image.

Metadata changes are combined in RAM. Successful ordinary writes can precede
persistence. CNP2 persists data and inactive metadata, waits for a device barrier, publishes the new root, and waits for a second barrier before replying. The older CNM1 journal uses four ordered barriers.

1. Finish data transfers and persist journal descriptors and final metadata images.
2. Persist the commit record binding that complete transaction.
3. Install those images at their home addresses and persist them.
4. Persist a clean record binding the exact commit.

Successful ACTION_FLUSH, inhibit/unmount and ACTION_DIE wait for persistence.
The native adapter refuses shutdown or inhibit while resources remain open.
An empty explicit flush still drains the device. The next mutation does not
start before clean retirement. Blocks freed in a buffered group are quarantined
until that retirement. A clean remount reads the small control records and loads
metadata on demand; it does not scan the allocation map.

Recovery selects the newest valid commit, validates the complete bounded redo
set before home writes, installs it idempotently, and publishes a clean record.
A failed, short or cancelled transfer poisons the mount; an uncertain operation
is not acknowledged as durable. Completion tokens remain monotonic across
remounts. Retries after failure require quiescence and a fresh recovered mount.
Pre-flush in-place data overwrites retain their existing atomicity limitations.
The persistence model assumes the device honors completed flush barriers.

Before mutation, supported operations admit the pages, directory space, extent
capacity and DOS resource slots they need. Resource exhaustion and unsupported
growth reject before editing the operation's metadata. Capacity pressure retires
an earlier group before retrying admission. A request spanning multiple transfers
can complete a prefix; a later I/O error retains that completed byte count.
Private packet/file-handle/data-buffer aliases are rejected before I/O or edits.

## CNP2 v7 format

On an empty CNP2 partition, run `CopperNovaFormat CNP0: INITIALIZE NAME "Work Disk"`. Omit NAME for the default `CopperNovaPrototype`. Change the mounted label with `Relabel CNP0: "New Name"`. Labels preserve 1–30 native AmigaDOS text bytes, including high-bit bytes. Slash, colon, and ASCII control characters are reserved. Relabel publishes the shadow root and completes both Flush barriers before replying. Labels and file comments use a fixed byte-to-Unicode mapping on disk (UTF-8). At most 30 DOS label bytes or 79 DOS comment bytes expand to 60 or 158 bytes respectively. A commented object still fits one 512-byte sector and uses the normal shadow write. Both formatters create v7 and the handler accepts only this CNP2 revision. Use a matching older handler to copy existing files to a fresh v7 partition.

A v6 index leaf contains 60 object slots. The first leaf is part of the empty volume. The first new object in each subsequent leaf allocates four consecutive sectors for a private object pair and a shadowed index pair. Later objects in that leaf reserve one pair each. Each allocation bitmap has 32 leaf pointers after its allocation bits. These pointers let the index grow with the partition. The implementation also bounds object IDs by half the partition's sector count and by the cache-key encoding.

An index entry can hold an empty file, an empty directory or a reusable slot. Objects with data, attributes or children use a private sector pair. Deleting a file releases its payload sectors and keeps the pair with the reusable object ID. Flush writes changed objects, shadow indexes and allocation pages before the first device barrier. It then publishes their common root and waits for the second barrier before acknowledging Flush.

A directory first uses its 192 inline bytes for names. When needed, it links additional name pages by a case-insensitive hash with 16 chains. These internal pages consume object slots and retain their space for reuse until the directory is deleted. DOS sees the logical directory as the parent. Enumeration uses stable object IDs across renames and deletions.

Explicit Flush writes changed objects and index leaves to shadow sectors, updates the shadow allocation metadata that selects those leaves, and waits for the first device barrier. It writes the common root and waits for the second barrier before acknowledging success.

The Kickstart runner accepts a total file count and a maximum file count per directory. Use a fresh output directory for each command.

~~~text
CopperNova.Qualification format-scale-v7 <handler> <formatter> <ROM> <output> 64 64
CopperNova.Qualification scale-v7 <handler> <ROM> <output> 4096 4096
CopperNova.Qualification scale-v7 <handler> <ROM> <output> 122880 128
CopperNova.Qualification fragmented-file <handler> <ROM> <output>
~~~

On emulated Kickstart 2.04 (68000), the native formatter initialized a v6 volume for the 64-file single-directory test. The same handler passed a 4,096-file single-directory test and a 122,880-file partition test with 128 files per directory. The partition-wide test used 2,487 index leaves. Each run checked explicit Flush, power cuts before both barriers, cold reopen and deletion, and a final cold lookup of every deleted name with zero writes. The tests verified sampled payloads. The large hardfiles exercised TD64 across 4 GiB using 512-byte filesystem sectors and 1,024-byte RDB metadata blocks. Physical hardware coverage remains open.

The fragmented-file gate alternates 512-byte writes to two files, checks that each has 193 distinct extents, and cold-reads every byte. It cuts power before each publication barrier during creation and deletion, then verifies the surviving file on a fresh Kickstart 2.04 boot.

## Deliberate prototype limits

This is a benchmark-capable metadata prototype, not a feature-complete filesystem:

- All layouts use 512-byte sectors. The single-group benchmarks use 16 MiB. Wide CNP2 volumes use multiple allocation groups.
- Object-index leaves and directory pages grow on demand as described above.
- File and directory names preserve DOS bytes using UTF-8 on disk. Each
  component holds up to 168 encoded bytes. DOS paths allow 255 input bytes.
- CNP2 keeps the newest eight extents inline. When another run is needed,
  it writes the full inline list to a new immutable 512-byte page. Each
  page identifies its file and the preceding logical range. The first
  Flush barrier covers payload, extent pages and allocation metadata. The
  second makes the root referencing those changes durable. Shrinking a
  file moves the surviving tail back inline and frees surplus pages.
  For new overflow pages, the inode indexes the current group of eight.
  Completed groups carry range indexes in the same 512-byte pages. A
  group index holds up to sixteen previous groups directly and uses
  subtree roots above that count. Pages without an extent index are traversed via
  their original links. Shrinking restores the saved index from the
  surviving prefix. The file size limit is
  2 GiB minus one byte.
- Create directories/files, shared locks, open/read/write/seek, shrink, same-parent
  rename to an unused name, close, flush, inhibit/remount and shutdown are supported.
- CNP2 also supports directory enumeration and cross-directory moves. Root version 2 adds deletion and reuse.
- Sparse growth, grow-by-resize, links, extended
  attributes, notifications, record locking, formatting via DOS, automatic trim,
  media-change integration and maintenance timers are outside this prototype.
  Scalar space is reserved; the production attribute/timestamp feature set is
  not reproduced. Unsupported packets return DOS errors.
- On-disk generation and logical/physical counters use the prototype's narrow
  bounds; high words are zero and rejected when not representable. This does not
  claim the proposed larger-format capacity or unrestricted CPX semantics.

The unchanged frozen guest HUNKs still supply their historical CPX4 DOS-type
environment field while explicitly loading this separate segment. The prototype
selects its own format from its anchors. The separate Workbench qualification boots the system disk and discovers its CNP2 partition from an RDB. Original handlers retain their complete feature sets
and their own cache policies, so these results do not establish general feature
or memory-budget parity.

## Build and reproduce

The installed SDK is .NET 10.0.401. The neighboring compiler also declares a
.NET 11 target. Commands select its existing .NET 10 target without editing it:

```powershell
dotnet build CopperFS.slnx -c Release -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0
dotnet test prototypes/CopperNova.Tests/CopperNova.Tests.csproj -c Release
./build/Build-NovaPrototype.ps1
./build/Benchmark-NovaPrototype.ps1 -Packed -Workload mixed -EvidenceRoot '<retained CopperFS artifacts>' -Rom '<licensed Kickstart 3.1 ROM>'
./build/Benchmark-NovaPrototype.ps1 -Packed -Workload stream -EvidenceRoot '<retained CopperFS artifacts>' -Rom '<licensed Kickstart 3.1 ROM>'
$env:NOVA_PACKED='1'
dotnet tools/CopperNova.Qualification/bin/Release/net10.0/CopperNova.Qualification.dll artifacts/nova-prototype-handler/CopperNovaPrototype '<licensed ROM>' artifacts/nova-rom-gate
```

The Kickstart 2.04 formatter replay starts with a blank partition, an RDB-installed handler with a 600-byte startup stack, and a DOS startup floppy. It runs CopperNovaFormat and checks the new root before exercising the namespace. It creates 3,125 files across nested directories, cuts power at both publication barriers, then cold-boots to delete the files and confirm their absence.

```powershell
dotnet run -c Release --project tools/CopperNova.Qualification -- format-object-growth artifacts/nova-prototype-handler/CopperNovaPrototype artifacts/nova-prototype-handler/CopperNovaFormat '<licensed Kickstart 2.04 ROM>' artifacts/nova-object-growth
```

The native build checks zero reachable managed allocation sites and records
source/compiler/input/HUNK hashes. The comparison wrapper validates original
FFS 40.1 and SFS 1.279 hashes, the SFS base image, licensed ROM hash and exact
frozen mixed/streaming guest HUNKs. It never recompiles the measured guests.
`tools/studies/prepare_nova_benchmark.py` derives the separate host runner from
the existing A86 runner. `prepare_nova_gate.py` derives a separate correctness
guest with public memory, a 4 KiB task stack and an unflushed tail before shutdown.
Neither generator changes the existing benchmark guests or CPX4 sources.

The independent decoder verifies complete inode reachability, data ownership,
bitmaps/counters, both retained payload hashes, metadata checksums and a clean
committed generation. Host crash tests exercise physical sectors with lost,
reordered, partially persisted and torn writes after three warmup commits,
followed by a changed retry. Actual-HUNK coverage is reported separately.

See the dated prototype benchmark report under `docs/` for final measurements,
coverage and limitations. ROMs, reference binaries and generated media/build
artifacts remain outside version control. No package or ROM is published.
