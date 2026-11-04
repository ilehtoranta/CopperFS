"""Summarize the compiled-handler write benchmark; Python standard library only."""
import argparse
import json
import math
import statistics
from pathlib import Path


def summarize(source: Path, output: Path, trst_policy: str = "unspecified"):
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    assert data["passed"] and not data["failure"], "Only completed benchmarks may be summarized"
    trials = data["trials"]
    rows = data["rawTrials"]
    assert len(rows) == trials * 12
    clock = data["cpuCyclesPerSecond"]
    handlers = ["TRST", "CSUN-strict", "CSUN-buffered", "Original"]
    workloads = ["append-8k", "append-64k", "fragmented-overwrite-8k"]
    summaries = []
    for workload in workloads:
        for handler in handlers:
            group = [r for r in rows if r["Handler"] == handler and r["Workload"] == workload]
            assert sorted(r["Trial"] for r in group) == list(range(1, trials + 1))
            durations = [r["DurableCycles"] for r in group]
            latencies = sorted(c for r in group for c in r["WriteCallCycles"])
            assert all(c > 0 for c in durations + latencies)
            assert len(latencies) == trials * (16 if workload == "append-64k" else 128)
            for r in group:
                io = r["Io"]
                assert io["CsUnImages"] == io["CsUnDescriptors"]
                assert io["TrstPages"] + io["CsUnImages"] + io["CsUnDescriptors"] + io["MarkerWrites"] <= io["Writes"]
                assert [m["Tag"] for m in r["Memory"]] == [150, 151, 152, 153, 153, 154, 155]
            med = statistics.median
            ram = lambda r, index: r["Memory"][0]["FreeBytes"] - r["Memory"][index]["FreeBytes"]
            item = {
                "handler": handler,
                "workload": "existing-file-overwrite-8k" if workload == "fragmented-overwrite-8k" else workload,
                "sourceWorkload": workload, "trials": trials,
                "durableCyclesMedian": med(durations),
                "durableCyclesMin": min(durations), "durableCyclesMax": max(durations),
                "durableKiBps": 1024 * clock / med(durations),
                "writeServiceKiBps": 1024 * clock / med(sum(r["WriteCallCycles"]) for r in group),
                "writeP50ms": med(latencies) * 1000 / clock,
                "writeP95ms": latencies[math.ceil(.95 * len(latencies)) - 1] * 1000 / clock,
                "writeMaxMs": max(latencies) * 1000 / clock,
                "checkpointMs": med(r["CheckpointCycles"] for r in group) * 1000 / clock,
                "inhibitMs": med(r["InhibitCycles"] for r in group) * 1000 / clock,
                "reads": med(r["Io"]["Reads"] for r in group),
                "writes": med(r["Io"]["Writes"] for r in group),
                "writeKiB": med(r["Io"]["Writes"] for r in group) / 2,
                "journalWriteKiB": med(r["Io"]["TrstPages"] + r["Io"]["CsUnImages"] + r["Io"]["CsUnDescriptors"] for r in group) / 2,
                "journalReadKiB": med(r["Io"]["JournalReads"] for r in group) / 2,
                "markerHeaderWrites": med(r["Io"]["MarkerWrites"] for r in group),
                "flushes": med(r["Io"]["Flushes"] for r in group),
                "writeRequests": med(r["Io"]["WriteRequests"] for r in group),
                "mountedRamKiB": med(ram(r, 1) for r in group) / 1024,
                "firstWriteRamKiB": med(ram(r, 3) for r in group) / 1024,
                "maxSampledRamKiB": med(max(ram(r, i) for i in range(1, 6)) for r in group) / 1024,
                "afterInhibitRamKiB": med(ram(r, 6) for r in group) / 1024,
            }
            summaries.append(item)
    output.mkdir(parents=True, exist_ok=True)
    (output / "summary.json").write_text(json.dumps({"schema": 1, "passed": True, "trstPolicy": trst_policy, "medians": summaries,
        "handlers": data["handlers"], "source": str(source), "trialsPerCell": trials}, indent=2) + "\n", encoding="utf-8")
    lines = ["# Compiled native write-policy benchmark", "",
        f"{trials} cold trials per cell, {len(rows)} checked runs. Each writes 1 MiB; every output passed amidisk deep checking and exact-byte extraction.", "",
        "Rates and latencies use emulated PAL time (7,093,790 cycles/s), not host wall time. All binaries run on the same emulated 68020, 512 KiB chip + 512 KiB slow + 2 MiB fast RAM, 512-byte blocks and 32 buffers. The device adds no seek, rotation or flush delay. Boot and validation are excluded. Four independent machines run concurrently; timing comes from each guest's own cycle counter.", "",
        "Each fresh machine mounts and verifies the seed Hello file before the measured interval. Append workloads create a 1 MiB file. The overwrite workload replaces the first 1 MiB of the fixture's 3 MiB file; this range fits within its first extent. Independent extraction also checks that its remaining 2 MiB are unchanged. The raw workload tag retains the seeded file's name, Fragmented; this is an existing-file overwrite measurement. Payload byte i is (i * 17 + 5) & 255, so results concern this nonzero-word pattern rather than zero-filled or varied-compressibility data.", "",
        "## Throughput and Write latency", "",
        "Durable throughput includes file open, all writes, Close and successful Inhibit. Service throughput uses only the summed DOS.Write intervals. Latency p50/p95 pools every Write call across trials (nearest-rank p95); all other figures are medians across trials. Fixed marker overhead is included.", "",
        "| Workload | Handler | Durable KiB/s | Write service KiB/s | Write p50 ms | Write p95 ms | Write max ms | Final checkpoint ms |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for r in summaries:
        lines.append(f'| {r["workload"]} | {r["handler"]} | {r["durableKiBps"]:.2f} | {r["writeServiceKiBps"]:.2f} | {r["writeP50ms"]:.3f} | {r["writeP95ms"]:.3f} | {r["writeMaxMs"]:.3f} | {r["checkpointMs"]:.3f} |')
    lines += ["", "## Device and journal I/O per 1 MiB", "",
        "Journal writes include TRST pages or CSUN descriptors plus raw saved old-block images. They exclude transaction markers and final home writes, which remain included in total writes. Journal reads follow observed sector roles and valid journal headers, including stale headers examined by the allocator. Counts describe requested sector transfers, not a controller's eventual write amplification.", "",
        "| Workload | Handler | Total writes KiB | Journal writes KiB | Journal reads KiB | Total reads (sectors) | Flush commands | Write requests |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for r in summaries:
        lines.append(f'| {r["workload"]} | {r["handler"]} | {r["writeKiB"]:.1f} | {r["journalWriteKiB"]:.1f} | {r["journalReadKiB"]:.1f} | {r["reads"]:g} | {r["flushes"]:g} | {r["writeRequests"]:g} |')
    lines += ["", "## Guest memory", "",
        "KiB consumed relative to Exec.AvailMem(Any) immediately before loading the handler. This includes HUNK/code, process stack, DOS mount records, caches and handler buffers. Common probe buffers are already allocated at baseline. Samples are taken mounted, opened, after first/final Write, closed and inhibited. Maximum sampled retention is not an allocation high-water mark and can miss transient allocations. Inhibit does not unload the handler process/code.", "",
        "| Workload | Handler | Mounted KiB | After first Write KiB | Maximum sampled KiB | After Inhibit KiB |",
        "| --- | --- | ---: | ---: | ---: | ---: |"]
    for r in summaries:
        lines.append(f'| {r["workload"]} | {r["handler"]} | {r["mountedRamKiB"]:.2f} | {r["firstWriteRamKiB"]:.2f} | {r["maxSampledRamKiB"]:.2f} | {r["afterInhibitRamKiB"]:.2f} |')
    trst_contract = {
        "strict": "This TRST build makes each successful Write packet durable.",
        "buffered": "This TRST build acknowledges whole Writes before durability. Its accepted group becomes durable on successful filesystem ACTION_FLUSH, writer Close, Inhibit or timer commit; a crash before commit may lose acknowledged Writes.",
        "unspecified": "The TRST build's durability policy is unspecified in this report; consult its hash-matched qualification before interpreting Write-return latency.",
    }[trst_policy]
    lines += ["", "## Binary identity and interpretation", "",
        "These results apply to the exact binaries listed below. Source snapshots and crash qualification must be matched to those hashes; this report does not infer qualification or ABI versions from handler names. Copper binaries target 68000; original SFS 1.279 requires 68020. All use identical guest hardware and filesystem fixtures.", "",
        trst_contract + " Strict CSUN makes successful Write packets durable. Buffered CSUN and original SFS can acknowledge pending writes. The successful final Inhibit brings all four to a clean checkpoint. TRST preserves whole-write atomicity through its qualified commit protocol; original SFS's metadata journal has different data guarantees.", "",
        "| Handler | HUNK bytes | SHA-256 |", "| --- | ---: | --- |"]
    for h in data["handlers"]:
        lines.append(f'| {h["Name"]} | {h["bytes"]} | `{h["sha256"]}` |')
    (output / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return summaries


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("measurements", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--trst-policy", choices=("strict", "buffered", "unspecified"), default="unspecified",
                        help="Policy of the hash-matched TRST build; never inferred from its benchmark label")
    args = parser.parse_args()
    summarize(args.measurements, args.output, args.trst_policy)
