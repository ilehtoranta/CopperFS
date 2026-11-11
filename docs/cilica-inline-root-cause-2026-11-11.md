# CILica inlining code-size investigation

The strongest isolated contributor in the tested CopperFFS handler is repeated
inlining of `AmigaFfsWriteCore.Header<T>` and `Put<T>` into larger generic
filesystem methods. `Put` performs a checksum loop and calls the device
interface; `Header` validates a metadata block. The methods are called several
times along failure-checked paths. Inlining exposes further calls, so their
combined effect is larger than the sum of blocking each helper independently.

The tested filesystem revision was
`c26e6e1c73b93c2bce3152b326f4dbedf559a946`. The relevant source is
`src/CopperFFS.Core/AmigaFfsWriteCore.cs` (`Header` at line 24, `Put` at line
29) and `src/CopperFFS.Core/AmigaOfsWrite.cs` (`WriteOfs` at line 103). This is
the newer detached revision from the previous comparison, not the older main
checkout. Its compiler and test dependency provenance is recorded in
[the original comparison](cilica-inlining-comparison-2026-11-11.md).

## Real-handler attribution

With CILica.Core 0.20.5-preview and CopperSharp's restricted pipeline:

| Measurement | Inlining off | Inlining on | On, prevent `Header` and `Put` inlining |
| --- | ---: | ---: | ---: |
| `WriteOfs` managed IL | 1,855 B | 4,146 B | 2,205 B |
| `WriteOfs` 68000 code | 1,976 B | 4,082 B | 2,244 B |
| Complete CopperFFS HUNK | 187,236 B | 213,372 B | 203,844 B |

The control temporarily marked only those two methods `NoInlining` in a copy of
the original managed input, ran the same CILica pipeline with inlining on, then
cleared the temporary flags in the rewritten output before CopperSharp compiled
it. The original managed input, source files and compiler binaries were not
changed. The compiler binary hashes matched the previous comparison. The
control removed 9,528 bytes, or 36.5% of the 26,136-byte HUNK increase; it does
not explain the remaining 16,608 bytes. On the 68000, `WriteOfs` alone lost
1,838 of the 2,106 bytes it gained. Its IL loses 1,941 of the 2,291 bytes it
gained. Preventing `Put` alone reduced the on caller IL by 318 bytes, and
preventing `Header` alone reduced it by 438; preventing both reduced it by
1,941, demonstrating interaction between decisions rather than independent
additive costs.

The ordinary on `WriteOfs` IL has no calls to `Header` and retains two of its
six original direct `Put` calls; inlining also exposes additional calls from
other helpers. `ReadUInt32` calls increase from 5 to 28, `WriteUInt32` from 14
to 30, and calls to device `WriteBlock` appear seven times. Counts describe the
rewritten IL, not runtime execution frequency. Other methods also grew, so this
is a substantial contributor rather than a claim of a single complete cause.

The control binary passed 114 selected FFS native instruction replay cases,
16 CopperStart block-copy replay cases, and three Kickstart 3.1 original-handler
mutation and clean-handoff geometries. This is targeted validation, not release
qualification. A later [performance follow-up](cilica-inline-performance-2026-11-11.md)
compares the control and the fully inlined binary on OFS writes and FFS reads.
The current, older main checkout's Release solution build passed and its tests
passed 1,323 cases with three external-image tests skipped.
The binary and IL experiments, reports and hashes remain under ignored
`artifacts/cilica-inline-20261111/il-audit/`.

## Independent x64 reproduction

The standalone [reproduction](../repros/cilica-inline-size/README.md) contains a
small generic device interface, checksum helper and repeated call sites. With
the CILica **default** pipeline—not just CopperSharp's restricted pipeline—it
grows the caller from 101 to 790 IL bytes and the JIT-compiled caller plus
helpers from 439 to 738 x64 bytes. Four correctness cases match. The trace shows
four inliner visits that successively inline all six checksum calls; an existing
200-byte `MaxInlinedMethodILSize` setting curbs growth in this reproduction.
The reproduction was also copied outside the repository and rerun successfully.

These results support an upstream discussion about size heuristics and budget
accumulation, not a claim of semantic incorrectness. The earlier full-ROM read
measurements showed speed improvements in several workloads. The performance
follow-up reports mixed results for the two-helper control and a synthetic x64
timing for the IL cap; neither result establishes a general setting. A
proposed report is in the reproduction's `issue-draft.md`; it has not been sent.
