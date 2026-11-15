# Profiling the two SFS overwrite relocations

Twelve consecutive 8 KiB packets overwrite a file with two 48 KiB extents. In the Kickstart 3.1 replay, the first CopperSFS relocation starts a replacement extent while preserving the committed payload. Later packets can extend it using a validated continuation. The second relocation consumes the remaining blocks of the first source extent, joins the replacement to the surviving successor and establishes the next continuation. The instruction trace enters ExtentLeafView during prefix installation, proving the first source leaf is below the root. The boundary trace enters RemoveLeafRecord and RememberTrstRelocation. This explains why a specialization limited to a root leaf missed this workload.

| Inclusive guest cycles | Archived candidate | Revised | Saved |
|---|---:|---:|---:|
| First relocation | 84,162 | 70,835 | 13,327 |
| Boundary relocation | 90,513 | 80,691 | 9,822 |
| Both disjoint calls | 174,675 | 151,526 | 23,149 |

The initial allocation used to find a packet-sized hole and search its bitmap again to see whether the extent would fit. For this non-root case, the initial one-bitmap scan now measures up to the source extent length while requiring only the requested packet. A short first fit keeps the existing placement search. Only the current packet's blocks are allocated. Prefix installation also uses the insertion offset it already found.

At the boundary, the preferred free-run search already has current bitmap scratch, a verified durable bitmap, and the accepted checkpoint. It applies the existing reservation and staged-target exclusions, then checks the words covering the requested span in every applicable image. An exact fit completes the search. Any miss retains the ordinary intersection and scan. This took the boundary's FindTrstFreeRun from 19,823 to 14,233 cycles. The following direct write still checks durable and accepted allocation. The continuation now reuses the two scalar record positions adjusted for deletion, reacquires the leaf, checks record keys and offsets, and retains the end-of-extent routing and link checks. RememberTrstRelocation fell from 16,429 to 11,708 cycles. These inclusive timings overlap the relocation totals.

## Paired timing

Mean guest cycles across three alternating trials per arm, with Kickstart 3.1, an emulated 68020, identical 16 MiB media, 32 DOS buffers and controlled caller-buffer placement.

| Phase | Original SFS 1.279 | Archived CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast overwrite including explicit flush | 324,600 | 769,420 | 750,210 |
| Chip overwrite including explicit flush | 324,810 | 771,277 | 752,320 |
| Fast growth + overwrite + verify through commit | 5,767,857 | 6,375,623 | 6,384,117 |
| Chip growth + overwrite + verify through commit | 5,762,700 | 6,381,330 | 6,388,720 |
| Fast warm mixed mutation | 17,002,250 | 17,781,437 | 17,798,950 |
| Chip warm mixed mutation | 17,010,883 | 17,782,313 | 17,800,373 |

All isolated CopperSFS trials issued 17 writes and four flushes. Original issued 12 writes and one flush. Total overwrite improved 2.50% Fast and 2.46% Chip. The complete workload regressed 0.13% Fast and 0.12% Chip. In Fast RAM, growth took 8,597 more cycles and verification took 19,110 more. The mixed release gate exceeded original by 3.699%/2.918% for Fast mutation/verification and 3.538%/3.129% for Chip. Its maximum is 0.1%. The grouped profile remains opt-in.

Retained mount memory is 126,008 bytes at mount and 138,888 after mixed mutation for both CopperSFS binaries. The M68000 HUNK shrank from 168,564 to 168,536 bytes. Inlining was disabled and scalar replacement enabled. The changes require no additional per-mount storage.

The archived control is `artifacts/sfs-continued/verified-handler/CopperSFS` (SHA-256 E5D56A0C02A732E9D7AF1BB9F200CE158A2D8EAF6303EBFC54EBECC8E986EC90). The revised binary is `artifacts/sfs-relocation-cost/combined/CopperSFS` (SHA-256 26580C9FBBE39E0C51198C044A20DA4725EF8CB2B1DC7B8E0E293318A0AD1A6E). Per-call measurements are in the TransactionCalls arrays of the overwrite profiles under `artifacts/sfs-relocation-cost/control` and `artifacts/sfs-relocation-final`. The latter directory also contains the ordinary paired trials and qualification logs. Physical Amiga and storage devices were not exercised.

## Qualification

The Release solution built with one nullable-reference warning in the external CopperSharp compiler. The host suite passed 2,171 tests, with five conditional skips. Both default and shared-pool standalone M68000 builds succeeded, and all 263 CopperStart native replays passed.

On Kickstart 3.1 both handlers recovered the complete old or new 96 KiB payload at nine interrupted overwrite/commit boundaries. Original SFS replayed 43 candidate sector-prefix images, including write and flush failures, with whole-group contents, intact neighbors and correct allocation counts. Grouped pressure recovery, partial journal/home writes, retry after a partial journal read, flush/close/timer, handoff, foreign startup, automatic TRIM and all five sequential-read cases passed. The separate original-handler parity tests failed as reported above.
