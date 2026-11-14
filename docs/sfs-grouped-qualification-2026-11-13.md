# SFS cross-packet grouping and deferred close qualification

The opt-in SFS profile now accepts namespace, size and metadata mutations into
the existing two-arena TRST group, and leaves accepted work pending across file
close. The paired warm workload uses **5.03% fewer mutation cycles**, **34.5%
fewer write requests** and **56.6% fewer device flushes**. Per-mount allocation
is unchanged. Original SFS remains faster; this profile is not a new default.

## Implementation and durability

`AmigaSmartFileSystemWriteCore.ExecuteTrstGroup` extends the existing Write-only
group to every TRST operation. The old Write entry points remain wrappers with
their existing semantics. The accepted arena stays immutable while a complete
packet edits its private clone. Failure restores the request, accepted overlay
and accepted roots; success publishes the trial roots and swaps arenas together.
Namespace/metadata operations invalidate the scalar Write continuation hints.
Deferred Write images are materialized before changing or cloning their owner.

Journal-space admission still runs before acknowledgement. Unpublished data
writes still require the existing committed/accepted allocation checks; freeing
and reallocating within a group does not authorize overwriting older accepted
data. The journal encoding and all four commit barriers are unchanged. Failed
publication retains the accepted view and uses the existing recovery path before
retry. Automatic TRIM still refuses a pending group; no low-level flush certifies
filesystem completion.

Manual TRIM (including dry run) now commits a pending group before allocation
validation. Query-only packets remain non-mutating. A failed manual-TRIM commit
sets recovery pending and cannot proceed to discard. Automatic service continues
to refuse pending groups. Full-ROM dry-run tests verify that manual maintenance
commits closed work without issuing UNMAP; automatic tests cover reallocation
and discard failure with the final candidate.

The native adapter groups mutations only with a working writeback timer. It uses
the existing initial arena size, growth policy and 192 KiB ceiling. Capacity or
journal-space pressure first commits the accepted group, then retries the whole
packet through the proven strict path. It never commits a rejected prefix.
Large Writes retain the existing strict fallback. No new allocation or persistent
state was introduced.

Changed-file close can defer only with a timer, no recovery-pending flag and no
failed group publication. Accepted images belong to the mount, independent of
the retiring handle. Explicit ACTION_FLUSH, the non-resetting five-second timer,
inhibit and pressure remain commit boundaries. A successful close consequently
does not promise durability in this opt-in profile: power loss before a commit
can lose accepted work. The existing default's close policy is unchanged.

Enable the profile with:

```powershell
build/Build-Handler.ps1 -GroupedSfsTrst -ArtifactRoot artifacts/sfs-grouped/no-auto
```

`-AutoTrim` is independent and remains opt-in. The build rejects combinations
with FFS, SFS2, strict writes or CSUN write policy. CSUN recovery is retained.
CILica scalar replacement stays enabled and inlining disabled.

## Paired measurements

Three alternating trials per arm and placement, identical SFS media, original
SFS 1.279, 68020 and 32 DOS buffers. Payload placement is explicitly Fast or Chip.
Mount is measured separately; mutation includes the final filesystem flush.
Cycles below are arithmetic means in millions, from the unmodified frame runner.

| Phase | Original | Previous candidate | Grouped candidate |
|---|---:|---:|---:|
| Fast mount | 0.522 | 0.374 | 0.379 |
| Fast mutation | 16.988 | 22.295 | 21.174 |
| Fast read/verify | 17.569 | 20.183 | 20.167 |
| Chip mount | 0.524 | 0.375 | 0.378 |
| Chip mutation | 16.993 | 22.298 | 21.178 |
| Chip read/verify | 17.617 | 20.279 | 20.259 |

Warm mutation improves approximately 5.03% in both placements. Read/verify changes by only
0.08–0.10%. The warm mutation gap to original is still approximately **24.6%**;
the cold release gate, which includes mount work, measures approximately **23%**.
Neither comparison passes the original-handler parity gate.

| Mutation work | Original | Previous | Grouped |
|---|---:|---:|---:|
| Commits (clean-marker writes) | 2 | 35 | 14 |
| Write requests | 17 | 203 | 133 |
| Device flushes | 7 | 113 | 49 |

All six instrumented closes now add zero flushes. Before the final explicit flush
the candidate has issued 46 flushes; the explicit flush adds three, including the
home-image and final clean-marker durability boundaries. In the simple grouped
growth/reopen scenario, all four device flushes occur at the final commit.

The unchanged eight-slot trial capacity still causes pressure commits and strict
retries. The workload does not grow its original small group allocation. Thus
there is a real command-count reduction, but this is not equivalent to original
SFS's two transactions or a claim that the remaining CPU overhead is eliminated.

