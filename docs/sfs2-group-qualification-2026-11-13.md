# SFS2 TRST cross-packet grouping

The opt-in `-GroupedSfs2Trst` handler groups successful packets in volatile
mount-owned RAM. The ordinary SFS2 TRST profile remains available, and automatic
TRIM remains disabled by default. This changes acknowledgement timing: successful
mutation packets can precede durability, while successful flush, changed-file
close and inhibit complete the existing TRST commit protocol.

## Design and boundaries

The existing 192 KiB SFS2 staging allocation contains a 32-byte group header
and two arenas. One arena represents accepted packets; the other receives a copy
of its occupied slots and the current packet's changes. Only a complete successful
packet exchanges the arenas. Reads and metadata lookups see the accepted overlay.
A failed packet restores the prior overlay and request outputs. No additional
per-mount allocation or on-disk structure is introduced.

New file data can bypass staging only when both the durable bitmap and the
earlier accepted bitmap prove its destination free. This is necessary because
an earlier packet can own blocks still marked free on disk. Failed later packets
must not overwrite that earlier packet's unpublished data. Existing reachable
data stays in redo staging. Unpublished writes from a rejected packet can leave
unreachable bytes on disk, but cannot change either accepted or durable files.

Commit boundaries are explicit flush, changed-file close, inhibit, the existing
five-second timer, and arena pressure. Pressure first commits the accepted group,
then retries the complete rejected packet through the existing strict path using
the full allocation. Without a working timer each successful mutation commits
before replying. Packet processing remains single-owner throughout.

Commit still writes the journal, publishes TRFA, writes home images and installs
TROK, with the existing barrier after every stage. A failed publication detaches
the overlay and blocks further staging until normal recovery succeeds. A power
loss before publication can lose the volatile group; a published group rolls
forward atomically, including under original SFS recovery.

Manual TRIM commits pending changes before inspecting allocation. The core also
rejects discard while a redo overlay remains attached: a device flush alone cannot
certify that transaction. Automatic TRIM's existing pending-write gate now sees
SFS2 groups, so it cannot reclaim blocks from an uncommitted accepted view.

Inhibit commits before handing the volume to another owner. Reopening invalidates
the existing metadata caches. Abrupt physical media replacement, controller reset
and physical-device timing are not qualified here.

## Qualification

### Performance against original SFS

Three alternating original/candidate trials use the existing full-ROM 68020
release workload, including its final filesystem flush. Payload placement is
controlled separately in Fast and Chip RAM; profiling is disabled for these gates.

| Placement / phase | Original SFS cycles | Grouped TRST cycles | Excess over original |
| --- | ---: | ---: | ---: |
| Fast / mutate | 15,586,510 | 30,266,940 | 94.19% |
| Fast / verify | 17,150,247 | 20,358,930 | 18.71% |
| Chip / mutate | 15,588,720 | 30,268,180 | 94.17% |
| Chip / verify | 17,192,233 | 20,441,927 | 18.90% |

The previous qualified TRST candidate measured 32,041,427 Fast-RAM mutation
cycles: this candidate reduces that by **5.54%**. This is a comparison with the
previous report's measurements, not a newly rebuilt baseline. In the same phase,
reads fall **109 → 48**, writes **154 → 52**, and flushes **104 → 28**. Original
SFS performs 17 writes and seven flushes. Both placement gates still fail their
speed assertions; payload and image validation pass. No deferred commit cost
is excluded from the mutation interval.

The first implementation staged all new data. It reduced flushes but regressed
CPU time and journal traffic; it is rejected. The selected two-view free-space
proof avoids that regression. The relatively modest cycle gain despite large
I/O-count savings means grouping does not solve the remaining CPU overhead.
These emulated cycle results do not measure physical controller flush latency.

### Correctness and build coverage

- Release solution build passes with one existing upstream compiler nullable
  warning. Final solution tests pass **1,501 filesystem-related cases** with
  three external-image skips, plus 39 prototype cases from concurrent workspace
  work. The prototype edits were preserved and are outside this change.
- Both grouped standalone profiles build, with automatic TRIM opted in and
  disabled. **193 CopperStart native replay tests pass**; grouped behavior itself
  is additionally exercised through the standalone full-ROM handler.
