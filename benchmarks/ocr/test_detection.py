import unittest
from detection import polygon, iou, score_boxes, aggregate_detection


def box(x, y, width, height):
    return [[x, y], [x + width, y], [x + width, y + height], [x, y + height]]


class DetectionTests(unittest.TestCase):
    def test_overlap_and_reversed_winding(self):
        self.assertAlmostEqual(1 / 3, iou(polygon(box(0, 0, 2, 2)), polygon(box(1, 0, 2, 2)[::-1])))

    def test_duplicate_prediction_is_false_positive(self):
        square = box(0, 0, 10, 10)
        self.assertEqual(dict(true_positives=1, false_positives=1, false_negatives=0), score_boxes([square], [square, square]))

    def test_maximum_matching_avoids_greedy_trap(self):
        # Wide prediction matches both targets; narrow prediction matches only the first.
        result = score_boxes([box(0, 0, 10, 10), box(10, 0, 10, 10)],
                             [box(0, 0, 20, 10), box(0, 0, 10, 10)], 0.5)
        self.assertEqual(2, result["true_positives"])

    def test_rotated_diamond(self):
        diamond = polygon([[0, 1], [1, 0], [2, 1], [1, 2]])
        self.assertAlmostEqual(0.5, iou(diamond, polygon(box(0, 0, 2, 2))))

    def test_blank_pages_keep_false_positives(self):
        self.assertEqual(dict(true_positives=0, false_positives=1, false_negatives=0, precision=0, recall=None, f1=0),
                         aggregate_detection([score_boxes([], [box(0, 0, 1, 1)])]))

    def test_invalid_boxes_and_thresholds(self):
        for malformed in ([[0, 0]] * 4, [[0, 0], [1, 1], [0, 1], [1, 0]], box(float("nan"), 0, 1, 1)):
            with self.assertRaises(ValueError):
                score_boxes([malformed], [])
        for threshold in (0, float("nan"), True, 2):
            with self.assertRaises(ValueError):
                score_boxes([], [], threshold)


if __name__ == "__main__":
    unittest.main()
