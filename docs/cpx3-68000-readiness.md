# CPX3 execution readiness for 68000

Date: 2026-11-13. This change prepares the research core for a native handler;
it does not implement the handler or change CPX3 bytes, journal publication,
transaction credits, reserve ownership or existing CopperFFS/CopperSFS formats.
It preserves the coordinated [Append correction](cpx3-append-fix-qualification.md).

## Arithmetic decisions

[WideMath32.cs](../prototypes/CopperFS.Prototype/WideMath32.cs) takes explicit
high/low uint parameters. Add/subtract return carry/borrow success, comparison
examines the high word first, and shifts handle 0, 32 and 64 boundaries explicitly.
Round-up shifts the quotient and then adds one for a nonzero remainder, avoiding
an overflowing `bytes + unit - 1` intermediate. DeviceRange checks the entire
partition-relative range, partition-base addition and absolute end before
returning an address. Failure clears device-range outputs.

The host geometry adapter uses these kernels for AU/byte/sector translations and
rounding. Groups and bitmap windows use power-of-two addressing. Non-power-of-two
discard alignment uses exactly 64 binary remainder rounds; power-of-two alignment
uses a mask. The kernels have no wide locals, division, multiplication, dynamic
allocation or data-dependent iteration count. Tests check those IL properties.
The host still uses ulong for public values, encoded IDs and ordinary bounded
arithmetic. The native adapter must use pairs at its device boundary and qualify
request splitting and TD64 independently; NSD remains deprecated in the design.

The instruction test source-links the actual kernel file and compiles it with
CopperSharp for MC68000, freestanding, no managed memory, then executes the emitted
instructions with Copper68k. It checks return values, stack pointer and guard
bytes. This establishes execution of these kernels, not code generation for the
entire ref-struct host core or a performance estimate on physical hardware.

## Scheduling contract

| Operation | Bound and retained state |
| --- | --- |
| FindFreeStep | At most four metadata page reads, including group descriptors; scans at most four 4096-bit windows; scalar cursor |
| FindNameStep | At most four metadata page reads and 64 candidate comparisons; eight path frames, one pending entry, exact input name |
| CheckAncestryStep | At most four metadata page reads; path and parent ID survive Pending |
| ReclaimStep | At most one retired reclaim/prune transaction; persistent work head identifies progress |
| PruneDirectoryStep | At most one retired prune transaction for one directory |
| Blocking core I/O | Cooperate before a fifth read/write/flush/discard request; all journal barriers retain their ordering |

StartNameSearch initializes caller-owned state through a ref parameter to avoid a
large returned-struct copy. Its initial parent lookup can traverse the eight-level
object tree; the four-page return bound applies to FindNameStep, not initialization.
Other initializers perform bounded control-page reads. Store cursors in a fixed
caller arena, not one on every open handle or a native stack local. They retain no
borrowed page view. Volume identity/generation detects mutation, but is not a
mount-instance cookie: discard cursors after every remount or media change,
including a different image with the same UUID/generation. Reusing NameSearch
with a different byte name returns Invalidated. A free-space result is a hint;
allocation must revalidate it under exclusive ownership.

The existing blocking APIs drive these continuations internally. The device's
Cooperate callback may schedule unrelated work but must preserve exclusive mount
ownership, scratch and media. Ready rejects reentry during the callback. Callback
failure poisons the mount and record leases unwind. A no-op default retains the
host API's synchronous behavior. This is a cooperative callback, not an async
DOS packet dispatcher, and a device request may take arbitrarily long.

With deferCleanup:true, mount recovers the journal but does not drain the durable
work queue. Delete and shrink publish their normal bounded namespace/size
transaction and return with work pending. The caller drains ReclaimStep until it
returns false, and may prune the affected directory separately. Other mutations
are rejected while work remains; reads retain the normal shrink/unlink semantics.
Each successful reclaim step retires both checkpoints before another transaction
can reuse storage. A crash does not lose the queue or skip journal recovery.

ReclaimStep and PruneDirectoryStep can search many empty tree pages, yielding
internally at device boundaries. Their public return bound is transactions, not
four reads. The checker and extent-count diagnostics are also cooperative,
constant-workspace scans. There is no latency or CPU-cycle guarantee. Extent data
lookup now descends to a predecessor, and tail reclamation descends to the
rightmost record, avoiding a full extent-list scan for each operation.

## Memory and stack decisions

Scratch remains 6P + 12 KiB: 18,432 bytes at P=1024 and 36,864 at P=4096.
Offsets below are relative to the end of the six page buffers.

| Offset | Bytes | Use |
| ---: | ---: | --- |
| 0 | 2048 | 64-entry transaction index |
| 2048 | 128 | Eight scalar insertion-path frames |
| 2176 | 1920 | Reserved |
| 4096 | 1536 | Two UTF-8 component buffers |
| 5632 | 512 | Reserved |
| 6144 | 4096 | LIFO record arena |
| 10240 | 2048 | Reserved |

Object, directory-entry, extent and inline-copy records use checked LIFO leases
from the record arena. Internal name and iterative tree searches also borrow this
arena. Disposal restores the prior watermark on both success and exceptions;
overflow fails instead of allocating another buffer. No instance core method
uses localloc except the fixed four-byte UTF-8 encoder in NameHash. This does not
eliminate scalar/compiler temporaries or returned small structs from the stack.
The test's nested namespace workload peaks at 2,552/4,096 record bytes at both
A=512 and A=4096, with scratch guards intact. This is measured workload coverage,
not a proof of every possible call path; the runtime limit remains enforced.

