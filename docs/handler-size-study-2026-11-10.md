# Handler size experiment, 2026-11-10

This document records successive experiments. The FFS foreign-startup
behavior described in the historical sections is superseded by the
foreign-volume rejection follow-up at the end.

The qualified release candidate at filesystem revision `3ff1de9` uses the
pinned CopperSharp `e16a42f` pipeline with CILica inlining enabled. An isolated
experiment used CopperSharp `b5802468c`, whose CILica integration independently
controls scalar replacement and inlining. Its optimizer wrapper was locally
modified to pass `--inlining off`. The CILica reports state `EnableInlining:
false`, `EnableScalarReplacement: true`, and verification on. The experiment
is not the reproducibly pinned release toolchain.

| Handler | Qualified bytes | Experimental bytes | Change |
| --- | ---: | ---: | ---: |
| CopperFFS | 219116 | 187012 | -32104 (-14.7%) |
| CopperSFS classic | 213788 | 182116 | -31672 (-14.8%) |
| CopperSFS SFS2 TRST | 266148 | 229976 | -36172 (-13.6%) |

The paired FFS native instruction benchmark compared the actual qualified and
experimental HUNKs on 68000 and 68020. Median cycles increased by 2.0–27.7%
across 64-byte, 512-byte, 4 KiB and 64 KiB read packets. A separate filtered
Kickstart 3.1 full-ROM paired test passed both arms; median session bus cycles
increased by 12.3%, 4.1%, 10.3% and 10.1% respectively. The emulator used a
68020 with synchronous CopperHDF. This is not a physical disk measurement.

The qualified-baseline comparison changes compiler revision as well as CILica
settings. A same-compiler comparison using `b5802468c` with inlining on versus
off passed and showed a 2.1–28.6% cycle increase for the no-inlining FFS build
across the same eight CPU/packet combinations. The inline and no-inline FFS
binaries are 218876 and 187012 bytes. An earlier unfiltered FFS-only manifest
caused four unrelated SFS2 fixture failures; it is excluded from the accepted
filtered result.

The experimental SFS2 TRST HUNK passed 204 native replay cases. Against the
qualified TRST HUNK, the paired instruction benchmark used 2.2–13.8% more
cycles across the same CPU and packet sizes. That comparison also changes
compiler revision and is not a full-ROM performance result. The experimental
classic SFS HUNK passed 12 cache replay cases, but its performance and complete
compatibility were not qualified. The experimental host suite was stopped
before the long SFS suite completed; it is not a pass.

Neither experimental FFS nor SFS2 TRST meets the earlier no-performance-
regression requirement. The user subsequently selected no inlining with scalar
replacement despite that tradeoff. The previously qualified release candidate
still retains the original binaries until the new builds are qualified. It omits the
separate SFS2 CSUN writer; TRST retains recovery of outstanding CopperSFS CSUN
journals on writable media. Existing published packages are unchanged. The
candidate archive and per-file SHA-256 list are under
`artifacts/release-preparation/`.

## Generated code profile

The following approximate symbol totals come from the experimental no-inline
MC68000 linker maps. Symbols can overlap or alias, so they are not an exact
partition of file bytes. The maps attribute 182290 bytes to 577 FFS symbols,
177598 to 562 classic SFS symbols, and 224580 to 690 SFS2 symbols.

| Emitted class | FFS bytes | Classic SFS bytes | SFS2 bytes |
| --- | ---: | ---: | ---: |
| Classic SFS write core | 76496 | 76296 | 64704 |
| SFS2 write core | not present | not present | 34998 |
| Shared packet filesystem | 25358 | 24106 | 26740 |
| FFS write core | 16754 | 16664 | 16928 |
| Native platform | 12800 | 10780 | 12496 |
| Classic SFS core | 10368 | 10312 | 10336 |
| FFS read core | 9010 | 8976 | 9040 |

The largest individual symbol is general packet dispatch (8006–8182 bytes),
followed by classic SFS overwrite relocation (4052–4116 bytes). The standalone
entry tests the filesystem type at runtime and delegates to shared packet and
mount logic, so these map entries show the compiler retained cross-format
paths. The FFS entry checks for DOS0/DOS1 after `CreateStartup`, but this does not by
itself prove that deleting every SFS path is safe. `COPPERSTART_FFS_ONLY`
already gates some code, but the standalone project does not select it today.
Profile-specialized builds are the strongest size lead; first prove that they
preserve packet behavior, existing FFS variants, error handling and SFS2 CSUN
recovery, then rerun native and full-ROM qualification and performance tests.
No byte-saving estimate is a promise until those builds are implemented.

