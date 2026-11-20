# CopperNova append-range qualification, 16 November 2026

The append-range redesign is qualified within the coverage below and committed
as `148dac8307ca69d409c670d57da0dc53c1baa02d` on
`codex/coppernova-architecture`. The performance objective remains unmet.
The [machine-readable report](../benchmarks/cpx4/2026-11-16-append-range-qualification.json)
records the measurements, image/compiler hashes and qualification receipts.

## Resulting behavior

Repeated aligned appends reuse a proved free interval, capped at 2,048 units
inside one bitmap window and extent. The existing 64-byte capability storage
pins the owned metadata images. Completed writes advance the file immediately;
allocation bitmap, counters and range-ledger updates are materialized together
before another operation observes or allocates metadata and before persistence.
The unconsumed suffix remains unallocated. Fused lookup also avoids an unused
parent-record copy; standalone lookup retains its existing result.

The disk format, packet ABI, journal/publication ordering and native memory size
are unchanged. Native execution remains single-owner, bounded and allocation-free
after startup. Begin submits no I/O. Successful ACTION_FLUSH, unmount/inhibit and
shutdown guarantee durability; earlier replies may precede persistence.

## Qualification

- Release solution build: passed. The rebuilt 272,924-byte MC68000 handler is
  byte-identical to the measured A86 image; all 87 production source hashes match.
- Full Release solution: **2,726 passed, zero failed, ten skipped**. This comprises
  2,587 host cases (including 756 CPX cases) and 139 compiled native cases.
- Rebuilt CopperStart native replay: **141 passed**.
- Actual-HUNK ROM coverage: **three targeted gates passed**, using MC68000,
  512 KiB Chip, zero Fast RAM and a 4 KiB handler task stack. These are the
  legacy and CPX5 allocation/packet probes plus the new CPX5 append probe.
- The append ROM probe observes the range capability before explicit flush and
  before shutdown. It verifies the first appends after flush and remount, leaves
  later appends without an explicit client flush, then verifies all 40,960 bytes
  and complete ownership independently after shutdown. It observes two device
  barriers for explicit flush and two during shutdown, zero handler operation
  Exec allocations, an intact stack guard, a 1,092-byte overall stack peak and
  a 748-byte shutdown peak.

The seven default-skipped native ROM methods and three SFS external-media cases
are not counted as passes. The three targeted ROM executions above are reported
separately. Broader ROM/media/platform coverage and physical devices were not run.

Full builds and the solution suite used .NET 10.0.401. During the final test-only
extension, the neighboring compiler checkout acquired a .NET 11 target. That
test assembly was rebuilt against the saved .NET 10 dependency binaries, with
compiler/core/runtime hash equality checked against the previous test snapshot.
No compiler or SDK sources were edited. A clean build against the updated
neighboring checkout needs a compatible SDK or compiler checkout.

## Fresh benchmark results

Two-trial median emulated bus cycles; lower is better. The guest workloads,
runner, 68020 machine with 512 KiB Chip and 2 MiB Fast, 16 MiB images, and original
Kickstart FFS 40.1 and SFS 1.279 inputs are unchanged. Every trial passes the guest
checks and independent persisted payload/ownership checks.

| Workload | CopperNova A86 | Kickstart FFS 40.1 | SFS 1.279 |
| --- | ---: | ---: | ---: |
| Mixed | 4,708,080 | 2,782,770 | 3,931,335 |
| Streaming | 369,030,775 | 367,442,645 | 369,868,185 |

Mixed improves about 0.14% over the preceding qualified A83 result. It remains
about 69.2% slower than FFS and 19.8% slower than SFS. Streaming beats SFS but
still loses to FFS.

## Why mixed remains slower

The mixed workload writes 8 KiB once, performs 1,024- and 769-byte overwrites,
truncates twice, closes/renames, writes a second file, flushes, remounts and reads
the result. The profile records **zero range arms, leased append starts or leased
append completions**. There are no repeated appends over which to amortize the
new allocation proof. Eighteen empty retirement checks cost 2,196 nominal CPU
cycles in the diagnostic; those counts are distinct from the bus-cycle medians.

| Mixed phase | CopperNova A86 | FFS | SFS 1.279 |
| --- | ---: | ---: | ---: |
| Mutation | 2,387,235 | 1,683,555 | 2,489,380 |
| Flush | 498,765 | 20,460 | 161,275 |
| Remount | 530,820 | 108,975 | 218,815 |
| Read/verify | 1,291,260 | 969,780 | 1,061,865 |

CPX's mutation phase is already slightly cheaper than SFS here. Flush and remount
account for **649,495 of the 776,745-cycle total gap to SFS**, about 84%. The CPX
profile shows CRC sealing/checking, reserve validation and checkpoint-body
comparison in these phases. Reopening and verifying also incurs directory/object
traversal and metadata materialization. FFS has cheaper mutation and boundary
phases throughout this workload.

The remaining optimization work is cheaper metadata lookup, publication and
recovery while preserving the same checks and ordering. The append proof reduces
repeated write bookkeeping; it does not remove those metadata lifecycle costs.
