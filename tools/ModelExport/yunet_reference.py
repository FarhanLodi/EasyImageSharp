"""Reference YuNet decoding in numpy, mirroring the OpenCV FaceDetectorYN post-processing.

The C# implementation (EasyImageSharp.AI FaceDetector) follows the same steps, so this file doubles as
its executable specification:

* input: BGR, raw 0-255 floats (no mean/std), NCHW, zero-padded at the bottom/right to multiples of 32;
* outputs: cls_{8,16,32} [1,N,1], obj_{8,16,32} [1,N,1], bbox_{8,16,32} [1,N,4], kps_{8,16,32} [1,N,10],
  where N = (padH/stride) * (padW/stride) in row-major anchor order;
* score = sqrt(clamp(cls,0,1) * clamp(obj,0,1));
* box centre = (col + dx, row + dy) * stride, size = exp(dw|dh) * stride;
* landmark k = (col + kx_k, row + ky_k) * stride, order: right eye, left eye, nose tip, right mouth
  corner, left mouth corner (from the subject's point of view);
* greedy NMS by descending score with IoU threshold 0.3.
"""
from __future__ import annotations

import numpy as np

STRIDES = (8, 16, 32)


def prepare(image_rgb: np.ndarray) -> tuple[np.ndarray, int, int]:
    """HWC uint8 RGB -> padded [1,3,H',W'] BGR float32 tensor, plus the padded width/height."""
    h, w = image_rgb.shape[:2]
    pad_h = ((h - 1) // 32 + 1) * 32
    pad_w = ((w - 1) // 32 + 1) * 32
    tensor = np.zeros((1, 3, pad_h, pad_w), dtype=np.float32)
    tensor[0, :, :h, :w] = image_rgb[..., ::-1].transpose(2, 0, 1).astype(np.float32)
    return tensor, pad_w, pad_h


def decode(outputs: dict[str, np.ndarray], pad_w: int, pad_h: int, score_threshold: float) -> np.ndarray:
    """Returns rows of (x, y, w, h, 10 landmark coords, score) before NMS."""
    rows = []
    for stride in STRIDES:
        cols = pad_w // stride
        cls = outputs[f"cls_{stride}"].reshape(-1)
        obj = outputs[f"obj_{stride}"].reshape(-1)
        bbox = outputs[f"bbox_{stride}"].reshape(-1, 4)
        kps = outputs[f"kps_{stride}"].reshape(-1, 10)
        score = np.sqrt(np.clip(cls, 0, 1) * np.clip(obj, 0, 1))
        for idx in np.nonzero(score >= score_threshold)[0]:
            r, c = divmod(int(idx), cols)
            cx = (c + bbox[idx, 0]) * stride
            cy = (r + bbox[idx, 1]) * stride
            bw = np.exp(bbox[idx, 2]) * stride
            bh = np.exp(bbox[idx, 3]) * stride
            landmarks = []
            for n in range(5):
                landmarks.append((kps[idx, 2 * n] + c) * stride)
                landmarks.append((kps[idx, 2 * n + 1] + r) * stride)
            rows.append([cx - bw / 2, cy - bh / 2, bw, bh, *landmarks, score[idx]])
    return np.array(rows, dtype=np.float32).reshape(-1, 15)


def box_iou(a: np.ndarray, b: np.ndarray) -> float:
    ax2, ay2 = a[0] + a[2], a[1] + a[3]
    bx2, by2 = b[0] + b[2], b[1] + b[3]
    iw = max(0.0, min(ax2, bx2) - max(a[0], b[0]))
    ih = max(0.0, min(ay2, by2) - max(a[1], b[1]))
    inter = iw * ih
    union = a[2] * a[3] + b[2] * b[3] - inter
    return 0.0 if union <= 0 else float(inter / union)


def nms(faces: np.ndarray, iou_threshold: float, top_k: int = 5000) -> np.ndarray:
    order = np.argsort(-faces[:, 14], kind="stable")[:top_k]
    keep = []
    for i in order:
        if all(box_iou(faces[i], faces[j]) <= iou_threshold for j in keep):
            keep.append(i)
    return faces[keep]


def detect(session, image_rgb: np.ndarray, score_threshold: float = 0.6, nms_threshold: float = 0.3) -> np.ndarray:
    tensor, pad_w, pad_h = prepare(image_rgb)
    names = [o.name for o in session.get_outputs()]
    values = session.run(names, {session.get_inputs()[0].name: tensor})
    faces = decode(dict(zip(names, values)), pad_w, pad_h, score_threshold)
    return nms(faces, nms_threshold) if len(faces) > 1 else faces