The experimental SFS2 TRST handler subsequently passed the two Kickstart 3.1
cross-recovery cases, including recovery of a dirty CSUN image written by the
previously qualified CSUN handler. The final build must match the experimental
handler hash before that result applies to it.

## Selected no-inline build verification

The standalone build now uses CopperSharp `b5802468c6ebb68fceeb0403bad3c02eb09678b5`.
It checks that the final optimizer report has inlining disabled and scalar
replacement enabled. Final FFS, classic SFS and SFS2 TRST HUNKs matched the
experimental hashes byte for byte. The Release host suite passed 1418 with
three external-image skips. The direct native handler suite passed 204 and
CopperStart's native replay suite passed 143. Kickstart 3.1 original-handler
comparison passed 11 enabled cases with 12 unrelated cases skipped. Two SFS2
cross-recovery cases passed, including dirty CSUN recovery by the selected
TRST HUNK. The TRST crash-recovery and file-position suite passed eight cases
with no skips.

## Experimental FFS-only compile guard

The opt-in `-Ffs -FfsOnly` build enables the existing
`COPPERSTART_FFS_ONLY` source guards. It does not change the default build.
With the selected no-inline CILica profile, the HUNK fell from 187012 to 183700
bytes (3312 bytes, 1.8%). Its map still attributes about 76450 bytes to the
classic SFS write core: the guard alone does not remove the cross-format graph.

The experimental binary passed 204 direct native replay cases, and 131
CopperStart source-consumer replays passed. A paired Kickstart 3.1 read test
passed both variants: median session cycles changed by -1.37%, +0.04%, +0.19%
and +0.52% for 64-byte, 512-byte, 4 KiB and 64 KiB packets respectively. The
last three are measured increases, not proof of identical performance. This test
uses synchronous emulated CopperHDF and does not establish physical performance.

The original-handler Kickstart 3.1 comparison passed 11 enabled cases; 12
unrelated cases skipped. The Release host suite passed 1418 cases; three
optional external-image cases skipped.
Do not treat the opt-in profile as a qualified replacement until those complete
and foreign-format startup/error paths are specifically checked. Full
specialization of SFS and SFS2 has not been implemented.
The guest buffer addresses differed by 512 bytes between the paired HUNKs;
the device request counts matched. The small timing differences therefore
need a controlled placement follow-up before attributing them to code changes.

The FFS standalone entry calls `CreateStartup` before checking the mounted
DOS type. That shared startup detects a smart filesystem and can call
`MountSmart`, including its recovery path, before the entry rejects a foreign
type. Removing this path or moving the check earlier can therefore change
startup errors, disk I/O and recovery ordering. A safe specialization needs an
explicit foreign-format test that compares both the reply and device effects;
valid FFS workloads alone do not establish equivalent behavior.

## Controlled FFS comparison and startup tests

The paired Kickstart sequential-read probe now aligns its guest read buffer to
64 KiB. The benchmark wrapper rejects trials unless both arms use the same
address. Newly generated storage-matrix manifests also require matching aligned
addresses; a two-trial legacy matrix smoke test passed. Historical manifests
retain their previous reporting rules. In the
FFS-only experiment both arms used `0x00220000`: median session cycles changed
by -1.40%, +0.00% (50 cycles), +0.32% and +0.20% for 64-byte, 512-byte, 4 KiB
and 64 KiB packets. All two benchmark cases passed. The measured increases are
not proof of performance parity, so the opt-in profile is not the default.

The new full-ROM startup comparisons record the DOS startup result, returned
error, ordered sector read/write/flush trace, and resulting media hash. Five
clean foreign-format combinations passed comparing the unspecialized baseline
and the FFS-only candidate (SFS and SFS2 are unchanged controls). Three dirty
CSUN cases passed: FFS and classic SFS rejected without writes; SFS2 TRST
actually recovered the journal, with writes and flushes, and left a clean marker.
The baseline and candidate operation traces and disk hashes matched in each
case. These cases do not cover every invalid media or interrupted recovery state.

