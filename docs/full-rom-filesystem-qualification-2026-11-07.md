# Full-ROM filesystem qualification, 2026-11-07

The rebuilt A1200 Full reset/extension ROM pair passes **40 logical full-ROM
cases**, with no failures or skips. This includes OFS/FFS DD and HD floppy
persistence, independent exported-media inspection and fresh protected-boot
readback. Classic SFS and SFS2 are qualified on hard-disk images; this is not an
SFS floppy result or a physical-floppy hardware measurement.

## Full-ROM results

| Suite | Passed | Scope |
| --- | ---: | --- |
| Cold boot and mutation | 14 | DOS0, DOS1, SFS0, SFS2; explicit partitions/RDB, writable/protected boots, classic recycling |
| Hard-disk format | 12 | Four formats, supported format paths and protected rejection |
| Extension rejection | 2 | Missing and foreign E0 extensions |
| Floppy persistence | 8 | DOS0/DOS1 × DD/HD × cache disabled/enabled |
| Floppy format | 4 | DOS0/DOS1 × DD/HD; protected rejection, format, persistence/readback |

The floppy persistence cases execute guest writes through the real trackdisk
and MFM paths, export ADFs, inspect on-disk checksums, ownership, allocation and
contents independently, then boot a fresh machine from the exported medium
with writes protected. The format cases also export and boot the resulting
medium. All eight persistence ADFs and four formatted ADFs were retained in
ignored local artifacts.

The two classic-SFS recycling cases were rerun with stronger distinct data:
the old object contains 32 bytes and its replacement contains seven different
bytes. Both the writing boot and fresh protected boot verify `.recycled/Trash`
and collision name `.recycled/Trash$AAA`, as well as the active file. The first
object is saved by `MODE_NEWFILE`; deleting its replacement creates the collision
case. These **2/2** reruns strengthen two of the 14 cold-boot cases and are not
additional logical cases.

The Full machine is PAL Alice/Lisa, 68EC020, 2 MiB Chip RAM, without Slow/Fast
RAM. It uses a 512 KiB F80000 reset ROM with native graphics at FF9600 and the
matching 512 KiB E00000 SFS extension (CSEE/15). SFS2 and formatting are enabled.

## Build and native results

| Coverage | Result |
| --- | --- |
| `dotnet build CopperFS.slnx -c Release` | Passed; one existing compiler nullable warning |
| Initial Full-snapshot Release solution tests | **1266 passed**, three unavailable external-image skips |
| Refreshed frozen-snapshot Release build and tests | Build passed; **1283 passed**, the same three external-image skips |
| CopperStart Release solution build | Passed; 13 warnings, zero errors |
| CopperStart focused filesystem instruction replay | **16 passed** |
| Standalone rebuilt-handler replay | **60 passed** |
| Classic direct FFS TRIM instruction replay | **12 passed** |
| Compiler native execution/register/frame regression | **1025 passed**, including 17 address-source arithmetic cases |
| Existing ROM copy-provider/private-payload native checks | **55 passed** |

CopperFFS, CopperSFS and SFS2 standalone handlers were rebuilt through the
maintained handler build. Compatibility reports contain no managed allocation
sites. A concurrent explicit `scsi.device` payload-bypass change required a
matching FFS rebuild before the final 60-case replay; the superseded binary/test
mismatch is retained diagnostically and is not included in the pass result.

