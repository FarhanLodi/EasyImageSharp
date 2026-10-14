"""Build the separate orientation manifest from the manually audited source labels."""
import hashlib
import json
from pathlib import Path
import sys


def main():
    original = Path(sys.argv[1])
    audit_path = Path(sys.argv[2])
    destination = Path(sys.argv[3])
    manifest = json.loads(original.read_text(encoding="utf-8-sig"))
    audit = json.loads(audit_path.read_text(encoding="utf-8"))
    source_hash = hashlib.sha256(original.read_bytes()).hexdigest()
    if source_hash != audit["originalManifestSha256"]:
        raise ValueError("Original manifest differs from the visually audited source.")
    labels = {p["id"]: p for p in audit["pages"]}
    pages = []
    excluded = []
    for page in manifest["images"]:
        if page["category"] != "digital":
            continue
        label = labels[page["id"]]
        actual = hashlib.sha256((original.parent / page["path"]).read_bytes()).hexdigest()
        if actual != label["sha256"]:
            raise ValueError(f"Audited image changed: {page['id']}")
        if label["singleOrientationCalibrationEligible"]:
            if label["pageUprightCorrectionClockwiseDegrees"] != 0:
                raise ValueError("This calibration tool requires upright source pages.")
            pages.append(page)
        else:
            excluded.append(page["id"])
    if destination.parent.resolve() != original.parent.resolve() or destination.resolve() == original.resolve():
        raise ValueError("Write a separate manifest beside the original to preserve relative paths.")
    manifest["corpusId"] = "mdpbench-five-language-orientation-audited-v1"
    manifest["originalManifestSha256"] = source_hash
    manifest["orientationAudit"] = {
        "report": audit_path.as_posix(),
        "reportSha256": hashlib.sha256(audit_path.read_bytes()).hexdigest(),
        "excludedIds": excluded,
        "exclusionReason": "Mixed text orientation: upright running header/footer and dominant table requiring90degree clockwise rotation. No unique single page correction target. Exclusion based on source semantics, not prediction scores.",
        "expectedSourceCorrectionClockwiseDegrees": 0,
    }
    manifest["images"] = pages
    destination.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Wrote {len(pages)} audited pages; excluded {excluded}.")


if __name__ == "__main__":
    main()
