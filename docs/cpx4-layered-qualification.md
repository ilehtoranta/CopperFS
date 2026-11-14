# CPX4 layered qualification

This is an implementation checkpoint, not certification of the complete native
handler. CPX4's format remains version 4; CPX3 and the existing FFS/SFS identities
and formats are unchanged. The native resource ceiling remains 72 KiB at a
4096-byte metadata page, including the 4096-byte task stack. After startup,
native filesystem operations use caller-owned storage without per-operation
heap allocation.

The current SetOwner and Format/label implementation and measurements are recorded in
[the native progress report](cpx4-native-progress.md). The `LBL1` extension adds
UTF-8 volume labels to the CPX4 state page; older CPX4 readers reject its larger
payload. Legacy unlabeled images remain readable. The new blank-media ROM
workload exercises actual DOS Format/Relabel alongside the existing growth,
depth-eight and recovery fixtures. Earlier results below retain their original
source and binary identities. Ownership uses version 2 attribute pages and a
sticky `OWN1` state extension; older builds reject owned volumes. A fifth ROM
fixture exercises SetOwner, comment preservation, ExAll level 7 and remount with
eight-level trees.

The SetOwner checkpoint passes 2,152 Release solution tests (four skips), 30
focused ownership/notification cases, 23 direct Release MC68000 component cases
and 141 CopperStart replay cases. All five full-ROM workloads pass at exactly
512 KiB with a 4096-byte task stack: measured peak 1,700 bytes, guards intact,
minimum free memory 158,576 bytes. Ownership's dedicated depth-eight path peaks
at 1,508 bytes. Its 3,560 crash/replay injections are host tests, separately
reported from compiled and full-ROM coverage. Exact hashes, memory accounting,
scope and receipts are recorded in the current native progress report.

## Reproducing tests

Run from the repository root with the existing CopperSharp68k, CopperScreen and
CopperStart source dependencies available:

```powershell
./build/Test-Cpx4Qualification.ps1 -Tier fast
./build/Test-Cpx4Qualification.ps1 -Tier integration
./build/Test-Cpx4Qualification.ps1 -Tier campaign
./build/Test-Cpx4Qualification.ps1 -Tier fast -Seed 12648430
./build/Test-Cpx4Qualification.ps1 -Replay artifacts/cpx4-qualification/failure-12648430-512.minimal.json
./build/Test-Cpx4Qualification.ps1 -Tier fast -RequireRom
```

The replay path above illustrates a generated failure artifact; it is not a
checked-in failing case. The runner accepts `-ArtifactDirectory` below the
repository's ignored `artifacts` directory. It restores environment variables on
exit, propagates failed commands, and records command results and source hashes.
Sources changing during a campaign invalidate its final receipt. A requested ROM
run requires a licensed ROM at `COPPERSHARP_KICKSTART31_ROM`; a missing ROM fails
the request instead of turning it into a pass.
Run full-ROM workloads serially, after host and compiled-probe campaigns finish.
The host-time guards include contention from other emulator processes; the
Inhibit/ExAll checkpoint retained timed-out attempts separately. Its quiet
depth-eight retry still reached the host watchdog while actively processing
ExAll, so the depth-only host watchdog was raised from 12 to 15 minutes, then
to 30 minutes after the second retry reached fresh-mount checks but timed out.
RAM, task stack, the 180,000-frame depth limit and native HUNK remained unchanged.
Requested ROM tests build the instruction-test project directly in an isolated
Release artifact tree. The umbrella solution can map external emulator projects
to Debug even when invoked with `-c Release`; reusing that output caused a sprite
assertion before the handler workload in an intermediate run. That aborted run is
retained separately. The runner does not suppress assertions or call that a
filesystem pass; the Release harness is identified by its assembly hash.

| Tier | Generated host sequences | Other work |
| --- | --- | --- |
| Fast | Four seeds × 128 operations | Packet contracts, corruption/geometry, arithmetic/transport, pool and scheduling boundaries |
| Integration | 32 seeds × 512 operations | Release solution build/tests, systematic crash sweeps, standalone build, compiled probes, CopperStart replay |
| Campaign | 1,024 seeds × 1,024 operations | Integration work and enabled 512 KiB ROM workloads |

`-Seed` selects one seed at the tier's operation count. Replays execute the exact
serialized operations. Ordinary solution runs default to the four fast seeds and
explicitly skip the optional ROM theory. `-RequireRom` enables ROM work in either
of the shorter tiers. The campaign tier is an executable configuration; its
million-operation run must not be inferred from successful shorter runs.
When an artifact directory is supplied, the generated runner updates
`generated-sequences.progress.json` after each fully verified seed. This reports
sequence progress only; the campaign's final `receipt.json` determines whether
all requested layers passed.

