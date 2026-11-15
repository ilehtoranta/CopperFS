# CPX4 scalar CPU redesign, 2026-11-15

The performance target remains unmet and optimization is paused. The default
source is qualified variant 7. The [latest benchmark report](cpx4-benchmark-2026-11-15.md)
contains the V26 results, phase breakdown, coverage limits and reproducible evidence.

## Default development checkpoint

Private engine arithmetic uses UInt64 while public Wide fields and packet
layouts remain intact. Direct scalar expressions remove wrapper calls. Tree
seek retains Wide arguments to avoid a native register-allocation failure.
Rollback copies only active descriptors and restores the same bounded range.
Buffered writes retain durability at successful ACTION_FLUSH, unmount/inhibit
and shutdown. Data, journal publication and flush ordering are unchanged.

The canonical 68000 handler is 201,104 bytes, SHA-256
`D8E72F2C606B47F00C1EC2BF29C511BC554E49F6AEE3ABF33E38E013621447A3`.
All 52 handler source hashes match its qualification manifest. Compatibility
analysis reports zero managed allocation sites. The existing development-workspace
Release build passes with one external compiler nullable warning; its solution
run passes 2,458 tests with nine skips across eight suites, including concurrent
FFS/SFS and CPX3 work outside the CPX-only commit.

CPX host coverage is 484 passes, compiled native coverage is 74 passes, and the
separate opted-in full-ROM gate is 19 passes. CopperStart geometry/buffer/transfer
replays pass 141 cases on the existing replay binary; adapters are unchanged.
Physical media/controller tests are unavailable, not passes. New candidates do
not inherit this full-ROM qualification.

## Experimental screening results

All rows below pass both workload and independent persisted-payload checks.
The rows describe separate candidates, not successive promoted defaults.
Lower emulated bus cycles are better; candidate screening uses one trial.

| Candidate | Mixed total | Streaming total | Additional observed coverage |
|---|---:|---:|---|
| 8: inline state getters | 7,631,130 | 375,794,110 | 484 host; 74 native |
| 9: grouped checksum loops | 7,804,510 | 376,018,910 | Real-page CRC oracle |
| 10: stable arena addresses | 7,399,920 | 375,355,960 | 484 host; native qualification incomplete |
| 12: expand private metadata memory helpers | 7,061,280 | 374,774,890 | Diagnostic profiles |
| 15: nonrecursive quick transport | 7,327,830 | 375,240,570 | Transport differential checks |
| 19: split create planning, grouped CRC, quick transport | 6,931,840 | 374,492,480 | 484 host; 74 native; no new ROM gate |
| 21: unrolled copy and zero scans | 7,052,360 | 374,702,770 | Rejected: slower than 19 |
| 23: metadata ownership views | 6,936,670 | 374,492,860 | 484 host; 7 compiled stack/core; four allocation-size probes |
| 25: singleton metadata view | 6,932,480 | 374,480,340 | 7 compiled stack/core; four allocation-size probes |
| 26: private static CRC kernel on 19 | 6,859,910 | 374,415,580 | 484 host; 7 compiled stack/core; CRC oracle |

Candidate 19 avoids recursive compiler spill growth by separating create
validation and metadata application through 76 bytes of existing caller-owned
scratch. Earlier candidate 10/17 full native attempts failed in compiler
allocation; those failures are not filesystem execution passes.

Whole-page copy profiles motivated candidates 23 and 25. Read operations borrow
validated or staged images; dirty edits save undo before mutation and materialize
scratch before returning. Independent interleave, remount and caller-buffer
replacement probes pass for four allocation sizes. Their macro measurements
show no material improvement over candidate 19, so they remain unpromoted.

Candidate 26 preserves the original six-member memory interface and extracts
CRC arithmetic into a private static kernel with a memory reference and compact
input context. Matched HUNK probes cover 23 real metadata pages plus 16 boundary
inputs, both CPUs and three provider/mode combinations: 234 executions pass.
Native-provider real-page cost falls from 302,287 to 271,105 cycles on 68020
and from 1,251,996 to 1,135,594 on 68000. The full handler is 201,756 bytes,
native compatible and has zero managed allocation sites. Its full native suite
and full-ROM gate have not run. The [experimental patch](../experiments/cpx4-v26/README.md)
preserves its source separately from V7.

## Rejected paths and measurement limits

Restricted CILica with inlining produces a 278,072-byte handler that fails mixed
stage 17 with DOS error 213. Its no-inlining alternative validates but is slower.
The candidate-19 68020 build is 213,672 bytes and also fails stage 17/error 213;
no streaming pass is claimed for that binary. An optional default-interface CRC
extension faults with a plain provider in compiled native execution and is rejected.
The V26 patch contains none of these experiments or their diagnostic access hooks.

Advancing-pointer copy and CopyMemQuick probes did not establish useful overall
gains. The unrolled candidate 21 improves isolated kernels but slows both full
workloads relative to 19. Assembly-output kernels originally omitted generated
code-size passes used by freestanding HUNK handlers; corrected HUNK probes use
the production pass list. Global helper inlining also makes a kernel depend on
its surrounding module. Only unprofiled complete-handler measurements establish
the benchmark comparison; diagnostic attribution and kernel wins do not.

The initial scalar ROM attempt failed before boot because prototype paths were
noncanonical. That guard was retained; canonical V7 sources were then rebuilt
normally for the passing 19-case gate. No packages, ROMs or compiled binaries
are published by this development checkpoint.