The normal grouped build without automatic TRIM also fails the cold parity gates:
Fast mutation 21.508M versus original 17.497M; Chip mutation 21.511M versus 17.528M.
It has the same 133 write requests and 49 flushes. Costs have not merely been
moved outside foreground timing: all reported mutation phases include the final
filesystem commit.

## Memory and code size

Availability measurements at mount/mutation/verification markers show identical
per-mount retention in the previous and grouped candidates:

| Sampling point | Previous | Grouped |
|---|---:|---:|
| After mount | 131,256 bytes | 131,256 bytes |
| After mutation | 139,976 bytes | 139,976 bytes |
| After verification | 139,976 bytes | 139,976 bytes |

The retained group allocation including allocator rounding is 8,720 bytes in
both. Original SFS retains 156,480 bytes after mount and 172,904 after mutation in
this default-buffer comparison. These are sampled retention measurements, not
physical-device RAM measurements or a new maximum-memory configuration.

Zero extra RAM here means no additional per-mount state or buffer allocation.
Executable code does grow: the AutoTrim HUNK changes from 156,880 to 157,640 bytes,
and compiler-reported code bytes from 153,122 to 154,094 (+972). The normal grouped
HUNK is 148,616 bytes. Both compiler reports contain zero managed allocation sites.

## Qualification and receipts

Receipts and media remain ignored under `artifacts/sfs-grouped/`; no media or
generated binaries belong in a commit. Important identities:

- Previous AutoTrim candidate:
  `621D4A82B767DA1A3AAFC68960F9C1240B258C42E513C4699AC79D3550015086`.
- Grouped AutoTrim candidate:
  `399797D0622338BEC241159FB2AD6EAEA3EAEFFC5361E8FA959ECC9740E3C6F1`.
- Grouped normal candidate:
  `28AE1A83EB612D9BEE619E047A367CD14CC2EDB54E05415E8332A761ECADF6BB`.
- Original SFS 1.279:
  `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

The extended host mixed-group test covers every mutation kind, rejected duplicate
creation, growth/truncation/replacement/reallocation and removal, cuts at every
modeled I/O event, write/flush failures and recovery/retry. It runs alongside the
existing group tests (36 selected cases pass). The final focused run also checks
allocation counts after every cut and a one-slot capacity rejection after trial
metadata edits; previously accepted metadata remains durable and flushable.

Full-ROM grouped correctness passes in both AutoTrim and normal builds: clean
handoffs, the close/flush trace, reopen visibility, flush/timer/inhibit boundaries,
pre-boundary power loss, published-prefix recovery with both handlers, and failed
commit refusal/recovery at each of four barriers. The published-prefix scenario
has ten sector boundaries, four flush boundaries and five distinct durable
prefixes; all five are restarted with each handler. This uses the emulator's
atomic-sector/reliable-flush model. Physical-device power cuts, DMA behavior and
device latency remain unqualified.

Final coverage, kept separate:

- Release solution build passes. Full solution tests: **1,891 pass, four skip,
  zero fail**. The filesystem subset has 1,560 passes (SFS 1,214, FFS 296,
  Amiga 18, storage 32). Three skips require optional external media and one is
  the opt-in CPX hardware boot fixture. The subsequently added capacity case
  passes in the 36-case focused run; it is not included in the full-suite count.
- Standalone grouped builds pass with and without automatic TRIM; the checked
  default profile also builds. No release defaults were changed.
- CopperStart native replay: **63 pass**, no skips, against the final grouped
  candidate. This is native instruction/source-integration coverage.
- Full-ROM correctness: **29 pass**, no skips: twelve grouped cases per profile,
  two automatic-TRIM cases (reallocation and discard failure), and three checked
  default-profile regressions. The grouped cases include manual TRIM after close.
- Warm paired performance diagnostics: two pass; the four cold original-handler
  parity gates (Fast/Chip, AutoTrim on/off) fail. These failures are not hidden
  inside the correctness totals.

Final receipts use `final-qualification`, `final-no-auto-qualification`,
`final-release-results`, `results/final-*.trx` and `final-native.log` beneath the
artifact root. Earlier pre-manual-TRIM builds and receipts are retained separately
and are not substituted for final candidate measurements. The frozen comparison
HUNK is from `artifacts/sfs-commit-tuning/candidate/CopperSFS`. Set
`COPPERFS_SFS_GROUPED=1`, `COPPERFS_CORRECTNESS_MANIFEST` to the desired manifest,
and `COPPERFS_SFS_REFERENCE` to that frozen HUNK when running `SfsGrouped*` and
`SfsCommitPerformance` tests. The licensed ROM remains an external input.
