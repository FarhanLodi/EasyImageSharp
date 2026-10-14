"""Independent reduced JPEG references: Pillow delegates draft decoding to libjpeg-turbo.

Invoked by generate.py after the JPEG fixtures exist. It can also run independently.
Existing JPEG bytes are inputs; no EasyImageSharp output is used.
"""
from pathlib import Path
import hashlib
import json
from PIL import Image, features

root = Path(__file__).resolve().parent
names = [
    "baseline_444", "baseline_gray", "baseline_422", "baseline_420", "baseline_420_odd",
    "progressive_444", "progressive_gray", "progressive_422", "progressive_420", "progressive_420_odd",
    "restart_baseline_420", "restart_progressive_420", "cmyk_adobe", "ycck_adobe",
]

def main():
    output = root / "jpeg-reduced"
    output.mkdir(exist_ok=True)
    records = []
    for name in names:
        source = root / "jpeg" / (name + ".jpg")
        for denominator in [2, 4, 8]:
            with Image.open(source) as image:
                original = image.size
                image.draft(image.mode, tuple(max(1, n // denominator) for n in original))
                image.load()
                expected = tuple((n + denominator - 1) // denominator for n in original)
                assert image.size == expected, (name, denominator, image.size, expected)
                target = output / f"{name}-{denominator}.png"
                image.convert("RGB").save(target)
            records.append({"name": name, "denominator": denominator,
                            "inputSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                            "outputSha256": hashlib.sha256(target.read_bytes()).hexdigest()})
    (output / "provenance.json").write_text(json.dumps({
        "pillow": Image.__version__, "libjpegTurbo": features.version_feature("libjpeg_turbo"),
        "method": "Image.draft native reduced-IDCT decoding; RGB conversion after decode",
        "references": records,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"jpeg-reduced: {len(records)} independent reduced-IDCT references")


if __name__ == "__main__":
    main()
