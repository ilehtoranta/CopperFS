# Compiled native write-policy benchmark

Historical baseline: [the qualified fixes and rerun](native-write-fixes.md)
contain the newer results. Numbers and binary identities below describe the
earlier build and are retained for comparison.

Follow-up: [instruction-level investigation](native-write-investigation.md)
identifies TRST encoding/replay, CSUN free-space scans and bulk-append fallbacks
as major costs in these pinned binaries.

Measured on 2026-10-25. The current compiled TRST implementation is substantially
slower than the qualified CSUN baselines: **16.5× slower** for 8 KiB appends,
**48.0× slower** for 64 KiB appends, and **4.8× slower** for existing-file
8 KiB overwrites, compared with strict CSUN. TRST provides compatible recovery
and whole-write atomicity with a substantial CPU and RAM cost in this build.

For overwrites, TRST halves journal writes (1,088 versus 2,176 KiB per MiB)
and reduces flush commands (512 versus 924). Its median Write latency remains
higher: 1.923 seconds versus 0.210 seconds for strict CSUN. CSUN has rarer long
pauses in this workload: its maximum was 5.306 seconds versus TRST's 1.924 seconds.

TRST's sampled active memory is 474 KiB. On appends, strict CSUN uses 282 KiB,
buffered CSUN 331 KiB, and original SFS 265 KiB. The extra memory versus strict
CSUN closely matches TRST's 192 KiB arena. Overwrites raise CSUN memory usage
by 64 KiB for its scratch buffer. The arena remains allocated across Inhibit;
temporary inhibition does not tear down the handler state.

Larger packets reduce TRST flushes from 516 to 68 per MiB without improving
throughput, which stays near 4 KiB/s. CPU work and read traffic remain material
costs in this zero-added-device-latency model. These results describe the
current compiled implementations and this payload; they do not establish an
intrinsic speed limit of the TRST disk format. Benchmarking changed no native
handler build policy.

5 cold trials per cell, 60 checked runs. Each writes 1 MiB; every output passed amidisk deep checking and exact-byte extraction.

Rates and latencies use emulated PAL time (7,093,790 cycles/s), not host wall time. All binaries run on the same emulated 68020, 512 KiB chip + 512 KiB slow + 2 MiB fast RAM, 512-byte blocks and 32 buffers. The device adds no seek, rotation or flush delay. Boot and validation are excluded. Four independent machines run concurrently; timing comes from each guest's own cycle counter.

Each fresh machine mounts and verifies the seed Hello file before the measured interval. Append workloads create a 1 MiB file. The overwrite workload replaces the first 1 MiB of the fixture's 3 MiB file; this range fits within its first extent. Independent extraction also checks that its remaining 2 MiB are unchanged. The raw workload tag retains the seeded file's name, Fragmented; this is an existing-file overwrite measurement. Payload byte i is (i * 17 + 5) & 255, so results concern this nonzero-word pattern rather than zero-filled or varied-compressibility data.

## Throughput and Write latency

Durable throughput includes file open, all writes, Close and successful Inhibit. Service throughput uses only the summed DOS.Write intervals. Latency p50/p95 pools every Write call across trials (nearest-rank p95); all other figures are medians across trials. Fixed marker overhead is included.

| Workload | Handler | Durable KiB/s | Write service KiB/s | Write p50 ms | Write p95 ms | Write max ms | Final checkpoint ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 4.10 | 4.10 | 1947.335 | 1950.612 | 2155.710 | 6.129 |
| append-8k | CSUN-strict | 67.53 | 67.97 | 113.575 | 115.591 | 435.622 | 5.932 |
| append-8k | CSUN-buffered | 450.66 | 504.05 | 13.090 | 20.701 | 134.349 | 183.963 |
| append-8k | Original | 517.63 | 533.74 | 14.693 | 16.946 | 23.115 | 32.494 |
| append-64k | TRST | 4.05 | 4.06 | 15553.140 | 17022.384 | 17022.384 | 6.129 |
| append-64k | CSUN-strict | 194.47 | 198.10 | 91.307 | 2164.081 | 2164.081 | 5.932 |
| append-64k | CSUN-buffered | 428.62 | 457.39 | 15.462 | 693.144 | 693.144 | 95.614 |
| append-64k | Original | 3300.05 | 4038.32 | 14.852 | 23.253 | 23.253 | 31.585 |
| existing-file-overwrite-8k | TRST | 4.16 | 4.16 | 1922.763 | 1923.612 | 1923.698 | 8.854 |
| existing-file-overwrite-8k | CSUN-strict | 19.80 | 19.80 | 210.336 | 215.941 | 5305.687 | 6.574 |
| existing-file-overwrite-8k | CSUN-buffered | 19.86 | 19.86 | 211.188 | 216.715 | 5255.996 | 6.615 |
| existing-file-overwrite-8k | Original | 2447.57 | 2670.87 | 2.828 | 3.651 | 5.553 | 24.983 |

## Device and journal I/O per 1 MiB

Journal writes include TRST pages or CSUN descriptors plus raw saved old-block images. They exclude transaction markers and final home writes, which remain included in total writes. Journal reads follow observed sector roles and valid journal headers, including stale headers examined by the allocator. Counts describe requested sector transfers, not a controller's eventual write amplification.