## Oracles and replay

`Qualification/Scenarios.cs` defines a host-only operation protocol, an in-memory
logical model and a specified xorshift32 generator. Traces contain explicit
names, offsets and bytes, independent of allocated object IDs or Random runtime
versions. Create means create-or-truncate, matching DOS FindOutput. Missing-name
and rename-collision outcomes are meaningful results, including in minimized
subsequences. The current generator also creates hard links. Its logical model
tracks shared file identity independently of disk IDs: writes, resizing and
create-or-truncate update every alias, while rename and deletion affect only the
selected entry. A compact deterministic alias lifecycle is a permanent regression.

The three current drivers execute the frozen reference implementation, resumable
native engine, and DOS dispatcher on separate fresh images. They compare each
result to the logical model. Periodic checks compare directory membership and
sizes and contents through an independent decoder, read contents through the driver under
test, and run the reference ownership checker. Agreement between two drivers
never overrides a disagreement with another oracle. Classic DOS Seek refuses
beyond-EOF positions, so the common trace uses positions that fit that ABI;
separate engine tests exercise direct sparse writes above 4 GiB.

Every continuation is reconstructed from caller-owned storage before resuming.
I/O completions are delayed, repeated Step calls must retain the request, and a
competing operation must be refused. Failures write the complete trace, exception
and a delta-debugged reproducer. Minimization preserves the failing driver and
exception signature and restarts from fresh media for each candidate. The search
has a 128-replay bound; it does not promise a globally minimal trace.

The same protocol admits additional drivers, but a generated-scenario driver for
the **actual standalone HUNK is still pending**. Compiled component and actual
HUNK/ROM workloads currently use dedicated probes; they are reported separately.

## Crash, corruption and boundary coverage

The fault device now retains every submitted version of a sector. Crashes can
occur before sector submission, after submission, and on either side of a flush.
Five systematic schedules retain none, reverse-order versions, alternating
reverse-order versions, the last submission, or torn 128-byte prefixes. A
successful barrier persists the last submitted version and retires all pending
versions. Tests exercise exact ordered subsets for small version sets. This is a
power-loss model, not a claim about all possible controller firmware behaviour.

Namespace, extent promotion/splitting, 42-image journal publication, recovery and
reserve-exhaustion sweeps use this strengthened model. Additional sweeps cover
native comments, sparse extension, hard links, inline/external soft links and
destructive reformatting. The reformat oracle accepts only an intact old volume,
a complete new volume, or an unmountable image. It never requires an in-progress
destructive format to remain mountable. External in-place overwrites remain
non-atomic; their old EOF prefix and untouched ranges must be preserved.

Every I/O boundary in representative create, rename, delete, truncate and write
operations is also tested with device failure, short completion and cancellation.
Uncertain writes may already have reached media. Tests require poisoned-mount
behaviour, no buffer reuse while I/O remains live, stale-token rejection after
reinitialization, and successful ownership checking after recovery.

All ten legal sector/allocation-unit combinations have mutation/remount coverage
and byte-for-byte formatter comparison against the frozen reference. Corruption
tests reseal malformed pages to distinguish structural checks from CRC checks:
oversized/empty counts, wrong payload lengths, impossible levels, cycles,
duplicate fences, unaligned children, wrong owners and future generations must
fail in bounded read-only traversal. Anchor fallback and loss of both anchors
are separate cases. The independent decoder now accepts a surviving valid
backup anchor and a surviving valid checkpoint without modifying the image.

## Notifications and timed record locks

