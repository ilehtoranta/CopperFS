> Historical research snapshot from CopperSFS commit 4a902a1ff9189bbdff841c23441c66678a6e2427.
> Preserved on 2026-11-04; commands, paths, results and recommendations below are historical, not current validation.
> See [current port status](../../research-port.md) before using this material.

# Large contiguous overwrite relocation



The initial change described here makes the portable SFS1 writer and

CopperStart's embedded native SFS1 writer

relocate full-block contiguous ranges of 64 KiB when a suitable free run exists.

Fragmented ranges retain their 16 KiB threshold. Blocks remain 512 bytes in the

native fixtures; the transfer buffer remains bounded to 64 KiB and MaxTransfer.

If the device or scratch limit yields a smaller contiguous range, the existing

in-place undo path handles it. Partial blocks initially remained in place;

the reservation follow-up below also handles a partial EOF block.



Replacement data is written and flushed before the extent mapping changes.

Metadata uses the existing undo transaction, and retired data blocks cannot be

reused until commit. The replacement preserves the prefix and suffix of an

overwritten extent and merges with the preceding replacement when adjacent.

No suitable free run means an in-place overwrite. The historical

`relocateFragmented` option now controls both kinds of relocation; setting it to

false retains in-place writes.



This extends the [native batched write integration](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/native-batched-writes.md).

Relocation itself requires no additional on-disk format or private ABI change.

The controlled before/after builds use CSE5. Concurrent TRST work introduced

CSE6 in the shared checkout after those builds were frozen; combined-checkout

checks are recorded separately from the controlled performance comparison.



## Validation protocol



The before binaries are the previously qualified buffered and strict native

handlers. Both before and after binaries run the same updated guest probe and

the same pinned 512-byte fixture. Each bulk benchmark performs five cold trials

of a 1 MiB fragmented overwrite and a 1 MiB contiguous overwrite, using sixteen

64 KiB Write packets. Guest cycle counts exclude boot and image-copy time.



Recovery adds a 64 KiB overwrite at offset 8192 in the contiguous file, alongside

the existing fragmented and contiguous-prefix cases. This exercises splitting

both ends of one extent. Each recovered image is independently checked and all

bytes of the changed file and neighboring files are compared. Atomic-sector

cuts that leave identical durable images share a reboot, with complete cut-point

coverage audited separately.



Host regressions exercise repeated contiguous replacements, untouched edges,

free-space accounting, adjacent extent records, transfer limits, allocation

fallback, disabled relocation, every mutation/recovery I/O failure, and torn

writes. The embedded native-core tests additionally cover logical buffering and

the physical write combiner independently and together.



Local source snapshots, native binaries, reports and TRX results are retained in

`artifacts/native-contiguous-relocation/`. Measurements are emulated guest CPU

cycles; they do not establish physical Amiga storage throughput.



The public reliability run is repeated from an isolated source snapshot because

unrelated concurrent edits changed the working tree during its first run.

`identity.json` retains the native build-time hashes; the audit reports later

source drift without replacing those hashes or attributing later code to the

frozen binaries. The relocation implementation and its new host tests are

verified against their captured hashes.



## Native bulk results



Five-trial medians for the 1 MiB overwrite workloads are:



| Policy | Workload | Before, guest cycles | After, guest cycles | Before / after |

| --- | --- | ---: | ---: | ---: |

| Buffered | Fragmented | 159,350,570 | 160,536,800 | 0.993x |

| Buffered | Contiguous | 494,838,180 | 109,357,630 | 4.52x |

| Strict | Fragmented | 160,356,359 | 159,161,790 | 1.008x |

| Strict | Contiguous | 496,916,729 | 108,506,660 | 4.58x |



All five trials produced identical cycle counts within each bulk workload and

handler build. Fragmented-write I/O counts are unchanged.



Contiguous overwrites now take fewer guest cycles than fragmented overwrites.

Both policies issue the same I/O counts: the contiguous workload uses 1,937

sector reads, 2,304 sector writes, 168 flushes and 220 device write requests;

the fragmented workload uses 2,641 reads, 2,464 writes, 232 flushes and 348 write

requests. The remaining difference includes extent splitting and removal work.



For both policies, contiguous sector reads fall from 17,601 to 1,937 and sector

