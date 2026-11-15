# SFS2 commit overhead qualification

The opt-in, zero-extra-mount-RAM SFS2 TRST profile now uses **25 device flushes
instead of 28** in the mixed workload. Commit processing takes **13.3% fewer
cycles**; warm mutation takes **1.55% fewer cycles**. Original SFS still wins
the release performance gate. Neither owned SFS2 nor automatic TRIM is enabled
by default.

## Why 28 versus 7

A new packet/device trace runs the same workload through original SFS 1.279
and the frozen previous candidate. The original's seven flushes are **not one
flush for each of our seven commits**:

| Boundary | Previous CopperSFS | Original SFS |
|---|---:|---:|
| Before the first changed-file close | No commit | One inline commit, 3 flushes |
| Six changed-file closes | Six commits, 24 flushes | No commit at these closes |
| Final explicit filesystem flush | One commit, 4 flushes | One inline commit, 4 flushes |
| Total | Seven commits, 28 flushes | Two commits, 7 flushes |

Original SFS retains changes across these closes. CopperSFS guarantees a
durable commit before a changed-file close replies. Matching the original's
commit count by deferring those closes would change the requested semantics.
The additional platform flush after a completed transaction was already
elided; it was not responsible for the difference.

Each external TRST transaction orders four stages: data/journal durability,
TRFA publication, home-image durability, and TROK clean-marker durability.
These remain necessary in the existing external-log protocol.

## Implemented change

The owned profile now uses the single-sector TRST publication already used by
original SFS and our SFS implementation. Encoding starts in the existing
journal scratch buffer at the marker's address. If the entire log fits, it
can be published there directly. On overflow, the still-unwritten first page
is relocated to space proven free in both allocation views, and the existing
external-page/TRFA protocol continues. No second encoding pass or new buffer
is needed.

This shortcut requires a 512-byte filesystem block and a 512-byte device
sector. Larger/multi-sector blocks and the checked reference writer retain
external publication. Recovery recognizes inline TRST in both checked and
owned handlers, including the CSUN-policy build; the same complete validation
pass precedes replay. CSUN journal encoding and recovery ordering are unchanged.

Inline ordering is:

1. Flush any unpublished data.
2. Write the complete TRST marker sector and flush it.
3. Write home images and flush them.
4. Write TROK and flush it.

All four ordering stages remain. A metadata-only transaction with an already
clean device can omit the *device command* at stage one; the core still calls
the barrier. No home write can precede durable publication. A failed commit
still suspends the mount, including a failed final clean-marker flush.

Five of the seven workload journals fit inline. Two have preceding data and
still use four device flushes; three metadata-only commits use three. The
two external commits use four each: **25 commands total**. Five external
journal-sector writes disappear. Headers that fit in a journal page are
written as aligned fields rather than seven byte-stream calls. Split headers,
split literals and exact page endings retain the streaming path.

Changed-close, explicit flush, timer, inhibit, capacity-pressure retry,
accepted-packet rollback, and TRIM-after-durable-commit semantics are unchanged.
The packet trace asserts that every candidate close/final-flush reply has no
unflushed writes. Disk layouts and packet ABI are unchanged.

## Paired measurements

Three alternating runs per handler use identical 16 MiB images and DOS
operations on the emulated M68020. Payload placement is explicitly Fast or
Chip; handler and mount storage remain in Fast RAM. Mount is measured
separately. Mutation includes its final explicit commit. The frozen previous
candidate is the zero-extra-RAM profile qualified earlier on this date.

| Handler | Warm Fast mutation | Fast read/verify | Warm Chip mutation | Chip read/verify |
|---|---:|---:|---:|---:|
| Original SFS | 15,083,933 | 16,684,653 | 15,093,977 | 16,713,413 |
| Previous candidate | 19,487,250 | 19,302,397 | 19,488,610 | 19,381,823 |
| New candidate | 19,185,410 | 19,278,843 | 19,185,913 | 19,365,803 |

Warm Fast mutation improves 1.55%, but remains **27.2% slower than original**.
Mutation plus verification is 38.46M cycles versus the original's 31.77M.

| Warm mutation I/O | Original | Previous | New |
|---|---:|---:|---:|
| Device read commands | 4 | 34 | 33 |
| Device write commands | 17 | 51 | 47 |
| Device flush commands | 7 | 28 | 25 |
| Whole workload read bytes | 175,104 | 683,520 | 683,008 |
| Whole workload write bytes | 145,920 | 162,816 | 160,256 |

Device commands and 512-byte sector writes are distinct: the write combiner
can put multiple sectors in a command. The removed five sectors correspond
to four fewer commands in this workload.

