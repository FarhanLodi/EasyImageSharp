import copy
import sys
import unittest
from compare_exports import compare
from prepare_block_corpus import select_block
from run_export_batches import run_bounded
from summarize_exports import summarize


def observation(identifier="one", error=None):
    return {"Id": identifier, "ImageSha256": "abc", "GroundTruth": "hello", "Prediction": "" if error else "hello",
            "Error": error, "Lines": 0 if error else 1, "CharacterErrorRate": 1 if error else 0,
            "WordErrorRate": 1 if error else 0}


def report(rows):
    return {"Engine": "test", "Rows": rows, "Assemblies": [], "ModelCacheSnapshot": [{"Name": "model", "Sha256": "ABC"}]}


class ExportTests(unittest.TestCase):
    def test_failed_empty_text_is_not_successful_parity(self):
        value = report([observation(), observation("failed", "size limit")])
        result = compare(value, copy.deepcopy(value))
        self.assertEqual(result["identicalSuccessfulTexts"], 1)
        self.assertEqual(result["bothFailedImages"], 1)
        self.assertEqual(result["changedRows"], [])

    def test_model_changes_and_added_assets_are_visible(self):
        left = report([observation()])
        right = copy.deepcopy(left)
        right["ModelCacheSnapshot"][0]["Sha256"] = "changed"
        right["ModelCacheSnapshot"].append({"Name": "extra", "Sha256": "def"})
        result = compare(left, right)
        self.assertEqual(result["mismatchedModelAssets"], ["model"])
        self.assertEqual(result["releaseOnlyModelAssets"], ["extra"])

    def test_label_mismatch_and_duplicate_ids_fail(self):
        left = report([observation()])
        right = copy.deepcopy(left)
        right["Rows"][0]["GroundTruth"] = "different"
        with self.assertRaises(ValueError):
            compare(left, right)
        with self.assertRaises(ValueError):
            compare(report([observation(), observation()]), left)

    def test_failed_sample_remains_in_character_denominator(self):
        manifest = {"images": [{"id": name, "sha256": "abc", "split": "held-out", "script": "Latin", "category": "digital"}
                               for name in ("one", "failed")]}
        result = summarize(manifest, report([observation(), observation("failed", "error")]))
        self.assertEqual(result["groups"]["held-out"]["cer"], .5)
        self.assertEqual(result["groups"]["held-out/Latin/digital"]["failed"], 1)

    def test_selection_prefers_text_block_before_larger_caption(self):
        blocks = [{"category_type": "table_caption", "text": "caption", "poly": [0, 0, 100, 0, 100, 100, 0, 100]},
                  {"category_type": "text_block", "text": "body", "poly": [0, 0, 10, 0, 10, 10, 0, 10]}]
        self.assertEqual(select_block(blocks)[0], 1)
        self.assertEqual(select_block(blocks[:1])[0], 0)

    def test_process_guard_stops_process_without_dropping_result(self):
        # A guard breach raises; orchestration cannot emit a falsely complete report.
        with self.assertRaises(RuntimeError):
            run_bounded([sys.executable, "-c", "import time; time.sleep(5)"], 1)


if __name__ == "__main__":
    unittest.main()