The FFS-only option remains experimental. It leaves the shared classic SFS
writer reachable; SFS and SFS2 specialization are not implemented, and no new
profile has been made a release default.

The final startup matrix passed ten cases: five clean foreign-format cases,
three dirty-SFS2 cases and two dirty-classic-SFS cases. In particular, the
baseline FFS handler recovered the dirty classic TRST journal (two writes and
two flushes in this fixture) before rejecting the foreign volume; the FFS-only
experimental handler matched its ordered device trace, reply and final image.
This behavior is why bypassing SFS startup to eliminate code would not preserve
current native ordering. The dirty-journal fixtures cover captured publication
points rather than every possible interrupted transaction.

## Direct FFS recovery experiment

The opt-in FFS-only profile now routes classic recovery directly to the
recovery core instead of through the generic standalone mutation switch. It
preserves journal cursor updates and the recovery error result. The generic
switch remains reachable from other shared operations; its presence means
this is not yet full specialization. The HUNK is 183972 bytes, 272 bytes
larger than the earlier FFS-only experiment, and still attributes approximately
76446 symbol bytes to the classic SFS writer.

The selected handler passed 204 direct native replays, 131 CopperStart
source-consumer replays, and all ten full-ROM clean/dirty startup comparisons.
The classic dirty fixture exercises actual recovery writes and flushes before
FFS rejects the foreign volume. The address-controlled Kickstart read comparison
passed both arms using `0x00220000`; median session changes relative to the
no-inline baseline were -1.56%, -0.10%, +0.24% and +0.14%. These measured
increases mean performance parity has not yet been established. The
original-handler comparison passed 11 enabled cases; 15 unrelated cases
skipped.

At this stage the SFS and SFS2 specializations had not yet been implemented.
None of the experimental format profiles is a default.

## Narrower FFS standalone dispatch

The experimental FFS-only build now handles the startup root, mount, usage
count and flush operations without entering the generic extension dispatcher.
Foreign classic-SFS recovery still runs before startup type rejection. The
standalone HUNK is 183216 bytes: 3796 bytes smaller than the 187012-byte
no-inline baseline. Much of the classic writer remains reachable through
runtime smart-filesystem paths; this is not complete FFS specialization.

The current solution Release build succeeded and its host suite passed 1419
cases with three optional external-image cases skipped. The narrowed binary
passed 204 native replay cases (three optional cases skipped) and ten full-ROM
foreign startup and recovery-order comparisons. With the same 64 KiB-aligned
guest buffer (`0x00220000`) and no-inline baseline, the paired Kickstart
sequential-read medians changed by -1.50%, +0.016%, +0.20% and -0.22% for
64-byte, 512-byte, 4 KiB and 64 KiB packets respectively. These synchronous
emulator measurements are not proof of performance parity on other workloads
or devices.

The existing SFS2 TRST handler accepts and recovers a classic-SFS volume, not
just SFS2 volumes. Consequently removing its classic runtime engine outright
would change current compatibility. Both SFS and SFS2 specialization still
need a design that preserves their supported formats and recovery order,
followed by compatibility and performance qualification. No specialized
profile has been selected as a default.

### Foreign-startup cleanup follow-up

`MountSmart` allocates a write combiner after recovery, including when the FFS
entry will subsequently reject that foreign volume. The FFS-only guard had
also removed the matching free in `ReleaseSmart`; that cleanup now remains
enabled. The rebuilt experimental HUNK is 182796 bytes, 4216 bytes (2.25%)
smaller than the no-inline baseline. The earlier 183216-byte results above
describe the intermediate build, not this corrected binary.

The corrected HUNK passed 204 native replays with three optional cases
skipped; the rebuilt CopperStart source consumer passed 131 replays. All ten
full-ROM foreign-startup and recovery-order comparisons passed. The
address-controlled sequential-read comparison passed both arms at `0x00220000`;
median session cycles changed by -1.48%, -0.16%, -0.01% and -0.19% across
the four packet sizes. The original-handler comparison passed 11 enabled cases
with 15 unrelated cases skipped; the host rerun passed 1419 cases with three
external-image skips.

