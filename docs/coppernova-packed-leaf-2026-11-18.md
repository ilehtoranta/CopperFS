# CNP2 packed leaf encoding and CRC qualification

The selected native handler uses the known length of a newly encoded leaf to checksum the page. The encoder clears the unused bytes. Position-weighted tables already include the contribution of the zero suffix to the full 512-byte CRC-32C. On reads, the handler verifies the suffix before omitting it from the arithmetic or reuses a previously verified, byte-identical page.

The resident encoder carries each inode’s kind, extent count and directory length through validation into the row writer. It writes the adjacent kind and count together and writes the inline extent fields once. Overflow and split leaves retain their existing paths. The handler uses the same 115,200-byte caller-owned arena and tables.

## Benchmark

Median guest bus cycles, lower is faster. Both the optimized and prior CNP2 ran twice per workload, as did the two original reference handlers. The two prior-CNP2 runs bracketed the final comparison. All runs used fresh images, the same frozen guest and runner, Kickstart 3.1, an emulated 68020, 512 KiB Chip RAM, 2 MiB Fast RAM and 32 DOS buffers. The workload starts after cold mount.

| Workload and phase | Previous CNP2 | Optimized CNP2 | Kickstart FFS 40.1 | SFS 1.279 |
|---|---:|---:|---:|---:|
| Mixed, mutation | 1,587,185 | 1,586,060 | 1,683,235 | 2,478,940 |
| Mixed, explicit flush | 108,200 | 100,360 | 20,440 | 161,230 |
| Mixed, mutation plus flush | 1,695,385 | 1,686,420 | 1,703,675 | 2,640,170 |
| Mixed, complete workload | 2,755,240 | 2,746,610 | 2,782,315 | 3,918,540 |
| 2 MiB stream, mutation | 1,298,945 | 1,300,835 | 2,111,135 | 4,572,680 |
| 2 MiB stream, explicit flush | 163,105 | 145,535 | 20,340 | 174,270 |
| 2 MiB stream, mutation plus flush | 1,462,050 | 1,446,370 | 2,131,475 | 4,746,950 |
| 2 MiB stream, complete workload | 365,866,475 | 365,855,090 | 367,442,775 | 369,873,825 |

Mixed mutation plus flush saved 8,965 cycles (0.53%). Explicit flush saved 7,840 cycles (7.25%). The complete workload saved 8,630 cycles (0.3132%).

Streaming mutation plus flush saved 15,680 cycles (1.07%). Explicit flush saved 17,570 cycles (10.77%). The complete workload saved 11,385 cycles (0.0031%).

Streaming mutation took 1,890 extra cycles. Readback accounts for over 99% of the workload, which limits the end-to-end gain from a faster flush.

All 16 final runs passed the guest and independent disk checks. Each CNP2 result image matched its previously qualified image byte for byte, with the same device command counts. Per-trial values, input hashes and independent checks are in the [benchmark evidence](../benchmarks/coppernova-prototype/2026-11-18-packed-leaf.json).

## Selection and native checks

Five native variants were screened. The simpler position-based encoder had the lowest mixed mutation-plus-flush screen at 1,683,350 cycles, but its mixed remount cost 91,290 cycles and its complete streaming run took 365,887,680. The selected compact encoder recorded 1,686,430, 86,550 and 365,854,690 respectively in the same screens. The four-way and eight-way slicing candidates took 1,691,180 and 1,720,200 mixed mutation-plus-flush cycles. The selected code reuses the original tables without extra mount initialization.

On the separate scalar 68000 correctness guest, the full leaf checksum fell from 44,322 to 34,996 handler cycles and the complete explicit-flush handler phase fell from 160,832 to 151,764. The emitted position-weighted kernel is 454 bytes with zero call instructions.

The Release solution built with zero warnings and errors. The solution recorded 2,902 passes, 10 skips and zero failures. The new regression exercises every valid four-byte-aligned used length from 40 through 512, checks the published CRC against independent bitwise CRC-32C, and verifies that changing the first unused byte fails read-side validation.

All 169 CopperStart instruction replays and 12 native packet I/O replays passed. Both full-ROM gates ran the timed handler on an emulated 68000 with 512 KiB Chip RAM and no Fast RAM. Both observed zero operation-time allocations, a 768-byte peak on a 4 KiB task stack, an intact guard and two device barriers on dirty shutdown. The independent disk check recovered the last buffered append. Flush, unmount and shutdown retain their buffered durability contract. All reported timings are from emulation.

The timed handler’s SHA-256 is `AD7A194107E06D2965126A4E107FB2AA660062A44FA025B8F45190A8A502A6C0`.
