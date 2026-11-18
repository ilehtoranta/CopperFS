# Packed CopperNova: mixed workload CPU study

This study changes the isolated CNP2 prototype. Its durable on-disk format and two-barrier protocol remain unchanged. The preceding packed handler is ED341072FA04E4CD9FC90565CD49383D68B3BB353BCEE683D4A0862935BFEF7D; the final candidate is D82EEF49968FAD0BEA20483CC25E5A005CFDAB35F6AE099B60C29D3A1946E8D5.

## Profile

The benchmark phases are measured with the frozen mixed guest on a 68020. Separately, a host-side scalar observer attributes instruction cycles to methods while the 68000 handler runs the mixed correctness probe. The correctness guest also verifies an unflushed append at shutdown. Scalar handler cycles and end-to-end benchmark bus cycles are different units.

| Correctness guest phase | Previous handler task cycles | New handler task cycles |
|---|---:|---:|
| Mutation | 935,464 | 632,884 |
| Flush | 449,084 | 266,262 |
| Remount | 394,026 | 153,412 |
| Read | 157,160 | 126,534 |

CRC alone accounted for 238,094 scalar cycles in the original flush and 238,086 in remount. The original mutation made 660 calls to the execution-state read helper and spent 72,048 scalar cycles in two transport bounce copies. The new mutation's largest measured method is packet dispatch (57,044 cycles). The new flush spends 136,498 scalar cycles in CRC; remount's largest method compares previously verified pages (37,002 cycles).

## Changes retained

- A 32-byte zero region advances CRC-32C using the exact equivalent linear transformation. Four 256-entry tables fit in the last 4 KiB of the existing arena. Nonzero chunks use the ordinary slicing loop. Metadata checksum bytes are still logically zero.
- A remount rereads the roots and tree. For each root and the most recently sealed or verified leaf, the engine can reuse the result of a successful CRC check after an exact 512-byte comparison. A changed byte takes the CRC path. Header, owner, generation and record validation remain separate.
- A partial-sector transfer within an existing extent can read one span of up to 4 KiB and merge the requested bytes. It never writes beyond the caller's destination. Larger transfers yield and repeat. Whole aligned sectors retain their direct path.
- The engine tracks dirty cache slots and can encode a complete, resident bucket directly when it fits a single leaf and has no overflow. Cache misses, branches, oversized leaves and extent overflow take the normal tree path. All changed metadata still goes to inactive sectors.
- Common packet paths retain a local state pointer. Resource counters avoid scans of unused handles. The standalone handler executes at most eight internal steps before polling cancellation again, stopping immediately at I/O or completion. The portable single-step API remains available.

An 8 KiB dirty-data cache was also tested and removed: compared to stage 2 it saved six backing reads and six writes but raised mixed from 3,131,180 to 3,165,060 cycles in single-run screens. Those screens do not substitute for repeated comparisons.

The allocation-free compiler report and both ROM gates apply to the same final HUNK as the timing runs. Decoding the emitted HUNK found a 610-byte CRC routine with no call instructions. The 32-byte zero transformation trades 240 bytes of native CRC code and 4 KiB of previously unused arena space for fewer table lookups on sparse pages. The arena is still 98,304 bytes; the dirty-slot vector, comparison snapshots and transfer scratch are all fixed-size.

## End-to-end mixed result

Two trials each, original Kickstart FFS 40.1 and SFS 1.279, 16 MiB images, identical 68020 guest and full readback. Lower is faster.

| Handler | Mutation | Flush | Remount | Verify | Total |
|---|---:|---:|---:|---:|---:|
| Previous packed prototype (previous comparison) | 1,923,790 | 230,095 | 199,360 | 1,020,065 | 3,373,310 |
| Current candidate | 1,764,050 | 154,555 | 107,865 | 1,025,660 | 3,052,130 |
| Kickstart FFS | 1,683,555 | 20,460 | 108,975 | 969,720 | 2,782,710 |
| SFS 1.279 | 2,489,380 | 161,275 | 218,815 | 1,061,930 | 3,931,400 |

The candidate saves 321,180 cycles versus its predecessor and misses FFS by 269,420. Replacing its entire flush with a zero-cost operation would still leave it behind FFS. The references received the same DOS operations; equal power-loss guarantees are not inferred.

The independent ownership and payload checks match the preceding prototype. The final disks differ from its disks at five bytes in sector 195: four CRC bytes and the low byte of the file change counter. Combining the two partial overwrites into fewer internal transfers advances this counter four fewer times. It is not a publication generation or a stable object ID. Mixed issues 9 read and 7 write device commands, compared with 11 and 11 in the preceding prototype; both perform three whole-run flushes.

## Qualification

Streaming (2 MiB, same end-to-end gate and two fresh trials per handler): CopperNova **366,806,765**, Kickstart FFS **367,442,605**, SFS 1.279 **369,862,180** bus cycles. CopperNova's streaming lead over FFS was 635,840 cycles (0.173%); it still loses the mixed comparison. All 12 timed guest and independent image checks passed. The [machine-readable evidence](../benchmarks/coppernova-prototype/2026-11-17-mixed-hot.json) includes the individual timings, hashes, coverage and complete before/after method profiles. Run `node tools/studies/record_mixed_hot.mjs` from the isolated worktree to regenerate it from the retained local receipts.

Release build: zero warnings and errors. All 42 focused tests and 169 CopperStart integration replays passed. Focused tests exercise a changed byte at either end of a remembered leaf, cross-boundary unaligned writes, torn and reordered publication, short I/O, changed retries, splits and collapses. Both actual-handler ROM gates passed on a 68000 with 512 KiB Chip RAM, no Fast RAM, a 4 KiB stack and no operation allocations. Both observed two barriers at shutdown and independently verified the final disk. No physical-device qualification was performed.
