# CopperFFS mount index and AutoTrim qualification

**Release status.** The indexed implementation substantially reduces the cost
of deleting and overwriting files. The current gates allow 0.5% slower cycles
with AutoTrim enabled and 0.1% slower cycles against FFS 40.1. Both remain
open. The November 17 test counts below use the earlier 0.1% AutoTrim limit.

Set `ClassicAutoTrimComparison` to `true` in the benchmark manifest for a
paired disabled/enabled build. Point `Original` and `Baseline` at the disabled
handler and `Candidate` at the enabled handler. The mixed, 96 KiB and cached-read
tests record their allowance as `MaximumCycleRatio` in `cycles.json`.
Original-handler manifests use the default `false` setting.

At 0.5%, the repeated compact warm run's four OFS cases pass. The remaining
failures in that run are FFS Chip RAM overwrite (+0.536%) and FFS cached reads
in Fast RAM (+1.049%) and Chip RAM (+1.211%). The earlier matched-layout pair
still misses the cold and unsupported OFS Fast RAM cached-read cases.
The first run through the updated harness passed the warm OFS Fast RAM
96 KiB workload and recorded `MaximumCycleRatio` as `1.005`.

## November 17 qualification

Fixed-disk truncation can consume a staged attribute header while retaining
the existing write-protection check. Portable tests passed on both OFS and
FFS and observed one write to the header through publication. The mixed FFS
ROM trace still used 50 write commands against FFS 40.1's 47. That probe
truncates one file and changes attributes on another.

Three alternating warm-device trials from the same 256 KiB AutoTrim-enabled
managed input measured the following effect of CILica inlining on the 96 KiB
workload. Both profiles used scalar replacement. The inlined handler was
153,528 bytes and the scalar-replacement-only handler was 129,932 bytes.

| Format and buffer | Growth | Overwrite | Verification |
|---|---:|---:|---:|
| FFS Fast | −10.001% | +1.244% | −0.045% |
| FFS Chip | −8.673% | +0.943% | −0.071% |
| OFS Fast | −2.176% | −3.496% | −0.020% |
| OFS Chip | −3.591% | −2.646% | −0.037% |

Inlining improved growth in every arm and regressed both FFS overwrite arms.
This comparison isolates inlining. It uses AutoTrim enabled in both handlers.

With the same inlined AutoTrim build against the ordinary disabled build,
warm-device overwrite used 0.655–0.728% fewer cycles on FFS and
1.991–2.007% fewer on OFS. FFS growth still cost 0.392–0.404% more and
OFS verification cost 0.277–0.286% more. The cached-read probe still failed
on FFS in Chip RAM and OFS in Fast RAM.

Against FFS 40.1, all 96 KiB phase timings passed. OFS growth still issued
five read commands versus three. The device trace attributes each read to
fetching the last sector of a staged write run before updating its forward
link or extending its partial payload. Each read fetched a 64 KiB window.

The next cached-read candidate used the OFS reader directly for DOS packets
whose output was disjoint from the native file record. OFS reads traverse their
own chain and need only clear the FFS cursor identity. Cached reads passed
against FFS 40.1 in both memory placements, taking 1.281% fewer cycles with
Fast RAM and 2.461% fewer with Chip RAM. The preceding candidate was 2.993%
and 2.820% slower, respectively.

Truncation can also reuse a sealed header retained at the preceding close.
Four focused portable tests passed across OFS and FFS, covering the retained
header, staged attributes and write protection.

Retaining the last sector of each successful staged OFS run removed the
five 64 KiB rereads from growth. The 96 KiB tests passed against FFS 40.1
with both Fast and Chip RAM. Growth used zero reads, 27 writes and one flush.
FFS 40.1 used three reads, 433 writes and one flush on the same packets.

The build that reused the previous close's header put FFS mixed mutation
0.682% behind FFS 40.1 in Fast RAM and 0.692% behind in Chip RAM.
The fragmented, original-created files passed both small append and scattered
overwrite tests in all four format and memory combinations. Appends took
3.741–4.979% fewer cycles and overwrites took 1.968–10.869% fewer.

The corresponding matched-layout control produced two 153,916-byte binaries
that differed in three mount-policy bytes. Warm FFS mixed mutation still cost
0.178% more in Fast RAM and 0.177% more in Chip RAM with AutoTrim enabled.
In the separate warm trace, deleting the donor cost 6,180 additional guest
cycles with AutoTrim enabled. Both arms performed the same DOS operations.
Profiling the timed warm workload attributed roughly 21,500 of its 28,000
additional guest cycles to execution outside the handler.

Keeping the sealed header with the parked allocation map and updating
successive attributes inside the existing transaction reduced the FFS
mixed-mutation gap to 0.638% in Fast RAM and 0.642% in Chip RAM.

Recording one hint for each uninterrupted run of freed bits brought the
matched-layout warm FFS mutation difference to +0.054% in each memory
placement. Verification and cached reads also passed in both placements.
All 442 CopperFFS core tests passed with that change.

Maintaining the checksum on the staged attribute header lets later lookups
borrow it before publication. The cached-read comparison with this candidate
passed against FFS 40.1 in both formats and both memory placements, with
0.411–1.374% fewer cycles. Its FFS mixed mutation remained 0.643–0.660%
slower. A separate unsupported-device run on the previous matched-layout
candidate passed 11 of 12 cases. Its OFS Fast RAM cached read cost +0.865%
with AutoTrim enabled.

The same matched-layout pair passed 12 of 12 warm-device cases and 11 of 12
cold-device cases. The cold failure was again the OFS cached read in Fast RAM,
at +0.848%. The same cached read passed warm at +0.012%. Opening the MicroHz
timer unit in both arms produced four cached-read failures with unsupported
discard and three with warm discard.

Keeping the native read window across disjoint writes and updating it for a
successful retained-sector write produced the same 11 reads, 50 writes and one
barrier during FFS mixed mutation. Against FFS 40.1 it took 0.664% more cycles
in Fast RAM and 0.658% more in Chip RAM. The implementation dropped this change.

Grouped truncation now edits its verified private header in place. The
profiled truncate and first write took 71,840 cycles, down from 77,220, with
two fewer sector copies. Reusing the sealed parent during delete and carrying
that directory update through the next file write saved 15,090 cycles across
those two traced intervals. The combined build took 0.572% more cycles than
FFS 40.1 in Fast RAM and 0.570% more in Chip RAM for mixed mutation.

Opt-in cached-read profiling with the archived guest probe reproduced the
unsupported-device OFS Fast RAM failure. One pair measured 19,120 additional
guest cycles with AutoTrim enabled, including 18,829 additional Kickstart
cycles and 17 fewer handler cycles. The handler executed 30,058 instructions
in both arms. In the ROM address ranges the largest increases were in
`intuition.library`, `exec.library` and `timer.device`. A rebuilt probe passed
against the same handler binaries. `COPPERFS_CACHED_READ_PROFILE=1` profiles
the first paired trial. `COPPERFS_CACHED_READ_PROBE` can supply the archived
guest executable for that diagnostic run.

An absolute EClock deadline kept the five-second delay and used a matched
binary layout. On the archived OFS Fast RAM unsupported-device case, cached
reads cost 1.090% more and verification cost 0.515% more.

The adaptive read clock now has a separate mount field from the deferred TRIM
request. On mounts that skip read sampling, opening the timer on the first
free leaves 4 KiB reads on the ordinary path. In the first compact warm-device
comparison, OFS overwrite cost +0.228% in Fast RAM and −1.272% in Chip RAM.
FFS overwrite cost +0.521% in Fast RAM and +0.565% in Chip RAM. FFS cached
reads cost +1.056% in Fast RAM and +1.222% in Chip RAM. Six of the twelve
warm-device cases passed with the compact binaries.

Profiling the first warm overwrite trial attributed the extra 1,150 FFS
Chip RAM guest cycles to execution outside the handler. The handler used 157
fewer cycles. OFS Fast RAM used 2,430 extra guest cycles, including 5,920 extra
handler cycles partly offset by lower Kickstart costs. `CopyAndSealOfs`,
`PatchGroupRun` and `WriteOfs` accounted for the largest increases.

