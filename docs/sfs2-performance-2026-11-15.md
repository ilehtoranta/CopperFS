# SFS2 performance and CILica inlining — 2026-11-15

## Latest completed acceptance measurement

The latest completed paired run is `append-codec-on-fast.log` under
`artifacts/sfs2-parity-20261115`. It passes four of five acceptance cases:
512-byte packets, 4 KiB packets, mixed operations, and clean six-boot
cross-handler handoff. The 64 KiB packet case fails only its growth phase.
These are emulated guest cycles and KiB/s (1 KiB = 1024 bytes), not host timing
or physical-drive throughput. Rates are calculated from the mean cycles.

| Packet | Operation | 1.279 cycles | Candidate cycles | Cycle change | 1.279 KiB/s | Candidate KiB/s |
|---|---|---:|---:|---:|---:|---:|
| 512 B | Grow | 28,925,000 | 20,040,727 | -30.71% | 31.39 | 45.31 |
| 512 B | Overwrite | 6,605,257 | 6,509,030 | -1.46% | 137.47 | 139.50 |
| 512 B | Read | 5,541,127 | 4,379,263 | -20.97% | 163.87 | 207.34 |
| 4 KiB | Grow | 3,639,703 | 2,635,437 | -27.59% | 249.47 | 344.54 |
| 4 KiB | Overwrite | 754,783 | 798,080 | +5.74% | 1,203.00 | 1,137.74 |
| 4 KiB | Read | 1,241,263 | 1,040,390 | -16.18% | 731.52 | 872.75 |
| 64 KiB | Grow | 405,157 | 462,040 | **+14.04%** | 2,241.12 | **1,965.21** |
| 64 KiB | Overwrite | 141,290 | 144,980 | +2.61% | 6,426.53 | 6,262.97 |
| 64 KiB | Read | 53,537 | 45,230 | -15.52% | 16,960.43 | 20,075.28 |

64 KiB growth is 12.31% slower in throughput. It must drop from 462,040 to
at most approximately 445,672 cycles (about 16,368 cycles, or 3.54% of current
cost) to meet the cycle gate. The independent throughput gate is at least
2,017.01 KiB/s. Mixed mutation is 15,590,110 versus 17,126,373 cycles
(+9.85%, narrowly passing); verification is 19,189,670 versus 19,409,500
(+1.15%, passing). Chip RAM cases have not been requalified on this binary.
The complete 10% goal remains open.

The measured profile is the opt-in owned SFS2 original-data-write profile,
with a restricted three-method codec CILica inlining experiment. The normal
build pipeline does not yet apply that codec rewrite. Its HUNK is 246,664
bytes with SHA256
`0413BA60AFBD3DDC4DC2D10E30C3EFDF104CA01D6BAA34711BB9B7F6A607C4F7`.
The original is SmartFilesystem 1.279 (23.12.2007), SHA256
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
The experiment uses CILica 0.20.2-preview revision
`ca199fd583f492e156f2617dff43b5f28ff8684d`, restricted to `Plan`,
`PlanSparse512`, and `SparseKind`; scalar replacement remains enabled,
little-endian assumptions and cross-assembly IPO are disabled. Frozen-input
composition scripts and optimizer sources remain in the local artifact tree.
This is experimental measurement provenance, not a claim that a normal
source build reproduces the measured HUNK.

Three alternating trials per arm run a 128 KiB payload on an emulated 68020,
512 KiB Chip plus 2 MiB Fast RAM, a 16 MiB synthetic SFS2 volume with 512-byte
sectors, and zero-latency virtual CopperHDF storage. Payload buffers are in
Fast RAM. Every throughput phase starts after WaitTOF outside the timed region; writes
include close and explicit flush. The guest clock is 7,093,790 Hz. Raw samples
and hashes are preserved in `benchmarks/sfs2/2026-11-15/` without media or ROMs.

The source checkpoint additionally includes interior extent insertion, lazy
empty overlays, and first-write before-image preflight/materialization added
after this measured binary. The latest focused host run passes 98 tests. These
later changes have no completed paired native performance measurement; the
table must not be attributed to the exact checkpoint source. The full
[checkpoint validation](filesystem-checkpoint-validation-2026-11-15.md) passes
2,508 solution tests (nine skipped), rebuilds all six native profiles, and
passes 234 native replay cases (five skipped). Earlier host,
native replay, and ROM results below describe their own snapshots.

Codec-only inlining saved about 1.5–1.85% in diagnostic growth instruction
profiles for 76 additional bytes versus codec inlining off. General adapter
inlining saved less than 1% while adding about 7% code, so it stays off by
default. Diagnostic profiles are not the acceptance averages above.