The unchanged cold-access release gate still fails in all four cases:

| Profile / payload placement | Candidate mutation | Original mutation | Candidate verify | Original verify |
|---|---:|---:|---:|---:|
| AutoTrim / Fast | 19,645,303 | 15,597,897 | 19,283,210 | 16,687,607 |
| AutoTrim / Chip | 19,647,467 | 15,595,600 | 19,369,883 | 16,723,197 |
| No AutoTrim / Fast | 19,573,277 | 15,587,903 | 19,269,733 | 16,687,297 |
| No AutoTrim / Chip | 19,574,093 | 15,616,600 | 19,350,740 | 16,718,933 |

The AutoTrim Fast gate is 25.95% slower for mutation and 15.55% slower for
verification. Disabling AutoTrim does not establish parity.

Matched-memory comparisons give original SFS 472 buffers: 412,264 bytes
after mutation versus 412,328 for both candidates. Fast mutation is 15,064,200
cycles for original, 19,487,157 for previous, and 19,185,413 for new. Both
Fast/Chip matched-memory runs pass their independent data checks.

Fragmented-volume diagnostics use one trial per arm and placement:

| Volume / payload placement | Original mutation | Previous mutation | New mutation |
|---|---:|---:|---:|
| 8 MiB / Fast | 15,261,090 | 19,823,770 | 19,567,480 |
| 8 MiB / Chip | 15,292,030 | 19,823,100 | 19,568,940 |
| 128 MiB / Fast | 15,292,970 | 20,362,870 | 20,099,070 |
| 128 MiB / Chip | 15,288,580 | 20,364,550 | 20,099,790 |

Fragmented read checks pass. Their cycle counts change by less than 0.001%
between candidates; these tests do not establish overall performance parity.

## Where commit cycles remain

Separate scalar instruction profiles use the same warm workload. Inclusive
rows are nested and must not be added together. Acceptance timing above uses
the normal frame runner.

| Inclusive routine | Previous cycles / calls | New cycles / calls |
|---|---:|---:|
| FlushSmart | 2,016,658 / 7 | 1,766,952 / 7 |
| CommitTrst | 1,904,867 / 7 | 1,651,070 / 7 |
| Compression planning | 311,159 / 42 | 306,455 / 42 |
| Journal space search | 168,652 / 12 | 71,188 / 7 |
| Byte stream helper | 171,471 / 341 | 146,120 / 47 |

Commit now consumes about 8.6% of the total warm mutation cycles. Removing
even all of that cost would not close the measured 4.10M-cycle gap with
original SFS. Physical writes, compression and seven durable boundaries remain
within commit; metadata copying/access remain significant outside it. Exclusive
copy cycles fall from 1,188,964 to 1,164,155, while owned ReadView costs 327,608,
hash lookup 286,026, and checksum scanning 223,392 cycles in the new profile.

The emulated device adds no real flush latency. Eighteen additional device
flushes versus original SFS can matter more on actual storage; these cycle
figures do not quantify that delay.

## RAM, code and deferred work

No native state field, mount allocation, cache slot or arena capacity was
added. Total measured mount allocation remains **412,328 bytes**, unchanged
through mutation and readback. Owned metadata remains 16,508 bytes with 29
clean-image slots; the transaction allocation remains 196,608 bytes.
Additional permanent mount storage above the established baseline remains zero.

This does not mean loaded code is free: the code hunk grows **652 bytes**,
from 218,636 to 219,288. The HUNK file grows from 223,724 to 225,416 bytes
(including relocation information). Fast cold mount changes from 467,690 to
468,670 cycles. The no-AutoTrim HUNK is 213,720 bytes, checked TRST 207,012,
and CSUN 249,196. CILica scalar replacement remains enabled and inlining disabled.

The 32 KiB deletion/automatic-TRIM diagnostic reports deferred work explicitly:

| Measurement | Previous | New |
|---|---:|---:|
| Foreground deletion | 296,140 | 306,150 |
| Tracking, already included in foreground | 1,482 | 1,490 |
| Deferred service | 139,819 | 139,366 |
| Foreground plus service | 435,959 | 445,516 |
| Range notifications / service turns / UNMAPs | 2 / 2 / 2 | 2 / 2 / 2 |
| Reclaimed bytes | 32,768 | 32,768 |

This sample's combined time **regresses 2.2%**. A follow-up full-delete
instruction profile measures handler-exclusive execution at 262,913 -> 260,793
cycles, while outside-handler execution rises 33,133 -> 45,069. Metadata
lookup/checksum call counts are unchanged. The observed end-to-end regression
is retained; it is not erased by the handler-only improvement. Service still
performs three reads, no clean-mount recovery, and one discard per turn.
There are no repeated capability queries during service. No reclamation cost
has been hidden outside foreground timing to declare parity.

