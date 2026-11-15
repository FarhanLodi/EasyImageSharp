# Vision models: provenance, licences and I/O contracts

Scripts in this folder verify or produce the ONNX files behind `ModelRegistry.ImageEmbedding`, `Inpainting`,
`ContentModeration` and `QualityAssessment` (`src/EasyImageSharp.AI/Models/ModelRegistry.cs`). Each file is published
unchanged, under the name below, at the flat root of `https://huggingface.co/EasyImageSharp/EasyImageSharp-models` and pinned
by the SHA-256 below. Run every script with `python -I` from a separate working directory (torch / timm / safetensors for
`export_moderation.py` and `verify_clip.py`, TensorFlow 2.15 + tf2onnx for `export_nima.py`, NumPy < 2 for both).
`reference_images.py` builds the deterministic scene whose reference outputs (each script's `--reference` output,
merged by `write_test_references.py`) live in `tests/EasyImageSharp.AI.Tests/Models/vision_model_references.json`; the opt-in real-model smoke tests
(`VisionModelSmokeTests`) compare against them when `EASYIMAGESHARP_REAL_MODELS` points at a folder holding the files.

| File | Bytes | SHA-256 | Licence (weights) |
|---|---:|---|---|
| clip-vit-base-patch32-vision.onnx | 351,685,709 | FD6E1402A588279D1723C7534D4BCBA5BC0B14B47DFAB0E46F8C47B8270D7D40 | MIT |
| clip-vit-base-patch32-vision.int8.onnx | 89,117,001 | 583FD1110A514667812FEE7D684952AAF82A99B959760C8D7DCA7E0AB9839299 | MIT |
| big-lama-512.onnx | 208,044,816 | 1FAEF5301D78DB7DDA502FE59966957EC4B79DD64E16F03ED96913C7A4EB68D6 | Apache-2.0 |
| nsfw-vit-tiny-384.onnx | 22,509,796 | 03D3AAB4390315F122E2A1827349C4818655776499CC36C1034AF69314BF56E2 | Apache-2.0 |
| nima-mobilenet-quality.onnx | 25,702,841 | F311E55DDC1115BBE32306CA6F511787B4913B60E1F788CCDC704B83D548667B | Apache-2.0 |

---

## clip-vit-base-patch32-vision.onnx (+ .int8.onnx) — registry `image-embedding`

* **Model**: OpenAI CLIP ViT-B/32 image tower + visual projection (`CLIPVisionModelWithProjection`).
* **Weights source**: `openai/clip-vit-base-patch32` @ `3d74acf9a28c67741b2f4f2ea7635f0aaf6f0268`
  (`pytorch_model.bin`, SHA-256 A63082132BA4F97A80BEA76823F544493BFFA8082296D62D71581A4FEFF1576F), the Hugging Face
  conversion of the weights released in https://github.com/openai/CLIP.
* **ONNX source**: byte-identical copies of `onnx/vision_model.onnx` and `onnx/vision_model_quantized.onnx` from
  https://huggingface.co/Xenova/clip-vit-base-patch32 @ `6b7c523703ae26e39be4b03774966f8571c7f523` (the LFS SHA-256
  values reported by the Hugging Face API match the files here).