An allocated-register compiler guard rejected legal word/long `ADD` and `SUB`
with an address-register source and data-register destination. The guard and
source effective-address encoding now accept those forms while retaining byte
and logical-operation rejection. Before the fix, all 12 legal-form cases
failed; the five rejection controls passed. Afterward all 17 pass, including
actual MC68000/MC68020 execution and result, CCR, register and stack checks.
The legal addressing modes follow the
[NXP M68000 Programmer's Reference Manual](https://www.nxp.com/docs/en/reference-manual/M68000PRM.pdf).

## Classic layout correction

Classic's FFS TRIM dispatch now uses a direct FFS entry when native SFS writes
are disabled. Its previous boolean dispatcher retained the unrelated SFS
journal writer in the closed-world ROM graph. The direct entry preserves
validation, buffer allocation and DMA-mask fallback, result fields, cleanup,
barriers and errors. Twelve MC68000 tests check these behaviors, including
query-only ordering, protection, allocation failure, invalid roots and absence
of linked smart-journal mutation routines.

The single-bank Classic build selects explicit general size passes, shared
returns, and the existing ordinary-memory copy provider with a 16-byte
threshold. An explicit general-policy `CompactFrameHomeInitialization` option
enables the ROM policy's existing frame transformation under the same narrow
MC68000/YOLO/no-managed-memory ROM guards. Its default is off; guest-memory
clears and other ROM profiles retain their previous selection. The 1025-case
compiler result includes 537 frame cases, with byte-identical initialization
against the existing ROM option and executed checks of holes, arguments,
registers, stack guards and excluded CPU/dynamic-frame cases. The earlier
488-case run is a subset and is not added again.

The maintained build reproduces the first successful candidate byte for byte
from the newer frozen source snapshot. Its compiled code and read-only data
occupy 485562 bytes. Including the eight-byte header, the linked region ends
at FF68C2, leaving **9810 bytes** before the graphics kernel at FF8F14.
The initializer starts at FF9338 and the resident remains at FF9600. Native
graphics, initialization and kernel bytes fit before the checksum trailer.

| Classic full-ROM coverage | Passed |
| --- | ---: |
| OFS/FFS DD persistence and fresh protected boot, cache off/on | 4 |
| OFS/FFS HD persistence and fresh protected boot, cache off/on | 4 |
| Boot, device, raw DD/HD I/O and native Exec identity | 50 |

These 58 cases have no failures or skips. The filesystem cases use PAL OCS,
MC68000, 512 KiB Chip plus 2 MiB Fast RAM. Their eight exported ADFs also pass
the independent filesystem checks and byte-identical protected readback.
Together with the 40 A1200 Full cases, this is **98 logical full-ROM cases**;
the two stronger recycling reruns remain separate repeat executions.

## Provenance and limits

The integration checkout is `CopperStart-copperfs-main` at `84578e9`. Its
development `CopperFSRoot` override consumes this working source; the default
submodule pin remains unchanged. Build scripts copy the filesystem core from
the actual DOS/SystemRom output so an override cannot silently package the
pinned older core. Existing unrelated working-tree changes were preserved.

Qualification uses frozen producer DLLs, compiler dependencies and emulator
runtime inputs. The final `full-qualified` rebuild is byte-identical to `full`,
which supplied the original matrix; the stronger recycling rerun uses
`full-qualified` directly. Replay manifests verify producer/runtime hashes.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Full reset | 524288 | `D3316B4242E41D648F987DA7A8E8C034784E05B92A2D608CA984945A6ED51E00` |
| Full SFS extension | 524288 | `9C300CEC45A948C5CD1F3EF09D9EF4022294F0F0FECA1A202F164B80A90D41B5` |
| Extension-first A1200 bundle | 1048576 | `0B9B2B7EFA1F19EE644300780E574F564D3235DC716653DE868988FF6B1DF1E7` |
| Classic single-bank reset with graphics | 524288 | `393783514B48EB63E677C7B8D42A7BBC635C470746A2C4E476B85202C98B2677` |

Local evidence is under `artifacts/full-rom-current-20261106`: `host-results`,
`results/{cold,cold-recycling,format,reject,floppy,floppy-format}`,
`results/native-instructions`, `results/handler-replay-current`,
`results/compiler-layout-native`, `results/classic-trim`, `results/classic-dd`,
`results/classic-hd`, `results/classic-boot` and `results/rom-copy-provider`.
The refreshed host results are in `host-qualified-results`; their 17 additional
passes comprise five SFS cases and twelve storage-command parser cases.
The newer Classic/host source is captured in `classic-qualified-source`, with
producer, source and compiler hashes retained separately from the Full inputs.
Superseded size-policy
experiments and negative regression runs are excluded from final qualification.
No packages, ROMs or media were published or added to version control.

Physical DD/HD drives, real Amiga hardware and performance improvements remain
unqualified. The three host skips require external images that were unavailable.