## Standalone packet-dispatch experiments

After an FFS/OFS volume has passed the standalone startup type check, the
experimental FFS-only entry excludes SFS mutation packet branches; foreign
startup and journal recovery remain intact. The resulting FFS HUNK is 111176
bytes versus 187012 bytes for the no-inline baseline, a reduction of 75836
bytes (40.6%). It passed 204 native replays (three optional skips), ten
full-ROM foreign startup and recovery-order cases, one full-ROM unsupported
64-bit resize packet comparison, and 11 original-handler comparisons (15
unrelated skips). In the final consolidated comparison, controlled median
session cycles changed by -0.94%, -0.32%, -0.30%, and -0.43% across the four
packet sizes. Rebuilding from the current
source and pinned compiler reproduced the tested FFS HUNK byte for byte.

The opt-in `-SmartOnly` profile removes FFS mutation dispatch from standalone
SFS entries after their startup type checks. Both the classic SFS and SFS2
variants still perform foreign startup probing; the SFS2 variant retains its
classic SFS runtime and recovery support. Classic SFS fell from 182116 to
166520 bytes (8.6%), and SFS2 TRST from 229976 to 213504 bytes (7.2%). The
SFS2 candidate and FFS candidate passed 204 native replays with three optional
skips. All comparisons use 68020 emulation, a synchronous device, and the
same `0x00220000` guest data buffer in both arms. A synthetic classic-SFS
image adds a paired read benchmark for that profile: median changes were
-0.14%, -0.03%, +0.05%, and -1.95%; SFS2 TRST changes were +0.25%, +0.08%,
+0.02%, and -0.15%. These are limited read workloads, not proof of parity for
writes, physical hardware, or every packet. The Release host suite passed 1419
cases with three external-image skips. The combined candidate has passed 11 full-ROM foreign
startup and packet checks, and 11 original-handler comparisons with 16
unrelated skips, including TRST/CSUN recovery handoffs. A further full-ROM
test mutated a classic-SFS image with both SFS2 variants and successfully read
each result with both variants. The maps still contain
foreign-format read paths and format-specific trim support;
complete specialization and qualification have not been established, so the
new profiles remain opt-in.

The specialized SFS2 TRST handler also passed all eight crash-recovery and
large-file-position cases, including six interrupted mutation operations.

The read report generator validates the hashes and aligned addresses of all
72 recorded FFS, classic-SFS and SFS2 trials. It accepts a manifest containing
a subset of profiles, with two passing test cases required per pair.

## Read, trim and write qualification follow-up

Standalone packet paths now use a compile-time format family after the entry
has accepted the mounted volume. Startup still checks the real mounted type,
including recovery before a foreign-format rejection. SFS2 retains both SFS2
and classic-SFS operation. Trim dispatch is specialized separately, so FFS
does not retain the SFS trim engine and SFS profiles do not retain FFS trim.

The classic FFS resolver is still reachable from the existing notification
implementation on SFS volumes. A full-ROM comparison found that this path
returns error 213 for the tested smart-volume notification, with the same
ordered device I/O in both arms. Removing or redirecting it would change
existing packet behavior, so it remains. FFS still retains classic-SFS startup
and recovery code for the same compatibility reason.

| Handler | No-inline baseline | Qualified candidate | Reduction |
|---|---:|---:|---:|
| FFS | 187012 | 92472 | 50.55% |
| classic SFS | 182116 | 145188 | 20.28% |
| SFS2 TRST | 229976 | 192584 | 16.26% |

The candidate passed 1419 Release host tests (three external-image skips),
204 native handler replays (three optional skips), 12 native cache-invalidation
replays, 131 rebuilt CopperStart source-consumer replays, 36 full-ROM startup,
trim, notification and classic-SFS handoff comparisons, 11 original-handler
comparisons, and eight SFS2 crash-recovery/large-file cases. The trim matrix
includes unsupported requests and an emulated discard-capable device: query,
dry run and actual discard, with range/order comparisons and a full payload
check. The original-handler broad filter skipped 22 unrelated cases. ROM tests
use emulated Kickstart 3.1; physical Amiga/storage and absent external-image
tests are not passes.

