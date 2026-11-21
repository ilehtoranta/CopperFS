# CNP2 on Workbench 3.1

Workbench discovers the CNP0 partition through its RDB partition record and loads the MC68000 handler stored in the FSHD/LSEG chain. The guest uses ordinary DOS calls. The fixture boots from a private copy of the supplied Workbench disk and starts the probe from DF1 after LoadWB.

## Object lifetime and durability

CNP2 root version 2 opts a freshly formatted volume into reusable object IDs. The original `FormatPacked` overload continues to create root version 1. All non-root metadata pages retain version 1. The earlier qualified handler rejects both roots of a reusable-object volume.

Root bytes 76–79 retain the highest issued ID. Bytes 88–91 hold the head of a free list, with zero for the empty list. Each freed ID occupies a 24-byte type-12 leaf record with parent ID 1, zero length and extents, and its next free ID at offset 12. Deletion rejects live locks, open handles and nonempty directories. It admits the extent bitmaps, clears the object's comment, frees its data using the existing quarantine, removes its name and puts the ID on the free list. Creation takes the head before issuing a fresh ID. Each directory entry receives a new serial. The independent disk checker walks the free list, verifies every freed ID appears exactly once, and checks namespace reachability and data ownership.

Directory enumeration uses the FileInfoBlock disk key as a cursor over object IDs. Moves preserve the object's ID, reject an occupied destination and check directory ancestry before editing either parent. The caller supplies the handler's working storage. Files have up to eight extents and each directory has 192 bytes for name records. Comments share a 24-group pool.

Ordinary write replies may precede persistence. Successful flush, unmount and shutdown wait for pending data and inactive metadata, a device barrier, root publication and a second barrier. In-place file overwrites retain buffered semantics.

## Reproduction

Build CopperFS in Release and build the prototype with `build/Build-NovaPrototype.ps1`. The qualification program accepts the handler, a licensed Kickstart 3.1 ROM and a supplied Workbench 3.1 ADF or ZIP. Use a disposable output directory.

```powershell
dotnet $qualification workbench $handler $rom $workbench $outputDirectory
```

The first guest creates two directories, moves and enumerates a file, deletes the file and the empty source directory, reuses an ID for the replacement, writes 512 patterned bytes and flushes. The fixture drops the first machine at the acknowledgement marker, discards volatile device state and boots a new machine from the sectors retained at barriers. The new guest checks every byte and enumerates the destination to end of directory. The independent checker verifies the expected reused object ID, ownership and payload digest.

### TD64 and interrupted publication

`workbench-td64` takes the same arguments as `workbench`. It uses a sparse
hardfile and places the 16 MiB test partition 64 KiB before the 4 GiB boundary.
This puts the CNP2 roots below the boundary and file data above it.

The first boot cuts power immediately before the second Flush barrier.
The independent checker must recover the original empty namespace from the
first barrier's durable sectors. A new guest boots that image, repeats the
workload and must issue TD64 writes on both sides of the boundary before
acknowledging Flush. The final cold boot must read back the file using TD64
and leave the disk unchanged. The hook holds only sectors written since the
last barrier in memory. The checker reads the metadata prefix and streams
payload checks from their partition-relative offsets.

This gate covers high device offsets with the 2–32 MiB layout used by root
versions 1 and 2.

### Full-disk CNP2 allocation

`NovaImage.FormatPackedWide` creates a fresh CNP2 root version 3 partition.
Physical extent addresses use 32-bit sector numbers. The first extent is in
the file record. Fragmented files select an overflow page from a fixed pair
of sectors assigned to the object ID. The partition uses 512-byte sectors and
the wide layout can describe up to 4,294,967,295 of them, subject to the
controller and RDB geometry. The qualified fixture covers 4 GiB + 64 MiB.

Each bitmap describes 2,048 sectors. An allocation selector describes 2,048
bitmap windows, and the root selects the top page that identifies the
active selectors. Flush writes the inactive bitmap, selector, extent and
namespace pages, finishes the first barrier, publishes the alternate root
and finishes the second barrier. Each root selects exact copies, even
when an interrupted commit and its retry use the same generation. A new
allocation waits for pending frees to become durable before using them.

The mount still uses a 115,200-byte arena. The existing namespace and
signed file-size limits apply. A shrink or delete that exceeds the
metadata cache streams inactive bitmap pages, retains their selectors
and publishes everything with one root change before replying. A cut
before that publication leaves both the original file and its allocation
intact.

