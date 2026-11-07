# Original-handler correctness A/B, 2026-11-05–06

Update, 2026-11-06: the classic-SFS gap below is now fixed and the expanded
matrix passes 9/9. See the [replacement qualification report](sfs-mode-newfile-compatibility-2026-11-06.md).
This report retains the initial findings and binary qualification.

Eight of nine strict comparison cases pass. Classic SFS passes the guest
operations and all clean handoffs, but its persisted file manifest differs from
the original: `MODE_NEWFILE` truncates the existing file in place instead of
preserving its previous contents in `.recycled`. This remains a failing test;
the comparison does not filter the recycled directory out of the results.

Two other problems found by these runs were corrected:

- The standalone OFS/FFS packet adapter interpreted Kickstart 3.1's date address
  as a BPTR and persisted `FFFFFFFF` date fields while returning success. It now
  accepts the expanded address when the shifted address is outside guest memory,
  matching the existing SFS adapter's compatibility path. The BPTR path remains.
- FFS 40.1's OFS append leaves the previous EOF forward link zero and starts the
  new allocation's sequence at one. Copper rejected these files. Reads now
  recognize that observed boundary; growth/release validate the separate runs
  and explicitly check earlier pointers for duplicate ownership when sequences
  restart. Canonical files retain their original path. Newly authored Copper
  OFS images still have absolute sequences and complete forward links.

## Main matrix

Each row uses identical initial media and the same compiled guest program for
both handlers. Both writers perform the workload independently. Each resulting
image then boots with both readers, giving six completed boots per row. All 54
guest workloads pass, including unchanged media hashes after verification boots.
The strict case result additionally requires equivalent independently extracted
file paths, sizes and content hashes, including recycled files.

| Format / candidate policy | Media / supplied geometry | Guest boots | Strict result |
| --- | --- | ---: | --- |
| OFS | 16 MiB, 2 × 128 × 128 | 6 | Pass |
| FFS | 16 MiB, 2 × 128 × 128 | 6 | Pass |
| OFS | DD, 1760 blocks, 2 × 11 × 80 | 6 | Pass |
| OFS | HD, 3520 blocks, 2 × 22 × 80 | 6 | Pass |
| FFS | DD, 1760 blocks, 2 × 11 × 80 | 6 | Pass |
| FFS | HD, 3520 blocks, 2 × 22 × 80 | 6 | Pass |
| SFS v3, buffered TRST | 16 MiB, 2 × 128 × 128 | 6 | **Fail: recycled truncation copy absent** |
| SFS2, CSUN | 16 MiB, 2 × 128 × 128 | 6 | Pass |
| SFS2, TRST | 16 MiB, 2 × 128 × 128 | 6 | Pass |

All sectors are 512 bytes. The machine uses a 68020, 512 KiB chip RAM, 2 MiB fast
RAM and real licensed Kickstart 3.1 Exec/DOS. Handlers and guest applications are
compiled for MC68000. Target storage is synchronous `copperhdf.device`; the
DD/HD rows exercise filesystem capacity and track geometry, not target floppy DMA.

The shared workload creates directories, a 32 KiB donor and an 81937-byte file
using varied packet sizes. It deletes the donor, appends 8193 bytes, performs
unaligned overwrites, renames the file, truncates another through `MODE_NEWFILE`,
creates an empty file and sets a fixed comment, protection and date. Reads check
every byte, backward seeks, EOF and short reads. Enumeration checks the complete
active directory without depending on entry order. Missing objects and deleting
a nonempty directory check DOS errors 205, 216 and enumeration EOF 232.

The independent DOS walker checks metadata/data checksums, pointer tables,
extension ownership, OFS data headers, bitmap ownership and padding, and extracts
payload hashes. It records the original OFS append pattern explicitly; arbitrary
sequence or link mismatches are rejected. The separately implemented SFS checker
walks objects, extents, nodes, administration allocation and bitmaps, including
recycled files. Neither checker calls the production filesystem traversal code.

The classic-SFS difference is precise: original output contains both
`/.recycled/Donor` and `/.recycled/Truncated` (the latter is the old 4097-byte
file); Copper output contains the donor but not the old truncated file. Active
`/AB` paths, sizes, metadata and bytes match, and both implementations read both
saved images. Recycle semantics are therefore not fully compatible.

## References and frozen candidates

Reference binaries remain external, unmodified inputs:

- Original FFS 40.1 (15.2.93), used for DOS0 and DOS1:
  `9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773`.
  Extracted earlier from the locally licensed Workbench 3.1 Install disk;
  archive/image/extraction hashes are retained in the provenance receipt.
