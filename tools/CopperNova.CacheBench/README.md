# Metadata cache experiment

This host-only runner compares the frozen CNM1-P4 cache policy with the isolated
prototype's CLOCK replacement. It measures metadata sector reads and hence
metadata CRC-validation bytes, not host execution time. Native execution is
measured separately by CopperNova.Bench's `cache` mode in a booted Kickstart ROM.

The engine retains the 64-page (32 KiB) metadata cache and 98,304-byte arena.
Existing packet access epochs protect pages used by an active operation. Dirty
pages remain ineligible for eviction. At eviction, CLOCK compares a page's last
access epoch with its last observed epoch; reuse earns a second chance. Newly
admitted pages begin probationary. The hand examines at most 128 slots. Admission
and dirty-page commit thresholds, checksums, on-disk layout and durability are
unchanged. Clean pages remain cached after flush. The hand can retain any bounded
position across remount; cache contents and access observations are invalidated.
Cache hits perform the same work as P4. Four bytes of existing state padding and
four bytes of existing per-slot padding hold the new state.

The fixture contains 115 metadata objects: a root, six directories, 36
subdirectories and 72 one-byte files. Every payload is independently checked.
Three scans of the 72 files are interspersed with six recurring hot paths after
each scan entry, yielding 1,512 opens/read-one-byte/closes. All reads verify their
payload. No writes or flushes occur in the measured host phase. Both variants
load the same seeded image, excluding setup/commit-layout differences.

Build/run (add `-p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0` when needed):

```
dotnet run --project tools/CopperNova.CacheBench -c Release -- artifacts/cache-policy/seed.hdf
dotnet run --project tools/CopperNova.CacheBench -c Release -- measure artifacts/cache-policy/seed.hdf
```

For the host baseline, supply `-p:CacheEngineProject=<absolute P4 project path>`
and use a separate `--artifacts-path`. The recorded experiment preserves P4's
four engine source files under ignored `artifacts/cache-policy/baseline-source`;
their hashes match the frozen P4 native source manifest.

Native pressure reproduction:

1. Run `node tools/studies/prepare_cache_pressure.mjs` to derive the guest's device
   setup and reporting from our existing mixed probe; then build CopperNova.Bench.
2. Run its DLL with `compile-cache artifacts/cache-policy/CACHEPROBE` once. Use
   that exact guest HUNK and seed for every handler variant.
3. Run `build/Benchmark-NovaCachePressure.ps1` with the baseline/changed handlers,
   seed, frozen probe, external evidence root, licensed ROM and output root.

The script runs baseline/changed/changed/baseline. `-ClockOnly` runs two changed
trials for reuse with an already completed baseline pair. Result files include
handler, probe and media hashes. The native runner also verifies the complete
persisted image independently and requires its hash to remain unchanged.

The original mixed and stream benchmarks still require their exact frozen guest
HUNKs through Benchmark-NovaPrototype.ps1. This pressure experiment supplements
those workloads; it does not establish feature parity or a cross-filesystem win.
