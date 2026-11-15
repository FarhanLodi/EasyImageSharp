"""Exports Marqo/nsfw-image-detection-384 (Apache-2.0, timm vit_tiny_patch16_384) to ONNX and verifies it.

Usage (Python 3.11, torch 2.x CPU, timm 1.0.x, safetensors, onnx, onnxruntime, numpy < 2 when torch was
built against NumPy 1.x):

    python -I export_moderation.py <dir with model.safetensors + config.json> <output.onnx> [--reference out.json] [image ...]

The weights are read with safetensors (no pickle). The graph is exported with fused attention disabled so
attention lowers to plain MatMul/Softmax, opset 17, a dynamic batch axis and fixed 384x384 spatial size; the
class names are stored in the model metadata under "labels" (comma-separated, output order).
Verification runs the PyTorch model and the exported graph on seeded random tensors, on the deterministic scene
from reference_images.py and on every image given on the command line (preprocessed exactly like the C#
contract: shorter side to 384 with PIL bicubic, longer side truncated, centre crop with a floor offset,
(x/255 - 0.5)/0.5) and fails when the logits differ by more than 1e-4. --reference writes the scene's class
probabilities for the C# smoke test.

Contract of the exported file:
    input  pixel_values float32 [N,3,384,384] RGB, (x/255 - 0.5) / 0.5
    output logits       float32 [N,2]          classes in config.json "label_names" order: ["NSFW", "SFW"]
"""
from __future__ import annotations

import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402
import onnx  # noqa: E402
import onnxruntime as ort  # noqa: E402
import timm  # noqa: E402
import torch  # noqa: E402
from PIL import Image  # noqa: E402
from safetensors.torch import load_file  # noqa: E402

from reference_images import scene, short_side_center_crop  # noqa: E402

SIZE = 384
REVISION = "0c26ec22111b83f106d72a55f611ec35962bcb65"


def to_tensor(image: Image.Image) -> np.ndarray:
    image = short_side_center_crop(image.convert("RGB"), SIZE, Image.BICUBIC)
    x = (np.asarray(image, dtype=np.float32) / 255.0 - 0.5) / 0.5
    return x.transpose(2, 0, 1)[None].copy()


def softmax(logits: np.ndarray) -> np.ndarray:
    e = np.exp(logits - logits.max(-1, keepdims=True))
    return e / e.sum(-1, keepdims=True)


def main() -> None:
    args = sys.argv[1:]
    reference_path = None
    if "--reference" in args:
        index = args.index("--reference")
        reference_path = args[index + 1]
        del args[index:index + 2]
    source_dir, output = args[0], args[1]
    images = args[2:]
    with open(os.path.join(source_dir, "config.json"), encoding="utf-8") as fh:
        config = json.load(fh)
    assert config["architecture"] == "vit_tiny_patch16_384", config["architecture"]
    assert config["label_names"] == ["NSFW", "SFW"], config["label_names"]

    timm.layers.set_fused_attn(False)
    model = timm.create_model(config["architecture"], pretrained=False, num_classes=config["num_classes"])
    model.load_state_dict(load_file(os.path.join(source_dir, "model.safetensors")), strict=True)
    model.eval()

    dummy = torch.zeros(1, 3, SIZE, SIZE)
    torch.onnx.export(
        model, dummy, output,
        input_names=["pixel_values"], output_names=["logits"],
        dynamic_axes={"pixel_values": {0: "batch"}, "logits": {0: "batch"}},
        opset_version=17, do_constant_folding=True)

    graph = onnx.load(output)
    onnx.checker.check_model(graph)
    graph.producer_name = "EasyImageSharp ModelExport (torch.onnx.export)"
    graph.doc_string = f"Marqo/nsfw-image-detection-384 @ {REVISION}, Apache-2.0. Labels: NSFW, SFW."
    del graph.metadata_props[:]
    onnx.helper.set_model_props(graph, {"labels": ",".join(config["label_names"])})
    onnx.save(graph, output)

    session = ort.InferenceSession(output, providers=["CPUExecutionProvider"])
    rng = np.random.default_rng(1234)
    reference_scene = to_tensor(scene())
    batches = [rng.uniform(-1, 1, (2, 3, SIZE, SIZE)).astype(np.float32), reference_scene]
    batches += [to_tensor(Image.open(path)) for path in images]
    worst = 0.0
    for batch in batches:
        with torch.no_grad():
            expected = model(torch.from_numpy(batch)).numpy()
        actual = session.run(["logits"], {"pixel_values": batch})[0]
        worst = max(worst, float(np.abs(expected - actual).max()))

    named = [("scene_320x240", reference_scene)] + [(os.path.basename(p), to_tensor(Image.open(p))) for p in images]
    for name, batch in named:
        probabilities = softmax(session.run(["logits"], {"pixel_values": batch})[0][0])
        print(f"{name}: NSFW={probabilities[0]:.4f} SFW={probabilities[1]:.4f}")

    print(f"max |torch - onnxruntime| = {worst:.2e}")
    if worst > 1e-4:
        raise SystemExit("ONNX export does not match the PyTorch model")

    if reference_path:
        with torch.no_grad():
            probabilities = softmax(model(torch.from_numpy(reference_scene)).numpy()[0].astype(np.float64))
        with open(reference_path, "w", encoding="utf-8") as fh:
            json.dump({"scene_320x240": [round(float(v), 6) for v in probabilities]}, fh)
        print("wrote", reference_path)

    with open(output, "rb") as fh:
        digest = hashlib.sha256(fh.read()).hexdigest().upper()
    print(f"{os.path.basename(output)} {os.path.getsize(output)} bytes SHA-256 {digest}")


if __name__ == "__main__":
    main()
