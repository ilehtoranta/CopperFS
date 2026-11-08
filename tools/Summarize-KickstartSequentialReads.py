"""Validate and summarize actual Kickstart DOS read trials (standard library only)."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
from statistics import median
import xml.etree.ElementTree as ET


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def test_counts(paths, name_prefix=None):
    counts = {"passed": 0, "notExecuted": 0, "failed": 0}
    for path in paths:
        tree = ET.parse(path)
        # The xUnit TRX adapter can leave Counters.notExecuted at zero while
        # emitting skipped UnitTestResult rows. Count the actual outcomes.
        for result in tree.findall(".//{*}Results/{*}UnitTestResult"):
            if name_prefix and not result.get("testName", "").startswith(name_prefix):
                continue
            key = {"Passed": "passed", "NotExecuted": "notExecuted"}.get(result.get("outcome"), "failed")
            counts[key] += 1
    return counts


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("artifacts", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("--date", default="5 November 2026", help="Date label for this measured run")
    args = parser.parse_args()
    root = args.artifacts.resolve()
    manifest = read_json(root / "benchmark.json")
    native_folder = Path(manifest["NativeManifest"]).parent
    try:
        native_folder = native_folder.relative_to(Path.cwd())
    except ValueError:
        pass
    fresh_artifacts = str(args.artifacts) + "-new"
    rerun = (f"./tools/Benchmark-KickstartSequentialReads.ps1 -NativeArtifactRoot '{native_folder}' "
             f"-ArtifactRoot '{fresh_artifacts}' -TestAssembly '<frozen Kickstart test DLL>'")
    if "PayloadCachePolicy" in manifest.get("Kickstart", {}):
        rerun += f" -PayloadCachePolicy {manifest['Kickstart']['PayloadCachePolicy']}"
    original = manifest.get("Comparison") == "original-handlers"
    left, right = ("Original (A)", "Copper (B)") if original else ("Before", "After")
    ratio = "B/A ratio" if original else "Speedup"
    cases = test_counts(root.glob("kickstart*.trx"), "CopperFS.Kickstart.Tests.SequentialReadThroughputTests.")
    assert cases == {"passed": 6, "notExecuted": 0, "failed": 0}, ("Incomplete or failed Kickstart cases", cases)
    payload = bytes((i * 17 + (i >> 9) * 31 + (i >> 17) * 7 + 3) & 255 for i in range(1048576))
    all_trials, rows, binaries = [], [], []
    rom_hashes = set()
    for pair in manifest["Pairs"]:
        profile = pair["Name"]
        variants = {}
        for variant in ("before", "after"):
            trials = read_json(root / "kickstart" / f"{profile}-{variant}.json")
            assert len(trials) == 12, (profile, variant, "Incomplete run", len(trials))
            handler = Path(pair[variant.capitalize()])
            if not original or variant == "after":
                compatibility = read_json(handler.parent / "compatibility.json")
                assert compatibility["IsCompatible"] and not compatibility["ManagedAllocationSites"]
            else:
                reference, = [r for r in manifest["ReferenceHandlers"] if r["Path"] == str(handler)]
                assert reference["Sha256"] == sha256(handler) and reference["Bytes"] == handler.stat().st_size
            media = root / "kickstart" / f"{profile}-{variant}" / "read.hdf"
            assert media.stat().st_size == 16777216
            probe = media.parent / "boot" / "PROBE"
            hashes = {"HandlerSha256": sha256(handler), "MediaSha256": sha256(media), "ProbeSha256": sha256(probe)}
            assert sha256(media.parent / "boot" / "HANDLER") == hashes["HandlerSha256"]
            for trial in trials:
                assert trial["Profile"] == profile and trial["Variant"] == variant
                if variant.capitalize() + "Control" in pair:
                    assert trial["MountControl"] == pair[variant.capitalize() + "Control"]
                for key, value in hashes.items():
                    assert trial[key] == value, (profile, variant, key)
                rom_hashes.add(trial["KickstartSha256"])
                expected = hashlib.sha256(payload[:trial["ReadBytes"]]).hexdigest().upper()
                assert trial["DataSha256"] == expected
                assert trial["SessionSeconds"] >= trial["ReadSeconds"] > 0
                assert abs(trial["ReadSeconds"] * 7093790 - trial["ReadBusCycles"]) < 0.001
                assert abs(trial["ReadKiBPerSecond"] * trial["ReadSeconds"] * 1024 - trial["ReadBytes"]) < 0.001
                assert abs(trial["SessionKiBPerSecond"] * trial["SessionSeconds"] * 1024 - trial["ReadBytes"]) < 0.001
                assert 0x200000 <= trial["BufferAddress"] < 0x300000 and trial["BufferAddress"] % 2 == 0
            variants[variant] = trials
            all_trials.extend(trials)
            binaries.append({"Profile": profile, "Variant": variant, "Bytes": handler.stat().st_size, **hashes})
        for field in ("MediaSha256", "ProbeSha256"):
            assert variants["before"][0][field] == variants["after"][0][field], (profile, field)
        for workload in manifest["Workloads"]:
            row = {"Profile": profile, **workload}
            for variant in ("before", "after"):
                group = [t for t in variants[variant] if t["PacketBytes"] == workload["PacketBytes"]]
                assert {t["Trial"] for t in group} == {0, 1, 2} and len(group) == 3
                assert {t["ReadBytes"] for t in group} == {workload["ReadBytes"]}
                for field in ("ReadKiBPerSecond", "SessionKiBPerSecond", "ReadSeconds", "SessionSeconds",
                              "ReadRequests", "DeviceReadBytes"):
                    values = [t[field] for t in group]
                    row[variant + field] = median(values)
                    row[variant + field + "Min"] = min(values)
                    row[variant + field + "Max"] = max(values)
            row["ReadSpeedup"] = row["afterReadKiBPerSecond"] / row["beforeReadKiBPerSecond"]
            row["SessionSpeedup"] = row["afterSessionKiBPerSecond"] / row["beforeSessionKiBPerSecond"]
            rows.append(row)
    assert len(rom_hashes) == 1
    if "Kickstart" in manifest:
        assert manifest["Kickstart"]["RomSha256"] == next(iter(rom_hashes))
    (root / "measurements.json").write_text(json.dumps(all_trials, indent=2) + "\n", encoding="utf-8")
    (root / "summary.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    with (root / "summary.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    lines = [
        "# " + ("Original-handler A/B: Kickstart sequential reads" if original else "Kickstart sequential read throughput") + " — " + args.date, "",
        "All 72 trials passed through a booted Kickstart 3.1 system: a native guest application calls "
        "DOS.Open/Read/Close, DOS sends packets to the loaded standalone handler, and the handler uses "
        "guest Exec and copperhdf.device. There are no Exec/DOS packet service doubles in this benchmark. " +
        ("A is the original Amiga FastFileSystem or SmartFilesystem binary, loaded unmodified; B is the optimized Copper handler. "
         "This is a fresh A/B run, distinct from comparing Copper versions before and after the optimization."
         if original else "Before and after are the frozen Copper handler binaries identified in the manifest and hash table below."), "",
        "These are end-to-end **virtual Kickstart** measurements. CopperHDF copies sectors synchronously "
        "from host storage without modelling physical seek, rotation or transfer latency in the guest clock. "
        "The numbers qualify this emulator/backend configuration, not physical Amiga disk throughput.", "",
        "## Configuration and timing", "",
        "- CopperMod.Amiga.Lightweight, M68020, PAL guest bus clock 7,093,790 Hz, 512 KiB Chip RAM, 2 MiB Fast RAM, no Slow RAM. "
        "Copper handlers and the probe use MC68000 HUNK code; the 1 MiB caller buffer is allocated in Fast RAM before timing.",
        "- Licensed Kickstart 3.1 ROM, SHA-256 `" + next(iter(rom_hashes)) + "`. No ROM is copied into the repository or artifacts.",
        "- Original synthetic, fully formatted 16 MiB FFS DOS1 / SFS2 volumes, 512-byte blocks, one 1 MiB file physically contiguous "
        "at LBA 8,192–10,239. Before and after use byte-identical, read-only images. FFS retains its pointer tables; SFS2 has one extent.",
        "- Same 64 KiB MaxTransfer and mount environment on both arms: 32 DOS buffers and 128 sectors per track. "
        "Copper retains its 64 KiB cache window and native extra-cache defaults of 16 entries for FFS / 32 for SFS2. " +
        f"Copper payload policy: {manifest.get('Kickstart', {}).get('PayloadCachePolicy', 'Cached')}; partial-sector and metadata caching remain enabled. " +
        ("Original handlers retain their own internal cache/read-ahead policies; cache memory budgets are not equalized." if original else ""),
        "- Same workloads as before: 64-byte reads consume 64 KiB; 512-byte reads consume 256 KiB; 4 KiB and 64 KiB reads consume 1 MiB each. "
        "Compare before/after within each row.",
        "- Three trials per workload and binary. Inhibit/uninhibit remounts the volume before each open to reset filesystem caches. "
        "Traversal and caches stay live between consecutive reads. Guest boot, handler loading and cache reset are excluded.",
        "- Read-loop time spans consecutive real DOS.Read calls, including guest packet transport, scheduling and device dispatch. "
        "Session time additionally includes pathname lookup, DOS.Open and DOS.Close. Timings come from parallel-port markers on the guest bus clock; "
        "KiB/s = bytes / 1,024 / guest seconds. Host elapsed time is diagnostic only. Tiny marker/loop overhead is included consistently.",
        "- Every returned byte is SHA-256 checked at the read-end marker without advancing guest time; each case asserts zero write requests "
        "and unchanged backing media. Host assertions and hashing do not contribute guest cycles. This benchmark does not measure EOF, fragmented files or writes.", "",
        "## Read-loop throughput", "",
        "Medians of three trials, KiB/s. Ranges below retain the observed guest scheduling/timing variation. " +
        ("B/A below 1 means Copper is slower than the original. The same original SFS binary is independently repeated for both Copper policy builds." if original else ""), "",
        f"| Handler | Read size | Read total | {left} KiB/s | {right} KiB/s | {ratio} |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for row in rows:
        lines.append(f"| {row['Profile']} | {row['PacketBytes']:,} B | {row['ReadBytes'] // 1024:,} KiB | "
                     f"{row['beforeReadKiBPerSecond']:,.3f} | {row['afterReadKiBPerSecond']:,.3f} | {row['ReadSpeedup']:.2f}× |")
    lines += ["", "## Open/read/close throughput", "",
              f"| Handler | Read size | {left} KiB/s | {right} KiB/s | {ratio} |",
              "|---|---:|---:|---:|---:|"]
    for row in rows:
        lines.append(f"| {row['Profile']} | {row['PacketBytes']:,} B | {row['beforeSessionKiBPerSecond']:,.3f} | "
                     f"{row['afterSessionKiBPerSecond']:,.3f} | {row['SessionSpeedup']:.2f}× |")
    if original:
        conclusion = ("The original handlers are faster in every measured row. "
                      if all(r["ReadSpeedup"] < 1 and r["SessionSpeedup"] < 1 for r in rows)
                      else "The tables show the measured original-versus-Copper throughput ratios. ")
        lines += ["", conclusion + "Results qualify the recorded complete binaries and payload policy in this virtual Kickstart configuration. "
                  "The large SFS2 session gap also includes Copper's higher open/close cost: compare the separate read-loop table. "
                  "Fewer device requests in some Copper rows do not translate directly into higher throughput here, because the backend "
                  "does not charge physical device latency to the guest clock."]
    lines += ["", "## Read-loop trial ranges and device traffic", "",
              "Device counts span read-loop entry through close, include metadata/read-ahead and exclude open/remount. "
              "Counts are shown as min–max when repetitions differ. Device KiB includes speculative and repeated bytes.", "",
              f"| Handler | Read size | {left} KiB/s range | {right} KiB/s range | Requests A → B | Device KiB A → B |",
              "|---|---:|---:|---:|---:|---:|"]
    def span(row, variant, field, divisor=1):
        lower, upper = row[variant + field + "Min"] / divisor, row[variant + field + "Max"] / divisor
        return f"{lower:,.0f}" if lower == upper else f"{lower:,.0f}–{upper:,.0f}"
    for row in rows:
        lines.append(f"| {row['Profile']} | {row['PacketBytes']:,} B | "
                     f"{row['beforeReadKiBPerSecondMin']:,.3f}–{row['beforeReadKiBPerSecondMax']:,.3f} | "
                     f"{row['afterReadKiBPerSecondMin']:,.3f}–{row['afterReadKiBPerSecondMax']:,.3f} | "
                     f"{span(row, 'before', 'ReadRequests')} → {span(row, 'after', 'ReadRequests')} | "
                     f"{span(row, 'before', 'DeviceReadBytes', 1024)} → {span(row, 'after', 'DeviceReadBytes', 1024)} |")
    if not original:
        lines += ["", "This full-ROM fixture exposes a remaining FFS small-read traffic increase: 64-byte reads use 3 → 4 requests, "
              "and 512-byte reads use 18 → 22. SFS2 small-read request counts are unchanged. The earlier instruction-only fixture "
              "had different mount/open/cache history and did not expose this FFS increase. Read-ahead remains active and the large "
              "one-request-per-packet regression is absent, but the previous no-request-regression result does not generalize to this "
              "Kickstart workload. These measurements reuse the frozen binaries without changing their cache policy."]
    lines += ["", "## Binary and fixture provenance", ""]
    if original:
        lines += ["A loads the supplied original handler binaries unmodified. Versions are extracted from their binary identifiers. "
                  "The original SmartFilesystem runs on the SFS2 version-4 image in this test, with the complete returned payload verified. "
                  "No reference binary, ROM or media is committed.", "", "| Original identifier | Bytes | SHA-256 |", "|---|---:|---|"]
        for reference in manifest["ReferenceHandlers"]:
            version = reference["Version"].split("(c)")[0].strip()
            lines.append(f"| {version} | {reference['Bytes']:,} | `{reference['Sha256']}` |")
        receipt = root / "reference-provenance.json"
        if receipt.exists():
            provenance = read_json(receipt)
            reference_hashes = {r["Sha256"] for r in manifest["ReferenceHandlers"]}
            assert provenance["Ffs"]["HandlerSha256"] in reference_hashes
            assert provenance["Sfs2"]["HandlerSha256"] in reference_hashes
            assert provenance["Sfs2"]["ArchiveSha256"] == provenance["Sfs2"]["ExpectedArchiveSha256"]
            ffs, sfs = provenance["Ffs"], provenance["Sfs2"]
            lines += ["", "Local reference source receipts:", "",
                      f"- FFS: `{Path(ffs['SourceArchive']).name}`, member `{ffs['ArchiveMember']}`, file `{ffs['Path']}`. "
                      f"Archive SHA-256 `{ffs['ArchiveSha256']}`; ADF SHA-256 `{ffs['ImageSha256']}`. "
                      "Extraction follows the original file's pointer tables and checks each traversed metadata checksum.",
                      f"- SFS2: `{sfs['Archive']}`, member `{sfs['Member']}`. Archive SHA-256 `{sfs['ArchiveSha256']}` "
                      "matches the pre-existing original-handler reference archive pin.",
                      "- The ignored `reference-provenance.json` retains these receipts."]
        lines += ["", "B uses the frozen Copper HUNKs named by `NativeManifest` and the hash table below. "
                  "The [read-ahead instruction benchmark](sequential-read-readahead-2026-11-05.md) documents the preceding reader work; "
                  "the hashes here identify this run's tested binaries. "
                  "The three Copper binaries have native-compatible reports with zero managed allocation sites. "
                  "No allocation behavior is claimed for the original handlers. The benchmark does not modify the filesystem implementation. "
                  "The same guest probe is used on both arms; its explicit Exec buffer/startup allocation occurs before timing.", ""]
    else:
        lines += ["Uses the frozen before/after HUNKs identified by `NativeManifest` and the hash table below. "
              "Recorded baseline lineage: `" + manifest["BaselineCommit"] + "`. Results compare these complete binaries; "
              "source/compiler snapshots may contain other changes and do not establish isolated per-change speedups. "
              "All six handlers have native-compatible compiler reports with zero managed allocation sites; "
              "the guest probe also passes the allocation-site check. Its explicit Exec buffer/startup allocation occurs before timing. "
              "No filesystem read scratch or cache allocation was added.", ""]
    if original:
        lines += ["Raw JSON retains the historical variant labels: `before` = original A; `after` = Copper B.", ""]
    lines += ["| Handler | Variant | Bytes | SHA-256 |", "|---|---|---:|---|"]
    for binary in binaries:
        lines.append(f"| {binary['Profile']} | {binary['Variant']} | {binary['Bytes']:,} | `{binary['HandlerSha256']}` |")
    lines += ["", "Before/after probe and media hashes are checked equal for each profile:", "",
              "| Handler | Media SHA-256 | Probe SHA-256 |", "|---|---|---|"]
    for binary in binaries[::2]:
        lines.append(f"| {binary['Profile']} | `{binary['MediaSha256']}` | `{binary['ProbeSha256']}` |")
    lines += ["", "## Reproduction", "",
              "The optional test project stays outside the normal solution because it needs a licensed ROM and the sibling CopperScreen emulator. "
              "Standalone handler dependencies are unchanged. Fixtures source-link this repository's original synthetic formatter/checker; "
              "the probe's mount sequence follows CopperStart's MIT-licensed NativeSfsAbProbe.", "",
              "```powershell",
              "# First build/freeze the handlers identified by NativeManifest above.",
              "$env:COPPERSHARP_KICKSTART31_ROM = '<licensed Kickstart 3.1 ROM path>'",
              rerun + (" -OriginalFfsHandler '<FastFileSystem path>' -OriginalSfs2Handler '<SmartFilesystem path>'" if original else ""),
              f"& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py '{fresh_artifacts}' '{args.report}' --date '{args.date}'",
              "```", "",
              "Raw per-case JSON, boot media, checked data images and guest HUNKs are under the ignored artifact directory. "
              "`measurements.json`, `summary.json` and `summary.csv` retain all 72 trials and medians/ranges. "
              "`environment.json` records the host SDK and Copper assembly hashes. " +
              "Both arms use identical emulator and guest-probe builds. " +
              "Only guest-clock throughput is compared; host execution overlapped and is not a performance result.", "",
              "## Coverage", ""]
    host = test_counts((root / "host-results").glob("*.trx"))
    if host["passed"]:
        assert not host["failed"]
        lines.append(f"- Host: Release solution build passed; {host['passed']:,} tests passed; {host['notExecuted']} optional tests skipped.")
    else:
        lines.append("- Host: run and report the normal solution build/tests separately.")
    lines += [
        ("- Native: the three optimized, allocation-checked Copper handlers were reused byte-for-byte alongside the two unmodified original binaries. "
         if original else "- Native: six previously built, allocation-checked standalone handlers reused byte-for-byte. ") +
        "The earlier instruction replay coverage is documented in the linked read-ahead report; it is not counted as a new run here.",
        "- Full-ROM/OS: six Kickstart benchmark cases passed, covering 72 data-checked sequential-read trials through real guest DOS "
        "for FFS and both SFS2 write-policy builds. This includes booting the benchmark floppy and mounting each filesystem.",
        "- Unavailable/not exercised: physical Amiga/disk throughput, rotational/device latency, replacement-ROM boot and a full hardware/media matrix.", "",
    ]
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text("\n".join(lines), encoding="utf-8")
    print(f"Validated {len(all_trials)} trials; wrote {args.report} and {root / 'summary.csv'}")


if __name__ == "__main__":
    main()
