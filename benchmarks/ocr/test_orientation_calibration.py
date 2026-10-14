import copy
import unittest
from calibrate_orientation import analyze, fit_isotonic, predict, reliability


class CalibrationTests(unittest.TestCase):
    def test_ties_keep_observation_weights_and_pool_violations(self):
        model = fit_isotonic([(0.2, 1), (0.2, 1), (0.2, 0), (0.5, 0), (0.9, 1)])
        self.assertEqual([p["probability"] for p in model], [0.5, 0.5, 1])
        self.assertAlmostEqual(predict(model, 0.7), 0.75)
        self.assertEqual(predict(model, 0), 0.5)
        self.assertEqual(predict(model, 1), 1)
        predictions = [predict(model, x / 100) for x in range(101)]
        self.assertEqual(predictions, sorted(predictions))

    def test_empty_and_small_bins_have_defined_metrics(self):
        result = reliability([0, 1], [0, 1])
        self.assertEqual(result["brier"], 0)
        self.assertEqual(result["ece"], 0)
        self.assertEqual([b["count"] for b in result["bins"]], [1, 0, 0, 0, 1])
        self.assertIsNone(result["bins"][1]["accuracy"])
        self.assertEqual(reliability([0.75], [0])["ece"], 0.75)
        with self.assertRaises(ValueError):
            reliability([], [])

    @staticmethod
    def rows():
        return [{"Id": str(i), "Family": str(i), "Sha256": str(i), "Script": "Latin",
                 "Split": "tuning" if i < 2 else "held-out", "Confidence": (i + 1) / 4,
                 "PredictedCorrection": 0, "ExpectedCorrection": 0 if i else 90}
                for i in range(3)]

    def test_heldout_labels_and_scores_cannot_change_fit(self):
        rows = self.rows()
        model = analyze(rows)["model"]
        rows[-1]["ExpectedCorrection"] = 90
        rows[-1]["Confidence"] = 0.01
        self.assertEqual(model, analyze(rows)["model"])

    def test_split_leakage_rejected_for_family_hash_and_id(self):
        for key in ("Family", "Sha256", "Id"):
            rows = copy.deepcopy(self.rows())
            rows[-1][key] = rows[0][key]
            with self.assertRaises(ValueError):
                analyze(rows)

    def test_invalid_inputs_and_single_score_fit(self):
        self.assertEqual(predict(fit_isotonic([(0.3, 0), (0.3, 1)]), 0.9), 0.5)
        for samples in ([], [(float("nan"), 1)], [(1.1, 1)], [(0.4, 2)]):
            with self.assertRaises(ValueError):
                fit_isotonic(samples)


if __name__ == "__main__":
    unittest.main()