## Qualification and scope

- **Host:** Release solution build passes (one existing CopperSharp compiler
  nullable warning). Release filesystem tests: **1,547 passed**, three external
  image tests skipped. The 65 unrelated prototype tests also pass and are
  excluded from the filesystem qualification count.
- Seven new focused host tests cover exact-full inline pages, overflow at a
  record boundary, split headers, split literals, multi-sector fallback, every
  write/flush cut and injected failure, arbitrary pending-write persistence
  subsets, and data durability before inline publication. Existing accepted
  rollback, grouped growth/overwrite/reallocation, and failed-final-barrier
  recovery tests also pass. An initial new test used a write beyond EOF; its
  fixture was corrected to an aligned append before qualification.
- **Native:** owned AutoTrim, owned no-AutoTrim, checked TRST and CSUN standalone
  handler builds pass. CopperStart native replay: **193 passed**. Compiled owned
  allocation/fallback/recovery checks: **12 passed**.
- **Full ROM:** **46 correctness cases passed**: 18 mutation/recovery/group/TRIM,
  18 foreign-startup/SFS/manual-TRIM/order checks, two owned journal handoffs,
  four checked TRST/CSUN handoffs, three no-AutoTrim boundary/pressure checks,
  and the packet/device commit trace. Recovery includes original SFS, cuts and
  interrupted replay, failed/partial discard, inhibit/remount, timer and flush.
- Eight paired performance cases, CPU profiles, and before/after maintenance
  diagnostics pass their data checks. **Four release performance assertions
  fail**, as reported above. No default enablement follows from correctness.
- **Physical devices:** not tested. ROM/emulator and host persistence-subset
  tests are not physical 68020/drive power-loss qualification. Inline publication
  relies on the same atomic 512-byte sector and reliable-flush contract already
  used for the SFS path.

## Reproduction and receipts

Local evidence is under `artifacts/commit-tuning/` (ignored build/test outputs):
`before/commit-trace`, `qualification/commit-trace`, `qualification/owned-performance`,
`qualification/original-performance*`, `before/owned-cpu-profile`,
`qualification/owned-cpu-profile`, `maintenance*`, `delete-*`, the qualification
manifests and the individual test logs. ROM and disk images are not committed.

The native profile is built with:

```powershell
./build/Build-Handler.ps1 -Sfs2 -OwnedSfs2 -AutoTrim -Sfs2ExtraKiB 0 -SkipCompilerBuild -ArtifactRoot artifacts/commit-tuning/inline
dotnet build CopperFS.slnx -c Release --artifacts-path artifacts/commit-tuning/release
dotnet test CopperFS.slnx -c Release --no-build --artifacts-path artifacts/commit-tuning/release
```

Native replay uses `CopperStartNativeSfs2=true` and the geometry, size and native
filesystem-buffer instruction suites. ROM tests use the qualification manifest,
licensed local Kickstart, `COPPERFS_SFS2_OWNED=1`, `COPPERFS_SFS2_GROUPED=1`,
and the candidate/original paths. Paired comparisons pin the previous handler
through `COPPERFS_GROUPED_REFERENCE` and its SHA-256 override; matched-memory
runs set `COPPERFS_MATCH_ORIGINAL_BUFFERS=472`. Fragmented diagnostics set
`COPPERFS_OWNED_FRAGMENTED=1`, `COPPERFS_OWNED_TRIALS=1` and volume MiB 8/128.
`COPPERFS_TRIM_DELETE_PROFILE=1` adds the full-delete diagnostic interval to
`COPPERFS_TRIM_PROFILE=1`.

SHA-256 receipts:

| Binary | SHA-256 |
|---|---|
| Previous candidate | `CB7FE660E5A55B6BBB361517E6D85D550CC1CBB41597E9A2DFC4F34EEB633CC3` |
| New owned AutoTrim | `2F0A591DEDBC205DCEFFFDDFEAD88A671D0811443FB86879E6C780D958872ECA` |
| New owned no-AutoTrim | `56F4542360D67587C49672B386A3994774FCA087E6690C41FA8351F2A56773DC` |
| New checked TRST | `16A1A4AAC119C52A17AB067814E9AA450713A488B0BD974B58F8FE4020D338FF` |
| New CSUN | `81F1E80AE6639C470485743C1B2C05B8B161F2C5A1D2DAEB0AF57D1BE2493522` |
| Original SFS 1.279 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