Doubling the unroll in the fused OFS sector copy increased 96 KiB growth
cost by 14.863% in Fast RAM and 14.242% in Chip RAM. Both arms used AutoTrim
and produced identical verified images. The candidate was dropped.

Same-directory rename now reuses the published parent sector when the
source is the bucket head. In alternating trials against the preceding
AutoTrim-enabled build, FFS mixed mutation used 11,277 fewer cycles in Fast
RAM and 11,127 fewer in Chip RAM. The rename, create and first-write trace
removed one sector copy.
The updated original-handler harness measured the resulting FFS mixed
mutation gap at +0.500% in Fast RAM and +0.493% in Chip RAM against its
0.1% limit.

Moving corruption recovery out of the normal packet reply reduced warm
96 KiB FFS overwrite time with AutoTrim enabled by about 3% in both memory
placements compared with the preceding compact build. Against its paired
disabled build, overwrite was 3.006% faster in Fast RAM and 2.981% faster
in Chip RAM. Cached reads still exceeded the 0.5% allowance at +0.520% and
+0.748%, respectively. In the profiled Fast RAM cached-read trial the
enabled handler used 10,648 fewer cycles while Kickstart used 22,206 more.

Combining the reply change with the rename improvement brought FFS mixed
mutation to +0.430% in Fast RAM and +0.413% in Chip RAM against FFS 40.1.
Mixed verification took 3.262% and 3.223% fewer cycles, respectively.
In the paired warm-device build, cached FFS reads cost +0.479% in Fast RAM
and +0.709% in Chip RAM. The OFS Fast RAM growth phase cost +1.198%.

A separate matched-layout control passed the four warm FFS cases, with
at most +0.055% on any timed workload there. Its OFS Fast RAM cached-read
delta was −0.001% on cold supported discard and within 0.001% on unsupported
discard. These results use the current guest probe and the control build.

The compact warm pair passed ten of twelve cases. In the profiled FFS Chip
RAM cached-read trial, AutoTrim added 17,870 guest cycles. Kickstart added
27,657 cycles while the handler used 10,645 fewer. Warm OFS Fast RAM growth
added 16,850 guest cycles, including 12,326 in Kickstart and 4,142 in the
handler. The handler's OFS copy and checksum routine accounted for 4,216
additional exclusive cycles.

The mixed-write trace against FFS 40.1 attributed 33,560 extra cycles to
creating and appending to the main file, 24,830 to deleting the donor and
48,690 to the final reopens and attribute changes. In the latter interval,
path resolution and checked block borrowing used 22,728 exclusive handler
cycles. Reusing the device protection check within a single append packet
saved about 1,550 cycles per mixed mutation. An exact repeated-path cache
saved 3,553–4,320 cycles. Neither experiment went into the handler.

Moving active write-group handling out of ordinary packet dispatch reduced
the compiler's dispatcher stack frame from 180 bytes to eight. In three
alternating warm trials, enabled FFS Chip RAM cached reads used 46,183 fewer
guest cycles than the preceding enabled build and 0.100% fewer than the paired
disabled build. Enabled OFS Fast RAM growth used 8,003 fewer cycles than the
preceding enabled build and 0.152% fewer than the paired disabled build.
The warm OFS Fast RAM overwrite also passed at −0.782%.
Against FFS 40.1, OFS passed both mixed cases. FFS mixed mutation remained
0.445% slower in Fast RAM and 0.460% slower in Chip RAM. The mixed FFS
trace still issued 50 write commands against 47 for FFS 40.1.

The wider matrix found a warm FFS overwrite regression. The enabled
dispatcher candidate took 213,100 cycles in Fast RAM and 218,143 in Chip RAM,
versus 199,137 and 199,700 with the preceding enabled build. The candidate
also missed the cold and unsupported growth limits in both formats. In the
profiled unsupported FFS Chip RAM growth trial, the enabled handler used
1,814 fewer cycles while Kickstart used 14,401 more. Both arms issued fourteen
write commands. Kickstart's resident ranges attributed 5,419 additional
cycles to Intuition, 4,785 to Exec and 3,217 to timer.device.
The dispatcher candidate passed 172 selected CopperStart replays and all
37 full-ROM correctness cases.

A separate candidate kept publication scratch out of continuing file
writes. It saved 14,563 mixed FFS cycles in Fast RAM and 15,683 in Chip RAM
against the dispatcher candidate. Each arm issued 50 write commands.
Warm FFS overwrite on that candidate still took 4.121% and 3.792% more
cycles than the preceding enabled build, respectively.

The indexed mutation path now leaves the cold allocation scan in its own
routine and reads the mount status once. The native compiler reports zero
local frame bytes in indexed preparation, down from 168. In separate
alternating mixed trials this saved 14,263 cycles in Fast RAM and 15,587 in
Chip RAM against the dispatcher candidate, with 50 writes in both arms.

Reserving the same workspace tail in the compact disabled build left
unsupported FFS growth 4.527% slower with AutoTrim enabled in Fast RAM and
4.482% slower in Chip RAM. Matching the workspace reservation alone did
not account for the cycle gap.

The combined indexed-preparation and publication changes narrowed the
direct FFS 40.1 mixed-mutation gap to 0.308% in Fast RAM and 0.314% in Chip
RAM. Mixed verification took 3.396% and 3.378% fewer cycles, respectively.

The dispatcher build's matched-layout binaries were each 154,412 bytes
and differed in three mount-policy bytes. All 36 cases passed across the
unsupported, cold-supported and warm-supported device modes. On the
unsupported device the largest absolute large-write phase difference was
0.021%. Across the cold cases the largest absolute difference was 0.092%,
including cached-read and mount timing.
Across the two unsupported-device runs, matched-layout FFS growth took
4.490–4.532% more cycles in the disabled arm and 0.061–0.077% more in the
enabled arm than the corresponding compact build. In a separate
enabled-to-enabled comparison, warm FFS overwrite took
0.362% more cycles in Fast RAM and 0.317% more in Chip RAM than the
preceding enabled compact build. The compact dispatcher build took
more than 7% longer in both memory placements.

The compact build with both changes passed all 37 selected full-ROM cases.
Its warm FFS overwrite took 8.896% more cycles in Fast RAM and 8.146% more
in Chip RAM with AutoTrim enabled. Warm OFS growth took 1.483% and 1.399%
more cycles. All four large-write tests exceeded the 0.5% ceiling.

The standalone classic handler now sends namespace packets directly to the
routines that decode their arguments. Three alternating FFS 40.1 comparisons
put mixed mutation 0.202% above the original in Fast RAM and 0.193% above
in Chip RAM. Mixed verification used 3.407% and 3.389% fewer cycles.
Reusing the first memory-region lookup for the counted filename and skipping
the token lookup for root-relative names reduced the mutation gap to 0.166%
in both placements. Verification used 3.406% fewer cycles in Fast RAM and
3.392% fewer in Chip RAM. Mutation still exceeds the 0.1% allowance and uses
50 write commands against the original's 47.

Against FFS 40.1, the same handler's 96 KiB workload measured the following
cycle changes over three alternating trials.

| Format and buffer | Growth | Overwrite | Verification |
|---|---:|---:|---:|
| FFS Fast | +1.915% | −28.221% | −0.509% |
| FFS Chip | +1.920% | −28.213% | −0.378% |
| OFS Fast | −62.569% | −64.185% | −12.412% |
| OFS Chip | −61.932% | −63.512% | −12.291% |

Both OFS cases passed the cycle and I/O assertions. Both FFS cases failed
the growth cycle assertion.

This build passed the two compact warm FFS cases and warm OFS in Chip RAM.
Warm OFS growth in Fast RAM cost 0.727% more with AutoTrim enabled. The
enabled FFS overwrite averaged 205,807 cycles in Fast RAM and 207,503 in
Chip RAM, down from 211,780 and 210,823 in the preceding compact build.
The published enabled build averaged 199,137 and 199,700 cycles in the same
workload. This build passed all 37 selected full-ROM correctness cases.

