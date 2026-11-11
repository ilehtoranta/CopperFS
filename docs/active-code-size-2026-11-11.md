# Active filesystem code size reduction, 2026-11-11

This continues the [family-specialization pass](handler-size-optimization-2026-11-11.md).
The active-code changes reduce all four handlers and pass the measured
performance checks. They also allow SFS2 TRST family specialization to be kept
after a fresh comparison against its optimized shared-dispatch control.
Original-handler size parity is still outstanding.

## Accepted executable sizes

Complete MC68000 freestanding HUNK files, in bytes:

| Handler | Previous accepted build | This build | Bytes removed |
| --- | ---: | ---: | ---: |
| CopperFFS, including OFS | 55,976 | 55,124 | 852 (1.52%) |
| Classic CopperSFS buffered TRST | 143,136 | 142,236 | 900 (0.63%) |
| SFS2 CSUN | 185,236 | 180,720 | 4,516 (2.44%) |
| SFS2 TRST | 208,396 | 174,808 | 33,588 (16.12%) |

The TRST active-code candidate with shared dispatch was 203,392 bytes, a
5,004-byte reduction. Its separately qualified family specialization removes
another 28,584 bytes. The other rows retain the already-qualified family choices.

Reference FFS 40.1 is 24,588 bytes and SmartFilesystem 1.279 is 97,984 bytes,
with provenance recorded in the first report. Remaining differences are 30,536
bytes for FFS, 44,252 for classic SFS, 82,736 for CSUN and 76,824 for TRST.

## Active-code changes

- FFS traversal uses a private 16-byte geometry record with filesystem
  dimensions and a cached root key. The public 40-byte device descriptor,
  device-offset arithmetic and DMA restrictions remain at the I/O boundary.
  Geometry arguments are readonly references; public filesystem entry points
  and caller-owned `Work`/`Request` records retain their layouts.
- The platform has an optional `TrackBlocks` scalar, defaulting to
  `Geometry.TrackBlocks`. The native platform reads its existing state field
  directly. The allocation-window policy and placement hint are unchanged.
- SFS2 readers, writers, recovery and trim use the existing stable scalar
  geometry getters instead of reconstructing the full descriptor to extract
  one field. In particular, checksum and traversal loops no longer rebuild
  that descriptor at their loop conditions. All validation and checksum
  iterations remain in place.
- Internal SFS2 volume descriptors pass by readonly reference instead of
  being copied at nested traversal calls. Their format and contents are
  unchanged; these are private calling-convention changes, not DOS packets.
- Native volume-name, root-key and buffer-accounting paths use scalar state
  fields where they do not need device geometry.
- SFS packet recovery and reservation release reuse shared helpers. The
  dispatcher no longer needs five separate 156-byte temporary work records.
  Its classic SFS frame drops from 1,300 to 520 bytes. The FFS dispatcher frame
  drops from 344 to 264 bytes. Recovery/flush ordering and error results remain
  unchanged.

The existing compiler pass list is unchanged. Compatibility reports record
zero managed allocation sites. There are no new application dependencies,
persistent globals, on-disk fields, packet operations or journal-policy changes.

Extracting the FFS write case and SFS resize cases into more helpers was also
tried. Although their dispatcher frames became smaller, their executable sizes
grew; those extractions were discarded.

## Performance checks

All percentages below are mean emulated bus-cycle changes; negative is faster.
The environment is licensed Kickstart 3.1, an emulated 68020, MC68000 handlers,
512-byte blocks and synchronous CopperHDF storage. These are guest-cycle
measurements, not host timings or physical-device throughput.

The mixed workload alternates arm order across three trials and separately
times mutations and verified reads/enumeration. Each pair has identical
read/write/flush counts; independent image walkers inspect persisted metadata.

| Handler | Mutations | Verification |
| --- | ---: | ---: |
| OFS | -1.60% | -0.45% |
| FFS | -2.17% | -0.47% |
| Classic SFS | -0.32% | -0.35% |
| SFS2 CSUN | -83.02% | -57.78% |
| SFS2 TRST, active-code shared control | -76.94% | -58.73% |

The separate sequential-read probe uses three trials per packet size and
checks data and unchanged media. Device read request and byte counts match.

