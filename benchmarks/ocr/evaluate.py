"""Score exported OCR predictions without loading models or changing source images.

Usage: python benchmarks/ocr/evaluate.py corpus.json predictions.json --output report.json
See benchmarks/README.md for the input contract and metric definitions.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import unicodedata
from detection import score_boxes, aggregate_detection


def edit_distance(reference, predicted):
    """Levenshtein distance with O(min(n, m)) auxiliary storage."""
    if len(reference) < len(predicted):
        reference, predicted = predicted, reference
    previous = list(range(len(predicted) + 1))
    for i, left in enumerate(reference, 1):
        current = [i]
        for j, right in enumerate(predicted, 1):
            current.append(min(current[-1] + 1, previous[j] + 1,
                               previous[j - 1] + (left != right)))
        previous = current
    return previous[-1]


def required_text(value, field):
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{field} must be a non-empty string")
    return value


def indexed_rows(rows, label):
    if not isinstance(rows, list) or not rows:
        raise ValueError(f"{label} must be a non-empty array")
    result = {}
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError(f"{label} entries must be objects")
        key = required_text(row.get("id"), f"{label}.id")
        if key in result:
            raise ValueError(f"duplicate {label} id: {key}")
        if not isinstance(row.get("text"), str):
            raise ValueError(f"{label}[{key}].text must be a string, including for empty pages")
        result[key] = row
    return result


def aggregate(rows):
    characters = sum(row["reference_characters"] for row in rows)
    words = sum(row["reference_words"] for row in rows)
    character_errors = sum(row["character_errors"] for row in rows)
    word_errors = sum(row["word_errors"] for row in rows)
    result = {
        "samples": len(rows),
        "failed_samples": sum(row["failed"] for row in rows),
        "exact_matches": sum(row["exact_match"] for row in rows),
        "reference_characters": characters,
        "character_errors": character_errors,
        "cer": character_errors / characters if characters else None,
        "reference_words": words,
        "word_errors": word_errors,
        "wer": word_errors / words if words else None,
    }
    if rows and "latency_ms" in rows[0]:
        times = sorted(row["latency_ms"] for row in rows)
        result["latency_ms"] = {
            "mean": sum(times) / len(times),
            "p50": times[math.ceil(0.50 * len(times)) - 1],
            "p95": times[math.ceil(0.95 * len(times)) - 1],
            "total": sum(times),
        }
    if rows and "detection" in rows[0]:
        result["detection"] = aggregate_detection([row["detection"] for row in rows])
    return result


def evaluate(corpus, run):
    if not isinstance(corpus, dict) or not isinstance(run, dict):
        raise ValueError("corpus and predictions must be JSON objects")
    if any(type(value.get("schema_version")) is not int or value["schema_version"] != 1
           for value in (corpus, run)):
        raise ValueError("corpus and predictions require schema_version: 1")
    for field in ("corpus_id", "license", "provenance"):
        required_text(corpus.get(field), f"corpus.{field}")
    if corpus.get("split") not in ("tuning", "heldout", "smoke"):
        raise ValueError("corpus.split must be tuning, heldout, or smoke")
    if run.get("corpus_id") != corpus["corpus_id"]:
        raise ValueError("prediction corpus_id does not match corpus")
    metadata = run.get("metadata")
    if not isinstance(metadata, dict):
        raise ValueError("predictions.metadata must be an object")
    for field in ("engine", "engine_version", "model_id"):
        required_text(metadata.get(field), f"metadata.{field}")
    checksum = metadata.get("model_sha256")
    if not isinstance(checksum, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", checksum):
        raise ValueError("metadata.model_sha256 must contain 64 hexadecimal characters")
    if not isinstance(metadata.get("preprocessing"), dict):
        raise ValueError("metadata.preprocessing must describe the settings as an object")
    if not isinstance(metadata.get("environment"), dict) or not metadata["environment"]:
        raise ValueError("metadata.environment must be a non-empty object")

    references = indexed_rows(corpus.get("samples"), "samples")
    predictions = indexed_rows(run.get("predictions"), "predictions")
    if references.keys() != predictions.keys():
        missing = sorted(references.keys() - predictions.keys())
        extra = sorted(predictions.keys() - references.keys())
        raise ValueError(f"prediction coverage mismatch; missing={missing}, extra={extra}")
    timed = ["latency_ms" in row for row in predictions.values()]
    if any(timed) and not all(timed):
        raise ValueError("latency_ms must be present for every prediction or none")
    boxed = ["boxes" in row for row in [*references.values(), *predictions.values()]]
    if any(boxed) and not all(boxed):
        raise ValueError("boxes must be present for every reference and prediction or none")
    iou_threshold = metadata.get("detection_iou_threshold", 0.5)

    results = []
    for key, reference in references.items():
        prediction = predictions[key]
        category = required_text(reference.get("category"), f"samples[{key}].category")
        script = required_text(reference.get("script"), f"samples[{key}].script")
        # NFC only: case, punctuation, whitespace, and reading order remain significant for CER.
        expected = unicodedata.normalize("NFC", reference["text"])
        actual = unicodedata.normalize("NFC", prediction["text"])
        error = prediction.get("error")
        if error is not None:
            required_text(error, f"predictions[{key}].error")
            if actual:
                raise ValueError(f"failed prediction {key} must use empty text")
        row = {
            "id": key, "category": category, "script": script,
            "failed": error is not None,
            "exact_match": error is None and expected == actual,
            "reference_characters": len(expected),
            "character_errors": edit_distance(expected, actual),
            "reference_words": len(expected.split()),
            "word_errors": edit_distance(expected.split(), actual.split()),
        }
        if all(timed):
            elapsed = prediction["latency_ms"]
            if (isinstance(elapsed, bool) or not isinstance(elapsed, (int, float))
                    or not math.isfinite(elapsed) or elapsed < 0):
                raise ValueError(f"latency_ms for {key} must be finite and non-negative")
            row["latency_ms"] = elapsed
        if all(boxed):
            if error is not None and prediction["boxes"]:
                raise ValueError(f"failed prediction {key} must use empty boxes")
            row["detection"] = score_boxes(reference["boxes"], prediction["boxes"], iou_threshold)
        results.append(row)

    return {
        "schema_version": 1,
        "corpus_id": corpus["corpus_id"],
        "split": corpus["split"],
        "license": corpus["license"],
        "provenance": corpus["provenance"],
        "metadata": metadata,
        "metrics": {"normalization": "NFC", "character_unit": "Unicode code point",
                    "word_tokenization": "Unicode whitespace", "aggregation": "micro",
                    "latency_percentiles": "nearest rank",
                    "detection": {"matching": "maximum-cardinality one-to-one convex quadrilateral IoU",
                                  "iou_threshold": iou_threshold, "ignored_regions": False} if all(boxed) else None},
        "overall": aggregate(results),
        "by_category": {key: aggregate([r for r in results if r["category"] == key])
                        for key in sorted({r["category"] for r in results})},
        "by_script": {key: aggregate([r for r in results if r["script"] == key])
                      for key in sorted({r["script"] for r in results})},
        "samples": results,
    }


def load_json(path):
    raw = path.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), hashlib.sha256(raw).hexdigest()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus", type=Path)
    parser.add_argument("predictions", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        if args.output.resolve() in (args.corpus.resolve(), args.predictions.resolve()):
            raise ValueError("output must not overwrite either input")
        corpus, corpus_hash = load_json(args.corpus)
        predictions, predictions_hash = load_json(args.predictions)
        report = evaluate(corpus, predictions)
        report["input_sha256"] = {"corpus": corpus_hash, "predictions": predictions_hash}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
                               + "\n", encoding="utf-8")
        print(json.dumps(report["overall"], ensure_ascii=False, allow_nan=False))
        return 0
    except (OSError, ValueError) as exc:
        print(f"Evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