Routing locate, lock release and close through the classic object dispatcher
eliminated their second argument decode. Close still publishes a pending
group before releasing its handle. The FFS mixed mutation gap dropped to
0.115% in Fast RAM and 0.118% in Chip RAM. Verification used 3.459% and
3.451% fewer cycles. The mixed mutation still issued 50 writes in both runs.

Routing initial writes directly to the classic writer after checking the
file handle reduced mixed mutation to 0.102% over FFS 40.1 in both placements.
The mixed run still exceeded the 0.1% allowance. The same build passed
both 96 KiB FFS cycle and I/O tests against FFS 40.1.

| FFS buffer | Growth | Overwrite | Verification |
|---|---:|---:|---:|
| Fast RAM | −4.823% | −29.108% | −0.444% |
| Chip RAM | −4.755% | −28.926% | −0.427% |

Growth issued 14 writes versus 15 for FFS 40.1. Overwrite issued 14 versus 16.

All six OFS cases against FFS 40.1 passed on this build. Mixed mutation used
8.001% fewer cycles in Fast RAM and 7.860% fewer in Chip RAM. Cached reads
used 4.037% and 4.546% fewer cycles, respectively. In the 96 KiB workload,
growth used 62.899% and 62.218% fewer cycles and overwrite used 64.228% and
63.433% fewer cycles.

Testing a single allocation-index status read at transaction entry used
1,567 fewer mixed mutation cycles in Fast RAM and 1,300 fewer in Chip RAM
than the direct-write build's separate trials. Growth and overwrite passed
in both placements. Mixed mutation passed at +0.094% in Fast RAM and
missed at +0.106% in Chip RAM. The original handler ran 2,040 cycles faster
in the Chip RAM comparison.

The retained revision passes the action decoded at packet entry to the
classic and general dispatchers. It passed all four FFS comparisons against
FFS 40.1. Mixed mutation cost 0.092% more cycles in Fast RAM and 0.097% more
in Chip RAM. Growth used 4.889% and 4.755% fewer cycles, and overwrite used
29.248% and 28.897% fewer cycles, respectively.

The retained handler passed all 16 original-handler performance tests,
including cached reads and the original-created fragmented files. On those
files, FFS short appends used 7.892–8.247% fewer cycles and overwrites used
10.628–10.766% fewer cycles. OFS appends used 5.226–5.459% fewer cycles and
overwrites used 13.670–13.723% fewer cycles. Cached reads used 2.222–2.310%
fewer cycles in FFS and 4.012–4.739% fewer in OFS. The mount probe measured
381,032 bytes per format and placement, versus 54,232 for FFS 40.1. Its
instrumented FFS mutation phase measured +0.659% in each placement, while
the separately timed standard mixed test passed.

With the compact handlers on the warm supported device, both FFS large-write
cases passed. Enabled FFS overwrite averaged 190,023 cycles in Fast RAM and
190,513 in Chip RAM, roughly 4.6% below the published enabled build.
OFS overwrite was 0.031% and 0.386% slower with AutoTrim enabled, within the
0.5% allowance. OFS growth remained above the limit at +1.934% and +1.884%.
The retained enabled build passed all 37 selected full-ROM correctness cases.

Writing a cached OFS forward link directly from its DMA-safe cache entry
used 2.873% fewer warm growth cycles in Fast RAM and 3.136% fewer in Chip
RAM in a separate enabled-to-enabled comparison. The successor data reaches
the device before the link is updated. A failed write evicts the cache
entry and leaves group publication failed.

The retained combined handler passed all 16 original-handler performance
tests and 37 full-ROM correctness checks. Its FFS mixed mutation was
0.087% slower than FFS 40.1 in Fast RAM and 0.092% slower in Chip RAM.
Warm OFS growth fell from 1,410,887 to 1,370,020 cycles with AutoTrim enabled
in Fast RAM, and from 1,441,617 to 1,394,967 in Chip RAM. The disabled build
also improved. The remaining compact gaps were +1.583% and +1.789% for warm
OFS growth and +0.514% for the warm Chip RAM FFS cached-read case. The SCSI
command and UNMAP counts in all enabled large-write trials matched their
warmup counts. The timed growth, overwrite and verification phases issued
no new SCSI commands.

In a paired warm Fast RAM OFS growth profile, enabling AutoTrim added
21,280 guest cycles. The handler used 564 fewer cycles and Kickstart used
21,165 more. In the paired warm Chip RAM FFS cached-read profile, enabling
AutoTrim added 11,880 guest cycles. The handler used 11,250 fewer cycles
and Kickstart used 22,225 more. That probe reads through the partial-block
path. The profiles do not isolate the cause of the Kickstart difference.

In a separate three-trial comparison of warm AutoTrim-enabled builds from
the same source, the CILica inlining build used 8.329–8.332% fewer FFS growth
cycles and 16.903–16.961% fewer FFS overwrite cycles. OFS growth used
2.158–2.233% fewer cycles and overwrite used 2.974–3.292% fewer. The inlining
build occupied 155,000 bytes, compared with 131,344 bytes with inlining off.

The OFS forward-link fault test observed the successor data on disk before
injecting a partial write of the preceding sector. The interrupted sector
retained its old checksum, the root stayed invalid, and close and flush
reported failure.

CopperStart native replay on the preceding handler passed 220 tests.
The four failed SFS2 cases also failed on published commit c734d87 using
the same CopperStart workspace and local CopperSharp SDK. The partial
aligned-write check passed with the preceding handler and both experimental
aligned FFS paths.

A separate direct flush dispatch preserved the published-group failure check
and the device barrier. It also passed the four FFS comparisons. Mixed
mutation was 0.089% and 0.085% slower than FFS 40.1 in Fast and Chip RAM.
Its growth advantage was 0.123% and 0.086%, respectively.

The matched-layout transaction-entry candidate used 4.50% fewer enabled
overwrite cycles than the published handler on the warm device and
4.26% fewer growth cycles. Against FFS 40.1 its mixed mutation was 0.108%
slower in Fast RAM and 0.142% slower in Chip RAM.

In a separate test, an open FFS group's aligned overwrite reused the verified
physical run without constructing a write request. Warm Fast RAM overwrite
averaged 179,063 cycles and Chip RAM averaged 179,510, 10.08% below the
published enabled handler in both memory placements. Mixed mutation on this
experimental build was 0.177% above FFS 40.1 in Fast RAM and 0.180% above in
Chip RAM.

## November 15 qualification

Three alternating Kickstart 3.1 trials with the 256 KiB workspace and restricted
CILica inlining gave the following cycle differences against FFS 40.1. Negative
values mean the guest finished sooner. Both arms performed the explicit flush.
The enabled handler SHA-256 was
`81FDF8006E45E932010F389157CDF90C31353B28577F313EFFE727EB1F7A49CF`.

| Format and buffer | Mixed mutation | Mixed verification | 96 KiB growth | 96 KiB overwrite | Growth writes, candidate / original |
|---|---:|---:|---:|---:|---:|
| FFS Fast | +4.231% | −3.228% | +6.784% | −19.646% | 14 / 15 |
| FFS Chip | +4.232% | −3.199% | +6.811% | −19.590% | 14 / 15 |
| OFS Fast | −6.160% | −2.117% | −61.462% | −49.572% | 27 / 433 |
| OFS Chip | −5.948% | −2.080% | −60.669% | −48.482% | 27 / 433 |

Mixed FFS writes fell from the audited 160 to 52, versus 47 for FFS 40.1.
On warm supported media the enabled build passed FFS large growth, overwrite
and verification against the ordinary compact disabled handler in both memory
placements. Warm OFS overwrite cost 0.115% more with Fast RAM and 0.123% more
with Chip RAM. Mixed mutation and some cached reads also exceeded the allowance.
The disabled SHA-256 was
`7497EE1DEA9375D99475F4E7CB9EC5DF6B7893FEA8C8BA92F791E658A96CA43E`.