writes from 6,264 to 2,304. This is not a reduction in

every kind of I/O: metadata publication increases flushes from 128 to 168 and

device write requests from 168 to 220. The result therefore measures the guest

CPU and modeled device path, not a universal speedup on high-latency hardware.



The benefit comes from avoiding undo images and journal records for every old

data block. Old data remains intact while the replacement is written, so only

the metadata changes require undo. Blocks still measure 512 bytes; batching and

relocation change how the handler transfers and protects those blocks.



Relocation does not guarantee that a whole file stays in one extent. In this

fixture, the completed contiguous overwrite has two extents rather than the

original one, while the fragmented file drops from 66 extents to three. The

contiguous case split because the allocator selected the first suitable free

hole for a 64 KiB request, without looking ahead to the whole 1 MiB file. That

hole spans blocks 21,054–21,565: 512 blocks, or 256 KiB. Four packets filled it;

`Fill6` occupies the following blocks. The next packet reused the now-free

prefix of the original file at block 25,150, and the remaining packets extended

that second run. The final extents are `(21054, 512)` and `(25150, 1536)`.



This particular split was not caused by a bitmap boundary. The allocator does

require each replacement batch to fit within one bitmap, but a larger free

area also existed: blocks 28,000–30,047 could hold the entire 1 MiB file within

one bitmap. Keeping a completed sequential full-file overwrite in one extent

would require choosing a longer destination run and protecting its unused tail

from other allocations across Write packets. Transfers could still use the

same 64 KiB buffer. Such lookahead/reservation is not implemented by this change.



The ordinary buffered workloads retain identical sector counts, device request

counts and flush counts. Five-trial median cycle changes are below 0.07%:



| Workload | Before, guest cycles | After, guest cycles |

| --- | ---: | ---: |

| Sequential 1 MiB read | 2,644,770 | 2,643,970 |

| Sequential 1 MiB write | 16,438,819 | 16,442,489 |

| Small-file creation | 12,112,560 | 12,104,769 |

| Directory enumeration | 6,285,670 | 6,286,830 |

| Mixed access | 12,851,939 | 12,860,210 |

| Warm 1 MiB read | 2,614,730 | 2,616,101 |



## Host and compiled validation



| Check | Result |

| --- | --- |

| Public Release reliability suite, isolated sources | 342 passed, 5 optional external-reference tests skipped |

| Embedded native core suite | 258 passed, 1 optional external-image test skipped |

| Compiled SFS transfer/deletion tests and extension checks | 17 passed |

| Native architecture tests | 68 passed |

| Native ordinary regressions, buffered / strict | 10 / 9 passed |

| CopperStart Release solution and buffered/strict handler builds | Passed |



The public relocation tests checked 119,319 exhaustive crash/failure images and

43,890 sampled images, plus 735 torn-write images that all recovered. The

batching tests additionally checked 12,032 exhaustive mutation/failure images,

166 exhaustive recovery images, 5,830 sampled mutation images, 130 sampled

recovery images and 51 torn-sector images that all recovered. These are host

storage-model results, separate from native guest reboots.



The public acceptance report has `sourceChangedDuringRun: false`. Native build

identity and frozen runtime hashes are in `identity.json`; the extra CSE6

snapshot and its independently built handler hashes are in

`combined-identity.json` and `combined-snapshot-sources.json`. Its default CSUN

writer contains the same relocation implementation, with the optional TRST

write policy disabled.

Later concurrent SFS2 seek/dispatch edits are also recorded as source drift;

these results qualify the pinned SFS1 builds, not those later additions.



## Native recovery



All three bulk cases passed on each of the four builds: fragmented overwrite,

contiguous prefix overwrite, and contiguous middle overwrite.



| Build | Covered cut points | Actual guest reboots | Result |

| --- | ---: | ---: | --- |

| Controlled CSE5 buffered | 498 | 45 | Passed |

| Controlled CSE5 strict | 498 | 45 | Passed |

| Combined CSE6 buffered, default CSUN | 498 | 45 | Passed |

| Combined CSE6 strict, default CSUN | 498 | 45 | Passed |



Each row covers 180 fragmented, 159 contiguous-prefix and 159 contiguous-middle

cut points. Cuts with the same durable image share a reboot; they are not

