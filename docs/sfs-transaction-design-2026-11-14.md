# SFS transaction design comparison

Status: design proposal, 14 November 2026. No production implementation or
default changes accompany this document.

Recommend prototyping a shared block-image pool with accepted and trial indexes,
copying an accepted image only when a packet first edits it. Keep the existing
grouped implementation as the comparison and fallback. This targets unnecessary
copying and partitioned capacity; it does not by itself solve data-write
amplification or establish original-handler parity.

The target is a 14 MHz 68020, the current measured RAM budget, allocation-free
packet processing, and the existing SFS TRST recovery protocol. SFS2 and CSUN
recovery remain separate, unchanged paths. CILica scalar replacement remains
enabled and inlining disabled.

## Evidence and the decision to make

The latest qualification compares
original SFS 1.279, the eight-image-per-arena candidate, and the current nine-image
candidate. Three alternating warm trials use identical media, a 68020, 32 DOS
buffers and controlled Fast/Chip payload placement. Mutation includes final
ACTION_FLUSH; mount is separate. These are existing results, not measurements
of the proposed pool.

| Warm Fast workload | Original SFS | Eight-image arenas | Nine-image arenas |
|---|---:|---:|---:|
| Mutation, million reported cycles | 16.992 | 20.758 | 20.800 |
| Verification, million reported cycles | 17.569 | 20.181 | 20.134 |
| Mutation + verification | 34.561 | 40.939 | 40.934 |
| Commits, counted by TROK writes | 2 | 10 | 9 |
| Device flushes | 7 | 36 | 33 |
| Device write requests | 17 | 112 | 108 |
| Physical bytes written | 150,016 | 210,432 | 230,400 |

The current mutation gap is approximately 22%. Reducing one commit produced
essentially no total-cycle improvement and increased bytes by 19,968 (9.5%).
Do not use commit count, packet reply time or copied bytes as the release gate.

A fresh aggregation of the retained per-sector commit traces gives:

| Bytes by recorded first-word signature | Original | Eight-image | Nine-image |
|---|---:|---:|---:|
| TRST / TRFA / TROK | 4,096 | 16,896 | 15,872 |
| SFS metadata signatures | 10,752 | 27,648 | 25,600 |
| Remaining writes | 135,168 | 165,888 | 188,928 |
| Total | 150,016 | 210,432 | 230,400 |

Metadata signatures here are SFS root, OBJC, BTMP, BNDC, NDC, ADMC and HTAB.
This is a signature classification, not a proof of each write's role. The extra
23,040 bytes in the remaining category outweigh 3,072 fewer journal/metadata
bytes. Attribute those writes to data growth, relocation, overwrites and retries
using block addresses and operation provenance before claiming a cause. A larger
group may change allocation/relocation decisions as well as amortize commits.

Reported emulator cycle units are retained without conversion to milliseconds.
Before presenting 14 MHz elapsed times, establish how the harness clock relates
to CPU and bus clocks. Do not assume an observed machine-cycle counter is a
14 MHz CPU clock counter. Device wait time needs separate reporting.

## Alternatives

Let A be accepted block images at packet start, C the distinct accepted images
edited by that packet, N newly staged targets, and B the filesystem block size.
Counts include staged file data as well as metadata.

| Design | Packet copying | Capacity and CPU characteristics | Main risk / decision |
|---|---|---|---|
| Current accepted/trial arenas | General path clones A images plus index/header; continuation can avoid this | Simple contiguous storage; nine final targets per arena at B=512 | Proven reference; fixed half-partition reserves rollback space even for unchanged images |
| Shared pool, two indexes | C image copies plus small index/order copies; new targets initialized separately | Peak A+C+N images; unchanged accepted images shared | Recommended prototype; extra ownership and lookup work, workload-dependent pressure |
| One mutable arena with full-block undo | C before-image copies | Similar A+C+N peak, but changes happen in accepted storage | More difficult rollback, pinning and failed-packet isolation; no clear capacity advantage over shared pool |
| Byte/word undo or patch log | Only changed spans plus log records | Attractive for sparse field edits; repeated/overlapping edits need bookkeeping; full data writes remain large | Variable worst-case space, reverse replay and validation costs; defer unless profiles justify complexity |
| Commit earlier or spill images to disk | Avoids some RAM pressure | Adds commits, journal work or I/O | Earlier commit is the existing fallback; disk spill is outside this proposal and its recovery scope |

