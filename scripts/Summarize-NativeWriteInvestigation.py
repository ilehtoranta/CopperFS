"""Summarize opt-in compiled instruction profiles; never replace acceptance timing."""
import argparse
import hashlib
import json
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    root = args.directory
    paths = sorted((root / "profiles").glob("*.json"))
    paths += sorted(root.glob("*/cpu-profile-*.json"))
    profiles = []
    packets = []
    for path in paths:
        data = read(path)
        total = data["measuredCycles"]
        profiles.append({
            "path": str(path.relative_to(root)),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
            "handlerSha256": data["handlerSha256"],
            "mapSha256": data["mapSha256"],
            "cycles": total,
            "attributedCycles": data["attributedInstructionCycles"],
            "topMethods": [dict(row, percent=round(100 * row["cycles"] / total, 4))
                           for row in data["methods"][:20]],
        })
        checkpoints = data.get("checkpoints", [])
        assert len(checkpoints) % 2 == 0
        for i in range(0, len(checkpoints), 2):
            start, end = checkpoints[i:i + 2]
            assert start["label"] == f"write-{i // 2}-start"
            assert end["label"] == f"write-{i // 2}-end"
            before = {entry["name"]: entry["hits"] for entry in start["entryHits"]}
            delta = {entry["name"]: entry["hits"] - before.get(entry["name"], 0)
                     for entry in end["entryHits"]}
            methods = {}
            for name in ("AppendContiguousRun", "FileBlock", "AllocateData", "GrowExtentAt",
                         "InsertExtent", "BitmapPosition", "WriteDataBlocks"):
                methods[name] = sum(hits for symbol, hits in delta.items()
                                    if f"::{name}<" in symbol or symbol.endswith(f"::{name}"))
            packets.append({"profile": str(path.relative_to(root)), "packet": i // 2,
                            "cycles": end["cycle"] - start["cycle"],
                            "ms": round((end["cycle"] - start["cycle"]) / 7093.79, 4),
                            "entryHits": methods})
    trials = []
    for path in sorted(root.glob("*/diagnostic-trial.json")):
        trials.append({"path": str(path.relative_to(root)), **read(path)})
    result = {"schema": 1, "diagnosticOnly": True, "profiles": profiles,
              "packetEntries": packets, "trials": trials}
    output = root / "analysis.json"
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "profiles": len(profiles), "packets": packets}, indent=2))


if __name__ == "__main__":
    main()
