# Native TRST and bulk-allocation fixes — 2026-10-25

Follow-up: [indexed staging results](native-trst-index.md) remove the measured
lookup bottleneck. This page retains the preceding build's measurements and
binary identities as the comparison baseline.

The fixes proposed in the [instruction-level investigation](native-write-investigation.md) are implemented and qualified in the compiled classic handler. TRST improves by **3.7–5.3×**, and buffered CSUN's 64 KiB append throughput improves by **4.1×**. TRST still has a large CPU cost relative to original SFS; these results do not establish performance parity.

## Implementation and shared-source coordination

TRST copies literal runs in bounded spans up to each journal-page boundary. A successful commit writes and flushes the journal, writes and flushes TRFA, then applies the final block images already retained in the staging arena. It flushes home writes before clearing the marker to TROK and flushing again. Healthy commits no longer decode and replay their own journal. Mount recovery still validates the entire journal before applying it, including when recovery itself is interrupted. Legacy CSUN recovery remains enabled.

The per-volume redo arena remains bounded at **192 KiB**, with the existing refusal behavior for requests that exceed its capacity. Whole-write atomicity and the commit barriers remain intact. The native compiler uses a local integer pointer in the literal-copy loop because assignment to an APTR argument is unsupported.

When a native append cannot extend its preferred run, it now searches for another complete contiguous run before falling back to sector-at-a-time allocation. Aligned requests up to 64 KiB can allocate and write that run in bulk. Free-run search consumes bitmap words, computes map indices once, and excludes transaction/admin blocks, buffered checkpoint reservations, pending journal blocks and overwrite reservations. The preferred append path avoids repeated reservation scans when the reservation set is empty. Existing retirement and metadata-pressure protections remain in place.

Changes were coordinated with the SFS2, reservation and record-API tasks. The final snapshot contains their agreed shared handler changes, including the six final record-API bridge files. Classic CSEB/v12, StateBytes 2320 and the 256 KiB extension profile are retained. SFS2-specific files and its CSEC/v13 profile are unchanged; the SFS2 task's earlier qualification applies to its own snapshot, not automatically to these optimized binaries.

The public and embedded cores both contain the TRST and bitmap-scan fixes. The native fast-append implementation lives in CopperStart's embedded core; the public core has a different batched-write structure. The final audit verifies all **275** frozen source inputs against their corresponding live CopperStart files, with no differences.

## Qualification

Evidence is under `artifacts/native-write-fixes/`; the machine-readable index is [final-audit.json](../artifacts/native-write-fixes/final-audit.json). Native builds use the sibling CopperStart `artifacts/native-write-fixes/source-v4` snapshot, whose hashes are recorded in [source-v4.json](../artifacts/native-write-fixes/source-v4.json).

| Check | Result | Scope |
| --- | --- | --- |
| Final compiled TRST suite | 12 passed, no skips | Writer/recovery cuts, original SFS replay, legacy CSUN recovery, bounded-arena behavior, shared/concurrent handles and native A500 integration |
| Final compiled CSUN append cuts | 1 strict + 1 buffered passed | 64 KiB bulk allocation, Copper recovery followed by original SFS reading the clean volume |
| Embedded core suite | 264 passed, 1 optional skip | Final embedded algorithms, including blocked-continuation bulk allocation and every-I/O-cut tests |
| Public core full suite | 362 passed, 5 external-input skips | Precedes the final scan-loop index strength reduction; TRST code already final |
| Public core affected tests | 81 passed, no skips | Final scan-loop source: batch, reservation, relocation and metadata-pressure cases |
| Portable TRST original-handler test | 1 passed | Original SFS recovery of data and metadata journals |
| Final benchmark | 60 checked runs passed | Four policies × three workloads × five fresh trials |
| Final instruction profiles | 2 passed | CSUN bulk append and TRST healthy commit |
| CopperStart Release solution | Passed, 6 warnings, 0 errors | Full solution build |
| Classic extension ROM | Built, 262144 bytes | Build verification only; no new ROM/media replay qualification |

The TRST receipts cover **902 writer cut points plus 6 interrupted-recovery cut points**, grouped into **53 distinct cut images** and tested through **106 recovery boots** across Copper and original SFS. This includes 64 KiB and 128 KiB packets. Equivalent durable states are grouped; 908 covered points does not mean 908 separate boots. Strict CSUN covers 161 points in 9 images; buffered CSUN covers 152 points in 5 images. Their 28 combined boots include Copper recovery and subsequent original-handler validation. Original SFS cannot replay CSUN itself.

