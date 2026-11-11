# Largest-function size experiments, 2026-11-12

No additional production optimization from this pass is retained. Smaller
FFS and classic SFS candidates failed the existing performance limits. The
new large-write performance test and its independent image checks are retained.
The original-handler size target remains outstanding.

## Candidates and disposition

Complete freestanding MC68000 HUNK files, bytes:

| Candidate | Matching control | Candidate | Removed | Rejection evidence |
| --- | ---: | ---: | ---: | --- |
| FFS shared table-extension writer and cached pointer-table remainder | 55,232 | 54,884 | 348 | One-byte-read cycles +0.111% |
| FFS shared table-extension writer alone | 55,232 | 54,932 | 300 | Large-file verification cycles +0.273% |
| Classic SFS consolidated extent-tail updates | 142,484 | 141,688 | 796 | Large-file growth cycles +0.296% |
| Classic SFS tail updates and simpler ceiling divisions | 142,484 | 141,512 | 972 | Large verification +0.124%; small seeks +0.259% |
| Frozen FFS table sharing and single packet-object traversal | 55,476 | 55,348 | 128 | Zero-length-read cycles +0.150% |
| Frozen classic SFS tail updates, shared 32-bit division/remainder, and packet-object traversal | 143,132 | 142,192 | 940 | Large growth +0.205%; one-byte reads +0.701% |

Each percentage is the mean of three rotated baseline/candidate pairs.
An increase above 0.1% fails; the tolerance was not relaxed. Individual passing
workloads or earlier passing snapshots do not qualify a later candidate.
These timings establish measured differences, not their hardware-level cause.

Other discarded experiments included deriving remainders from existing
quotients, caching remainders without table sharing, and removing a redundant
direct-read condition. Smaller source or a smaller individual method did not
consistently produce a smaller executable or acceptable overall timings.

## Largest functions investigated

The starting maps put packet dispatch at 4,696 bytes for FFS and 6,596 bytes
for classic SFS. The FFS/OFS writers were 1,966 and 2,036 bytes. Classic SFS
relocation was 4,090 bytes, ordinary writing 2,974, resizing 2,102 and reading
1,816. The experiments targeted these functions and their repeated work.

The frozen classic candidate reduced relocation to 3,712 bytes, writing to
2,774, resizing to 1,800 and reading to 1,668. Those individual reductions
were still insufficient to pass all performance gates, so they were discarded.
No validation, journal operation, durability barrier or filesystem feature was
removed to achieve the candidate sizes.

## Retained coverage

`LargeWritesPreserveCyclesAndDiskOperations` supplements the existing mixed
workload and small-packet probes. It creates forty small files, grows a 96 KiB
file around another allocation, overwrites the file in twelve 8 KiB packets,
and verifies every byte. Growth, overwrite and verification are timed
separately after setup. OFS and FFS cross pointer-table boundaries; classic
SFS has an extent tree containing many records.

The test checks exact paired read/write/flush command counts and independently
walks persisted metadata and allocation maps. The FFS/OFS walker now makes its
original workload-specific file assertions optional; its structural, ownership,
checksum, bitmap and forward-link checks remain enabled. Existing callers keep
the original assertions by default.

Initial probe setup errors were corrected before the final comparisons. Their
failed logs remain in the ignored artifacts and are not filesystem failures.
The corrected benchmark also passed all three same-binary control cases
(OFS, FFS and classic SFS), including its timing and paired-I/O assertions.

## Coverage and reproducibility

- **Host:** Release solution builds and full solution test runs passed on the
  experimental snapshots. The later frozen run additionally covers the shared
  arithmetic candidate: 1,337 passed, three media-dependent cases skipped.
  After removing this pass's production edits, a fresh Release build and the
  Amiga/FFS host tests passed again (275 tests). Concurrent edits are outside
  this pass's qualification. TRX files are in the artifact logs.
- **Native:** CopperStart actual-HUNK instruction replays passed for the earlier
  experimental handlers: FFS 28, classic SFS 113, SFS2 CSUN 26 and SFS2 TRST 26.
  Each SFS2 run skipped one optional cache-summary case. These are candidate
  checks, not qualification of retained production changes. An additional
  exploratory private-entry packet-lookup harness did not return through its
  assumed calling convention and is not counted as passing native coverage.
- **Full ROM:** Kickstart 3.1 / emulated 68020 ran real DOS/Exec workloads with
  MC68000 handlers. Seven clean original/Copper handoff cases passed on the
  intermediate candidates. The paired performance gates failed as recorded
  above. This pass does not qualify other ROMs, physical devices, floppy DMA,
  hardware timings, or crash/torn-write behavior through a full ROM.

Concurrent edits changed the same FFS loops, SFS reader, trim implementation
and supporting arithmetic while this pass was running. They were preserved.
The final comparisons therefore use two frozen source trees and a copied
compiler, with matching concurrent changes in both arms. Their control sizes
are not directly comparable to the previous report's accepted binaries.
Only this pass's rejected production edits were removed from the shared tree.

Ignored evidence is under `artifacts/largest-code-20261112`: source snapshots,
input assemblies, HUNK files and maps, compiler copy, manifests, raw cycle
samples, `paired-summary.json`, provenance, logs and TRX files. Baseline and
candidate hashes accompany every completed performance comparison. No handler,
package, media image or ROM was published or added to version control.