CPX4 now implements classic record packets 2008/2009 and the existing SFS2
26402/26403 paired range-pointer ABI. Locks are cooperative and belong to the
opening handle, with shared/exclusive conflict rules, exact-range release and
recursive acquisitions by the same handle. Other handles referring to a hard
link alias share the conflict identity. Invalid modes, range overflow, exhausted
grants, immediate collisions and timeouts have distinct DOS errors. These rules
follow the [classic LockRecord contract](https://developer.amigaos3.net/autodocs/dos.library/LockRecord.html).

The fixed pools remain 32 grants and 16 waiters. Parking a conflicting timed
request returns filesystem-operation ownership to dispatch while retaining the
DOS packet in its waiter slot. Timer start, cancellation, completion and packet
reply are separate events. A granted or cancelled waiter retains its timer
storage until completion is reaped; only then can its packet be replied and its
slot reused. Slot generations reject stale completions. Closing an owner cancels
its waits with InvalidLock and releases its grants, including a provisional
grant whose timer is still being cancelled. Ordinary reads, writes and unrelated
metadata operations continue during a wait.

Notifications use eight fixed watch slots, each with eight fixed message buffers.
Initial notifications require an existing object; absent names remain watched.
Signal delivery and message delivery are supported. WAIT_REPLY coalesces changes
while a message is outstanding; a full message pool also coalesces a pending
change. One application withholding replies cannot starve another watch. File
data changes notify after close, while namespace and attribute changes become
eligible only after transaction retirement, consistent with the
[StartNotify contract](https://developer.amigaos3.net/autodocs/dos.library/StartNotify.html).
Rename checks both entry names and both parent directories. RemoveNotify detaches
the caller's request immediately; a held message keeps its private slot alive
until acknowledgement. Replies are matched against owned addresses rather than
trusting application-writable message links.

The new host regressions reconstruct dispatcher state between steps and cover:

- All grant/wait/watch slots, exhaustion, release and reuse; simultaneous full
  pools; stale and duplicate timer completions; timeout, cancellation and close
  races; cancellation before submission and completion during unrelated I/O.
- Four explicit seeds × 512 classic/wide record operations against an independent
  cooperative-lock model, including zero-length and above-4-GiB ranges.
- Initial, signal, WAIT_REPLY and ordinary message delivery; message-pool
  overflow; held-message removal; tampered private links; invalid flags/signals;
  255/256-byte name limits; hard-link identity; create/delete/recreate and rename.
- No notification before all six journal barriers, and no publication after an
  uncertain first write. Full-pool event phases visit at most 64 resource headers;
  the instrumented host paths allocate zero bytes after generic JIT warmup, and
  arena guards remain intact. Compiled/native allocation and stack evidence is
  reported separately, not inferred from host allocation counts.

An initial starvation regression failed when a full first watch prevented the
next watch from delivering. The dispatcher now skips full watches with a bounded
scan; the regression passes. Matching-source compiled-native and full-ROM
qualification of this extension is recorded below. The earlier lifecycle
measurements remain a separate checkpoint and do not qualify this larger handler.

## Earlier native changes

Sparse engine writes no longer require a preceding resize. Before publishing an
extension, the continuation clears and persists a mapped old EOF unit's hidden
suffix. It does not publish a larger size until the first write chunk retires.
Inline promotion preserves its original prefix. Both halves of a position above
4 GiB participate in lookup and size updates.

Comments use the existing tag-1 attribute page. BeginComment copies at most 79
Latin-1 bytes, converts at most 64 characters per phase, allocates at most one
metadata page, and commits within six images. Empty comments release their page;
replacement reuses the existing page. DOS SetComment uses the existing BPTR
string packet ABI.

Hard-link creation uses the existing 22-image/10-new-page reservation and updates
the entry, parent, link count and volume count atomically. Soft links use inline
data through 160 UTF-8 bytes and a bounded owned page through 768 bytes. Encoding
and link reads process at most 64 characters/bytes per phase. Core tests verify
ownership until the last hard-link name is deleted and reclamation of external
soft-link pages. DOS packet and library-level coverage is recorded separately
below; a passing core operation alone does not qualify a packet family.

The portable formatter has explicit phases for old-anchor invalidation,
one-window bitmap construction, metadata bootstrap, metadata persistence, backup
anchor publication and primary publication. It accepts an explicit volume ID,
geometry and optional persistent label. It uses paired arithmetic and the existing scratch.
Formatting and link payloads share a documented top-level operation-arena union;
neither can execute inside the other. Existing operation slots and pools were
not enlarged.

The coordinated dispatcher change supplies full FIBs, bounded ExNext tokens,
Info/DiskInfo and the existing wide examination/QueryAttribute ABI. See the
executable `Qualification/PacketContracts.cs` inventory and
`docs/cpx4-native-progress.md` for the exact packet and ROM checkpoint.

The subsequent dispatcher checkpoint implements FhFromLock, CopyDirFh,
ParentFh, file/hard-link Parent, ChangeMode, keyed software write protection,
and MoreCache. Successful conversion consumes exactly the input lock; admission,
I/O, protection, wrong-type and pool-exhaustion failures preserve it. Mode changes
exclude their own resource when checking conflicts. The fixed six scratch pages
are not a persistent block cache, so MoreCache reports zero without allocation.
Protected Flush and shutdown issue a persistence barrier without metadata pruning
or TRIM. Read-only device policy remains independent of software protection.

MakeLink supports regular-file hard links and bounded Latin-1 soft-link targets.
ReadLink decodes UTF-8 back to Latin-1, expands one relative or absolute target,
and retains the unconsumed path suffix. It checks output capacity before writing.
It explicitly rejects non-Latin-1 targets; it does not invent a lossy conversion.
The current input-path limit is 255 bytes, while stored targets can occupy 768
UTF-8 bytes and the expanded output may be longer. Parent slash components work
from both directories and file locks. Directory hard links remain unsupported.
As required by the classic packet protocol, lookup reports error 233 at a soft
link and each ReadLink expands one link. DOS owns repeated expansion and its
cycle limit; the ROM workload includes opening a cyclic pair. This is not an
unbounded recursive resolver inside the handler.

Classic packet contracts are checked against the existing repository dispatchers
and the [AmigaDOS RKRM, chapter 14](https://developer.amigaos3.net/sites/default/files/downloads/2024-10/Amiga_ROM_Kernel_Reference_Manual_DOS.pdf).
Later OS packet numbers listed there do not automatically enter this milestone.
One disagreement is resolved explicitly: ACTION_SAME_LOCK returns Boolean
DOSTRUE/DOSFALSE, whereas dos.library SameLock returns its comparison enum.
CPX4 now uses the packet convention and checks the library result in the ROM
probe. The existing FFS/SFS dispatcher was not changed by this correction.

## Inhibit and ExAll checkpoint

Inhibit is now an idempotent Boolean lifecycle operation. Entry refuses live
handles, locks or retained record-wait ownership with error 202. It publishes
the inhibited state only after a successful persistence barrier. During inhibit,
ordinary media operations return error 226, information queries describe absent
media, and resource cleanup remains available. Uninhibit rereads and recovers
the image before allowing access; a failed remount leaves access disabled.
Software write protection survives this cycle. Watches retain their bounded
message ownership while name refresh is suspended; cursors retire at the media
boundary.

The typed storage-stop inputs retain outstanding request/buffer ownership until
completion is quiesced. Cancellation and removal cannot advance a pending read's
file position. Old locks/handles and pending record replies must be released
before remount. Native transport cancellation and media-change coverage is
recorded separately from the host dispatcher tests.

ExAll implements ED_NAME through ED_COMMENT using word-aligned, NUL-terminated
records in the caller's buffer. Its final error-232 reply can still contain
entries. ExAll and ExNext share the existing **16 enumeration slots**; each
cursor retains a logical tree key and reseeks the current root. ExAllEnd retires
the matching cursor without touching its data-buffer argument. Cursors can move
to a surviving copied lock on the same directory; final unlock, EOF, admitted
scan failure, restart and inhibit retire them. Stale or foreign control keys
cannot consume another cursor. No directory-sized cache or new pool is allocated.

Initial pattern/hook requests return error 209 before taking a cursor, allowing
dos.library's enumeration/filtering emulation. Changing to a filter during a
native scan returns error 115 and retires that scan. At this older checkpoint,
ED_OWNER returned error 115 because there was no persistent owner encoding;
the SetOwner checkpoint supersedes that limitation with level 7 support.
The packet ABI and fallback follow the
[official ExAll autodoc](https://developer.amigaos3.net/autodocs/dos.library/ExAll.html)
and [ExAllEnd autodoc](https://developer.amigaos3.net/autodocs/dos.library/ExAllEnd.html).

The new host tests cover all six levels, final entries at EOF, exact/undersized
buffers, copied-lock transfer, mixed ExNext/ExAll pool exhaustion, stale/foreign
keys, restart, serial exhaustion, mutation reseeking, Latin-1 and UTF-8 aliases,
corrupt names, I/O failure and cleanup. Inhibit tests cover delayed/failed
barriers, failed remount, replacement images, all handle/lock/wait/watch slots,
held notification messages, cursor retirement, software protection, and storage
cancellation/removal during a pending read. A separate full-pool ExAll test
reconstructs the dispatcher each step and measures at most 64 resource-header
visits and 64 output bytes per phase, zero warmed host heap allocation, stable
outstanding I/O and intact arena guards. Metered Inhibit validation and retirement
run in separate phases, including entry while media is already unavailable.

Fourteen additional stop/recovery cases cancel or remove media at every one of
the six publication barriers, let the already-submitted request finish, and
require termination without further original-operation I/O. Remount must recover
old contents before the third barrier and new contents afterward, passing both
independent content/ownership checks and the reference checker. An interrupted
multi-chunk write reports and recovers its retired 512-byte prefix. A cancelled
in-flight chunk can still become durable without being reported as completed;
the handler does not retract already-retired progress or claim that cancellation
undoes published metadata. These are deterministic stop/recovery cases, separate
from the broader power-loss persistence campaign.

The fast tier passed **282 tests**, including the 35 new lifecycle, ExAll,
scheduling and stop/recovery cases. All **125 captured source hashes** stayed
unchanged during that run. The later host-only depth watchdog adjustment changed
`CpxBootTests.cs`; it did not change the fast suite's executed code or native
handler. Its receipt is
`artifacts/cpx4-exall-final/fast/receipt.json`. That host-only receipt records the
HUNK present before the final native rebuild; it does not qualify that binary.
The native manifest and ROM receipts below identify the final HUNK separately.
CopperStart geometry/buffer replay
passed **141 tests**, recorded in `artifacts/cpx4-exall-final/replay-results`.
The Release solution build passed with one existing CopperSharp compiler
nullable-reference warning; the final incremental rebuild passed without
warnings or errors. The solution tests passed **2,004 cases**, with four
explicit skips (three external SFS media prerequisites and the opt-in ROM
theory). This includes **336 CPX4 host**, 296 FFS and 1,225 SFS tests. Results are
in `artifacts/cpx4-exall-final/solution-results`. That build captured 19 compiled
tests before the last native additions; the final direct suite below separately
qualifies all 22 current compiled tests. All three full-ROM workloads below
passed on the rebuilt handler; earlier checkpoint measurements remain separate.

The rebuilt HUNK is **175,568 bytes**, SHA-256
`2A2EE840855B5F70CCB15A2576C76DA01A572E63FC382CB74CD0973BCD20B9BF`.
All 40 native source inputs match its manifest. The direct Release native suite
passed **22 compiled probes**, recorded in
`artifacts/cpx4-media-native/final-results`. They include delayed abort/reap of
read, write, flush and discard; bounce/split-transfer cancellation; startup
subscription exhaustion/refusal; callback register preservation; failed detach;
and the actual compiled handler loop's cancellation/media-event reply ordering.
The actual-loop stop probes measured 576/576/552-byte stack peaks; the record-wait
loop measured 708 bytes. These use simulated Exec/device boundaries and are
component measurements, separate from the full-HUNK ROM stack below.

The new ROM workload includes direct packet exhaustion and actual dos.library
ExAll/ExAllEnd, copied-lock continuation, and both pattern and rejecting-hook
fallback. The first normal-image attempt reached the hook fallback at substage
520 but hit its 480-second host-time limit after 65,826 frames, while the host
and compiled suites were also running. Its sampled PC was in the page-checksum
loop, with dispatcher ACTION_EXAMINE_NEXT active. It reported no DOS error and
measured 1,700 stack bytes with an intact guard; these are **failed-run
observations**, not a passing full-ROM gate. The failed log is retained in
`artifacts/cpx4-media-boot.log` and
`artifacts/cpx4-media-native/failed-growth-wall-limit`. The normal-image retry
passed with unchanged source, RAM, stack and run limits: **68,845 frames,
390.993 host seconds**, whole-handler peak **1,700 / 4,096 bytes**, guard intact.
ExAll peaked at 1,088 bytes; Inhibit and media-event work at 1,520 bytes each.
Minimum free RAM was 168,360 bytes. FreeAfterWork equalled FreeMounted at
168,968 bytes. The initial depth run also hit its host-time limit (720.001
seconds, 139,409 frames) while checksumming metadata in the existing notification
workload, before reaching ExAll. It likewise reported no DOS error. Both failed
attempts remain separate from passing receipts. Committed-journal recovery then
passed in **68,931 frames / 318.160 host seconds**, with the same stack peaks and
minimum free RAM as the normal-image retry. The quiet depth retry also hit the
12-minute host watchdog, at **153,247 frames / 720.004 seconds**, in the ExAll
pool-exhaustion setup (substage 513). It reported no DOS error, an intact guard,
and a 1,700-byte stack peak, but did not reach Inhibit or shutdown. This failure
is retained in `artifacts/cpx4-media-depth-retry.log`. A further serial retry
uses a 15-minute host watchdog while retaining the original 180,000 emulated
frames, 512 KiB RAM, 4 KiB stack and unchanged HUNK. It also reached its host
watchdog: **163,708 frames / 900.006 seconds**, after ExAll completed, during
uninhibit/remount (substage 407). It reported no DOS error, a 1,700-byte stack
peak and intact guard, but did not finish the media-event/shutdown workload.
This failed attempt is retained in `artifacts/cpx4-media-depth-final.log`.
The final serial run, with a 30-minute host watchdog and the same 180,000-frame
bound, **passed in 167,162 frames / 657.707 seconds**, including media-event and
shutdown checks. It finished below even the original twelve-minute watchdog,
demonstrating the variation in host throughput between attempts. Earlier
timeouts remain failed runs; the passing result does not erase them. The host
test assembly hash changed with each watchdog adjustment.

The actual compiled DOS probe HUNK is unchanged across that host-only edit:
SHA-256 `83566AC470A43C2833C1D6E305A9D8FDD48D417C22593335827820CC16E2F223`.
Normal/recovery receipts identify their earlier host test assembly
(`914CFA04CACA6FB16C2CEC2EE8C678C776B24EFC3DFD7CECB6FD96DCF6E9C7EA`);
the failed 15-minute depth retry identifies the rebuilt host assembly
(`C655B33F828162E5C66A9FC205C77069EFFB4F2DD0E50EE36C9B172BBD5F7CAA`).
The passing depth receipt identifies the final host assembly
(`B8E0207CA64527A2388C4F831217A5D48D2F2B577035CB8AAE052DD48A959BFC`).
The checkpoint retains both harness/input manifests and the old assembly under
`artifacts/cpx4-media-native`, alongside the unchanged handler source manifest.

| Passing ROM workload | Frames | Host seconds | Stack peak | Guard |
| --- | ---: | ---: | ---: | --- |
| Normal / growth | 68,845 | 390.993 | 1,700 bytes | Intact |
| Committed-journal recovery | 68,931 | 318.160 | 1,700 bytes | Intact |
| Eight-level trees | 167,162 | 657.707 | 1,700 bytes | Intact |

Receipts are `artifacts/cpx4-boot-512k/{growth,recovery,depth8}/qualification.json`,
with the preserved checkpoint under `artifacts/cpx4-media-native/checkpoint`.
The passing depth test log is `artifacts/cpx4-media-depth-complete.log` and its
TRX is under `artifacts/cpx4-media-native/depth-complete-results`.

All three passing workloads used the actual standalone HUNK under
licensed Kickstart 3.1 with MC68000 and exactly **524,288 bytes of chip RAM**, no
fast or slow RAM. All recorded these allocations and stack measurements:

| Measurement | Bytes |
| --- | ---: |
| Resident arenas | 69,632 |
| Task stack allocation | 4,096 |
| Resident arenas plus stack | 73,728 (72 KiB) |
| Loaded handler segment allocation | 156,864 |
| OS/device net mount allocation | 1,272 |
| Minimum free memory | 168,360 |
| Whole-handler stack peak | 1,700 |
| ExAll stack peak | 1,088 |
| Inhibit / media-event stack peak | 1,520 |

Stack guards remained intact. Free memory after work equalled its mounted value
of 168,968 bytes in all three runs; after shutdown it was 243,904 bytes.
These are measurements of the executed workloads,
not a mathematical maximum over all possible paths. The emulated hardfile
refused change-interrupt registration, so the ROM media-removal workload injected
the callback's latch and Signal output into the actual handler. Separate compiled
tests execute the callback and its ownership paths against simulated Exec/device
boundaries. Neither result qualifies physical controller interrupt delivery or
SSD persistence behaviour; physical media remain untested.

## Earlier notification and record-lock qualification

The final Release solution build passed with zero errors and one existing
nullable-reference warning in CopperSharp's compiler. The solution tests passed
**1,954 cases**, with four explicit skips: three external SFS media
prerequisites and the opt-in ROM theory. This includes **301 CPX4 host tests**,
296 FFS tests and 1,220 SFS tests. Receipts and logs are under
`artifacts/cpx4-record-lifecycle/final-solution-results`, `final-solution-build.log`
and `final-solution-tests.log`. An initial build caught a concurrently edited probe's
CS0819 error; its failed log is retained separately. The corrected build and
test run are the results reported here.

The final fast tier passed **247 tests**, including four seeds × 128 filesystem
operations and four explicit seeds × 512 record-lock model operations. All
**113 captured source hashes** stayed unchanged. Its reproducible receipt is
`artifacts/cpx4-record-lifecycle/final-corrected-fast/receipt.json`. Environment
restoration was also checked with the diagnostic variable initially set.
CopperStart geometry
and transfer replay passed **141 tests**, with receipts in
`artifacts/cpx4-record-lifecycle/copperstart-results`; its build retained two
existing unused-variable warnings in CopperStart.

The final solution and direct Release instruction-project runs passed **all nine
compiled probes**, including
the new timer adapter's delayed abort/reap, natural timeout, provisional grant,
maximum timeout conversion and partial-startup cleanup tests. It is recorded in
`artifacts/cpx4-notify-final/compiled-results`. The timer component compiled
to 134,148 bytes with no managed allocation sites. Its observed stack peaks were
572 bytes for normal timer processing and 280 bytes for startup failure; these
are **component measurements**, not whole-handler stack bounds.

The first standalone handler in this iteration was **164,400 bytes**, SHA-256
`A0EF55AEF0A541D87BD79AA3CBFECB85A899ADDD634837F1386783652A7461D5`.
All 37 native source inputs matched its build manifest. The first new 512 KiB
normal-workload attempt failed at its 120,000-frame limit during record-lock
processing (456.560 host seconds, no explicit DOS error). Its observed stack
peak was 1,668 bytes, record-workload peak 1,292 bytes, and the guard was intact;
notifications had not been reached. These are **failure-path observations, not
a successful qualification**.

The diagnosis found two native-loop defects. `Exec.Wait` consumes returned
signals, so discarding its result lost cancellation arriving while idle. Also,
the last replied waiter could leave bounded record-rescan work behind after all
retained resources and timer requests were gone. Excluding that work from event
servicing, while still using it to prevent sleep, made the higher-priority
handler spin and starve its DOS caller. The loop now retains returned signals
and admits pending record work independently of retained resources.

Two additional tests execute the **actual compiled `NativeCpxHandler.Run`**
against a simulated Exec boundary. They require the loop to reach quiescent
`Exec.Wait` after the final reply, and inject Ctrl-C exclusively through Wait's
return value. Both pass with a 704-byte observed stack peak, intact guards and
no managed allocation sites. They use a 3,000,000-instruction termination limit;
the successful paths execute 149,353 and 89,391 instructions. A host regression
separately covers the residual rescan state after immediate reply consumption.

The corrected HUNK is **164,700 bytes**, SHA-256
`06A21D8BE8581D80DCD49A5E923658BBA4FB5C0CDCF564E6303F0EBAB705A37D`.
Short normal and recovery ROM diagnostics now pass the asynchronous lifecycle;
the short depth diagnostic reaches notification processing but exceeds its
intentional 10,000-frame cap. These diagnostics are distinct from the completed
full-ROM qualification. All three corrected full workloads passed with unchanged
RAM, stack and full-run limits. The qualification runner clears
`COPPERCPX_ASYNC_DIAGNOSTIC` so an
inherited diagnostic setting cannot silently replace a requested full ROM run.

| Complete 512 KiB MC68000 workload | Frames | Host seconds | Whole-handler stack peak | Guard |
| --- | ---: | ---: | ---: | --- |
| Normal image | 59,408 | 238.49 | 1,696 bytes | Intact |
| Eight-level object and directory trees | 142,755 | 538.81 | 1,696 bytes | Intact |
| Committed-journal startup replay, then full workload | 59,494 | 219.74 | 1,696 bytes | Intact |

Each run used zero fast/slow RAM and a 4096-byte task stack. Record-lock processing
peaked at 1,320 bytes and notification processing at 1,696 bytes. Loaded segment
allocation was 147,560 bytes, the fixed arena 69,632 bytes and net OS/device mount
allocation 1,216 bytes. Minimum free RAM was 180,432 bytes; FreeAfterWork equalled
FreeMounted (181,040 bytes). Final media passed independent content/ownership
checks. These are executed-path measurements, not an exhaustive stack maximum.
The nine compiled probes and three full-ROM passes are recorded separately in
`artifacts/cpx4-notify-final/{compiled-results,boot-results}`. The matching HUNK,
37 native source inputs, harness hashes and all three receipts are archived in
`artifacts/cpx4-notify-final/checkpoint`. The initial failed run remains preserved.
Physical-media qualification has not been performed.

## Earlier lifecycle qualification

The lifecycle/link integration sequences passed 32 seeds × 512 operations on all
three host drivers, including shared hard-link identity and independent content
decoding. The final coordinated Release solution run passed **1,889 tests**, with
four explicit skips (three external SFS media cases and the opt-in ROM theory).
It includes **248 CPX4 host tests** and five compiled probes. Receipts are in
`artifacts/cpx4-lifecycle-final/results`. The strengthened
reformat sweeps covered 3,100 crash schedules
(472 and 148 cuts, each with five persistence schedules), including temporarily
unmountable images as required by the destructive-format contract.

The small-state filesystem sweeps passed **176 exhaustive states**: all 128
ordered subsets at 38 mutation cuts with at most three pending sector versions,
plus 48 interrupted-replay states. These enforce old metadata before publication,
new metadata after the third durable transaction barrier, unchanged contents,
and independent ownership checks after recovery. Larger pending sets still use
systematic schedules; this does not claim exhaustive coverage of every trace.
Object-tree insertion is now tested at every height from one through eight:
successful full-spine promotion below the limit and refusal of a ninth level
without publishing a partial update. The height-seven cascade uses 19 journal
images; each successful case stays within the 42-image ceiling.

The separate compiled core probe exercised formatting, a sparse write above
4 GiB, an external Latin-1 soft link, a hard link, a maximum-length comment, reads
and final deletion/cleanup. It passed 96,009,546 emulated MC68000 instructions and
487 storage completions with a **1,324-byte stack peak** and intact 4096-byte
guards. Its 95,546-byte compiled code had no managed allocation sites. This is
component execution, not a DOS or full-ROM result. Receipt:
`artifacts/cpx4-layered-native/results`.

CopperStart's existing geometry/buffer replay passed 141 tests; the coordinated
rerun is logged in `artifacts/cpx4-lifecycle-review/replay.log`. The final Release
solution build succeeded with zero warnings/errors. Actual-HUNK ROM receipts
are separate from host and component results. All three final workloads passed:
growth, maximum-depth trees, and committed-journal startup recovery.

| Final native measurement | Result |
| --- | --- |
| CPU / RAM | MC68000; 524,288-byte chip RAM, zero fast/slow RAM |
| HUNK | 148,872 bytes; SHA-256 `40E97ECFC49CAE78790453EB75ECD9C480556018FEA2944CC10DEA46A0B7EE43` |
| Source match | All 34 native inputs matched the build manifest |
| Allocated task stack / measured peak | 4,096 / 1,636 bytes; guard intact in all three boots |
| Resident arena | 69,632 bytes; 73,728 bytes including the allocated stack |
| Loaded segment allocation | 133,616 bytes |
| Net OS/device mount allocation | 536 bytes |
| Minimum free memory | 198,368 bytes in each workload |
| Release emulator SHA-256 | `4BB0BEF378332A66C61B130A3100C361D64B433863405F2DC8DAB6D7CEC84329` |

Receipts and harness manifests are archived in
`artifacts/cpx4-lifecycle-qualified/checkpoint`; the live per-workload receipts are
under `artifacts/cpx4-boot-512k`. These cover the executed operation/failure
families, including actual DOS library conversions and cyclic soft-link opens;
they do not certify every possible native failure path.

The expanded eight-level ROM workload initially reached the old allowance at
119,357 frames / 480.005 seconds without reaching completion. Its stack guard was
intact and its observed peak was 1,636 bytes, but this result is a **timeout, not
a pass**. The coordinated retry increases only that fixture's host allowance to
180,000 frames / 12 minutes. MC68000, exactly 512 KiB RAM, the 4096-byte task
stack, assertions, and the HUNK remain unchanged. Both results are retained.
The retry passed at **129,797 frames / 478.152 seconds**, confirming that the
expanded workload exceeded the former frame allowance. The earlier timeout is
not retroactively counted as a pass.

The final fast runner passed **194 tests**, including four seeds × 128 operations,
with all 102 captured source hashes unchanged. Its receipt is
`artifacts/cpx4-qualification/final-qualified-fast/receipt.json`. The final
**32 seeds × 512 operations** repeat against the scheduling revision also passed,
with all 102 source hashes unchanged before and after execution. Its separate
host-only receipt is
`artifacts/cpx4-qualification/final-qualified-integration-sequences/receipt.json`.

## Open acceptance gates

The full requested milestone remains incomplete. The following are outstanding,
and successful tests of explicit refusal must not count them as implemented:

- ReadLink input paths beyond 255 bytes and non-Latin-1 stored targets. Directory
  hard links conflict with CPX4's single-parent
  directory model and are explicitly refused.
- Native ExAll filtering uses DOS emulation; ED_OWNER is now implemented.
  Notification acknowledgement/removal, timed record-lock waits and Inhibit are
  implemented; record-wait cancellation covers owner close and Ctrl-C.
- Periodic TRIM maintenance across unrelated packets remains open. Startup
  now includes [DMA buffer reservation](cpx4-native-progress.md#dma-constrained-allocation).
  The native event loop drives [primary/backup geometry
  discovery](cpx4-native-progress.md#startup-geometry-discovery),
  mount/replay, shutdown barriers and detach in bounded visits. See the
  [lifecycle checkpoint](cpx4-native-progress.md#bounded-startup-recovery-and-shutdown).
- A compiled-handler driver for serialized scenarios; complete operation-family
  failure/stack coverage; larger corruption and persistence campaigns, including
  broader exhaustive small filesystem traces; the full 1,024-seed campaign.

SetOwner now uses the defined version 2 attribute encoding and sticky `OWN1`
state-page feature. It preserves comments and is exposed through classic/wide
FIB queries and ExAll level 7. See the ownership section of the design document.
Persistent volume labels, RenameDisk and DOS Format now use the explicit `LBL1`
state-page extension described in `new-filesystem-design.md`. Older CPX4 readers
reject the extended payload. Native qualification for this revision is recorded
in `cpx4-native-progress.md`; previous receipts qualify their own source snapshots.
Physical SSD/controller qualification remains outside this milestone.

Host, compiled-component, actual-HUNK and full-ROM receipts must always identify
their source checkpoint. A measured stack peak covers executed paths, not a
mathematical maximum. ROMs, media, binaries and generated receipts stay ignored.
The ROM gate checks the HUNK and every native source against the standalone build
manifest before and after execution; stale builds fail rather than qualifying
the current source. It also records the actual probe assembly hash.
