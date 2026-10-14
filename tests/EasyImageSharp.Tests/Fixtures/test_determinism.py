"""Regression checks for reduced JPEG provenance without weakening pixel comparisons."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image
from check_determinism import compare_file


class ReducedProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.left = Path(self.scratch.name) / "left"
        self.right = Path(self.scratch.name) / "right"
        self.write_tree(self.left, 0)
        self.write_tree(self.right, 9)

    @staticmethod
    def write_tree(root, compression, color=(17, 23, 31)):
        (root / "jpeg").mkdir(parents=True, exist_ok=True)
        output = root / "jpeg-reduced"
        output.mkdir(exist_ok=True)
        source = root / "jpeg" / "sample.jpg"
        target = output / "sample-2.png"
        Image.new("RGB", (4, 4), (17, 23, 31)).save(source)
        Image.new("RGB", (2, 2), color).save(target, compress_level=compression)
        document = {"method": "test", "references": [{"name": "sample", "denominator": 2,
                    "inputSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                    "outputSha256": hashlib.sha256(target.read_bytes()).hexdigest()}]}
        (output / "provenance.json").write_text(json.dumps(document), encoding="utf-8")

    def compare(self, relative):
        return compare_file(relative, str(self.left / relative), str(self.right / relative))

    def test_different_png_encoding_same_pixels_and_valid_hashes_pass(self):
        relative = "jpeg-reduced/sample-2.png"
        self.assertNotEqual((self.left / relative).read_bytes(), (self.right / relative).read_bytes())
        self.assertIsNone(self.compare(relative)[1])
        self.assertIsNone(self.compare("jpeg-reduced/provenance.json")[1])

    def test_incorrect_encoded_digest_fails(self):
        path = self.right / "jpeg-reduced/provenance.json"
        document = json.loads(path.read_text(encoding="utf-8"))
        document["references"][0]["outputSha256"] = "0" * 64
        path.write_text(json.dumps(document), encoding="utf-8")
        self.assertIn("does not match", self.compare("jpeg-reduced/provenance.json")[1])

    def test_changed_pixels_with_updated_digest_still_fail_pixel_comparison(self):
        self.write_tree(self.right, 9, (18, 23, 31))
        self.assertIsNone(self.compare("jpeg-reduced/provenance.json")[1])
        self.assertIsNotNone(self.compare("jpeg-reduced/sample-2.png")[1])


if __name__ == "__main__":
    unittest.main()
