# CopperNova packet handling and mixed-workload results

The isolated CNP2 handler finished ahead of Kickstart FFS 40.1 and SmartFilesystem 1.279 in both workloads. Each of the six trials per workload ran its frozen guest against a fresh 16 MiB image on the same emulated 68020 with Kickstart 3.1, 512 KiB Chip RAM, 2 MiB Fast RAM and 32 DOS buffers. The numbers are median end-to-end guest bus cycles. Lower is faster.

| Workload | CopperNova CNP2 | Kickstart FFS | SFS 1.279 | CopperNova lead over FFS |
|---|---:|---:|---:|---:|
| Mixed | 2,754,690 | 2,782,750 | 3,916,615 | 28,060 (1.01%) |
| Stream, 2 MiB | 365,867,095 | 367,443,035 | 369,874,085 | 1,575,940 (0.43%) |

Both CopperNova trials finished ahead of both FFS trials in each workload. Mixed ranges were 2,754,580–2,754,800 for CopperNova and 2,782,260–2,783,240 for FFS. All 12 guest runs and independent disk checks passed. A separate six-trial mixed rerun using the final rebuild confirmed the result, with medians of 2,754,685 for CopperNova, 2,782,770 for FFS and 3,931,335 for SFS. The final CopperNova images from both workloads were byte-for-byte identical to their counterparts from the previous packed prototype. The timed binary’s SHA-256 is `E33C8DFF83A2340E9C0EECA985C1654415E60A3A3B555870735C2DC9A0273C9B`. The [trial data, profiles and qualification receipts](../benchmarks/coppernova-prototype/2026-11-18-packet-handling.json) identify the guest, reference handlers, ROM and native sources.

## Where the mixed result changed

| Mixed phase | Previous CNP2 | Current CNP2 | FFS | SFS 1.279 |
|---|---:|---:|---:|---:|
| Mutation | 1,764,050 | 1,586,725 | 1,683,600 | 2,477,325 |
| Explicit flush | 154,555 | 108,200 | 20,460 | 168,020 |
| Remount | 107,865 | 86,390 | 108,975 | 215,205 |
| Read and verify | 1,025,660 | 973,375 | 969,715 | 1,056,065 |

CopperNova saved 297,440 mixed cycles against its preceding build. Its 96,875-cycle mutation lead and 22,585-cycle remount lead over FFS covered an 87,740-cycle flush deficit and a 3,660-cycle readback deficit. The mixed run still issued nine read commands, seven write commands and three device flushes over the whole run. The gains came with the same device command counts as the previous prototype.

Cold startup takes more work. On the same 512 KiB, no-Fast-RAM correctness guest, the handler used 5,758,140 scalar cycles at startup versus 2,892,462 previously. Most of the new startup time is in mount initialization. The workload totals begin after the initial mount.

The separate 68000 correctness guest uses instruction-level profiling on the handler task. Its scalar cycles measure a different quantity from end-to-end 68020 bus cycles.

| Handler phase | Previous scalar cycles | Current scalar cycles |
|---|---:|---:|
| Mutation | 632,884 | 378,930 |
| Flush | 266,262 | 160,832 |
| Remount | 153,412 | 91,830 |
| Read | 126,534 | 83,688 |

## Implementation

- Cache lookups use a byte per possible metadata address. The cache resets only occupied slots. Directory traversal carries its cursor and working pointers across up to four resident components before yielding. An I/O miss saves the exact continuation. Open files use the cached inode for seek, resize and transfer packets.
- Creation uses the packed root’s monotonic object count. Bitmap allocation scans full bytes as well as words. The resize path retains preflight and quarantine, and resumes explicitly when it yields between fragmented extents.
- The native adapter caches the geometry and DMA address limits at mount. Eligible transfers go directly to the device. Pending or split commands, incompatible DMA masks, bounce buffers, cancellation and deferred completions use the normal transport. A short transfer or device error fails the request.
- CRC-32C still covers the full 512-byte metadata sector. Position tables accelerate a fresh checksum. To publish a root or bitmap with a known, verified predecessor, the engine applies CRC differences from every 32-byte region it could have edited. It computes a full checksum when it lacks that predecessor. Remount rereads media and reuses a previous CRC validation only after an exact sector match.
- The fixed caller-owned arena grew from 98,304 to 115,200 bytes. The extra 16,896 bytes hold the CRC tables and are initialized in bounded batches during cold mount. The emitted HUNK contains a 642-byte full-sector CRC routine and a 638-byte delta CRC routine, both with zero call instructions.

The one-pass component-table experiment and the trial that read an aligned prefix and partial tail into one private buffer were discarded. The latter removed a read command but added a copy that made readback slower. A fragmented-file test caught a missing resume phase during development. The final handler passes that case and a new multi-window allocation, truncation and remount test.

## Durability and coverage

Ordinary write replies retain buffered semantics. A nonempty CNP2 flush persists pending data and inactive metadata, waits for the first device barrier, publishes the new root, and waits for the second barrier before replying. Successful unmount and shutdown drain pending work. In-place overwrites retain their existing power-loss semantics.

The .NET 10 Release solution built with zero warnings and errors. Its seven test assemblies recorded 2,769 passes, 10 skips and zero failures. Seven CPX instruction tests and three SFS image tests were skipped.

The focused host suite passed all 43 tests, including torn and reordered publication, short transfers, generation reuse, full-sector CRC comparison against an independent implementation, and changed retries. CopperStart passed all 169 filesystem-buffer and geometry instruction replays. An additional 12 MC68000 instruction cases exercised the final HUNK’s quick and deferred device operations, short and failed completions, cancellation, stale tokens, bounce buffers and transfer splitting.

Both full-ROM gates passed on a 68000 with 512 KiB Chip RAM and no Fast RAM. They observed zero filesystem-operation allocations, two shutdown barriers, an intact stack guard and a 768-byte peak on a 4 KiB handler stack. Independent checks verified the final ownership graph and post-shutdown payload. These results cover emulated devices. Physical-device timing and power-loss testing remain unmeasured.

## Reproduction

Build the isolated handler from this worktree. Set `$evidence` to the retained comparison inputs and `$rom` to the licensed Kickstart image. The benchmark wrapper checks the FFS and SFS handler hashes, SFS base image, guest program and ROM before running the trials.

```powershell
./build/Build-NovaPrototype.ps1 -ArtifactRoot artifacts/packet-core/final
$handler = 'artifacts/packet-core/final/CopperNovaPrototype'
foreach ($workload in 'mixed', 'stream') {
    ./build/Benchmark-NovaPrototype.ps1 -Packed -Workload $workload -EvidenceRoot $evidence -Rom $rom -Handler $handler -ArtifactRoot "artifacts/packet-core/comparison-$workload"
}
dotnet run --project tools/CopperNova.PacketIoReplay -c Release -p:TargetFrameworks=net10.0 -- $handler artifacts/packet-core/io-replay.json
```

The host-only packet replays use CopperStart’s existing HUNK loader and MC68000 test bus. With the study’s local receipts present, `node tools/studies/record_packet_handling.mjs` verifies the measurements, image hashes and qualification outputs and rebuilds the accompanying JSON.