## Earlier experiments and qualification history

This optimization is in progress. The target is no more than 10% additional
guest cycles and at least 90% of SmartFilesystem 1.279's read/write throughput.
The complete target has not yet passed. No release default or package changes.

## Profiles

The existing SFS2 TRST write policy still journals overwritten file data.
The owned mount profile remains opt-in. These changes reduce redundant data
copies, permit capable backends to batch unaligned reads, borrow current payload
windows, retain windows across disjoint journal writes, reuse lookup scalars
after mutation, and reduce allocator/redo lookup overhead. DMA bounce paths still
invalidate the window before using its storage. Journal barriers remain intact.

An explicitly requested additional profile uses original-style payload writes:

```powershell
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -Sfs2OriginalDataWrites -AutoTrim -ArtifactRoot artifacts/sfs2-original-data
```

This profile writes existing file data before metadata commit. A failed packet
or power loss can leave a partially overwritten payload. Metadata remains
transactional. New allocations retain the existing dual-view proof; a reused
committed block with a private payload image continues to use full-data redo,
including subsequent overwrites of that image. This is a build-time opt-in,
not an on-disk format change, and does not become the default.

`-CilicaInlining` independently enables CILica inlining while keeping scalar
replacement and the native compiler passes enabled. Omit it to retain the
current no-inline default. It cannot be combined with `-SkipCilica` or `-Cpx4`.

## Completed inlining comparison

The two variants consume the same frozen managed inputs. CILica 0.20.2-preview,
revision `ca199fd583f492e156f2617dff43b5f28ff8684d`, retains scalar replacement
in both modes. The native target is 68000, executing on an emulated 68020 with
Kickstart 3.1, 512 KiB Chip and 2 MiB Fast RAM. Three alternating trials use the
same 16 MiB synthetic volume, DOS packets and explicitly Fast payload buffer.
Mutation includes the final flush. Read/verification includes guest checks.

| Inlining | Handler bytes | Mutation cycles | Read/verification cycles |
|---|---:|---:|---:|
| Off | 232,312 | 18,225,787 | 19,481,690 |
| On | 248,516 | 18,102,690 | 19,436,723 |

Inlining saves 0.68% mutation cycles and 0.23% verification cycles, with 6.98%
more handler bytes. Both variants remain outside the mutation target against
1.279. This supports retaining inlining as an experiment rather than changing
the default. These measurements precede the later allocator and input-path
changes, and precede the original-style data profile.

