# CPX4 compact buffering, 2026-11-15

Historical checkpoint. See the [latest benchmark report](cpx4-benchmark-2026-11-15.md)
for current results and coverage. Optimization is paused; the target remains unmet.

At this checkpoint the goal remained unmet. This continues
[native admission](cpx4-native-admission-2026-11-15.md).

The larger handler exhausted optional buffering on the 512 KiB machine. Its
strict fallback caused journal ROM tests to observe publication before explicit
flush. Those superseded validation runs are not passes.

Native startup now tries eight clean cache pages, then four if necessary. Both
retain 42 dirty images, 42 undo images, the object cache, rollback storage and
every persistence barrier. Full-memory mounts keep eight pages. Compact storage
saves 4,160 bytes for 1 KiB pages and 16,448 bytes for 4 KiB pages. Disk and
packet layouts are unchanged.

All 28 buffered-write tests pass, including compact durability/remount/canary
cases at all four allocation-unit sizes. HUNK size is 221,428 bytes; SHA-256
`D70EC02CEE964BCF29736F142A80CF4721B0766FE8B68CAC0E4561A86B99DE92`. Its 51 source inputs match and
compatibility analysis reports zero managed allocation sites.

Early ROM growth and unsupported-trim receipts pass with buffering allocated,
zero operation-time Exec allocations and 3,968 / 4,464 bytes minimum free.
The full 19-case ROM run passed with zero failures and zero skips.
The compact-checkpoint Release solution suite passed 2,445 cases, with
nine skips: six opt-in ROM theories and three unavailable-media cases.
Its compiled CPX native suite passed 74 cases; the separate opted-in full-ROM
run passed 19. These receipts qualify the compact checkpoint, not later scalar
source.

Mixed total median is 8,324,720 cycles, versus FFS 2,782,770 and SFS 3,931,335.
The goal is not achieved. Current results and live handles are recorded in
`artifacts/cpx4-compact-redesign-20261115/progress.json`.

An isolated arithmetic probe produces identical results with Wide wrappers and
primitive UInt64. On 68020 the costs are 232,933 versus 25,389 cycles; on
68000, 1,184,192 versus 124,448. This suggests redesigning internal arithmetic
while preserving the public Wide representation. It is not a filesystem speed
claim. Probe source and receipts are under the ignored artifact directory.
