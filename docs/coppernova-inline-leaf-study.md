# CNP2 inline object leaf experiment

The v5 prototype in this study keeps v4's 122,759 user-object limit. Each 512-byte index leaf has 60 eight-byte entries. The first word records the private sector pair and its selected side. The second is zero for an external object image, or encodes a compact record. An empty file or directory records its kind and parent. A deleted slot can record its free-list link. Data extents, directory children, comments and retained file revisions can require an external image. The study used a v4 Amiga formatter. Preview.6 formats v5 and drops v3/v4 mount support.

A compact record inherits the leaf's checksum, identity and generation. Each commit writes changed external images and alternate leaves before the first durability barrier. The second barrier publishes the new root. Reads use the selected leaf. A later edit can expand the record into its private sector pair.

V5 still reserves a sector pair per new object. Its additional 1,023 index pairs reserve 2,046 more sectors before alignment. On the test partition the metadata floor grew by 1 MiB. The current experiment addresses I/O and CPU cost. An in-place upgrade and physical drive qualification are outside its coverage.

## Kickstart 2.04 namespace measurements

The three formats used the same native HUNK, 512 KiB Chip RAM, 2 MiB Fast RAM, and a 4 GiB + 64 MiB partition. The handler loaded 61,440 code bytes and used a 113,408-byte mount arena. Their combined 174,848 bytes fit within the previous candidate's 175,372 bytes. All three used the same emulated 68000 and DOS probe for each workload. The numbers are measured elapsed emulator cycles. Guest markers bracket each phase, including its explicit Flush. Boot time and diagnostic power-cut replays are excluded from the sums.

| Tree workload | v3 writes / cycles | v4 writes / cycles | Inline v5 writes / cycles |
|---|---:|---:|---:|
| Create 120 directories and 243 files, one with payload | 330 / 90,122,880 | 586 / 87,021,750 | 252 / 78,061,050 |
| Reopen and delete the 243 files, including Flush | 227 / 115,510,830 | 361 / 96,571,360 | 38 / 88,506,730 |
| Cold absence check | 0 / 37,118,110 | 0 / 29,930,840 | 0 / 28,650,780 |
| **Total** | **557 / 242,751,820** | **947 / 213,523,950** | **290 / 195,218,560** |
| Total with four payload bytes in every file | 1,067 / 287,340,200 | 1,421 / 259,732,050 | 1,000 / 248,231,430 |

V4 and v5 both issued 22 barriers during creation and 20 during deletion on the first tree. On the populated tree, v5 took 758 writes to create the files against v3's 675, then 242 to reopen and delete them against v3's 392.

## Growing beyond v3

| 3,125-file phase | v4 writes / cycles | Inline v5 writes / cycles |
|---|---:|---:|
| Create and Flush | 5,994 / 1,119,590,380 | 2,161 / 1,043,513,250 |
| Cold reopen, delete and Flush | 4,174 / 1,326,637,520 | 453 / 1,129,261,800 |
| Cold absence check | 0 / 482,061,800 | 0 / 488,499,450 |
| **Total** | **10,168 / 2,928,289,700** | **2,614 / 2,661,274,500** |

Each format issued 206 barriers during creation and 196 during deletion. On each volume a subsequent uncommitted addition was interrupted before each of its two barriers. Both cold checks retained the original 3,125 files and excluded the new name. TD64 reads and writes crossed 4 GiB. Managed coverage separately exercised allocating and reusing ID 122,760 at the end of the index.

## Sequential I/O

With a 68020 and settled Workbench, all three probes started at emulator cycle 426,275,280. The 2 MiB write through explicit Flush took 1,669,840 cycles on v3, 1,776,560 on v4 and 1,776,980 on v5. The I/O hook counted 4,106, 4,109 and 4,109 sector writes respectively, with two barriers each. The 420-cycle v5 difference from v4 is 0.024% in this run.

## Reproduction with the study build

Build the native handler with Build-NovaPrototype.ps1 and the CopperSharp compiler at f07e8ec99de4ddbdfad74cc2dbb4a9343e1e55b7. Build tools/CopperNova.Qualification in Release with NovaTreeComparison=true for the common 243-file test. Build it with both NovaTreeComparison=true and NovaTreePayload=true for the populated control. The default probe performs the 3,125-file test and crash cuts. Use a new output directory for each run.

~~~text
CopperNova.Qualification compare-object-growth <3|4|5> <handler> <Kickstart 2.04 ROM> <output>
CopperNova.Qualification workbench-compare <v3-mid|v4-mid|v5-mid> <handler> <ROM> <Workbench disk> <output>
~~~

The study’s large probe accepted v4 and v5. The streaming comparison used NOVA_WORKBENCH_SYNC=1, NOVA_WORKBENCH_TRIGGER_FRAME=3000 and NOVA_WORKBENCH_RELEASE_OFFSET=60000. The host formatter explicitly selects inlineLeaves for v5. The measured HUNK SHA-256 is 88E25445EB41822AD3788EAD4A3CA56CEB451F467779452017F9528E430E8E4D.
