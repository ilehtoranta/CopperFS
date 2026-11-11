# Main integration, 2026-11-12

This integrates the saved development changes from `94ca34e` with remote main
`21a09a9`. Integration and validation used an isolated worktree because newer
freed-range queue work continued in the original checkout. That newer work is
outside this commit and remains in the original checkout.

Upstream's FFS read cursors, metadata borrowing, read-ahead, adaptive timing,
SFS2 input caches and qualification tooling are retained. The smaller private
FFS geometry and paired arithmetic changes are applied to the compatible
traversal and writer paths. Public layouts, filesystem formats and journal
ordering are unchanged.

The build keeps upstream's no-inline CILica stage, FFS call-clustering exception
and default SFS2 TRST policy. CSUN is selected explicitly with
`-Sfs2 -Sfs2CsunWrites`. `-SharedDispatch` aliases `-NoSpecialization`;
the `CopperFsHandler` property also selects the existing family symbols.
Automatic TRIM remains disabled unless `-AutoTrim` is supplied.

Timer integration keeps the normal FFS sampling request and its cleanup.
Opt-in automatic maintenance uses a deferred timer on FFS and strict mounts,
and inhibit/remount resets its validation state. Native test probes retain
explicit compiler assembly inputs and source-linked arithmetic helpers.

## Validation

- Release solution build succeeded. The full host suite passed 1,436 tests;
  three external-media tests were skipped.
- All four MC68000 handlers were rebuilt with automatic TRIM disabled and
  enabled. Artifacts remain outside version control.
- CopperStart actual-handler instruction replay passed 193 tests; two
  optional SFS2 cache-summary cases were skipped.
- Seven arithmetic checks passed, including native 68000/68020 execution.
  Two opt-in validation-invalidation instruction cases passed.
- Kickstart 3.1 original/Copper clean handoff passed all nine profiles, including
  OFS/FFS DD/HD geometry cases. These do not qualify physical floppy DMA.
- Thirteen opt-in OFS/FFS/classic-SFS/SFS2 discard, failure and inhibit/remount
  cases passed through Kickstart with poisoned deallocated sectors and original
  handler readback.

The integration run is functional coverage, not a new performance comparison
or release qualification. Earlier dated performance reports apply to their
recorded source/compiler snapshots. Other ROM versions, physical-device tests
and a new full-ROM crash matrix were not run here. No package, ROM, media or
handler binary was published.

Logs, TRX results, input assemblies, maps and binary hashes are retained under
the ignored `artifacts/publish-main-20261112` directory in the original checkout.