- Host grouping cases cover every commit write/barrier cut, injected commit and
  read failures, pending-view reads, buffer exhaustion, accepted unpublished
  data and refusal to TRIM an uncommitted group.
- The native pressure probe confirms two commits (eight barriers) before the
  oversized write replies: the earlier accepted group and the strict retry.
  Timer and inhibit probes confirm no pending writes followed by four barriers.
  Their initial five-barrier expectation was corrected: the existing platform
  suppresses a redundant clean-device flush. Handler code was unchanged.
- **31 full-ROM correctness/recovery cases pass** on the selected automatic-TRIM
  candidate: 17 main matrix cases, 12 foreign/manual-TRIM/SFS handoff cases, one
  pressure case and one published-CSUN recovery case. The main log contains two
  superseded boundary-assertion failures; `boundaries-final.log` replaces those
  two cases and adds pressure coverage, without double-counting them.
  The grouped-without-automatic-TRIM build passes three further boundary cases,
  for **34 cases across both configurations**.
- All six mutation cut matrices and the additional multi-packet group matrix
  include recovery with original SFS. Larger, fragmented and warm automatic
  reclamation, reallocation, partial discard failure, a cut during discard and
  inhibit/remount also pass. CSUN recovery and SFS support are preserved.
- Logs: `build-final.log`, `solution-tests-final.log`, `replay-selected.log`,
  `compatibility-final.log`, `foreign-final.log`, `boundaries-final.log`,
  `boundaries-no-auto.log`, `csun-recovery-final.log`, and `parity-final.log`.
  Physical-controller/SSD, real DMA, AmigaOS 2.0 hardware and abrupt physical
  media replacement qualification remain unavailable; they are not passes.

Artifacts are under `artifacts/trst-group`; `native` and `final-native` are
intermediate all-data-staged builds. `optimized-native` uses the two-view free-space
proof and is the candidate to qualify. Its CILica profile retains scalar
replacement with inlining disabled.

The selected handler is 210,916 bytes, SHA-256
`0733EAA7EFE6B90EFC93800B3209677CAF4830456FC2CB59D07B4BB937ABDE34`.
The grouped handler without automatic TRIM is 198,772 bytes, SHA-256
`95E385338353FC480543EE8565B8DB68AC3BFABAF1217FF1447A91CEFDE2E229`;
its timer, inhibit and pressure probes also pass (three ROM cases). It is under
`no-auto-native`. The full compatibility/performance matrix uses the opt-in
automatic-TRIM binary above, matching the prior qualification profile.
The manifest pins original SFS 1.279, SHA-256
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
Raw samples are in `original-performance/sfs2-trst/cycles.json` and
`original-performance-chip/sfs2-trst/cycles.json`; `performance-summary.json`
contains their means. Grouping remains opt-in while the original parity gate
is unmet. No physical-device qualification is claimed.

Host tests exercise accepted-view reads, rejected large packets, read failures,
pending-TRIM refusal, newly allocated data, every commit write/barrier cut and
commit failures followed by recovery. Full-ROM tests add multi-packet recovery
with original SFS and timer/inhibit boundary counters.

Reproduce the native build with:

```powershell
./build/Build-Handler.ps1 -Sfs2 -GroupedSfs2Trst -AutoTrim `
  -ArtifactRoot artifacts/trst-group/optimized-native
```

`-AutoTrim` opts in solely for qualification. Set `COPPERFS_CORRECTNESS_MANIFEST`
and `COPPERFS_AUTOTRIM_MANIFEST` to the manifest in this artifact directory.
Supply the licensed ROM via `COPPERSHARP_KICKSTART31_ROM`, the candidate via
`COPPERFS_SFS2_TRST_HANDLER`, and original SFS via `COPPERFS_ORIGINAL_SFS`.
The new grouped-only ROM assertions require `COPPERFS_SFS2_GROUPED=1`.
Foreign/manual-TRIM comparisons also use `COPPERFS_FOREIGN_BASELINE_ROOT` and
`COPPERFS_FOREIGN_CANDIDATE_ROOT`, containing each manifest's pinned binaries
under `ffs`, `sfs` and `sfs2-trst` subdirectories.
Existing strict no-op assertions describe the non-grouped profile; the grouped
boundary tests replace them for this candidate.

No ROMs, media, packages or release publication are included in this work.