- Original SmartFilesystem 1.279 (23.12.2007), used for SFS v3 and SFS2:
  `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
  Its pinned SFS.lha archive hash is
  `E6C4C3AEE7BAC96C7E6EDDED162FDA8B5F9D9ABF9680112B5028C5CF96B47D92`.
- Kickstart SHA-256:
  `8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE`.
- The clean classic-SFS base is independently formatted by amidisk at revision
  `a4faa7b36bd7c005371d9bb509a936a13d58b43f`, with two reserved blocks.
  DOS fixtures are original repository fixtures; SFS2 uses the repository's
  synthetic fixture, independently accepted and mutated by the original handler.

| Candidate | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS | 171532 | `FA0694CD9CD70BC1B8DDA4DCD3FDD10D0C0022F98C00D9F218BBA0C2901C3C7C` |
| CopperSFS, classic buffered TRST | 171956 | `0116D7EFC3A5AAE500BC5E6649CF9B9307213D58AE330D6352A4132B04181141` |
| CopperSFS, SFS2 CSUN | 216388 | `B2E9778C37DED2C7598B592591724233BF3A7F7B3BCDA1AAC71ED9809BA83C34` |
| CopperSFS, SFS2 TRST | 210704 | `4620F2C741FB54BC1479F8A580BACAA3B0FBAABC69A0A48F1278363F6857C15D` |

All four handler builds are framework compatible with zero managed allocation
sites. The guest probes have the same compatibility/allocation checks.

Read optimizations continued concurrently. `NativeAmigaFileSystem.cs` changed
after the captured inputs for all four candidates; `NativeFileSystemReadCore.cs`
also changed after the CopperFFS input build. Results qualify the hashes above,
not those later working-tree edits. The saved portable-PDB document receipts
record actual source hashes per candidate, alongside managed-input hashes and
compiler compatibility reports. Both corrections described above are present
in the tested candidates.

## Separate validation coverage

Host: `dotnet build CopperFS.slnx -c Release` succeeds with one existing compiler
nullable warning. `dotnet test CopperFS.slnx -c Release` ran the complete suite.
The initial FFS read-count assertion failed during concurrent read optimization;
the subsequent current FFS suite passes 227 tests. Together with 956 SFS and 18
Amiga host tests, the validated project results are **1201 passed, 3 external
fixture skips**. The four new OFS regression cases include reads into an appended
run, subsequent overwrite/growth/release and duplicate-pointer rejection; the
existing corruption and write-failure tests still pass.

Compiled native replay: **32 passed**, no skips, using the rebuilt CopperFFS and
SFS2 CSUN HUNKs on MC68000/MC68020 through CopperStart's instruction replay bus.

Real Kickstart: the main matrix completes **54 distinct guest boot checks**, but
its strict aggregate is **8 passed / 1 failed**. A rerun completed all six classic
boots before retaining the manifest failure. Separately, CopperStart's existing
classic-SFS A/B suite passes **7 tests / 18 guest boots**, using the frozen classic
handler and its independently formatted fragmented fixture. These cover mount,
write/read, clean handoffs, date, shared handles and mutation-aware enumeration.
That older shared workload truncates through `SetFileSize`, so it does not cover
the newly identified `MODE_NEWFILE` recycle difference.

These are clean-close/remount tests. There is no new crash/torn-write A/B,
physical-floppy test, target floppy-DMA qualification, physical speed claim or
requalification of a custom CopperStart ROM for these corrections. Earlier ROM
and floppy results are separate binary qualifications.

## Reproduction and evidence

Use [the optional suite instructions](../tests/CopperFS.Kickstart.Tests/README.md)
and `tools/Test-OriginalHandlers.ps1`, supplying the original binaries, licensed
ROM and independently formatted clean 16 MiB SFS image. The default script builds
all four candidate profiles; `-FrozenHandlerRoot` explicitly accepts frozen ones.
At this baseline the strict suite was intentionally left failing rather than
excluding recycled files. The linked follow-up records the fix and passing rerun.

Local evidence is under ignored `artifacts/correctness-ab/`: `summary.json`,
`final/provenance.json`, `final/source-document-receipts.json`, per-profile
`qualification.json`, both independent inspection reports, per-boot `result.json`,
and TRX results. `final/results/correctness.trx` records the complete 8/1 run;
`classic-recheck.trx` records the completed classic handoffs and retained failure.
`final-host-results`, `final-native-results`, `copperstart-results` and the copied
18 `copperstart-reports` retain the other coverage. Initial failing controls and
the targeted OFS fix rerun remain alongside them.

No ROMs, original handlers, generated media or build artifacts are tracked, and
no package or ROM was published.
