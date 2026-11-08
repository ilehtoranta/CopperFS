"""Summarize checked native read trials; uses only the Python standard library."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
from statistics import median


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("artifacts", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("--read-ahead", action="store_true", help="Check the device-request regression is eliminated")
    parser.add_argument("--previous", type=Path, help="Earlier benchmark artifacts for a three-way comparison")
    args = parser.parse_args()
    root = args.artifacts.resolve()
    manifest = json.loads((root / "benchmark.json").read_text(encoding="utf-8-sig"))
    trials = json.loads((root / "measurements.json").read_text(encoding="utf-8-sig"))
    sources = json.loads((root / "source-manifest.json").read_text(encoding="utf-8-sig"))
    source_sets = {v: {s["Path"]: s["SHA256"] for s in sources if s["Variant"] == v} for v in ("before", "after")}
    changed = {p for p in source_sets["before"].keys() | source_sets["after"].keys()
               if source_sets["before"].get(p) != source_sets["after"].get(p)}
    expected_changes = {
        "src/CopperFFS.Core/AmigaDiskFileSystemCore.cs",
        "src/CopperSFS.Core/AmigaSmartFileSystemReadOnlyCore.cs",
        "src/CopperFS.Amiga/NativeAmigaFileSystem.cs",
        "src/CopperFS.Amiga/NativeSfsExtension.cs",
        "src/CopperFS.Amiga/NativeSfs2Bridge.cs",
        "src/CopperFS.Amiga/NativeSfs2Bridge.Disabled.cs",
        "src/CopperFS.Amiga/NativeFileSystemReadCore.cs",
    }
    assert changed == expected_changes, f"Unexpected source differences: {changed ^ expected_changes}"
    expected = len(manifest["Pairs"]) * len(manifest["CpuModels"]) * len(manifest["Workloads"]) * manifest["Trials"] * 2
    assert len(trials) == expected, (len(trials), expected)
    for pair in manifest["Pairs"]:
        for workload in manifest["Workloads"]:
            for variant in ("before", "after"):
                io_counts = {(t["DeviceRequests"], t["DataRequests"], t["MetadataRequests"], t["DeviceBytes"])
                             for t in trials if (t["Profile"], t["PacketBytes"], t["Variant"])
                             == (pair["Name"], workload["PacketBytes"], variant)}
                assert len(io_counts) == 1, "I/O counts differ between CPU models"
    rows = []
    for cpu in manifest["CpuModels"]:
        for pair in manifest["Pairs"]:
            for workload in manifest["Workloads"]:
                groups = {}
                for variant in ("before", "after"):
                    group = [t for t in trials if (t["Profile"], t["Cpu"], t["PacketBytes"], t["Variant"])
                             == (pair["Name"], cpu, workload["PacketBytes"], variant)]
                    assert len(group) == manifest["Trials"]
                    for field in ("Cycles", "Instructions", "DeviceRequests", "DataRequests", "MetadataRequests",
                                  "DeviceBytes", "ContentSha256", "HandlerSha256", "FirstPacketCycles"):
                        assert len({t[field] for t in group}) == 1, (pair["Name"], cpu, field)
                    assert sha256(Path(pair[variant.capitalize()])) == group[0]["HandlerSha256"]
                    groups[variant] = group
                before, after = groups["before"][0], groups["after"][0]
                assert before["ContentSha256"] == after["ContentSha256"]
                row = {"Profile": pair["Name"], "Cpu": cpu, "PacketBytes": workload["PacketBytes"],
                       "ReadBytes": workload["ReadBytes"], "CpuSpeedup": before["Cycles"] / after["Cycles"]}
                for variant in ("before", "after"):
                    first = groups[variant][0]
                    for field in ("Cycles", "Instructions", "FirstPacketCycles", "DeviceRequests", "DataRequests", "MetadataRequests", "DeviceBytes"):
                        row[variant + field] = first[field]
                    times = [t["HostExecutionMilliseconds"] for t in groups[variant]]
                    row[variant + "HostMedianMs"] = median(times)
                    row[variant + "HostMinMs"] = min(times)
                    row[variant + "HostMaxMs"] = max(times)
                rows.append(row)
    if args.read_ahead:
        for row in rows:
            assert row["afterDeviceRequests"] <= row["beforeDeviceRequests"], (row["Profile"], row["PacketBytes"], "device requests regressed")
    previous_rows = None
    if args.previous:
        previous_root = args.previous.resolve()
        previous_rows = json.loads((previous_root / "summary.json").read_text(encoding="utf-8-sig"))
        previous_manifest = json.loads((previous_root / "benchmark.json").read_text(encoding="utf-8-sig"))
        for pair in manifest["Pairs"]:
            old_pair = next(p for p in previous_manifest["Pairs"] if p["Name"] == pair["Name"])
            assert sha256(Path(pair["Before"])) == sha256(Path(old_pair["Before"])), "Original baseline binaries differ"
        for row in rows:
            old = next(r for r in previous_rows if (r["Profile"], r["Cpu"], r["PacketBytes"])
                       == (row["Profile"], row["Cpu"], row["PacketBytes"]))
            for field in ("beforeCycles", "beforeInstructions", "beforeDeviceRequests", "beforeDeviceBytes"):
                assert row[field] == old[field], (row["Profile"], row["Cpu"], field, "baseline changed")
    (root / "summary.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    with (root / "summary.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)

    title = "Native sequential reads with read-ahead" if args.read_ahead else "Native sequential-read benchmark"
    lines = [f"# {title} — 5 November 2026", "",
             "This measures the actual standalone HUNK handlers with CopperStart's instruction replay bus and Copper68k 1.5.4. "
             "The numbers are emulated filesystem CPU cycles with synchronous Exec/device service doubles. "
             "They exclude real device latency, real Exec/device-task execution, Amiga bus contention and DMA timing. "
             "CPU speedup is therefore not an end-to-end disk-throughput measurement.", "",
             f"All {len(trials)} timed trials passed packet result, position, bounds and full-stream SHA-256 checks. "
             "Each configuration was repeated three times in alternating before/after order; cycles, instructions and I/O counts were identical across repetitions. "
             "Each trial starts with a fresh handle and empty filesystem caches; code warm-up uses separate fixtures.", "",
             "## Configuration", "",
             "- Same frozen compiler and compiler flags for both variants; MC68000 HUNKs executed on both CPU models.",
             "- One 1 MiB physically contiguous file on a synthetic 4 MiB volume, with 512-byte filesystem/device blocks. "
             "FFS uses 29 pointer tables; SFS2 uses one extent.",
             "- 64 KiB device MaxTransfer, a 64 KiB read-ahead window, DMA-safe aligned caller buffer, "
             "and the native mount defaults of 16 FFS / 32 SFS2 extra cache entries.",
             "- 64-byte packets read 64 KiB; 512-byte packets read 256 KiB; 4 KiB and 64 KiB packets each read 1 MiB. "
             "Compare before/after within a row; packet sizes do not all process the same total byte count.",
             "- Reads start on an already open handle. Mount, pathname lookup, open/close and writes are excluded. "
             "Direct handler packet dispatch is included; client packet transport and DOS buffering are not. "
             "The cache and traversal state remain live across consecutive packets within a trial.", "",
             "The fixture installs mounted geometry and an open native file record directly. The synthetic image contains the "
             "metadata needed by the read paths; it is not bootable media or a full filesystem-format qualification.", "",
             "## Guest CPU cycles", ""]
    for cpu in manifest["CpuModels"]:
        lines += [f"### {cpu}", "", "| Handler | Packet | Read total | Before cycles | After cycles | CPU speedup |", "|---|---:|---:|---:|---:|---:|"]
        for r in rows:
            if r["Cpu"] == cpu:
                lines.append(f'| {r["Profile"]} | {r["PacketBytes"]:,} B | {r["ReadBytes"] // 1024:,} KiB | '
                             f'{r["beforeCycles"]:,} | {r["afterCycles"]:,} | {r["CpuSpeedup"]:.2f}× |')
        lines += [""]
    lines += ["## Device requests and transferred bytes", "",
              "These counts are identical on both CPU models. Metadata requests include read-ahead; "
              "data requests count any device request overlapping the file's payload. "
              "Device bytes include every requested byte, including speculative and repeated reads.", "",
              "| Handler | Packet | All requests before → after | Data requests before → after | Metadata requests before → after | Device KiB before → after |",
              "|---|---:|---:|---:|---:|---:|"]
    for r in rows:
        if r["Cpu"] == "M68000":
            lines.append(f'| {r["Profile"]} | {r["PacketBytes"]:,} B | {r["beforeDeviceRequests"]:,} → {r["afterDeviceRequests"]:,} | '
                         f'{r["beforeDataRequests"]:,} → {r["afterDataRequests"]:,} | {r["beforeMetadataRequests"]:,} → {r["afterMetadataRequests"]:,} | '
                         f'{r["beforeDeviceBytes"] / 1024:,.0f} → {r["afterDeviceBytes"] / 1024:,.0f} |')
    if args.read_ahead:
        lines += ["", "FFS and SFS2 transfers up to 4 KiB now use the existing physical read-ahead cache when its window is larger than the request. "
                  "Larger transfers remain direct. Pending writes and redo overlays retain their existing precedence. "
                  "A failed speculative refill retries only the requested suffix through the existing direct-transfer path. "
                  "No cache allocation or native record growth was added. Device requests do not exceed the original baseline in any measured row.", ""]
    else:
        lines += ["", "The direct-read path bypasses the existing block read-ahead window for aligned whole-block packets. "
                  "This can increase device requests even while CPU work falls: small partial-block reads continue to use the cache, "
                  "and large packets amortize direct I/O. Actual storage latency and Exec/device overhead must be measured before translating these CPU ratios into disk speedups.", ""]
    if previous_rows is not None:
        lines += ["## Comparison with the first sequential-read optimization", "",
                  "The original baseline binaries and their measured cycles/I/O match the preceding run exactly. "
                  "The middle column below is the preceding direct-read implementation; the final column includes this read-ahead fix. "
                  "I/O counts apply to both CPU models; cycle comparisons below use M68000.", "",
                  "| Handler | Packet | Requests: original → direct → fixed | Cycles: direct → fixed | CPU gain vs original, fixed |",
                  "|---|---:|---:|---:|---:|"]
        for row in rows:
            if row["Cpu"] != "M68000":
                continue
            old = next(r for r in previous_rows if (r["Profile"], r["Cpu"], r["PacketBytes"])
                       == (row["Profile"], row["Cpu"], row["PacketBytes"]))
            lines.append(f'| {row["Profile"]} | {row["PacketBytes"]:,} B | {row["beforeDeviceRequests"]:,} → '
                         f'{old["afterDeviceRequests"]:,} → {row["afterDeviceRequests"]:,} | '
                         f'{old["afterCycles"]:,} → {row["afterCycles"]:,} | {row["CpuSpeedup"]:.2f}× |')
        lines += ["", "Caching small reads adds CPU copies compared with the direct-only version, while eliminating its small-packet I/O regression. "
                  "The fixed reader retains the traversal improvements and the large-read batching gains. "
                  "Device latency and real Exec/device overhead remain outside this CPU benchmark.", ""]
    lines += [
              "## First packet and host execution diagnostics", "",
              "The first-packet cycles expose initialization cost. Host time is the median of three instruction-execution-loop timings, "
              "excluding fixture setup and correctness checks but including host service doubles. It is affected by host load/JIT/GC and is not native throughput.", "",
              "| CPU | Handler | Packet | First-packet cycles before → after | Host median ms before → after |",
              "|---|---|---:|---:|---:|"]
    for r in rows:
        lines.append(f'| {r["Cpu"]} | {r["Profile"]} | {r["PacketBytes"]:,} B | {r["beforeFirstPacketCycles"]:,} → {r["afterFirstPacketCycles"]:,} | '
                     f'{r["beforeHostMedianMs"]:,.2f} → {r["afterHostMedianMs"]:,.2f} |')
    lines += ["", "## Provenance and reproduction", "",
              f'Baseline reader revision: `{manifest["BaselineCommit"]}`. Only the six modified reader/adapter source files were '
              "restored from this revision, and the new native cursor adapter was omitted. All other working-tree production sources, "
              "including the unrelated write-allocation changes, are identical in the before/after snapshots. The source-manifest comparison checks this boundary.", "",
              "All six handlers compiled as native-compatible with zero managed allocation sites. Handler SHA-256:", "",
              "| Handler | Variant | Bytes | SHA-256 |", "|---|---|---:|---|"]
    for pair in manifest["Pairs"]:
        for variant in ("before", "after"):
            binary = Path(pair[variant.capitalize()])
            compatibility = json.loads((binary.parent / "compatibility.json").read_text(encoding="utf-8-sig"))
            assert compatibility["IsCompatible"] and not compatibility["ManagedAllocationSites"]
            lines.append(f'| {pair["Name"]} | {variant} | {binary.stat().st_size:,} | `{sha256(binary)}` |')
    lines += ["", "The ignored artifact directory contains raw trials (`measurements.json`), summary JSON/CSV, source and compiler manifests, "
              "compile commands, compatibility reports, maps and HUNKs. No ROM or media is needed for this instruction benchmark.", "",
              "```powershell", "./tools/Benchmark-SequentialReads.ps1 -BaselineRef " + manifest["BaselineCommit"] + f" -ArtifactRoot artifacts/{root.name}",
              f"$env:COPPERFS_READ_BENCH_MANIFEST = (Resolve-Path artifacts/{root.name}/benchmark.json).Path",
              "dotnet test tests/CopperFS.Native.Tests/CopperFS.Native.Tests.csproj -c Release --filter 'FullyQualifiedName~SequentialReadBenchmarks'",
              f"& (uv python find --offline) tools/Summarize-SequentialReads.py artifacts/{root.name} docs/{args.report.name}"
              + (" --read-ahead" if args.read_ahead else "")
              + (f" --previous artifacts/{args.previous.name}" if args.previous else ""), "```", "",
              "Use a fresh `-ArtifactRoot` when rebuilding; existing snapshots are not overwritten. "
              "The build script freezes the compiler and rejects SDK/compiler metadata drift between input builds. "
              "The native test project uses CopperStart's MIT-licensed replay bus by source reference; standalone handlers retain their CopperFS/CopperSharp-only build boundary.", "",
              "Host semantic tests, native instruction replays and full-ROM/media tests are separate coverage. "
              "This benchmark adds no full-ROM, real storage or physical Amiga performance qualification.", ""]
    validation_path = root / "validation.json"
    if validation_path.exists():
        validation = json.loads(validation_path.read_text(encoding="utf-8-sig"))
        lines += ["## Correctness coverage for this source", "",
                  f'- Host: Release build passed; {validation["HostPassed"]:,} tests passed; '
                  f'{validation["HostMediaSkipped"]} optional media tests skipped.',
                  f'- Native: {validation["NativeHandlerReplaysPassed"]} actual-handler packet replays, '
                  f'{validation["CopperStartBufferProbesPassed"]} CopperStart buffer/cache probes, and '
                  f'{validation["TimedNativeTrialsPassed"]} data-checked timed trials passed.',
                  f'- Full-ROM/OS correctness: {validation["Sfs2KickstartCorrectnessReplaysPassed"]} SFS2 Kickstart '
                  'CSUN/TRST sparse-storage and cold-reboot replays passed. These do not measure storage throughput.',
                  '- Not exercised: ' + ', '.join(validation["Unexercised"]) + '.', ""]
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {len(rows)} comparisons from {len(trials)} checked trials to {args.report}")


if __name__ == "__main__":
    main()
