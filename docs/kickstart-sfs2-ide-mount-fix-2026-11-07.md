# Original SFS2 IDE mount: benchmark setup correction

Original SFS 1.279 can mount and read through the unmodified A1200 ROM and Gayle
IDE implementation. The earlier driver-incompatibility conclusion was too broad:
the benchmark supplied a flat filesystem at device offset zero, selecting the
original handler's whole-device geometry path instead of its partition path.
This correction is entirely C# benchmark code. No ROM, original handler,
filesystem implementation, CPU implementation or emulated controller was patched.

## Reproduced cause

An optional external scalar trace observes real Exec `OpenDevice`, `DoIO` and
`SendIO`, their native return values, and bounded handler instruction sequences.
It reads guest memory without touching device registers or inserting guest code.
It is excluded from acceptance runs and the production engine.

The original handler computes the first sector from the supplied environment,
then tests whether it is zero. In the retained relocated 40.68 trace, the test
is at PC `0012B87C` and the following nonzero branch skips capacity probing.
With the old `LowCylinder=0`, the handler calls `TD_GETGEOMETRY`, receives -3,
clears the device size and refuses the mount with DOS error 225. The separate
probe's identical geometry error was real, but it did not establish that the
handler could not use a normal partition.

| Controlled setup | Native result |
|---|---|
| Flat filesystem, partition start zero | Whole-device geometry query fails; mount returns DOS 225 |
| Leading cylinder and nonzero mount start, stale SFS root ranges | Geometry query skipped; two root reads succeed, but mount returns DOS 225 |
| Leading cylinder, nonzero start and corrected root ranges | Mount succeeds; full 1 MiB original-handler read matches its expected hash |

Private diagnostic receipts are under `artifacts/storage-sfs-mount/`:
`diagnostic-before/`, `diagnostic-pointers/`, `diagnostic-partition/` and
`diagnostic-partition-roots/`. A separate `regression-flat/` negative control
uses the final probe and reproduces DOS 225 as a failed test. It is expected
negative evidence, not an accepted read trial. The former unavailable-result
predicate could also swallow the stale-root failure because it matched a
separate geometry probe and DOS error alone. That classification was removed
from the current ROM harness.

The final flat negative control and partitioned qualification use the identical
SFS probe image, SHA-256
`D55E936F2A9484646EB8BDE9BE7A02E98F10F4D3DB2DEF7BDEF9AC771BEB5BF5`.
The original handler is the previously pinned 1.279 binary, SHA-256
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
The 40.68 ROM identity remains
`6D43840D4099A74170EA0F0425B6257C3891EBCAA39C4D1840075A9AB22B5707`.
The unchanged engine and these matched input identities isolate the correction
to the media/mount setup rather than generated-code or emulation behavior.

## C# correction and protocol boundary

`StorageIdePartition.Create` constructs a host-side drive fixture with one
reserved 128 KiB cylinder and a 16 MiB filesystem at sector 256. The guest probe,
compiled from C# by CopperSharp68k, registers `LowCylinder=1` and
`HighCylinder=128`, keeping the existing two-surface, 128-sector logical mount
geometry. The ATA drive's four-head, 64-sector description has the same cylinder
size. This is a manual DOS mount, not an RDB auto-mount or a replacement driver.

For SFS2, both root blocks record the absolute byte interval
`[131072, 16908288)` and receive new checksums. Their relative block numbers,
extent pointers and payload remain unchanged. FFS bytes are copied unchanged
after the reserved cylinder. Host tests check both root ranges/checksums,
preserved relative block numbers and input bytes, and rejected invalid fixtures.

New manifests use `kickstart-storage-v2` and `IdeLayout=partitioned-v1`.
Measurements retain layout, first sector and partition sector count alongside
all existing hashes, device counts and memory placement. New mount failures
fail the workload; they cannot become unavailable read passes. The independent
geometry probe is disabled in acceptance runs. Diagnostics remain optional and
outside production engine execution. Both generated guest probes compile with
zero managed allocation sites. Filesystem code and its native binaries remain
the same frozen inputs as the earlier storage matrix.

Historical v1 raw records and reports remain immutable and revalidate unchanged.
The earlier configuration/validation documents link to this correction. Their
unavailable attempts describe the old fixture and must not be read as a general
Kickstart/SFS incompatibility. V1 flat-media rates and v2 partitioned-media rates
must not be pooled or presented as an optimization speedup.

## Full IDE rerun

The corrected 4 KiB qualification completes **72 of 72 read cases**, including
every original SFS2 arm on 39.106 and 40.68, with no unavailable attempts.

The full IDE run completes **864 validated read trials, 72 passing cases and
zero unavailable attempts** under `artifacts/storage-sfs-mount/ide-full-v2/`:

