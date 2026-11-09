# Release candidate qualification, 2026-11-09

This record covers CopperFS source revision `3ff1de96e24ccd9733301175b95fb58faa129926`.
It is a qualification record, not a published release. The candidate was built
from a clean CopperFS working tree. CopperSharp68k, CopperStart and CopperScreen
had local modifications; their revisions and local state are recorded under the
ignored `artifacts/release-candidate-3ff1de9/provenance` directory. These builds
must not be represented as reproducible from the dependency revisions alone.

## Candidate binaries

The four standalone handlers use the MC68000 target and the default CILica
pipeline. Their compatibility reports recorded zero managed allocation sites.

| Binary | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperFFS | 218876 | `5B24F7254E72AAD913FC9C2C696CBDA8E84A6992232AAFBF999554B3B0B7F0C6` |
| CopperSFS, classic TRST | 213604 | `EC5E2E6CE451DB97838B605DC3A641B58FED7CDEAA450C790968B443EE00367A` |
| CopperSFS, SFS2 TRST | 265824 | `4272DF05978392AB5FB4FA3CB837FBCBD8E8CEED0E2EACACB189D42E827D4D68` |
| CopperSFS, SFS2 CSUN | 272708 | `A90072AE9226EA41E7ECEAA2511E908E913965CE7CD2C95E92C6BCC2D5D02EFA` |
| CopperTrim and CopperSFSTrim, each | 1588 | `E712622AF56D5DD2137E5E52805773CE9EBEA759E2EF21E53C5521044ECA789A` |

## Results

- **Host:** Release solution build passed with zero warnings and errors. The
  Release solution tests passed 1,418, with three external-image tests skipped.
- **Native instruction replay:** the current FFS and SFS2 handler test suite
  passed 204 cases with each SFS2 write policy. CopperStart source-consumer
  instruction tests passed 131 cases. A separate handler copy test passed 16.
  The old cache replay failed ten cases before executing the behavior because
  it expects `NativeFileSystemPlatform::InvalidateBlocks`, which is absent from
  the current optimized handler map; one cache-summary case skipped. These
  failures are not passes and need an updated replay or equivalent coverage.
- **Kickstart 3.1 full-ROM:** both CopperStart classic-SFS integration cases
  passed. The original-handler comparison passed 11 enabled cases, comprising
  nine format/geometry comparisons and two SFS2 journal handoffs; 12 unrelated
  unconfigured tests skipped. The SFS2 TRST power-cut and file-position suite
  passed eight cases, with no skips.
- **SFS2 CSUN power-cut matrix:** six cases passed with no skips.

The A/B uses the original FFS 40.1 and SmartFilesystem 1.279 binaries checked
against the hashes pinned in `tools/Test-OriginalHandlers.ps1`. It uses an
emulated 68020 with 68000 handlers and synchronous CopperHDF sectors. DD/HD
cases cover partition geometry, not floppy DMA or physical disk persistence.
Results, input identities and candidate hashes are retained under the ignored
`artifacts/release-candidate-3ff1de9` directory. ROMs and media are not checked in.

Kickstart 2.0, physical Amiga hardware and physical media were not tested in
this run. The three optional host image tests remain unavailable coverage.
Before a stable release, resolve the cache replay gap, establish reproducible
dependency inputs, and decide the supported release
scope and version without replacing existing published versions.
