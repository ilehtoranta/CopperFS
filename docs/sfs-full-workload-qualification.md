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

## Growth commit profile after the full-workload changes

The reference for this pass is commit `259f645`. On the growth instruction trace its handler used 1,100,216 cycles versus 789,501 in original SFS 1.279. The reference issued one read, nine writes and four flushes during growth. The first original Fast RAM trial issued zero reads, eight writes and four flushes. Command count alone does not explain the cycle difference.

One reference commit took 519,848 inclusive cycles in `CommitTrstStream`. Planning eleven compressed records took 134,229 cycles, emission 245,264 and home writes 79,703. These times are inside the commit. Emission made 141 calls to the byte-stream helper and 63 to the literal-span helper. A record crossing a journal page used these helpers for every run, even when the current code byte and literal both fit. Original SFS has no symbol map in this trace, so its handler time cannot be split into comparable named routines.

A separate fixed-displacement planner experiment retained one read per input word but took 134,355 cycles in the planner and 1,273,750 for instrumented growth, up from 1,268,130. It was discarded.

The SFS emission change writes each run code and fitting literal directly into the existing journal page. At an exact page boundary, or when literal data crosses the boundary, the existing transition code still allocates, seals and links pages. Scratch ownership, wire encoding, publication and the four flush barriers stay the same.

The retained emitter keeps the current output pointer and page end in locals. Instrumented growth commit fell to 456,641 cycles (−12.2%). Emission fell to 184,528 cycles (−24.8%), with 12 byte-helper and three literal-helper calls. Planning still cost 129,623 cycles and the home-write pass cost 84,629. Across the entire growth phase 191 block-copy calls took 201,708 cycles, some within commit. CopperSFS still executed 1,038,744 handler cycles during growth versus original SFS's 789,501. The final growth/overwrite/verify disk image had the same SHA-256 as the reference.

| Mean ordinary guest cycles, three trials | Original SFS 1.279 | Pushed reference | Revised CopperSFS |
|---|---:|---:|---:|
| Fast growth and explicit flush | 1,036,740 | 1,268,247 | 1,197,993 |
| Fast overwrite and explicit flush | 324,637 | 736,873 | 753,383 |
| Fast verification | 4,070,357 | 4,000,360 | 4,004,980 |
| Fast total | 5,756,657 | 6,327,000 | 6,266,277 |
| Chip growth and explicit flush | 1,039,397 | 1,268,417 | 1,198,090 |
| Chip overwrite and explicit flush | 348,000 | 738,853 | 759,873 |
| Chip verification | 4,073,017 | 4,004,143 | 4,004,107 |
| Chip total | 5,785,337 | 6,332,927 | 6,271,957 |

Both placements saved approximately 0.96% on the complete workload and 5.54% on growth against the pushed reference. The 96 KiB overwrite regressed 2.24% Fast and 2.84% Chip. In the Fast instruction trace the overwrite commit itself fell from 87,116 to 83,743 cycles. Of the 16,470-cycle increase across the overwrite phase, Kickstart accounted for 14,383 and handler code for 1,645. The trace does not identify a specific cause for the extra ROM execution. Fast verification regressed 0.12% and Chip verification was effectively unchanged. A separate emitter with parallel fitting-record and spanning-record loops took 6,277,037 Fast and 6,281,577 Chip total cycles, both slower than the retained version.

Both CopperSFS handlers used 13 reads, 26 writes and eight flushes over the complete workload in every trial. During growth original SFS used eight writes and four flushes and CopperSFS used nine writes and four flushes. Original SFS's overwrite used 12 writes and one flush in every Fast trial and two Chip trials, and 15 writes and four flushes in the remaining Chip trial. Both CopperSFS handlers used 17 writes and four flushes for overwrite. The paired ordinary timings include these differences.

The revised M68000 binary is 168,656 bytes, 16 bytes larger than the pushed reference. The new loop uses existing journal storage and requests no additional mount or heap memory. The compiler map reports a four-byte local spill in the emitter and one fewer function argument. CILica scalar replacement was enabled and inlining disabled. The retained binary, phase profiles, paired measurements and qualification logs are under `artifacts/sfs-growth-commit-cursor`; its SHA-256 is B5D3E3D3AAAC3073F8DEFF6781532E5C833C12BA054C55E3266909ADC15DF294.

The warm mixed mutation phase saved 19,517 cycles Fast (0.11%) and 16,867 Chip (0.095%) versus the pushed reference. Warm verification cost an additional 5,274 Fast and 6,164 Chip (0.026% and 0.030%). Both CopperSFS arms retained 126,008 bytes at mount and 138,888 after mutation. Original SFS retained 156,480 and 172,904 bytes in the same Fast RAM probe with 32 DOS buffers.

The separate original-handler gate measured +3.547% Fast mutation, +2.957% Fast verification, +3.410% Chip mutation and +3.191% Chip verification against original SFS. Each exceeds the +0.1% threshold. The grouped profile remains opt-in.

On Kickstart 3.1 both handlers recovered the complete old or new payload at all nine continuation cuts. Original SFS remounted 43 candidate sector prefixes across aligned and partial overwrites, including injected write and flush failures. Group pressure, partial journal and home writes, retry after a partial journal read, rejected packets, close/flush/timer boundaries, handoff, foreign startup and automatic TRIM after reallocation all passed. The default and grouped M68000 handlers built, and all 186 CopperStart cache, transfer, buffer and geometry replays and all 77 link and size replays passed. Physical hardware was not exercised.

The five sequential-read fixtures passed. Across 20 cold/warm and Fast/Chip comparisons the revised and original handlers both issued 64 reads per MiB, and revised CopperSFS used 0.43–0.52% fewer cycles than original SFS.

The Release solution build completed with one nullable-reference warning in the CopperSharp compiler dependency. The Release solution test run passed 2,171 tests with five conditional skips and zero failures.
