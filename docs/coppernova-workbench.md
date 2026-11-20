# CNP2 on Workbench 3.1

Workbench discovers the CNP0 partition through its RDB partition record and loads the MC68000 handler stored in the FSHD/LSEG chain. The guest uses ordinary DOS calls. The fixture boots from a private copy of the supplied Workbench disk and starts the probe from DF1 after LoadWB.

## Object lifetime and durability

CNP2 root version 2 opts a freshly formatted volume into reusable object IDs. The original `FormatPacked` overload continues to create root version 1. All non-root metadata pages retain version 1. The earlier qualified handler rejects both roots of a reusable-object volume.

Root bytes 76–79 retain the highest issued ID. Bytes 88–91 hold the head of a free list, with zero for the empty list. Each freed ID occupies a 24-byte type-12 leaf record with parent ID 1, zero length and extents, and its next free ID at offset 12. Deletion rejects live locks, open handles and nonempty directories. It admits the extent bitmaps, clears the object's comment, frees its data using the existing quarantine, removes its name and puts the ID on the free list. Creation takes the head before issuing a fresh ID. Each directory entry receives a new serial. The independent disk checker walks the free list, verifies every freed ID appears exactly once, and checks namespace reachability and data ownership.

Directory enumeration uses the FileInfoBlock disk key as a cursor over object IDs. Moves preserve the object's ID, reject an occupied destination and check directory ancestry before editing either parent. The caller supplies the handler's working storage. Files have up to eight extents, each directory has 192 bytes for name records, and volumes span 2–32 MiB. Comments share a 24-group pool.

Ordinary write replies may precede persistence. Successful flush, unmount and shutdown wait for pending data and inactive metadata, a device barrier, root publication and a second barrier. In-place file overwrites retain buffered semantics.

## Reproduction

Build CopperFS in Release and build the prototype with `build/Build-NovaPrototype.ps1`. The qualification program accepts the handler, a licensed Kickstart 3.1 ROM and a supplied Workbench 3.1 ADF or ZIP. Use a disposable output directory.

```powershell
dotnet $qualification workbench $handler $rom $workbench $outputDirectory
```

The first guest creates two directories, moves and enumerates a file, deletes the file and the empty source directory, reuses an ID for the replacement, writes 512 patterned bytes and flushes. The fixture drops the first machine at the acknowledgement marker, discards volatile device state and boots a new machine from the sectors retained at barriers. The new guest checks every byte and enumerates the destination to end of directory. The independent checker verifies the expected reused object ID, ownership and payload digest.

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
