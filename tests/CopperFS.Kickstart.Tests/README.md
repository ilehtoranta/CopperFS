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
