import copy
import json
from pathlib import Path
import tempfile
import unittest

from evaluate import edit_distance, evaluate, main


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        self.corpus = {
            "schema_version": 1, "corpus_id": "unit", "license": "MIT",
            "provenance": "synthetic unit test", "split": "smoke",
            "samples": [
                {"id": "a", "text": "hello world", "category": "scan", "script": "Latin"},
                {"id": "b", "text": "文字", "category": "photo", "script": "Han"},
            ],
        }
        self.run = {
            "schema_version": 1, "corpus_id": "unit",
            "metadata": {
                "engine": "test", "engine_version": "1", "model_id": "synthetic",
                "model_sha256": "0" * 64, "preprocessing": {},
                "environment": {"runtime": "unit test"},
            },
            "predictions": [{"id": "a", "text": "hello word", "latency_ms": 10},
                            {"id": "b", "text": "文字", "latency_ms": 30}],
        }

    def test_known_distance_and_symmetry(self):
        for left, right, expected in [("kitten", "sitting", 3), ("", "abc", 3),
                                      ("same", "same", 0), ("ab", "ba", 2)]:
            self.assertEqual(expected, edit_distance(left, right))
            self.assertEqual(expected, edit_distance(right, left))

    def test_detection_metrics_aggregate_and_require_complete_geometry(self):
        square = [[0, 0], [10, 0], [10, 10], [0, 10]]
        for row in self.corpus["samples"]:
            row["boxes"] = [square]
        self.run["predictions"][0]["boxes"] = [square, square]
        self.run["predictions"][1]["boxes"] = []
        report = evaluate(self.corpus, self.run)
        self.assertEqual(0.5, report["overall"]["detection"]["precision"])
        self.assertEqual(0.5, report["overall"]["detection"]["recall"])
        self.assertEqual(0, report["by_script"]["Han"]["detection"]["true_positives"])
        del self.run["predictions"][1]["boxes"]
        with self.assertRaises(ValueError):
            evaluate(self.corpus, self.run)

    def test_micro_metrics_and_groups(self):
        report = evaluate(self.corpus, self.run)
        self.assertAlmostEqual(1 / 13, report["overall"]["cer"])
        self.assertAlmostEqual(1 / 3, report["overall"]["wer"])
        self.assertEqual(0, report["by_script"]["Han"]["cer"])
        self.assertEqual(1, report["by_category"]["scan"]["character_errors"])
        self.assertEqual(30, report["overall"]["latency_ms"]["p95"])

    def test_unicode_canonical_equivalence_and_codepoints(self):
        self.corpus["samples"][0]["text"] = "é😀"
        self.run["predictions"][0]["text"] = "e\u0301😀"
        row = evaluate(self.corpus, self.run)["samples"][0]
        self.assertEqual(2, row["reference_characters"])
        self.assertEqual(0, row["character_errors"])

    def test_blank_reference_retains_insertions_and_null_rate(self):
        self.corpus["samples"][0]["text"] = ""
        report = evaluate(self.corpus, self.run)["by_category"]["scan"]
        self.assertIsNone(report["cer"])
        self.assertEqual(10, report["character_errors"])
        self.assertIsNone(report["wer"])

    def test_failed_predictions_are_counted_as_deletions(self):
        self.run["predictions"][0].update(text="", error="inference failed")
        row = evaluate(self.corpus, self.run)["samples"][0]
        self.assertTrue(row["failed"])
        self.assertEqual(11, row["character_errors"])

    def test_missing_duplicate_and_extra_predictions_rejected(self):
        for predictions in [self.run["predictions"][:1],
                            self.run["predictions"] * 2,
                            self.run["predictions"] + [{"id": "x", "text": ""}]]:
            run = copy.deepcopy(self.run)
            run["predictions"] = predictions
            with self.assertRaises(ValueError):
                evaluate(self.corpus, run)

    def test_invalid_latency_and_partial_timing_rejected(self):
        for value in [-1, float("nan"), float("inf"), True, "10"]:
            run = copy.deepcopy(self.run)
            run["predictions"][0]["latency_ms"] = value
            with self.assertRaises(ValueError):
                evaluate(self.corpus, run)
        del self.run["predictions"][0]["latency_ms"]
        with self.assertRaises(ValueError):
            evaluate(self.corpus, self.run)

    def test_no_timing_is_supported(self):
        for row in self.run["predictions"]:
            del row["latency_ms"]
        self.assertNotIn("latency_ms", evaluate(self.corpus, self.run)["overall"])

    def test_unpinned_model_rejected(self):
        del self.run["metadata"]["model_sha256"]
        with self.assertRaises(ValueError):
            evaluate(self.corpus, self.run)

    def test_boolean_is_not_a_schema_version(self):
        self.corpus["schema_version"] = True
        with self.assertRaises(ValueError):
            evaluate(self.corpus, self.run)

    def test_cli_records_hashes_and_protects_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            corpus, predictions, output = root / "corpus.json", root / "run.json", root / "report.json"
            corpus.write_text(json.dumps(self.corpus), encoding="utf-8")
            predictions.write_text(json.dumps(self.run), encoding="utf-8")
            self.assertEqual(0, main([str(corpus), str(predictions), "--output", str(output)]))
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(64, len(report["input_sha256"]["corpus"]))
            self.assertEqual(1, main([str(corpus), str(predictions), "--output", str(corpus)]))


if __name__ == "__main__":
    unittest.main()
