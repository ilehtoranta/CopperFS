# CPX allocation-test investigation

The intermittent failure in `InhibitValidationRetirementAndRemountStayInSeparateBoundedPhases`
was reproduced with the original one-pass warm-up on .NET 10.0.12, Windows x64.
A diagnostic build recorded `Stopwatch` timestamps immediately around the two
`GC.GetAllocatedBytesForCurrentThread` reads and the dispatcher step. An EventPipe
trace enabled the CLR GC and `GCAllObjectAllocation` providers. In a failing
sample the counter increased by 496 bytes, while a GC suspension and restart
occurred inside the measured interval. The trace reported zero lost events and
no managed allocation on the measured thread within that interval. Two allocations
on the finalizer thread were outside the measured thread's accounting. This
establishes that this particular reported delta did not correspond to an observed
managed dispatcher allocation. The exact runtime accounting mechanism has not
been established; do not infer that every possible reported delta has the same
cause. Native heap behavior is a separate qualification.

The [.NET 10.0.12 counter implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/comutilnative.cpp)
computes the result from the current thread's small- and large-object allocation
accounting, less the unused part of its current allocation context. During a GC,
the [collector's allocation-context fixup](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/gc/gc.cpp)
updates that accounting and clears the context. That explains why GC bookkeeping
is relevant, but the observed trace does not identify which runtime field or
operation produced the 496-byte discrepancy. A simple spin-wait control with
forced collections did not reproduce it on this machine. The earlier suggestion
that tiered PGO caused the discrepancy was an unsupported inference: correlation
alone did not establish that cause.

The trace also identified an avoidable real test-fixture allocation: constructing
`new Disk { Bytes = d.Image }` for every I/O first allocates the `Disk` field's
4 MiB default array and then discards that array when `Bytes` is replaced. The
lifecycle test now constructs one disk wrapper and reuses it. This removes the
repeated large-object churn without changing device bytes or the lifecycle.

The former no-GC-region workaround and managed allocation-counter assertions
have since been removed from the CPX scheduling tests. Host tests continue to
check bounded steps, results and caller-owned arena guards. Native compiler
analysis checks the complete handler entry for reachable managed allocation
sites with memory management disabled, and a deliberately allocating negative
control checks that the compiler rejects such code. The actual standalone
handler build also rejects a missing, incompatible or nonempty allocation report.
Compiled native event-loop tests separately reject unexpected Exec allocation
calls during the packet-processing scenarios they exercise. They allow the
startup-owned arena, ports and timer requests; they do not prove every possible
device or packet sequence. See `NativeAllocationInstructions` and the native
event-loop and storage-loop instruction tests.

In the 2026-11-14 68000 Kickstart 3.1 replay, the source-matched standalone
HUNK completed the ExAll, record-wait and notification workload. The Exec-vector
observer saw 45 startup allocation calls and zero calls during the monitored
operation phases. The startup count is a positive control for the observer;
it is not an allocation count for other phases or for code inside external
devices and libraries. The local receipt is under
`artifacts/cpx-allocation-rom-final/growth/qualification.json`.

`GC.GetAllocatedBytesForCurrentThread` was a managed-host test instrument; reading
it did not itself request a collection. It measured allocations on the executing
managed thread, not allocations in the freestanding native handler. The portable
filesystem and native handler do not require the managed garbage collector.

The EventPipe diagnostic files under `artifacts/exall-trace-run` are local build
artifacts, not committed fixtures or portable qualification receipts. They may
contain runtime process details; use source-controlled tests to reproduce.
