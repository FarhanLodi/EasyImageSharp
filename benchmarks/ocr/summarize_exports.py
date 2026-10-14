"""Aggregate corpus exports without dropping failed images or mixing tuning and evaluation."""
import os
import argparse
import hashlib
import json
from pathlib import Path


def summarize(manifest, report):
    inputs = {row["id"]: row for row in manifest["images"]}
    rows = report["Rows"]
    if len(rows) != len(inputs) or {row["Id"] for row in rows} != inputs.keys():
        raise ValueError("Export must contain exactly one observation per manifest image")
    groups = {}
    for row in rows:
        item = inputs[row["Id"]]
        if row["ImageSha256"].lower() != item["sha256"].lower():
            raise ValueError("Image fingerprint mismatch: " + row["Id"])
        for group in (item["split"], item["split"] + "/" + item["script"],
                      item["split"] + "/" + item["category"],
                      item["split"] + "/" + item["script"] + "/" + item["category"]):
            groups.setdefault(group, []).append(row)
    result = {}
    for group, values in sorted(groups.items()):
        characters = sum(len(row["GroundTruth"]) for row in values)
        words = sum(len(row["GroundTruth"].split()) for row in values)
        result[group] = {
            "images": len(values), "failed": sum(row.get("Error") is not None for row in values),
            "referenceCharacters": characters, "referenceWords": words,
            "cer": sum(row["CharacterErrorRate"] * len(row["GroundTruth"]) for row in values) / characters if characters else None,
            "wer": sum(row["WordErrorRate"] * len(row["GroundTruth"].split()) for row in values) / words if words else None,
            "exactMatches": sum(row["Prediction"] == row["GroundTruth"] for row in values),
        }
    return {"engine": report["Engine"], "profile": report.get("PreprocessingProfile"),
            "downloadsAllowed": report.get("ModelDownloadsAllowed", False),
            "textNormalization": report.get("TextNormalization"),
            "assemblies": report.get("Assemblies"),
            "modelCacheAssets": len(report.get("ModelCacheSnapshot", [])),
            "modelCacheSnapshotSha256": hashlib.sha256(json.dumps(report.get("ModelCacheSnapshot", []), sort_keys=True).encode()).hexdigest(),
            "sampledPeakWorkingSetBytes": report.get("SampledPeakWorkingSetBytes"),
            "memoryMeasuredBatches": report.get("MemoryMeasuredBatches", 0), "groups": result,
            "observations": [{"id": row["Id"], "split": inputs[row["Id"]]["split"],
                              "script": inputs[row["Id"]]["script"], "category": inputs[row["Id"]]["category"],
                              "referenceCharacters": len(row["GroundTruth"]), "referenceWords": len(row["GroundTruth"].split()),
                              "cer": row["CharacterErrorRate"], "wer": row["WordErrorRate"],
                              "exactMatch": row["Prediction"] == row["GroundTruth"], "lines": row["Lines"],
                              "failed": row.get("Error") is not None,
                              "errorType": row["Error"].split(":", 1)[0] if row.get("Error") else None} for row in rows]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("reports", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    reports = []
    for path in args.reports:
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        item = summarize(manifest, report)
        item.update(report=path.name, reportPath=Path(os.path.relpath(path.resolve(), args.output.parent.resolve())).as_posix(), reportSha256=hashlib.sha256(path.read_bytes()).hexdigest())
        reports.append(item)
    result = {"corpus": manifest["corpusId"], "revision": manifest["revision"],
              "manifestSha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              "limitations": manifest["limitations"] + ["Whitespace WER is not a language-neutral metric for scripts without spaces.",
                  "Source-page families prevent same-page variant leakage; shared book/source provenance and model training overlap remain unknown, so independence is not established.",
                  "Cold export timings are excluded from this quality report; failed images remain in denominators."],
              "runs": reports}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
