# SFS2 open under Kickstart

CopperSFS2 now decodes the primary root into local scalar fields before checking the backup root. The selected fields survive reuse of the scratch block. Both root copies still undergo format, checksum and sequence checks, and the public root probe still returns the selected block image in the caller's buffer.

For a path starting in the root directory, lookup consumes the root record already checked during volume opening. Its offset is used only before the next block access. The final node-tree lookup, container identity, name and comment bounds, clean-volume check and wide file-size bounds still apply. Open extracts the size, directory flag and parent from the checked object without constructing a complete directory entry. All temporary state belongs to the current operation.

## Measurement

The DOS probe uses separate guest bus markers for open, the read loop and close. Each workload is remounted before open, outside the timed interval. The two Copper builds use the same compiler executable and the same Kickstart harness as the preceding run. The native source snapshot isolates the SFS2 reader change from concurrent development. The original is unmodified SmartFilesystem 1.279.

Both CopperHDF cache policies run on the same read-only SFS2 image, with 512-byte sectors, 64 KiB MaxTransfer, Kickstart 3.1 and the PAL M68020 emulator. Results are medians of three trials per arm. The 64-byte workload reads 64 KiB, the 512-byte workload reads 256 KiB, and both larger workloads read 1 MiB. Physical controller latency is outside this measurement.

## What the instruction profile showed

On the 64 KiB workload, CSUN went from nine checksum calls to seven and from eleven block copies to nine during open. Checksum work fell from 39,657 to 30,878 cycles, and copies from 26,783 to 21,877. Total cycles spent executing Copper handler instructions during open fell from 130,061 to 102,051 (21.5%). TRST also dropped two checksums and two copies, with handler cycles falling from 129,640 to 101,849. The original handler took 39,570 handler cycles on the CSUN comparison. All three opens issued zero device reads.

Instruction totals come from separate diagnostic runs. The following table uses complete DOS timings from the ordinary emulator frame driver, including time spent in Kickstart.

| Build | Policy | Read size | Open cycles, previous / new / original | Session KiB/s, previous / new / original |
|---|---|---:|---:|---:|
| sfs2-csun | cached | 64 | 156,430 / 128,420 / 67,700 | 24.18 / 24.14 / 23.68 |
| sfs2-csun | cached | 512 | 156,420 / 135,470 / 66,450 | 165.00 / 165.14 / 163.26 |
| sfs2-csun | cached | 4,096 | 156,360 / 129,750 / 66,450 | 778.15 / 779.44 / 745.18 |
| sfs2-csun | cached | 65,536 | 157,550 / 128,410 / 71,090 | 12,646.09 / 13,490.65 / 18,249.53 |
| sfs2-csun | bypass | 64 | 156,340 / 128,470 / 72,270 | 24.18 / 24.14 / 23.67 |
| sfs2-csun | bypass | 512 | 156,370 / 128,540 / 71,030 | 189.38 / 189.42 / 163.28 |
| sfs2-csun | bypass | 4,096 | 156,420 / 128,450 / 71,030 | 1,470.82 / 1,476.28 / 744.94 |
| sfs2-csun | bypass | 65,536 | 156,330 / 128,520 / 71,080 | 12,644.99 / 13,484.64 / 18,251.36 |
| sfs2-trst | cached | 64 | 157,200 / 128,160 / 72,270 | 24.13 / 24.14 / 23.68 |
| sfs2-trst | cached | 512 | 157,290 / 129,290 / 66,450 | 164.78 / 165.20 / 163.35 |
| sfs2-trst | cached | 4,096 | 168,640 / 136,440 / 71,020 | 776.17 / 779.52 / 745.30 |
| sfs2-trst | cached | 65,536 | 160,560 / 128,150 / 71,140 | 12,637.73 / 13,524.56 / 18,252.74 |
| sfs2-trst | bypass | 64 | 157,260 / 128,080 / 67,700 | 24.12 / 24.16 / 23.68 |
| sfs2-trst | bypass | 512 | 161,850 / 128,170 / 66,460 | 188.90 / 189.68 / 163.30 |
| sfs2-trst | bypass | 4,096 | 157,330 / 128,160 / 71,020 | 1,467.85 / 1,480.49 / 745.38 |
| sfs2-trst | bypass | 65,536 | 157,380 / 128,060 / 71,060 | 12,637.73 / 13,507.21 / 18,248.61 |

At 64 KiB with bypass, the CSUN read-loop median went from 385,410 to 377,720 cycles and close from 17,980 to 17,960. TRST went from 384,750 to 372,420 read cycles and 18,000 to 22,540 close cycles. The original took about 311,000 cycles to read and 14,860 to close. Open accounts for the largest repeatable reduction. At 64 bytes the complete CSUN session median dropped slightly from 24.18 to 24.14 KiB/s despite the faster open.

## Verification

- Host Release build succeeded. 1,270 tests passed and three external-media tests skipped. Four root-selection cases exercise a valid but stale root pointing to the wrong node tree and count primary-root and root-directory reads.
- Native packet replay passed 120 cases across MC68000/020. Both SFS2 HUNKs reported native compatibility and zero managed allocation sites. CopperStart OS integration passed 157 replays with one classic-only skip.
- Kickstart ran 144 current-versus-original trials including FFS controls, and 48 previous-Copper SFS2 trials. Returned bytes were hash-checked and media remained read-only. Twelve separate SFS2 write/read interoperability boots passed across the two original/Copper pairings.
- Reproduction uses the existing Kickstart sequential-read harness, original handler hashes and both payload policies. Raw trials and ranges are under `artifacts/kickstart-ab14-{cached,bypass}`, previous runs under `artifacts/ab14-validation/previous-{cached,bypass}`, profiles under `artifacts/ab14-profile`, and native build inputs under `artifacts/ab14-final`. The A/B used hash-verified original HUNKs from the preceding boot fixtures.