Crash claims assume atomic 512-byte sectors and reliable flushes in the emulated device. Every benchmark output passes deep image checking and exact-byte extraction, including the unchanged 2 MiB tail in the overwrite fixture. These checks qualify the listed HUNK binaries, not untested hardware or a different ROM build.

## Performance change

Durable throughput in KiB/s, including open, writes, close and final successful Inhibit:

| Workload | TRST before → after | Strict CSUN before → after | Buffered CSUN before → after | Original SFS after |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 4.10 → **19.47** | 67.53 → **70.71** | 450.66 → **504.25** | 516.88 |
| 64 KiB append | 4.05 → **15.10** | 194.47 → **605.24** | 428.62 → **1765.30** | 3249.31 |
| 8 KiB overwrite | 4.16 → **22.12** | 19.80 → **36.91** | 19.86 → **36.78** | 2419.13 |

Before values come from the [previous qualified benchmark](native-write-benchmark.md). The new snapshot also incorporates coordinated reservation and record-API changes, so this is a comparison of complete qualified builds, not an isolated single-change experiment. Original SFS was rerun with the same final probe; its small baseline variation is retained in the raw results.

All sixteen 64 KiB append packets now enter the bulk write path once and perform zero scalar `AllocateData` calls. The three previously slow allocation packets drop from roughly 664–693 ms to 70–93 ms. Buffered CSUN's 8 KiB append throughput is within about 2.4% of original SFS. CSUN overwrite maximum latency drops from roughly 5.3 s to 0.61 s, although median latency remains about 0.21 s.

TRST's 8 KiB and 64 KiB append read counts fall from 19,512 to 8,152 sectors and from 10,288 to 3,080 sectors respectively. Journal-read counters can include stale journal headers examined by allocation; the final instruction profile confirms **zero healthy entries into ReplayNative, NativePass, NativeByte or NativeRequired**. Journal-write volume remains largely unchanged because whole-write atomicity still journals file data.

One I/O tradeoff remains: applying retained arena images in staging order increases TRST append write-request counts from 1,033 to 1,351 for 8 KiB packets and from 168 to 197 for 64 KiB packets. Better home-write coalescing remains possible. The benchmark adds no seek, rotation or flush delay, so these results do not predict physical disk latency.

TRST's maximum sampled guest retention is **490.56 KiB**, including code, stack, caches and the 192 KiB arena; this is about 16.4 KiB above the earlier build. Shared code changes are included. Buffered CSUN samples 354.68 KiB for append and 418.68 KiB for overwrite; original SFS samples about 264.6 KiB. These are sampled retained memory figures, not transient allocation peaks.

## Remaining bottleneck

The final TRST profile attributes **46.23%** of measured cycles exclusively to `RedoPlatform.Slot`, 13.27% to `TrstEncode`, and 8.73% to `CopyBlock`. Repeated staging-arena lookup is now the largest measured cost. The implemented fixes remove byte-copy overhead and redundant healthy replay, but do not optimize this lookup. TRST remains especially slow with 64 KiB packets. A subsequent optimization should preserve bounded memory and repeat this binary-level qualification.

Original SFS has different guarantees: its metadata journal does not provide the same whole-data-write atomicity, and it can acknowledge pending writes. Buffered CSUN can also acknowledge pending writes; strict CSUN and TRST make successful Write packets durable. Final Inhibit brings all four to a clean checkpoint for the durable-throughput comparison. The existing TRST default is retained; no handler has been installed or deployed by this qualification.

## Reproduction and identities