reported as separate executions. The audit verifies every planned cut is

covered exactly once and that grouped cuts have the same flushed prefix.

Each reboot must expose the complete old or complete new contents, preserve

neighboring files, and pass the independent filesystem check.



The ordinary mutation matrices add 329 cut points and 329 reboots: 205 for

CopperSFS across buffered and strict policies, and 124 for the original SFS

reference. Together with the bulk matrices, all **2,321 cut points** are covered

by **509 actual guest reboots**, with no failures. Of those cut points, 2,197

exercise CopperSFS and 124 exercise the original handler. The 32 plans are

audited without sampling; native sectors are atomic and completed flushes are

trusted. Torn-sector behavior is covered separately by the host tests above.



The ordinary native suites also passed shared-handle updates, resize behavior,

concurrent guest processes, startup, clean remount, the buffered timer commit,

and original-handler TRST replay. Those compatibility checks are additional to

the cut-matrix totals.



The retained `summary.json` has `passed: true`: all 15 required result suites,

32 recovery plans and 10 before/after benchmark comparisons are complete. Raw

reports are retained in `reports/`, TRX results in `results/`, and the independent

extent observations in `extent-observations.json`. `Audit-Results.py --saved-only`

can recheck the saved reports, binary identities and source manifests without

rerunning the guest tests. Five-trial bulk results and ordinary workloads use

the same frozen emulator on both sides; no emulator implementation changes were

made for this feature.





## Sequential overwrite lookahead and reservation (2026-10-25)



A follow-up adds whole-file lookahead to the portable core and native classic

CSUN writer. A sequential overwrite beginning at byte zero with a request of at

least 64 KiB looks for a free run large enough for the existing file, instead of

choosing a run that fits only the current packet. The file keeps 512-byte blocks;

the data buffer and each transfer remain bounded at 64 KiB (or the device's

smaller MaxTransfer).



The selected run's unused tail is reserved in mount-owned RAM. All data,

metadata and journal allocators exclude that tail, including interleaved writes

to other files. Each successful packet consumes its next section and merges it

with the preceding relocated extent. Reservations can span bitmap boundaries.

The final short packet, including a partial EOF block, joins the same extent.

Intermediate packets must contain whole blocks and continue at the expected

offset; a different write position or file size ends the reservation.

No unwritten reserved blocks are marked allocated on disk, and free-space

accounting does not change merely because a reservation exists.



The native handler owns eight reservations in 260 bytes of zeroed mount state.

Close, discontinuous seek, scalar overwrite, resize, truncation, deletion and

failed writes release the affected reservation. Entering the TRST writer

also ends any existing CSUN reservation streams. Crash recovery discards stale

RAM reservations; the existing transaction protocol determines whether the

current packet's old or new mapping survives. An earlier acknowledged packet

remains durable when a later packet is interrupted.



This is best effort: insufficient contiguous free space, a full reservation

table, a smaller transfer buffer, or allocation pressure falls back to the

existing writer. Reservations are evictable so they cannot manufacture a

DiskFull result in data, journal, or metadata allocation. Classic SFS's 16-bit extent length limits one extent to 65,535

blocks; larger files cannot be represented as one extent. Lookahead also leaves

128 free blocks beyond the held runs for transaction and metadata work.



The checkout's newer default TRST policy continues using its existing in-place

writer. The new relocation/reservation path is selected by `-LegacyCsUn` in the

native build. SFS2 dispatch and both journal policies are preserved. Because

mount state grew from 2,052 to 2,312 bytes, the extension profiles are distinct

CSE9 (classic, 256 KiB) and CSEA (SFS2, 512 KiB); matching clients/providers must

be rebuilt together. These qualification snapshots precede the separate native

record-lock integration, which subsequently grew shared mount state to 2,320

bytes. That work is outside the reservation benchmark comparison.



Validation evidence for this follow-up is retained under

`artifacts/native-reserved-overwrites/`. The before and after handlers, replay

runtime, compiler inputs and source snapshots are pinned there. Full numerical

results are recorded in `summary.json`; `Inspect-Final-Extents.py` independently reads the final allocation chains.





### Reservation results



All twenty completed after-change bulk trials (five trials × two workloads ×

two write policies) have exactly one extent: start block 27,198, length 2,048