The subsequent enabled build
(`B443FC8A89809E3B6DA98B1E4C692D46B18EEEEEBEBA21CCC82CD00876A3B1F2`)
cut the Fast RAM FFS growth difference to +4.414% and mixed mutation to
+3.763%. FFS overwrite was 25.219% faster than FFS 40.1. All 31 standalone
full-ROM correctness, interrupted-write, geometry and remount cases passed.
Warm cached FFS reads still exceeded the allowance in both Fast and Chip RAM.

On the subsequent 256 KiB build
(`A92901F85F6433E9E80C08F072FBC84BF0657DE2345797F2A10DC95108E590F0`),
FFS large growth took 3.899% more cycles in Fast RAM and 4.014% more in
Chip RAM than FFS 40.1. Overwrite took 26.815% and 26.595% fewer cycles.
OFS large growth took 61.526% and 60.845% fewer cycles, and overwrite took
51.675% and 50.863% fewer. The Fast RAM cold mount occupied 382,448 bytes on
FFS versus 54,232 for FFS 40.1, including the handler and its workspace.

Three Fast RAM trials with
`14AE492F2CF5DA714D1241F7156E0DEA633742BE84A057D421776D12EDDF3D40`
on the fragmented, original-created OFS file placed overwrite 13.070% ahead
of FFS 40.1 with 27 versus 28 writes. Appending 32 packets of 257 bytes
still cost 8.651% more despite issuing 11 versus 70 writes. On the same
binary FFS mixed mutation cost 3.422% more and 96 KiB growth cost 10.230%
more. The performance gates remained open.

The first short append to a checked file can now share a transfer with later
packets. The enabled handler
`DB0AC04EEC57D946C3A1B2FBC29580439273B3825488BB5B373D68FEA127F9D2`
gave the following means over three Fast RAM trials on identical fragmented
files created by FFS 40.1.

| Format | 32 short appends vs FFS 40.1 | Append writes | Fragmented overwrite vs FFS 40.1 | Overwrite writes |
|---|---:|---:|---:|---:|
| FFS | −3.040% | 7 / 22 | −6.396% | 14 / 15 |
| OFS | −4.252% | 8 / 70 | −10.911% | 27 / 28 |

Keeping the published file header and last extension available for the next
packet reduced the FFS growth gap to +0.476% on
`37AFC765AEDCDAEB009BD95850D84FAD656A465CE6FA3A89967977FA1D438E17`.
FFS mixed mutation took 3.178% longer with 52 writes versus 47. A subsequent
build that checksummed only the used fields of newly cleared pointer tables
measured +5.659% on growth and +3.296% on mixed mutation. The whole-guest
growth result varies appreciably between native code layouts.

Parking the last closed file while a newly created sibling grows avoids a
second chain walk when the first file is deleted. The 256 KiB enabled build
with that change measured FFS mixed mutation at +1.994% (52 / 47 writes)
and growth at +1.030% in Fast RAM and +1.024% in Chip RAM (14 / 15 writes).
Overwrite used 16.615% and 16.863% fewer cycles than FFS 40.1, respectively.
Neither mixed mutation nor growth met the 0.1% allowance on that build.

Writing adjacent FFS extension tables in child-first order reduced the mixed
workload to **50 writes versus 47**. With enabled handler
`6AFD30A3A172A6593C0694C9581954D79B6FCAC59A7F96F6D16292DC72842C9B`,
three alternating Fast RAM trials put FFS mixed mutation 1.508% behind FFS
40.1 and verification 3.152% ahead. Large growth was 1.310% behind in Fast
RAM and 1.355% behind in Chip RAM. Large overwrite was 21.292% and 20.793%
ahead. Patching the old partial tail directly in the physical cache reduced
the mixed difference to 1.401% and the growth differences to 1.271% and
1.319% on the next measured build.

On the extension-pair build, warm supported FFS overwrite took 0.420% fewer
cycles enabled than disabled in Fast RAM and 0.446% fewer in Chip RAM. FFS
growth still cost 0.473% and 0.431% more, respectively. Warm OFS overwrite
cost 0.418% more in Fast RAM and took 0.482% fewer cycles in Chip RAM.
The largest warm cached-read penalty on this build was 1.108% on OFS in
Fast RAM. Both arms used normal release code and the same warm-up packets.

The same extension-pair build passed 36 of the 37 full-ROM correctness
cases. Its large FFS delete reclaimed only 2,233,856 of the required
2,359,296 data bytes. Limiting child-first placement to the two extensions
held for publication and releasing complete physical runs restored
2,392,064 bytes, including the file header and pointer tables. The original
handler read the poisoned image successfully. This repair took 552,860
foreground cycles and issued 73 UNMAPs. The earlier passing large-delete
build took 772,990 foreground cycles and issued nine UNMAPs.

Coalescing the complete hint queue on a separate idle turn reduced the same
large delete to **three UNMAPs** on enabled handler
`E63A435317BD6228E8BD8D13D9831CC5CF048F66A3F82E0BD6B209FE6EC0F020`.
It reclaimed all 2,392,064 bytes. Foreground deletion took 542,540 cycles
and service took 306,113 cycles, down from 3,490,606 service cycles with
73 UNMAPs. The longest turn fell from 104,468 to 99,921 cycles. Original
handler readback passed after the discarded sectors were poisoned.

For OFS, a contained overwrite of an already verified contiguous run can
use one staged read and write, including its partial edge sectors. The
96 KiB warm overwrite dropped from 39 writes to 23. The reader preserves
each sector's sequence and forward link, including original-created append
boundaries, and the writer regenerates each changed checksum.

The ordinary enabled/disabled comparison also remains sensitive to time spent
in Kickstart. In a warm, supported-device FFS overwrite profile of enabled
handler `F94294E593A64832F68E2466C4197B61F3338AAEC05C9A9ABF9A636902A4FBCB`,
handler instructions consumed 101,214 cycles enabled and 101,241 disabled.
Kickstart consumed 102,860 and 94,882 cycles respectively. Both issued
14 writes and one flush. The three alternating full-workload trials measured
a 4.112% enabled penalty in Fast RAM and 4.141% in Chip RAM for overwrite;
growth was 4.883% and 4.871% faster. The release gate includes all guest cycles.

The 32 KiB delete workload reclaimed 33,280 bytes on FFS and 35,328 bytes on
OFS in two UNMAPs each. In the first build above the foreground replies took
71,200 and 75,790 cycles respectively. Its older service profiles timed the
discard worker and omitted the prior turn that published the pending namespace
group. Complete service comparisons require a profile of the outer idle turn.

The outer-turn measurement on
`F6F272B96BE70B85E291A398A02D945536053098AFB9D588D980E22B6BC5A1F9`
includes capability discovery, namespace publication, the durability barrier
and transport completion. The five-second idle wait is excluded. Each format
used one foreground write, then one read, two writes, one barrier and five SCSI
commands during service. Three SCSI commands discovered capabilities and two
issued UNMAP. Both poisoned images passed original-handler readback.

| 32 KiB delete | Foreground cycles | Service cycles, three turns | Longest turn | Combined cycles | Audited combined cycles | Reclaimed bytes |
|---|---:|---:|---:|---:|---:|---:|
| FFS | 74,780 | 117,644 | 62,663 | 192,424 | 666,069 | 33,280 |
| OFS | 71,520 | 124,962 | 64,311 | 196,482 | 2,471,083 | 35,328 |

