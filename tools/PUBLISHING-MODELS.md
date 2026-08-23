# Publishing the models to Hugging Face

`EasyImageSharp.AI` fetches its neural-network weights at run time from

```
https://huggingface.co/EasyImageSharp/EasyImageSharp-models/resolve/main/<file>
```

This document is what you need to stand that repository up. Two of the six models are already published
elsewhere and only need mirroring; the other four need exporting from their upstream projects.

---

## 1. Create the repository

On huggingface.co, create an organisation named **`EasyImageSharp`**, then a **public model repository**
inside it named **`EasyImageSharp-models`**.

An organisation rather than a personal account matters here: the URL is compiled into a shipped library,
so it should survive a change of maintainer. If you would rather publish under your own username, change
`ModelRegistry.DefaultBaseUrl` to match before releasing — users can already override it at run time via
`ImageAiOptions.BaseUrlOverride` or `EASYIMAGESHARP_MODEL_BASE_URL`, but the built-in default is what
almost everyone will use.

Layout rules the downloader relies on:

- **Files sit flat at the repository root.** The URL is `{base}/{fileName}` with a single path segment;
  a file in a subdirectory cannot be reached.
- **A published file is never overwritten.** A re-export gets a new file name (`_v2`, or a date) and a
  new checksum entry. Old files stay forever so a pinned library version keeps resolving the exact bytes
  it was tested against.
- **Single-file ONNX only** — no external `.onnx.data` sidecar, because one URL must be one asset for
  the resumable downloader. Every model here is far below the 2 GB protobuf limit.

---

## 2. What to upload

### Already published upstream — mirror these two

Download them from `https://huggingface.co/PaddleOcrNet/PaddleOcrNet-models` and re-upload the
**identical bytes**. Their checksums are already pinned in the registry, so if the bytes match, nothing
in the code needs to change except pointing the two descriptors at `DefaultBaseUrl`.

| File | Task | Size | Licence |
|---|---|---|---|
| `PP-LCNet_x1_0_doc_ori.onnx` | Document orientation (0/90/180/270) | 6.8 MB | Apache-2.0 |
| `UVDoc.onnx` | Page dewarping | 31 MB | MIT |

After mirroring, in `ModelRegistry.cs` change `BaseUrl = PaddleOcrNetBaseUrl` to
`BaseUrl = DefaultBaseUrl` on those two descriptors. Verify the pinned hashes still match with
`python tools/pin-models.py <folder> --check` — if they do, the mirror is byte-exact and the whole
supply chain is now first-party.

### Need exporting — four models

Export each to ONNX with **opset 17**, matching the input contract exactly. The contract is not
negotiable: the operations feed these tensors and interpret the output accordingly.

| File | Task | Input | Output | Upstream | Licence |
|---|---|---|---|---|---|
| `realesrgan_general_x4v3.onnx` | Super-resolution ×4 | `input` `[1,3,H,W]` RGB in 0–1, **dynamic H/W** | `[1,3,4H,4W]` in 0–1 | [xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) `realesr-general-x4v3` (SRVGGNetCompact) | BSD-3-Clause |
| `dncnn_gray_blind.onnx` | Grayscale denoise | `input` `[1,1,H,W]` luminance in 0–1, **dynamic H/W** | `[1,1,H,W]` **noise residual** — the op computes `clean = input − output` | [cszn/DnCNN](https://github.com/cszn/DnCNN) `dncnn_gray_blind` | check upstream terms |
| `u2netp.onnx` | Saliency / background | `input` `[1,3,320,320]` RGB, ImageNet mean/std | `[1,1,320,320]` mask in 0–1 | [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net) `u2netp` | Apache-2.0 |
| `sauvolanet.onnx` | Learned binarisation | `input` `[1,1,H,W]` luminance in 0–1, **dynamic H/W** | per-pixel threshold map | [Leedeng/SauvolaNet](https://github.com/Leedeng/SauvolaNet) | check upstream terms |

Dynamic height and width matter for three of these — the operations tile large inputs, so a model frozen
at a fixed size will fail on anything else.

**Optional int8 variants.** Each may also have a `<name>.int8.onnx` next to it (dynamic quantisation,
per-channel QDQ for the convolutional nets). Users opt in with `ImageAiOptions.Quantize = true`. Skip
int8 for Real-ESRGAN if quality drops noticeably — fp16 is the better trade there.

**Before publishing DnCNN and SauvolaNet, check their upstream licence terms.** Both are research
releases and the registry deliberately records their licence as "see upstream" rather than asserting one.
Redistributing weights you do not have the right to redistribute is the one mistake here that is hard to
undo. If the terms do not permit it, leave them unpublished — the library already handles that case:
users point at their own export via `ImageAiOptions.ModelPathOverrides`.

---

## 3. Pin the checksums

**This is the step that makes the models actually load.** Verification is fail-closed: a file whose
SHA-256 is not pinned, or does not match, is deleted and the load throws `ModelChecksumException`.

```bash
# 1. Hash exactly what you are about to upload
python tools/pin-models.py ./models-to-upload

# 2. Paste the printed lines into the Checksums dictionary in
#    src/EasyImageSharp.AI/Models/ModelRegistry.cs, then rebuild.

# 3. Upload to Hugging Face.

# 4. Download the files back from the repository and re-verify — this hashes what the CDN
#    actually serves, not what you meant to send.
python tools/pin-models.py ./downloaded-back --check
```

Step 4 is not paranoia: it catches Git LFS pointer files, transcoding and truncated uploads, all of which
produce a file that looks right in the web UI and fails for every user.

---

## 4. Write the model card

The repository's `README.md` is its model card. It should carry, for each file: the task, the exact input
and output contract, the upstream project and commit it was exported from, the licence, and the SHA-256.
Users auditing a supply chain will read this before allowing a download.

Also add a machine-readable `checksums.json` at the root (`{"file.onnx": "ABC123..."}`), so CI can diff
the published hashes against the pinned ones and catch drift.

---

## 5. Verify end to end

```csharp
using var ai = new ImageAiSession(new ImageAiOptions { Log = Console.WriteLine });
using Image<Rgb24> page = Image.Load<Rgb24>("test-page.jpg");
Console.WriteLine(page.DetectOrientation(ai));   // downloads, verifies, caches, runs
```

The cache lands in `%LOCALAPPDATA%/EasyImageSharp/models` on Windows and `~/.local/share` elsewhere.
Delete it and re-run to prove a cold download works for a new user.

---

## What happens until you publish

Nothing breaks. The two mirrored models keep working from their current upstream location. The four
unpublished ones have no pinned checksum, so the library refuses to download them and says so — and
users can supply their own export today:

```csharp
var options = new ImageAiOptions();
options.ModelPathOverrides["super-resolution-x4"] = "/models/my-export.onnx";
```

Model names for overrides: `doc-orientation`, `doc-dewarp`, `super-resolution-x4`, `denoise-gray`,
`saliency`, `binarization`.
