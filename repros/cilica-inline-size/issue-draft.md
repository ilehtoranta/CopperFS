# Repeated inlining passes substantially increase generated code for generic callers

`Cilica.Core` 0.20.5-preview's default pipeline can repeatedly inline the same
generic checksum helper into one caller. In a reduced example based on a
filesystem workload, this increases the caller from 101 to 790 bytes of IL.
With .NET 10 x64 FullOpts, the resulting caller is 738 bytes of machine code;
with CILica inlining disabled, the caller plus the two JIT-compiled helpers total
439 bytes. The four small correctness cases in the attached reproduction agree.

## Reproduce

1. Use the accompanying `cilica-inline-size` directory with .NET SDK 10,
   PowerShell 7, and an x64 .NET runtime supporting JIT disassembly.
2. Run `./run.ps1`. It uses `CompilationPipeline.CreateDefault`, optimizing the
   same `Workload.dll` in all arms with `VerifyEachPass=true`,
   `AssumeLittleEndian=false`, and `AllowCrossAssemblyIPO=false`.
3. Compare `off`, `on`, and `on-cap200`; the generated `jit.txt` and
   `optimizer-trace.txt` files are in the output directory printed by the script.

On Windows x64, SDK 10.0.401, runtime 10.0.12:

```text
off:       IL Write 101; x64 caller + helpers 240 + 50 + 149 = 439 bytes
on:        IL Write 790; x64 caller                            = 738 bytes
on-cap200: IL Write 165; x64 caller + helper  245 + 149      = 394 bytes
```

Tiered compilation and ReadyToRun are disabled. This is a method-level code-size
comparison, not a whole-process size measurement. A separate `./benchmark.ps1`
microbenchmark of the reduced workload measured median 103.28 ns/op with
inlining off and 89.48 ns/op on (nine rotated rounds, three million operations
per arm per round, Ryzen 5 5600X, .NET 10.0.12, tiering and ReadyToRun off).
The median paired time reduction was 13.06%; it is a speed/size tradeoff for
this particular synthetic workload. With a 200-byte IL cap the median was
100.04 ns/op, with a 3.22% median paired reduction versus off. The original
filesystem has also shown a 13–14% larger 68000 handler when general CILica
inlining was enabled; it does not need to be built to run this reproduction.

## Observed behavior and question

The trace shows four inliner visits to `Write`. The first inlines one `Put` and
rejects the others after spending its cost budget; subsequent visits admit the
remaining five `Put` calls. The default 32 KiB `MaxInlinedMethodILSize` allows
the caller to grow to 790 bytes. A 200-byte limit controls this example, although
we have not tested its effect on application-wide performance.

Would it be useful to offer a size-oriented policy or account for accumulated
growth across pipeline visits and retained callees? This may be an expected
speed/size tradeoff rather than an incorrect optimization. The synthetic timing
above should not be generalized to other methods or architectures.
