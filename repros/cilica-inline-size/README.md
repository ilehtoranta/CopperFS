# CILica inline code-size reproduction

This self-contained reproduction reduces the repeated generic `Header` and
checksum-writing `Put` calls in CopperFS's `WriteOfs`. It requires .NET SDK 10,
NuGet access, PowerShell 7, and an x64 .NET runtime with JIT disassembly enabled.
It does not require CopperFS, CopperSharp, an Amiga, or a ROM.

From this directory, run:

```powershell
./run.ps1
```

The script builds the same workload once, applies `Cilica.Core` 0.20.5-preview's
**default** pipeline with inlining off, on, and on with a 200-byte per-method IL
limit, and runs each output. It checks four results and emits JIT disassembly
for `InlineSize.Filesystem` with tiering and ReadyToRun disabled. Each run writes
its traces and binaries to a fresh ignored directory under `artifacts`.
Use `-OutputRoot <new-directory>` to choose another location. The script leaves
existing output directories untouched. `Optimizer/Program.cs` is the complete
CILica API invocation; set `CILICA_TRACE=1` to inspect decisions when invoking it
directly.

Run `./benchmark.ps1` for a small successful-write microbenchmark of the same
three outputs. It checks the results, warms up each process, rotates arm order,
and reports nine samples of three million operations per arm by default. Use
`-Rounds` and `-Iterations` to change the run length. The samples are saved as
`benchmark-samples.json` in a fresh output directory. This is a synthetic,
single-threaded benchmark; wall-clock results depend on machine load and CPU
frequency.

On Windows x64, SDK 10.0.401, .NET runtime 10.0.12:

| CILica setting | `Write` IL bytes | x64 caller | Other emitted `Filesystem` helpers | Total |
| --- | ---: | ---: | ---: | ---: |
| Inlining off | 101 | 240 | 50 + 149 | 439 |
| Inlining on, default limit | 790 | 738 | 0 | 738 |
| Inlining on, limit 200 | 165 | 245 | 149 | 394 |

The total covers only these JIT-compiled `Filesystem` methods, not the entire
process or the separate `Device` implementation. The off and on runs use the
same input DLL, settings and pipeline apart from `EnableInlining`; all four
functional cases matched. The capped run illustrates an existing per-method
control, not a recommended global limit or proof of equal performance. JIT
results depend on runtime, architecture and configuration. `NoInlining` on
the caller keeps it observable as a separate JIT method; CILica still optimizes
its body. `NoInlining` on the `Device` methods keeps their bodies outside the
comparison.

On an AMD Ryzen 5 5600X, the synthetic write microbenchmark measured median
103.28 ns/op with inlining off, 89.48 ns/op on, and 100.04 ns/op with the
200-byte cap. Median within-round time changes relative to off were -13.06%
and -3.22%, respectively. This measures the reduced case only; it does not
predict 68000 handler throughput.

The default-on trace shows four `InlineMethods` visits to `Write`: together they
inline three `Header` calls and all six `Put` calls. The first visit rejects
five remaining `Put` calls after its cost budget is spent; later visits admit
the same kind of expansion. `MaxInlinedMethodILSize` defaults to 32 KiB in this
version, so it permits the 790-byte result. The trace establishes repeated
inlining; it does not establish that code growth is unintended. See
[the issue draft](issue-draft.md) for a proposed report.