The general clone is not necessarily the dominant remaining cost. Existing
continuation paths already avoid some cloning. Count fast-path use and preserve
it where its proof remains valid; replacing it with unconditional copy-on-write
would risk a regression.

## Shared pool within the existing allocation

Keep the 32-entry enumeration cache, DOS buffers and existing scratch allocations.
Do not recover space by shrinking cache capacity: the earlier 16-entry experiment
made a 24-entry directory scan approximately 65% slower. Do not treat the existing
192 KiB strict-fallback ceiling as a new ordinary-mount allowance.

The following proposed layout fits the **current 9,756-byte group allocation**
at 512-byte filesystem blocks. It uses ordinary aligned 32-bit index entries and
ordered slot references. Exact field offsets are an implementation review item;
the byte ceilings below are requirements, not measured prototype memory use.

| Storage | Proposed bytes |
|---|---:|
| Group state, existing continuation/proof records, pool control | 256 |
| Accepted + trial hash indexes: 2 × 32 × 4 | 256 |
| Accepted + trial ordered references: 2 × 17 × 4 | 136 |
| 17 image descriptors: 17 × 16 | 272 |
| 17 image payloads: 17 × 512 | 8,704 |
| Used | **9,624** |
| Remaining within 9,756 | **132** |

Each descriptor budgets block identity, verification/type certificate, revision,
ownership flags and pin count. A free slot can reuse its inactive fields as a
free-list link. Slot references identify descriptor addresses; no divide or
slot-stride multiplication is required on ordinary lookup. Index probing is
bounded by its bucket count, with at most 17 occupied entries out of 32. This
does not assume collision-free or constant worst-case lookup.

The 256-byte state allowance includes the current 220-byte group records plus
36 bytes for new control fields. Reuse current fields where their arena meaning
is replaced. If the field audit exceeds that allowance, reduce capacity or
revise the layout explicitly; do not hide state in new mount allocations.

The existing 8,716-byte fallback allocation can hold 15 images using the same
256-byte header and two 32-bucket indexes: 256+256+120+15×528 = **8,552** bytes,
leaving 164 bytes. Preserve existing allocation-failure fallback behavior.
For other block sizes and existing budgets, solve the bounded layout using
checked arithmetic and indexes sized for the chosen capacity. Reuse the strict
path when no supported pool layout fits. Do not assume 17 images at every block
size or grow the allocation to meet that number.

Keep the allocator's original extent for freeing and for the strict fallback.
Current measured retained memory is 130,104 bytes after mount and 139,864 after
mutation. The prototype must not exceed the corresponding baseline at the same
allocation state, including alignment, control records, scratch and task stack.
No new normal-operation managed or native allocations are permitted.

### Capacity tradeoff

For the proposed normal layout, A+C+N must be at most 17. This is shared storage,
not a guarantee of 17 accepted images plus unrestricted rollback capacity.

| Packet shape at B=512 | Pool peak | General-path payload copying | Consequence |
|---|---:|---:|---|
| A=9, C=2, N=1 | 12 | 1,024 bytes versus a 4,608-byte arena clone | Pool can accept a tenth target; current nine-slot trial cannot |
| A=12, C=3, N=1 | 16 | 1,536 bytes | A larger group fits when edits are sparse |
| A=9, C=9, N=0 | 18 | Would require 4,608 bytes | Does not fit proposed pool; current nine-slot arenas can handle it |

These are arithmetic examples, not performance predictions. The pool trades
guaranteed wide-edit headroom for capacity when packets touch few old images.
The 15-image fallback likewise cannot guarantee an eight-image-wide edit.
Measure these adverse cases explicitly. If they lose enough cycles or add
writes, retain arenas for those configurations or reject the pool design.

## Packet ownership and rollback

1. **Begin:** copy the accepted index and ordered references into the trial
   tables. Share payloads; load trial roots into existing caller-owned Work.
   With A accepted targets, table copying is 128+4A bytes for this layout.
2. **Read:** resolve trial then accepted state through one access API. Borrow
   images with bounded explicit pins; reading must not create a dirty slot.
   Device/cache misses continue to use existing scratch and validation rules.
3. **Edit:** first modification of a shared accepted image acquires a free slot
   and copies B bytes. Redirect only the trial index and ordered reference.
   Further edits reuse that private image. A newly staged disk target still
   needs its initial contents read/copied when required; “copies only for changed
   blocks” does not mean zero I/O or zero initialization.
