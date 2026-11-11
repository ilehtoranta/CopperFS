# Automatic TRIM qualification, 2026-11-12

Automatic TRIM remains experimental and disabled by default. The repeated-map
work is reduced, but the candidate does not meet the requested performance gate
against the original implementations. No package or ROM has been published.

## Bitmap and barrier cost

The same 8 MiB SFS2 fixture previously needed 92 block reads and four flushes for
four deferred windows. The revised certified path needs 65 reads and one flush:
29.3% fewer reads and 75% fewer barriers. For comparison, one unwindowed manual
pass uses 23 reads and one flush; deferral still has a real scheduling cost.

Additional host cases measured 257 reads/one flush across sixteen windows on a
32 MiB volume with 512-byte blocks, and 115 reads/one flush across eight windows
on a 128 MiB volume with 4 KiB blocks. These are synthetic I/O counts, not device
latency or sustained SSD throughput measurements. Whole-layout validation is
retained once per unchanged mount state, and touched bitmap pages are checked
each time. Mutation and lifecycle events invalidate the certificate.

The earlier extra OFS verification read was traced to automatic unsupported-device
discovery reading the boot block before querying support. The automatic path now
queries first; a targeted test confirms that unsupported discovery performs no
block read or flush. The manual command keeps its established ordering.

## Original-handler performance gate

The references are hash-pinned original FFS 40.1 and SmartFilesystem 1.279. Each
profile rotates three original/candidate trials using the same media, emulated
CPU and Fast RAM payload allocated before loading the handler. Mutation timing
includes an explicit filesystem ACTION_FLUSH packet; verification is measured
separately. DOS stream Flush(file) is not used as a filesystem checkpoint.

The final comparison produced the following cycle changes relative to the
originals (positive means slower):

| Handler | Mutation + filesystem flush | Verification |
| --- | ---: | ---: |
| ofs | +320.38% | +16.79% |
| ffs | +239.50% | +17.14% |
| sfs | +104.05% | +13.76% |
| sfs2-csun | +326.56% | +70.50% |
| sfs2-trst | +1774.13% | +67.72% |

All five profiles fail parity, even with the existing 0.1% interrupt allowance.
Earlier internal Copper-before/after comparisons are diagnostics, not the release
gate. A frozen build with automatic TRIM disabled also failed the initial FFS
comparison (+240.15% mutation, +15.61% verification), showing that the large gap
predates this feature. That diagnostic did not include the added flush packet;
it is not substituted for the final checkpointed comparison above.

Mutation-phase I/O command counts help explain where further work is needed:

| Handler | Write commands original / Copper | Flushes original / Copper |
| --- | ---: | ---: |
| ofs | 314 / 914 | 2 / 1 |
| ffs | 47 / 630 | 2 / 1 |
| sfs | 17 / 563 | 7 / 108 |
| sfs2-csun | 17 / 337 | 7 / 260 |
| sfs2-trst | 17 / 149 | 7 / 104 |

These are device command counts, not bytes or individual sectors. FFS metadata
publication and batching, SFS transaction/barrier frequency, and native execution
cost need separate profiling. The read-verification gaps also remain. Removing
TRIM preflight alone cannot close these differences; weakening recovery ordering
or hiding the failed profiles would not establish parity.

## Coverage and limits

- Host Release solution: 1,337 passed, three external-image tests skipped.
  Additional final focused checks cover 128 MiB/4 KiB media, certificate
  invalidation after failed writes, cached checkpoint cuts, and unsupported
  automatic discovery. The final focused run passed 22 cases.
- Native: all four affected standalone 68000 profiles rebuilt with automatic TRIM.
  Family-separation checks passed. CopperStart's 147 selected deletion, buffer and
  geometry replays passed. Two additional 68000/68020 instruction replays verify
  invalidation on attempted writes and lifecycle reset without managed allocation.
- Full ROM: all 19 automatic qualification cases passed. The suite exercises
  two windows, poisoned freed space,
  reallocation, original-handler readback, partial-discard errors, inhibit/resume,
  and cuts during irreversible discard. Results are retained in the evidence
  directory described below. Host checkpoint-cut tests cover the earlier
  durability gate with cached and uncached validation.

Cuts intended to test durable post-workload state are armed only after the guest
has completed its workload and scheduled a fresh maintenance pass. An initial
TRST cut fired during the long mutation workload, before its final metadata
updates; expecting that workload's final timestamps after that earlier crash
was an invalid test assumption. The corrected test retains the full file and
metadata assertions and explicitly selects the intended boundary.

Physical SSD/controller behavior, asynchronous device completion and actual
media replacement remain unavailable coverage, not passes. Inhibit/remount tests
are separate emulator evidence. Larger real partitions and foreground latency
under a genuinely slow discard device still need qualification. The default
must remain disabled until these limits and the original-handler performance
gap are addressed within the chosen release scope.

Evidence, frozen handler hashes, compiler inputs and manifests are retained
under ignored `artifacts/autotrim2`. The `qualified` subdirectory contains the
final tested handlers. `checkpoint-original.log` and `performance-summary.json`
record the final reference comparison; earlier prototype results are separate.