| Dimension | Selection |
|---|---|
| A1200 ROM | Kickstart 3.0 39.106 and 3.1 40.68 |
| RAM / caller buffer | 2 MiB Chip / Chip; plus 8 MiB Fast / Fast |
| Native backend | Gayle PIO through ROM `scsi.device`, CopperHDF absent |
| Synthetic drive | HDD and CF, existing timing presets |
| Filesystem | FFS, SFS2 CSUN and SFS2 TRST |
| Arms | Original, Copper cached, Copper payload bypass |
| Reads / payload totals | 64 B / 64 KiB; 512 B / 256 KiB; 4 KiB / 1 MiB; 64 KiB / 1 MiB |
| Trials | Three cold starts per workload |

All **192 original SFS2 trials** pass, including all **96 previously missing
40.68 trials**. Every read size has 216 validated trials across the matrix.
The run validates **499.5 MiB** of returned payload, correct Chip/Fast buffer
placement, native session PIO bytes and unchanged read-only media. All 72
matching 64-byte cached/bypass pairs retain identical payload-command counts.
No CopperHDF gateway is fitted to these machines.

The complete outputs retain all four sizes, three-trial ranges, input hashes,
timing parameters and transport/media/PIO counts:

- [Full IDE A/B report](../artifacts/storage-sfs-mount/ide-full-v2/report.md)
- [Medians and ranges](../artifacts/storage-sfs-mount/ide-full-v2/summary.csv)
- [864 raw trials](../artifacts/storage-sfs-mount/ide-full-v2/measurements.json)
- [Final coverage receipts](../artifacts/storage-sfs-mount/ide-full-v2/validation-receipts.json)

Selected **Kickstart 3.1, 4 KiB** end-to-end medians in KiB/s include cold
mount/open, a 1 MiB read and close. These compare matching v2 configurations:

| RAM | Drive | FS | Original | Copper cached | Copper bypass |
|---|---|---|---:|---:|---:|
| 2 MiB Chip | HDD | FFS | 333.85 | 395.84 | 326.28 |
| 2 MiB Chip | HDD | SFS2 CSUN | 404.86 | 385.20 | 357.68 |
| 2 MiB Chip | HDD | SFS2 TRST | 401.93 | 396.57 | 367.25 |
| 2 MiB Chip | CF | FFS | 518.20 | 495.48 | 483.54 |
| 2 MiB Chip | CF | SFS2 CSUN | 429.42 | 420.52 | 411.70 |
| 2 MiB Chip | CF | SFS2 TRST | 429.40 | 419.97 | 411.61 |
| 2 MiB Chip + 8 MiB Fast | HDD | FFS | 333.57 | 344.72 | 319.68 |
| 2 MiB Chip + 8 MiB Fast | HDD | SFS2 CSUN | 369.95 | 345.79 | 349.94 |
| 2 MiB Chip + 8 MiB Fast | HDD | SFS2 TRST | 369.93 | 344.58 | 349.29 |
| 2 MiB Chip + 8 MiB Fast | CF | FFS | 490.82 | 425.65 | 461.73 |
| 2 MiB Chip + 8 MiB Fast | CF | SFS2 CSUN | 389.10 | 366.72 | 393.28 |
| 2 MiB Chip + 8 MiB Fast | CF | SFS2 TRST | 389.03 | 366.55 | 393.13 |

For example, cached SFS2 reaches 97.8–97.9% of original throughput in the stock
CF 4 KiB configuration; expanded CF bypass reaches about 101.1%. HDD and other
read sizes differ, so these cells are not an overall performance result. No
filesystem performance optimization is attributed to this setup correction.

No physical A1200 timing claim is made. Native PIO instructions execute through
the unchanged Gayle wait-state model; AA-Gayle physical cycle accuracy remains
unverified. ROMs, keys, original packages and media are not published.

## Coverage

The Release solution build passes (one existing frozen-compiler nullable warning).
The full host Release suite passes **1,304 tests**, with **three external-media
tests skipped**: 18 Amiga integration, 266 FFS, 989 SFS and 31 storage-runner
tests. A separate native guest compilation check passes with no managed
allocation sites for both probes. Native filesystem
handlers are reused unchanged; no standalone handler source integration or
CopperStart replay implementation was modified by this benchmark setup fix.

Portable PDB receipts match 54 engine files, 12 harness files, four runner files
and two shared sources exactly. These source/binary receipts accompany the full
run. Host unit checks, generated native compilation and full-ROM read trials
are separate coverage categories.

Focused report checks on a private copy of the 72-case qualification reject
wrong partition bounds, altered payload hashes, unavailable original arms,
missing receipts, differing existing reports and reused execution directories.
The original records remain unchanged. Receipts are in
`artifacts/storage-sfs-mount/report-negative-fixtures/validation.json` and
`host-validation.json`. Both historical v1 reports also revalidate unchanged.