Off HUNK: `138A71949BEB6289F39495221A330B55BFC2FDAC10D450C7D9FCD4CC0E694BDD`.
On HUNK: `2F6985D9E68CE97EB216AE8D4747E980F013F26214180B4C64D399A3A2138CA3`.
Shared managed input: `4B490C649C55BED3B31492173A1945B55737FDEA09B2F6EF3D4FB6AE10063FF9`.
Original 1.279: `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

## Throughput and qualification

`Sfs2ReadWriteThroughputWithinTenPercent` transfers 128 KiB in 512-byte, 4 KiB
and 64 KiB DOS packets, separately in Fast and Chip payload RAM. Pattern setup,
buffer clearing and readback verification are outside timing. Writes include
close and explicit flush. Every image is independently inspected. Reports use
KiB/s at 7,093,790 emulated bus cycles/second; these are virtual zero-latency
CopperHDF results, not physical-device throughput.

The 64 KiB safe-profile overwrite exposed 17 MiB of physical read traffic from
payload-window invalidation. Range invalidation reduces it to 526 KiB without
changing journaling. The subsequent input-path change brings the 64 KiB read
case to 5.2% fewer cycles than 1.279. Safe-profile mixed mutation is still 14.3%
slower. Full-data overwrite remains substantially slower; the original trace
writes the payload once, whereas the full-data profile additionally journals it.

Completed checks so far:

- Host: Release solution build passed with zero warnings/errors. Focused read,
  owned allocation, fragmented overwrite and policy tests passed. The complete
  solution run completed with 2,427 passed and 9 skipped. That run predates the
  later original-data metadata no-op and unchanged-packet changes; it does not
  qualify those later edits. Focused tests and final qualification are separate.
- Native: CopperStart buffer/geometry/size replays passed 193 cases. Compiled
  copy, payload-window and arithmetic tests passed 14 cases on 68000/68020.
- Full ROM: the measured trials above passed their functional probes and image
  checks, but their performance assertions failed as stated. Qualification of
  the latest source and opt-in data policy is still in progress.

Retained local evidence is under ignored `artifacts/sfs2-parity-20261115`.
`qualification-off` and `qualification-on` contain the paired inlining samples;
`qualification-tuned` contains the subsequent safe-profile measurements.
Diagnostic instruction profiles are separate from uninstrumented acceptance
runs. ROMs and original handlers remain external inputs. Physical Amiga,
controller/SSD latency and real power-loss behavior are unqualified.

## Subsequent original-data measurements

The `bulk-original` 68000-targeted candidate
`DD31B864723588B76E327FF461F4BCD77D8B9F3C432EE280E3D853EBAC502FC0`
adds pending-image key scanning, bitmap-word allocation scanning, metadata no-op
elision and short-packet queue ownership. With the same probe for both arms,
three alternating 64 KiB Fast trials measured:

| Phase | Candidate KiB/s | Original KiB/s | Extra candidate cycles |
|---|---:|---:|---:|
| Growth | 1,552.39 | 2,324.60 | 49.74% |
| Overwrite | 4,749.89 | 8,603.69 | 81.13% |
| Read | 20,019.22 | 16,051.00 | -19.82% |

The mixed trace measured +10.561% mutation cycles and +1.070% verification
cycles. Functional probes and image checks passed; performance acceptance
failed. Evidence: `resume-bulk-original-throughput.log`,
`resume-bulk-original-mixed.log` and `resume-bulk-original/`.

The next source change attempts to retain the acknowledged metadata arena for
opt-in complete aligned overwrites contained in one checked extent. It still
checks the object, extent, protection, private images and mount state. Growth,
archive-bit changes and private payloads use the ordinary transaction trial.
Packet-local preparation is passed directly into that trial on fallback, with
no persistent file-description certificate. Its native performance and final
recovery qualification remain incomplete. A 68020-targeted variant uses the same
frozen managed inputs for a separate CPU-target comparison.

The `unchanged-original` 68000 build is 235,576 bytes, SHA-256
`53754DAAF869398CA71AD426B94F307E93B3DAF013C6FCF66FF38217F71CEE3F`.
The frozen-input 68020 build is 251,200 bytes, SHA-256
`57C59909D035B73593ABCBC1391C9805B71980252FF66151548AF91682C874E4`.
Both contain no reachable managed allocations. Each passed the six-boot clean
handoff test, including reading 1.279's output and having 1.279 read its output.
Each failed all six throughput acceptance cases because of write performance;
every read phase passed. Extra cycles against the paired original runs:

| Native target | Packet | Payload RAM | Growth | Overwrite | Read |
|---|---:|---|---:|---:|---:|
| 68000 | 512 B | Fast | 32.40% | 132.64% | -22.36% |
| 68000 | 512 B | Chip | 32.31% | 131.88% | -20.23% |
| 68000 | 4 KiB | Fast | 37.80% | 147.99% | -17.50% |
| 68000 | 4 KiB | Chip | 37.87% | 148.08% | -16.04% |
| 68000 | 64 KiB | Fast | 56.03% | 15.68% | -17.75% |
| 68000 | 64 KiB | Chip | 56.07% | 15.72% | -16.90% |
| 68020 | 512 B | Fast | 41.36% | 139.93% | -19.95% |
| 68020 | 512 B | Chip | 41.29% | 138.98% | -17.45% |
| 68020 | 4 KiB | Fast | 47.78% | 162.74% | -4.93% |
| 68020 | 4 KiB | Chip | 47.89% | 163.08% | -3.59% |
| 68020 | 64 KiB | Fast | 65.04% | 15.45% | -5.53% |
| 68020 | 64 KiB | Chip | 65.12% | 15.54% | -4.55% |

Mixed mutation/verification: 68000 +10.531%/+1.155%; 68020
+13.113%/+1.795%. The 68020 target does not provide a general improvement.
Use the individual paired samples, rather than comparing rates across different
probe binaries. Evidence is in `qualification-unchanged-original/` and
`qualification-unchanged-original-020/`. The separately instrumented 68000
overwrite profile reports 180,970 cycles and still identifies repeated object
lookup/validation as a significant cost.

The latest original-data host suite passed 10 cases, covering safe-default
isolation, original-data writes, failed writes and retries, reused committed
payloads, archive-bit updates, consecutive unchanged-arena packets followed by
metadata mutation, and failed empty-journal flush/recovery. The resumed Release
solution build passed with zero warnings/errors; its tests are pending. The
rebuilt native replay run passed 193 buffer/geometry/size cases. The default
full-data handler rebuilt successfully with no reachable managed allocations;
its full-ROM overwrite recovery matrix passed. These results precede the next
continuation experiment described below.

## Pending continuation experiment

The next source version adds a caller-owned 48-byte original-data continuation
certificate, stored in the existing native SFS2 handle tail. It caches a checked
whole extent only if that entire extent has no private payload images. Hits
require the same mount epoch, metadata revision, group and accepted arena, and
remain inside both file EOF and the checked extent. No metadata revision changes
on a data-only hit; the group stays pending and requires its ordinary flush.
Metadata edits, remount and flush expire the certificate. The native adapter
uses it only when a bounded flush timer exists; other cases use normal Mutate.
The default full-data profile does not use this path.

The frozen-source original-data and owned-state suite passed 44 cases, including
continuation over a different sector in the extent, metadata protection changes,
reused private payload rejection, and an old mount with otherwise identical
view/arena values. This run includes the defensive physical-range check. Native
continuation qualification remains pending. The ongoing solution test
run was built before this continuation change and must be identified accordingly.

The `cursor-range-original` build completed at 237,344 bytes with no reachable
managed allocations, SHA-256
`C1E23BA2280B54CBF4098CD4EB156F9AE808482938ABEBB56437893B0CA95228`.
Focused unaligned-phase Fast measurements (unchanged 128 KiB payload) report:

| Packet | Growth extra cycles | Overwrite extra cycles | Read extra cycles |
|---|---:|---:|---:|
| 512 B | 36.30% | -1.83% | -22.35% |
| 64 KiB | 58.55% | -13.99% | 17.30% |

The mixed trace remains outside the gate: +10.892% mutation and +1.212%
verification cycles. These performance failures remain recorded.

Profiling the apparent 64 KiB read regression shows almost identical handler
work: 26,053 versus 25,912 cycles for the preceding and continuation builds.
Kickstart work changes from 18,254 to 36,331 cycles. A separate read-start
diagnostic calls graphics.library WaitTOF outside timing for both arms. On that
probe, the same continuation binary reads at 20,066.41 KiB/s versus 16,939.34
KiB/s, using 15.58% fewer cycles. Growth still fails (+58.55%); overwrite passes
(-1.72%). This supports interrupt/task phase sensitivity in the short burst,
rather than slower handler read code. A second diagnostic aligns all three
phase starts, retaining the same payload, packets, close/flush requirements and
10% gate. It records separate directories and flags; it does not replace the
unaligned-phase evidence. The initial all-phase 64 KiB Fast run reports +53.06%
growth, -9.61% overwrite and -16.77% read cycles; growth still fails.

Optimizing the entire core assembly with restricted CILica (scalar replacement
on, inlining separately off/on) passes 50 focused host tests in both modes, but
both native compilations reject a stack-type merge in classic JournalSpace
(C68K0005). Those variants are unusable, not native passes. A separate frozen-input
experiment selects 112 metadata/read/codec helper methods. Both modes produce
allocation-free native handlers and pass 50 focused host cases; ROM performance
qualification is pending. These experimental tools remain in ignored artifacts,
and no default pipeline change has been made.

The next source change caches the immutable 64-bit mount start during Ready
checks and rejects empty/out-of-range continuation hints before deeper checks.
Its focused host suite passed 44 cases; its native build completed at 237,516
bytes. Its clean-handoff test passed. Fast all-phase timings still fail: growth
+33.92%/+39.05%/+51.56% for 512 B/4 KiB/64 KiB, and 4 KiB overwrite +12.50%.
The other overwrite/read phases pass. Mixed mutation is +10.621%, verification
+1.149%. Source snapshots
and experimental optimizer modes must be kept distinct when assessing coverage.

The selected 112-method CILica experiments subsequently failed full-ROM
execution in both modes (guest timeout after writes). Remaining runs were
stopped after those failures. Passing host tests and native compilation did not
qualify these binaries. They have not entered the build pipeline.

The resumed full solution test run completed: 2,448 passed, 9 skipped. It covers
the snapshot built before continuation changes, not the later source. A separate
frozen continuation SFS2 host run passed 223 cases with 2 skipped media-dependent
fixtures.

The next opt-in source version also retains checked file size/first scalars
after successful nonempty writes. This descriptor has the same mount/view/arena
lifetime as the extent hint, but cannot certify a direct data run. Empty writes
cannot assert that archive was cleared. Protection changes expire the proof.
Its focused suite passed 47 cases, including partial/full growth from an empty
file and zero-length archive handling. The manual-copy native build is 238,456
bytes; its ROM qualification is in progress.

An opt-in native copy experiment uses Exec CopyMemQuick for aligned,
non-overlapping spans of at least 512 bytes. Short/unaligned copies retain their
existing path. It allocates no additional memory and leaves the default profile
unchanged. That native build is 238,264 bytes; ROM qualification is pending.
Another small source change advances the clean-cache invalidation slot pointer
instead of recomputing each slot product. Its focused host checks pass 47 cases;
its native rebuild is pending.

Further results: the checked file-description version passed the separate SFS2
host run (226 passed, 2 skipped). Its Fast growth overhead is +25.79%/+31.55%/
+50.37% for 512 B/4 KiB/64 KiB. Mixed mutation is +10.039%, so it still fails the
strict 10% gate. The Exec-copy experiment regresses growth to +29.17%/+34.51%/
+54.28% and mixed mutation to +11.028%. Its paired growth profile takes 616,260
cycles versus 600,410; inclusive CopyBlock work increases from 103,907 to
115,243 cycles. Comparing the frozen adapter inputs identifies exactly one
changed method, CopyBlock. The Exec-copy source experiment has been reverted.

Current follow-up source changes reuse the validated object-record location
when updating file size, edit existing private extent images without a scratch
copy/copy-back, and reduce unchanged-run loop bookkeeping in the shared journal
codec. The codec preserves single reads and canonical wire bytes, tested with
every changed-word position. The focused suite passes 56 cases, including
rename invalidation and append after extent-tree splits. Corresponding native
builds/measurements are in progress; these changes are not yet fully qualified.

The completed `extent-original` native build is 239,772 bytes. The mixed trace
now passes (+9.201% mutation, +1.148% verification), as does the six-boot clean
handoff. Fast all-phase growth still fails at +17.10%/+24.11%/+40.84% for
512 B/4 KiB/64 KiB; 4 KiB overwrite also fails at +18.12%. Reads pass.

The next opt-in version maintains checksums incrementally for reviewed scalar
metadata edits, using the previously verified image. Arbitrary/dirty edits
retain full sealing. The `scalar-original` build is 239,524 bytes and passes
clean handoff and the mixed gate. Fast growth remains +15.07%/+20.75%/+39.03%;
4 KiB overwrite remains +12.97%. These are still acceptance failures.

The opt-in `retained-original` build (240,300 bytes) uses the existing synchronous
retained-write API for journal, home and marker images. It expires the owned
physical before-image before I/O; publication and all durability barriers remain
unchanged. Its focused suite passes 62 cases, including every append commit cut
with both platform paths. The 64 KiB growth instruction profile falls from
561,310 to 511,760 cycles. Its paired all-phase Fast growth is
+15.33%/+19.83%/+27.55%; 4 KiB overwrite is +14.26%. Read and the other overwrite
phases pass; clean handoff and mixed timing also pass.

An exact smaller-set cache invalidation scan produces `adaptive-original`
(240,408 bytes), composed from the same frozen adapter and a separately built
core. It reports +15.76%/+17.87%/+26.74% Fast growth, +14.94% 4 KiB overwrite,
with the remaining overwrite/read phases passing. Invalidation still uses many
hash lookups: cache slot counts include invalidated holes, and refills used to
append until reaching capacity. The current source reuses those holes without
resetting their revisions, preventing stale-handle revival.

The current experimental core also tracks a conservative mask of changed
16-byte regions in each private SFS2 image. Its RAM slot header grows from 16
to 20 bytes within the same caller-supplied budgets; no disk or packet field
changes. Reviewed scalar edits accumulate region bits, including the checksum.
Arbitrary stores/edits use the full mask. A certified predecessor clone may copy
only those regions; a sparse 512-byte journal planner emits exactly the full
planner's canonical codes. Other block sizes and untracked edits retain full
copy/planning. The minimum one-image scratch case remains supported. Generic
editable views preserve pins and advance revisions before mutation.

This mask and hole-reuse snapshot passes 86 focused host cases, including every
changed-word boundary, randomized masks, failed append retry, untracked metadata
edits, predecessor rejection, and stale handles after repeated invalidation.
Its broader SFS2 host run and native qualification are pending. The preceding
scalar/retained source passed 193 CopperStart native replay cases. A full
solution build succeeded with one warning in the external CopperSharp compiler;
its test run is still active and does not cover the later mask changes.

The separately rebuilt classic SFS handler (165,888 bytes) passes the six-boot
cross-handler handoff using the existing `artifacts/sfs-capacity/classic-base.hdf`
fixture. The rebuilt default-safe SFS2 handler is 238,592 bytes; its full-ROM
overwrite recovery matrix is running. Both builds precede the mask layout
change, so they do not qualify that later snapshot. No physical-media tests or
package/ROM publication have been performed. The performance goal remains open.
