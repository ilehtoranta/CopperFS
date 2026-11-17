# CPX3 prototype qualification

13 November 2026. Scope: the isolated host prototype, its tests/demo and the
[new-filesystem design](new-filesystem-design.md). The other prototype-editing
chat completed its CPX2 handoff before these edits. Its source checkpoint is
retained under ignored local artifacts. No CopperFFS/CopperSFS/native-handler
source changes belong to this task; concurrent production work is left intact.

## Implemented design decisions

- A=512..4096, P=max(1024,A), 160 inline bytes, six inline extents, fixed-width
  object/directory/extent trees, eight-level ceiling and nonmerging deletion.
- Two journal slots with 64 on-disk image positions, operation-specific credit
  limits up to 42, streaming staging/replay and two-checkpoint retirement.
- Reserve target 32, capacity 64, protected growth margin 12; separate six-target
  refill. Unlink/shrink/reclaim/prune do not allocate metadata.
- UTF-8 storage, explicit Latin-1 DOS conversion, strict separate host import,
  complete-name comparison on CRC collisions and stable legacy aliases.
- Persistent head-only cleanup, sparse/unwritten zero semantics and synchronous
  discard of retired free space, with a four-bitmap-window scan budget.

CPX3 bytes are in [FORMAT.md](../prototypes/CopperFS.Prototype/FORMAT.md).
CPX1/CPX2 are incompatible and require reformatting. No registered DosType,
published package or production format has changed.

## Crash and memory review

The journal index stores descriptors, not payload pages. Repeated changes to one
home target overwrite its fixed staging slot. Both checkpoint copies retire a
transaction before the next slot can be invalidated. Replay validates the entire
committed log before writing any home metadata. Uncertain I/O freezes the mount.
Detached objects retain ownership until bounded cleanup commits. Returned reserve
pages cannot be assigned within that same transaction. Discard only runs outside
transactions and excludes pool pages and unwritten extents.

The tests deliberately distinguish three bounds:

1. Real seven-level split propagation emits 15 tree images plus the reserve image
   and uses eight new pages; a full eight-level path refuses a ninth level without
   changing home pages. This is a detached structural tree fixture, not a complete
   giant namespace.
2. A valid 42-target journal witness commits and recovers under every injected
   sector/barrier cut; a 43rd target is refused before publication. This checks
   enforcement and recovery, not natural attainment of the worst operation count.
3. Real unwritten middle writes perform two extent insertions, including inline
   descriptor overflow into an extent tree. All real operations run through their
   own credit limits; no operation can borrow unused format slots past its limit.

A full-volume fixture transfers the allocated reserve run to an existing file,
maintaining ownership/bitmap consistency while leaving zero reserve pages and
zero free data units. Create refuses; deletion and recovery need no new metadata,
reclaim ownership, and permit a later create. Its independent checker does not
manufacture exhaustion with unreachable allocated units.

Scratch is exactly 6P+12 KiB: 18,432/24,576/36,864 bytes at P=1024/2048/4096.
Managed allocation tests cover successful mount/create/read/rename/delete/check
paths after warmup. Page buffers and bounded record/path locals do not grow with
tree height or namespace size. There is no claim that those tests measure native
stack frames, compiler spills, DMA buffers, OS structures or per-open state.

Review fixes included preserving inherited first fences during pruning,
case-insensitive reservation of the exact alias shape, checked common-header
payload lengths, immutable charset-profile validation and disabling mutation/
TRIM after an ownership-check failure. A regression also exposed stale partial-AU
data after shrink followed by growth across a sparse gap. Append, random write
and unwritten preallocation now zero the retained old tail before publishing the
larger size; regression and crash tests cover all three paths. Native arithmetic, stack watermarks,
continuation scheduling and slow scans remain measured implementation gates.

## Reproducible validation

Validation completed on 13 November 2026. All runs below completed normally.
The solution uses isolated outputs to avoid locking another chat's build files:

~~~powershell
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-qualification
dotnet test CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-qualification --no-build --logger trx --results-directory artifacts/cpx3-qualification/results
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/cpx3-certified
dotnet test prototypes/CopperFS.Prototype.Tests/CopperFS.Prototype.Tests.csproj -c Release --artifacts-path artifacts/cpx3-certified --logger trx --results-directory artifacts/cpx3-certified/results
dotnet run --project prototypes/CopperFS.Prototype.Demo -c Release --artifacts-path artifacts/cpx3-certified --no-build
~~~

The second build/prototype run includes final review and zeroing fixes made while the long
unrelated SFS host suite was running. It does not imply that those existing
filesystems use the prototype format. Raw results remain in ignored artifacts.

| Coverage | Result |
| --- | --- |
| Final solution build | Passed; one existing CS8602 warning in sibling CopperSharp compiler |
| Full solution host tests | 1,570 passed, zero failed, three external-media cases skipped; SFS completed in 17 min 53 s |
| Final CPX3 host tests | 53 passed, zero failed/skipped; 8,522 injected recoveries across the finite schedules below |
| Disposable demo | Passed for A=512/1024/2048/4096 |
| New-format native/CopperStart replay | Not run; no native handler exists |
| New-format full-ROM/physical media | Not run |

The solution run contained 40 prototype cases before the final review additions,
plus StorageBench 32, Amiga host 18, FFS 296 and SFS 1,184 passes. The separate
final 53-case prototype run supersedes that earlier prototype result; do not add
the two counts as independent coverage. The three skipped original-media cases
are not passes. The sibling compiler warning is CS8602 at
CopperSharp68k/Compiler/Backend/M68kSpillAndCopy.cs:76; prototype builds emitted no
warnings. The final source hashes are retained with the ignored test artifacts.

| Injected scenario | Recoveries |
| --- | ---: |
| Create/rename/delete/append, A=512 | 1,660 |
| Create/append, A=4096 with 512-byte sectors | 2,140 |
| Interrupted journal replay | 70 |
| Exactly 42 journal targets | 990 |
| Empty-reserve full-disk cleanup | 1,842 |
| Unwritten middle write with two insertions | 245 |
| Persistent shrink | 610 |
| Cross-directory replacement | 490 |
| Sparse growth and preallocation after shrink | 475 |
| Total | 8,522 |

The demo checked readback, ownership and remount for 34 files in every AU profile.
It observed peaks of nine journal targets at A=512/1024/2048 and seven at A=4096;
those workload observations do not replace the 42-target qualification witness.

## Limits of this evidence

The fault device cuts after each sector write and before/after each flush. Five
deterministic schedules preserve no pending sectors, all in reverse order,
alternating sectors, only the last sector, or torn 128-byte prefixes. Empty-reserve
cleanup uses the first three. This is **not every possible persistence subset**,
not proof against CRC collisions and not a physical SSD/controller qualification.

The independent decoder reconstructs bitmap ownership, tree bounds, object/entry
links, reserve ownership, exact counters and file data. Tests cover interrupted
replay, slot reuse, corrupt/rechecksummed invalid logs before any home write,
checkpoint repair, read-only refusal, aliases/collisions, sparse zeroing, nonzero
discarded reads and addresses beyond 2^32. The wide-address fixture is a synthetic
reader, not a formatted multi-terabyte volume.

Before native work: specify packet/open-lock/error behavior and continuation
boundaries; use high/low u32 arithmetic in hot paths; measure emitted stack and
buffer guards; provide indexed extent predecessor/tail traversal; complete the
attribute/symlink/alternative-charset profile. Existing host ulong behavior and
zero managed allocations do not establish 68000 cost. Native replay, full-ROM
boot and real storage validation must each be reported independently.
