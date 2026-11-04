# SFS2 format verification and read-only support

`AmigaSmartFileSystemReadOnlyCore` reads clean classic `SFS\0` version-3 and
`SFS\2` version-4 volumes. This is an additive portable API. The existing
`AmigaSmartFileSystemCore` and write/recovery APIs remain classic-only; they
reject SFS2 before changing media. Neither existing volumes nor native handler
binaries are upgraded by this work.

The subsequent [SFS2 writer and recovery API](sfs2-writes.md) is available
separately. The read-only API still never changes media or performs recovery.

## Verified layout

The version-4 interpretation was checked against a disposable image formatted
and populated by the original AmigaOS 3.x SmartFilesystem 1.279 binary, running
under licensed Kickstart 3.1 on an emulated 68020. The generator does not use
CopperSFS to format or write the reference filesystem.

| Field | Classic, structure version 3 | SFS2, structure version 4 |
| --- | --- | --- |
| Root identifier | `0x53465300` | `0x53465302` |
| Sum of metadata longwords, modulo 2^32 | `0xFFFFFFFF` | `0xFFFFFFFE` |
| Fixed object header | 25 bytes | 27 bytes |
| File length, relative to object | 32-bit big-endian at 16 | 48-bit big-endian at 16; `(U32(16) << 16) \| U16(20)` |
| Directory first-container pointer | 32-bit at 16 | 32-bit at 16; the extra two bytes do not widen it |
| Modification seconds since 1978 | 32-bit at 20 | 32-bit at 22 |
| Object flags / start of name | 24 / 25 | 26 / 27 |
| Extent leaf record | 14 bytes; 16-bit block count at 12 | 16 bytes; 32-bit block count at 12 |
| Object-node branch pointer decoding | Shift right by `log2(blockBytes) - 5` | Shift right by **1**, discarding the fullness bit |

Root geometry and tree pointers retain their offsets: total blocks 48, block
bytes 52, root object container 104, extent tree 108, object-node tree 112.
Object containers still start their entries at 24, with parent/next/previous
at 12/16/20. Names and comments are NUL terminated and the following object
is aligned to an even byte. Object-node leaves retain 10-byte entries. The
clean transaction marker remains `TROK` at root-object-container plus two.

Two independent readers were consulted as layout references:

