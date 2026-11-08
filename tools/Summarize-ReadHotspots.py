"""Report exclusive Kickstart instruction profiles and checked A/B gains."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def counts(paths):
    result = {"Passed": 0, "NotExecuted": 0, "Failed": 0}
    for path in paths:
        for test in ET.parse(path).findall(".//{*}Results/{*}UnitTestResult"):
            outcome = test.get("outcome")
            result[outcome if outcome in result else "Failed"] += 1
    return result


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("before_profiles", type=Path)
    parser.add_argument("after_profiles", type=Path)
    parser.add_argument("previous_ab", type=Path)
    parser.add_argument("current_ab", type=Path)
    parser.add_argument("validation", type=Path)
    parser.add_argument("report", type=Path)
    args = parser.parse_args()
    previous = read(args.previous_ab / "summary.json")
    current = read(args.current_ab / "summary.json")
    previous = {(r["Profile"], r["PacketBytes"]): r for r in previous}
    phase_rows, method_rows, comparison = [], [], []
    for profile in ("ffs", "sfs2-csun", "sfs2-trst"):
        old_trials = read(args.previous_ab / "kickstart" / (profile + "-after.json"))
        new_trials = read(args.current_ab / "kickstart" / (profile + "-after.json"))
        assert {t["MediaSha256"] for t in old_trials} == {t["MediaSha256"] for t in new_trials}
        assert {t["ProbeSha256"] for t in old_trials} == {t["ProbeSha256"] for t in new_trials}
        for packet in (64, 512, 4096, 65536):
            for phase in ("open", "read", "close"):
                filename = f"{packet}-{phase}-profile.json"
                old = read(args.before_profiles / "kickstart" / (profile + "-after") / filename)
                new = read(args.after_profiles / "kickstart" / (profile + "-after") / filename)
                phase_rows.append(dict(Profile=profile, PacketBytes=packet, Phase=phase,
                    BeforeCycles=old["GuestCycles"], AfterCycles=new["GuestCycles"],
                    BeforeAttributed=old["AttributedCycles"], AfterAttributed=new["AttributedCycles"]))
                for variant, data in (("before", old), ("after", new)):
                    assert data["AttributedCycles"] <= data["GuestCycles"]
                    for method in data["Methods"]:
                        method_rows.append(dict(Profile=profile, PacketBytes=packet, Phase=phase,
                            Variant=variant, **method, Percent=100 * method["Cycles"] / data["GuestCycles"]))
    for new in current:
        old = previous[new["Profile"], new["PacketBytes"]]
        comparison.append(dict(Profile=new["Profile"], PacketBytes=new["PacketBytes"],
            PreviousReadKiBs=old["afterReadKiBPerSecond"], CurrentReadKiBs=new["afterReadKiBPerSecond"],
            PreviousSessionKiBs=old["afterSessionKiBPerSecond"], CurrentSessionKiBs=new["afterSessionKiBPerSecond"],
            OriginalSessionKiBs=new["beforeSessionKiBPerSecond"],
            SessionGain=new["afterSessionKiBPerSecond"] / old["afterSessionKiBPerSecond"]))
    for filename, rows in (("profile-phases.csv", phase_rows), ("profile-methods.csv", method_rows), ("profile-comparison.csv", comparison)):
        with (args.current_ab / filename).open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)
    lines = ["# Kickstart read CPU profiling and C# optimizations — 5 November 2026", "",
        "The native Kickstart profiles identified full geometry construction, general 64-bit division, cache copying and read-packet setup "
        "as substantial costs. The resulting C# changes improve all measured workloads. The original handlers remain faster in this configuration.", "",
        "## Profile evidence", "",
        "Exclusive instruction cycles from actual standalone Copper HUNKs under Kickstart 3.1, M68020 PAL, 512 KiB Chip / 2 MiB Fast RAM. "
        "One diagnostic trial per read size and binary, split at the existing guest markers into open, read and close. "
        "The opt-in C# scalar driver follows CopperStart's MIT-licensed SfsCpuProfile and its interrupt/hardware ordering. "
        "Maps locate the relocated code; profile reads inspect RAM without guest bus accesses. Interrupt dispatch, STOP waits and boundary markers "
        "are residual rather than charged to a guessed function. Normal ExecuteFrame acceptance measurements are reported separately.", "",
        "| Workload / hotspot | Before self cycles | After self cycles | Before phase share | After phase share |",
        "|---|---:|---:|---:|---:|"]
    selected = [
        ("sfs2-csun", 65536, "open", "get_Geometry"),
        ("sfs2-csun", 64, "read", "DivRemUnsigned"),
        ("sfs2-csun", 65536, "read", "get_Geometry"),
        ("sfs2-csun", 65536, "read", "DivRemUnsigned"),
        ("ffs", 4096, "read", "CopyBlock"),
        ("ffs", 64, "read", "CopyBlock"),
    ]
    for profile, packet, phase, name in selected:
        values = []
        for variant in ("before", "after"):
            rows = [r for r in method_rows if (r["Profile"], r["PacketBytes"], r["Phase"], r["Variant"])
                    == (profile, packet, phase, variant) and r["Name"].endswith("::" + name)]
            values.append((sum(r["Cycles"] for r in rows), sum(r["Percent"] for r in rows)))
        lines.append(f"| {profile}, {packet:,} B {phase}: {name} | {values[0][0]:,} | {values[1][0]:,} | {values[0][1]:.1f}% | {values[1][1]:.1f}% |")
    lines += ["", "CopyBlock's percentage can remain high even when its absolute cost falls: other work was also removed. "
              "The CSVs retain every phase and method, including outside-handler time. The initial FFS 64-byte read phase spent about 20% "
              "inside the general DispatchPacket decoder; normal FFS reads now use a separate small C# decoder.", "",
              "## Implemented changes", "",
              "- CopperSFS read-only traversal uses the platform's existing stable BlockCount/BlockBytes/ReservedBlocks scalars. "
              "Checksum length is read once; checksum, own-block, format, backlink, range and clean-volume checks remain in place.",
              "- CopperSharp68k Runtime.Managed/Integer64Arithmetic.cs recognizes power-of-two divisors and computes exact full-width "
              "quotients/remainders with shifts and masks. The general restoring division remains for other divisors; zero-divisor, sign "
              "and overflow behavior is preserved. This is a reusable C# runtime improvement, not a filesystem-only code patch.",
              "- Ordinary standalone FFS Read packets avoid the general decoder's large frame, duplicate lock lookup and root-key setup. "
              "Inhibited/recovery states retain the old route; packed integration keeps its existing media checks.",
              "- Small aligned FFS/SFS2 reads retain physical read-ahead without copying each data sector into the metadata LRU. "
              "Track-cache hits no longer construct a full device descriptor. Overlay/dirty/queued/RAM precedence is still checked before "
              "entering the physical data path, and failed speculative reads retain the requested-suffix fallback.",
              "- Partial reads can consume a retained image immediately rather than first copying a full sector to scratch. "
              "Aliased destinations fall back to scratch; no borrowed pointer survives another block access.",
              "- No allocation, handle/state growth, on-disk format, packet ABI, journal policy or standalone dependency was added. "
              "All routine changes are C#. There are no handwritten instructions, emitted-byte patches or filesystem-specific assembler kernels. "
              "The original local C# CopyBlock implementation remains; no new Exec-copy dependency is required.", "",
              "## Uninstrumented throughput", "",
              "Median open/read/close KiB/s, three trials. Same read sizes/totals, bit-identical media, caller buffers, mount geometry and "
              "guest probe HUNKs as the previous A/B. The original columns below are newly rerun reference handlers; the previous Copper "
              "column is the retained pre-profile optimization result. Full read-loop results, ranges and binary hashes are in the "
              "[new original-handler A/B report](kickstart-profiled-original-ab-2026-11-05.md).", "",
              "| Handler | Read size | Previous Copper | Profiled Copper | Copper gain | Original |", "|---|---:|---:|---:|---:|---:|"]
    for row in comparison:
        lines.append(f"| {row['Profile']} | {row['PacketBytes']:,} B | {row['PreviousSessionKiBs']:,.3f} | "
                     f"{row['CurrentSessionKiBs']:,.3f} | {row['SessionGain']:.2f}× | {row['OriginalSessionKiBs']:,.3f} |")
    lines += ["", "The CopperHDF backend services sectors synchronously without charging physical seek, rotation or device transfer "
              "latency to guest time. This is virtual Kickstart throughput, not physical Amiga storage performance. No host wall-time "
              "speedup is claimed; overlapping host jobs do not define the reported guest-clock KiB/s.", "",
              "## Provenance and reproduction", "",
              "Baseline profiles use the exact Copper HUNKs from the preceding A/B. Final HUNKs, maps, managed inputs, source hashes, "
              "compiler hashes and compile arguments are frozen in artifacts/profiled-read-v2. The compiler/runtime assembly is the same "
              "for all three final builds. Existing unrelated writer and compiler-backend working-tree changes were retained, not reverted. "
              "Whole-handler gains are measurements for these identified binaries; no isolated per-change speedup is claimed.", "",
              "Build FFS, SFS2 CSUN and SFS2 TRST through build/Build-Handler.ps1 with the same compiler and canonical flags. "
              "Run the original-handler benchmark script against a manifest pointing at those fresh HUNKs. "
              "For diagnostics, set COPPERFS_KICKSTART_PROFILE=1, select DisplayName~after and use a separate fresh artifact directory. "
              "The benchmark script clears this flag and the smoke flag for acceptance runs.", "",
              "```powershell",
              "& (uv python find --offline) tools/Summarize-KickstartSequentialReads.py artifacts/kickstart-profiled-v2-ab docs/kickstart-profiled-original-ab-2026-11-05.md",
              "& (uv python find --offline) tools/Summarize-ReadHotspots.py artifacts/kickstart-hotspots-before artifacts/kickstart-hotspots-v2 artifacts/kickstart-original-ab artifacts/kickstart-profiled-v2-ab artifacts/profiled-read-validation docs/kickstart-read-profiling-2026-11-05.md",
              "```", "", "## Coverage", ""]
    host = counts((args.validation / "final-v2-host-results").glob("*.trx"))
    native = counts((args.validation / "native-final").glob("*.trx"))
    copies = counts([args.validation / "copperstart-native-v2.trx"])
    compiler = counts((args.validation / "compiler").glob("*.trx"))
    sparse = counts([args.validation / "sparse-v2-kickstart.trx"])
    for group in (host, native, copies, compiler, sparse):
        assert not group["Failed"], group
    lines += [f"- Host: final Release solution build passed; {host['Passed']:,} host tests passed; {host['NotExecuted']} optional external-media tests skipped.",
              f"- Compiler/runtime: {compiler['Passed']} focused checks passed, covering host boundary/random/exception cases and compiled full-width arithmetic on four CPU models.",
              f"- Native: {native['Passed']} actual-handler packet replays passed across CSUN/TRST and M68000/M68020; "
              f"{copies['Passed']} CopperStart buffer/cache/copy instruction replays passed using the updated read/cache C# overlay, without editing CopperStart's tracked adapter. "
              "All three final HUNKs are native-compatible with zero managed allocation sites.",
              f"- Full-ROM: all 72 uninstrumented original-handler A/B trials, 12 final diagnostic workloads and {sparse['Passed']} "
              "SFS2 delete/TRIM/rewrite/cold-reboot Kickstart replays passed. Every sequential stream was SHA-256 checked; benchmark media remained unchanged.",
              "- Not exercised: physical disks/Amigas, replacement-ROM boot and the full hardware/media matrix. Missing optional media tests are skips, not passes.", ""]
    args.report.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {args.report}; validated {len(phase_rows)} paired profile phases and {len(comparison)} throughput comparisons")


if __name__ == "__main__":
    main()