The fixed-disk writer can keep the root invalid across consecutive writes to
the same open file. It can retain a file header, its first extension and last
extension, one
directory link and one changed bitmap page. Short sequential appends can also
remain in mount-owned RAM after the write reply. Admission covers all buffered
bytes and any required extension tables. When the buffer fills, another write
touches different bytes, or an observing packet arrives, the handler submits
the buffered data. A seek on the owning handle can continue the group. Close
publishes the bitmap, extension tables, file header, directory link and valid
root after the corresponding data writes succeed. When a request allocates
two adjacent extensions, the writer allocates the child first. Close can
submit both sectors in dependency order. A partially completed transfer
leaves the root invalid and close reports the error. Opening a second root file
can share the first file's invalid root and pending bitmap when an empty hash
bucket proves its name is new. The mount keeps the first file's pending header
and extension table until publication. Closing either handle publishes both
files. An occupied bucket or insufficient workspace uses ordinary publication
before lookup. Explicit flush
and inhibit establish durability through the device barrier. A failed metadata publication
invalidates the proofs and reports an error on close or handoff. Automatic
discard waits while the group is open. A power cut between a successful write
reply and group completion can leave the root invalid. Removable media and
portable callers that leave grouping disabled complete each packet separately.

Fixed-disk DOS0–DOS3 namespace packets can also share an invalid root.
Directory creation, rename and delete make their links visible to the next
DOS lookup. When packet readers share the mount index, they see root hash
links in the verified root image until close or another publication boundary.
Successive date, comment and protection changes to
one object share a private header. A directory lookup or examination can
publish that header while the root remains invalid. Closing a file, an
incompatible packet, or an eligible idle discard turn publishes the valid
root. Explicit flush and inhibit also issue the device barrier. Loss of power
between the namespace reply and publication can leave the root invalid.
Portable callers opt in through the work record.

### Grouped writes and warmed discard devices

On the 256 KiB inlined handler with SHA-256
`9E5DE8F1B765042A364711FD818CE907718B98D994CB29D147DCB4A9D1D65C76`,
the standard three alternating Fast RAM trials gave the following differences
from FFS 40.1. A negative change means fewer guest cycles.

| Format | Mixed mutation | Mixed verification | 96 KiB growth | 96 KiB overwrite | Growth writes, candidate / original |
|---|---:|---:|---:|---:|---:|
| FFS | +4.273% | −3.302% | +18.004% | −18.635% | 15 / 15 |
| OFS | −5.497% | −2.115% | −60.876% | −50.299% | 27 / 433 |

The FFS growth phase issued one flush in both handlers. The FFS mixed mutation
still issued 58 writes against 47. On the warmed supported backend, FFS
overwrite cost 238,070–238,080 cycles with AutoTrim enabled and 224,830–224,840
with it disabled. Each arm issued 14 writes, zero reads and one flush.
This 5.890% difference exceeds the 0.1% limit.

Instruction profiles of the same warmed overwrite measured 117,854 handler
cycles enabled and 118,092 disabled. Kickstart consumed 114,340 versus
101,184. Mapping sampled PCs to the ROM resident modules placed 5,419 of the
extra cycles in Intuition, 4,169 in Exec and 2,964 in timer.device. With the
enabled binary held fixed, warming an unsupported device changed the full
phase from 238,070 to 238,100 cycles. The acceptance numbers above include
all guest cycles.

All 31 full-ROM group, discard, geometry and remount cases passed on that
enabled binary. Four additional Kickstart 3.1 cases passed. Two injected
partial writes into a cached or cold FFS sector. Two verified that an
unsupported device inquiry leaves the FFS or OFS namespace group open through
the idle turn and that an explicit flush publishes the group.

The next enabled binary (`C1D802FFC6E6D261307BFDE6E23E885C88D8CA63E3E7DB505C34A32C607148EE`)
staged short overwrites across verified adjacent sectors. The mixed FFS device
trace dropped from 58 writes to 55. Both traced filesystems passed independent
image inspection. On OFS, each staged sector retains its own header, forward
link and checksum.

### Isolating the effect of handler layout

For a matched-layout experiment build both specialized FFS handlers with
`-Ffs -FfsTrimLayoutControl -CilicaInlining`, adding `-AutoTrim` to the
enabled build. The control carries the same maintenance code and mount state.
Its disabled policy prevents hint capture and device discovery. The ordinary
disabled build continues to omit the maintenance code.

Two experimental 146,532-byte handlers differed in three bytes, all copies
of the mount policy immediate. Their SHA-256 hashes were
`4E1DA87609F55DC30172F32D4B308C1C50776647AEBD46AB6F7599FFAA62D379` (on)
and `C98872FD3F6F7A528A7EFACC2C89A75FAAB2D53068CE99579EF8B3D3BF794025` (off).
The supported warmup issued one discard in the enabled arm and zero in the
disabled arm. Three alternating trials measured all guest cycles.

| Memory and format | Growth | Overwrite | Verify |
|---|---:|---:|---:|
| FFS Fast | +0.000% | −0.020% | −0.004% |
| FFS Chip | +0.011% | −0.015% | +0.001% |
| OFS Fast | −0.004% | +0.012% | −0.001% |
| OFS Chip | +0.002% | −0.001% | +0.001% |

The same enabled handler was also tested against the ordinary 132,076-byte off
build (`9BF2A6A9C584B526B41FD9F280FACDE015D87BDEA0D7EC5B0CCEC9F5D46C3044`).
Fast FFS growth was 2.243% slower and overwrite 0.182% faster. Chip FFS
growth was 2.240% slower and overwrite 0.171% faster. All six OFS phase means
improved. The compact-off FFS growth comparison still fails the release gate.

### Restricted CILica inlining

Both 256 KiB handlers enabled AutoTrim and consumed managed input SHA-256
`10294B3280BC0A5C96964D92AC74C76660889DE484925F0C6C2E1AB70E909511`.
The optimizer used scalar replacement in both arms. The same CopperSharp
compiler emitted 115,552 bytes without CILica inlining and 139,620 with it.
Both native compatibility reports found zero reachable managed allocations.
The following changes are three-trial means with a warmed supported device.
Negative percentages indicate fewer guest cycles with inlining.

| Format and memory | 96 KiB growth | 96 KiB overwrite | 96 KiB verification | Cached read | Mount |
| --- | ---: | ---: | ---: | ---: | ---: |
| FFS Fast | −10.318% | −4.036% | −0.477% | −3.513% | +2.991% |
| FFS Chip | −10.365% | −4.688% | −0.490% | −3.697% | +2.503% |
| OFS Fast | −1.368% | −3.879% | −0.063% | −0.853% | +2.998% |
| OFS Chip | −1.967% | −2.905% | −0.316% | −0.814% | +2.494% |

The large-write tests issued the same number of commands in both arms.
The inlined HUNK had SHA-256
`13AC4D35583560B4B5FC14AD1C34DB3491B003A62831936674F9EDA22FA16F51`.
The scalar-replacement-only HUNK had SHA-256
`49EE13D1A60DCE7F44A75F82722107ACCC42F3721AEECC64C40EBA12CB27620D`.

### Truncate, append and unaligned-read measurements

With the inlined 256 KiB handler
`D072E1CE477B1A0F62FB0251126BA5D6938B7318E09DF81F136683D3BC5CF3F0`,
a large append can use the beginning of the caller's packet to finish a
buffered partial sector. The remainder can then go directly to the device.
One directory write also suffices when renaming the first entry of a
non-root hash bucket into another bucket of the same directory.

| Fast RAM workload | FFS cycles vs. FFS 40.1 | OFS cycles vs. FFS 40.1 |
| --- | ---: | ---: |
| Mixed mutation, including flush | +4.672% | −5.128% |
| Mixed verification | −3.309% | −2.251% |
| 96 KiB growth | +28.678% | −61.245% |
| 96 KiB overwrite | −16.701% | −49.698% |
| Cached read | −0.941% | +1.006% |
| 32 small appends to an original-created file | +9.013% | +5.465% |
| 12 scattered overwrites of that file | +20.659% | +14.295% |

FFS mixed mutation used 58 writes against 47. FFS growth used 15 writes
and one flush in each handler. The small appends used nine FFS writes
against 22, and 11 OFS writes against 70. In the separate warm-device
AutoTrim on/off comparison, FFS growth took 0.411% more cycles and overwrite
took 0.117% fewer. OFS growth took 0.102% more cycles and overwrite took
0.671% more. Each percentage uses three alternating trials.