Use `scripts/Test-NativeTrst.ps1` with explicit `-Handler`, `-NativeTestAssembly`, `-FixtureManifest`, `-Python`, `-LegacyHandler` and a fresh `-OutputDirectory`. Use `scripts/Measure-NativeWritePolicies.ps1` with the three explicit policy HUNK paths, the same assembly/fixture/Python, `-Trials 5` and another fresh output directory. `scripts/Summarize-NativeWritePolicies.py` produces the tables below. Run metadata, test receipts, raw measurements, compiler maps and profiles are retained alongside the audit.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| TRST HUNK | 143836 | `B3C8F857B3A046FE07C0BF2CD6814FF3480008388C0032D530F06D1DC8EAFCC1` |
| Strict CSUN HUNK | 150788 | `6B412E7A11B53844C4C1D84490E70AFA7F05AAA8D73C7A8B6CDEF94BD786004C` |
| Buffered CSUN HUNK | 151016 | `F8CECDF666290394CC2A6E9D34AF713F2B9E7BBFCF1DE93ADB319567CC802D17` |
| Classic extension ROM (build only) | 262144 | `88655061DF63F4ABC1AD6873296B1A8DCE5BC982A9E16C6579DC4C6167CD0DD8` |

Native test assembly SHA-256: `2BE620087EF337B616E45CC3C84F7417404D89D7F63FF2AEB1E51A2C583BB3FD`. The final audit binds benchmark results to these binaries. Generated artifacts and licensed media remain outside version control.

## Complete final benchmark


5 cold trials per cell, 60 checked runs. Each writes 1 MiB; every output passed amidisk deep checking and exact-byte extraction.

Rates and latencies use emulated PAL time (7,093,790 cycles/s), not host wall time. All binaries run on the same emulated 68020, 512 KiB chip + 512 KiB slow + 2 MiB fast RAM, 512-byte blocks and 32 buffers. The device adds no seek, rotation or flush delay. Boot and validation are excluded. Four independent machines run concurrently; timing comes from each guest's own cycle counter.

Each fresh machine mounts and verifies the seed Hello file before the measured interval. Append workloads create a 1 MiB file. The overwrite workload replaces the first 1 MiB of the fixture's 3 MiB file; this range fits within its first extent. Independent extraction also checks that its remaining 2 MiB are unchanged. The raw workload tag retains the seeded file's name, Fragmented; this is an existing-file overwrite measurement. Payload byte i is (i * 17 + 5) & 255, so results concern this nonzero-word pattern rather than zero-filled or varied-compressibility data.

## Throughput and Write latency

Durable throughput includes file open, all writes, Close and successful Inhibit. Service throughput uses only the summed DOS.Write intervals. Latency p50/p95 pools every Write call across trials (nearest-rank p95); all other figures are medians across trials. Fixed marker overhead is included.

| Workload | Handler | Durable KiB/s | Write service KiB/s | Write p50 ms | Write p95 ms | Write max ms | Final checkpoint ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 19.47 | 19.51 | 422.948 | 426.353 | 467.938 | 9.872 |
| append-8k | CSUN-strict | 70.71 | 71.22 | 111.732 | 113.392 | 150.763 | 8.183 |
| append-8k | CSUN-buffered | 504.25 | 561.72 | 12.432 | 15.478 | 84.191 | 147.643 |
| append-8k | Original | 516.88 | 533.00 | 14.695 | 16.921 | 25.859 | 35.224 |
| append-64k | TRST | 15.10 | 15.12 | 4810.801 | 4874.020 | 4874.020 | 7.344 |
| append-64k | CSUN-strict | 605.24 | 643.81 | 95.731 | 158.628 | 158.628 | 7.363 |
| append-64k | CSUN-buffered | 1765.30 | 2368.87 | 14.844 | 93.485 | 93.485 | 89.557 |
| append-64k | Original | 3249.31 | 3924.86 | 14.885 | 26.001 | 26.001 | 31.420 |
| existing-file-overwrite-8k | TRST | 22.12 | 22.13 | 358.887 | 364.883 | 367.162 | 7.343 |
| existing-file-overwrite-8k | CSUN-strict | 36.91 | 36.94 | 206.500 | 212.115 | 607.839 | 8.017 |
| existing-file-overwrite-8k | CSUN-buffered | 36.78 | 36.81 | 207.139 | 212.767 | 609.965 | 8.018 |
| existing-file-overwrite-8k | Original | 2419.13 | 2638.72 | 2.828 | 4.012 | 6.764 | 24.989 |

## Device and journal I/O per 1 MiB

Journal writes include TRST pages or CSUN descriptors plus raw saved old-block images. They exclude transaction markers and final home writes, which remain included in total writes. Journal reads follow observed sector roles and valid journal headers, including stale headers examined by the allocator. Counts describe requested sector transfers, not a controller's eventual write amplification.

