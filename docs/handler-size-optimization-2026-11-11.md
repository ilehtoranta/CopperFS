# Standalone handler size reduction, 2026-11-11

This records the first pass. The subsequent
[active-code pass](active-code-size-2026-11-11.md) reduces the handlers further
and requalifies TRST specialization with the changed code and new measurements.

FFS, classic SFS and SFS2 CSUN are smaller without a measured performance
regression in the qualified workloads. Original-handler size parity has **not**
been reached. SFS2 TRST specialization exposed a repeatable slowdown and is not
enabled by default.

## Accepted sizes

All sizes are complete MC68000 freestanding HUNK files, in bytes, using the
existing compiler pass list and no general CILica preprocessing.

| Handler | Shared dispatcher baseline | Accepted build | Reduction |
| --- | ---: | ---: | ---: |
| CopperFFS, including OFS | 173,536 | 55,976 | 117,560 (67.7%) |
| CopperSFS classic buffered TRST | 173,536 | 143,136 | 30,400 (17.5%) |
| SFS2 CSUN | 215,448 | 185,236 | 30,212 (14.0%) |
| SFS2 TRST | 208,396 | 208,396 | unchanged |

The local reference FFS 40.1 is 24,588 bytes (SHA-256
`9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773`).
SmartFilesystem 1.279 is 97,984 bytes (SHA-256
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`).
The accepted FFS and classic SFS builds are still 2.28 and 1.46 times those
sizes: another 31,388 and 45,152 bytes would have to be removed to match them.
SFS2 CSUN remains 1.89 times the original SFS size, with 87,252 bytes to remove.
These are specific reference versions, not a universal original-handler size.

## Implementation

Previously both standalone handlers retained both filesystem implementations.
Selecting a different entry point did not specialize their shared dispatcher.
`Build-Handler.ps1` now sets `CopperFsHandler=Ffs` or `Sfs` for the qualified
standalone profiles. C# constants remove the other family's call sites before
native reachability analysis. Merely making `Smart()` return a constant removed
native branches too late: the other writer still occupied the executable.

FFS also excludes SFS timer, overlay and journal paths that its mounts never
construct, and uses the existing direct FFS trim entry. Classic SFS excludes the
FFS mount attempt and FFS trim branch. Wrong-family media remain rejected; the
specialized mount now rejects them before invoking the other family's recovery.
The final map check fails a specialized build that retains the other writer.

The portable cores are unchanged. State layout, packet layouts, filesystem
identities, accepted on-disk formats, allocation/recovery algorithms and write
policies are retained. The ordinary managed project and CopperStart's source
consumer keep mixed dispatch. No CopperStart build dependency was introduced.
`-SharedDispatch` retains the comparison control for standalone builds.
Strict and legacy classic write profiles also retain shared dispatch until
their performance has been measured separately.

## Performance

The paired workload uses real Kickstart 3.1 DOS/Exec on an emulated 68020,
MC68000 handlers, 512-byte blocks and synchronous CopperHDF storage. Three
trials per arm alternate execution order. Timing begins after mount and
separately covers growth, overwrites and metadata mutations, then verified
reads and directory enumeration. Each pair issues identical read/write/flush
command counts; independent image walkers validate persisted metadata.

Percentage changes below compare mean bus cycles; negative is faster.

| Accepted handler | Mutation workload | Verification workload |
| --- | ---: | ---: |
| OFS | -2.09% | -1.34% |
| FFS | -1.86% | -1.41% |
| Classic SFS | -0.19% | -0.20% |
| SFS2 CSUN | -5.95% | -5.31% |

The separate sequential-read probe uses three trials at each packet size and
validates data. Its open/read/close session cycle changes were:

| Accepted handler | 64 B requests | 512 B | 4 KiB | 64 KiB |
| --- | ---: | ---: | ---: | ---: |
| FFS | -14.62% | -9.69% | -3.84% | -3.09% |
| SFS2 CSUN | -7.71% | -7.54% | -6.28% | -1.79% |

The workloads read 64 KiB, 256 KiB, 1 MiB and 1 MiB respectively.

These results support retaining performance for these workloads. They do not
establish a guarantee for every packet sequence, CPU or physical device.
The checked-in paired benchmark now fails a mean cycle increase above 0.1%
and retains all samples and handler hashes. See the
[benchmark instructions](../tests/CopperFS.Kickstart.Tests/README.md).

### Rejected SFS2 TRST specialization

Specializing SFS2 TRST produced a 178,592-byte binary, a reduction of 14.3%.
Mutations improved by 1.03%, but verification regressed by **0.778%**. The three
baseline verification samples were 79,343,700 / 79,344,690 / 79,342,850 cycles;
the candidate samples
were 79,961,120 / 79,961,190 / 79,961,360. Each issued twelve reads and no
writes or flushes. The difference is repeatable, not explained by trial noise.
The sequential-read probe also regressed by 1.18%, 1.16%, 0.99% and 0.35% for
64-byte, 512-byte, 4 KiB and 64 KiB requests respectively.

The initial 1% benchmark tolerance let this case pass. That was too permissive
for the stated performance requirement; the tolerance was tightened and TRST
specialization was excluded from the default build. The accepted SFS2 TRST output
is byte-identical to its shared-dispatch baseline. CSUN passed the completed
performance comparisons and retains its size reduction. The rejected TRST binary
and results remain under ignored artifacts for further investigation.

## Validation and provenance

- **Host:** `dotnet build CopperFS.slnx -c Release` and
  `dotnet test CopperFS.slnx -c Release` passed. Tests: 1,323 passed, three
  optional external-media tests skipped. The portable cores were not edited.
- **Native:** The accepted FFS/classic SFS binaries passed 141 CopperStart
  instruction-replay cases: 28 copy/cache cases per handler and 85 classic SFS
  TRST, cursor and read-window cases. The replays exercise both 68000 and 68020
  CPU models internally. Compatibility reports record zero managed allocation
  sites. SFS2 CSUN passed a further 26 cases; one optional cache-summary case
  was skipped because that summary is absent in SFS2. Final accepted rebuilds
  are byte-identical to their qualified binaries.
- **Full ROM:** The accepted FFS/OFS, classic SFS and SFS2 CSUN binaries passed eight
  original-handler mutation and clean-handoff cases, including DD/HD geometries,
  with six boots per case. The paired mixed-workload and FFS/CSUN sequential-read
  probes passed their data checks. Wrong-family mount probes verify rejection
  without writes. These geometry cases use synchronous hardfile storage, not
  floppy DMA or physical media.
- **SFS2 journal and rejected TRST experiment:** Both specialized policies passed
  cross-policy published-journal recovery. The rejected TRST binary also passed
  original-handler handoffs and data/I/O checks. Its additional 26 replay cases
  passed with one absent-cache-summary skip. These results do not override its
  performance rejection or qualify it as the default build.

The starting filesystem revision was
`cb6dec0d8ded72cefd82eb80d787ca6bde6868bc`, the working checkout, not the newer
detached source used in the earlier CILica investigation. CopperSharp was
`452d70c364ad4b1452e66cc30b7152615de8facd`, CopperScreen was
`7304daac403c1fecf7aead0eac0193392a58e014`, and CopperStart replay sources were
`ed02a70a2c500c79884c7030d2267b4b1e62f197`, all with local modifications.
Commit IDs alone do not reproduce this toolchain. Status records, compiler and
test-assembly hashes, inputs, maps, manifests, raw samples and TRX receipts are
in ignored `artifacts/handler-size-20261111/`. The sequential-read and SFS2
journal probes reuse the frozen harness from the earlier CILica investigation;
the mixed-workload and rejection probes are maintained in this checkout.

No ROM, media, binary artifact, package or release is published by this change.
Unconfigured external-media tests and physical-hardware coverage are not passes.

## Remaining size work

After removing the unused implementation, the FFS map attributes 16,692 bytes
to its writer, 11,504 to native handler operations, 5,816 to the reader and
5,516 to the device platform, plus links, trim, locks, notifications and shared
helpers. The remaining gap is active implementation code. Further reductions
need measured source or compiler improvements; removing supported operations or
enabling extra shared-return jumps would not establish the requested result.