| Workload | Handler | Total writes KiB | Journal writes KiB | Journal reads KiB | Total reads (sectors) | Flush commands | Write requests |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 2564.0 | 1152.5 | 4842.0 | 19512 | 516 | 1033 |
| append-8k | CSUN-strict | 1930.0 | 518.0 | 0.0 | 3072 | 516 | 907 |
| append-8k | CSUN-buffered | 1035.5 | 7.0 | 0.0 | 8 | 4 | 131 |
| append-8k | Original | 1029.0 | 1.0 | 0.0 | 0 | 4 | 132 |
| append-64k | TRST | 2156.5 | 1081.0 | 4195.0 | 10288 | 68 | 168 |
| append-64k | CSUN-strict | 1146.0 | 70.0 | 6.5 | 424 | 68 | 139 |
| append-64k | CSUN-buffered | 1035.5 | 7.0 | 0.0 | 8 | 4 | 30 |
| append-64k | Original | 1029.0 | 1.0 | 0.0 | 0 | 4 | 20 |
| existing-file-overwrite-8k | TRST | 2304.0 | 1088.0 | 2176.0 | 17384 | 512 | 896 |
| existing-file-overwrite-8k | CSUN-strict | 3527.0 | 2176.0 | 1507.0 | 10096 | 924 | 1066 |
| existing-file-overwrite-8k | CSUN-buffered | 3527.0 | 2176.0 | 1507.0 | 10096 | 924 | 1066 |
| existing-file-overwrite-8k | Original | 1025.5 | 0.5 | 0.0 | 0 | 4 | 131 |

## Guest memory

KiB consumed relative to Exec.AvailMem(Any) immediately before loading the handler. This includes HUNK/code, process stack, DOS mount records, caches and handler buffers. Common probe buffers are already allocated at baseline. Samples are taken mounted, opened, after first/final Write, closed and inhibited. Maximum sampled retention is not an allocation high-water mark and can miss transient allocations. Inhibit does not unload the handler process/code.

| Workload | Handler | Mounted KiB | After first Write KiB | Maximum sampled KiB | After Inhibit KiB |
| --- | --- | ---: | ---: | ---: | ---: |
| append-8k | TRST | 281.88 | 474.15 | 474.15 | 473.88 |
| append-8k | CSUN-strict | 281.71 | 281.98 | 281.98 | 281.71 |
| append-8k | CSUN-buffered | 331.03 | 331.30 | 331.30 | 331.03 |
| append-8k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| append-64k | TRST | 281.88 | 474.15 | 474.15 | 473.88 |
| append-64k | CSUN-strict | 281.71 | 281.98 | 281.98 | 281.71 |
| append-64k | CSUN-buffered | 331.03 | 331.30 | 331.30 | 331.03 |
| append-64k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| existing-file-overwrite-8k | TRST | 281.88 | 474.15 | 474.15 | 473.88 |
| existing-file-overwrite-8k | CSUN-strict | 281.71 | 345.98 | 345.98 | 345.71 |
| existing-file-overwrite-8k | CSUN-buffered | 331.03 | 395.30 | 395.30 | 395.03 |
| existing-file-overwrite-8k | Original | 248.37 | 248.55 | 264.41 | 264.10 |

## Binary identity and interpretation

TRST is the exact ABI-v8 default binary previously qualified with crash recovery. CSUN-strict and CSUN-buffered are the previously qualified ABI-v6 baselines; this compares retained native builds, not an experiment varying only the journal format. Copper binaries target 68000; original SFS 1.279 requires 68020. All use identical guest hardware and filesystem fixtures.

TRST and strict CSUN make successful Write packets durable. Buffered CSUN and original SFS can acknowledge pending writes; their short Write-return latency is a different durability contract. The successful final Inhibit brings all four to a clean checkpoint. TRST journals file data for whole-write atomicity; original SFS's metadata journal has different data guarantees.

| Handler | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 126660 | `6F9331C6BBA0D479A06282FED2AD3994B432FB57273D0077BA39F8C303204820` |
| CSUN-strict | 126328 | `32EFA0F0955B1507CCC1FD9984C4E814DA217E9AF77157F6A703ABECB8BA7F4B` |
| CSUN-buffered | 126536 | `CEE80B0F3D6716138EA8C1C1D2C29EF4130D96112DCE93DE98DA8F219E935A08` |
| Original | 97984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

## Reproduction and evidence

Build `CopperStart/tests/CopperStart.Sfs.Native.Tests` into a separate output
directory. Set `COPPERSHARP_KICKSTART31_ROM` to the licensed Kickstart 3.1 ROM.
Run [Measure-NativeWritePolicies.ps1](../scripts/Measure-NativeWritePolicies.ps1)
with the three Copper handler paths, native test assembly, pinned fixture
manifest, Python path, and a fresh output directory. Original SFS is selected
and hash-checked through the fixture manifest. Five trials are the default.
Then run [Summarize-NativeWritePolicies.py](../scripts/Summarize-NativeWritePolicies.py)
on `measurements.json` with `--output` pointing at the result directory.

Local evidence is retained in `artifacts/native-trst-benchmark/qualified/`:
all 60 raw trials, cycle distributions, I/O counts, memory snapshots, binary
and emulator hashes, the successful TRX, and generated summary/report files.
All five trials reproduced identical cycle totals within each measurement
cell. The overwrite region and extent layout are independently recorded in
`artifacts/native-trst-benchmark/fixture-layout.json`.

The benchmark and all six I/O/accounting tests passed with no skips. The full
CopperStart Release solution build passed with six existing warnings, using
isolated outputs to avoid another task's shared compiler file lock. This is
compiled native emulation coverage, separate from portable host timings and
physical hardware performance. Licensed media and compiled binaries remain
local artifacts.