```powershell
dotnet $qualification workbench-full $handler $rom $workbench $emptyOutputDirectory
dotnet $qualification workbench-full-bench $handler $rom $workbench $otherEmptyOutputDirectory
dotnet $qualification workbench-full-retire $handler $rom $workbench $retireOutputDirectory
```

Both commands create a sparse RDB image with one partition occupying the
disk after the RDB reserve. The guest writes 2 MiB in 64 KiB DOS requests
starting two sectors below the 4 GiB device offset. A small interleaved
file forces the main file to use an overflow extent page. The 68000
recovery run cuts before each barrier and checks the empty namespace
before retrying. The 68020 benchmark runs normal interrupt timing.
Both cold-boot the acknowledged image, read above 4 GiB using TD64 and
check every payload byte and bitmap against the independent host checker.
The receipt distinguishes TD64 commands from the emulator's backing
sector operations.

The retirement replay creates a 48 MiB file on the same geometry. It
interrupts deletion before each barrier, verifies the original file and
allocation after each cut, then completes deletion and checks the
surviving neighbor on a cold boot.

`prepare-wide-cnp2 $handler $blankRdb` creates the same geometry for a
preview with the normal allocation cursor. `workbench-full-preview
$handler $rom $workbench $blankRdb $outputDirectory` boots and tests a
copy of that image.

#### Full-disk benchmark on 21 November 2026

Three emulated 68020 trials used Kickstart and Workbench 3.1, normal
interrupt timing, 512 KiB Chip RAM, 2 MiB Fast RAM and a 64 KiB Fast RAM
transfer buffer. All three trials reported the same emulated bus cycles.
The 2 MiB file and its interleaved neighbor used allocation groups 127
and 128.

| Phase | Bus cycles | TD64 reads | TD64 writes | Barriers |
|---|---:|---:|---:|---:|
| Mutation through explicit Flush | 10,315,090 | 8 | 44 | 2 |
| Explicit Flush portion | 245,860 | | | 2 |
| Cold readback after open | 1,515,790 | 36 | 0 | 0 |

TD64 counts include the boot that ran each phase. Readback cycles include
the guest's payload checks and exclude boot and file open.

The MC68000 recovery runs verified the old state after both cuts and
recovered the new state after acknowledgment. The 48 MiB deletion used
54 TD64 writes and two barriers. The cold boot confirmed that only the
interleaved 512-byte file remained allocated. Physical controllers and
media have not been qualified.

#### Paired single-group comparison

The November 21 comparison used the exact archived single-group and
multi-group handlers, both built with the same compiler. Every arm used
the 2 MiB payload, interleaved 512-byte file, explicit Flush and
independent cold checks. Both handlers loaded at the same Fast RAM code
address. The transfer buffer was 64 KiB in Fast RAM.

| Handler and placement | Ordinary elapsed cycles | Ordinary handler cycles | Synchronized elapsed cycles | Synchronized handler cycles |
|---|---:|---:|---:|---:|
| Single-group, 16 MiB below 4 GiB | 9,233,260 | 1,493,751 | 1,979,790 | 1,434,946 |
| Single-group, 16 MiB with payload above 4 GiB | 9,655,950 | 1,919,338 | 3,185,990 | 1,862,785 |
| Multi-group binary on the same 16 MiB v2 image below 4 GiB | 8,365,840 | 1,765,027 | 3,018,810 | 1,706,706 |
| Multi-group v3, spanning the equivalent allocation boundary at 2 GiB | 10,094,670 | 2,328,400 | 2,858,360 | 2,268,314 |
| Multi-group v3, spanning 4 GiB | 10,315,090 | 2,543,484 | 3,090,140 | 2,484,037 |

Each ordinary arm reproduced exactly in two uninstrumented trials and
one instruction trace. The synchronized arms began mutation at board
cycle 312,592,730, line 136 and color clock 168. A separate uninstrumented
run reproduced the synchronized result of each traced arm. Workbench
and interrupts remained active. With the second release phase, the
single-group low and multi-group high arms reproduced the same ordering.

In the ordinary low single-group versus high multi-group comparison,
1,049,733 of the 1,081,830 extra cycles were inside the handler. Kickstart
code on the Workbench task used 6,507,549 cycles in the old run and
6,503,616 in the new run. The generic transport accounts for 393,305
extra mutation cycles. Direct native I/O requires a partition entirely
below 4 GiB. On the wide layout, crossing 4 GiB adds 215,084 handler
cycles over the same layout crossing 2 GiB.