| Workload | Handler | Total writes KiB | Journal writes KiB | Journal reads KiB | Total reads (sectors) | Flush commands | Write requests |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 2563.0 | 1152.5 | 499.0 | 8152 | 516 | 1351 |
| append-8k | CSUN-strict | 1925.5 | 515.0 | 0.0 | 3072 | 516 | 904 |
| append-8k | CSUN-buffered | 1035.5 | 7.0 | 0.0 | 8 | 4 | 133 |
| append-8k | Original | 1029.0 | 1.0 | 0.0 | 0 | 4 | 132 |
| append-64k | TRST | 2155.0 | 1080.5 | 436.5 | 3080 | 68 | 197 |
| append-64k | CSUN-strict | 1141.5 | 67.0 | 0.0 | 384 | 68 | 135 |
| append-64k | CSUN-buffered | 1035.5 | 7.0 | 0.0 | 8 | 4 | 26 |
| append-64k | Original | 1029.0 | 1.0 | 0.0 | 0 | 4 | 20 |
| existing-file-overwrite-8k | TRST | 2304.0 | 1088.0 | 0.0 | 4592 | 512 | 896 |
| existing-file-overwrite-8k | CSUN-strict | 3527.0 | 2176.0 | 1507.0 | 10096 | 924 | 1066 |
| existing-file-overwrite-8k | CSUN-buffered | 3527.0 | 2176.0 | 1507.0 | 10096 | 924 | 1066 |
| existing-file-overwrite-8k | Original | 1025.5 | 0.5 | 0.0 | 0 | 4 | 131 |

## Guest memory

KiB consumed relative to Exec.AvailMem(Any) immediately before loading the handler. This includes HUNK/code, process stack, DOS mount records, caches and handler buffers. Common probe buffers are already allocated at baseline. Samples are taken mounted, opened, after first/final Write, closed and inhibited. Maximum sampled retention is not an allocation high-water mark and can miss transient allocations. Inhibit does not unload the handler process/code.

| Workload | Handler | Mounted KiB | After first Write KiB | Maximum sampled KiB | After Inhibit KiB |
| --- | --- | ---: | ---: | ---: | ---: |
| append-8k | TRST | 298.30 | 490.56 | 490.56 | 490.30 |
| append-8k | CSUN-strict | 305.08 | 305.34 | 305.34 | 305.08 |
| append-8k | CSUN-buffered | 354.41 | 354.68 | 354.68 | 354.41 |
| append-8k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| append-64k | TRST | 298.30 | 490.56 | 490.56 | 490.30 |
| append-64k | CSUN-strict | 305.08 | 305.34 | 305.34 | 305.08 |
| append-64k | CSUN-buffered | 354.41 | 354.68 | 354.68 | 354.41 |
| append-64k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| existing-file-overwrite-8k | TRST | 298.30 | 490.56 | 490.56 | 490.30 |
| existing-file-overwrite-8k | CSUN-strict | 305.08 | 369.34 | 369.34 | 369.08 |
| existing-file-overwrite-8k | CSUN-buffered | 354.41 | 418.68 | 418.68 | 418.41 |
| existing-file-overwrite-8k | Original | 248.37 | 248.55 | 264.41 | 264.10 |

## Binary identity and interpretation

These results apply to the exact binaries listed below. Source snapshots and crash qualification must be matched to those hashes; this report does not infer qualification or ABI versions from handler names. Copper binaries target 68000; original SFS 1.279 requires 68020. All use identical guest hardware and filesystem fixtures.

TRST and strict CSUN make successful Write packets durable. Buffered CSUN and original SFS can acknowledge pending writes; their short Write-return latency is a different durability contract. The successful final Inhibit brings all four to a clean checkpoint. TRST journals file data for whole-write atomicity; original SFS's metadata journal has different data guarantees.

| Handler | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 143836 | `B3C8F857B3A046FE07C0BF2CD6814FF3480008388C0032D530F06D1DC8EAFCC1` |
| CSUN-strict | 150788 | `6B412E7A11B53844C4C1D84490E70AFA7F05AAA8D73C7A8B6CDEF94BD786004C` |
| CSUN-buffered | 151016 | `F8CECDF666290394CC2A6E9D34AF713F2B9E7BBFCF1DE93ADB319567CC802D17` |
| Original | 97984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