- [WinUAE `zfile_archive.cpp`, revision 18376e3e](https://github.com/tonioni/WinUAE/blob/18376e3e27daaeb7ed22cb95944c4c3b211735c0/zfile_archive.cpp)
  corroborates checksums, object offsets, 48-bit lengths and wide extents.
- [Aaru SFS reader, revision 1f9c0a50](https://github.com/aaru-dps/Aaru/tree/1f9c0a50b814a338a5671b408ad7abacab0345eb/Aaru.Filesystems/SFS)
  provides a second layout reference. Its object-node index decoder applies
  the classic shift to SFS2; the original-handler growth fixture demonstrated
  that version 4 instead uses one fullness bit. It is not treated as an oracle.

The original-handler fixture creates `Docs/Payload` (1,301 known bytes),
`LengthProbe` (513 known bytes), an original-handler comment, a known date and
protection, and 80 empty files in `Docs`. This grows the packed directory chain
and the object-node index. In the verified image, the index entries `0x1D`
and `0x1E` resolve to NDC leaves 14 and 15, with the former marked full.

No source from those implementations is included or translated into this
repository. The new implementation, probe and synthetic tests are original
work. Original handler binaries, licensed ROMs and generated images remain
local, ignored artifacts.

## API and compatibility

```csharp
if (!AmigaSmartFileSystemReadOnlyCore.Mount(ref platform, metadata, out var format))
    return; // Unsupported, damaged or dirty volume; no recovery is attempted.

uint node = AmigaSmartFileSystemReadOnlyCore.Resolve(
    ref platform, 1, path, pathBytes, metadata, out var error);
if (node == 0) return;

if (!AmigaSmartFileSystemReadOnlyCore.Describe(ref platform, node, metadata, out var entry))
    return;
ulong size = entry.Size;

int actual = AmigaSmartFileSystemReadOnlyCore.Read(
    ref platform, node, position64, destination, requestedBytes,
    metadata, data, out error);
```

The platform needs only `IAmigaFileSystemPlatform`. The reader never calls
device writes or flushes, even if the supplied platform also supports writing.
`Entry` exposes full-width length, flags, protection, ownership, modification
seconds and name/comment pointers into the metadata buffer. Copy strings before
reusing that buffer. Metadata, data and destination buffers must be distinct,
mapped and appropriately sized. Each read is limited to `Int32.MaxValue` bytes;
its position is 64-bit. Reads may return a positive partial count with an I/O
error, as in the classic reader.

For enumeration, initialize a `uint cursor` to the directory's node and call
`ExamineNext` until `NoMoreEntries`. Hidden entries are skipped. `CountUsed`
reports used filesystem blocks from the clean root's free-space count.

Version/identifier pairs and checksum seeds must match. Both root copies are
examined; incompatible valid formats are rejected and the newest valid sequence
is selected, including sequence wrap. Dirty volumes are refused without redo
or undo replay. Recover them with their writing handler before using this API.

The original classic reader/writer APIs are still available with their previous
signatures and semantics. Call the new API explicitly to read SFS2. It does not
change AmigaDOS packet handling, legacy FileInfoBlock sizes, mount packaging or
the downloadable Amiga handler. Old classic handlers still cannot read SFS2.
The subsequent write milestone also extends `CopperSFS.Check` to both formats.

## Validation and reproduction

The reference generator is deliberately outside the default solution. It needs
the sibling CopperSharp68k and CopperScreen trees, an independently obtained
original handler and a licensed Kickstart 3.1 ROM. It only writes a **new** output
directory and a freshly created disposable 16 MiB image:

```powershell
dotnet run --project tools/CopperSFS.Sfs2Reference -c Release -- `
  C:/path/to/SmartFilesystem C:/path/to/kickstart-3.1.rom `
  artifacts/sfs2-reference/new-run

$env:COPPERSFS_SFS2_REFERENCE_IMAGE = (Resolve-Path `
  artifacts/sfs2-reference/new-run/reference.hdf).Path
dotnet test CopperSFS.slnx -c Release
```

`report.json` records guest outcome, image, handler, ROM, compiler and emulator
hashes. Guest result 42 confirms format, creation, writes, metadata and the final
inhibit/checkpoint completed. The optional external-image test then compares
paths, sizes, contents, comments, dates, protection, indexed lookup and directory
enumeration, asserting no device writes/flushes and no source-image changes.
Without the environment variable that test is explicitly skipped; synthetic
tests still run.

The verified 2026-10-24 reference run is retained at
`artifacts/sfs2-reference/original-06/`. Its image SHA-256 is
`B11C46915C402D5A8E617FBC2F0BC2CD67549E4FD03F9363256148AD168D65B0`;
the original handler SHA-256 is
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

Synthetic tests additionally cover both formats, 512/1,024/2,048/4,096-byte
blocks, 107-byte names, Latin-1 case folding and case-sensitive lookup, root
fallback/wrap/mismatch, malformed strings/checksums/trees, dirty-volume refusal,
and mutation/recovery rejection. Sparse fixtures exercise a volume beyond
128 GiB, a file beyond 4 GiB, extents longer than 65,535 blocks, indexed extent
and object-node trees, high offsets, EOF and seek overflow. Sparse backing is
a host test technique; it does not claim SFS sparse-file semantics.

The final focused run passed all 30 cases, including the external image, with
zero skips (`artifacts/sfs2-reference/final-focused/sfs2.trx`). An earlier full
solution run passed 263 tests. After six more reader regressions and concurrent
TRST writer work were added, the recorded full run passed all 269 tests outside
`TrstWriteTests`; that separate suite had four passes and 15 failures. The full
solution is therefore not reported as green. Both runs and source/binary
identities are retained in `artifacts/sfs2-reference/verification.json` and its
referenced TRX files. No TRST writer files were changed for this milestone.

The native reference image uses 512-byte blocks and small files. Larger files,
larger partitions and other block sizes have **synthetic host coverage**, not
original-handler/native qualification. These tests qualify portable traversal,
not a compiled native CopperSFS SFS2 handler or complete filesystem integrity.
SFS2 recovery and mutation are covered by the subsequent write milestone.
Formatting, conversion, link-target traversal, older experimental SFS2 layouts
and newer vendor variants remain outside these milestones.
