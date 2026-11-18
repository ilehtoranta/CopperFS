# CNP2 write-side CRC-32C on the 68000

The packed leaf and verified-reference delta kernels are used only by `Stamp` when publishing a metadata page. The leaf encoder has already cleared the unused suffix. The write kernel zeros the checksum field and uses existing position weights to cover the complete 512-byte sector. The bitmap and root kernel starts from the verified reference CRC, sets the candidate checksum field equal to it, and accumulates word differences in the marked 32-byte regions. Each kernel visits at most 128 longwords. Both operate on the unpublished candidate in the existing 115,200-byte arena.

## Benchmark

Median emulated guest bus cycles from two full trials per supported handler, workload and machine. Lower is faster. Mutation plus flush is the sum within each trial. Remount spans the guest’s inhibit and uninhibit calls. Each trial starts with a fresh 16 MiB disk image. Kernel selection and timing used the compiled handler. The retained compiler backend also reproduced the qualified control HUNK byte for byte.

| CPU and workload | Phase | New CNP2 | Qualified CNP2 | Kickstart FFS 40.1 | SFS 1.279 |
|---|---|---:|---:|---:|---:|
| 68000 mixed | Mutation | 3,841,340 | 3,840,175 | 3,996,245 | Unsupported |
| 68000 mixed | Explicit flush | 142,430 | 156,755 | 26,605 | Unsupported |
| 68000 mixed | Mutation + flush | 3,983,770 | 3,996,930 | 4,022,850 | Unsupported |
| 68000 mixed | Remount | 134,445 | 135,210 | 176,565 | Unsupported |
| 68000 mixed | Complete workload | 6,802,570 | 6,816,710 | 6,898,875 | Unsupported |
| 68000 2 MiB stream | Mutation | 2,033,295 | 2,009,830 | 3,587,070 | Unsupported |
| 68000 2 MiB stream | Explicit flush | 227,845 | 248,490 | 32,350 | Unsupported |
| 68000 2 MiB stream | Mutation + flush | 2,261,140 | 2,258,320 | 3,619,420 | Unsupported |
| 68000 2 MiB stream | Remount | 136,460 | 134,890 | 179,035 | Unsupported |
| 68000 2 MiB stream | Complete workload | 1,100,697,965 | 1,100,836,155 | 1,103,620,140 | Unsupported |
| 68020 mixed | Mutation | 1,586,405 | 1,586,365 | 1,684,020 | 2,477,325 |
| 68020 mixed | Explicit flush | 93,750 | 100,420 | 20,480 | 168,020 |
| 68020 mixed | Mutation + flush | 1,680,155 | 1,686,785 | 1,704,500 | 2,645,345 |
| 68020 mixed | Remount | 86,540 | 86,555 | 108,970 | 215,205 |
| 68020 mixed | Complete workload | 2,740,490 | 2,747,135 | 2,783,360 | 3,916,615 |
| 68020 2 MiB stream | Mutation | 1,294,945 | 1,300,910 | 2,110,920 | 4,556,325 |
| 68020 2 MiB stream | Explicit flush | 140,435 | 145,525 | 20,370 | 169,455 |
| 68020 2 MiB stream | Mutation + flush | 1,435,380 | 1,446,435 | 2,131,290 | 4,725,780 |
| 68020 2 MiB stream | Remount | 86,680 | 86,695 | 115,085 | 219,805 |
| 68020 2 MiB stream | Complete workload | 365,843,750 | 365,855,230 | 367,442,820 | 369,856,675 |

The 68000 used 512 KiB Chip RAM and no Fast RAM. Its common probe requests public memory and a 4,096-longword handler stack. The 68020 used 512 KiB Chip RAM, 2 MiB Fast RAM and the published mixed and streaming probe HUNKs. Both profiles used Kickstart 3.1 and 32 DOS buffers. The original SFS 1.279 requires a 68020. In two attempted Chip-only trials per workload it issued no device commands and reached no timing markers within 20,000 frames. All 28 timed trials on supported configurations passed their independent disk checks. Candidate and control CNP2 produced byte-identical result images and identical device command counts in both workloads.

68000 mixed mutation plus flush took 13,160 fewer cycles (0.33%). Explicit flush saved 14,325 cycles. Remount took 765 fewer cycles.

68000 streaming mutation plus flush took 2,820 more cycles (0.12%). Explicit flush saved 20,645 cycles. Remount took 1,570 more cycles.

68020 mixed mutation plus flush took 6,630 fewer cycles (0.39%). Explicit flush saved 6,670 cycles. Remount took 15 fewer cycles.

68020 streaming mutation plus flush took 11,055 fewer cycles (0.76%). Explicit flush saved 5,090 cycles. Remount took 15 fewer cycles.

In a separate 68000 streaming control, extending the original handler’s code HUNK and load reservation by 408 bytes left its original instructions intact. Mutation took 2,033,190 cycles and remount took 136,400. Extending only the load reservation gave 2,008,210 and 134,860 respectively. These single-run controls show the Chip-only measurement is sensitive to the loaded file layout. The table uses the original unmodified control.

## Native inspection and durability

The 454-byte read-side position kernel is byte-identical to the qualified handler. The 144-byte sector-validation wrapper differs only in its final branch displacement to that same kernel. In the emitted HUNK the two publication loops have no per-word helper calls. Each calls the shared three-step fold once after scanning. CopperSharp emits address arithmetic and longword loads for the nibble lookups.

On a separate scalar 68000 correctness guest, one leaf and two delta CRCs, including their three final folds, took 42,454 handler instruction cycles compared with 57,550 before. The complete explicit-flush handler phase took 136,690 compared with 151,764 before. These numbers use the scalar profiler’s instruction clock. The benchmark table uses guest bus cycles.

Successful flush, unmount and shutdown retain the buffered durability contract. Pending data and inactive metadata are ordered before root publication, and the durable reply follows the second barrier. Both full-ROM 68000 gates observed two barriers at dirty shutdown, zero operation-time allocations, an intact stack guard and a 768-byte maximum on the 4 KiB handler stack. Independent disk checks recovered the last buffered append in the ordinary and fragmented cases.

The Release solution built with zero warnings and zero errors in the final incremental build targeting .NET 10. The solution recorded 2,903 passes, 10 skips and zero failures. All 45 CNP2 host tests passed again on the final implementation, including the independent full-sector CRC comparison. All 169 CopperStart instruction replays and 12 native packet I/O replays passed. These runs cover emulated hardware.

The timed HUNK SHA-256 is `13C1C0BC682A3EE35FD3DA8C791AE6ABEA3E296F02E0B8884AAC09CB534C4D09`. [Per-trial measurements, input hashes and verification receipts](../benchmarks/coppernova-prototype/2026-11-19-write-crc.json)
