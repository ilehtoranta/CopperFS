# SFS2 validation after merging main into PR #11

The source merge is `2914862332613b47e02c416d1f71380584515010`, with
`main` at `161ffed1093c86b105a6681f58b78ee4a93690d3`. The merge brings in
the newer FFS and classic SFS changes and retains the SFS2 source and
build switches from the checkpoint.

The faster overwrite mode requires `-Sfs2 -OwnedSfs2 -Sfs2OriginalDataWrites`.
Existing payload writes can reach storage before metadata commit, so an
interruption can leave part of the payload overwritten. Metadata remains
journaled and device flushes still order publication. The default
`-Sfs2` profile still journals overwritten payload. Both profiles here
use `-AutoTrim`. The build switch is rejected without owned SFS2. The
native map includes the faster overwrite entry point only in the opt-in
binary.

| Profile | HUNK bytes | SHA256 |
|---|---:|---|
| Default | 232,840 | `AA96CFDE2242E3B81455A33D540F5853E84EB48BB256490F02C8DA53BAF460C7` |
| Opt-in | 250,488 | `F00B3810E78519E0A3CC9D59D49E8E7EE72711496CAF98B73AF9D0498BDFE7BC` |

CopperSharp `f07e8ec99` built both M68000 handlers through the normal
pipeline. The native compatibility checks report zero reachable managed
allocations. The merged default binary differs from the earlier checkpoint
binary, so recovery is exercised on the merged build.

## SmartFilesystem 1.279 measurements

The benchmark uses the same probe and three alternating trials per arm as
the [checkpoint](../2026-11-17/README.md). The emulated 68020 runs Kickstart
3.1 with 512 KiB Chip and 2 MiB Fast RAM on zero-latency virtual storage.
Throughput cases use a 128 KiB payload. Timing starts after WaitTOF and
includes close and explicit flush for writes. The cycle and throughput
gates are at most 110% and at least 90% of the reference, respectively.

| Buffer | 64 KiB operation | 1.279 cycles | Opt-in cycles | Cycle change | 1.279 KiB/s | Opt-in KiB/s |
|---|---|---:|---:|---:|---:|---:|
| Fast | Grow | 398,957 | 434,550 | +8.92% | 2,275.95 | 2,089.53 |
| Fast | Overwrite | 141,393 | 95,180 | -32.68% | 6,421.84 | 9,539.87 |
| Fast | Read | 53,537 | 52,130 | -2.63% | 16,960.43 | 17,418.09 |
| Chip | Grow | 401,260 | 434,660 | +8.32% | 2,262.88 | 2,089.00 |
| Chip | Overwrite | 165,010 | 95,260 | -42.27% | 5,502.73 | 9,531.86 |
| Chip | Read | 54,360 | 52,610 | -3.22% | 16,703.55 | 17,259.17 |

Fast RAM overwrite also passes against the original's fastest measured
two-write trial of 93,780 cycles and 9,682.29 KiB/s (+1.49% cycles and
-1.47% throughput). The opt-in candidate uses two writes and one flush in
every 64 KiB overwrite trial. The individual I/O counts and timings are in
the paired JSON samples.

Mixed mutation uses 9.71% more cycles in Fast RAM and 9.73% more in Chip
RAM. Mixed verification uses 1.20% and 1.12% more, respectively. The six-boot
handoff passes with matching file digests in both directions.

All nine acceptance cases pass. This includes all six throughput cases at
512 B, 4 KiB, and 64 KiB in both RAM classes, the two mixed workloads, and
the six-boot handoff. The 64 KiB Fast RAM growth phase is the closest packet
case to the cycle limit. Its throughput is 8.19% below the reference.

## Recovery and native checks

The merged default TRST handler passes overwrite, truncate, rename, delete,
create, and comment recovery. Each case exercises five distinct durable
device images. Both the merged handler and the 1.279 reference recover the
images and reopen the recovered filesystem. These runs use the emulator's
atomic sector and flush model.

The merged opt-in handler passes the five metadata recovery cases (truncate,
rename, delete, create, and comment). The default overwrite case exercises
the full-data recovery policy. The focused tests exercise the opt-in policy
of allowing partial payload writes after I/O failure. Per-case results and
handler hashes are recorded in [recovery.json](recovery.json).

The focused portable SFS2 tests pass 92 cases. CopperStart instruction
replay passes 234 cases with five external-fixture skips. FFS, classic SFS,
classic SFS with the shared pool, default SFS2, and opt-in SFS2 all rebuild
with zero reachable managed allocations.

The Release solution build passes with zero warnings and errors. The full
solution test run has 2,518 passed, nine fixture skips, and zero failures.
