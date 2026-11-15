# CPX4 native request admission, 2026-11-15

Historical checkpoint. See the [latest benchmark report](cpx4-benchmark-2026-11-15.md)
for current results and coverage. Optimization is paused; the target remains unmet.

At this checkpoint the benchmark goal remained unmet. This continues the
[metadata checkpoint](cpx4-metadata-redesign-2026-11-15.md).

Ordinary single-command transfers now use bounded native admission without
constructing the generic cursor. Sector alignment, partition/buffer bounds,
maximum transfer and DMA range are checked. Wide offsets, split transfers and
bounced buffers retain the general path. Flush barriers, cancellation and
completion handling are unchanged; buffered durability is preserved.

The exact implementation passed 147 differential command/completion cases in
the prototype. After promotion, 31 focused native tests pass. The discovery
fixture now expects recovery to read two adjacent checkpoints, split at the
device maximum, while retaining all discovery/address/error assertions.

| Phase | CPX4 | FFS 40.1 | SFS 1.279 |
|---|---:|---:|---:|
| Mixed mutation | 4,092,680 | 1,683,555 | 2,494,475 |
| Mixed flush | 1,226,305 | 20,460 | 161,380 |
| Mixed remount | 1,471,500 | 108,975 | 218,790 |
| Mixed total | 8,330,870 | 2,782,770 | 3,936,530 |
| Streaming mutation | 5,877,850 | 2,111,110 | 4,572,820 |
| Streaming flush | 1,499,045 | 20,370 | 174,265 |
| Streaming remount | 1,613,375 | 115,155 | 215,020 |
| Streaming total | 376,920,955 | 367,444,505 | 369,874,035 |

Two trials per filesystem use the established order and unchanged workloads.
All twelve trials pass guest assertions and independent disk validation.

The 221,304-byte HUNK SHA-256 is
`6D09C60532F830AFDBC0B0380EAF371867167C885438F69B48DA9C92B88FBB92`. Its 51 source inputs
match the manifest; the compatibility report has zero managed allocation sites.

Release solution build succeeds. Full solution tests and a fresh 19-case ROM
run are live; these are not passes yet. CopperStart’s existing geometry/buffer
replay binary passes 141 cases; consumer adapter sources are unchanged.

Early ROM receipts pass but indicate strict fallback on the 512 KiB machine,
where the preceding HUNK had only 672 bytes minimum free. Keeping optional
buffering available within that memory budget requires attention before adding
more native code. Benchmark mounts with 2 MiB Fast RAM retain buffering.

Live handles and results are in ignored
`artifacts/cpx4-admission-redesign-20261115/progress.json`. No publication occurred.
