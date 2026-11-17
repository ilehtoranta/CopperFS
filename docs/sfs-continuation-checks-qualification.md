# SFS overwrite continuation and relocation profiling

The opt-in shared-pool handler now consumes the borrowed extent leaf immediately after file-block lookup, before any metadata access or device operation can replace it. An established pool continuation validates the tree image and source bitmap. When destination slot, revision and block all match the source, the same validation covers the destination. Different identities still take the full ownership check. The general relocation count stays at two.

## Timing and memory

Three alternating Kickstart 3.1 trials per arm used an emulated 68020, 16 MiB SFS media and 32 DOS buffers. The overwrite phase includes twelve 8 KiB writes and explicit flush.

| Mean guest cycles | Original SFS 1.279 | Archived CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast overwrite and flush | 347,723 | 797,120 | 769,453 |
| Chip overwrite and flush | 324,860 | 799,150 | 771,343 |
| Fast growth, overwrite and verification | 5,781,223 | 6,402,990 | 6,375,607 |
| Chip growth, overwrite and verification | 5,762,257 | 6,409,057 | 6,381,267 |
| Fast warm mixed mutation | 16,986,123 | 17,787,783 | 17,781,287 |
| Chip warm mixed mutation | 16,999,270 | 17,789,383 | 17,782,623 |

Every CopperSFS overwrite issued 17 writes and four flushes. Original SFS issued 12 writes and one flush in five trials. One Fast trial issued 15 writes and four flushes. Growth alone regressed about 12,000 cycles in both RAM classes. The independent mixed release gate exceeded original by 3.647% and 3.506% on Fast and Chip mutation and 2.952% and 3.163% on verification. The 0.1% gate fails.

Measured retained memory was 126,008 bytes on mount and 138,888 after mixed mutation for both CopperSFS handlers. The native file grew from 168,024 to 168,564 bytes. CILica scalar replacement was enabled and inlining disabled.

## Attribution

The archived handler took 797,120 scalar cycles in the preceding profile. A fresh rebuild of the checkpoint took 797,110. The revised handler took 769,410. The table uses inclusive cycles, which overlap.

| Method or region | Archived | Revised |
|---|---:|---:|
| Pool ownership checks, ten calls | 34,110 | 19,250 |
| All continuation attempts, eleven calls | 172,023 | 168,825 |
| Read already located extent, two calls | 5,268 | 1,058 |
| General relocations, two calls | 169,879 | 174,683 |
| WriteOperation, two calls | 238,848 | 234,788 |
| Explicit flush | 112,770 | 92,577 |
| Handler region, exclusive | 649,178 | 629,226 |

General relocation's inclusive cost increased despite removing the duplicate leaf acquisition. Code placement and guest scheduling contribute to whole-phase timing. The first revision wins on total cycles including flush. A second prototype relinked an exhausted root-leaf extent to its successor and retained the terminal successor's location for a fresh allocation proof. Two dedicated host tests exercised this path, but the benchmark still took two general relocations. The prototype cost 774,447 Fast and 776,433 Chip overwrite cycles. The branch retains the faster revision.

## Reproduction

The three-arm ordinary measurements, Kickstart tests, scalar trace and native replay logs for the final binary are under `artifacts/sfs-continued-verified`. The solution and standalone build logs are under `artifacts/sfs-continued`. The isolated successor experiment and patch are under `artifacts/sfs-successor`. The archived control is `artifacts/sfs-overwrite-tail/final/candidate/CopperSFS`, SHA-256 `42C554CF2440ED2F6C6123B81F8FCEB490A9C574CA169F17B157ADD29C6BBB39`. The revised binary is `artifacts/sfs-continued/verified-handler/CopperSFS`, SHA-256 `E5D56A0C02A732E9D7AF1BB9F200CE158A2D8EAF6303EBFC54EBECC8E986EC90`.

## Qualification of the retained revision

Release solution build succeeded with zero warnings and errors. The final Release solution run passed 2,171 tests with five conditional skips and zero failures. The first full solution run exposed a recursive FFS table-read fallback in the checkpoint. Restoring the device read and the unsupported DOS-type fixture gave 278 passing FFS host tests. The rebuilt CopperFFS handler and 141 native buffer and geometry replays passed. The default and shared-pool M68000 standalone builds passed. CopperStart passed 186 cache, buffer, transfer and geometry replays and 77 link and size replays.

Kickstart 3.1 passed 12 grouped recovery cases, including the 96 KiB workload cut at nine points and remounted by both handlers. All 43 candidate sector prefixes remounted by original SFS showed entirely old or new payloads, intact neighbors and correct free space. Partial journal and home writes, partial journal reads, handoff, close, timer, explicit flush, packet rejection, foreign startup, automatic TRIM and all five sequential-read matrix cases passed. The mixed performance gate failed on Fast and Chip RAM. Physical Amiga and storage-device coverage remains outstanding.
