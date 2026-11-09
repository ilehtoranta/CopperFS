# Pinned-input release candidate qualification, 2026-11-09

The handler source is CopperFS commit `3ff1de96e24ccd9733301175b95fb58faa129926`.
The replay and dependency-lock additions accompany this record and do not change
filesystem production code. This run supersedes the binary qualification in
[the earlier record](release-candidate-qualification-2026-11-09.md). No package
or ROM was published.

## Dependency and build identity

The exact dependency revisions, SDK, captured source patches and NuGet package
content hashes are in `qualification/dependencies`. The initializer verifies
commit IDs and Git trees and refuses unrelated local changes. Both captured
patches were independently applied to fresh checkouts and produced the locked
Git tree IDs. The CopperScreen patch is for the test emulator; the CopperStart
patch updates test fixture adapters and the cache replay. Neither enters standalone handler builds.

Four default-CILica 68000 handlers and the Trim command were built twice into
separate artifact directories. Each pair was byte-identical. The compatibility
reports for all four handlers reported zero managed allocation sites.

| Binary | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS | 219116 | `E9DACCE57CA4C0DF2214C618952DC372AE4921374D0FDDD40753ECFB733470A4` |
| CopperSFS classic TRST | 213788 | `E707D28F729D17BEF568178807CF6060366146FF7D91D2EF2F4672A046ED6ED2` |
| CopperSFS SFS2 TRST | 266148 | `E878BE0345E383C58B949C7FB5C4DF5CE7E769E2F57431C810E3CD74BF291CFD` |
| CopperSFS SFS2 CSUN | 273448 | `005CED218FD843D4279AE047B21544543136E231BC686F1804A24C040C9ED046` |
| CopperTrim and CopperSFSTrim, each | 1588 | `E712622AF56D5DD2137E5E52805773CE9EBEA759E2EF21E53C5521044ECA789A` |

## Results

- **Host:** Release solution build passed with one existing CopperSharp nullable
  warning. The solution tests passed 1,418; three optional external-image tests
  skipped. Their absence is not a pass.
- **Native:** The updated cache replay passed 12 cases on the classic handler,
  including both CPU models, cache membership, collision chains, LRU links,
  cursors and cache summary. `InvalidateBlocks` was inlined in this HUNK; the
  replay executes its two compiled operations in source order. The FFS/SFS2
  direct-handler suite passed 204 cases for each SFS2 write policy. The pinned
  CopperStart suite passed 143 cases with no skips, including the 12 updated
  cache replay cases.
- **Kickstart 3.1 full-ROM:** The original FFS 40.1 / SmartFilesystem 1.279
  comparison passed all 11 enabled cases; 12 unrelated cases without their
  separate inputs were skipped. CopperStart classic SFS integration passed two
  cases. SFS2 TRST power-cut and file-position qualification passed eight cases
  with no skips. The SFS2 CSUN power-cut matrix passed six cases with no skips.

The A/B runs 68000 handlers on an emulated 68020 using synchronous CopperHDF
sectors. DD/HD cases qualify partition geometry, not physical floppy DMA or
persistence. Kickstart 2.0, real Amigas and physical storage were not tested.
Original media, ROMs and generated binaries remain in ignored local storage;
results and provenance are under `artifacts/repro-qualification`.