4. **Validate:** revoke certification before an edit; seal changed images once
   before acceptance and retain verification for unchanged revisions. Materialize
   deferred updates and finish all fallible admission checks before publishing.
   Unsealed images must never escape to the journal, device or raw-image readers.
5. **Accept:** with exclusive mount ownership, publish trial roots and mappings
   together, swap table roles and reclaim superseded images. Preserve target
   insertion order through the ordered references. Read pins cannot survive the
   packet boundary; assert this before acceptance or rejection.
6. **Reject:** discard private slots and trial root changes; restore request,
   accepted overlay and transient operation state. Never write accepted images
   in place on a fallible path. Harmless rejections retain pending accepted work
   without a commit.

A handle binds slot identity and revision to its mount/packet lifetime. Pinning
prevents reuse while live; a revision check alone does not make stale pointers
safe. Recycle/revision wrap is allowed only after handles have ended and related
certificates are invalidated. Recovery, media change and external access end the
context before storage can be reused. Use bounded lists/free links, not a
general-purpose heap or reference-counted object graph.

On capacity failure, discard the entire trial, commit the accepted group, and
retry the complete packet once. If it still cannot fit, use the existing strict
fallback with its existing budget policy. Preserve whole-packet results and
error handling. Do not commit a trial prefix, borrow pinned images, or overwrite
the accepted checkpoint to get another slot.

The continuation optimization is a separately proved exception: accepted images
may change in place only after every possible failure has been excluded, as in
the current fast path. Port its certificates and slot references explicitly or
disable it in a measured prototype variant; never silently assume old arena
pointer proofs remain valid with pool ownership.

## Commit protocol remains unchanged

Adapt commit and journal-space lookup to consume ordered pool references directly.
Do not gather the pool into another contiguous arena: that would restore copying
cost and require storage. Keep existing target order and TRST encoding, inline
512-byte publication eligibility, external journal pages and TRFA/TROK handling.

The durability sequence remains:

1. Finish unpublished data and external journal pages; durability barrier.
2. Publish inline TRST or TRFA; durability barrier.
3. Write accepted home images; durability barrier.
4. Write the clean TROK marker; final durability barrier.

A clean-device flush elision may avoid a device command under the existing
rules; it does not remove a logical ordering requirement. Original SFS's seven
measured flushes are a comparison target, not permission to remove our barriers.

Keep accepted images until successful completion. Commit failure suspends normal
mutations and retains the existing recover-before-retry behavior; never encode
a new transaction against partially applied home blocks. Published journals
remain replayable by original SFS. CSUN recovery is unaffected.

Unpublished data and journal placement must still prove destination space free
in both committed and accepted allocation views, excluding staged targets and
permanent metadata. Sharing bitmap images cannot collapse those two views.
Freeing a block in the pending group does not make it safe to overwrite directly.

Explicit flush remains synchronous through the final barrier. Preserve current
opt-in deferred-close behavior, timer, inhibit, pressure and manual-TRIM commit
boundaries. Automatic TRIM becomes eligible only after durable completion, and
still revalidates current allocation while holding exclusive ownership. A
successful low-level Flush alone does not complete a filesystem transaction.

## Writes need a separate optimization decision

The pool first changes private representation only. It may reduce pressure
commits and repeated home writes across packets, but it can also prolong a group
and affect relocation. Instrument before adding write-suppression policy.

Current CommitTrst plans a delta against the committed image for every staged
target, then writes every staged home image. An independent follow-up could
detect a final image byte-identical to the committed image during that existing
comparison, suppressing its journal record and home write without another scan.
Equality to the prior accepted image only avoids a trial copy/update; it does
not establish equality to disk. Checksums alone cannot establish byte equality.

Such suppression requires a reviewed emission/home selection mechanism within
the same layout, correct handling of a completely empty transaction, and proof
that allocation/journal-space checks remain conservative. Do not release a
staged target merely because the final bitmap marks it free. Keep this change
separate from pool qualification so its cycle and write effects are attributable.

For the unexplained remaining writes, count repeated physical destinations and
relocated bytes by packet and commit group. Distinguish successful unpublished
writes from safe but wasted writes made by a rejected trial or capacity retry.
Preserve free-in-both-views checks even if they account for extra writes. Only
then choose whether relocation policy or transaction capacity is the larger
opportunity.

## Measurement and acceptance plan