The 256 KiB AutoTrim build with grouped replacement files and retained append
state had SHA-256 `C634F6D50C667C96ABF2C7D8F2D939573AE225615B92837225328C4CCCE9F908`.
These are three alternating Fast RAM trials under Kickstart 3.1. Mixed timings
come from the original-performance probe. Cached reads use the separate mount
probe. Positive percentages mean the enabled handler took more guest cycles.

| Format and comparator | Mixed mutation | Mixed verification | 96 KiB growth | 96 KiB overwrite | Cached read |
| --- | ---: | ---: | ---: | ---: | ---: |
| FFS vs. FFS 40.1 | +6.359% | −2.949% | +29.751% | −12.177% | +0.304% |
| OFS vs. FFS 40.1 | −3.761% | −1.888% | −59.751% | −48.467% | +1.429% |
| FFS vs. AutoTrim off, warm device | +0.296% | +0.026% | +0.312% | −4.651% | −0.347% |
| OFS vs. AutoTrim off, warm device | +0.098% | +0.045% | −0.621% | −0.296% | −0.019% |

Mixed FFS mutation issued 62 writes against the original's 47. Each handler
used 15 writes and one flush for FFS growth. Cold Fast RAM mount used 382,184
bytes for CopperFFS and 54,232 for the original. All 31 selected full-ROM
correctness cases passed. With discarded sectors poisoned, the 32 KiB delete
took 83,530 foreground cycles and 116,467 service cycles on FFS. Its longest
service turn took 85,357 cycles. OFS took 83,670 foreground and 116,557
service cycles, with a longest turn of 87,437. These include two UNMAPs per
format, reclaiming 33,280 and 35,328 bytes respectively.

As a separate control, both arms loaded this same enabled binary and ran the
same guest warm-up. The supported and unsupported devices averaged 241,220
and 241,253 cycles respectively for FFS overwrite, each with 14 writes and
one flush. The measured on/off gate above uses distinct handler builds.

### Namespace grouping and retained bitmap measurements

The 256 KiB AutoTrim-on build with grouped namespace operations and cached
bitmap pages had SHA-256
`5E97C9021D17AF192EC9A34EA0C640C0F57D54F7738B4A87ECA2378CF1FE43C2`.
Three alternating Fast RAM trials under Kickstart 3.1 gave these mean cycle
changes. A positive value means CopperFFS took longer.

| Format and comparator | Mixed mutation | Mixed verification | 96 KiB growth | 96 KiB overwrite |
| --- | ---: | ---: | ---: | ---: |
| FFS vs. FFS 40.1 | +8.789% | +1.156% | +36.214% | −14.466% |
| OFS vs. FFS 40.1 | −3.376% | −0.355% | −60.003% | −48.438% |
| FFS vs. AutoTrim off, warm device | +0.069% | −0.024% | −0.869% | +2.116% |
| OFS vs. AutoTrim off, warm device | +0.073% | +0.007% | −1.156% | −0.977% |

FFS mixed mutation issued 74 writes against the original's 47. OFS issued 80
against 314. On a supported device a 32 KiB deletion took 88,040 foreground
and 100,689 service cycles for FFS, with a longest turn of 74,226 cycles.
OFS took 88,280 foreground and 103,669 service cycles, with a longest turn of
75,829 cycles. Two UNMAP commands reclaimed 33,280 bytes on FFS and 35,328
on OFS. All 31 selected Kickstart correctness cases passed, including
namespace visibility and original-handler remount after handoff.

### Qualification before namespace grouping

The 256 KiB AutoTrim-on handler with bulk OFS preflight and directly built FFS
pointer tables had SHA-256
`F6FB58C8139AB830315407DAAA2EECA9FF00D132F8B0678B1E121270739DFFE4`.
Three alternating Fast RAM trials against FFS 40.1 gave the following means
as percentage changes in complete guest cycles.

| Format | Mixed mutation | Mixed verification | 96 KiB growth | 96 KiB overwrite | 32 small appends | 12 fragmented overwrites |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| FFS | +11.851% | +1.352% | +53.983% | −12.744% | +45.974% | +55.630% |
| OFS | −0.596% | −0.240% | −58.824% | −46.507% | +25.356% | +54.989% |

FFS issued 15 growth writes and one flush, matching FFS 40.1. FFS mixed
mutation still issued 88 writes against 47. OFS mixed mutation issued 94
against 314. For the Fast RAM FFS overwrite the paired on/off result was
−4.053% unsupported, −4.059% cold supported, and +0.039% warm supported.
Warm OFS overwrite was +0.553%. Mixed mutation remained above +0.1% on both
formats for all three device conditions.

All 372 portable CopperFFS cases and all 27 selected Kickstart 3.1 correctness
cases passed for this build. For the supported delete, FFS used 116,940
foreground and 100,432 service cycles. The longest turn was 74,101 cycles.
OFS used 102,210 foreground and 102,989 service cycles, with a longest turn of
75,288. Each issued two UNMAPs and reclaimed 33,280 and 35,328 bytes respectively.
Both delete-plus-service totals beat the audited 666,069 and 2,471,083 cycles.
The preceding sibling-close build measured 185,576, 382,184 and 1,168,616
bytes of guest memory at mount for the 64, 256 and 1,024 KiB workspaces.
The timing matrix has yet to qualify a workspace size.

### Cross-packet measurements

The 256 KiB build with grouped root-directory creation and a single-pass bitmap
scan used SHA-256 `234FEC97684920E34BBA67249DCD8A74D52D86F7DB9F60A39AE07FB1699617CC` on and
`DE4282399686AEDF63C8C871A4EEE35F67A716612DD2376475595DB813E03C89` off.
Three alternating Fast RAM trials produced these changes against FFS 40.1.

| Format | Mixed mutation | Mixed verification | 96 KiB growth | 96 KiB overwrite | Growth writes | Overwrite writes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| FFS | +16.515% | +1.332% | +130.818% | −7.192% | 19 / 15 | 14 / 16 |
| OFS | +9.237% | −0.218% | −16.571% | −47.043% | 32 / 433 | 39 / 216 |

Warm FFS overwrite was 0.995% faster on than off in Fast RAM and 0.980%
faster in Chip RAM. Warm FFS growth still cost 4.789% and 4.896% respectively.
Cold supported deletion took 128,510 foreground and 103,520 service cycles
on FFS, and 115,170 foreground and 105,249 service cycles on OFS. The longest
turn took 75,936 and 77,568 cycles respectively. Two UNMAPs reclaimed 33,280
FFS bytes and 35,328 OFS bytes. All 25 selected full-ROM correctness cases
passed, including visibility immediately after close and original-handler
readback after cutting power immediately after flush or inhibit.

With short append buffering and nested-directory creation enabled, 32 appends
of 257 bytes to the fragmented original-created seed required nine device
writes on FFS and eleven on OFS. FFS 40.1 issued 22 and 70 respectively.
Fast RAM append cycles remained 45.676% above FFS 40.1 on FFS and 112.631%
above it on OFS. The following qualification tables record the earlier
per-packet implementation identified by the hashes at the end of the report.

The standalone CopperFFS handler uses a 256 KiB workspace per mounted volume by
default. Both AutoTrim configurations use it. Build alternatives with
`Build-Handler.ps1 -Ffs -FfsIndexKiB 64` or `-FfsIndexKiB 1024`. Automatic
discard remains opt-in through `-AutoTrim` and is qualified for DOS0 and DOS1.

## Ownership and failure behavior

The first allocation preflight verifies the bitmap directory, extension chain,
checksums, permanent-block ownership and total allocation. The workspace holds
the directory, sorted permanent-block identities, used-block count, a root
image, two banks of file block identities and bounded transfer staging. Each
bank includes pointer-table identities. Creating a disjoint sibling can park
the preceding file mapping. Removing a separate file can use the spare bank
to verify both allocations before freeing any block. A dense bit set or a
sorted list detects duplicate blocks during full validation. Memory use stays
bounded with fragmented files.
An unavailable workspace or a structure that exceeds its budget falls back to
the original caller-scratch traversal.

