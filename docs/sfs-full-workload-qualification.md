# SFS full-workload growth and verification qualification

The 68020 benchmark creates a file with two 48 KiB extents separated by a gap, flushes it, overwrites it in twelve 8 KiB packets, flushes again, reopens it and checks every byte. Instruction profiling now records growth and verification separately from overwrite. Whole-workload timing uses independent ordinary emulator runs.

## Source of the preceding regression

| Instrumented guest cycles | Before relocation optimization | Archived relocation candidate | Revised |
|---|---:|---:|---:|
| Growth with explicit flush | 1,305,580 | 1,314,280 | 1,268,130 |
| Growth inside the handler | 1,130,795 | 1,139,839 | 1,100,216 |
| Verification | 3,992,150 | 4,011,310 | 4,000,470 |
| Verification inside the handler | 128,447 | 127,963 | 121,865 |
| Verification inside Kickstart | 311,707 | 330,843 | 326,277 |

The archived growth regression was in handler execution. Three redundant GrowExtent calls increased from 29,742 to 34,634 inclusive cycles between those two builds, while the final flush increased from 553,524 to 559,523. During verification the two builds executed exactly 15,414 instructions inside CopperSFS and issued the same twelve reads. The ROM executed 1,710 more instructions, adding 19,136 cycles. The trace identifies where the time went without attributing it to a particular interrupt or device delay. The guest's byte-by-byte verification loop accounts for most of the four-million-cycle phase.

## Changes

Grouped redo appends already know the complete contiguous allocation. The leaf insertion now encodes its actual length immediately, including when insertion splits the tree. This removes all three follow-up searches, private edits and seals of the same extent. Internal tree records still store child pointers. The strict write path keeps its original update order. The grouped path retains rollback, allocation checks and explicit commit barriers.

Bitmap allocation and release validate all affected bits in each bitmap image before updating them. They use the existing checksum-delta helper once per range. Cached cursor reads larger than the 4 KiB read-ahead window skip its policy lookup and retain the overlay, dirty buffer and pending physical write checks. All changes reuse existing mount storage.

The instrumented append calls fell from 221,504 to 189,514 inclusive cycles, and the growth flush from 559,523 to 536,208. Large-read verification entered the read-ahead policy once instead of twelve times and saved 6,098 handler cycles. Full overwrite remained faster, although its two general relocation calls increased from 151,525 to 156,976 inclusive cycles. Whole-phase timing includes the filesystem, Kickstart and caller.

A growth-only build saved time on the full workload but did not improve verification. Two read-path variants improved verification while slightly regressing total overwrite. The retained build includes the bitmap range changes and is measured independently against both controls below.

## Paired ordinary 68020 runs

Each arm used the same 16 MiB SFS medium and 32 DOS buffers on Kickstart 3.1. The table gives mean guest cycles over three alternating trials with controlled caller-buffer placement. The complete interval includes the code between phase markers and both explicit commits.

| Phase | Original SFS 1.279 | Archived CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast growth + flush | 1,037,680 | 1,314,137 | 1,268,250 |
| Fast overwrite + flush | 347,760 | 750,217 | 736,877 |
| Fast verification | 4,069,730 | 4,011,317 | 4,000,337 |
| Fast complete workload | 5,781,623 | 6,384,180 | 6,327,010 |
| Chip growth + flush | 1,039,287 | 1,314,297 | 1,268,497 |
| Chip overwrite + flush | 347,927 | 752,350 | 738,827 |
| Chip verification | 4,072,970 | 4,013,653 | 4,004,047 |
| Chip complete workload | 5,785,107 | 6,388,813 | 6,332,920 |

Both CopperSFS binaries issued 13 reads, 26 writes and eight flushes over the full interval in every trial. During overwrite they issued 17 writes and four flushes. In each RAM class original SFS issued 12 writes and one flush on two overwrite trials and 15 writes and four flushes on the third. Verification took twelve reads with every handler.

The complete interval improved 0.90% Fast and 0.87% Chip against the archived candidate. Original SFS remains 9.43–9.47% ahead when expressed as CopperSFS's excess cycles over original. The separate warm mixed workload improved about 0.13% in mutation but regressed 0.018% Fast and 0.016% Chip in verification. The independent original-handler release gate including mount exceeded original by 3.542% for Fast mutation, 2.970% for Fast verification, 3.540% for Chip mutation and 3.184% for Chip verification. The tolerance is 0.1%. The grouped profile remains opt-in.

Retained memory at mount and after mixed mutation measured 126,008 and 138,888 bytes respectively for both CopperSFS handlers. The M68000 HUNK grew 104 bytes to 168,640 bytes. CILica scalar replacement remained enabled with inlining disabled.

The ordinary trials, native replay logs and full-ROM recovery results are under `artifacts/sfs-full-cycles/sealed-run`. Phase profiles for the pre-regression binary, archived candidate, original SFS and retained binary are respectively under `previous`, `archive`, `original` and `sealed-run` in `artifacts/sfs-full-cycles`. In each arm use `overwrite-profile/candidate/{growth,overwrite,verification}-profile.json`. The revised binary is `artifacts/sfs-full-cycles/sealed/CopperSFS` (SHA-256 81602D379EB99C39D80DF32DD159F3C36DC704ABEE81F3832587CEF612C9C6D2). The archived control is `artifacts/sfs-relocation-cost/combined/CopperSFS` (SHA-256 26580C9FBBE39E0C51198C044A20DA4725EF8CB2B1DC7B8E0E293318A0AD1A6E).

## Qualification

The Release solution built and the host test run passed 2,171 tests with five conditional skips and zero failures. The default and grouped M68000 standalone handlers built. All 186 CopperStart cache, transfer, buffer and geometry replays and all 77 link and size replays passed.

On Kickstart 3.1, both CopperSFS and original SFS recovered the entire old or new 96 KiB payload at all nine tested cuts. Original SFS remounted 43 candidate sector prefixes, including payload-write and flush failures, with intact neighbors and allocation counts. Group pressure, partial journal and home writes, journal-read retry, close/flush/timer ordering, handoff, foreign startup and automatic TRIM tests passed. All five sequential-read fixtures passed. All three handlers issued 64 reads per 1 MiB in the 20 cold/warm and Fast/Chip comparisons; revised CopperSFS used 0.43–0.52% fewer cycles than original SFS. Physical Amiga and storage devices were not exercised.
