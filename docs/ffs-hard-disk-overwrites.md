# FFS hard-disk overwrites

CopperFFS can replace scattered DOS1 data blocks during an aligned overwrite wholly inside the file. It keeps standard FFS pointers. Trackdisk, removable media and unrecognized drivers use the in-place writer. DOS0 uses a separate, narrower [OFS overwrite policy](ofs-hard-disk-overwrites.md). The native mount currently recognizes `scsi.device`, `copperhdf.device` and `uaehf.device` when a complete TD_GETGEOMETRY reply identifies fixed direct-access media. This identifies the device interface. An emulated hardfile or a flash-backed controller can report the same class as a mechanical disk. The portable writer's original entry point defaults to in-place overwrites.

## Placement

The writer considers each affected pointer table once. For 8–31 complete blocks it requires at least four fewer breaks in physical continuity. For 32 or more it requires at least three fewer breaks. It charges a break for each contiguous connection it could lose to an untouched neighbor in the same table. For example, eight blocks in two runs stay in place. Eight singleton runs can qualify.

The source blocks for that table segment must share a bitmap page. The writer tries the space following the last old block and then the beginning of the page. Each probe examines at most the requested block count plus 128 positions, clipped at the valid end of the page. The replacement must fit as one run. Otherwise the existing overwrite proceeds, including on a full volume. Unaligned packets and packets extending EOF use the existing writer.

The writer flushes the invalid-root marker, allocates and writes the new run, and flushes before publishing its checksummed pointer table. It flushes the published table before freeing the old blocks and flushes the updated bitmap before allowing the root to become valid. It counts replacement bytes when their table has been written. A failed later table can leave an already published prefix. The existing three scratch blocks suffice.

## What justified the cutoff

The exploratory handler showed why write size matters alongside fragmentation. Relocating 16 KiB in four separated runs saved about 1.82 million bus cycles on the overwrite alone and reduced the next cold read from four commands to one. That result lowered the threshold for segments of at least 32 blocks. At the other end, relocating 2 KiB in two runs saved just 195,420 write cycles and 14,330 cycles per cold read while adding the same four flushes.

The hardfile completes storage commands synchronously without mechanical seek or rotational delays. One sensitivity check charges each additional flush 20 ms, holds all other measured costs fixed, and uses the emulated PAL bus clock of 7,093,790 Hz. Under that assumption the 2 KiB/two-run case needs 26 subsequent cold reads to recover the extra write cost. The 4 KiB/four-run case needs three, the 4 KiB/eight-run case needs two, and the 16 KiB/four-run case pays during the overwrite. This is a way to choose a conservative default for an unknown future read count. Physical hard disks can have different break-even points.

### Selected policy under Kickstart 3.1

Each pair below shows in-place followed by the selected policy. These are single deterministic DOS calls using an MC68000 handler on an emulated M68020.

| Overwrite and original layout | Overwrite bus cycles | Cold-read bus cycles | Cold-read commands | Extra flushes |
|---|---:|---:|---:|---:|
| 4 KiB, eight separated runs | 666,280 → 227,470 | 141,220 → 57,420 | 8 → 1 | 4 |
| 4 KiB, four separated runs | 666,390 → 667,640 | 100,500 → 100,670 | 4 → 4 | 0 |
| 16 KiB, four separated runs | 2,170,770 → 354,630 | 151,830 → 28,020 | 4 → 1 | 4 |
| 16 KiB, eight runs with single-sector gaps | 2,170,880 → 354,630 | 143,350 → 28,020 | 1 → 1 | 4 |
| 32 KiB, eight separated runs | 4,143,150 → 475,540 | 284,620 → 29,850 | 8 → 1 | 4 |
| 32 KiB, already contiguous | 4,144,080 → 4,146,390 | 29,780 → 29,850 | 1 → 1 | 0 |

The nearby case shows why counting pointer runs alone can overstate mechanical savings. Native read-ahead already covered all eight runs in a single device command. The measured improvement there came with the same cold-read command count.

The selected handler passed all 14 Kickstart cases across 52 boots. All four OFS/FFS and DD/HD floppy configurations retained their original pointers and passed fresh-boot readback after media export. An unrecognized fixed device also retained its pointers. The standalone MC68000 compatibility report recorded zero managed allocation sites, and six CopperStart overwrite instruction replays passed. The Release solution build passed with 1,255 host tests passing and three external SFS media cases skipped.

## Reproducing the comparison

Run `tools/Benchmark-FfsOverwrites.ps1 -ExploreCutoff` with `COPPERSHARP_KICKSTART31_ROM` set. The script snapshots the local filesystem sources and builds a baseline with relocation disabled, the production policy, and an exploratory handler that can relocate short writes with as little as one net break removed. Generated media and binaries go under `artifacts/`.

Each arm starts with identical independently scattered file pointers on a 16 MiB hardfile. A guest program opens the existing file and issues one aligned DOS overwrite. A fresh Kickstart 3.1 boot loads the same handler, issues one sequential DOS read and checks every byte. Measurement markers bracket each DOS call and exclude mounting, opening, verification and boot time. The fixture checks the resulting physical runs separately. The harness records emulated bus cycles, device read/write commands and flushes for each phase in `measurements.json`. The default separated runs start 257 sectors apart. The nearby case leaves a single sector between runs.

For the host block-I/O comparison, run `dotnet test tests/CopperFFS.Core.Tests -c Release --filter FullyQualifiedName~BenchmarkFragmentedOverwriteAndColdSequentialRead --logger "console;verbosity=detailed"`.