The writer withdraws the allocation proof before writing the invalid root.
It updates the owned root image when a directory operation changes that block.
A completed group publishes the count and file mapping after the bitmap
and valid root writes. While the root is invalid, only the owning group can
use the provisional count and mapping. Format, inhibit, attachment changes, transport errors
and writes outside an indexed transaction invalidate the proof. An interrupted
write leaves the next writer subject to the on-disk root-valid check.

OFS validates restarted sequence numbers and zero forward links at original
handler append boundaries. Some FFS 40.1 files also point the header's
`firstData` at a later run. The writer accepts this only after matching the
header to a checked run head. Batches retain each sector's own header, payload
length, checksum and forward link. New file-data pointers follow their data
writes. Relocation still passes through the existing durability barriers.

FFS schedules the five-second eligibility timer on the first pending range.
Its first eligible service turn allocates the DMA command buffer and discovers
device capabilities. A waiting packet invalidates a prepared batch. Service
flushes, rechecks the selected bitmap bits and holds the mount through the
synchronous UNMAP. Each turn submits at most one command. Partial failure
invalidates every submitted range. Manual trim builds a fresh full-volume
layout proof before issuing discards.

## Reproducing the measurements

Build fixed 68000 HUNKs into separate directories for each size and AutoTrim
setting. Supply a licensed Kickstart 3.1 ROM through
`COPPERSHARP_KICKSTART31_ROM`. Use the correctness, size and AutoTrim manifests
described in the [ROM harness guide](../tests/CopperFS.Kickstart.Tests/README.md).
Each comparison uses identical media, guest code and payload placement and
alternates the order of the two handlers over three trials. Keep the real
FFS 40.1 binary in the correctness manifest for original-created append tests.

To compare restricted CILica inlining, pass `-CilicaInlining` to
`build/Build-Handler.ps1` for the inlined build. Keep CPU, index size and
AutoTrim setting the same across the two arms. The CILica report in each
artifact directory records the input SHA-256, inlining flag and scalar
replacement flag. Compare the guest timings only after verifying matching
input hashes and checking original-handler readback.

| Test | Measurement |
| --- | --- |
| `ReleaseCandidateStaysOnParWithOriginalHandler` and Chip counterpart | Mixed mutation, explicit flush and verification |
| `LargeWritesPreserveCyclesAndDiskOperations` | Fragmented 96 KiB growth, twelve 8 KiB overwrites and payload verification, Fast and Chip RAM |
| `MeasureFfsMountAndCachedReads` | Cold DeviceProc startup, Exec available-memory change, warm repeated reads, both memory classes |
| `AppendAndOverwriteOriginalFile` | 32 small appends and 12 separated overwrites on an independently inspected FFS 40.1 seed |
| `IndexedVariantCanWriteAnOriginalFormattedVolume` | FFS 40.1 formats DOS2–DOS5, CopperFFS mutates each image and FFS 40.1 verifies it on a fresh boot |
| `AutomaticTrimAfterDeletePreservesReallocatedFiles` and `AutomaticTrimBoundedIdleTurnsReclaimFreedData` | Foreground deletion, collection, poisoned readback, cold and warm UNMAP |
| `TrackdiskOverwritePreservesPointersAcrossFreshBoot` | Actual emulated DD and HD trackdisk writes and fresh-boot readback |

For a supported timing device set `COPPERFS_FFS_TIMING_DEVICE` to `cold` or
`warm`. Warm mode creates and deletes a disposable file and waits for service
before the timed sections. Each guest result records the warm-up UNMAP count.
Leave the variable unset for the unsupported backend. Enable
`COPPERFS_TRIM_PROFILE=1` for service timing and per-turn device counters.
Instrumented service measurements are separate from uninstrumented foreground
parity runs. Mount RAM is the observed Exec available-memory delta across
DeviceProc after LoadSeg, including the handler process and caches.

For a matched on/off run, point both manifest environment variables at the same
JSON file. Set `Original` and `Baseline` to the disabled HUNK, set `Candidate`
to the enabled HUNK, and set `OriginalSha256` to the disabled HUNK's digest.
The following PowerShell command runs the 12 format and buffer cases for the
selected backend. Use a different manifest directory for each backend and
workspace size to keep the independent JSON results.

```powershell
$env:COPPERFS_SIZE_MANIFEST = $env:COPPERFS_CORRECTNESS_MANIFEST
dotnet build tests/CopperFS.Kickstart.Tests -c Release
$suite = 'tests/CopperFS.Kickstart.Tests/bin/Release/net10.0/CopperFS.Kickstart.Tests.dll'
$cases = '(FullyQualifiedName~ReleaseCandidateStaysOnParWithOriginalHandler|FullyQualifiedName~ReleaseCandidateChipMemoryStaysOnParWithOriginalHandler|FullyQualifiedName~LargeWritesPreserveCyclesAndDiskOperations|FullyQualifiedName~MeasureFfsMountAndCachedReads)&(DisplayName~"ffs"|DisplayName~"ofs")'
dotnet vstest $suite "--TestCaseFilter:$cases"
```

### Timer isolation

With the same 256 KiB index and AutoTrim enabled in both handlers, switching
the five-second request from the microsecond unit to the vertical-blank unit
reduced mixed verification cycles by 0.474% on FFS and 0.569% on OFS. Mixed
mutation fell by 0.096% on both. Both arms used the same guest probe and I/O
counts. The timer-only comparison passed all five selected ROM tests, including
poisoned readback and partial UNMAP. The standalone FFS handler uses the
vertical-blank unit for this deadline.

### Comparison with the audited AutoTrim build

The frozen audit harness used the pre-change and indexed AutoTrim-on binaries
on the same packets. The following are three-trial means with Fast RAM buffers.

| Workload | FFS audited → indexed cycles | Change | OFS audited → indexed cycles | Change |
| --- | ---: | ---: | ---: | ---: |
| Mixed mutation and flush | 20,788,967 → 15,266,437 | −26.57% | 34,507,010 → 20,025,860 | −41.97% |
| Mixed verification | 17,944,123 → 17,680,867 | −1.47% | 20,460,073 → 18,539,943 | −9.39% |
| 96 KiB growth | 2,411,917 → 1,116,137 | −53.72% | 6,629,067 → 3,703,530 | −44.13% |
| 96 KiB overwrite | 1,834,530 → 669,973 | −63.48% | 40,262,673 → 2,375,743 | −94.10% |
| 96 KiB read and verify | 4,027,477 → 4,023,523 | −0.10% | 7,344,863 → 5,537,690 | −24.60% |

OFS large-file verification issued 14 physical read commands against the
audited build's three. Its cycle time improved, but it failed the separate
command-count gate.

### Reclamation

The 32 KiB cold-device delete retained the audited coverage, including the file
header and filesystem overhead. Each case issued three discovery commands, two
UNMAPs, and one durability flush during service. Neither service needed a
physical read or write command. These are guest CPU cycles, excluding the
five-second eligibility delay.

| Format | Foreground | Total service | Longest turn | Reclaimed bytes | Combined, audited → indexed |
| --- | ---: | ---: | ---: | ---: | ---: |
| FFS | 198,830 | 103,789 | 75,011 | 33,280 | 666,069 → 302,619 |
| OFS | 187,940 | 104,277 | 76,767 | 35,328 | 2,471,083 → 292,217 |

With capabilities already cached, FFS required 82,805 service cycles (55,295
in its longest turn) and OFS required 84,563 (57,053 in its longest turn).
The warm create, write, close and delete interval took 567,140 cycles on FFS
and 1,020,670 on OFS. Each reclaimed the same bytes in two UNMAPs.
The 16-file fragmentation workload reclaimed 73,728 FFS bytes and 81,920 OFS
bytes in 17 commands each. The 2.25 MiB delete reclaimed 2,392,064 FFS bytes
and 2,510,336 OFS bytes in ten commands each. The original handler read back
the surviving files from the poisoned images.

### Workspace selection