Caller cursor reservations are 64 bytes for FreeSearch, 1,152 for NameSearch and
256 for AncestrySearch (measured host sizes: 40, 1,144 and 240 bytes). Host sizeof
assertions enforce these budgets; the native
ABI must be checked again. These are additional to the mount arena when external
storage is supplied. Reuse one operation's cursor storage after completion;
do not multiply this cost by every open file without an explicit handle limit.
Mount scalars, native task stack, OS requests/ports, optional DMA bounce buffers,
code and future charset tables are additional to RequiredScratchBytes.

The compiled arithmetic probe executes 14,222 instructions with a measured
168-byte peak stack and a 512-byte enforced budget. Guard bytes below that budget
remain intact. This includes the test entry point and its kernel calls; it is
**not** a bound for native filesystem operations, exception runtime, callbacks or
Amiga OS calls. The earlier provisional 4–8 KiB handler stack remains unqualified.

## Validation

All results below are from this uncommitted workspace, using isolated artifacts:

| Scope | Result |
| --- | --- |
| Final Release solution build | Pass; one existing CopperSharp CS8602 warning |
| Release solution test checkpoint | 1,622 passed, three external-media cases skipped, zero failed; includes 74 prototype cases and the arithmetic instruction probe |
| Final prototype suite after removing the by-value name initializer and adding three tests | 77 passed, zero skipped/failed; 10,827 crash/replay recoveries, including 230 new deferred-cleanup recoveries |
| CPX3 emitted MC68000 arithmetic | One probe passed; 14,222 instructions, 168-byte measured peak stack |
| CopperStart targeted native instruction replay | 141 passed, zero skipped/failed; geometry and buffer/transfer classes only |
| Disposable memory demo | All four allocation-unit sizes passed |
| CPX3 native handler/full ROM/physical storage | Not run; handler does not exist |

The full solution checkpoint preceded the final prototype-only API/test changes;
the 77-case suite reran the entire changed prototype, including all prior crash
cases. The final solution was rebuilt afterwards. These are distinct runs, not
a claimed single 1,625-case solution test run. Existing production cores were not
edited by this task. CopperStart replay builds also report existing compiler and
unrelated source warnings.

Reproduction from the repository root:

```powershell
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-68000-qualification
dotnet test CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-68000-qualification --no-build --logger trx --results-directory artifacts/cpx3-68000-qualification/results
dotnet test prototypes/CopperFS.Prototype.Tests -c Release --artifacts-path artifacts/cpx3-68000-final --logger trx --results-directory artifacts/cpx3-68000-final/results
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-68000-final
dotnet run --project prototypes/CopperFS.Prototype.Demo -c Release --no-build --artifacts-path artifacts/cpx3-68000-final
dotnet test ../CopperStart/tests/CopperStart.Floppy.Native.Tests/CopperStart.Floppy.Native.Tests.csproj -c Release --artifacts-path "$PWD/artifacts/cpx3-68000-replay" --filter 'FullyQualifiedName~FileSystemGeometryInstructionTests|FullyQualifiedName~NativeFileSystemBufferInstructionTests' --logger trx --results-directory "$PWD/artifacts/cpx3-68000-replay/results"
```

TRX receipts remain under these ignored artifact directories. A source SHA-256
manifest in `artifacts/cpx3-68000-final/source-manifest.json` identifies the final
prototype and document inputs; it is not a release or publication receipt.

New host coverage includes seeded arithmetic against BigInteger, carry/overflow
edges, shift boundaries, partition limits, arbitrary alignment, per-step page
counts, stale/name-changed cursors, long ancestry, fragmented allocator windows,
deferred cleanup, indexed extent lookup, caller scratch guards, cursor sizes,
IL allocation/arithmetic audits and scheduling-failure unwind/poisoning.
Deferred reclaim is interrupted after every sector write and before/after each
flush under the same five persistence schedules as the existing crash suite.
Recovery drains the durable queue and the independent decoder verifies namespace,
contents and ownership. Finite crash schedules are not every possible persistence
subset. The earlier 42-page/43rd-page and empty-reserve tests remain in the suite.

Native coverage is the CPX3 arithmetic instruction probe plus the separately
scoped CopperStart FileSystemGeometryInstructionTests and
NativeFileSystemBufferInstructionTests. The latter cover existing native geometry
and transfer behavior, not a CPX3 handler. No production handler source was changed
by this work; no CPX3 HUNK exists to rebuild. Full-ROM, physical SSD/controller,
asynchronous cancellation and cross-platform native execution are not covered.
No ROM, media, build artifacts or packages are published.

## Next native integration gates

1. Use fixed mount/cursor arenas and measure their actual emitted ABI sizes.
   Compile the whole operation path and watermark its stack, including callbacks,
   deep split/rename/reclaim paths and OS/device calls. Fail the configured stack
   budget in tests; do not infer it from managed zero-allocation results.
2. Define DOS packet ownership, open/lock/error semantics and bounded dispatch.
   Preserve single-owner execution across cooperative callbacks and pending I/O.
   If operations must release their task stack while waiting, convert the remaining
   blocking transaction driver to explicit continuation phases first.
3. Implement TD64 range splitting, completion/error handling, device quiescence,
   DMA constraints and reliable ordering barriers. Qualify TRIM separately against
   the controller/backend. Test shutdown/media change with outstanding work.
4. Replay complete native operations under crashes, then run full-ROM and physical
   media qualification. Arithmetic instruction execution alone clears none of
   these handler-level gates.
