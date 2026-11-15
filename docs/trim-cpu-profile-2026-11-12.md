# Automatic TRIM CPU profile, 2026-11-12

Metadata preparation and validation dominate the current deferred TRIM path.
Device-capability discovery is also repeated per range. The discard validation
and dispatch path accounts for 8–16% of measured maintenance cycles in this
fixture. Range tracking adds a separate 50–57 thousand cycles during unallocation.

## Workload and measurement boundaries

The selected binaries from the [performance qualification](handler-performance-qualification-2026-11-12.md)
ran under Kickstart 3.1 with the 68020 emulator, using their existing 68000 HUNKs
and maps. CILica scalar replacement is enabled and inlining disabled. All five
fixtures are 16 MiB with 512-byte blocks.

After the normal compatibility workload, the guest inhibits/remounts to clear
old hints, creates a 32 KiB temporary file, closes it and deletes it. SFS also
purges that temporary file from its recycled directory so its data is actually
unallocated. The retained files and metadata are independently checked, then
read back by the original handler after discarded sectors have been poisoned.

The profiler starts at method entry and stops at the matching return for
`NativeAutomaticTrim.Service` and `NativeFileSystemPlatform.ObserveFreedRange`.
It excludes the five-second timer delay and stops watching before ordinary
verification reads and shutdown. Timer-side `FlushSmart` calls are watched
separately; none occurred in the isolated maintenance window here. Earlier
captures that included verification/shutdown flushes are retained separately
and are not used for the results below.

Cycles include emulated OS execution and modeled completion waits, rather than
host file-operation wall time. This is not SSD latency or throughput evidence.
Exclusive instruction attribution covers over 99.9% of each service scope.
Inclusive method families include their callees; nested forwarding overloads
with the same map name are counted once. Inclusive stage percentages and
exclusive copy/checksum percentages must not be added together.

## Deferred service cost

Each profile completed one service batch. OFS/FFS also freed file metadata, so
their discarded byte counts exceed the 32 KiB payload.

| Profile | Service cycles | Discarded bytes | UNMAPs | Discovery calls | Data reads | Device flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OFS | 377,263 | 35,328 | 2 | 3 | 2 | 1 |
| FFS | 313,467 | 33,280 | 2 | 3 | 0 | 1 |
| SFS | 121,389 | 32,768 | 1 | 2 | 0 | 0 |
| SFS2 CSUN | 327,047 | 32,768 | 2 | 3 | 2 | 0 |
| SFS2 TRST | 329,370 | 32,768 | 2 | 3 | 3 | 0 |

There were no normal data-write commands during these service scopes. The
deletions had already committed: recovery checks performed no journal replay,
and the SFS platform could acknowledge a clean durability barrier without
another physical flush. A pending transaction or cold/removable device can have
different costs; these measurements do not price that recovery work.

| Profile | Pointer-layout checks | Prepare | Capability discovery | Discard validation/dispatch |
| --- | ---: | ---: | ---: | ---: |
| OFS | 30.72% | 15.08% | 13.25% | 13.56% |
| FFS | 33.14% | 18.10% | 14.48% | 9.38% |
| SFS | — | 16.97% | 24.83% | 10.07% |
| SFS2 CSUN | — | 39.60% | 13.91% | 16.16% |
| SFS2 TRST | — | 41.07% | 13.73% | 8.19% |

Specific findings:

- **FFS/OFS:** two hints trigger two `TrimTrackedLayout` passes and two
  `Prepare` calls. FFS makes 26 bitmap-pointer lookups and 14 outer header
  validations. Copies cost 59,868 cycles (19.10%) and checksums 37,416 (11.94%).
  All FFS metadata reads hit memory: zero physical reads does not make repeated
  copying and verification free. The bitmap-pointer layout walk still depends
  on volume layout size, despite eliminating scans of unrelated bitmap contents.
- **SFS2:** two hints cause two preparations, two clean recovery checks, two
  mounts and four outer root selections. Preparation costs 129,503 cycles for
  CSUN and 135,291 for TRST. Copies account for another 10.67%/11.34% of exclusive
  service cycles and checksums 5.41%/5.37%; those operations overlap the preparation
  stages. Repeated descriptor/root validation is the largest identifiable phase.
- **SFS:** one hint avoids the second preparation. Discovery is its largest
  individual inclusive phase at 30,136 cycles (24.83%). Its separate clean
  `Recover` check costs 12,545 cycles (10.33%).
- **Discovery:** `Service` queries once to obtain the queue-splitting limits;
  each `TrimWindowCore` queries again. Every query sends VPD B2, VPD B0 and
  READ CAPACITY(16). Thus FFS/OFS/SFS2 send nine discovery commands plus two
  UNMAPs; SFS sends six plus one. These are actual command counts, not estimates.
- **Allocation/barriers:** allocating the DMA-compatible control buffer costs
  about 2,800 cycles per batch, under 2.4%. The clean platform `Flush` calls cost
  under 0.6% in this workload. They are not the dominant CPU cost here.

## Tracking cost during unallocation

| Profile | Callback entries | Tracking cycles | Share of tracking + service |
| --- | ---: | ---: | ---: |
| OFS | 69 | 55,179 | 12.76% |
| FFS | 65 | 52,051 | 14.24% |
| SFS | 64 | 50,283 | 29.29% |
| SFS2 CSUN | 64 | 56,727 | 14.78% |
| SFS2 TRST | 64 | 57,159 | 14.79% |

The queue is coalescing ranges, but these delete paths still call the observer
once per freed block. Those scopes cover the observer and queue bookkeeping,
not ordinary allocator work. Small differences can include interrupt placement.

## Optimization priorities suggested by the profiles

1. Prepare/recover the filesystem once per exclusively owned batch. Repeated
   ranges should still validate their current allocation pages and preserve
   the durability gate.
2. Perform capability discovery once per batch and reuse the queried control
   information for its ranges. A long-lived cache is not required to remove
   the measured duplicate queries.
3. Share FFS pointer-layout validation across the batch, retaining the checks
   needed for every touched bitmap page. Avoid repeatedly copying/checksumming
   the same root solely to walk its pointer array.
4. Report contiguous freed runs from deletion loops where practical, reducing
   the roughly 64 observer calls for this small file.

These are profiling conclusions, not changes made to the handler in this task.
Automatic TRIM remains opt-in and its earlier original-handler performance
qualification failures remain applicable.

## Evidence and reproduction

`artifacts/trim-profile/isolated/manifest.json` pins the existing handlers.
`automatic/<profile>/poison/*-profile.json` holds per-call exclusive/inclusive
cycles and I/O snapshots; `summary.json` aggregates them. The earlier broader
captures are outside `isolated` and must not be mixed with it.

With a locally supplied licensed ROM configured, run:

```powershell
$env:COPPERFS_AUTOTRIM_MANIFEST = (Resolve-Path artifacts/trim-profile/isolated/manifest.json).Path
$env:COPPERFS_TRIM_PROFILE = '1'
dotnet test tests/CopperFS.Kickstart.Tests -c Release --filter 'FullyQualifiedName~AutomaticTrimAfterDeletePreservesReallocatedFiles&DisplayName~False'
```

The five profiled full-ROM cases passed, including poisoned-discard safety and
original-handler readback. The Release solution build passed; host tests passed
1,456 cases with three external-image skips. The native binaries are the frozen
qualified-input candidates; this task changes diagnostic test tooling only.
Physical devices, outstanding-recovery cost and larger/fragmented batch scaling
remain unmeasured by this particular profile.
