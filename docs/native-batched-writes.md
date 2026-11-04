# Native batched writes and relocation

This records the initial fragmented-only relocation integration. The subsequent
[large contiguous overwrite change](contiguous-relocation.md) extends that
policy and records its own validation results.

The native integration is in the sibling CopperStart checkout, including its
embedded `external/CopperSFS` core. The portable benchmark implementation was
adapted to that newer core's buffered transactions, metadata views, recycled-file
accounting, physical write queue and device range-transfer API. Filesystem blocks
remain 512 bytes in the native qualification fixtures.

## Write policy

- Small writes retain the existing buffered or strict policy.
- Aligned contiguous appends use the existing allocation path, extended to
  transfers of up to 64 KiB. Buffered mounts can still combine their metadata
  updates across DOS packets.
- Large overwrites first checkpoint acknowledged buffered operations. They then
  use one strict CSUN transaction for the complete DOS Write packet. Contiguous
  old-data images and journal records are transferred in ranges before the undo
  head is published and the home blocks are overwritten.
- Fully overwritten fragmented ranges of at least 16 KiB can move to a new
  contiguous run. Replacement data is flushed before changing the extent chain;
  retired blocks cannot be reused until the operation commits. A failed search
  for contiguous space falls back to an in-place overwrite.
- Partial blocks retain their unchanged bytes. The replacement can split an
  extent, preserve a suffix, merge with a preceding replacement, or prune empty
  extent-tree leaves. First-extent owner tags are distinguished from block links.

The mount allocates an optional 64 KiB scratch buffer on the first large write
and frees it on unmount. Allocation failure retains the existing writer. The
native adapter continues to apply the device's DMA mask, bounce-buffer,
MaxTransfer and TD64 constraints. Existing invalidation clears every open
handle's extent cursor when writes change a file's mapping. The private extension
ABI is now CSE5 (version 6), so the DOS provider and extension ROM must be built
together; the availability check rejects earlier layouts.

## Validation on 2026-10-24

Both buffered and strict MC68000 handlers and the CSE5 extension ROM build
successfully. The full CopperStart Release solution build passes with six
existing warnings. Validation also passes at each layer:

| Layer | Result |
| --- | --- |
| Portable Release/reliability suite | 239 passed |
| Embedded core host tests | 250 passed; one optional external-image test skipped |
| Compiled native adapter tests | 51 passed |
| Compiled SFS transfer/deletion tests, 68000 and 68020 | 16 passed |
| Native architecture checks | 68 passed |
| Extension ROM signature and checksum | 1 passed |
| Buffered native regression suite | 10 passed |
| Strict native regression suite | 9 passed |
| Bulk overwrite recovery matrices | Both policies passed |

The native recovery audit covers **1,485 sector-write/flush cut points across
383 actual cut/reboot executions**, including the original reference handler.
CopperSFS accounts for 1,361 of those points. Bulk tests cover 1,156 points with
54 reboots by grouping identical durable images; the existing mutation matrix
executes all 329 cuts separately. Every planned point is accounted for, with
no failed recovery or independent image check. The buffered and strict runs
also exercise A500 startup, shared handles, resize, flush and remount behavior.

The fragmented benchmark file finishes with **3 extents instead of 66**. The
contiguous file retains one extent. Every completed bulk trial is independently
checked and the entire overwritten file is extracted and compared. Recovery
tests also compare neighboring files.

The bulk workloads overwrite 1 MiB using sixteen 64 KiB DOS Write packets.
These are five-trial median guest CPU cycles; the baseline is the pre-change
buffered handler:

| Workload | Before, buffered | After, buffered | Cycle speedup | After, strict |
| --- | ---: | ---: | ---: | ---: |
| Fragmented overwrite | 8,350,734,090 | 159,336,549 | **52.41×** | 160,370,590 |
| Contiguous overwrite | 8,310,075,571 | 494,830,279 | **16.79×** | 496,916,630 |

The default buffered handler's I/O counts explain the improvement:

| Workload | Sector reads, before → after | Sector writes, before → after | Flushes, before → after | Read requests, before → after | Write requests, before → after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Fragmented overwrite | 696,121 → 2,641 | 6,288 → 2,464 | 192 → 232 | 87,006 → 316 | 259 → 348 |
| Contiguous overwrite | 696,169 → 17,601 | 6,288 → 6,264 | 192 → 128 | 87,007 → 1,954 | 257 → 168 |

