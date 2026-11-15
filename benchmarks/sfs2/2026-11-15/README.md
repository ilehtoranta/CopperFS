# SFS2 paired measurements, 2026-11-15

These JSON files are raw samples from the latest completed `append-codec-on-fast`
acceptance run. See [the report](../../../docs/sfs2-performance-2026-11-15.md)
for the comparison, configuration, limitations, and source/binary distinction.
They contain no executable, ROM, or disk-image data.

`512-fast-cycles.json`, `4096-fast-cycles.json`, and `65536-fast-cycles.json`
come from `Sfs2ThroughputTests.PacketThroughput`. `mixed-cycles.json` comes
from the paired original-handler performance test. Both arms execute the same
DOS probe, with three alternating trials. The original and candidate SHA256
hashes are embedded in every file. KiB means 1024 bytes.

For each operation, average `Cycles` separately for `original` and `candidate`.
Compute cycle change as `100 * (candidate / original - 1)`. Throughput is
`128 * 7093790 / meanCycles` KiB/s for the throughput probes. Do not average
the per-trial rates when reproducing the report. Both acceptance conditions
apply: at most 110% of original cycles and at least 90% of original throughput.

The measured candidate includes a local, restricted three-method CILica codec
rewrite beyond the normal handler pipeline. The later source checkpoint has
additional changes and does not reproduce this HUNK through the normal build.
Licensed reference executables and ROMs remain external local inputs. This
measurement does not qualify physical Amiga storage or the Chip RAM cases.
