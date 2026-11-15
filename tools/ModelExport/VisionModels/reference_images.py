"""Deterministic synthetic test images shared by the verification scripts and the C# smoke tests.

The C# twin is RealModels.Scene in tests/EasyImageSharp.AI.Tests/VisionModelSmokeTests.cs; both use
integer arithmetic only, so they produce identical pixels.
"""
from __future__ import annotations

import numpy as np
from PIL import Image


def scene(width: int = 320, height: int = 240) -> Image.Image:
    """A horizontal red ramp, a vertical blue ramp, a 16 px green checkerboard and one yellow disc."""
    y, x = np.mgrid[0:height, 0:width]
    r = (x * 255) // (width - 1)
    g = (((x // 16) + (y // 16)) % 2) * 200 + 30
    b = (y * 255) // (height - 1)
    cx, cy, radius = width * 2 // 3, height // 2, min(width, height) // 5
    disc = (x - cx) ** 2 + (y - cy) ** 2 < radius * radius
    rgb = np.stack([r, g, b], axis=-1)
    rgb[disc] = (240, 200, 40)
    return Image.fromarray(rgb.astype(np.uint8), "RGB")


def short_side_center_crop(image: Image.Image, size: int, resample: int) -> Image.Image:
    """The CLIP / timm evaluation transform: shorter side to `size` (longer side truncated, as torchvision and
    transformers do), then the centred size x size crop with a floor offset."""
    if image.width <= image.height:
        width, height = size, int(size * image.height / image.width)
    else:
        width, height = int(size * image.width / image.height), size
    image = image.resize((width, height), resample)
    left, top = (width - size) // 2, (height - size) // 2
    return image.crop((left, top, left + size, top + size))
