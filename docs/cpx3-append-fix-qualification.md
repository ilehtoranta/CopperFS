# CPX3 unwritten-extent append qualification

13 November 2026. This follow-up qualifies the append fix after the independent
review of [CPX3](cpx3-prototype-qualification.md). It changes the isolated host
prototype and its tests/documentation. It does not change CopperFFS/CopperSFS,
their native handlers, or the CPX3 on-disk format.

## Defect and correction

Preallocation followed by a partial-allocation-unit shrink can retain an
unwritten extent at EOF. Append previously discarded that extent's unwritten
flag, wrote bytes to its storage, and reported success without converting the
mapping. Reads continued to return zeros, including after remount.

Append now retains its EOF/overflow validation and delegates to the bounded
Write path. That path zero-initializes unwritten units, splits descriptors where
needed, admits all metadata targets before payload writes, and persists data
before publishing the written mapping. Both entry points now share conversion,
credit enforcement, partial-progress reporting and commit ordering. The fixed
scratch budget and operation credits are unchanged.

## Regression and recovery evidence

All ten new cases in
[AppendTests.cs](../prototypes/CopperFS.Prototype.Tests/AppendTests.cs) failed on
the old implementation and passed after the fix:

- Six regression cases cover A=512/1024/2048/4096, append at EOF or after a gap,
  one inline descriptor, six descriptors requiring tree promotion, and an
  existing extent tree. The fixtures fill storage with nonzero bytes before
  formatting. Assertions check acknowledged byte counts, immediate reads,
  independent decoding, remounts, preservation of earlier data and zeros, and
  later extension of the allocation-unit tail.
- Four fault matrices cover 512-byte allocation units with one/six/nine extents
  and a 4096-byte allocation unit with 512-byte device sectors. They cut at every
  sector write and both sides of every flush, using five deterministic pending
  persistence schedules: none, all in reverse order, alternating sectors, only
  the last sector, and torn 128-byte prefixes.

Those four matrices execute **1,145 injected recoveries**: 175, 205, 225 and 540.
Each recovered file must contain exactly the old or new bytes; acknowledged
completion must contain the new bytes. Each case checks ownership, idempotent
remount, and another append after recovery to exercise journal/mapping reuse.
These schedules are finite evidence, not every possible persistence subset or
physical-device qualification.

## TD64 policy

The [design](new-filesystem-design.md#wide-geometry-on-the-68000) now makes TD64
first-class and preferred. TD64-only devices must work independently of NSD
query support. NSD is deprecated in this design and limited to legacy
compatibility. Native qualification must cover TD64-only devices, TD64 preference,
4 GiB/partition-base carries, split/retried requests and completion failures;
NSD results do not substitute for TD64 coverage. This documentation change does
not claim that the host IBlockDevice abstraction implements either device ABI.

## Validation checkpoint

The Release solution build and all ten targeted append cases passed before
handing the core back to the coordinated 68000 follow-up chat. Its later
arithmetic, scheduling and stack changes require their own qualification.
The source manifest, append source snapshot and built assemblies for this
checkpoint are retained under ignored `artifacts/cpx3-append-fix`. Full-solution
validation uses those assemblies with `--no-build`, so concurrent source edits
cannot silently change the code under test.

~~~powershell
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-append-fix
dotnet test prototypes/CopperFS.Prototype.Tests/CopperFS.Prototype.Tests.csproj -c Release --artifacts-path artifacts/cpx3-append-fix --filter FullyQualifiedName~AppendTests --logger trx --results-directory artifacts/cpx3-append-fix/targeted-results
dotnet test CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-append-fix --no-build --logger trx --results-directory artifacts/cpx3-append-fix/solution-results
dotnet run --project prototypes/CopperFS.Prototype.Demo -c Release --artifacts-path artifacts/cpx3-append-fix --no-build
~~~

| Coverage | Result |
| --- | --- |
| Release solution build | Passed; existing CopperSharp CS8602 warning at M68kSpillAndCopy.cs:76 |
| Targeted append tests | 10 passed; 1,145 injected recoveries |
| Complete prototype host suite | 63 passed; 10,597 injected recoveries including the new append cases |
| Full solution host tests | 1,603 passed, zero failed, three external-media cases skipped |
| Disposable host demo | Passed at A=512/1024/2048/4096 |
| New-format native handler/CopperStart replay | Not run; this change has no native handler implementation |
| New-format full-ROM/physical media | Not run |

Skipped external-media cases are not passes. The original CPX3 qualification
remains historical evidence for its earlier checkpoint; repeated targeted and
full-suite tests must not be counted as independent additional test cases.
The solution total includes prototype 63, SFS 1,194, FFS 296, Amiga host 18 and
StorageBench 32 passes. The SFS suite completed in 9 min 45 s. The 10,597 recovery
count is read from this run's test output; some existing scenarios now include
reserve-refill boundaries at different points after the shared write-path change.
