# CNP2 v6 random reads on Kickstart 2.04

The handler with SHA-256 `5EF275198761C7DD3A3A899785C093397B313997E968BEED3A372E5AAD1F3721` used one extra 512-byte device read and about 23,300 elapsed cycles per overflow page on the emulated 68020. The driver returned one payload sector per request at every fragmentation level.

| File extents | Mean overflow reads | Mean DOS Read cycles | Extra Read cycles vs. inline | Mean DOS Seek + Read cycles |
|---:|---:|---:|---:|---:|
| 8 | 0 | 23,471 | 0 | 40,369 |
| 64 | 3.5 | 105,427 | 81,955 | 121,634 |
| 1,024 | 63.5 | 1,500,448 | 1,476,976 | 1,516,195 |

Deltas use the unrounded means. Dividing the excess elapsed Read time by the observed extra reads gives 23,416 cycles per page at 64 extents and 23,259 at 1,024. These figures include the emulated device command, page verification, handler execution and normal interrupt activity.

Each layout used a fresh 16 MiB CNP2 partition. Alternating writes to two files produced contiguous runs of 128, 16 and one sector respectively. Independent on-disk verification checked the extent count and identical content hash of the 512 KiB target file. A fresh Kickstart 2.04 boot opened the target with a Fast RAM caller buffer, on an MC68020 with 512 KiB Chip and 2 MiB Fast RAM. Boot, open and payload verification were outside the timed intervals.

The probe read one aligned sector from each of 32 equal-size regions in a fixed permuted order. Complementary offsets balance the sample across the file. Three consecutive passes used the same offsets, with parallel-port markers around DOS Seek and Read. The I/O hook identified overflow reads by page type, physical block identity and file owner, and payload reads by sector contents. All seeks issued zero reads. Across 96 requests the 1,024-extent file issued 6,096 overflow reads and 96 payload reads. Each pass issued exactly 2,032 overflow reads. The deepest sampled request in that layout read 127 overflow pages and averaged 2,954,337 Read cycles across passes.

To repeat, build CopperNova.Qualification in Release and run:

```
dotnet CopperNova.Qualification.dll random-extent-reads <handler> <Kickstart-2.04-ROM> <output-directory>
```

The output files `8-reads.json`, `64-reads.json` and `1024-reads.json` record hashes, sector numbers, command counts, seek cycles and read cycles per sample. Physical-controller latency is outside this benchmark.

## Indexed overflow pages

The matched comparison used the same 512 KiB payload and byte-identical guest read probe (SHA-256 `BECC33FB997A38C185783A27348D41CF8AB82CB600C32A0C7A640DA5E8DD33AD`) for the previous handler and the indexed handler. Each arm made 96 reads per layout. The JSON probe hash identifies the complete boot floppy.

| Extents | Mean overflow reads, previous → indexed | Maximum, previous → indexed | Mean Read cycles, previous → indexed | Mean Seek + Read cycles, previous → indexed |
|---:|---:|---:|---:|---:|
| 8 | 0 → 0 | 0 → 0 | 23,471 → 23,568 | 40,369 → 40,406 |
| 64 | 3.500 → 0.875 | 7 → 1 | 105,464 → 46,886 | 121,632 → 63,259 |
| 1,024 | 63.500 → 2.6875 | 127 → 3 | 1,500,502 → 104,643 | 1,516,241 → 120,750 |

The 1,024-extent run issued 258 overflow reads across the three passes, down from 6,096. Both handlers issued one payload read per sample and zero device reads during Seek. The small inline case used 0.41% more Read cycles with the indexed binary.

Use `random-extent-reads` for the previous handler and `random-indexed-reads` for the indexed handler, each with its own output directory. The indexed gate checks the payload and the maximum number of physical overflow reads for each sampled sector.
