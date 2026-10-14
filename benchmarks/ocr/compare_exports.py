"""Compare identical-corpus source/released dependency exports without hiding failures."""
import os
import argparse
import hashlib
import json
from pathlib import Path


def compare(source, release):
    if source["Engine"] != release["Engine"] or source.get("PreprocessingProfile") != release.get("PreprocessingProfile"):
        raise ValueError("Engine/profile differ")
    def indexed(report):
        result = {}
        for row in report["Rows"]:
            if row["Id"] in result:
                raise ValueError("Duplicate image: " + row["Id"])
            result[row["Id"]] = row
        return result
    left, right = indexed(source), indexed(release)
    if left.keys() != right.keys():
        raise ValueError("Image sets differ")
    changed, same_success, both_failed = [], 0, 0
    for key, a in left.items():
        b = right[key]
        if a["ImageSha256"] != b["ImageSha256"] or a["GroundTruth"] != b["GroundTruth"]:
            raise ValueError("Source input/label differs for " + key)
        same = a["Prediction"] == b["Prediction"]
        failed_a, failed_b = a.get("Error") is not None, b.get("Error") is not None
        same_success += same and not failed_a and not failed_b
        both_failed += failed_a and failed_b
        if not same or failed_a != failed_b or a["Lines"] != b["Lines"]:
            changed.append({"id": key, "sameText": same, "sourceFailed": failed_a, "releaseFailed": failed_b,
                            "sourceLines": a["Lines"], "releaseLines": b["Lines"],
                            "sourceCer": a["CharacterErrorRate"], "releaseCer": b["CharacterErrorRate"]})
    def models(report):
        snapshots = report.get("ModelCacheSnapshotsByBatch", [report["ModelCacheSnapshot"]])
        result = {}
        for snapshot in snapshots:
            for asset in snapshot:
                name, checksum = asset["Name"], asset["Sha256"].lower()
                if name in result and result[name] != checksum:
                    raise ValueError("Model changed within run: " + name)
                result[name] = checksum
        return result
    a_models, b_models = models(source), models(release)
    common = a_models.keys() & b_models.keys()
    mismatched = sorted(name for name in common if a_models[name] != b_models[name])
    return {"engine": source["Engine"], "images": len(left), "identicalSuccessfulTexts": same_success,
            "bothFailedImages": both_failed, "sourceFailures": sum(r.get("Error") is not None for r in left.values()),
            "releaseFailures": sum(r.get("Error") is not None for r in right.values()), "changedRows": changed,
            "commonModelAssets": len(common), "mismatchedModelAssets": mismatched,
            "commonModelFingerprints": [{"name": name, "sourceSha256": a_models[name], "releaseSha256": b_models[name]}
                                        for name in sorted(common)],
            "sourceOnlyModelAssets": sorted(a_models.keys() - b_models.keys()),
            "releaseOnlyModelAssets": sorted(b_models.keys() - a_models.keys()),
            "sourceAssemblies": source["Assemblies"], "releaseAssemblies": release["Assemblies"],
            "limitations": ["Cache fingerprints cover all assets, including unused models; they do not identify the exact models used per image.",
                            "Successful means no recorded exception, not a correct transcription; parity is separate from accuracy.",
                            "Identical failed empty predictions are not counted as successful text parity.",
                            "This checks one corpus and execution configuration, not universal binary or OCR equivalence."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("release", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source, release = [json.loads(p.read_text(encoding="utf-8-sig")) for p in (args.source, args.release)]
    result = compare(source, release)
    result["reports"] = [{"name": p.name, "path": Path(os.path.relpath(p.resolve(), args.output.parent.resolve())).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in (args.source, args.release)]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