| Handler | 64 B requests | 512 B | 4 KiB | 64 KiB |
| --- | ---: | ---: | ---: | ---: |
| FFS | -4.10% | -1.95% | -0.13% | -0.16% |
| SFS2 CSUN | -85.32% | -83.56% | -69.10% | -19.88% |
| SFS2 TRST, active-code shared control | -86.37% | -84.71% | -70.92% | -21.18% |

The large SFS2 improvements accompany removal of full descriptor construction
from repeated scalar queries; validation and physical I/O are retained.

An additional probe warms a small file, then times 512 alternating seeks,
512 seek/one-byte-read pairs and 512 zero-length reads. It rotates three trials
per arm, checks returned data and compares I/O counts. Relative to the previous
accepted builds, the active-code candidates improve these phases by:

| Handler | Seeks | One-byte reads | Zero-length reads |
| --- | ---: | ---: | ---: |
| OFS | -1.66% | -3.54% | -6.28% |
| FFS | -1.48% | -4.00% | -6.35% |
| Classic SFS | -12.21% | -5.66% | -0.29% |
| SFS2 CSUN | -16.19% | -84.59% | -85.76% |
| SFS2 TRST, active-code shared control | -15.39% | -85.63% | -86.78% |

### SFS2 TRST specialization requalified

The first pass rejected TRST specialization because it consistently slowed
verification and sequential reads. After the active-code changes, a separate
paired comparison against the new 203,392-byte shared-dispatch control gave:

- Mutations: +0.024%; verification: -0.051%, within the 0.1% qualification margin.
- Sequential reads: -0.050%, -0.064%, -0.019% and -0.006% across the four sizes.
- Small packets: seeks -6.15%, one-byte reads -0.47%, zero-length reads -0.07%.

Data checks and paired I/O counts passed. The additional size reduction is
therefore retained. The normal build reproduces the qualified 174,808-byte
binary exactly. The earlier rejection remains valid for its older inputs;
it was not overridden without remeasurement.

## Coverage and reproducibility

- **Host:** Release solution build and tests passed: 1,323 tests passed and
  three optional external-media tests were skipped. The final solution build
  completed without warnings. The opt-in small-packet tests also compile and run.
- **Native:** The accepted binaries passed 193 CopperStart instruction-replay
  cases. Two optional SFS2 cache-summary cases were skipped because those
  profiles do not contain that summary. Replays execute both 68000 and 68020
  CPU models internally. All four accepted handler profiles were rebuilt.
- **Full ROM:** Nine original-handler mutation/clean-handoff cases passed,
  including OFS/FFS DD and HD geometries, classic SFS and both SFS2 policies.
  Each case performs six boots. All five mixed-workload and all five
  small-packet comparisons passed. The six sequential-read arms and the
  cross-policy published-journal recovery cases passed. TRST specialization
  separately passed its native, handoff, journal and performance checks.

The base source revision and locally modified compiler/emulator dependencies
are the same as in the first report. No compiler source was changed in this
pass. Baseline binaries are the accepted first-pass artifacts, not a fresh
unoptimized build or the older shared-family baseline. Intermediate candidates,
source snapshots, native maps, compatibility reports, manifests, hashes, raw
samples and TRX receipts are in ignored `artifacts/active-size-20261111/`.

The mixed and small-packet probes are maintained in this repository. Sequential
and journal probes reuse the previously frozen CILica-investigation harness;
they execute the newly built HUNKs. See the
[benchmark instructions](../tests/CopperFS.Kickstart.Tests/README.md).

Concurrent compatibility work lowered the standalone startup's minimum
`dos.library` version from 37 to 36. That edit is preserved and is separate from
these optimizations. The final rebuild comparison records the resulting single
immediate-byte change in FFS, classic SFS and CSUN; their measured filesystem
code and executable sizes are unchanged. The qualified TRST binary already
contains the version-36 startup. No AmigaOS 2.0 qualification is claimed here.

Qualification covers these workloads, ROM and emulator settings. Physical
hardware, other ROM versions and unavailable external-media tests are not
passes. Strict/legacy classic write profiles were not performance-qualified
here and retain their shared family dispatcher. No packages, ROMs, reference
media or generated binaries are published by these source changes.