The Exec available-memory delta across cold mount was 185,568 bytes with the
64 KiB index, 382,176 bytes with 256 KiB, and 1,168,608 bytes with 1 MiB
(AutoTrim on, handler already loaded). At 256 KiB, the corresponding off build
used 380,536 bytes. FFS 40.1 used 54,232 bytes in the same measurement.
Cold FFS mount with Fast RAM buffers took 252,883 cycles on and 266,380 off.
OFS took 252,840 on and 266,360 off. With Chip buffers both formats mounted in
253,920 cycles on and about 265,000 off. The allocation preflight is charged
to the first mutation that needs it.

Compared with 256 KiB, 64 KiB slowed large OFS growth by 2.60% in Fast RAM
and 2.58% in Chip RAM, and needed extra device commands. The 1 MiB build
changed all measured mixed, cached-read and large-write phase means by less
than 0.02% in the direct unsupported-device comparison. The default remains
256 KiB. Each size also underwent its own matched on/off comparison on all
three device conditions. The table counts phase means above the +0.100% limit
out of 24 per condition, across both formats and both buffer classes.

| Workspace | Unsupported misses | Cold-supported misses | Warm-supported misses | Largest warm penalty |
| --- | ---: | ---: | ---: | ---: |
| 64 KiB | 7 / 24 | 7 / 24 | 11 / 24 | +3.154% |
| 256 KiB | 9 / 24 | 9 / 24 | 13 / 24 | +2.935% |
| 1 MiB | 9 / 24 | 9 / 24 | 13 / 24 | +3.165% |

All three largest warm penalties occurred on the FFS Chip RAM overwrite.
No workspace size passed the complete performance matrix.

### AutoTrim on and off

Both arms used the 256 KiB indexed handler. Percentages are the change in mean
guest cycles with AutoTrim enabled. The release limit is +0.100% per phase.
The warm candidate completed an UNMAP before each timed workload. The off
arm issued none. The mixed workload in the first two columns includes the
explicit filesystem flush and uses a separate reader verification phase.

| Device | Format and buffer | Mixed mutation | Mixed verify | 96 KiB growth | 96 KiB overwrite | 96 KiB verify | Cached read |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Unsupported | FFS Fast | +0.088% | +0.087% | −0.862% | −2.574% | +0.515% | −0.214% |
| Unsupported | FFS Chip | +0.103% | +0.040% | −0.858% | −2.558% | +0.515% | −0.266% |
| Unsupported | OFS Fast | −0.012% | +0.151% | −0.212% | −0.234% | +0.249% | +0.137% |
| Unsupported | OFS Chip | −0.009% | +0.106% | −0.247% | −0.516% | +0.415% | +0.488% |
| Cold supported | FFS Fast | +0.089% | +0.086% | −0.865% | −2.572% | +0.516% | −0.218% |
| Cold supported | FFS Chip | +0.103% | +0.041% | −0.853% | −2.564% | +0.520% | −0.260% |
| Cold supported | OFS Fast | −0.011% | +0.147% | −0.209% | −0.329% | +0.292% | +0.140% |
| Cold supported | OFS Chip | −0.009% | +0.106% | −0.249% | −0.512% | +0.411% | +0.490% |
| Warm supported | FFS Fast | +0.188% | +0.032% | +0.704% | +2.476% | −0.098% | +0.519% |
| Warm supported | FFS Chip | +0.188% | +0.058% | +0.727% | +2.935% | −0.119% | −0.151% |
| Warm supported | OFS Fast | +0.122% | −0.015% | −0.033% | +0.578% | −0.123% | +0.368% |
| Warm supported | OFS Chip | +0.117% | −0.024% | −0.296% | +0.575% | −0.093% | +0.278% |

Each device condition missed the release limit. The largest on/off slowdown
was the warm FFS Chip RAM overwrite. On the unsupported backend the largest
slowdown occurred in FFS large-file verification. The 256 KiB native HUNK
occupies 93,844 bytes on and 82,852 bytes off. The compiler reports 160 versus
142 bytes for `Dispatch` and 398 versus 290 bytes for `Run` before its final
peephole pass, with zero spills in either version. The separate instrumented
FFS mixed verification recorded 551 more exclusive cycles in `Dispatch`,
4,290 more in the `Run` loop and 10,080 more outside the handler. The whole
guest phase took 17,050 additional cycles. OFS verification added 547, 4,134
and 22,587 exclusive cycles in those respective locations, and 31,110 overall.
The changes in routine size and placement alone do not establish a cycle cost.

### Comparison with FFS 40.1

The same guest workloads ran against FFS 40.1 and the 256 KiB AutoTrim-on
handler. Percentages are the change relative to FFS 40.1. The comparison with
the earlier CopperFFS implementation above uses its frozen probe separately.

| Format and buffer | Mixed mutation | Mixed verify | 96 KiB growth | 96 KiB overwrite | 96 KiB verify | Cached read |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| FFS Fast | +25.505% | +1.658% | +282.285% | +144.562% | +0.842% | +0.082% |
| FFS Chip | +25.517% | +1.873% | +282.212% | +144.206% | +0.831% | +0.927% |
| OFS Fast | +32.240% | +0.258% | −1.742% | −33.181% | −11.568% | +29.900% |
| OFS Chip | +32.320% | +0.439% | −1.712% | −33.137% | −11.500% | +30.128% |

On FFS the mixed mutation issued 154 writes against 47 for FFS 40.1.
Large growth issued 38 against 15, and overwrite issued 37 against 16.
Each of the twelve FFS overwrite packets writes an invalid root, at least one
data transfer and a valid root. Those writes alone account for at least 36
commands under that build's per-packet ordering.
On OFS the candidate used 46 writes against 433 for large growth, but it
needed eight reads against three, which failed the separate I/O gate. OFS
cached reads also remain about 30% slower than the original handler.

### Original-created fragmented files

FFS 40.1 created the 48 KiB seed by alternating appends to two files. Each
subsequent trial started with identical seed bytes. Independent traversal
checked the resulting payload hash, checksums, block ownership and allocation
bits. All payload checks passed. All four timing gates failed.

| Format and buffer | 32 appends, CopperFFS / original cycles | 12 scattered overwrites, CopperFFS / original cycles |
| --- | ---: | ---: |
| FFS Fast | 3,743,927 / 690,747 | 985,000 / 426,223 |
| FFS Chip | 3,748,760 / 695,500 | 985,720 / 426,410 |
| OFS Fast | 8,492,527 / 1,156,890 | 1,537,633 / 653,960 |
| OFS Chip | 8,498,323 / 1,160,017 | 1,534,010 / 653,867 |

The 32 small appends issued 178 FFS writes and 196 OFS writes. In this build,
per-packet invalid-root and valid-root writes account for at least 64 of those.

## Correctness and platform coverage

| Layer | Result |
| --- | --- |
| Host solution | Release build passed. The complete isolated-output Release test run passed 2,299 and skipped eight. Five CPX boot cases and three external CopperSFS media cases were skipped. |
| Portable CopperFFS | All 328 tests passed in Release, including DOS0–DOS5, disk-full admission, aliases, bitmap boundaries, interrupted writes, reuse, fallback capacity and discard failure handling. |
| Native | All six standalone 68000 HUNK builds (64, 256 and 1024 KiB, on and off) reported zero reachable managed allocation sites. All 199 selected CopperStart replays and eight selected 68000/68020 instruction cases passed. |
| Full ROM | 19 fixed-disk, geometry, service and interrupted-discard cases passed. Four DD/HD trackdisk fresh-boot write/read cases passed. Four further original-format/Copper-write/original-read cases passed on DOS2–DOS5. Only DOS0 and DOS1 were qualified for discard. |
| Hardware | Physical controller and SSD command latency were not measured. |

The qualification used the 256 KiB candidate with SHA-256
`A6F2AE8949623C3A0D61A879523861CD81D4EB8AACF12C129152FB6A8E477E93`.
The matched disabled build was
`F53C9D79B172DFB7B223BF9D9D33E40F9DC0785CB512402489A5D1D764871CEB`.