The bitmap bit test, transfer state machine, and arena and cache
lookups account for much of the rest. The old and new binaries both
made 4,005 calls to the bit test on the same v2 image. The emitted routine
went from 56,334 to 76,096 instructions in that arm. In the wide run,
the finder spent an additional 65,570 cycles versus the old single-group
run, and the three arena helpers `R`, `Meta` and `Ptr` added 206,845.
Flush increased by 39,960 elapsed cycles and 53,582 handler cycles.

The earlier wide binary that measured 4,355,670 cycles also ran in
10,038,280 cycles with the later probe. Its handler totals were
2,271,063 and 2,271,358 cycles respectively. Kickstart on the Workbench
task grew from 1,187,100 to 6,481,577 cycles as the boot and workload
timing changed. Under the later probe the final binary adds 272,126
handler cycles over that earlier binary, chiefly in the allocation
state machine, bitmap tests and cache accessors.

Use `workbench-compare` with `v2-low`, `v2-high`, `v3-mid` or
`v3-high`, followed by the handler, ROM, Workbench disk and a fresh
output directory. Set `NOVA_WORKBENCH_PROBE` to reuse a compiled probe
in ordinary runs. Set `NOVA_WORKBENCH_SYNC=1` and
`NOVA_WORKBENCH_RELEASE_OFFSET=62000` for the synchronized probe.
`NOVA_WORKBENCH_PROFILE=1` writes the separate instruction trace.

#### Direct TD64 and bitmap indexing

The native direct path now supplies both TD64 offset words, including
transfers that span 4 GiB. Splits, restricted DMA buffers and outstanding
requests continue through the common transport. Flush still issues
CMD_UPDATE at both publication barriers. Wide bitmap slots use an index
in the existing mount arena, and allocation examines aligned bitmap
words with byte handling at mixed and unaligned edges.

The five 68020 benchmark arms used the same probe and 64 KiB Fast RAM
buffer. Mutation began at cycle 312,592,730, raster line 136 and color
clock 168 in every arm. The values for the optimized wide build are
identical in the ordinary and instruction-traced executions of this
synchronized probe.

| Handler and placement | Mutation | Explicit Flush | Total bus cycles |
|---|---:|---:|---:|
| Old single-group, below 4 GiB | 1,774,970 | 204,820 | 1,979,790 |
| Old single-group, payload above 4 GiB | 2,953,850 | 232,140 | 3,185,990 |
| Previous multi-group, crossing 4 GiB | 2,829,040 | 261,100 | 3,090,140 |
| Optimized multi-group, crossing 2 GiB | 2,173,030 | 249,180 | 2,422,210 |
| Optimized multi-group, crossing 4 GiB | 2,173,030 | 249,250 | 2,422,280 |

Across 4 GiB, handler cycles fell from 2,484,037 to 1,146,213.
Write-transport cycles fell from 431,207 to 43,924. Bitmap-run and
individual bit-test cycles fell from 457,881 to 39,046 combined.
The cross-boundary run still issued 44 TD64 writes and two barriers.
The mount arena remains 115,200 bytes. The native HUNK grew by 772
bytes to 58,752 bytes.

#### Preview 3 comparison with the original handlers

The optimized HUNK ran the same benchmark probes as Kickstart FFS
40.1 and SmartFilesystem 1.279 on both SFS and SFS2 media. The four
arms used Kickstart 3.1, an emulated 68020, 512 KiB Chip RAM, 2 MiB Fast
RAM, 32 DOS buffers and 16 MiB images with 512-byte sectors. CopperNova
used root version 3. The SFS2 fixture retained its unrelated 1 MiB
seed file. Each arm had two trials with the second round in reverse
order. All sixteen trials passed guest readback and their on-disk
checks.

| Handler | Mixed through Flush | 2 MiB through Flush | 2 MiB remount |
|---|---:|---:|---:|
| CopperNova optimized | 1,768,720 | 1,695,440 | 104,030 |
| Kickstart FFS 40.1 | 1,686,265 | 2,113,620 | 114,895 |
| SmartFilesystem 1.279, SFS | 2,548,370 | 4,707,130 | 217,505 |
| SmartFilesystem 1.279, SFS2 | 2,557,445 | 4,747,420 | 215,015 |

