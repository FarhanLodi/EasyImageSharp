"""Merges the reference outputs of the verification scripts into the smoke-test fixture.

Usage:

    python -I verify_clip.py <openai/clip-vit-base-patch32 dir> <vision_model.onnx> --reference <work>/clip_reference.json
    python -I export_moderation.py <Marqo dir> <out.onnx> --reference <work>/nsfw_reference.json
    python -I export_nima.py <idealo MobileNet dir> <out.onnx> --reference <work>/nima_reference.json
    python -I write_test_references.py <work> tests/EasyImageSharp.AI.Tests/Models/vision_model_references.json

Every value is computed from the official weights (the NumPy re-implementation for CLIP and NIMA, PyTorch for the
moderation classifier) on the deterministic scene of reference_images.py.
"""
from __future__ import annotations

import json
import sys


def main() -> None:
    work, target = sys.argv[1], sys.argv[2]
    with open(f"{work}/clip_reference.json", encoding="utf-8") as fh:
        clip = json.load(fh)
    with open(f"{work}/nsfw_reference.json", encoding="utf-8") as fh:
        nsfw = json.load(fh)
    with open(f"{work}/nima_reference.json", encoding="utf-8") as fh:
        nima = json.load(fh)

    merged = {
        "_comment": "Reference outputs of the official models for the deterministic 320x240 scene of "
                    "tools/ModelExport/VisionModels/reference_images.py, written by verify_clip.py --reference, "
                    "export_moderation.py --reference and export_nima.py --reference.",
        "clip_vit_b32_embedding": clip["scene_320x240"],
        "nsfw_probabilities": nsfw["scene_320x240"],
        "nima_technical": nima["scene_320x240_technical"],
        "nima_aesthetic": nima["scene_320x240_aesthetic"],
    }
    with open(target, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(merged, fh, indent=1)
        fh.write("\n")
    print(f"wrote {target}")


if __name__ == "__main__":
    main()
