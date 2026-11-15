"""Verifies an ONNX export of the CLIP ViT-B/32 image tower against the official OpenAI weights.

Usage:

    python -I verify_clip.py <openai/clip-vit-base-patch32 dir> <vision_model.onnx> [quantized.onnx] [--reference out.json]

The reference is an independent NumPy (float64) implementation of the CLIP vision transformer and visual
projection, fed with `pytorch_model.bin` from the official repository (loaded with `weights_only=True`, so no
pickled code runs). It is compared with ONNX Runtime on seeded random tensors and on the deterministic scene
from reference_images.py preprocessed with the CLIP evaluation transform (shorter side to 224 with PIL bicubic,
centre crop, mean (0.48145466, 0.4578275, 0.40821073), std (0.26862954, 0.26130258, 0.27577711)).

The run fails when the fp32 export's cosine similarity with the reference drops below 0.99999. A quantized
export is reported (cosine similarity with fp32) but not gated. --reference writes the L2-normalised scene
embedding for the C# smoke test.
"""
from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402
import onnxruntime as ort  # noqa: E402
import torch  # noqa: E402
from PIL import Image  # noqa: E402

from reference_images import scene, short_side_center_crop  # noqa: E402

MEAN = np.array([0.48145466, 0.4578275, 0.40821073])
STD = np.array([0.26862954, 0.26130258, 0.27577711])


def layer_norm(x, weight, bias, eps=1e-5):
    mean = x.mean(-1, keepdims=True)
    var = ((x - mean) ** 2).mean(-1, keepdims=True)
    return (x - mean) / np.sqrt(var + eps) * weight + bias


def clip_vision(weights: dict, pixels: np.ndarray, layers: int = 12, heads: int = 12, patch: int = 32) -> np.ndarray:
    w = lambda name: weights[name].astype(np.float64)  # noqa: E731
    n, c, h, wd = pixels.shape
    gh, gw = h // patch, wd // patch
    # Non-overlapping stride-32 convolution == matmul over flattened (c, kh, kw) patches in row-major grid order.
    patches = pixels.reshape(n, c, gh, patch, gw, patch).transpose(0, 2, 4, 1, 3, 5).reshape(n, gh * gw, c * patch * patch)
    kernel = w("vision_model.embeddings.patch_embedding.weight")
    tokens = patches @ kernel.reshape(kernel.shape[0], -1).T
    cls = np.broadcast_to(w("vision_model.embeddings.class_embedding"), (n, 1, kernel.shape[0]))
    x = np.concatenate([cls, tokens], axis=1) + w("vision_model.embeddings.position_embedding.weight")
    x = layer_norm(x, w("vision_model.pre_layrnorm.weight"), w("vision_model.pre_layrnorm.bias"))
    dim = x.shape[-1]
    head = dim // heads
    for i in range(layers):
        p = f"vision_model.encoder.layers.{i}."
        y = layer_norm(x, w(p + "layer_norm1.weight"), w(p + "layer_norm1.bias"))
        proj = lambda t, name: t @ w(p + f"self_attn.{name}.weight").T + w(p + f"self_attn.{name}.bias")  # noqa: E731
        q = proj(y, "q_proj").reshape(n, -1, heads, head).transpose(0, 2, 1, 3) * head ** -0.5
        k = proj(y, "k_proj").reshape(n, -1, heads, head).transpose(0, 2, 1, 3)
        v = proj(y, "v_proj").reshape(n, -1, heads, head).transpose(0, 2, 1, 3)
        scores = q @ k.transpose(0, 1, 3, 2)
        scores = np.exp(scores - scores.max(-1, keepdims=True))
        scores /= scores.sum(-1, keepdims=True)
        attended = (scores @ v).transpose(0, 2, 1, 3).reshape(n, -1, dim)
        x = x + proj(attended, "out_proj")
        y = layer_norm(x, w(p + "layer_norm2.weight"), w(p + "layer_norm2.bias"))
        y = y @ w(p + "mlp.fc1.weight").T + w(p + "mlp.fc1.bias")
        y = y * (1.0 / (1.0 + np.exp(-1.702 * y)))  # quick_gelu
        x = x + (y @ w(p + "mlp.fc2.weight").T + w(p + "mlp.fc2.bias"))
    pooled = layer_norm(x[:, 0], w("vision_model.post_layernorm.weight"), w("vision_model.post_layernorm.bias"))
    return pooled @ w("visual_projection.weight").T


def normalize(v: np.ndarray) -> np.ndarray:
    return v / np.linalg.norm(v, axis=-1, keepdims=True)


def cosine(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    return (normalize(a) * normalize(b)).sum(-1)


def main() -> None:
    args = sys.argv[1:]
    reference_path = None
    if "--reference" in args:
        index = args.index("--reference")
        reference_path = args[index + 1]
        del args[index:index + 2]
    source_dir, fp32_path = args[0], args[1]
    quantized_path = args[2] if len(args) > 2 else None

    state = torch.load(os.path.join(source_dir, "pytorch_model.bin"), map_location="cpu", weights_only=True)
    weights = {k: v.numpy() for k, v in state.items() if k.startswith(("vision_model.", "visual_projection."))}

    image = short_side_center_crop(scene(), 224, Image.BICUBIC)
    pixels = ((np.asarray(image, dtype=np.float64) / 255.0 - MEAN) / STD).transpose(2, 0, 1)[None]
    rng = np.random.default_rng(1234)
    batch = np.concatenate([pixels, rng.normal(0, 1, (2, 3, 224, 224))]).astype(np.float32)

    expected = clip_vision(weights, batch.astype(np.float64))
    session = ort.InferenceSession(fp32_path, providers=["CPUExecutionProvider"])
    actual = session.run(["image_embeds"], {"pixel_values": batch})[0]
    similarity = cosine(expected, actual)
    print("fp32 export vs NumPy reference, cosine:", np.array2string(similarity, precision=7))
    print("fp32 export vs NumPy reference, max |diff|:", float(np.abs(expected - actual).max()))
    if similarity.min() < 0.99999:
        raise SystemExit("The ONNX export does not reproduce the official weights.")

    if quantized_path:
        quantized = ort.InferenceSession(quantized_path, providers=["CPUExecutionProvider"])
        approx = quantized.run(["image_embeds"], {"pixel_values": batch})[0]
        print("quantized vs fp32, cosine:", np.array2string(cosine(actual, approx), precision=5))

    if reference_path:
        embedding = normalize(expected[0])
        with open(reference_path, "w", encoding="utf-8") as fh:
            json.dump({"scene_320x240": [round(float(v), 7) for v in embedding]}, fh)
        print("wrote", reference_path)


if __name__ == "__main__":
    main()