The native adapter already combined physical writes. Batching undo records
removes much of the per-block journal lookup and read work. Relocation also
avoids saving the overwritten data as undo images. Publishing replacement
data safely requires additional flushes in the fragmented case, and its
metadata updates produce more write requests despite fewer written sectors.
The strict handler has the same bulk I/O counts as the buffered handler.

The six standard workloads have these five-trial median guest CPU cycles for
the default buffered handler. Positive change means more cycles:

| Workload | Before | After | Change |
| --- | ---: | ---: | ---: |
| Sequential 1 MiB read | 2,643,471 | 2,674,350 | +1.17% |
| Sequential 1 MiB write | 16,151,441 | 16,459,239 | +1.91% |
| Small-file creation | 12,053,720 | 12,136,150 | +0.68% |
| Directory enumeration | 6,331,000 | 6,317,419 | −0.21% |
| Mixed access | 12,805,460 | 12,887,549 | +0.64% |
| Warm-cache 1 MiB read | 2,636,821 | 2,608,410 | −1.08% |

The sequential write retains the same I/O: 8 sector reads, 2,071 sector writes,
4 flushes, 1 read request and 131 write requests. This workload already benefited
from the native append writer; the new overwrite path does not improve it.

## Reproduction and evidence

Build the MC68000 development handlers from CopperStart:

```powershell
./build/Build-SfsHandler.ps1 -ArtifactRoot <buffered-output>
./build/Build-SfsHandler.ps1 -StrictWrites -ArtifactRoot <strict-output>
```

`build/Prepare-SfsBulkReference.py` derives a disposable bulk-write fixture from
the pinned independent A/B reference image. It uses amidisk to create a 1 MiB
file with 66 extents and another with one extent. The bulk workload writes
64 KiB packets, checks another already-open reader, and flushes. Separate native
correctness and recovery cases remount after a 64 KiB overwrite. The cut matrix
covers every individual sector write and flush during one
64 KiB overwrite, including sectors inside a single device request. Under this
model, cuts between the same two flushes leave identical durable images; bulk
cases replay one representative per such image and record all covered points.
The audit checks complete coverage and equal durable prefixes. Each
recovered image is independently checked and extracted with amidisk; neighboring
file bytes are also checked.

The standard six-workload benchmark uses five cold trials per handler and a
separate warmed-read workload. The bulk benchmark adds five trials each for
fragmented and contiguous 1 MiB overwrites. Timing is guest CPU cycles between
guest markers, excluding boot. These are native emulation measurements, distinct
from the portable host timings in [copy-on-write.md](copy-on-write.md).

Local build inputs, HUNKs, source hashes, benchmark reports, cut plans and TRX
results are retained under `artifacts/native-batched-writes/`. The downloadable
release has not been replaced by these development binaries.

`summary.json` and `audit-final.log` record the successful final audit of 719
source files, all required test results, five trials per measured workload,
matching before/after fixture and emulator settings, and complete cut coverage.
The retained benchmark reports are:

- Baseline bulk: `performance-1d1f4194099446138c3fa4b09c6e823a.json`
- Buffered bulk: `performance-d4cb64036f5645079cc93a8bb22c70e2.json`
- Strict bulk: `performance-7a71dbbd2b114cb7b12e32b92c2b10f5.json`
- Baseline standard: `performance-895420b0dbaa4cd986de6eea3c661af8.json`
- Buffered standard: `performance-94e28bd8cecd411c8a936b5b1758ef65.json`

The final buffered HUNK is 126,536 bytes, SHA-256
`CEE80B0F3D6716138EA8C1C1D2C29EF4130D96112DCE93DE98DA8F219E935A08`.
The strict HUNK is 126,328 bytes, SHA-256
`32EFA0F0955B1507CCC1FD9984C4E814DA217E9AF77157F6A703ABECB8BA7F4B`.
Both use embedded core SHA-256
`30464C4709D1069044BDDDF57693B46C6E58050029DB77DFB62207D0FBB674BD`.
The pre-change buffered handler is 115,692 bytes, SHA-256
`E569023831916DB6FD13F83ACD29F3F92CFBE2829DEDD47D4B4B16447686EF06`.

The native crash model uses atomic 512-byte sectors, reliable flushes, and loss
of unflushed device writes. Host fault tests additionally cover operation
failure and same-process retry. These results do not qualify torn device
sectors, controller reordering or physical Amiga hardware.
