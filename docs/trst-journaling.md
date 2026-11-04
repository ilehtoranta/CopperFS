# Opt-in native SFS TRST journaling

`AmigaSmartFileSystemWriteCore.ExecuteTrst` writes classic `SFS\0` redo
transactions. Ordinary mutation APIs still default to CSUN. CSUN recovery is
retained, and ExecuteTrst recovers either journal type before staging new work.

```csharp
var request = new AmigaSmartFileSystemWriteCore.Request
{
    Node = node, Position = position, Buffer = source, Length = length
};
bool ok = AmigaSmartFileSystemWriteCore.ExecuteTrst(
    ref platform, ref work, ref request,
    AmigaSmartFileSystemWriteCore.TrstOperation.Write,
    stagingBuffer, stagingBytes, out uint actual);
```

The operation enum also covers file/directory creation, remove, rename, resize,
truncate, protection, comment and date. Request fields match the ordinary APIs;
resize takes the new size in `Position`. The four Work scratch blocks, source,
name/date inputs and staging buffer must be mapped and nonoverlapping as
appropriate. The staging buffer must be disjoint from every input and scratch
block. The adapter must serialize access to the volume for the complete call
and any required recovery; this API does not implement locking.

## Whole-write atomicity and bounded memory

Every changed data and metadata block is staged in a caller-owned RAM overlay.
Repeated writes to a block replace its staged version, and reads see the latest
staged contents. One entry costs `BlockBytes + 4` bytes after a 24-byte header
and a bounded hash index, all inside the supplied buffer. The index stores
slot addresses, resolves collisions by probing and always retains empty
buckets. It is cleared at the start of every transaction. The caller must allow
room for all data blocks and metadata touched by the request, including extent
index growth. There is no heap allocation or implicit splitting into transactions.
Insufficient staging memory returns `NoFreeStore` before changing live blocks.
At 512-byte blocks, 128 data slots alone require 66,048 bytes; the arena header,
hash table, metadata and four scratch blocks need additional storage. Supported block sizes for
this writer are powers of two from 512 through 32,768 bytes (native record
lengths are 16-bit).

The public core stages through its scalar mutation engine; the embedded native
core also supports bulk append allocation. Both use the indexed overlay for
staged-block lookup. These implementation differences do not split a Write
request into smaller transactions.

## Commit protocol and format

1. Stage the entire mutation without writing any live block or dirty marker.
2. Encode each final block relative to its original disk contents using native
   LONGCOMPRESSION. Pack word-aligned OperationInformation records into linked,
   checksummed TRST blocks; operations can cross page boundaries.
3. Flush the complete journal, publish its head in TRFA and flush again.
4. Apply the redo records, flush the target blocks, then write and flush TROK.

The encoder classifies each original/final longword once and saves one byte
per run in the existing `Work.Bitmap` scratch block. The plan needs at most
`BlockBytes / 4` bytes; literals remain in the staging arena until emission.
Journal allocation uses `Work.Meta`, so page crossings preserve the plan.
This adds no allocation or staging overhead. The four compression modes,
64-word run limit, record alignment and commit barriers are unchanged. See
the [compiled encoding and copy qualification](native-trst-codec.md).

Journal space must be free in both original and final allocation bitmaps and
must not be a staged target. Monotonic allocation keeps journal pages distinct.
Blocks freed by the pending operation cannot hold its journal. Disk-full or
I/O failure before publication leaves the previous filesystem state intact;
unreferenced journal pages remain free and can be reused.

After publication, a failed operation needs recovery and rolls **forward** to
the complete new state. Failed writes report zero actual bytes even when the
published transaction will complete. Callers must recover and inspect the
result before retrying. Unchanged-word compression skips only words identical
in original and final images, so repeated or interrupted replay is idempotent.
Checksums, ordinary SFS metadata and file data are included in the transaction.

The protocol assumes reliable flush barriers and whole-block persistence,
matching the existing crash model. It does not qualify torn sectors. TRST and
CSUN are distinguished by record IDs during recovery; existing CSUN volumes
remain recoverable. Native format references are listed in [provenance](provenance.md).

## Validation and default policy

`TrstWriteTests` checks complete independent volume manifests after every
write/flush cut and transient/persistent I/O failure for twelve mutation types.
It also interrupts recovery, tests legacy CSUN recovery before TRST writes,
buffer exhaustion, journal exhaustion, no-op writes, and 512/1024/2048/4096-byte
blocks. The 128 KiB test visits every sector/flush event with explicitly bounded
persistence samples rather than claiming exhaustive large-write enumeration.

The 2026-10-24 focused run passed 22 tests, checking 8,938 exhaustive
crash/failure/recovery images plus 5,330 sampled images for the 128 KiB request
(all 533 sector/flush cut points). A protection-bit update used four block
writes and four flushes with TRST versus six writes and five flushes with CSUN.
These are host I/O counts, not native throughput measurements.

`TrstOriginalHandlerTests` is optional because its native harness and licensed
media are external. It boots **original SmartFilesystem 1.279** under Kickstart
3.1 on cloned, independently formatted media. For overwrite, append, first
write, create, rename, truncate and delete, it recovers distinct dirty images
covering durable and all-pending-write prefixes at every writer cut point.
Each result must have a durable clean marker and match a full independent
amidisk manifest, including file hashes, metadata and free-block count.
The fixture has pre-existing extent links outside the stricter local checker's
accepted subset; native checks use pinned amidisk without changing that fixture
or relaxing the local checker. Arbitrary pending-write subsets are covered by
the separate synthetic host tests.

Build host tests, then supply the existing native A/B harness and local inputs:

```powershell
dotnet test tests/CopperSFS.Core.Tests/CopperSFS.Core.Tests.csproj -c Release `
  --filter FullyQualifiedName~TrstWriteTests
./scripts/Test-TrstOriginalHandler.ps1 `
  -NativeTestAssembly <native-reference-test-assembly.dll> `
  -FixtureManifest <pinned-reference-manifest.json> `
  -CopperHandler <existing-development-handler> `
  -Python <python-with-pinned-amidisk> -Rom <licensed-kickstart-3.1.rom>
```

`-TestAssembly` supports isolated build outputs and `-OutputDirectory` selects
the retained TRX, receipts, expected images and boot artifacts. The harness
checks the original handler/archive and amidisk identities. The CopperHandler
input satisfies its existing fixture contract; recovery boots explicitly use
the original handler. No original implementation source or media is included.

On 2026-10-24 the initial native run passed **36 distinct dirty-image recovery
boots** across seven operations. This validates journal interoperability, not
execution of the new writer as compiled MC68000 code or physical-device
behavior. The subsequent [native handler integration](native-trst.md) qualifies
the compiled writer with bounded memory and makes TRST the classic native
development default. Ordinary portable mutation APIs keep their existing
CSUN default. No downloadable handler or release was changed.
