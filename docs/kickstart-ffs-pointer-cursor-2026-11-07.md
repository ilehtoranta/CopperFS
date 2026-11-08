# FFS pointer scan on Kickstart, 7 November 2026

The remaining synchronous CopperHDF FFS 64 KiB read-loop cost was measured
against an exact pre-edit source snapshot, then against unmodified
FastFileSystem 40.1. This experiment uses the same frozen compiler, Kickstart
3.1 A500 ROM harness, read-only media and C# probe on both sides. It does not
model A1200 IDE latency; the separate [IDE matrix](kickstart-ide-read-optimization-2026-11-07.md)
already places FFS 64 KiB throughput near original-handler parity there.

## Change

`ContiguousPointers` now advances one APTR cursor through the reverse-order FFS
pointer table and reads adjacent entries at constant negative offsets. The
same cursor pattern was then applied independently to the FFS metadata
checksum, reading eight longwords per iteration. The existing bounds,
first-discontinuity return, checksum result, persistent traversal cursor,
payload cache, read-ahead and device requests are unchanged. These are C#
source changes. No CopperSharp68k compiler change was needed: the frozen
compiler lowered the pointer scanner from 240 to 186 bytes and the checksum
from 144 to 78 bytes, with no spills. The native compatibility report still
lists zero managed allocation sites.

## Controlled result

Two consecutive controlled comparisons each have identical source hashes
except `src/CopperFFS.Core/AmigaDiskFileSystemCore.cs`. The second comparison
was built from the first comparison's frozen source tree so concurrent native
adapter edits did not enter it. All arms use the same frozen compiler and SDK
metadata. Three fresh mounts per packet size validated 24/24 Kickstart workload
trials in each comparison, with identical payload/media hashes, read requests
and device bytes in every corresponding trial.

| Packet | Previous session KiB/s | Cursor session KiB/s | Previous read cycles | Cursor read cycles |
|---:|---:|---:|---:|---:|
| 64 B | 27.800 | 27.799 | 16,244,050 | 16,240,510 |
| 512 B | 207.125 | 207.230 | 8,661,990 | 8,672,320 |
| 4 KiB | 844.892 | 846.650 | 8,509,360 | 8,488,730 |
| 64 KiB | 8,208.327 | 8,395.794 | 798,540 | 772,970 |

The pointer scanner's 64 KiB session gain is 2.28%; the other changes are
small. Its paired instruction profile attributes 75,831 to 54,901 cycles to
`ContiguousPointers`, a 27.6% reduction. Total profiled 64 KiB read-loop
cycles fall from 806,970 to 785,790.

The independently isolated checksum change raises 64 KiB session throughput
from 8,394.339 to 8,519.770 KiB/s, another 1.49%. The paired checksum profile
falls from 66,700 to 53,447 exclusive cycles; total profiled read-loop cycles
fall from 785,750 to 774,670. The 64 B, 512 B and 4 KiB session rates change
from 27.797 / 207.267 / 846.863 to 27.757 / 206.965 / 847.732 KiB/s.
Those small changes do not establish a short-read improvement. `CopyBlock`
remains about 70,800 exclusive cycles at 64 KiB.

## Read-ahead copy regression found in the live tree

While qualifying the current tree, another native adapter edit grouped eight
loads before eight stores in `CopyReadAhead`. The frozen compiler made that
method 82 bytes with two spills. Restoring the previous interleaved C# loop
made it 30 bytes with no spills. A controlled single-file build changes the
4 KiB session rate from 726.015 to 848.643 KiB/s, with 44 requests in both
arms and identical payload hashes. The 64 B, 512 B and 64 KiB rates change
by less than 0.2%. The interleaved loop is retained in the live source.
An additional four-word-at-a-time C# copy variant emitted 126 bytes without
spills but reached only 774.061 KiB/s at 4 KiB, so it was rejected. Its
payload hashes still matched. The accepted source and both SFS2 native-build
source snapshots have matching hashes after restoring the interleaved loop.
An aligned `Exec.CopyMemQuick` variant passed native replay and payload checks
but reached 778.870 KiB/s at 4 KiB. It too was rejected. All three copy
variants were measured with otherwise identical frozen source hashes.

## Original handler comparison

Unmodified FastFileSystem 40.1 was rerun with the same ROM probe and media.
Median complete-session throughput is 35.826 / 242.029 / 1,822.883 /
11,231.432 KiB/s at 64 B / 512 B / 4 KiB / 64 KiB. The accepted live-tree
handler reaches 32.524 / 207.452 / 848.694 / 8,558.819 KiB/s, or 90.8% /
85.7% / 46.6% / 76.2% of original. The 64 B gain over the isolated FFS build
comes from a separate partial-sector native adapter change present in the
live tree, rather than the pointer/checksum edits. At 64 KiB, both handlers issue
71 reads and fetch 1,062,912 bytes during the read loop. This remaining gap
is CPU execution in the synchronous model, rather than extra device traffic.
For 4 KiB, the retained read-ahead payload copy still dominates the older
paired profile; changing that policy requires measuring the full IDE model.

Both current SFS2 standalone variants were rebuilt from the accepted shared
copy source. Their original-handler Kickstart A/B also passed all 48 workload
trials with matching payload and media hashes. CSUN reaches 23.963 / 164.151 /
781.816 / 15,146.040 KiB/s at the four packet sizes versus original SFS2's
23.681 / 163.245 / 744.940 / 18,252.276 KiB/s. TRST reaches 23.969 /
164.274 / 781.595 / 15,304.962 KiB/s versus 23.666 / 163.290 / 745.533 /
18,306.555 KiB/s. The 64 KiB synchronous model still favors the original
SFS2 handler; the previous A1200 IDE model placed both near parity.

## Verification and limits

The final Release solution build passed with zero warnings. The existing 267
FFS host tests passed with both cursor changes. The complete Release solution
test run passed 1,305 tests with three optional external-media cases skipped.
All 76 actual-handler instruction replays passed with the accepted FFS HUNK
and with each freshly rebuilt current SFS2 HUNK on 68000 and 68020.
CopperStart's two enabled full-ROM native OS replay cases passed.
The initial `--no-build` native test invocation used stale test binaries and
is not an acceptance result. The full-ROM A/B validates read-only payload
hashes and traffic; it is not a physical A1200 hard disk measurement.
Compiler inputs, source manifests, maps, HUNKs, compatibility receipts and
trial JSON remain in ignored private artifacts. No ROM, media or handler
binary is committed or published.