blocks. This run spans the bitmap boundary at block 28,000. Before lookahead,

the contiguous file finished in two extents and the fragmented file in three.

Every measured bulk trial retains a maximum write transfer of 65,536 bytes,

with transfers composed of whole 512-byte sectors.



Five cold-trial medians, in emulated guest CPU cycles:



| Policy | Full-file overwrite | Before | After | Speedup |

| --- | --- | ---: | ---: | ---: |

| Buffered | Fragmented 1 MiB | 159,401,039 | 101,951,310 | 1.56× |

| Buffered | Contiguous 1 MiB | 108,696,880 | 87,095,091 | 1.25× |

| Strict | Fragmented 1 MiB | 160,385,050 | 102,488,901 | 1.56× |

| Strict | Contiguous 1 MiB | 109,285,481 | 87,680,680 | 1.25× |



The fresh sequential-write workload is a tradeoff: cycles increased by 8.9%.

The other ordinary workloads increased by 0.3–2.8%. Their sector, request and

flush counts are unchanged; the table records the added CPU cost rather than

attributing all of it to a particular reservation check.



| Ordinary buffered workload | Before, cycles | After, cycles |

| --- | ---: | ---: |

| sequential-1m-read | 2,831,340 | 2,855,131 |

| sequential-1m-write | 16,632,401 | 18,105,250 |

| small-file-creation | 12,245,450 | 12,591,140 |

| directory-enumeration | 6,415,440 | 6,554,550 |

| mixed-access | 13,023,501 | 13,388,940 |

| warm-cache-1m-read | 2,799,130 | 2,806,470 |



Final validation:



- Public Release reliability: **362 passed, five optional external-reference

  tests skipped**; `sourceChangedDuringRun: false`. Relocation tests cover

  144,560 exhaustive and 47,400 sampled crash/failure images, plus 1,185 torn

  writes that recovered. The report retains separate batching, TRST and SFS2

  coverage rather than combining different storage models into one count.

- Embedded native core: **262 passed, one optional external-image test skipped**.

- Compiled filesystem/extension checks: **17 passed**. Release solution and

  buffered, strict and default-TRST handler builds passed.

- Native CSUN recovery: **1,596 cut points across 128 actual guest reboots**,

  covering fragmented, contiguous-prefix, contiguous-middle and two-packet

  sequential overwrites in both policies. All planned cuts are accounted for;

  cuts with identical durable images share a reboot. The two-packet case accepts

  only zero, one, or two complete committed packets, and verifies all unchanged

  bytes and neighboring files with the independent checker.

- Default TRST: bulk writes/remount, arena-capacity handling and interrupted

  128 KiB writes all passed. Its **546 cut points** form nine durable images;

  both CopperSFS and the original handler recover each, for **18 guest reboots**.

- Final native shared-handle, resize, concurrent-process and buffering checks:

  **five buffered / four strict tests passed**.



`final-source-hashes.json`, `identity-final.json`, raw reports and TRX files tie

these results to the frozen inputs. `Audit-Final.py --saved-only` verifies saved

coverage and identities; `summary.json` has `passed: true`. The initially found

metadata-allocation pressure case was fixed, tested, and the full portable and

native qualification rerun on those final sources. The later independent native

record-lock changes remain outside these benchmark/recovery binaries.



A supplementary check against the concurrently changing native checkout initially
passed 67 of 68 architecture tests and flagged a raw record-field offset in
`NativeRecordLockCore.cs`, outside the reservation snapshot. The integration task
subsequently resolved that finding: its final combined snapshot passes all 69
architecture checks, all 21 SFS2 native tests, and four classic A500 checks. It
covers 220 sector/flush cut points through 95 actual cut/reboot groups and
includes the final reservation-pressure fixes. See the separate
[native SFS2 qualification](https://github.com/ilehtoranta/CopperFS/blob/4a902a1ff9189bbdff841c23441c66678a6e2427/docs/native-sfs2.md) and
`artifacts/native-reserved-overwrites/integration-followup.json`.

These later results retain their CSEB/CSEC binary identities. The reservation
benchmark numbers above still describe the pinned CSE9 comparison. Subsequent
classic performance optimizations and the dos.library record-lock API bridge
are outside both recorded snapshots and require their own qualification.
