"""Offline experimental isotonic calibration; fit only tuning-family predictions."""
import bisect
import hashlib
import json
import math
from pathlib import Path
import sys


def fit_isotonic(samples):
    """Weighted PAV after grouping tied scores, preserving every observation's weight."""
    grouped = {}
    for score, correct in samples:
        if not math.isfinite(score) or not 0 <= score <= 1 or correct not in (0, 1):
            raise ValueError("Expected finite scores in [0,1] and binary outcomes.")
        count, total = grouped.get(score, (0, 0))
        grouped[score] = count + 1, total + correct
    if not grouped:
        raise ValueError("Calibration requires tuning observations.")
    blocks = []
    for score, (count, total) in sorted(grouped.items()):
        blocks.append(([score], count, total))
        while len(blocks) > 1 and blocks[-2][2] / blocks[-2][1] > blocks[-1][2] / blocks[-1][1]:
            right = blocks.pop()
            left = blocks.pop()
            blocks.append((left[0] + right[0], left[1] + right[1], left[2] + right[2]))
    return [{"score": score, "probability": total / count}
            for scores, count, total in blocks for score in scores]


def predict(model, score):
    """Linear interpolation between fitted unique scores, clamped at endpoints."""
    if not math.isfinite(score) or not 0 <= score <= 1:
        raise ValueError("Invalid score.")
    index = bisect.bisect_right([v["score"] for v in model], score)
    if index == 0:
        return model[0]["probability"]
    if index == len(model):
        return model[-1]["probability"]
    left, right = model[index - 1], model[index]
    weight = (score - left["score"]) / (right["score"] - left["score"])
    return left["probability"] + weight * (right["probability"] - left["probability"])


def reliability(probabilities, outcomes, bin_count=5):
    if len(probabilities) != len(outcomes) or not probabilities or bin_count < 1:
        raise ValueError("Expected matching nonempty samples and positive bin count.")
    bins = [[] for _ in range(bin_count)]
    for p, y in zip(probabilities, outcomes):
        if not math.isfinite(p) or not 0 <= p <= 1 or y not in (0, 1):
            raise ValueError("Invalid probability or outcome.")
        bins[min(int(p * bin_count), bin_count - 1)].append((p, y))
    result = []
    ece = 0.0
    for index, values in enumerate(bins):
        confidence = sum(p for p, _ in values) / len(values) if values else None
        accuracy = sum(y for _, y in values) / len(values) if values else None
        if values:
            ece += len(values) / len(probabilities) * abs(confidence - accuracy)
        result.append({"lowerInclusive": index / bin_count, "upper": (index + 1) / bin_count,
                       "upperInclusive": index == bin_count - 1, "count": len(values),
                       "meanProbability": confidence, "accuracy": accuracy})
    return {"count": len(probabilities), "brier": sum((p - y) ** 2 for p, y in zip(probabilities, outcomes)) / len(probabilities),
            "ece": ece, "bins": result}


def analyze(rows):
    owners = {"Family": {}, "Sha256": {}, "Id": {}}
    for row in rows:
        if row["Split"] not in ("tuning", "held-out"):
            raise ValueError("Unknown split.")
        for key, seen in owners.items():
            previous = seen.setdefault(row[key], row["Split"])
            if previous != row["Split"]:
                raise ValueError(f"{key} crosses splits.")
    tuning = [r for r in rows if r["Split"] == "tuning"]
    held = [r for r in rows if r["Split"] == "held-out"]
    if not tuning or not held:
        raise ValueError("Require tuning and held-out observations.")
    # Correct is recomputed from labels, never from acceptance/final-orientation policy.
    correct = lambda r: int(r["PredictedCorrection"] == r["ExpectedCorrection"])
    model = fit_isotonic([(r["Confidence"], correct(r)) for r in tuning])

    def evaluate(values):
        outcomes = [correct(r) for r in values]
        return {"families": len({r["Family"] for r in values}),
                "raw": reliability([r["Confidence"] for r in values], outcomes),
                "isotonic": reliability([predict(model, r["Confidence"]) for r in values], outcomes)}

    def script_summary(values):
        summary = evaluate(values)
        for name in ("raw", "isotonic"):
            summary[name].pop("bins")
        return summary

    return {"fitFamilies": len({r["Family"] for r in tuning}), "fitObservations": len(tuning),
            "model": model, "heldOut": evaluate(held),
            "heldOutByScript": {script: script_summary([r for r in held if r["Script"] == script])
                                for script in sorted({r["Script"] for r in held})}}


def main():
    paths = [Path(p) for p in sys.argv[1:-1]]
    results = []
    for path in paths:
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        results.append({"sourceReport": path.as_posix(), "sourceReportSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "manifestSha256": report["ManifestSha256"], "detector": report["Detector"], **analyze(report["Rows"])})
    result = {"schemaVersion": 1, "method": "Tuning-only weighted isotonic PAV, ties grouped; linear interpolation; endpoint clamping",
              "target": "Correct cardinal orientation estimate, regardless of threshold acceptance",
              "eceBins": "Five fixed equal-width bins; last bin includes 1; empty-bin metrics are null",
              "limitations": ["Experimental analysis, no library default or acceptance policy changes.",
                              "Four rotated views per family are dependent; only ten tuning and nine held-out families.",
                              "Sparse script bins are descriptive, not reliable population calibration estimates.",
                              "Isotonic fitting may overfit or worsen held-out Brier/ECE; no production calibration claim.",
                              "ECE depends on bin definitions; Brier includes both calibration and discrimination effects."],
              "results": results}
    Path(sys.argv[-1]).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