Use four explicit arms: original SFS; the current nine-image grouped candidate;
shared pool with otherwise unchanged policies; and, only if warranted, pool
plus separately qualified write suppression. Keep the checked strict path for
correctness comparison. Do not compare compiler or cache-policy changes at the
same time.

Report total cycles from operation start through final commit and deferred
maintenance, plus foreground packet median/tail/max latency. Count payload-copy
bytes, index/order-copy bytes, index probes, checksum bytes, A/C/N high-water
marks, pinned images, continuation hits and wasted retry work. Attribute commits
to explicit flush, timer, inhibit, pressure, strict fallback and recovery. Separate
logical barriers from device flush commands.

Report device requests, sectors and bytes for journal, markers, metadata homes,
data homes and unpublished relocation; record unique versus repeated targets.
Keep unclassified sectors visible. Include reads, journal compression work,
TRIM work and reclaimed bytes. Host-side trace aggregation adds no handler RAM;
do not leave profiling arrays or counters outside the declared release budget.
Use uninstrumented paired runs for the performance gate.

For a 68020, evaluate the whole tradeoff:

    Total = packet/traversal + copy + lookup/ownership + checksum/encoding
          + allocation/relocation + commit dispatch + device wait
          + final commit and deferred maintenance

Analytically, replacing A×B clone bytes with C×B saves payload traffic when C is
small; index maintenance and ownership checks consume some of that saving.
Instruction/bus traces must supply the cycle costs. No predicted percentage
speedup or original-handler parity is claimed from this byte model.

Cover cold/warm runs, sparse and wide edits, metadata-heavy directories, sequential
growth, small overwrites, growth followed by overwrite/reallocation, fragmentation,
full queues/groups, small/large media and supported block sizes. Retain 24/64-entry
enumeration checks. Keep identical starting images, packet streams and 32 DOS
buffers. Control payload and metadata/cache Fast/Chip placement, recording actual
addresses and retained/peak memory; payload placement alone is insufficient.
Measure fixed 14 MHz CPU configuration and separately disclose emulator storage
latency assumptions. Record handler code/HUNK sizes and stack high-water use.

Correctness must cover rejection after each private edit, exhaustion with pins,
index collisions, stale handles, reuse and generation wrap, failed allocation,
root/tree replacement, continuation invalidation, trial/accepted swaps, and strict
fallback. Repeat power cuts and injected failures before/after each barrier and
during home writes. Verify old-or-replayed complete state, original-SFS recovery,
CSUN recovery, handoffs, foreign startup, flush/close/timer/inhibit, media changes,
pending-transaction TRIM and discard failure.

Run Release solution build/tests, affected standalone builds, CopperStart native
replays and full-ROM qualification. Report host, native, full-ROM and physical
device results separately. The existing qualification does not qualify this
design; physical power-loss and real-device latency remain separate work.

Keep the prototype opt-in. Advance it only if correctness passes, actual RAM
stays within the existing budget, total cycles improve over the grouped baseline,
and write changes are accounted for. Release/default enablement still requires
the existing original-handler parity gate. If copying falls but total cycles do
not, stop treating copying as the leading bottleneck and use the measured
allocation/relocation, journal or lookup attribution to choose the next change.

## Implementation references and receipts

- [Group acceptance, rollback and flush](../src/CopperSFS.Core/AmigaSmartFileSystemTrstGroup.cs)
- [Current indexed arenas and cloning](../src/CopperSFS.Core/AmigaSmartFileSystemRedoArena.cs)
- [TRST emission and home writes](../src/CopperSFS.Core/AmigaSmartFileSystemTrst.cs)
- [Recovery and final clean barrier](../src/CopperSFS.Core/AmigaSmartFileSystemNativeRecovery.cs)
- [Unpublished data relocation](../src/CopperSFS.Core/AmigaSmartFileSystemRelocation.cs)
- [Native allocation and pressure fallback](../src/CopperFS.Amiga/NativeSfsTrstGroup.cs)

Trace aggregation uses ignored local receipts:
`artifacts/sfs-buffer-reuse/qualification/commit-trace/candidate/commit-trace.json`
and `artifacts/sfs-capacity/compact-qualification/commit-trace/{candidate,original}/commit-trace.json`.
Those traces contain 411, 450 and 293 sector writes respectively; the device-request
counts in the baseline table come from the paired performance reports, a different
measurement layer. No ROMs, media or generated binaries are documentation inputs
to commit.