All 72 paired read trials used guest buffer `0x00220000` and passed the report
validator. The four packet-size medians improved by 2.29–5.04% for FFS,
0.58–0.97% for classic SFS, and 0.55–2.37% for SFS2 TRST. The write report
validates 72 trials and 72 fresh-boot checks over 4 KiB overwrite/append and
64 KiB append, with matching buffer addresses, output images and device
command counts. FFS writes improved 1.42–2.64%; classic SFS improved
0.43–0.84%; SFS2 TRST improved 51–57%; and SFS2 handling classic SFS improved
0.18–0.83%.

The classic bulk-append profile attributed the handler itself fewer cycles,
but the candidate incurred an additional Kickstart scheduling interval. Six
paired startup-phase offsets ranged from 2.25% faster to 0.56% slower, with
identical buffer placement, disk image and device-command counts. After the
phase-control code was added, a fresh current-source run measured the classic
overwrite, append and bulk-append medians 0.64%, 0.43% and 0.84% faster. The
earlier 1.16% increase is retained here as a measured scheduling-sensitive
result, not erased by the rerun.

The SFS2 write profile found that native journal encoding/replay constructed a
full geometry record for each byte. Switching to the existing equivalent scalar
platform getters preserved the journal and device trace while removing the
observed 2–3% regression and substantially reducing total cycles. In the
bulk-append diagnostic, total cycles fell from 364273820 to 173015380 and
exclusive geometry-getter cycles from 202676725 to 38466678. The default
standalone FFS, classic SFS and SFS2 TRST builds now select the qualified
specialization; other write-policy combinations retain combined dispatch by
default, and `-NoSpecialization` provides the combined build explicitly. All
three ordinary default builds reproduced their tested HUNKs byte for byte.
The combined FFS and classic-SFS builds reproduced the earlier baseline HUNKs;
combined SFS2 passed 204 native replays (three optional skips) and ten full-ROM
compatibility cases. The separately rebuilt CSUN writer passed 204 native
replays, original-handler/cross-recovery comparisons and all six interrupted
mutation recovery cases. The expanded CopperStart source-consumer replay passed
230 cases with SFS2 TRST enabled. An initial run of those extra SFS2 cases
without the SFS2 integration flag failed and is not counted as a pass.

## FFS foreign-volume rejection follow-up

Standalone FFS now rejects a non-FFS/OFS signature before calling the smart
mount or journal-recovery paths. This deliberately changes the former behavior:
FFS must not repair a foreign SFS volume. Both the default specialized and
the combined FFS builds passed ten Kickstart 3.1 foreign-startup cases each,
including dirty classic-SFS and SFS2 images. The FFS cases returned
ObjectWrongType, issued no writes or flushes, and left the image hash unchanged;
the SFS and SFS2 controls retained their recovery behavior. Six original-FFS
and OFS mutation and clean-handoff comparisons passed.

The Release host suite passed 1,419 tests, with three optional external-image
skips. The direct native handler suite passed 216 tests, with its optional
benchmark skipped. The SFS2-enabled CopperStart source-consumer replay passed
230 tests. The default FFS HUNK is 90,864 bytes, down from 92,472 bytes;
its SHA-256 is B74FC79CE4A4C6F06FFAB15F63BFEE3056BC7BB9994001C627CD7CF7B5A640B9.

Performance parity is not established. With aligned buffers, matching data
hashes and device request counts, the 24 paired FFS read trials measured
median session-cycle changes of +0.26%, -0.19%, +0.04% and +0.87% at 64,
512, 4,096 and 65,536 bytes. The 18 paired write trials and fresh-boot checks
passed, with overwrite, append and bulk append respectively +0.30%, +2.02%
and +1.00% in emulated bus cycles. Six startup-phase offsets kept append
between +2.02% and +2.05%, with identical image hashes and device counts.
The exclusive instruction profile attributes most of the increase to the
FFS bitmap-loading path; it does not establish the underlying cause or a
physical-hardware effect. The correction is not performance-qualified for
release until this difference is resolved or explicitly accepted.

### FFS geometry follow-up

The append profile showed repeated root-block geometry calculations while
validating a file chain. The FFS chain validator now calculates the root once
per pass; OFS retains its existing validation path. A new damaged-pointer
case confirms that a data pointer to the root is rejected before any write.
The resulting default FFS HUNK is 91,324 bytes, SHA-256
FEDD80E84ED4E94F4042B174819C75AF14FCDBC54C431DF681457F37A645EF65.

