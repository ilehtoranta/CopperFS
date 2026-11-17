# SFS2 1.279 comparison, 2026-11-17

`checkpoint/` contains the complete paired run of the opt-in owned SFS2
handler rebuilt from the November 15 source checkpoint. It uses the normal
build pipeline with adapter CILica inlining disabled. The candidate SHA256 is
`26192DC97730DF7F4D8C5E8B4439B77DA580D52892B6BB7315FC724E07907E2C`.
See the [previous run](../2026-11-15/README.md) for rate calculations and gates.

The same frozen Kickstart test harness ran both handlers on an emulated 68020,
with 512 KiB Chip and 2 MiB Fast RAM and a 16 MiB synthetic volume on zero-latency
virtual storage. Each throughput case transfers 128 KiB, starts each phase after
WaitTOF, and includes close and explicit flush in the write phases. Three trials
alternate between handlers. The files record which buffer class each run used.

| Buffer | 64 KiB operation | 1.279 cycles | Candidate cycles | Cycle change | 1.279 KiB/s | Candidate KiB/s |
|---|---|---:|---:|---:|---:|---:|
| Fast | Grow | 401,270 | 433,240 | +7.97% | 2,262.83 | 2,095.85 |
| Fast | Overwrite | 141,290 | 156,220 | **+10.57%** | 6,426.53 | 5,812.35 |
| Fast | Read | 53,537 | 45,250 | -15.48% | 16,960.43 | 20,066.41 |
| Chip | Grow | 402,843 | 433,290 | +7.56% | 2,253.99 | 2,095.61 |
| Chip | Overwrite | 141,383 | 142,970 | +1.12% | 6,422.29 | 6,351.02 |
| Chip | Read | 53,530 | 45,780 | -14.48% | 16,962.55 | 19,834.10 |

Both 512 B and both 4 KiB throughput cases pass. Both mixed workloads pass.
The Fast RAM mutation phase uses 17,107,960 versus 15,582,860 cycles (+9.79%).
The Chip RAM mutation phase uses 17,110,040 versus 15,596,303 cycles (+9.71%).
Both verification phases use about 1.08% more cycles. The six-boot clean
handoff passes. The default SFS2 build separately passes the overwrite
recovery test across the distinct durable states produced by device flushes.

`codec-inlining.json` contains a controlled instruction profile on these same
managed inputs, with CopperSharp `f07e8ec99` and CILica 0.20.2-preview. Both
arms rewrite the same three codec methods and retain scalar replacement.
Enabling inlining adds 80 HUNK bytes and lowers 64 KiB growth from 434,010 to
427,430 guest cycles (1.52%). Overwrite moves from 142,630 to 142,800 cycles
and read stays at 49,820. These unaligned instruction profiles measure the
effect of the optimizer. The paired tests above determine acceptance.

## Payload-only flush

`flush-fast/` measures the source change that skips root and redo setup when
the accepted transaction contains only payload writes. It still issues the
device flush and suspends the mount on a failed barrier. The 248,928-byte HUNK
was built through the normal pipeline using CopperSharp `f07e8ec99`. SHA256
`7066123E5AAC14087997DFCF1A1313BEA74B9CB74F7BEF2B8480A719F0893030` identifies
the candidate.

| Buffer | 64 KiB operation | 1.279 cycles | Candidate cycles | Cycle change | 1.279 KiB/s | Candidate KiB/s |
|---|---|---:|---:|---:|---:|---:|
| Fast | Grow | 409,010 | 433,280 | +5.93% | 2,220.01 | 2,095.65 |
| Fast | Overwrite | 93,780 | 135,580 | **+44.57%** | 9,682.29 | 6,697.19 |
| Fast | Read | 51,870 | 45,240 | -12.78% | 17,505.40 | 20,070.85 |
| Chip | Grow | 401,260 | 433,373 | +8.00% | 2,262.88 | 2,095.20 |
| Chip | Overwrite | 165,033 | 135,660 | -17.80% | 5,501.95 | 6,693.24 |
| Chip | Read | 54,360 | 45,750 | -15.84% | 16,703.55 | 19,847.11 |

The candidate issues two data writes and one flush for the 64 KiB overwrite.
The original also follows that path in all three Fast RAM trials here, at
93,780 cycles each. In the checkpoint comparison above, only one Fast RAM
trial took that path. The other two performed five writes and four flushes
and took about 165,000 cycles. Preserve both observed reference paths when
interpreting the means. The faster reference path allows up to 103,158 cycles,
so the new candidate still needs to remove 32,422 cycles. The 512 B and 4 KiB
cases pass in both buffer classes. Fast and Chip mixed mutation use 9.53% and
9.54% more cycles, respectively. Verification uses 1.19% and 1.20% more.
The six-boot handoff also passes. The completed matrix is eight passed and
one failed, with 64 KiB Fast RAM overwrite as the only failing phase.

The matching unaligned overwrite instruction profile drops from 142,690 to
141,430 cycles. The remaining first-packet lookup costs include 12,626 cycles
to find and check the file object and 11,950 to walk its extent. Those figures
are inclusive instruction profile costs and exclude the second packet's
already-cached extent path.

The Release solution builds with the flush change. The focused SFS2 suite
passes 91 tests, including failed device flush recovery and first-write retry
after a pending create. The SFS2-enabled CopperStart instruction replay passes
234 tests with five external-fixture skips.

Both native builds have zero reachable managed allocations. The default
rebuild is byte-identical to the HUNK that passed full-ROM overwrite recovery,
with SHA256 `BE3922F2299F276B199F065E2E52664049EE81750B808BA803ED18C5DDFD4130`.
