# Optional Kickstart comparisons

`tools/Test-OriginalHandlers.ps1` runs a correctness A/B against external original
FFS 40.1 and SmartFilesystem 1.279 binaries. Supply their paths, a licensed ROM,
and an independently formatted clean 16 MiB classic SFS image (two reserved
blocks). The script pins reference binary hashes, builds four candidate profiles,
and records binary/source/input provenance under a fresh ignored output directory.
`-FrozenHandlerRoot` explicitly reuses previously built `ffs`, `sfs`,
`sfs2-csun` and `sfs2-trst` subdirectories instead.

The nine cases are OFS/FFS at 16 MiB, OFS/FFS at DD and HD capacities/geometries,
classic SFS with buffered TRST, and SFS2 with CSUN or TRST. Each case boots both
writers from identical media, independently checks their persisted images, then
fresh-boots both readers against each writer's output (six boots per case).
The guest checks large growth, fragmented allocation opportunities, unaligned
overwrites, truncation by reopening, rename, comment/protection/date, directory
enumeration, missing objects, nonempty-directory deletion and EOF/backward seeks.
The independent walkers compare every file's path, size and content hash across
the two writers, without requiring identical allocation or timestamps.

DD/HD correctness cases use the synchronous CopperHDF device with 11/22 sectors
per track and 80 cylinders. They qualify filesystem geometry and interoperability,
not floppy DMA, physical persistence, rotational timing or physical performance.
This is clean-close/remount coverage; it is not a crash or torn-write A/B.
Original FFS 40.1's OFS append sequence restarts and omitted old-EOF link are
recorded in its inspection report. Copper's newly authored images must retain
canonical sequences and complete forward links.

Classic SFS additionally checks repeated `MODE_NEWFILE` replacement, preserved
recycled bytes/metadata, collision suffixes, empty-file recycling, and the
original handler's delete/write-protection rules. Recycled entries remain in
the strict file-manifest comparison.

For direct invocation, set `COPPERFS_CORRECTNESS_MANIFEST` to the script's
manifest and select `FullyQualifiedName~OriginalHandlerCorrectnessTests`.
Direct tests skip when the ROM or manifest is absent; invalid configured inputs
fail. Reference binaries and images are never added to the repository.

## Native SFS2 TRST crash recovery

`NativeSfs2RecoveryMatrix` ports the six single-mutation cases from the
CopperSFS research snapshot `4a902a1`. It boots Kickstart with a synthetic
16 MiB SFS2 partition, performs each mutation through DOS with the standalone
CopperSFS TRST handler, and checks the uninterrupted result. The mutations
overwrite across a sector boundary, truncate, rename, delete, create an empty
file, and set a comment.

The device model accepts atomic 512-byte writes into volatile storage and
makes them durable on flush. The test traces every sector write and flush,
then repeats the guest workload and interrupts it once per distinct durable
image. It compares the interrupted disk byte for byte with the traced image.
CopperSFS and original SmartFilesystem 1.279 independently mount copies of
that disk. A published TRST journal must recover the complete committed
manifest, including free space and file hashes. Each handler must leave a
clean marker and preserve the disk byte for byte on its next boot.

Build the handler and set the local input paths before running:

```powershell
./build/Build-Handler.ps1 -Sfs2 -Sfs2TrstWrites -ArtifactRoot "$PWD/artifacts/sfs2-trst"
$env:COPPERFS_SFS2_TRST_HANDLER = "$PWD/artifacts/sfs2-trst/CopperSFS"
$env:COPPERFS_ORIGINAL_SFS = '<path to original SmartFilesystem 1.279>'
$env:COPPERSHARP_KICKSTART31_ROM = '<path to decoded Kickstart 3.1 ROM>'
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~NativeSfs2RecoveryMatrix
```

## Native SFS2 file positions

`NativeSfs2FilePosition` runs the two DOS packet regressions from CopperSFS
research commit `4a902a1`. Use the Kickstart ROM and freshly built SFS2 TRST
handler variables from the recovery matrix above, then run:

```powershell
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter FullyQualifiedName~NativeSfs2FilePosition
```

The wide case requires a sparse-capable temporary directory. Its 136 GiB
logical HDF places a file extent at 128 GiB. The guest writes 1,027 bytes
starting three bytes below 4 GiB, checks both words of the resulting position
and file size, and checks that shrinking respects a second open handle's
position. After that handle closes, the guest shrinks the file. A fresh
Kickstart boot verifies the surviving 512 bytes and EOF without writing the
disk. Host inspection checks the affected extent, object, bitmap, free count
and clean journal marker without scanning the whole logical device.

The exhaustion case writes a 17-byte prefix, then submits a 256 KiB write
which must report NoFreeStore and preserve position 17. A 1 KiB retry on the
same handle must succeed, with the original prefix and exact EOF intact.
The host checks the persisted file hash and observes TRST journal writes.