The revised paired write measurements show +0.003% overwrite, -7.35% append
and -2.99% bulk append in emulated bus cycles, with matching buffer addresses,
image hashes and device counts; all fresh-boot verifications passed. Two
independent 24-trial read runs measured median session increases of
0.27-0.29%, 0.06-0.07%, 0.13-0.15% and 0.78-0.87% at 64, 512, 4,096 and
65,536 bytes respectively, with matching buffer addresses, data hashes and
device request counts. For the 65,536-byte workload the read phase itself
was faster in the first run, while open/session costs increased. These results
do not establish read-performance parity or physical-hardware performance.
The foreign-startup, original-handler and native tests described above were
rerun for this HUNK. The read differences remain a release qualification
limitation.

### FFS read qualification follow-up

The earlier 64 KiB profile attributed most of the open-time difference to
Kickstart, and the two handlers loaded 512 bytes apart. A diagnostic candidate
whose code hunk was padded to match the baseline's loaded address reduced the
largest session difference; that padded binary was not used for qualification.
The unpadded candidate subsequently combines partial-read and cached-packet
alias checks without relaxing their bounds, routes FFS input opens to the existing object dispatcher
without unrelated general-decoder lookups, and omits smart overlay checks from
the specialized FFS data path. FFS cannot create those overlays because it
rejects smart volumes before mount. Other profiles retain the overlay checks.
The FFS-only build also disables the native compiler's call-clustering pass;
the CILica report still records inlining disabled and scalar replacement enabled.

The final 68000 HUNK is 91,820 bytes, SHA-256
`63936F65ADF8E0556064FDDA81E1C7BFA98D12B92068D50E0A2E01654283B5B6`.
A separate build through `Build-Handler.ps1` reproduced it byte for byte.
Restricting the FFS startup parameter to builds that need it also restored
the classic SFS and SFS2 TRST HUNKs byte for byte to their previously qualified
hashes, `59FFB75DED4B7071F08BDD2F2ABD9AFDAA81DC07E6DFE47CD4DB521B44A49652`
and `8AAE316CEA987C6B0B93EE51AB1609371F1B11550C4E5E20CC96C280B7D9A753`.
Against the previous 92,472-byte specialized handler (SHA-256
`2F8BD2A36B1352E640C7DE227D462E82A9495F1287DF1E51A265EB5DAED3CD90`),
six guest scheduling offsets and three trials per arm produced 144 validated
full-ROM sessions. Each comparison matched buffer addresses, payload and media
hashes, probe/ROM hashes, device request counts and device bytes. The ranges
below are the smallest and largest changes in median session bus cycles over
the six offsets; negative means the candidate used fewer cycles.

| Packet bytes | Change in median session cycles |
| ---: | ---: |
| 64 | -1.755% to -1.545% |
| 512 | -2.507% to -2.076% |
| 4,096 | -1.768% to -1.585% |
| 65,536 | -1.502% to -0.624% |

The 18 paired FFS write trials and 18 fresh-boot checks matched guest buffer
addresses, final image hashes and device command counts. Median overwrite,
append and bulk-append bus cycles changed by -0.035%, -7.148% and -2.900%.
These measurements use synchronous CopperHDF and an emulated 68020 running
Kickstart 3.1; they do not establish physical-device timing.

To reproduce the read sweep, prepare a native benchmark manifest with one
`ffs` pair pointing to the frozen baseline and candidate, then run the normal
benchmark wrapper for each phase. The ROM and reference binaries are external.

```powershell
foreach ($delay in @(0, 100, 500, 1000, 2000, 4000)) {
    ./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot $nativeInput `
        -ArtifactRoot "$output/phase-$delay" -PhaseDelay $delay -TestAssembly $kickstartTests
}
./tools/Summarize-FfsReadPhases.ps1 -ArtifactRoot $output
```

The summary tool rejects incomplete tests, mismatched binaries, media, probes,
ROMs, buffers, returned data or device counts before reporting timings. Native
FFS tests also compare the fast input path against the general decoder for
invalid locks, names and handles. Physical Amiga/storage and optional external
image tests remain unavailable.
