# CPX4 metadata and persistence redesign, 2026-11-15

Historical checkpoint. See the [latest benchmark report](cpx4-benchmark-2026-11-15.md)
for current results and coverage. Optimization is paused; the target remains unmet.

At this checkpoint the performance goal remained unmet. CPX4 must beat original Kickstart FFS 40.1
and SmartFilesystem 1.279 in both established workloads while retaining buffered
durability at successful ACTION_FLUSH, unmount/inhibit, and shutdown.

This continues [the CPU redesign checkpoint](cpx4-cpu-redesign-2026-11-15.md).
The current changes preserve the disk format, descriptor ordering, six commit
barriers, the final flush barrier, and bounded discard policy. Earlier write
replies may precede persistence.

## Evidence and changes

A new scalar instruction profile of the retained `2ABA2C...` HUNK measures
3,307,526 exclusive cycles across mixed flush and remount: 778,101 in CRC,
156,509 in native transport service, and 155,145 in recovery itself. The two
phases issue 53 I/O requests. Acceptance continues to use the unchanged
ExecuteFrame benchmark; scalar stepping is diagnostic only.

The buffered commit now seals each owned image in a bounded processing step,
then writes their contiguous journal range as one logical request. The journal
header and four descriptor pages are prepared in five retired undo slots and
written together. Parent scratch pages and the capacity-retry snapshot remain
untouched. Undo storage is borrowed only after retirement begins, when an I/O
failure poisons the mount and requires recovery. Transport still applies its
maximum-transfer and DMA splitting rules. Every existing durability barrier
remains.

Recovery reads the two adjacent checkpoints in one request but authenticates
and selects their generations independently. Reserve validation now reuses
the retired 2 KiB descriptor index as 128 address buckets for at most 64 entries.
It compares full 64-bit addresses, bounds collision probing, and retains the
existing address and allocation checks. It adds no memory requirement.

Four reserve fixtures cover 64 colliding addresses across five bitmap windows,
with strict and prepared buffered mounts, and a duplicate at the end of the
list. All four passed before and after the lookup change. The 55 focused core
cases also pass, including buffered sector-level power cuts and capacity retry.

## Preliminary native measurements

These single CPX4 trials measure journal batching and the checkpoint read,
before the reserve lookup change. They are not the final cross-filesystem
acceptance run. All guest assertions and independent disk/payload checks pass.

| Phase | Prior checkpoint | Batched candidate |
|---|---:|---:|
| Mixed mutation | 4,137,120 | 4,138,970 |
| Mixed flush | 1,553,295 | 1,287,600 |
| Mixed remount | 1,816,280 | 1,777,660 |
| Mixed verification | 1,535,665 | 1,557,370 |
| Streaming mutation | 6,067,910 | 6,074,190 |
| Streaming flush | 1,864,820 | 1,570,810 |
| Streaming remount | 1,996,320 | 1,981,410 |
| Streaming verification | 368,051,555 | 368,099,280 |

Mixed device write commands fall from 35 to 23; physical write bytes remain
39,936. Streaming physical write bytes remain 2,130,944. Both runs still issue
eight flush commands. Their resulting media hashes equal the preceding
checkpoint results exactly. The metadata bytes and publication order have not
changed.

## Combined candidate cross-filesystem comparison

The `B6952B...` HUNK is 220,468 bytes, targets 68000, and has zero reachable
managed allocation sites. SHA-256:
`B6952B0B75B20FFF4B2FCF463684B4CD8A6FC66AF3C40C684C05CBF365FB15B5`.
Its 51 source inputs match the source manifest. Two
trials per filesystem use the same established order and machine. Every trial
passes guest assertions and independent disk/payload validation.

| Phase | CPX4 | FFS 40.1 | SFS 1.279 |
|---|---:|---:|---:|
| Mixed mutation | 4,131,885 | 1,683,555 | 2,489,325 |
| Mixed flush | 1,300,495 | 20,460 | 161,275 |
| Mixed remount | 1,563,270 | 108,975 | 218,805 |
| Mixed verification | 1,547,675 | 969,780 | 1,061,920 |
| Mixed total | 8,543,325 | 2,782,770 | 3,931,325 |
| Streaming mutation | 6,066,930 | 2,110,610 | 4,561,845 |
| Streaming flush | 1,557,415 | 20,345 | 171,925 |
| Streaming remount | 1,702,155 | 114,995 | 217,345 |
| Streaming verification | 368,103,815 | 365,196,285 | 364,911,320 |
| Streaming total | 377,430,315 | 367,442,235 | 369,862,435 |

Mixed flush and remount together fall from 3,369,575 to 2,863,765 cycles;
streaming flush and remount fall from 3,861,140 to 3,259,570. The overall goal
remains unmet in both workloads. Mutation remains essentially unchanged.

The native request-admission experiment is isolated in ignored artifacts and
is not part of this HUNK or these filesystem results. Its ordinary 32-request
microbenchmark falls from 122,418 to 71,282 cycles. All 147 differential
command/completion cases match the original transport, including split requests,
DMA and forced-bounce cases, invalid inputs, and wide/narrow offset limits. It
has not been promoted into production.

The follow-up combined persistence profile falls from 3,307,526 to 2,829,083
exclusive cycles. Recovery self time falls from 155,145 to 85,927 cycles. CRC
remains the largest individual cost at 778,544 cycles across 37 calls. A
two-byte CRC slicing experiment was rejected: it increased 68020 dense-page
cost from 35,160 to 69,244 cycles and sparse-page cost from 11,734 to 18,128.

## Qualification in progress

The combined native rebuild and Release solution build succeed. The solution
tests and fresh 19-case ROM run are running under `artifacts/cpx4-metadata-redesign-20261115`. CopperStart’s existing geometry/buffer replay binary passes all 141 cases; the
consumer adapter sources are unchanged by these CPX4 core edits. Broader native
and ROM results will be recorded separately. Pending work
is not a pass, and the benchmark goal has not been achieved.

The original FFS input path disappeared during work. Byte-identical copies
were recovered from the preceding retained benchmark artifacts. FFS, SFS and
the SFS base image were checked against their recorded SHA-256 hashes before
copying into ignored `reference-inputs`; the adjacent provenance receipt records
the exact source paths and hashes. ROMs and generated artifacts are not releases.
