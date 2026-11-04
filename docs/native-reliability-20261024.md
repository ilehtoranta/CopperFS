# Native development-handler validation, 2026-10-24

The standalone handler's newer embedded CopperSFS core now includes the bitmap
undo fix and the applicable subsequent portable-core review fixes: admin-span
reservation and bitmap encoding, node-branch growth, extent bounds/pruning,
and parent-path handling. Indexed deletion was already supported there.
Buffered-write features were preserved. This is a port into the newer core
API, not a replacement of that checkout with the older portable source.

Both development HUNKs target **68000**. They are local artifacts; no existing
download was replaced and no release was published.

| Policy | Bytes | SHA-256 |
| --- | ---: | --- |
| Buffered | 115,692 | `E569023831916DB6FD13F83ACD29F3F92CFBE2829DEDD47D4B4B16447686EF06` |
| Strict | 115,480 | `C64D5D03CF488281636CF009BAD12155C32B1D510AF42511FC94DA4E2B709C51` |

Both policies and the native test host use managed core input SHA-256
`C394DB83BA87F3886372F4A4C077D836CDCE6DB2C3F455013AB9B7F36C6D64C0`.
Local evidence retains source, compiler and runtime hashes, managed input
hashes, TRX results and per-cut JSON traces. Maps, managed assemblies, licensed
ROMs, original handlers, disk images and private bridge source are not release
assets.

## Native crash sweep

Native guest programs use licensed Kickstart 3.1 on an emulated 68020 with
512 KiB chip RAM and 2 MiB fast RAM, `copperhdf.device`, and fresh clones of a
pinned, independently formatted 16 MiB SFS image. Original SmartFilesystem
1.279 supplies the reference runs. Each cut loses pending device writes and
reboots with the same writing handler.

| Operation | CopperSFS buffered | CopperSFS strict | Reference per run |
| --- | ---: | ---: | ---: |
| Create/write | 26 | 35 | 15 |
| Overwrite | 15 | 15 | 9 |
| Truncate | 18 | 18 | 12 |
| Rename | 15 | 15 | 11 |
| Delete | 24 | 24 | 15 |
| Total | 98 | 107 | 62 |

All **329 write/flush cut/reboot cases** passed: 205 CopperSFS cases and 124
reference cases. No sampling limit was enabled. The trace audit requires
exactly one cut report for every write/flush event from the first write
through the final barrier in each operation/policy trace.

Pinned amidisk revision `a4faa7b36bd7c005371d9bb509a936a13d58b43f` checks
recovered images, deep structures, committed base-file data and interrupted
operation bytes independently of the CopperSFS reader. Native write/read and
the shared correctness workload with clean remount also pass.

Atomicity is per operation: create followed by Write may leave an absent,
empty or fully written file, corresponding to different completed DOS packets.

## A500 and TRIM

Each HUNK also passes the A500/68000 floppy replay under Kickstart 3.1 with
512 KiB chip RAM and 512 KiB slow RAM: startup/read, create/write/read,
fresh-boot remount, one captured dirty-transaction recovery, and concurrent
writer tasks with a reader. These four test methods are separate from the
full hardfile sweep.

The newer core includes manual TRIM. Additional native tests pass **47
buffered and 49 strict cut/reboot cases**, selecting every checkpoint
write/flush and the first, middle and last discarded sector of each request.
This is bounded discard-boundary sampling, not every discarded sector. The
hook irreversibly poisons discarded sectors. Query, dry-run, unsupported-device,
partial-failure, concurrent-reallocation and compiled command-line checks also
pass for both policies.

An initial TRIM test incorrectly rejected an empty file durably created before
its separate Write packet; all 20 affected strict cuts preceded any discard.
The corrected expectation accepts that valid state while still requiring the
full file after any discard. The command-line case needed its compiled utility
supplied. Initial failed results are retained separately from successful reruns;
these setup corrections did not change the handler binaries. The final builds
were subsequently rebuilt with the review fixes and qualified again.

## Host checks and limits

The embedded core's suite passes 237 tests, including six packed-file rollback
regressions; one optional external-image host test is not enabled. The 25 new
portable review regressions also pass against the embedded core through a
platform adapter and independent image checker.

The supplementary crash matrix's nearly-full case subsequently completed on
2026-10-24 against the exact frozen core DLL recorded above. The earlier run
had passed 53 of 54 cases before hitting its 65,536-state limit. That original
partial result is retained; its missing case now has a separate completed
receipt, closing the supplementary matrix's coverage gap.

The default limit is now 16,777,216 states per fault point, configurable with
`COPPERSFS_MAX_CRASH_STATES`. The completed case covers 600 fault executions
and 304,017,665 logical Cartesian combinations. The maximum at one fault point
is 3,899,392. Byte-exact domain containment reduces their union to 11 retained
spaces, all fully enumerated: **3,899,421 image validations passed**, without
sampling, in 536.45 seconds using eight workers. Every original fault point
has an explicit containment mapping with identical durable bytes and expected
manifests; hashes alone do not authorize containment. Seven coverage-helper
tests also pass. Recovery, full-file manifests, allocation consistency, guard
blocks and repeat recovery are checked for each enumerated image.

Local evidence is in `artifacts/native-reliability-20261024-current/stress-completion/`:
`completion-audit.json`, `result.json`, `plan.json`, the TRX results, source and
binary hashes, and frozen replay sources/project. Handler hashes are unchanged.

This completion applies to the recorded C394DB83 core input. The second
compatibility-review corrections described in [reliability testing](reliability.md)
are separate and still require an updated handler build and native qualification.
In particular, these recorded HUNKs retain the earlier admin-header value `2`;
the subsequent correction uses the reference implementation's `32`. The
nearly-full append stress case does not exercise new admin-container headers.

Native sector transfers are atomic and successful device flushes are trusted.
Torn sectors, lying flushes, arbitrary reordering and physical media faults are
not modeled. TRIM uses a simulated provider, not a physical SSD. Real hardware,
other ROM/device profiles and native partitions above 4 GiB remain unqualified.
Results apply to the exact development binaries and tested profiles.
