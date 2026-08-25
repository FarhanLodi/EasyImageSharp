# webpenc

Source images for the WebP **encoder** tests (`WebpEncoderTests`). Each `<name>.png` is a lossless
RGBA source; `manifest.json` lists its dimensions, whether it uses alpha, how many distinct colours
it has, and the byte size libwebp produces for the same pixels with `lossless=True, exact=True` at
encoder methods 0, 4 and 6. The test encodes each image with the library and requires the result to
round-trip pixel-exactly and to stay within 15% (plus a small fixed slack for tiny files) of those
libwebp sizes.

Regenerate with `python generate.py`. Cross-check the library's own output against libwebp with
`python gen_webpenc.py --verify <test output>/webpenc-output`.

| image | size | alpha | colours | libwebp m0 | libwebp m4 | libwebp m6 |
| --- | --- | --- | --- | --- | --- | --- |
| bars | 64x48 | yes | 4 | 134 | 126 | 94 |
| flat | 32x24 | no | 1 | 38 | 38 | 32 |
| gradient | 96x64 | no | 6144 | 374 | 66 | 58 |
| gray_ramp | 80x60 | no | 200 | 4050 | 1150 | 1112 |
| noise | 48x36 | no | 1728 | 5308 | 5314 | 5310 |
| noise_alpha | 48x36 | yes | 1728 | 7070 | 7062 | 7062 |
| odd | 37x23 | yes | 851 | 688 | 180 | 184 |
| palette256 | 64x48 | no | 256 | 554 | 116 | 68 |
| photo | 96x72 | no | 6876 | 11436 | 9846 | 10046 |
| sprite | 40x40 | yes | 1120 | 448 | 368 | 276 |
| two_color | 24x18 | no | 2 | 52 | 52 | 48 |
