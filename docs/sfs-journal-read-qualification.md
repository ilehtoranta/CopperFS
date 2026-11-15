# SFS journal reads and overwrite qualification

## Where the reads came from

The saved shared-pool handler issued 19 device reads in mixed mutation after mount. Its predecessor issued 14. Instruction traces put the difference in the TRST encoder, which needs the old contents of changed home sectors to build original-SFS compression records. The mount reads and foreground payload reads were the same. In the first commit, four one-sector commands fetched sectors 107–110. In the second, sectors 301–305 were interleaved with metadata in transaction order. The older encoder fetched aligned windows of eight blocks and copied neighbors into the LRU.

One experiment batched the known targets and put the neighbors back in the LRU. It recovered the five commands, but the instruction replay took 18,179,000 mixed-mutation cycles versus 18,143,800 for the saved handler. Its block-copy routine cost about 21,000 additional exclusive cycles. This experiment was discarded.

The revised encoder reads short consecutive runs into unused space at the end of the existing journal page buffer. Journal output grows from the front. When it reaches an input sector, it expires just that sector before clearing the page. The single-owner commit prevents home writes before publication, and the physical reader checks for logical overlays and overlapping queued writes. A short or failed bulk read discards the prefetch and retries the required sector alone. Single-page scratch and low-memory mounts retain their existing fallback.

## Where the overwrite time went

The isolated 96 KiB overwrite issued the same device commands on the two preceding handlers. Fresh scalar replays measured 892,780 and 912,750 cycles. The 19,970-cycle increase included 17,631 more Kickstart cycles. The interval from the start of the clean-marker write to the start of the final flush grew from 4,498 to 23,908 cycles. Time inside the nine traced low-level transfer calls increased only 38 cycles altogether. The trace locates the delay between those commands and does not identify a particular interrupt source.

Extent routing also copied verified internal nodes and leaves to scratch merely to identify a leaf. The relocation path now reads borrowed views and consumes the final leaf before another cache access can expire it. Callers that edit scratch request an owned read. The free-space scan resolves staged targets through the arena's slot accessor so it follows the shared pool's order vector as well as the original contiguous arena layout.

## Ordinary 68020 timing

Three alternating trials per arm used Kickstart 3.1, identical 16 MiB SFS media and 32 DOS buffers. The overwrite and mixed mutation phases include the explicit flush. Fast and Chip indicate the caller's buffer placement. Both Copper builds used the shared pool and whole-group relocation, with scalar replacement enabled and CILica inlining disabled.

| Mean cycles | Original SFS 1.279 | Saved CopperSFS | Revised CopperSFS |
|---|---:|---:|---:|
| Fast mixed mutation after mount | 16,994,607 | 17,797,227 | 17,761,363 |
| Chip mixed mutation after mount | 17,008,993 | 17,798,693 | 17,762,580 |
| Fast mixed verification | 20,072,780 | 20,674,790 | 20,684,123 |
| Chip mixed verification | 20,139,763 | 20,785,817 | 20,787,853 |
| Fast 96 KiB overwrite and flush | 347,713 | 912,743 | 872,973 |
| Chip 96 KiB overwrite and flush | 347,967 | 914,610 | 873,920 |
| Fast growth, overwrite and verification through commits | 5,781,660 | 6,515,223 | 6,497,980 |
| Chip growth, overwrite and verification through commits | 5,788,960 | 6,520,837 | 6,499,923 |

On each RAM class the revised handler issued 14 reads, 24 writes and eight flushes in mixed mutation, versus 19/24/8 for the saved candidate and 5/17/7 for original SFS. Both Copper builds transferred 168,448 read bytes and 147,456 write bytes over the complete mixed trace and produced byte-identical media. All Copper overwrite trials issued 17 writes and four flushes. The original issued 12 writes and one flush in two trials per RAM class and 15 writes and four flushes in the third.

Mixed verification increased by 0.045% Fast and 0.010% Chip versus the saved candidate. Cold mount also increased from 368,370 to 386,787 Fast cycles and from 368,480 to 386,540 Chip cycles. The independent gate, which includes mount in mutation, placed CopperSFS 3.632% above original for Fast mutation, 3.538% for Chip mutation, 3.041% for Fast verification and 3.243% for Chip verification. The 0.1% gate still fails.

Both Copper builds retained 126,008 bytes on mount and 138,888 bytes after the mixed workload. Original SFS retained 156,480 and 172,904 bytes. The revised M68000 HUNK grew by 744 bytes to 167,760, including 163,672 bytes of code. The revised binary SHA-256 is A2F5FC94158FFDB6117429ED68FC04BA7371B75B3EF1A8C42F555555BCB1B71B. The saved binary is A14A9472F958400AB610731AE06B6D6666575A7B9652F618492AEC06DACEC230.

## Recovery and compatibility

- **Host.** The Release solution built and recorded 2,295 test passes, eight conditional skips and zero failures. Three skips require external SFS/SFS2 fixtures and five are optional CPX native configurations. A scratch-backed view test caught an ownership violation during development. All four variants of that transition pass with the corrected mutable-read path.
- **Native.** The default and shared-pool M68000 handlers built. CopperStart passed 186 cache, transfer, geometry and buffer replays and 77 link and size replays. The latter set needs the SFS2-enabled source backend. Running it without that backend produced four SFS2 failures.
- **Kickstart 3.1.** The 12 grouped commit and recovery cases passed. Original SFS replayed 43 candidate sector prefixes across aligned and partial overwrites, including injected device-write and explicit-flush errors. The results contained wholly old or wholly new file data with intact neighbors and correct free counts. Both handlers recovered the preceding group after cuts inside three external journal runs, and the published group after cuts inside two home-write runs. A fault on sector 108 during the 107–110 read run produced a byte-identical final medium to the clean run.
- **Lifecycle.** The boundary trace, all 22 handoff, close, flush, timer, rejection, enumeration and capacity cases, all seven foreign/dirty startup cases and all five automatic-TRIM cases passed.
- **Sequential reads.** All five volume and placement cases passed. The original, saved and revised handlers issued 64 commands per 1 MiB cold or warm pass. Revised CopperSFS took 0.41–0.50% fewer cycles than original SFS across the 20 Fast/Chip, cold/warm comparisons. On the matched 16 MiB fixture both Copper handlers retained 156,560 bytes, within 80 bytes of the original's 156,480.
- **Hardware.** Physical Amigas and physical storage were not exercised.

For reproduction, the final manifest, handler, scalar profiles, individual trials and test logs are under `artifacts/sfs-io/release-candidate`. Set `COPPERFS_SFS_REFERENCE` to `artifacts/sfs-copy-reduction/final/candidate/CopperSFS`. The instrumented predecessor and saved control traces are under `artifacts/sfs-io/older` and `artifacts/sfs-io/saved`. The paired measurements use the ordinary frame runner. Only the diagnostic runs enable instruction profiling.
