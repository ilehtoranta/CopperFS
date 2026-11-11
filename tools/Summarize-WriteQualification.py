"""Validate paired native write trials and their fresh-boot verification."""
import argparse
import hashlib
import json
from pathlib import Path
from statistics import median


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("ffs_sfs_results", type=Path)
    parser.add_argument("sfs2_results", type=Path)
    parser.add_argument("baseline_handlers", type=Path)
    parser.add_argument("candidate_handlers", type=Path)
    parser.add_argument("report", type=Path)
    args = parser.parse_args()
    lines = ["# Paired native write qualification", "",
             "Three fresh-boot trials per arm; the measured interval includes writes, close and inhibit/flush. "
             "A second boot verifies the changed bytes, an unchanged region and final file size. "
             "Buffer addresses, handler and media hashes, probe and ROM hashes, and device command counts are checked.", "",
             "| Profile | Workload | Baseline cycles | Candidate cycles | Change |",
             "|---|---|---:|---:|---:|"]
    roms = set()
    for profile in ("ffs", "sfs", "sfs2-trst", "sfs2-classic"):
        root = args.sfs2_results if profile.startswith("sfs2-") else args.ffs_sfs_results
        name = "CopperFFS" if profile == "ffs" else "CopperSFS"
        handler_profile = "sfs2-trst" if profile == "sfs2-classic" else profile
        handlers = {"baseline": args.baseline_handlers / handler_profile / name,
                    "candidate": args.candidate_handlers / handler_profile / name}
        hashes = {arm: digest(path) for arm, path in handlers.items()}
        for workload in ("overwrite", "append", "bulk-append"):
            case = root / profile / workload
            rows = read(case / "write-results.json")
            assert len(rows) == 6, (case, "expected six trials")
            assert {(r["Arm"], r["Trial"]) for r in rows} == {(a, t) for a in hashes for t in range(3)}
            assert len({r["BufferAddress"] for r in rows}) == 1
            address = rows[0]["BufferAddress"]
            assert address and address % 65536 == 0
            assert len({r["InitialSha256"] for r in rows}) == 1
            assert len({(r["ReadCommands"], r["WriteCommands"], r["Flushes"]) for r in rows}) == 1
            probes = set()
            for row in rows:
                arm, trial = row["Arm"], row["Trial"]
                assert row["BusCycles"] > 0 and row["WriteCommands"] > 0
                assert row["HandlerSha256"] == hashes[arm]
                label = f"{arm}-{trial}"
                result = read(case / label / "result.json")
                verify = read(case / (label + "-verify") / "result.json")
                assert result["Passed"] and verify["Passed"]
                assert result["Stage"] == row["BufferAddress"]
                assert result["HandlerSha256"] == verify["HandlerSha256"] == hashes[arm]
                assert result["InitialSha256"] == row["InitialSha256"]
                assert result["ResultSha256"] == verify["InitialSha256"] == verify["ResultSha256"] == row["WrittenSha256"]
                assert digest(case / label / "disk.hdf") == digest(case / (label + "-verify") / "disk.hdf") == row["WrittenSha256"]
                probes.add(result["ProbeSha256"])
                roms.update((result["KickstartSha256"], verify["KickstartSha256"]))
            assert len(probes) == 1
            for trial in range(3):
                a, = [r for r in rows if r["Arm"] == "baseline" and r["Trial"] == trial]
                b, = [r for r in rows if r["Arm"] == "candidate" and r["Trial"] == trial]
                assert a["WrittenSha256"] == b["WrittenSha256"]
            before = median(r["BusCycles"] for r in rows if r["Arm"] == "baseline")
            after = median(r["BusCycles"] for r in rows if r["Arm"] == "candidate")
            lines.append(f"| {profile} | {workload} | {before:,} | {after:,} | {(after / before - 1) * 100:+.2f}% |")
    assert len(roms) == 1
    lines += ["", "The emulator uses a synchronous CopperHDF backend and a 68020. "
              "Negative percentages mean fewer bus cycles. This does not measure physical devices."]
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Validated 72 write trials and 72 fresh-boot verifications; wrote {args.report}")


if __name__ == "__main__":
    main()
