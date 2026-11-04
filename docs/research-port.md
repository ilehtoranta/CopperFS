# Selective research port (2026-11-04)

Source: CopperSFS research commit `4a902a1ff9189bbdff841c23441c66678a6e2427`,
preserved as `codex/preserve-local-research` in CopperFS. Baseline:
`a110fcf8e586287c795e0256c70d2147672becf8`. This port changes host tests and
diagnostic tools; it does not replace filesystem or native handler implementations.

## Included

- Crash-state Cartesian products, byte-verified corpus containment and their tests.
- Reliability simulation with guarded partition boundaries, read/write/flush failures,
  torn writes and restart checks. Selected exhaustive mutations: create, overwrite,
  append, shrink, truncate, delete and rename. Seeded 300-operation reference-model
  tests and torn-write scenarios remain separate tests.
- Block-size round trips; SFS2 read-only, metadata recovery, batched writes and
  wide-file tests; image-checker corruption cases.
- Optional recovery of a transaction captured from the original SFS2 handler.
- `CopperSFS.Check`, a read-only checker for a raw partition image, not a whole
  partitioned disk; `CopperSFS.Bench`, an in-memory diagnostic study runner.
- Dated research documents under `docs/history/research-20261024`. Their results
  and commands describe the old branch and are not current qualification.

The existing mainline StudyDisk, StudyFixture, discard simulator and image checker
were moved into the tools and shared with tests through project references.
Their implementation was retained. New simulator adapters implement the current
metadata/data I/O contract; the wide simulator also implements the TRST overlay.
No CopperStart or CopperScreen build dependency was added.

## Running

```powershell
dotnet build CopperFS.slnx -c Release
dotnet test CopperFS.slnx -c Release
dotnet run --project tools/CopperSFS.Check -c Release -- partition.img 512 1
dotnet run --project tools/CopperSFS.Bench -c Release -- --mode benchmark --repetitions 3 --output artifacts/study.json
```

The two `FullBatchesRelocationAndCutsPreserveEveryFile` cases are tagged
`Category=Extended` and can take many minutes each. The unfiltered solution
command includes them. For a routine run use `--filter "Category!=Extended"`;
run `--filter "Category=Extended"` separately to retain the full coverage.

The study requires a fresh output filename. Modes are `benchmark`, `recovery`,
and `all`. It hashes source and binaries and reports failures, not valid timings,
when setup or integrity checks fail. These timings are host CPU costs on an
in-memory device, not host disk or Amiga throughput.

Exhaustive wide-volume crash enumeration is included in the default suite.
A focused run passed all three scalar, batched and TRST cases; separate quick
round trips also verify wide writes, resize and reopen.

External image tests use `COPPERSFS_SFS2_REFERENCE_IMAGE`; captured redo recovery
uses `COPPERSFS_SFS2_NATIVE_REDO_IMAGE`. Missing images are skipped, not passes.
No licensed media is included. `COPPERSFS_RELIABILITY_ARTIFACTS` optionally names
an ignored output directory for failed synthetic images and context.
`COPPERSFS_MAX_CRASH_STATES` is a safety bound; exceeding it fails, rather than
silently sampling an allegedly exhaustive test.

## Deferred differences found during the port

These are unresolved research-versus-main differences, not dismissed failures or
proof that the old implementation should replace main. Original assertions remain
available at the source commit. No assertions were weakened to hide these cases.

| Research suites / cases | Observation and follow-up |
| --- | --- |
| FileSystemCompatibilityTests | Packed containers, embedded-null comments, new extent owner tags, node fullness bits and metadata relocation need focused investigation. The benchmark independently fails its setup checker with `Block 6: incorrect node fullness flag`. |
| FileSystemRegressionTests | Node-branch growth failure scenario needs comparison with current tree allocation behavior. |
| TransactionRegressionTests | Archive-bit updates, empty directory-container unlinking and reads after failed writes differ. Check current mount/read contracts before porting fixes. |
| ReliabilityTests advanced growth scenarios | Admin-growth setup failed; advanced growth and packed cases are not part of the selected basic-mutation matrix. |
| BatchedWriteTests, RelocationTests, ReservedOverwriteTests | Old range-count and scratch-buffer assumptions differ from current scalar/range APIs and small-write policy. Main's existing batching/relocation tests remain intact. |
| TrstWriteTests | Hard-coded marker position, four-flush expectation and journal-space assumptions differ from current TRST placement and reservation. Main's existing TRST tests remain intact. |
| Native reference runners and original-handler handoff suites | Depend on external emulator/compiler harnesses, media and old reflection contracts. Only captured-image recovery is included here. |

Classic historical zero first-extent backlinks are accepted by main's checker.
The old test rejecting that representation was omitted; the existing wrong-owner
corruption case remains. The benchmark currently produces a failed diagnostic
report at workload preparation; it does not provide current performance evidence.
The research writer contains an `UpdateNodeFullness` routine absent from main's
classic writer. That is a concrete candidate for the next production fix, with
`FillingNodeLeafMustSetItsParentFullBit` and
`FreeingNodeLeafSlotMustClearItsParentFullBit` as regression cases. Its interaction
with main's newer buffered/TRST paths still needs review and native validation.

## Validation

On Windows, the final standard Release solution build succeeded with zero
warnings and errors. An earlier isolated build emitted one existing CS8602
warning in the sibling CopperSharp compiler. The final edits passed 52 focused tests;
one external-image case was unavailable. The checker command was exercised on
valid and corrupt images, with input bytes unchanged, and on missing arguments.
The standalone recovery command passed all 40 scenarios across four block sizes;
its source hashes were unchanged during execution. The benchmark correctly
returned failure for the node-fullness finding described above.

The final frozen solution, excluding the two extended batch cases, passed
1,118 tests with three external-media tests unavailable. Both extended batch
cases also passed (9m54s without relocation, 1m19s with relocation). Across these
runs, 1,120 distinct tests passed and three external-media cases were unavailable.
The final Category filter was checked to select exactly those two extended cases.
No native replay pass is inferred from these managed results. Native
handlers, full ROM boots and Linux/macOS storage integration were not rerun
for this host-only port.

Local receipts (ignored build artifacts): `artifacts/research-production-build.log`,
`artifacts/research-frozen/`, `artifacts/research-verified/`,
`artifacts/research-final-edits/`, `artifacts/research-wide/`,
`artifacts/research-recovery-validated.json`, and
`artifacts/research-benchmark-validated.json`.
