# Native sequential read optimization — 2026-10-25

The classic native handler now serves an aligned read directly from the extent
already stored in its file handle. The ordinary core path still discovers the
first extent, crosses extent boundaries, handles short or unaligned requests,
and supplies the fallback after seeks or invalidation. The embedded core also
uses its caller-owned cursor directly when a complete aligned request stays
inside the current extent. Mutations and media changes clear native cursors;
a failed direct read clears the cursor and reports only a completed prefix.

This does not change the 512-byte filesystem block size, MaxTransfer handling,
DMA bounce path, physical read requests, on-disk format, or mount memory use.
No prefetch buffer is allocated. A compiled 4 KiB native copy probe in
[the codec study](native-trst-codec.md) cost 41,666 guest cycles; copying a
1 MiB prefetched stream at that rate would cost roughly 10.7 million cycles.
That estimate, plus the existing direct multi-block read path, argues against
enabling read-ahead for the current 8 KiB packet workload without evidence of
substantial device command latency. It is an extrapolation, not a measured
read-ahead implementation.

Five cold trials per binary and workload used the same compiled MC68000 guest
probe, Kickstart 3.1 ROM, 16 MiB original-SFS-formatted fixture, and 8 KiB DOS
Read packets. The measured file has fragmented extents; 1 MiB was read in each
trial. The warm workload reads that MiB once before the timed pass. Times are
emulated guest CPU cycles; the device model adds no seek or rotation latency.

| 1 MiB read | Before median | After median | Cycle reduction | Sector reads | Device requests |
| --- | ---: | ---: | ---: | ---: | ---: |
| Cold | 2,907,140 | 2,348,170 | 19.2% | 2,048 → 2,048 | 128 → 128 |
| Warm | 2,879,900 | 2,300,129 | 20.1% | 2,048 → 2,048 | 128 → 128 |

The improvement comes from avoiding repeated cursor conversion and extent
traversal in the handler. Because the request count and sector count are the
same, adding an equal fixed latency to every device command leaves the cycle
difference unchanged. This benchmark does not establish performance on a
physical disk or on a differently fragmented file. The unchanged original SFS
control stayed within 0.03% across the two cold trial sets.

The compiled guest also checked the first 1 MiB against an independently
computed FNV-1a digest, then sought backward and read again. Focused native
shared-handle and resize checks passed: 3/3. Portable embedded-core tests
cover the direct path, short device reads, interleaved handles, backward seeks,
and cursor invalidation. The full public Release suite passed 378 tests with
five optional external-input skips; the full embedded suite passed 292 with
one optional skip. The focused CopperStart.Dos Release build passed.

The full CopperStart solution build is currently blocked in the separate
CopperOS Shell project by missing `LoadSegmentNative`, `UnloadSegmentNative`,
and `UnloadDosProcessSegment` methods. Those errors are outside the SFS read
path. No extension ROM or physical-device replay is claimed here.

Reproduction data and both handler binaries are under
`artifacts/native-read-ahead/`: `baseline/`, `fastpath2/`, and five individual
`trials-*/*.json` receipts per binary. These are local generated artifacts, not
release packages. The read probe itself does not checksum the timed workload;
the separate compiled correctness case checks its bytes so benchmark timing
remains comparable.

The before HUNK SHA-256 is
`E5295B612EA34101C2CCDA941C46D606AA14FD05A0458882258768C73454E5D4`;
the after HUNK is
`9BA53430AD49CE759B04E55638E54676EA2815AF96797D6D3BCEB6275936585C`.
The fixture SHA-256 is
`56F3F8A79AD643A28EED58213128FC618DEE4F43FAD9F9A5611CD699AFB600F0`.
