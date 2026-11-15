# CPX4 benchmark checkpoint, 15 November 2026

CPX4 still misses the requirement to beat original Kickstart FFS 40.1 and
SmartFilesystem 1.279 in both workloads. Optimization is paused. The default
development source is the qualified V7 checkpoint; V26 is a newer experimental
candidate, preserved as a [source patch](../experiments/cpx4-v26/changes.patch).

## Latest results

Lower emulated bus cycles are better. V7 and the reference handlers use two-trial
medians; V26 has one unprofiled screening trial per workload. Profile-enabled
runs are excluded from this table. Every listed trial passes the guest checks
and independent persisted-disk/payload verification.

| Handler | Mixed total | Streaming total |
|---|---:|---:|
| Original Kickstart FFS 40.1 | 2,782,595 | 367,442,910 |
| Original SFS 1.279 | 3,918,700 | 369,862,555 |
| CPX4 V7, default checkpoint | 7,880,655 | 376,135,445 |
| CPX4 V26, latest prototype | 6,859,910 | 374,415,580 |

V26 mixed cost is **2.47 times FFS and 1.75 times SFS**. Its streaming cost is
**1.90% above FFS and 1.23% above SFS**. Relative to V7, mixed total falls 12.95%
and streaming total falls 0.46%. These screening results do not qualify V26 for
promotion or establish the performance goal as achieved.

The machine is Kickstart 3.1 on an emulated 68020 with 512 KiB Chip RAM,
2 MiB Fast RAM, a 16 MiB disk, 512-byte sectors and 32 requested DOS buffers.
The CPX4 handler targets 68000. Mixed creates a directory, writes 8 KiB, performs
two partial overwrites, truncates twice to 6003 bytes, renames, writes a second
1 KiB file, flushes, remounts and verifies all data. Streaming starts with
32 writes of 64 KiB (2 MiB), retains the partial/namespace operations and verifies
the full 2 MiB after flush/remount. The device is RAM-backed; this measures
emulated CPU/bus work, not physical-drive throughput. CPX4's fixed internal arenas
are independent of the requested DOS-buffer count.

## Where the remaining time goes

The mixed phase breakdown locates the largest excess costs:

| Mixed phase | FFS median | SFS median | CPX4 V26 trial |
|---|---:|---:|---:|
| Mutation | 1,683,535 | 2,478,940 | 3,362,060 |
| Explicit flush | 20,440 | 161,230 | 961,020 |
| Remount | 108,975 | 218,880 | 1,158,970 |
| Read/verify | 969,645 | 1,059,650 | 1,377,860 |

CPX4 spends 2,119,990 cycles in flush/remount, versus 129,415 for FFS and
380,110 for SFS. That accounts for about half its total mixed gap to FFS.
Buffered replies remove repeated retirement from ordinary DOS writes, but final
publication and mount recovery still prepare, copy and authenticate metadata.
Retirement retains six ordered barriers: invalidate the old commit, persist the
staged images/descriptors, persist the commit, install home pages, then persist
each checkpoint. Explicit flush also drains the device. The mixed V26 run issues
eight device flushes, against two for FFS and four for SFS. These are observations
of the tested implementations, not an assertion of equal crash guarantees.

Earlier diagnostic profiles identify CRC arithmetic and whole-page metadata
movement as substantial CPU costs. V12 flush/remount attribution measured
780,092 CRC cycles in 37 calls out of 2,077,663 CPX cycles. V19's mixed copy
attribution measured 112,505 cycles in ReadStaged and 49,821 in PublishMetadata;
streaming measured 199,848 and 146,835 respectively. These are earlier-candidate
profiles, not exact V26 percentages. State-machine dispatch, allocation and
transport admission add work during mutation. A metadata-view prototype was
correct in focused tests but did not materially improve the full workloads.

V26 extracts CRC arithmetic into a private static kernel using a memory reference
and a compact input context. It preserves the original memory-provider interface.
The native-provider 23-page kernel probe falls from 302,287 to 271,105 cycles on
68020, but the full mixed workload improves only about 1% from V19. Faster CRC
alone leaves the metadata, publication, recovery and dispatcher costs in place.

Streaming total also needs care: V26 spends 367,460,270 of 374,415,580 cycles in
read/verification. The guest's byte-checking loop dominates this phase for all
handlers. V26 mutation is 4,508,030 cycles, versus 2,110,850 for FFS and 4,562,125
for SFS. Thus V26 narrowly beats SFS in streaming mutation alone, while flush,
remount and verification leave its complete workload slower than both references.
Neither whole-test percentages nor an isolated kernel improvement establish
filesystem throughput independently of that guest verification cost.

## Durability and coverage

Buffered durability remains guaranteed at successful ACTION_FLUSH, unmount/inhibit
and shutdown; earlier replies may precede persistence. Journal ordering, checksums,
disk format and packet ABI are preserved. The measurements do not prove that
this contract makes the performance target impossible.

| Coverage | Default V7 | Experimental V26 |
|---|---|---|
| CPX host | 484 passed | 484 passed |
| Compiled native | 74 passed | 7 stack/extended-core cases passed; full suite not run |
| Separate opted-in full ROM | 19 passed | Not run |
| Release solution | Build passed; 2,458 tests passed, 9 skipped across 8 suites | Not qualified as a replacement default |
| CopperStart native replay | 141 passed on existing geometry/buffer/transfer replay binary; adapters unchanged | No new replay gate |
| Physical media/controller | Not tested | Not tested |

The solution coverage above is the existing development-workspace run, which
included concurrent FFS/SFS and CPX3 work. It is not a clean-checkout claim about
the CPX-only commit. V7's 52 handler source hashes match its qualification
manifest. Its HUNK is 201,104 bytes; V26's is 201,756 bytes. Both report native
compatibility and zero managed allocation sites. One external compiler nullable
warning occurred during the Release solution build. Skips and unavailable gates
are not passes; V26 does not inherit V7's full-ROM coverage.

## Reproduction and retained evidence

The [machine-readable record](../benchmarks/cpx4/2026-11-15.json) preserves all
reference/V7 trials, the unprofiled V26 trials, phase timings, input/output hashes,
independent checks and explicit coverage limits. The [benchmark tool instructions](../tools/CopperCPX.Bench/README.md)
describe the required local licensed inputs and commands. The [V26 patch instructions](../experiments/cpx4-v26/README.md)
preserve its source separately from the default implementation.

Original local logs remain in ignored `artifacts/cpx4-scalar-redesign-20261115`
and `artifacts/cpx4-compact-redesign-20261115`. No ROMs, media, original-handler
binaries, compiled handlers, packages or publication outputs are included.