Values are median emulated bus cycles. The stream probe writes
2 MiB in 64 KiB requests, overwrites two partial ranges, renames the
file and writes a second file before explicit Flush. It then remounts
and verifies the payload. The mixed probe uses a smaller file and
the same packet timing boundaries. The DOS buffer setting is shared;
the handlers manage their own internal memory.

For the 512 KiB Chip-only stack and allocation replay, set `NOVA_PACKED=1` and `NOVA_NAMESPACE=1` and run the existing qualification executable with the handler, ROM and output directory. The separate fragmented-file test covers the overflow and attribute paths.

## Qualification on 19 November 2026

The Workbench RDB session ran under Kickstart 3.1 on an emulated 68000 with 512 KiB Chip RAM and 2 MiB Fast RAM. The mutation boot issued five device writes and two barriers. The cold boot verified all 512 bytes and issued zero writes. The physical test is pending.

On the separate Chip-only 68000 replay, the namespace operations used a 115,200-byte startup arena, zero operation-time allocations and 768 bytes of a guarded 4 KiB stack. Dirty shutdown observed both barriers and the independent checker recovered the last buffered append. The ordinary and fragmented-file ROM tests also passed. The 12 native packet I/O cases and the 169 CopperStart filesystem instruction replays passed. An additional unfiltered CopperStart run was stopped and is excluded from these counts.

The .NET 10 Release solution built with zero warnings and errors. The solution suite recorded 2,905 passes, 10 skips and zero failures. All 48 CNP2 host tests passed on the final source.

The host tests recovered the old or new namespace across simulated lost, reordered and torn sector writes during delete and reuse. They confirmed reuse of the same eight data sectors after flush and remount, and completed 1,030 additional create/delete cycles with existing objects still reachable.

### Benchmark

Each cell gives median mutation plus flush / remount in emulated bus cycles across two trials. Lower is faster. The new handler runs on a fresh root-version-2 volume, and the previously qualified handler runs on root version 1. Original SFS 1.279 requires the 68020. All 28 supported trials passed their independent disk checks.

| CPU and workload | Workbench CNP2 | Prior CNP2 | Kickstart FFS 40.1 | SFS 1.279 |
|---|---:|---:|---:|---:|
| 68000 mixed | 3,966,825 / 154,065 | 3,983,805 / 134,435 | 4,021,020 / 176,565 | Requires 68020 |
| 68000 2 MiB stream | 2,266,335 / 137,150 | 2,261,640 / 136,425 | 3,619,140 / 179,030 | Requires 68020 |
| 68020 mixed | 1,682,565 / 105,605 | 1,680,165 / 86,540 | 1,703,580 / 108,980 | 2,650,665 / 218,815 |
| 68020 2 MiB stream | 1,425,740 / 91,690 | 1,434,975 / 86,685 | 2,131,100 / 114,995 | 4,747,000 / 215,010 |

Mixed remount took 19,630 more cycles on the 68000 and 19,065 more on the 68020 than the previous CNP2 handler. Both complete workloads, mutation plus flush, and remount remained faster than the original handlers on their supported machines. The matrix uses the established workload probes. To reproduce it, supply the previous qualified HUNK via the benchmark wrapper’s packed baseline option, and add the reusable-objects switch for the candidate.

The qualified handler SHA-256 is `34B39A69125EC0756023FC2ACCBA64FF8AB4D43EFEAAFD5A69F96AC0EF8F851F`. [Trial measurements, hashes and qualification receipts](../benchmarks/coppernova-prototype/2026-11-19-workbench-qualification.json)

## Physical test kit

```powershell
dotnet $qualification prepare-workbench $handler $workbench $emptyKitDirectory
dotnet $qualification check-workbench $capturedDiskImage
```

Preparation creates local boot and probe ADFs and a blank RDB image. The boot ADF contains licensed Workbench material. Use DF0 for the boot ADF and DF1 for the probe. The first guest prints “CNP2 flush acknowledged” and emits parallel byte 210 after ACTION_FLUSH returns. An independent power control can cut the designated test system and its storage at that point. The second boot prints “CNP2 cold readback passed” and emits byte 211. Capture the designated disposable disk for the host checker and record the controller, cache settings, medium, power-cut trigger and outcome. Software receipts cover emulation only.
