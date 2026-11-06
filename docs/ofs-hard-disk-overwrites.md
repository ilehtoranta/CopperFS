# OFS hard-disk overwrites

On a confirmed fixed disk, DOS0 considers relocating a complete file of 64–72 full data blocks on a 512-byte-block volume. At least seven transitions between successive data blocks must span 128 or more block addresses. The allocation must fit one contiguous free run on the source bitmap page. A full disk, a partial tail, an interior overwrite or an extension table keeps the existing placement.

Each replacement block has a 24-byte OFS header, a 488-byte payload, an owner, a sequence number, a forward link and a checksum. The last forward link is zero. Both the pointer table and first-data pointer are published together in the file header after the replacement chain has been written and flushed. Old blocks are freed after the header flush. The procedure uses four flushes and the existing three scratch blocks.

## Cutoff experiment

The same Kickstart 3.1 guest overwrote a scattered file in one DOS call, then read and checked every byte in a fresh boot. The control used the same payload-copy primitive as the relocation trial, so replacing the legacy byte-copy loop could not by itself justify moving data. The trial rebuilt the complete forward chain and checksums and paid for bitmap reservation, pointer publication, release and four flushes.

| OFS layout | In-place write cycles | Trial write cycles | In-place cold read cycles | Trial cold read cycles | Cold read commands |
|---|---:|---:|---:|---:|---:|
| 8 blocks, eight separated runs | 532,760 | 484,510 | 200,010 | 154,240 | 8 → 1 |
| 32 blocks, eight separated runs | 1,639,460 | 1,170,450 | 587,310 | 547,780 | 8 → 2 |
| 32 blocks, eight closely spaced runs | 1,589,530 | 1,117,380 | 541,680 | 547,930 | 1 → 2 |
| 64 blocks, eight separated runs | 3,124,770 | 2,010,570 | 1,124,500 | 1,077,690 | 8 → 2 |

At the emulated PAL clock of 7,093,790 Hz the 64-block rewrite saved 157 ms before accounting for additional physical flush latency. The write alone breaks even at about 39 ms per additional flush. Charging an illustrative 20 ms per flush leaves about 77 ms saved before the first reread. Under that same assumption the 32-block separated case needs three subsequent cold reads and the closely spaced case gets slower on each reread. Physical hard-disk timings remain unmeasured.

Run `tools/Benchmark-FfsOverwrites.ps1` with a licensed ROM to compare the selected handler with in-place overwrites. The OFS cases also exercise already-packed files. Raw measurements are in the generated `ofs-*/placement.json` files.

In the final paired run against the existing in-place handler, the qualifying 31,232-byte overwrite fell from 5,133,010 to 2,079,080 bus cycles. It issued 12 reads and 69 writes plus four flushes, versus 62 reads and 67 writes in place. A fresh boot verified the new contents and both chains. Its sequential read took 1,109,600 cycles and two device commands, versus 1,125,600 cycles and eight commands in place. Most of the measured gain was on the overwrite.
