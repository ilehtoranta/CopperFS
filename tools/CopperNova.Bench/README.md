# CopperNova prototype comparison

This separate runner compares the unpublished direct-metadata prototype against
original Kickstart FFS 40.1 and SmartFilesystem 1.279. Existing CPX4 tooling and
guest sources are unchanged. Build the solution and prototype handler, then use
`build/Benchmark-NovaPrototype.ps1` as described in the prototype README.

Both established guest HUNKs are supplied from the earlier qualified evidence
and hash-checked. Mixed and streaming operations, DOS calls, marker locations,
machine configuration, fixture sizes and interleaved two-trial order are retained.
The handler is loaded explicitly; the measured guests are not recompiled.

Results are median emulated bus cycles from the normal `ExecuteFrame` path.
The separate scalar correctness gate does not supply timing results. The
CopperHDF backend does not charge physical seek, rotation or transfer latency;
these are emulator/backend results, not physical Amiga storage rates.

The prototype uses a fixed 96 KiB core/transport arena, startup-owned I/O
resources and the device's DMA constraints. The 32 DOS-buffer setting does not
equalize handler cache memory. The reference handlers retain their own policies.
Independent post-run checks cover both prototype files, full ownership/counters,
and the expected final main-file payloads for the original handlers.

`CPX_BENCH_*` environment names remain compatible with the existing runner's
input convention. `NOVA_BENCH_PROBE` names the frozen guest HUNK. The wrapper
requires the retained artifact paths and original input hashes. All generated
HUNKs, ROM inputs, images and raw logs stay in ignored artifact directories.

`-BaselineHandler <HUNK>` adds the frozen current prototype to the comparison.
The four-way order is baseline/new/FFS/SFS/SFS/FFS/new/baseline, with independent
CNM1 checks for both prototypes and baseline handler hashes recorded in the
report. Leaving that argument empty retains the original three-way comparison.
The separate `cache` mode and `compile-cache` utility support the metadata cache
pressure experiment; they do not replace the frozen mixed/stream guest HUNKs.

For the CNP2 root-publication comparison, pass `-Packed` and the frozen item-1 HUNK via `-BaselineHandler`. The candidate receives a new packed CNP2 image; the control receives CNM1. The same hash-checked guest probes, reference handlers, geometry, trial order and independent payload checks apply. Use `-BenchAssembly` only when using a separately built copy of the benchmark tool. See [format and measurements](../../docs/coppernova-packed-root-2026-11-17.md).