* **Licence**: MIT — https://github.com/openai/CLIP/blob/main/LICENSE (staged as `LICENSE.clip-vit-base-patch32.txt`); the Xenova
  repository declares `license: mit`. Note: the upstream model card (https://github.com/openai/CLIP/blob/main/model-card.md) describes the
  model as a research artefact and states that "any deployed use case of the model ... is currently out of scope"; that
  is guidance in the card, not a term of the MIT licence.
* **Verification**: `verify_clip.py` re-implements the vision transformer in NumPy (float64) from the official weights;
  the fp32 export matches it with cosine similarity 1.0000000 (max |diff| 1.4e-5) on the reference scene and two random
  tensors. The int8 file (ONNX Runtime dynamic quantisation by the exporter) has cosine similarity 0.982-0.992 with fp32.
* **Contract**: input `pixel_values` float32 `[N,3,224,224]` RGB; shorter side resized to 224 with bicubic (longer side
  truncated), centred 224x224 crop, mean (0.48145466, 0.4578275, 0.40821073), std (0.26862954, 0.26130258, 0.27577711)
  on 0-1 values. Output `image_embeds` float32 `[N,512]` (unnormalised; EasyImageSharp L2-normalises it).

## big-lama-512.onnx — registry `inpainting`

* **Model**: LaMa (big-lama), "Resolution-robust Large Mask Inpainting with Fourier Convolutions" (Suvorov et al.,
  WACV 2022), https://github.com/advimman/lama.
* **ONNX source**: byte-identical copy of `lama_fp32.onnx` from https://huggingface.co/Carve/LaMa-ONNX @
  `c3c0c9e468934d62e79c329e35d82dd09ff8c444` (export notebook: https://github.com/Carve-Photos/lama,
  `export_LaMa_to_onnx.ipynb`; torch.onnx.export, opset 17, custom FourierUnit for the FFT).
* **Licence**: Apache-2.0 — advimman/lama `LICENSE` (Copyright 2021 Samsung Research, staged as `LICENSE.big-lama.txt`); the
  Carve repository and its code fork declare Apache-2.0.
* **Verification**: `verify_lama.py` runs the exporter's sample through ONNX Runtime: outside the hole the output equals
  255 * input exactly; inside the hole PSNR is 83.2 dB against the original PyTorch model's output shipped by the exporter.
* **Contract**: inputs `image` float32 `[1,3,512,512]` RGB in 0-1 and `mask` float32 `[1,1,512,512]` (1 = hole,
  0 = keep); output `output` float32 `[1,3,512,512]` = clamp(255 * (mask * prediction + (1 - mask) * image), 0, 255).

## nsfw-vit-tiny-384.onnx — registry `content-moderation`

* **Model**: `Marqo/nsfw-image-detection-384` (timm `vit_tiny_patch16_384`, 2 classes NSFW / SFW) @
  `0c26ec22111b83f106d72a55f611ec35962bcb65`, `model.safetensors` SHA-256
  6BF2E0F64A1D20169736C2836E3A787B12379FDC08BA87F7D94A7A3D58EEEFCE.
* **Export**: `export_moderation.py` (torch 2.0.1, timm 1.0.15, fused attention disabled, opset 17, dynamic batch);
  class names stored in the ONNX metadata (`labels` = `NSFW,SFW`).
* **Licence**: Apache-2.0, declared in the model card metadata (https://huggingface.co/Marqo/nsfw-image-detection-384);
  the repository ships no separate LICENSE file. The training data is the publisher's proprietary set.
* **Verification**: max |torch - onnxruntime| = 3.3e-6 on random tensors and the reference scene.
* **Contract**: input `pixel_values` float32 `[N,3,384,384]` RGB; shorter side to 384 with bicubic, centred crop,
  (x/255 - 0.5)/0.5. Output `logits` float32 `[N,2]` (NSFW, SFW); EasyImageSharp applies the softmax.
* **Limitations**: sexual / explicit content only; makes mistakes in both directions; not a guarantee.

## nima-mobilenet-quality.onnx — registry `quality-assessment`

* **Model**: NIMA (Talebi & Milanfar, 2018) on MobileNet v1 from https://github.com/idealo/image-quality-assessment @
  `dceaf7c2d218bc6e80b21d6e147e3b56a21b7f31`: `models/MobileNet/weights_mobilenet_technical_0.11.hdf5`
  (SHA-256 78C8BB95098FD27695DE4147465F4FD504D0CA5E11699A487534854B5AEB3036, trained on TID2013) and
  `weights_mobilenet_aesthetic_0.07.hdf5` (SHA-256 E563AD91B3D47410E45F7238F07AB8F6ABD1BD0C4B18A4B0AF9C681A21A91CB2,
  trained on AVA).
* **Export**: `export_nima.py` rebuilds the Keras 2.1.6 MobileNet layout the weights were trained with (symmetric
  ZeroPadding2D((1,1)) + 'valid' convolutions), loads both weight files, runs the two networks side by side on one
  input and converts with tf2onnx 1.17 (TensorFlow 2.15.1, opset 17, NCHW input).
* **Licence**: Apache-2.0 — idealo/image-quality-assessment `LICENSE` (Copyright 2018 idealo internet GmbH,
  staged as `LICENSE.nima-mobilenet.txt`). The training datasets (TID2013, AVA) have their own research-oriented terms.
* **Verification**: max |keras - onnxruntime| = 2.7e-7 and max |independent NumPy float64 implementation -
  onnxruntime| = 4.1e-7 (probabilities).
* **Contract**: input `pixel_values` float32 `[N,3,224,224]` RGB, image stretched to 224x224 with nearest-neighbour
  sampling (PIL / Keras `load_img` default), (x/255 - 0.5)/0.5. Outputs `technical`, `aesthetic` float32 `[N,10]`,
  softmax over ratings 1..10; score = sum_i i * p_i.
